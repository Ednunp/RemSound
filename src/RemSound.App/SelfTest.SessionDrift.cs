using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App;

/// <summary>
/// The per-person timing correction on the receiving side: each sender's own clock-drift loop in SessionPlayout. Ed,
/// 2026-09-15: leave it as it is. Its window starts only once that person's buffer has filled to target, so the filling-up
/// never gets into its first reading, and it already ignores a wildly wrong reading. Nothing drove this loop before these
/// steps. They run a real SessionPlayout on a simulated clock, on every lane of every setup, and pin what it does today,
/// so a later change that makes it worse is seen.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>One simulated output lane: the block it pulls, the latency and smoothness it starts at, the sender's clock
    /// and the output card's clock, both against the Stopwatch.</summary>
    private sealed record DriftLane(string Name, int BlockFrames, int TargetMs, int Smoothness, double SenderRatio, double CardRate)
    {
        /// <summary>The ratio this lane's loop should find: sender against card.</summary>
        public double ClockRatio => SenderRatio / CardRate;
    }

    /// <summary>A simulated Stopwatch shared by every session in a run. Never zero: the loop reads zero as "not started".</summary>
    private sealed class DriftClock
    {
        public long Ticks = Stopwatch.Frequency;
        public void SetSeconds(double seconds) => Ticks = Stopwatch.Frequency + (long)(seconds * Stopwatch.Frequency);
    }

    /// <summary>One lane being driven: its session, the target it reads at now, when it next writes and reads, and for
    /// each whole second (index = second) the average depth just before each pull, which is what the loop steers, and the
    /// buffer's time-weighted average level, which is what a rate of fall is measured on.</summary>
    private sealed class DriftRun(DriftLane lane, SessionPlayout session)
    {
        public DriftLane Lane { get; } = lane;
        public SessionPlayout Session { get; } = session;
        public int TargetMs = lane.TargetMs;
        public double NextWriteSec;
        public double NextReadSec = 0.0005;
        public readonly float[] ReadBuffer = new float[4096];
        public readonly List<double> LevelPerSecond = [];
        public double LevelSec;
        public readonly List<double> DepthPerSecond = [];
        public int Second = -1;
        public double DepthSum;
        public int DepthReads;
    }

    /// <summary>One cycle of a 1 kHz tone at 48 kHz, stereo: what every simulated packet carries.</summary>
    private static readonly float[] DriftToneCycle = [.. Enumerable.Range(0, 96).Select(i => 0.25f * MathF.Sin(2 * MathF.PI * (i / 2) / 48f))];

    /// <summary>The lanes a setup plays through. A WASAPI output pulls 480 frames every 10 ms; an ASIO output pulls 128-frame
    /// driver blocks. With both, each lane runs its own copy of the loop, and the ASIO card's crystal runs 300 ppm slow
    /// against the WASAPI one, so the two loops have different clocks to find.</summary>
    private static DriftRun[] NewDriftRuns(DriftClock clock, AudioConfiguration configuration, double senderRatio,
        (int TargetMs, int Smoothness) wasapi, (int TargetMs, int Smoothness) asio)
    {
        var lanes = new List<DriftLane>();
        if (configuration.UsesWasapi()) lanes.Add(new("WASAPI output", 480, wasapi.TargetMs, wasapi.Smoothness, senderRatio, 1.0));
        if (configuration.UsesAsio())
            lanes.Add(new("ASIO output", 128, asio.TargetMs, asio.Smoothness, senderRatio, configuration.HasTwoLanes() ? 0.9997 : 1.0));
        return [.. lanes.Select(lane => new DriftRun(lane,
            new SessionPlayout(new IPEndPoint(IPAddress.Loopback, 47830), 1, 1 << 20) { NowTicks = () => clock.Ticks }))];
    }

    /// <summary>Advance to <paramref name="untilSec"/>: the sender writes <paramref name="packetMs"/> packets on its clock
    /// and each lane pulls its blocks on its card's, in time order.</summary>
    private static void RunDrift(DriftClock clock, IReadOnlyList<DriftRun> runs, double untilSec, int packetMs)
    {
        var packet = new float[48 * packetMs * 2];
        for (var i = 0; i < packet.Length; i++) packet[i] = DriftToneCycle[i % DriftToneCycle.Length];
        while (true)
        {
            DriftRun? next = null;
            var isWrite = false;
            var at = double.MaxValue;
            foreach (var run in runs)
            {
                if (run.NextWriteSec < at) { at = run.NextWriteSec; next = run; isWrite = true; }
                if (run.NextReadSec < at) { at = run.NextReadSec; next = run; isWrite = false; }
            }
            if (next is null || at > untilSec) return;
            clock.SetSeconds(at);
            IntegrateLevel(next, at);

            if (isWrite)
            {
                next.Session.Write(MemoryMarshal.AsBytes(packet.AsSpan()));
                next.Session.NoteFramesQueued(next.TargetMs);
                next.NextWriteSec += packetMs / 1000.0 / next.Lane.SenderRatio;
                continue;
            }
            var second = (int)at;
            if (second != next.Second)
            {
                if (next.DepthReads > 0) next.DepthPerSecond.Add(next.DepthSum / next.DepthReads);
                next.Second = second;
                next.DepthSum = 0;
                next.DepthReads = 0;
            }
            // Depth as the loop sees it: just before the pull.
            next.DepthSum += next.Session.BufferedBytes / 8.0 * 1000 / 48000;
            next.DepthReads++;
            next.Session.ReadFloats(next.ReadBuffer, next.Lane.BlockFrames, next.TargetMs, 1000, next.Lane.Smoothness,
                applyShaping: false, emitRecordTap: false);
            next.NextReadSec += next.Lane.BlockFrames / (48000.0 * next.Lane.CardRate);
        }
    }

    /// <summary>A sender's backlog arriving all at once: <paramref name="ms"/> of audio in 2 ms packets, back to back.</summary>
    private static void DriftBurst(DriftRun run, int ms)
    {
        var packet = new float[96 * 2];
        for (var i = 0; i < packet.Length; i++) packet[i] = DriftToneCycle[i % DriftToneCycle.Length];
        for (var i = 0; i < ms / 2; i++) run.Session.Write(MemoryMarshal.AsBytes(packet.AsSpan()));
        run.Session.NoteFramesQueued(run.TargetMs);
    }

    /// <summary>Add the buffer's level since this lane's last event to its per-second time-weighted average. The level only
    /// changes at this lane's own events, so it is flat in between. A depth sampled at each pull beats against the packet
    /// rate once the card's clock slides past the sender's, and read a 3 ms-a-second fall as 3.27; this does not.</summary>
    private static void IntegrateLevel(DriftRun run, double untilSec)
    {
        var levelMs = run.Session.BufferedBytes / 8.0 * 1000 / 48000;
        for (var from = run.LevelSec; from < untilSec;)
        {
            var second = (int)from;
            var to = Math.Min(untilSec, second + 1.0);
            while (run.LevelPerSecond.Count <= second) run.LevelPerSecond.Add(0);
            run.LevelPerSecond[second] += levelMs * (to - from);
            from = to;
        }
        run.LevelSec = untilSec;
    }

    /// <summary>The most the buffer's average level fell from one second to the next, over (<paramref name="fromSecond"/>,
    /// <paramref name="toSecond"/>].</summary>
    private static double SteepestFall(DriftRun run, int fromSecond, int toSecond)
    {
        var fall = 0.0;
        for (var s = fromSecond + 1; s <= toSecond && s < run.LevelPerSecond.Count; s++)
            fall = Math.Max(fall, run.LevelPerSecond[s - 1] - run.LevelPerSecond[s]);
        return fall;
    }

    private static string DriftWhere(AudioConfiguration configuration, DriftRun run) =>
        $"{configuration.Describe()}, the {run.Lane.Name}";

    /// <summary>
    /// THE PER-PERSON TIMING CORRECTION SETTLES ON THE SENDER'S CLOCK.
    ///
    /// <para>A sender 200 ppm slow and one 1,000 ppm fast, for 90 seconds, at a WASAPI output's 40 ms and default smoothness
    /// and an ASIO output's 12 ms and tightest smoothness: the correction must end on the sender's real clock, the buffer
    /// must sit on its target, and once settled nothing may run dry, be trimmed or be dropped.</para>
    /// </summary>
    private static string? AuditPerPersonTimingSettlesOnTheSendersClock()
    {
        var lanesChecked = 0;
        var worstPpm = 0.0;
        var worstOffMs = 0.0;
        foreach (var configuration in AudioConfigurations.All)
        {
            foreach (var senderRatio in new[] { 0.9998, 1.001 })
            {
                var clock = new DriftClock();
                var runs = NewDriftRuns(clock, configuration, senderRatio, wasapi: (40, 3), asio: (12, 1));
                try
                {
                    RunDrift(clock, runs, 30, packetMs: 1);
                    var at30 = runs.Select(r => (r.Session.UnderrunCount, r.Session.TrimFireCount, r.Session.DropCount)).ToArray();
                    RunDrift(clock, runs, 91, packetMs: 1);
                    for (var i = 0; i < runs.Length; i++)
                    {
                        var run = runs[i];
                        lanesChecked++;
                        var where = $"{DriftWhere(configuration, run)} (sender clock {run.Lane.ClockRatio:0.00000} of the card's)";
                        var ppm = Math.Abs(run.Session.DriftResamplerRatio - run.Lane.ClockRatio) * 1e6;
                        worstPpm = Math.Max(worstPpm, ppm);
                        Check(ppm <= 50,
                            $"in {where}, the per-person timing correction must settle on the sender's clock (it settled on "
                            + $"{run.Session.DriftResamplerRatio:0.000000}, {ppm:0} ppm out)");
                        var tail = run.DepthPerSecond.Skip(60).Take(30).ToArray();
                        var offMs = tail.Length == 30 ? tail.Max(d => Math.Abs(d - run.TargetMs)) : double.MaxValue;
                        worstOffMs = Math.Max(worstOffMs, offMs);
                        Check(offMs <= 2,
                            $"in {where}, once the per-person timing correction has settled the buffer must hold its {run.TargetMs} ms "
                            + $"target (it strayed {offMs:0.0} ms in the last 30 seconds)");
                        Check(run.Session.UnderrunCount == at30[i].UnderrunCount && run.Session.TrimFireCount == at30[i].TrimFireCount
                              && run.Session.DropCount == at30[i].DropCount,
                            $"in {where}, once the per-person timing correction has settled nothing may run dry, be trimmed or be dropped "
                            + $"(from 30 to 90 seconds: {run.Session.UnderrunCount - at30[i].UnderrunCount} short reads, "
                            + $"{run.Session.TrimFireCount - at30[i].TrimFireCount} trims, {run.Session.DropCount - at30[i].DropCount} drops)");
                    }
                }
                finally { foreach (var run in runs) run.Session.Dispose(); }
            }
        }
        Check(lanesChecked == 8, $"two senders over the four lanes of the three setups must all have run ({lanesChecked})");
        return $"slow and fast senders: every lane of every setup settled within {worstPpm:0} ppm of the sender's clock and "
            + $"{worstOffMs:0.0} ms of its target, with nothing run dry, trimmed or dropped once settled";
    }

    /// <summary>
    /// THE PER-PERSON TIMING CORRECTION IGNORES A WILDLY WRONG READING.
    ///
    /// <para>700 ms of backlog landing inside one 10-second reading makes that reading 7 % fast. It must be thrown out,
    /// not let in, and not clamped to the edge of what is believable either.</para>
    /// </summary>
    private static string? AuditPerPersonTimingIgnoresAWildReading()
    {
        foreach (var configuration in AudioConfigurations.All)
        {
            var clock = new DriftClock();
            var runs = NewDriftRuns(clock, configuration, 0.9998, wasapi: (40, 3), asio: (12, 1));
            try
            {
                RunDrift(clock, runs, 35, packetMs: 2);
                foreach (var run in runs)
                    Check(run.Session.DriftResamplerUpdates >= 3,
                        $"in {DriftWhere(configuration, run)}, the per-person timing correction must be running before the backlog lands, or this proves nothing");
                var before = runs.Select(r => (r.Session.DriftResamplerRatio, r.Session.DriftResamplerUpdates)).ToArray();
                foreach (var run in runs) DriftBurst(run, 700);
                RunDrift(clock, runs, 41, packetMs: 2);
                for (var i = 0; i < runs.Length; i++)
                {
                    Check(runs[i].Session.DriftResamplerUpdates > before[i].DriftResamplerUpdates,
                        $"in {DriftWhere(configuration, runs[i])}, the reading with the backlog in it must have ended, or this proves nothing");
                    Check(runs[i].Session.DriftResamplerRatio == before[i].DriftResamplerRatio,
                        $"in {DriftWhere(configuration, runs[i])}, a reading 7 % fast must be ignored, not let into the correction "
                        + $"({before[i].DriftResamplerRatio:0.000000} became {runs[i].Session.DriftResamplerRatio:0.000000})");
                }
            }
            finally { foreach (var run in runs) run.Session.Dispose(); }
        }
        return "a reading made 7 % fast by a backlog left the correction exactly where it was, on every lane of every setup";
    }

    /// <summary>
    /// THE PER-PERSON TIMING CORRECTION STARTS AFTER ONE READING, USED AS IT IS.
    ///
    /// <para>Its window starts only once the buffer has filled, so its first 10-second reading holds no start-up fill and is
    /// used whole. Throwing it away, as the shared correction does, would start this one 10 seconds later; blending it with
    /// "no correction" would start it a third of the way there. Ed, 2026-09-15: keep it this way. A sender 0.3 % fast makes
    /// either change stand out.</para>
    /// </summary>
    private static string? AuditPerPersonTimingStartsAfterOneReading()
    {
        var worstPpm = 0.0;
        foreach (var configuration in AudioConfigurations.All)
        {
            var clock = new DriftClock();
            var runs = NewDriftRuns(clock, configuration, 1.003, wasapi: (40, 3), asio: (12, 1));
            try
            {
                RunDrift(clock, runs, 9.5, packetMs: 1);
                foreach (var run in runs)
                    Check(run.Session.DriftResamplerUpdates == 0,
                        $"in {DriftWhere(configuration, run)}, the per-person timing correction must not act before its first 10-second reading ends "
                        + $"({run.Session.DriftResamplerUpdates} updates by 9.5 seconds)");
                RunDrift(clock, runs, 10.5, packetMs: 1);
                foreach (var run in runs)
                {
                    Check(run.Session.DriftResamplerUpdates >= 1,
                        $"in {DriftWhere(configuration, run)}, the per-person timing correction must start after ONE 10-second reading: its window "
                        + "starts once the buffer has filled, so that reading holds no start-up fill (Ed, 2026-09-15: keep it this way). "
                        + "It had not started by 10.5 seconds");
                    var ppm = Math.Abs(run.Session.DriftResamplerRatio - run.Lane.ClockRatio) * 1e6;
                    worstPpm = Math.Max(worstPpm, ppm);
                    Check(ppm <= 100,
                        $"in {DriftWhere(configuration, run)}, the per-person timing correction's first reading must be used as it is "
                        + $"(the sender is {run.Lane.ClockRatio:0.000000}; the correction started on {run.Session.DriftResamplerRatio:0.000000})");
                }
            }
            finally { foreach (var run in runs) run.Session.Dispose(); }
        }
        return $"on every lane of every setup the correction waited out one 10-second reading, then started on it, {worstPpm:0} ppm from the sender";
    }

    /// <summary>
    /// THE PER-PERSON TIMING CORRECTION NEVER BRINGS A LOWERED TARGET DOWN FASTER THAN TODAY.
    ///
    /// <para>Ed's hard rule: the latency descent must never get faster. The auto-tune lowers the target 60 ms, with no drain
    /// and no fast approach, and trimming is off, so only this loop brings the buffer down. It must never fall faster than
    /// the 0.3 % cap allows, about 3 ms a second, must not have come further in its first 25 seconds than it does today,
    /// and must still arrive.</para>
    /// </summary>
    private static string? AuditPerPersonTimingBringsALoweredTargetDownNoFasterThanToday()
    {
        var measured = new List<string>();
        foreach (var configuration in AudioConfigurations.All)
        {
            var clock = new DriftClock();
            var runs = NewDriftRuns(clock, configuration, 1.0, wasapi: (100, 10), asio: (72, 10));
            try
            {
                RunDrift(clock, runs, 25, packetMs: 2);
                foreach (var run in runs) run.TargetMs -= 60;
                RunDrift(clock, runs, 117, packetMs: 2);
                foreach (var run in runs)
                {
                    var fall = SteepestFall(run, 24, 115);
                    Check(fall <= 3.2,
                        $"in {DriftWhere(configuration, run)}, the per-person timing correction must never bring latency down faster than its 0.3 % cap, "
                        + $"about 3 ms a second, after the target is lowered (it fell {fall:0.00} ms in one second)");
                    var in25 = run.LevelPerSecond[24] - run.LevelPerSecond[49];
                    // Today, 2026-09-15: 49.2 ms on every lane (48.9 on the ASIO lane beside WASAPI, whose card runs slow).
                    Check(in25 <= 51,
                        $"in {DriftWhere(configuration, run)}, the per-person timing correction must not bring latency down faster than it does today: "
                        + $"25 seconds after the target was lowered 60 ms it had come down {in25:0.0} ms, where today it comes down 49");
                    var offMs = Math.Abs(run.DepthPerSecond[115] - run.TargetMs);
                    Check(offMs <= 2,
                        $"in {DriftWhere(configuration, run)}, the buffer must still reach its lowered {run.TargetMs} ms target (90 seconds on it was {offMs:0.0} ms away)");
                    measured.Add($"{DriftWhere(configuration, run)} {fall:0.00} ms/s, {in25:0.0} ms in 25 s, {offMs:0.0} ms off at the end");
                }
            }
            finally { foreach (var run in runs) run.Session.Dispose(); }
        }
        return $"a target lowered 60 ms came down no faster than today: {string.Join("; ", measured)}";
    }

    /// <summary>
    /// THE PER-PERSON TIMING CORRECTION NEVER BRINGS A BACKLOG DOWN FASTER THAN TODAY.
    ///
    /// <para>60 ms of backlog lands at once, trimming off. Part of it reads as a fast sender clock for a window or two, so
    /// it comes down faster than the depth nudge alone would take it. That is today's behaviour, and Ed's answer on
    /// 2026-09-15 was to leave the loop alone; it must not get any faster.</para>
    /// </summary>
    private static string? AuditPerPersonTimingBringsABacklogDownNoFasterThanToday()
    {
        var measured = new List<string>();
        foreach (var configuration in AudioConfigurations.All)
        {
            var clock = new DriftClock();
            var runs = NewDriftRuns(clock, configuration, 1.0, wasapi: (60, 10), asio: (60, 10));
            try
            {
                RunDrift(clock, runs, 25, packetMs: 2);
                foreach (var run in runs) DriftBurst(run, 60);
                RunDrift(clock, runs, 117, packetMs: 2);
                foreach (var run in runs)
                {
                    var fall = SteepestFall(run, 25, 115);
                    var lowest = run.DepthPerSecond.Skip(25).Take(91).Min() - run.TargetMs;
                    // Today, 2026-09-15: 4.80 ms a second on every lane (4.83 on the ASIO lane beside WASAPI).
                    Check(fall <= 5.0,
                        $"in {DriftWhere(configuration, run)}, the per-person timing correction must not bring latency down faster than it does today "
                        + $"after 60 ms of backlog lands (it fell {fall:0.00} ms in one second, where today it falls 4.8)");
                    // And it must come down at all, dipping under the target no further than it does today. Only the speed
                    // limit was checked until 2026-09-24, and a correction that never touched the backlog is well inside it.
                    var offMs = Math.Abs(run.DepthPerSecond[115] - run.TargetMs);
                    // Today, 2026-09-24: 2.4 ms off on every lane (3.0 on the ASIO lane beside WASAPI), still settling after the dip below.
                    // A correction that never touched the backlog ends about 60 ms off.
                    Check(offMs <= 5,
                        $"in {DriftWhere(configuration, run)}, 60 ms of backlog must be worked off: 90 seconds on the buffer was still {offMs:0.0} ms from its {run.TargetMs} ms target, where today it is 2.4");
                    // Today, 2026-09-24: 11.6 ms under on every lane (11.7 on the ASIO lane beside WASAPI).
                    Check(lowest >= -13,
                        $"in {DriftWhere(configuration, run)}, working off a backlog must not take the buffer further under its target than today "
                        + $"(it went {-lowest:0.0} ms under, where today it goes 11.6) - under target is where the dropouts are");
                    measured.Add($"{DriftWhere(configuration, run)} {fall:0.00} ms/s, lowest {lowest:0.0} ms against target, {offMs:0.0} ms off at the end");
                }
            }
            finally { foreach (var run in runs) run.Session.Dispose(); }
        }
        return $"60 ms of backlog came down no faster than today: {string.Join("; ", measured)}";
    }
}
