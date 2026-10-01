using System.Diagnostics;
using System.Net;
using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>THE NETWORK RECORDER (2026-09-30, the app's half of the simulator's replay). The receiver keeps the last
    /// minute of audio-packet arrivals - a minute, not more, however long it runs, each stream numbered as it first appears
    /// and described from its Format packet, with what the receiver heard go wrong over the same minute. Record > Save the last
    /// minute of the network writes it to the logs folder, driven as a user would through the control channel, and says so;
    /// with nothing arrived it says there is nothing to save and writes nothing.</summary>
    private static string TheNetworkRecorderKeepsTheLastMinute()
    {
        // 1. The recorder, on a simulated clock: a stream heard only in the first 5 s, then 70 s of two more, 400 and 100
        // packets a second, and an audible gap every second throughout.
        var from = new IPEndPoint(IPAddress.Parse("10.1.1.1"), 5000);
        var other = new IPEndPoint(IPAddress.Parse("10.1.1.2"), 5000);
        var gone = new IPEndPoint(IPAddress.Parse("10.1.1.3"), 5000);
        double now = 0;
        var baseTicks = Stopwatch.GetTimestamp();
        AudioClock.UseSimulated(() => baseTicks + (long)(now * Stopwatch.Frequency));
        NetworkRecording rec;
        NetworkRecorder recorder;
        try
        {
            recorder = new NetworkRecorder { Enabled = true };
            long gaps = 0;
            recorder.Note(gone, 3, 0, 60); // the first stream the recorder ever heard, long gone by the saved minute
            for (var i = 0; i < 28_000; i++)
            {
                now = i * 0.0025;
                if (now < 5) recorder.Note(gone, 3, (uint)i + 1, 60);
                recorder.Note(from, 7, (uint)i, 60);
                if (i % 4 == 0) recorder.Note(other, 9, (uint)(i / 4), 200);
                if (i % 400 == 0)
                {
                    gaps++;
                    recorder.NoteSecond(new NetworkRecorder.Counters(AudioClock.GetTimestamp(), gaps, gaps, 0, 0, 0, 0, 0, 0, 0, 0));
                }
            }
            recorder.NoteSecond(new NetworkRecorder.Counters(AudioClock.GetTimestamp(), gaps, gaps, 0, 0, 0, 0, 0, 0, 0, 0));
            rec = recorder.Take([new("machine", "test")],
                (addr, id) => id == 7 ? ("AsioLane", "48000 Hz, 2 channel(s), 16-bit PCM over Opus (2.5 ms) [AsioLane]", (int)AudioTransportCodec.Opus, 120) : null);
        }
        finally { AudioClock.UseSimulated(null); }
        // Off (every receiver but the app's own and a recording simulation), it keeps nothing and allocates nothing.
        var off = new NetworkRecorder();
        for (uint i = 0; i < 100; i++) off.Note(from, 7, i, 60);
        Check(off.HeldForTest == 0 && off.CapacityForTest == 0, $"a recorder left off must keep nothing ({off.HeldForTest} held, {off.CapacityForTest} room)");
        var span = rec.Packets[^1].Ms - rec.Packets[0].Ms;
        Check(span <= NetworkRecorder.KeepSeconds * 1000 && span > NetworkRecorder.KeepSeconds * 1000 - 10,
            $"a minute must be kept, not more (the saved span is {span / 1000:0.000} s)");
        var mine = rec.Packets.Where(p => p.Stream == 0).ToList();
        Check(mine.Count >= 23_990 && mine.Count <= 24_001 && mine[0].Sequence >= 3_999 && mine[^1].Sequence == 27_999,
            $"the first stream's last minute must be whole ({mine.Count} packets, {mine[0].Sequence} to {mine[^1].Sequence})");
        Check(rec.Streams.Count == 2 && rec.Streams[0].From == from.ToString() && rec.Streams[0].StreamId == 7 && rec.Streams[0].Lane == "AsioLane"
              && rec.Streams[0].FrameSamples == 120 && rec.Streams[1].Format == "no Format packet seen",
            $"each stream numbered as it appears and described from its Format packet when there was one ({string.Join("; ", rec.Streams)})");
        Check(rec.Packets.Count(p => p.Stream == 1) is >= 5_990 and <= 6_001, "and the second stream's minute too");
        Check(recorder.CapacityForTest <= 1 << 18, $"the store must stay within its ceiling ({recorder.CapacityForTest} entries)");
        Check(rec.Fact("live audible gaps") == "60",
            $"the live figures must be what changed over the saved minute, not since the start: 60 audible gaps, not 70 ({rec.Fact("live audible gaps")})");

        // 2. The window: Record > Save the last minute of the network, as a user would drive it.
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        MainForm? form = null;
        RemoteControlServer? server = null;
        var pipe = "RemSound.control.selftest.network." + Guid.NewGuid().ToString("N")[..8];
        string? saved = null;
        try
        {
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            var main = form;
            server = new RemoteControlServer(pipe, () => main, new RemoteControlEngine(() => main));
            server.Start();
            string Ask(string command)
            {
                var reply = RemoteControl.Send(command, pipe, out var reached, connectTimeoutMs: 5000);
                Check(reached, $"the control channel did not answer \"{command}\": {reply}");
                return reply;
            }
            bool Showing(string words) => Ask("windows").Contains(words, StringComparison.Ordinal);
            // Where the window's log is, or the logs folder when it keeps none (as this test copy doesn't).
            var logFolder = (main.LogPathForTest is { } log ? Path.GetDirectoryName(log) : null) ?? AppConfig.LogsDirectory;
            string[] Recordings() => Directory.Exists(logFolder) ? Directory.GetFiles(logFolder, "RemSound-network-*.txt") : [];
            var before = Recordings();
            var mark = HeadlessRecords.Log.Mark;
            DriveWhilePumping(main, () =>
            {
                Check(Ask("menus").Contains("Save the last minute of the network", StringComparison.Ordinal), "the Record menu must offer it");
                // Nothing has arrived yet: nothing to save.
                Ask("menu Record > Save the last minute of the network");
                Check(WaitFor(() => Showing("no network to save"), TimeSpan.FromSeconds(10)), $"with nothing arrived it must say so (windows: {Head(Ask("windows"))})");
                Check(Ask("answer OK").StartsWith("ok", StringComparison.Ordinal), "and close with OK");
                Check(Recordings().Length == before.Length, "and write nothing");

                // 400 audio packets from a peer, straight off the socket.
                var peer = new IPEndPoint(IPAddress.Parse("192.168.77.5"), 47830);
                for (uint seq = 0; seq < 400; seq++)
                {
                    var packet = new byte[RemPacket.HeaderSize + 20];
                    RemPacket.WriteHeader(packet, RemPacketType.Audio, 11, seq);
                    main.ReceiverForTest.InjectExternalPacket(packet, packet.Length, peer);
                }
                // Two of the window's once-a-second ticks: what the saved minute's live figures come from.
                main.Invoke(() => { main.TickStreamBuffersForTest(); main.TickStreamBuffersForTest(); });
                Ask("menu Record > Save the last minute of the network");
                Check(WaitFor(() => Showing("Saved the last minute of the network: 400 packets from 1 stream(s)"), TimeSpan.FromSeconds(10)),
                    $"saving must say what was saved (windows: {Head(Ask("windows"))})");
                Check(Ask("answer OK").StartsWith("ok", StringComparison.Ordinal), "and close with OK");
            }, TimeSpan.FromSeconds(60));
            var made = Recordings().Except(before).ToList();
            Check(made.Count == 1, $"one file must be written to the logs folder ({made.Count})");
            saved = made[0];
            var back = NetworkRecording.Load(saved);
            Check(back.Packets.Count == 400 && back.Streams.Count == 1 && back.Streams[0].From == "192.168.77.5:47830" && back.Streams[0].StreamId == 11,
                $"the file must hold the 400 packets and their stream ({back.Packets.Count}, {string.Join("; ", back.Streams)})");
            Check(back.Fact("machine") == Environment.MachineName && back.Fact("configuration") == main.ActiveAudioConfigurationForTest().Describe()
                  && back.Fact("auto-tune ASIO") is "on" or "off" && back.Fact("buffer ms") is not null && back.Fact("ASIO output period ms") is not null
                  && back.Fact("live audible gaps") is not null,
                $"and the facts a replay needs (machine, configuration, auto-tune, buffers, output periods, the live figures): {string.Join("; ", back.Facts.Select(f => $"{f.Key}={f.Value}"))}");
            Check(HeadlessRecords.Log.Since(mark).Any(l => l.Contains("network recording saved: ", StringComparison.Ordinal) && l.Contains(Path.GetFileName(saved), StringComparison.Ordinal)),
                "and the log must say where it went");
        }
        finally
        {
            server?.Dispose();
            try { form?.Dispose(); } catch { /* teardown */ }
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
            if (saved is not null) try { File.Delete(saved); } catch { /* a test file */ }
        }
        return "a minute kept, not more, over 70 s of two streams; streams numbered as they appear and described from their Format "
             + "packets; the live figures over that minute; Record > Save the last minute of the network writes it to the logs folder "
             + "with the facts a replay needs, says so, and with nothing arrived says there is nothing to save";
    }
}
