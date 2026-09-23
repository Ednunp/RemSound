using System.Net;
using RemSound.Core;

namespace RemSound.App;

// -------------------------------------------------------------------------------------------------
// Ed's laptop, 2026-09-10, 14:49–14:53. Eight hours of WASAPI with the Roger On present showed no creep
// at all. Then: unplug the Roger On, hibernate for 98 s, wake, plug it back in — and the delay was
// audibly up for about a minute. The log explained it in separate pieces, and each test below pins one.
// -------------------------------------------------------------------------------------------------
internal static partial class SelfTest
{
    private static readonly RenderRoute[] TunedLanesForTest = [RenderRoute.WasapiLane, RenderRoute.AsioLane, RenderRoute.Mixed];

    /// <summary>
    /// A WAKE MUST HOLD THE TUNER AND THROW AWAY THE READINGS ACROSS THE GAP — WHICHEVER TIMER NOTICES FIRST — AND
    /// KEEP WHAT THE TUNER LEARNED.
    ///
    /// <para>At 14:51:40 the auto-tune raised WASAPI from 35 to 65 ms on 119 underruns that had piled up
    /// while the Roger On was unplugged before sleep. Windows' Resume notification arrived at 14:51:43,
    /// and the new stream session that used to be the only thing resetting the tuner opened at 14:51:47.
    /// Both too late: the tuner runs on its own timer, and on waking it won the race.</para>
    ///
    /// <para>So this drives the AUTO-TUNE tick first — the order the field log recorded — and requires
    /// that tick itself to notice the wake from the gap since it last ran. Then it checks that every
    /// lane's windows (including the shared ones the tuner falls back to) and underrun baselines are
    /// gone, that every lane is held, and that the hold ends.</para>
    ///
    /// <para>And that the learned FLOORS survive. The first version of this fix forgot them too, and on
    /// 2026-09-12 that is what let the ASIO lane fall from 26 to 21 ms on its first decision after a 4.8-hour
    /// hibernate and run short for minutes. The network and the devices are the same ones after a sleep; only
    /// the readings taken across it are worthless. Putting the whole tune back is the restart's job — see
    /// AuditWakeRestartsTheAudioWithTheTuneFromBeforeSleep.</para>
    ///
    /// <para>All three tuned lanes, deliberately: WASAPI, ASIO and the Mixed route a WASAPI-only
    /// configuration tunes. A wake holds every lane, so every configuration is affected.</para>
    /// </summary>
    private static string? AuditWakeHoldsTheTunerAndKeepsWhatItLearned()
    {
        // The detector, with the timings the app really has.
        var t0 = new DateTime(2026, 9, 10, 14, 50, 2, DateTimeKind.Utc);
        var detector = new SuspendDetector();
        Check(detector.NoteTick(t0) is null, "the first tick has nothing to compare with and must never read as a wake");
        Check(detector.NoteTick(t0.AddSeconds(1)) is null, "a one-second tick is not a wake");
        Check(detector.NoteTick(t0.AddSeconds(4)) is null, "a three-second auto-tune interval is not a wake");
        var gap = detector.NoteTick(t0.AddSeconds(102));
        Check(gap is { } g && Math.Abs(g.TotalSeconds - 98) < 0.5,
            $"the 98-second hibernate from the field log must read as a wake (got {gap?.TotalSeconds:0.0} s)");
        Check(detector.NoteTick(t0.AddSeconds(101)) is null, "a clock that moved backwards is not a wake");
        Check(SuspendDetector.Threshold >= TimeSpan.FromSeconds(5) && SuspendDetector.Threshold <= TimeSpan.FromSeconds(15),
            $"the wake threshold must stay between 5 and 15 s (got {SuspendDetector.Threshold}) — shorter and a busy "
            + "machine reads as asleep, longer and a short hibernate slips past");

        MainForm form;
        try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
        catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }

        using (form)
        {
            var lines = new List<string>();
            form.LogForTest.EventTapForTest = line => { lock (lines) lines.Add(line); };
            try
            {
                form.SnapshotTickForTest();
                form.SnapshotTickForTest();
                Check(!lines.Any(l => l.Contains("not ticking for", StringComparison.Ordinal)),
                    $"ordinary back-to-back ticks must not read as a wake — got: [{string.Join(" | ", lines)}]");
                foreach (var lane in TunedLanesForTest)
                    Check(form.TuneLaneAllowed(lane, DateTime.UtcNow), $"nothing may be held before anything has happened ({lane})");

                // What the tuner knew before the machine slept.
                form.SeedTuneEvidenceForTest(RenderRoute.WasapiLane, renderGapMs: 12, floorMs: 35);
                form.SeedTuneEvidenceForTest(RenderRoute.AsioLane, renderGapMs: 3, floorMs: 25);
                var before = form.TuneEvidenceForTest(RenderRoute.WasapiLane);
                var asioFloorBefore = form.TuneEvidenceForTest(RenderRoute.AsioLane).FloorMs;
                Check(before.LaneRenderGaps > 0 && before.SharedRenderGaps > 0 && before.FloorMs > 0 && !before.NeedsBaseline,
                    $"premise: the tuner must be holding real evidence before the sleep, or nothing below proves anything (got {before})");

                // THE MACHINE SLEEPS FOR 98 SECONDS, AND THE AUTO-TUNE TIMER WAKES FIRST.
                form.RewindTickClockForTest(TimeSpan.FromSeconds(98));
                lines.Clear();
                form.ContinuousTuneTickForTest();
                var wake = Require(lines.FirstOrDefault(l => l.Contains("not ticking for", StringComparison.Ordinal)),
                    "the AUTO-TUNE tick must itself notice a wake from the gap since it last ran. On 2026-09-10 it fired "
                    + "three seconds before Windows reported the resume and raised the WASAPI buffer from 35 to 65 ms on "
                    + $"evidence from before the sleep. Tick logged: [{(lines.Count == 0 ? "NOTHING" : string.Join(" | ", lines))}]");
                Check(wake.Contains("98 s", StringComparison.Ordinal), $"the wake line must say how long the gap was (got: {wake})");
                Check(wake.Contains('['), $"the wake line must carry the audio configuration (got: {wake})");

                var now = DateTime.UtcNow;
                foreach (var lane in TunedLanesForTest)
                    Check(!form.TuneLaneAllowed(lane, now),
                        $"every lane must be held after a wake — {lane} was not. A wake rebuilds every device path, and a "
                        + "tuner left running acts on the rebuild as though it were the network");

                var after = form.TuneEvidenceForTest(RenderRoute.WasapiLane);
                Check(after.LaneRenderGaps == 0, $"the lane's render-gap window must be emptied ({after.LaneRenderGaps} left)");
                Check(after.SharedRenderGaps == 0,
                    $"the SHARED render-gap window must be emptied too ({after.SharedRenderGaps} left) — the tuner falls back to "
                    + "it whenever a lane's own window is empty, so clearing only the lane leaves the stale gaps in play");
                Check(after.FloorMs == before.FloorMs && after.FloorMs > 0,
                    $"the learned floor must SURVIVE a wake ({before.FloorMs} ms before, {after.FloorMs} ms after) — forgetting it is what let "
                    + "the ASIO lane fall from 26 to 21 ms on the first decision after the 2026-09-12 hibernate and run short for minutes");
                Check(after.NeedsBaseline,
                    "the underrun count must be re-baselined — the 119 underruns that raised the buffer to 65 ms had piled up "
                    + "BEFORE the sleep, while the Roger On was unplugged, and were counted as one tick's worth after it");
                var asioFloorAfter = form.TuneEvidenceForTest(RenderRoute.AsioLane).FloorMs;
                Check(asioFloorAfter == asioFloorBefore && asioFloorAfter > 0,
                    $"the ASIO lane must keep its floor too ({asioFloorBefore} ms before, {asioFloorAfter} ms after)");

                Check(!form.TuneLaneAllowed(RenderRoute.WasapiLane, now + TimeSpan.FromSeconds(5)),
                    "the hold must still be in force a few seconds later, while the backend re-initialises");
                Check(form.TuneLaneAllowed(RenderRoute.WasapiLane, now + TuneEvidenceHold.AfterWake + TimeSpan.FromSeconds(1)),
                    "and it must END — a hold that never releases is a tuner that never tunes again");

                // AND THE PER-SECOND TICK MUST NOTICE TOO, when it is the one that wakes first.
                form.RewindTickClockForTest(TimeSpan.FromSeconds(60));
                lines.Clear();
                form.SnapshotTickForTest();
                Check(lines.Any(l => l.Contains("not ticking for", StringComparison.Ordinal)),
                    $"the per-second tick must notice a wake as well — got: [{string.Join(" | ", lines)}]");
            }
            finally { form.LogForTest.EventTapForTest = null; }
        }

        return "the auto-tune tick notices a 98 s sleep on its own, every lane's windows (shared ones included) and underrun "
             + "baselines are discarded while the learned floors are kept, all three lanes hold for "
             + $"{TuneEvidenceHold.AfterWake.TotalSeconds:0} s and then release, and the per-second tick notices too";
    }

    /// <summary>
    /// RE-OPENING AN OUTPUT IS NOT NETWORK JITTER.
    ///
    /// <para>14:51:56: Windows invalidated the Roger On while it was re-enumerating. RemSound re-opened it at
    /// 14:52:00. That four-second outage reached the tuner as a 57 ms render-callback gap, it recommended
    /// 80 ms, and applied 80 ms. Nothing about the network had changed.</para>
    ///
    /// <para>This drives the real per-second tick through a fault and a recovery, and requires the lanes
    /// that output carries to be held with their evidence discarded — through the recovery too, because the
    /// outage gap is still waiting to be read on the next tick. It also requires the OTHER output kind to
    /// be left alone: Ed's Audient never moved while the Roger On was pulled in and out, and its evidence
    /// was good the whole time.</para>
    ///
    /// <para>Both output kinds are covered by the lane mapping; the live fault path is WASAPI-only because
    /// only a WASAPI endpoint can be invalidated under us — an ASIO problem arrives as a ticked output that
    /// is not open, which the mapping routes to the ASIO lane alone.</para>
    /// </summary>
    private static string? AuditOutputReopenIsNotJitter()
    {
        var wasapiFault = OutputFaultLanes.For(wasapiOutputFaulted: true, []);
        Check(wasapiFault.Contains(RenderRoute.WasapiLane) && wasapiFault.Contains(RenderRoute.Mixed) && !wasapiFault.Contains(RenderRoute.AsioLane),
            $"a WASAPI output fault must hold the WASAPI lane AND the Mixed route a WASAPI-only configuration tunes, and not ASIO "
            + $"(got {string.Join(", ", wasapiFault)})");
        var asioMissing = OutputFaultLanes.For(false, [AsioDeviceId.Format(1)]);
        Check(asioMissing.Length == 1 && asioMissing[0] == RenderRoute.AsioLane,
            $"a missing ASIO output must hold the ASIO lane and nothing else (got {string.Join(", ", asioMissing)})");
        var wasapiMissing = OutputFaultLanes.For(false, ["{0.0.0.00000000}.{roger-on-3}"]);
        Check(wasapiMissing.Contains(RenderRoute.WasapiLane) && !wasapiMissing.Contains(RenderRoute.AsioLane),
            $"a missing WASAPI output must hold the WASAPI side only (got {string.Join(", ", wasapiMissing)})");
        Check(OutputFaultLanes.For(false, []).Length == 0, "nothing wrong must hold nothing");

        MainForm form;
        try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
        catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }

        using (form)
        {
            var lines = new List<string>();
            form.LogForTest.EventTapForTest = line => { lock (lines) lines.Add(line); };
            try
            {
                form.ReceiverForTest.ForceFaultedOutputForTest = false;
                form.SeedTuneEvidenceForTest(RenderRoute.WasapiLane, renderGapMs: 12, floorMs: 0);
                form.SeedTuneEvidenceForTest(RenderRoute.Mixed, renderGapMs: 12, floorMs: 0);
                form.SeedTuneEvidenceForTest(RenderRoute.AsioLane, renderGapMs: 3, floorMs: 0);
                form.SnapshotTickForTest();
                foreach (var lane in TunedLanesForTest)
                    Check(form.TuneLaneAllowed(lane, DateTime.UtcNow), $"a healthy tick must hold nothing ({lane})");

                // THE ROGER ON IS INVALIDATED.
                form.ReceiverForTest.ForceFaultedOutputForTest = true;
                lines.Clear();
                form.SnapshotTickForTest();
                var hold = Require(lines.FirstOrDefault(l => l.Contains("auto-tune: holding", StringComparison.Ordinal)),
                    "a faulted output must hold the tuner lanes it carries. On 2026-09-10 a four-second re-open reached the tuner "
                    + $"as a 57 ms gap and pushed the buffer to 80 ms. Tick logged: [{string.Join(" | ", lines)}]");
                Check(hold.Contains("WasapiLane", StringComparison.Ordinal) && hold.Contains('['),
                    $"the hold line must name the lane and carry the configuration (got: {hold})");

                var now = DateTime.UtcNow;
                Check(!form.TuneLaneAllowed(RenderRoute.WasapiLane, now) && !form.TuneLaneAllowed(RenderRoute.Mixed, now),
                    "the WASAPI lane and the Mixed route must be held while the output is being re-opened");
                Check(form.TuneLaneAllowed(RenderRoute.AsioLane, now),
                    "the ASIO lane must NOT be held — its device never moved, and holding it would stop it tuning for no reason");

                var was = form.TuneEvidenceForTest(RenderRoute.WasapiLane);
                Check(was.LaneRenderGaps == 0 && was.SharedRenderGaps == 0,
                    $"the WASAPI lane's render gaps and the shared window must be emptied ({was.LaneRenderGaps} lane, {was.SharedRenderGaps} shared)");
                Check(was.NeedsBaseline, "the WASAPI lane must re-baseline its underruns across the re-open");
                Check(form.TuneEvidenceForTest(RenderRoute.AsioLane).LaneRenderGaps > 0,
                    "the ASIO lane's own evidence must survive a WASAPI output fault");

                // STILL FAULTED ON THE NEXT TICK: held, but not announced again every second.
                lines.Clear();
                form.SnapshotTickForTest();
                Check(!lines.Any(l => l.Contains("auto-tune: holding", StringComparison.Ordinal)),
                    $"a hold must be logged once per episode, not once a tick — got: [{string.Join(" | ", lines)}]");
                Check(!form.TuneLaneAllowed(RenderRoute.WasapiLane, DateTime.UtcNow), "the hold must be kept up while the fault lasts");

                // RECOVERY. The outage gap is read on the NEXT tick, so the hold must outlast that read.
                form.ReceiverForTest.ForceFaultedOutputForTest = false;
                lines.Clear();
                form.SnapshotTickForTest();
                Check(lines.Any(l => l.Contains("recovered", StringComparison.OrdinalIgnoreCase))
                      && lines.Any(l => l.Contains("auto-tune: holding", StringComparison.Ordinal)),
                    "coming back must hold the lanes again — the four-second gap is still sitting in the measurement and is read on the "
                    + $"next tick, which is exactly when the 57 ms reached the tuner in the field. Got: [{string.Join(" | ", lines)}]");
                now = DateTime.UtcNow;
                Check(!form.TuneLaneAllowed(RenderRoute.WasapiLane, now + TimeSpan.FromSeconds(5)),
                    "the recovery hold must outlast the next few ticks");
                Check(form.TuneLaneAllowed(RenderRoute.WasapiLane, now + TuneEvidenceHold.AfterRecovery + TimeSpan.FromSeconds(1)),
                    "and must end");
            }
            finally
            {
                form.LogForTest.EventTapForTest = null;
                form.ReceiverForTest.ForceFaultedOutputForTest = false;
            }
        }

        return "a WASAPI output fault holds the WASAPI lane and Mixed route and empties their render evidence (shared window "
             + "included), leaves ASIO alone, logs once per episode, and holds again on recovery until the outage gap has been read";
    }

    /// <summary>
    /// A SETTLING WASAPI ENDPOINT IS NOT A CLOCK.
    ///
    /// <para>After the Roger On re-opened at 14:52:00, the first window was discarded as start-up fill —
    /// as designed — and the raw readings that followed were −999, +998, +800, 0 and +2 ppm. The −999
    /// was taken whole, the stage drained the device too slowly on it, and the cushion sat at 30 ms
    /// against a 12 ms target for about forty seconds.</para>
    ///
    /// <para>This feeds that exact sequence in. Without the acquisition gate the −999 is taken whole —
    /// that half is the flaw, pinned so it cannot be quietly "fixed" by breaking the gate. With the gate,
    /// nothing is trusted until two readings agree, and tracking starts at +1 ppm.</para>
    ///
    /// <para>WASAPI-only: this is the output stage's clock, and ASIO has no output stage.</para>
    /// </summary>
    private static string? AuditSettlingEndpointIsNotTakenAsAClock()
    {
        double[] fieldReadingsPpm = [-999, +998, +800, 0, +2];

        // WITHOUT THE GATE — how every other caller of the tracker behaves, and how this stage behaved.
        var ungated = new DriftRatioTracker(48000, windowSec: 0.001);
        long fed = 0, drained = 0;
        ungated.Update(0, 0, 576, 576);                            // anchors the window
        FeedClockWindow(ungated, ref fed, ref drained, 0);         // start-up fill, discarded
        FeedClockWindow(ungated, ref fed, ref drained, fieldReadingsPpm[0]);
        var ungatedPpm = (ungated.ClockRatio - 1.0) * 1e6;
        Check(ungated.IsTracking && Math.Abs(ungatedPpm - fieldReadingsPpm[0]) < 3,
            $"premise: without the gate the first settling reading is taken whole (tracking at {ungatedPpm:0} ppm) — this is "
            + "exactly what pushed Ed's cushion to 30 ms, and if it no longer happens the test below proves nothing");

        // WITH THE GATE.
        var gated = new DriftRatioTracker(48000, windowSec: 0.001, acquireAgreementPpm: DriftRatioTracker.DeviceAcquisitionAgreementPpm);
        fed = 0; drained = 0;
        gated.Update(0, 0, 576, 576);
        FeedClockWindow(gated, ref fed, ref drained, 0);
        for (var i = 0; i < fieldReadingsPpm.Length - 1; i++)
        {
            FeedClockWindow(gated, ref fed, ref drained, fieldReadingsPpm[i]);
            Check(!gated.IsTracking,
                $"a settling endpoint must not be tracked on reading {i + 1} ({fieldReadingsPpm[i]:+0;-0} ppm) — the readings so far "
                + "swing from one side to the other, and a real crystal does not");
            Check(gated.AppliedRatio == 1.0, $"nothing may be applied while acquiring (applied {gated.AppliedRatio:0.000000})");
        }
        FeedClockWindow(gated, ref fed, ref drained, fieldReadingsPpm[^1]);
        var gatedPpm = (gated.ClockRatio - 1.0) * 1e6;
        Check(gated.IsTracking, "once two readings agree (0 and +2 ppm) the tracker must start");
        Check(Math.Abs(gatedPpm - 1.0) < 3,
            $"it must start at the agreed value, +1 ppm, not at any of the swings (got {gatedPpm:0.0} ppm)");

        // STEADY STATE UNCHANGED. Once tracking, the 70/30 smoothing is exactly what it always was.
        FeedClockWindow(gated, ref fed, ref drained, 0);
        var smoothedPpm = (gated.ClockRatio - 1.0) * 1e6;
        Check(Math.Abs(smoothedPpm - 0.7) < 0.5,
            $"after acquisition the gate must be out of the way — the next reading is smoothed 70/30 as always (got {smoothedPpm:0.00} ppm, expected 0.70)");

        // BOUNDED. A link too noisy for two windows ever to agree must not leave the stage uncorrected for ever.
        var noisy = new DriftRatioTracker(48000, windowSec: 0.001, acquireAgreementPpm: DriftRatioTracker.DeviceAcquisitionAgreementPpm);
        fed = 0; drained = 0;
        noisy.Update(0, 0, 576, 576);
        FeedClockWindow(noisy, ref fed, ref drained, 0);
        for (var i = 1; i <= DriftRatioTracker.MaxAcquisitionWindows; i++)
        {
            var ppm = i % 2 == 0 ? -500 : +500;
            FeedClockWindow(noisy, ref fed, ref drained, ppm);
            if (i < DriftRatioTracker.MaxAcquisitionWindows)
                Check(!noisy.IsTracking, $"still acquiring after {i} disagreeing readings");
        }
        Check(noisy.IsTracking,
            $"after {DriftRatioTracker.MaxAcquisitionWindows} disagreeing readings the gate must fall back to the old rule rather than "
            + "never correcting at all — on a noisy link, no worse than before");

        Check(DriftRatioTracker.DeviceAcquisitionAgreementPpm == 100 && DriftRatioTracker.MaxAcquisitionWindows == 6,
            $"the gate's numbers are pinned (agreement {DriftRatioTracker.DeviceAcquisitionAgreementPpm} ppm, give up after "
            + $"{DriftRatioTracker.MaxAcquisitionWindows} readings) — a change to either is a change to how long a re-opened device runs uncorrected");

        return $"the field sequence {string.Join(", ", fieldReadingsPpm.Select(p => p.ToString("+0;-0")))} ppm is taken whole at "
             + $"{ungatedPpm:0} ppm without the gate, and with it nothing is trusted until 0 and +2 agree, starting at {gatedPpm:+0.0} ppm; "
             + "steady-state smoothing is unchanged and a noisy link falls back after six readings";
    }

    private static void FeedClockWindow(DriftRatioTracker tracker, ref long fed, ref long drained, double ppm)
    {
        var bytes = 48000L * 2 * sizeof(float) * 10;
        fed += (long)Math.Round(bytes * (1 + ppm / 1e6));
        drained += bytes;
        tracker.RewindWindowStartForTest(1.0);
        tracker.Update(fed, drained, 576, 576);
    }

    /// <summary>
    /// AFTER A WAKE, THE REPORT BURSTS — AND SPLITS NETWORK GAPS FROM RENDER GAPS.
    ///
    /// <para>The short wake on 2026-09-10 was over in about ninety seconds, inside what would have been one
    /// five-minute report. The overnight wake on 2026-09-09 was the opposite: minutes of genuine underruns
    /// on BOTH lanes, and no line in the log could say whether packets were arriving late or devices were
    /// rendering late. So after a wake the report runs every ten seconds for three minutes, each line says
    /// how long after the wake it was taken, and each carries the worst network arrival gap and the worst
    /// render gap per lane.</para>
    ///
    /// <para>Both lanes, and a dash for anything not measured — never a fake zero.</para>
    /// </summary>
    private static string? AuditPostWakeBurstSplitsNetworkFromRender()
    {
        var measured = LongRunReport.Format(new LongRunReport.Reading(
            Uptime: TimeSpan.FromHours(8), Tag: "after-resume +10s", Configuration: AudioConfiguration.Both,
            WasapiBufferMs: 42, WasapiTargetMs: 65, WasapiUnderrunsPerSec: 11.9, WasapiLearnedFloorMs: 0, WasapiRenderMsPerSec: 18.9,
            AsioBufferMs: 20, AsioTargetMs: 30, AsioUnderrunsPerSec: 0.0, AsioLearnedFloorMs: 0, AsioRenderMsPerSec: 39.4,
            Stages: [], SendFramesPerSec: 0, SourceDrift: "[]",
            NetGapMaxMs: 412, WasapiRenderGapMaxMs: 57, AsioRenderGapMaxMs: 3));
        Check(measured.Contains("net=412ms", StringComparison.Ordinal)
              && measured.Contains("wasapiRender=57ms", StringComparison.Ordinal)
              && measured.Contains("asioRender=3ms", StringComparison.Ordinal),
            $"the line must carry the network gap and each lane's render gap separately (got: {measured})");

        var unmeasured = LongRunReport.Format(new LongRunReport.Reading(
            Uptime: TimeSpan.FromHours(8), Tag: "", Configuration: AudioConfiguration.WasapiOnly,
            WasapiBufferMs: 0, WasapiTargetMs: 37, WasapiUnderrunsPerSec: 0, WasapiLearnedFloorMs: 0, WasapiRenderMsPerSec: null,
            AsioBufferMs: 0, AsioTargetMs: 30, AsioUnderrunsPerSec: 0, AsioLearnedFloorMs: 0, AsioRenderMsPerSec: null,
            Stages: [], SendFramesPerSec: 0, SourceDrift: "[]"));
        Check(unmeasured.Contains("net=-", StringComparison.Ordinal)
              && unmeasured.Contains("wasapiRender=-", StringComparison.Ordinal)
              && unmeasured.Contains("asioRender=-", StringComparison.Ordinal),
            $"a gap that was not measured must read as a dash, never as zero (got: {unmeasured})");

        Check(LongRunReport.BurstInterval <= TimeSpan.FromSeconds(15) && LongRunReport.BurstLength >= TimeSpan.FromMinutes(2)
              && LongRunReport.BurstLength <= TimeSpan.FromMinutes(10),
            $"the burst must be fine-grained enough to see a ninety-second episode and long enough to cover minutes of post-wake "
            + $"underruns (interval {LongRunReport.BurstInterval}, length {LongRunReport.BurstLength})");

        MainForm form;
        try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
        catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }

        using (form)
        {
            var lines = new List<string>();
            form.LogForTest.EventTapForTest = line => { lock (lines) lines.Add(line); };
            try
            {
                form.SnapshotTickForTest();        // the first report of the session
                lines.Clear();
                form.SnapshotTickForTest();
                Check(!lines.Any(l => l.StartsWith("longrun ", StringComparison.Ordinal)), "premise: no report until the interval is up");

                form.SimulateWakeForTest();
                lines.Clear();
                form.SnapshotTickForTest();
                var burst = Require(lines.FirstOrDefault(l => l.StartsWith("longrun ", StringComparison.Ordinal)),
                    $"a wake must bring the next report forward rather than wait out five minutes. Tick logged: [{string.Join(" | ", lines)}]");
                Check(burst.Contains("[after-resume +0s]", StringComparison.Ordinal),
                    $"a post-wake line must say how long after the wake it was taken (got: {burst})");
                Check(burst.Contains("gaps net=", StringComparison.Ordinal),
                    $"and must carry the network/render gap split (got: {burst})");

                lines.Clear();
                form.SnapshotTickForTest();
                Check(!lines.Any(l => l.StartsWith("longrun ", StringComparison.Ordinal)),
                    "the burst must still keep its own interval, not write every second");
            }
            finally { form.LogForTest.EventTapForTest = null; }
        }

        return $"after a wake the report runs every {LongRunReport.BurstInterval.TotalSeconds:0} s for {LongRunReport.BurstLength.TotalMinutes:0} minutes, "
             + "tagged with time since the wake, carrying network and per-lane render gaps with dashes for anything unmeasured";
    }

    /// <summary>
    /// A WAKE PUTS BACK THE TUNE THE TUNER HAD SETTLED ON — NOT WHATEVER IT HAD AT THE LAST MOMENT.
    ///
    /// <para>What goes back is the median slider over the five minutes that ended a minute before the sleep, with the
    /// floors as they stood at the end of those five minutes. It is fed the shape of Ed's laptop on 2026-09-12: the tuner
    /// hunting through the settled window, then a raise with the floors lost in the last minute, when the Roger On was
    /// unplugged. The obvious rule — whatever it had when it went to sleep — puts back the unplug.</para>
    ///
    /// <para>And a history trimmed by age would lose everything on the first tick after a long sleep, so five hours asleep
    /// must change nothing; it must stay bounded; and an app started just before the sleep puts back what it had.</para>
    /// </summary>
    private static string? AuditWakePutsBackTheSettledTuneNotTheLastMoment()
    {
        var slept = new DateTime(2026, 9, 12, 14, 46, 47, DateTimeKind.Utc);
        Check(new TuneHistory().BeforeSleep(slept) is null, "nothing recorded must put nothing back");

        var history = new TuneHistory();
        FeedFieldShapedTuneHistory(history.Note, slept);
        var putOrNothing = history.BeforeSleep(slept);
        Check(putOrNothing is not null, "seven minutes recorded before the sleep must give a tune to put back");
        var put = putOrNothing!.Value;
        Check(put.MainSliderMs == 37 && put.AsioSliderMs == 27,
            "the sliders put back must be what the tuner settled on over the five minutes before the machine went down — "
            + "WASAPI 37, ASIO 27 — not the raise on the unplug in the last minute (41, 31) nor the stale 80s from before "
            + $"(got {put.MainSliderMs}, {put.AsioSliderMs})");
        Check(put.MixedFloorMs == 30 && put.WasapiFloorMs == 35 && put.AsioFloorMs == 26,
            "the floors put back must be where they stood at the end of the settled window (30/35/26), not what the unplug "
            + $"left behind (got {put.MixedFloorMs}/{put.WasapiFloorMs}/{put.AsioFloorMs})");

        history.Note(new TuneSnapshot(slept.AddHours(5), 99, 99, 0, 0, 0));
        Check(history.BeforeSleep(slept) == put,
            "five hours asleep and a sample after waking must change nothing about what was true before the sleep — the history is "
            + "trimmed by count, never by age, or the first tick after waking would throw away exactly what the restart needs");

        // WHEN THE SLEEP BEGAN is read from the history itself: nothing is recorded while the machine sleeps.
        var foundSleep = history.SleepStartedBefore(slept.AddHours(5).AddSeconds(4), SuspendDetector.Threshold);
        Check(foundSleep == slept,
            $"the sleep must be found where the samples stop — the last one before the five-hour gap, {slept:HH:mm:ss} — not where "
            + $"they start again (got {foundSleep:HH:mm:ss})");
        Check(new TuneHistory().SleepStartedBefore(slept, SuspendDetector.Threshold) is null, "an empty history has no sleep in it");

        var bounded = new TuneHistory();
        for (var i = 0; i < TuneHistory.MaxSamples * 3; i++) bounded.Note(new TuneSnapshot(slept.AddSeconds(i), 30, 30, 0, 0, 0));
        Check(bounded.Count == TuneHistory.MaxSamples,
            $"the history must stay bounded at {TuneHistory.MaxSamples} samples (holds {bounded.Count})");

        var brief = new TuneHistory();
        for (var s = 20; s >= 1; s--) brief.Note(new TuneSnapshot(slept.AddSeconds(-s), 45, 33, 0, 0, 0));
        Check(brief.BeforeSleep(slept) is { MainSliderMs: 45, AsioSliderMs: 33 },
            "an app that had run for only twenty seconds before the sleep has no settled window, and must put back what it had rather than nothing");
        Check(brief.SleepStartedBefore(slept, SuspendDetector.Threshold) is null,
            "samples one second apart right up to now are no sleep at all — nothing may be restored for a gap that is not there");

        return "the tune put back is the median of the five minutes before the machine went down (37/27 ms) with the floors from "
             + "then (30/35/26) — not the unplug raise in its last minute — five hours asleep does not age it out, the history "
             + $"stays at {TuneHistory.MaxSamples} samples, and a brief run puts back what it had";
    }

    /// <summary>
    /// WAKING STOPS ALL THE AUDIO, STARTS IT AGAIN, AND PUTS BACK THE TUNE FROM BEFORE THE SLEEP.
    ///
    /// <para>Ed, 2026-09-12: "stop all the streams and restart them with the auto tune before it went to sleep." Three
    /// versions had tried to teach the tuner which readings to believe after a wake. The 4.8-hour hibernate that afternoon
    /// still ended with the ASIO lane's floor wiped, its buffer dropped from 26 to 21 ms on the first decision, and
    /// minutes of underruns.</para>
    ///
    /// <para>Which tune goes back is AuditWakePutsBackTheSettledTuneNotTheLastMoment. This is the wiring, in all three
    /// configurations. A real headless window playing a live stream sleeps for 98 seconds and gets Windows' Resume. It must
    /// stop — every stream session gone — put the tune back, rebuild, and play again, in that order. The main slider must
    /// come back on the lane it drives in that configuration (the Mixed route in WASAPI-only, the WASAPI lane otherwise),
    /// the ASIO slider on the ASIO lane, and every lane's floor with them. The restart's own streams arriving and its
    /// outputs starting again must not wipe it. And after the minute, a genuinely new stream must still be treated as
    /// new.</para>
    ///
    /// <para><b>Honest scope.</b> The send side restarts through the same calls the Send tick uses, but a headless gate has
    /// no capture device to start, so the receive side carries the observable proof. No sound device is opened: the ticked
    /// outputs are synthetic list entries that name no device.</para>
    /// </summary>
    private static string? AuditWakeRestartsTheAudioWithTheTuneFromBeforeSleep()
    {
        var proven = new List<string>();
        foreach (var configuration in AudioConfigurations.All)
        {
            MainForm form;
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }

            using (form)
            {
                var lines = new List<string>();
                form.LogForTest.EventTapForTest = line => { lock (lines) lines.Add(line); };
                var receiver = form.ReceiverForTest;
                try
                {
                    SetAudioConfigurationForTest(form, configuration);
                    // The app sets this whenever the mode changes (ApplyAsioMode); the helper only chooses the driver, so do
                    // what the app does. One slider means one shared buffer; a driver chosen means one per lane.
                    receiver.SetIndependentLaneLatency(configuration != AudioConfiguration.WasapiOnly);
                    var name = configuration.Describe();
                    var mainRoute = configuration == AudioConfiguration.WasapiOnly ? RenderRoute.Mixed : RenderRoute.WasapiLane;

                    // The per-second tick is what records the history a restart reads.
                    var recordedBefore = form.TuneHistoryCountForTest;
                    form.SnapshotTickForTest();
                    Check(form.TuneHistoryCountForTest == recordedBefore + 1,
                        $"{name}: the per-second tick must record what the tuner has, or there is nothing to put back after a wake "
                        + $"({recordedBefore} before the tick, {form.TuneHistoryCountForTest} after)");

                    // SEVEN MINUTES OF TUNE BEFORE A SLEEP THAT BEGAN 98 SECONDS AGO, AND WHAT THE LAST MINUTE LEFT.
                    var sleptAt = DateTime.UtcNow - TimeSpan.FromSeconds(98);
                    var reference = new TuneHistory();
                    FeedFieldShapedTuneHistory(sample => { reference.Note(sample); form.NoteTuneHistoryForTest(sample); }, sleptAt);
                    var expectedOrNothing = reference.BeforeSleep(sleptAt);
                    Check(expectedOrNothing is not null, $"{name}: premise — the fed history must give a tune");
                    var expected = expectedOrNothing!.Value;
                    form.SetTuneForTest(new TuneSnapshot(DateTime.UtcNow, 41, 31, 0, 0, 0));

                    // ASLEEP FOR 98 SECONDS. THE TICK NOTICES.
                    form.RewindTickClockForTest(TimeSpan.FromSeconds(98));
                    form.SnapshotTickForTest();

                    // PLAYING A LIVE STREAM when Windows reports the resume, as the laptop was.
                    var peer = new IPEndPoint(IPAddress.Parse("192.168.77.20"), 47830);
                    receiver.SetAllowedSenders([peer]);
                    receiver.SetPlaybackEnabled(true);
                    var format = new AudioFormatInfo(48000, 2, 16, 1, 4, 192_000, (int)AudioTransportCodec.Opus, 480);
                    receiver.InjectExternalPacket(BuildFormatPacket(11, 1, format), FormatPacketLength(format), peer);
                    Check(receiver.IsRunning && receiver.LiveSessionCountForTest == 1,
                        $"{name}: premise — the receiver must be playing a live stream at the resume, or the restart proves nothing "
                        + $"(playing={receiver.IsRunning}, sessions={receiver.LiveSessionCountForTest})");

                    lines.Clear();
                    form.SimulateSystemResumeForTest();

                    string[] steps = ["power: woke up — stopping all audio", "power: all audio stopped", "put back the tune from before the sleep",
                                      "power: audio backend re-initialised", "power: all audio started again"];
                    var at = steps.Select(step => lines.FindIndex(l => l.Contains(step, StringComparison.Ordinal))).ToArray();
                    Check(at.All(i => i >= 0) && at.Zip(at.Skip(1)).All(pair => pair.First < pair.Second),
                        $"{name}: waking must stop the audio, put the tune back, rebuild the devices and start again — in that order. "
                        + $"Logged: [{string.Join(" | ", lines)}]");
                    Check(at[..3].All(i => i >= 0 && lines[i].Contains($"[{name}]", StringComparison.Ordinal)),
                        $"{name}: the stop and tune lines must carry the configuration they happened in. Logged: [{string.Join(" | ", lines)}]");

                    Check(receiver.LiveSessionCountForTest == 0,
                        $"{name}: every stream from before the sleep must be gone — that is what stopping all the streams means "
                        + $"({receiver.LiveSessionCountForTest} still open)");
                    Check(receiver.IsRunning, $"{name}: and the receiver must be playing again — stopped and never started is silence");

                    var tune = form.CurrentTuneForTest();
                    Check(tune.MainSliderMs == expected.MainSliderMs && tune.AsioSliderMs == expected.AsioSliderMs,
                        $"{name}: both sliders must be back where the tuner had them before the sleep ({expected.MainSliderMs}/{expected.AsioSliderMs}), "
                        + $"not left on the unplug raise (got {tune.MainSliderMs}/{tune.AsioSliderMs})");
                    // In WASAPI-only there is one shared buffer and the ASIO slider governs nothing, so only the main one is audible.
                    var asioTargetBack = configuration == AudioConfiguration.WasapiOnly
                        || receiver.MaxLatencyMsFor(RenderRoute.AsioLane) == expected.AsioSliderMs;
                    Check(receiver.MaxLatencyMsFor(mainRoute) == expected.MainSliderMs && asioTargetBack,
                        $"{name}: the receiver must be playing at them — the main slider drives {mainRoute} in this configuration "
                        + $"(receiver {mainRoute}={receiver.MaxLatencyMsFor(mainRoute)}, AsioLane={receiver.MaxLatencyMsFor(RenderRoute.AsioLane)})");
                    Check(tune.MixedFloorMs == expected.MixedFloorMs && tune.WasapiFloorMs == expected.WasapiFloorMs && tune.AsioFloorMs == expected.AsioFloorMs,
                        $"{name}: every lane's learned floor must be back ({expected.MixedFloorMs}/{expected.WasapiFloorMs}/{expected.AsioFloorMs}; "
                        + $"got {tune.MixedFloorMs}/{tune.WasapiFloorMs}/{tune.AsioFloorMs})");
                    Check(TunedLanesForTest.All(lane => !form.TuneLaneAllowed(lane, DateTime.UtcNow)),
                        $"{name}: every lane must be held while the restarted devices settle");

                    // THE RESTART'S OWN STREAMS ARRIVE AND ITS OUTPUTS START AGAIN — neither may wipe what was put back.
                    form.NewStreamSessionsForTest();
                    foreach (var lane in new[] { RenderRoute.WasapiLane, RenderRoute.AsioLane })
                    {
                        form.NoteLaneAudibilityForTest(lane, consuming: false);
                        form.NoteLaneAudibilityForTest(lane, consuming: true);
                    }
                    var kept = form.CurrentTuneForTest();
                    Check(kept with { AtUtc = default } == expected with { AtUtc = default },
                        $"{name}: the restart's own streams arriving and its outputs starting again must keep the tune from before the "
                        + $"sleep — forgetting it then is how the ASIO lane lost its floor on 2026-09-12 (expected {expected.Describe()}; got {kept.Describe()})");

                    // AFTER THE MINUTE, A NEW STREAM IS NEW.
                    form.EndWakeTuneWindowForTest();
                    form.NewStreamSessionsForTest();
                    var later = form.CurrentTuneForTest();
                    Check(later.MixedFloorMs == 0 && later.WasapiFloorMs == 0 && later.AsioFloorMs == 0,
                        $"{name}: once the minute after the restart is over, a new stream must be treated as new and re-earn its floors "
                        + $"(got {later.Describe()})");
                    proven.Add(name);
                }
                finally
                {
                    form.LogForTest.EventTapForTest = null;
                    receiver.SetPlaybackEnabled(false);
                }
            }
        }

        return $"in {string.Join(", ", proven)}, a window playing a live stream stops every stream when it wakes, puts the tune from "
             + "before the sleep back on the lanes this configuration's sliders drive, rebuilds, plays again, keeps the tune through "
             + "its own streams and outputs returning, and treats a stream after the minute as new";
    }

    /// <summary>
    /// A WAKE PUTS BACK THE TUNE WITH LOGGING SWITCHED OFF — AND AUTO-TUNE OFF TOO.
    ///
    /// <para>Ed, 2026-09-13: "the new resume behaviour takes the last tuned settings when it resumes ... but does it do this
    /// even with the logs turned off?" It does, and this pins why. The per-second tick records the tuner's history ABOVE the
    /// diagnostics switch, and the restart comes from Windows' resume notice, which has nothing to do with logging. A later
    /// change that moved the recording below that switch, or behind the log, would break the restore only for people who
    /// never turn logging on — which is almost everybody, and nobody would see it in a log.</para>
    ///
    /// <para>So everything that can hold the diagnostics switch on is turned off first — logging, the main auto-tune and the
    /// ASIO auto-tune — and the switch is checked off before and after. The wiring itself is
    /// AuditWakeRestartsTheAudioWithTheTuneFromBeforeSleep; this proves it does not lean on logging.</para>
    /// </summary>
    private static string? AuditWakeRestoresTheTuneWithLogsOff()
    {
        var restoreGate = DiagnosticsGate.Enabled;
        var proven = new List<string>();
        try
        {
            foreach (var configuration in AudioConfigurations.All)
            {
                MainForm form;
                try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
                catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }

                using (form)
                {
                    var receiver = form.ReceiverForTest;
                    try
                    {
                        SetAudioConfigurationForTest(form, configuration);
                        receiver.SetIndependentLaneLatency(configuration != AudioConfiguration.WasapiOnly);
                        var name = configuration.Describe();

                        // EVERYTHING THAT COULD HOLD THE SWITCH ON, OFF.
                        form.LogForTest.Enabled = false;
                        form.SetContinuousTuneForTest(false);
                        const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                        var settingsField = Require(typeof(MainForm).GetField("settings", Private), "MainForm.settings not found");
                        var settings = Require(settingsField.GetValue(form), "MainForm.settings is empty");
                        Require(settings.GetType().GetMethod("SaveContinuousAutoTuneAsioEnabled"), "SaveContinuousAutoTuneAsioEnabled not found")
                            .Invoke(settings, [false]);
                        Require(typeof(MainForm).GetMethod("UpdateDiagnosticsGate", Private), "MainForm.UpdateDiagnosticsGate not found").Invoke(form, null);
                        Check(!DiagnosticsGate.Enabled,
                            $"{name}: premise — with logging and both auto-tunes off the diagnostics switch must be off, or this proves nothing");

                        var sleptAt = DateTime.UtcNow - TimeSpan.FromSeconds(98);
                        var recordedBefore = form.TuneHistoryCountForTest;
                        form.SnapshotTickForTest();
                        Check(form.TuneHistoryCountForTest == recordedBefore + 1,
                            $"{name}: with logging off, the per-second tick must still record what the tuner has — it is the only record a "
                            + $"wake has to put back ({recordedBefore} before the tick, {form.TuneHistoryCountForTest} after)");

                        var reference = new TuneHistory();
                        FeedFieldShapedTuneHistory(sample => { reference.Note(sample); form.NoteTuneHistoryForTest(sample); }, sleptAt);
                        Check(reference.BeforeSleep(sleptAt) is not null, $"{name}: premise — the fed history must give a tune");
                        var expectedTune = reference.BeforeSleep(sleptAt)!.Value;
                        form.SetTuneForTest(new TuneSnapshot(DateTime.UtcNow, 41, 31, 0, 0, 0));

                        form.RewindTickClockForTest(TimeSpan.FromSeconds(98));
                        form.SnapshotTickForTest();
                        form.SimulateSystemResumeForTest();

                        var tune = form.CurrentTuneForTest();
                        Check(tune.MainSliderMs == expectedTune.MainSliderMs && tune.AsioSliderMs == expectedTune.AsioSliderMs,
                            $"{name}: with logging off, waking must still put both sliders back where the tuner had them before the sleep "
                            + $"({expectedTune.MainSliderMs}/{expectedTune.AsioSliderMs}; got {tune.MainSliderMs}/{tune.AsioSliderMs})");
                        Check(tune.MixedFloorMs == expectedTune.MixedFloorMs && tune.WasapiFloorMs == expectedTune.WasapiFloorMs
                              && tune.AsioFloorMs == expectedTune.AsioFloorMs,
                            $"{name}: ...and every lane's learned floor ({expectedTune.MixedFloorMs}/{expectedTune.WasapiFloorMs}/{expectedTune.AsioFloorMs}; "
                            + $"got {tune.MixedFloorMs}/{tune.WasapiFloorMs}/{tune.AsioFloorMs})");
                        Check(!DiagnosticsGate.Enabled,
                            $"{name}: the wake must not have needed the diagnostics switch on — it is on again after the restart");
                        proven.Add(name);
                    }
                    finally { receiver.SetPlaybackEnabled(false); }
                }
            }
        }
        finally { DiagnosticsGate.Enabled = restoreGate; }

        return $"in {string.Join(", ", proven)}, with logging and both auto-tunes off, the tick still records the tune and a wake "
             + "puts the sliders and floors from before the sleep back";
    }

    /// <summary>Seven minutes of tuner history shaped like Ed's laptop before the 2026-09-12 hibernate: stale values from
    /// before the settled window; the tuner hunting through the five minutes that count (WASAPI 32–43, ASIO 22–33, floors
    /// 30/35/26); and the last minute, when the Roger On was unplugged, the tuner raised on it and the floors went.</summary>
    private static void FeedFieldShapedTuneHistory(Action<TuneSnapshot> note, DateTime sleptAt)
    {
        int[] wasapiHunt = [32, 37, 43, 37];
        int[] asioHunt = [22, 27, 33, 27];
        for (var s = 7 * 60; s >= 0; s--)
        {
            var at = sleptAt.AddSeconds(-s);
            note(s >= 6 * 60 ? new TuneSnapshot(at, 80, 80, 0, 0, 0)
               : s >= 60 ? new TuneSnapshot(at, wasapiHunt[s / 10 % 4], asioHunt[s / 10 % 4], 30, 35, 26)
               : new TuneSnapshot(at, 41, 31, 0, 0, 0));
        }
    }
}
