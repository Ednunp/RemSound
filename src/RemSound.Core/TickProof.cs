using System.Buffers.Binary;

namespace RemSound.Core;

/// <summary>
/// "I have ticked you, and I hold the password" - packet type 11, <see cref="RemPacketType.TickProof"/> (Ed, 2026-09-25:
/// "anyone who has the password should be prompted or auto accepted ... regardless"). A peer that ticks somebody sends
/// this to them every few seconds, whatever it sends or receives, so "Accept connections" can act on a peer that only
/// listens - a phone ticking a PC to hear it sends no audio, and the audio's format packet was the only thing on the wire
/// that proved the password.
///
/// <para><b>Sealed, not a fingerprint.</b> The plaintext is sealed with the SAME AES-256-GCM key the audio uses (derived
/// from the profile password), exactly as remote-control commands are (<see cref="ControlSealing"/>): only a
/// password-holder can make one, and a captured one cannot be reused - it carries its own time, and the receiver
/// remembers every seal it has accepted (<see cref="TickProofGuard"/>).</para>
///
/// <para><b>Wire layout</b> (for the iPhone and Android ports): the 12-byte RemSound header with type 11, stream id 0xFFFF,
/// then the sealed payload of <see cref="SealedPayloadBytes"/> = 53 bytes: a 12-byte nonce, the 25-byte ciphertext and
/// the 16-byte GCM tag, as <see cref="RemSoundCrypto.Encrypt"/> lays them out. The 25 plaintext bytes are: format version
/// (1 byte, = 1), the sender's UTC time in Unix seconds (8 bytes, little-endian), and the sender's discovery instance id
/// (16 bytes, the GUID in RFC 4122 big-endian order - the same bytes its text form reads left to right). Older peers see
/// an unknown packet type and drop it, as they did Control (5) and AddrCheck (10).</para>
/// </summary>
public static class TickProof
{
    public const byte FormatVersion = 1;

    /// <summary>version(1) + unix-utc-seconds(8) + instance id(16).</summary>
    public const int PlainBytes = 1 + 8 + 16;

    /// <summary>Sealed wire size: nonce + tag over the 25-byte plaintext (= 53).</summary>
    public const int SealedPayloadBytes = PlainBytes + RemSoundCrypto.EncryptionOverheadBytes;

    /// <summary>How often a peer sends it to each peer it has ticked.</summary>
    public static readonly TimeSpan SendInterval = TimeSpan.FromSeconds(5);

    public static byte[] Seal(byte[] key, Guid senderInstanceId, long unixUtcSeconds)
    {
        Span<byte> plain = stackalloc byte[PlainBytes];
        plain[0] = FormatVersion;
        BinaryPrimitives.WriteInt64LittleEndian(plain[1..], unixUtcSeconds);
        senderInstanceId.TryWriteBytes(plain[9..], bigEndian: true, out _);
        return RemSoundCrypto.Encrypt(key, plain);
    }

    /// <summary>The whole packet: header (type 11, stream 0xFFFF) and the sealed payload.</summary>
    public static byte[] BuildPacket(byte[] key, Guid senderInstanceId, long unixUtcSeconds, uint sequence)
    {
        var sealedPayload = Seal(key, senderInstanceId, unixUtcSeconds);
        var packet = new byte[RemPacket.HeaderSize + sealedPayload.Length];
        RemPacket.WriteHeader(packet, RemPacketType.TickProof, 0xFFFF, sequence);
        sealedPayload.CopyTo(packet.AsSpan(RemPacket.HeaderSize));
        return packet;
    }

    /// <summary>Open a sealed proof. False when the size is wrong, the tag fails (another password, or tampered), or the
    /// format version is one this build doesn't know.</summary>
    public static bool TryUnseal(byte[] key, ReadOnlySpan<byte> payload, out Guid senderInstanceId, out long unixUtcSeconds, out ulong nonceId)
    {
        senderInstanceId = Guid.Empty;
        unixUtcSeconds = 0;
        nonceId = 0;
        if (payload.Length != SealedPayloadBytes) return false;
        if (!RemSoundCrypto.TryDecrypt(key, payload, out var plain) || plain.Length != PlainBytes) return false;
        if (plain[0] != FormatVersion) return false;
        unixUtcSeconds = BinaryPrimitives.ReadInt64LittleEndian(plain.AsSpan(1));
        senderInstanceId = new Guid(plain.AsSpan(9, 16), bigEndian: true);
        // The nonce is random per seal, so its first 8 bytes identify this packet for the replay memory.
        nonceId = BinaryPrimitives.ReadUInt64LittleEndian(payload[..8]);
        return true;
    }
}

/// <summary>Receiver-side gate for tick proofs: authenticates with the key, bounds staleness against clock skew, and never
/// accepts the same seal twice - so a captured proof, replayed from another address, proves nothing. One per app; call
/// from one thread (the window's).</summary>
public sealed class TickProofGuard(TimeSpan? maxSkew = null)
{
    /// <summary>Generous, as for remote control: peers' clocks are not synchronised.</summary>
    public static readonly TimeSpan DefaultMaxSkew = TimeSpan.FromMinutes(10);

    private readonly TimeSpan maxSkew = maxSkew ?? DefaultMaxSkew;
    private readonly Dictionary<ulong, DateTime> seenNonces = [];

    public bool TryAccept(byte[] key, ReadOnlySpan<byte> payload, DateTime utcNow, out Guid senderInstanceId, out string rejectReason)
    {
        if (!TickProof.TryUnseal(key, payload, out senderInstanceId, out var ts, out var nonceId))
        {
            rejectReason = "not our password";
            return false;
        }
        var age = utcNow - DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime;
        if (age > maxSkew || age < -maxSkew)
        {
            rejectReason = $"stale (sent {Math.Abs(age.TotalMinutes):0.0} min from now, window {maxSkew.TotalMinutes:0} min)";
            return false;
        }
        if (seenNonces.ContainsKey(nonceId))
        {
            rejectReason = "replay (this proof was already used)";
            return false;
        }
        // A peer sends one every few seconds for as long as it has us ticked, so the memory is pruned of anything older
        // than the window, and hard-capped against a flood.
        if (seenNonces.Count >= 4096)
        {
            foreach (var stale in seenNonces.Where(kv => utcNow - kv.Value > maxSkew + maxSkew).Select(kv => kv.Key).ToList())
                seenNonces.Remove(stale);
            if (seenNonces.Count >= 4096) seenNonces.Clear();
        }
        seenNonces[nonceId] = utcNow;
        rejectReason = "";
        return true;
    }
}
