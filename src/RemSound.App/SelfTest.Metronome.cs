using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// THE METRONOME (Ed, 2026-09-28: "the main thing is making the design rock solid so it all lines up"). The parts that
/// decide WHEN a click is heard, each measured: the timeline every metronome plays from, the clock estimate that maps a
/// follower's clock onto the master's, the sealed type-12 messages that carry them, and the placing of each click into an
/// output's block to the sample. PLAN-metronome-2026-09-28.md holds every decision.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>METRONOME TIMELINE: a start puts beat 0 the lead ahead; a tempo change lands on the first bar line at least
    /// the lead ahead with the beat count carried on, the beats before it still at the old tempo; a late joiner - on a clock
    /// of its own - places the very beat the master is on; 6/8 and 7/8 accent the first of every 6 and 7; stop and start
    /// raise the version; a state survives the wire.</summary>
    private static string? MetronomeTimelineRules()
    {
        var t = MetronomeTimeline.Stopped().Start(1_000_000);
        Check(t is { Running: true, AnchorBeat: 0, AnchorTimeUs: 1_400_000, Version: 1 }, $"start: beat 0 the lead (400 ms) ahead ({t.Describe()})");
        Check(Math.Abs(t.BeatTimeUs(1) - 1_900_000) < 1e-6 && Math.Abs(t.BeatTimeUs(8) - 5_400_000) < 1e-6, "at 120 bpm the beats are 500 ms apart");
        Check(t.IsAccent(0) && !t.IsAccent(1) && !t.IsAccent(3) && t.IsAccent(4) && t.IsAccent(8), "4/4: the first of every four is the accent");
        Check(t.Start(9_000_000) == t, "starting what is running changes nothing");

        // A change at 3.05 s: the first beat at least 400 ms ahead is beat 5 (3.9 s), not a bar line; the bar line is beat 8.
        var changed = t.Change(3_050_000, 90_000, new MetronomeSignature(4, 4));
        Check(changed is { AnchorBeat: 8, AnchorTimeUs: 5_400_000, MilliBpm: 90_000, Version: 2 },
            $"a tempo change must land on the next bar line at least 400 ms ahead, the count carried on ({changed.Describe()})");
        Check(Math.Abs(changed.BeatTimeUs(9) - (5_400_000 + 60e6 / 90)) < 1e-6, "and play the new tempo from there");
        var schedule = new MetronomeSchedule(changed, t, MetronomeClockMap.Identity, null, 1, false, 1);
        Check(schedule.TryBeat(6, out var b6, out _) && Math.Abs(b6 - 4_400_000) < 1e-6 && schedule.TryBeat(7, out var b7, out _) && Math.Abs(b7 - 4_900_000) < 1e-6,
            "the beats before the change's bar line are still at the old tempo");
        Check(schedule.TryBeat(8, out var b8, out var a8) && Math.Abs(b8 - 5_400_000) < 1e-6 && a8, "and the bar line itself is the new timeline's first beat, accented");
        Check(schedule.FirstBeatAtOrAfter(4_000_000) == 6 && schedule.FirstBeatAtOrAfter(5_500_000) == 9, "the beat at or after a moment is found in whichever timeline it falls");
        var joinedDuring = new MetronomeSchedule(changed, null, MetronomeClockMap.Identity, null, 1, false, 1);
        Check(!joinedDuring.TryBeat(7, out _, out _), "a follower that knows only the new timeline plays nothing before its bar line - silence, not a guess");
        var sig = changed.Change(6_000_000, 90_000, new MetronomeSignature(3, 4));
        Check(sig.AnchorBeat == 12 && sig.IsAccent(12) && sig.IsAccent(15) && !sig.IsAccent(14), $"a signature change starts its bars at the bar line ({sig.Describe()})");

        // 6/8 and 7/8.
        var six = MetronomeTimeline.Stopped(120_000, new MetronomeSignature(6, 8)).Start(0);
        Check(Math.Abs(six.BeatIntervalUs - 500_000) < 1e-6, "6/8 at 120 is eighth notes at 120 a minute");
        Check(Enumerable.Range(0, 18).Where(n => six.IsAccent(n)).SequenceEqual([0, 6, 12]), "6/8 accents the first of every six");
        var seven = MetronomeTimeline.Stopped(120_000, new MetronomeSignature(7, 8)).Start(0);
        Check(Enumerable.Range(0, 21).Where(n => seven.IsAccent(n)).SequenceEqual([0, 7, 14]) && seven.BeatInBar(9) == 3, "7/8 accents the first of every seven");
        Check(MetronomeSignature.Offered.Select(s => s.ToString()).SequenceEqual(["2/4", "3/4", "4/4", "5/4", "6/8", "7/8", "9/8", "12/8"]),
            "the window offers 2/4, 3/4, 4/4, 5/4, 6/8, 7/8, 9/8 and 12/8");

        // A late joiner, its clock 1234.567 ms behind the master's, arrives 10.3 s in: it lands on the master's beat.
        var run = MetronomeTimeline.Stopped(100_000, new MetronomeSignature(4, 4)).Start(0);   // 600 ms beats from 0.4 s
        var map = new MetronomeClockMap(1_234_567, 0, 0);
        var lane = new MetronomeClickLane("late joiner");
        var sound = new MetronomeSound("test", Impulse(0.9f), Impulse(0.5f));
        var late = new MetronomeSchedule(run, null, map, sound, 1, false, 7);
        var block = new float[480 * 2];
        var localNow = 10_300_000 - 1_234_567.0;
        var placed = -1L;
        for (var i = 0; i < 200 && placed < 0; i++)
        {
            Array.Clear(block);
            lane.Mix(block, 2, 48000, localNow + i * 10_000, 0, late);
            if (lane.OnsetsPlaced > 0) placed = lane.LastOnsetBeat;
        }
        Check(placed == 17 && !lane.LastOnsetAccent && Math.Abs(lane.LastOnsetMasterUs - run.BeatTimeUs(17)) < 1e-6,
            $"a late joiner must land on the beat the master is on - beat 17 at {run.BeatTimeUs(17) / 1e6:0.000} s on the master's clock (placed beat {placed})");

        Check(t.Stop() is { Running: false, Version: 2 } s && s.Start(20_000_000) is { Running: true, Version: 3, AnchorBeat: 0 }, "stop and start each raise the version, and a start counts from 0");
        return "start 400 ms ahead; a change at the next bar line with the count carried on and the beats before it at the old tempo; "
             + "a late joiner on the master's beat; 6/8 and 7/8 accents; versions raised";
    }

    /// <summary>A click that is a single loud sample then silence: its onset is unmistakable.</summary>
    private static float[] Impulse(float level)
    {
        var s = new float[2 * 64];
        s[0] = s[1] = level;
        for (var i = 1; i < 32; i++) s[2 * i] = s[2 * i + 1] = level * 0.01f;
        return s;
    }

    /// <summary>
    /// METRONOME CLOCK ESTIMATE (the plan's test, measured): a follower whose clock is 1234.567 ms behind the master's and 50
    /// ppm slow, a path with random queueing on both legs and 2 ms more one way than the other, requests every 250 ms for 3 s
    /// and then every second. After two minutes the offset is right to within half the asymmetry plus 0.2 ms at every
    /// moment it is asked, the rate to within 5 ppm; and a burst of replies held up on one leg alone does not move it.
    /// </summary>
    private static string? MetronomeClockEstimate()
    {
        const double trueOffset = 1_234_567;   // master minus follower at follower time 0
        const double skew = 50e-6;
        const double asymmetryUs = 2000;
        var rnd = new Random(20260928);
        double Master(double local) => local + trueOffset + skew * local;
        double Exp(double mean) => -mean * Math.Log(1 - rnd.NextDouble());
        var est = new MetronomeClockEstimator();
        double now = 5_000_000;
        var worstAfter30 = 0.0;
        var exchanges = 0;
        void Exchange(double extraForward = 0, double extraBack = 0)
        {
            var forward = 300 + asymmetryUs + Exp(1500) + extraForward;
            var back = 300 + Exp(1500) + extraBack;
            var t0 = now;
            var t1 = Master(t0 + forward);
            var t2 = t1 + 40;
            var t3 = t0 + forward + 40 + back;
            est.Add((long)t0, (long)t1, (long)t2, (long)t3);
            exchanges++;
        }
        double Error(double local) => est.Map.LocalToMaster(local) - Master(local);
        var start = now;
        while (now - start < 120_000_000)
        {
            Exchange();
            // Now and again a reply is badly late, as on a real network.
            if (rnd.NextDouble() < 0.05) Exchange(extraBack: Exp(30_000));
            now += now - start < 3_000_000 ? 250_000 : 1_000_000;
            if (now - start > 30_000_000) worstAfter30 = Math.Max(worstAfter30, Math.Abs(Error(now)));
        }
        var bound = asymmetryUs / 2 + 200;
        var err = Error(now);
        Check(est.Settled && est.SkewMeasured, $"after two minutes the estimate must be settled and have measured the rate ({est.Describe()})");
        Check(Math.Abs(err) <= bound, $"THE OFFSET: within half the asymmetry plus 0.2 ms ({bound / 1000:0.000} ms) - {err / 1000:+0.000;-0.000} ms ({est.Describe()})");
        Check(worstAfter30 <= bound, $"and at every moment from 30 s on ({worstAfter30 / 1000:0.000} ms at worst)");
        Check(Math.Abs(est.SkewPpm - 50) <= 5, $"THE RATE: within 5 ppm of the 50 ppm difference ({est.SkewPpm:0.00} ppm)");
        Check(Math.Abs(Error(now + 10_000_000)) <= bound + 100, $"carried 10 s on at that rate, still within the bound ({Error(now + 10_000_000) / 1000:+0.000;-0.000} ms)");

        // A burst: twenty replies in a row held up 40 ms on the way back only (half of that would be 20 ms of error).
        var errBeforeBurst = Error(now);
        for (var i = 0; i < 20; i++) { Exchange(extraBack: 40_000); now += 1_000_000; }
        var errAfterBurst = Error(now);
        Check(Math.Abs(errAfterBurst - errBeforeBurst) <= 100 && Math.Abs(errAfterBurst) <= bound,
            $"A BURST of twenty replies 40 ms late one way, a second apart, must not move it ({errBeforeBurst / 1000:+0.000;-0.000} ms before, {errAfterBurst / 1000:+0.000;-0.000} ms after)");
        Check(Math.Abs(est.SkewPpm - 50) <= 5, $"nor the rate ({est.SkewPpm:0.00} ppm)");

        // Nonsense is refused: a reply that arrives before it was asked for.
        Check(!est.Add(1000, 5, 6, 500), "an exchange with a negative round trip must be refused");

        // Early on, before the rate is known: only the last 8 s is chosen from, so an unknown 50 ppm costs 0.4 ms at most.
        var measuredSkew = est.SkewPpm;
        var early = new MetronomeClockEstimator();
        est = early;
        now = 2_000_000;
        var start2 = now;
        var worstEarly = 0.0;
        while (now - start2 < 15_000_000)
        {
            Exchange();
            now += now - start2 < 3_000_000 ? 250_000 : 1_000_000;
            if (early.Settled) worstEarly = Math.Max(worstEarly, Math.Abs(Error(now)));
        }
        Check(worstEarly <= asymmetryUs / 2 + 600, $"before the rate is measured, within half the asymmetry plus 0.6 ms from the fourth sample on ({worstEarly / 1000:0.000} ms)");
        return $"offset within {Math.Abs(err) / 1000:0.000} ms (bound {bound / 1000:0.0} ms: half the 2 ms asymmetry plus 0.2), worst {worstAfter30 / 1000:0.000} ms from 30 s, "
             + $"rate {measuredSkew:0.0} ppm of 50 measured over 2 minutes; a 40 ms one-way burst moved it {(errAfterBurst - errBeforeBurst) / 1000:+0.000;-0.000} ms; "
             + $"before the rate was known, {worstEarly / 1000:0.000} ms at worst ({exchanges} exchanges)";
    }

    /// <summary>METRONOME MESSAGES: every kind builds a type-12 packet with stream 0xFFFF and opens with the password to
    /// exactly what was sealed; another password, a changed byte, another version, an unknown kind and a cut-short one are
    /// refused; the guard refuses a replay; a long name is cut on a character boundary.</summary>
    private static string? MetronomeMessagesSealed()
    {
        var key = Require(RemSoundCrypto.ForPlainPassword("metronome-password").Key, "a key");
        var other = Require(RemSoundCrypto.ForPlainPassword("another-password").Key, "another key");
        var session = MetronomeWire.NewSessionId();
        var state = new MetronomeTimeline(7, true, 97_500, 7, 8, 1234, 9_876_543_210);
        MetronomeMessage[] all =
        [
            new(MetronomeKind.Offer, session) { MasterName = "Ed's desktop" },
            new(MetronomeKind.Accept, session),
            new(MetronomeKind.Decline, session),
            new(MetronomeKind.Release, session),
            new(MetronomeKind.State, session) { State = state },
            new(MetronomeKind.SyncRequest, session) { Seq = 42, T0 = 111_111_111 },
            new(MetronomeKind.SyncReply, session) { Seq = 42, T0 = 111_111_111, T1 = 222_222_222, T2 = 222_222_262 },
            new(MetronomeKind.Leave, session),
        ];
        var guard = new MetronomeReplayGuard();
        foreach (var m in all)
        {
            var packet = MetronomeWire.BuildPacket(key, m, 5);
            Check(RemPacket.TryReadHeader(packet, out var type, out var stream, out _) && type == RemPacketType.Metronome && (byte)type == 12 && stream == 0xFFFF,
                $"{m.Kind}: type 12, stream 0xFFFF");
            var payload = packet.AsSpan(RemPacket.HeaderSize).ToArray();
            Check(payload.Length >= MetronomeWire.MinSealedBytes && payload.Length <= MetronomeWire.MaxSealedBytes, $"{m.Kind}: a size the receiver's gate lets through ({payload.Length})");
            Check(guard.TryAccept(key, payload, out var opened, out var why) && opened == m, $"{m.Kind}: must open to exactly what was sealed ({why}; {opened})");
            Check(!guard.TryAccept(key, payload, out _, out var replay) && replay.Contains("replay", StringComparison.Ordinal), $"{m.Kind}: a replay must be refused ({replay})");
            Check(!MetronomeWire.TryOpen(other, payload, out _, out _), $"{m.Kind}: another password must not open it");
            var tampered = (byte[])payload.Clone();
            tampered[^20] ^= 1;
            Check(!MetronomeWire.TryOpen(key, tampered, out _, out _), $"{m.Kind}: a changed byte must not open");
        }
        Check(MetronomeWire.Plaintext(all[4]).Length == 37 && MetronomeWire.Plaintext(all[5]).Length == 22 && MetronomeWire.Plaintext(all[6]).Length == 38
              && MetronomeWire.Plaintext(all[1]).Length == 10, "the documented sizes: state 37, sync request 22, sync reply 38, the rest 10 bytes of plaintext");
        var wrongVersion = MetronomeWire.Plaintext(all[1]);
        wrongVersion[0] = 2;
        Check(!MetronomeWire.TryOpen(key, RemSoundCrypto.Encrypt(key, wrongVersion), out _, out _), "another format version must be refused");
        var unknownKind = MetronomeWire.Plaintext(all[1]);
        unknownKind[1] = 99;
        Check(!MetronomeWire.TryOpen(key, RemSoundCrypto.Encrypt(key, unknownKind), out _, out _), "an unknown kind must be refused");
        var shortState = MetronomeWire.Plaintext(all[4])[..^1];
        Check(!MetronomeWire.TryOpen(key, RemSoundCrypto.Encrypt(key, shortState), out _, out _), "a state one byte short must be refused");
        var longer = MetronomeWire.Plaintext(all[5]).Concat(new byte[5]).ToArray();
        Check(MetronomeWire.TryOpen(key, RemSoundCrypto.Encrypt(key, longer), out var extended, out _) && extended!.Seq == 42,
            "bytes after what a kind needs are ignored, as every RemSound reader does");
        var name = new string('é', 40);   // 80 bytes of UTF-8
        var cut = MetronomeWire.NameBytes(name);
        Check(cut.Length == 64 && System.Text.Encoding.UTF8.GetString(cut) == new string('é', 32), $"a long name must be cut to 64 bytes on a character boundary ({cut.Length})");
        return "all eight kinds sealed as type 12, opened exactly, and refused on replay, another password or a changed byte; versions, kinds "
             + "and short messages refused; trailing bytes ignored; names cut on a character";
    }

    /// <summary>
    /// METRONOME CLICK PLACEMENT (measured to the sample): an output's render call with a known ear time puts each click's
    /// onset on round((t - earTime) * rate). ASIO (64-frame blocks, the driver's stated delay), WASAPI (480-frame blocks,
    /// the stage's measured delay) and both at once (each output its own delay, the same beat heard at the same moment).
    /// A click crossing a block edge plays whole; a render thread woken late or early by up to half a millisecond moves no
    /// onset by more than a tenth of one.
    /// </summary>
    private static string? MetronomeClickPlacement()
    {
        var sound = new MetronomeSound("test", Impulse(0.8f), Impulse(0.4f));
        var run = MetronomeTimeline.Stopped(137_000, new MetronomeSignature(3, 4)).Start(1_000_000);
        var said = new List<string>();
        foreach (var configuration in AudioConfigurations.All)
        {
            // Each output this configuration has: its block size and its delay.
            var outputs = new List<(string Name, int Block, double DelayUs)>();
            if (configuration.UsesAsio()) outputs.Add(("ASIO", 64, 5_333));
            if (configuration.UsesWasapi()) outputs.Add(("WASAPI", 480, 23_700));
            var heard = new List<List<double>>();
            foreach (var (name, blockFrames, delay) in outputs)
            {
                var lane = new MetronomeClickLane(name);
                var schedule = new MetronomeSchedule(run, null, MetronomeClockMap.Identity, sound, 1, false, 1);
                var onsets = new List<double>();
                var mismatches = new List<string>();
                var block = new float[blockFrames * 2];
                var nowUs = 1_000_000.0;
                for (var i = 0; i < 48000 * 4 / blockFrames; i++)
                {
                    Array.Clear(block);
                    var ear = nowUs + delay;
                    lane.Mix(block, 2, 48000, nowUs, delay, schedule);
                    for (var f = 0; f < blockFrames; f++)
                    {
                        if (block[2 * f] < 0.39f) continue;
                        var at = ear + f * 1e6 / 48000;
                        onsets.Add(at);
                        var beat = run.BeatAtOrAfter(at - 30);
                        var expected = (int)Math.Round((run.BeatTimeUs(beat) - ear) * 48000 / 1e6);
                        if (expected != f) mismatches.Add($"beat {beat} at frame {f}, expected {expected}");
                        var accent = run.IsAccent(beat);
                        if (Math.Abs(block[2 * f] - (accent ? 0.8f : 0.4f)) > 1e-6) mismatches.Add($"beat {beat} {(accent ? "accent" : "beat")} level {block[2 * f]}");
                    }
                    nowUs += blockFrames * 1e6 / 48000;
                }
                Check(mismatches.Count == 0, $"{configuration.Describe()}, {name}: every onset on its exact sample: {string.Join("; ", mismatches.Take(4))}");
                var expectedCount = Enumerable.Range(0, 100).Count(n => run.BeatTimeUs(n) >= 1_000_000 + delay && run.BeatTimeUs(n) < nowUs + delay);
                Check(onsets.Count == expectedCount, $"{configuration.Describe()}, {name}: one onset a beat, none missed or doubled ({onsets.Count} of {expectedCount})");
                heard.Add(onsets);
            }
            if (heard.Count == 2)
            {
                var worst = heard[0].Zip(heard[1], (a, b) => Math.Abs(a - b)).Max();
                Check(worst <= 1e6 / 48000, $"{configuration.Describe()}: each output's delay its own, the same beat heard within a sample on both ({worst:0.0} us)");
            }
            said.Add($"{configuration.Describe()}: {string.Join(", ", outputs.Select(o => o.Name))} exact");
        }

        // A click across a block edge plays whole: the sum of two blocks is the sound itself.
        var whole = new MetronomeSound("long", Enumerable.Range(0, 400).SelectMany(i => new[] { 0.5f - i / 1000f, 0.25f }).ToArray(), Impulse(0.4f));
        var edge = new MetronomeClickLane("edge");
        var at0 = MetronomeTimeline.Stopped().Start(0);   // beat 0 at 400 ms
        var s0 = new MetronomeSchedule(at0, null, MetronomeClockMap.Identity, whole, 1, false, 1);
        var joined = new List<float>();
        var ear0 = 400_000.0 - 100 * 1e6 / 48000;          // beat 0 at frame 100 of the first block
        for (var i = 0; i < 3; i++)
        {
            var b = new float[256 * 2];
            edge.Mix(b, 2, 48000, ear0 + i * 256 * 1e6 / 48000, 0, s0);
            joined.AddRange(b);
        }
        var start = joined.FindIndex(v => v != 0) / 2;
        var played = joined.Skip(start * 2).Take(whole.Accent.Length).ToArray();
        Check(start == 100 && played.SequenceEqual(whole.Accent), $"a click crossing two block edges must play whole, from its onset (onset frame {start})");

        // A jittery render thread: now read up to half a millisecond early or late; the ear clock holds the onsets.
        var jitterLane = new MetronomeClickLane("jitter");
        var js = new MetronomeSchedule(run, null, MetronomeClockMap.Identity, sound, 1, false, 1);
        var rnd = new Random(12);
        var worstJitter = 0.0;
        var nominal = 1_000_000.0;
        for (var i = 0; i < 48000 * 6 / 480; i++)
        {
            var b = new float[480 * 2];
            var wobble = (rnd.NextDouble() - 0.5) * 1000;
            jitterLane.Mix(b, 2, 48000, nominal + wobble, 10_000, js);
            for (var f = 0; f < 480; f++)
            {
                if (b[2 * f] < 0.39f || i < 100) continue;
                var at = nominal + 10_000 + f * 1e6 / 48000;
                var beat = run.BeatAtOrAfter(at - 1000);
                worstJitter = Math.Max(worstJitter, Math.Abs(at - run.BeatTimeUs(beat)));
            }
            nominal += 480 * 1e6 / 48000;
        }
        Check(worstJitter <= 100, $"with the render thread waking up to 0.5 ms early or late, no onset may move more than 0.1 ms ({worstJitter:0.0} us at worst)");
        return string.Join("; ", said) + $"; a click across block edges whole; a jittery thread moved onsets {worstJitter:0.0} us at most";
    }

    /// <summary>
    /// SIMULATOR METRONOME (the plan's simulator test - measure, don't reason): two simulated machines, the follower's clock
    /// 1234.567 ms off and 50 ppm fast, their sound cards 30 ppm fast and 40 ppm slow, output callbacks woken up to half a
    /// millisecond late, and a network with jitter and a path 2 ms slower from the master than back. Every click each card
    /// played is found in its audio and the moment it was heard compared: the master's and the follower's outputs agree
    /// within half the asymmetry plus half a millisecond on every beat, none missing or doubled - WASAPI only, ASIO only and
    /// both (both outputs of one machine agreeing too). And nothing is ever sent: each machine sends a tone and a loopback of
    /// a WASAPI output of its own, the click kept off that one, and what went out is the tone, sample for sample.
    /// </summary>
    private static string? SimulatorMetronomeLinesUp()
    {
        var said = new List<string>();
        foreach (var configuration in AudioConfigurations.All)
        {
            var config = configuration == AudioConfiguration.WasapiOnly ? Simulator.SimConfig.WasapiOnly
                : configuration == AudioConfiguration.AsioOnly ? Simulator.SimConfig.AsioOnly : Simulator.SimConfig.Both;
            var r = Simulator.SimMetronome.Run(new Simulator.SimMetronomeScenario(config));
            var bound = r.Scenario.AsymmetryMs / 2 + 0.5;
            var what = configuration.Describe();
            Check(r.BeatsCompared >= 100, $"{what}: premise - both machines played the beats ({r})");
            Check(r.Unmatched == 0, $"{what}: every beat on one machine must be on the other, none missing or doubled ({r})");
            Check(r.WorstMs <= bound, $"{what}: THE CLICKS LINE UP - every beat heard within {bound:0.0} ms on both machines ({r})");
            Check(r.SentSamples > Simulator.Simulation.SampleRate * 2 * 50 && r.SentMismatches == 0, $"{what}: NOTHING SENT - what went out must be the capture's tone, sample for sample ({r})");
            if (configuration.UsesWasapi()) Check(r.LoopedOutputClicks == 0, $"{what}: the output sent as loopback must never click ({r})");
            said.Add($"{what}: worst {r.WorstMs:0.000} ms ({r.WorstOnceRateKnownMs:0.000} ms once the rate is known), median {r.MedianMs:0.000} ms over {r.BeatsCompared} beats, "
                     + $"the follower's estimate {r.EstimateErrorAll} off the truth; {r.SentSamples} sent samples all the tone");
        }
        return string.Join("; ", said) + $" (bound {2 / 2.0 + 0.5:0.0} ms: half the 2 ms asymmetry plus 0.5)";
    }

    /// <summary>METRONOME SOUNDS: the five shipped pairs are listed in Ed's order, each loads as stereo 48 kHz and starts on
    /// its first sample, the accent louder than the beat; a person's own "name accent.wav" + "name beat.wav", or one
    /// "name.wav", appears in the list and replaces a shipped one of the same name.</summary>
    private static string? MetronomeSoundsLoad()
    {
        var names = MetronomeSoundLibrary.Names();
        Check(names.Take(5).SequenceEqual(MetronomeSoundLibrary.ShippedNames), $"the shipped sounds, in Ed's order: {string.Join(", ", names)}");
        foreach (var name in MetronomeSoundLibrary.ShippedNames)
        {
            var s = Require(MetronomeSoundLibrary.Load(name), $"{name} must load");
            Check(Math.Abs(s.Accent[0]) > 0.05f && Math.Abs(s.Beat[0]) > 0.05f, $"{name}: the click must start on its first sample ({s.Accent[0]:0.000}, {s.Beat[0]:0.000})");
            Check(s.Accent.Max(Math.Abs) > s.Beat.Max(Math.Abs), $"{name}: the accent must be louder than the beat");
            Check(s.Accent.Length / 2 < MetronomeSound.SampleRate / 3, $"{name}: a click, not a note ({s.Accent.Length / 2} frames)");
            var at441 = s.At(44100);
            Check(Math.Abs(at441.Accent.Length / 2 - s.Accent.Length / 2 * 44100 / 48000) <= 1, $"{name}: a copy at 44.1 kHz for a DAW");
        }
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-metronome-sounds-" + Guid.NewGuid().ToString("N"));
        using (AppConfig.UseThrowawayUserDataDirectory(scratch))
        {
            Directory.CreateDirectory(MetronomeSoundLibrary.UserDirectory);
            var shipped = Path.Combine(MetronomeSoundLibrary.ShippedDirectory, "Beep accent.wav");
            File.Copy(shipped, Path.Combine(MetronomeSoundLibrary.UserDirectory, "Tabla accent.wav"));
            File.Copy(Path.Combine(MetronomeSoundLibrary.ShippedDirectory, "Rim beat.wav"), Path.Combine(MetronomeSoundLibrary.UserDirectory, "Tabla beat.wav"));
            File.Copy(shipped, Path.Combine(MetronomeSoundLibrary.UserDirectory, "Clap.wav"));
            File.Copy(Path.Combine(MetronomeSoundLibrary.ShippedDirectory, "Cowbell beat.wav"), Path.Combine(MetronomeSoundLibrary.UserDirectory, "Click.wav"));
            File.WriteAllText(Path.Combine(MetronomeSoundLibrary.UserDirectory, "Lonely accent.wav"), "half a pair");
            var mine = MetronomeSoundLibrary.Names();
            Check(mine.SequenceEqual(MetronomeSoundLibrary.ShippedNames.Concat(["Clap", "Tabla"])), $"a person's own sounds follow the shipped ones, half a pair left out ({string.Join(", ", mine)})");
            var tabla = Require(MetronomeSoundLibrary.Load("Tabla"), "a pair must load");
            var clap = Require(MetronomeSoundLibrary.Load("Clap"), "one file for both must load");
            Check(clap.Accent.SequenceEqual(clap.Beat) && !tabla.Accent.SequenceEqual(tabla.Beat), "one file plays for both; a pair plays each");
            var replaced = Require(MetronomeSoundLibrary.Load("Click"), "a replaced shipped sound must load");
            Check(replaced.Accent.Length > 5000, $"a person's own Click must replace the shipped one ({replaced.Accent.Length / 2} frames)");
        }
        return "five shipped pairs in order, each starting on its first sample with the accent louder; a person's own pair, single file and replacement all offered and loaded";
    }
}
