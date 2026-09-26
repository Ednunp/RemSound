using System.Net;
using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>
    /// NOTHING MAY CLIMB OVER A NIGHT.
    ///
    /// <para>Ed has reported the WASAPI side going slow after being left overnight, and Andre reported
    /// the same thing with no hibernation involved at all — it simply grew while the app ran. Three
    /// separate diagnoses from me, three real bugs found and fixed, and the symptom still there. The
    /// reason is that every fix was proved against a test I wrote for the fault I had guessed at, and
    /// none was ever proved against the property the reports are actually about: leave it running, and
    /// the latency should be the same in the morning.</para>
    ///
    /// <para>So this asserts that property directly, on the values that could carry it. It runs the
    /// equivalent of many hours in milliseconds, and every check is of the form "the end must not be
    /// meaningfully higher than the beginning" rather than "this number is correct".</para>
    ///
    /// <para><b>Why the WASAPI cushion target is first.</b> The fault is on WASAPI and never on ASIO,
    /// and the lanes differ in exactly one place — the WASAPI output stage. Within that stage, the
    /// cushion target is the only thing that decides how much latency is deliberately held. If latency
    /// appears over a night with nobody asking for it, this is the mechanism that could do it.</para>
    ///
    /// <para><b>Not per-configuration.</b> Each part is driven directly and the ASIO lane genuinely has
    /// no equivalent: no device buffer, no cushion target, no rate-trimming resampler. That asymmetry
    /// is the subject rather than an untested gap — running the same arithmetic three times would say
    /// nothing extra.</para>
    /// </summary>
    private static string? AuditNothingCreepsOverHours()
    {
        var rng = new Random(20260909);   // fixed seed: a soak that fails once a fortnight is useless

        // ---- 1. THE WASAPI CUSHION TARGET, over a night of jittery pull measurements. ----------
        // A real card's average pull wobbles from window to window — scheduling, load, a browser
        // waking up. The target must settle on the card's true size and then STOP, because a target
        // that ratchets on wobble is latency nobody asked for, added while you sleep.
        const double truePullMs = 10.0;
        var windows = (int)(12 * 60 * 60 / DriftRatioTracker.MeasurementWindowSec);   // twelve hours

        // (a) A REAL CARD, as Ed's logs actually show it. Every drift line from his 2026-09-09 session
        // reports gulpMs=10, window after window, for the whole run — a window AVERAGE of many pulls is
        // a very stable number. Under that stimulus the target must settle once and never move again,
        // which is exactly what the code claims for itself.
        var target = AdaptiveCushionTarget.DefaultMs;
        var settled = 0;
        var movesAfterSettling = 0;
        for (var i = 0; i < windows; i++)
        {
            var measured = truePullMs * (0.99 + rng.NextDouble() * 0.02);   // ±1 %, a steady card
            var next = AdaptiveCushionTarget.Next(target, measured);
            if (i == 60) settled = target;
            if (i > 60 && next != target) movesAfterSettling++;
            target = next;
        }
        Check(settled > 0, "the target must have settled on something within ten minutes");
        Check(movesAfterSettling == 0,
            $"against a steady card the target must settle once and never move again ({movesAfterSettling} moves over "
            + "twelve hours) — it is meant to read the card and HOLD, never to be a load-reactive loop");

        // (b) A NASTY CARD, wobbling ±15 % with the occasional coalesced double-pull. Here the target
        // WILL move — the hysteresis band is 2 ms and that wobble spans more than 2 ms of candidate, so
        // it oscillates. That is tolerable. What is NOT tolerable is a RATCHET: oscillating around a
        // level is a few ms of wander, whereas climbing and never coming back is the overnight report.
        // So this half asserts the property that actually matters, and deliberately does not pretend
        // the target is immovable under a stimulus where it demonstrably is not.
        var nasty = AdaptiveCushionTarget.DefaultMs;
        var firstHour = 0;
        var highest = nasty;
        for (var i = 0; i < windows; i++)
        {
            var measured = truePullMs * (0.85 + rng.NextDouble() * 0.30);
            if (rng.NextDouble() < 0.01) measured *= 2;
            nasty = AdaptiveCushionTarget.Next(nasty, measured);
            if (i == 360) firstHour = nasty;                 // one hour in
            if (nasty > highest) highest = nasty;
        }
        Check(nasty <= firstHour + AdaptiveCushionTarget.HysteresisMs,
            $"after twelve hours of a wobbling card the target must be where it was after one (hour one {firstHour} ms, "
            + $"hour twelve {nasty} ms, peaked {highest} ms) — wander around a level is fine, a climb that never comes "
            + "back is latency added overnight with nobody asking, which is the report");
        Check(highest <= AdaptiveCushionTarget.MaxMs,
            $"even a card behaving badly all night must not push the cushion past its cap (peaked {highest} ms)");

        // A card that GENUINELY changes must still be followed, or the hysteresis has just become a
        // way of ignoring reality.
        var followed = AdaptiveCushionTarget.Next(settled, 18.0);
        Check(followed > settled,
            $"a real change in the card's pull must still move the target ({settled} ms -> {followed} ms) — holding "
            + "steady must not mean ignoring an 18 ms card");

        // ---- 2. THE DRIFT LOOP, over a night against a slightly fast crystal. -------------------
        // A real clock difference is tens of ppm and CONSTANT. The applied rate must converge on it
        // and stay there; it must not wander further out the longer it runs.
        var tracker = new DriftRatioTracker(48000, windowSec: 0.001);
        const double crystalPpm = 40.0;
        long fed = 0, drained = 0;
        var bytesPerWindow = (long)(48000 * 2 * sizeof(float) * DriftRatioTracker.MeasurementWindowSec);
        double? afterTenMinutes = null;
        var worstAfterSettling = 0.0;
        for (var i = 0; i < windows; i++)
        {
            fed += (long)(bytesPerWindow * (1 + crystalPpm / 1e6));
            drained += bytesPerWindow;
            tracker.RewindWindowStartForTest(1.0);
            tracker.Update(fed, drained, 12 * 48, 12 * 48);   // depth exactly on target: no depth term
            if (i == 60) afterTenMinutes = tracker.AppliedRatio;
            if (i > 60 && afterTenMinutes is { } settledRatio)
            {
                var wander = Math.Abs(tracker.AppliedRatio - settledRatio) * 1e6;
                if (wander > worstAfterSettling) worstAfterSettling = wander;
            }
        }
        var trackedPpm = (tracker.ClockRatio - 1.0) * 1e6;
        Check(Math.Abs(trackedPpm - crystalPpm) < 5,
            $"after twelve hours against a {crystalPpm:0} ppm crystal the loop must be tracking it, not something else "
            + $"(got {trackedPpm:0.0} ppm)");
        Check(worstAfterSettling < 5,
            $"once settled the applied rate must stay put ({worstAfterSettling:0.0} ppm of wander over twelve hours) — a "
            + "rate that keeps wandering is a buffer that keeps moving, all night");

        // ---- 3. THE TUNER'S LEARNED FLOOR, over a night of occasional real underruns. ----------
        // The floor is allowed to rise on evidence. What it must not do is ratchet on its own and
        // never come back down, which is precisely the shape of "it was fine last night".
        // The floor OSCILLATES by design: a shortfall pushes it up, clean minutes walk it back down.
        // So comparing two instants twelve hours apart says nothing — it says only where in the cycle
        // each sample landed. (My first version of this check did exactly that and failed for that
        // reason, not because the code was wrong.) The two properties that actually matter are: it
        // never learns MORE than the evidence justified, and it still comes back down at the end of a
        // long night rather than getting stuck at the top.
        const int justifiedMs = 30;
        var creep = new AutoTuneDescent.CreepState();
        var highestFloor = 0;
        var reachedZeroInLastHour = false;
        var minutes = 12 * 60;
        // Clean ticks counted SINCE THE LAST SHORTFALL, as the app counts them (MainForm's CleanTicks). This passed the
        // absolute minute until 2026-09-24, which let the floor relax the very tick after a shortfall.
        var cleanTicks = 0;
        for (var i = 0; i < minutes; i++)
        {
            if (rng.NextDouble() < 1.0 / 30) { creep.NoteShortfallAt(atMs: 40, justifiedMs: justifiedMs); cleanTicks = 0; }
            else creep.NoteCleanRun(cleanTicks: ++cleanTicks);
            if (creep.DiscoveredFloorMs > highestFloor) highestFloor = creep.DiscoveredFloorMs;
            if (i > minutes - 60 && creep.DiscoveredFloorMs == 0) reachedZeroInLastHour = true;
        }
        Check(highestFloor <= justifiedMs + AutoTuneDescent.Default.CreepStepMs,
            $"the learned floor must never exceed what the measurement justified (peaked {highestFloor} ms against "
            + $"{justifiedMs} ms of evidence) — a floor that learns from its own output is the ratchet that took Ed's "
            + "slider from 79 to 104 ms in eighteen seconds while the measurement sat at 37");
        Check(reachedZeroInLastHour,
            $"after twelve hours the floor must still be relaxing on quiet spells (never reached zero in the final hour, "
            + $"peak was {highestFloor} ms) — a floor that only ever goes up is the buffer being higher every morning");

        return $"twelve hours simulated: cushion target settled at {settled} ms and never moved again, held its level "
             + $"under a wobbling card, drift held {trackedPpm:0.0} ppm within {worstAfterSettling:0.0} ppm, learned "
             + $"floor peaked at {highestFloor} ms against {justifiedMs} ms of evidence and still relaxed to zero at the end";
    }

    /// <summary>
    /// AND A WAKE MUST COME BACK TO WHERE IT WAS.
    ///
    /// <para>A resume does several violent things to the audio path at once: the output device is
    /// invalidated and reopened, packets stop for hours and then restart, the sequence number jumps by
    /// an enormous amount, and the buffer arrives far too deep. Ed's log from 2026-09-09 caught the
    /// last of those — the device buffer was at 100 ms with the correction pinned at its ceiling one
    /// second after wake.</para>
    ///
    /// <para>It recovered that time. The question this asks is whether recovery is a property or a
    /// piece of luck: after the shock, does the stage come back to the SAME cushion it held before,
    /// within a bounded time, rather than settling somewhere higher and staying there.</para>
    /// </summary>
    private static string? AuditResumeReturnsToWhereItWas()
    {
        // Before the wake: a settled 10 ms card.
        var target = AdaptiveCushionTarget.DefaultMs;
        for (var i = 0; i < 60; i++) target = AdaptiveCushionTarget.Next(target, 10.0);
        var before = target;

        // THE WAKE. The first windows after a resume are garbage: the card reports nonsense pull sizes
        // while the driver settles, and the buffer is far too deep.
        var wild = new[] { 0.0, 45.0, 0.0, 38.0, 2.0 };
        foreach (var w in wild) target = AdaptiveCushionTarget.Next(target, w);
        var worstDuringWake = target;

        // ...and then the card behaves again.
        for (var i = 0; i < 60; i++) target = AdaptiveCushionTarget.Next(target, 10.0);

        Check(target == before,
            $"after a wake the cushion target must return to exactly where it was ({before} ms before, {target} ms after, "
            + $"peaked {worstDuringWake} ms during) — coming back to a HIGHER cushion is latency that a night of sleep "
            + "added, which is the report");
        Check(worstDuringWake <= AdaptiveCushionTarget.MaxMs,
            $"even garbage pull measurements during a wake must stay inside the cap (got {worstDuringWake} ms)");

        // THE DEPTH SHOCK. A buffer arriving far too deep must be walked back, and the correction that
        // does it must stay bounded the whole way — a correction that is not capped is a pitch shift.
        var tracker = new DriftRatioTracker(48000, windowSec: 0.001);
        long fed = 0, drained = 0;
        var bytesPerWindow = (long)(48000 * 2 * sizeof(float) * DriftRatioTracker.MeasurementWindowSec);
        var targetFrames = before * 48;
        var depthFrames = 100 * 48;      // the 100 ms Ed's log actually recorded one second after wake
        var windowsToRecover = 0;
        var worstCorrection = 0.0;
        for (var i = 0; i < 200 && depthFrames > targetFrames; i++)
        {
            fed += bytesPerWindow;
            drained += bytesPerWindow;
            tracker.RewindWindowStartForTest(1.0);
            if (tracker.Update(fed, drained, depthFrames, targetFrames))
            {
                worstCorrection = Math.Max(worstCorrection, Math.Abs(tracker.DepthCorrection));
                // The correction drains the excess at its own rate over the window.
                var drainedFrames = (int)(tracker.DepthCorrection * 48000 * DriftRatioTracker.MeasurementWindowSec);
                depthFrames -= Math.Max(1, drainedFrames);
                windowsToRecover++;
            }
        }
        Check(depthFrames <= targetFrames,
            $"a buffer that arrives 100 ms deep after a wake must be walked back to its target, not left there "
            + $"(still {depthFrames / 48} ms after {windowsToRecover} windows)");
        Check(worstCorrection <= DriftRatioTracker.MaxDepthBias + 1e-9,
            $"the correction must stay inside its cap the whole way back ({worstCorrection * 1e6:0} ppm against a cap of "
            + $"{DriftRatioTracker.MaxDepthBias * 1e6:0}) — an uncapped correction is an audible pitch slide, not a trim");

        var secondsToRecover = windowsToRecover * DriftRatioTracker.MeasurementWindowSec;
        Check(secondsToRecover <= 15 * 60,
            $"recovery from a wake must take minutes, not the rest of the session (took {secondsToRecover / 60:0.0} minutes)");

        return $"a wake returns the cushion to exactly {before} ms after peaking at {worstDuringWake} ms, and a 100 ms "
             + $"post-wake buffer walks back to target in {secondsToRecover / 60:0.0} minutes inside the 0.3 % cap";
    }
}
