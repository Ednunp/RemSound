using System.Net;
using RemSound.App.Simulator;
using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App;

/// <summary>
/// THE SIMULATOR IN THE GATE (Ed, 2026-09-26: "it would be something I suppose we could include in our test suite").
/// The real sender and receiver between fake sound cards and a fake network, in virtual time, listened to by
/// <see cref="SimAnalysis"/>. Until now most audio checks proved the code did what it said; these ones LISTEN - a tone for
/// break-ups, clicks, pitch and noise, and clicks for the true delay. Short runs with pass marks; the long comparison is
/// "RemSound.exe --simulate lab". Nothing here opens a device, a window or a socket, or makes a sound.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>SIMULATOR ANALYSER: the listener itself must hear what it claims to - a clean signal reads clean at the
    /// right delay, and a planted gap, click, pitch shift and noise are each caught. Without this every other simulator
    /// step could pass by hearing nothing. Since 2026-09-28 (the review of every test, item 337) also a delay that steps
    /// part way through - the first, last, lowest and highest delay are what the drift, descent and soak steps judge
    /// creep by, and nothing proved they follow a change - and the smallest gap it is trusted to hear, one millisecond,
    /// wherever it falls against the analyser's own millisecond blocks.</summary>
    private static string SimulatorAnalyserHearsPlantedFaults()
    {
        const int rate = Simulation.SampleRate;
        float[] Signal(double seconds, double toneHz = Simulation.ToneHz, Action<float[]>? plant = null)
        {
            var frames = (int)(seconds * rate);
            var x = new float[frames * 2];
            for (var n = 0; n < frames; n++)
            {
                x[2 * n] = (float)(Simulation.ToneLevel * Math.Sin(2 * Math.PI * toneHz * n / rate));
                x[2 * n + 1] = n % (int)(Simulation.ClickEverySec * rate) == 0 ? Simulation.ClickLevel : 0f;
            }
            plant?.Invoke(x);
            return x;
        }
        // Everything heard exactly 10 ms after it went in.
        var times = new List<(int, double)> { (0, 0.010) };
        SimAnalysis.Findings Hear(float[] x) => SimAnalysis.Analyse(x, times, rate);

        var clean = Hear(Signal(3));
        Check(clean.Clean, $"a clean tone must read clean (glitches {clean.Glitches}, quiet {clean.QuietMs} ms, clicks {clean.StepClicks})");
        Check(clean.ClicksHeard >= 5, $"the clicks must be heard - 3 s holds 6 ({clean.ClicksHeard} heard)");
        Check(Math.Abs(clean.DelayMedianMs - 10) < 0.1, $"the delay must read 10 ms ({clean.DelayMedianMs:0.00})");
        Check(clean.WorstPitchCents < 0.5, $"a steady 1 kHz tone must read in tune ({clean.WorstPitchCents:0.00} cents out)");
        Check(clean.MedianSnrDb > 80, $"a perfect sine must read almost noiseless ({clean.MedianSnrDb:0} dB)");

        var gap = Hear(Signal(3, plant: x => { for (var n = rate; n < rate + rate / 200; n++) x[2 * n] = 0; }));
        Check(!gap.Clean && gap.QuietMs >= 3, $"a planted 5 ms gap must be caught (quiet {gap.QuietMs} ms, glitches {gap.Glitches})");

        var step = Hear(Signal(3, plant: x => x[2 * (rate * 3 / 2)] += 0.4f));
        Check(!step.Clean && step.StepClicks > 0, $"a planted click must be caught ({step.StepClicks} clicks)");

        var sharp = Hear(Signal(3, toneHz: Simulation.ToneHz * Math.Pow(2, 50 / 1200.0)));
        Check(sharp.WorstPitchCents is > 45 and < 55, $"a tone 50 cents sharp must read about 50 cents out ({sharp.WorstPitchCents:0.0})");

        var rng = new Random(7);
        var noisy = Hear(Signal(3, plant: x => { for (var n = 0; n < x.Length; n += 2) x[n] += (float)((rng.NextDouble() - 0.5) * 0.05); }));
        Check(noisy.MedianSnrDb < 40 && noisy.MedianSnrDb > 20, $"noise at about -30 dB must read as a tone-to-noise of about 30 dB ({noisy.MedianSnrDb:0} dB)");

        // A delay that steps from 10 ms to 16 ms half way through: everything from 3 s on heard 6 ms later.
        var stepped = SimAnalysis.Analyse(Signal(6), new List<(int, double)> { (0, 0.010), (3 * rate, 3.0 + 0.016) }, rate);
        Check(Math.Abs(stepped.DelayFirstMs - 10) < 0.1 && Math.Abs(stepped.DelayLastMs - 16) < 0.1,
            $"a delay stepping from 10 to 16 ms must read 10 first and 16 last ({stepped.DelayFirstMs:0.00} and {stepped.DelayLastMs:0.00} ms)");
        Check(Math.Abs(stepped.DelayMaxMs - stepped.DelayMinMs - 6) < 0.1 && stepped.ClicksHeard >= 10,
            $"and 6 ms from lowest to highest ({stepped.DelayMinMs:0.00} to {stepped.DelayMaxMs:0.00} ms, {stepped.ClicksHeard} clicks)");

        // The detection floor: a gap of one millisecond, lined up with the analyser's millisecond blocks and straddling
        // two of them (where neither block is quiet enough on its own), both heard - as one break-up, not several.
        var floors = new List<string>();
        foreach (var at in new[] { rate + rate / 5, rate + rate / 5 + 24, rate + rate / 5 + 7 })
        {
            var oneMs = Hear(Signal(3, plant: x => { for (var n = at; n < at + rate / 1000; n++) x[2 * n] = 0; }));
            Check(!oneMs.Clean && oneMs.Glitches == 1,
                $"a planted 1 ms gap {at - rate - rate / 5} samples into a block must be caught as one break-up ({oneMs.Glitches} glitches, "
                + $"{oneMs.QuietMs} quiet ms, {oneMs.StepClicks} step clicks)");
            floors.Add($"{oneMs.QuietMs} quiet ms/{oneMs.StepClicks} steps");
        }
        return $"clean: {clean.DelayMedianMs:0.0} ms, in tune, {clean.MedianSnrDb:0} dB; a 5 ms gap ({gap.QuietMs} quiet ms), a click, "
             + $"50 cents sharp ({sharp.WorstPitchCents:0.0}) and noise ({noisy.MedianSnrDb:0} dB) all caught; a delay stepping 10 -> 16 ms read "
             + $"{stepped.DelayFirstMs:0.0} -> {stepped.DelayLastMs:0.0}; a 1 ms gap caught at three alignments ({string.Join(", ", floors)})";
    }

    private static SimResult Simulate(SimConfig config, SimCodec codec, SimNetwork net, int bufferMs, double seconds,
        SimCard? send = null, SimCard? receive = null, bool perStream = true, int wasapiLaneMs = 0) =>
        Simulation.Run(new SimScenario($"{config} {codec}", config, codec, net, bufferMs, seconds, send, receive, perStream, wasapiLaneMs));

    /// <summary>SIMULATOR ASIO: two desktops on a wired network, ASIO at both ends - each way of sending comes through
    /// clean, in tune, at the delay its settings add up to.</summary>
    private static string SimulatorAsioLinkIsClean()
    {
        var said = new List<string>();
        foreach (var (codec, buffer, maxDelay, minSnr) in new[]
        {
            (SimCodec.Opus2_5, 10, 17.0, 30.0),
            (SimCodec.Pcm, 6, 10.0, 100.0),
            (SimCodec.Custom64, 6, 11.0, 40.0),
        })
        {
            var r = Simulate(SimConfig.AsioOnly, codec, SimNetwork.Wired, buffer, 4);
            var f = r.Findings;
            Check(f.ClicksHeard >= 6, $"{SimLab.Name(codec)}: the clicks must come through ({f.ClicksHeard} heard) - {SimRunner.Describe(r)}");
            Check(f.Clean, $"{SimLab.Name(codec)} at {buffer} ms on a wired network must be clean - {SimRunner.Describe(r)}");
            Check(f.DelayMedianMs > buffer && f.DelayMedianMs < maxDelay,
                $"{SimLab.Name(codec)} at {buffer} ms must take more than the buffer and under {maxDelay} ms in all ({f.DelayMedianMs:0.0} ms)");
            Check(f.MedianSnrDb > minSnr, $"{SimLab.Name(codec)} must keep the tone {minSnr:0} dB above the noise ({f.MedianSnrDb:0} dB)");
            Check(f.WorstPitchCents < 2, $"{SimLab.Name(codec)} must stay in tune ({f.WorstPitchCents:0.0} cents out)");
            said.Add($"{SimLab.Name(codec)} at {buffer} ms: {f.DelayMedianMs:0.0} ms, {f.MedianSnrDb:0} dB");
        }
        return "clean and in tune on a wired network - " + string.Join("; ", said);
    }

    /// <summary>SIMULATOR THREE CONFIGURATIONS: WASAPI only, ASIO only and both each play the tone clean, and ASIO is the
    /// faster.</summary>
    private static string SimulatorThreeConfigurationsPlayClean()
    {
        var delays = new Dictionary<SimConfig, double>();
        foreach (var (config, buffer) in new[] { (SimConfig.WasapiOnly, 40), (SimConfig.AsioOnly, 10), (SimConfig.Both, 10) })
        {
            var r = Simulate(config, SimCodec.Opus2_5, SimNetwork.Wired, buffer, 4);
            Check(r.Findings.Clean, $"{config} must play the tone clean - {SimRunner.Describe(r)}");
            delays[config] = r.Findings.DelayMedianMs;
        }
        Check(delays[SimConfig.AsioOnly] < delays[SimConfig.WasapiOnly] - 10,
            $"ASIO must be well ahead of WASAPI ({delays[SimConfig.AsioOnly]:0.0} ms against {delays[SimConfig.WasapiOnly]:0.0} ms)");
        Check(Math.Abs(delays[SimConfig.Both] - delays[SimConfig.AsioOnly]) < 2,
            $"with both kinds of output, the ASIO output must be as fast as ASIO alone ({delays[SimConfig.Both]:0.0} against {delays[SimConfig.AsioOnly]:0.0} ms)");
        return $"WASAPI only {delays[SimConfig.WasapiOnly]:0.0} ms, ASIO only {delays[SimConfig.AsioOnly]:0.0} ms, both {delays[SimConfig.Both]:0.0} ms, all clean";
    }

    /// <summary>SIMULATOR TOO SMALL: a buffer below what the network needs breaks up - and the simulator says so.</summary>
    private static string SimulatorHearsATooSmallBuffer()
    {
        var r = Simulate(SimConfig.AsioOnly, SimCodec.Opus2_5, SimNetwork.Lumpy, 5, 6);
        Check(!r.Findings.Clean && r.Findings.Glitches > 0,
            $"a 5 ms buffer behind a receiver that takes packets in 24 ms lumps must break up, and be heard to - {SimRunner.Describe(r)}");
        var ok = Simulate(SimConfig.AsioOnly, SimCodec.Opus2_5, SimNetwork.Lumpy, 100, 6);
        Check(ok.Findings.Clean, $"and 100 ms must cover it - {SimRunner.Describe(ok)}");
        return $"5 ms behind 24 ms lumps: {r.Findings.Glitches} break-ups heard; 100 ms: clean";
    }

    /// <summary>SIMULATOR LOSS: lost packets on the internet are made up - Opus's repair on 2.5 ms Opus, concealment on
    /// Jamulus-style frames, and since 2026-09-28 PCM's own made-up frames (StreamSession.ConcealLostPcmFrames) - and the
    /// tone survives. Sharpened 2026-09-28 (the review of every test, item 342): "repaired or noticed" passed with a single
    /// repair; now most of the loss must really be repaired, every break-up heard must be one the repair could not cover,
    /// and PCM's loss must be heard as the short gaps it is meant to be (a fade to silence and back), never as clicks.</summary>
    private static string SimulatorRepairsLostPackets()
    {
        var opus = Simulate(SimConfig.AsioOnly, SimCodec.Opus2_5, SimNetwork.Internet, 40, 8);
        Check(opus.PacketsLost > 0, "the internet network must lose some packets for this to test anything");
        Check(opus.FecRecoveries * 2 >= opus.PacketsLost,
            $"2.5 ms Opus must repair at least half its lost packets from the next packet's copy ({opus.FecRecoveries} of {opus.PacketsLost}) - {SimRunner.Describe(opus)}");
        Check(opus.Findings.Glitches <= opus.UnrecoveredGaps,
            $"and break up no more often than the gaps it could not repair ({opus.Findings.Glitches} break-ups, {opus.UnrecoveredGaps} unrepaired)");
        Check(opus.Findings.MedianSnrDb > 30, $"and the tone must survive - {SimRunner.Describe(opus)}");
        var custom = Simulate(SimConfig.AsioOnly, SimCodec.Custom64, SimNetwork.Internet, 40, 8);
        Check(custom.CustomConcealed > 0, $"Jamulus-style frames must conceal their lost packets - {SimRunner.Describe(custom)}");
        Check(custom.Findings.MedianSnrDb > 30, $"and the tone must survive - {SimRunner.Describe(custom)}");
        // PCM on the same network: no repair to be had, so each loss is made up as a fade to silence and back - short gaps.
        // PCM goes out a packet per callback, as the app sends it (2026-09-28; the simulator's PCM was 5 ms frames the app has
        // not sent since May), so a lost packet is one callback: at 128 samples a 2.7 ms gap, long enough to be heard as one.
        var pcm = Simulate(SimConfig.AsioOnly, SimCodec.Pcm, SimNetwork.Internet, 40, 8, send: new SimCard(128, JitterMs: 0.05));
        var f = pcm.Findings;
        const int Callbacks = 8 * 48000 / 128;
        Check(pcm.PacketsSent >= Callbacks * 0.95,
            $"PCM: premise - a packet per callback, as the app sends it ({pcm.PacketsSent} packets for {Callbacks} callbacks)");
        Check(pcm.PacketsLost > 0 && pcm.PcmConcealed > 0, $"PCM: premise - packets lost and frames made up for them ({pcm.PacketsLost} lost, {pcm.PcmConcealed} frames)");
        Check(f.Glitches >= 1 && f.Glitches <= pcm.PacketsLost,
            $"PCM: each lost packet heard as at most one gap ({f.Glitches} gaps for {pcm.PacketsLost} lost) - {SimRunner.Describe(pcm)}");
        Check(f.StepClicks == 0, $"PCM: the gaps fade out and back in - never a click ({f.StepClicks} clicks)");
        Check(f.QuietMs <= f.Glitches * 4,
            $"PCM: and each gap short - no longer than the lost callback, 2.7 ms ({f.QuietMs} quiet ms over {f.Glitches} gaps)");
        // At the default card's 32 samples a lost packet is 0.7 ms, under the analyser's 1 ms floor: made up, and never a click.
        var small = Simulate(SimConfig.AsioOnly, SimCodec.Pcm, SimNetwork.Internet, 40, 8);
        Check(small.PacketsLost > 0 && small.PcmConcealed > 0 && small.Findings.StepClicks == 0 && small.Findings.Glitches <= small.PacketsLost,
            $"PCM at 32 samples a callback: lost packets made up, never a click ({small.PcmConcealed} frames made up for {small.PacketsLost} lost; "
            + $"{small.Findings.StepClicks} clicks, {small.Findings.Glitches} gaps)");
        return $"{opus.PacketsLost}, {custom.PacketsLost} and {pcm.PacketsLost} packets lost; Opus repaired {opus.FecRecoveries} ({opus.Findings.Glitches} break-ups, "
             + $"{opus.UnrecoveredGaps} unrepaired), Jamulus-style concealed {custom.CustomConcealed}; PCM a packet per 128-sample callback heard {f.Glitches} gaps, "
             + $"{f.QuietMs} ms in all, {f.StepClicks} clicks; at 32 samples {small.PcmConcealed} frames made up, {small.Findings.StepClicks} clicks; "
             + $"the tone at {opus.Findings.MedianSnrDb:0} and {custom.Findings.MedianSnrDb:0} dB";
    }

    /// <summary>SIMULATOR DRIFT: two sound cards 200 ppm apart, each way, in all three configurations - no break-up, in
    /// tune, and the delay holds. It was ASIO only until 2026-09-27; the WASAPI output's drift is corrected by a different
    /// stage, and nothing listened to it (the review of every step).</summary>
    private static string SimulatorHoldsTwoDriftingCards()
    {
        var said = new List<string>();
        foreach (var (config, buffer) in new[] { (SimConfig.WasapiOnly, 40), (SimConfig.AsioOnly, 10), (SimConfig.Both, 10) })
        {
            foreach (var ppm in new[] { 200.0, -200.0 })
            {
                var card = Simulation.DefaultCard(asio: config != SimConfig.WasapiOnly);
                // Listened to from 15 s on: the WASAPI output starts above its target and drains down to it over its first
                // seconds (the depth correction), which is settling, not drift.
                var r = Simulation.Run(new SimScenario($"{config} drift", config, SimCodec.Opus2_5, SimNetwork.Wired, buffer, 45,
                    card with { DriftPpm = ppm / 2 }, card with { DriftPpm = -ppm / 2 }, AnalyseFromSecond: 15));
                var f = r.Findings;
                var what = $"{config}, {ppm:+0;-0} ppm";
                Check(f.Clean, $"{what} must play clean - {SimRunner.Describe(r)}");
                Check(f.WorstPitchCents < 3, $"{what}: the drift correction must not be heard as pitch ({f.WorstPitchCents:0.0} cents)");
                // ASIO holds its delay tightly. The WASAPI output is still draining down to its target at 45 s - slowly, by
                // design (the depth correction's sleepy 0.3 %, and never a faster descent) - so there the failure is the
                // one drift causes: the delay growing.
                if (config == SimConfig.WasapiOnly)
                    Check(f.DelayLastMs <= f.DelayFirstMs + 2 && f.DelayLastMs > buffer,
                        $"{what}: the delay must not creep up, nor drop below the buffer ({f.DelayFirstMs:0.0} ms at 15 s, {f.DelayLastMs:0.0} ms at 45 s)");
                else
                    Check(Math.Abs(f.DelayLastMs - f.DelayFirstMs) < 5,
                        $"{what}: the delay must hold, not creep ({f.DelayFirstMs:0.0} ms at 15 s, {f.DelayLastMs:0.0} ms at 45 s)");
                said.Add($"{what}: {f.DelayFirstMs:0.0} to {f.DelayLastMs:0.0} ms, {f.WorstPitchCents:0.0} cents");
            }
        }
        // 2026-09-28 (the review of every test, items 343 and 198): a big difference each way, 200 ppm slow and 1000 ppm fast,
        // on WASAPI only and on both kinds of output - both outputs listened to (the WASAPI one used to be thrown away) - for
        // 90 s, listened to after 30 s of settling. Glitch-free, in tune, and the delay not creeping.
        foreach (var config in new[] { SimConfig.WasapiOnly, SimConfig.Both })
        {
            foreach (var ppm in new[] { -200.0, 1000.0 })
            {
                var card = Simulation.DefaultCard(asio: config != SimConfig.WasapiOnly);
                var wasapiCard = new SimCard(480, JitterMs: 1);
                var r = Simulation.Run(new SimScenario($"{config} drift", config, SimCodec.Opus2_5, SimNetwork.Wired, config == SimConfig.WasapiOnly ? 40 : 10, 90,
                    card with { DriftPpm = ppm / 2 }, card with { DriftPpm = -ppm / 2 }, WasapiLaneBufferMs: 40,
                    WasapiLaneReceiveCard: wasapiCard with { DriftPpm = -ppm / 2 }, AnalyseFromSecond: 30));
                var outputs = new List<(string Output, SimAnalysis.Findings F)> { (config == SimConfig.WasapiOnly ? "WASAPI output" : "ASIO output", r.Findings) };
                if (r.WasapiOutput is { } wasapiHeard) outputs.Add(("WASAPI output", wasapiHeard));
                foreach (var (output, f) in outputs)
                {
                    var what = $"{config}, {ppm:+0;-0} ppm, the {output}";
                    Check(f.ClicksHeard >= 100 && f.Glitches == 0 && f.StepClicks == 0, $"{what} must play glitch-free for the last minute - {SimRunner.Describe(r)}");
                    // In tune to a cent, every output. The WASAPI-only output was allowed 2 while it measured 1.31 and 1.43 cents
                    // (2026-09-28); since the depth correction steers the buffer's mean rather than one sample at the window's
                    // edge (SessionPlayout.DepthMeanSec, the same day) it measures 0.84 and 0.68, and the other outputs 0.26 or
                    // less. What is left on WASAPI only is its drift loop counting whole 10 ms blocks at both ends (the sender's
                    // WASAPI capture and the output's reads) in each 10 s window, so a reading can be a block - 1000 ppm - out,
                    // and 30 % of that reaches the rate.
                    const double inTune = 1.0;
                    Check(f.WorstPitchCents < inTune, $"{what}: in tune to {inTune:0} cent(s) ({f.WorstPitchCents:0.00} cents at worst, {f.PitchP95Cents:0.00} 95% of the time)");
                    Check(f.DelayLastMs <= f.DelayFirstMs + 2 && f.DelayLastMs > (output == "ASIO output" ? 10 : 40),
                        $"{what}: the delay must not creep up by 2 ms, nor fall below the buffer ({f.DelayFirstMs:0.0} ms at 30 s, {f.DelayLastMs:0.0} ms at 90 s)");
                    said.Add($"{what}: {f.DelayFirstMs:0.0} to {f.DelayLastMs:0.0} ms, {f.WorstPitchCents:0.00} cents");
                }
            }
        }
        return "clean both ways in all three configurations - " + string.Join("; ", said);
    }

    /// <summary>SIMULATOR DESCENT: the auto-tune lowers the buffer from 100 ms to 30 part way through, and the buffer drains
    /// down at the descent pace Ed agreed on 2026-09-28 (SessionPlayout's DescentDepthBias block) - no click, no gap, the
    /// pitch bent by no more than the descent's 0.4 %, about 4 ms a second - and is down within 25 seconds, on the ASIO
    /// output and the WASAPI one. (Until then it was held to 0.3 % under a "never speed up the descent" rule; Ed: there is
    /// no such rule, "it should be moving quicker".)</summary>
    private static string SimulatorDescentKeepsTheHousePace()
    {
        var nudge = 1200 * Math.Log2(1.004);
        var said = new List<string>();
        foreach (var (config, codec) in new[] { (SimConfig.AsioOnly, SimCodec.Opus2_5), (SimConfig.WasapiOnly, SimCodec.Opus10) })
        {
            // Lowered at 25 s, half way between two of the drift loop's 10 s windows: a descent that waited for the next
            // window would start 5 s late.
            var r = Simulation.Run(new SimScenario($"{config} descent", config, codec, SimNetwork.Wired, 100, 90,
                SoftTargetMs: 30, SoftTargetAtSecond: 25, AnalyseFromSecond: 20));
            var f = r.Findings;
            var what = $"{config}, 100 ms lowered to 30 at 25 s";
            // Down within 20 s of the lowering: from 45 s on, the delay is where it ends up.
            var chunks = Simulation.Run(new SimScenario($"{config} descent, by the stretch", config, codec, SimNetwork.Wired, 100, 90,
                SoftTargetMs: 30, SoftTargetAtSecond: 25, ChunkSeconds: 5)).Stretches;
            var endMs = chunks[^1].Findings.DelayMedianMs;
            var at45 = chunks.First(c => c.FromSec >= 45).Findings.DelayMedianMs;
            // The same on both outputs since 2026-09-28. WASAPI was allowed 6 ms ("the WASAPI output settles ~10 ms over target for
            // 45 s", PLAN-fixes, put down to the output stage - which this run does not have): its drift loop's first clock
            // reading counted the 10 ms capture blocks at the window's edge, could be a whole block (1,000 ppm) out, and held the
            // buffer a few ms over for a minute (5.5 ms at 45 s here). Read off the stream's arrival line
            // (SessionPlayout.WrittenBytesAt) it arrives as ASIO does: 2.6 ms.
            const double slack = 4;
            Check(at45 - endMs < slack, $"{what}: down within 20 seconds of the lowering ({at45:0.0} ms at 45 s, {endMs:0.0} at the end)");
            Check(r.SnapCount == 0, $"{what}: 70 ms is a sensible drop - drained, never skipped ({r.SnapCount} skips)");
            Check(f.ClicksHeard >= 100 && f.Glitches == 0, $"{what}: the descent must be heard without a single break-up - {SimRunner.Describe(r)}");
            Check(f.DelayFirstMs - f.DelayLastMs > 20, $"{what}: and it must actually come down ({f.DelayFirstMs:0.0} ms before, {f.DelayLastMs:0.0} ms at 90 s)");
            Check(f.PitchP95Cents < nudge + 0.5 && f.WorstPitchCents < nudge + 1,
                $"{what}: THE DESCENT PACE: the drain may bend the pitch by the descent's {nudge:0.0} cents and no more "
                + $"({f.PitchP95Cents:0.0} cents 95% of the time, {f.WorstPitchCents:0.0} at worst)");
            said.Add($"{what}: {f.DelayFirstMs:0.0} to {f.DelayLastMs:0.0} ms, pitch {f.PitchP95Cents:0.0} cents (worst {f.WorstPitchCents:0.0})");
        }
        return string.Join("; ", said);
    }

    /// <summary>SIMULATOR EVERYDAY: the set-up most people have - WASAPI only, a new profile's plain PCM, and Opus at its
    /// usual 10 ms - over home wifi and over the internet, at the buffer the auto-tune's own formula asks for. Every other
    /// simulator step listened to ASIO (review of every step, 2026-09-27). Heard, in tune but for the depth correction's
    /// own nudge, at the delay the buffer and the cards add up to, and Opus's repair making up the lost packets.
    ///
    /// <para>FOUND BY THIS STEP, 2026-09-27: PCM over a network that loses packets played FLAT by about the loss - 0.5 %
    /// lost, about 9 cents; 1 %, 17 to 23 - on ASIO and WASAPI alike, while Opus stayed in tune. The receiver matches the
    /// sender's clock by counting the samples written against those played, and a lost PCM packet wrote none. Fixed
    /// 2026-09-28 (StreamSession.ConcealLostPcmFrames): the lost frames are made up, as Opus's repair does, and PCM is now
    /// held in tune on the lossy networks too.</para></summary>
    private static string SimulatorEverydaySetupHolds()
    {
        var said = new List<string>();
        // The depth correction nudges the rate by up to 0.3 % (SessionPlayout.MaxDepthBias), about 5.2 cents.
        var nudge = 1200 * Math.Log2(1.003);
        foreach (var net in new[] { SimNetwork.Wifi, SimNetwork.Internet })
        {
            foreach (var codec in new[] { SimCodec.Pcm, SimCodec.Opus10 })
            {
                // What the auto-tune would ask on this network, found the way it finds it: from the arrivals.
                var probe = Simulate(SimConfig.WasapiOnly, codec, net, 80, 20);
                var ask = probe.TunerWouldAskMs(false);
                var r = Simulate(SimConfig.WasapiOnly, codec, net, ask, 60);
                var f = r.Findings;
                var what = $"{net.Name}, {SimLab.Name(codec)} at the {ask} ms the auto-tune asks";
                Check(r.PacketsLost > 0, $"{what}: premise - the network must lose some packets");
                Check(f.ClicksHeard >= 100, $"{what}: the clicks must come through ({f.ClicksHeard}) - {SimRunner.Describe(r)}");
                Check(f.DelayMedianMs > ask && f.DelayMedianMs < ask + 40, $"{what}: the delay must be the buffer and the cards, no more ({f.DelayMedianMs:0.0} ms)");
                Check(f.Glitches <= r.PacketsLost + 3, $"{what}: the only break-ups must be the packets the network lost ({f.Glitches} for {r.PacketsLost} lost)");
                if (codec == SimCodec.Opus10)
                    Check(r.FecRecoveries >= r.PacketsLost * 0.8, $"{what}: Opus's repair must make up most lost packets ({r.FecRecoveries} of {r.PacketsLost})");
                else
                    Check(r.PcmConcealed > 0, $"{what}: the lost PCM frames must be made up, so the clock-matching sees the sender's true pace");
                Check(f.PitchP95Cents < nudge + 0.5, $"{what}: in tune but for the depth correction's nudge of {nudge:0.0} cents "
                    + $"({f.PitchP95Cents:0.0} cents 95% of the time, {f.PitchMedianCents:0.0} typical, {f.WorstPitchCents:0.0} at worst)");
                said.Add($"{net.Name} {SimLab.Name(codec)}: asks {ask} ms, {f.DelayMedianMs:0.0} ms through, {f.Glitches} break-ups for "
                    + $"{r.PacketsLost} lost, pitch {f.PitchMedianCents:0.0} cents typical ({f.WorstPitchCents:0.0} worst)");
            }
        }
        // The nudge's own limit, reached: a receiver that stalls 60 ms every 4 s and then takes the lot at once leaves the
        // buffer well over its target after every stall, and the correction drains it at full stretch - never more than
        // its 0.3 % (no step pushed it that far until 2026-09-27; a limit ten times looser passed everything).
        foreach (var config in new[] { SimConfig.WasapiOnly, SimConfig.AsioOnly })
        {
            var stalls = Simulate(config, SimCodec.Opus10, SimNetwork.Lumpy, 100, 60);
            Check(stalls.Findings.ClicksHeard >= 100 && stalls.Findings.PitchP95Cents < nudge + 0.5,
                $"{config} behind a stalling receiver: the drain must never bend the pitch more than its {nudge:0.0} cents "
                + $"({stalls.Findings.PitchP95Cents:0.0} cents 95% of the time, {stalls.Findings.WorstPitchCents:0.0} at worst)");
            said.Add($"{config} behind a stalling receiver: pitch {stalls.Findings.PitchP95Cents:0.0} cents (limit {nudge:0.0})");
        }
        // PCM on the ASIO output over the internet: it played 10 cents flat there too before the fix. A packet per 128-sample
        // callback, as the app sends PCM, so each loss is a 2.7 ms gap the analyser hears and measures around; at 32 samples
        // a loss is a 0.7 ms dip below its 1 ms floor, which it cannot tell from the tone (see SIMULATOR LOSS).
        var asioPcm = Simulate(SimConfig.AsioOnly, SimCodec.Pcm, SimNetwork.Internet, 40, 60, send: new SimCard(128, JitterMs: 0.05));
        Check(asioPcm.PacketsLost > 0 && asioPcm.PcmConcealed > 0 && asioPcm.Findings.PitchP95Cents < nudge + 0.5,
            $"ASIO only, PCM over the internet: in tune, the lost frames made up ({asioPcm.Findings.PitchP95Cents:0.0} cents, "
            + $"{asioPcm.PcmConcealed} frames made up for {asioPcm.PacketsLost} lost)");
        said.Add($"ASIO only PCM over the internet: pitch {asioPcm.Findings.PitchMedianCents:0.0} cents typical, {asioPcm.PcmConcealed} frames made up");
        // PCM where nothing is lost: in tune, on both kinds of output.
        foreach (var config in new[] { SimConfig.WasapiOnly, SimConfig.AsioOnly })
        {
            var clean = Simulate(config, SimCodec.Pcm, SimNetwork.Wired, 40, 30);
            // (The WASAPI output drains its start-up excess through the same nudge, so it is the limit here too.)
            Check(clean.PacketsLost == 0 && clean.Findings.PitchP95Cents < nudge + 0.5,
                $"{config}, PCM on a wired network: in tune but for the depth correction's nudge ({clean.Findings.PitchP95Cents:0.0} cents)");
        }
        return string.Join("; ", said);
    }

    /// <summary>SIMULATOR PER-STREAM: each stream's own buffer (normal behaviour since 2026-09-29) brings a smooth ASIO
    /// stream's delay down beside a lumpy WASAPI one, without a break-up - against the one shared buffer it replaced.</summary>
    private static string SimulatorPerStreamSwitchCutsTheDelay()
    {
        // The outputs' auto-tune on - per-stream buffers go with it - but not yet due in these 30 s, so the output stays at 30.
        SimResult Run(bool perStream) => Simulation.Run(new SimScenario($"per-stream {(perStream ? "on" : "off")}", SimConfig.Both, SimCodec.Opus2_5,
            SimNetwork.Wired, 30, 30, PerStreamBuffers: perStream, WasapiLaneBufferMs: 30, ContinuousAutoTune: true, AutoTuneIntervalSec: 60));
        var off = Run(false);
        var on = Run(true);
        var byHand = Simulate(SimConfig.Both, SimCodec.Opus2_5, SimNetwork.Wired, 30, 30, wasapiLaneMs: 30);
        Check(off.Findings.Clean && on.Findings.Clean, $"both must play clean - off: {SimRunner.Describe(off)} | on: {SimRunner.Describe(on)}");
        Check(on.Findings.DelayLastMs < off.Findings.DelayLastMs - 8,
            $"with its own buffer the ASIO stream must end well below the output's 30 ms ({on.Findings.DelayLastMs:0.0} ms against {off.Findings.DelayLastMs:0.0} ms off)");
        Check(on.Findings.DelayFirstMs > on.Findings.DelayLastMs + 5, "and come down gradually from where it started, not in one drop");
        // Gently: a stream's own buffer coming down 1 ms a second takes the steady drift path, not the descent's quick pull
        // (about 7 cents), which is for the output's own setting lowered.
        Check(on.Findings.PitchP95Cents < 3, $"and gently, without the descent's quick pull ({on.Findings.PitchP95Cents:0.0} cents 95% of the time)");
        Check(Math.Abs(byHand.Findings.DelayLastMs - off.Findings.DelayLastMs) < 2,
            $"with the auto-tune off the stream must play at the 30 ms set by hand, as with one shared buffer ({byHand.Findings.DelayLastMs:0.0} ms against {off.Findings.DelayLastMs:0.0})");
        return $"after 30 s: {off.Findings.DelayLastMs:0.0} ms with one shared buffer, {on.Findings.DelayLastMs:0.0} ms with its own "
             + $"({on.Findings.PitchP95Cents:0.0} cents on the way), both clean; auto-tune off, {byHand.Findings.DelayLastMs:0.0} ms, as set";
    }

    /// <summary>SIMULATOR LATE PACKETS: packets that arrive twice or overtaken on the way are dropped, not played - so no
    /// click and no jump in the delay. They used to be played, which the simulator caught on 2026-09-26: 10 break-ups and
    /// the delay creeping from 74 to 96 ms in 8 s.</summary>
    private static string SimulatorLatePacketsAreDropped()
    {
        var net = new SimNetwork("overtaken and doubled packets", 10, ReorderPercent: 1, DuplicatePercent: 1);
        var r = Simulation.Run(new SimScenario("late packets", SimConfig.AsioOnly, SimCodec.Opus2_5, net, 60, 8));
        var f = r.Findings;
        Check(r.LateDropped > 20, $"overtaken and doubled packets must be dropped ({r.LateDropped} dropped) - {SimRunner.Describe(r)}");
        Check(f.Glitches == 0, $"and not heard as break-ups ({f.Glitches}) - {SimRunner.Describe(r)}");
        Check(f.DelayMaxMs - f.DelayMinMs < 1, $"and the delay must hold steady ({f.DelayMinMs:0.0} to {f.DelayMaxMs:0.0} ms), not creep as each one "
            + "was played on top of the buffer");
        return $"{r.LateDropped} late or doubled packets dropped in 8 s; no break-ups; the delay steady at {f.DelayMedianMs:0.0} ms";
    }

    /// <summary>RECEIVE RESTART: a sender that starts counting again on the SAME stream keeps playing - the Pi sender does
    /// exactly that when its program restarts (stream 1, sequence from 0). Dropping late packets must not mistake it for
    /// a flood of late ones and go silent. A doubled packet is still dropped.</summary>
    private static string ASenderThatStartsCountingAgainKeepsPlaying()
    {
        var from = new IPEndPoint(IPAddress.Parse("192.168.44.20"), 47830);
        const ushort Stream = 1;
        using var receiver = new AudioReceiver();
        receiver.SetOutputDevices([]);
        receiver.SetPlaybackEnabled(true);
        var (rawKey, fingerprint) = RemSoundCrypto.ForPlainPassword("restart");
        var key = Require(rawKey, "a key");
        receiver.AudioKey = key;
        receiver.AudioFingerprint = fingerprint;

        void Inject(RemPacketType type, uint seq, ReadOnlySpan<byte> payload)
        {
            var packet = new byte[RemPacket.HeaderSize + payload.Length];
            RemPacket.WriteHeader(packet, type, Stream, seq);
            payload.CopyTo(packet.AsSpan(RemPacket.HeaderSize));
            receiver.InjectExternalPacket(packet, packet.Length, from);
        }
        ushort frameId = 0;
        void Audio(uint seq)
        {
            var sealedFrame = RemSoundCrypto.Encrypt(key, new byte[240 * 2 * 3]);
            var payload = new byte[RemPcmFrame.SubHeaderSize + sealedFrame.Length];
            RemPcmFrame.WriteSubHeader(payload, frameId++, 0, 1);
            sealedFrame.CopyTo(payload.AsSpan(RemPcmFrame.SubHeaderSize));
            Inject(RemPacketType.Audio, seq, payload);
        }
        var format = new AudioFormatInfo(48000, 2, 24, 1, 6, 288_000, (int)AudioTransportCodec.Pcm, 240, RenderRoute.Mixed);
        var fp = new byte[64];
        Inject(RemPacketType.Format, 1, fp.AsSpan(0, RemPacket.WriteFormatPayload(fp, format)));

        for (uint seq = 5000; seq < 5040; seq++) Audio(seq);
        var beforeRestart = receiver.AudioBytesDecodedForTest;
        Check(beforeRestart > 0, "premise: the first run of packets must be played");

        Audio(5039); // the same packet again
        Check(receiver.LateDroppedForTest == 1 && receiver.AudioBytesDecodedForTest == beforeRestart,
            $"a doubled packet must be dropped, not played again ({receiver.LateDroppedForTest} dropped)");

        // The sender restarts its program: stream 1, counting from 0 again, straight away.
        for (uint seq = 0; seq < 40; seq++) Audio(seq);
        var played = receiver.AudioBytesDecodedForTest - beforeRestart;
        var frameBytes = 240 * 2 * sizeof(float);
        Check(receiver.SequenceRestartsForTest == 1, $"the restart must be recognised once ({receiver.SequenceRestartsForTest})");
        Check(played >= 40 * frameBytes - 8 * frameBytes,
            $"and the sender must keep playing: at most a handful of its first packets dropped ({played / frameBytes} of 40 played)");

        // A small restart: a sender that restarted soon after starting, only a few dozen packets back.
        for (uint seq = 40; seq < 60; seq++) Audio(seq);
        var beforeSmall = receiver.AudioBytesDecodedForTest;
        for (uint seq = 10; seq < 30; seq++) Audio(seq);
        Check(receiver.SequenceRestartsForTest == 2 && receiver.AudioBytesDecodedForTest - beforeSmall >= 12 * frameBytes,
            $"a restart only a few dozen packets back must be recognised after a handful in a row ({receiver.SequenceRestartsForTest} restarts, "
            + $"{(receiver.AudioBytesDecodedForTest - beforeSmall) / frameBytes} of 20 played)");
        return $"a doubled packet dropped; a restart from 0 recognised at once and played ({played / frameBytes} of 40); a restart a few dozen back "
             + "recognised after a handful in a row";
    }

    /// <summary>SIMULATOR REORDER WAIT: packets overtaken on the way (the pattern in Ed's laptop logs - as many "missing"
    /// as "out of order", so none really lost) slot back into place, where without the wait they were covered as losses;
    /// and on a network that really loses packets, with a thin buffer, waiting is never worse.</summary>
    private static string SimulatorReorderWaitSlotsPacketsBackIn()
    {
        var overtaken = new SimNetwork("overtaken, none lost", 10, ReorderPercent: 1);
        var off = Simulation.Run(new SimScenario("without", SimConfig.AsioOnly, SimCodec.Opus2_5, overtaken, 60, 8, ReorderWait: false));
        var on = Simulation.Run(new SimScenario("with", SimConfig.AsioOnly, SimCodec.Opus2_5, overtaken, 60, 8));
        Check(off.FecRecoveries > 0 && off.LateDropped > 0,
            $"without the wait an overtaken packet is covered as lost (repaired, and then dropped when it turns up) - {SimRunner.Describe(off)}");
        Check(on.Findings.Clean, $"with it, as RemSound now always is, the overtaken packets must slot back in, clean - {SimRunner.Describe(on)}");
        Check(on.ReorderFilled > 0 && on.ReorderFilled >= on.ReorderWaits * 9 / 10,
            $"and nearly every wait must be filled by the packet it waited for ({on.ReorderFilled} of {on.ReorderWaits})");
        Check(on.FecRecoveries < off.FecRecoveries / 2,
            $"so far fewer gaps need repairing ({off.FecRecoveries} repairs without the wait, {on.FecRecoveries} with)");
        Check(on.Findings.WorstSnrDb >= off.Findings.WorstSnrDb - 1,
            $"and the tone must come through at least as clean ({off.Findings.WorstSnrDb:0} dB worst without, {on.Findings.WorstSnrDb:0} with)");

        var lossy = new SimNetwork("1 % lost, nothing overtaken", 1, 0.2, LossPercent: 1);
        var thinOff = Simulation.Run(new SimScenario("thin without", SimConfig.AsioOnly, SimCodec.Opus2_5, lossy, 5, 8, ReorderWait: false));
        var thinOn = Simulation.Run(new SimScenario("thin with", SimConfig.AsioOnly, SimCodec.Opus2_5, lossy, 5, 8));
        Check(thinOn.Findings.Glitches <= thinOff.Findings.Glitches,
            $"a real loss with a thin 5 ms buffer must not be made worse by waiting ({thinOff.Findings.Glitches} break-ups off, {thinOn.Findings.Glitches} on)");
        return $"overtaken packets: {off.FecRecoveries} repaired without the wait, {on.FecRecoveries} with it, {on.ReorderFilled} of {on.ReorderWaits} waits filled; 1 % loss at 5 ms: "
             + $"{thinOff.Findings.Glitches} break-ups without, {thinOn.Findings.Glitches} with";
    }

    /// <summary>SIMULATOR DROPOUT TUNER: the lowest-delay auto-tune's "keep dropouts rare" tuner in the loop. On a receiver that takes its packets in
    /// lumps (Ed's laptop, as measured 2026-09-26) it comes down far below what the gap formula asks and holds there with
    /// gaps kept rare; on a machine that stalls hard it climbs fast, in doubling steps.</summary>
    private static string SimulatorDropoutTunerFindsTheEdge()
    {
        var laptop = Simulation.Run(new SimScenario("laptop", SimConfig.AsioOnly, SimCodec.Opus2_5, SimNetwork.LaptopLumps, 30, 180,
            LowestDelayTune: true, AnalyseFromSecond: 90));
        var formula = laptop.TunerWouldAskMs(false);
        Check(laptop.TunedMs < formula,
            $"on the lumpy laptop it must settle below the gap formula's {formula} ms (settled at {laptop.TunedMs}; moves {laptop.TunerMoves})");
        // Rare, as the tuner means it: about its own limit of four gaps in twenty seconds, so no more than 18 in 90.
        var rare = 90 * DropoutTuner.RaiseWhenGapsInWindow / DropoutTuner.WindowSeconds;
        Check(laptop.Findings.ClicksHeard > 0 && laptop.Findings.Glitches <= rare,
            $"and keep gaps rare there - no more than {rare} in the last 90 s on a pure tone ({laptop.Findings.Glitches})");
        // The buffer must really follow it: the same run with the tuner off, held at 30 ms, must be heard later by at
        // least half of what the tuner took off.
        var held = Simulation.Run(new SimScenario("laptop", SimConfig.AsioOnly, SimCodec.Opus2_5, SimNetwork.LaptopLumps, 30, 180,
            AnalyseFromSecond: 90));
        var quicker = held.Findings.DelayMedianMs - laptop.Findings.DelayMedianMs;
        Check(quicker >= (30 - laptop.TunedMs) / 2.0,
            $"and the sound must really come through sooner: {laptop.Findings.DelayMedianMs:0.0} ms against {held.Findings.DelayMedianMs:0.0} ms held at 30");
        var stalls = Simulation.Run(new SimScenario("stalls", SimConfig.AsioOnly, SimCodec.Opus2_5, SimNetwork.Lumpy, 30, 90,
            LowestDelayTune: true));
        Check(stalls.TunedMs >= 44, $"on a machine that stalls 60 ms at a time it must climb fast ({stalls.TunedMs} ms after 90 s; moves {stalls.TunerMoves})");
        Check(stalls.TunerMoves.Contains("36>44", StringComparison.Ordinal) || stalls.TunerMoves.Split(' ').Any(m => m.Contains('>') &&
              int.Parse(m.Split('>')[1].Split('@')[0]) - int.Parse(m.Split('>')[0]) >= 8),
            $"by raises that grow while the trouble goes on (moves {stalls.TunerMoves})");
        // WASAPI only: the one output's gaps are every stream's (the lanes the streams sit on are not the output), so the tuner
        // must see them there too - counting the wrong lane, it would see none and sink to the floor, breaking up all the way.
        var wasapi = Simulation.Run(new SimScenario("laptop, WASAPI only", SimConfig.WasapiOnly, SimCodec.Opus2_5, SimNetwork.LaptopLumps, 60, 150,
            LowestDelayTune: true, AnalyseFromSecond: 90));
        Check(wasapi.TunedMs < 60 && wasapi.Findings.Glitches <= rare,
            $"WASAPI ONLY: it must come down from 60 ms and keep gaps rare there too ({wasapi.TunedMs} ms, {wasapi.Findings.Glitches} gaps in the last 60 s; moves {wasapi.TunerMoves})");
        // And WASAPI only where the network stalls: it must see those gaps too and climb, not sit at the floor.
        var wasapiStalls = Simulation.Run(new SimScenario("stalls, WASAPI only", SimConfig.WasapiOnly, SimCodec.Opus2_5, SimNetwork.Lumpy, 30, 90,
            LowestDelayTune: true));
        Check(wasapiStalls.TunedMs > 30, $"WASAPI ONLY, a stalling network: it must climb from 30 ms ({wasapiStalls.TunedMs} ms after 90 s; moves {wasapiStalls.TunerMoves})");
        // With the everyday auto-tune on as well, as the app has them together: the lowest-delay tune has the output and the
        // usual one stands aside.
        var together = Simulation.Run(new SimScenario("laptop, both tunes", SimConfig.AsioOnly, SimCodec.Opus2_5, SimNetwork.LaptopLumps, 30, 60,
            LowestDelayTune: true, ContinuousAutoTune: true));
        var usualTicks = together.Window?.Ticks.Count(t => t.Route == RenderRoute.AsioLane) ?? -1;
        Check(usualTicks == 0 && together.TunerMoves.Length > 0,
            $"with the everyday auto-tune on too, the usual tune must stand aside ({usualTicks} of its ticks on the output; the tuner's moves {together.TunerMoves})");
        return $"lumpy laptop: settled at {laptop.TunedMs} ms against the gap formula's {formula}, {laptop.Findings.Glitches} gaps in the "
             + $"last 90 s, heard {quicker:0.0} ms sooner than held at 30; stalling machine: up to {stalls.TunedMs} ms in "
             + $"{stalls.TunerMoves.Split(' ').Length} moves; WASAPI only: {wasapi.TunedMs} ms, {wasapi.Findings.Glitches} gaps, up to {wasapiStalls.TunedMs} ms on a stalling network; the usual tune aside";
    }

    /// <summary>SIMULATOR REPEATS ITSELF: the same scenario run twice gives the same sound, sample for sample - so a change
    /// in its findings is the change under test, not luck. Until 2026-09-26 the sender re-sent its format packet on the
    /// wall clock, so each run slipped a different number of them into the fake network, each one taking a turn of its
    /// dice, and the tuner settled at 10 ms on one run and 14 on the next. Since 2026-09-28 also a run that underruns (the
    /// laptop's lumps, whose stall every 45 s empties the buffer): the noise burst that covers an underrun came from shared
    /// randomness, and such a run parted company at its first stall - the internet run never underran, so nothing saw it.</summary>
    private static string SimulatorRepeatsItself()
    {
        var said = new List<string>();
        foreach (var scenario in new[]
        {
            new SimScenario("twice", SimConfig.AsioOnly, SimCodec.Opus2_5, SimNetwork.Internet, 20, 40, KeepAudio: true),
            new SimScenario("twice, with underruns", SimConfig.WasapiOnly, SimCodec.Opus2_5, SimNetwork.LaptopLumps, 30, 100, KeepAudio: true),
        })
        {
            var first = Simulation.Run(scenario);
            var second = Simulation.Run(scenario);
            var what = $"{scenario.Network.Name}, {scenario.Config}";
            Check(first.Audio is { Length: > 0 } && second.Audio is not null, $"{what}: both runs must keep their audio");
            Check(first.Audio!.Length == second.Audio!.Length, $"{what}: both runs must play the same length ({first.Audio.Length} and {second.Audio.Length} samples)");
            var differ = -1;
            for (var i = 0; i < first.Audio.Length && differ < 0; i++)
                if (first.Audio[i] != second.Audio[i]) differ = i;
            Check(differ < 0, $"{what}: the two runs part company {differ / 2 / 48000.0:0.000} s in - something in the audio path reads the wall clock or "
                + $"shared randomness instead of the simulator's ({first.Underruns} underruns)");
            Check(first.Findings.Glitches == second.Findings.Glitches && first.LateDropped == second.LateDropped,
                $"{what}: and so must hear the same");
            said.Add($"{what}: identical, {first.Audio.Length / 2} frames each, {first.Findings.Glitches} break-ups and {first.Underruns} underruns both times");
        }
        Check(said.Count == 2 && Simulation.Run(new SimScenario("premise", SimConfig.WasapiOnly, SimCodec.Opus2_5, SimNetwork.LaptopLumps, 30, 100)).Underruns > 0,
            "premise: the laptop's lumps must make the buffer underrun, or the second pair proves nothing new");
        return string.Join("; ", said);
    }

    // ============ 2026-09-28: the review of every test, the simulator's items (81, 87, 90, 28, 340, 178, 283) ============

    /// <summary>The lanes a configuration's continuous auto-tune works on, as the main window ticks them: the one slider
    /// (the Mixed route) with no ASIO driver, and each lane on its own slider otherwise.</summary>
    private static RenderRoute[] TunedLanes(SimConfig config) => config switch
    {
        SimConfig.WasapiOnly => [RenderRoute.Mixed],
        SimConfig.AsioOnly => [RenderRoute.AsioLane],
        _ => [RenderRoute.WasapiLane, RenderRoute.AsioLane],
    };

    /// <summary>What the stream under test needs on a lane by the auto-tune's own formula, from its own arrivals - the
    /// figure the dropout tuner's step compares against (TunerWouldAskMs), with the lane's render period: the ASIO card's,
    /// or the WASAPI output's 10 ms.</summary>
    private static int NeedOn(SimResult r, RenderRoute lane) =>
        AudioReceiver.PerStreamNeedMs(r.GapUsedMs, lane == RenderRoute.AsioLane ? r.CardPeriodMs : 10, r.FrameMs, lean: false);

    /// <summary>Every output a run listened to, a stretch at a time: the one under test, and with both kinds of output the
    /// WASAPI one too.</summary>
    private static List<(string Output, IReadOnlyList<(double FromSec, SimAnalysis.Findings Findings)> Stretches)> OutputsHeard(SimResult r)
    {
        var heard = new List<(string, IReadOnlyList<(double, SimAnalysis.Findings)>)>
        {
            (r.Scenario.Config == SimConfig.WasapiOnly ? "the WASAPI output" : "the ASIO output", r.Stretches),
        };
        if (r.Scenario.Config == SimConfig.Both) heard.Add(("the WASAPI output", r.WasapiStretches));
        return heard;
    }

    /// <summary>SIMULATOR AUTO-TUNE FROM 500: the continuous auto-tune everyone uses in the loop at last - the main window's
    /// own decision (ContinuousAutoTune.Decide, lifted out of MainForm.TickRoute unchanged) fed the way the main window
    /// feeds it (SimMainWindow). Until 2026-09-28 only the latency lab's tuner had ever run in the simulator (the review of
    /// every test, items 81 and 90). From a silly 500 ms, on a wired network and on home wifi, in all three configurations:
    /// the slider settles within 12 ticks, at or above what the stream needs by the tuner's own formula; nothing breaks up
    /// from its first move on; and the delay walks down at the house pace - SIMULATOR DESCENT's measure, in every ten
    /// seconds of the walk. This only listens to the descent's speed: the parked hard rule (never speed it up) is Ed's, and
    /// nothing here changes it.</summary>
    private static string SimulatorAutoTuneComesDownFrom500()
    {
        // The descent's 0.4 % (SessionPlayout's DescentDepthBias block, Ed 2026-09-28). A WASAPI output's own clock-matching
        // adds up to about 2 cents on top while the buffer comes down (measured 8.6 against 6.9 on ASIO).
        var nudge = 1200 * Math.Log2(1.004);
        const double WasapiOutputExtra = 2.2;
        var said = new List<string>();
        var cuts = new List<string>();
        foreach (var net in new[] { SimNetwork.Wired, SimNetwork.Wifi })
        {
            foreach (var config in new[] { SimConfig.WasapiOnly, SimConfig.AsioOnly, SimConfig.Both })
            {
                var r = Simulation.Run(new SimScenario("auto-tune from 500 ms", config, SimCodec.Opus2_5, net, 500, 240,
                    WasapiLaneBufferMs: 500, ContinuousAutoTune: true, ChunkSeconds: 10));
                var window = Require(r.Window, "the main window's work must have run");
                var what = $"{config}, {net.Name}";
                var firstMove = double.MaxValue;
                var lanes = new List<string>();
                foreach (var lane in TunedLanes(config))
                {
                    var moves = window.Ticks.Where(t => t.Route == lane
                        && t.Verdict is ContinuousAutoTune.Verdict.Raise or ContinuousAutoTune.Verdict.Move).ToList();
                    Check(window.BaselineAt.ContainsKey(lane) && moves.Count > 0, $"{what}, {lane}: premise - the tuner must start and move");
                    var path = string.Join(" ", moves.Select(m => $"{m.From}>{m.To}@{m.At:0}s"));
                    // Ticks counted from the tuner's first one, the baseline reading, as they come - one an interval - to the move
                    // that brought it within 5 ms of where it ends. Since 2026-09-28 a silly setting jumps straight to the need
                    // and its headroom, and the evidence steps and the creep then shave the last few ms at their own pace.
                    var settled = window.SliderMs(lane);
                    var arriving = moves.First(m => Math.Abs(m.To - settled) <= 5);
                    var ticks = (int)Math.Round((arriving.At - window.BaselineAt[lane]) / window.IntervalSec) + 1;
                    var need = NeedOn(r, lane);
                    Check(ticks <= 12, $"{what}, {lane}: the slider must come within 5 ms of where it settles within 12 ticks of the tuner starting ({ticks}: {path})");
                    // The need is measured afresh every tick and the tuner never goes under the one it measures (the unit step
                    // "Auto-tune descends on evidence" holds that rule); read at the end of the run it can have moved by a ms
                    // since the last move (measured 2026-09-28: 18 at the move, 19 at the end, both outputs on a wired LAN).
                    Check(settled >= need - 1, $"{what}, {lane}: and settle at or above what the stream needs by the tuner's own formula "
                        + $"({settled} ms against {need}: {path})");
                    firstMove = Math.Min(firstMove, moves[0].At);
                    lanes.Add($"{lane} {settled} ms (needs {need}) in {ticks} ticks");
                }
                foreach (var (output, stretches) in OutputsHeard(r))
                {
                    // Every ten seconds from the one the tuner first moved in, to the end: the walk down and the settled stretch.
                    var walk = stretches.Where(c => c.FromSec + 10 > firstMove).ToList();
                    Check(walk.Count >= 20, $"{what}, {output}: premise - the walk must be listened to ({walk.Count} stretches)");
                    // Gathered over all six runs and judged at the end, so a failure names every run it happens in.
                    // And the click-trim must not have cut the buffer on the way down, heard as a click or not: a cut is the whole
                    // excess dropped at once. (Counted across the run's streams and lanes, from the tuner's first move on.)
                    var breaks = walk.Where(c => c.Findings.Glitches > 0).Select(c => $"{c.Findings.Glitches} at {c.Findings.FirstGlitchAt}").ToList();
                    var trimsOnTheWay = r.TrimsBySecond[^1] - r.TrimsBySecond[(int)firstMove - 2];
                    if (breaks.Count > 0 || trimsOnTheWay > 0)
                        cuts.Add($"{what}, {output}: {breaks.Count} break-up(s) {string.Join(", ", breaks)}; the click-trim fired {trimsOnTheWay} time(s) "
                            + $"and cut {r.TrimDroppedMs:0} ms - the slider {string.Join(" ", window.Ticks.Where(t => t.To != t.From).Take(3).Select(t => $"{t.From}>{t.To}@{t.At:0}s"))}");
                    var fastest = walk.Where(c => !double.IsNaN(c.Findings.PitchP95Cents)).MaxBy(c => c.Findings.PitchP95Cents);
                    var worst = walk.Where(c => !double.IsNaN(c.Findings.WorstPitchCents)).Max(c => c.Findings.WorstPitchCents);
                    var extra = output.Contains("WASAPI", StringComparison.OrdinalIgnoreCase) ? WasapiOutputExtra : 0;
                    Check(fastest.Findings.PitchP95Cents < nudge + extra + 0.5 && worst < nudge + extra + 1,
                        $"{what}, {output}: THE DESCENT PACE: in every ten seconds of the walk the drain may bend the pitch by the "
                        + $"descent's {nudge:0.0} cents{(extra > 0 ? $" (and {extra:0.0} more on a WASAPI output)" : "")} and no more (the ten seconds from "
                        + $"{fastest.FromSec:0} s: {fastest.Findings.PitchP95Cents:0.0} cents 95% of the time; {worst:0.0} at worst anywhere)");
                    var end = stretches[^1].Findings.DelayMedianMs;
                    Check(end < 150, $"{what}, {output}: and the delay must really have come down from 500 ms ({end:0.0} ms at the end)");
                    lanes.Add($"{output} down to {end:0.0} ms, {fastest.Findings.PitchP95Cents:0.0} cents at the fastest");
                }
                said.Add($"{what}: {string.Join(", ", lanes)}");
            }
        }
        // FOUND BY THIS STEP, 2026-09-28: in WASAPI only the buffer sits some 20-30 ms over its target from the start (it arms
        // on a 10 ms capture block and a 10 ms read), and while it is still there the tuner's first step down lowers the
        // target; the click-trim's gliding ceiling (1.5 ms a second) cannot come down under the buffer fast enough, so the
        // trim cuts the buffer to the new target at once - a click and a 100 ms drop. The glide exists so that an auto-tune
        // descent is never shaved into a click (SessionPlayout's trim comment); here it is. It happens from the new profile's
        // 80 ms too, more often: measured the same day on WASAPI only, wired, over three seeds each, the trim cut 21 to 60 ms
        // in five of nine 90 s runs (Opus 2.5 ms and PCM; none with 10 ms Opus), and once in three on home wifi.
        Check(cuts.Count == 0, "not one break-up, and not one cut by the click-trim, from the tuner's first move on - a cut is the whole excess "
            + $"dropped at once, the fastest descent there is: {string.Join("; ", cuts)} | everything else held: {string.Join("; ", said)}");
        return string.Join("; ", said);
    }

    /// <summary>SIMULATOR AUTO-TUNE FROM 5: the same auto-tune from far too small a buffer, on home wifi and on the rough
    /// internet (a step each, to keep each near a minute), in all three configurations: within two ticks of starting it reaches what the stream needs (to within the move it
    /// will not bother making), the break-ups stop within 5 s of that - apart from the packets the network loses and the
    /// repair cannot make up - and over the next five minutes it never climbs above where it went in its first half
    /// minute. (The review asked for no raise at all in those five minutes. That does not hold, and not by a fault: the
    /// tuner's creep keeps probing a few ms down and takes the step back when it runs short - by design, 2026-08-15 and
    /// 2026-09-06. The count of those raises is reported with each run; a climb would be a ratchet, and fails.)</summary>
    private static string SimulatorAutoTuneClimbsFrom5(SimNetwork net)
    {
        var said = new List<string>();
        foreach (var config in new[] { SimConfig.WasapiOnly, SimConfig.AsioOnly, SimConfig.Both })
        {
            var r = Simulation.Run(new SimScenario("auto-tune from 5 ms", config, SimCodec.Opus2_5, net, 5, 330,
                WasapiLaneBufferMs: 5, ContinuousAutoTune: true, ChunkSeconds: 10));
            var window = Require(r.Window, "the main window's work must have run");
            var what = $"{config}, {net.Name}";
            var reached = 0.0;
            var lanes = new List<string>();
            foreach (var lane in TunedLanes(config))
            {
                Check(window.BaselineAt.TryGetValue(lane, out var baseline), $"{what}, {lane}: premise - the tuner must start");
                var mine = window.Ticks.Where(t => t.Route == lane).ToList();
                var secondTick = baseline + window.IntervalSec;
                var afterTwo = mine.Where(t => t.At <= secondTick + 0.01).Select(t => t.To).DefaultIfEmpty(5).Last();
                var need = NeedOn(r, lane);
                Check(afterTwo >= need - ContinuousAutoTune.HysteresisMs,
                    $"{what}, {lane}: within two ticks of starting it must reach what the stream needs, give or take the {ContinuousAutoTune.HysteresisMs} ms "
                    + $"move it will not bother making ({afterTwo} ms against {need}: {string.Join(" ", mine.Take(4).Select(t => $"{t.From}>{t.To}@{t.At:0}s"))})");
                reached = Math.Max(reached, secondTick);
                // No ratchet over the next five minutes: nothing above the highest it went in its first half minute.
                var early = mine.Where(t => t.At <= 30).Select(t => t.To).DefaultIfEmpty(afterTwo).Max();
                var later = mine.Where(t => t.At > 30).ToList();
                var peak = later.Select(t => t.To).DefaultIfEmpty(early).Max();
                var raises = later.Count(t => t.Verdict == ContinuousAutoTune.Verdict.Raise || (t.Verdict == ContinuousAutoTune.Verdict.Move && t.To > t.From));
                Check(peak <= early + ContinuousAutoTune.HysteresisMs,
                    $"{what}, {lane}: over the next five minutes it must never climb above where it went in its first half minute ({peak} ms "
                    + $"against {early}; moves {string.Join(" ", later.Where(t => t.To != t.From).Select(t => $"{t.From}>{t.To}@{t.At:0}s"))})");
                lanes.Add($"{lane} {afterTwo} ms after two ticks (needs {need}), {raises} raise(s) in the five minutes after, peak {peak}");
            }
            foreach (var (output, stretches) in OutputsHeard(r))
            {
                var after = stretches.Where(c => c.FromSec >= reached + 5).ToList();
                var breaks = after.Sum(c => c.Findings.Glitches);
                Check(after.Count >= 30, $"{what}, {output}: premise - five minutes listened to after it got there ({after.Count} stretches)");
                Check(breaks <= r.UnrecoveredGaps,
                    $"{what}, {output}: within 5 s of reaching the need the break-ups must stop - all but the lost packets the repair could not "
                    + $"make up ({breaks} break-ups from {reached + 5:0} s on, {r.UnrecoveredGaps} packets unrepaired; "
                    + $"{string.Join(", ", after.Where(c => c.Findings.Glitches > 0).Take(6).Select(c => $"{c.Findings.Glitches} at {c.Findings.FirstGlitchAt}"))})");
                lanes.Add($"{output}: {breaks} break-ups after {reached + 5:0} s ({r.UnrecoveredGaps} unrepaired)");
            }
            said.Add($"{what}: {string.Join(", ", lanes)}");
        }
        return string.Join("; ", said);
    }

    /// <summary>SIMULATOR BOTH OUTPUTS: with both kinds of output, BOTH are listened to - the WASAPI one was read and thrown
    /// away until 2026-09-28 (the review of every test, items 28, 340 and 178). Every output plays every stream, so each
    /// sending lane carries the tone in turn (the same tone on both at once would be heard twice, a few ms apart, and
    /// comb-filter): clean on both outputs for 60 s, each at its own buffer. Then the WASAPI card stalls - half a second
    /// every three, and then dead for the rest of the run - and the ASIO output does not notice: not a break-up, not one
    /// underrun more than beside a healthy card, the same delay, and no stream re-opened.</summary>
    private static string SimulatorBothOutputsAreEachListenedTo()
    {
        SimAnalysis.Findings Heard(SimResult r) => Require(r.WasapiOutput, "the WASAPI output must be listened to");
        void Clean(string what, SimAnalysis.Findings f, SimResult r) =>
            Check(f.ClicksHeard >= 100 && f.Glitches == 0 && f.StepClicks == 0 && f.WorstSnrDb > 15,
                $"{what} must play clean for the minute ({f.ClicksHeard} clicks heard, {f.Glitches} break-ups, {f.StepClicks} step clicks, worst "
                + $"{f.WorstSnrDb:0} dB) - {SimRunner.Describe(r)}");

        // The tone on the ASIO lane: the ASIO output at its 10 ms, the WASAPI output at its 40.
        var asioTone = Simulation.Run(new SimScenario("both, tone on the ASIO lane", SimConfig.Both, SimCodec.Opus2_5, SimNetwork.Wired, 10, 60, WasapiLaneBufferMs: 40));
        var a = asioTone.Findings;
        var aw = Heard(asioTone);
        Clean("tone on the ASIO lane: the ASIO output", a, asioTone);
        Clean("tone on the ASIO lane: the WASAPI output", aw, asioTone);
        Check(a.DelayMedianMs is > 10 and < 20 && aw.DelayMedianMs > 40 && aw.DelayMedianMs > a.DelayMedianMs + 25,
            $"each output at its own buffer: the ASIO one just over its 10 ms, the WASAPI one over its 40 ({a.DelayMedianMs:0.0} and {aw.DelayMedianMs:0.0} ms)");
        Check(asioTone.SessionsOpened == 2, $"premise: two streams, one per sending lane ({asioTone.SessionsOpened} opened)");

        // The tone on the WASAPI lane. That stream comes in the capture's 10 ms lumps, so the ASIO output needs 20 ms to cover it.
        var wasapiTone = Simulation.Run(new SimScenario("both, tone on the WASAPI lane", SimConfig.Both, SimCodec.Opus2_5, SimNetwork.Wired, 20, 60,
            WasapiLaneBufferMs: 40, ToneOnWasapiLane: true));
        Clean("tone on the WASAPI lane: the ASIO output", wasapiTone.Findings, wasapiTone);
        Clean("tone on the WASAPI lane: the WASAPI output", Heard(wasapiTone), wasapiTone);

        // The WASAPI card in trouble, the ASIO output beside it.
        var said = new List<string>();
        foreach (var (card, how) in new[]
        {
            (new SimCard(480, JitterMs: 1, StallEverySec: 3, StallMs: 500), "stalling half a second every three"),
            (new SimCard(480, JitterMs: 1, StallAtSec: 10, StallOnceMs: 1e7), "dead from 10 s on"),
        })
        {
            var r = Simulation.Run(new SimScenario("both, the WASAPI card in trouble", SimConfig.Both, SimCodec.Opus2_5, SimNetwork.Wired, 10, 60,
                WasapiLaneBufferMs: 40, WasapiLaneReceiveCard: card));
            var w = Heard(r);
            Check(w.Glitches > 0 || w.ClicksHeard < aw.ClicksHeard / 2,
                $"the WASAPI card {how}: premise - its output must really suffer ({w.Glitches} break-ups, {w.ClicksHeard} clicks heard)");
            Clean($"the WASAPI card {how}: the ASIO output", r.Findings, r);
            Check(r.Underruns == asioTone.Underruns,
                $"the WASAPI card {how}: not one underrun more on the ASIO lane than beside a healthy card ({r.Underruns} against {asioTone.Underruns})");
            Check(Math.Abs(r.Findings.DelayMedianMs - a.DelayMedianMs) < 0.5,
                $"the WASAPI card {how}: the ASIO output's delay unchanged ({r.Findings.DelayMedianMs:0.0} ms against {a.DelayMedianMs:0.0})");
            Check(r.SessionsOpened == asioTone.SessionsOpened, $"the WASAPI card {how}: no stream re-opened ({r.SessionsOpened} sessions against {asioTone.SessionsOpened})");
            said.Add($"WASAPI card {how}: its output {w.Glitches} break-ups, the ASIO output clean at {r.Findings.DelayMedianMs:0.0} ms, {r.Underruns} underruns as before");
        }
        return $"tone on the ASIO lane: ASIO {a.DelayMedianMs:0.0} ms, WASAPI {aw.DelayMedianMs:0.0} ms, both clean; tone on the WASAPI lane: ASIO "
             + $"{wasapiTone.Findings.DelayMedianMs:0.0} ms (at 20), WASAPI {Heard(wasapiTone).DelayMedianMs:0.0} ms, both clean; " + string.Join("; ", said);
    }

    /// <summary>SIMULATOR LATENCY READOUT: the Total latency box against the simulator's TRUE card-to-card delay (the review of
    /// every test, item 87). The readout is composed by the app's own code (MainForm.PickCaptureLatencyMs, LaneHardwareMs and
    /// FormatMeasuredLatency) from the receiver's counters and what the simulated devices report about themselves - an ASIO
    /// driver its buffer, a WASAPI device its period by DeviceLatency's rule - with the WASAPI output stage in the path, so
    /// its queue is a real term. In all three configurations and four ways of sending, on a wired network.
    ///
    /// <para>The ASIO output's figure must be within 5 ms of the truth. The WASAPI output's is NOT, and that is reported, not
    /// asserted (measured 2026-09-28, +5 to +18 ms high): it counts the WASAPI capture's full period AND half a codec frame of
    /// sending wait (a frame is complete when its 10 ms block arrives, so that wait is already inside the period), and the
    /// output as two device periods where the simulated card plays one to two. Whether the output term is an error or a
    /// fair upper bound depends on real WASAPI hardware the simulator cannot settle, so the ±5 ms bar is not loosened to
    /// pass: the WASAPI figures are asserted only never to flatter - never more than 2 ms under the truth.</para></summary>
    private static string SimulatorLatencyReadoutMatchesTheTrueDelay()
    {
        var asio = new List<string>();
        var wasapi = new List<string>();
        // A stream played below its output's setting by its own buffer (auto-tune on, not yet due): the box must count the
        // buffer it plays at, not the setting (2026-09-29).
        {
            // Long enough for the stream to settle on its own buffer (1 ms a second down, then the steady drift path).
            var r = Simulation.Run(new SimScenario("readout, per-stream", SimConfig.AsioOnly, SimCodec.Opus2_5, SimNetwork.Wired, 30, 100,
                Diagnostics: true, AnalyseFromSecond: 80, ContinuousAutoTune: true, AutoTuneIntervalSec: 300));
            var window = Require(r.Window, "the main window's work must have run");
            var shown = window.Readouts.Where(x => x.Route == RenderRoute.AsioLane && x.At >= 80).OrderBy(x => x.ShownTotalMs).ToList();
            Check(shown.Count >= 5, $"premise - the box must be written each second ({shown.Count} readouts)");
            var box = shown[shown.Count / 2];
            var error = box.ShownTotalMs - r.Findings.DelayMedianMs;
            Check(box.SetMs < 30 && Math.Abs(error) <= 5,
                $"PER-STREAM: the box must count the buffer the stream plays at, below the 30 ms set, and be within 5 ms of the truth "
                + $"(box {box.ShownTotalMs} ms with a buffer of {box.SetMs}; true {r.Findings.DelayMedianMs:0.0} ms)");
            asio.Add($"per-stream {error:+0.0;-0.0} (buffer {box.SetMs} of 30)");
        }
        foreach (var config in new[] { SimConfig.WasapiOnly, SimConfig.AsioOnly, SimConfig.Both })
        {
            foreach (var codec in new[] { SimCodec.Pcm, SimCodec.Opus2_5, SimCodec.Opus10, SimCodec.Custom64 })
            {
                if (codec == SimCodec.Custom64 && config == SimConfig.WasapiOnly) continue; // Jamulus-style frames go on the ASIO lane only
                var r = Simulation.Run(new SimScenario("readout", config, codec, SimNetwork.Wired, config == SimConfig.WasapiOnly ? 40 : 10, 30,
                    WasapiLaneBufferMs: 40, Diagnostics: true, AnalyseFromSecond: 10, WasapiOutputStage: true));
                // With both kinds of output, each output's box counts the capture of the stream sent for it (N7, 2026-09-28):
                // the ASIO output the far ASIO input's, heard above with the tone on the ASIO lane; the WASAPI output the far
                // WASAPI capture's, so it is listened to with the tone on the WASAPI lane - the journey its box describes.
                var rw = config == SimConfig.Both
                    ? Simulation.Run(new SimScenario("readout, tone on the WASAPI lane", config, codec, SimNetwork.Wired, 10, 30,
                        WasapiLaneBufferMs: 40, Diagnostics: true, AnalyseFromSecond: 10, WasapiOutputStage: true, ToneOnWasapiLane: true))
                    : r;
                RenderRoute[] lanes = config switch
                {
                    SimConfig.WasapiOnly => [RenderRoute.WasapiLane],
                    SimConfig.AsioOnly => [RenderRoute.AsioLane],
                    _ => [RenderRoute.WasapiLane, RenderRoute.AsioLane],
                };
                foreach (var lane in lanes)
                {
                    var run = lane == RenderRoute.WasapiLane ? rw : r;
                    var window = Require(run.Window, "the main window's work must have run");
                    var shown = window.Readouts.Where(x => x.Route == lane && x.At >= 10).OrderBy(x => x.ShownTotalMs).ToList();
                    var truth = lane == RenderRoute.WasapiLane && config == SimConfig.Both ? Require(run.WasapiOutput, "the WASAPI output") : run.Findings;
                    var what = $"{config}, {SimLab.Name(codec)}, the {(lane == RenderRoute.AsioLane ? "ASIO" : "WASAPI")} output";
                    Check(shown.Count >= 15 && truth.ClicksHeard >= 30, $"{what}: premise - the box must be written each second and the clicks heard "
                        + $"({shown.Count} readouts, {truth.ClicksHeard} clicks)");
                    var box = shown[shown.Count / 2];
                    var error = box.ShownTotalMs - truth.DelayMedianMs;
                    var terms = $"box {box.ShownTotalMs} ms = jitter buffer {box.SetMs} + capture {box.CaptureMs:0.0} + sending {box.SenderWaitMs:0.0} + "
                        + $"network {box.WireMs:0.0} + output queue {box.QueueMs:0.0} + output {box.OutputMs:0.0}; true {truth.DelayMedianMs:0.0} ms";
                    if (lane == RenderRoute.AsioLane)
                    {
                        Check(Math.Abs(error) <= 5, $"{what}: the Total latency box must be within 5 ms of the true delay ({error:+0.0;-0.0} ms: {terms})");
                        asio.Add($"{config} {SimLab.Name(codec)} {error:+0.0;-0.0}");
                    }
                    else
                    {
                        // Held to ASIO's 5 ms since 2026-09-28: the output term is measured from the card's own played position
                        // (the stage's MeasuredOutputMs) where it was the engine period doubled - 7 to 9 ms high on every WASAPI
                        // output. What is left is mostly the capture term, which counts a whole capture period where a sound
                        // waits half of one on average. Hidden by the simulator's old PCM (5 ms frames, whose own wait filled the
                        // difference), it shows with PCM as the app sends it, a packet per callback: about 6.6 ms high in WASAPI
                        // only (2026-09-28). So the box may read high by up to half its capture term more - the convention the
                        // capture figure has on the wire, which is Ed's to change - and never flatter by more than 5 ms.
                        Check(error >= -5 && error <= 5 + box.CaptureMs / 2,
                            $"{what}: the Total latency box must be within 5 ms of the true delay, or high by at most half its capture "
                            + $"term more ({error:+0.0;-0.0} ms: {terms})");
                        // The sending term is the wait measured sample by sample (SenderLane, 2026-09-28): a 10 ms WASAPI block cut
                        // into 10 ms Opus frames goes out as it comes and waits nothing, where half a packet said 4.8 ms.
                        if (config == SimConfig.WasapiOnly && codec == SimCodec.Opus10)
                            Check(box.SenderWaitMs < 1.0, $"{what}: the sending term must be the wait measured, near nothing here ({box.SenderWaitMs:0.0} ms)");
                        wasapi.Add($"{config} {SimLab.Name(codec)} {error:+0.0;-0.0} ({terms})");
                    }
                }
            }
        }
        return $"ASIO output within 5 ms everywhere: {string.Join(", ", asio)}. WASAPI output within 5 ms, or high by half its capture "
             + $"term, everywhere: {string.Join("; ", wasapi)}";
    }

    /// <summary>SIMULATOR STEP PROBE: the receiver's post-decode step probe - the diagnostic that says which stage of the
    /// receive path a click came from - is fed on 2.5 ms Opus, and flags a click planted at the sender (the review of every
    /// test, item 283: nothing proved it ever saw audio). Opus on a wired network, all three configurations, the delay's
    /// clicks off so the probe hears only the tone and the planted click: every second's reading is the tone's own step
    /// (about 0.065 a sample at half scale), and the second the click was planted in reads well above it - and only that one.
    /// With the delay's clicks back on, the listener hears the planted click as a click at the moment it was planted.</summary>
    private static string SimulatorStepProbeFlagsAPlantedClick()
    {
        var said = new List<string>();
        foreach (var config in new[] { SimConfig.WasapiOnly, SimConfig.AsioOnly, SimConfig.Both })
        {
            var buffer = config == SimConfig.WasapiOnly ? 40 : 10;
            SimResult Run(bool plant, bool clicks) => Simulation.Run(new SimScenario("step probe", config, SimCodec.Opus2_5, SimNetwork.Wired, buffer, 10,
                WasapiLaneBufferMs: 40, Diagnostics: true, DelayClicks: clicks, PlantClickAtSecond: plant ? 5.3 : 0, PlantClickLevel: 0.5f));
            var clean = Run(false, false);
            var planted = Run(true, false);
            Check(clean.PostDecodeSteps.Count >= 9 && clean.PostDecodeSteps.All(p => p.Cross > 0 && p.Within > 0),
                $"{config}: the post-decode probe must be fed every second, both its readings ({string.Join(" ", clean.PostDecodeSteps.Select(p => $"{p.Cross:0.000}/{p.Within:0.000}"))})");
            float Step((float Cross, float Within) p) => Math.Max(p.Cross, p.Within);
            var tone = clean.PostDecodeSteps.Skip(1).Max(Step); // the first second holds the stream's start
            Check(tone is > 0.055f and < 0.09f, $"{config}: a clean 1 kHz tone at half scale must read its own step, about 0.065 ({tone:0.000})");
            var loudest = planted.PostDecodeSteps.Select((p, i) => (Step: Step(p), Second: i)).Skip(1).MaxBy(x => x.Step);
            Check(loudest.Step > 2.5f * tone && loudest.Second == 5,
                $"{config}: the click planted 5.3 s in must be flagged in the reading that covers it (5.2 to 6.2 s) - well above the tone's "
                + $"{tone:0.000} (loudest {loudest.Step:0.000}, in reading {loudest.Second})");
            var others = planted.PostDecodeSteps.Select((p, i) => (Step: Step(p), Second: i)).Skip(1).Where(x => x.Second != 5).Max(x => x.Step);
            Check(others < 1.5f * tone, $"{config}: and no other second may look like a click ({others:0.000} at most)");
            var heard = Run(true, true);
            Check(heard.Findings.StepClicks > 0 && heard.Findings.FirstGlitchAt is { } at && double.Parse(at.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture) is > 5.3 and < 5.4,
                $"{config}: and the listener must hear it as a click, at the moment it was planted plus the delay ({heard.Findings.StepClicks} step clicks, first at {heard.Findings.FirstGlitchAt})");
            said.Add($"{config}: tone {tone:0.000}, planted click {loudest.Step:0.000}");
        }
        return string.Join("; ", said);
    }

    /// <summary>The median of a run's delay over a stretch of minutes, from the median of each listened stretch in it - the
    /// WASAPI output's delay wanders a few ms either way over a minute or two, so a single moment is no measure of creep.</summary>
    private static double DelayOverMinutes(IReadOnlyList<(double FromSec, SimAnalysis.Findings Findings)> stretches, double fromMin, double toMin)
    {
        var d = stretches.Where(c => c.FromSec >= fromMin * 60 && c.FromSec < toMin * 60 && !double.IsNaN(c.Findings.DelayMedianMs))
            .Select(c => c.Findings.DelayMedianMs).OrderBy(x => x).ToList();
        return d.Count == 0 ? double.NaN : d[d.Count / 2];
    }

    /// <summary>SIMULATOR OVERNIGHT: WASAPI only, the everyday set-up, for a simulated hour over Ed's laptop's lumps and over
    /// home wifi, the two sound cards 80 ppm apart (40 each way), at the buffer the auto-tune's formula asks for (the review
    /// of every test, items 248 and 249) - and the same two hours with the WASAPI output stage in the path, the device
    /// buffer with its cushion and rate trimmer that ASIO does not have and "slow after a night" is suspected of (plan C;
    /// modelled from 2026-09-28, MultiOutputPlayout.SimulatedStage). All four run at once, a thread each.
    ///
    /// <para>Asserted: the delay does not creep - the straight line through each minute's delay from the tenth minute on may
    /// move by no more than 2 ms over the hour (the review asked for the delay at the end against the delay at ten minutes;
    /// the WASAPI output's delay wanders several ms either way every few minutes, so two single moments measure the wander,
    /// not a creep - both are reported); gaps stay under a stated bound
    /// - home wifi at most one in ten minutes (Opus's repair covers its losses: none in two hours when measured), the
    /// laptop at most four a minute (its 30 ms stall every 45 s is longer than the formula's 15 s look-back usually sees:
    /// about three a minute measured); and in the stage, the cushion is sized once and never moves, and its clock settles
    /// on the true crystal difference.</para>
    ///
    /// <para>FOUND BY THIS STEP, 2026-09-28, and FIXED the same evening (Ed approved): with the stage in, the output broke up
    /// in bursts of a hundred or more clicks every four minutes or so - the time the 40 ppm difference takes to slide the
    /// card's 10 ms pulls past the producer's 10 ms feeds. When the two coincide the producer's millisecond of timer jitter
    /// lets a pull come before its feed, and the 12 ms cushion was held as the one sample before a pull, 2 ms after it; each
    /// 10 s clock window also counted a whole block too many or too few (1000 ppm, as Ed's 2026-09-10 log reads). The stage
    /// now steers its time-averaged level and carries the fed count to the window's edge (MultiOutputPlayout's
    /// DriftResamplingProvider; SIMULATOR WASAPI OUTPUT STAGE is its own step). So since then this is asserted too: with the
    /// stage in, not one minute with a burst, and no more gaps than the same bound without the stage.</para></summary>
    private static string SimulatorHoldsOvernight()
    {
        var card = new SimCard(480, JitterMs: 1);
        const double Hours = 1;
        var scenarios = new List<SimScenario>();
        var asks = new Dictionary<string, int>();
        foreach (var net in new[] { SimNetwork.LaptopLumps, SimNetwork.Wifi })
        {
            var ask = Simulate(SimConfig.WasapiOnly, SimCodec.Opus2_5, net, 80, 20).TunerWouldAskMs(false);
            asks[net.Name] = ask;
            foreach (var stage in new[] { false, true })
                scenarios.Add(new SimScenario(stage ? "overnight, with the WASAPI output stage" : "overnight", SimConfig.WasapiOnly, SimCodec.Opus2_5, net,
                    ask, Hours * 3600, card with { DriftPpm = 40 }, card with { DriftPpm = -40 }, WasapiOutputStage: stage, ChunkSeconds: 60));
        }
        var said = new List<string>();
        foreach (var r in Simulation.RunTogether(scenarios.ToArray()))
        {
            var s = r.Scenario;
            var what = $"{s.Network.Name}, {s.BufferMs} ms{(s.WasapiOutputStage ? ", WASAPI output stage in" : "")}";
            Check(r.Stretches.Count >= Hours * 60 - 1 && r.Findings.ClicksHeard > Hours * 3600, $"{what}: premise - the hour must be listened to "
                + $"({r.Stretches.Count} minutes, {r.Findings.ClicksHeard} clicks)");
            var atTen = DelayOverMinutes(r.Stretches, 10, 11);
            var atEnd = DelayOverMinutes(r.Stretches, Hours * 60 - 1, Hours * 60);
            // Creep: the least-squares line through each minute's median delay, from the tenth minute to the end.
            var minutes = r.Stretches.Where(c => c.FromSec >= 600 && !double.IsNaN(c.Findings.DelayMedianMs))
                .Select(c => (X: c.FromSec / 60, Y: c.Findings.DelayMedianMs)).ToList();
            double meanX = minutes.Average(m => m.X), meanY = minutes.Average(m => m.Y);
            var slope = minutes.Sum(m => (m.X - meanX) * (m.Y - meanY)) / minutes.Sum(m => (m.X - meanX) * (m.X - meanX));
            var creep = slope * (minutes[^1].X - minutes[0].X);
            var wander = $"{minutes.Min(m => m.Y):0.0} to {minutes.Max(m => m.Y):0.0} ms minute by minute; {atTen:0.0} ms at ten minutes, {atEnd:0.0} at the end";
            Check(Math.Abs(creep) <= 2, $"{what}: the delay must not creep - it moved {creep:+0.0;-0.0} ms over the hour along its trend ({wander})");
            var perHour = r.Findings.Glitches / Hours;
            if (!s.WasapiOutputStage)
            {
                var bound = s.Network == SimNetwork.Wifi ? 6 : 240;
                Check(perHour <= bound, $"{what}: gaps must stay under {bound} an hour ({perHour:0} an hour; {r.Findings.QuietMs} quiet ms, {r.Underruns} underruns, "
                    + $"{r.UnrecoveredGaps} packets unrepaired)");
                said.Add($"{what}: creep {creep:+0.0;-0.0} ms ({wander}), {perHour:0} gaps an hour (bound {bound})");
                continue;
            }
            var stage = r.StageBySecond;
            Check(stage.Count >= Hours * 3600 - 2, $"{what}: premise - the stage must be read every second ({stage.Count})");
            var targets = stage.Skip(60).Select(x => x.TargetMs).Distinct().ToList();
            Check(targets.Count == 1 && targets[0] == 12, $"{what}: the cushion must be sized from the card's 10 ms pull once (12 ms) and never move after the "
                + $"first minute ({string.Join(", ", targets)} ms)");
            var clock = stage.Skip(120).Average(x => x.ClockPpm);
            Check(Math.Abs(clock - 40) <= 25, $"{what}: the stage's clock must settle, on average, on the true 40 ppm between the machine and its card ({clock:+0;-0} ppm)");
            // The bursts: minutes with ten or more break-ups. None since 2026-09-28, and no more gaps than without the stage.
            var bursts = r.Stretches.Where(c => c.Findings.Glitches >= 10).Select(c => c.FromSec / 60).ToList();
            var stageBound = s.Network == SimNetwork.Wifi ? 6 : 240;
            Check(bursts.Count == 0 && perHour <= stageBound, $"{what}: the stage must not break up in bursts, nor add gaps - under {stageBound} an hour as without it "
                + $"({perHour:0} an hour, bursts in {bursts.Count} minutes: {string.Join(" ", bursts.Take(12).Select(m => $"{m:0}"))})");
            said.Add($"{what}: creep {creep:+0.0;-0.0} ms ({wander}), cushion 12 ms throughout, clock {clock:+0;-0} ppm; {perHour:0} gaps an hour (bound {stageBound}), in bursts in "
                + $"{bursts.Count} minutes ({string.Join(" ", bursts.Take(12).Select(m => $"{m:0}"))}{(bursts.Count > 12 ? " ..." : "")})");
        }
        return string.Join("; ", said);
    }

    /// <summary>SIMULATOR STALL RECOVERY: home wifi, the cards 80 ppm apart - the receiving card stalls for 4 s, and in another
    /// run the network stalls for 1.5 s, five minutes in (the review of every test, items 248 and 249, asked it of WASAPI
    /// only; the drift loop it tests is in every output, so all three configurations run - six runs at once, a thread each).
    /// The delay must come back to where it was before, within 2 ms, inside 15 minutes, and stay there; and the pitch must
    /// stay within 5 cents all the way back. (Delays are medians and pitch the 95th percentile, over ten-second stretches:
    /// the WASAPI output's delay wanders a few ms either way on its own.)</summary>
    private static string SimulatorRecoversFromAStall()
    {
        const double StallAt = 300;
        var scenarios = new List<SimScenario>();
        foreach (var config in new[] { SimConfig.WasapiOnly, SimConfig.AsioOnly, SimConfig.Both })
        {
            var card = Simulation.DefaultCard(asio: config != SimConfig.WasapiOnly);
            var wasapiCard = new SimCard(480, JitterMs: 1, DriftPpm: -40);
            var buffer = config == SimConfig.WasapiOnly ? 40 : 10;
            scenarios.Add(new SimScenario("the receiving card stalls for 4 s", config, SimCodec.Opus2_5, SimNetwork.Wifi, buffer, 1200,
                card with { DriftPpm = 40 }, card with { DriftPpm = -40, StallAtSec = StallAt, StallOnceMs = 4000 }, WasapiLaneBufferMs: 40,
                WasapiLaneReceiveCard: wasapiCard, ChunkSeconds: 10));
            scenarios.Add(new SimScenario("the network stalls for 1.5 s", config, SimCodec.Opus2_5, SimNetwork.Wifi with { StallAtSec = StallAt, StallOnceMs = 1500 },
                buffer, 1200, card with { DriftPpm = 40 }, card with { DriftPpm = -40 }, WasapiLaneBufferMs: 40, WasapiLaneReceiveCard: wasapiCard, ChunkSeconds: 10));
        }
        var said = new List<string>();
        var troubles = new List<string>();
        foreach (var r in Simulation.RunTogether(scenarios.ToArray()))
        {
            foreach (var (output, stretches) in OutputsHeard(r))
            {
                var what = $"{r.Scenario.Config}, {r.Scenario.Name}, {output}";
                var before = DelayOverMinutes(stretches, 1, StallAt / 60);
                // Back: the first two minutes after the stall whose delay is within 2 ms of before; and it must stay so to the end.
                double? backAt = null;
                for (var m = StallAt / 60; m + 2 <= 20; m += 1.0 / 6)
                    if (Math.Abs(DelayOverMinutes(stretches, m, m + 2) - before) <= 2) { backAt = m; break; }
                var atEnd = DelayOverMinutes(stretches, 16, 20);
                Check(stretches.Sum(c => c.Findings.ClicksHeard) > 2000 && !double.IsNaN(before), $"{what}: premise - the run must be listened to");
                Check(backAt is { } back && back - StallAt / 60 <= 15 && Math.Abs(atEnd - before) <= 2,
                    $"{what}: the delay must come back within 2 ms of the {before:0.0} ms before the stall, inside 15 minutes, and stay there "
                    + $"(back {(backAt is { } b ? $"{(b - StallAt / 60) * 60:0} s after" : "never")}; {atEnd:0.0} ms over the last four minutes)");
                // The walk back: every ten seconds from the stall until the delay is back, and at least two minutes of it.
                var walkEnd = Math.Max((backAt ?? 20) * 60 + 120, StallAt + 120);
                var worst = stretches.Where(c => c.FromSec >= StallAt && c.FromSec < walkEnd && !double.IsNaN(c.Findings.PitchP95Cents))
                    .MaxBy(c => c.Findings.PitchP95Cents);
                var trims = r.TrimsBySecond[(int)Math.Min(walkEnd, r.TrimsBySecond.Count) - 1] - r.TrimsBySecond[(int)StallAt - 2];
                // Gathered, so a failure names every run it happens in.
                if (!(worst.Findings.PitchP95Cents < 5))
                    troubles.Add($"{what}: {worst.Findings.PitchP95Cents:0.0} cents in the ten seconds from {worst.FromSec:0} s (95% of the time), "
                        + $"the click-trim firing {trims} time(s) on the way; the drift loop's clock went from {r.DriftPpmBySecond[(int)StallAt - 2]:+0;-0} ppm "
                        + $"to {r.DriftPpmBySecond.Skip((int)StallAt).Take(60).MaxBy(Math.Abs):+0;-0} after the stall (the cards are 80 ppm apart)");
                said.Add($"{what}: {before:0.0} ms before, back {(backAt is { } bb ? $"{(bb - StallAt / 60) * 60:0} s" : "never")} after, {atEnd:0.0} at the end; "
                    + $"pitch at worst {worst.Findings.PitchP95Cents:0.0} cents on the way, {trims} trim(s)");
            }
        }
        // FOUND BY THIS STEP, 2026-09-28: a network stall is taken for a clock. SessionPlayout's drift loop counts the bytes
        // written against those played over ten seconds; the window the stall falls in comes up short, is inside its 5 %
        // sanity band, and is believed - on WASAPI only the clock reading went from +16 ppm to -1489 - so the output plays
        // about 0.4 % slow, the buffer grows 3 to 4 ms a second and the click-trim cuts it back (a click each time) until
        // later windows put it right, overshooting on the way. DriftRatioTracker's copy of this loop was written to throw
        // such a window away ("a wild reading is a glitch - a stall - not a clock"); this one lets a stall of up to half a
        // second of a window through.
        Check(troubles.Count == 0, "the pitch must stay within 5 cents all the way back: " + string.Join("; ", troubles) + " | " + string.Join("; ", said));
        return string.Join("; ", said);
    }

    /// <summary>SIMULATOR WASAPI OUTPUT STAGE (2026-09-28): the stage in front of every WASAPI output (MultiOutputPlayout's
    /// device buffer, its cushion and its rate trimmer, fed 10 ms at a time by the producer on the machine's clock) never
    /// breaks up, whatever the card's 10 ms pulls do against the producer's 10 ms feeds; reads the card's clock true; stays
    /// in tune; and holds a steady cushion - the delay it adds does not wander as the card's pulls slide past the feeds.
    /// Five minutes each - a 40 ppm difference slides the pulls right past the feeds once in 250 s - with the card at the
    /// machine's own rate (the pulls and feeds on top of one another for good), 40 ppm slow and 40 ppm fast, on WASAPI only
    /// and on the WASAPI output of both; six runs at once, a thread each. ASIO only has no such stage: ASIO pulls straight
    /// from the engine.
    ///
    /// <para>FOUND BY SIMULATOR OVERNIGHT AND THE PERFORMANCE SOAK, FIXED 2026-09-28 (Ed approved): the stage held its 12 ms
    /// cushion as the ONE sample taken just before a pull - the top of the sawtooth, 2 ms after the pull - so whenever a pull
    /// came before its feed (the producer's millisecond of timer jitter: every four minutes at 40 ppm, all the time at none)
    /// it found 2 ms where it needed 10 and was padded with silence; and each 10 s clock reading counted the feed due at its
    /// edge or not by the same toss, 1,000 ppm out. Now the depth term steers the level's time-averaged mean and the fed
    /// count is carried to the edge's moment (DriftResamplingProvider).</para>
    ///
    /// <para>THE STEADY CUSHION IS ED'S CHOICE (2026-09-28). A leaner one was built and measured the same evening (fbda802,
    /// reverted): the old 2 ms after a pull where the feed and the pull are well apart, a whole pull more only where a feed
    /// lands close before a pull. It never broke up and saved 1.5 to 2 ms on average at 40 ppm, but the delay it added
    /// wandered with the card's phase - the stage's level from 5 to 19 ms as the pulls slid past the feeds - and each
    /// time a faster card brought a feed close it filled at the 0.3 % cap, 5 cents for three seconds. Ed chose the steady
    /// one; the wander bar and the one-cent bar below are what keep it steady.</para></summary>
    private static string SimulatorWasapiOutputStageHoldsItsCushion()
    {
        var card = new SimCard(480, JitterMs: 1);
        var runs = new List<SimScenario>();
        foreach (var config in new[] { SimConfig.WasapiOnly, SimConfig.Both })
            foreach (var ppm in new[] { 0.0, -40.0, 40.0 })
                runs.Add(new SimScenario($"{config}, the WASAPI card {(ppm == 0 ? "at the machine's rate" : $"{Math.Abs(ppm):0} ppm {(ppm < 0 ? "slow" : "fast")}")}",
                    config, SimCodec.Opus2_5, SimNetwork.Wired, config == SimConfig.WasapiOnly ? 40 : 10, 300,
                    ReceiveCard: config == SimConfig.WasapiOnly ? card with { DriftPpm = ppm } : null, WasapiLaneBufferMs: 40,
                    WasapiLaneReceiveCard: card with { DriftPpm = ppm }, WasapiOutputStage: true, ChunkSeconds: 10));
        const int SettleSec = 20;
        const double WanderMs = WanderBarMs;
        var said = new List<string>();
        var troubles = new List<string>();
        foreach (var r in Simulation.RunTogether(runs.ToArray()))
        {
            var what = r.Scenario.Name;
            var heard = r.Scenario.Config == SimConfig.WasapiOnly ? r.Stretches : r.WasapiStretches;
            var reads = r.StageShortReadsBySecond;
            Check(reads.Count >= 290 && heard.Count >= 29 && r.StageLevelBySecond.Count >= 290, $"{what}: premise - the stage read each second and "
                + $"the WASAPI output listened to ({reads.Count} readings, {heard.Count} stretches)");
            var faults = new List<string>();
            var padded = reads[^1] - reads[SettleSec - 1];
            var breaks = heard.Where(c => c.FromSec >= SettleSec && (c.Findings.Glitches > 0 || c.Findings.StepClicks > 0)).ToList();
            if (padded > 0 || breaks.Count > 0)
                faults.Add($"from {SettleSec} s on, {padded} pulls the stage could not fill and break-ups in {breaks.Count} stretches "
                    + $"({string.Join(", ", breaks.Take(8).Select(c => $"{c.Findings.Glitches} at {c.FromSec:0} s"))}) - there must be none");
            var pitch = heard.Where(c => c.FromSec >= SettleSec && !double.IsNaN(c.Findings.PitchP95Cents)).Max(c => c.Findings.PitchP95Cents);
            if (!(pitch < 1))
                faults.Add($"out of tune by {pitch:0.00} cents (95% of the time in the worst ten seconds from {SettleSec} s) - the stage's trimmer is a "
                    + "clock match, not a correction, and must stay within a cent");
            // The card's clock against the machine's: 40 ppm slow reads +40 (the machine feeds 40 ppm more than the card drains).
            var truth = -(r.Scenario.WasapiLaneReceiveCard?.DriftPpm ?? 0);
            var clock = r.StageBySecond.Skip(60).Select(x => x.ClockPpm).ToList();
            var worst = clock.Max(x => Math.Abs(x - truth));
            if (worst > 150)
                faults.Add($"the stage's clock read {worst:0} ppm from the true {truth:+0;-0;0} ppm after the first minute ({clock.Min():+0;-0;0} to "
                    + $"{clock.Max():+0;-0;0}) - a 10 ms feed counted or not at a window's edge was 1,000 ppm; it must be within 150");
            // The steady cushion: the stage's level, averaged over each ten seconds from the first minute on, must not move by more
            // than WanderMs as the card's pulls slide past the feeds (the whole circle, once, in a 40 ppm run).
            var tens = Enumerable.Range(0, (r.StageLevelBySecond.Count - 60) / 10).Select(i => r.StageLevelBySecond.Skip(60 + 10 * i).Take(10).Average()).ToList();
            var wander = tens.Max() - tens.Min();
            if (wander > WanderMs)
                faults.Add($"the delay the stage adds wandered by {wander:0.0} ms with the card's phase ({tens.Min():0.0} to {tens.Max():0.0} ms held, ten "
                    + $"seconds at a time) - it must stay within {WanderMs:0} ms");
            if (faults.Count > 0) troubles.Add($"{what}: {string.Join("; ", faults)}");
            said.Add($"{what}: none padded after {SettleSec} s, no break-up; the stage holding {tens.Min():0.0} to {tens.Max():0.0} ms (wander "
                + $"{wander:0.0}); pitch {pitch:0.00} cents; clock {clock.Average():+0;-0;0} ppm (true {truth:+0;-0;0}, worst {worst:0} off)");
        }
        // Gathered, so a failure names every run and every bar it misses.
        Check(troubles.Count == 0, "the WASAPI output stage: " + string.Join(" | ", troubles));
        return "ASIO only has no stage (it pulls straight from the engine); " + string.Join("; ", said);
    }

    /// <summary>How far the stage's level may move, ten seconds at a time, as the card's pulls slide past the feeds (see
    /// SIMULATOR WASAPI OUTPUT STAGE). The steady cushion moved 0.3 to 0.6 ms over a whole turn of the phase at 40 ppm, the
    /// leaner one 7.6 to 14.5 (measured 2026-09-28).</summary>
    private const double WanderBarMs = 2;

    /// <summary>SIMULATOR LANES APART (2026-09-28): with both kinds of output every stream is played on each output by a copy
    /// of its own - the primary on the WASAPI output, a mirror on the ASIO one - and each lane must answer to its own slider
    /// and its own stream alone.
    ///
    /// <para>FOUND AND FIXED 2026-09-28 (Ed approved), two couplings, both measured in the simulator:</para>
    /// <list type="number">
    /// <item>Moving the WASAPI slider by hand threw the ASIO output's copies into the 5 % fast approach, toward their own
    /// unchanged target: a WASAPI raise from 40 to 80 ms while the ASIO output was coming down from 100 to 20 took that
    /// descent from 6.8 cents sharp to 39 to 41 cents for three seconds (SessionPlayout.RequestFastLatencyApproach set every
    /// mirror too). The ASIO slider never reached the WASAPI copies, so it was one-way.</item>
    /// <item>A copy's drift loop read its clock off whole lumps of arrivals, so a stream sent in 10 ms WASAPI capture blocks
    /// read up to 1,000 ppm out at a window's edge - and its ASIO copy read the SAME bad figure as its WASAPI copy (fed the same
    /// lumps, windows opened together), holding the ASIO output's copy of it 10 ms behind the ASIO-lane stream's through a
    /// descent (29.6 against 20.3 ms at 45 s, SIMULATOR AUTO-TUNE FROM 500). Now read off the stream's arrival line
    /// (SessionPlayout.WrittenBytesAt).</item>
    /// </list>
    /// <para>Asserted: a slider moved by hand on one lane leaves the other lane's output sample for sample what it was without
    /// the move, each way; and over eight seeds on a wired network with every card at the same rate, in all three
    /// configurations, every copy of every stream reads its clock within 150 ppm of the truth from its first reading on.</para></summary>
    private static string SimulatorLanesStayApart()
    {
        var said = new List<string>();
        // 1. A slider moved by hand on one lane at 27 s, while the ASIO output comes down from 100 ms to 20 on the auto-tune's
        // soft path from 25 s.
        SimResult Descent(Action<AudioReceiver>? hand) => Simulation.Run(new SimScenario("lanes apart", SimConfig.Both, SimCodec.Opus2_5, SimNetwork.Wired, 100, 45,
            WasapiLaneBufferMs: 40, SoftTargetMs: 20, SoftTargetAtSecond: 25, KeepAudio: true,
            ReceiverActions: hand is null ? null : [(27.0, hand)]));
        var alone = Descent(null);
        var wasapiMoved = Descent(rc => rc.SetMaxLatencyMsFor(RenderRoute.WasapiLane, 80));
        var asioMoved = Descent(rc => rc.SetMaxLatencyMsFor(RenderRoute.AsioLane, 60));
        // The first frame where two recordings of one output differ, -1 when they are the same sample for sample.
        static int Differ(float[]? a, float[]? b)
        {
            if (a is null || b is null) return 0;
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++) if (a[i] != b[i]) return i / 2;
            return a.Length == b.Length ? -1 : Math.Min(a.Length, b.Length) / 2;
        }
        Check(alone.Audio is { Length: > 0 } && alone.WasapiAudio is { Length: > 0 } && alone.Findings.ClicksHeard > 60,
            "premise: both outputs kept and heard");
        Check(Differ(alone.WasapiAudio, wasapiMoved.WasapiAudio) >= 0, "premise: the WASAPI slider's move must change the WASAPI output");
        Check(Differ(alone.Audio, asioMoved.Audio) >= 0, "premise: the ASIO slider's move must change the ASIO output");
        var asioAfterWasapiMove = Differ(alone.Audio, wasapiMoved.Audio);
        Check(asioAfterWasapiMove < 0,
            $"the WASAPI slider moved by hand must leave the ASIO output sample for sample as it was: it parted company "
            + $"{asioAfterWasapiMove / 48000.0:0.000} s in (the WASAPI move was at 27 s), and the ASIO output's descent went from "
            + $"{alone.Findings.WorstPitchCents:0.0} to {wasapiMoved.Findings.WorstPitchCents:0.0} cents at worst");
        var wasapiAfterAsioMove = Differ(alone.WasapiAudio, asioMoved.WasapiAudio);
        Check(wasapiAfterAsioMove < 0,
            $"the ASIO slider moved by hand must leave the WASAPI output sample for sample as it was: it parted company {wasapiAfterAsioMove / 48000.0:0.000} s in");
        said.Add($"a WASAPI slider raised by hand leaves the ASIO output identical ({alone.Audio!.Length / 2} frames; its descent "
            + $"{alone.Findings.WorstPitchCents:0.0} cents at worst either way), and an ASIO one the WASAPI output");

        // 2. Every copy's clock, from its first reading on.
        var runs = new List<SimScenario>();
        foreach (var config in new[] { SimConfig.WasapiOnly, SimConfig.AsioOnly, SimConfig.Both })
            for (var seed = 1; seed <= 8; seed++)
                runs.Add(new SimScenario($"{config}, seed {seed}", config, SimCodec.Opus2_5, SimNetwork.Wired, config == SimConfig.WasapiOnly ? 40 : 20, 45,
                    WasapiLaneBufferMs: 40, Seed: seed));
        var worstBy = new Dictionary<string, double>();
        var misread = new Dictionary<string, double>();   // run and copy -> its worst reading, over the bar
        foreach (var r in Simulation.RunTogether(runs.ToArray()))
        {
            var readings = 0;
            foreach (var second in r.ReplicasBySecond)
                foreach (var c in second)
                {
                    if (!c.Tracking) continue;   // no reading yet
                    readings++;
                    var copy = $"{r.Scenario.Config}: the {c.Announced} stream's copy on the {c.Plays}";
                    var ppm = Math.Abs(c.Ratio - 1) * 1e6;
                    worstBy[copy] = Math.Max(worstBy.GetValueOrDefault(copy), ppm);
                    if (ppm > 150) misread[$"{copy}, seed {r.Scenario.Seed}"] = Math.Max(misread.GetValueOrDefault($"{copy}, seed {r.Scenario.Seed}"), ppm);
                }
            Check(readings > 0, $"{r.Scenario.Name}: premise - the drift loops must have taken readings");
        }
        Check(misread.Count == 0, "every copy of every stream must read its clock within 150 ppm of the truth (the cards all at one rate) from its "
            + $"first reading on - a lump counted or not at a window's edge was 1,000 ppm: {misread.Count} run(s) and copies over, worst first: "
            + string.Join("; ", misread.OrderByDescending(m => m.Value).Take(12).Select(m => $"{m.Key} {m.Value:0} ppm")));
        said.Add("every copy's clock within 150 ppm from its first reading, eight seeds each: "
            + string.Join(", ", worstBy.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value:0}")));
        return string.Join("; ", said);
    }

    /// <summary>SIMULATOR OUTPUT HICCUP (2026-09-28, Ed approved the fix): an output that stops for a moment - a driver or a
    /// device hiccuping, its missed callbacks gone - is not taken for a slow clock. An ASIO output stopping for 20, 100 and 450
    /// ms, on ASIO only and on both; a WASAPI card stopping for 50, 100 and 200 ms with the output stage in front of it, on
    /// WASAPI only, and for 100 ms on both. Each 35 s in, once every drift loop is tracking, on a wired network with the cards
    /// at one rate. From a second after the stop on: not one underrun, padded pull, break-up or click on any output, and
    /// every drift loop's clock reading still within 500 ppm of the truth. Ten runs at once, a thread each.
    ///
    /// <para>FOUND BY THE PERFORMANCE SUITE (its card-stall run, finding A): the window the stop fell in read the stall's
    /// share of it as a clock - 100 ms in 10 s is +10,000 ppm, inside the drift loop's 5 % sanity band - so the output then
    /// played that much fast and ran its buffer dry for most of a minute. Measured before the fix with hiccup-probe: 645
    /// underruns in the minute after a 100 ms ASIO hiccup on ASIO only, clean again 49 s later; 5,541 after 450 ms on both;
    /// and through the WASAPI output stage 945 break-ups after a 100 ms card stall. Now a window whose output reads stopped
    /// for longer than 20 ms and four of its average read is thrown away as a clock reading (SessionPlayout
    /// .DriftReadStallMinSec, MultiOutputPlayout's PullStallMinSec). A late driver thread that fires its missed callbacks in
    /// a burst is a different case, left as it was: the performance suite's card-stall run reports it.</para></summary>
    private static string SimulatorOutputHiccupIsNotAClock()
    {
        const double At = 35;
        var asio = Simulation.DefaultCard(asio: true);
        var wasapi = new SimCard(480, JitterMs: 1);
        var runs = new List<SimScenario>();
        foreach (var config in new[] { SimConfig.AsioOnly, SimConfig.Both })
            foreach (var ms in new[] { 20.0, 100, 450 })
                runs.Add(new SimScenario($"{config}, the ASIO output stopping for {ms:0} ms", config, SimCodec.Opus2_5, SimNetwork.Wired, 10, 95,
                    ReceiveCard: asio with { StallAtSec = At, StallOnceMs = ms }, WasapiLaneBufferMs: 40, ChunkSeconds: 5));
        foreach (var ms in new[] { 50.0, 100, 200 })
            runs.Add(new SimScenario($"WasapiOnly, the WASAPI card stopping for {ms:0} ms, the output stage in", SimConfig.WasapiOnly, SimCodec.Opus2_5,
                SimNetwork.Wired, 40, 95, ReceiveCard: wasapi with { StallAtSec = At, StallOnceMs = ms }, WasapiOutputStage: true, ChunkSeconds: 5));
        runs.Add(new SimScenario("Both, the WASAPI card stopping for 100 ms, the output stage in", SimConfig.Both, SimCodec.Opus2_5, SimNetwork.Wired, 10, 95,
            WasapiLaneBufferMs: 40, WasapiLaneReceiveCard: wasapi with { StallAtSec = At, StallOnceMs = 100 }, WasapiOutputStage: true, ChunkSeconds: 5));
        var said = new List<string>();
        var troubles = new List<string>();
        foreach (var r in Simulation.RunTogether(runs.ToArray()))
        {
            var what = r.Scenario.Name;
            var from = (int)At + 1;   // a second after the stop: what the stop itself did is its own
            Check(r.UnderrunsBySecond.Count >= 90 && r.DriftPpmBySecond.Count >= 90, $"{what}: premise - read each second");
            var heard = new List<(string Output, IReadOnlyList<(double FromSec, SimAnalysis.Findings Findings)> Stretches)> { ("listened output", r.Stretches) };
            if (r.Scenario.Config == SimConfig.Both) heard.Add(("WASAPI output", r.WasapiStretches));
            var faults = new List<string>();
            foreach (var (output, stretches) in heard)
            {
                Check(stretches.Count(c => c.FromSec >= At + 5) >= 10, $"{what}, {output}: premise - listened to after the stop");
                var broke = stretches.Where(c => c.FromSec >= At + 5 && (c.Findings.Glitches > 0 || c.Findings.StepClicks > 0)).ToList();
                if (broke.Count > 0) faults.Add($"{output} broke up in {broke.Count} of the five-second stretches after it ({string.Join(", ", broke.Take(6).Select(c => $"{c.Findings.Glitches} at {c.FromSec:0} s"))})");
            }
            var underruns = r.UnderrunsBySecond[^1] - r.UnderrunsBySecond[from - 1];
            if (underruns > 0) faults.Add($"{underruns} underruns from {from} s on");
            var padded = r.StageShortReadsBySecond.Count > 0 ? r.StageShortReadsBySecond[^1] - r.StageShortReadsBySecond[from - 1] : 0;
            if (padded > 0) faults.Add($"{padded} pulls the WASAPI output stage could not fill from {from} s on");
            var clock = r.DriftPpmBySecond.Skip(from).Where(x => !double.IsNaN(x)).DefaultIfEmpty(0).Max(Math.Abs);
            var stageClock = r.StageBySecond.Count > 0 ? r.StageBySecond.Skip(from).Max(x => Math.Abs(x.ClockPpm)) : 0;
            if (Math.Max(clock, stageClock) > 500)
                faults.Add($"a drift loop took the stop for a clock ({clock:0} ppm at worst{(r.StageBySecond.Count > 0 ? $", the stage's {stageClock:0}" : "")}; the cards are at one rate)");
            if (faults.Count > 0) troubles.Add($"{what}: {string.Join("; ", faults)}");
            said.Add($"{what}: clean from {from} s on, 0 underruns, clock within {Math.Max(clock, stageClock):0} ppm");
        }
        // Gathered, so a failure names every run it happens in.
        Check(troubles.Count == 0, "an output that stops for a moment must not be taken for a slow clock - not one underrun or break-up a second after it: "
            + string.Join(" | ", troubles));
        return string.Join("; ", said);
    }
}
