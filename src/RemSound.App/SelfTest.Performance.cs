using System.Diagnostics;
using RemSound.App.Simulator;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// THE PERFORMANCE, SOAK AND STRESS SUITE (2026-09-28). Ed: "this is supposed to be a very light weight program that runs for
/// days sometimes weeks at a time with very little load on the computer. using our simulator we should be able to perform
/// some perf, soak and stress tests etc and to benchmark but also make sure nothing untoward is going on."
///
/// <para>Everything here runs the REAL AudioSender and AudioReceiver in the performance rig (Simulator\PerfRig.cs): fake
/// sound cards and a fake network, in virtual time, on a thread of the step's own, with nothing opened - no device, no
/// socket to anywhere, no window. Every step runs WASAPI only, ASIO only and both.</para>
///
/// <list type="bullet">
/// <item>CPU: what the audio path costs a second of sound, per configuration and way of sending, one peer and four and
/// eight into one receiver, the sender alone and the receiver alone - against a budget for "very little load" and this
/// machine's baseline (tests\perf-baseline.json).</item>
/// <item>ALLOCATIONS: nothing per audio packet or output read once a stream runs.</item>
/// <item>SOAK: a simulated day, the process and every table watched, the counters carried past their wrap.</item>
/// <item>STRESS: many senders, thousands of joins and leaves, a flapping network, floods, stalling sound cards - and back to
/// normal after each.</item>
/// </list>
/// The long runs are <c>RemSound.exe --simulate soak [hours]</c> and <c>--simulate perf</c> (the whole suite, with a report).
/// </summary>
internal static partial class SelfTest
{
    private static SimConfig SimConfigOf(AudioConfiguration configuration) => configuration switch
    {
        AudioConfiguration.WasapiOnly => SimConfig.WasapiOnly,
        AudioConfiguration.AsioOnly => SimConfig.AsioOnly,
        _ => SimConfig.Both,
    };

    /// <summary>PERFORMANCE CPU. Every benchmark (PerfSuite.CpuCases) in each configuration: the simulation thread's own CPU per
    /// simulated second, the rig's own share taken off, as percent of one core at real time. Each must measure a healthy
    /// stream, stay inside its "very little load" budget, and - where this machine and build have a baseline - stay within
    /// 25 % (plus 0.05 of a percent) of it, three tries running. See PerfBaseline for how the tolerance was chosen.</summary>
    private static string PerformanceCpu()
    {
        var wall = Stopwatch.StartNew();
        var root = FindSourceRoot();
        var machine = ThreadCpu.MachineKey();
        var build = ThreadCpu.BuildKind();
        var baseline = PerfBaseline.Load(PerfBaseline.PathIn(root), machine, build, out var said);
        PerfBench.WarmUp();
        var all = new List<PerfResult>();
        var compared = new List<string>();
        foreach (var configuration in AudioConfigurations.All)
        {
            var config = SimConfigOf(configuration);
            var cases = PerfSuite.CpuCases(config);
            var results = cases.Select(PerfBench.Run).ToList();
            Check(results.Count == cases.Count && results.All(r => r.RawPercent > 0 && r.Windows.Length == PerfBench.Windows),
                $"{PerfBench.Name(config)}: premise - every benchmark must run and measure something");
            var verdict = PerfSuite.Judge(results, baseline, said, PerfBench.Run);
            Check(verdict.Failures.Count == 0, $"{PerfBench.Name(config)}: {string.Join(" | ", verdict.Failures)}");
            all.AddRange(results);
            compared.Add($"{PerfBench.Name(config)} {string.Join(" ", verdict.Lines)}");
        }
        return $"{machine}, {build} build, the benchmark thread {ThreadCpu.LastHold}. {PerfSuite.Summarise(all)}. Budgets: a two-way call "
             + $"{PerfSuite.TwoWayBudget:0} %, the sender alone {PerfSuite.SenderBudget:0} %, one receiver playing eight peers "
             + $"{PerfSuite.EightPeerReceiverBudget:0} % of one core - all within. Against the baseline: {string.Join("; ", compared)}. "
             + $"{all.Count} benchmarks in {wall.Elapsed.TotalSeconds:0} s";
    }

    /// <summary>PERFORMANCE ALLOCATIONS. Eight simulated seconds of steady streaming (after twelve of warm-up) in each
    /// configuration and way of sending (one peer; and four on 2.5 ms Opus, for the per-stream figure), every call into RemSound
    /// counted: audio packets and output reads must allocate nothing (no more than one in a hundred, a scratch array growing
    /// once); the sender only its format re-announces; and a format packet on the receiver only what RemPacket.TryReadFormat
    /// allocates, measured on its own, and AudioReceiver.HandleFormat's closure. See PerfAlloc for the findings.</summary>
    private static string PerformanceAllocations()
    {
        var wall = Stopwatch.StartNew();
        var (readFormat, formatRecord) = PerfAlloc.Sites();
        var said = new List<string>();
        foreach (var configuration in AudioConfigurations.All)
        {
            var config = SimConfigOf(configuration);
            foreach (var codec in PerfSuite.Codecs)
            {
                // One peer in every way of sending; four (the per-stream figure) in the way most people send.
                foreach (var peers in codec == SimCodec.Opus2_5 ? new[] { 1, 4 } : new[] { 1 })
                {
                    var r = PerfAlloc.Measure(config, codec, peers);
                    var what = r.Plain;
                    Check(r.AudioPackets > 0 && r.RenderCalls > 0 && r.SendCalls > 0 && r.FormatPackets > 0, $"premise - {what}: the stream must run");
                    Check(r.AllocatingAudioPackets <= r.AudioPackets * PerfAlloc.AllocatingCallsAtMost,
                        $"{what}: an audio packet must allocate nothing ({r.AllocatingAudioPackets} of {r.AudioPackets} packets allocated, {r.AudioBytes} bytes in all)");
                    Check(r.AllocatingReads <= r.Reads * PerfAlloc.AllocatingCallsAtMost,
                        $"{what}: an output read must allocate nothing ({r.AllocatingReads} of {r.Reads} reads allocated: {r.RenderBytes} bytes over {r.RenderCalls} card reads, "
                        + $"{r.ProducerBytes} over {r.ProducerCalls} reads by the WASAPI stage's producer)");
                    Check(r.SendPerLaneSecond <= PerfAlloc.SenderBytesPerLaneSecond,
                        $"{what}: the sender may allocate only its format re-announces, {PerfAlloc.SenderBytesPerLaneSecond:0} bytes a lane a second "
                        + $"({r.SendPerLaneSecond:0}; {r.SendBytes} bytes over {r.SendCalls} capture blocks)");
                    Check(r.FormatPerPacket <= PerfAlloc.FormatPacketBytes && r.FormatPerPacket >= readFormat,
                        $"{what}: a format packet may allocate what RemPacket.TryReadFormat does ({readFormat} bytes measured on its own) and "
                        + $"AudioReceiver.HandleFormat's closure, and no more: {r.FormatPerPacket:0} bytes each (bar {PerfAlloc.FormatPacketBytes:0})");
                    Check(r.EverySecondPerStreamSecond <= PerfAlloc.EverySecondBytesPerStreamSecond,
                        $"{what}: the once-a-second work may allocate {PerfAlloc.EverySecondBytesPerStreamSecond:0} bytes a stream a second ({r.EverySecondPerStreamSecond:0})");
                    if (peers == 1) said.Add($"{PerfBench.Name(config)} {SimLab.Name(codec)}: sender {r.SendPerLaneSecond:0} B a lane a second, format packets {r.FormatPerPacket:0} B, "
                        + $"once-a-second work {r.EverySecondPerStreamSecond:0} B a stream a second, {r.Gen0Collections} collections");
                }
            }
        }
        return $"audio packets and output reads: nothing, every configuration and way of sending (one peer, and four on 2.5 ms Opus). The rest: {string.Join("; ", said)}. "
             + $"The sites: RemPacket.TryReadFormat {readFormat} bytes a format packet (two AudioFormatInfo records and the fingerprint's copy) "
             + "and AudioReceiver.HandleFormat's closure the rest (see PerfAlloc); "
             + $"the sender's AudioFormatInfo {formatRecord} bytes a re-announce, four a second a lane ({wall.Elapsed.TotalSeconds:0} s)";
    }

    /// <summary>PERFORMANCE SOAK. A simulated day in each configuration, side by side (SimSoak and PerfVerdicts.Soak): a
    /// two-hour call that carries its stream past the ring positions' 2^31-byte wrap and its wire counters past 2^32, a
    /// visitor every half hour all day, and an evening call listened to. The heap after a full collection, the handles and
    /// the threads must not climb through the idle hours, every table must hold its size, the call must be clean, its cost
    /// must not creep, and the evening call must sound as the morning's did. Longer: RemSound.exe --simulate soak 72.</summary>
    private static string PerformanceSoak()
    {
        var result = SimSoak.Run(new SoakOptions(24));
        var verdict = PerfVerdicts.Soak(result);
        foreach (var configuration in AudioConfigurations.All)
        {
            var config = SimConfigOf(configuration);
            // Each failure names its configuration already.
            var failures = verdict.FailuresFor(config);
            Check(failures.Count == 0, string.Join(" | ", failures));
            Check(result.Heard.ContainsKey(config) && result.CallCounters.ContainsKey(config), $"{PerfBench.Name(config)}: premise - the day must have run to the end");
        }
        Check(verdict.FailuresFor(null).Count == 0, string.Join(" | ", verdict.FailuresFor(null)));
        return string.Join(" ", verdict.Lines);
    }

    /// <summary>One stress scenario in each configuration (PerfStress): every problem it found fails the step, naming the
    /// configuration; what it measured is the step's line.</summary>
    private static string PerformanceStress(Func<SimConfig, StressOutcome> scenario)
    {
        SettleTheGatesOwnThreads();
        var said = new List<string>();
        foreach (var configuration in AudioConfigurations.All)
        {
            var outcome = scenario(SimConfigOf(configuration));
            Check(outcome.Problems.Count == 0, $"{outcome.Scenario}, {PerfBench.Name(outcome.Config)}: {string.Join(" | ", outcome.Problems)} [{outcome.Describe()}]");
            said.Add(outcome.Describe());
        }
        return string.Join(" || ", said);
    }

    /// <summary>Before a stress step's first "before": whatever the process makes once, on first need, is made. The gate's own
    /// once-a-second watch for stuck dialogs (StuckDialogWatch) starts a timer thread a second into every step, the thread pool
    /// a worker for it; and a first rig's first minute has Windows start a thread-pool worker of its own (a thread with no
    /// start address, waiting on its worker factory - seen 2026-09-28 in a Debug build's gate process, five handles with it,
    /// in the middle of the first scenario and never in the later ones; a leak would grow, and the soak's day and the churn's
    /// two thousand sessions show none). So a throwaway call runs first, a session pruned and opened again in it.</summary>
    private static bool gateThreadsSettled;

    private static void SettleTheGatesOwnThreads()
    {
        if (gateThreadsSettled) return;
        gateThreadsSettled = true;
        using var ran = new ManualResetEventSlim();
        ThreadPool.QueueUserWorkItem(_ => ran.Set());
        ran.Wait(TimeSpan.FromSeconds(5));
        PerfBench.OnOwnThread(() =>
        {
            using var rig = new PerfRig(new PerfRigOptions(SimConfig.WasapiOnly, SimCodec.Opus2_5));
            rig.Join(0, PerfSignal.ToneAndClicks);
            rig.RunFor(6);
            rig.NetworkDown = true;
            rig.RunFor(6);
            rig.NetworkDown = false;
            PerfStress.Listen(rig, 5);
            return 0;
        });
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false);
        Thread.Sleep(1200);
    }

    /// <summary>PERFORMANCE STRESS: MANY SENDERS. Fifteen more peers join a call at once for ten seconds.</summary>
    private static string PerformanceStressManySenders()
    {
        return PerformanceStress(PerfStress.ManySenders);
    }

    /// <summary>PERFORMANCE STRESS: CHURN. A thousand joins and leaves in each configuration, each a session opened and
    /// pruned (in "both" two, one per lane).</summary>
    private static string PerformanceStressChurn()
    {
        return PerformanceStress(PerfStress.Churn);
    }

    /// <summary>PERFORMANCE STRESS: NETWORK FLAPS. Drops of 2 and 6 s, a 1.5 s stall and a rough spell.</summary>
    private static string PerformanceStressNetworkFlaps()
    {
        return PerformanceStress(PerfStress.NetworkFlaps);
    }

    /// <summary>PERFORMANCE STRESS: FLOODS. Doubled, overtaken, junk and wrong-password packets, and a burst of format
    /// packets from many ports.</summary>
    private static string PerformanceStressFloods()
    {
        return PerformanceStress(PerfStress.Floods);
    }

    /// <summary>PERFORMANCE STRESS: CARD STALLS. The output hiccuping and stopping, a late driver, the capture stopping.</summary>
    private static string PerformanceStressCardStalls()
    {
        return PerformanceStress(PerfStress.CardStalls);
    }
}
