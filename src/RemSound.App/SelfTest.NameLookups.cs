using System.Net;
using System.Reflection;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// NAMES THAT CANNOT BE LOOKED UP AT START ARE TRIED AGAIN, AND KEPT; A SERVER THAT MOVES IS FOLLOWED (2026-09-25 sweep,
/// Ed: "yes all 10"). A profile starting with Windows named its server and peers; at sign-in the names could not be looked
/// up yet, the app never tried again, and the next save forgot them.
/// </summary>
internal static partial class SelfTest
{
    private static string? NamesNotFoundAtStartAreTriedAgainAndKept()
    {
        var restorePeer = MainForm.PeerResolveForTest;
        var restoreRelay = MainForm.RelayResolveForTest;
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        MainForm? form = null;
        try
        {
            MainForm.PeerResolveForTest = _ => null;
            MainForm.RelayResolveForTest = _ => null;
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            var main = form;
            var lines = new List<string>();
            main.LogForTest.EventTapForTest = line => { lock (lines) lines.Add(line); };
            bool Logged(string words) { lock (lines) return lines.Any(l => l.Contains(words, StringComparison.Ordinal)); }
            bool Pumped(Func<bool> condition, int ms = 5000)
            {
                var until = DateTime.UtcNow.AddMilliseconds(ms);
                while (DateTime.UtcNow < until) { Application.DoEvents(); if (condition()) return true; Thread.Sleep(10); }
                return condition();
            }
            var studio = IPAddress.Parse("10.0.0.9");

            // At start, nothing can be looked up.
            main.ReconnectSavedPeersForTest(["studio-pc"]);
            Check(Pumped(() => main.PendingPeerNamesForTest.Contains("studio-pc")), "a peer name that can't be looked up must be kept to try again");
            Check(!main.SelectedSendEndpointsForTest().Any(e => e.Address.Equals(studio)), "premise: not connected yet");
            Check(main.GatherSelectedPeerEntriesForTest().Contains("studio-pc"),
                "THE SAVE: saving meanwhile must keep the peer in the profile - left out, the next start did not even try");
            var profile = Profile.NewBlank();
            profile.RelayServer = "relay.example.test";
            profile.RelayConnectOnStart = true;
            main.ApplyRelayProfileForTest(profile);
            Check(Pumped(() => main.PendingRelayNameForTest == "relay.example.test"), "the profile's server, not found, must be kept to try again");
            var saved = Profile.NewBlank();
            main.GatherRelayProfileForTest(saved);
            Check(saved.RelayConnectOnStart && saved.RelayServer == "relay.example.test",
                "THE SAVE: saving meanwhile must keep \"connect to this server at start\" - saved as off, the next start did not try");
            Check(Logged("could not resolve \"studio-pc\" - trying again") && Logged("could not resolve \"relay.example.test\" - trying again"),
                "THE LOG: each name that can't be looked up must be logged once, saying it will be tried again");

            // The network comes up: the names work now, and a network change looks them up at once.
            var server = new IPEndPoint(IPAddress.Parse("203.0.113.50"), RemPacket.DefaultPort);
            MainForm.PeerResolveForTest = _ => studio;
            // The once-a-second tick tries again by itself - every 30 s, the first time at once.
            main.SnapshotTickForTest();
            Check(Pumped(() => main.PendingPeerNamesForTest.Count == 0),
                "THE TICK: the once-a-second tick must look a waiting name up again by itself, not only when the network changes");
            MainForm.RelayResolveForTest = _ => server;
            var changed = Require(typeof(MainForm).GetMethod("OnNetworkChangedForLookups", BindingFlags.Instance | BindingFlags.NonPublic),
                "MainForm.OnNetworkChangedForLookups not found");
            changed.Invoke(main, [null, EventArgs.Empty]);
            Check(Pumped(() => main.SelectedSendEndpointsForTest().Any(e => e.Address.Equals(studio))),
                "THE RETRY: once the network changes, a peer name that can now be looked up must be connected");
            Check(Pumped(() => server.Equals(main.RelayGroupForTest.ConnectedRelay)),
                "and the profile's server must be connected");
            Check(main.PendingPeerNamesForTest.Count == 0 && main.PendingRelayNameForTest is null, "and nothing left waiting");
            Check(Logged("\"studio-pc\" found at last") && Logged("\"relay.example.test\" found at last"), "THE LOG: and each found at last");

            // The server goes quiet: the window's own server status is what starts the clock.
            main.SetProfilePasswordForTest("names-check");
            main.BackdateRelayConnectForTest(MainForm.RelayAnswerGrace + TimeSpan.FromSeconds(1));
            main.SyncRelayPeersListForTest();
            Check(main.RelayNotAnsweringSinceForTest is not null,
                "THE CLOCK: a server that has stopped answering must be noted as silent - nothing else ever starts the look-up again");

            // The server moves (a home connection with no fixed address). Silent 5 s: not looked up yet.
            var moved = new IPEndPoint(IPAddress.Parse("203.0.113.51"), RemPacket.DefaultPort);
            MainForm.RelayResolveForTest = _ => moved;
            main.RelaySilentSinceForTest(DateTime.UtcNow - TimeSpan.FromSeconds(5));
            main.RetryNameLookupsForTest(networkChanged: false);
            Thread.Sleep(200);
            Application.DoEvents();
            Check(server.Equals(main.RelayGroupForTest.ConnectedRelay), "a server silent for only a few seconds must not be looked up again yet");
            // Silent for longer: looked up, and followed.
            main.RelaySilentSinceForTest(DateTime.UtcNow - MainForm.RelayMovedAfter - TimeSpan.FromSeconds(1));
            main.RetryNameLookupsForTest(networkChanged: false);
            Check(Pumped(() => moved.Equals(main.RelayGroupForTest.ConnectedRelay)),
                $"THE MOVE: a server by name that has stopped answering must be looked up again and followed to its new address (on {main.RelayGroupForTest.ConnectedRelay})");
            Check(Logged("\"relay.example.test\" now looks up to 203.0.113.51:47830"), "THE LOG: the move must be logged");
        }
        finally
        {
            MainForm.PeerResolveForTest = restorePeer;
            MainForm.RelayResolveForTest = restoreRelay;
            try { form?.Dispose(); } catch { /* teardown */ }
            CuePlayer.GloballyMuted = restoreMuted;
        }
        return "a peer and a server the profile named, not found at start, were kept - and kept in what a save writes - then found at "
             + "once when the network changed; a server by name, silent for a few seconds, was left alone, and silent longer, looked "
             + "up again and followed to its new address; each logged";
    }
}
