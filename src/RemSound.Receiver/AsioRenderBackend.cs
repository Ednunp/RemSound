using NAudio.Wave;
using RemSound.Core;

namespace RemSound.Receiver;

/// <summary>
/// ASIO render backend. Drives a single <see cref="AsioOut"/> for the chosen ASIO driver,
/// pulling the receiver's mixed stereo audio from <see cref="PlayoutEngine"/> and broadcasting
/// it across one or more output channel pairs of the driver. Same shape as
/// <see cref="MultiOutputPlayout"/>: <see cref="AudioReceiver"/> doesn't care which is active.
///
/// Spec identity: each output ID is a synthetic <c>"asio:&lt;channel-pair-index&gt;"</c>. Pair 0
/// = ASIO output channels 0+1, pair 1 = 2+3, etc. The driver itself is locked at construction.
///
/// Same simplifications as <see cref="AsioCaptureBackend"/>: 48 kHz fixed; driver is single
/// per session. Always opens the AsioOut with the driver's full output channel count so that
/// adding/removing channel pairs never requires reopening the driver — important when the
/// sender and receiver are both holding the same single-client driver (Komplete Audio etc.):
/// reopening one while the other is alive caused 15-second freezes.
/// </summary>
internal sealed class AsioRenderBackend : IRenderBackend
{
    private const int MixSampleRate = 48000;
    private const int MixChannels = 2;

    // Same reasoning as MultiOutputPlayout — source typed as IWaveProvider so the composite
    // backend can hand us a tee'd buffer.
    private readonly IWaveProvider source;
    private readonly Action<string>? onDiagnostic;
    private readonly string driverName;
    private readonly object gate = new();

    // The driver, SHARED with the sender's capture side - one instance per driver name, full duplex.
    // See SharedAsioDevice, and the matching field in AsioCaptureBackend for why.
    private SharedAsioDevice? device;
    private List<int> activeChannelPairs = [];
    private BroadcastProvider? broadcaster;

    // Every driver control call - create, init, play, stop, dispose - runs on the shared device's one
    // dedicated pumped STA thread. That discipline came to this file in the 2026-08-23 audit (finding
    // R1: the render side was opening and closing the COM object straight from the UI thread) and it
    // is kept, one level down, by SharedAsioDevice.

    /// <summary>Generous, and NOT tuned to one card — see the matching constant on the capture side.
    /// Ed's Audient takes 7.94 s to close about one time in nine, another card could be slower, and
    /// the elapsed time is logged every close so we accumulate real numbers instead of guessing.</summary>
    private const int CloseTimeoutMs = 30_000;

    public AsioRenderBackend(string driverName, IWaveProvider source, Action<string>? onDiagnostic = null)
    {
        this.driverName = driverName;
        this.source = source;
        this.onDiagnostic = onDiagnostic;
    }

    public bool IsRunning => device is not null;

    // The driver's own playback latency, read once at open. Volatile store so the UI thread can read
    // it while the apartment thread writes it.
    private volatile float reportedOutputLatencyStore;
    private double reportedOutputLatencyMs
    {
        get => reportedOutputLatencyStore;
        set => reportedOutputLatencyStore = (float)value;
    }

    /// <summary>ASIO pulls straight from the playout engine — there is no intermediate buffer to
    /// queue in. Always zero. See <see cref="IRenderBackend.OutputQueueMs"/>.</summary>
    private const double NoQueue = 0;   // ASIO pulls straight from the engine — nothing is buffered here

    /// <summary>This backend IS the ASIO lane and has nothing to say about WASAPI. See
    /// <see cref="IRenderBackend.ReportedOutputLatencyMsFor"/>.</summary>
    public double ReportedOutputLatencyMsFor(RenderRoute route) =>
        route == RenderRoute.AsioLane ? reportedOutputLatencyMs : 0;

    /// <inheritdoc cref="ReportedOutputLatencyMsFor"/>
    public double OutputQueueMsFor(RenderRoute route) => NoQueue;

    /// <summary>Always empty, and that is a statement rather than a gap: ASIO pulls straight from the
    /// playout engine, so there is no device buffer, no adaptive cushion target and no rate-trimming
    /// resampler between the engine and the driver. Everything the WASAPI lane has here, ASIO does
    /// without — which is why a fault seen on WASAPI and never on ASIO has to live in that stage.</summary>
    public IReadOnlyList<WasapiOutputStage> OutputStages() => Array.Empty<WasapiOutputStage>();

    public string ActiveDeviceSummary
    {
        get
        {
            lock (gate)
            {
                if (activeChannelPairs.Count == 0) return "(none)";
                var names = activeChannelPairs.Select(p => $"{driverName} ASIO {p * 2 + 1}/{p * 2 + 2}").ToList();
                if (names.Count <= 3) return string.Join(", ", names);
                return $"({names.Count} ASIO outputs)";
            }
        }
    }

    public IReadOnlyList<string> ActiveDeviceIds
    {
        get { lock (gate) return activeChannelPairs.Select(AsioDeviceId.Format).ToList(); }
    }

    public void Start()
    {
        // ASIO render starts lazily when SetOutputDevices is given a non-empty list. There's no
        // useful "open driver but render to nothing" state — that just locks the device with no
        // benefit. The MixingEngine equivalent (producer loop) for WASAPI runs continuously
        // even with zero outputs to keep state alive; ASIO doesn't need that since the AsioOut
        // *is* the output and there's nothing to keep alive when no channels are wanted.
        // Caller is expected to call SetOutputDevices first; this method is a no-op when empty.
        lock (gate)
        {
            if (IsRunning) return;
            if (activeChannelPairs.Count == 0) return;
            OpenAsioLocked();
        }
    }

    public void Stop()
    {
        lock (gate) StopInternal();
    }

    public void SetOutputDevices(IReadOnlyList<string> deviceIds)
    {
        lock (gate)
        {
            var newPairs = ParsePairs(deviceIds);
            if (newPairs.Count == 0)
            {
                if (IsRunning) StopInternal();
                activeChannelPairs = newPairs;
                return;
            }

            activeChannelPairs = newPairs;

            // First time we have any pairs → open the driver. Otherwise we never reopen on a
            // pair-set change, because we already opened with the driver's full channel count
            // at Start time. Just update the broadcaster's pair list and we're done.
            if (device is null)
            {
                OpenAsioLocked();
                return;
            }
            broadcaster?.SetActivePairs(activeChannelPairs);
            onDiagnostic?.Invoke($"asio render: pairs updated to {string.Join(",", activeChannelPairs)} (no driver restart)");
        }
    }

    private void OpenAsioLocked()
    {
        try
        {
            // Through the SHARED device - see SharedAsioDevice. The capture side may already hold this
            // driver open; either way there is one instance, opened full duplex on the device's
            // apartment thread with the same breadcrumbs as before.
            var shared = SharedAsioDevice.Acquire(driverName, onDiagnostic);
            device = shared;
            try
            {
                shared.EnsureOpen();
                // The device opened the driver's full output channel count. Channels we don't
                // broadcast to are zero-filled by BroadcastProvider, which is essentially free, and
                // adding or removing an output pair never reopens anything.
                var outputChannelCount = shared.OutputChannelCount;
                if (outputChannelCount <= 0)
                    throw new InvalidOperationException($"driver \"{driverName}\" reports zero output channels");
                var maxPair = activeChannelPairs.Max();
                var highestNeededChannel = (maxPair + 1) * 2;
                if (highestNeededChannel > outputChannelCount)
                {
                    onDiagnostic?.Invoke($"asio render: driver \"{driverName}\" only has {outputChannelCount} output channels, but spec requests pair {maxPair} (channels {maxPair * 2 + 1}/{maxPair * 2 + 2})");
                }
                broadcaster = new BroadcastProvider(source, outputChannelCount, activeChannelPairs);
                shared.AttachPlayback(broadcaster);
                // The DRIVER'S OWN figure for how long playback takes, in samples - the one number in
                // the latency estimate that comes from the hardware rather than from us.
                reportedOutputLatencyMs = shared.PlaybackLatencySamples > 0
                    ? shared.PlaybackLatencySamples * 1000.0 / MixSampleRate
                    : 0;
                onDiagnostic?.Invoke(reportedOutputLatencyMs > 0
                    ? $"asio render: driver reports {shared.PlaybackLatencySamples} samples of playback latency = {reportedOutputLatencyMs:0.0} ms"
                    : "asio render: driver did not report a playback latency — the estimate falls back to its own figure");
                onDiagnostic?.Invoke($"asio render started \"{driverName}\" {MixSampleRate} Hz, {outputChannelCount} output channel(s); pairs={string.Join(",", activeChannelPairs)}");
            }
            catch
            {
                device = null;
                shared.Release(onDiagnostic, CloseTimeoutMs);
                throw;
            }
        }
        catch (Exception ex)
        {
            onDiagnostic?.Invoke($"asio render start failed: {ex.GetType().Name}: {SharedAsioDevice.Explain(ex)}");
            StopInternal();
        }
    }

    private void StopInternal()
    {
        var shared = device;
        if (shared is not null)
        {
            // Silence on our outputs at once (a volatile detach). The driver itself closes only when
            // the capture side has let go as well, on the device's apartment thread, bounded and timed
            // as the 2026-08-23 audit required - see SharedAsioDevice.Close.
            shared.DetachPlayback();
            device = null;
            shared.Release(onDiagnostic, CloseTimeoutMs);
        }
        broadcaster = null;
        reportedOutputLatencyMs = 0;   // belongs to the stream we just closed
    }

    public void Dispose()
    {
        Stop();
    }

    private static List<int> ParsePairs(IReadOnlyList<string> deviceIds)
    {
        var result = new List<int>();
        foreach (var id in deviceIds)
        {
            if (AsioDeviceId.TryParse(id, out var pair) && pair >= 0)
            {
                result.Add(pair);
            }
        }
        result.Sort();
        return result.Distinct().ToList();
    }

    /// <summary>
    /// Wave provider that pulls stereo audio from <see cref="PlayoutEngine"/> and writes it to
    /// a multi-channel ASIO buffer at the requested channel pair positions, zero-filling the
    /// channels that aren't selected. Output is interleaved 32-bit float at 48 kHz, exactly
    /// what NAudio's AsioOut wants.
    /// </summary>
    private sealed class BroadcastProvider : IWaveProvider
    {
        private readonly IWaveProvider source;
        private readonly int outputChannelCount;
        private byte[] sourceScratchBytes = new byte[16384];
        private List<int> activePairs;

        public WaveFormat WaveFormat { get; }

        public BroadcastProvider(IWaveProvider source, int outputChannelCount, List<int> activePairs)
        {
            this.source = source;
            this.outputChannelCount = outputChannelCount;
            this.activePairs = new List<int>(activePairs);
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(MixSampleRate, outputChannelCount);
        }

        public void SetActivePairs(IEnumerable<int> pairs)
        {
            // Atomic swap. Read side reads activePairs once per Read so a partial swap is
            // tolerable — at worst we get one tick of stale routing.
            activePairs = pairs.ToList();
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            // Frame size in BYTES on the output side.
            var bytesPerOutputFrame = outputChannelCount * sizeof(float);
            var frames = count / bytesPerOutputFrame;
            if (frames <= 0) return 0;

            // Pull stereo from PlayoutEngine — its WaveFormat is 48k stereo float, so 8 bytes
            // per frame.
            var sourceBytes = frames * MixChannels * sizeof(float);
            if (sourceScratchBytes.Length < sourceBytes) sourceScratchBytes = new byte[sourceBytes];
            source.Read(sourceScratchBytes, 0, sourceBytes);

            // Interpret source bytes as float array, output bytes as float array, broadcast.
            var srcFloats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(sourceScratchBytes.AsSpan(0, sourceBytes));
            var dstFloats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count));
            dstFloats.Clear();

            var pairs = activePairs;
            for (var f = 0; f < frames; f++)
            {
                var l = srcFloats[f * MixChannels];
                var r = srcFloats[f * MixChannels + 1];
                var dstFrameStart = f * outputChannelCount;
                foreach (var pair in pairs)
                {
                    var lCh = pair * 2;
                    var rCh = pair * 2 + 1;
                    if (lCh < outputChannelCount) dstFloats[dstFrameStart + lCh] = l;
                    if (rCh < outputChannelCount) dstFloats[dstFrameStart + rCh] = r;
                }
            }

            return count;
        }
    }
}
