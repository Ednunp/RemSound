using System.Globalization;
using RemSound.App.Simulator;
using RemSound.Core;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>SIMULATOR REPLAY (2026-09-30, the simulator review's first improvement). A network the receiver recorded,
    /// played back. A round trip: a run over a known network records at its receiver; the file is written and read back;
    /// replayed, the same packets arrive when they did and the sound is the same. A hand-made minute - a stall, a lost
    /// packet, an overtaken one, a doubled one - puts each where it was made, at the recording's own packet rate and at
    /// another. And the file on its own terms.</summary>
    /// <summary>A recording as the Mac port writes it (RemSoundJam's NetworkRecordingTests.crossPortText: change it in
    /// neither).</summary>
    private const string MacRecording = """
        # RemSound network recording 1
        machine: Andres-Mac
        saved: 2026-09-30 18:00:00
        RemSound: Mac 0.1.47
        configuration: Mac, Core Audio
        receive card frames: 64
        buffer ms: 5
        buffer ASIO ms: 5
        one-way ms: 4.5
        live seconds: 2.0
        live audible gaps: 1
        live late or doubled dropped: 0
        live gaps not repaired: 0
        live concealed: 2
        stream 0: from 192.168.1.82:50123 id 59828 lane unknown codec 3 frame 64 format 48000 Hz, 2 channels, Jamulus-style, 64 frames
        stream 1: from 192.168.1.82:50123 id 4282 lane unknown codec 2 frame 120 format 48000 Hz, 2 channels, Opus, 120 frames
        packets: ms stream sequence bytes
        0.000 0 100 60
        1.333 0 101 60
        2.500 1 7 148
        2.667 0 102 60
        4.125 0 104 61

        """;

    private static string SimulatorReplaysARecordedNetwork()
    {
        // 1. The round trip, over the internet preset (jitter, loss, overtaking, the receiving PC's hiccups).
        var original = Simulation.Run(new SimScenario("round trip", SimConfig.AsioOnly, SimCodec.Opus2_5, SimNetwork.Internet, 30, 12,
            KeepAudio: true, RecordNetwork: true));
        var rec = Require(original.Recording, "the run's own recording");
        var path = Path.Combine(Path.GetTempPath(), $"RemSound-replay-{Guid.NewGuid():N}.txt");
        NetworkRecording loaded;
        try
        {
            rec.Save(path);
            loaded = NetworkRecording.Load(path);
        }
        finally { try { File.Delete(path); } catch { /* temp */ } }
        Check(loaded.Packets.Count == rec.Packets.Count && loaded.Streams.Count == rec.Streams.Count && loaded.Facts.Count == rec.Facts.Count,
            $"the file must read back whole ({loaded.Packets.Count}/{rec.Packets.Count} packets, {loaded.Streams.Count}/{rec.Streams.Count} streams)");
        var heard = rec.Packets.Where(p => p.Stream == 0).ToList();
        Check(rec.Streams.Count == 1 && rec.Streams[0].Codec == (int)AudioTransportCodec.Opus && rec.Streams[0].FrameSamples == 120,
            $"the recording must say what its stream was ({string.Join("; ", rec.Streams.Select(s => s.Format))})");
        var replay = NetworkReplay.From(loaded);
        var distinct = heard.Select(p => p.Sequence).Distinct().Count();
        Check(Math.Abs(replay.Period * 1000 - 2.5) < 0.01, $"the stream's period must come out as 2.5 ms ({replay.Period * 1000:0.0000})");
        Check(replay.Doubled == heard.Count - distinct, $"what came twice must be kept ({replay.Doubled} against {heard.Count - distinct})");
        Check(replay.Lost == replay.Places - distinct, $"what never came must be kept ({replay.Lost} against {replay.Places - distinct})");
        Check(replay.Lost > 0 && original.PacketsLost > 0, "premise: the internet preset loses some packets");

        var again = Simulation.Run(original.Scenario with { Name = "round trip, replayed", Network = SimNetwork.Replaying(replay, SimNetwork.Internet.BaseMs) });
        var twice = Require(again.Recording, "the replay's own recording");
        // Packet by packet: when each arrived in the replay against the original, from the first arrival.
        var was = FirstArrivals(rec);
        var now = FirstArrivals(twice);
        var shared = was.Keys.Intersect(now.Keys).ToList();
        Check(shared.Count >= was.Count - 2 && now.Count <= was.Count + 2,
            $"the replay must deliver the same packets ({now.Count} against {was.Count}, {shared.Count} in both)");
        var offset = shared.Select(k => now[k] - was[k]).OrderBy(d => d).ElementAt(shared.Count / 2);
        var worst = shared.Max(k => Math.Abs(now[k] - was[k] - offset));
        var worstFew = string.Join(", ", shared.OrderByDescending(k => Math.Abs(now[k] - was[k] - offset)).Take(6).Select(k => $"#{k}: {now[k] - was[k] - offset:0.000}"));
        Check(worst < 0.002, $"every packet must arrive when it did, to the recording's microsecond (the worst {worst:0.000} ms off: {worstFew})");
        // And in the order it did: packets that came together come together again, in the order they were sent.
        var orderWas = rec.Packets.Where(p => p.Stream == 0).Select(p => p.Sequence).ToList();
        var orderNow = twice.Packets.Where(p => p.Stream == 0).Select(p => p.Sequence).ToList();
        Check(orderWas.SequenceEqual(orderNow), "every packet must arrive in the order it did");
        var fo = original.Findings;
        var fa = again.Findings;
        Check(Math.Abs(fa.DelayMedianMs - fo.DelayMedianMs) < 1 && fa.Glitches == fo.Glitches && again.LateDropped == original.LateDropped
              && again.Underruns == original.Underruns && again.FecRecoveries == original.FecRecoveries && again.ReorderWaits == original.ReorderWaits,
            $"and sound the same: delay {fa.DelayMedianMs:0.0} against {fo.DelayMedianMs:0.0} ms, {fa.Glitches} against {fo.Glitches} glitches, "
            + $"{again.LateDropped} against {original.LateDropped} late dropped - {SimRunner.Describe(again)}");

        // 2. A hand-made recording: 2.5 ms packets for 10 s, 5 ms of network, and four faults where they are put.
        var made = new NetworkRecording();
        made.AddFact("machine", "hand-made");
        made.Streams.Add(new NetworkRecording.Stream(0, "10.0.0.9:50000", 7, "AsioLane", "hand-made", (int)AudioTransportCodec.Opus, 120));
        const double StallAt = 3.0, StallMs = 120;
        const uint LostAt = 2000, OvertakenAt = 2800, DoubledAt = 3200;
        for (uint n = 0; n < 4000; n++)
        {
            if (n == LostAt) continue;
            var sent = n * 2.5;
            var arrive = sent + 5;
            if (sent >= StallAt * 1000 && sent < StallAt * 1000 + StallMs) arrive = StallAt * 1000 + StallMs + 5; // held, then all at once
            if (n == OvertakenAt) arrive += 4;                                                                     // overtaken by the next
            made.Packets.Add(new NetworkRecording.Packet(arrive, 0, n + 1000, 60));
            if (n == DoubledAt) made.Packets.Add(new NetworkRecording.Packet(arrive + 0.5, 0, n + 1000, 60));
        }
        made.Packets.Sort((a, b) => a.Ms.CompareTo(b.Ms));
        var madeReplay = NetworkReplay.From(made);
        Check(madeReplay.Lost == 1 && madeReplay.Doubled == 1 && Math.Abs(madeReplay.Period * 1000 - 2.5) < 0.001,
            $"the hand-made minute: one lost, one doubled, a 2.5 ms period ({madeReplay.Lost}, {madeReplay.Doubled}, {madeReplay.Period * 1000:0.000})");
        var fixture = new SimScenario("hand-made faults", SimConfig.AsioOnly, SimCodec.Opus2_5, SimNetwork.Replaying(madeReplay, 5), 15, 10);
        var r = Simulation.Run(fixture);
        var at = Seconds(r.Findings.FirstGlitchAt);
        Check(at is not null && Math.Abs(at.Value - StallAt) < 0.3, $"the stall must be heard where it was made ({r.Findings.FirstGlitchAt ?? "nowhere"}) - {SimRunner.Describe(r)}");
        Check(r.PacketsLost == 1, $"the one packet that never came must be lost in the replay ({r.PacketsLost})");
        Check(r.LateDropped >= 1, $"the doubled packet must reach the receiver twice, and be dropped ({r.LateDropped} dropped)");
        Check(r.ReorderWaits >= 1, $"the overtaken packet must make the receiver wait for it ({r.ReorderWaits} waits)");
        // The same minute at another packet rate: 10 ms Opus, four times fewer packets - the stall still where it was.
        var slower = Simulation.Run(fixture with { Name = "hand-made faults, 10 ms Opus", Codec = SimCodec.Opus10, BufferMs = 25 });
        var at10 = Seconds(slower.Findings.FirstGlitchAt);
        Check(at10 is not null && Math.Abs(at10.Value - StallAt) < 0.3,
            $"replayed as 10 ms packets, the stall must still be heard where it was ({slower.Findings.FirstGlitchAt ?? "nowhere"}) - {SimRunner.Describe(slower)}");

        // A sender slower than the recorded one (its clock 0.1 % slow, so 10 ms behind after 4,000 packets) still meets the
        // recorded delays - here 0 and 4 ms by turns - as the replay moves the stream's start on to follow it; left at the
        // first packet, the late sends would crush every delay to nothing.
        var steady = new NetworkRecording();
        steady.Streams.Add(new NetworkRecording.Stream(0, "10.0.0.9:50000", 8, "AsioLane", "hand-made", (int)AudioTransportCodec.Opus, 120));
        for (uint n = 0; n < 6000; n++) steady.Packets.Add(new NetworkRecording.Packet(n * 2.5 + (n % 2 == 1 ? 4 : 0), 0, n, 60));
        steady.Packets.Sort((a, b) => a.Ms.CompareTo(b.Ms));
        var follow = NetworkReplay.From(steady);
        var header = new byte[RemPacket.HeaderSize + 4];
        var kept = new List<double>();
        for (uint n = 0; n < 6000; n++)
        {
            RemPacket.WriteHeader(header, RemPacketType.Audio, 21, 500 + n);
            var sent = 1.0 + n * 0.0025 * 1.001;
            var got = follow.ArrivalsFor(header, sent, 0.005, 0.0025);
            if (n >= 4000) kept.Add((got[0] - sent - 0.005) * 1000);
        }
        Check(kept.Where((_, i) => i % 2 == 0).All(d => Math.Abs(d) < 0.02) && kept.Where((_, i) => i % 2 == 1).All(d => Math.Abs(d - 4) < 0.02),
            $"a slower sender must still meet the recorded 0 and 4 ms by turns (from the 4,000th packet: {string.Join(" ", kept.Take(6).Select(d => d.ToString("0.00", CultureInfo.InvariantCulture)))} ...)");

        // 3. The file on its own terms.
        CheckThrows<FormatException>(() => NetworkRecording.Load(new StringReader("# some other file\nmachine: x\npackets: ms stream sequence bytes\n")),
            "a file that is not a recording must be refused, however like one it looks");
        CheckThrows<FormatException>(() => NetworkRecording.Load(new StringReader(NetworkRecording.FirstLine + "\nmachine: x\npackets: ms stream sequence bytes\n1.0 3 5 60\n")),
            "a packet of a stream that was never named must be refused");
        var two = NetworkRecording.Load(new StringReader(string.Join("\n",
            new[] { NetworkRecording.FirstLine, "machine: two streams",
                "stream 0: from 10.0.0.1:1 id 1 lane Mixed codec 2 frame 480 format a",
                "stream 1: from 10.0.0.2:2 id 2 lane AsioLane codec 2 frame 120 format b",
                "packets: ms stream sequence bytes" }
            .Concat(Enumerable.Range(0, 40).Select(i => string.Create(CultureInfo.InvariantCulture, $"{i * 2.5:0.000} 1 {i} 60")))
            .Concat(Enumerable.Range(0, 10).Select(i => string.Create(CultureInfo.InvariantCulture, $"{i * 10.0:0.000} 0 {i} 200"))))));
        Check(two.Fact("machine") == "two streams" && two.Streams[1].Lane == "AsioLane" && two.Streams[0].FrameSamples == 480,
            "the facts and streams must read back as written");
        Check(Math.Abs(NetworkReplay.From(two).Period * 1000 - 2.5) < 0.001 && Math.Abs(NetworkReplay.From(two, 0).Period * 1000 - 10) < 0.001,
            "unless one is named, the stream with the most packets is replayed; a named one is that one");
        var scenario = SimRunner.ScenarioFor(two, 1, SimNetwork.Perfect, 5);
        Check(scenario.Codec == SimCodec.Opus2_5 && SimRunner.CodecOf((int)AudioTransportCodec.OpusCustom, 64) == SimCodec.Custom64
              && SimRunner.ConfigOf("both [ASIO + WASAPI]") == SimConfig.Both && SimRunner.ConfigOf("WASAPI only") == SimConfig.WasapiOnly,
            "a recording's stream and configuration must become the scenario it was made in");

        // A recording made by the Mac port (2026-09-30: RemSoundJam's NetworkRecording.swift, whose own tests write exactly
        // this text - NetworkRecordingTests.crossPortText). The two ports are pinned to one format from both sides.
        var mac = NetworkRecording.Load(new StringReader(MacRecording));
        Check(mac.Streams.Count == 2 && mac.Packets.Count == 5 && mac.Fact("machine") == "Andres-Mac" && mac.Number("live concealed") == 2
              && mac.Streams[0].Codec == (int)AudioTransportCodec.OpusCustom && mac.Streams[0].FrameSamples == 64 && mac.Streams[0].StreamId == 59828
              && mac.Streams[0].Format == "48000 Hz, 2 channels, Jamulus-style, 64 frames" && mac.Packets[4] is { Ms: 4.125, Stream: 0, Sequence: 104, Bytes: 61 },
            "a recording made by the Mac port must load: its facts, its streams and every packet");
        var macScenario = SimRunner.ScenarioFor(mac, -1, SimNetwork.Perfect, 5);
        Check(macScenario.Codec == SimCodec.Custom64 && macScenario.BufferMs == 5 && macScenario.ReceiveCard?.Frames == 64 && macScenario.Config == SimConfig.AsioOnly,
            $"and become the scenario it was made in: Jamulus-style 64 at 5 ms, a 64-frame card ({macScenario.Codec}, {macScenario.BufferMs} ms, {macScenario.ReceiveCard?.Frames} frames)");
        var macWritten = new StringWriter();
        mac.Save(macWritten);
        Check(macWritten.ToString().Replace("\r\n", "\n") == MacRecording.Replace("\r\n", "\n"),
            "and written back out by this port, it must be the same text, line for line");

        return $"round trip over the internet preset: every packet when and in the order it first arrived (the worst {worst * 1000:0.0} µs off), delay "
             + $"{fa.DelayMedianMs:0.0} against {fo.DelayMedianMs:0.0} ms, {fa.Glitches} glitches both times; a hand-made minute's stall, loss, "
             + $"overtaking and doubling each met where they were made, at 2.5 ms and 10 ms packets; the Mac port's recording loads, as written";

        // The analyser's "3.123 s", as seconds.
        static double? Seconds(string? text) =>
            text is not null && double.TryParse(text.Replace(" s", ""), NumberStyles.Float, CultureInfo.CurrentCulture, out var v) ? v : null;

        static void CheckThrows<T>(Action act, string what) where T : Exception
        {
            try { act(); }
            catch (T) { return; }
            Check(false, what);
        }

        static Dictionary<uint, double> FirstArrivals(NetworkRecording r)
        {
            var d = new Dictionary<uint, double>();
            foreach (var p in r.Packets.Where(p => p.Stream == 0)) d.TryAdd(p.Sequence, p.Ms);
            return d;
        }
    }
}
