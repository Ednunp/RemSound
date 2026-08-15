namespace RemSound.Core;

/// <summary>
/// How fast the continuous auto-tune is allowed to come DOWN, and on what evidence.
///
/// <para>The problem (Ed, 2026-08-15): the tuner already computes the right answer every tick — a
/// recommendation built from the worst arrival gaps and render-callback lateness it has actually
/// measured — and then ignores it on the way down, stepping a fixed 5 ms per tick. From a silly
/// starting value that's twenty-plus ticks to travel a distance it measured in one, so it reads as
/// random: you see the crawl, not the thinking.</para>
///
/// <para>Why the crawl existed: shedding cushion is the risky direction. Cut padding you turn out to
/// need and you get clicks, which is the failure users actually abandon the app over — and a bad
/// guess used to take minutes to undo, because a raise crawled too. Two things changed that calculus
/// (both 2026-08): a raise now converges in seconds (SessionPlayout's fast approach), so a mistake is
/// cheap to reverse; and short-reads are now split by cause, so "the device asked in awkward lumps"
/// no longer masquerades as "we needed more buffer". This class is the deliberate, prompted reopening
/// of a rule that was parked after the overnight-lag saga — it keeps the caution and drops the
/// arbitrariness.</para>
///
/// <para>The evidence it descends on is the LOW-WATER MARK: the shallowest the buffer actually got
/// over the lookback window. If the target was 500 ms and the buffer never dipped below 300, then
/// 300 ms of that cushion was demonstrably never touched, and shedding most of it is a measurement
/// rather than a hope. That's the "look ahead before moving" — it looks at margin that went unused,
/// which is the only honest way to know what's spare without first taking it away.</para>
///
/// <para>Pure and parameterless-by-design so the scoring harness (--latency-lab tune) and the gate can
/// drive it directly: every number lives in <see cref="Policy"/>, and none of them were chosen by
/// intuition — they're whatever the harness showed settles fastest without buying dropouts.</para>
/// </summary>
public static class AutoTuneDescent
{
    /// <summary>The tunable numbers, gathered in one place so the harness can sweep them and the
    /// shipped defaults are visibly the ones that were measured.</summary>
    public sealed record Policy(
        /// <summary>Cushion left behind when shedding unused margin — we never cut right to the
        /// shallowest point the buffer reached, because that point was survived, not spare.</summary>
        int HeadroomMs = 15,
        /// <summary>Fraction of the distance to the evidence-backed target taken in one move. Below 1
        /// so a wrong reading is a partial mistake and the next tick re-measures from the new depth.</summary>
        double BigStepFraction = 0.7,
        /// <summary>Fraction taken when the evidence isn't strong enough for a big step. Replaces the
        /// old fixed 5 ms: proportional, so the step respects the size of the error either way.</summary>
        double SmallStepFraction = 0.2,
        /// <summary>Never move less than this, or a descent would asymptote and never arrive.</summary>
        int MinStepMs = 5,
        /// <summary>Consecutive clean ticks (no tune-blocking short-reads) required before a big,
        /// evidence-backed step is allowed. One quiet tick is luck; several is a pattern.</summary>
        int CleanTicksForBigStep = 3,
        /// <summary>Seconds of history required before the low-water mark counts as evidence at all.</summary>
        int MinSamplesForEvidence = 10);

    public static readonly Policy Default = new();

    /// <summary>The next latency target on a DESCENT — the caller has already established that the
    /// recommendation is at or below the current value, that this tick isn't being skipped for
    /// short-reads, and has clamped/hysteresis-checked the result.
    /// </summary>
    /// <param name="currentMs">Where the slider is now.</param>
    /// <param name="recommendedMs">The jitter-based recommendation (worst gaps + render lateness +
    /// margin, codec-floored). Never descend below this: it is the measured need.</param>
    /// <param name="lowWaterMs">Shallowest buffered depth seen over the lookback window, or a
    /// negative value when unknown. This is the unused-margin evidence.</param>
    /// <param name="sampleCount">Seconds of history behind <paramref name="lowWaterMs"/>.</param>
    /// <param name="consecutiveCleanTicks">Ticks in a row with no tune-blocking short-reads.</param>
    public static int NextTarget(
        int currentMs, int recommendedMs, int lowWaterMs, int sampleCount, int consecutiveCleanTicks, Policy? policy = null)
    {
        var p = policy ?? Default;
        if (recommendedMs >= currentMs) return currentMs; // not a descent; the caller handles raises

        // The floor is always the measured need. Nothing below this, whatever the low-water mark says.
        var floor = recommendedMs;

        // Evidence: cushion the buffer never touched, minus the headroom we deliberately keep.
        var haveEvidence = lowWaterMs >= 0
            && sampleCount >= p.MinSamplesForEvidence
            && consecutiveCleanTicks >= p.CleanTicksForBigStep;
        var goal = floor;
        if (haveEvidence)
        {
            // Shed only what went unused: the buffer sat at least lowWater deep the whole window, so
            // (lowWater - headroom) is provably spare. Deeper than the jitter recommendation wins —
            // the low-water mark is the more conservative of the two whenever it's shallower.
            var shed = Math.Max(0, lowWaterMs - p.HeadroomMs);
            goal = Math.Max(floor, currentMs - shed);
        }

        var fraction = haveEvidence ? p.BigStepFraction : p.SmallStepFraction;
        var distance = currentMs - goal;
        var step = Math.Max(p.MinStepMs, (int)Math.Round(distance * fraction));
        var next = currentMs - step;
        return Math.Max(goal, Math.Max(floor, next));
    }
}
