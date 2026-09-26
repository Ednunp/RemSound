using System.Net;
using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App;

/// <summary>
/// NO ECHO LOOP, AND NO FLOOD REACHES THE WINDOW (2026-09-25 sweep; Ed: "yes all 10"). The app and the lock-screen service
/// answered every server address check from anyone, so one forged packet bounced between two RemSound computers for ever;
/// and junk remote-control packets from anyone each cost the window a call and a log line.
/// </summary>
internal static partial class SelfTest
{
    private static string? NoEchoLoopAndNoFloodReachesTheWindow()
    {
        // 1. The gate: only somebody we talk to, at most once a second each; refusals said at most once a minute.
        var lines = new List<string>();
        var gate = new AddrCheckEchoGate(lines.Add);
        var peer = new IPEndPoint(IPAddress.Parse("192.168.1.15"), 47830);
        var stranger = new IPEndPoint(IPAddress.Parse("198.51.100.66"), 47830);
        bool TalkingTo(IPAddress a) => a.Equals(peer.Address);
        var now = 1_000_000L;
        Check(gate.ShouldEcho(peer, TalkingTo, now), "a server or peer we talk to must be answered");
        Check(!gate.ShouldEcho(peer, TalkingTo, now + 10), "THE LOOP: but not again within a second - a bounce is at most one a second");
        Check(gate.ShouldEcho(peer, TalkingTo, now + 1100), "and again after a second");
        for (var i = 0; i < 1000; i++) Check(!gate.ShouldEcho(stranger, TalkingTo, now + 2000 + i), "THE STRANGER: an address check from anybody else must never be answered");
        Check(gate.RefusedCount == 1000 && lines.Count == 1, $"a thousand refused must be said once, not a thousand times ({lines.Count} lines)");
        gate.ShouldEcho(stranger, TalkingTo, now + 2000 + (long)AddrCheckEchoGate.RefusalLogInterval.TotalMilliseconds + 1);
        Check(lines.Count == 2, "and said again a minute later");

        // 2. The app's own question: whom is it talking to - its ticked peers and the server it is on, nobody else.
        MainForm? form = null;
        try
        {
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            var heartbeat = form.StartHeartbeatForTest((_, _, _) => true);
            heartbeat.SetTrackedPeers([peer]);
            Check(form.AddrCheckTalkingToForTest(peer.Address), "THE APP: a peer it has ticked is somebody it talks to");
            Check(!form.AddrCheckTalkingToForTest(stranger.Address), "THE APP: a stranger is not");
            var check = new byte[RemPacket.HeaderSize + 8];
            RemPacket.WriteHeader(check, RemPacketType.AddrCheck, 0xFFFF, 1);
            form.AddrCheckArrivedForTest(check, stranger);
            Check(form.AddrCheckGateForTest.RefusedCount == 1, "THE APP: an address check arriving from a stranger must go through the gate and be refused");
            var relay = new IPEndPoint(IPAddress.Parse("203.0.113.77"), RemPacket.DefaultPort);
            form.ConnectToRelayForTest("relay.example.test", relay);
            Check(form.AddrCheckTalkingToForTest(relay.Address), "THE APP: the server it is on is somebody it talks to - its check must still be answered");
            form.DisconnectFromRelayForTest();
            heartbeat.Dispose();
        }
        finally { try { form?.Dispose(); } catch { /* teardown */ } }

        // 3. The service asks the same question - it tracks the app (Ed's standing rule).
        var sender = new RemSound.Sender.AudioSender();
        var presence = new ServiceNetworkPresence(sender, null);
        try
        {
            var servicePeer = new IPEndPoint(IPAddress.Loopback, RemPacket.DefaultPeerDialPort);
            presence.Start(FreeUdpPort(), [servicePeer]);
            Check(presence.TalkingToForTest(servicePeer.Address), "THE SERVICE: a peer its profile names is somebody it talks to");
            Check(!presence.TalkingToForTest(stranger.Address), "THE SERVICE: a stranger is not - the service answered everybody too");
            var check = new byte[RemPacket.HeaderSize + 8];
            RemPacket.WriteHeader(check, RemPacketType.AddrCheck, 0xFFFF, 1);
            presence.EchoAddrCheckForTest(check, new IPEndPoint(stranger.Address, stranger.Port));
            Check(presence.EchoRefusedForTest == 1, "THE SERVICE: an address check arriving from a stranger must go through the gate and be refused");
        }
        finally
        {
            try { presence.Dispose(); } catch { /* teardown */ }
            try { sender.Dispose(); } catch { /* teardown */ }
        }

        // 4. Remote-control packets from senders not ticked stop at the receiver, counted, and never reach the window.
        using (var receiver = new AudioReceiver())
        {
            var reached = 0;
            receiver.OnRemoteControlReceived = (_, _) => reached++;
            receiver.SetAllowedSenders([peer]);
            var key = Require(RemSoundCrypto.ForPlainPassword("flood-password").Key, "a password must give a key");
            var sealedCommand = ControlSealing.Seal(key, RemoteControlKind.VolumeUp, 5, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var packet = new byte[RemPacket.HeaderSize + sealedCommand.Length];
            RemPacket.WriteHeader(packet, RemPacketType.Control, 0xFFFF, 1);
            sealedCommand.CopyTo(packet.AsSpan(RemPacket.HeaderSize));
            for (var i = 0; i < 500; i++) receiver.InjectExternalPacket(packet, packet.Length, stranger);
            Check(reached == 0 && receiver.ControlRefusedForTest == 500,
                $"THE FLOOD: remote-control packets from somebody not ticked must stop at the receiver ({reached} reached the window, {receiver.ControlRefusedForTest} refused)");
            receiver.InjectExternalPacket(packet, packet.Length, peer);
            Check(reached == 1, "while one from a ticked peer still gets through, to be checked against the password there");
        }
        return "an address check was answered only for a peer or server this copy talks to, at most once a second, and a thousand from a "
             + "stranger were refused with one log line; the app and the service ask the same question; and 500 remote-control packets "
             + "from somebody not ticked stopped at the receiver while a ticked peer's got through";
    }
}
