using System.Buffers.Binary;
using System.Net;

namespace RemSound.Core;

/// <summary>What a message on the app↔plugin link means.</summary>
public enum PluginBridgeMessage : byte
{
    /// <summary>Plugin → app, on load: "I exist, here is my instance id."</summary>
    Hello = 1,
    // 2 and 5 were the one-peer claim and its reply, spoken only by the plugin's test builds before 6.0. Retired
    // 2026-09-13 and never to be reused, so a stray old test plugin cannot be taken for a current one.
    /// <summary>Plugin → app: "I have let that peer go" — bypassed, removed, or switched to sending.</summary>
    ReleasePeer = 3,
    /// <summary>Plugin → app: a block of this DAW track's audio, to go out to the peers.</summary>
    TrackAudio = 4,
    /// <summary>Plugin → app, on unload: drop everything I held, immediately.</summary>
    Goodbye = 6,
    /// <summary>App → plugin, in reply to Hello: who this machine can receive, one per line as
    /// "address TAB name". Also the plugin's proof that RemSound is running — no reply means the app
    /// is closed, or the link is switched off in RemSound's DAW plugin menu, and the plugin says so.</summary>
    PeerList = 7,
    /// <summary>Plugin → app, every audio block, one message doing three jobs: it claims the peers on this
    /// track, it is the heartbeat that keeps the claim alive (see <see cref="PluginPeerClaims"/>), and it is
    /// the request for the next block. Payload is a little-endian int32 frame count, then a byte count, then
    /// that many 4-byte IPv4 addresses, then the int32 ask number.
    ///
    /// <para><b>Why the app sums rather than the plugin.</b> One track can carry several people (a
    /// conversation you want to hear over your own session), and the naive way to do it — one request
    /// and one reply per peer — multiplies the loopback traffic and the bridge thread's work by the
    /// number of people on the track. The app already has each peer decoded and shaped, so it reads
    /// them all and returns ONE mixed block, and the plugin's audio path is unchanged: one reply per
    /// block, one resampler. The cost is that a per-peer trim is not available in the plugin;
    /// RemSound's own per-peer volume, pan and EQ apply to what the plugin gets (they run inside the
    /// session read this path calls), so that control already exists where the user already knows
    /// it.</para></summary>
    ClaimPeers = 8,
    /// <summary>App → plugin: a block of the claimed peers' audio,
    /// stamped with WHICH DAW BLOCK it is for. Payload is a little-endian int64 round number, then the
    /// int32 ask number the plugin sent with its request, then the int32 offset of this part and the int32
    /// length of the whole block (both in floats), then this part's interleaved float samples. A block
    /// that fits one message is one part; a bigger one - a DAW buffer of 4096 or more - comes in as many
    /// parts as it takes, in order, and the plugin puts them back together (review 2026-09-25: those
    /// buffers used to go silent, because the whole block could never fit one message).
    ///
    /// <para>The app numbers the blocks of each DAW: every instance in one host process asking once is
    /// one round, and an instance asking again is the next. So two tracks that ask in the same DAW
    /// block get the same round number, and the plugin can play "the round this block's ask will get,
    /// minus a fixed lead" instead of "the oldest reply in a queue" — which is what put two tracks on
    /// the same peer a few blocks apart, by however many replies had piled up in each queue at start.</para></summary>
    PeerAudioRound = 9,
}

/// <summary>
/// The private link between the RemSound app and a VST plugin instance on the same machine.
///
/// <para><b>Why a link at all.</b> The plugin runs inside the DAW's process, not the app's, so they
/// cannot share a socket or a peer list by simply existing. Ed chose the architecture where the app
/// keeps the ONE connection and hands audio to and from plugin instances, precisely because the
/// alternative (each plugin its own client) cannot solve the double-audio problem: nothing would
/// know to stop a claimed peer coming out of the speakers as well. "Or else it's going to be damn
/// confusing."</para>
///
/// <para><b>No encryption here, deliberately.</b> This link never leaves the machine — it is bound to
/// loopback only. The audio it carries is already decrypted (it has arrived and been unsealed) or
/// about to be encrypted by the sender. Adding a second crypto layer would buy nothing against an
/// attacker who is already running code as this user, and would cost latency on the hot path. The
/// network-facing protocol keeps its encryption; this is a different thing with a different threat
/// model, and saying so plainly here is safer than leaving it to be rediscovered as an oversight.</para>
///
/// <para><b>Framing.</b> Fixed 16-byte header, then payload. Deliberately NOT RemPacket: that format
/// carries wire concerns (sequence numbers, crypto nonces, a fingerprint) which are meaningless
/// locally, and reusing it would tempt someone to route a bridge message onto the network or accept
/// a network packet on the bridge. Separate purposes, separate formats.</para>
/// </summary>
public static class PluginBridgeProtocol
{
    /// <summary>Marks a bridge message. Different from the network magic ("RMND") ON PURPOSE, so a
    /// bridge message can never be mistaken for a peer packet or vice versa, even if a port were
    /// misconfigured — a local message escaping onto the network would be an audio leak.</summary>
    public static ReadOnlySpan<byte> Magic => "RSBR"u8;

    public const int HeaderSize = 16;
    public const byte Version = 1;

    /// <summary>A line in the peer list saying RemSound's "Receive audio" is off, so there is nobody to receive.</summary>
    public const string ReceiveOffLine = "#receive-off";

    /// <summary>The loopback port the app listens on for plugin instances. Fixed so a plugin can find
    /// the app with no discovery step; bound to 127.0.0.1 only, never to a routable address.</summary>
    public const int DefaultPort = 47831;

    /// <summary>Largest audio payload a single message carries: 4096 frames of stereo float. Sized so a
    /// message always fits a loopback datagram without fragmentation. A bigger block goes as several
    /// messages: in parts on the way to the plugin (see <see cref="PluginBridgeMessage.PeerAudioRound"/>),
    /// and as consecutive track messages on the way to the app, which reads them as one stream.</summary>
    public const int MaxAudioBytes = 4096 * 2 * sizeof(float);

    /// <summary>The largest DAW block the link carries, in frames at <see cref="WireSampleRate"/>, however
    /// many messages it takes: past any buffer a DAW offers (8192 at most in the ones we know), with room
    /// for a 44.1 kHz host's block growing when it is converted to 48 kHz.</summary>
    public const int MaxBlockFrames = 16384;

    /// <summary>What audio on this link is: interleaved stereo 32-bit float at this rate. The DAW may
    /// well be running at 44.1 or 96 kHz, so the PLUGIN resamples at its own boundary rather than
    /// letting a rate mismatch travel — SonoBus has an open bug where exactly that transposes the
    /// audio, and pinning the rate here is what stops us inheriting it.</summary>
    public const int WireSampleRate = 48000;
    public const int WireChannels = 2;

    /// <summary>
    /// Header layout, 16 bytes:
    ///   0..3  magic "RSBR"
    ///   4     version
    ///   5     message type
    ///   6..7  payload length (ushort, little-endian)
    ///   8..11 instance id hash — which plugin instance this concerns
    ///   12..15 peer address (IPv4), or zero when the message is not about a peer
    /// </summary>
    public static int WriteHeader(Span<byte> destination, PluginBridgeMessage type, int instanceHash, IPAddress? peer, int payloadLength)
    {
        if (destination.Length < HeaderSize) throw new ArgumentException("Bridge header does not fit", nameof(destination));
        if (payloadLength is < 0 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(payloadLength));
        Magic.CopyTo(destination);
        destination[4] = Version;
        destination[5] = (byte)type;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], (ushort)payloadLength);
        BinaryPrimitives.WriteInt32LittleEndian(destination[8..], instanceHash);
        WritePeer(destination[12..], peer);
        return HeaderSize;
    }

    private static void WritePeer(Span<byte> destination, IPAddress? peer)
    {
        destination[..4].Clear();
        if (peer is null) return;
        Span<byte> bytes = stackalloc byte[16];
        if (peer.TryWriteBytes(bytes, out var written) && written == 4) bytes[..4].CopyTo(destination);
    }

    /// <summary>Parse a bridge message. Returns false — never throws — on anything that isn't one:
    /// the wrong magic, a future version, a message type this build does not speak, a truncated header, or a
    /// length that overruns the buffer.
    /// A local socket still receives whatever the OS hands it, and a malformed datagram must not be
    /// able to take down a DAW.</summary>
    public static bool TryReadHeader(
        ReadOnlySpan<byte> packet, out PluginBridgeMessage type, out int instanceHash, out IPAddress? peer, out int payloadLength)
    {
        type = default;
        instanceHash = 0;
        peer = null;
        payloadLength = 0;
        if (packet.Length < HeaderSize) return false;
        if (!packet[..4].SequenceEqual(Magic)) return false;
        if (packet[4] != Version) return false;

        var raw = packet[5];
        // Only the types this build speaks. 2 and 5 sit inside the range but went with the plugin's test builds.
        if ((PluginBridgeMessage)raw is not (PluginBridgeMessage.Hello or PluginBridgeMessage.ReleasePeer or PluginBridgeMessage.TrackAudio
            or PluginBridgeMessage.Goodbye or PluginBridgeMessage.PeerList or PluginBridgeMessage.ClaimPeers or PluginBridgeMessage.PeerAudioRound))
            return false;
        type = (PluginBridgeMessage)raw;

        payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(packet[6..]);
        if (HeaderSize + payloadLength > packet.Length) return false;
        if (payloadLength > MaxAudioBytes) return false;

        instanceHash = BinaryPrimitives.ReadInt32LittleEndian(packet[8..]);
        var addr = packet.Slice(12, 4);
        if (addr[0] != 0 || addr[1] != 0 || addr[2] != 0 || addr[3] != 0) peer = new IPAddress(addr);
        return true;
    }

    /// <summary>A stable 32-bit id for a plugin instance, so the header stays small. A Guid would
    /// cost 16 bytes per message on the hot path for no benefit locally.</summary>
    public static int InstanceHash(Guid instanceId) => instanceId.GetHashCode();

    // ---- The claim set ---------------------------------------------------------------------------

    /// <summary>How many peers one plugin instance may put on one track. Not a musical judgement: it
    /// bounds the request datagram and the per-block work on the bridge thread, and it is far past
    /// anything a session would do. A set longer than this is truncated rather than refused, because
    /// dropping the whole request would silence the track.</summary>
    public const int MaxClaimedPeers = 32;

    /// <summary>Bytes a claim-set payload needs for <paramref name="peerCount"/> peers.</summary>
    public static int ClaimSetSize(int peerCount) => sizeof(int) + 1 + peerCount * 4;

    /// <summary>The same with the trailing ask number (see <see cref="PluginBridgeMessage.PeerAudioRound"/>).</summary>
    public static int ClaimSetSizeWithAsk(int peerCount) => ClaimSetSize(peerCount) + sizeof(int);

    /// <summary>Header of a <see cref="PluginBridgeMessage.PeerAudioRound"/> payload: the round, the ask number, then this
    /// part's offset and the whole block's length, in floats.</summary>
    public const int AudioRoundHeaderSize = sizeof(long) + 3 * sizeof(int);

    /// <summary>The most samples one part carries, in floats: whole frames, and a message that fits <see cref="MaxAudioBytes"/>.</summary>
    public const int AudioRoundPartFloats = (MaxAudioBytes - AudioRoundHeaderSize) / sizeof(float) / WireChannels * WireChannels;

    /// <summary>Write the round, ask number, part offset and block length in front of a part's samples.</summary>
    public static void WriteAudioRoundHeader(Span<byte> destination, long round, int askNumber, int offsetFloats, int totalFloats)
    {
        BinaryPrimitives.WriteInt64LittleEndian(destination, round);
        BinaryPrimitives.WriteInt32LittleEndian(destination[sizeof(long)..], askNumber);
        BinaryPrimitives.WriteInt32LittleEndian(destination[(sizeof(long) + sizeof(int))..], offsetFloats);
        BinaryPrimitives.WriteInt32LittleEndian(destination[(sizeof(long) + 2 * sizeof(int))..], totalFloats);
    }

    /// <summary>Read them back; the part's samples follow. False for anything that is not a sane part of a block.</summary>
    public static bool TryReadAudioRoundHeader(ReadOnlySpan<byte> payload, out long round, out int askNumber, out int offsetFloats, out int totalFloats)
    {
        round = 0;
        askNumber = -1;
        offsetFloats = 0;
        totalFloats = 0;
        if (payload.Length < AudioRoundHeaderSize) return false;
        round = BinaryPrimitives.ReadInt64LittleEndian(payload);
        askNumber = BinaryPrimitives.ReadInt32LittleEndian(payload[sizeof(long)..]);
        offsetFloats = BinaryPrimitives.ReadInt32LittleEndian(payload[(sizeof(long) + sizeof(int))..]);
        totalFloats = BinaryPrimitives.ReadInt32LittleEndian(payload[(sizeof(long) + 2 * sizeof(int))..]);
        return totalFloats > 0 && totalFloats <= MaxBlockFrames * WireChannels && offsetFloats >= 0 && offsetFloats < totalFloats;
    }

    /// <summary>Write "I want these people, and I just consumed this many frames". IPv4 only, which is
    /// what this link has always carried — the header's own peer field is four bytes.</summary>
    /// <returns>Bytes written.</returns>
    private static int WriteClaimSet(Span<byte> destination, int frames, ReadOnlySpan<IPAddress> peers)
    {
        var count = Math.Min(peers.Length, MaxClaimedPeers);
        if (destination.Length < ClaimSetSize(count)) throw new ArgumentException("Claim set does not fit", nameof(destination));
        BinaryPrimitives.WriteInt32LittleEndian(destination, frames);
        var written = 0;
        Span<byte> scratch = stackalloc byte[16];
        for (var i = 0; i < count; i++)
        {
            if (!peers[i].TryWriteBytes(scratch, out var length) || length != 4) continue;   // IPv6: not on this link
            scratch[..4].CopyTo(destination[(sizeof(int) + 1 + written * 4)..]);
            written++;
        }
        destination[sizeof(int)] = (byte)written;
        return ClaimSetSize(written);
    }

    /// <summary>The request a plugin sends: the claim set with the plugin's ask number after the addresses,
    /// so the reply can say which ask it answers (see <see cref="PluginBridgeMessage.PeerAudioRound"/>).</summary>
    public static int WriteClaimSet(Span<byte> destination, int frames, ReadOnlySpan<IPAddress> peers, int askNumber)
    {
        var length = WriteClaimSet(destination, frames, peers);
        if (destination.Length < length + sizeof(int)) throw new ArgumentException("Claim set does not fit", nameof(destination));
        BinaryPrimitives.WriteInt32LittleEndian(destination[length..], askNumber);
        return length + sizeof(int);
    }

    /// <summary>Read a claim set back. The addresses are handed over as RAW BYTES rather than
    /// <see cref="IPAddress"/> objects on purpose: this runs once per audio block per instance, the
    /// set is identical on all but a handful of those blocks, and the caller can compare the bytes
    /// against what it already holds and allocate nothing at all in the normal case.</summary>
    private static bool TryReadClaimSet(ReadOnlySpan<byte> payload, out int frames, out ReadOnlySpan<byte> addresses)
    {
        frames = 0;
        addresses = default;
        if (payload.Length < sizeof(int) + 1) return false;
        frames = BinaryPrimitives.ReadInt32LittleEndian(payload);
        int count = payload[sizeof(int)];
        if (count > MaxClaimedPeers) return false;
        if (payload.Length < ClaimSetSize(count)) return false;
        addresses = payload.Slice(sizeof(int) + 1, count * 4);
        return true;
    }

    /// <summary>Read a claim set and its ask number. A request without the number is refused: only the
    /// plugin's test builds before 6.0 sent one, and they are not supported (Ed, 2026-09-13).</summary>
    public static bool TryReadClaimSet(ReadOnlySpan<byte> payload, out int frames, out ReadOnlySpan<byte> addresses, out int askNumber)
    {
        askNumber = -1;
        if (!TryReadClaimSet(payload, out frames, out addresses)) return false;
        var tail = ClaimSetSize(addresses.Length / 4);
        if (payload.Length < tail + sizeof(int)) return false;
        askNumber = BinaryPrimitives.ReadInt32LittleEndian(payload[tail..]);
        return true;
    }
}
