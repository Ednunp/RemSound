namespace RemSound.Core;

/// <summary>
/// ONE TICK of the continuous auto-tune on one lane - the arithmetic of MainForm.TickRoute, lifted out whole on
/// 2026-09-28 so the simulator can run the very decision the app runs instead of a copy of it (the review of every
/// test, items 81 and 90: only the latency lab's tuner was ever in the simulated loop).
///
/// <para>Nothing was changed on the way out: the same windows, the same order, the same numbers, and the same side
/// effects on the lane's clean-tick run and learned floor. MainForm keeps everything around the decision - reading the
/// underrun counters, the first-tick baseline, the pause after the user's own move, the slider, and the log line - and
/// applies what this returns. The simulator does the same from its own per-second readings. The comments below moved
/// with the code they describe.</para>
/// </summary>
public static class ContinuousAutoTune
{
    /// <summary>A move smaller than this is not made (the descent's rule; a raise rounds up instead).</summary>
    public const int HysteresisMs = 5;
    /// <summary>The auto-tune never recommends more than this on its own; beyond it the user is in "I want a huge
    /// buffer for a terrible network" territory and can drag the slider there by hand.</summary>
    public const int RecommendationCapMs = 200;
    /// <summary>Seconds of the rolling windows a tick looks back over.</summary>
    public const int LookbackSeconds = 15;

    /// <summary>What a tick decided.</summary>
    public enum Verdict
    {
        /// <summary>The buffer ran short: raise to <see cref="Decision.Target"/>.</summary>
        Raise,
        /// <summary>The buffer ran short but is already at or above what the evidence asks: hold.</summary>
        HoldAfterShortfall,
        /// <summary>A clean tick with a move worth making (up to the measured need, or down on the evidence).</summary>
        Move,
        /// <summary>A clean tick with nothing worth moving.</summary>
        Stay,
    }

    /// <summary>The decision and every figure the app's log line prints about it.</summary>
    public readonly record struct Decision(
        Verdict Verdict, int Target, int CurrentMs, int GapPeakMs, int GapUsedMs, int RenderCbUsedMs, int SampleCount,
        int RecommendedMs, int CappedMs, int LowWaterMs, int LearnedFloorMs, int CappedFloorMs);

    /// <summary>
    /// Decide one lane's tick. See MainForm.TickRoute for the rules around it (the baseline, the user's own move).
    /// </summary>
    /// <param name="currentMs">Where the lane's slider is now.</param>
    /// <param name="sliderMinMs">The slider's own minimum, which every move is clamped to.</param>
    /// <param name="sliderMaxMs">The slider's own maximum.</param>
    /// <param name="underrunDelta">New tune-blocking underruns on this lane since the last tick.</param>
    /// <param name="arrivalGaps">The per-second worst arrival gaps (shared by every lane - a packet arrives once).</param>
    /// <param name="renderCbGaps">This lane's per-second render-callback periods (or the machine's until it has its own).</param>
    /// <param name="minBuffers">This lane's per-second buffer readings (or the machine's until it has its own).</param>
    /// <param name="codecFloorMs">The codec floor for the stream's frame size.</param>
    /// <param name="renderPeriodFloorMs">The least render period assumed (2 ms, or 1 with the lab's lean margins).</param>
    /// <param name="safetyMarginMs">The safety margin on top (5 ms, or 2 lean).</param>
    /// <param name="cleanTicks">This lane's run of ticks with no tune-blocking underruns - updated here.</param>
    /// <param name="creep">What this lane has learned about how thin it can run - updated here.</param>
    public static Decision Decide(
        int currentMs, int sliderMinMs, int sliderMaxMs,
        long underrunDelta,
        IReadOnlyCollection<int> arrivalGaps, IReadOnlyCollection<int> renderCbGaps, IReadOnlyCollection<int> minBuffers,
        int codecFloorMs, int renderPeriodFloorMs, int safetyMarginMs,
        ref int cleanTicks, AutoTuneDescent.CreepState creep)
    {
        // MEASURE FIRST, then decide. This block used to sit below the underrun branch, so a raise
        // had nothing to aim at except the learned floor's 3 ms crawl — while the recommendation that
        // knew the real answer was computed a few lines later and thrown away on that path.
        // Ed, 2026-08-22: "I wonder if I had set it to 5 ms whether it would have seen that as stupid
        // and jumped up, like it jumps down?" It would not have. It would have gone 5, 8, 11, 14 and
        // taken most of a minute of broken audio to arrive. Now it can jump.
        var sampleCount = Math.Min(LookbackSeconds, arrivalGaps.Count);
        var skip = arrivalGaps.Count - sampleCount;
        // Track the TWO highest arrival-gap seconds, not just the worst. A single transient spike —
        // one bad second from an OS/driver hiccup that doesn't recur — used to drive the whole
        // recommendation (one 1046ms gap pushed the buffer straight to the 200ms cap and shed a big
        // trim burst). Using the SECOND-highest requires the jitter to persist across >=2 seconds
        // before it counts, while still honouring sustained jitter at full speed. The true peak is
        // still logged for diagnosis; we fall back to the single value when there's only one sample.
        int gapPeak = 0, gapSecond = 0;
        var i = 0;
        foreach (var gap in arrivalGaps)
        {
            if (i++ < skip) continue;
            if (gap > gapPeak) { gapSecond = gapPeak; gapPeak = gap; }
            else if (gap > gapSecond) { gapSecond = gap; }
        }
        var observedGap = sampleCount >= 2 ? gapSecond : gapPeak;

        // Same lone-spike rejection for the render-callback gap — from THIS LANE's window. The render
        // period belongs to an output device, and the two lanes' devices are not the same device.
        int rcbPeak = renderPeriodFloorMs, rcbSecond = renderPeriodFloorMs;
        var rcbSkip = renderCbGaps.Count - Math.Min(sampleCount, renderCbGaps.Count);
        var rcbI = 0;
        foreach (var rcb in renderCbGaps)
        {
            if (rcbI++ < rcbSkip) continue;
            if (rcb > rcbPeak) { rcbSecond = rcbPeak; rcbPeak = rcb; }
            else if (rcb > rcbSecond) { rcbSecond = rcb; }
        }
        var observedRenderCb = sampleCount >= 2 ? rcbSecond : rcbPeak;

        var jitterBased = observedGap + observedRenderCb + safetyMarginMs;
        var recommended = Math.Max(codecFloorMs, jitterBased);
        var capped = Math.Min(recommended, RecommendationCapMs);
        var current = currentMs;

        Decision Made(Verdict verdict, int target, int lowWater = -1, int learnedFloor = 0, int cappedFloor = 0) =>
            new(verdict, target, current, gapPeak, observedGap, observedRenderCb, sampleCount, recommended, capped, lowWater,
                learnedFloor, cappedFloor);

        if (underrunDelta > 0)
        {
            cleanTicks = 0; // this lane's buffer ran short — its evidence for shedding is void
            // ...and record WHERE it ran short: that value is too thin for THIS lane, so its creep
            // must never probe back down to it. A floor learned by experiment beats a guessed constant.
            // Capped at what the measurement justifies (capped = the recommendation, computed above).
            // Without that cap the floor was learned from the slider's own value, so every raise
            // justified the next one. See CreepState.NoteShortfallAt. 2026-09-06.
            creep.NoteShortfallAt(current, capped);

            // UNDERRUNS MUST RAISE, NOT FREEZE.
            //
            // This used to log "skipping" and return, doing nothing at all. The guard is right for
            // LOWERING — you must never shave the buffer on the back of a second where it already ran
            // out — but returning here blocked raising too, and an underrun is the strongest evidence
            // there is that the buffer is too thin. So the one condition that proves a raise is needed
            // was the one condition that prevented it.
            //
            // Ed hit it on 2026-08-22: set to 20 ms, auto-tune on, and it sat at 20 for six minutes
            // underrunning hundreds of times a tick. Fifteen ticks, fourteen of them "skipping". The
            // fifteenth happened to be clean, and it immediately went 20 -> 37 and settled. Auto-tune
            // had the answer the whole time and no way to act on it.
            var learnedFloor = creep.DiscoveredFloorMs;

            // AIM AT THE MEASUREMENT, NOT JUST THE CRAWL.
            //
            // The learned floor only ever sits 3 ms above wherever it last ran short, so using it
            // alone meant every raise was a 3 ms step no matter how badly the buffer failed — one
            // underrun and fifteen underruns moved it exactly the same distance. From a silly value
            // like 5 ms that is fourteen ticks and the best part of a minute of broken audio.
            //
            // `capped` is the jitter measurement's own answer, computed above from the arrival gaps
            // and the render period. Taking whichever is higher means a wild setting is corrected in
            // ONE step, while the learned floor still does its real job: stopping a later descent
            // from probing back into a depth already proven too thin.
            // The learned floor grows a few ms per shortfall with nothing to stop it, while the
            // recommendation is capped. Taking the higher of the two without capping BOTH let a long
            // rough patch walk the buffer past the ceiling the measurement itself respects.
            var cappedFloor = Math.Min(learnedFloor, RecommendationCapMs);
            var raiseTarget = AutoTuneDescent.NextRaiseTarget(current, capped, cappedFloor, HysteresisMs);
            if (raiseTarget > current)
                return Made(Verdict.Raise, Math.Clamp(raiseTarget, sliderMinMs, sliderMaxMs), learnedFloor: learnedFloor, cappedFloor: cappedFloor);

            // Already at or above both; hold and gather more evidence rather than climbing on every
            // tick, which would ratchet the buffer up on a single bad second.
            return Made(Verdict.HoldAfterShortfall, current, learnedFloor: learnedFloor, cappedFloor: cappedFloor);
        }
        cleanTicks++;
        // A quiet stretch also RELAXES the learned floor, one step at a time. Without this a floor
        // learned in a noisy first twenty seconds bound the rest of the session, however clean it
        // became. See CreepState.NoteCleanRun. 2026-09-06.
        creep.NoteCleanRun(cleanTicks);

        // Low-water evidence: the shallowest the buffer got across the same lookback window. Cushion
        // it never touched is provably spare — that's what lets a descent be a measurement rather
        // than a crawl. -1 = unknown, which AutoTuneDescent treats as "no evidence, step gently".
        // ...and the low-water mark likewise: how shallow THIS lane's buffer got, not the machine's.
        var lowWater = -1;
        if (minBuffers.Count > 0)
        {
            var lwSkip = minBuffers.Count - Math.Min(LookbackSeconds, minBuffers.Count);
            var lwI = 0;
            lowWater = int.MaxValue;
            foreach (var m in minBuffers)
            {
                if (lwI++ < lwSkip) continue;
                if (m < lowWater) lowWater = m;
            }
            if (lowWater == int.MaxValue) lowWater = -1;
        }

        int target;
        if (capped > current)
        {
            target = capped;
        }
        else
        {
            // Descend on evidence rather than a fixed 5 ms crawl (2026-08-15, Ed). MaxDecreasePerTick
            // is gone: AutoTuneDescent sizes the step from the unused margin when the evidence is
            // strong, and proportionally when it isn't — so a silly value converges in a few ticks
            // while a small correction stays gentle. Never below `capped`, which is the measured need.
            target = AutoTuneDescent.NextTarget(current, capped, lowWater, sampleCount, cleanTicks,
                policy: null, creep: creep);
        }

        var clamped = Math.Clamp(target, sliderMinMs, sliderMaxMs);
        if (Math.Abs(clamped - current) < HysteresisMs) return Made(Verdict.Stay, current, lowWater, creep.DiscoveredFloorMs);

        // A RAISE ENDS THE CLEAN RUN. Those ticks were evidence that the CURRENT depth was
        // comfortable; the moment the depth changes upward they describe a setting that no longer
        // exists. Left standing, the tuner could take a big confident descent step on the very next
        // tick — undoing the raise it just decided was necessary.
        if (clamped > current) cleanTicks = 0;
        return Made(Verdict.Move, clamped, lowWater, creep.DiscoveredFloorMs);
    }
}
