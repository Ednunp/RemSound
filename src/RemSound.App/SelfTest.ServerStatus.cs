using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using RemSound.App.Simulator;
using RemSound.Core;
using RemSound.Sender;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>SERVER STATUS, SIMULATED (Ed, 2026-10-01: "make sure you simulate"). Your real group client on a virtual clock,
    /// a server doing what server-v2.16 does with status and pings, and networks from the simulator: in the house, over the
    /// internet, and with an upload that stalls once. The round trip must be what the networks give; the server's count of
    /// people, groups and places, and its version, must arrive; your sound's steadiness must be what the server saw - steady
    /// when it is, uneven when the upload stalled; and the app's own words must say so, in Ed's order.</summary>
    private static string ServerStatusSimulated()
    {
        var said = new List<string>();
        string Lines(ServerStatusResult r) => r.StatusText.Replace(Environment.NewLine, " | ");
        void InEdsOrder(ServerStatusResult r)
        {
            var lines = r.StatusText.Split(Environment.NewLine);
            Check(lines.Length >= 3 && lines[0].StartsWith("On ", StringComparison.Ordinal) && lines[1].StartsWith("Your round trip", StringComparison.Ordinal)
                  && lines[2].StartsWith("Your sound reaches the server", StringComparison.Ordinal),
                $"{r.Scenario.Name}: the server and how long first, then the round trip, then the sound's steadiness (Ed: \"very near the top\") ({Lines(r)})");
        }

        var home = ServerStatusSimulation.Run(new ServerStatusScenario("in the house",
            new SimNetwork("to the server in the house", 0.3, 0.05), new SimNetwork("back", 0.3, 0.05), ["yours", "another band", "another band"]));
        Check(home.RoundTripMs is { } h && h < 2, $"IN THE HOUSE: the round trip must be under 2 ms ({home.RoundTripMs})");
        Check(home.Info is { People: 4, Groups: 2, PlacesFree: 60, Version: "server-v2.16" },
            $"and the server must say four people in two groups, sixty places free, server-v2.16 ({home.Info})");
        Check(home.Info is { YourLongGaps: 0 } && home.Info.YourLongestGapMs < 3 && home.Info.YourPackets > 9000,
            $"and your sound must reach it steadily, every packet counted ({home.Info})");
        Check(home.StatusText.Contains("Your sound reaches the server steadily", StringComparison.Ordinal)
              && home.StatusText.Contains("On the server: 4 people in 2 groups, 60 places free.", StringComparison.Ordinal)
              && home.StatusText.Contains("Server version server-v2.16.", StringComparison.Ordinal)
              && home.StatusText.Contains("In your group: mate 1, who has ticked you.", StringComparison.Ordinal),
            $"and the words must say so ({Lines(home)})");
        InEdsOrder(home);
        said.Add($"in the house {home.RoundTripMs:0.0} ms, steady");

        var internet = ServerStatusSimulation.Run(new ServerStatusScenario("over the internet",
            new SimNetwork("up", 12, 3), new SimNetwork("down", 12, 3), ["yours"]));
        Check(internet.RoundTripMs is { } r && r >= 24 && r <= 34,
            $"OVER THE INTERNET: 12 ms each way and a few of jitter must come to a round trip of 24 to 34 ms ({internet.RoundTripMs:0.0})");
        Check(internet.Info is { } ii && Math.Abs(ii.YourLongestGapMs - internet.ExpectedLongestGapMs) < 0.01 && ii.YourLongGaps == internet.ExpectedLongGaps,
            $"and the steadiness the app read must be what the server saw ({internet.Info?.YourLongestGapMs:0.00} against {internet.ExpectedLongestGapMs:0.00}, "
            + $"{internet.Info?.YourLongGaps} against {internet.ExpectedLongGaps})");
        Check(internet.StatusText.Contains($"Your round trip to the server: {internet.RoundTripMs:0} ms.", StringComparison.Ordinal),
            $"and the words must give the round trip ({Lines(internet)})");
        InEdsOrder(internet);
        said.Add($"over the internet {internet.RoundTripMs:0.0} ms, longest gap {internet.Info?.YourLongestGapMs:0} ms");

        var stall = ServerStatusSimulation.Run(new ServerStatusScenario("an upload that stalls once",
            new SimNetwork("up, stalls 150 ms once", 12, 1, StallAtSec: 20, StallOnceMs: 150), new SimNetwork("down", 12, 1), ["yours"]));
        Check(stall.Info is { YourLongGaps: >= 1 } si && si.YourLongestGapMs >= 140,
            $"AN UPLOAD THAT STALLS: the server must have seen the 150 ms gap ({stall.Info})");
        Check(stall.StatusText.Contains("Your sound reaches the server unevenly: longest gap", StringComparison.Ordinal),
            $"and the words must say your sound reaches it unevenly ({Lines(stall)})");
        InEdsOrder(stall);
        said.Add($"a stalled upload: longest gap {stall.Info?.YourLongestGapMs:0} ms, said unevenly");
        return string.Join("; ", said) + "; the server's size and version arrived, in Ed's order each time";
    }

    /// <summary>SERVER STATUS THROUGH THE REAL SERVER (2026-10-01). The working tree's relay and three apps, two on one password
    /// and one on another: each is sent the server's status (three people in two groups, its version) and has its pings sent
    /// back; once Ed sends sound, his own figures count it. REMSOUND_TEST_LIVE_RELAY=host:port: Ed alone through a real server.</summary>
    private static string? ServerStatusThroughTheServer()
    {
        var liveText = Environment.GetEnvironmentVariable("REMSOUND_TEST_LIVE_RELAY");
        var live = string.IsNullOrWhiteSpace(liveText) ? null : IPEndPoint.Parse(liveText.Trim());
        var root = FindSourceRoot();
        if (root is null && live is null) return Skip("the source tree is not reachable (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var python = FindPython();
        if (python is null && live is null) return Skip("there is no Python on this machine to run the relay (py.exe, or a python.exe outside WindowsApps)");
        var port = FreeUdpPort();
        var logDir = Path.Combine(Path.GetTempPath(), $"remsound-relay-status-{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "relay.log");
        var relayProcess = live is null ? StartRelay(python!, Path.Combine(root!, "server", "remsound-relay.py"), port, logPath) : null;
        var apps = new List<RelayTestApp>();
        using var stopFeeding = new CancellationTokenSource();
        Thread? feeder = null;
        AudioSender? talker = null;
        try
        {
            if (relayProcess is not null)
                Check(WaitUntil(() => ReadShared(logPath).Contains("event=startup"), 10000) && !relayProcess.HasExited, $"the relay must start under {python}");
            var relay = live ?? new IPEndPoint(IPAddress.Loopback, port);
            var password = live is null ? "status" : $"remsound self-test {Guid.NewGuid():N}";
            var ed = new RelayTestApp("Ed", password, relay, groups: true);
            apps.Add(ed);
            Check(WaitUntil(() => ed.Group!.IsInGroup(relay), 15000), "Ed must join the server's group");
            Check(WaitUntil(() => ed.Group!.LatestServerInfo is not null && ed.Group.ServerRoundTripMs is not null, 10000),
                $"the server must send Ed its status and his pings back ({ed.Group!.LatestServerInfo}; round trip {ed.Group.ServerRoundTripMs})");
            if (live is not null)
            {
                var li = ed.Group.LatestServerInfo!;
                return $"THROUGH THE REAL SERVER {live}: {li.Version}, {li.People} on it in {li.Groups} group(s), {li.PlacesFree} places free, "
                     + $"running {li.Uptime}; Ed's round trip {ed.Group.ServerRoundTripMs:0.0} ms";
            }

            var andre = new RelayTestApp("Andre", password, relay, groups: true);
            var carol = new RelayTestApp("Carol", "another password", relay, groups: true);
            apps.AddRange([andre, carol]);
            Check(WaitUntil(() => ed.Group.Members.Count == 1 && andre.Group!.Members.Count == 1 && carol.Group!.IsInGroup(relay), 15000),
                "Andre must join Ed's group, and Carol a group of her own");
            Check(WaitUntil(() => ed.Group.LatestServerInfo is { People: 3, Groups: 2 }, 12000),
                $"THE WHOLE SERVER: three people in two groups ({ed.Group.LatestServerInfo})");
            var info = ed.Group.LatestServerInfo!;
            Check(info.Version == File.ReadAllText(Path.Combine(root!, "server", "VERSION")).Trim() && info.PlacesFree == 61 && info.Phones == 0,
                $"its version, and 61 of 64 places free ({info})");
            Check(ed.Group.ServerRoundTripMs is < 50, $"THE ROUND TRIP: on this computer, well under 50 ms ({ed.Group.ServerRoundTripMs:0.0})");

            // Ed sends: his own figures count it.
            ed.Tick([ed.Group.Members.Single().Id]);
            andre.Tick([andre.Group!.Members.Single().Id]);
            ed.Sender.ConfigureCodec(AudioTransportCodec.Opus, 120);
            ed.Sender.UseDefaultLaneForTest(RenderRoute.WasapiLane);
            talker = ed.Sender;
            feeder = new Thread(() =>
            {
                var block = new float[480 * 2];
                var next = Environment.TickCount64;
                while (!stopFeeding.IsCancellationRequested)
                {
                    talker.FeedDefaultLaneForTest(block);
                    next += 10;
                    var wait = next - Environment.TickCount64;
                    if (wait > 0) Thread.Sleep((int)wait);
                }
            }) { IsBackground = true, Name = "self-test server status" };
            feeder.Start();
            Check(WaitUntil(() => ed.Group.LatestServerInfo is { YourPackets: > 200 }, 12000),
                $"HIS OWN SOUND: once Ed sends, the server's figures for him must count it ({ed.Group.LatestServerInfo})");
            Check(WaitUntil(() => andre.Group.LatestServerInfo is { YourPackets: 0 }, 6000) || andre.Group.LatestServerInfo?.YourPackets == 0,
                $"and Andre's own figures must stay his: he sent nothing ({andre.Group.LatestServerInfo})");
            var mine = ed.Group.LatestServerInfo!;
            return $"three people in two groups, {mine.Version}, 61 places free; round trip {ed.Group.ServerRoundTripMs:0.0} ms on this computer; "
                 + $"Ed's sound counted ({mine.YourPackets} packets, longest gap {mine.YourLongestGapMs:0.0} ms) and Andre's left his own";
        }
        finally
        {
            stopFeeding.Cancel();
            feeder?.Join(2000);
            foreach (var app in apps) app.Dispose();
            try { relayProcess?.Kill(entireProcessTree: true); } catch { /* already gone */ }
            relayProcess?.Dispose();
            try { Directory.Delete(logDir, recursive: true); } catch { /* the relay may still hold its log a moment */ }
        }
    }

    /// <summary>SERVER STATUS IN THE SERVER WINDOW (Ed, 2026-10-01: "put it in the server window"). The words, line by line,
    /// in Ed's order; an older server said to be one; the box there only while on a server, and never rewritten while it is
    /// being read; the window's own status from a stand-in server - its status packet and a ping back - and the log line; and
    /// the tray ("connected to 1 server, 4 peers").</summary>
    private static string? ServerStatusInTheServerWindow()
    {
        var at = new IPEndPoint(IPAddress.Parse("203.0.113.90"), RemPacket.DefaultPort);
        var info = new RelayGroupClient.ServerInfo("server-v2.16", TimeSpan.FromDays(2), 5, 3, 59, 1, 4000, 12.4, 0, 60, DateTime.UtcNow);
        var text = MainForm.ServerStatusText(new MainForm.ServerStatusFacts("remote.example.test", at, TimeSpan.FromMinutes(12), 34.4, info,
            OlderServer: false, Sending: true, [("Andre", true), ("MERJILLE", false)], PhonePaired: true));
        var expected = new[]
        {
            "On remote.example.test (203.0.113.90), connected for 12 minutes.",
            "Your round trip to the server: 34 ms.",
            "Your sound reaches the server steadily: longest gap 12 ms in the last minute.",
            "In your group: Andre and MERJILLE. Andre has ticked you; MERJILLE hasn't yet.",
            "On the server: 5 people in 3 groups, 59 places free.",
            "Server version server-v2.16.",
            "A phone or older app is paired with you here.",
        };
        Check(text.Split(Environment.NewLine).SequenceEqual(expected), $"THE WORDS, in Ed's order:{Environment.NewLine}{text}");
        var older = MainForm.ServerStatusText(new MainForm.ServerStatusFacts("192.168.1.82", new IPEndPoint(IPAddress.Parse("192.168.1.82"), RemPacket.DefaultPort),
            TimeSpan.FromSeconds(30), null, null, OlderServer: true, Sending: true, [], PhonePaired: false));
        Check(older == "On 192.168.1.82, connected for less than a minute." + Environment.NewLine
                     + "Nobody else from your group is on the server yet." + Environment.NewLine
                     + "This server doesn't report its size, version or your round trip (it's older than server-v2.16).",
            $"AN OLDER SERVER: what the app knows, and that it is older ({older.Replace(Environment.NewLine, " | ")})");
        var uneven = MainForm.ServerStatusText(new MainForm.ServerStatusFacts("s", at, TimeSpan.FromHours(1.5), null, info with { YourLongestGapMs = 85, YourLongGaps = 14 },
            false, true, [("Andre", true)], false));
        Check(uneven.Contains("connected for 1 hour 30 minutes", StringComparison.Ordinal)
              && uneven.Contains("Your round trip to the server: measuring.", StringComparison.Ordinal)
              && uneven.Contains("Your sound reaches the server unevenly: longest gap 85 ms, and 14 gaps over 20 ms in the last minute.", StringComparison.Ordinal)
              && uneven.Contains("In your group: Andre, who has ticked you.", StringComparison.Ordinal),
            $"and the other wordings ({uneven.Replace(Environment.NewLine, " | ")})");
        Check(MainForm.ServerStatusText(new MainForm.ServerStatusFacts("s", at, TimeSpan.Zero, 3, info, false, Sending: false, [], false))
                .Contains("Send my audio is on: your sound goes to the server once you tick somebody there, or unmute yourself to test round trip.", StringComparison.Ordinal),
            "SEND ON, NOBODY TICKED: not \"not sending\" (Ed: \"i'm fairly sure I was sending\") - why none goes there yet");
        Check(MainForm.ServerStatusText(new MainForm.ServerStatusFacts("s", at, TimeSpan.Zero, 3, info, false, Sending: false, [], false, SendOn: false))
                .Contains("Send my audio is off, so none of your sound goes to the server.", StringComparison.Ordinal), "and SEND OFF, said so");
        Check(RelayConnectDialog.StatusShape("Your round trip to the server: 21 ms.") == RelayConnectDialog.StatusShape("Your round trip to the server: 23 ms.")
              && RelayConnectDialog.StatusShape("Your round trip to the server: measuring.") != RelayConnectDialog.StatusShape("Your round trip to the server: 23 ms."),
            "THE SHAPE: figures moving keep the shape; measuring becoming a figure changes it");
        Check(MainForm.TrayConnectedText(onServer: true, 4) == "connected to 1 server, 4 peers" && MainForm.TrayConnectedText(false, 4) == "connected to 4 peers"
              && MainForm.TrayConnectedText(true, 1) == "connected to 1 server, 1 peer" && MainForm.TrayConnectedText(true, 0) == "connected to 1 server, no peers"
              && MainForm.TrayConnectedText(false, 0) == "no peers",
            "THE TRAY: \"connected to 1 server, 4 peers\", or \"connected to 4 peers\" with no server (Ed)");

        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        Windowless.SetActiveForTest(true);
        MainForm? form = null;
        try
        {
            // The window: the box only while on a server, and held still while being read.
            string? on = "remote.example.test";
            var shown = "Your round trip to the server: measuring.";
            using (var dialog = new RelayConnectDialog(() => on, _ => { }, () => on = null, () => ["remote.example.test"], _ => { },
                       () => "", () => on is null ? null : shown))
            {
                dialog.StartPosition = FormStartPosition.Manual;
                dialog.Show();
                for (var i = 0; i < 5; i++) Application.DoEvents();
                var box = dialog.ServerStatusBoxForTest;
                Check(IsSetVisibleForAltAudit(box) && box.Text == shown && box.ReadOnly, "ON A SERVER: the Server window must show the status box, read-only");
                // The keyboard in the box, made sure of: in the full gate another window could hold it, and a test of "while it
                // is being read" then tested nothing (2026-10-02).
                dialog.Activate();
                box.Focus();
                // In the full gate another window can keep the keyboard from it (2026-10-02, twice): then the window is told
                // the box is being read, and the rule below is still the one tested.
                var realFocus = WaitFor(() => { Application.DoEvents(); dialog.Activate(); box.Focus(); return box.ContainsFocus; }, TimeSpan.FromSeconds(3));
                if (!realFocus) dialog.BeingReadForTest = true;
                shown = "Your round trip to the server: 21 ms.";
                dialog.TickForTest();
                Check(box.Text == shown, "BEING READ, A LINE CHANGING IN KIND: measuring becoming a figure must be written even while it has the keyboard (Ed: it \"hangs on measuring\")");
                shown = "Your round trip to the server: 23 ms.";
                dialog.TickForTest();
                Check(box.Text == "Your round trip to the server: 21 ms.", "BEING READ, A FIGURE MOVING: not rewritten while it has the keyboard");
                dialog.RememberedListForTest.Focus();
                for (var i = 0; i < 5; i++) Application.DoEvents();
                if (!realFocus) dialog.BeingReadForTest = false;
                dialog.TickForTest();
                Check(box.Text == shown, "and brought up to date once it does not");
                on = null;
                dialog.TickForTest();
                Check(!IsSetVisibleForAltAudit(box), "OFF A SERVER: the box must go");
            }

            // The app's own status from a stand-in server: what it knows at once, then the status packet and a ping back.
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            var said = new ConcurrentQueue<string>();
            form.LogForTest.EventTapForTest = said.Enqueue;
            Check(form.ServerStatusForTest() is null, "NO SERVER: no status");
            form.ConnectToRelayForTest("server.example.test", at);
            var group = form.RelayGroupForTest;
            var roster = RosterPacket([(Guid.NewGuid(), "Andre", true)], paired: false);
            var length = roster.Length;
            group.HandleInbound(roster, ref length, at, out _);
            var first = form.ServerStatusForTest()!;
            Check(first.StartsWith("On server.example.test (203.0.113.90), connected for less than a minute.", StringComparison.Ordinal)
                  && first.Contains("Your round trip to the server: measuring.", StringComparison.Ordinal),
                $"JUST JOINED: the server and how long, the round trip being measured ({first.Replace(Environment.NewLine, " | ")})");
            group.BackdateInGroupForTest(MainForm.ServerStatusOlderAfter);
            Check(form.ServerStatusForTest()!.Contains("it's older than server-v2.16", StringComparison.Ordinal),
                "NOTHING SAID IN TIME: the server must be taken for an older one");

            var version = Encoding.ASCII.GetBytes("server-v2.16");
            var status = new byte[RelayGroupClient.GroupHeaderSize + 2 + version.Length + 24];
            RemPacket.WriteHeader(status, (RemPacketType)RelayGroupClient.TypeServerInfo, 0, 0);
            status[4] = RelayGroupClient.GroupVersion;
            status[RelayGroupClient.GroupHeaderSize] = 1;
            status[RelayGroupClient.GroupHeaderSize + 1] = (byte)version.Length;
            version.CopyTo(status, RelayGroupClient.GroupHeaderSize + 2);
            var body = status.AsSpan(RelayGroupClient.GroupHeaderSize + 2 + version.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(body[4..], 2);
            BinaryPrimitives.WriteUInt16LittleEndian(body[6..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(body[8..], 62);
            length = status.Length;
            group.HandleInbound(status, ref length, at, out _);
            var clock = 1_000_000L;
            group.ClockUsForTest = () => clock;
            var ping = group.PingForTest();
            clock += 21_000;
            length = ping.Length;
            group.HandleInbound(ping, ref length, at, out _);
            var full = form.ServerStatusForTest()!;
            Check(full.Contains("Your round trip to the server: 21 ms.", StringComparison.Ordinal)
                  && full.Contains("On the server: 2 people in 1 group, 62 places free.", StringComparison.Ordinal)
                  && full.Contains("Server version server-v2.16.", StringComparison.Ordinal)
                  && full.Contains("In your group: Andre, who has ticked you.", StringComparison.Ordinal)
                  && !full.Contains("older", StringComparison.Ordinal),
                $"WHAT THE SERVER SAID: its size and version, and the round trip of a ping it sent back ({full.Replace(Environment.NewLine, " | ")})");
            form.LogServerStatusForTest();
            Check(said.Any(l => l.StartsWith("server status [", StringComparison.Ordinal) && l.Contains("Your round trip to the server: 21 ms. |", StringComparison.Ordinal)),
                "THE LOG: the status on one line, stamped with the configuration");
            form.DisconnectFromRelayForTest();
            Check(form.ServerStatusForTest() is null && group.LatestServerInfo is null && group.ServerRoundTripMs is null,
                "LEFT THE SERVER: no status, and what it said forgotten");
            form.LogForTest.EventTapForTest = null;
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
        }
        return "the seven lines in Ed's order (send on with nobody ticked, and send off, each said as it is), the older-server line and the other wordings; the box only on a server and held "
             + "still while read; the app's status from a stand-in server's packet and a ping back, and its log line; the tray";
    }
    /// <summary>SERVER SOUNDS (Ed's, 2026-10-01: "server connect disconnect sounds are in. please do the usual with them";
    /// then "they should be played when you connect and disconnect to the server. you hear the peer sounds when you join a
    /// group"). The shipped sounds are the cues' own; one plays as you connect to a server and the other as you leave it, and
    /// neither when its group or its people change; switched off in Preferences, neither plays and the log says so.</summary>
    private static string? ServerSoundsPlay()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-server-sounds-" + Guid.NewGuid().ToString("N"));
        using var userData = AppConfig.UseThrowawayUserDataDirectory(scratch);
        var restoreMuted = CuePlayer.GloballyMuted;
        var restoreNoDevice = CuePlayer.NoDeviceForTest;
        MainForm? form = null;
        try
        {
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            CuePlayer.GloballyMuted = false;
            CuePlayer.NoDeviceForTest = true;   // counted, never heard
            var said = new ConcurrentQueue<string>();
            form.LogForTest.EventTapForTest = said.Enqueue;
            Check(Path.GetFileName(form.ServerConnectSoundFileForTest ?? "").StartsWith("server connect ", StringComparison.OrdinalIgnoreCase)
                  && Path.GetFileName(form.ServerDisconnectSoundFileForTest ?? "").StartsWith("server disconnect ", StringComparison.OrdinalIgnoreCase),
                $"THE SOUNDS: each cue must play its own shipped sound ({form.ServerConnectSoundFileForTest}; {form.ServerDisconnectSoundFileForTest})");

            var at = new IPEndPoint(IPAddress.Parse("203.0.113.95"), RemPacket.DefaultPort);
            var plays = CuePlayer.PlaysStartedForTest;
            form.ConnectToRelayForTest("server.example.test", at);
            Check(CuePlayer.PlaysStartedForTest == plays + 1 && said.Any(l => l.Contains("server sound: connected to the server (played)", StringComparison.Ordinal)),
                "CONNECTING: the server connected sound must play as you connect, and the log must say so");
            var roster = RosterPacket([(Guid.NewGuid(), "Andre", true)], paired: false);
            var length = roster.Length;
            form.RelayGroupForTest.HandleInbound(roster, ref length, at, out _);
            form.SyncRelayPeersListForTest();
            Check(CuePlayer.PlaysStartedForTest == plays + 1, "A GROUP: the server taking us into its group, with Andre on it, must not play the server sound again");
            form.DisconnectFromRelayForTest();
            Check(CuePlayer.PlaysStartedForTest == plays + 2 && said.Any(l => l.Contains("server sound: disconnected from the server (played)", StringComparison.Ordinal)),
                "DISCONNECTING: the server disconnected sound must play at once, and the log must say so");
            form.DisconnectFromRelayForTest();
            Check(CuePlayer.PlaysStartedForTest == plays + 2, "and not again when there was no server to leave");

            var cfg = AppConfig.Load();
            cfg.EnableServerConnectCue = false;
            cfg.Save();
            form.ConnectToRelayForTest("server.example.test", at);
            Check(CuePlayer.PlaysStartedForTest == plays + 2 && said.Any(l => l.Contains("server sound: connected to the server (switched off)", StringComparison.Ordinal)),
                "SWITCHED OFF: no sound, and the log must say it was switched off");
            form.LogForTest.EventTapForTest = null;
        }
        finally
        {
            CuePlayer.GloballyMuted = restoreMuted;
            CuePlayer.NoDeviceForTest = restoreNoDevice;
            try { form?.Dispose(); } catch { /* teardown */ }
        }
        return "each cue plays its own shipped sound: connected as you connect, disconnected as you leave, neither when a group "
             + "forms; switched off, nothing plays and the log says so";
    }
}
