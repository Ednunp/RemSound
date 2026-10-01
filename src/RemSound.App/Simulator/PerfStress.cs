using System.Diagnostics;
using System.Net;
using RemSound.Core;

namespace RemSound.App.Simulator;

/// <summary>What one stress run found in one configuration: the process and every table before the stress and after
/// the return to normal, what the listened peer sounded like before and after, the figures the stress produced, and
/// every way it did not come back.</summary>
internal sealed class StressOutcome(string scenario, SimConfig config)
{
    public string Scenario { get; } = scenario;
    public SimConfig Config { get; } = config;
    public SimCodec Codec { get; init; }
    public ProcessState Before { get; set; }
    public ProcessState After { get; set; }
    public long RigBefore { get; set; }
    public long RigAfter { get; set; }
    public IReadOnlyList<(string Table, long Count)> TablesBefore { get; set; } = [];
    public IReadOnlyList<(string Table, long Count)> TablesAfter { get; set; } = [];
    public SimAnalysis.Findings? HeardBefore { get; set; }
    public SimAnalysis.Findings? HeardAfter { get; set; }
    public List<string> Figures { get; } = new();
    public List<string> Problems { get; } = new();
    public double WallSeconds { get; set; }

    /// <summary>The managed heap's change across the stress, less what the rig itself kept (its packet pool).</summary>
    public long HeapChange => After.HeapBytes - Before.HeapBytes - (RigAfter - RigBefore);

    public string Describe() => $"{PerfBench.Name(Config)}: {string.Join("; ", Figures)}; back to normal: heap {HeapChange / 1024.0:+0;-0} KB, "
        + $"handles {After.Handles - Before.Handles:+0;-0}, threads {After.Threads - Before.Threads:+0;-0}, {(Problems.Any(p => p.StartsWith("tables")) ? "tables NOT as before" : "every table as before")}, "
        + $"the call {(HeardAfter is { } f ? f.Clean ? $"clean at {f.DelayMedianMs:0.0} ms" : $"{f.Glitches} break-ups" : "not heard")} "
        + $"(it was at {HeardBefore?.DelayMedianMs:0.0} ms) ({WallSeconds:0.0} s)";
}

/// <summary>
/// THE STRESS RUNS (2026-09-28). Each starts one peer on a call (the tone and its clicks, listened to), measures the process
/// and every table, stresses the receiver, lets it return to normal, and measures again: the heap (less the rig's own pool),
/// handles, threads and every table must be back where they were, and the call must sound as it did.
/// </summary>
internal static class PerfStress
{
    /// <summary>The longest a call may take to play clean again after a stress, and stay so (see <see cref="TimeToClean"/>).
    /// Measured 2026-09-28: at once after most; after a short output stall on an ASIO output, 49 to 64 s, until that was
    /// fixed the same evening (see CardStalls).</summary>
    public const double RecoveryLimit = 120;

    /// <summary>After a stress, long enough for every departed stream to be pruned (four seconds idle, and the next
    /// once-a-second prune) and the call's buffer to settle.</summary>
    public const double RecoverSeconds = 8;

    /// <summary>Back to normal means: the heap within this of before (less the rig's own pool; a table that grew for a while
    /// keeps its larger bucket array, a few kilobytes), handles and threads within these. Windows starts and retires its own
    /// thread-pool workers in the process as it likes - measured 2026-09-28 in the gate's process: one such thread, with five
    /// or six handles, appearing in the middle of a stress run now and then, with no start address and no CPU. A thread or a
    /// handle kept per peer or per session would be fifteen here and two thousand in the churn.</summary>
    public const long HeapSlackBytes = 512 * 1024;
    public const int HandleSlack = 8;
    public const int ThreadSlack = 2;

    /// <summary>Tables that are figures, not collections, and move with the audio itself.</summary>
    private static readonly HashSet<string> Moving = new(StringComparer.Ordinal) { "engine: ring bytes holding audio" };

    /// <param name="sameDelay">The call must come back at the delay it had. Not after a stall that leaves a backlog in the
    /// buffer (the network holding packets, the output not reading): that backlog is drained at the house pace, a few ms a
    /// second and never faster (Ed's rule; SIMULATOR STALL RECOVERY listens to the walk back), so the delay is reported.</param>
    /// <param name="asioFrames">The ASIO cards' buffer (32 frames unless a run has a reason: the churn's forty sessions
    /// waiting to be pruned are each read on every callback, and what it is about is sessions, not the card).</param>
    private static StressOutcome Around(string scenario, SimConfig config, SimCodec codec, Action<PerfRig, StressOutcome>? warmUp,
        Action<PerfRig, StressOutcome> stress, bool sameDelay = true, int asioFrames = 32)
    {
        return PerfBench.OnOwnThread(() =>
        {
            var wall = Stopwatch.StartNew();
            var o = new StressOutcome(scenario, config) { Codec = codec };
            var rigOptions = new PerfRigOptions(config, codec) { Seed = 5, AsioFrames = asioFrames };
            using var rig = new PerfRig(rigOptions);
            rig.Join(0, PerfSignal.ToneAndClicks);
            rig.RunFor(6);
            // A short round first, so what a first time costs (a table's first growth, code compiled) is not taken for a leak.
            if (warmUp is not null)
            {
                warmUp(rig, o);
                rig.RunFor(RecoverSeconds);
                o.Figures.Clear();
                o.Problems.Clear();
            }
            o.HeardBefore = Listen(rig, 5);
            o.TablesBefore = Tables(rig);
            o.RigBefore = rig.RetainedBytes;
            o.Before = ProcessState.Sample();
            var threadsBefore = Diagnose is null ? null : ThreadList();
            Diagnose?.Invoke($"  handles before the stress {ThreadCpu.HandleCount()}");
            stress(rig, o);
            Diagnose?.Invoke($"  handles after the stress {ThreadCpu.HandleCount()}");
            // Back to normal: the call clean again and staying so, and long enough for every departed stream to be pruned.
            var stressEnded = rig.Now;
            var back = TimeToClean(rig, RecoveryLimit);
            o.Figures.Add(back is { } seconds ? $"the call clean again {seconds:0} s after the stress" : $"the call NOT clean again within {RecoveryLimit:0} s");
            if (back is null) o.Problems.Add($"the call must play clean again within {RecoveryLimit:0} s of the stress ending");
            Diagnose?.Invoke($"  handles after waiting for clean {ThreadCpu.HandleCount()}");
            rig.RunUntil(Math.Max(rig.Now, stressEnded + RecoverSeconds));
            o.HeardAfter = Listen(rig, 5);
            Diagnose?.Invoke($"  handles after listening {ThreadCpu.HandleCount()}");
            o.TablesAfter = Tables(rig);
            o.RigAfter = rig.RetainedBytes;
            o.After = ProcessState.Settled(o.Before);
            if (Diagnose is { } say && threadsBefore is not null)
                foreach (var (id, what) in ThreadList().Where(t => !threadsBefore.ContainsKey(t.Key)))
                    say($"  a thread that was not there before: {id} {what}");
            Compare(o, sameDelay);
            o.WallSeconds = wall.Elapsed.TotalSeconds;
            return o;
        });
    }

    /// <summary>A hearing in words: break-ups, clicks, the tone's worst stretch against the noise, the pitch, the delay.</summary>
    public static string Said(SimAnalysis.Findings f) =>
        $"{f.Glitches} break-ups ({f.QuietMs} quiet ms, {f.StepClicks} clicks{(f.FirstGlitchAt is { } at ? $", first at {at}" : "")}), worst tone-to-noise {f.WorstSnrDb:0} dB, "
        + $"pitch within {f.WorstPitchCents:0.0} cents, {f.ClicksHeard} timing clicks heard, delay {f.DelayMedianMs:0.0} ms";

    /// <summary>How long a call must stay clean to count as clean again: two of the drift loop's ten-second windows. A
    /// disturbance is often only taken in by the loop when its window closes, seconds later, and the trouble starts then
    /// (found 2026-09-28: "clean again at once" after an output hiccup, and breaking up from nine seconds on).</summary>
    public const double StaysCleanSeconds = 20;

    /// <summary>In tune, for a call coming back: the depth correction may bend the pitch by its 0.3 % (5.2 cents, the house
    /// pace) while it walks a buffer back; more is the drift loop mistaking a disturbance for a clock.</summary>
    public const double InTuneCents = 6.5;

    /// <summary>How long after now until the call plays clean again and stays so for <see cref="StaysCleanSeconds"/> -
    /// listened to a second at a time (in tune, the tone well above the noise, not a break-up or a click) - for up to
    /// <paramref name="limit"/> seconds (null if it never does).</summary>
    public static double? TimeToClean(PerfRig rig, double limit)
    {
        var start = rig.Now;
        double? cleanSince = null;
        while (rig.Now - start < limit + StaysCleanSeconds)
        {
            var at = rig.Now;
            var f = Listen(rig, 1);
            var clean = f.Glitches == 0 && f.StepClicks == 0 && f.ClicksHeard >= 1 && f.WorstSnrDb > 15 && f.WorstPitchCents <= InTuneCents;
            if (!clean) cleanSince = null;
            else cleanSince ??= at;
            if (cleanSince is { } since && rig.Now - since >= StaysCleanSeconds) return since - start;
        }
        return null;
    }

    /// <summary>Investigation (REMSOUND_PERF_DIAGNOSE=1): each stress run prints the process's handles as it goes, and names
    /// any thread that appeared across it - how the Windows thread-pool worker behind <see cref="HandleSlack"/> was found.</summary>
    internal static Action<string>? Diagnose { get; set; } = Environment.GetEnvironmentVariable("REMSOUND_PERF_DIAGNOSE") == "1" ? Console.WriteLine : null;

    private static Dictionary<int, string> ThreadList()
    {
        using var p = Process.GetCurrentProcess();
        var list = new Dictionary<int, string>();
        foreach (ProcessThread t in p.Threads)
        {
            try { list[t.Id] = $"start 0x{t.StartAddress.ToInt64():X} state {t.ThreadState} {(t.ThreadState == System.Diagnostics.ThreadState.Wait ? t.WaitReason.ToString() : "")} cpu {t.TotalProcessorTime.TotalMilliseconds:0} ms"; }
            catch { list[t.Id] = "(gone)"; }
        }
        return list;
    }

    public static SimAnalysis.Findings Listen(PerfRig rig, double seconds)
    {
        rig.StartListening(seconds + 1);
        rig.RunFor(seconds);
        return rig.StopListening();
    }

    public static IReadOnlyList<(string Table, long Count)> Tables(PerfRig rig)
    {
        var list = new List<(string Table, long Count)>();
        if (rig.Receiver is { } receiver) list.AddRange(receiver.TablesForTest());
        list.AddRange(rig.Senders.SelectMany(s => s.TablesForTest()).GroupBy(t => t.Table).Select(g => (g.Key, g.Sum(t => t.Count))));
        list.Add(("Jamulus-style peers remembered (machine-wide)", CustomOpusPeers.EntriesForTest));
        return list;
    }

    private static void Compare(StressOutcome o, bool sameDelay)
    {
        var before = o.TablesBefore.ToDictionary(t => t.Table, t => t.Count);
        var after = o.TablesAfter.ToDictionary(t => t.Table, t => t.Count);
        var changed = before.Keys.Union(after.Keys).Where(k => !Moving.Contains(k) && before.GetValueOrDefault(k) != after.GetValueOrDefault(k))
            .Select(k => $"{k} {before.GetValueOrDefault(k)} before, {after.GetValueOrDefault(k)} after").ToList();
        if (changed.Count > 0) o.Problems.Add($"tables not back where they were: {string.Join("; ", changed)}");
        if (o.HeapChange > HeapSlackBytes)
            o.Problems.Add($"the managed heap did not come back: {o.HeapChange / 1024.0:+0} KB after a full collection (the rig's own pool taken off; bar {HeapSlackBytes / 1024} KB)");
        if (o.After.Handles - o.Before.Handles > HandleSlack) o.Problems.Add($"handles did not come back: {o.Before.Handles} before, {o.After.Handles} after");
        if (o.After.Threads - o.Before.Threads > ThreadSlack) o.Problems.Add($"threads did not come back: {o.Before.Threads} before, {o.After.Threads} after");
        if (o.HeardAfter is not { } f || !f.Clean || f.ClicksHeard < 8)
            o.Problems.Add($"the call did not come back clean: {(o.HeardAfter is { } g ? Said(g) : "nothing heard")}");
        else if (sameDelay && o.HeardBefore is { ClicksHeard: > 0 } b && Math.Abs(f.DelayMedianMs - b.DelayMedianMs) > DelayWander(o.Config, o.Codec))
            o.Problems.Add($"the call came back at a different delay: {b.DelayMedianMs:0.0} ms before, {f.DelayMedianMs:0.0} ms after (bar {DelayWander(o.Config, o.Codec):0} ms)");
    }

    /// <summary>How far the call's delay may differ, before and after a stress that leaves no backlog: what it wanders on its
    /// own, and a millisecond and a half more. Measured 2026-09-28 (RemSound.exe --simulate wander-probe: the call alone for
    /// ten minutes, heard five seconds at a time every ten): the WASAPI-only output 12 to 19 ms from lowest to highest
    /// (SIMULATOR OVERNIGHT's "several ms either way every few minutes"); the ASIO output 2.5 ms on 2.5 ms Opus, 4.6 on PCM,
    /// 11.4 on 10 ms Opus. A stress that left the call's buffer deeper than that would be heard here.</summary>
    public static double DelayWander(SimConfig config, SimCodec codec) => config == SimConfig.WasapiOnly ? 20 : codec switch
    {
        SimCodec.Opus2_5 => 4,
        SimCodec.Pcm => 6,
        _ => 13,
    };

    private static double CpuPercent(PerfRig rig, Action run)
    {
        var t0 = rig.Now;
        var c0 = ThreadCpu.Cycles();
        run();
        return ThreadCpu.ToSeconds(ThreadCpu.Cycles() - c0) / Math.Max(1e-9, rig.Now - t0) * 100;
    }

    // =====================================================================================================================
    // 1. Many senders at once
    // =====================================================================================================================

    /// <summary>Fifteen more peers (10 ms Opus, music-like) join the call at once for six seconds - sixteen people, 32
    /// streams with both kinds of output, the receiver's own ceiling - then leave, their programs closing.</summary>
    public const int ManyPeers = 15;

    public static StressOutcome ManySenders(SimConfig config) => Around("many senders", config, SimCodec.Pcm,
        (rig, o) => Crowd(rig, o, 2), (rig, o) => Crowd(rig, o, 6));

    private static void Crowd(PerfRig rig, StressOutcome o, double seconds)
    {
        var slots = Enumerable.Range(1, ManyPeers).Select(i => rig.Join(i, PerfSignal.Busy, SimCodec.Opus10)).ToList();
        var peak = 0;
        var cpu = CpuPercent(rig, () =>
        {
            for (var t = 0; t < seconds; t++)
            {
                rig.RunFor(1);
                peak = Math.Max(peak, rig.Receiver!.LiveSessionCountForTest);
            }
        });
        var state = ProcessState.Sample();
        foreach (var slot in slots) rig.Leave(slot, forget: true);
        o.Figures.Add($"{ManyPeers + 1} peers sending at once: {peak} sessions at most, {cpu:0.0} % of a core for all their senders and the receiver, "
            + $"heap {state.HeapMb:0.0} MB and private {state.PrivateMb:0} MB while they were in");
    }

    // =====================================================================================================================
    // 2. Peers joining and leaving thousands of times
    // =====================================================================================================================

    public const int ChurnJoins = 1000;
    public const double ChurnEverySeconds = 0.1;
    public const double ChurnStaySeconds = 0.2;
    public const int ChurnIdentities = 8;

    /// <summary>A thousand comings and goings in each configuration: every tenth of a second one of eight peers (10 ms Opus)
    /// starts sending from a new port with a new stream, as a program restarting does, and stops a fifth of a second later -
    /// each stream a session opened and, four seconds after, pruned; about forty at a time waiting to be, over the receiver's
    /// ceiling of 32.</summary>
    public static StressOutcome Churn(SimConfig config) => Around("session churn", config, SimCodec.Pcm,
        (rig, o) => ChurnRound(rig, o, 100), (rig, o) => ChurnRound(rig, o, ChurnJoins), asioFrames: 256);

    private static void ChurnRound(PerfRig rig, StressOutcome o, int joins)
    {
        var receiver = rig.Receiver!;
        var opened0 = receiver.SessionsOpenedCount;
        var leaving = new Queue<(double At, int Slot)>();
        var peak = 0;
        var start = rig.Now;
        var wall = Stopwatch.StartNew();
        var cpu = CpuPercent(rig, () =>
        {
            for (var i = 0; i < joins; i++)
            {
                rig.RunUntil(start + i * ChurnEverySeconds);
                while (leaving.Count > 0 && leaving.Peek().At <= rig.Now) rig.Leave(leaving.Dequeue().Slot);
                leaving.Enqueue((rig.Now + ChurnStaySeconds, rig.Join(1 + i % ChurnIdentities, PerfSignal.Busy, SimCodec.Opus10)));
                if (i % 10 == 0) peak = Math.Max(peak, receiver.LiveSessionCountForTest);
            }
            while (leaving.Count > 0)
            {
                var (at, slot) = leaving.Dequeue();
                rig.RunUntil(at);
                rig.Leave(slot, forget: true);
            }
        });
        // Every churning peer's program closes.
        foreach (var identity in Enumerable.Range(1, ChurnIdentities))
            if (rig.Join(identity, PerfSignal.Silence) is var slot) rig.Leave(slot, forget: true);
        var opened = receiver.SessionsOpenedCount - opened0;
        o.Figures.Add($"{joins} joins and leaves in {rig.Now - start:0} simulated s ({wall.Elapsed.TotalSeconds:0.0} s): {opened} sessions opened and pruned, "
            + $"{peak} live at most (ceiling {RemSound.Receiver.AudioReceiver.MaxLiveSessions}), {cpu:0.00} % of a core");
        var streams = rig.Options.Config == SimConfig.Both ? 2 : 1;
        if (opened < joins * streams)
            o.Problems.Add($"premise: every join must open its stream(s) ({opened} sessions opened for {joins} joins of {streams} stream(s))");
    }

    // =====================================================================================================================
    // 3. The network flapping
    // =====================================================================================================================

    /// <summary>The call's network drops for 2 s, for 6 s (longer than the four seconds after which a silent stream is
    /// pruned), stalls for 1.5 s and delivers the lot at once, and turns rough (2 % lost, doubled and overtaken packets) for
    /// 10 s, between spells of normal.</summary>
    public static StressOutcome NetworkFlaps(SimConfig config) => Around("network flaps", config, SimCodec.Opus2_5,
        (rig, o) => { rig.NetworkDown = true; rig.RunFor(1); rig.NetworkDown = false; }, Flaps, sameDelay: false);

    private static void Flaps(PerfRig rig, StressOutcome o)
    {
        var receiver = rig.Receiver!;
        var opened0 = receiver.SessionsOpenedCount;
        rig.NetworkDown = true;
        rig.RunFor(2);
        rig.NetworkDown = false;
        rig.RunFor(5);
        var shortOutage = receiver.SessionsOpenedCount - opened0;
        rig.NetworkDown = true;
        rig.RunFor(6);
        var sessionsInLongOutage = receiver.LiveSessionCountForTest;
        rig.NetworkDown = false;
        rig.RunFor(5);
        var longOutage = receiver.SessionsOpenedCount - opened0 - shortOutage;
        rig.HoldNetwork(1.5);
        rig.RunFor(6.5);
        rig.Network = SimNetwork.Rough;
        rig.RunFor(10);
        rig.Network = SimNetwork.Wired;
        o.Figures.Add($"a 2 s drop reopened {shortOutage} session(s); a 6 s drop left {sessionsInLongOutage} session(s) open at its end and reopened {longOutage} "
            + $"when the network came back; then a 1.5 s stall and 10 s of a rough network ({rig.Lost} packets lost in all)");
        var streams = rig.Options.Config == SimConfig.Both ? 2 : 1;
        if (shortOutage != 0) o.Problems.Add($"a 2 s drop must not cost the call its session ({shortOutage} reopened)");
        if (sessionsInLongOutage != 0 || longOutage != streams)
            o.Problems.Add($"a 6 s drop must see the silent stream pruned and opened again when it returns ({sessionsInLongOutage} left open, {longOutage} reopened)");
    }

    // =====================================================================================================================
    // 4. Floods
    // =====================================================================================================================

    /// <summary>While the call plays, listened to throughout: 6,000 packets of junk from strangers; 6,000 of junk from the call's
    /// own address (random bytes, and every packet type with a garbage payload); a peer with the wrong password sending for
    /// 5 s; the call's own packets every one doubled for 5 s, then one in five overtaken for 5 s; and a burst of format packets
    /// from 64 ports of one address. None but the overtaken packets may be heard at all.
    ///
    /// <para>FINDING, 2026-09-28, reported: one packet in five overtaken by 3 ms (on 2.5 ms Opus; two in a row often enough to
    /// beat the reorder wait) is heard on the WASAPI-only output - measured 7 to 14 break-ups, 30 to 55 ms of silence, in
    /// five seconds - and afterwards the drift loop reads the losses as a clock (-4,236 ppm) and bends the pitch about 9.5
    /// cents while the buffer refills, for ten seconds or so. ASIO only and both played it clean. It is far past any real
    /// network (the rough internet preset overtakes one in two hundred).</para></summary>
    public const int JunkPerSecond = 2000;
    public const int FormatBurst = 64;

    public static StressOutcome Floods(SimConfig config) => Around("floods", config, SimCodec.Opus2_5,
        (rig, o) => Flood(rig, o, 0.3), (rig, o) => Flood(rig, o, 3));

    private static void Flood(PerfRig rig, StressOutcome o, double seconds)
    {
        var receiver = rig.Receiver!;
        var rng = new Random(77);
        var callAddress = PerfRig.IdentityAddress(0);
        var buffer = new byte[1600];

        // Junk: from strangers, and from the call's own address.
        foreach (var (who, fromCall) in new[] { ("strangers", false), ("the call's own address", true) })
        {
            rig.Meter.Reset();
            rig.StartListening(seconds + 1);
            var count = 0;
            for (var tick = 0; tick < seconds * 100; tick++)
            {
                rig.RunFor(0.01);
                for (var k = 0; k < JunkPerSecond / 100; k++)
                {
                    var from = fromCall ? new IPEndPoint(callAddress, 40000 + rng.Next(20000))
                        : new IPEndPoint(new IPAddress([10, 99, (byte)rng.Next(256), (byte)(1 + rng.Next(250))]), 1024 + rng.Next(60000));
                    var length = Junk(buffer, rng, fromCall && k % 2 == 1);
                    rig.Inject(buffer.AsSpan(0, length), from);
                    count++;
                }
            }
            var heard = rig.StopListening();
            var perPacketNs = rig.Meter.Seconds(PerfCost.Flood) / Math.Max(1, count) * 1e9;
            var perPacketBytes = rig.Meter.Allocated(PerfCost.Flood) / (double)Math.Max(1, count);
            o.Figures.Add($"{count} junk packets from {who} ({JunkPerSecond} a second): {perPacketNs:0} ns and {perPacketBytes:0} bytes allocated each, "
                + $"{(heard.Clean ? "the call clean throughout" : $"{heard.Glitches} break-ups")}");
            // Junk never reaches the call's stream, so the call must not hear it.
            if (!heard.Clean) o.Problems.Add($"junk from {who} must not be heard ({Said(heard)})");
        }

        // The wrong password: a peer at an address that is let in, whose key is not ours.
        rig.StartListening(seconds + 3);
        var stranger = rig.Join(2, PerfSignal.Busy, SimCodec.Opus2_5, password: "not the simulator's password");
        rig.RunFor(seconds + 2);
        var wrongKey = rig.StopListening();
        var status = receiver.GetPeerSecurityStatuses().FirstOrDefault(kv => kv.Key.Equals(PerfRig.IdentityAddress(2))).Value;
        rig.Leave(stranger, forget: true);
        o.Figures.Add($"a peer with the wrong password for {seconds + 2:0.#} s: seen as {status}, "
            + $"{(wrongKey.Clean ? "the call clean throughout" : $"{wrongKey.Glitches} break-ups")}");
        if (!wrongKey.Clean) o.Problems.Add($"a peer with the wrong password must not be heard ({Said(wrongKey)})");
        if (status != PeerSecurityStatus.PasswordMismatch) o.Problems.Add($"premise: the wrong password must be seen as one ({status})");

        // The call's own packets doubled - every one - and then overtaken, one in five, by 3 ms. Doubled copies are dropped
        // and never heard. Overtaken ones are what the reorder wait is for; one in five, and two in a row often enough to beat
        // it, is far past any real network, and what it does is reported (and the call must keep playing, and come back).
        foreach (var (what, network) in new[]
        {
            ("every packet doubled", SimNetwork.Wired with { Name = "doubled", DuplicatePercent = 100 }),
            ("one packet in five overtaken", SimNetwork.Wired with { Name = "overtaken", ReorderPercent = 20 }),
        })
        {
            rig.Network = network;
            rig.StartListening(seconds + 3);
            var late0 = receiver.LateDroppedForTest;
            var (fec0, unrecovered0, _) = receiver.CodecRepairsForTest;
            rig.RunFor(seconds + 2);
            var heard = rig.StopListening();
            var (fec, unrecovered, _) = receiver.CodecRepairsForTest;
            rig.Network = SimNetwork.Wired;
            var back = TimeToClean(rig, RecoveryLimit);
            o.Figures.Add($"{what} for {seconds + 2:0.#} s: {receiver.LateDroppedForTest - late0} dropped as late or doubled, {fec - fec0} repaired, "
                + $"{unrecovered - unrecovered0} gaps not repairable; {(heard.Clean ? "the call clean throughout" : Said(heard))}; "
                + $"clean again {(back is { } b ? $"{b:0} s" : "NEVER")} after");
            if (heard.ClicksHeard < 2 || heard.QuietMs > 500) o.Problems.Add($"the call must keep playing through {what} ({Said(heard)})");
            if (network.ReorderPercent == 0 && !heard.Clean) o.Problems.Add($"{what}: the copies must be dropped unheard ({Said(heard)})");
            if (back is null) o.Problems.Add($"after {what} the call must play clean again within {RecoveryLimit:0} s");
        }

        // A burst of format packets from many ports of one address that is let in: each opens a session, with its ring.
        var before = receiver.TablesForTest().ToDictionary(t => t.Table, t => t.Count);
        var format = new AudioFormatInfo(48000, 2, 16, 1, 4, 192_000, (int)AudioTransportCodec.Opus, 120, RenderRoute.Mixed, 0);
        var (_, fingerprint) = RemSoundCrypto.ForPlainPassword("simulator");
        var burst = seconds < 1 ? 4 : FormatBurst;
        for (var i = 0; i < burst; i++)
        {
            var packet = new byte[RemPacket.HeaderSize + RemPacket.FormatPayloadWithCaptureSize];
            RemPacket.WriteHeader(packet, RemPacketType.Format, (ushort)(1000 + i), 1);
            var payload = RemPacket.WriteFormatPayload(packet.AsSpan(RemPacket.HeaderSize), format, fingerprint);
            rig.Inject(packet.AsSpan(0, RemPacket.HeaderSize + payload), new IPEndPoint(PerfRig.IdentityAddress(3), 30000 + i));
        }
        var tables = receiver.TablesForTest().ToDictionary(t => t.Table, t => t.Count);
        var peakState = ProcessState.Sample();
        rig.RunFor(1.2);
        var afterPrune = receiver.LiveSessionCountForTest;
        o.Figures.Add($"{burst} format packets from {burst} ports of one address at once: {tables["receiver: stream sessions"] - before["receiver: stream sessions"]} "
            + $"sessions opened before the next once-a-second prune, {(tables["engine: ring bytes owned"] - before["engine: ring bytes owned"]) / 1048576.0:0} MB of rings "
            + $"(private bytes {peakState.PrivateMb:0} MB); {afterPrune} session(s) left after that prune");
    }

    /// <summary>A junk packet into <paramref name="buffer"/>: random bytes of a random length, or (<paramref name="shaped"/>)
    /// a real header of any type with a garbage payload - the ones that get past the first check.</summary>
    private static int Junk(byte[] buffer, Random rng, bool shaped)
    {
        var length = 1 + rng.Next(1400);
        rng.NextBytes(buffer.AsSpan(0, length));
        if (!shaped) return length;
        length = Math.Max(length, RemPacket.HeaderSize + 1);
        var types = Enum.GetValues<RemPacketType>();
        RemPacket.WriteHeader(buffer, types[rng.Next(types.Length)], (ushort)rng.Next(65536), (uint)rng.Next());
        return length;
    }

    // =====================================================================================================================
    // 5. Sound cards stalling
    // =====================================================================================================================

    /// <summary>The output device hiccups for a tenth of a second, and stops for 3 s and comes back; its driver thread is
    /// 0.5 s late and fires every missed callback at once; and the sending end's capture stops for 2 s. Each is waited out
    /// until the call is clean again, and how long that took is the figure.
    ///
    /// <para>FINDING, 2026-09-28, reported and not asserted beyond "clean again within two minutes": on an ASIO output
    /// (ASIO only, and both) a stall of 20 to 450 ms is read by the playout's drift loop as a clock difference of the
    /// stall's share of its ten-second window (0.1 s: +10,000 ppm, one per cent), which is inside the loop's 5 % sanity band
    /// and so believed: the output then plays about one per cent fast, runs its buffer dry some 90 times a second and breaks
    /// up for most of a minute while the reading decays window by window (measured in the minute after: 127 underruns for a
    /// 20 ms stall, 1,353 for 50 ms, 2,715 for 100 ms, 6,598 for 450 ms on ASIO only; a third to half as many again with
    /// both kinds of output). The WASAPI-only output had none. The same family as SIMULATOR STALL RECOVERY's finding for a
    /// network stall. FIXED the same evening (Ed approved): a window whose output reads stopped is not a clock reading
    /// (SessionPlayout.DriftReadStallMinSec, and the WASAPI output stage's PullStallMinSec); measured after it, the 0.1 s
    /// hiccup is clean again at once on ASIO only and both, and SIMULATOR OUTPUT HICCUP asserts it. STILL A FINDING: the
    /// driver 0.5 s late that fires its missed callbacks in a burst gives about 725 underruns on ASIO only and 1,450 with
    /// both, all within a second of it - the reads catch up, so no stall shows in them; left as it is.</para></summary>
    public static StressOutcome CardStalls(SimConfig config) => Around("card stalls", config, SimCodec.Opus2_5, null, Stalls, sameDelay: false);

    private static void Stalls(PerfRig rig, StressOutcome o)
    {
        var receiver = rig.Receiver!;
        foreach (var (what, stall) in new (string, Action)[]
        {
            ("the output hiccuping for 0.1 s", () => { rig.StallOutputs(0.1, catchUp: false); rig.RunFor(0.1); }),
            ("the output stopping for 3 s", () => { rig.StallOutputs(3, catchUp: false); rig.RunFor(3); }),
            ("its driver 0.5 s late", () => { rig.StallOutputs(0.5, catchUp: true); rig.RunFor(0.5); }),
            ("the capture stopping for 2 s", () => { rig.StallSenders(2); rig.RunFor(2); }),
        })
        {
            var u0 = receiver.TuneBlockingUnderruns;
            var t0 = receiver.TrimFireCount;
            stall();
            var back = TimeToClean(rig, RecoveryLimit);
            o.Figures.Add($"{what}: clean again {(back is { } s ? $"{s:0} s after" : $"NOT within {RecoveryLimit:0} s")} ({receiver.TuneBlockingUnderruns - u0} underruns, "
                + $"{receiver.TrimFireCount - t0} click-trims on the way)");
            if (back is null) o.Problems.Add($"after {what} the call must play clean again within {RecoveryLimit:0} s ({receiver.TuneBlockingUnderruns - u0} underruns)");
        }
    }
}
