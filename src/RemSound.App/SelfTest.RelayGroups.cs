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
        me.SetTargets([relay]);
        me.Start((data, length, to) => { Capture(data.AsSpan(0, length), to); return true; });
        try
        {
            var audio = PlainPacket(RemPacketType.Audio, 7, [1, 2, 3, 4, 5]);
            Check(!me.TryRoute(audio, relay, Capture),
                "until a relay confirms a group, a packet to it must go exactly as it always has (pairs are untouched)");

            me.NoteRelay(relay);
            var hello = sent.LastOrDefault(s => s.To.Equals(relay) && s.Data.Length > 5 && s.Data[4] == RelayGroupClient.GroupVersion
                                                 && s.Data[5] == RelayGroupClient.TypeHello);
            Check(hello.Data is not null, "learning a peer is a relay must send it a hello at once, to join the group for our password");
            Check(hello.Data!.Length == RelayGroupClient.GroupHeaderSize + RelayGroupClient.NameBytes + RelayGroupClient.GroupTagBytes
                  && hello.Data.AsSpan(RelayGroupClient.GroupHeaderSize + RelayGroupClient.NameBytes).SequenceEqual(fingerprint)
                  && RelayGroupClient.DecodeName(hello.Data.AsSpan(RelayGroupClient.GroupHeaderSize, RelayGroupClient.NameBytes)) == "Me",
                "the hello must carry our name and, as the group tag, our password fingerprint");

            var alice = Guid.NewGuid();
            var bob = Guid.NewGuid();
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
            Check(sent.Count == 2 && sent.Any(s => s.Data[4] == RemPacket.Version) && sent.Any(s => s.Data[4] == RelayGroupClient.GroupVersion),
                "paired with a phone or older app, audio must go in both forms: group-framed for the group, ordinary for the phone");

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
            again.SetTargets([relay]);
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
            Check(WaitUntil(() => group.All(a => a.Group!.Members.All(m => a.Receiver.IsAudioFlowingFrom(m.Address.Address, TimeSpan.FromSeconds(2)))), 8000),
                "each member must hear the other two ("
                + string.Join("; ", group.Select(a => $"{a.Name} hears {string.Join(", ", a.Group!.Members.Where(m => a.Receiver.IsAudioFlowingFrom(m.Address.Address, TimeSpan.FromSeconds(2))).Select(m => m.Name))}")) + ")");
            foreach (var a in group)
            {
                var expected = a == partner ? 3 : 2;
                Check(a.Receiver.LiveSessionCountForTest == expected,
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
            return $"three people on one password heard each other as three; {partner.Name} also partnered the older app, which still heard one person; "
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
        var missing = new List<string>();
        void Need(string text, string code, string what) { if (!text.Contains(code, StringComparison.Ordinal)) missing.Add(what); }

        Need(app, "sender.RelayRouter = relayGroup;", "the app must route what it sends through its relay group");
        Need(app, "relayGroup.HandleInbound(buffer, ref length, remote, out var member)", "the app must hand what arrives on its sending socket to the relay group first");
        Need(app, "receiver.RelayOfMember = relayGroup.RelayOf;", "the app's receiver must let in the members of a chosen relay");
        Need(app, "heartbeatService.RelayOfMember = relayGroup.RelayOf;", "the app's heartbeat must count a member's pong for its relay");
        Need(app, "relayGroup.NoteRelay(remote);", "the app must learn a relay from its address check");
        Need(app, "relayGroup.Start(sender.SendRaw);", "the app must start its relay group on the raw socket");
        Need(app, "relayGroup.SetTargets(endpoints);", "the app must tell its relay group who it sends to");
        Need(app, "relayGroup.SetIdentity(Environment.MachineName, currentAudioFingerprint);", "the app must tell its relay group its password");
        Need(service, "sender.RelayRouter = group;", "the service must route what it sends through its relay group");
        Need(service, "group.HandleInbound(buf, ref len, remote, out var member)", "the service must hand what arrives on its sending socket to the relay group first");
        Need(service, "heartbeat.RelayOfMember = group.RelayOf;", "the service's heartbeat must count a member's pong for its relay");
        Need(service, "else if (type == RemPacketType.AddrCheck) EchoAddrCheck(buf, len, remote);", "the service must echo a relay's address check that arrives on its sending socket");
        Need(service, "relayGroup?.NoteRelay(remote);", "the service must learn a relay from its address check");
        Need(service, "group.Start(sender.SendRaw);", "the service must start its relay group on the raw socket");
        Need(service, "group.SetTargets(endpoints);", "the service must tell its relay group who it sends to");
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

        public RelayTestApp(string name, string password, IPEndPoint relay, bool groups)
        {
            Name = name;
            var (key, fingerprint) = RemSoundCrypto.ForPlainPassword(password);
            Sender.AudioKey = key;
            Sender.AudioFingerprint = fingerprint;
            Sender.ConfigureCodec(AudioTransportCodec.Pcm);
            Sender.SetSendRate(SendRate.Standard);
            Sender.SetTightLatency(false);
            Sender.SetReceivers([relay]);
            Receiver.AudioKey = key;
            Receiver.AudioFingerprint = fingerprint;
            Receiver.SetOutputDevices([]);       // decode only; the gate never opens a device or makes a sound
            Receiver.SetPlaybackEnabled(true);
            Receiver.SetAllowedSenders([relay]); // only the relay is chosen: its members must get in through it
            Heartbeat.SendTransport = Sender.SendVia;
            if (groups)
            {
                Group = new RelayGroupClient(Guid.NewGuid());
                Group.SetIdentity(name, fingerprint);
                Group.SetTargets([relay]);
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
