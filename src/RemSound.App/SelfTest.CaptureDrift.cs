using NAudio.Wave;
using RemSound.Core;
using RemSound.Sender;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>
    /// TWO CAPTURE SOURCES IN ONE STREAM MUST STAY ALIGNED.
    ///
    /// <para>Anthony Reyers, 2026-09: individual WASAPI sources summed into one stream get out of
    /// alignment. He is right, and <c>CaptureSource</c>'s own class comment had said so all along —
    /// "Per-source clock drift across independent audio devices IS unavoidable ... A proper
    /// drift-correcting micro-resample is a future addition."</para>
    ///
    /// <para>Every device has its own crystal and the mix loop pulls a fixed 10 ms per tick on a
    /// Stopwatch belonging to none of them. Nothing corrected that: a source running fast filled its
    /// 250 ms ring until the oldest audio was discarded (a jump); a source running slow emptied it and
    /// was padded with silence (a hole). Neither happens until the error is already a quarter of a
    /// second, and the relative offset between two sources grows from the first second.</para>
    ///
    /// <para>This drives the corrector with a source that genuinely runs fast and requires the ratio
    /// to MOVE in the right direction and by roughly the right amount — not merely that a corrector
    /// exists. And it requires a lone source to be left exactly alone, because that is the setup
    /// almost everybody has and correcting it would be all risk and no benefit.</para>
    ///
    /// <para>Not per-configuration: this is the WASAPI capture mixer. ASIO capture is a separate send
    /// lane fed straight from one driver callback — one clock, no mixing engine, nothing to drift
    /// against — and the plugin lane likewise rides the DAW's clock. Those are different streams, so
    /// no configuration puts them in the same mix.</para>
    /// </summary>
    private static string? AuditCaptureSourcesStayAligned()
    {
        const int sampleRate = 48000, channels = 2;

        // The SHIPPED window is pinned in absolute seconds. The runs below use a half-second window so
        // the gate measures a real correction in seconds instead of minutes — and a short window in a
        // test must never be able to hide a changed default in the product.
        //
        // TWO windows have to close before anything moves, not one: the first is start-up fill and is
        // deliberately thrown away. The corrector did not do that when it was first written; it does
        // now, because it shares the receive side's loop. Hence 2 s of running for a 0.5 s window.
        Check(CaptureDriftCorrector.DefaultMeasurementWindowSec == 10.0,
            $"the shipped measurement window must stay 10 s, matching the receive side and the plugin lane (got "
            + $"{CaptureDriftCorrector.DefaultMeasurementWindowSec}) — shorter and one lumpy callback reads as a clock error");

        // A source that produces MORE than the mixer takes — a fast crystal, exaggerated so a short
        // window is enough to measure. Reports its own fed-bytes and ring depth the way a real
        // CaptureSource does.
        var fedBytes = 0L;
        var ringMs = 30;
        var correcting = true;

        var upstream = new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels)).ToSampleProvider();
        var corrector = new CaptureDriftCorrector(
            upstream,
            () => ringMs,
            () => fedBytes,
            () => correcting,
            "fast device",
            measurementWindowSec: 0.5,
            onDiagnostic: null);

        Check(Math.Abs(corrector.AppliedRatio - 1.0) < 1e-9,
            $"a corrector must start at ratio 1.0 and earn any change (started at {corrector.AppliedRatio})");

        // Feed 1000 ppm MORE than we pull, across enough wall clock for the discarded start-up window
        // AND a measured one to close.
        var block = new float[480 * channels];
        var pulledFrames = 0L;
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < TimeSpan.FromSeconds(2.0))
        {
            corrector.Read(block, 0, block.Length);
            pulledFrames += 480;
            // The device fed 0.1% more than was taken — a fast clock, and the ring creeps up with it.
            fedBytes += (long)(480 * channels * sizeof(float) * 1.001);
            ringMs = Math.Min(250, 30 + (int)(pulledFrames / 48000 * 3));
            Thread.Sleep(1);
        }

        // The premise: enough wall clock passed for the one-second window to close, and we really did
        // pull through the corrector rather than sitting idle. Both, or the checks below prove nothing.
        Check(DateTime.UtcNow - started >= TimeSpan.FromSeconds(1.0),
            "two measurement windows must have had time to close, or no ratio was ever computed — the first one is "
            + "start-up fill and is deliberately discarded");
        Check(pulledFrames > 480 * 20,
            $"the test must actually pull audio through the corrector (pulled {pulledFrames} frames)");
        Check(corrector.AppliedRatio > 1.0,
            $"a source feeding FASTER than the mixer pulls must be consumed faster to stop its ring growing — the ratio "
            + $"should have risen above 1.0 and is {corrector.AppliedRatio:0.000000}. Without this the ring simply fills "
            + "to 250 ms and starts discarding, which is the source jumping forward");
        Check(corrector.AppliedRatio < 1.05,
            $"the correction must stay inside its sanity clamp (got {corrector.AppliedRatio:0.000000}) — a runaway ratio "
            + "would be a pitch shift, not a drift correction");

        var ppm = (corrector.AppliedRatio - 1.0) * 1e6;
        Check(ppm > 100,
            $"a 1000 ppm feed excess must produce a correction of the same order, not a token one (got {ppm:0} ppm)");

        // A LONE SOURCE IS LEFT ALONE. Nothing to align with, so correcting is all risk.
        var aloneFed = 0L;
        var alone = new CaptureDriftCorrector(
            new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels)).ToSampleProvider(),
            () => 200,          // a deep ring, which WOULD provoke a correction if it were enabled
            () => aloneFed,
            () => false,        // ...but it is the only source
            "only device",
            measurementWindowSec: 0.5,
            onDiagnostic: null);
        var soloStart = DateTime.UtcNow;
        while (DateTime.UtcNow - soloStart < TimeSpan.FromSeconds(2.0))
        {
            alone.Read(block, 0, block.Length);
            aloneFed += (long)(480 * channels * sizeof(float) * 1.001);
            Thread.Sleep(1);
        }
        Check(alone.AppliedRatio == 1.0,
            $"a LONE capture source must be left exactly alone (ratio {alone.AppliedRatio:0.000000}) — it has nothing to "
            + "stay aligned with, and resampling the commonest setup of all would be pure risk for no benefit");

        return $"a source feeding 1000 ppm fast is corrected by {ppm:0} ppm and held on the mix clock; a lone source is "
             + "left untouched at ratio 1.0";
    }

    /// <summary>
    /// THE ONE DRIFT LOOP, AND ITS FOUR HARD-WON RULES.
    ///
    /// <para>Three places had their own copy of this arithmetic: the receive side's device output, the
    /// plugin sender's second-DAW lane, and the capture-source corrector. They were folded onto
    /// <see cref="DriftRatioTracker"/> on 2026-09-07, and the reason was not tidiness. The newest copy
    /// had been written without three of the rules below, and nothing would have caught that — each
    /// caller's own test proves ITS behaviour, and a missing safety rule only shows up in the field, as
    /// a glitch that drags the rate for a minute afterwards.</para>
    ///
    /// <para>So the rules are pinned HERE, once, on the shared type. Every caller inherits them: break
    /// any one of these and all three lanes go red together, which is exactly the point of there being
    /// one copy.</para>
    ///
    /// <para>Not per-configuration. This is arithmetic over two byte counters — there is no device, no
    /// driver and no output lane in it. What differs between ASIO and WASAPI is which callers use it:
    /// the receive-side device output is WASAPI-only (ASIO pulls straight from the engine and has no
    /// buffer of its own to hold flat), while the two sender-side callers are on the capture path,
    /// which is upstream of every output choice. Those differences are covered by each caller's own
    /// test; this one covers what they share.</para>
    /// </summary>
    private static string? AuditSharedDriftLoopRules()
    {
        const int rate = 48000, channels = 2;
        var bytesPerSec = (long)rate * channels * sizeof(float);
        var target = rate * 12 / 1000;   // a 12 ms cushion, the long-proven receive-side value

        // The shipped numbers. Every caller now inherits these, so a change here is a change to the
        // receive side that has held real buffers flat for months — it must be deliberate.
        Check(DriftRatioTracker.MeasurementWindowSec == 10.0,
            $"the measurement window must stay 10 s (got {DriftRatioTracker.MeasurementWindowSec}) — shorter and one "
            + "lumpy callback reads as a clock error");
        Check(DriftRatioTracker.RatioSmoothingNew == 0.30,
            $"smoothing must stay 70/30 (got {DriftRatioTracker.RatioSmoothingNew}) so no single window can swing the rate");
        Check(DriftRatioTracker.RatioMin == 0.95 && DriftRatioTracker.RatioMax == 1.05,
            "the sanity band must stay ±5 % — it is what separates a clock from a glitch");
        Check(DriftRatioTracker.MaxDepthBias == 0.003,
            $"the depth nudge must stay capped at 0.3 % (got {DriftRatioTracker.MaxDepthBias}) — above that it stops "
            + "being a sub-audible pitch trim and becomes something you can hear");

        // RULE 1: THE FIRST WINDOW IS DISCARDED. It is start-up fill, not drift. On the receive side it
        // measured −1199 ppm, and that shoved the buffer off target for the window after it.
        //
        // The bogus figure below is deliberately INSIDE the sanity band, because the real one was: a
        // −1199 ppm reading is 0.9988, nowhere near ±5 %. Use a wild out-of-band number here and the
        // sanity gate rejects it, the discard is never exercised, and the check passes whether the
        // discard exists or not. (It was written that way first, and deleting the discard left the
        // gate green — which is exactly what "prove it can fail" is for.)
        var t = new DriftRatioTracker(rate, windowSec: 0.05);
        long fed = 0, drained = 0;
        t.Update(fed, drained, target, target);                 // anchors the window; measures nothing yet
        fed += (long)(bytesPerSec * 1.02); drained += bytesPerSec;   // start-up fill: plausible, and in band
        t.RewindWindowStartForTest(1.0);
        var firstAccepted = t.Update(fed, drained, target, target);
        Check(!firstAccepted && t.AppliedRatio == 1.0 && !t.IsTracking,
            $"the FIRST completed window must be discarded and change nothing (ratio {t.AppliedRatio:0.000000}) — it "
            + "contains the buffer filling from empty, which is not a clock error however sane the number looks");

        // RULE 2: A READING OUTSIDE THE BAND IS DISCARDED, NOT CLAMPED. This is the one that is easy to
        // get wrong, because clamping looks safe. It is not: a stall or a device hiccup would be folded
        // into the smoother at the band edge and drag the rate for several windows afterwards.
        fed += bytesPerSec; drained += bytesPerSec / 2;         // ratio 2.0 again — a glitch, not a clock
        t.RewindWindowStartForTest(1.0);
        t.Update(fed, drained, target, target);
        Check(t.AppliedRatio == 1.0 && !t.IsTracking,
            $"a measurement outside ±5 % must be DISCARDED, not clamped to the edge (ratio {t.AppliedRatio:0.000000}) — "
            + "clamping feeds a glitch into the smoother and drags the rate for windows afterwards");

        // RULE 3: A VALID READING IS ACCEPTED, WHOLE, ON THE FIRST MEASUREMENT. No smoothing toward it
        // from 1.0 — the first real number is the best estimate there is, and easing into it would
        // leave the buffer drifting for a minute for no reason.
        fed += (long)(bytesPerSec * 1.001); drained += bytesPerSec;   // 1000 ppm fast, well inside the band
        t.RewindWindowStartForTest(1.0);
        var accepted = t.Update(fed, drained, target, target);
        Check(accepted && t.IsTracking,
            "a measurement inside the sanity band must be accepted");
        Check(Math.Abs(t.ClockRatio - 1.001) < 1e-4,
            $"the first valid window must be taken whole, not smoothed up from 1.0 (clock {t.ClockRatio:0.000000}, "
            + "expected about 1.001000)");

        // RULE 4: THE DEPTH NUDGE IS BOUNDED. A buffer that is absurdly deep must still only be walked
        // back gently — the whole point is that the correction is never audible.
        var deep = rate;   // a full second too deep, far beyond anything the cap should allow through
        t.RewindWindowStartForTest(1.0);
        fed += (long)(bytesPerSec * 1.001); drained += bytesPerSec;
        t.Update(fed, drained, deep, target);
        Check(Math.Abs(t.DepthCorrection - DriftRatioTracker.MaxDepthBias) < 1e-9,
            $"a hugely over-deep buffer must clamp to the 0.3 % cap, not chase the error (got {t.DepthCorrection:0.000000})");
        Check(t.DepthCorrection > 0,
            "a buffer deeper than target must bias the rate UP, so the consumer pulls more and the depth comes down");

        // AND THE ORDERING THE TWO DEVICE CALLERS DEPEND ON. Both size their wanted cushion from the
        // window that just ended, so they take the update in two steps and do that sizing in between.
        // ApplyMeasurement must therefore refuse to run unless a window really did just close, or a
        // stray call would count one window twice.
        Check(!t.ApplyMeasurement(target, target),
            "ApplyMeasurement must do nothing unless a window has just closed — otherwise a second call would fold the "
            + "same window into the ratio twice");

        var settled = t.ClockRatio;
        t.Reset();
        Check(t.AppliedRatio == 1.0 && t.ClockRatio == 1.0 && !t.IsTracking,
            $"Reset must forget everything (clock {t.ClockRatio:0.000000}) — a reopened device or a source that went "
            + "away and came back is a new clock, and the old lesson no longer holds");

        return $"the shared loop discards its start-up window and any out-of-band glitch, takes the first valid reading "
             + $"whole ({settled:0.000000}), caps the depth nudge at 0.3 %, and refuses to count a window twice";
    }
}
