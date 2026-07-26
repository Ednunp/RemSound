using System.Buffers.Binary;

namespace RemSound.Core;

/// <summary>
/// Authenticated remote-control commands (2026-07-26 security audit, finding: Control packets were
/// the ONE thing on the wire that wasn't cryptographically protected — a 2-byte plaintext payload
/// gated only by a forgeable source-IP check. Anyone who learned an allowed peer's address could
/// spoof it and drive the receiving machine's SYSTEM volume/mute — and muting a blind user's
/// machine mutes their screen reader).
///
/// Control payloads are now sealed with the SAME AES-256-GCM key the audio uses (derived from the
/// profile password), so only a password-holder can issue one. The sealed plaintext carries the
/// command plus the sender's UTC timestamp; the receiving side (<see cref="ControlReceiveGuard"/>)
/// rejects anything outside a generous clock-skew window and remembers recent nonces inside it, so
/// a captured packet can't be replayed later to re-toggle mute. Legacy plaintext control packets
/// no longer verify and are dropped — peers on older builds must update before remote volume works
/// again (called out in the release notes).
/// </summary>
public static class ControlSealing
{
    /// <summary>kind(1) + delta(1) + unix-utc-seconds(8).</summary>
    private const int PlainBytes = 10;

    /// <summary>Sealed wire size: nonce+tag overhead over the 10-byte plaintext (= 38). The receiver
    /// uses this to cheaply distinguish a sealed payload from legacy 2-byte plaintext.</summary>
    public const int SealedPayloadBytes = PlainBytes + RemSoundCrypto.EncryptionOverheadBytes;

    /// <summary>Seal a control command. <paramref name="key"/> is the profile's audio key — no key,
    /// no remote control (same rule as audio: encryption is mandatory).</summary>
    public static byte[] Seal(byte[] key, RemoteControlKind kind, sbyte delta, long unixUtcSeconds)
    {
        Span<byte> plain = stackalloc byte[PlainBytes];
        plain[0] = (byte)kind;
        plain[1] = unchecked((byte)delta);
        BinaryPrimitives.WriteInt64LittleEndian(plain[2..], unixUtcSeconds);
        return RemSoundCrypto.Encrypt(key, plain);
    }

    /// <summary>Open a sealed control payload. False when the size is wrong, the auth tag fails
    /// (wrong password / tampered / legacy plaintext), or the command byte isn't a known kind.</summary>
    public static bool TryUnseal(byte[] key, ReadOnlySpan<byte> payload,
        out RemoteControlKind kind, out sbyte delta, out long unixUtcSeconds, out ulong nonceId)
    {
        kind = RemoteControlKind.VolumeUp;
        delta = 0;
        unixUtcSeconds = 0;
        nonceId = 0;
        if (payload.Length != SealedPayloadBytes) return false;
        if (!RemSoundCrypto.TryDecrypt(key, payload, out var plain) || plain.Length != PlainBytes) return false;
        if (!RemPacket.TryReadControl(plain, out kind, out delta)) return false;
        unixUtcSeconds = BinaryPrimitives.ReadInt64LittleEndian(plain.AsSpan(2));
        // First 8 nonce bytes as the replay id — the nonce is random per seal, so this is unique
        // per packet for the guard's purposes (a collision just double-rejects, never double-accepts).
        nonceId = BinaryPrimitives.ReadUInt64LittleEndian(payload[..8]);
        return true;
    }
}

/// <summary>Receiver-side gate for sealed control commands: authenticates (via the key), bounds
/// staleness against clock skew, and blocks replays of captured packets inside the window. One
/// instance per receiving app; not thread-safe (call from one thread — the UI marshal point).</summary>
public sealed class ControlReceiveGuard(TimeSpan? maxSkew = null)
{
    /// <summary>Generous on purpose: peers' clocks are not synchronised, and a rejected volume
    /// command because someone's PC clock drifted eight minutes would be a miserable support case.
    /// Within the window, the nonce memory below blocks replays; outside it, staleness rejects.</summary>
    public static readonly TimeSpan DefaultMaxSkew = TimeSpan.FromMinutes(10);

    private readonly TimeSpan maxSkew = maxSkew ?? DefaultMaxSkew;
    private readonly Dictionary<ulong, DateTime> seenNonces = [];

    /// <summary>Authenticate + accept a sealed control payload. On false, <paramref name="rejectReason"/>
    /// says why in one word (for the log line): not-sealed / stale / replay.</summary>
    public bool TryAccept(byte[] key, ReadOnlySpan<byte> payload, DateTime utcNow,
        out RemoteControlKind kind, out sbyte delta, out string rejectReason)
    {
        delta = 0;
        if (!ControlSealing.TryUnseal(key, payload, out kind, out delta, out var ts, out var nonceId))
        {
            rejectReason = "not-sealed (wrong password, tampered, or a pre-5.6 peer)";
            return false;
        }
        var sentUtc = DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime;
        var age = utcNow - sentUtc;
        if (age > maxSkew || age < -maxSkew)
        {
            rejectReason = $"stale (sent {Math.Abs(age.TotalMinutes):0.0} min from now, window {maxSkew.TotalMinutes:0} min)";
            return false;
        }
        if (seenNonces.ContainsKey(nonceId))
        {
            rejectReason = "replay (nonce already accepted)";
            return false;
        }
        // Remember this nonce for the life of the skew window; prune opportunistically. Control
        // commands are human-rate (hotkey presses), so this stays tiny.
        if (seenNonces.Count >= 256)
        {
            foreach (var stale in seenNonces.Where(kv => utcNow - kv.Value > maxSkew + maxSkew).Select(kv => kv.Key).ToList())
                seenNonces.Remove(stale);
        }
        seenNonces[nonceId] = utcNow;
        rejectReason = "";
        return true;
    }
}
