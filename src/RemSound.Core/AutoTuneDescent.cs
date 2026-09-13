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
        int MinSamplesForEvidence = 10,
        // --- Phase 2: the creep (Ed, 2026-08-15) ---
        // The fast descent lands at roughly need + HeadroomMs and stops, which felt wasteful next to
        // the old crawl — that kept shaving all the way down. It was right to: the headroom above is a
        // GUESS about how much margin this machine needs, and a guess can be replaced by an
        // experiment. So once the fast phase has arrived, keep probing: shed a few ms, wait, and only
        // shed again if nothing ran short. The first shortfall sets a floor discovered on THIS machine
        // and this network, which beats any constant chosen in advance.
        /// <summary>How much to shed per creep probe. Small — the point is that a wrong step costs
        /// almost nothing and is immediately reversed.</summary>
        int CreepStepMs = 3,
        /// <summary>Clean ticks required between creep probes. Deliberately slower than the fast
        /// phase: each step must be VALIDATED by a quiet spell before the next one is earned.</summary>
        int CreepIntervalTicks = 3,
        /// <summary>Clean ticks required before creeping starts at all — a longer settling period than
        /// the fast phase demands, because this is the phase that goes below the comfortable margin.</summary>
        int CreepCleanTicks = 6);

    public static readonly Policy Default = new();

    /// <summary>What the tuner has learned about THIS machine while creeping. Lives across ticks;
    /// reset when the stream changes (new conditions, so the lesson may not hold).</summary>
    public sealed class CreepState
    {
        /// <summary>The lowest latency that proved unsafe here, plus a step — the floor the creep
        /// will not go under again. Discovered by experiment rather than assumed. 0 = nothing learned
        /// yet.</summary>
        public int DiscoveredFloorMs { get; private set; }
        /// <summary>Ticks since the last creep probe, so each step is spaced by a validating wait.</summary>
        public int TicksSinceProbe { get; set; }

        /// <summary>
        /// The buffer ran short at <paramref name="atMs"/> — record it as too thin for this machine
        /// so the creep never returns there.
        /// </summary>
        /// <param name="justifiedMs">What the MEASUREMENTS say this lane needs right now — the
        /// tuner's own recommendation. The learned floor is capped at this, and that cap is the
        /// entire point of the parameter.</param>
        /// <remarks>
        /// <para><b>Why the cap exists.</b> This used to learn the floor from <paramref name="atMs"/>
        /// alone — the tuner's OWN current position. An underrun at 79 ms taught it "79 is too thin",
        /// which justified raising to 84, which taught it 84, which justified 89. Every raise
        /// manufactured the evidence for the next one: a control loop taking its own output as its
        /// input, and such a loop can only travel one way.</para>
        ///
        /// <para>Ed's laptop, 2026-08-28: 79 → 84 → 89 → 94 → 104 in eighteen seconds while the
        /// measurement sat at <b>37 ms the whole time</b> and never supported a single step of it. The
        /// floor climbed 82 → 102 alongside, and since it only reset on a new stream, nothing he typed
        /// into the box afterwards could get underneath it. "No matter how much I put in manually, it
        /// never seemed to recover."</para>
        ///
        /// <para>Running short far ABOVE what the measurements ask for is not evidence that the buffer
        /// is too thin. It is evidence that something else is wrong, and more buffer will not fix it.
        /// So the floor may still be taught by experience — but never further than the evidence
        /// reaches. 2026-09-06.</para>
        /// </remarks>
        public void NoteShortfallAt(int atMs, int justifiedMs, Policy? policy = null)
        {
            var p = policy ?? Default;
            // Never learn a floor above what the measurements justify. One step of headroom above the
            // SMALLER of the two, so a genuine shortfall at or below the measured need still teaches.
            var floor = Math.Min(atMs, Math.Max(justifiedMs, 0)) + p.CreepStepMs;
            if (floor > DiscoveredFloorMs) DiscoveredFloorMs = floor;
        }

        /// <summary>
        /// A clean stretch — nothing has run short for a while, so let the learned floor come DOWN.
        /// </summary>
        /// <remarks>
        /// <para>Without this the floor is permanent for the life of a stream: learned in the noisy
        /// first seconds after a connect, or during a spell when the machine was busy, and then
        /// binding for the rest of the session however quiet things become afterwards. That is what
        /// made a bad first twenty seconds cost Ed the following twenty minutes.</para>
        ///
        /// <para>One step at a time, and only after a validating quiet spell — the same shape as the
        /// creep itself, so one lucky tick can never unlearn a real limit. This does NOT speed up the
        /// descent: it lifts a ceiling that was stopping the descent being attempted at all. The trim
        /// glide and the per-tick descent limit are untouched. 2026-09-06.</para>
        /// </remarks>
        public void NoteCleanRun(int cleanTicks, Policy? policy = null)
        {
            if (DiscoveredFloorMs <= 0) return;
            var p = policy ?? Default;
            if (cleanTicks <= 0 || cleanTicks % Math.Max(1, p.CreepIntervalTicks) != 0) return;
            DiscoveredFloorMs = Math.Max(0, DiscoveredFloorMs - p.CreepStepMs);
        }

        /// <summary>Put back a floor learned earlier. After a wake the audio is restarted, but the network and the devices
        /// the floor was learned on are the same ones, so it still describes them.</summary>
        public void RestoreFloor(int floorMs)
        {
            DiscoveredFloorMs = Math.Max(0, floorMs);
            TicksSinceProbe = 0;
        }

        /// <summary>Conditions changed (a new stream): the discovered floor described the old ones.</summary>
        public void Reset()
        {
            DiscoveredFloorMs = 0;
            TicksSinceProbe = 0;
        }
    }

    /// <summary>The next target when the buffer has just RUN SHORT — the way up.
    ///
    /// <para>Counterpart to <see cref="NextTarget"/>, and deliberately the same shape: aim at the
    /// evidence rather than crawl. The descent has looked ahead and moved by a sensible amount since
    /// v6.0; the raise used to inch upward in <see cref="Policy.CreepStepMs"/> steps because it had
    /// only the learned floor to go on, which sits exactly that far above the last failure. One
    /// underrun and fifty underruns moved it the same distance, and a setting of 5 ms against a real
    /// need of 47 took fourteen ticks of broken audio to correct (Ed, 2026-08-22).</para>
    ///
    /// <para>So it takes whichever is higher of the two things it knows:</para>
    ///
    /// <para><paramref name="recommendedMs"/> — what the jitter measurement says is needed right now,
    /// from the arrival gaps and the render period. This is what lets a wild setting be corrected in
    /// ONE step.</para>
    ///
    /// <para><paramref name="learnedFloorMs"/> — depths already proven too thin on this machine. It
    /// still matters: a measurement taken during a calm second can read lower than what this hardware
    /// actually needs, and the floor stops the raise landing somewhere experience has already ruled
    /// out.</para>
    ///
    /// <para>Returns <paramref name="currentMs"/> unchanged when it is already at or above both —
    /// holding rather than ratcheting the buffer up on every bad second.</para></summary>
    /// <param name="minStepMs">Smallest move worth making. Every latency change is AUDIBLE — the
    /// buffer either stretches slightly while it banks the extra, or drops when it sheds — so a change
    /// too small to matter is a change nobody should have to hear. The descent has always skipped
    /// those; the raise did not, so a learned floor offering 3 ms at a time produced a 3 ms adjustment
    /// on every tick of a rough patch, each one audible and none of them worth it.</param>
    public static int NextRaiseTarget(int currentMs, int recommendedMs, int learnedFloorMs, int minStepMs = 0)
    {
        var target = Math.Max(recommendedMs, learnedFloorMs);
        if (target <= currentMs) return currentMs;

        // ROUND UP rather than skip. Skipping a sub-threshold raise would put the freeze straight back
        // — the buffer has just run short, so doing nothing is the one answer that is always wrong.
        // Going a little further than asked costs a few milliseconds of delay and buys one audible
        // change instead of a stream of them.
        return Math.Max(target, currentMs + minStepMs);
    }

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
        int currentMs, int recommendedMs, int lowWaterMs, int sampleCount, int consecutiveCleanTicks,
        Policy? policy = null, CreepState? creep = null)
    {
        var p = policy ?? Default;
        if (recommendedMs >= currentMs) return currentMs; // not a descent; the caller handles raises

        // The floor is the measured need, raised by anything the creep has LEARNED is too thin here.
        var floor = Math.Max(recommendedMs, creep?.DiscoveredFloorMs ?? 0);
        if (currentMs <= floor) return currentMs;

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
        var next = Math.Max(goal, Math.Max(floor, currentMs - step));

        // PHASE 2 — the creep. The fast phase above stops at roughly need + headroom, because the
        // headroom is a guess about how much margin this machine wants. Once it has arrived, replace
        // the guess with an experiment: shed a few ms, wait for a validating quiet spell, shed again.
        // Every step is cheap to undo and the first shortfall sets a floor for THIS machine (see
        // CreepState.NoteShortfallAt), so the tuner ends up where the hardware actually wants to be
        // rather than where a constant guessed. This is the persistence the old fixed crawl had and
        // the fast phase lost — Ed asked for it back without inventing another number.
        if (creep is not null && Math.Abs(next - currentMs) < p.MinStepMs)
        {
            if (consecutiveCleanTicks >= p.CreepCleanTicks && ++creep.TicksSinceProbe >= p.CreepIntervalTicks)
            {
                creep.TicksSinceProbe = 0;
                return Math.Max(floor, currentMs - p.CreepStepMs);
            }
            return currentMs; // still earning the next probe
        }
        if (creep is not null) creep.TicksSinceProbe = 0;
        return next;
    }
}
