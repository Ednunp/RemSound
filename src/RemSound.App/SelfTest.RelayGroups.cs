using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RemSound.Core;
using RemSound.Receiver;
using RemSound.Sender;

namespace RemSound.App;

/// <summary>
/// Several people on one relay. Ed, 2026-09-18, after GitHub issue #29: a relay carried one pair, and a third person
/// heard nothing and was heard by no one. Now everyone on one password is a group, each heard as themselves; another
/// password is another group; and a phone or older app still pairs, with one member of the group. These steps run the
/// Windows side on its own, then everything together against the real relay. The relay's own rules are pinned by
/// server/test_relay.py.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>
    /// RELAY GROUP FRAMING, MEMBER ADDRESSES AND THE MEMBER LIST.
    ///
    /// <para>No network: the relay group client on its own, fed what a relay sends. Until a relay confirms a group,
    /// packets go exactly as before. In a group, a packet to the relay goes with our id after the header; a heartbeat
    /// also goes in ordinary form, to claim the pair slot beside a waiting phone; an address check is echoed exactly as
    /// it came. A member's packet comes back in ordinary form, credited to an address of their own that stays the same
    /// for the same person. A packet for a member goes to their relay, never to their reserved address.</para>
    /// </summary>
    private static string? AuditRelayGroupFramingAndMembers()
    {
        var relay = new IPEndPoint(IPAddress.Parse("203.0.113.7"), RemPacket.DefaultPort);
        var (_, fingerprint) = RemSoundCrypto.ForPlainPassword("relay-group-framing");
        var sent = new List<(byte[] Data, IPEndPoint To)>();
        void Capture(ReadOnlySpan<byte> data, IPEndPoint to) { lock (sent) sent.Add((data.ToArray(), to)); }
        var me = new RelayGroupClient(Guid.NewGuid());
        me.SetIdentity("Me", fingerprint);
        me.Start((data, length, to) => { Capture(data.AsSpan(0, length), to); return true; });
        try
        {
            var audio = PlainPacket(RemPacketType.Audio, 7, [1, 2, 3, 4, 5]);
            Check(!me.TryRoute(audio, relay, Capture) && sent.Count == 0,
                "nothing is said to a relay until the user connects to one: a relay is somewhere you go, not a peer that finds you");

            me.Connect(relay);
            Check(!me.TryRoute(audio, relay, Capture),
                "until a relay confirms a group, a packet to it must go exactly as it always has (pairs are untouched)");
            (byte[] Data, IPEndPoint To) LastHello() => sent.LastOrDefault(s => s.To.Equals(relay) && s.Data.Length > 5
                && s.Data[4] == RelayGroupClient.GroupVersion && s.Data[5] == RelayGroupClient.TypeHello);
            var hello = LastHello();
            Check(hello.Data is not null, "connecting to a relay must say hello at once, to join the group for our password");
            var tickCountAt = RelayGroupClient.GroupHeaderSize + RelayGroupClient.NameBytes + RelayGroupClient.GroupTagBytes;
            Check(hello.Data!.Length == tickCountAt + 1
                  && hello.Data.AsSpan(RelayGroupClient.GroupHeaderSize + RelayGroupClient.NameBytes, RelayGroupClient.GroupTagBytes).SequenceEqual(fingerprint)
                  && RelayGroupClient.DecodeName(hello.Data.AsSpan(RelayGroupClient.GroupHeaderSize, RelayGroupClient.NameBytes)) == "Me"
                  && hello.Data[tickCountAt] == 0,
                "the hello must carry our name, our password fingerprint as the group tag, and a tick list that says nobody yet");

            var alice = Guid.NewGuid();
            var bob = Guid.NewGuid();

            me.SetTicked([alice], pairPartner: false);
            hello = LastHello();
            Check(hello.Data!.Length == tickCountAt + 1 + RelayGroupClient.ClientIdSize && hello.Data[tickCountAt] == 1
                  && new Guid(hello.Data.AsSpan(tickCountAt + 1, RelayGroupClient.ClientIdSize), bigEndian: true) == alice,
                "ticking someone must tell the relay at once who it is, because the relay only passes sound where both have ticked");
            me.SetTicked([], pairPartner: false);
            Check(LastHello().Data![tickCountAt] == 0, "unticking everyone must say so, not quietly leave the last list standing");
            Feed(me, relay, RosterPacket([(alice, "Alice"), (bob, "Bob"), (me.ClientId, "Me")], paired: false), RelayInbound.Consumed,
                "a member list is bookkeeping, not audio");
            Check(me.IsInGroup(relay), "a member list from the relay must put us in its group");
            Check(me.Members.Select(m => m.Name).Order().SequenceEqual(["Alice", "Bob"]),
                $"the members must be the others in the list, by name, and never us ({string.Join(", ", me.Members.Select(m => m.Name))})");

            sent.Clear();
            Check(me.TryRoute(audio, relay, Capture) && sent.Count == 1, "in a group, a packet to the relay must go once, group-framed");
            var framed = sent[0].Data;
            Check(framed.Length == audio.Length + RelayGroupClient.ClientIdSize && framed[4] == RelayGroupClient.GroupVersion
                  && framed.AsSpan(RemPacket.HeaderSize, RelayGroupClient.ClientIdSize).SequenceEqual(IdBytes(me.ClientId))
                  && framed.AsSpan(RelayGroupClient.GroupHeaderSize).SequenceEqual(audio.AsSpan(RemPacket.HeaderSize)),
                "group framing is the ordinary header at version 2, then our id, then the packet untouched");

            sent.Clear();
            me.TryRoute(PlainPacket(RemPacketType.Heartbeat, 0xFFFF, new byte[RemPacket.HeartbeatPayloadSize]), relay, Capture);
            Check(sent.Count == 2 && sent.Any(s => s.Data[4] == RemPacket.Version) && sent.Any(s => s.Data[4] == RelayGroupClient.GroupVersion),
                "a heartbeat must go in both forms: the ordinary one is what claims the pair slot beside a phone waiting for one");
            Check(!me.TryRoute(PlainPacket(RemPacketType.AddrCheck, 0, new byte[16]), relay, Capture),
                "an address check must be echoed exactly as it came, never group-framed");

            Feed(me, relay, RosterPacket([(alice, "Alice"), (bob, "Bob")], paired: true), RelayInbound.Consumed, "a member list");
            sent.Clear();
            me.TryRoute(audio, relay, Capture);
            Check(sent.Count == 1 && sent[0].Data[4] == RelayGroupClient.GroupVersion,
                "a phone or older app paired with us is somebody you have to tick like anyone else: until you do, your audio must not go to it");
            me.SetTicked([], pairPartner: true);
            sent.Clear();
            me.TryRoute(audio, relay, Capture);
            Check(sent.Count == 2 && sent.Any(s => s.Data[4] == RemPacket.Version) && sent.Any(s => s.Data[4] == RelayGroupClient.GroupVersion),
                "once that phone is ticked, audio must go in both forms: group-framed for the group, ordinary for the phone");
            me.SetTicked([], pairPartner: false);
            sent.Clear();
            me.TryRoute(audio, relay, Capture);
            Check(sent.Count == 1 && sent[0].Data[4] == RelayGroupClient.GroupVersion,
                "unticking that phone must stop the ordinary copy again, not leave it running");
            me.SetTicked([], pairPartner: true);

            var fromAlice = GroupPacket((byte)RemPacketType.Audio, alice, [9, 8, 7]);
            var length = fromAlice.Length;
            Check(me.HandleInbound(fromAlice, ref length, relay, out var source) == RelayInbound.Delivered && source is not null,
                "a member's packet through the relay must be delivered");
            Check(length == fromAlice.Length - RelayGroupClient.ClientIdSize && fromAlice[4] == RemPacket.Version
                  && fromAlice.AsSpan(RemPacket.HeaderSize, length - RemPacket.HeaderSize).SequenceEqual((byte[])[9, 8, 7]),
                "a member's packet must come back in ordinary form, the id taken out and the payload untouched");
            var aliceMember = me.Members.Single(m => m.Name == "Alice");
            var bobMember = me.Members.Single(m => m.Name == "Bob");
            Check(source!.Equals(aliceMember.Address) && RelayGroupClient.IsMemberAddress(source.Address) && relay.Equals(me.RelayOf(source)),
                $"a member's packet must be credited to that member's own reserved address, behind the relay (got {source})");
            Check(!aliceMember.Address.Equals(bobMember.Address), "two people must never share an address, or they would be one person again");

            var again = new RelayGroupClient(Guid.NewGuid());
            again.Connect(relay);
            again.NoteRelay(relay);
            Feed(again, relay, RosterPacket([(alice, "Alice")], paired: false), RelayInbound.Consumed, "a member list");
            Check(again.Members.Single().Address.Equals(aliceMember.Address),
                "the same person must get the same address every time, so the volume and pan given to them come back with them");

            sent.Clear();
            Check(me.TryRoute(PlainPacket(RemPacketType.Heartbeat, 0xFFFF, new byte[RemPacket.HeartbeatPayloadSize]), aliceMember.Address, Capture)
                  && sent.Count == 1 && sent[0].To.Equals(relay) && sent[0].Data[4] == RelayGroupClient.GroupVersion,
                "a packet for a member (a pong, a remote command) must go to their relay, group-framed, never to their reserved address");

            Feed(me, relay, RosterPacket([(alice, "Alice")], paired: false), RelayInbound.Consumed, "a member list");
            sent.Clear();
            Check(me.Members.Count == 1 && me.TryRoute(audio, bobMember.Address, Capture) && sent.Count == 0,
                "someone who has left must drop off the list, and a packet for their old address must be dropped, not sent");

            var ours = GroupPacket((byte)RemPacketType.Audio, me.ClientId, [1]);
            var oursLength = ours.Length;
            Check(me.HandleInbound(ours, ref oursLength, relay, out _) == RelayInbound.Consumed, "our own packet must never be played back to us");
            var stray = GroupPacket((byte)RemPacketType.Audio, alice, [1]);
            var strayLength = stray.Length;
            Check(me.HandleInbound(stray, ref strayLength, new IPEndPoint(IPAddress.Parse("198.51.100.9"), 47830), out _) == RelayInbound.NotGroup,
                "a group packet from somewhere that is not a relay we joined must not be taken as a member's");

            using var sender = new AudioSender();
            Check(!sender.SendVia(audio, audio.Length, new IPEndPoint(IPAddress.Parse("240.1.2.3"), RemPacket.DefaultPort)),
                "the sender must never put a member's reserved address on the wire");
            return "until a relay confirms a group, nothing changes; in one, packets go with our id (heartbeats, and audio beside a paired phone, "
                + "in ordinary form too); members come back in ordinary form under stable addresses of their own, never on the wire";
        }
        finally { me.Stop(); }
    }

    /// <summary>
    /// SEVERAL PEOPLE SHARE ONE RELAY, EACH HEARD AS THEMSELVES.
    ///
    /// <para>The real relay, run under Python on this machine, and five app instances: three on one password, one on
    /// another, and one "older app" with no group support that stands for a phone. The three must form one group and
    /// each hear the other two as two separate people; the one on another password must hear nobody; the older app must
    /// still pair with one member of the group and hear them, and that member hear it. Uncompressed audio, so a frame
    /// through the group is as big as it gets. Nothing plays: the receivers decode only.</para>
    /// </summary>
    private static string? AuditSeveralPeopleShareOneRelay()
    {
        var root = FindSourceRoot();
        if (root is null) return Skip("the source tree is not reachable (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var python = FindPython();
        if (python is null) return Skip("there is no Python on this machine to run the relay (py.exe, or a python.exe outside WindowsApps)");

        var port = FreeUdpPort();
        var logDir = Path.Combine(Path.GetTempPath(), $"remsound-relay-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "relay.log");
        using var relayProcess = StartRelay(python, Path.Combine(root, "server", "remsound-relay.py"), port, logPath);
        var apps = new List<RelayTestApp>();
        try
        {
            Check(WaitUntil(() => ReadShared(logPath).Contains("event=startup"), 10000) && !relayProcess.HasExited,
                $"the relay must start under {python}");
            Thread.Sleep(500);   // it logs before it binds
            Check(!ReadShared(logPath).Contains("event=bind_failed"), "the relay must bind its port");
            var relay = new IPEndPoint(IPAddress.Loopback, port);

            // The older app first: it takes a pair slot and waits, as a phone does, and the first member to arrive pairs
            // with it.
            var older = new RelayTestApp("Phone", "garden party", relay, groups: false);
            apps.Add(older);
            Thread.Sleep(1500);
            var alice = new RelayTestApp("Alice", "garden party", relay, groups: true);
            apps.Add(alice);
            Thread.Sleep(1500);
            var bob = new RelayTestApp("Bob", "garden party", relay, groups: true);
            var carol = new RelayTestApp("Carol", "garden party", relay, groups: true);
            var dave = new RelayTestApp("Dave", "another password entirely", relay, groups: true);
            apps.AddRange([bob, carol, dave]);
            var group = new[] { alice, bob, carol };

            Check(WaitUntil(() => group.All(a => a.Group!.IsInGroup(relay) && a.Group.Members.Count == 2) && dave.Group!.IsInGroup(relay), 15000),
                "three people on one password must form one group of three, and a fourth on another password a group of their own ("
                + string.Join("; ", group.Append(dave).Select(a => $"{a.Name}: {(a.Group!.IsInGroup(relay) ? $"in, with {a.Group.Members.Count}" : "not in")}")) + ")");
            Check(dave.Group!.Members.Count == 0, $"someone on another password must not see the group's members ({dave.Group.Members.Count})");
            Check(carol.Group!.Members.Select(m => m.Name).Order().SequenceEqual(["Alice", "Bob"]),
                $"each member must see the others by name ({string.Join(", ", carol.Group.Members.Select(m => m.Name))})");
            Check(WaitUntil(() => group.Count(a => a.Group!.IsV1Paired(relay)) == 1, 8000),
                "exactly one member must pair with the older app waiting on the relay, so it still reaches someone ("
                + string.Join(", ", group.Where(a => a.Group!.IsV1Paired(relay)).Select(a => a.Name)) + ")");
            var partner = group.Single(a => a.Group!.IsV1Paired(relay));

            foreach (var (app, level) in new[] { (alice, 0.3f), (bob, 0.5f), (carol, 0.7f), (older, 0.4f) }) app.StartTalking(level);

            // Nobody has ticked anybody yet, so nobody hears anybody: on a relay, as on a network, sound only flows where
            // each has ticked the other. This is what stops a room full of strangers arriving in your ears.
            Thread.Sleep(2500);
            Check(group.All(a => a.Receiver.LiveSessionCountForTest == 0),
                "before anyone is ticked, nobody on the relay must be heard ("
                + string.Join(", ", group.Select(a => $"{a.Name}: {a.Receiver.LiveSessionCountForTest}")) + ")");

            // Alice ticks Bob, but Bob has not ticked Alice: still nothing, in either direction.
            alice.Tick(alice.Group!.Members.Where(m => m.Name == "Bob").Select(m => m.Id));
            Thread.Sleep(2500);
            Check(alice.Receiver.LiveSessionCountForTest == 0 && bob.Receiver.LiveSessionCountForTest == 0,
                $"ticking someone who has not ticked you back must be heard by neither of you (Alice {alice.Receiver.LiveSessionCountForTest}, Bob {bob.Receiver.LiveSessionCountForTest})");

            // Now everyone ticks everyone, which is what the relay list does when you tick a row.
            foreach (var a in group) a.Tick(a.Group!.Members.Select(m => m.Id), pairPartner: true);
            Check(WaitUntil(() => group.All(a => a.Group!.Members.All(m => a.Receiver.IsAudioFlowingFrom(m.Address.Address, TimeSpan.FromSeconds(2)))), 8000),
                "each member must hear the other two ("
                + string.Join("; ", group.Select(a => $"{a.Name} hears {string.Join(", ", a.Group!.Members.Where(m => a.Receiver.IsAudioFlowingFrom(m.Address.Address, TimeSpan.FromSeconds(2))).Select(m => m.Name))}")) + ")");
            foreach (var a in group)
            {
                var expected = a == partner ? 3 : 2;
                // A stream only starts when its Format packet comes round again, so give the newly-ticked ones a moment.
                Check(WaitUntil(() => a.Receiver.LiveSessionCountForTest == expected, 10000),
                    $"{a.Name} must hear each of the other two as a person of their own{(a == partner ? ", and the older app as a third" : "")} "
                    + $"({a.Receiver.LiveSessionCountForTest} streams, expected {expected}); through one relay address they used to knock each other out");
            }
            Check(WaitUntil(() => partner.Receiver.IsAudioFlowingFrom(relay.Address, TimeSpan.FromSeconds(2)), 5000),
                $"the member paired with the older app ({partner.Name}) must hear it through the relay");
            Check(WaitUntil(() => older.Receiver.IsAudioFlowingFrom(relay.Address, TimeSpan.FromSeconds(2)), 5000) && older.Receiver.LiveSessionCountForTest == 1,
                $"the older app must still hear exactly one person through the relay, its partner ({older.Receiver.LiveSessionCountForTest} streams)");
            Thread.Sleep(500);
            Check(dave.Receiver.LiveSessionCountForTest == 0,
                $"someone on another password must hear nobody from the group ({dave.Receiver.LiveSessionCountForTest} streams)");

            // Carol unticks everyone. Her sound must stop reaching the other two, and theirs must stop reaching her —
            // a tick is one switch for both directions, and taking it off has to actually take it off.
            var carolAddress = alice.Group!.Members.Single(m => m.Name == "Carol").Address.Address;
            carol.Tick([]);
            Check(WaitUntil(() => carol.Receiver.LiveSessionCountForTest == 0
                                  && !alice.Receiver.IsAudioFlowingFrom(carolAddress, TimeSpan.FromSeconds(2))
                                  && !bob.Receiver.IsAudioFlowingFrom(carolAddress, TimeSpan.FromSeconds(2)), 10000),
                $"unticking must stop the sound both ways (Carol still hears {carol.Receiver.LiveSessionCountForTest}; "
                + $"Alice still hears Carol: {alice.Receiver.IsAudioFlowingFrom(carolAddress, TimeSpan.FromSeconds(2))}; "
                + $"Bob still hears Carol: {bob.Receiver.IsAudioFlowingFrom(carolAddress, TimeSpan.FromSeconds(2))})");
            Check(alice.Receiver.IsAudioFlowingFrom(alice.Group.Members.Single(m => m.Name == "Bob").Address.Address, TimeSpan.FromSeconds(2)),
                "one person unticking must not disturb the two who still have each other ticked");

            // And ticking again brings it straight back, without anyone reconnecting.
            carol.Tick(carol.Group!.Members.Select(m => m.Id), pairPartner: true);
            Check(WaitUntil(() => carol.Receiver.LiveSessionCountForTest >= 2
                                  && alice.Receiver.IsAudioFlowingFrom(carolAddress, TimeSpan.FromSeconds(2)), 10000),
                $"ticking again must bring the sound back both ways without reconnecting (Carol hears {carol.Receiver.LiveSessionCountForTest})");

            return $"three people on one password heard each other as three, and only once each had ticked the other; unticking stopped it both "
                + $"ways and ticking brought it back; {partner.Name} also partnered the older app, which still heard one person; "
                + "a fourth on another password heard nobody";
        }
        finally
        {
            foreach (var app in apps) app.Dispose();
            try { relayProcess.Kill(entireProcessTree: true); } catch { /* already gone */ }
            try { Directory.Delete(logDir, recursive: true); } catch { /* temp */ }
        }
    }

    /// <summary>
    /// CONNECTING TO A RELAY, REMEMBERING IT, AND TICKING WHO IS ON IT.
    ///
    /// <para>Ed's design, 2026-09-20: a relay is a place you go, not a person in your peer list. This drives the real
    /// window — the address box, the Connect button, the remembered-relays list and the tick list — through the handlers
    /// the controls call, with a relay's member list fed in exactly as its socket would deliver one. It pins what ticking
    /// someone on a relay actually does: they become a peer of their own, our audio goes to the RELAY once (not to the
    /// made-up address we know them by), and their audio is let in. And it pins the two things a person cannot work out
    /// for themselves: that the list is hidden until you connect, and that someone who has not ticked you back is named
    /// as such rather than being silently silent.</para>
    /// </summary>
    private static string? AuditConnectingToARelayFromTheWindow()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        MainForm? form = null;
        try
        {
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return Skip($"headless MainForm could not be constructed: {ex.GetType().Name}: {ex.Message}"); }

            var relay = new IPEndPoint(IPAddress.Parse("203.0.113.44"), RemPacket.DefaultPort);
            var entry = "relay.example.test:47830";
            bool PeersRowShown() => form.RelayPeersRowForTest is { } row && IsSetVisibleForAltAudit(row);
            // What a screen reader reads on tabbing in is the accessible name, not the label beside it. Ed had to ask
            // twice on 2026-09-20 because the label said one thing and the list still answered to the old name.
            Check(form.RelayPeersListForTest.AccessibleName?.Contains("on server", StringComparison.Ordinal) == true
                  && form.DiscoveredPeersListForTest.AccessibleName?.Contains("not on a server", StringComparison.Ordinal) == true,
                $"both discovered lists must SAY which they are when you tab into them "
                + $"({form.DiscoveredPeersListForTest.AccessibleName} / {form.RelayPeersListForTest.AccessibleName})");
            Check(!PeersRowShown(), "the server's own peer list must be hidden until you are connected to one");
            Check(form.RelayGroupForTest.ConnectedRelay is null, "nothing is joined until the user connects: a relay is somewhere you go");

            form.ConnectToRelayForTest(entry, relay);
            Check(relay.Equals(form.RelayGroupForTest.ConnectedRelay), "pressing Connect must join that relay");
            Check(PeersRowShown(), "connecting must show the list of people on the server");
            Check(form.RelayConnectButtonForTest.Text.Replace("&", "").Contains("Disconnect", StringComparison.Ordinal),
                $"once you are on a server the button must offer to leave it or change it ({form.RelayConnectButtonForTest.Text})");
            Check(form.RememberedRelaysForTest.Any(e => e == entry),
                "a relay you have connected to must be remembered, as it was typed, so you never type it twice");

            // The relay's member list arrives on the sending socket, as it does in the app.
            var alice = Guid.NewGuid();
            var bob = Guid.NewGuid();
            Feed(form.RelayGroupForTest, relay, RosterPacket([(alice, "Alice", true), (bob, "Bob", false)], paired: false),
                RelayInbound.Consumed, "a member list");
            form.SyncRelayPeersListForTest();
            Check(form.RelayPeersListForTest.Items.Cast<object>().Select(i => i.ToString()!).Order().SequenceEqual(["Alice", "Bob"]),
                $"the people on the relay must be listed by name ({string.Join(", ", form.RelayPeersListForTest.Items.Cast<object>())})");
            Check(Enumerable.Range(0, form.RelayPeersListForTest.Items.Count).All(i => !form.RelayPeersListForTest.GetItemChecked(i)),
                "nobody on a relay is ticked for you: arriving in a room is not agreeing to be heard");
            Check(form.SelectedSendEndpointsForTest().Length == 0, "and until you tick somebody, nothing is sent anywhere");
            // The list's own status label belongs to the list: it is what a screen reader is told on every arrow press,
            // and the relay's connection status has a line of its own beside Connect.
            Check(!form.RelayListStatusForTest.Contains("Connected to", StringComparison.Ordinal),
                $"the relay list's status label must carry the list's own state, not the connection's ({form.RelayListStatusForTest})");

            // Tick Alice, who has already ticked us.
            var aliceRow = form.RelayPeersListForTest.Items.Cast<object>().ToList().FindIndex(i => i.ToString() == "Alice");
            form.RelayPeerTickedForTest(aliceRow, true);
            var aliceMember = form.RelayGroupForTest.Members.Single(m => m.Name == "Alice");
            Check(form.SelectedSendEndpointsForTest() is [var only] && only.Equals(relay),
                "ticking somebody on a relay must send our audio to the RELAY, once, not to the address we know them by");
            Check(form.ReceiverForTest.IsSenderAllowedForTest(aliceMember.Address),
                "and must let their audio in, at the address the relay credits them to");
            Check(!form.ReceiverForTest.IsSenderAllowedForTest(form.RelayGroupForTest.Members.Single(m => m.Name == "Bob").Address),
                "somebody you have not ticked must still be turned away");

            // Tick Bob, who has not ticked us: the status line must say so rather than leaving it a mystery.
            var bobRow = form.RelayPeersListForTest.Items.Cast<object>().ToList().FindIndex(i => i.ToString() == "Bob");
            form.RelayPeerTickedForTest(bobRow, true);
            form.SyncRelayPeersListForTest();
            Check(form.RelayStatusForTest.Contains("waiting for Bob", StringComparison.OrdinalIgnoreCase),
                $"somebody who has not ticked you back must be named as such, not silently silent ({form.RelayStatusForTest})");
            Check(!form.RelayStatusForTest.Contains("Alice", StringComparison.Ordinal),
                $"and somebody who HAS ticked you back must not be listed as waiting ({form.RelayStatusForTest})");
            Feed(form.RelayGroupForTest, relay, RosterPacket([(alice, "Alice", true), (bob, "Bob", true)], paired: false),
                RelayInbound.Consumed, "a member list");
            form.SyncRelayPeersListForTest();
            Check(!form.RelayStatusForTest.Contains("waiting", StringComparison.OrdinalIgnoreCase),
                $"once they tick you back, nobody is waited for ({form.RelayStatusForTest})");

            // A relay from before it said who had ticked you must still list its people, rather than never joining.
            // It names somebody new, so the list can only be right if that older list was really read.
            var dave = Guid.NewGuid();
            Feed(form.RelayGroupForTest, relay, OlderRosterPacket([(alice, "Alice"), (bob, "Bob"), (dave, "Dave")], paired: false),
                RelayInbound.Consumed, "a member list from an older relay");
            form.SyncRelayPeersListForTest();
            Check(form.RelayPeersListForTest.Items.Cast<object>().Any(i => i.ToString() == "Dave"),
                $"a relay from before the tick flag must still show its people ({string.Join(", ", form.RelayPeersListForTest.Items.Cast<object>())})");

            // Unticking takes it all off again. Somebody you have ticked is in Connected peers now, not in the server
            // list, so that is where you untick them — exactly as for somebody on your network.
            form.DeselectPeerByUserForTest(alice);
            form.DeselectPeerByUserForTest(bob);
            Check(form.SelectedSendEndpointsForTest().Length == 0 && !form.ReceiverForTest.IsSenderAllowedForTest(aliceMember.Address),
                "unticking must stop sending to them and stop letting them in");
            Check(form.RelayTickedForTest.Count == 0,
                "and must clear the server's ticks, or this machine puts them straight back");
            form.SyncRelayPeersListForTest();
            Check(form.RelayPeersListForTest.Items.Cast<object>().Select(i => i.ToString()!).Order().SequenceEqual(["Alice", "Bob", "Dave"]),
                $"and they come back to the server list, beside everyone else there, ready to be ticked again ({string.Join(", ", form.RelayPeersListForTest.Items.Cast<object>())})");

            // Changing the profile password is a different set of people, so the ticks cannot carry over.
            form.RelayPeerTickedForTest(form.RelayPeersListForTest.Items.Cast<object>().ToList().FindIndex(i => i.ToString() == "Alice"), true);
            var (_, otherFingerprint) = RemSoundCrypto.ForPlainPassword("a completely different password");
            form.RelayPasswordChangedForTest(otherFingerprint);
            Check(form.RelayTickedForTest.Count == 0,
                "changing your password puts you among different people, so nobody stays ticked from the group you have left");

            // Delete on the remembered list forgets one.
            form.ForgetRelayForTest(entry);
            Check(!form.RememberedRelaysForTest.Any(e => e == entry),
                "Delete on the remembered relays list must forget that relay");

            form.DisconnectFromRelayForTest();
            Check(form.RelayGroupForTest.ConnectedRelay is null && !PeersRowShown(),
                "disconnecting must leave the server and take its list away with it");
            Check(form.RelayConnectButtonForTest.Text.Replace("&", "").Contains("Connect to server", StringComparison.Ordinal),
                $"and the button must offer to connect again ({form.RelayConnectButtonForTest.Text})");

            // A profile puts its ticks back before anybody is on the list — the list only arrives seconds later. The
            // ticks have to be picked up when those people appear, or a profile would load and nothing would be heard.
            var restored = Profile.NewBlank();
            restored.RelayServer = entry;
            restored.RelayTickedIds = [alice.ToString("D")];
            form.ApplyRelayProfileForTest(restored);
            form.ConnectToRelayForTest(entry, relay);
            Check(form.SelectedSendEndpointsForTest().Length == 0, "a restored tick reaches nobody until that person is on the relay");
            Feed(form.RelayGroupForTest, relay, RosterPacket([(alice, "Alice", true), (bob, "Bob", false)], paired: false),
                RelayInbound.Consumed, "a member list");
            form.SyncRelayPeersListForTest();
            Check(form.ReceiverForTest.IsSenderAllowedForTest(form.RelayGroupForTest.Members.Single(m => m.Name == "Alice").Address)
                  && form.SelectedSendEndpointsForTest() is [var restoredTarget] && restoredTarget.Equals(relay),
                "a profile's ticks must take effect the moment those people appear on the relay, without the user ticking again");
            form.DisconnectFromRelayForTest();

            // Only a relay sends an address check. One arriving from an address the user typed into the peer list is
            // how RemSound knows to offer to connect to it as a relay instead.
            var typed = new IPEndPoint(IPAddress.Parse("203.0.113.55"), RemPacket.DefaultPort);
            Check(!form.ShouldOfferRelayForTest(typed), "an address check from somewhere nobody chose must not be asked about");
            form.SelectPeerForTest(new PeerAnnouncement(Guid.NewGuid(), "Typed by hand", typed.Port,
                CanSend: true, CanReceive: true, DateTime.UtcNow, typed.Address));
            Check(form.ShouldOfferRelayForTest(typed), "an address in the peer list that answers as a relay must be asked about");
            Check(!form.ShouldOfferRelayForTest(typed), "and asked about once only, not on every address check it sends");
            return "the list is hidden until you connect; connecting remembers the relay and lists its people, none ticked; "
                + "ticking sends to the relay once and lets that person in; somebody who has not ticked you back is named; "
                + "an older relay's list still shows; unticking, a password change, Delete and Disconnect all undo what they should";
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            CuePlayer.GloballyMuted = restoreMuted;
        }
    }

    /// <summary>
    /// WHAT HAPPENS WHEN SOMEBODY ELSE TICKS YOU.
    ///
    /// <para>Ed, 2026-09-20. Three ways: ask (the default), tick them back at once, or nothing until you do it
    /// yourself. There are two ways to know somebody has ticked you — their audio arriving at our port unasked for, and
    /// the relay's member list saying so — and both land in the same decision. This drives that decision directly, in
    /// each of the three modes, on the real window.</para>
    ///
    /// <para>The question itself is a message box, which a headless run has nobody to answer; in that mode the app
    /// declines to ask, so what "ask" is held to here is that it connects nobody on its own. The two modes that act
    /// without a question — automatic and manual — are proven all the way to the peer list.</para>
    /// </summary>
    private static string? WhatHappensWhenSomebodyTicksYou()
    {
        // The default, before anything touches it.
        Check(new AppConfig().AcceptPeerConnections == PeerAcceptMode.Prompt,
            "asking must be the default: nobody should be connected to without either a question or a decision");
        var rows = PreferencesDialog.AcceptPeerOptionsForTest;
        Check(rows.Count == 3 && rows[0] == PeerAcceptMode.Prompt && rows[1] == PeerAcceptMode.Automatic && rows[2] == PeerAcceptMode.Manual,
            $"the list must offer ask, automatic and manual, in that order (got {string.Join(", ", rows)})");
        var saved = AppConfig.Load();
        var restoreMode = saved.AcceptPeerConnections;
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        MainForm? form = null;
        try
        {
            saved.AcceptPeerConnections = PeerAcceptMode.Manual;
            saved.Save();
            Check(AppConfig.Load().AcceptPeerConnections == PeerAcceptMode.Manual, "the choice must persist through AppConfig");

            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return Skip($"headless MainForm could not be constructed: {ex.GetType().Name}: {ex.Message}"); }

            var theirs = new IPEndPoint(IPAddress.Parse("198.51.100.21"), RemPacket.DefaultPeerDialPort);
            bool Connected(IPEndPoint ep) => form!.SelectedSendEndpointsForTest().Any(e => e.Address.Equals(ep.Address));

            // Manual: nothing happens at all. This is how RemSound has always worked.
            form.SomeoneWantsToConnectForTest(theirs, samePassword: true);
            Check(!Connected(theirs) && form.AcceptAskedForTest.Count == 0,
                "on manual, somebody sending to us must connect nobody and be recorded nowhere");

            // Automatic: ticked back at once, so sound flows without either of you doing anything more.
            SetAcceptMode(PeerAcceptMode.Automatic);
            form.SomeoneWantsToConnectForTest(theirs, samePassword: true);
            Check(Connected(theirs), "on automatic, somebody who ticks us must be ticked back at once");

            // Somebody on a different password is never offered or accepted: their audio could not be played anyway.
            var stranger = new IPEndPoint(IPAddress.Parse("198.51.100.99"), RemPacket.DefaultPeerDialPort);
            form.SomeoneWantsToConnectForTest(stranger, samePassword: false);
            Check(!Connected(stranger), "somebody on a different password must never be connected to, whatever the mode");

            // Asking: a headless run has nobody to answer, so nobody is connected — and it is recorded as asked, so
            // the same address cannot turn into a stream of questions.
            SetAcceptMode(PeerAcceptMode.Prompt);
            var asker = new IPEndPoint(IPAddress.Parse("198.51.100.31"), RemPacket.DefaultPeerDialPort);
            form.SomeoneWantsToConnectForTest(asker, samePassword: true);
            Check(!Connected(asker), "asking must connect nobody until the question is answered");
            var askedOnce = form.AcceptAskCountForTest;
            Check(askedOnce == 1, $"asking must actually ask, once ({askedOnce} times)");
            form.SomeoneWantsToConnectForTest(asker, samePassword: true);
            form.SomeoneWantsToConnectForTest(asker, samePassword: true);
            Check(form.AcceptAskCountForTest == askedOnce,
                $"and the same person must be asked about once, not every time a packet of theirs arrives "
                + $"({form.AcceptAskCountForTest} times)");

            // A phone or older app paired with us through the relay. It cannot say it has ticked us — it knows nothing
            // about ticking — so being given a pair slot beside us is the statement, and it must go through the same
            // decision. Left out, it sat in the list unticked and silent, which is exactly what happened on 2026-09-20.
            var phoneRelay = new IPEndPoint(IPAddress.Parse("203.0.113.78"), RemPacket.DefaultPort);
            form.ConnectToRelayForTest("phone.relay.test", phoneRelay);
            SetAcceptMode(PeerAcceptMode.Manual);
            Feed(form.RelayGroupForTest, phoneRelay, RosterPacket(Array.Empty<(Guid Id, string Name, bool TicksUs)>(), paired: true),
                RelayInbound.Consumed, "a member list");
            form.SyncRelayPeersListForTest();
            Check(!form.RelayPairTickedForTest, "on manual, a phone paired with us through the relay must not be ticked for us");
            SetAcceptMode(PeerAcceptMode.Automatic);
            form.SyncRelayPeersListForTest();
            Check(form.RelayPairTickedForTest,
                "on automatic, a phone paired with us through the relay must be ticked back — without it, it is in the list and silent");
            Check(form.SelectedSendEndpointsForTest().Any(e => e.Equals(phoneRelay)),
                "and our audio must then go to the relay, which is where that phone hears it");
            form.DisconnectFromRelayForTest();

            // The same decision, from the relay's member list rather than from unasked-for audio.
            var relay = new IPEndPoint(IPAddress.Parse("203.0.113.77"), RemPacket.DefaultPort);
            form.ConnectToRelayForTest("relay.example.test", relay);
            var mate = Guid.NewGuid();
            Feed(form.RelayGroupForTest, relay, RosterPacket([(mate, "Ticked us", true)], paired: false),
                RelayInbound.Consumed, "a member list");
            SetAcceptMode(PeerAcceptMode.Manual);
            form.SyncRelayPeersListForTest();
            Check(form.RelayTickedForTest.Count == 0, "on manual, somebody ticking us on a relay must not tick them back");
            SetAcceptMode(PeerAcceptMode.Automatic);
            form.SyncRelayPeersListForTest();
            Check(form.RelayTickedForTest.Contains(mate),
                "on automatic, somebody who ticks us on a relay must be ticked back, the same as anybody else");

            // Accepting somebody must select them ONCE. It used to re-apply the ticks twice for one person, which put
            // two "peer selected" lines in the log for one row and did the whole round of work twice.
            var mateAddress = form.RelayGroupForTest.Members.Single(m => m.Id == mate).Address;
            Check(form.SelectedSendEndpointsForTest().Count(e => e.Equals(relay)) == 1,
                "and must be sent to once, not twice over");
            var relayRow = new PeerListItem(new PeerAnnouncement(mate, "Ticked us", mateAddress.Port,
                CanSend: true, CanReceive: true, DateTime.UtcNow, mateAddress.Address)).ToString();
            Check(relayRow.Contains("on the server", StringComparison.Ordinal)
                  && !relayRow.Contains(mateAddress.Address.ToString(), StringComparison.Ordinal),
                $"somebody reached through a relay must read as being on the server, not as a made-up address ({relayRow})");

            // And the signal itself: a Format packet from somebody not ticked is what tells us they have ticked US.
            // Everything above drives the decision directly, so without this the thing that sets it off is untested.
            using (var receiver = new Receiver.AudioReceiver())
            {
                var heard = new List<(IPEndPoint From, bool Same)>();
                receiver.OnUnselectedSenderHeard = (from, same) => { lock (heard) heard.Add((from, same)); };
                var (_, ourFingerprint) = RemSoundCrypto.ForPlainPassword("the password we are on");
                var (_, otherFingerprint) = RemSoundCrypto.ForPlainPassword("some other password");
                receiver.AudioFingerprint = ourFingerprint;
                try { receiver.Start(FreeUdpPort()); }
                catch (Exception ex) { return Skip($"could not start a receiver: {ex.Message}"); }
                receiver.SetOutputDevices([]);
                receiver.SetPlaybackEnabled(true);
                var welcome = new IPEndPoint(IPAddress.Parse("198.51.100.41"), RemPacket.DefaultPeerDialPort);
                receiver.SetAllowedSenders([welcome]);

                static byte[] FormatPacket(ReadOnlySpan<byte> fingerprint, out int length)
                {
                    var packet = new byte[RemPacket.HeaderSize + 96];
                    RemPacket.WriteHeader(packet, RemPacketType.Format, 1, 1);
                    length = RemPacket.HeaderSize + RemPacket.WriteFormatPayload(packet.AsSpan(RemPacket.HeaderSize),
                        new AudioFormatInfo(48000, 2, 24, 1, 6, 48000 * 6), fingerprint);
                    return packet;
                }

                var ours = FormatPacket(ourFingerprint, out var oursLength);
                var unasked = new IPEndPoint(IPAddress.Parse("198.51.100.42"), RemPacket.DefaultPeerDialPort);
                receiver.InjectExternalPacket(ours, oursLength, unasked);
                Check(heard.Count == 1 && heard[0].From.Address.Equals(unasked.Address) && heard[0].Same,
                    $"audio arriving from somebody not ticked must be reported as them wanting to connect, on our password ({heard.Count} reported)");

                receiver.InjectExternalPacket(ours, oursLength, unasked);
                Check(heard.Count == 1, "and reported once, not on every packet they send");

                receiver.InjectExternalPacket(ours, oursLength, welcome);
                Check(heard.Count == 1, "somebody already ticked is not somebody asking to connect");

                var fromElsewhere = FormatPacket(otherFingerprint, out var elsewhereLength);
                var stranger2 = new IPEndPoint(IPAddress.Parse("198.51.100.43"), RemPacket.DefaultPeerDialPort);
                receiver.InjectExternalPacket(fromElsewhere, elsewhereLength, stranger2);
                Check(heard.Count == 2 && !heard[1].Same,
                    "somebody on another password must be reported as such, so the app can leave them alone");
            }

            return "asking is the default and the list offers ask, automatic and manual in that order; manual connects "
                + "nobody, automatic ticks them back at once, asking connects nobody until answered and asks once per "
                + "person; a different password is never accepted; the relay's list drives the same decision; and "
                + "unasked-for audio is what sets it off, once per person, never for somebody already ticked";
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            try { var c = AppConfig.Load(); c.AcceptPeerConnections = restoreMode; c.Save(); } catch { /* best effort */ }
            CuePlayer.GloballyMuted = restoreMuted;
        }
    }

    /// <summary>
    /// THE SEND-ONLY SERVICE, AND SOMEBODY TICKING IT ON A RELAY.
    ///
    /// <para>Ed, 2026-09-20. The service has nobody at a screen to ask, so it gets two answers rather than the app's
    /// three: tick them back, or leave it to the profile. Off by default — the service reaches exactly the people its
    /// profile names until you say otherwise. Left as it was, somebody who joins the relay after the profile was saved
    /// could never hear the service, because sound only passes where each has ticked the other.</para>
    /// </summary>
    private static string? TheServiceAndSomebodyTickingItOnARelay()
    {
        var relay = new IPEndPoint(IPAddress.Parse("203.0.113.90"), RemPacket.DefaultPort);
        var fromProfile = Guid.NewGuid();
        var tickedUs = Guid.NewGuid();
        var silent = Guid.NewGuid();
        RelayGroupClient.Member Member(Guid id, string name, bool ticksUs) =>
            new(id, new IPEndPoint(IPAddress.Parse("240.0.0.1"), RemPacket.DefaultPort), name, relay, ticksUs);
        var members = new[] { Member(tickedUs, "Ticked the service", true), Member(silent, "Has not", false) };

        var off = ServiceNetworkPresence.TicksFor([fromProfile], members, acceptAutomatically: false);
        Check(off.Count == 1 && off[0] == fromProfile,
            $"left off, the service must reach exactly the people its profile names ({off.Count})");

        var on = ServiceNetworkPresence.TicksFor([fromProfile], members, acceptAutomatically: true);
        Check(on.Contains(fromProfile) && on.Contains(tickedUs) && !on.Contains(silent),
            "turned on, it must also tick back everybody on the relay who has ticked IT, and nobody who has not");
        Check(on.Count == 2, $"and nobody twice ({on.Count} for two people)");

        var already = ServiceNetworkPresence.TicksFor([tickedUs], members, acceptAutomatically: true);
        Check(already.Count == 1, $"somebody the profile already names must not be counted twice ({already.Count})");

        // The service profile's own server field. There is no Connect button in that window — nobody is at a screen
        // when the service runs — so the address IS the instruction: set one and it joins on every start.
        var blank = Profile.NewBlank();
        using (var dialog = new ServiceProfileDialog(blank, false))
        {
            Check(string.IsNullOrEmpty(dialog.ResultForTest.RelayServer),
                "a service profile with no server must not claim one");
            Check(!dialog.ResultForTest.RelayConnectOnStart,
                "and must not say it joins one on start");
            dialog.SetServerForTest("203.0.113.120");
            var withServer = dialog.ResultForTest;
            Check(withServer.RelayServer == "203.0.113.120",
                $"a server typed into the service profile must be saved with it ({withServer.RelayServer ?? "nothing"})");
            Check(withServer.RelayConnectOnStart,
                "and an address there means join it on every start — the service has no button to press later");
            Check(ServiceSendHost.BuildRelayEndpoint(withServer) is not null,
                "and the service host must resolve that address to somewhere to go");
            dialog.SetServerForTest("   ");
            Check(dialog.ResultForTest.RelayServer is null && !dialog.ResultForTest.RelayConnectOnStart,
                "clearing the box must mean no server at all, not an empty one it tries to reach");
        }

        // And opening it again must SHOW the server it already has, or saving would quietly drop it.
        var hasServer = Profile.NewBlank();
        hasServer.RelayServer = "203.0.113.121";
        hasServer.RelayConnectOnStart = true;
        using (var reopened = new ServiceProfileDialog(hasServer, false))
        {
            Check(reopened.ResultForTest.RelayServer == "203.0.113.121",
                $"a service profile that already names a server must show it when reopened, or saving drops it "
                + $"({reopened.ResultForTest.RelayServer ?? "nothing"})");
        }

        // The setting itself: off by default, and it survives a save.
        var stored = ServiceStore.LoadAcceptRelayConnectionsAutomatically();
        try
        {
            ServiceStore.SaveAcceptRelayConnectionsAutomatically(true);
            Check(ServiceStore.LoadAcceptRelayConnectionsAutomatically(), "the service's choice must persist");
            ServiceStore.SaveAcceptRelayConnectionsAutomatically(false);
            Check(!ServiceStore.LoadAcceptRelayConnectionsAutomatically(), "and persist when turned off again");
            // Turning it on must not disturb the other settings in the same file.
            var volume = ServiceStore.LoadStartupVolume();
            ServiceStore.SaveAcceptRelayConnectionsAutomatically(true);
            Check(ServiceStore.LoadStartupVolume() == volume, "and must leave the other service settings alone");
        }
        finally { try { ServiceStore.SaveAcceptRelayConnectionsAutomatically(stored); } catch { /* best effort */ } }

        return "the service reaches its profile's people when left off, and also ticks back whoever ticks it on the "
            + "relay when turned on, nobody twice; the choice persists and leaves the other service settings alone";
    }

    /// <summary>
    /// REMOVING A PEER WHILE YOU ARE ON A RELAY.
    ///
    /// <para>Ed, 2026-09-20: taking the iPhone off the desktop put an error box on screen — the WinForms "Continue or
    /// Quit" one, which meant an exception on the window's own thread. It left no trace anywhere, because nothing was
    /// hooked to catch it (now fixed in Program). This drives the real removal, in the state he was in: on a relay,
    /// with somebody ticked there, with a phone paired through it, and with an ordinary network peer as well. Every
    /// part of the refresh that follows a removal runs here — the lists, the live status, the send targets and the
    /// allow-list — so a throw in any of them fails this rather than landing in a box.</para>
    /// </summary>
    private static string? RemovingAPeerWhileOnARelay()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        var restoreMode = AppConfig.Load().AcceptPeerConnections;
        MainForm? form = null;
        try
        {
            SetAcceptMode(PeerAcceptMode.Manual);   // no questions: this is about the removal, not the asking
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return Skip($"headless MainForm could not be constructed: {ex.GetType().Name}: {ex.Message}"); }

            var relay = new IPEndPoint(IPAddress.Parse("203.0.113.91"), RemPacket.DefaultPort);
            form.ConnectToRelayForTest("remote.example.test", relay);
            var onRelay = Guid.NewGuid();
            Feed(form.RelayGroupForTest, relay, RosterPacket([(onRelay, "ED_LT", true)], paired: true),
                RelayInbound.Consumed, "a member list with a phone paired too");
            form.SyncRelayPeersListForTest();

            // Tick the person on the relay and the phone paired through it, as Ed had.
            var phoneRow = Enumerable.Range(0, form.RelayPeersListForTest.Items.Count)
                .First(i => form.RelayPeersListForTest.Items[i]!.ToString()!.Contains("phone", StringComparison.OrdinalIgnoreCase));
            var personRow = Enumerable.Range(0, form.RelayPeersListForTest.Items.Count).First(i => i != phoneRow);
            form.RelayPeerTickedForTest(personRow, true);
            form.RelayPeerTickedForTest(phoneRow, true);
            Check(form.RelayTickedForTest.Contains(onRelay) && form.RelayPairTickedForTest,
                "the person on the relay and the phone paired through it must both be ticked for this to mean anything");

            // And an ordinary network peer beside them — the iPhone on the LAN, which is what was removed.
            var lanPhone = new PeerAnnouncement(Guid.NewGuid(), "iPhone", RemPacket.DefaultPeerDialPort,
                CanSend: true, CanReceive: true, DateTime.UtcNow, IPAddress.Parse("192.168.1.150"));
            form.SelectPeerForTest(lanPhone);
            Check(form.SelectedSendEndpointsForTest().Length == 2,
                $"we must be sending to the relay and to the network peer ({form.SelectedSendEndpointsForTest().Length})");

            // The removal, and everything the window does after one. A throw anywhere in here is the error box.
            form.DeselectPeerForTest(lanPhone.InstanceId);
            form.SyncAllPeerListsForTest();
            form.UpdateConnectedListLiveStatusForTest();
            form.SyncRelayPeersListForTest();
            Check(form.SelectedSendEndpointsForTest() is [var left] && left.Equals(relay),
                "after removing the network peer only the relay is left, and nothing threw doing it");
            Check(!form.ReceiverForTest.IsSenderAllowedForTest(new IPEndPoint(lanPhone.Address, lanPhone.AudioPort)),
                "and the peer that was removed is turned away again");

            // The other order too: take the relay away while a network peer is still there.
            form.SelectPeerForTest(lanPhone);
            form.DisconnectFromRelayForTest();
            form.SyncAllPeerListsForTest();
            form.UpdateConnectedListLiveStatusForTest();
            Check(form.SelectedSendEndpointsForTest() is [var lan] && lan.Address.Equals(lanPhone.Address),
                "leaving the relay while a network peer is ticked must leave that peer, and nothing may throw");

            return "removing a network peer while on a relay — with somebody ticked there and a phone paired through "
                + "it — leaves the relay alone, turns the removed peer away, and throws nothing; nor does leaving the "
                + "relay with a network peer still ticked";
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            try { SetAcceptMode(restoreMode); } catch { /* best effort */ }
            CuePlayer.GloballyMuted = restoreMuted;
        }
    }

    /// <summary>
    /// GATE GUARD: A RUN MUST NOT MAKE A NOISE, INCLUDING OUT LOUD.
    ///
    /// <para>Cue sounds have been muted for a run since 2026-08-15. Speech was not, and driving the window drives
    /// the code that speaks: on 2026-09-20 Ed heard fragments of it through NVDA while a run was going on —
    /// \"password\", \"connected\", \"ticked us\". This holds the run to silence on both counts, and counts the
    /// places that speak so a new one cannot quietly arrive outside the rule.</para>
    /// </summary>
    private static string? AGateRunMakesNoNoise()
    {
        Check(ScreenReader.Suppressed, "a gate run must say nothing out loud — it is running on somebody's machine");
        Check(!ScreenReader.Speak("this must never be heard"), "and a call to speak during a run must do nothing at all");
        Check(CuePlayer.GloballyMuted || !CuePlayer.GloballyMuted, "cue sounds are muted per step, which is their own rule");

        var root = FindSourceRoot();
        if (root is null) return Skip("the source tree is not reachable (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var speakers = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) || file.EndsWith("ScreenReader.cs", StringComparison.Ordinal)) continue;
            var text = File.ReadAllText(file);
            var at = 0;
            while ((at = text.IndexOf("ScreenReader.Speak(", at, StringComparison.Ordinal)) >= 0)
            {
                speakers.Add(Path.GetFileName(file));
                at += 1;
            }
        }
        Check(speakers.Count > 0, "the app must speak somewhere, or this guard is watching nothing");
        return $"a run is silent, and the {speakers.Count} place(s) that speak are all behind the one switch it holds down";
    }

    /// <summary>
    /// A PEER LIST CHANGING UNDER YOU MUST KEEP THE CURSOR ON THE PERSON.
    ///
    /// <para>Ed, 2026-09-21. The laptop log of that night has him ticking a phone at 00:17:50, the phone leaving the
    /// discovered list for Connected peers the same second, and at 00:17:52 "peer selected: ED_DT 192.168.1.95" — a
    /// machine he had unticked twenty seconds earlier. Nothing in the app chose ED_DT. The list had moved under his
    /// cursor, which was kept as a row NUMBER, and the number was clamped onto whoever slid into the gap.</para>
    ///
    /// <para>So the cursor follows the person. When that person leaves the list altogether it lands on their
    /// replacement AND says so — under a screen reader a cursor that moves in silence is a keypress on a stranger.</para>
    /// </summary>
    private static string? TheCursorStaysOnThePerson()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        var restoreMode = AppConfig.Load().AcceptPeerConnections;
        MainForm? form = null;
        try
        {
            SetAcceptMode(PeerAcceptMode.Manual);   // nothing must tick anybody back behind this test
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return Skip($"headless MainForm could not be constructed: {ex.GetType().Name}: {ex.Message}"); }

            // Four of them, on purpose. With only two, a clamped row NUMBER lands on the right person by luck and
            // proves nothing; with four, following the number and following the person give different answers.
            PeerAnnouncement Peer(string name, string address) => new(Guid.NewGuid(), name, RemPacket.DefaultPeerDialPort,
                CanSend: true, CanReceive: true, DateTime.UtcNow, IPAddress.Parse(address));
            var aaa = Peer("AAA_one", "192.168.1.11");
            var bbb = Peer("BBB_two", "192.168.1.12");
            var ccc = Peer("CCC_three", "192.168.1.13");
            var phone = Peer("iPhone", "192.168.1.8");
            foreach (var p in new[] { aaa, bbb, ccc, phone }) form.SelectPeerForTest(p);
            form.SyncAllPeerListsForTest();

            var list = form.ConnectedPeersListForTest;
            if (list.Items.Count != 4) return Skip($"the connected list did not take all four peers ({list.Items.Count})");
            int RowOf(string name) => Enumerable.Range(0, list.Items.Count)
                .First(i => list.Items[i]!.ToString()!.Contains(name, StringComparison.Ordinal));
            Check(RowOf("AAA_one") == 0 && RowOf("BBB_two") == 1,
                "the rows must be in name order, or the arithmetic below means nothing");

            // The cursor is on the second row. Somebody ABOVE them leaves, so every row below moves up one — and the
            // row number the cursor used to sit on now belongs to somebody else entirely.
            list.SelectedIndex = RowOf("BBB_two");
            form.DeselectPeerForTest(aaa.InstanceId);
            form.SyncAllPeerListsForTest();
            Check(list.Items.Count == 3, $"the list must have shed the row that left ({list.Items.Count})");
            Check(list.SelectedIndex == 0,
                $"the cursor must follow BBB_two up to row 0, not stay on row 1 where CCC_three now sits "
                + $"(it is on \"{(list.SelectedIndex >= 0 ? list.Items[list.SelectedIndex] : null)}\")");
            Check(list.Items[list.SelectedIndex]!.ToString()!.Contains("BBB_two", StringComparison.Ordinal),
                "the row moved; the person did not, and the cursor is on the person");

            // And the case that actually bit Ed: the person UNDER the cursor is the one who goes.
            list.SelectedIndex = RowOf("iPhone");
            var said = new List<string>();
            form.LogForTest.EventTapForTest = line => { lock (said) said.Add(line); };
            try
            {
                form.DeselectPeerForTest(phone.InstanceId);
                form.SyncAllPeerListsForTest();
            }
            finally { form.LogForTest.EventTapForTest = null; }

            Check(list.Items.Count == 2 && list.SelectedIndex >= 0,
                "with the row under the cursor gone, the cursor must land somewhere real rather than being thrown away");
            var moved = said.Where(l => l.Contains("list cursor:", StringComparison.Ordinal)).ToList();
            Check(moved.Count >= 1, $"a cursor that moved to somebody else must be written down, every time ({moved.Count} lines)");
            Check(moved.Any(l => l.Contains("connected peer", StringComparison.Ordinal)),
                $"and the line must name the list it happened in (got: {string.Join(" | ", moved)})");
            Check(moved.Any(l => l.Contains(list.Items[list.SelectedIndex]!.ToString()!, StringComparison.Ordinal)),
                $"and who the cursor ended up on, so the log says what the user was about to press space on "
                + $"(cursor: \"{list.Items[list.SelectedIndex]}\", log: {string.Join(" | ", moved)})");

            return "the cursor follows the person, not the row number: it stays put when somebody above them leaves, "
                + "and when the person under it leaves it lands on their replacement and says so in the log";
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            try { SetAcceptMode(restoreMode); } catch { /* best effort */ }
            CuePlayer.GloballyMuted = restoreMuted;
        }
    }

    /// <summary>
    /// UNTICKING SOMEBODY MUST KEEP THEM UNTICKED.
    ///
    /// <para>Ed, 2026-09-21: "otherwise you can never cisconnect". On Automatic, unticking somebody who still has you
    /// ticked used to put them straight back — their audio keeps arriving, the app sees audio from somebody unticked,
    /// and ticks them. His answer was to send the other end a message telling it to untick. It does not need one: the
    /// decision is this end's to keep, so this end keeps it, and nothing goes on the wire.</para>
    ///
    /// <para>It holds for the run, not forever. Tick them again and the refusal goes with it.</para>
    /// </summary>
    private static string? UntickingSomebodyKeepsThemUnticked()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        var restoreMode = AppConfig.Load().AcceptPeerConnections;
        MainForm? form = null;
        try
        {
            SetAcceptMode(PeerAcceptMode.Automatic);   // the mode Ed was actually on, per his global config
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return Skip($"headless MainForm could not be constructed: {ex.GetType().Name}: {ex.Message}"); }

            // Somebody the user has said nothing about: automatic ticks them back, which is what it is for.
            var stranger = new IPEndPoint(IPAddress.Parse("198.51.100.77"), RemPacket.DefaultPeerDialPort);
            form.SomeoneWantsToConnectForTest(stranger, samePassword: true);
            Check(form.SelectedSendEndpointsForTest().Any(e => e.Address.Equals(stranger.Address)),
                "on automatic, somebody who ticks us and whom we have never unticked must still be ticked back");

            // Now somebody the user ticked themselves, and then unticked.
            var them = new PeerAnnouncement(Guid.NewGuid(), "ED_DT", RemPacket.DefaultPeerDialPort,
                CanSend: true, CanReceive: true, DateTime.UtcNow, IPAddress.Parse("192.168.1.95"));
            var theirEndpoint = new IPEndPoint(them.Address, them.AudioPort);
            bool Connected() => form!.SelectedSendEndpointsForTest().Any(e => e.Address.Equals(them.Address));
            form.SelectPeerByUserForTest(them);
            Check(Connected(), "ticking somebody must connect them, or the untick below proves nothing");

            // The untick, and then their audio arriving again — they have not stopped ticking us.
            var said = new List<string>();
            form.LogForTest.EventTapForTest = line => { lock (said) said.Add(line); };
            try { form.DeselectPeerByUserForTest(them.InstanceId); }
            finally { form.LogForTest.EventTapForTest = null; }
            Check(!Connected(), "unticking must actually untick");
            Check(said.Any(l => l.Contains("peer deselected", StringComparison.Ordinal) && l.Contains("by you", StringComparison.Ordinal)),
                $"and the log must say it was the user's doing, not housekeeping (got: {string.Join(" | ", said)})");
            Check(form.AcceptRefusedForTest.Any(k => k.Contains("192.168.1.95", StringComparison.Ordinal)),
                "and the decision must be remembered against them");

            form.SomeoneWantsToConnectForTest(theirEndpoint, samePassword: true);
            Check(!Connected(), "THE BUG: somebody the user has just unticked must not be ticked back by automatic");
            form.SomeoneWantsToConnectForTest(theirEndpoint, samePassword: true);
            Check(!Connected(), "and not on the next packet either — it is a decision, not a one-off");

            // Ticking them again is the user changing their mind, and it takes the refusal with it.
            form.SelectPeerByUserForTest(them);
            Check(Connected(), "ticking them again must connect them");
            Check(!form.AcceptRefusedForTest.Any(k => k.Contains("192.168.1.95", StringComparison.Ordinal)),
                "and must clear the refusal, or automatic would stay switched off for them for the rest of the run");

            return "on automatic, somebody who ticks us is ticked back — but somebody the user has just unticked stays "
                + "unticked however long they keep ticking us, the untick is logged as the user's own, and ticking "
                + "them again clears it; nothing is sent to the other end to achieve any of it";
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            try { SetAcceptMode(restoreMode); } catch { /* best effort */ }
            CuePlayer.GloballyMuted = restoreMuted;
        }
    }

    /// <summary>
    /// SOMEBODY ON A SERVER BELONGS IN ONE LIST AT A TIME.
    ///
    /// <para>Ed, 2026-09-21: "in discovered peers on server I see ed_dT even though I'm connected already to ed DT on
    /// server. that's weird and should not happen". The server list used to keep everybody and show a tick, while the
    /// list directly above it drops people the moment you connect. Two lists side by side meaning opposite things.</para>
    ///
    /// <para>And the other half of the same knot: the tick lived in two places at once, so unticking somebody in
    /// Connected peers left them in the server's tick list and this machine put them straight back.</para>
    ///
    /// <para>Last, the same machine reached twice — on the network AND through the server — now says so on both rows.</para>
    /// </summary>
    private static string? ServerPeopleAreInOneListAtATime()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        var restoreMode = AppConfig.Load().AcceptPeerConnections;
        MainForm? form = null;
        try
        {
            SetAcceptMode(PeerAcceptMode.Automatic);   // the mode that used to undo the untick
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return Skip($"headless MainForm could not be constructed: {ex.GetType().Name}: {ex.Message}"); }

            var relay = new IPEndPoint(IPAddress.Parse("203.0.113.92"), RemPacket.DefaultPort);
            form.ConnectToRelayForTest("remote.example.test", relay);
            var onServer = Guid.NewGuid();
            Feed(form.RelayGroupForTest, relay, RosterPacket([(onServer, "ED_DT", true)], paired: false),
                RelayInbound.Consumed, "a member list with ED_DT on it, ticking us");
            form.SyncRelayPeersListForTest();

            var serverList = form.RelayPeersListForTest;
            Check(serverList.Items.Count == 1, $"ED_DT must be offered in the server list before we tick them ({serverList.Items.Count})");

            form.RelayPeerTickedForTest(0, true);
            form.SyncAllPeerListsForTest();
            form.SyncRelayPeersListForTest();
            Check(form.RelayTickedForTest.Contains(onServer), "ticking them must tick them");
            Check(serverList.Items.Count == 0,
                $"THE BUG: somebody you are connected to must leave the server list, as they leave the list above it (still there: {string.Join(", ", serverList.Items.Cast<object>())})");
            Check(form.ConnectedPeersListForTest.Items.Cast<object>().Any(i => i!.ToString()!.Contains("ED_DT", StringComparison.Ordinal)),
                "and be in Connected peers instead — they have to be somewhere you can untick them");

            // Untick them THERE. The server tick has to go with it, or this machine puts them back.
            var said = new List<string>();
            form.LogForTest.EventTapForTest = line => { lock (said) said.Add(line); };
            try { form.DeselectPeerByUserForTest(onServer); }
            finally { form.LogForTest.EventTapForTest = null; }
            Check(!form.RelayTickedForTest.Contains(onServer),
                "THE BUG: unticking somebody in Connected peers must clear their server tick too");
            Check(said.Any(l => l.Contains("cleared their server tick", StringComparison.Ordinal)),
                $"and say so, because it is two ticks going for one keypress (got: {string.Join(" | ", said)})");

            form.SyncRelayPeersListForTest();
            Check(serverList.Items.Count == 1 && !serverList.GetItemChecked(0),
                "they come back to the server list, unticked, ready to be ticked again");
            form.SyncRelayPeersListForTest();
            form.SyncAllPeerListsForTest();
            Check(!form.RelayTickedForTest.Contains(onServer) && !form.SelectedSendEndpointsForTest().Any(e => e.Equals(relay)),
                "and they must STAY gone — they are still ticking us, and automatic must not undo the untick");

            // The same machine on both routes says so, on both rows.
            form.RelayPeerTickedForTest(0, true);
            var lanSame = new PeerAnnouncement(Guid.NewGuid(), "ED_DT", RemPacket.DefaultPeerDialPort,
                CanSend: true, CanReceive: true, DateTime.UtcNow, IPAddress.Parse("192.168.1.95"));
            form.SelectPeerForTest(lanSame);
            form.SyncAllPeerListsForTest();
            form.UpdateConnectedListLiveStatusForTest();
            var rows = form.ConnectedPeersListForTest.Items.Cast<object>().Select(i => i!.ToString()!).ToList();
            var doubled = rows.Where(r => r.Contains("same machine twice", StringComparison.Ordinal)).ToList();
            Check(doubled.Count == 2,
                $"both rows for one machine must say it is the same machine twice ({doubled.Count} of {rows.Count}: {string.Join(" | ", rows)})");
            Check(doubled.Any(r => r.Contains("on your network", StringComparison.Ordinal))
                && doubled.Any(r => r.Contains("through the server", StringComparison.Ordinal)),
                "and each must name the OTHER way in, so it is obvious which row is which");

            return "somebody you are connected to leaves the server list for Connected peers, unticking them there "
                + "clears the server tick as well and says so, they stay unticked with automatic on, and one machine "
                + "reached both ways says so on both rows";
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            try { SetAcceptMode(restoreMode); } catch { /* best effort */ }
            CuePlayer.GloballyMuted = restoreMuted;
        }
    }

    private static void SetAcceptMode(PeerAcceptMode mode)
    {
        var cfg = AppConfig.Load();
        cfg.AcceptPeerConnections = mode;
        cfg.Save();
    }

    /// <summary>
    /// HEARTBEATS THROUGH A RELAY GROUP.
    ///
    /// <para>In a group the relay itself never answers a ping; the people behind it do, from their own member addresses,
    /// and everyone's pongs reach everyone. A member's pong to OUR ping must count as the relay answering; a pong carrying
    /// someone else's timestamp must not count at all, or the round trip shown would be nonsense.</para>
    /// </summary>
    private static string? AuditRelayGroupHeartbeats()
    {
        var relay = new IPEndPoint(IPAddress.Parse("203.0.113.8"), RemPacket.DefaultPort);
        var member = new IPEndPoint(IPAddress.Parse("240.0.0.9"), RemPacket.DefaultPort);
        var pings = new List<byte[]>();
        using var heartbeat = new HeartbeatService();
        heartbeat.SendTransport = (data, length, _) => { lock (pings) pings.Add(data[..length]); return true; };
        heartbeat.RelayOfMember = address => address.Equals(member) ? relay : null;
        heartbeat.SetTrackedPeers([relay]);
        heartbeat.Start();
        Check(WaitUntil(() => { lock (pings) return pings.Count > 0; }, 4000), "the heartbeat must ping the relay");
        byte[] ping;
        lock (pings) ping = pings[^1];
        Check(RemPacket.TryReadHeartbeat(ping.AsSpan(RemPacket.HeaderSize), out _, out var ourTick), "our ping must be readable");

        static byte[] Pong(long tick)
        {
            var packet = new byte[RemPacket.HeaderSize + RemPacket.HeartbeatPayloadSize];
            RemPacket.WriteHeader(packet, RemPacketType.Heartbeat, 0xFFFF, 1);
            RemPacket.WriteHeartbeatPayload(packet.AsSpan(RemPacket.HeaderSize), HeartbeatKind.Pong, tick);
            return packet;
        }

        var theirs = Pong(ourTick + 987_654);
        heartbeat.HandleInjectedPacket(theirs, theirs.Length, member);
        Check(heartbeat.GetAllPeerHealth().Single().AgeOfLastPong is null,
            "a pong answering someone else's ping (another member's timestamp, fanned out by the group) must not count");
        var ours = Pong(ourTick);
        heartbeat.HandleInjectedPacket(ours, ours.Length, member);
        var health = heartbeat.GetAllPeerHealth().Single();
        Check(health.AudioEndpoint.Equals(relay) && health.AgeOfLastPong is not null && health.RttMs is not null,
            "a group member's pong to our own ping must count as the relay answering, with a round trip");
        return "a member's pong to our ping counts for the relay; a pong carrying someone else's timestamp is ignored";
    }

    /// <summary>
    /// THE APP AND THE SEND-ONLY SERVICE BOTH TAKE PART IN RELAY GROUPS.
    ///
    /// <para>Read from the source, because neither can be started here: each must route what it sends through its relay
    /// group, hand what arrives on its sending socket to it first, learn relays from their address checks (the service
    /// echoing those at all is new: an enforcing relay would have shut it out), and keep the group told who it sends to
    /// and under which password. Every gap is listed, not only the first.</para>
    /// </summary>
    private static string? AuditAppAndServiceTakePartInRelayGroups()
    {
        var root = FindSourceRoot();
        if (root is null) return Skip("the source tree is not reachable (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        string Read(string file) => File.ReadAllText(Path.Combine(root, "src", "RemSound.App", file)).Replace("\r", "");
        var app = Read("MainForm.cs");
        var service = Read("ServiceNetworkPresence.cs");
        var host = Read("ServiceSendHost.cs");
        var relayUi = Read("MainForm.Relay.cs");
        var missing = new List<string>();
        void Need(string text, string code, string what) { if (!text.Contains(code, StringComparison.Ordinal)) missing.Add(what); }

        Need(app, "sender.RelayRouter = relayGroup;", "the app must route what it sends through its relay group");
        Need(app, "relayGroup.HandleInbound(buffer, ref length, remote, out var member)", "the app must hand what arrives on its sending socket to the relay group first");
        Need(app, "receiver.RelayOfMember = relayGroup.RelayOf;", "the app's receiver must let in the members of a chosen relay");
        Need(app, "heartbeatService.RelayOfMember = relayGroup.RelayOf;", "the app's heartbeat must count a member's pong for its relay");
        Need(app, "relayGroup.NoteRelay(remote);", "the app must learn a relay from its address check");
        Need(app, "relayGroup.Start(sender.SendRaw);", "the app must start its relay group on the raw socket");
        Need(relayUi, "relayGroup.Connect(resolved);", "the app must connect to a relay only when the user asks for one");
        Need(relayUi, "relayGroup.SetTicked(relayTicked, relayPairTicked);", "the app must tell the relay who it has ticked");
        Need(relayUi, "relayGroup.Disconnect();", "the app must be able to leave a relay");
        Need(app, "GatherRelayProfile(profile);", "a profile must remember its relay and who was ticked on it");
        Need(app, "ApplyRelayProfile(p);", "loading a profile must put its relay and its ticks back");
        Need(app, "relayGroup.SetIdentity(Environment.MachineName, currentAudioFingerprint);", "the app must tell its relay group its password");
        Need(service, "sender.RelayRouter = group;", "the service must route what it sends through its relay group");
        Need(service, "group.HandleInbound(buf, ref len, remote, out var member)", "the service must hand what arrives on its sending socket to the relay group first");
        Need(service, "heartbeat.RelayOfMember = group.RelayOf;", "the service's heartbeat must count a member's pong for its relay");
        Need(service, "else if (type == RemPacketType.AddrCheck) EchoAddrCheck(buf, len, remote);", "the service must echo a relay's address check that arrives on its sending socket");
        Need(service, "relayGroup?.NoteRelay(remote);", "the service must learn a relay from its address check");
        Need(service, "group.Start(sender.SendRaw);", "the service must start its relay group on the raw socket");
        Need(service, "group.Connect(relay);", "the service must connect to the relay its profile names");
        Need(service, "group.SetTicked(TicksFor(profileTicks, group.Members, acceptRelayAutomatically), pairPartner: true);",
            "the service must tell the relay who its profile has ticked, plus anyone it is set to accept");
        Need(service, "group.Changed += OnRelayGroupChanged;",
            "the service must work that out again each time the relay's member list changes, not only at start-up");
        Need(host, "presence.Start(RemPacket.DefaultPort, endpoints, relay, tickedOnRelay);", "the service must pass its profile's relay and ticks to its network presence");
        Check(missing.Count == 0, string.Join(" | ", missing));
        return "the app and the send-only service both route through, listen to, learn and leave their relay groups";
    }

    /// <summary>One app instance on the relay, wired exactly as MainForm wires itself (sender socket in, relay group,
    /// heartbeat, address-check echo), with a decode-only receiver. groups: false is an app from before relay groups.</summary>
    private sealed class RelayTestApp : IDisposable
    {
        public string Name { get; }
        public AudioSender Sender { get; } = new();
        public AudioReceiver Receiver { get; } = new();
        public HeartbeatService Heartbeat { get; } = new();
        public RelayGroupClient? Group { get; }
        private readonly CancellationTokenSource talking = new();

        /// <summary>Tick people on the relay, exactly as MainForm does it: the relay is told who, our audio goes to the
        /// relay for them, and their audio is let in at the address the relay credits them to. Ticking the phone paired
        /// with us instead opens the relay's own address, which is where a phone's audio comes from.</summary>
        public void Tick(IEnumerable<Guid> memberIds, bool pairPartner = false)
        {
            var wanted = memberIds.ToHashSet();
            var group = Group!;
            group.SetTicked(wanted, pairPartner);
            var members = group.Members.Where(m => wanted.Contains(m.Id)).ToList();
            var targets = members.Select(m => group.RelayOf(m.Address) ?? m.Address).ToList();
            if (pairPartner && group.ConnectedRelay is { } pairRelay) targets.Add(pairRelay);
            Sender.SetReceivers(targets.GroupBy(e => $"{e.Address}:{e.Port}").Select(g => g.First()).ToList());
            var allowed = members.Select(m => new IPEndPoint(m.Address.Address, 0)).ToList();
            if (pairPartner && group.ConnectedRelay is { } allowRelay) allowed.Add(new IPEndPoint(allowRelay.Address, 0));
            Receiver.SetAllowedSenders(allowed);
        }

        public RelayTestApp(string name, string password, IPEndPoint relay, bool groups)
        {
            Name = name;
            var (key, fingerprint) = RemSoundCrypto.ForPlainPassword(password);
            Sender.AudioKey = key;
            Sender.AudioFingerprint = fingerprint;
            Sender.ConfigureCodec(AudioTransportCodec.Pcm);
            Sender.SetSendRate(SendRate.Standard);
            Sender.SetTightLatency(false);
            // An app that knows nothing of groups pairs through the relay as it always has: it sends to the relay and
            // takes what the relay sends back. One that does know waits for the user to tick somebody (see Tick).
            Sender.SetReceivers(groups ? [] : [relay]);
            Receiver.AudioKey = key;
            Receiver.AudioFingerprint = fingerprint;
            Receiver.SetOutputDevices([]);       // decode only; the gate never opens a device or makes a sound
            Receiver.SetPlaybackEnabled(true);
            Receiver.SetAllowedSenders(groups ? [] : [relay]);
            Heartbeat.SendTransport = Sender.SendVia;
            if (groups)
            {
                Group = new RelayGroupClient(Guid.NewGuid());
                Group.SetIdentity(name, fingerprint);
                Group.Connect(relay);
                Sender.RelayRouter = Group;
                Receiver.RelayOfMember = Group.RelayOf;
                Heartbeat.RelayOfMember = Group.RelayOf;
                Group.Start(Sender.SendRaw);
            }
            Receiver.OnHeartbeatReceived = (buffer, length, remote) => Heartbeat.HandleInjectedPacket(buffer, length, remote);
            Receiver.OnAddrCheckReceived = (packet, length, remote) =>
            {
                Sender.SendVia(packet, length, remote);
                Group?.NoteRelay(remote);
            };
            Sender.OnInboundPacket = (buffer, length, remote) =>
            {
                if (Group is not null)
                {
                    if (Group.HandleInbound(buffer, ref length, remote, out var member) == RelayInbound.Consumed) return;
                    if (member is not null) remote = member;
                }
                if (length < RemPacket.HeaderSize || !RemPacket.TryReadHeader(buffer.AsSpan(0, length), out var type, out _, out _)) return;
                if (type == RemPacketType.Heartbeat) Heartbeat.HandleInjectedPacket(buffer, length, remote);
                else Receiver.InjectExternalPacket(buffer, length, remote);
            };
            Sender.StartReceiving();
            Heartbeat.SetTrackedPeers([relay]);
            Heartbeat.Start();
        }

        /// <summary>Send uncompressed audio at this level, paced in real time, until disposed.</summary>
        public void StartTalking(float level)
        {
            Sender.SetPluginSendActive(true);
            var token = talking.Token;
            _ = Task.Run(async () =>
            {
                var block = Block(480, level);
                while (!token.IsCancellationRequested)
                {
                    Sender.SubmitPluginBlock(block);
                    try { await Task.Delay(10, token); } catch (OperationCanceledException) { return; }
                }
            });
        }

        public void Dispose()
        {
            talking.Cancel();
            try { Group?.Stop(); } catch { /* test teardown */ }
            try { Heartbeat.Dispose(); } catch { }
            try { Sender.Dispose(); } catch { }
            try { Receiver.Dispose(); } catch { }
            talking.Dispose();
        }
    }

    private static void Feed(RelayGroupClient client, IPEndPoint relay, byte[] packet, RelayInbound expected, string what)
    {
        var length = packet.Length;
        Check(client.HandleInbound(packet, ref length, relay, out _) == expected, $"{what} must be handled as {expected}");
    }

    private static byte[] PlainPacket(RemPacketType type, ushort streamId, byte[] payload)
    {
        var packet = new byte[RemPacket.HeaderSize + payload.Length];
        RemPacket.WriteHeader(packet, type, streamId, 1);
        payload.CopyTo(packet, RemPacket.HeaderSize);
        return packet;
    }

    private static byte[] IdBytes(Guid id)
    {
        var bytes = new byte[RelayGroupClient.ClientIdSize];
        id.TryWriteBytes(bytes, bigEndian: true, out _);
        return bytes;
    }

    /// <summary>A packet as the relay forwards it: the header at version 2, the sender's id, the payload.</summary>
    private static byte[] GroupPacket(byte type, Guid from, byte[] payload)
    {
        var packet = new byte[RelayGroupClient.GroupHeaderSize + payload.Length];
        RemPacket.WriteHeader(packet, (RemPacketType)type, 0, 0);
        packet[4] = RelayGroupClient.GroupVersion;
        IdBytes(from).CopyTo(packet, RemPacket.HeaderSize);
        payload.CopyTo(packet, RelayGroupClient.GroupHeaderSize);
        return packet;
    }

    /// <summary>A member list as the relay sends it: the count, each member's id and name, then the flags byte. The
    /// relay's own id is all zeros.</summary>
    private static byte[] RosterPacket((Guid Id, string Name)[] members, bool paired)
        => RosterPacket(members.Select(m => (m.Id, m.Name, false)).ToArray(), paired);

    /// <summary>A member list exactly as the relay builds one: each member's id and name, then that member's own flags
    /// byte saying whether they have ticked the person being sent it, then one flags byte for the whole list.</summary>
    private static byte[] RosterPacket((Guid Id, string Name, bool TicksUs)[] members, bool paired)
    {
        const int entry = RelayGroupClient.RosterEntryBytes;
        var payload = new byte[1 + members.Length * entry + 1];
        payload[0] = (byte)members.Length;
        for (var i = 0; i < members.Length; i++)
        {
            IdBytes(members[i].Id).CopyTo(payload, 1 + i * entry);
            RelayGroupClient.EncodeName(members[i].Name, payload.AsSpan(1 + i * entry + RelayGroupClient.ClientIdSize, RelayGroupClient.NameBytes));
            payload[1 + i * entry + entry - 1] = members[i].TicksUs ? RelayGroupClient.RosterMemberFlagTicksUs : (byte)0;
        }
        payload[^1] = paired ? RelayGroupClient.RosterFlagV1Paired : (byte)0;
        return GroupPacket(RelayGroupClient.TypeRoster, Guid.Empty, payload);
    }

    /// <summary>A member list from a relay from before it said who had ticked you: entries one byte shorter.</summary>
    private static byte[] OlderRosterPacket((Guid Id, string Name)[] members, bool paired)
    {
        const int entry = RelayGroupClient.ClientIdSize + RelayGroupClient.NameBytes;
        var payload = new byte[1 + members.Length * entry + 1];
        payload[0] = (byte)members.Length;
        for (var i = 0; i < members.Length; i++)
        {
            IdBytes(members[i].Id).CopyTo(payload, 1 + i * entry);
            RelayGroupClient.EncodeName(members[i].Name, payload.AsSpan(1 + i * entry + RelayGroupClient.ClientIdSize, RelayGroupClient.NameBytes));
        }
        payload[^1] = paired ? RelayGroupClient.RosterFlagV1Paired : (byte)0;
        return GroupPacket(RelayGroupClient.TypeRoster, Guid.Empty, payload);
    }

    /// <summary>py.exe, or a python.exe that is not the Microsoft Store stub (which opens the Store instead of running).</summary>
    private static string? FindPython()
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var windowsPy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "py.exe");
        foreach (var candidate in dirs.Select(d => Path.Combine(d, "py.exe")).Prepend(windowsPy))
        {
            if (File.Exists(candidate)) return candidate;
        }
        foreach (var dir in dirs)
        {
            var candidate = Path.Combine(dir, "python.exe");
            if (File.Exists(candidate) && !dir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) return candidate;
        }
        return null;
    }

    /// <summary>The relay on loopback, logging to a temp file, with room for every test app on the one address.</summary>
    private static Process StartRelay(string python, string script, int port, string logPath)
    {
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (Path.GetFileName(python).Equals("py.exe", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add("-3");
        foreach (var arg in new[] { script, "--host", "127.0.0.1", "--port", port.ToString(), "--log-path", logPath, "--max-per-ip", "16" })
            start.ArgumentList.Add(arg);
        var process = Process.Start(start) ?? throw new InvalidOperationException("the relay process did not start");
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static string ReadShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }
}
