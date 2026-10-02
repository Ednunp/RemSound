using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RemSound.Core;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>OUT-OF-DATE SERVERS (Ed, 2026-10-01: "when RemSound connects to a server, it should check the server version
    /// and compare it to the latest available on github. if there is a version mismatch or if a server doesn't say its
    /// version, you get a warning that always shows ... the buttons are connect anyway or cancel"; 2026-10-02: "it should
    /// give you the warning instantly and not connect"). The rule: a server is out of date when it says no version or one
    /// older than the newest - GitHub's highest plain server release, or this build's own server when that is newer or
    /// GitHub can't be reached; a test build ahead of its release is not. The newest is read from a stand-in release list as
    /// GitHub's would be, drafts, prereleases and test tags passed over, and asked for once. In the window, the server is
    /// checked BEFORE connecting: out of date, Ed's warning (his words, Connect anyway, Visit GitHub or Cancel, no tick box,
    /// every time); Cancel never connects - no sound, nobody told; Connect anyway connects as ever; up to date connects
    /// without a word; a server overtaken by leaving is not connected to; one that does not answer the check is connected to
    /// and judged once it says its version, after. Each logged with the configuration.</summary>
    private static string? OutOfDateServers()
    {
        // The rule.
        Check(ServerVersionCheck.Parse("server-v2.16") == new Version(2, 16, 0) && ServerVersionCheck.Parse("server-v2.13.3-metronome-test") == new Version(2, 13, 3)
              && ServerVersionCheck.Parse("v6.1") is null && ServerVersionCheck.Parse("") is null && ServerVersionCheck.Parse(null) is null,
            "a server's version is read from server-vX.Y, a test build's words after it set aside, and nothing else");
        Check(ServerVersionCheck.IsPlainReleaseTag("server-v2.16") && !ServerVersionCheck.IsPlainReleaseTag("server-v2.13.3-metronome-test")
              && !ServerVersionCheck.IsPlainReleaseTag("v6.0"), "only a plain server-vX.Y is a release");
        Check(!ServerVersionCheck.IsOutOfDate("server-v2.17", "server-v2.17"), "THE NEWEST: not out of date");
        Check(ServerVersionCheck.IsOutOfDate("server-v2.16", "server-v2.17") && ServerVersionCheck.IsOutOfDate("server-v2.13.3-metronome-test", "server-v2.17"),
            "OLDER than GitHub's newest: out of date");
        Check(ServerVersionCheck.IsOutOfDate(null, "server-v2.17") && ServerVersionCheck.IsOutOfDate("", "server-v2.17") && ServerVersionCheck.IsOutOfDate("hello", "server-v2.17"),
            "SAYS NO VERSION: out of date (Ed: \"or if a server doesn't say its version\")");
        Check(!ServerVersionCheck.IsOutOfDate("server-v2.18", "server-v2.17"), "NEWER than the newest release, a test build ahead of it: not out of date");
        Check(ServerVersionCheck.IsOutOfDate("server-v2.15", null) && !ServerVersionCheck.IsOutOfDate(ServerVersionCheck.BuiltWith, null),
            "GITHUB NOT REACHED: judged against the server this build was made with");
        Check(ServerVersionCheck.IsOutOfDate("server-v2.15", "server-v2.10"), "and GitHub's list older than this build's own server does not let an older server through");
        var root = FindSourceRoot();
        if (root is not null)
        {
            var file = File.ReadAllText(Path.Combine(root, "server", "VERSION")).Trim();
            Check(file == ServerVersionCheck.BuiltWith, $"THIS BUILD'S SERVER: ServerVersionCheck.BuiltWith ({ServerVersionCheck.BuiltWith}) must be server/VERSION ({file})");
        }

        // The newest release, from a stand-in for GitHub's list.
        const string list = "[{\"tag_name\":\"v6.0\",\"draft\":false,\"prerelease\":false,\"assets\":[]},"
            + "{\"tag_name\":\"server-v2.15\",\"draft\":false,\"prerelease\":false,\"assets\":[]},"
            + "{\"tag_name\":\"server-v2.17\",\"draft\":false,\"prerelease\":false,\"assets\":[]},"
            + "{\"tag_name\":\"server-v2.20.1-metronome-test\",\"draft\":false,\"prerelease\":false,\"assets\":[]},"
            + "{\"tag_name\":\"server-v2.30\",\"draft\":true,\"prerelease\":false,\"assets\":[]},"
            + "{\"tag_name\":\"server-v2.25\",\"draft\":false,\"prerelease\":true,\"assets\":[]}]";
        var restoreUrl = RemSoundUpdater.ReleasesUrlForTest;
        var restoreGate = ServerVersionCheck.ForGateRun;
        var restoreMuted = CuePlayer.GloballyMuted;
        var restoreNoDevice = CuePlayer.NoDeviceForTest;
        MainForm? form = null;
        try
        {
            ServerVersionCheck.ForGateRun = true;
            ServerVersionCheck.ForgetForTest();
            RemSoundUpdater.ReleasesUrlForTest = null;
            var none = ServerVersionCheck.LatestReleasedAsync(new RemSoundUpdater());
            Check(none.IsCompleted && none.Result is null, "A GATE RUN: GitHub itself is never asked");
            using var releases = new ReleaseListServer(list);
            RemSoundUpdater.ReleasesUrlForTest = releases.Url;
            var newest = ServerVersionCheck.LatestReleasedAsync(new RemSoundUpdater()).GetAwaiter().GetResult();
            Check(newest == "server-v2.17", $"GITHUB'S NEWEST: the highest plain server release, passing over the app's own, drafts, prereleases and test tags ({newest})");
            var again = ServerVersionCheck.LatestReleasedAsync(new RemSoundUpdater()).GetAwaiter().GetResult();
            Check(again == "server-v2.17" && Volatile.Read(ref releases.Requests) == 1, $"and kept: asked once, not on every connection ({releases.Requests} asks)");

            // The window: the server checked before connecting, the check answered by the test.
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            CuePlayer.GloballyMuted = false;
            CuePlayer.NoDeviceForTest = true;   // counted, never heard
            var said = new ConcurrentQueue<string>();
            form.LogForTest.EventTapForTest = said.Enqueue;
            var shown = new List<TaskDialogPage>();
            var connectAnyway = false;
            form.OutOfDateServerAnswerForTest = page => { shown.Add(page); return connectAnyway; };
            form.CheckServerFirstForTest = true;
            var checkedAt = new List<IPEndPoint>();
            ServerVersionProbe.Result answer = new(true, "server-v2.16", 3, false);
            form.ServerProbeForTest = server => { checkedAt.Add(server); return Task.FromResult(answer); };
            var at = new IPEndPoint(IPAddress.Parse("203.0.113.91"), RemPacket.DefaultPort);
            var group = form.RelayGroupForTest;
            bool Said(string words) => said.Any(l => l.Contains(words, StringComparison.Ordinal));
            int Count(string words) => said.Count(l => l.Contains(words, StringComparison.Ordinal));
            void Pump() { for (var i = 0; i < 5; i++) Application.DoEvents(); }

            var plays = CuePlayer.PlaysStartedForTest;
            form.ConnectToRelayForTest("old.example.test", at);
            Pump();
            Check(checkedAt.Count == 1 && checkedAt[0].Equals(at) && Said("server version: checking old.example.test (203.0.113.91:47830) before connecting ["),
                "BEFORE CONNECTING: the server must be checked first, and the log says so");
            Check(shown.Count == 1, "OUT OF DATE (server-v2.16, the newest server-v2.17): the warning must be shown");
            var warning = shown[0];
            Check(warning.Text == "Warning: you are connecting to a RemSound server that is out of date. Please contact the server administrator to request an update "
                                + "to the latest version for features and security enhancements.", $"in Ed's words ({warning.Text})");
            // WinForms hands back an empty tick box when none was given; one with no words is never shown.
            Check(string.IsNullOrEmpty(warning.Verification?.Text), $"with no tick box to switch it off ({warning.Verification?.Text})");
            var buttons = warning.Buttons.ToList();
            Check(buttons.Count == 3 && buttons[0].Text == "&Connect anyway" && buttons[1].Text == "&Visit GitHub for the latest RemSound server code"
                  && buttons[2] == TaskDialogButton.Cancel && warning.DefaultButton == TaskDialogButton.Cancel,
                $"Connect anyway, Visit GitHub for the latest RemSound server code, or Cancel; Cancel for Enter ({string.Join(", ", buttons.Select(b => b.Text))})");
            // The GitHub button (Ed, 2026-10-02): the newest release's page in the browser, and the warning stays up.
            Check(!buttons[1].AllowCloseDialog, "VISIT GITHUB: the warning must stay up, so Connect anyway or Cancel is still to choose");
            var browsed = new List<string>();
            var restoreBrowser = HelpLauncher.BrowserForTest;
            HelpLauncher.BrowserForTest = browsed.Add;
            try
            {
                var onClick = Require(typeof(TaskDialogButton).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public),
                    "TaskDialogButton.OnClick not found");
                onClick.Invoke(buttons[1], [EventArgs.Empty]);
            }
            finally { HelpLauncher.BrowserForTest = restoreBrowser; }
            Check(browsed.SequenceEqual(["https://github.com/Ednunp/RemSound/releases/tag/server-v2.17"]) && Said("browser: opening the latest RemSound server code on GitHub"),
                $"and open the newest server release's page, logged ({string.Join(" | ", browsed)})");
            Check(ServerVersionCheck.LatestCodeUrl(null) == "https://github.com/Ednunp/RemSound/releases"
                  && ServerVersionCheck.LatestCodeUrl("server-v2.20.1-metronome-test") == "https://github.com/Ednunp/RemSound/releases",
                "with GitHub not reached, the release list instead");
            Check(group.ConnectedRelay is null && CuePlayer.PlaysStartedForTest == plays && !Said("relay: connected to \"old.example.test\""),
                "CANCEL: never connected - no connect sound, and the log never says connected");
            Check(Said("server version: old.example.test says server-v2.16; the newest is server-v2.17") && Said("OUT OF DATE [")
                  && Said("server version: Cancel - not connecting to the out-of-date server"), "and the log says why, with the configuration");

            connectAnyway = true;
            form.ConnectToRelayForTest("old.example.test", at);
            Pump();
            Check(shown.Count == 2, "EVERY TIME: the same server again is warned about again");
            Check(at.Equals(group.ConnectedRelay) && CuePlayer.PlaysStartedForTest == plays + 1 && Said("server version: Connect anyway - connecting to the out-of-date server"),
                "CONNECT ANYWAY: connected as ever, the connect sound once, and logged");
            Check(!form.ServerVersionPendingForTest, "and, judged already, not judged again after connecting");
            form.DisconnectFromRelayForTest();

            answer = new(true, "server-v2.17", 3, false);
            form.ConnectToRelayForTest("old.example.test", at);
            Pump();
            Check(shown.Count == 2 && at.Equals(group.ConnectedRelay) && Said("old.example.test says server-v2.17; the newest is server-v2.17 (GitHub's newest is server-v2.17) - up to date"),
                "UP TO DATE: connected without a word, and logged");
            form.DisconnectFromRelayForTest();

            answer = new(true, null, null, false);
            form.ConnectToRelayForTest("old.example.test", at);
            Pump();
            Check(shown.Count == 3 && Said("old.example.test says no version (it's older than server-v2.16)"),
                "SAYS NO VERSION: an older server, warned about before connecting");
            form.DisconnectFromRelayForTest();
            connectAnyway = true;
            form.ConnectToRelayForTest("old.example.test", at);
            Pump();
            Check(at.Equals(group.ConnectedRelay) && form.ServerKnownOlderForTest(at)
                  && (form.ServerStatusForTest() ?? "").Contains("it's older than server-v2.16", StringComparison.Ordinal)
                  && !(form.ServerStatusForTest() ?? "").Contains("measuring", StringComparison.Ordinal),
                $"KNOWN OLDER, CONNECTED ANYWAY: the status says so at once, not \"measuring\" ({form.ServerStatusForTest()?.Replace(Environment.NewLine, " | ")})");
            form.DisconnectFromRelayForTest();
            connectAnyway = false;

            // Left before the check came back: the check does nothing.
            var later = new TaskCompletionSource<ServerVersionProbe.Result>(TaskCreationOptions.RunContinuationsAsynchronously);
            form.ServerProbeForTest = _ => later.Task;
            var connectsBefore = Count("relay: connected to");
            form.ConnectToRelayForTest("old.example.test", at);
            Pump();
            Check(group.ConnectedRelay is null && shown.Count == 4, "CHECKING: not connected while the check is out");
            form.DisconnectFromRelayForTest();
            later.SetResult(new(true, "server-v2.17", 3, false));
            var waitUntil = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < waitUntil) { Application.DoEvents(); Thread.Sleep(10); }
            Check(group.ConnectedRelay is null && Count("relay: connected to") == connectsBefore && shown.Count == 4,
                "LEFT BEFORE THE CHECK CAME BACK: never connected afterwards");

            // A server that does not answer the check: connected as before, and judged once it says its version.
            form.ServerProbeForTest = _ => Task.FromResult(new ServerVersionProbe.Result(false, null, null, false));
            connectAnyway = false;
            form.ConnectToRelayForTest("old.example.test", at);
            Pump();
            Check(at.Equals(group.ConnectedRelay) && form.ServerVersionPendingForTest && Said("old.example.test did not answer the check - connecting as usual"),
                "NO ANSWER TO THE CHECK: connected as usual, with its version still to judge");
            var roster = RosterPacket([(Guid.NewGuid(), "Andre", true)], paired: false);
            var length = roster.Length;
            group.HandleInbound(roster, ref length, at, out _);
            var status = StatusPacket("server-v2.16");
            length = status.Length;
            group.HandleInbound(status, ref length, at, out _);
            form.TickServerVersionCheckForTest();
            Check(shown.Count == 5 && group.ConnectedRelay is null && Said("server version: Cancel - leaving the out-of-date server"),
                "and once it says it is out of date, the warning; Cancel leaves it");

            ServerVersionCheck.ForgetForTest();
            RemSoundUpdater.ReleasesUrlForTest = null;
            form.ServerProbeForTest = _ => Task.FromResult(new ServerVersionProbe.Result(true, "server-v2.15", 3, false));
            form.ConnectToRelayForTest("old.example.test", at);
            Pump();
            Check(shown.Count == 6 && Said($"says server-v2.15; the newest is {ServerVersionCheck.BuiltWith} (this build's own: GitHub was not reached)"),
                "GITHUB NOT REACHED: an older server than this build's own is still warned about, and the log says which was used");

            form.OutOfDateServerAnswerForTest = null;
            form.ConnectToRelayForTest("old.example.test", at);
            Pump();
            Check(at.Equals(group.ConnectedRelay) && Said("the out-of-date warning was not shown - nobody to answer it; connecting"),
                "NOBODY TO ANSWER (headless): logged, and connected");
            form.DisconnectFromRelayForTest();
            form.LogForTest.EventTapForTest = null;
        }
        finally
        {
            RemSoundUpdater.ReleasesUrlForTest = restoreUrl;
            ServerVersionCheck.ForGateRun = restoreGate;
            ServerVersionCheck.ForgetForTest();
            CuePlayer.GloballyMuted = restoreMuted;
            CuePlayer.NoDeviceForTest = restoreNoDevice;
            try { form?.Dispose(); } catch { /* teardown */ }
        }
        return "out of date when it says no version or an older one than GitHub's newest plain release, or this build's own without "
             + "GitHub; a newer test build is not; the newest read once from a stand-in list; the server checked BEFORE connecting: "
             + "Ed's words, Connect anyway, Visit GitHub (the warning left up) or Cancel, no tick box, every time; Cancel never "
             + "connects (no sound), Connect anyway connects with it, up to date says nothing, leaving first stops it, no answer "
             + "connects and judges after; all logged";
    }

    /// <summary>SERVER CHECK (the check before connecting, 2026-10-02): the real relay from this tree says its version within
    /// a second, under a throwaway name, and the check says goodbye; an older server - one that answers a hello with its
    /// address check and never pings back - is told by its silence about as fast; and an address with nothing there is told
    /// apart from both.</summary>
    private static string? ServerCheckBeforeConnecting()
    {
        // An older server, as server-v2.15 and before: a hello gets an address check back, and nothing else ever does.
        using var older = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var olderAt = (IPEndPoint)older.Client.LocalEndPoint!;
        using var stopOlder = new CancellationTokenSource();
        var olderLoop = Task.Run(async () =>
        {
            while (!stopOlder.IsCancellationRequested)
            {
                UdpReceiveResult got;
                try { got = await older.ReceiveAsync(stopOlder.Token); }
                catch { return; }
                if (got.Buffer.Length > 5 && got.Buffer[5] == RelayGroupClient.TypeHello)
                {
                    var check = new byte[RemPacket.HeaderSize + 16];
                    RemPacket.WriteHeader(check, RemPacketType.AddrCheck, 0, 0);
                    try { await older.SendAsync(check, check.Length, got.RemoteEndPoint); } catch { /* the probe has gone */ }
                }
            }
        });
        var clock = Stopwatch.StartNew();
        var olderResult = ServerVersionProbe.ProbeAsync(olderAt).GetAwaiter().GetResult();
        var olderTook = clock.Elapsed;
        stopOlder.Cancel();
        Check(olderResult.Answered && olderResult.Version is null && olderResult.RoundTripMs is null,
            $"AN OLDER SERVER: answered, but no version and no ping back ({olderResult})");
        Check(olderTook < TimeSpan.FromSeconds(1.6), $"and told within about a second, not the old 8 ({olderTook.TotalMilliseconds:0} ms)");

        // Nothing there.
        using var nobody = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var nobodyAt = (IPEndPoint)nobody.Client.LocalEndPoint!;
        nobody.Dispose();
        clock.Restart();
        var nothing = ServerVersionProbe.ProbeAsync(nobodyAt, TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        Check(!nothing.Answered && nothing.Version is null, $"NOTHING THERE: not answered ({nothing}, {clock.ElapsedMilliseconds} ms)");

        // The real relay from this tree.
        var root = FindSourceRoot();
        var python = FindPython();
        if (root is null || python is null)
            return "an older server told by its silence within a second, and nothing there told apart; the real relay was not run "
                 + "(no source tree or Python here)";
        var port = FreeUdpPort();
        var logDir = Path.Combine(Path.GetTempPath(), $"remsound-relay-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "relay.log");
        using var relayProcess = StartRelay(python, Path.Combine(root, "server", "remsound-relay.py"), port, logPath);
        try
        {
            Check(WaitUntil(() => ReadShared(logPath).Contains("event=startup"), 10000) && !relayProcess.HasExited, $"the relay must start under {python}");
            clock.Restart();
            var real = ServerVersionProbe.ProbeAsync(new IPEndPoint(IPAddress.Loopback, port)).GetAwaiter().GetResult();
            var took = clock.Elapsed;
            var tree = File.ReadAllText(Path.Combine(root, "server", "VERSION")).Trim();
            Check(real.Answered && real.Version == tree && real.RoundTripMs is not null, $"THE REAL RELAY: it must say its version ({tree}) and ping back ({real})");
            Check(took < TimeSpan.FromSeconds(1.5), $"within about a second ({took.TotalMilliseconds:0} ms)");
            Check(WaitUntil(() => ReadShared(logPath).Contains($"name='{ServerVersionProbe.Name}'") && ReadShared(logPath).Contains("reason=bye"), 3000),
                "under its throwaway name, and it must say goodbye so the server forgets it at once");
            return $"the real relay said {real.Version} in {took.TotalMilliseconds:0} ms under the name \"{ServerVersionProbe.Name}\" and was told goodbye; "
                 + $"an older server was told by its silence in {olderTook.TotalMilliseconds:0} ms; nothing there was told apart";
        }
        finally
        {
            try { relayProcess.Kill(entireProcessTree: true); } catch { /* already gone */ }
            try { Directory.Delete(logDir, recursive: true); } catch { /* the relay may still hold its log */ }
        }
    }

    /// <summary>The server's status packet as server-v2.16 sends it, saying <paramref name="version"/>.</summary>
    private static byte[] StatusPacket(string version)
    {
        var said = Encoding.ASCII.GetBytes(version);
        var status = new byte[RelayGroupClient.GroupHeaderSize + 2 + said.Length + 24];
        RemPacket.WriteHeader(status, (RemPacketType)RelayGroupClient.TypeServerInfo, 0, 0);
        status[4] = RelayGroupClient.GroupVersion;
        status[RelayGroupClient.GroupHeaderSize] = 1;
        status[RelayGroupClient.GroupHeaderSize + 1] = (byte)said.Length;
        said.CopyTo(status, RelayGroupClient.GroupHeaderSize + 2);
        var body = status.AsSpan(RelayGroupClient.GroupHeaderSize + 2 + said.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(body[4..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(body[6..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(body[8..], 62);
        return status;
    }
}
