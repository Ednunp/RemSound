using System.Net;
using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App;

/// <summary>
/// The two faults Ed hit on 2026-08-28: a jitter buffer that climbed to 104 ms and would not come
/// back, and a buffer-depth figure that could not be believed.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>
    /// THE TUNER MUST NOT LEARN ITS FLOOR FROM ITS OWN POSITION.
    ///
    /// <para>Ed, 2026-08-28: "the wasapi jitter buffer got set to 104 at one point and no matter how
    /// much I put in manually, it never seemed to recover." His log shows 79 → 84 → 89 → 94 → 104 in
    /// eighteen seconds, on a handful of underruns, while the MEASUREMENT sat at 37 ms the whole way
    /// and never supported a single step of it. The learned floor climbed 82 → 102 alongside.</para>
    ///
    /// <para>The floor was learned as "wherever the slider is now, plus a step". So every raise taught
    /// a higher floor, and the higher floor justified the next raise — a control loop taking its own
    /// output as its input. And because the floor only reset on a new stream, once it was high nothing
    /// he typed could get underneath it for the rest of the session.</para>
    /// </summary>
    private static string? AuditTunerFloorIsNotSelfJustifying()
    {
        // --- THE RATCHET, replayed with his real numbers ------------------------------------------
        const int measured = 37;
        var creep = new AutoTuneDescent.CreepState();
        foreach (var at in new[] { 79, 84, 89, 94, 99 }) creep.NoteShortfallAt(at, measured);

        Check(creep.DiscoveredFloorMs <= measured + 5,
            $"after running short at 79, 84, 89, 94 and 99 ms with the measurement steady at {measured} ms, the learned "
            + $"floor is {creep.DiscoveredFloorMs} ms. It must stay near the measurement: running short far ABOVE what "
            + "the measurements ask for is not evidence the buffer is too thin, it is evidence something else is wrong. "
            + "Learning a floor from the tuner's own position is what made every raise justify the next one");
        Check(creep.DiscoveredFloorMs < 82,
            $"the floor reached {creep.DiscoveredFloorMs} ms — Ed's session learned 82 and climbed to 102, and that is "
            + "what stopped him getting back down by hand");

        // --- IT MUST STILL LEARN when the evidence really is there --------------------------------
        var honest = new AutoTuneDescent.CreepState();
        honest.NoteShortfallAt(30, 30);
        Check(honest.DiscoveredFloorMs > 30,
            $"a shortfall the measurement AGREES with must still teach a floor above it (got {honest.DiscoveredFloorMs} ms) "
            + "— capping the lesson must not delete it");

        // --- AND IT MUST COME BACK DOWN when things go quiet --------------------------------------
        var decaying = new AutoTuneDescent.CreepState();
        decaying.NoteShortfallAt(40, 40);
        var learned = decaying.DiscoveredFloorMs;
        Check(learned > 0, "the floor must actually be set before its decay can be tested");
        for (var tick = 1; tick <= 60; tick++) decaying.NoteCleanRun(tick);
        Check(decaying.DiscoveredFloorMs < learned,
            $"a long clean stretch must RELAX the learned floor — it is still {decaying.DiscoveredFloorMs} ms after 60 "
            + $"clean ticks, learned at {learned} ms. A floor discovered during one bad spell must not bind the whole "
            + "session, which is what made a rough first twenty seconds cost Ed the next twenty minutes");
        Check(decaying.DiscoveredFloorMs >= 0, "the floor must never go negative");

        // ...but never on a single lucky tick.
        var impatient = new AutoTuneDescent.CreepState();
        impatient.NoteShortfallAt(40, 40);
        var before = impatient.DiscoveredFloorMs;
        impatient.NoteCleanRun(1);
        Check(impatient.DiscoveredFloorMs == before,
            $"one clean tick must not move the floor (went {before} → {impatient.DiscoveredFloorMs} ms). The relaxation "
            + "is earned by a quiet spell, exactly as the creep earns its steps — otherwise one good second unlearns a "
            + "limit that was discovered the hard way");

        return "the learned floor is capped at what the measurement justifies, still learns when the evidence is real, "
             + "relaxes after a sustained quiet spell, and never moves on a single tick";
    }

    /// <summary>
    /// THE BUFFER DEPTH IS ONE LANE'S, NOT THE SUM OF EVERY SESSION.
    ///
    /// <para><c>CurrentBufferMs</c> — the status line's buffer column — added <c>BufferedBytes</c>
    /// across the whole session snapshot. That snapshot holds primaries AND mirror replicas, so with
    /// both kinds of output ticked a single stream was counted twice and the figure came out at
    /// roughly double the real depth.</para>
    ///
    /// <para>It read 115 ms against Ed's 20 ms target on 2026-08-28 while the true per-lane depth was
    /// about 55. I read that at face value and told him the buffer was refusing to hold its target.
    /// The meter was wrong. This is the lane-free accessor for a per-lane quantity that our own rule
    /// warns about — and it is the one the user actually reads.</para>
    /// </summary>
    private static string? AuditBufferDepthIsNotDoubleCounted()
    {
        var peer = new IPEndPoint(IPAddress.Parse("192.168.1.65"), 47830);

        foreach (var configuration in AudioConfigurations.All)
        {
            var engine = new PlayoutEngine(new ReceiverDiagnostics());
            engine.SetIndependentLaneLatency(configuration.HasTwoLanes());
            engine.SetLaneActive(RenderRoute.WasapiLane, configuration.UsesWasapi());
            engine.SetLaneActive(RenderRoute.AsioLane, configuration.UsesAsio());
            engine.SetMaxLatencyMs(RenderRoute.Mixed, 30);
            engine.SetMaxLatencyMs(RenderRoute.WasapiLane, 30);
            engine.SetMaxLatencyMs(RenderRoute.AsioLane, 30);

            var session = engine.GetOrCreateSession(peer, 1, 1024 * 1024);
            FillSession(session, 0.4f);

            var lane = configuration.SingleRoute() ?? RenderRoute.WasapiLane;
            var perLane = engine.CurrentBufferMsFor(lane);
            var headline = engine.CurrentBufferMs;

            Check(perLane > 0,
                $"in {configuration.Describe()} the lane must report a real depth or this check means nothing "
                + $"(got {perLane} ms)");
            Check(headline == perLane,
                $"in {configuration.Describe()} the headline buffer figure says {headline} ms but the lane carrying the "
                + $"audio holds {perLane} ms. ONE stream is playing. Summing the snapshot counts a mirror replica as if "
                + "it were extra delay, so the number a person reads to answer \"why does this feel late\" is inflated "
                + "exactly when having two lanes makes that hardest to judge");
        }

        // The sharpest version: both lanes live, so one stream has a primary AND a mirror.
        var both = new PlayoutEngine(new ReceiverDiagnostics());
        both.SetIndependentLaneLatency(true);
        both.SetLaneActive(RenderRoute.WasapiLane, true);
        both.SetLaneActive(RenderRoute.AsioLane, true);
        both.SetMaxLatencyMs(RenderRoute.Mixed, 30);
        both.SetMaxLatencyMs(RenderRoute.WasapiLane, 30);
        both.SetMaxLatencyMs(RenderRoute.AsioLane, 30);
        var mirrored = both.GetOrCreateSession(peer, 2, 1024 * 1024);
        FillSession(mirrored, 0.4f);

        var wasapiDepth = both.CurrentBufferMsFor(RenderRoute.WasapiLane);
        var asioDepth = both.CurrentBufferMsFor(RenderRoute.AsioLane);

        // PROVE THE PREMISE FIRST. Without a mirror actually holding audio there is nothing to
        // double-count, and every check below would pass against the summing version too — which is
        // exactly what happened on the first run of this test. A guard that skips the real assertion
        // when the setup is empty is a test that cannot fail.
        Check(wasapiDepth > 0 && asioDepth > 0,
            $"with both lanes ticked one stream must exist on BOTH — a primary and its mirror — or this check proves "
            + $"nothing (WASAPI {wasapiDepth} ms, ASIO {asioDepth} ms)");

        var worst = Math.Max(wasapiDepth, asioDepth);
        Check(both.CurrentBufferMs == worst,
            $"with both lanes live the headline must be the DEEPEST lane ({worst} ms: WASAPI {wasapiDepth}, ASIO "
            + $"{asioDepth}), not the two added together — got {both.CurrentBufferMs} ms. The deepest lane is the delay "
            + "somebody is actually hearing");
        Check(both.CurrentBufferMs < wasapiDepth + asioDepth,
            $"the headline must not be the sum of {wasapiDepth} and {asioDepth} — that sum IS the double count, and it "
            + "is what made a 55 ms buffer read as 115 against a 20 ms target");

        return "the headline buffer depth is the deepest LANE, never the sum of primaries and their mirrors — checked in "
             + "all three configurations and with a mirrored stream";
    }
}
