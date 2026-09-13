using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RemSound.Core;

namespace RemSound.Sender;

/// <summary>
/// One capture source feeding the mixer. Wraps a single <see cref="IWaveIn"/> capture and produces
/// 48 kHz stereo float samples through an NAudio sample-provider chain.
///
/// Pipeline:
///   capture — WasapiCapture for an input (event-sync, 10 ms buffer),
///             LowLatencyWasapiLoopbackCapture for an output device (10 ms buffer),
///             ProcessLoopbackCapture for one application (20 ms buffer)
///     → BufferedWaveProvider  (250 ms ring; ReadFully=true pads with silence on underflow,
///                              DiscardOnBufferOverflow=true drops oldest on overflow)
///     → ToSampleProvider      (bytes → floats)
///     → WdlResamplingSampleProvider (any rate → 48 kHz)
///     → StereoMixDown         (any channel layout → stereo)
///     → CaptureDriftCorrector (holds this source on the mix clock while more than one is live)
///
/// The <see cref="Provider"/> exposes that final 48 kHz stereo float stream so the mixing engine
/// can plug it into NAudio's <see cref="MixingSampleProvider"/>.
///
/// Threading: the capture delivers on its own dedicated thread. We push samples into a
/// thread-safe BufferedWaveProvider; the mixer's pull thread reads from the sample-provider
/// chain. Standard NAudio idiom — well-tested and avoids hand-rolling SPSC ring buffers.
///
/// Per-source clock drift across independent audio devices IS unavoidable
/// (https://rogueamoeba.com/support/knowledgebase/?showArticle=Loopback-AggregateDeviceHandling).
/// A lone source has nothing to drift against; with several live, each one's CaptureDriftCorrector
/// resamples it onto the mixer's clock — see the constructor.
/// </summary>
internal sealed class CaptureSource : IDisposable
{
    private const int MixSampleRate = 48000;
    private const int MixChannels = 2;
    /// <summary>How much audio the capture device collects before handing it over. Internal (not
    /// private) because the end-to-end latency estimate MUST count it — it was omitted entirely, which
    /// is why the app under-reported the delay by a full capture buffer (Ed's friend heard ~40 ms while
    /// the probe claimed 23; his ears were right and our number was wrong, 2026-08-15).</summary>
    internal const int CaptureBufferMs = 10;
    private const int RingBufferMs = 250;

    private readonly IWaveIn capture;
    private readonly BufferedWaveProvider buffer;
    private readonly Action<string>? onDiagnostic;
    private long callbackCount;
    private long bytesCaptured;
    private string? lastError;

    public string Name { get; }
    public CaptureKind Kind { get; }
    public string DeviceId { get; }
    public ISampleProvider Provider { get; }
    public string CaptureFormatDescription { get; }

    public long CallbackCount => Interlocked.Read(ref callbackCount);
    public long BytesCaptured => Interlocked.Read(ref bytesCaptured);
    public string? LastError => lastError;
    public int BufferedMilliseconds =>
        (int)(buffer.BufferedDuration.TotalMilliseconds);

    /// <summary>A count of the device's own bytes, expressed as the bytes of 48 kHz stereo float they
    /// become once resampled and mixed down: the units the drift corrector counts its pulls in. Channel
    /// count and sample size change how many bytes a frame takes, not how many frames there are; the
    /// sample rate changes how many frames there are.</summary>
    internal static long ToMixEquivalentBytes(long deviceBytes, WaveFormat format)
    {
        var frames = deviceBytes / Math.Max(1, (int)format.BlockAlign);
        var mixFrames = (long)Math.Round(frames * (double)MixSampleRate / Math.Max(1, format.SampleRate));
        return mixFrames * MixChannels * sizeof(float);
    }

    /// <summary>Set by the mixing engine: true only while more than one source is live. A lone source
    /// has nothing to stay aligned WITH, so its drift corrector stays idle at ratio 1.0 and the
    /// commonest setup of all behaves exactly as it always has.</summary>
    internal Func<bool>? CorrectionWanted { get; set; }

    /// <summary>This source's drift corrector, so the mixer can report what it is applying.</summary>
    internal CaptureDriftCorrector? DriftCorrector { get; private set; }

    /// <summary>Rate ratio currently applied to hold this source on the mix clock. 1.0 = untouched.
    /// Surfaced on the diag line so per-source drift is visible instead of only audible.</summary>
    public double AppliedDriftRatio => DriftCorrector?.AppliedRatio ?? 1.0;

    public CaptureSource(MMDevice device, CaptureKind kind, string displayName, Action<string>? onDiagnostic = null)
        : this(
            kind == CaptureKind.Loopback
                ? new LowLatencyWasapiLoopbackCapture(device, CaptureBufferMs, onDiagnostic)
                : (IWaveIn)new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: CaptureBufferMs),
            kind, device.ID, displayName, onDiagnostic)
    {
    }

    /// <summary>Wraps an arbitrary <see cref="IWaveIn"/> capture — used for per-application
    /// process-loopback (<see cref="ProcessLoopbackCapture"/>), where the source isn't an
    /// <see cref="MMDevice"/> and <paramref name="deviceId"/> is the synthetic <c>"proc:&lt;pid&gt;"</c> id.</summary>
    /// <param name="driftWindowSec">The drift corrector's measurement window. Overridable ONLY so the
    /// gate can see a correction in seconds; every shipped caller takes the default.</param>
    public CaptureSource(IWaveIn waveIn, CaptureKind kind, string deviceId, string displayName, Action<string>? onDiagnostic = null,
        double driftWindowSec = CaptureDriftCorrector.DefaultMeasurementWindowSec)
    {
        Name = displayName;
        Kind = kind;
        DeviceId = deviceId;
        this.onDiagnostic = onDiagnostic;

        capture = waveIn;

        var captureFormat = capture.WaveFormat;
        CaptureFormatDescription =
            $"{captureFormat.SampleRate} Hz, {captureFormat.Channels} ch, {captureFormat.BitsPerSample}-bit "
            + (captureFormat.Encoding == WaveFormatEncoding.IeeeFloat ? "float" : captureFormat.Encoding.ToString());

        buffer = new BufferedWaveProvider(captureFormat)
        {
            ReadFully = true,
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromMilliseconds(RingBufferMs),
        };

        ISampleProvider sp = buffer.ToSampleProvider();
        if (sp.WaveFormat.SampleRate != MixSampleRate)
        {
            sp = new WdlResamplingSampleProvider(sp, MixSampleRate);
        }
        if (sp.WaveFormat.Channels != MixChannels)
        {
            sp = new StereoMixDownSampleProvider(sp);
        }
        // LAST in the chain, on the pull side: hold this device on the mixer's clock so several
        // sources summed into one stream stay aligned with each other. Without it the only thing that
        // ever corrected a drifting source was the ring hitting 250 ms and discarding, or emptying and
        // padding silence — a jump or a hole, and only after a quarter-second of error. Idle (ratio
        // pinned at 1.0) while this is the only live source. See CaptureDriftCorrector. 2026-09-07.
        //
        // FED IS COUNTED IN THE CORRECTOR'S UNITS, NOT THE DEVICE'S. The corrector counts what it pulls as
        // 48 kHz stereo float, and it used to be handed the device's own byte count to compare against:
        // a 44.1 kHz source read as 8 % slow, a mono or 16-bit one as half speed, a 96 kHz one as double.
        // Every one of those is outside the ±5 % sanity band, so every window was thrown away and the
        // correction never ran for most real devices, without a word in the log. Only a 48 kHz stereo
        // float source ever measured right — which is exactly what the test fed it. 2026-09-13 review.
        var drift = new CaptureDriftCorrector(
            sp,
            () => BufferedMilliseconds,
            () => ToMixEquivalentBytes(BytesCaptured, captureFormat),
            () => CorrectionWanted?.Invoke() ?? false,
            displayName,
            driftWindowSec,
            onDiagnostic);
        DriftCorrector = drift;
        Provider = drift;

        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += OnRecordingStopped;
    }

    public void Start()
    {
        try
        {
            capture.StartRecording();
            onDiagnostic?.Invoke($"capture started \"{Name}\" ({Kind}) at {CaptureFormatDescription}");
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
            onDiagnostic?.Invoke($"capture start failed for \"{Name}\": {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    public void Stop()
    {
        try { capture.StopRecording(); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        Stop();
        capture.DataAvailable -= OnDataAvailable;
        capture.RecordingStopped -= OnRecordingStopped;
        capture.Dispose();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        Interlocked.Increment(ref callbackCount);
        Interlocked.Add(ref bytesCaptured, e.BytesRecorded);
        if (e.BytesRecorded <= 0) return;
        try
        {
            buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
            onDiagnostic?.Invoke($"capture buffer error for \"{Name}\": {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The capture stopped. An exception here means nobody asked it to — the device was taken, the
    /// audio service restarted, or (for a per-application source) the process-loopback client
    /// errored out and its thread ended. Flag it so <see cref="MixingEngine"/> can drop this source
    /// and re-open it, instead of leaving a dead source in the mix quietly contributing silence for
    /// the rest of the session. 2026-08-23 audit, findings S6 and S7.
    /// </summary>
    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            lastError = e.Exception.Message;
            Faulted = true;
            onDiagnostic?.Invoke($"capture DIED for \"{Name}\" (not a requested stop): {e.Exception.GetType().Name}: {e.Exception.Message}");
        }
    }

    /// <summary>True when this source's capture stopped on its own with an error. The mixer reads it
    /// to evict and re-open the source; nothing else clears it, because a faulted CaptureSource is
    /// always thrown away rather than revived.</summary>
    public bool Faulted { get; private set; }

    /// <summary>How long audio waits inside THIS source's device, as the device reports it. Zero for
    /// a per-application capture, which has no device of its own — Windows mixes it for us and there
    /// is nothing to ask. See <see cref="ICaptureBackend.ReportedInputLatencyMs"/>. 2026-08-24.</summary>
    public double ReportedInputLatencyMs { get; init; }

    /// <summary>
    /// Down-mixes any channel layout to stereo. Mono is duplicated to L=R; stereo passes through;
    /// multi-channel (5.1, 7.1, etc.) takes the front L/R channels (a basic "front-pair" pick,
    /// not a full ITU down-mix matrix).
    /// </summary>
    private sealed class StereoMixDownSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider source;
        private float[] sourceBuffer = new float[4096];

        public StereoMixDownSampleProvider(ISampleProvider source)
        {
            this.source = source;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var frames = count / 2;
            var sourceChannels = source.WaveFormat.Channels;
            var sourceFloats = frames * sourceChannels;
            if (sourceBuffer.Length < sourceFloats) sourceBuffer = new float[sourceFloats];
            var read = source.Read(sourceBuffer, 0, sourceFloats) / Math.Max(sourceChannels, 1);
            var written = 0;
            for (var i = 0; i < read; i++)
            {
                if (sourceChannels == 1)
                {
                    var s = sourceBuffer[i];
                    buffer[offset + written++] = s;
                    buffer[offset + written++] = s;
                }
                else
                {
                    buffer[offset + written++] = sourceBuffer[i * sourceChannels];
                    buffer[offset + written++] = sourceBuffer[i * sourceChannels + 1];
                }
            }
            if (written < count) Array.Clear(buffer, offset + written, count - written);
            return count;
        }
    }
}
