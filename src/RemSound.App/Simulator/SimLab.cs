namespace RemSound.App.Simulator;

/// <summary>
/// The latency lab, simulated (2026-09-26). For each network, configuration and codec: the smallest jitter buffer that
/// stays perfectly clean (not one break-up or click) over three different random runs, the true delay there, and what
/// the auto-tune's formula would have asked for (normal and lean margins) - which checks the lab's assumptions. Then the
/// per-stream switch with a lumpy WASAPI stream beside a smooth ASIO one, clock drift, and a stalling sender. WAVs of the
/// fastest clean setting and of one step below it (what breaking up sounds like) go to the output folder.
/// </summary>
internal static class SimLab
{
    private static readonly int[] Sweep = [1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 15, 20, 25, 30, 40, 50, 60, 80, 100, 150, 200];

    private sealed record Best(SimNetwork Network, SimConfig Config, SimCodec Codec, int BufferMs, SimResult Result, int BelowMs)
    {
        /// <summary>Break-ups over the three runs that no buffer cures (lost packets): 0 on a clean network.</summary>
        public int FloorGlitches { get; init; }
    }

    public static void Run(double seconds, Action<string> say, string outDir)
    {
        var networks = new[] { SimNetwork.Perfect, SimNetwork.Wired, SimNetwork.Wifi, SimNetwork.Internet, SimNetwork.Rough, SimNetwork.Lumpy };
        var cases = new (SimConfig Config, SimCodec Codec)[]
        {
            (SimConfig.AsioOnly, SimCodec.Pcm),
            (SimConfig.AsioOnly, SimCodec.Custom64),
            (SimConfig.AsioOnly, SimCodec.Custom128),
            (SimConfig.AsioOnly, SimCodec.Opus2_5),
            (SimConfig.AsioOnly, SimCodec.Opus20),
            (SimConfig.WasapiOnly, SimCodec.Pcm),
            (SimConfig.WasapiOnly, SimCodec.Opus2_5),
            (SimConfig.WasapiOnly, SimCodec.Opus20),
        };
        var bests = new List<Best>();
        say("");
        say("THE SMALLEST BUFFER THAT IS AS GOOD AS EACH NETWORK ALLOWS - over three different random runs each");
        foreach (var net in networks)
        {
            say("");
            say($"{net.Name}:");
            foreach (var (config, codec) in cases)
            {
                var best = FindSmallestClean(net, config, codec, seconds);
                if (best is null) { say($"  {config} {Name(codec)}: the tone never comes through intact, whatever the buffer"); continue; }
                bests.Add(best);
                var r = best.Result;
                var f = r.Findings;
                var how = best.FloorGlitches == 0 ? "clean" : $"as good as this network allows ({best.FloorGlitches} break-ups in {3 * seconds:0} s, all from lost packets)";
                say($"  {config} {Name(codec)}: {how} from {best.BufferMs} ms -> {f.DelayMedianMs:0.0} ms through RemSound "
                    + $"(tone-to-noise {f.MedianSnrDb:0} dB); {(best.BelowMs == 0 ? "nothing smaller to try" : $"at {best.BelowMs} ms it broke up")}; the auto-tune's formula would ask "
                    + $"{r.TunerWouldAskMs(false)} ms, or {r.TunerWouldAskMs(true)} ms with lean margins");
            }
        }

        say("");
        say("THE FASTEST CLEAN CHOICE ON EACH NETWORK (ASIO at both ends)");
        foreach (var net in networks)
        {
            var fastest = bests.Where(b => b.Network == net && b.Config == SimConfig.AsioOnly)
                .OrderBy(b => b.Result.Findings.DelayMedianMs).FirstOrDefault();
            if (fastest is null) continue;
            say($"  {net.Name}: {Name(fastest.Codec)} with a {fastest.BufferMs} ms buffer - {fastest.Result.Findings.DelayMedianMs:0.0} ms");
            var below = Simulation.Run(new SimScenario("below", SimConfig.AsioOnly, fastest.Codec, net, fastest.BelowMs, Math.Min(seconds, 8), KeepAudio: true));
            var at = Simulation.Run(new SimScenario("at", SimConfig.AsioOnly, fastest.Codec, net, fastest.BufferMs, Math.Min(seconds, 8), KeepAudio: true));
            SimRunner.WriteWav(Path.Combine(outDir, $"{net.Name} - {Name(fastest.Codec)} at {fastest.BufferMs} ms (clean).wav"), at.Audio!);
            SimRunner.WriteWav(Path.Combine(outDir, $"{net.Name} - {Name(fastest.Codec)} at {fastest.BelowMs} ms (breaking up).wav"), below.Audio!);
        }

        say("");
        say("PER-STREAM BUFFERS - a smooth ASIO stream beside a lumpy WASAPI one, the ASIO output set to 30 ms as the auto-tune leaves it");
        foreach (var on in new[] { false, true })
        {
            var r = Simulation.Run(new SimScenario($"per-stream {(on ? "on" : "off")}", SimConfig.Both, SimCodec.Opus2_5, SimNetwork.Wired, 30,
                Math.Max(seconds, 40), PerStreamBuffers: on, WasapiLaneBufferMs: 30));
            say($"  switch {(on ? "on " : "off")}: {SimRunner.Describe(r)}");
        }

        say("");
        say("CLOCK DRIFT - the two sound cards 100 ppm apart each way, a minute");
        foreach (var (config, codec, buffer) in new[] { (SimConfig.AsioOnly, SimCodec.Opus2_5, 10), (SimConfig.WasapiOnly, SimCodec.Opus2_5, 40) })
        {
            var asio = config != SimConfig.WasapiOnly;
            var card = Simulation.DefaultCard(asio);
            foreach (var ppm in new[] { 200.0, -200.0 })
            {
                var r = Simulation.Run(new SimScenario($"drift {ppm:+0;-0} ppm", config, codec, SimNetwork.Wired, buffer, 60,
                    SendCard: card with { DriftPpm = ppm / 2 }, ReceiveCard: card with { DriftPpm = -ppm / 2 }));
                say($"  {SimRunner.Describe(r)}");
            }
        }

        say("");
        say("A SENDER THAT STALLS - WASAPI capture late by 40 ms every 4 s, then catching up (the Andre pattern)");
        var stalled = FindSmallestClean(SimNetwork.Wired, SimConfig.WasapiOnly, SimCodec.Opus2_5, seconds,
            sendCard: new SimCard(480, JitterMs: 1, StallEverySec: 4, StallMs: 40));
        say(stalled is null ? "  never clean up to 200 ms"
            : $"  clean from {stalled.BufferMs} ms -> {stalled.Result.Findings.DelayMedianMs:0.0} ms; the auto-tune would ask {stalled.Result.TunerWouldAskMs(false)} ms");
    }

    /// <summary>The smallest buffer that is as good as this network allows, over three runs. Every run of a scenario meets
    /// the same lost, doubled and late packets whatever the buffer (the network's dice are thrown per packet, and the
    /// sender does not depend on the receiver), so the break-ups still left at 200 ms are the ones no buffer can cure -
    /// lost packets. "As good as it gets" is the smallest buffer with no more than those; on a clean network that is
    /// simply clean.</summary>
    /// <summary>
    /// The reorder wait, off against on (Ed, 2026-09-26: "if never worse, make it default"). For every network and codec:
    /// the smallest buffer that is as good as the network allows, off and on; and at every buffer from that size up to
    /// 200 ms the break-ups over three runs, off and on - so "never worse" means at EVERY buffer, not just the best one.
    /// Ends with a verdict line. Also Ed's laptop pattern: packets overtaken on a wired LAN with nothing lost at all.
    /// </summary>
    public static void RunReorder(double seconds, Action<string> say)
    {
        var laptop = new SimNetwork("Ed's laptop pattern (overtaken, none lost)", 1, 0.3, ReorderPercent: 0.3, HiccupPercent: 0.2, HiccupMs: 8);
        var networks = new[] { SimNetwork.Wired, laptop, SimNetwork.Wifi, SimNetwork.Internet, SimNetwork.Rough, SimNetwork.Lumpy };
        var cases = new (SimConfig Config, SimCodec Codec)[]
        {
            (SimConfig.AsioOnly, SimCodec.Pcm), (SimConfig.AsioOnly, SimCodec.Custom64), (SimConfig.AsioOnly, SimCodec.Custom128),
            (SimConfig.AsioOnly, SimCodec.Opus2_5), (SimConfig.AsioOnly, SimCodec.Opus20), (SimConfig.WasapiOnly, SimCodec.Opus2_5),
        };
        var worse = new List<string>();
        var better = new List<string>();
        foreach (var net in networks)
        {
            say("");
            say($"{net.Name}:");
            foreach (var (config, codec) in cases)
            {
                var off = FindSmallestClean(net, config, codec, seconds, reorderWait: false);
                var on = FindSmallestClean(net, config, codec, seconds, reorderWait: true);
                var label = $"{config} {Name(codec)}";
                string Show(Best? b) => b is null ? "never intact" : $"{b.BufferMs} ms ({b.Result.Findings.DelayMedianMs:0.0} ms through, {b.FloorGlitches} break-ups left)";
                say($"  {label}: off {Show(off)}; on {Show(on)}");
                // Every buffer from the smaller of the two best sizes up: break-ups over three runs, off and on.
                var from = Math.Min(off?.BufferMs ?? Sweep[^1], on?.BufferMs ?? Sweep[^1]);
                var detail = new List<string>();
                foreach (var buffer in Sweep.Where(b => b >= from))
                {
                    var gOff = Glitches(net, config, codec, buffer, seconds, false);
                    var gOn = Glitches(net, config, codec, buffer, seconds, true);
                    if (gOn > gOff) worse.Add($"{net.Name}, {label}, {buffer} ms: {gOff} break-ups off, {gOn} on");
                    if (gOn < gOff) better.Add($"{net.Name}, {label}, {buffer} ms: {gOff} -> {gOn}");
                    if (gOff != gOn) detail.Add($"{buffer} ms {gOff}->{gOn}");
                    if (buffer >= 100) break;
                }
                if (detail.Count > 0) say($"    break-ups off->on where they differ: {string.Join(", ", detail)}");
                if (off is not null && on is not null && on.BufferMs > off.BufferMs)
                    worse.Add($"{net.Name}, {label}: needs {on.BufferMs} ms on against {off.BufferMs} ms off");
            }
        }
        say("");
        say(worse.Count == 0
            ? $"VERDICT: never worse - no network, codec or buffer where waiting made it worse. Better in {better.Count} places."
            : $"VERDICT: worse in {worse.Count} places: " + string.Join("; ", worse));
        if (better.Count > 0) say("Better: " + string.Join("; ", better.Take(40)));
    }

    /// <summary>
    /// The "keep dropouts rare" auto-tune in the loop: from 30 ms, four simulated minutes, on each network with Opus 2.5
    /// and Jamulus-style 64. Where it settles, how it got there, and what the LAST two minutes sound like (break-ups, the
    /// true delay) - set against the smallest buffer a sweep finds clean, and against what the long-standing gap formula
    /// would ask. Seconds per run is ignored: the tuner needs minutes.
    /// </summary>
    public static void RunTuner(double seconds, Action<string> say)
    {
        _ = seconds;
        foreach (var net in new[] { SimNetwork.Wired, SimNetwork.LaptopLumps, SimNetwork.Wifi, SimNetwork.Internet, SimNetwork.Lumpy })
        {
            say("");
            say($"{net.Name}:");
            foreach (var codec in new[] { SimCodec.Opus2_5, SimCodec.Custom64 })
            {
                var r = Simulation.Run(new SimScenario("tuner", SimConfig.AsioOnly, codec, net, 30, 240, LowestDelayTune: true, AnalyseFromSecond: 120));
                var f = r.Findings;
                var sweep = FindSmallestClean(net, SimConfig.AsioOnly, codec, 10);
                say($"  {Name(codec)}: settled at {r.TunedMs} ms; last two minutes {(f.Clean ? "clean" : $"{f.Glitches} break-ups")}, "
                    + $"{f.DelayMedianMs:0.0} ms through RemSound; a sweep finds {(sweep is null ? "nothing clean" : $"{sweep.BufferMs} ms clean")}; "
                    + $"the gap formula would ask {r.TunerWouldAskMs(false)} ms");
                say($"    moves: {(r.TunerMoves.Length == 0 ? "none" : r.TunerMoves)}");
            }
        }
    }

    private static int Glitches(SimNetwork net, SimConfig config, SimCodec codec, int buffer, double seconds, bool reorderWait)
    {
        var total = 0;
        for (var seed = 1; seed <= 3; seed++)
        {
            var r = Simulation.Run(new SimScenario("g", config, codec, net, buffer, seconds, Seed: seed, ReorderWait: reorderWait));
            total += r.Findings.ClicksHeard == 0 ? 1000 : r.Findings.Glitches + (r.Findings.WorstSnrDb > 15 ? 0 : 1);
        }
        return total;
    }

    private static Best? FindSmallestClean(SimNetwork net, SimConfig config, SimCodec codec, double seconds, SimCard? sendCard = null,
        bool reorderWait = true)
    {
        (int Glitches, bool TonesOk, SimResult First) Try(int buffer)
        {
            var glitches = 0;
            var ok = true;
            SimResult? first = null;
            for (var seed = 1; seed <= 3; seed++)
            {
                var r = Simulation.Run(new SimScenario($"{net.Name} {config} {codec} {buffer}", config, codec, net, buffer, seconds,
                    SendCard: sendCard, Seed: seed, ReorderWait: reorderWait));
                first ??= r;
                glitches += r.Findings.ClicksHeard == 0 ? 1_000_000 : r.Findings.Glitches;
                ok &= r.Findings.MedianSnrDb > 15;
            }
            return (glitches, ok, first!);
        }
        var floor = Try(Sweep[^1]);
        if (!floor.TonesOk) return null;
        var previous = 0;
        foreach (var buffer in Sweep)
        {
            var (glitches, ok, first) = buffer == Sweep[^1] ? floor : Try(buffer);
            if (ok && glitches <= floor.Glitches) return new Best(net, config, codec, buffer, first, previous) { FloorGlitches = floor.Glitches };
            previous = buffer;
        }
        return null;
    }

    public static string Name(SimCodec codec) => codec switch
    {
        SimCodec.Pcm => "PCM",
        SimCodec.Opus2_5 => "Opus 2.5 ms",
        SimCodec.Opus10 => "Opus 10 ms",
        SimCodec.Opus20 => "Opus 20 ms",
        SimCodec.Custom128 => "Jamulus-style 128",
        SimCodec.Custom64 => "Jamulus-style 64",
        _ => codec.ToString(),
    };
}
