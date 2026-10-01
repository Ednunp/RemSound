using System.Diagnostics;
using System.Globalization;
using System.Text;
using RemSound.Core;

namespace RemSound.App.Simulator;

/// <summary>
/// The performance, soak and stress modes of <c>RemSound.exe --simulate</c> (2026-09-28):
/// <list type="bullet">
/// <item><c>perf</c> - the whole suite (the CPU benchmarks, allocations, the gate's soak day, every stress run) and a report
/// in plain English, perf-&lt;date&gt;.txt, in --out (the source tree's simulation-results when it can be found);</item>
/// <item><c>soak [hours]</c> - one call all the way through for as many hours as asked (24 by default; 72 overnight), all
/// three configurations side by side (<c>--day</c> for the gate's day, <c>--codec opus2.5|opus10</c>);</item>
/// <item><c>stress [--only 12345]</c> - the stress runs;</item>
/// <item><c>perf-baseline [--passes N]</c> - measure this machine and build's CPU baseline into tests\perf-baseline.json;</item>
/// <item>and the investigations behind the bars and findings: <c>perf-probe</c>, <c>soak-probe</c>, <c>stall-probe</c>,
/// <c>wander-probe</c> and <c>alloc-probe</c>; and since 2026-09-28 (the WASAPI output stage, and the lanes kept apart)
/// <c>stage-probe</c>, <c>sim-probe</c>, <c>clock-probe</c>, <c>first-probe</c>, <c>settle-probe</c> and <c>hiccup-probe</c>
/// (SimRunner.StageProbe.cs).</item>
/// </list>
/// </summary>
internal static partial class SimRunner
{
    private static bool RunPerformanceMode(string what, Action<string> say, string[] args, string outDir)
    {
        switch (what)
        {
            case "perf": PerfReport(say, args); return true;
            case "soak": SoakMode(say, args); return true;
            case "stress": StressMode(say, args); return true;
            case "perf-baseline": PerfBaselineMode(say, args); return true;
            case "perf-probe": PerfProbe(say, args); return true;
            case "soak-probe": SoakProbe(say, args); return true;
            case "stage-probe": StageProbe(say, args); return true;
            case "sim-probe": SimProbe(say, args); return true;
            case "clock-probe": ClockProbe(say, args); return true;
            case "first-probe": FirstReadingProbe(say, args); return true;
            case "settle-probe": SettleProbe(say, args); return true;
            case "hiccup-probe": HiccupProbe(say, args); return true;
            case "stall-probe": StallProbe(say, args); return true;
            case "wander-probe": WanderProbe(say); return true;
            case "alloc-probe": AllocProbe(say, args); return true;
            default: return false;
        }
    }

    private static double Number(string[] args, string name, double fallback) =>
        double.TryParse(ValueAfter(args, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static SimConfig ConfigArgument(string[] args, SimConfig fallback) => ValueAfter(args, "--config") switch
    {
        "wasapi" => SimConfig.WasapiOnly,
        "asio" => SimConfig.AsioOnly,
        "both" => SimConfig.Both,
        _ => fallback,
    };

    private static SimCodec CodecArgument(string[] args, SimCodec fallback) => ValueAfter(args, "--codec") switch
    {
        "pcm" => SimCodec.Pcm,
        "opus2.5" => SimCodec.Opus2_5,
        "opus10" => SimCodec.Opus10,
        _ => fallback,
    };

    /// <summary>"--simulate perf": everything the gate's performance steps run, and the report.</summary>
    private static void PerfReport(Action<string> say, string[] args)
    {
        var root = PerfBaseline.FindSourceRoot();
        var folder = ValueAfter(args, "--out") ?? (root is null ? Path.Combine(Path.GetTempPath(), "RemSound-simulation") : Path.Combine(root, "simulation-results"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"perf-{DateTime.Now:yyyy-MM-dd}.txt");
        var report = new StringBuilder();
        void Line(string text = "") { say(text); report.AppendLine(text); }
        var wall = Stopwatch.StartNew();
        var machine = ThreadCpu.MachineKey();
        var build = ThreadCpu.BuildKind();
        Line($"RemSound performance, soak and stress report, {DateTime.Now:d MMMM yyyy, HH:mm}.");
        Line($"Machine: {machine}. Build: {build}. Opus: {OpusKind()}.");
        Line("Everything below is the real sender and receiver in the simulator's performance rig: fake sound cards and a fake network, "
            + "in virtual time, one thread per run. CPU is that thread's own time, as percent of ONE core at real time.");
        Line();

        Line("1. CPU.");
        var baseline = PerfBaseline.Load(PerfBaseline.PathIn(root), machine, build, out var said);
        var results = PerfSuite.RunAll(PerfSuite.CpuCases());
        Line($"The benchmark thread was {ThreadCpu.LastHold}.");
        foreach (var r in results) Line($"  {PerfBench.Describe(r)}.");
        var verdict = PerfSuite.Judge(results, baseline, said, PerfBench.Run);
        Line($"In short, {verdict.Summary}.");
        foreach (var l in verdict.Lines) Line(l + ".");
        Line(verdict.Failures.Count == 0 ? "Every benchmark within its budget and its baseline." : "PROBLEMS: " + string.Join(" | ", verdict.Failures));
        Line();

        Line("2. Allocations on the audio path.");
        var (readFormat, formatRecord) = PerfAlloc.Sites();
        foreach (var config in PerfSuite.Configs)
            foreach (var codec in PerfSuite.Codecs)
                foreach (var peers in codec == SimCodec.Opus2_5 ? new[] { 1, 4 } : new[] { 1 })
                    Line($"  {PerfAlloc.Measure(config, codec, peers).Plain}.");
        Line($"The sites: RemPacket.TryReadFormat allocates {readFormat} bytes a format packet on its own; the sender's AudioFormatInfo is "
            + $"{formatRecord} bytes a re-announce.");
        Line();

        Line("3. The soak: a simulated day in each configuration, side by side.");
        var soak = SimSoak.Run(new SoakOptions(24));
        var soakVerdict = PerfVerdicts.Soak(soak);
        foreach (var l in soakVerdict.Lines) Line($"  {l}.");
        Line(soakVerdict.Failures.Count == 0 ? "The soak held." : "PROBLEMS: " + string.Join(" | ", soakVerdict.Failures));
        Line();

        Line("4. The stress runs.");
        foreach (var scenario in Scenarios)
            foreach (var config in PerfSuite.Configs)
            {
                var outcome = scenario(config);
                Line($"  {outcome.Scenario}, {outcome.Describe()}.");
                foreach (var problem in outcome.Problems) Line($"    PROBLEM: {problem}.");
            }
        Line();
        Line($"All of it took {wall.Elapsed.TotalMinutes:0.0} minutes.");
        File.WriteAllText(path, report.ToString());
        say($"written: {path}");
    }

    private static readonly Func<SimConfig, StressOutcome>[] Scenarios =
        [PerfStress.ManySenders, PerfStress.Churn, PerfStress.NetworkFlaps, PerfStress.Floods, PerfStress.CardStalls];

    /// <summary>"--simulate stress [--only 12345]": the stress runs (1 many senders, 2 churn, 3 network flaps, 4 floods,
    /// 5 card stalls), each in every configuration.</summary>
    private static void StressMode(Action<string> say, string[] args)
    {
        var only = ValueAfter(args, "--only");
        for (var i = 0; i < Scenarios.Length; i++)
        {
            if (only is not null && !only.Contains((char)('1' + i))) continue;
            foreach (var config in PerfSuite.Configs)
            {
                var outcome = Scenarios[i](config);
                say($"{outcome.Scenario}, {outcome.Describe()}");
                foreach (var problem in outcome.Problems) say($"  PROBLEM: {problem}");
            }
        }
    }

    /// <summary>"--simulate soak [hours]": one call all the way through, for as many hours as asked (24 by default), each
    /// configuration side by side, the process and the rigs' tables looked at every half hour or so. Long runs belong here,
    /// overnight: RemSound.exe --simulate soak 72. With --day, the gate's day instead (a two-hour call, visitors, an evening
    /// call); --codec opus2.5 or opus10 for Opus (about ten times the CPU of PCM).</summary>
    private static void SoakMode(Action<string> say, string[] args)
    {
        var at = Array.FindIndex(args, a => string.Equals(a, "soak", StringComparison.OrdinalIgnoreCase));
        var hours = at >= 0 && at + 1 < args.Length && double.TryParse(args[at + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)
            ? h : Number(args, "--hours", 24);
        var codec = CodecArgument(args, SimCodec.Pcm);
        var gateDay = args.Any(a => string.Equals(a, "--day", StringComparison.OrdinalIgnoreCase));
        var sampleEvery = Math.Max(30, Math.Ceiling(hours * 60 / 48 / 30) * 30);
        var o = gateDay ? new SoakOptions(hours, codec) : new SoakOptions(hours, codec, SampleEveryMinutes: sampleEvery, CallHours: hours);
        o = o with
        {
            VisitEveryMinutes = Number(args, "--visit-every", o.VisitEveryMinutes),
            SampleEveryMinutes = Number(args, "--sample-every", o.SampleEveryMinutes),
            CallHours = Number(args, "--call-hours", o.CallHours),
            VisitSeconds = Number(args, "--visit-seconds", o.VisitSeconds),
        };
        say($"soak: {hours:0.#} simulated hours, {SimLab.Name(codec)}, {(gateDay ? "the gate's day" : "one call all the way through")}, all three configurations "
            + $"side by side; machine {ThreadCpu.MachineKey()}, {ThreadCpu.BuildKind()} build");
        var result = SimSoak.Run(o, say);
        var verdict = PerfVerdicts.Soak(result);
        foreach (var line in verdict.Lines) say(line);
        say(verdict.Failures.Count == 0 ? "RESULT: the soak held" : "RESULT: PROBLEMS - " + string.Join(" | ", verdict.Failures));
    }

    /// <summary>"--simulate perf-baseline [--passes N] [--root folder]": measure every CPU benchmark N times (3 by default)
    /// and write the median of each into tests\perf-baseline.json as this machine and build's entry, keeping the others.</summary>
    private static void PerfBaselineMode(Action<string> say, string[] args)
    {
        var passes = int.TryParse(ValueAfter(args, "--passes"), out var p) && p > 0 ? p : 3;
        var machine = ThreadCpu.MachineKey();
        var build = ThreadCpu.BuildKind();
        var path = PerfBaseline.PathIn(ValueAfter(args, "--root") ?? PerfBaseline.FindSourceRoot());
        if (path is null) { say("the source tree was not found: set REMSOUND_SOURCE_ROOT or pass --root"); return; }
        say($"measuring the CPU baseline for {machine}, {build} build: {passes} passes; into {path}");
        var cases = PerfSuite.CpuCases();
        var all = new Dictionary<string, List<double>>();
        for (var pass = 1; pass <= passes; pass++)
        {
            var wall = Stopwatch.StartNew();
            foreach (var r in PerfSuite.RunAll(cases))
            {
                if (!all.TryGetValue(r.Case.Key, out var list)) all[r.Case.Key] = list = new List<double>();
                list.Add(r.CorePercent);
            }
            say($"pass {pass}: {wall.Elapsed.TotalSeconds:0.0} s");
        }
        var medians = all.ToDictionary(kv => kv.Key, kv => PerfBench.Median(kv.Value.ToArray()));
        foreach (var (key, values) in all.OrderBy(kv => kv.Key))
            say($"{key}: {medians[key]:0.000} (passes {string.Join(" ", values.Select(v => v.ToString("0.000")))}, spread {(values.Max() - values.Min()) / Math.Max(1e-9, medians[key]) * 100:0.0} %)");
        PerfBaseline.Save(path, machine, build, medians, passes, $"the median of {passes} passes of RemSound.exe --simulate perf-baseline; the benchmark thread {ThreadCpu.LastHold}");
        say($"written: {path}");
    }

    /// <summary>"--simulate perf-probe [--cases words] [--repeat N]": the CPU benchmarks whose key contains the words.</summary>
    private static void PerfProbe(Action<string> say, string[] args)
    {
        var repeat = int.TryParse(ValueAfter(args, "--repeat"), out var r) ? r : 1;
        say($"machine: {ThreadCpu.MachineKey()}, {ThreadCpu.BuildKind()} build, cycle rate {ThreadCpu.CyclesPerSecond / 1e9:0.000} GHz");
        var warm = Stopwatch.StartNew();
        PerfBench.WarmUp();
        say($"warm-up {warm.Elapsed.TotalSeconds:0.0} s; the benchmark thread: {ThreadCpu.LastHold}");
        var filter = ValueAfter(args, "--cases");
        for (var i = 0; i < repeat; i++)
        {
            var all = Stopwatch.StartNew();
            foreach (var c in PerfSuite.CpuCases().Where(c => filter is null || c.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                say(PerfBench.Describe(PerfBench.Run(c)));
            say($"pass {i + 1}: {all.Elapsed.TotalSeconds:0.0} s");
        }
    }

    /// <summary>Investigation, "--simulate soak-probe [--config c] [--codec c] [--hours h] [--every minutes] [--stage off]":
    /// one call for hours, heard for ten seconds every so many minutes, and where its time went. How the WASAPI output
    /// stage's clicks were found with no clock difference at all (see the report; fixed 2026-09-28 - the "stage-probe" and
    /// "sim-probe" modes measure it now).</summary>
    private static void SoakProbe(Action<string> say, string[] args)
    {
        var config = ConfigArgument(args, SimConfig.WasapiOnly);
        var codec = CodecArgument(args, SimCodec.Pcm);
        var stage = ValueAfter(args, "--stage") != "off";
        var hours = Number(args, "--hours", 2);
        var every = Number(args, "--every", 10);
        var wall = Stopwatch.StartNew();
        using var rig = new PerfRig(new PerfRigOptions(config, codec) { AsioFrames = (int)Number(args, "--asio", 128), WasapiOutputStage = stage, Seed = 11, ListenAtCard = true });
        rig.Join(0, PerfSignal.ToneAndClicks);
        for (var t = every * 60; t <= hours * 3600 + 1; t += every * 60)
        {
            rig.RunUntil(t - 10);
            var r = rig.Receiver!;
            var u0 = r.TuneBlockingUnderruns;
            var c0 = r.ConcealmentFires;
            var tr0 = r.TrimFireCount;
            rig.StartListening(11);
            rig.RunUntil(t);
            // With the stage in, hear the card's own output (the stage's), not the engine's: the stage is what this is about.
            var f = rig.StopListening();
            say($"{t / 3600:0.000} h: {PerfStress.Said(f)}; underruns +{r.TuneBlockingUnderruns - u0}, concealed +{r.ConcealmentFires - c0}, "
                + $"trims +{r.TrimFireCount - tr0}; buffer {r.CurrentBufferMs} ms; {wall.Elapsed.TotalSeconds:0} s");
        }
        var m = rig.Meter;
        say("time inside each kind of call: " + string.Join(", ", Enum.GetValues<PerfCost>().Select(k => $"{k} {m.Seconds(k):0.00} s ({m.Calls(k)} calls)")));
    }

    /// <summary>Investigation, "--simulate stall-probe [--config c] [--what output|late|capture|netstall|dup|reorder|none]
    /// [--first seconds] [--at seconds] [--quiet 1]": one call, a disturbance, and each second after it the buffer,
    /// underruns, the drift loop's clock reading, click-trims and what was heard. How the ASIO output's slow recovery from a
    /// short stall was found (see PerfStress.CardStalls).</summary>
    private static void StallProbe(Action<string> say, string[] args)
    {
        var config = ConfigArgument(args, SimConfig.AsioOnly);
        var what = ValueAfter(args, "--what") ?? "output";
        var seconds = Number(args, "--seconds", 90);
        var at = Number(args, "--at", 20);
        using var rig = new PerfRig(new PerfRigOptions(config, SimCodec.Opus2_5) { Seed = 5 });
        rig.Join(0, PerfSignal.ToneAndClicks);
        if (ValueAfter(args, "--first") is not null)
        {
            // A shorter stall of the output first, at 6 s.
            rig.RunUntil(6);
            rig.StallOutputs(Number(args, "--first", 0.1), catchUp: false);
        }
        rig.RunUntil(at);
        var r = rig.Receiver!;
        var route = config == SimConfig.WasapiOnly ? RenderRoute.WasapiLane : RenderRoute.AsioLane;
        var totalU0 = r.TuneBlockingUnderruns;
        switch (what)
        {
            case "output": rig.StallOutputs(3, catchUp: false); break;
            case "late": rig.StallOutputs(0.5, catchUp: true); break;
            case "capture": rig.StallSenders(2); break;
            case "netstall": rig.HoldNetwork(1.5); break;
            case "dup": rig.Network = SimNetwork.Wired with { DuplicatePercent = 100 }; break;
            case "reorder": rig.Network = SimNetwork.Wired with { ReorderPercent = 20 }; break;
        }
        for (var t = 0; t < seconds; t++)
        {
            if (t == 5 && what is "dup" or "reorder") rig.Network = SimNetwork.Wired;
            var u0 = r.TuneBlockingUnderruns;
            var c0 = r.ConcealmentFires;
            var tr0 = r.TrimFireCount;
            var l0 = r.LateDroppedForTest;
            var (fec0, un0, _) = r.CodecRepairsForTest;
            var f = PerfStress.Listen(rig, 1);
            var ratios = r.DriftRatiosForTest(route);
            var (fec, un, _) = r.CodecRepairsForTest;
            if (ValueAfter(args, "--quiet") is null)
                say($"{rig.Now - at:0} s: buffer {r.CurrentBufferMsFor(route)} ms, underruns +{r.TuneBlockingUnderruns - u0}, concealed +{r.ConcealmentFires - c0}, "
                    + $"trims +{r.TrimFireCount - tr0}, late +{r.LateDroppedForTest - l0}, repaired +{fec - fec0}, unrepairable +{un - un0}, "
                    + $"clock {string.Join("/", ratios.Select(x => $"{(x - 1) * 1e6:+0;-0}"))} ppm; {PerfStress.Said(f)}");
        }
        say($"{what} at {at} s: {r.TuneBlockingUnderruns - totalU0} underruns in the {seconds} s after");
    }

    /// <summary>Investigation, "--simulate wander-probe": how far the call's delay wanders on its own, heard five seconds at
    /// a time every ten, for ten minutes, in each configuration and way of sending - where PerfStress.DelayWander's bars
    /// come from.</summary>
    private static void WanderProbe(Action<string> say)
    {
        foreach (var config in PerfSuite.Configs)
            foreach (var (codec, frames) in new[] { (SimCodec.Pcm, 32), (SimCodec.Pcm, 256), (SimCodec.Opus2_5, 32), (SimCodec.Opus10, 32) })
            {
                var delays = PerfBench.OnOwnThread(() =>
                {
                    using var rig = new PerfRig(new PerfRigOptions(config, codec) { Seed = 5, AsioFrames = frames });
                    rig.Join(0, PerfSignal.ToneAndClicks);
                    rig.RunFor(20);
                    var list = new List<double>();
                    for (var i = 0; i < 60; i++)
                    {
                        list.Add(PerfStress.Listen(rig, 5).DelayMedianMs);
                        rig.RunFor(5);
                    }
                    return list;
                });
                say($"{PerfBench.Name(config)}, {SimLab.Name(codec)}, ASIO cards {frames} frames: delay {delays.Min():0.0} to {delays.Max():0.0} ms "
                    + $"(spread {delays.Max() - delays.Min():0.0}), largest change between hearings ten seconds apart "
                    + $"{delays.Zip(delays.Skip(1), (a, b) => Math.Abs(a - b)).Max():0.0} ms");
            }
    }

    /// <summary>Investigation, "--simulate alloc-probe [--config c] [--codec c]": every call into RemSound that allocated
    /// in a minute (format packets and the once-a-second work left out), and what one format packet allocates in the
    /// receiver, step by step - how the allocation sites in PerfAlloc were found.</summary>
    private static void AllocProbe(Action<string> say, string[] args)
    {
        var config = ConfigArgument(args, SimConfig.WasapiOnly);
        var codec = CodecArgument(args, SimCodec.Pcm);
        var lines = PerfBench.OnOwnThread(() =>
        {
            var found = new List<string>();
            using var rig = new PerfRig(new PerfRigOptions(config, codec));
            rig.Join(0, PerfSignal.Busy);
            rig.Meter.OnAllocated = (kind, bytes) =>
            {
                if (kind is not PerfCost.EverySecond and not PerfCost.DeliverFormat) found.Add($"{rig.Now:0.0000} s {kind} {bytes} bytes");
            };
            rig.RunUntil(60);
            return found;
        });
        foreach (var line in lines.Take(80)) say(line);
        say($"{lines.Count} allocating calls (format packets and the once-a-second work left out)");

        using var receiver = new RemSound.Receiver.AudioReceiver();
        var (key, fingerprint) = RemSoundCrypto.ForPlainPassword("simulator");
        receiver.SetOutputDevices([]);
        receiver.AudioKey = key;
        receiver.AudioFingerprint = fingerprint;
        receiver.SetPlaybackEnabled(true);
        var ticked = new System.Net.IPEndPoint(PerfRig.IdentityAddress(0), 50123);
        receiver.SetAllowedSenders([ticked]);
        var format = new AudioFormatInfo(48000, 2, 16, 1, 4, 192_000, (int)AudioTransportCodec.Opus, 120, RenderRoute.AsioLane, 0.7);
        var packet = new byte[RemPacket.HeaderSize + RemPacket.FormatPayloadWithCaptureSize];
        RemPacket.WriteHeader(packet, RemPacketType.Format, 7, 1);
        var length = RemPacket.HeaderSize + RemPacket.WriteFormatPayload(packet.AsSpan(RemPacket.HeaderSize), format, fingerprint);
        var stranger = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("10.99.0.1"), 5);
        static long Cost(Action a)
        {
            a();
            a();
            var a0 = GC.GetAllocatedBytesForCurrentThread();
            a();
            return GC.GetAllocatedBytesForCurrentThread() - a0;
        }
        say($"one format packet: RemPacket.TryReadFormat alone {Cost(() => RemPacket.TryReadFormat(packet.AsSpan(RemPacket.HeaderSize, length - RemPacket.HeaderSize), out _, out _))} bytes; "
            + $"the receiver taking it from a stranger {Cost(() => receiver.InjectExternalPacket(packet, length, stranger))} bytes");
        receiver.InjectExternalPacket(packet, length, ticked);
        say($"the receiver taking it from a ticked peer whose session is open: {Cost(() => receiver.InjectExternalPacket(packet, length, ticked))} bytes");

        // A sender's native Opus encoders on Dispose: a hundred senders on 2.5 ms Opus built and disposed, the process's
        // private bytes read before, after (no collection), and after a full collection has run the finalisers.
        static long Private() { using var p = Process.GetCurrentProcess(); p.Refresh(); return p.PrivateMemorySize64; }
        for (var round = 1; round <= 3; round++)
        {
            var before = ProcessState.Sample();
            for (var i = 0; i < 100; i++)
            {
                using var sender = new RemSound.Sender.AudioSender();
                Simulation.ConfigureCodec(sender, SimCodec.Opus2_5);
            }
            var afterDispose = Private();
            var afterCollect = ProcessState.Sample();
            say($"round {round}, a hundred senders on 2.5 ms Opus built and disposed: private bytes {before.PrivateMb:0.0} MB before, "
                + $"{afterDispose / 1048576.0:0.0} MB after (nothing collected), {afterCollect.PrivateMb:0.0} MB once a full collection has run the finalisers");
        }
    }
}
