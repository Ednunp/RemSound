using NAudio.Dsp;
using NAudio.Wave;
using RemSound.Core;

namespace RemSound.Sender;

/// <summary>
/// Holds ONE capture source on the mixer's clock, so several sources summed into one stream stay
/// aligned with each other for the length of a session.
///
/// <para><b>The problem.</b> Every capture device has its own crystal, and the mix loop pulls a fixed
/// 10 ms per tick paced by a Stopwatch that belongs to none of them. So each source runs slightly
/// fast or slightly slow against the mix clock, and two sources drift apart from each other at the
/// difference of their errors. At a very ordinary 50 ppm that is about 3 ms a minute — roughly 18 ms
/// after six minutes, and it keeps going.</para>
///
/// <para>Before this, nothing corrected it. A source running fast filled its 250 ms ring until
/// <c>DiscardOnBufferOverflow</c> threw the oldest audio away — that source jumping forward. A source
/// running slow emptied its ring and <c>ReadFully</c> padded the gap with silence. Both are audible
/// events rather than corrections, and neither happens until the error is already a quarter of a
/// second. <c>CaptureSource</c>'s own class comment has said so all along: "Per-source clock drift
/// across independent audio devices IS unavoidable ... A proper drift-correcting micro-resample is a
/// future addition." Anthony Reyers raised it from the field, 2026-09; this is that addition.</para>
///
/// <para><b>The mechanism</b> is literally the one this codebase already runs on the receive side and
/// in the plugin's second-DAW lane: <see cref="DriftRatioTracker"/> in Core. Measure how much the
/// producer fed against how much the consumer pulled over a ten-second window, discard the start-up
/// window and any reading outside ±5 %, smooth 70/30, and add a bounded depth term steering the ring
/// toward a small cushion. It is the same code, not a copy of it — this file was written with its own
/// copy first, and that copy silently lacked the first-window discard, the sanity gate and the
/// depth-bias clamp that the receive side had learned the hard way over months in the field.</para>
///
/// <para><b>Pull side only.</b> The correction runs where the mixer reads, never where the device
/// writes. The receive-side file records that a producer-side attempt starved the cushion to zero and
/// crackled; there is no reason this would behave differently.</para>
///
/// <para><b>A single source is left completely alone.</b> With nothing to drift against, correcting is
/// all risk and no benefit — so the ratio stays exactly 1.0 until the mixer says more than one source
/// is live. That keeps the overwhelmingly common setup bit-for-bit as it was.</para>
/// </summary>
internal sealed class CaptureDriftCorrector : ISampleProvider
{
    /// <summary>The shipped measurement window — the shared one, not a second copy of the number.
    /// Ten seconds is long enough that one lumpy callback cannot be mistaken for a clock error.</summary>
    public const double DefaultMeasurementWindowSec = DriftRatioTracker.MeasurementWindowSec;

    /// <summary>The cushion the corrector aims to hold. Small — this is a capture ring being emptied
    /// every 10 ms, not a device buffer — but enough to absorb one lumpy callback.</summary>
    private const int TargetDepthMs = 30;
    private const int Channels = 2;
    private const int SampleRate = 48000;

    private readonly ISampleProvider upstream;
    private readonly Func<int> bufferedMs;
    private readonly Func<bool> correctionWanted;
    private readonly string name;
    private readonly Action<string>? onDiagnostic;

    private readonly WdlResampler resampler = new();
    private float[] inputScratch = [];

    private readonly DriftRatioTracker tracker;
    private long pulledFrames;
    private bool everCorrected;

    /// <summary>The rate ratio in force right now. 1.0 means untouched. Surfaced so the gate can
    /// assert the correction really engages, and so the diag line can show it per source.</summary>
    public double AppliedRatio { get; private set; } = 1.0;

    public WaveFormat WaveFormat => upstream.WaveFormat;

    /// <param name="bufferedMs">The source ring's current depth, for the cushion term.</param>
    /// <param name="correctionWanted">False while this is the only live source — see the class note
    /// on leaving a single source alone.</param>
    /// <param name="fedBytes">Total bytes the device has captured, for the feed side of the ratio.</param>
    public CaptureDriftCorrector(
        ISampleProvider upstream,
        Func<int> bufferedMs,
        Func<long> fedBytes,
        Func<bool> correctionWanted,
        string name,
        double measurementWindowSec = DefaultMeasurementWindowSec,
        Action<string>? onDiagnostic = null)
    {
        this.upstream = upstream;
        this.bufferedMs = bufferedMs;
        this.correctionWanted = correctionWanted;
        this.name = name;
        // Overridable ONLY so the gate can measure a real correction in seconds rather than minutes.
        // The shipped value is pinned by the test that uses this.
        tracker = new DriftRatioTracker(SampleRate, measurementWindowSec);
        this.onDiagnostic = onDiagnostic;
        FedBytes = fedBytes;

        resampler.SetMode(interp: true, filtercnt: 0, sinc: false);
        resampler.SetFeedMode(false);   // output-driven: we ask for N frames out
        resampler.SetRates(SampleRate, SampleRate);
    }

    private Func<long> FedBytes { get; }

    public int Read(float[] destination, int offset, int count)
    {
        var outFrames = count / Channels;
        if (outFrames <= 0) return 0;

        // ONE SOURCE: hand the upstream straight through, ratio pinned at 1.0. Nothing to align
        // against, so any correction here would be pure risk on the commonest setup of all.
        if (!correctionWanted())
        {
            if (AppliedRatio != 1.0)
            {
                AppliedRatio = 1.0;
                resampler.SetRates(SampleRate, SampleRate);
                tracker.Reset();
            }
            return upstream.Read(destination, offset, count);
        }

        pulledFrames += outFrames;
        UpdateRatioIfDue();

        var inputFramesNeeded = resampler.ResamplePrepare(outFrames, Channels, out var inBuf, out var inBufOff);
        if (inputFramesNeeded > 0)
        {
            var inputFloats = inputFramesNeeded * Channels;
            if (inputScratch.Length < inputFloats) inputScratch = new float[inputFloats];
            // The upstream pads with silence on underflow (ReadFully), so this always fills.
            var got = upstream.Read(inputScratch, 0, inputFloats);
            for (var i = 0; i < got; i++) inBuf[inBufOff + i] = inputScratch[i];
            for (var i = got; i < inputFloats; i++) inBuf[inBufOff + i] = 0f;
        }

        var produced = resampler.ResampleOut(destination, offset, inputFramesNeeded, outFrames, Channels);
        // ResampleOut can come up a frame short on a ratio change; pad rather than hand the mixer a
        // short block, which it would read as a gap.
        for (var i = produced * Channels; i < count; i++) destination[offset + i] = 0f;
        return count;
    }

    private void UpdateRatioIfDue()
    {
        // The loop itself lives in Core, shared with the receive side's device output and the plugin
        // sender's second-DAW lane. Writing a fourth copy here is how the first three drifted apart:
        // this one originally lacked the first-window discard, the sanity gate and the depth-bias
        // clamp that the oldest copy had learned the hard way. See DriftRatioTracker.
        var depthFrames = bufferedMs() * SampleRate / 1000;
        var targetFrames = TargetDepthMs * SampleRate / 1000;
        if (!tracker.Update(FedBytes(), pulledFrames * Channels * sizeof(float), depthFrames, targetFrames)) return;

        AppliedRatio = tracker.AppliedRatio;
        resampler.SetRates(SampleRate * AppliedRatio, SampleRate);

        if (!everCorrected && Math.Abs(AppliedRatio - 1.0) > 0.00005)
        {
            everCorrected = true;
            onDiagnostic?.Invoke(
                $"capture drift: \"{name}\" running at {(tracker.ClockRatio - 1.0) * 1e6:0} ppm against the mix clock "
                + $"(ring {bufferedMs()} ms) — correcting so it stays aligned with the other sources");
        }
    }
}
