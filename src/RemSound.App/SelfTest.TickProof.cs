using System.Net;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// ANYONE WITH THE PASSWORD IS ASKED ABOUT OR ACCEPTED, WHATEVER THEY SEND OR RECEIVE - AND NOBODY WITHOUT IT (Ed,
/// 2026-09-25, in the live phone test). Only audio proved the password, so a phone that ticked this PC only to listen was
/// never accepted. Now every peer sends everybody it has ticked a proof sealed with the password (packet type 11), and
/// "Accept connections" acts on that.
/// </summary>
internal static partial class SelfTest
{
    private static string? AnyoneWithThePasswordIsOfferedOrAccepted()
    {
        var key = Require(RemSoundCrypto.ForPlainPassword("tick-proof-password").Key, "a password must give a key");
        var otherKey = Require(RemSoundCrypto.ForPlainPassword("somebody-elses-password").Key, "a password must give a key");
        var device = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // 1. The seal: opens with the password, names the device and the time, and nothing else opens it.
        var sealedProof = TickProof.Seal(key, device, now);
        Check(sealedProof.Length == TickProof.SealedPayloadBytes && TickProof.SealedPayloadBytes == 53, $"the sealed proof must be 53 bytes on the wire (was {sealedProof.Length})");
        Check(TickProof.TryUnseal(key, sealedProof, out var who, out var when, out _) && who == device && when == now,
            "THE PROOF: sealed with the password, it must open with it and name the device and the time it was made");
        Check(!TickProof.TryUnseal(otherKey, sealedProof, out _, out _, out _), "THE PASSWORD: a different password must not open it");
        var tampered = (byte[])sealedProof.Clone();
        tampered[20] ^= 0x01;
        Check(!TickProof.TryUnseal(key, tampered, out _, out _, out _), "a proof changed in flight must not open");
        var idBytes = new byte[16];
        device.TryWriteBytes(idBytes, bigEndian: true, out _);
        Check(Convert.ToHexString(idBytes).Equals(device.ToString("N"), StringComparison.OrdinalIgnoreCase),
            "the device id travels in RFC 4122 order - the bytes its text form reads left to right - so the phone apps can match it");

        // 2. The guard: fresh accepted once; a replay, a stale one and another password refused.
        var guard = new TickProofGuard();
        var utcNow = DateTime.UtcNow;
        Check(guard.TryAccept(key, sealedProof, utcNow, out var accepted, out _) && accepted == device, "a fresh proof must be accepted");
        Check(!guard.TryAccept(key, sealedProof, utcNow, out _, out var replayWhy) && replayWhy.StartsWith("replay", StringComparison.Ordinal),
            "THE REPLAY: the same proof again - a captured one, sent from anywhere - must be refused");
        var old = TickProof.Seal(key, device, now - (long)TimeSpan.FromMinutes(30).TotalSeconds);
        Check(!guard.TryAccept(key, old, utcNow, out _, out var staleWhy) && staleWhy.StartsWith("stale", StringComparison.Ordinal), "a proof from half an hour ago must be refused");
        Check(!guard.TryAccept(otherKey, TickProof.Seal(key, device, now), utcNow, out _, out var keyWhy) && keyWhy == "not our password",
            "a proof made with another password must be refused as such");

        // 3. The packet: type 11, handed up by the receiver as it arrives, whoever sent it; the wrong size dropped.
        var packet = TickProof.BuildPacket(key, device, now, 7);
        Check(RemPacket.TryReadHeader(packet, out var type, out _, out _) && type == RemPacketType.TickProof && (byte)type == 11,
            "the packet must carry type 11 in the ordinary header");
        using (var receiver = new RemSound.Receiver.AudioReceiver())
        {
            var handedUp = new List<(byte[] Payload, IPEndPoint From)>();
            receiver.OnTickProofReceived = (p, from) => handedUp.Add((p, from));
            var from = new IPEndPoint(IPAddress.Parse("192.0.2.8"), 50001);
            receiver.InjectExternalPacket(packet, packet.Length, from);
            Check(handedUp.Count == 1 && handedUp[0].From.Equals(from) && handedUp[0].Payload.AsSpan().SequenceEqual(packet.AsSpan(RemPacket.HeaderSize)),
                "THE RECEIVER: a proof from somebody not ticked must be handed up, untouched, with where it came from");
            var shortPacket = packet[..^1];
            receiver.InjectExternalPacket(shortPacket, shortPacket.Length, from);
            Check(handedUp.Count == 1, "a proof of the wrong size must be dropped before it reaches the app");
        }

        // 4. The window: asked about or accepted as the setting says - the very device the proof names.
        var saved = AppConfig.Load();
        var restoreMode = saved.AcceptPeerConnections;
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        MainForm? form = null;
        try
        {
            var profile = Profile.NewBlank();
            profile.Password = RemSoundCrypto.Obfuscate("tick-proof-password");
            try { form = new MainForm(null, profile, null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            var lines = new List<string>();
            form.LogForTest.EventTapForTest = line => { lock (lines) lines.Add(line); };
            bool Connected(IPAddress a) => form!.SelectedSendEndpointsForTest().Any(e => e.Address.Equals(a));
            bool Logged(string words) { lock (lines) return lines.Any(l => l.Contains(words, StringComparison.Ordinal)); }
            var discovery = Require(FieldOf<PeerDiscoveryService>(form, "discovery"), "MainForm.discovery not found");
            // As a running copy does before anything arrives: the profile's password becomes its key.
            Require(typeof(MainForm).GetMethod("RecomputeAudioCrypto", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
                "MainForm.RecomputeAudioCrypto not found").Invoke(form, null);
            byte[] ProofFrom(Guid id) => TickProof.Seal(key, id, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            // Two phones of one name, as on the night. His proves the password; the proof names HIS phone.
            var his = new PeerAnnouncement(Guid.NewGuid(), "iPhone", 47830, true, true, DateTime.UtcNow, IPAddress.Parse("192.168.1.8"));
            var hers = new PeerAnnouncement(Guid.NewGuid(), "iPhone", 47830, true, true, DateTime.UtcNow, IPAddress.Parse("192.168.1.241"));
            discovery.RecordForTest(his);
            discovery.RecordForTest(hers);

            SetAcceptMode(PeerAcceptMode.Manual);
            form.TickProofForTest(ProofFrom(his.InstanceId), new IPEndPoint(his.Address, 50001));
            Check(!Connected(his.Address), "on manual, a proof must connect nobody");

            SetAcceptMode(PeerAcceptMode.Automatic);
            form.TickProofForTest(ProofFrom(his.InstanceId), new IPEndPoint(his.Address, 50001));
            Check(Connected(his.Address) && !Connected(hers.Address),
                $"THE LISTENER: on automatic, a phone that proves the password - sending nothing - must be ticked back, and it must be THAT phone (sending to: {string.Join(", ", form.SelectedSendEndpointsForTest().Select(e => e.ToString()))}; log: {string.Join(" | ", lines.Where(l => l.Contains("accept", StringComparison.OrdinalIgnoreCase)).TakeLast(4))})");
            Check(Logged("has ticked us and proved our password and is not ticked (mode=Automatic)") && Logged("accept connections: connected to iPhone at 192.168.1.8"),
                "THE LOG: why it was accepted, and that it was, must be logged");

            // Another password: never offered or accepted, and said once.
            var stranger = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 50003);
            form.TickProofForTest(TickProof.Seal(otherKey, Guid.NewGuid(), DateTimeOffset.UtcNow.ToUnixTimeSeconds()), stranger);
            Check(!Connected(stranger.Address) && Logged("203.0.113.9 has ticked us but did not prove our password (not our password)"),
                "THE STRANGER: a proof on another password must never be offered or accepted, and the log must say why");

            // Unticked by the person: it stays unticked however often it proves the password. A peer they ticked and unticked
            // themselves, never asked about - so it is the unticking that holds it, not the ask-once memory.
            var desk = new PeerAnnouncement(Guid.NewGuid(), "Desk", 47830, true, true, DateTime.UtcNow, IPAddress.Parse("192.168.1.30"));
            discovery.RecordForTest(desk);
            form.SelectPeerForTest(desk);
            form.DeselectPeerByUserForTest(desk.InstanceId);
            Check(!Connected(desk.Address), "premise: unticked by the person");
            form.TickProofForTest(ProofFrom(desk.InstanceId), new IPEndPoint(desk.Address, 50006));
            Check(!Connected(desk.Address), "THE UNTICK: a peer the person unticked must stay unticked while its proofs keep coming");

            // Ask: headless, nobody answers - asked once, connected to nobody.
            SetAcceptMode(PeerAcceptMode.Prompt);
            var asker = new PeerAnnouncement(Guid.NewGuid(), "Studio", 47830, true, true, DateTime.UtcNow, IPAddress.Parse("192.168.1.50"));
            discovery.RecordForTest(asker);
            var before = form.AcceptAskCountForTest;
            form.TickProofForTest(ProofFrom(asker.InstanceId), new IPEndPoint(asker.Address, 50004));
            form.TickProofForTest(ProofFrom(asker.InstanceId), new IPEndPoint(asker.Address, 50004));
            Check(!Connected(asker.Address) && form.AcceptAskCountForTest == before + 1,
                $"on ask, a proof must ask once and connect nobody unanswered (asked {form.AcceptAskCountForTest - before} times)");

            // The network side's own brake: several from one address within two seconds reach the window once.
            SetAcceptMode(PeerAcceptMode.Automatic);
            _ = form.Handle;   // the network side hands proofs to the window's thread
            var flood = new IPEndPoint(IPAddress.Parse("192.168.1.60"), 50005);
            var reachedBefore = form.TickProofsReachedWindowForTest;
            for (var i = 0; i < 20; i++) form.TickProofArrivedForTest(ProofFrom(Guid.NewGuid()), flood);
            for (var i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(10); }
            var reached = form.TickProofsReachedWindowForTest - reachedBefore;
            Check(reached == 1, $"THE BRAKE: twenty proofs from one address inside two seconds must reach the window's thread exactly once ({reached})");

            // 5. Sending: to everybody we have ticked - never to a server or anybody behind one.
            var ticked = new PeerAnnouncement(Guid.NewGuid(), "Laptop", 47830, true, true, DateTime.UtcNow, IPAddress.Parse("192.168.1.15"));
            form.SelectPeerForTest(ticked);
            var targets = form.TickProofTargetsForTest();
            Check(targets.Any(t => t.Address.Equals(ticked.Address)), "a peer we have ticked must be sent a proof");
            // A server somebody put in their peer list (a phone reached through it): ticked, and still never sent a proof.
            var relay = new IPEndPoint(IPAddress.Parse("203.0.113.77"), RemPacket.DefaultPort);
            form.SelectPeerForTest(new PeerAnnouncement(Guid.NewGuid(), "203.0.113.77", RemPacket.DefaultPort, true, true, DateTime.UtcNow, relay.Address));
            Check(form.SelectedSendEndpointsForTest().Any(e => e.Address.Equals(relay.Address)), "premise: the server's address is ticked as a peer");
            form.ConnectToRelayForTest("relay.example.test", relay);
            Check(!form.TickProofTargetsForTest().Any(t => t.Address.Equals(relay.Address)), "THE SERVER: a server must never be sent a proof - its member list does that job");
            form.DisconnectFromRelayForTest();
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            SetAcceptMode(restoreMode);
            CuePlayer.GloballyMuted = restoreMuted;
        }

        // 6. Sending for real: a connected window sends its ticked peer a proof it can open, naming this copy.
        var sink = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int windowPort;
        using (var probe = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Any, 0)))
            windowPort = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        MainForm.LocalAudioPortForTest = windowPort;
        MainForm? sending = null;
        try
        {
            var profile = Profile.NewBlank();
            profile.Password = RemSoundCrypto.Obfuscate("tick-proof-password");
            try { sending = new MainForm(null, profile, null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = sending.Handle;
            var sinkPort = ((IPEndPoint)sink.Client.LocalEndPoint!).Port;
            sending.SelectPeerForTest(new PeerAnnouncement(Guid.NewGuid(), "Proof sink", sinkPort, true, true, DateTime.UtcNow, IPAddress.Loopback));
            Require(typeof(MainForm).GetMethod("Connect", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, Type.EmptyTypes),
                "MainForm.Connect not found").Invoke(sending, null);
            // The window's own once-a-second tick, as a running copy does it.
            sending.SnapshotTickForTest();
            sink.Client.ReceiveTimeout = 3000;
            Guid? named = null;
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (named is null && DateTime.UtcNow < deadline)
            {
                IPEndPoint? any = null;
                byte[] got;
                try { got = sink.Receive(ref any); } catch { break; }
                if (RemPacket.TryReadHeader(got, out var t, out _, out _) && t == RemPacketType.TickProof
                    && TickProof.TryUnseal(key, got.AsSpan(RemPacket.HeaderSize), out var id, out _, out _))
                    named = id;
            }
            Check(named == sending.DiscoveryInstanceIdForTest,
                $"THE SENDER: a window must send the peers it has ticked a proof that opens with the password and names this copy (got {named?.ToString() ?? "nothing"})");
        }
        finally
        {
            try { sending?.Dispose(); } catch { /* teardown */ }
            MainForm.LocalAudioPortForTest = 0;
            sink.Dispose();
        }
        return "the proof opened with the password only, named its device and time, and refused a replay, a stale one, a changed one and "
             + "another password; the receiver handed it up untouched and dropped a wrong size; on automatic the phone that proved it - "
             + "with another \"iPhone\" beside it - was ticked back, itself; another password was refused and logged; an unticked peer stayed "
             + "unticked; ask asked once; a flood reached the window once; proofs go to ticked peers, never a server; and a connected window "
             + "really sent one naming itself";
    }
}
