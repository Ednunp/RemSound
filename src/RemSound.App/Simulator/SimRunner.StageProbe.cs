using System.Diagnostics;
using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App.Simulator;

internal static partial class SimRunner
{
    /// <summary>Investigation, "--simulate stage-probe [--ppm p] [--jitter ms] [--card-jitter ms] [--minutes m] [--every s]
    /// [--phase ms] [--seed n]": the WASAPI output stage on its own - the producer's 10 ms feeds on the machine's clock and a
    /// 10 ms card on its own, a tone and clicks through it - and each stretch the pulls the stage could not fill, the delay,
    /// the buffer's time-averaged level and its range just before a pull, and the drift loop's readings (2026-09-28, the
    /// investigation behind SIMULATOR WASAPI OUTPUT STAGE). Break-ups are the full simulator's to hear ("sim-probe --stage"):
    /// a pull padded by a frame or two is a click this bare tone does not always show.</summary>
    private static void StageProbe(Action<string> say, string[] args)
    {
        var ppm = Number(args, "--ppm", 0);
        var jitterMs = Number(args, "--jitter", 1);
        var cardJitterMs = Number(args, "--card-jitter", 1);
        var minutes = Number(args, "--minutes", 10);
        var every = Number(args, "--every", 10);
        var phaseMs = Number(args, "--phase", 0);
        var rng = new Random((int)Number(args, "--seed", 1));
        const int Rate = 48000;
        double now = 0;
        var baseTicks = Stopwatch.GetTimestamp();
        AudioClock.UseSimulated(() => baseTicks + (long)(now * Stopwatch.Frequency));
        try
        {
            var stage = new MultiOutputPlayout.SimulatedStage("probe");
            var frameBytes = MultiOutputPlayout.SimulatedStage.ProducerFrameBytes;
            var feedFrames = frameBytes / 8;
            var feed = new byte[frameBytes];
            var feedFloats = new float[feedFrames * 2];
            long generated = 0;
            var cardPeriod = 480 / (Rate * (1 + ppm * 1e-6));
            var read = new byte[480 * 8];
            var heard = new List<float>();
            var times = new List<(int Frame, double Time)>();
            double nextFeed = 0.010, nextPull = 0.010 + phaseMs / 1000;
            long feedK = 1, pullK = 1;
            double lastPull = 0, lastFeed = 0;
            double levelArea = 0, levelSince = 0, minBefore = double.MaxValue, maxBefore = 0;
            long shortReads0 = 0;
            var stretchStart = 0.0;
            var countFrom = 0;
            double levelNow() => stage.BufferedMs;
            void Accumulate() { levelArea += levelNow() * (now - levelSince); levelSince = now; }
            double feedAt = nextFeed + rng.NextDouble() * jitterMs / 1000;
            double pullAt = nextPull + rng.NextDouble() * cardJitterMs / 1000;
            while (now < minutes * 60)
            {
                if (feedAt <= pullAt)
                {
                    now = Math.Max(feedAt, lastFeed);
                    lastFeed = now;
                    Accumulate();
                    for (var i = 0; i < feedFrames; i++)
                    {
                        var n = generated + i;
                        feedFloats[2 * i] = (float)(Simulation.ToneLevel * Math.Sin(2 * Math.PI * Simulation.ToneHz * n / Rate));
                        feedFloats[2 * i + 1] = n % (Rate / 2) == 0 ? Simulation.ClickLevel : 0f;
                    }
                    generated += feedFrames;
                    Buffer.BlockCopy(feedFloats, 0, feed, 0, frameBytes);
                    stage.Feed(feed, 0, frameBytes);
                    feedK++;
                    feedAt = feedK * 0.010 + rng.NextDouble() * jitterMs / 1000;
                }
                else
                {
                    now = Math.Max(pullAt, lastPull);
                    lastPull = now;
                    Accumulate();
                    var before = levelNow();
                    minBefore = Math.Min(minBefore, before);
                    maxBefore = Math.Max(maxBefore, before);
                    Array.Clear(read);
                    stage.Read(read, 0, read.Length);
                    times.Add((heard.Count / 2, now + cardPeriod));
                    heard.AddRange(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(read).ToArray());
                    pullK++;
                    pullAt = pullK * cardPeriod + phaseMs / 1000 + rng.NextDouble() * cardJitterMs / 1000;
                    if (now - stretchStart >= every)
                    {
                        var f = SimAnalysis.Analyse(heard, times, Rate, 1 / (1 + ppm * 1e-6), countFromFrame: countFrom);
                        var st = stage.Snapshot();
                        say($"{stretchStart:0} s: pulls padded with silence {stage.ShortReads - shortReads0}; "
                            + $"delay {f.DelayMedianMs:0.0} ms ({f.DelayMinMs:0.0}-{f.DelayMaxMs:0.0}); level mean {levelArea / (now - stretchStart):0.0} ms, "
                            + $"before-pull {minBefore:0.0}-{maxBefore:0.0}; target {st.TargetMs}, clock {st.ClockPpm:+0;-0;0} ppm, corr {st.CorrectionPpm:+0;-0;0} ppm");
                        stretchStart = now;
                        levelArea = 0;
                        minBefore = double.MaxValue;
                        maxBefore = 0;
                        shortReads0 = stage.ShortReads;
                        // Keep the last second, so the next stretch starts with a click found (and is counted from there).
                        var keep = Rate;
                        var drop = heard.Count / 2 - keep;
                        countFrom = Math.Min(keep, heard.Count / 2);
                        if (drop > 0)
                        {
                            heard.RemoveRange(0, drop * 2);
                            times = times.Where(t => t.Frame >= drop).Select(t => (t.Frame - drop, t.Time)).ToList();
                        }
                    }
                }
            }
        }
        finally
        {
            AudioClock.UseSimulated(null);
        }
    }
}

internal static partial class SimRunner
{
    private static SimNetwork NetworkArgument(string[] args) => ValueAfter(args, "--net") switch
    {
        "wifi" => SimNetwork.Wifi,
        "internet" => SimNetwork.Internet,
        "rough" => SimNetwork.Rough,
        "laptop" => SimNetwork.LaptopLumps,
        "lumpy" => SimNetwork.Lumpy,
        "perfect" => SimNetwork.Perfect,
        _ => SimNetwork.Wired,
    };

    /// <summary>Investigation, "--simulate sim-probe [--config c] [--codec c] [--net n] [--buffer ms] [--wasapi-buffer ms]
    /// [--seconds s] [--stage] [--tone-wasapi] [--auto-tune] [--chunk s] [--drift ppm] [--wasapi-drift ppm] [--soft ms --soft-at s]
    /// [--seed n] [--every s] [--card-stall-at s --card-stall-ms ms] [--phase ms]": one simulator run (the WASAPI card --phase ms behind the stage's producer) (every receiving card stopping for a
    /// moment if asked), each output's stretches, the WASAPI output stage and every stream copy's depth.</summary>
    private static void SimProbe(Action<string> say, string[] args)
    {
        var config = ConfigArgument(args, SimConfig.Both);
        var codec = CodecArgument(args, SimCodec.Opus2_5);
        var net = NetworkArgument(args);
        var buffer = (int)Number(args, "--buffer", 20);
        var wasapiBuffer = (int)Number(args, "--wasapi-buffer", 40);
        var seconds = Number(args, "--seconds", 60);
        var chunk = Number(args, "--chunk", 10);
        var drift = Number(args, "--drift", 0);
        var wasapiDrift = Number(args, "--wasapi-drift", -drift / 2);
        var every = (int)Number(args, "--every", 10);
        bool Flag(string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        var card = Simulation.DefaultCard(asio: config != SimConfig.WasapiOnly);
        var stallAt = Number(args, "--card-stall-at", 0);
        var stallMs = Number(args, "--card-stall-ms", 0);
        var phase = Number(args, "--phase", 0);
        var s = new SimScenario("probe", config, codec, net, buffer, seconds,
            card with { DriftPpm = drift / 2 }, card with { DriftPpm = -drift / 2, StallAtSec = stallAt, StallOnceMs = stallMs, PhaseMs = config == SimConfig.WasapiOnly ? phase : 0 }, WasapiLaneBufferMs: wasapiBuffer,
            Seed: (int)Number(args, "--seed", 1), ToneOnWasapiLane: Flag("--tone-wasapi"),
            WasapiLaneReceiveCard: new SimCard(480, JitterMs: 1, DriftPpm: wasapiDrift, StallAtSec: stallAt, StallOnceMs: stallMs, PhaseMs: phase), WasapiOutputStage: Flag("--stage"),
            ContinuousAutoTune: Flag("--auto-tune"), ChunkSeconds: chunk, SoftTargetMs: (int)Number(args, "--soft", 0),
            SoftTargetAtSecond: Number(args, "--soft-at", 0),
            ReceiverActions: Number(args, "--wasapi-hand", 0) > 0
                ? [(Number(args, "--wasapi-hand-at", 30), rc => rc.SetMaxLatencyMsFor(RenderRoute.WasapiLane, (int)Number(args, "--wasapi-hand", 0)))]
                : null);
        var r = Simulation.Run(s);
        say(Describe(r));
        say($"trims {r.TrimFires} ({r.TrimDroppedMs:0} ms), snaps {r.SnapCount} ({r.SnapSkippedMs:0} ms), stage short reads {r.StageShortReads}");
        if (r.StageLevelBySecond.Count > 30)
            say($"stage level (time-averaged) from 30 s: {r.StageLevelBySecond.Skip(30).Average():0.00} ms; pulls padded from 30 s: {r.StageShortReadsBySecond[^1] - r.StageShortReadsBySecond[29]}");
        void Stretches(string what, IReadOnlyList<(double FromSec, SimAnalysis.Findings Findings)> list)
        {
            foreach (var (from, f) in list)
                say($"  {what} {from:0} s: delay {f.DelayMedianMs:0.0} ({f.DelayMinMs:0.0}-{f.DelayMaxMs:0.0}), {f.Glitches} break-ups ({f.QuietMs} quiet, {f.StepClicks} clicks), pitch p95 {f.PitchP95Cents:0.00} worst {f.WorstPitchCents:0.00}");
        }
        Stretches(config == SimConfig.WasapiOnly ? "WASAPI output" : "ASIO output", r.Stretches);
        if (config == SimConfig.Both) Stretches("WASAPI output", r.WasapiStretches);
        for (var i = every - 1; i < r.ReplicasBySecond.Count; i += every)
        {
            var line = string.Join("; ", r.ReplicasBySecond[i].Select(c => $"{c.Announced}->{c.Plays}{(c.Mirror ? " (mirror)" : "")} {c.DepthMs:0.0} ms {(c.Ratio - 1) * 1e6:+0;-0} ppm"));
            var stage = i < r.StageBySecond.Count ? $" | stage {r.StageBySecond[i]}" : "";
            say($"  {i + 1} s: {line}{stage}");
        }
        if (r.Window is { } w)
            say("  tuner: " + string.Join(" ", w.Ticks.Where(t => t.To != t.From).Select(t => $"{t.Route}:{t.From}>{t.To}@{t.At:0}s")));
    }
}

internal static partial class SimRunner
{
    /// <summary>Investigation, "--simulate clock-probe [--seconds s] [--from s] [--seeds n]": every stream copy's drift-loop
    /// clock estimate against the truth (the cards all at the same rate, so 0 ppm), in each configuration and on each
    /// network - how far the block-counting at a window's edge throws it.</summary>
    private static void ClockProbe(Action<string> say, string[] args)
    {
        var seconds = Number(args, "--seconds", 300);
        var from = (int)Number(args, "--from", 60);
        var seeds = (int)Number(args, "--seeds", 2);
        var codec = CodecArgument(args, SimCodec.Opus2_5);
        foreach (var net in new[] { SimNetwork.Wired, SimNetwork.Wifi, SimNetwork.Internet, SimNetwork.LaptopLumps })
            foreach (var config in new[] { SimConfig.WasapiOnly, SimConfig.AsioOnly, SimConfig.Both })
            {
                var byCopy = new Dictionary<string, List<double>>();
                var depthErr = new Dictionary<string, List<double>>();
                long glitches = 0;
                for (var seed = 1; seed <= seeds; seed++)
                {
                    var buffer = config == SimConfig.WasapiOnly ? 40 : net == SimNetwork.Wired ? 15 : 30;
                    var r = Simulation.Run(new SimScenario("clock", config, codec, net, buffer, seconds, WasapiLaneBufferMs: 40, Seed: seed));
                    glitches += r.Findings.Glitches + (r.WasapiOutput?.Glitches ?? 0);
                    for (var i = from; i < r.ReplicasBySecond.Count; i++)
                        foreach (var c in r.ReplicasBySecond[i])
                        {
                            var key = $"{c.Announced}->{c.Plays}";
                            if (!byCopy.TryGetValue(key, out var list)) byCopy[key] = list = new List<double>();
                            list.Add((c.Ratio - 1) * 1e6);
                            var target = c.Plays == RenderRoute.AsioLane ? buffer : 40;
                            if (!depthErr.TryGetValue(key, out var d)) depthErr[key] = d = new List<double>();
                            d.Add(c.DepthMs - target);
                        }
                }
                say($"{net.Name}, {config}: {glitches} break-ups; " + string.Join("; ", byCopy.Select(kv =>
                    $"{kv.Key} clock rms {Math.Sqrt(kv.Value.Average(x => x * x)):0} ppm, worst {kv.Value.Max(Math.Abs):0}; depth off target rms "
                    + $"{Math.Sqrt(depthErr[kv.Key].Average(x => x * x)):0.0} ms, worst {depthErr[kv.Key].Max(Math.Abs):0.0}")));
            }
    }
}

internal static partial class SimRunner
{
    /// <summary>Investigation, "--simulate first-probe [--seeds n] [--seconds s] [--net n]": each stream copy's FIRST clock
    /// reading (the one taken whole) and its worst, over several seeds, the cards at the same rate.</summary>
    private static void FirstReadingProbe(Action<string> say, string[] args)
    {
        var seeds = (int)Number(args, "--seeds", 8);
        var seconds = Number(args, "--seconds", 45);
        var net = NetworkArgument(args);
        foreach (var config in new[] { SimConfig.WasapiOnly, SimConfig.AsioOnly, SimConfig.Both })
        {
            var firsts = new Dictionary<string, List<double>>();
            var worsts = new Dictionary<string, double>();
            for (var seed = 1; seed <= seeds; seed++)
            {
                var r = Simulation.Run(new SimScenario("first", config, SimCodec.Opus2_5, net, config == SimConfig.WasapiOnly ? 40 : 20, seconds,
                    WasapiLaneBufferMs: 40, Seed: seed));
                var seen = new HashSet<string>();
                foreach (var second in r.ReplicasBySecond)
                    foreach (var c in second)
                    {
                        var key = $"{c.Announced}->{c.Plays}";
                        var ppm = (c.Ratio - 1) * 1e6;
                        if (!c.Tracking) continue;
                        if (seen.Add(key)) { if (!firsts.TryGetValue(key, out var l)) firsts[key] = l = new(); l.Add(ppm); }
                        worsts[key] = Math.Max(worsts.GetValueOrDefault(key), Math.Abs(ppm));
                    }
            }
            say($"{config}, {net.Name}: " + string.Join("; ", firsts.Select(kv => $"{kv.Key} first {string.Join(" ", kv.Value.Select(x => $"{x:+0;-0}"))}, worst {worsts[kv.Key]:0}")));
        }
    }
}


internal static partial class SimRunner
{
    /// <summary>Investigation, "--simulate settle-probe [--seeds n] [--net n] [--stage]": how far over its target each copy of
    /// the stream sits in the first minute after a start, in each configuration and way of sending - the mean over the
    /// seeds, and the worst seed, at 10, 20, 30, 45 and 60 s.</summary>
    private static void SettleProbe(Action<string> say, string[] args)
    {
        var seeds = (int)Number(args, "--seeds", 6);
        var net = NetworkArgument(args);
        var stage = args.Any(a => string.Equals(a, "--stage", StringComparison.OrdinalIgnoreCase));
        int[] at = [10, 20, 30, 45, 60];
        foreach (var config in new[] { SimConfig.WasapiOnly, SimConfig.AsioOnly, SimConfig.Both })
            foreach (var codec in new[] { SimCodec.Opus2_5, SimCodec.Opus10, SimCodec.Pcm })
            {
                var over = new Dictionary<string, double[]>();
                var worst = new Dictionary<string, double[]>();
                var buffer = config == SimConfig.WasapiOnly ? 40 : 20;
                for (var seed = 1; seed <= seeds; seed++)
                {
                    var r = Simulation.Run(new SimScenario("settle", config, codec, net, buffer, 61, WasapiLaneBufferMs: 40, Seed: seed, WasapiOutputStage: stage));
                    for (var i = 0; i < at.Length; i++)
                        foreach (var c in r.ReplicasBySecond[at[i] - 1])
                        {
                            var key = $"{c.Announced}->{c.Plays}";
                            if (!over.TryGetValue(key, out var v)) over[key] = v = new double[at.Length];
                            var off = c.DepthMs - (c.Plays == RenderRoute.AsioLane ? buffer : 40);
                            v[i] += off / seeds;
                            if (!worst.TryGetValue(key, out var w)) worst[key] = w = new double[at.Length];
                            if (Math.Abs(off) > Math.Abs(w[i])) w[i] = off;
                        }
                }
                say($"{config}, {SimLab.Name(codec)}: " + string.Join("; ", over.Select(kv => $"{kv.Key} over target at {string.Join("/", at)} s: "
                    + string.Join(" ", kv.Value.Select(x => $"{x:+0.0;-0.0;0.0}")) + " (worst seed " + string.Join(" ", worst[kv.Key].Select(x => $"{x:+0.0;-0.0;0.0}")) + ")")));
            }
    }
}

internal static partial class SimRunner
{
    /// <summary>Investigation, "--simulate hiccup-probe [--at s] [--sizes 0.02,0.05,...]": the output hiccuping (the card stops
    /// for a moment and its missed callbacks are gone), in each configuration and at each size - the underruns in the minute
    /// after, how long until the call is clean again (PerfStress.TimeToClean), the click-trims, and the drift loop's clock
    /// reading at its furthest from the truth (the cards are at one rate, so 0 ppm). The investigation behind the performance
    /// suite's finding A (2026-09-28): a hiccup of 20 to 450 ms is taken for a slow clock.</summary>
    private static void HiccupProbe(Action<string> say, string[] args)
    {
        var at = Number(args, "--at", 11);
        var sizes = (ValueAfter(args, "--sizes") ?? "0.02,0.05,0.1,0.2,0.45,0.6")
            .Split(',').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        foreach (var config in new[] { SimConfig.WasapiOnly, SimConfig.AsioOnly, SimConfig.Both })
            foreach (var size in sizes)
            {
                var line = PerfBench.OnOwnThread(() =>
                {
                    using var rig = new PerfRig(new PerfRigOptions(config, SimCodec.Opus2_5) { Seed = 5 });
                    rig.Join(0, PerfSignal.ToneAndClicks);
                    rig.RunUntil(at);
                    var r = rig.Receiver!;
                    var route = config == SimConfig.WasapiOnly ? RenderRoute.WasapiLane : RenderRoute.AsioLane;
                    var u0 = r.TuneBlockingUnderruns;
                    var t0 = r.TrimFireCount;
                    rig.StallOutputs(size, catchUp: false);
                    rig.RunFor(size);
                    var worstPpm = 0.0;
                    for (var s = 0; s < 60; s++)
                    {
                        rig.RunFor(1);
                        foreach (var ratio in r.DriftRatiosForTest(route))
                            if (Math.Abs(ratio - 1) * 1e6 > Math.Abs(worstPpm)) worstPpm = (ratio - 1) * 1e6;
                    }
                    var underruns = r.TuneBlockingUnderruns - u0;
                    var trims = r.TrimFireCount - t0;
                    // A fresh rig for the time to clean, the same run to the same moment (listening changes nothing heard).
                    using var again = new PerfRig(new PerfRigOptions(config, SimCodec.Opus2_5) { Seed = 5 });
                    again.Join(0, PerfSignal.ToneAndClicks);
                    again.RunUntil(at);
                    again.StallOutputs(size, catchUp: false);
                    again.RunFor(size);
                    var back = PerfStress.TimeToClean(again, 120);
                    return $"{PerfBench.Name(config)}, a {size * 1000:0} ms hiccup at {at:0} s: {underruns} underruns in the minute after, clean again "
                        + $"{(back is { } b ? $"{b:0} s after" : "NOT within 120 s")}, {trims} click-trim(s), the drift loop's clock at worst {worstPpm:+0;-0;0} ppm";
                });
                say(line);
            }
    }
}
