using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Net;
using System.Text;

namespace RemSound.Core;

/// <summary>Send one datagram on the app's socket. A span, so the audio path never allocates.</summary>
public delegate void DatagramSender(ReadOnlySpan<byte> datagram, IPEndPoint destination);

/// <summary>Decides whether a packet bound for a destination travels through a relay group, and if so sends it.</summary>
public interface IRelayRouter
{
    /// <summary>True when the packet was handled here: sent through a relay group, or dropped because it was bound for a
    /// group member whose relay is gone. False when the caller should send it exactly as it always has.</summary>
    bool TryRoute(ReadOnlySpan<byte> packet, IPEndPoint destination, DatagramSender send);
}

/// <summary>What <see cref="RelayGroupClient.HandleInbound"/> made of an arriving packet.</summary>
public enum RelayInbound
{
    /// <summary>Not a relay-group packet: handle it exactly as before.</summary>
    NotGroup,
    /// <summary>Relay bookkeeping (the member list, "the relay is full"): nothing more to do.</summary>
    Consumed,
    /// <summary>A group member's packet, now in ordinary form and credited to that member's own address.</summary>
    Delivered,
}

/// <summary>
/// Being one person among several on a relay.
///
/// <para><b>Why.</b> Ed, 2026-09-18, after GitHub issue #29: a relay carried one pair. The first two devices to reach it
/// got in, and a third heard nothing and was heard by no one. Letting the relay pass everyone's audio to everyone was no
/// answer on its own: every app keys a person by address, and through a relay everyone has the relay's address, so their
/// streams would knock each other out. Each packet has to say whose it is. The relay's group framing already does: the
/// ordinary 12-byte header with version 2, then the sender's 16-byte client id, then the payload untouched.</para>
///
/// <para><b>What this does.</b> Once a peer shows itself to be a relay (it sends an address check, or a member list),
/// this joins the group for our password: a hello every <see cref="HelloInterval"/> with our name and our 8-byte password
/// fingerprint, which the relay groups by. The fingerprint is the same one every Format packet already carries in the
/// clear, so it gives the relay nothing new. Everything we send to that relay then goes group-framed, and every group
/// member's packet coming back is put into ordinary form and credited to an address of that member's own, in 240.0.0.0/5,
/// which is reserved and can never be a real peer. Downstream nothing changes: a session, a row, a volume slider, a
/// recording track, a password check, each is per address, so each is now per person.</para>
///
/// <para><b>What it leaves alone.</b> Pairs. Until the relay confirms we are in a group, every packet goes exactly as it
/// always has. A phone or an older app still pairs through the relay's two ordinary slots, and the relay lets a group
/// member partner it. That is why heartbeats always go in ordinary form as well: they are what claims the slot beside a
/// waiting phone. Once the member list says we have such a partner, audio goes in both forms, one for the group and one
/// for the phone. An address check is echoed exactly as it came.</para>
///
/// <para>A member's address never reaches the wire: a packet for one goes to its relay, group-framed, and so to the
/// whole group (a pong, a remote-volume command: both are ignored by anyone they were not meant for).</para>
/// </summary>
public sealed class RelayGroupClient : IRelayRouter, IDisposable
{
    /// <summary>The header version the relay reads as its group framing.</summary>
    public const byte GroupVersion = 2;
    public const int ClientIdSize = 16;
    /// <summary>The ordinary 12-byte header plus the client id: 28.</summary>
    public const int GroupHeaderSize = RemPacket.HeaderSize + ClientIdSize;
    public const byte TypeHello = 6;
    public const byte TypeRoster = 7;
    public const byte TypeFull = 8;
    public const byte TypeBye = 9;
    public const int NameBytes = 32;
    public const int GroupTagBytes = RemPacket.PasswordFingerprintSize;
    private const int RosterEntryBytes = ClientIdSize + NameBytes;
    /// <summary>Member-list flags byte, bit 0: we hold one of the relay's ordinary pair slots with a partner.</summary>
    public const byte RosterFlagV1Paired = 0x01;
    public static readonly TimeSpan HelloInterval = TimeSpan.FromSeconds(2);
    /// <summary>The relay sends the member list every second; this long without one and we go back to ordinary form.</summary>
    public static readonly TimeSpan GroupAliveFor = TimeSpan.FromSeconds(5);

    private readonly object gate = new();
    private readonly byte[] clientId = new byte[ClientIdSize];
    private readonly Dictionary<IPEndPoint, RelayState> relays = new();
    private readonly Dictionary<IPEndPoint, MemberState> memberByAddress = new();
    private HashSet<IPEndPoint> targets = [];
    private string displayName = Environment.MachineName;
    private byte[]? groupTag;
    private Func<byte[], int, IPEndPoint, bool>? rawSend;
    private System.Threading.Timer? timer;
    private volatile Snapshot snapshot = Snapshot.Empty;
    [ThreadStatic] private static byte[]? wrapBuffer;

    public RelayGroupClient(Guid clientId, Action<string>? log = null)
    {
        if (clientId == Guid.Empty) throw new ArgumentException("a client id must not be empty", nameof(clientId));
        clientId.TryWriteBytes(this.clientId, bigEndian: true, out _);
        ClientId = clientId;
        Log = log;
    }

    public Guid ClientId { get; }

    /// <summary>Where group events are written (joined, a member arrived or left, paired with a phone).</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Raised (on whichever thread noticed) when the groups or their members change.</summary>
    public event Action? Changed;

    /// <summary>One person in one of our groups, as the rest of the app sees them.</summary>
    public sealed record Member(IPEndPoint Address, string Name, IPEndPoint Relay);

    private sealed class RelayState(IPEndPoint endpoint)
    {
        public IPEndPoint Endpoint { get; } = endpoint;
        public DateTime LastRosterUtc = DateTime.MinValue;
        public DateTime LastHelloUtc = DateTime.MinValue;
        public bool InGroup;
        public bool V1Paired;
        public readonly Dictionary<Guid, MemberState> Members = new();
    }

    private sealed class MemberState(Guid id, IPEndPoint address, IPEndPoint relay)
    {
        public Guid Id { get; } = id;
        public IPEndPoint Address { get; } = address;
        public IPEndPoint Relay { get; } = relay;
        public string Name = "";
        public bool Listed;
    }

    /// <summary>What the audio path reads on every packet, without a lock: the relays we are in a group on, and the
    /// relay behind each member address.</summary>
    private sealed class Snapshot(FrozenDictionary<IPEndPoint, bool> groupRelays, FrozenDictionary<IPEndPoint, IPEndPoint> memberRelay,
        IReadOnlyList<Member> members)
    {
        public static readonly Snapshot Empty = new(FrozenDictionary<IPEndPoint, bool>.Empty, FrozenDictionary<IPEndPoint, IPEndPoint>.Empty, []);
        /// <summary>Relay endpoint → whether we also hold an ordinary pair slot there with a partner.</summary>
        public FrozenDictionary<IPEndPoint, bool> GroupRelays { get; } = groupRelays;
        public FrozenDictionary<IPEndPoint, IPEndPoint> MemberRelay { get; } = memberRelay;
        public IReadOnlyList<Member> Members { get; } = members;
    }

    // ------- the app tells us who we are and who we talk to ------------------------------------------------------------

    /// <summary>Our name as others see it, and our password fingerprint (null: no password, so no group to join).</summary>
    public void SetIdentity(string name, byte[]? passwordFingerprint)
    {
        var tag = passwordFingerprint is { Length: GroupTagBytes } ? (byte[])passwordFingerprint.Clone() : null;
        bool changed;
        lock (gate)
        {
            var trimmed = string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name.Trim();
            changed = trimmed != displayName || !SameTag(tag, groupTag);
            displayName = trimmed;
            groupTag = tag;
            if (changed) foreach (var r in relays.Values) r.LastHelloUtc = DateTime.MinValue;
        }
        if (changed) Tick();
    }

    /// <summary>The peers we send to. Only a relay among them is joined; one we no longer send to is told goodbye.</summary>
    public void SetTargets(IEnumerable<IPEndPoint> endpoints)
    {
        var desired = new HashSet<IPEndPoint>(endpoints);
        List<IPEndPoint> leaving;
        lock (gate)
        {
            targets = desired;
            leaving = relays.Keys.Where(r => !desired.Contains(r)).ToList();
            foreach (var r in leaving) ForgetRelayLocked(r);
        }
        foreach (var r in leaving) SendBye(r);
        if (leaving.Count > 0) Publish();
    }

    /// <summary>Start sending hellos. <paramref name="send"/> must go straight to the socket: our own packets are already
    /// group-framed and must not be routed again.</summary>
    public void Start(Func<byte[], int, IPEndPoint, bool> send)
    {
        rawSend = send;
        timer ??= new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>Say goodbye to every relay we are in a group on and forget them all.</summary>
    public void Stop()
    {
        timer?.Dispose();
        timer = null;
        List<IPEndPoint> leaving;
        lock (gate)
        {
            leaving = relays.Values.Where(r => r.InGroup).Select(r => r.Endpoint).ToList();
            foreach (var r in relays.Keys.ToList()) ForgetRelayLocked(r);
        }
        foreach (var r in leaving) SendBye(r);
        Publish();
        rawSend = null;
    }

    public void Dispose() => Stop();

    // ------- what the rest of the app asks ----------------------------------------------------------------------------

    /// <summary>The relay behind a member's address, or null when the address is not a group member's.</summary>
    public IPEndPoint? RelayOf(IPEndPoint address) =>
        snapshot.MemberRelay.TryGetValue(address, out var relay) ? relay : null;

    public bool IsInGroup(IPEndPoint relay) => snapshot.GroupRelays.ContainsKey(relay);

    /// <summary>True while the relay's member list says we also hold one of its ordinary pair slots with a partner (a
    /// phone or an older app), so our audio goes to it in ordinary form as well.</summary>
    public bool IsV1Paired(IPEndPoint relay) => snapshot.GroupRelays.TryGetValue(relay, out var paired) && paired;

    /// <summary>Everyone in every group we are in, us excepted.</summary>
    public IReadOnlyList<Member> Members => snapshot.Members;

    public bool TryGetMember(IPEndPoint address, out Member member)
    {
        foreach (var m in snapshot.Members)
        {
            if (m.Address.Equals(address)) { member = m; return true; }
        }
        member = null!;
        return false;
    }

    /// <summary>True for an address in 240.0.0.0/5, the reserved block member addresses are drawn from.</summary>
    public static bool IsMemberAddress(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        Span<byte> b = stackalloc byte[4];
        return address.TryWriteBytes(b, out _) && b[0] >= 240 && b[0] <= 247;
    }

    // ------- inbound ----------------------------------------------------------------------------------------------------

    /// <summary>An address check arrived from this endpoint: only a relay sends one. If we send to it, join its group.</summary>
    public void NoteRelay(IPEndPoint endpoint)
    {
        bool isNew;
        lock (gate)
        {
            if (!targets.Contains(endpoint)) return;
            isNew = !relays.ContainsKey(endpoint);
            if (isNew) relays[endpoint] = new RelayState(endpoint);
        }
        if (!isNew) return;
        Log?.Invoke($"{endpoint} is a relay — joining the group for our password");
        Tick();
    }

    /// <summary>
    /// A packet arrived on the sending socket. If it is a group packet from a relay we know, put it into ordinary form
    /// in place (<paramref name="length"/> shrinks by the client id) and name the member who sent it.
    /// </summary>
    public RelayInbound HandleInbound(byte[] buffer, ref int length, IPEndPoint remote, out IPEndPoint? source)
    {
        source = null;
        if (length < GroupHeaderSize || buffer[4] != GroupVersion) return RelayInbound.NotGroup;
        if (BinaryPrimitives.ReadInt32LittleEndian(buffer) != RemPacket.Magic) return RelayInbound.NotGroup;
        lock (gate)
        {
            if (!relays.ContainsKey(remote)) return RelayInbound.NotGroup;
        }
        var type = buffer[5];
        if (type == TypeRoster)
        {
            ReadRoster(remote, buffer.AsSpan(GroupHeaderSize, length - GroupHeaderSize));
            return RelayInbound.Consumed;
        }
        if (type == TypeFull)
        {
            var full = length >= GroupHeaderSize + 2 ? $" ({buffer[GroupHeaderSize]} of {buffer[GroupHeaderSize + 1]})" : "";
            Log?.Invoke($"{remote} is full{full}: this device cannot join its group");
            return RelayInbound.Consumed;
        }
        if (type is TypeHello or TypeBye) return RelayInbound.Consumed;

        var idSpan = buffer.AsSpan(RemPacket.HeaderSize, ClientIdSize);
        if (idSpan.SequenceEqual(clientId)) return RelayInbound.Consumed;   // our own packet: never expected, never played
        var id = new Guid(idSpan, bigEndian: true);
        IPEndPoint address;
        bool isNew;
        lock (gate)
        {
            if (!relays.TryGetValue(remote, out var relay)) return RelayInbound.NotGroup;
            isNew = !relay.Members.TryGetValue(id, out var member);
            if (member is null)
            {
                member = new MemberState(id, AddressForLocked(id, remote), remote);
                relay.Members[id] = member;
                memberByAddress[member.Address] = member;
            }
            address = member.Address;
        }
        if (isNew) Publish();

        // Ordinary form: version 1, and the payload moved down over the client id.
        buffer[4] = RemPacket.Version;
        Buffer.BlockCopy(buffer, GroupHeaderSize, buffer, RemPacket.HeaderSize, length - GroupHeaderSize);
        length -= ClientIdSize;
        source = address;
        return RelayInbound.Delivered;
    }

    private void ReadRoster(IPEndPoint relayEndpoint, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1) return;
        var count = payload[0];
        if (payload.Length < 1 + count * RosterEntryBytes) return;
        var flags = payload.Length > 1 + count * RosterEntryBytes ? payload[1 + count * RosterEntryBytes] : (byte)0;
        var listed = new Dictionary<Guid, string>();
        for (var i = 0; i < count; i++)
        {
            var entry = payload.Slice(1 + i * RosterEntryBytes, RosterEntryBytes);
            if (entry[..ClientIdSize].SequenceEqual(clientId)) continue;
            listed[new Guid(entry[..ClientIdSize], bigEndian: true)] = DecodeName(entry[ClientIdSize..]);
        }

        var events = new List<string>();
        bool changed;
        lock (gate)
        {
            if (!relays.TryGetValue(relayEndpoint, out var relay)) return;
            relay.LastRosterUtc = DateTime.UtcNow;
            var paired = (flags & RosterFlagV1Paired) != 0;
            changed = !relay.InGroup || relay.V1Paired != paired;
            if (!relay.InGroup) events.Add($"in a group on {relayEndpoint} with {listed.Count} other{(listed.Count == 1 ? "" : "s")}");
            if (relay.V1Paired != paired) events.Add(paired ? $"a phone or older app on {relayEndpoint} is paired with us: sending it our audio as well" : $"no longer paired with a phone or older app on {relayEndpoint}");
            relay.InGroup = true;
            relay.V1Paired = paired;
            foreach (var (id, name) in listed)
            {
                if (!relay.Members.TryGetValue(id, out var member))
                {
                    member = new MemberState(id, AddressForLocked(id, relayEndpoint), relayEndpoint);
                    relay.Members[id] = member;
                    memberByAddress[member.Address] = member;
                }
                if (!member.Listed || member.Name != name)
                {
                    if (!member.Listed) events.Add($"{Describe(name)} joined the group on {relayEndpoint} (as {member.Address})");
                    else events.Add($"{Describe(member.Name)} on {relayEndpoint} is now called {Describe(name)}");
                    member.Listed = true;
                    member.Name = name;
                    changed = true;
                }
            }
            foreach (var gone in relay.Members.Values.Where(m => !listed.ContainsKey(m.Id)).ToList())
            {
                events.Add($"{Describe(gone.Name)} left the group on {relayEndpoint}");
                relay.Members.Remove(gone.Id);
                memberByAddress.Remove(gone.Address);
                changed = true;
            }
        }
        foreach (var e in events) Log?.Invoke(e);
        if (changed) Publish();
    }

    // ------- outbound ---------------------------------------------------------------------------------------------------

    public bool TryRoute(ReadOnlySpan<byte> packet, IPEndPoint destination, DatagramSender send)
    {
        var snap = snapshot;
        if (snap.MemberRelay.TryGetValue(destination, out var viaRelay))
        {
            // A packet for one member (a pong, a remote-volume command) goes to their relay, and so to the whole group.
            if (snap.GroupRelays.ContainsKey(viaRelay)) SendWrapped(packet, viaRelay, send);
            return true;
        }
        if (IsMemberAddress(destination.Address)) return true;   // a member we no longer know: never onto the wire
        if (!snap.GroupRelays.TryGetValue(destination, out var v1Paired)) return false;
        if (packet.Length < RemPacket.HeaderSize) return false;
        var type = (RemPacketType)packet[5];
        if (type == RemPacketType.AddrCheck) return false;   // the echo goes back exactly as it came
        SendWrapped(packet, destination, send);
        // Heartbeats also go in ordinary form: they claim the pair slot beside a phone that is waiting for one. The rest
        // goes in ordinary form only while we actually have such a partner.
        if (type == RemPacketType.Heartbeat || v1Paired) send(packet, destination);
        return true;
    }

    private void SendWrapped(ReadOnlySpan<byte> packet, IPEndPoint relay, DatagramSender send)
    {
        if (packet.Length < RemPacket.HeaderSize) return;
        var needed = packet.Length + ClientIdSize;
        var buffer = wrapBuffer;
        if (buffer is null || buffer.Length < needed) wrapBuffer = buffer = new byte[Math.Max(needed, 2048)];
        packet[..RemPacket.HeaderSize].CopyTo(buffer);
        buffer[4] = GroupVersion;
        clientId.CopyTo(buffer.AsSpan(RemPacket.HeaderSize));
        packet[RemPacket.HeaderSize..].CopyTo(buffer.AsSpan(GroupHeaderSize));
        send(buffer.AsSpan(0, needed), relay);
    }

    private void Tick()
    {
        var send = rawSend;
        if (send is null) return;
        var now = DateTime.UtcNow;
        var hellos = new List<(IPEndPoint Relay, byte[] Packet)>();
        var events = new List<string>();
        var changed = false;
        lock (gate)
        {
            foreach (var relay in relays.Values)
            {
                if (groupTag is not null && now - relay.LastHelloUtc >= HelloInterval)
                {
                    relay.LastHelloUtc = now;
                    hellos.Add((relay.Endpoint, BuildHelloLocked()));
                }
                if (relay.InGroup && now - relay.LastRosterUtc > GroupAliveFor)
                {
                    relay.InGroup = false;
                    relay.V1Paired = false;
                    changed = true;
                    events.Add($"no member list from {relay.Endpoint} for {GroupAliveFor.TotalSeconds:0} s: sending to it as an ordinary pair again");
                }
            }
        }
        foreach (var e in events) Log?.Invoke(e);
        foreach (var (relay, packet) in hellos)
        {
            try { send(packet, packet.Length, relay); } catch { /* UDP; the next hello tries again */ }
        }
        if (changed) Publish();
    }

    private byte[] BuildHelloLocked()
    {
        var packet = new byte[GroupHeaderSize + NameBytes + GroupTagBytes];
        RemPacket.WriteHeader(packet, (RemPacketType)TypeHello, 0, 0);
        packet[4] = GroupVersion;
        clientId.CopyTo(packet.AsSpan(RemPacket.HeaderSize));
        EncodeName(displayName, packet.AsSpan(GroupHeaderSize, NameBytes));
        groupTag?.CopyTo(packet.AsSpan(GroupHeaderSize + NameBytes));
        return packet;
    }

    private void SendBye(IPEndPoint relay)
    {
        var send = rawSend;
        if (send is null) return;
        var packet = new byte[GroupHeaderSize];
        RemPacket.WriteHeader(packet, (RemPacketType)TypeBye, 0, 0);
        packet[4] = GroupVersion;
        clientId.CopyTo(packet.AsSpan(RemPacket.HeaderSize));
        try { send(packet, packet.Length, relay); } catch { /* UDP; the relay forgets us after a minute anyway */ }
    }

    // ------- bookkeeping ------------------------------------------------------------------------------------------------

    private void ForgetRelayLocked(IPEndPoint endpoint)
    {
        if (!relays.Remove(endpoint, out var relay)) return;
        foreach (var m in relay.Members.Values) memberByAddress.Remove(m.Address);
    }

    /// <summary>
    /// The address a member is known by: 240.0.0.0/5 plus a hash of their client id, so the same person gets the same
    /// address every time (their volume and pan, stored by address, come back with them). Two ids that hash alike get the
    /// next free address.
    /// </summary>
    private IPEndPoint AddressForLocked(Guid id, IPEndPoint relay)
    {
        Span<byte> idBytes = stackalloc byte[ClientIdSize];
        id.TryWriteBytes(idBytes, bigEndian: true, out _);
        var hash = 2166136261u;
        foreach (var b in idBytes) hash = unchecked((hash ^ b) * 16777619u);
        var value = 0xF0000000u | (hash & 0x07FFFFFFu);
        Span<byte> octets = stackalloc byte[4];
        while (true)
        {
            BinaryPrimitives.WriteUInt32BigEndian(octets, value);
            var candidate = new IPEndPoint(new IPAddress(octets), RemPacket.DefaultPort);
            if (!memberByAddress.TryGetValue(candidate, out var holder) || (holder.Id == id && holder.Relay.Equals(relay))) return candidate;
            value = 0xF0000000u | ((value + 1) & 0x07FFFFFFu);
        }
    }

    private void Publish()
    {
        lock (gate)
        {
            var groupRelays = relays.Values.Where(r => r.InGroup).ToFrozenDictionary(r => r.Endpoint, r => r.V1Paired);
            var memberRelay = memberByAddress.ToFrozenDictionary(kv => kv.Key, kv => kv.Value.Relay);
            var members = memberByAddress.Values
                .Select(m => new Member(m.Address, m.Name, m.Relay))
                .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            snapshot = new Snapshot(groupRelays, memberRelay, members);
        }
        try { Changed?.Invoke(); } catch { /* a listener's failure must not break the network path */ }
    }

    private static bool SameTag(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);

    private static string Describe(string name) => string.IsNullOrWhiteSpace(name) ? "someone not yet named" : $"\"{name}\"";

    /// <summary>The relay's name field: UTF-8, cut to 32 bytes on a character boundary, zero-padded.</summary>
    internal static void EncodeName(string name, Span<byte> into)
    {
        into.Clear();
        var text = name;
        while (Encoding.UTF8.GetByteCount(text) > NameBytes) text = text[..^1];
        Encoding.UTF8.GetBytes(text, into);
    }

    internal static string DecodeName(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? field : field[..end]).Trim();
    }
}
