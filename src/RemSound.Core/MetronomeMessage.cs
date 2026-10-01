using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RemSound.Core;

/// <summary>What a metronome message says. The numbers are on the wire.</summary>
public enum MetronomeKind : byte
{
    /// <summary>Master to everyone connected: "may I be the master metronome?", with the master's name.</summary>
    Offer = 1,
    /// <summary>Follower to master: yes.</summary>
    Accept = 2,
    /// <summary>Follower to master: no.</summary>
    Decline = 3,
    /// <summary>Master to followers: I am no longer the master.</summary>
    Release = 4,
    /// <summary>Master to followers: the timeline.</summary>
    State = 5,
    /// <summary>Follower to master: a clock reading, stamped t0.</summary>
    SyncRequest = 6,
    /// <summary>Master to follower: the answer, with t1 (read) and t2 (sent).</summary>
    SyncReply = 7,
    /// <summary>Follower to master: I have stopped following.</summary>
    Leave = 8,
}

/// <summary>One metronome message, opened. Only the fields its <see cref="Kind"/> carries are meaningful.</summary>
public sealed record MetronomeMessage(MetronomeKind Kind, ulong SessionId)
{
    public string MasterName { get; init; } = "";
    public MetronomeTimeline? State { get; init; }
    public uint Seq { get; init; }
    public long T0 { get; init; }
    public long T1 { get; init; }
    public long T2 { get; init; }

    public override string ToString() => Kind switch
    {
        MetronomeKind.Offer => $"offer from \"{MasterName}\" (session {SessionId:x16})",
        MetronomeKind.State => $"state {State?.Describe()} (session {SessionId:x16})",
        MetronomeKind.SyncRequest => $"sync request {Seq}",
        MetronomeKind.SyncReply => $"sync reply {Seq}",
        _ => $"{Kind.ToString().ToLowerInvariant()} (session {SessionId:x16})",
    };
}

/// <summary>
/// THE METRONOME ON THE WIRE - packet type 12, <see cref="RemPacketType.Metronome"/> (Ed, 2026-09-28: a master metronome
/// everyone else's follows, each playing its own click sound). Only timing travels; no sound ever does.
///
/// <para><b>Sealed</b> with the SAME AES-256-GCM key the audio uses (derived from the profile password), exactly as
/// <see cref="TickProof"/> and remote control are, so only a password-holder can make one; and the receiver refuses any
/// seal it has already accepted (<see cref="MetronomeReplayGuard"/>).</para>
///
/// <para><b>Wire layout</b> (for the iPhone, Android and Mac ports): the 12-byte RemSound header with type 12, stream id
/// 0xFFFF, then the sealed payload as <see cref="RemSoundCrypto.Encrypt"/> lays it out - a 12-byte nonce, the 16-byte GCM
/// tag, then the ciphertext (the tag BEFORE the ciphertext, as every RemSound packet has it). The plaintext, little-endian
/// throughout:
/// <list type="bullet">
/// <item>version (1 byte, = 1), kind (1 byte, <see cref="MetronomeKind"/>), the master's session id (8 bytes, random, new
/// each time someone becomes master);</item>
/// <item>Offer: the master's display name - a length byte then that many bytes of UTF-8, at most 64;</item>
/// <item>Accept, Decline, Release, Leave: nothing more;</item>
/// <item>State: version (u32), running (u8, 0 or 1), tempo in milli-beats a minute (u32), beats per bar (u8), beat unit
/// (u8), anchor beat (i64), anchor time in microseconds on the master's clock (i64) - beat n is at
/// anchorTime + (n - anchorBeat) * 60e9 / milliBpm, the anchor is a bar line, and the first beat of each bar is accented;</item>
/// <item>SyncRequest: sequence (u32), t0 (i64, the follower's clock, microseconds);</item>
/// <item>SyncReply: sequence (u32), t0 (i64, echoed), t1 (i64, the master's clock as the request was read), t2 (i64, the
/// master's clock just before the reply was sent).</item>
/// </list>
/// A reader takes the least each kind needs and ignores anything after it. Older peers see an unknown packet type and drop
/// it, as they did Control (5), AddrCheck (10) and TickProof (11) - and a master only ever sends them the Offer, since
/// everything else goes to peers that answered it.</para>
/// </summary>
public static class MetronomeWire
{
    public const byte FormatVersion = 1;
    public const int MaxNameBytes = 64;
    /// <summary>version + kind + session id.</summary>
    public const int CommonBytes = 1 + 1 + 8;
    public const int StateBytes = CommonBytes + 4 + 1 + 4 + 1 + 1 + 8 + 8;
    public const int SyncRequestBytes = CommonBytes + 4 + 8;
    public const int SyncReplyBytes = CommonBytes + 4 + 8 + 8 + 8;
    /// <summary>The smallest and largest sealed payload any kind makes - the receiver's cheap size gate.</summary>
    public const int MinSealedBytes = CommonBytes + RemSoundCrypto.EncryptionOverheadBytes;
    public const int MaxSealedBytes = CommonBytes + 1 + MaxNameBytes + RemSoundCrypto.EncryptionOverheadBytes;

    /// <summary>A new master's session id: 8 random bytes.</summary>
    public static ulong NewSessionId() => BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8));

    /// <summary>A display name cut to <see cref="MaxNameBytes"/> of UTF-8 without splitting a character.</summary>
    public static byte[] NameBytes(string? name)
    {
        var bytes = Encoding.UTF8.GetBytes(name ?? "");
        if (bytes.Length <= MaxNameBytes) return bytes;
        var cut = MaxNameBytes;
        while (cut > 0 && (bytes[cut] & 0xC0) == 0x80) cut--;
        return bytes[..cut];
    }

    public static byte[] Plaintext(MetronomeMessage m)
    {
        var name = m.Kind == MetronomeKind.Offer ? NameBytes(m.MasterName) : [];
        var length = m.Kind switch
        {
            MetronomeKind.Offer => CommonBytes + 1 + name.Length,
            MetronomeKind.State => StateBytes,
            MetronomeKind.SyncRequest => SyncRequestBytes,
            MetronomeKind.SyncReply => SyncReplyBytes,
            _ => CommonBytes,
        };
        var plain = new byte[length];
        plain[0] = FormatVersion;
        plain[1] = (byte)m.Kind;
        BinaryPrimitives.WriteUInt64LittleEndian(plain.AsSpan(2), m.SessionId);
        var p = plain.AsSpan(CommonBytes);
        switch (m.Kind)
        {
            case MetronomeKind.Offer:
                p[0] = (byte)name.Length;
                name.CopyTo(p[1..]);
                break;
            case MetronomeKind.State:
                var s = m.State ?? throw new ArgumentException("a State message needs its timeline", nameof(m));
                BinaryPrimitives.WriteUInt32LittleEndian(p, s.Version);
                p[4] = s.Running ? (byte)1 : (byte)0;
                BinaryPrimitives.WriteUInt32LittleEndian(p[5..], s.MilliBpm);
                p[9] = s.BeatsPerBar;
                p[10] = s.BeatUnit;
                BinaryPrimitives.WriteInt64LittleEndian(p[11..], s.AnchorBeat);
                BinaryPrimitives.WriteInt64LittleEndian(p[19..], s.AnchorTimeUs);
                break;
            case MetronomeKind.SyncRequest:
                BinaryPrimitives.WriteUInt32LittleEndian(p, m.Seq);
                BinaryPrimitives.WriteInt64LittleEndian(p[4..], m.T0);
                break;
            case MetronomeKind.SyncReply:
                BinaryPrimitives.WriteUInt32LittleEndian(p, m.Seq);
                BinaryPrimitives.WriteInt64LittleEndian(p[4..], m.T0);
                BinaryPrimitives.WriteInt64LittleEndian(p[12..], m.T1);
                BinaryPrimitives.WriteInt64LittleEndian(p[20..], m.T2);
                break;
        }
        return plain;
    }

    /// <summary>Read an opened plaintext. False for another version, an unknown kind, or too few bytes.</summary>
    public static bool TryParse(ReadOnlySpan<byte> plain, out MetronomeMessage? message)
    {
        message = null;
        if (plain.Length < CommonBytes || plain[0] != FormatVersion) return false;
        var kind = (MetronomeKind)plain[1];
        var session = BinaryPrimitives.ReadUInt64LittleEndian(plain[2..]);
        var p = plain[CommonBytes..];
        switch (kind)
        {
            case MetronomeKind.Offer:
                if (p.Length < 1 || p[0] > MaxNameBytes || p.Length < 1 + p[0]) return false;
                message = new MetronomeMessage(kind, session) { MasterName = Encoding.UTF8.GetString(p.Slice(1, p[0])) };
                return true;
            case MetronomeKind.Accept or MetronomeKind.Decline or MetronomeKind.Release or MetronomeKind.Leave:
                message = new MetronomeMessage(kind, session);
                return true;
            case MetronomeKind.State:
                if (plain.Length < StateBytes || p[4] > 1) return false;
                message = new MetronomeMessage(kind, session)
                {
                    State = new MetronomeTimeline(BinaryPrimitives.ReadUInt32LittleEndian(p), p[4] == 1, BinaryPrimitives.ReadUInt32LittleEndian(p[5..]),
                        p[9], p[10], BinaryPrimitives.ReadInt64LittleEndian(p[11..]), BinaryPrimitives.ReadInt64LittleEndian(p[19..])),
                };
                return true;
            case MetronomeKind.SyncRequest:
                if (plain.Length < SyncRequestBytes) return false;
                message = new MetronomeMessage(kind, session) { Seq = BinaryPrimitives.ReadUInt32LittleEndian(p), T0 = BinaryPrimitives.ReadInt64LittleEndian(p[4..]) };
                return true;
            case MetronomeKind.SyncReply:
                if (plain.Length < SyncReplyBytes) return false;
                message = new MetronomeMessage(kind, session)
                {
                    Seq = BinaryPrimitives.ReadUInt32LittleEndian(p),
                    T0 = BinaryPrimitives.ReadInt64LittleEndian(p[4..]),
                    T1 = BinaryPrimitives.ReadInt64LittleEndian(p[12..]),
                    T2 = BinaryPrimitives.ReadInt64LittleEndian(p[20..]),
                };
                return true;
            default:
                return false;
        }
    }

    /// <summary>The whole packet: header (type 12, stream 0xFFFF) and the sealed payload.</summary>
    public static byte[] BuildPacket(byte[] key, MetronomeMessage message, uint sequence)
    {
        var sealedPayload = RemSoundCrypto.Encrypt(key, Plaintext(message));
        var packet = new byte[RemPacket.HeaderSize + sealedPayload.Length];
        RemPacket.WriteHeader(packet, RemPacketType.Metronome, 0xFFFF, sequence);
        sealedPayload.CopyTo(packet.AsSpan(RemPacket.HeaderSize));
        return packet;
    }

    /// <summary>Open a sealed payload. False when the size is out of range, the tag fails (another password, or tampered),
    /// or what is inside is not a message this build reads.</summary>
    public static bool TryOpen(byte[] key, ReadOnlySpan<byte> payload, out MetronomeMessage? message, out ulong nonceId)
    {
        message = null;
        nonceId = 0;
        if (payload.Length < MinSealedBytes || payload.Length > MaxSealedBytes + 64) return false;
        if (!RemSoundCrypto.TryDecrypt(key, payload, out var plain)) return false;
        if (!TryParse(plain, out message)) return false;
        // The nonce is random per seal, so its first 8 bytes identify this packet for the replay memory.
        nonceId = BinaryPrimitives.ReadUInt64LittleEndian(payload[..8]);
        return true;
    }
}

/// <summary>Receiver-side gate for metronome messages: authenticates with the key and never accepts the same seal twice, so
/// a captured Offer or State replayed later - from anywhere - does nothing. The memory keeps the most recent seals (oldest
/// forgotten first), which outlasts any session: a State carries its own version and a SyncReply must answer a request
/// still outstanding, so even a seal old enough to be forgotten changes nothing. Thread-safe.</summary>
public sealed class MetronomeReplayGuard(int capacity = 16384)
{
    private readonly HashSet<ulong> seen = [];
    private readonly Queue<ulong> order = new();
    private readonly object gate = new();

    public bool TryAccept(byte[] key, ReadOnlySpan<byte> payload, out MetronomeMessage? message, out string rejectReason)
    {
        if (!MetronomeWire.TryOpen(key, payload, out message, out var nonceId))
        {
            rejectReason = "not our password, or not a message this build reads";
            return false;
        }
        lock (gate)
        {
            if (!seen.Add(nonceId))
            {
                message = null;
                rejectReason = "replay (this seal was already used)";
                return false;
            }
            order.Enqueue(nonceId);
            while (order.Count > capacity) seen.Remove(order.Dequeue());
        }
        rejectReason = "";
        return true;
    }
}
