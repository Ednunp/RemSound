using System.Diagnostics;
using NAudio.Wave;
using NAudio.Wave.Asio;
using RemSound.Core;

namespace RemSound.Sender;

/// <summary>
/// ASIO capture backend. Drives a single <see cref="AsioOut"/> for the chosen ASIO driver and
/// produces 48 kHz stereo float frames in the same shape <see cref="MixingEngine"/> does, so
/// <see cref="AudioSender"/> doesn't care which backend is active.
///
/// Spec identity: each <see cref="CaptureSourceSpec"/> for ASIO uses a synthetic
/// <c>DeviceId</c> of the form <c>"asio:&lt;channel-pair-index&gt;"</c>. Channel pair 0 = ASIO
/// channels 0+1, pair 1 = channels 2+3, etc. The driver is implicit (a single driver per
/// session, configured through the Connectivity &amp; transport dialog).
///
/// Limitations vs the WASAPI backend (deliberate to keep this manageable):
///   • Driver is locked at <see cref="Start"/> time. Switching drivers means Stop + new instance.
///   • We always open the AsioOut with the driver's full input channel count, regardless of
///     which pairs the user selected. The unused channels are pulled but discarded. This
///     trades a tiny amount of buffer memory for a big stability win: adding or removing a
///     channel pair never requires reopening the driver, which means we don't fight a
///     concurrent receiver-side AsioOut on single-client drivers (Komplete Audio etc.).
///   • Sample rate is fixed at 48 kHz; if the driver doesn't support that, capture fails to
///     start (the diagnostic line says so). All modern pro audio interfaces support 48 kHz.
///   • Hardware loopback channels (e.g. EVO 8's Loop-back 1/2) are just regular ASIO inputs
///     from our perspective; they live in the same channel space and are picked the same way.
/// </summary>
internal sealed class AsioCaptureBackend : ICaptureBackend
{
    private const int MixSampleRate = 48000;
    private const int MixChannels = 2;

    // Volatile-published callback. The ASIO audio thread reads this every callback to
    // decide where to deliver samples; AudioSender swaps it on mode changes so the same
    // open driver can keep running while routing changes between Mixed / AsioLane / no-op.
    // Volatile is sufficient for reference assignment on .NET (atomic, with memory barrier).
    private volatile Action<ReadOnlyMemory<float>> onMixedSamples;
    // Raw-capture step probe — measures discontinuities in the ASIO buffer exactly as the
    // driver delivered it, BEFORE our code sums the selected channel pairs or clamps to ±1.0.
    // Each capture backend owns its own probe so BothIndependent mode (ASIO and WASAPI both
    // capturing) can be diagnosed without the probes contaminating each other's state.
    private readonly AudioStepProbe rawCaptureStepProbe = new();
    private readonly Action<string>? onDiagnostic;
    private readonly string driverName;
    public string DriverName => driverName;
    private readonly object gate = new();

    private AsioOut? asio;
    // Every AsioOut control call (create / init / play / stop / dispose) is marshalled onto this one
    // dedicated STA+message-pump thread. That single-threaded, pumped home is what lets the driver close
    // WITHOUT the native crash we used to hit on "ASIO → none" and driver switches — see AsioApartment.
    // Created in the ctor so it can announce its own thread up/down through the same diagnostic sink.
    private readonly AsioApartment apartment;
    private List<int> activeChannelPairIndices = [];
    private int recordChannelCount;
    private float[] mixScratch = new float[1024];
    private float[] interleavedScratch = new float[1024];

    private long callbackCount;
    private long bytesCaptured;
    private long clippedSampleCount;
    private string? lastError;
    private string? captureFormat;
    private readonly Stopwatch uptime = new();
    // Per-callback gap tracking. The ASIO callback should fire on a strict period (= buffer
    // size in samples / sample rate). When the .NET runtime, GC, USB driver, or Windows
    // scheduler stalls the audio thread, that period stretches and the audio stream gets a
    // discontinuity — which the receiver can't detect because it just sees a packet arrive
    // late. We measure the elapsed time between consecutive callbacks here, track the worst
    // since the last read, and let the sender's diag logger surface it. Plain int; access
    // is via Interlocked which provides its own memory barriers (no need for volatile).
    private long lastCallbackTimestamp;
    private int maxCallbackGapMs;
    // Cumulative ticks the ASIO capture callback spent doing per-callback work. The diag
    // log samples this once a second; per-thread CPU instrumentation from item 2 of
    // RemSoundefficiency.md. Gated by DiagnosticsGate.Enabled so logs-off costs nothing.
    // 2026-05-22.
    private long cumulativeCaptureTicks;

    public AsioCaptureBackend(string driverName, Action<ReadOnlyMemory<float>> onMixedSamples, Action<string>? onDiagnostic = null)
    {
        this.driverName = driverName;
        this.onMixedSamples = onMixedSamples;
        this.onDiagnostic = onDiagnostic;
        apartment = new AsioApartment($"asio-control:{driverName}", onDiagnostic);
    }

    /// <summary>
    /// Swap the callback that captured audio is delivered to. Used by AudioSender to keep
    /// one persistent AsioCaptureBackend instance alive across audio-mode changes — the
    /// driver stays open, the callback gets rewired to the lane appropriate for the new
    /// mode (Mixed in AsioOnly, AsioLane in BothIndependent, or a no-op while the
    /// composite is being rebuilt). Volatile write, so the audio thread picks the new
    /// callback up on its very next ASIO buffer.
    /// </summary>
    public void SetCallback(Action<ReadOnlyMemory<float>> callback) =>
        onMixedSamples = callback;

    public float TakeMaxRawCaptureStep() => rawCaptureStepProbe.TakeMax();
    public float TakeMaxRawCaptureStepCrossBuffer() => rawCaptureStepProbe.TakeMaxCrossBuffer();
    public float TakeMaxRawCaptureStepWithinBuffer() => rawCaptureStepProbe.TakeMaxWithinBuffer();
    public long TakeCumulativeCaptureTicks() => Interlocked.Exchange(ref cumulativeCaptureTicks, 0);

    public bool IsRunning => asio is not null;
    public long TotalCaptureCallbacks => Interlocked.Read(ref callbackCount);
    public long TotalCaptureBytes => Interlocked.Read(ref bytesCaptured);
    public string? FirstCaptureFormatDescription => captureFormat;
    public string? FirstCaptureLastError => lastError;
    public long ClippedSampleCount => Interlocked.Read(ref clippedSampleCount);

    public IReadOnlyList<string> ActiveSourceNames
    {
        get
        {
            lock (gate)
            {
                return activeChannelPairIndices
                    .Select(p => $"{driverName} ASIO {p * 2 + 1}/{p * 2 + 2}")
                    .ToList();
            }
        }
    }

    public void Start(IReadOnlyList<CaptureSourceSpec> specs)
    {
        lock (gate)
        {
            if (IsRunning) StopInternal();
            if (specs.Count == 0) return;

            activeChannelPairIndices = ParseChannelPairIndices(specs);
            if (activeChannelPairIndices.Count == 0)
            {
                onDiagnostic?.Invoke("asio capture: no valid channel pair indices in spec list");
                return;
            }

            try
            {
                // Open + init + play, ALL on the ASIO apartment thread (see the apartment field). A zero-
                // channel driver becomes a throw so the catch below runs the same StopInternal cleanup.
                apartment.Invoke(() =>
                {
                    // Step-by-step breadcrumbs, symmetric with the close path in StopInternal. If a
                    // native call here takes the process down (as the driver used to do on close), the
                    // last line in the log names exactly which stage died — with AutoFlush on, each line
                    // is on disk before the next native call runs.
                    onDiagnostic?.Invoke($"asio open: creating driver \"{driverName}\"");
                    asio = new AsioOut(driverName);
                    // Always open with the driver's full input channel count. Pulling channels we
                    // don't immediately need is essentially free — the driver fills them anyway —
                    // and it removes the need to ever reopen the AsioOut when the user toggles a
                    // higher-numbered channel pair. Reopening is what previously caused 15-second
                    // freezes when both sender and receiver held the same single-client driver
                    // (Komplete Audio etc.) — see Andre's localhost lockup, 2026-04-30.
                    recordChannelCount = asio.DriverInputChannelCount;
                    if (recordChannelCount <= 0)
                        throw new InvalidOperationException($"driver \"{driverName}\" reports zero input channels");
                    asio.InputChannelOffset = 0;
                    // Sanity-check that the requested pairs are within the driver's channel range.
                    // We open the full count anyway, but if a saved spec references a pair above
                    // the driver's range, the OnAudioAvailable mixer would silently emit zero —
                    // surface that as a diagnostic so it's not mysterious.
                    var maxPairIndex = activeChannelPairIndices.Max();
                    var highestNeededChannel = (maxPairIndex + 1) * 2;
                    if (highestNeededChannel > recordChannelCount)
                        onDiagnostic?.Invoke($"asio capture: driver \"{driverName}\" only has {recordChannelCount} input channels, but spec requests channel pair {maxPairIndex} (channels {maxPairIndex * 2 + 1}/{maxPairIndex * 2 + 2})");
                    onDiagnostic?.Invoke($"asio open: init record+playback ({recordChannelCount} ch @ {MixSampleRate} Hz)");
                    asio.InitRecordAndPlayback(null, recordChannelCount, MixSampleRate);
                    asio.AudioAvailable += OnAudioAvailable;
                    captureFormat = $"{MixSampleRate} Hz, {recordChannelCount} input channel(s), 32-bit float (ASIO)";
                    onDiagnostic?.Invoke("asio open: starting stream (play)");
                    asio.Play();
                    onDiagnostic?.Invoke("asio open: stream running");
                });
                uptime.Restart();
                onDiagnostic?.Invoke($"asio capture started \"{driverName}\" {captureFormat}; pairs={string.Join(",", activeChannelPairIndices)}");
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                onDiagnostic?.Invoke($"asio capture start failed: {ex.GetType().Name}: {ex.Message}");
                StopInternal();
            }
        }
    }

    public void UpdateSources(IReadOnlyList<CaptureSourceSpec> specs)
    {
        lock (gate)
        {
            if (!IsRunning)
            {
                Start(specs);
                return;
            }
            var newPairs = ParseChannelPairIndices(specs);
            // No reopen needed regardless of which pairs change. We always opened the driver
            // with its full input channel count at Start, so adding or removing a pair is just
            // a matter of which input channels the OnAudioAvailable mixer reads from. Even
            // when the new pair set is empty we DO NOT close the driver here — Audient's
            // ASIO driver (and several others) doesn't tolerate a close+reopen within a few
            // seconds, which is exactly the pattern the user produces by unticking the last
            // ASIO source and then ticking another one. Keeping the driver open with zero
            // active pairs makes the callback fire harmlessly (zeros) and the next pair
            // addition takes effect on the very next callback. Toggling pairs never closes the
            // driver; the actual close happens elsewhere — Stop()/Dispose() (sender disabled or
            // app exit) and AudioSender releasing the driver when ASIO is deselected or the user
            // picks a different driver — all of which are now safe on the apartment thread.
            activeChannelPairIndices = newPairs;
            onDiagnostic?.Invoke($"asio capture: pairs updated to [{string.Join(",", activeChannelPairIndices)}] (no driver restart)");
        }
    }

    public void Stop()
    {
        lock (gate) StopInternal();
    }

    private void StopInternal()
    {
        var toClose = asio;
        if (toClose is not null)
        {
            // Close the driver on the ASIO apartment thread — the same single, pumped thread it was opened
            // on. That is the fix for the native access violation that used to kill the process here (it
            // blew past these try/catch blocks with no managed stack). Step-by-step logging still pinpoints
            // any native call that dies, and the callback is unhooked + drained before stop/dispose so the
            // close isn't racing a live buffer callback (a common trigger for the crash).
            apartment.Invoke(() =>
            {
                onDiagnostic?.Invoke("asio close: unhooking callback");
                try { toClose.AudioAvailable -= OnAudioAvailable; } catch { /* ignore */ }
                System.Threading.Thread.Sleep(60);
                onDiagnostic?.Invoke("asio close: stopping stream");
                try { toClose.Stop(); } catch (Exception ex) { onDiagnostic?.Invoke($"asio close: stop threw {ex.GetType().Name}: {ex.Message}"); }
                onDiagnostic?.Invoke("asio close: releasing driver (dispose)");
                try { toClose.Dispose(); } catch (Exception ex) { onDiagnostic?.Invoke($"asio close: dispose threw {ex.GetType().Name}: {ex.Message}"); }
                onDiagnostic?.Invoke("asio close: driver released cleanly");
            });
            asio = null;
        }
        uptime.Stop();
        activeChannelPairIndices = [];
        recordChannelCount = 0;
    }

    public void Dispose()
    {
        Stop();
        apartment.Dispose(); // shut down the dedicated ASIO thread last, after the driver is closed
    }

    private static List<int> ParseChannelPairIndices(IReadOnlyList<CaptureSourceSpec> specs)
    {
        var result = new List<int>();
        foreach (var spec in specs)
        {
            if (AsioDeviceId.TryParse(spec.DeviceId, out var pair))
            {
                result.Add(pair);
            }
        }
        result.Sort();
        return result.Distinct().ToList();
    }

    public int TakeMaxCallbackGapMs() => Interlocked.Exchange(ref maxCallbackGapMs, 0);

    private void OnAudioAvailable(object? sender, AsioAudioAvailableEventArgs e)
    {
        Interlocked.Increment(ref callbackCount);
        // Capture-callback gap timing. First callback seeds the timestamp without recording a
        // gap (we have nothing to compare to). Subsequent callbacks compute the elapsed ms
        // since the previous one and CAS-update the max. Skipped entirely when diagnostics
        // are off — saves the Stopwatch reads, exchange and CAS loop on every ASIO callback.
        var diag = RemSound.Core.DiagnosticsGate.Enabled;
        long workStart = 0;
        if (diag)
        {
            var now = Stopwatch.GetTimestamp();
            workStart = now;
            var prev = Interlocked.Exchange(ref lastCallbackTimestamp, now);
            if (prev != 0)
            {
                var gapMs = (int)((now - prev) * 1000 / Stopwatch.Frequency);
                int current;
                do
                {
                    current = Volatile.Read(ref maxCallbackGapMs);
                    if (gapMs <= current) break;
                } while (Interlocked.CompareExchange(ref maxCallbackGapMs, gapMs, current) != current);
            }
        }
        // Pull all interleaved float samples for the recorded channels into a reusable buffer.
        var samplesNeeded = e.SamplesPerBuffer * e.InputBuffers.Length;
        if (interleavedScratch.Length < samplesNeeded) interleavedScratch = new float[samplesNeeded];
        var written = e.GetAsInterleavedSamples(interleavedScratch);
        Interlocked.Add(ref bytesCaptured, written * sizeof(float));
        var interleaved = interleavedScratch;

        // Frame count = total samples / channel count.
        var frames = written / Math.Max(1, recordChannelCount);
        var stereoFloats = frames * MixChannels;
        if (mixScratch.Length < stereoFloats) mixScratch = new float[stereoFloats];
        Array.Clear(mixScratch, 0, stereoFloats);

        // Mix selected channel pairs into the stereo output. Each pair contributes its L/R to
        // the mix bus.
        List<int> pairs;
        lock (gate) pairs = activeChannelPairIndices;

        if (pairs.Count == 0) return;

        // Diagnostic raw-capture probe — scans the FIRST active channel pair's L channel in
        // the as-delivered-by-the-driver interleaved buffer. Fires BEFORE the mix/sum/clamp
        // below so the probe sees the driver's data verbatim. If this probe goes non-zero
        // on big steps while the post-mix probe also does, the discontinuity is upstream of
        // our code (driver, USB transport, audio hardware). If it stays clean while the
        // post-mix probe goes non-zero, something in the mix/clamp loop is creating the step.
        if (frames > 0 && recordChannelCount > 0)
        {
            var firstPair = pairs[0];
            var lCh = firstPair * 2;
            if (lCh < recordChannelCount)
            {
                rawCaptureStepProbe.ScanInterleavedChannel(
                    new ReadOnlySpan<float>(interleavedScratch, 0, written), recordChannelCount, lCh);
            }
        }

        for (var f = 0; f < frames; f++)
        {
            var srcBase = f * recordChannelCount;
            var dstBase = f * MixChannels;
            float l = 0f, r = 0f;
            foreach (var pair in pairs)
            {
                var lCh = pair * 2;
                var rCh = pair * 2 + 1;
                if (lCh < recordChannelCount) l += interleaved[srcBase + lCh];
                if (rCh < recordChannelCount) r += interleaved[srcBase + rCh];
            }
            // Soft-limit-ish clamp at the encoder boundary; matches MixingEngine.
            if (l > 1f) { l = 1f; Interlocked.Increment(ref clippedSampleCount); }
            else if (l < -1f) { l = -1f; Interlocked.Increment(ref clippedSampleCount); }
            if (r > 1f) { r = 1f; Interlocked.Increment(ref clippedSampleCount); }
            else if (r < -1f) { r = -1f; Interlocked.Increment(ref clippedSampleCount); }
            mixScratch[dstBase] = l;
            mixScratch[dstBase + 1] = r;
        }

        onMixedSamples(new ReadOnlyMemory<float>(mixScratch, 0, stereoFloats));
        // Capture-thread CPU instrumentation (item 2 of RemSoundefficiency.md). Records
        // the time the WHOLE callback spent — including the synchronous downstream
        // OnMixedSamples invocation, because that runs on this same thread and counts
        // toward "the capture thread's per-second CPU load". Send-side encode work is
        // ALSO tallied separately via AudioSender.cumulativeEmitTicks for a more detailed
        // breakdown; capture vs send columns let us see "is the bottleneck the buffer
        // copy + mix loop, or is it encode + sendto".
        if (diag) Interlocked.Add(ref cumulativeCaptureTicks, Stopwatch.GetTimestamp() - workStart);
    }
}
