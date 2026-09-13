using NAudio.Dsp;
using RemSound.Core;
using RemSound.Sender;

namespace RemSound.Plugin;

/// <summary>
/// The DAW as a capture source.
///
/// <para>RemSound's sender already talks to an <see cref="ICaptureBackend"/> that hands it 48 kHz
/// stereo float frames — that abstraction is what makes a plugin possible at all. WASAPI and ASIO
/// are two implementations; this is a third, where the "device" is whatever track the plugin sits on
/// and the frames arrive from the host's process callback instead of a driver.</para>
///
/// <para><b>Two rules, both load-bearing.</b></para>
///
/// <para>1. <b>Nothing allocates on the audio thread.</b> .NET has no real-time garbage collector, and
/// a collection stops every thread in the process — which inside a DAW means every plugin in the
/// session, not just ours. Every buffer here is allocated when the host announces its block size and
/// reused thereafter; the steady-state path allocates zero bytes. That property is asserted by the
/// gate, because it is the single most likely cause of a "RemSound makes my DAW crackle" report.</para>
///
/// <para>2. <b>The host is not necessarily running at 48 kHz.</b> Our wire format is, so a host at
/// 44.1 or 96 must be resampled at this boundary or the audio arrives transposed at the far end.
/// SonoBus ships with exactly this bug open (their issue #1: "the resampling is doing the
/// transposition"), which is a good reason to get it right on day one rather than later.</para>
/// </summary>
internal sealed class HostCaptureBackend : ICaptureBackend
{
    private const int WireSampleRate = RemSoundPlugin.WireSampleRate;
    private const int Channels = 2;

    private readonly Action<ReadOnlyMemory<float>> onMixedSamples;
    private readonly WdlResampler resampler = new();

    // Pre-allocated at PrepareForBlockSize, reused forever after. Sized for the worst case the host
    // declared, with headroom for the resampler asking for more output frames than input frames
    // (a 44.1k host produces ~1.09 output frames per input frame at 48k).
    private float[] interleavedIn = [];
    private float[] resampledOut = [];

    private double hostSampleRate;
    private long callbacks;
    private long bytes;
    private volatile bool running;

    public HostCaptureBackend(Action<ReadOnlyMemory<float>> onMixedSamples)
    {
        this.onMixedSamples = onMixedSamples;
        // Linear interpolation: the same low-cost mode the receiver's drift corrector uses. Ample for
        // a fixed whole-number-ish rate conversion, and cheap enough for the audio thread.
        resampler.SetMode(true, 0, false);
        resampler.SetFeedMode(true); // we push input and ask what came out
    }

    /// <summary>Allocate every buffer the audio thread will need, once, off the audio thread. Called
    /// from the host's block-size announcement — never from the process callback.</summary>
    public void PrepareForBlockSize(int maxFramesPerBlock, double sampleRate)
    {
        hostSampleRate = sampleRate <= 0 ? WireSampleRate : sampleRate;
        resampler.SetRates(hostSampleRate, WireSampleRate);

        var interleaved = Math.Max(1, maxFramesPerBlock) * Channels;
        // Output can exceed input when converting UP (44.1k -> 48k). Doubling is generous headroom
        // for any sane host rate and costs a few hundred kilobytes once.
        var outFloats = (int)Math.Ceiling(interleaved * (WireSampleRate / Math.Max(1.0, hostSampleRate)) * 2) + Channels * 8;
        if (interleavedIn.Length < interleaved) interleavedIn = new float[interleaved];
        if (resampledOut.Length < outFloats) resampledOut = new float[outFloats];
    }

    /// <summary>Hand the host's block for this track to RemSound's sender. Called on the DAW's audio
    /// thread — allocation-free by construction, see the class summary.</summary>
    /// <param name="gain">Send trim, 1 = unity. Applied HERE, in the same pass that interleaves and
    /// narrows to float, so it costs one multiply per sample and no extra pass over the block. It is
    /// applied to the copy going out and never to the host's own buffers, so the track keeps playing
    /// at its own level whatever the trim is set to — a send level that also turned the track down
    /// would be a surprise nobody asked for.</param>
    public void SubmitHostBlock(ReadOnlySpan<double> left, ReadOnlySpan<double> right, float gain = 1f)
    {
        if (!running) return;
        var frames = Math.Min(left.Length, right.Length);
        if (frames <= 0 || interleavedIn.Length < frames * Channels) return;

        // Interleave and narrow to float — the mix bus RemSound speaks everywhere else.
        for (var i = 0; i < frames; i++)
        {
            interleavedIn[i * Channels] = (float)left[i] * gain;
            interleavedIn[i * Channels + 1] = (float)right[i] * gain;
        }

        callbacks++;
        bytes += frames * Channels * sizeof(float);

        // Fast path: the host is already at our rate, so hand the frames straight over.
        if (Math.Abs(hostSampleRate - WireSampleRate) < 0.5)
        {
            onMixedSamples(new ReadOnlyMemory<float>(interleavedIn, 0, frames * Channels));
            return;
        }

        var produced = Resample(frames);
        if (produced > 0) onMixedSamples(new ReadOnlyMemory<float>(resampledOut, 0, produced * Channels));
    }

    /// <summary>Push one block through the resampler and report how many 48 kHz frames came out.
    /// The gate reaches it through <see cref="SubmitHostBlock"/> with a 44.1 kHz host and a known
    /// tone, and measures the pitch on the wire — "it ran" is not evidence that a resampler is
    /// correct.</summary>
    private int Resample(int inFrames)
    {
        var needed = resampler.ResamplePrepare(inFrames, Channels, out var inBuf, out var inOffset);
        var copy = Math.Min(needed, inFrames);
        for (var i = 0; i < copy * Channels; i++) inBuf[inOffset + i] = interleavedIn[i];
        var maxOutFrames = resampledOut.Length / Channels;
        return resampler.ResampleOut(resampledOut, 0, copy, maxOutFrames, Channels);
    }

    // ---- ICaptureBackend. There is no device here: the host owns it, so the device-shaped members
    // report the host rather than pretending to enumerate hardware. ----------------------------

    public bool IsRunning => running;

    /// <summary>There is no device to lose — the host owns it, and if the DAW stops calling us there
    /// is nothing here to re-open. Always false. See <see cref="ICaptureBackend.HasFaulted"/>.</summary>
    public bool HasFaulted => false;

    /// <summary>The DAW owns the device and does not tell us its latency, so we do not claim one.
    /// See <see cref="ICaptureBackend.ReportedInputLatencyMs"/>.</summary>
    public double ReportedInputLatencyMs => 0;
    public long TotalCaptureCallbacks => callbacks;
    public long TotalCaptureBytes => bytes;
    public string? FirstCaptureFormatDescription => $"{hostSampleRate:0} Hz, {Channels} ch, host-driven (DAW)";
    public string? FirstCaptureLastError => null;
    public long ClippedSampleCount => 0;
    public IReadOnlyList<string> ActiveSourceNames => running ? ["DAW track"] : [];

    /// <summary>Callback-gap timing belongs to the host's scheduler, not to us — reporting a number
    /// we don't measure would be a constant dressed as a measurement, which is exactly the mistake
    /// the latency estimate made (2026-08-15).</summary>
    public int TakeMaxCallbackGapMs() => 0;
    public float TakeMaxRawCaptureStepCrossBuffer() => 0;
    public float TakeMaxRawCaptureStepWithinBuffer() => 0;
    public long TakeCumulativeCaptureTicks() => 0;

    /// <summary>No device to open — the host is already running. Specs are ignored: in a plugin the
    /// source is the track this instance sits on, and there is nothing to choose.</summary>
    public void Start(IReadOnlyList<CaptureSourceSpec> specs) => running = true;
    public void UpdateSources(IReadOnlyList<CaptureSourceSpec> specs) { }
    public void Stop() => running = false;
    public void Dispose() => running = false;
}
