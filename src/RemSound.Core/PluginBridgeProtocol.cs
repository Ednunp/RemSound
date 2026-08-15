using System.Buffers.Binary;
using System.Net;

namespace RemSound.Core;

/// <summary>What a message on the app↔plugin link means.</summary>
public enum PluginBridgeMessage : byte
{
    /// <summary>Plugin → app, on load: "I exist, here is my instance id."</summary>
    Hello = 1,
    /// <summary>Plugin → app, every audio block: "I am receiving this peer onto a track, and I just
    /// consumed N frames — send me the next N." One message doing three jobs: it claims the peer, it
    /// is the heartbeat that keeps the claim alive (see <see cref="PluginPeerClaims"/>), and it is the
    /// request for the next block. Payload is a little-endian int32 frame count.</summary>
    ClaimPeer = 2,
    /// <summary>Plugin → app: "I have let that peer go" — bypassed, removed, or switched to sending.</summary>
    ReleasePeer = 3,
    /// <summary>Plugin → app: a block of this DAW track's audio, to go out to the peers.</summary>
    TrackAudio = 4,
    /// <summary>App → plugin: a block of the claimed peer's audio, to land on the DAW track.</summary>
    PeerAudio = 5,
    /// <summary>Plugin → app, on unload: drop everything I held, immediately.</summary>
    Goodbye = 6,
    /// <summary>App → plugin, in reply to Hello: who this machine can receive, one per line as
    /// "address TAB name". Also the plugin's proof that RemSound is running — no reply means the app
    /// is closed, or the user has switched the plugin off in Preferences, and the plugin says so.</summary>
    PeerList = 7,
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

    /// <summary>The loopback port the app listens on for plugin instances. Fixed so a plugin can find
    /// the app with no discovery step; bound to 127.0.0.1 only, never to a routable address.</summary>
    public const int DefaultPort = 47831;

    /// <summary>Largest audio payload a single message carries. One DAW block of stereo float at a
    /// generous 4096 frames; bigger blocks are split by the caller. Sized so a message always fits a
    /// loopback datagram without fragmentation.</summary>
    public const int MaxAudioBytes = 4096 * 2 * sizeof(float);

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
    /// the wrong magic, a future version, a truncated header, or a length that overruns the buffer.
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
        if (raw is < (byte)PluginBridgeMessage.Hello or > (byte)PluginBridgeMessage.PeerList) return false;
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
}
