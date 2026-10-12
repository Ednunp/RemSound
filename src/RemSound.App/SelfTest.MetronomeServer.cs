using System.Net;
using RemSound.Core;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>METRONOME THROUGH THE SERVER (2026-09-29, PLAN-metronome-server-2026-09-29.md): the real relay, run under Python
    /// on this machine, and two windows on one password joining it, each sending sound as it would. One asks to lead: the
    /// other follows, the server's metronome is claimed and started, and each window plays its own grid from the server - its
    /// delay measured from its own sound. On one machine the two share a clock and a path, so their beats must fall together.
    /// The other then asks to take over: the leader is asked, says yes, and the server's metronome passes to the new master.
    /// REMSOUND_TEST_LIVE_RELAY=host:port runs it against a real relay instead (Ed, 2026-09-29: "you can update my pi and test
    /// with that") - never in the gate: its log is on that server, so the checks that read it are left to whoever reads it.</summary>
    private static string? MetronomeThroughTheServer()
    {
        // Its own questions only: the metronome's accuracy warning has a step of its own.
        using var noAccuracyWarning = WithoutMetronomeAccuracyWarning();
        var liveText = Environment.GetEnvironmentVariable("REMSOUND_TEST_LIVE_RELAY");
        var live = string.IsNullOrWhiteSpace(liveText) ? null : IPEndPoint.Parse(liveText.Trim());
        var root = FindSourceRoot();
        if (root is null && live is null) return Skip("the source tree is not reachable (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var python = FindPython();
        if (python is null && live is null) return Skip("there is no Python on this machine to run the relay (py.exe, or a python.exe outside WindowsApps)");

        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        var port = FreeUdpPort();
        var logDir = Path.Combine(Path.GetTempPath(), $"remsound-relay-metronome-{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "relay.log");
        var relayProcess = live is null ? StartRelay(python!, Path.Combine(root!, "server", "remsound-relay.py"), port, logPath) : null;
        var loggedEd = new List<string>();
        var loggedAndre = new List<string>();
        MainForm? ed = null, andre = null;
        var apartMs = double.NaN;
        using var stopFeeding = new CancellationTokenSource();
        Thread? feeder = null;
        try
        {
            if (relayProcess is not null)
                Check(WaitUntil(() => ReadShared(logPath).Contains("event=startup"), 10000) && !relayProcess.HasExited, $"the relay must start under {python}");
            var relay = live ?? new IPEndPoint(IPAddress.Loopback, port);
            var relayText = live is null ? $"127.0.0.1:{port}" : liveText!.Trim();
            try
            {
                // Two computers: each its own server id (kept in its own settings), as two real machines have.
                using (AppConfig.UseThrowawayUserDataDirectory(Path.Combine(logDir, "ed")))
                    (ed, _) = MetronomeLoopbackWindow("metronome-server", new MetronomeSettings(), null, loggedEd);
                using (AppConfig.UseThrowawayUserDataDirectory(Path.Combine(logDir, "andre")))
                    (andre, _) = MetronomeLoopbackWindow("metronome-server", new MetronomeSettings { WhenAsked = MetronomeMasterAnswer.AcceptAutomatically }, null, loggedAndre);
            }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            var a = ed;
            var b = andre;
            foreach (var w in new[] { a, b }) { w.SomebodyToAnswerForTest = true; w.NobodyToAskForTest = () => false; }
            var control = new RemoteControlEngine(() => a);
            bool Asking(MainForm w) => w.Invoke(() => Application.OpenForms.OfType<HeadlessQuestionForm>().Any());
            string Trail() => ReadShared(logPath).Split('\n').Where(l => l.Contains("metronome", StringComparison.Ordinal) || l.Contains("client_ticks", StringComparison.Ordinal))
                .TakeLast(6).Aggregate("", (s, l) => s + " | " + l.Trim()) + " || Ed: "
                + string.Join(" | ", loggedEd.Where(l => l.Contains("metronome", StringComparison.Ordinal)).TakeLast(4))
                + " || Andre: " + string.Join(" | ", loggedAndre.Where(l => l.Contains("metronome", StringComparison.Ordinal)).TakeLast(4))
                + $" || reports: Ed {a.Invoke(() => a.ReportsForTest)}; Andre {b.Invoke(() => b.ReportsForTest)}";

            // The window's once-a-second work (who is sent sound, the reports to the server) runs from its status timer, which a
            // hidden window does not start: run it here, each second, for both.
            using var secondTick = new System.Windows.Forms.Timer { Interval = 1000 };
            secondTick.Tick += (_, _) => { a.SnapshotTickForTest(); b.SnapshotTickForTest(); };
            secondTick.Start();
            DriveWhilePumping(a, () =>
            {
                // Both join the server, on the same password, and tick each other there.
                a.Invoke(() => a.ConnectToRelayForTest(relayText, relay));
                b.Invoke(() => b.ConnectToRelayForTest(relayText, relay));
                Check(WaitFor(() => a.Invoke(() => a.RelayGroupForTest.IsInGroup(relay) && a.RelayGroupForTest.Members.Count == 1)
                                    && b.Invoke(() => b.RelayGroupForTest.IsInGroup(relay) && b.RelayGroupForTest.Members.Count == 1), TimeSpan.FromSeconds(15)),
                    $"both windows must join the server's group for their password, and see each other (Ed: {a.Invoke(() => $"{a.RelayGroupForTest.IsInGroup(relay)} {a.RelayGroupForTest.Members.Count} {a.RelayStatusForTest}")}; "
                    + $"Andre: {b.Invoke(() => $"{b.RelayGroupForTest.IsInGroup(relay)} {b.RelayGroupForTest.Members.Count} {b.RelayStatusForTest}")}; relay: "
                    + string.Join(" | ", ReadShared(logPath).Split('\n').TakeLast(8).Select(l => l.Trim())) + ")");
                foreach (var w in new[] { a, b })
                {
                    Check(WaitFor(() => w.Invoke(() => { w.SyncRelayPeersListForTest(); return w.RelayPeersListForTest.Items.Count; }) >= 1, TimeSpan.FromSeconds(5)),
                        "the other must show in the window's list of people on the server");
                    w.Invoke(() => w.RelayPeersListForTest.SetItemChecked(0, true));
                }
                Check(WaitFor(() => live is null
                        ? ReadShared(logPath).Split('\n').Count(l => l.Contains("event=client_ticks", StringComparison.Ordinal) && l.Contains("ticked=1", StringComparison.Ordinal)) >= 2
                        : a.Invoke(() => a.RelayGroupForTest.Members.All(m => m.TicksUs)) && b.Invoke(() => b.RelayGroupForTest.Members.All(m => m.TicksUs)),
                    TimeSpan.FromSeconds(10)), $"each must tick the other on the server ({Trail()})");
                Check(a.Invoke(() => a.MetronomeForTest.OnServer) && b.Invoke(() => b.MetronomeForTest.OnServer), "and each window's metronome must know it is on a server");

                // Andre's own metronome, on a server and not leading: it clicks for him, and never says there is no server.
                b.SomebodyToAnswerForTest = false;
                b.Invoke(() => b.MetronomeStartStopForTest("the menu"));
                Check(b.Invoke(() => b.MetronomeForTest.Playing) && !loggedAndre.Any(l => l.Contains("started with no server", StringComparison.Ordinal)),
                    "ON A SERVER: Andre's own metronome must start without saying there is no server");
                b.Invoke(() => b.MetronomeStartStopForTest("the menu"));
                b.SomebodyToAnswerForTest = true;

                // Sound, as a capture would send it: 10 ms blocks, from each.
                feeder = new Thread(() =>
                {
                    var block = new float[480 * 2];
                    var next = Environment.TickCount64;
                    while (!stopFeeding.IsCancellationRequested)
                    {
                        a.SenderForTest.FeedDefaultLaneForTest(block);
                        b.SenderForTest.FeedDefaultLaneForTest(block);
                        next += 10;
                        var wait = next - Environment.TickCount64;
                        if (wait > 0) Thread.Sleep((int)wait);
                    }
                }) { IsBackground = true, Name = "self-test sound for the server's metronome" };
                feeder.Start();

                // Ed leads: Andre follows (set to accept); the server's metronome is claimed and started.
                a.Invoke(() => a.SetMetronomeMasterForTest(true));
                Check(WaitFor(() => b.Invoke(() => b.MetronomeForTest.Role == MetronomeRole.Follower), TimeSpan.FromSeconds(8)),
                    $"asked through the server, Andre (set to accept) must follow ({Trail()})");
                a.Invoke(() => a.MetronomeStartStopForTest("the menu"));
                Check(WaitFor(() => a.Invoke(() => a.MetronomeForTest.ServerGridNow is { Running: true, Measured: true, YouLead: true })
                                    && b.Invoke(() => b.MetronomeForTest.ServerGridNow is { Running: true, Measured: true, YouLead: false }), TimeSpan.FromSeconds(15)),
                    $"THE GRIDS: each window must get its own playing grid, its delay measured from its own sound, Ed's saying he leads ({Trail()})");
                var gridA = a.Invoke(() => a.MetronomeForTest.ServerGridNow!);
                var gridB = b.Invoke(() => b.MetronomeForTest.ServerGridNow!);
                Check(gridA.Master == gridB.Master && gridA.Master == a.Invoke(() => a.RelayGroupForTest.ClientId), "both grids must name Ed as the one who leads it");
                var nowA = a.Invoke(() => a.MetronomeForTest.NowUs());
                Check(gridA.Timeline is { MilliBpm: 120_000, BeatsPerBar: 4, BeatUnit: 4 } && Math.Abs(gridA.Timeline.AnchorTimeUs - nowA) < 60_000_000,
                    $"THE WIRE: the grid read from the server must carry Ed's setting, 120 bpm in 4/4, and a beat in this computer's own time ({gridA.Timeline.Describe()}; now {nowA})");
                Check(a.Invoke(() => a.MetronomeForTest.CurrentSchedule is { Audible: true }) && b.Invoke(() => b.MetronomeForTest.CurrentSchedule is { Audible: true }),
                    "and each must play from it");
                Check(loggedEd.Any(l => l.Contains("metronome: the server times this computer's sound from the WASAPI input, its delay taken as", StringComparison.Ordinal)),
                    "THE LOG: each window must say which input the server times it by, and the delay it takes on trust");
                // One machine, one clock, one path: the two beats must fall together.
                var beat = gridA.Timeline.BeatAtOrAfter(a.Invoke(() => (double)a.MetronomeForTest.NowUs()) + 1_000_000);
                var apart = Math.Abs(gridA.Timeline.BeatTimeUs(beat) - gridB.Timeline.BeatTimeUs(beat)) / 1000;
                apartMs = apart;
                Check(apart < 3, $"ONE MACHINE: sharing a clock and a path to the server, the two windows' beats must fall within 3 ms of each other ({apart:0.00} ms)");
                Check(a.Invoke(() => a.MetronomeStatusForTest)?.Contains("master metronome through the server", StringComparison.Ordinal) == true
                      && b.Invoke(() => b.MetronomeStatusForTest)?.Contains("through the server", StringComparison.Ordinal) == true,
                    $"the status lines must say it is through the server ({a.Invoke(() => a.MetronomeStatusForTest)} / {b.Invoke(() => b.MetronomeStatusForTest)})");

                // Andre asks to take over: Ed is asked, says yes, and the server's metronome passes to Andre.
                b.Invoke(() => b.SetMetronomeMasterForTest(true));
                Check(WaitFor(() => Asking(a), TimeSpan.FromSeconds(6)), $"TAKE OVER: Ed, leading, must be asked ({Trail()})");
                Check(a.Invoke(() => control.Execute("answer Yes")).StartsWith("ok", StringComparison.Ordinal), "the question must take Yes");
                Check(WaitFor(() => b.Invoke(() => b.MetronomeForTest.ServerGridNow is { YouLead: true })
                                    && a.Invoke(() => a.MetronomeForTest.Role == MetronomeRole.Follower), TimeSpan.FromSeconds(10)),
                    $"and the server's metronome must pass to Andre, Ed following ({Trail()})");
            }, TimeSpan.FromSeconds(90));
            if (live is null)
                Check(ReadShared(logPath).Contains("event=metronome_master", StringComparison.Ordinal)
                      && ReadShared(logPath).Contains("event=metronome_set", StringComparison.Ordinal), "and the server's log must say who leads it and what they set");
            if (live is not null)
                return $"THROUGH THE REAL SERVER {live}: followed, claimed, started, each played its own measured grid, the beats {apartMs:0.00} ms apart, "
                     + $"handed over; now Ed {a.Invoke(() => a.MetronomeStatusForTest)}; Andre {b.Invoke(() => b.MetronomeStatusForTest)}; "
                     + $"reports: Ed {a.Invoke(() => a.ReportsForTest)}; Andre {b.Invoke(() => b.ReportsForTest)}";
            return "two windows on one server, each sending sound: asked through the server, followed; the server's metronome claimed and "
                 + "started; each window played its own grid, measured from its own sound, the two beats together on one machine; the "
                 + "lead handed over through the server";
        }
        finally
        {
            stopFeeding.Cancel();
            feeder?.Join(2000);
            // Each window leaves the server first, as closing RemSound does: on a real server (REMSOUND_TEST_LIVE_RELAY) a
            // window only thrown away was listed there for another minute, until it timed out (the live tests, 2026-10-02).
            foreach (var w in new[] { ed, andre })
            {
                try { w?.DisconnectFromRelayForTest(); } catch { /* teardown */ }
                try { w?.Dispose(); } catch { /* teardown */ }
            }
            try { relayProcess?.Kill(entireProcessTree: true); } catch { /* already gone */ }
            relayProcess?.Dispose();
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
            try { Directory.Delete(logDir, recursive: true); } catch { /* the relay may still hold its log a moment */ }
        }
    }
}
