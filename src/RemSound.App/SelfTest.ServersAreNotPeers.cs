using System.Collections.Concurrent;
using System.Net;
using RemSound.Core;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>SERVERS ARE NEVER PEERS (Ed, 2026-10-01: "a server should never be in a profile peer list. you can connect to
    /// them if the profile is connected but we shouldn't mix them up with peers"). Ed's evening, step by step: his server's
    /// address in his peers is taken out when he connects to it; after he leaves, its sound arriving is not accepted as a
    /// peer; it cannot be ticked, remembered, reconnected from a profile or typed in as one; the next window knows it too.
    /// A server's address check from an address he typed takes it out and asks; from one he did not type, it is taken out
    /// without a word. A person on a server, at the address the server gives them, is never mistaken for one.</summary>
    private static string? ServersAreNeverPeers()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-servers-not-peers-" + Guid.NewGuid().ToString("N"));
        using var userData = AppConfig.UseThrowawayUserDataDirectory(scratch);
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        MainForm? form = null;
        MainForm? next = null;
        try
        {
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            // This computer's own loopback is never a server: a relay run here and a second RemSound here share it.
            form.ConnectToRelayForTest("127.0.0.1:47999", new IPEndPoint(IPAddress.Loopback, 47999));
            form.DisconnectFromRelayForTest();
            Check(!form.IsKnownServerForTest(IPAddress.Loopback) && !AppConfig.Load().KnownServerAddresses.Contains("127.0.0.1"),
                "LOOPBACK: a server on 127.0.0.1 must never make 127.0.0.1 a server - a second RemSound here shares the address (the full gate, 2026-10-02)");
            var fresh = new MainForm(null, Profile.NewBlank(), null, null, headless: true);
            try { Check(!fresh.IsKnownServerForTest(IPAddress.Loopback), "nor through the remembered servers list, in the next window"); }
            finally { fresh.Dispose(); }
            var said = new ConcurrentQueue<string>();
            form.LogForTest.EventTapForTest = said.Enqueue;
            bool Said(string words) => said.Any(l => l.Contains(words, StringComparison.Ordinal));
            bool Offered(string address) => said.Any(l => l.Contains("answers as a relay", StringComparison.Ordinal) && l.Contains(address, StringComparison.Ordinal));

            var serverAt = IPAddress.Parse("203.0.113.80");
            var server = new IPEndPoint(serverAt, RemPacket.DefaultPort);
            PeerAnnouncement PeerAt(IPAddress a, string name) => new(Guid.NewGuid(), name, RemPacket.DefaultPort, true, true, DateTime.UtcNow, a);

            // The profile has the server among its peers, as Ed's did: ticked and remembered.
            form.SelectPeerForTest(PeerAt(serverAt, "82.35.70.222"));
            form.SettingsForTest.SaveRememberedPeers(["203.0.113.80", "somebody-else"]);
            Check(form.IsSelectedForTest(serverAt) && !form.IsKnownServerForTest(serverAt), "premise: a profile with the server among its peers");

            // Connecting to it as a server: known from now on, and taken out of the peers.
            form.ConnectToRelayForTest("server.example.test", server);
            Check(form.IsKnownServerForTest(serverAt), "CONNECTED: the server must be known as one");
            Check(!form.IsSelectedForTest(serverAt), "and must no longer be ticked as a peer");
            Check(form.SettingsForTest.LoadRememberedPeers().SequenceEqual(["somebody-else"]),
                $"and must be forgotten from the remembered peers, nobody else with it ({string.Join(", ", form.SettingsForTest.LoadRememberedPeers())})");
            Check(Said("servers: 203.0.113.80 is a server (you connected to it as a server)") && Said("203.0.113.80 was among the peers"),
                "the log must say it is a server and that it was taken out of the peers");
            Check(AppConfig.Load().KnownServerAddresses.Contains("203.0.113.80"), "and this computer must remember it");

            // Left the server: its sound arriving is not somebody calling (Ed, 20:05:29).
            form.DisconnectFromRelayForTest();
            SetAcceptMode(PeerAcceptMode.Automatic);
            form.SomeoneWantsToConnectForTest(server, samePassword: true);
            Check(!form.IsSelectedForTest(serverAt), "AFTER LEAVING: the server's own sound arriving must not be accepted as a peer");
            Check(Said("is a server, not a person - is sending to us, not accepted as a peer"), "and the log must say why");

            // Nor ticked, nor typed, nor reconnected from a profile.
            form.SelectPeerForTest(PeerAt(serverAt, "ticked by something"));
            Check(!form.IsSelectedForTest(serverAt) && Said("not ticked as a peer"), "TICKED: a server must not be ticked as a peer");
            var typing = form.AddManualPeerForTest("203.0.113.80");
            var until = Environment.TickCount64 + 5000;
            while (!typing.IsCompleted && Environment.TickCount64 < until) Application.DoEvents();
            Check(typing.IsCompleted && !form.IsSelectedForTest(serverAt) && Said("typed as a peer (\"203.0.113.80\"), not added"),
                "TYPED: a known server typed as a peer must not be added");
            Check(!form.SettingsForTest.LoadRememberedPeers().Contains("203.0.113.80"), "nor remembered");
            form.ReconnectSavedPeersForTest(["203.0.113.80"]);
            until = Environment.TickCount64 + 5000;
            while (!Said("is the server at 203.0.113.80, not a person - left out") && Environment.TickCount64 < until) Application.DoEvents();
            Check(Said("is the server at 203.0.113.80, not a person - left out") && !form.IsSelectedForTest(serverAt),
                "A PROFILE: a server saved among its peers must be left out when it reconnects");

            // An address check: from somebody typed, taken out and asked about; from somebody accepted, taken out quietly.
            var typedAt = IPAddress.Parse("203.0.113.81");
            form.MarkTypedPeerForTest(typedAt);
            form.SelectPeerForTest(PeerAt(typedAt, "typed"));
            form.ServerAnsweredAtForTest(new IPEndPoint(typedAt, RemPacket.DefaultPort));
            Check(form.IsKnownServerForTest(typedAt) && !form.IsSelectedForTest(typedAt) && Offered("203.0.113.81"),
                "TYPED, THEN IT ANSWERS AS A SERVER: taken out, and the person asked (nobody to ask in this copy, so logged)");
            var acceptedAt = IPAddress.Parse("203.0.113.82");
            form.SelectPeerForTest(PeerAt(acceptedAt, "accepted"));
            form.ServerAnsweredAtForTest(new IPEndPoint(acceptedAt, RemPacket.DefaultPort));
            Check(form.IsKnownServerForTest(acceptedAt) && !form.IsSelectedForTest(acceptedAt) && !Offered("203.0.113.82"),
                "NOT TYPED: taken out without a question - Ed was asked while typing another server's name");
            form.ServerAnsweredAtForTest(new IPEndPoint(IPAddress.Parse("203.0.113.83"), RemPacket.DefaultPort));
            Check(!form.IsKnownServerForTest(IPAddress.Parse("203.0.113.83")), "from somewhere nobody chose, an address check must say nothing");

            // A person on a server is never one.
            var member = IPAddress.Parse("242.1.2.3");
            form.NoteServerAddressForTest(member);
            Check(!form.IsKnownServerForTest(member), "A PERSON ON A SERVER, at the address the server gives them, must never count as a server");
            form.LogForTest.EventTapForTest = null;

            // The next window knows, and so do the remembered servers typed as addresses.
            form.SettingsForTest.SaveRememberedRelays(["198.51.100.90:47831"]);
            try { next = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            Check(next.IsKnownServerForTest(serverAt) && next.IsKnownServerForTest(IPAddress.Parse("198.51.100.90")),
                "THE NEXT WINDOW: must know the servers this computer has met, and a remembered server written as an address");
        }
        finally
        {
            SetAcceptMode(PeerAcceptMode.Prompt);
            try { form?.Dispose(); } catch { /* teardown */ }
            try { next?.Dispose(); } catch { /* teardown */ }
            CuePlayer.GloballyMuted = restoreMuted;
        }
        return "connecting to a server remembers it and takes it out of the peers and the remembered list; after leaving, its "
             + "sound is not accepted; it cannot be ticked, typed in or reconnected from a profile; an address check takes it "
             + "out, asking only if it was typed; a person on a server is never one; the next window knows them all";
    }
}
