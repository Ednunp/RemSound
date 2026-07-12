using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RemSound.Core;

namespace RemSound.Sender;

/// <summary>
/// One capture source feeding the mixer. Wraps a single <see cref="WasapiCapture"/> (loopback or
/// direct input) and produces 48 kHz stereo float samples through an NAudio sample-provider chain.
///
/// Pipeline:
///   WasapiCapture (event-sync, 10 ms buffer)
///     → BufferedWaveProvider  (250 ms ring; ReadFully=true pads with silence on underflow,
///                              DiscardOnBufferOverflow=true drops oldest on overflow)
///     → ToSampleProvider      (bytes → floats)
///     → WdlResamplingSampleProvider (any rate → 48 kHz)
///     → StereoMixDown         (any channel layout → stereo)
///
/// The <see cref="Provider"/> exposes that final 48 kHz stereo float stream so the mixing engine
/// can plug it into NAudio's <see cref="MixingSampleProvider"/>.
///
/// Threading: NAudio's capture event runs on its own dedicated thread. We push samples into a
/// thread-safe BufferedWaveProvider; the mixer's pull thread reads from the sample-provider
/// chain. Standard NAudio idiom — well-tested and avoids hand-rolling SPSC ring buffers.
///
/// Per-source clock drift across independent audio devices IS unavoidable
/// (https://rogueamoeba.com/support/knowledgebase/?showArticle=Loopback-AggregateDeviceHandling)
/// but the 250 ms ring + automatic discard-on-overflow tolerates it for realistic session
/// lengths. A proper drift-correcting micro-resample is a future addition.
/// </summary>
internal sealed class CaptureSource : IDisposable
{
    private const int MixSampleRate = 48000;
    private const int MixChannels = 2;
    private const int CaptureBufferMs = 10;
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

    public CaptureSource(MMDevice device, CaptureKind kind, string displayName, Action<string>? onDiagnostic = null)
        : this(
            kind == CaptureKind.Loopback
                ? new LowLatencyWasapiLoopbackCapture(device, audioBufferMilliseconds: CaptureBufferMs)
                : new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: CaptureBufferMs),
            kind, device.ID, displayName, onDiagnostic)
    {
    }

    /// <summary>Wraps an arbitrary <see cref="IWaveIn"/> capture — used for per-application
    /// process-loopback (<see cref="ProcessLoopbackCapture"/>), where the source isn't an
    /// <see cref="MMDevice"/> and <paramref name="deviceId"/> is the synthetic <c>"proc:&lt;pid&gt;"</c> id.</summary>
    public CaptureSource(IWaveIn waveIn, CaptureKind kind, string deviceId, string displayName, Action<string>? onDiagnostic = null)
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
        Provider = sp;

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

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            lastError = e.Exception.Message;
            onDiagnostic?.Invoke($"capture stopped with error for \"{Name}\": {e.Exception.GetType().Name}: {e.Exception.Message}");
        }
    }

    /// <summary>
    /// Down-mixes any channel layout to stereo. Mono is duplicated to L=R; stereo passes through;
    /// multi-channel (5.1, 7.1, etc.) takes the front L/R channels (a basic "front-pair" pick,
    /// not a full ITU down-mix matrix). Same approach as the legacy RSound build.
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
