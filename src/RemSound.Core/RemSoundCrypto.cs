using System.Security.Cryptography;
using System.Text;

namespace RemSound.Core;

/// <summary>How a selected peer's encryption lines up with ours, derived from the password
/// fingerprint they advertise in their format packets.</summary>
public enum PeerSecurityStatus
{
    /// <summary>No fingerprint seen yet (or we have no password set) — nothing to report.</summary>
    Unknown,
    /// <summary>Their password fingerprint matches ours: audio will decrypt, the link is secure.</summary>
    Secure,
    /// <summary>They advertised a fingerprint, but it differs from ours — different passwords, so
    /// no audio will pass. The user needs to make the two passwords match.</summary>
    PasswordMismatch,
    /// <summary>They sent format packets with no fingerprint at all — an older, pre-encryption
    /// build. They need to update before audio can flow.</summary>
    PeerNeedsUpdate,
}

/// <summary>
/// Cryptographic helpers for RemSound's always-on audio encryption (in development, 2026-05-31).
///
/// Model (agreed design): each profile carries a password. Two peers can exchange audio only
/// when their profile passwords match, because the audio is encrypted with a key derived from
/// the password — same password → same key → each side can unscramble the other; different
/// passwords → the integrity check fails and packets are dropped (silence, never garbage).
///
/// Primitives:
///   * <see cref="DeriveKey"/> — turns a password into a 256-bit AES key via PBKDF2 (slow on
///     purpose, to make guessing expensive). Run once per password and cached by the caller,
///     never per packet.
///   * <see cref="Fingerprint"/> — a short, non-reversible id two peers can compare to discover
///     they share a password WITHOUT sending it. Different salt from the key so it can't double
///     as the key.
///   * <see cref="Encrypt"/> / <see cref="TryDecrypt"/> — AES-256-GCM (authenticated) on a
///     packet payload. Fast (microseconds, hardware-accelerated). A wrong key fails the auth
///     tag and TryDecrypt returns false.
///   * <see cref="Obfuscate"/> / <see cref="Deobfuscate"/> — a LIGHT, reversible scramble for
///     the password as it sits in the profile JSON. NOT encryption (the key is in the binary):
///     it just keeps the password from being readable at a glance in a possibly-synced file.
///     That's an accepted trade-off of a portable per-profile password.
///
/// All algorithms are supported back to Windows 7 (PBKDF2 is pure-managed; AES-GCM goes through
/// Windows CNG) — worth verifying on a real Win7 box before this ships, same as the updater.
/// </summary>
public static class RemSoundCrypto
{
    private const int KeyBytes = 32;        // AES-256
    private const int FingerprintBytes = 8; // enough to compare; not a key
    private const int NonceBytes = 12;      // AES-GCM standard nonce
    private const int TagBytes = 16;        // AES-GCM auth tag

    // PBKDF2 cost. High enough to make brute-forcing a captured fingerprint expensive, low
    // enough not to stall a connect on older (Win7-era) hardware. Run once per password, cached.
    private const int Pbkdf2Iterations = 100_000;

    // Fixed salts. A per-connection random salt would be stronger, but both peers must derive
    // the SAME key from the SAME password with no key-exchange round, so the salt has to be
    // shared and known in advance. Distinct salts keep the key and the fingerprint independent.
    private static readonly byte[] KeySalt = Encoding.UTF8.GetBytes("RemSound.v1.audio-key");
    private static readonly byte[] FingerprintSalt = Encoding.UTF8.GetBytes("RemSound.v1.fingerprint");

    // Repeating-XOR key for the light on-disk scramble (see class summary — NOT security).
    private static readonly byte[] ObfuscationKey =
        Encoding.UTF8.GetBytes("RemSound-profile-password-scramble-v1");

    /// <summary>The one rule for turning a PLAIN password into the audio credentials: null/empty →
    /// (null, null) → no audio flows (encryption is mandatory); otherwise the key AND the fingerprint,
    /// always together — the peer verifies the fingerprint before accepting a stream, so a key without
    /// its fingerprint gets the audio silently rejected at the far end (a divergence that already bit
    /// the service once). The app and the service both derive through THIS.</summary>
    public static (byte[]? Key, byte[]? Fingerprint) ForPlainPassword(string? plainPassword) =>
        string.IsNullOrEmpty(plainPassword) ? (null, null) : (DeriveKey(plainPassword), Fingerprint(plainPassword));

    /// <summary>Derive the 256-bit AES key for a password. Cache the result; never call per packet.</summary>
    public static byte[] DeriveKey(string? password) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password ?? ""), KeySalt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeyBytes);

    /// <summary>A short, non-reversible id for a password. Two peers compare fingerprints to
    /// learn they share a password without revealing it.</summary>
    public static byte[] Fingerprint(string? password) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password ?? ""), FingerprintSalt, Pbkdf2Iterations, HashAlgorithmName.SHA256, FingerprintBytes);

    /// <summary>Encrypt a payload. Output layout: nonce(12) || tag(16) || ciphertext. A fresh
    /// random nonce is generated per call. (The live wire layer may later derive the nonce from
    /// the packet sequence number instead, which is the textbook approach for a long-lived key.)</summary>
    public static byte[] Encrypt(byte[] key, ReadOnlySpan<byte> plaintext)
    {
        var nonce = new byte[NonceBytes];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];
        using (var aes = new AesGcm(key, TagBytes))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }
        var output = new byte[NonceBytes + TagBytes + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, output, 0, NonceBytes);
        Buffer.BlockCopy(tag, 0, output, NonceBytes, TagBytes);
        Buffer.BlockCopy(ciphertext, 0, output, NonceBytes + TagBytes, ciphertext.Length);
        return output;
    }

    /// <summary>Reverse <see cref="Encrypt"/>. Returns false (and an empty payload) if the auth
    /// tag doesn't verify — i.e. the key is wrong or the packet was tampered with.</summary>
    public static bool TryDecrypt(byte[] key, ReadOnlySpan<byte> packet, out byte[] plaintext)
    {
        plaintext = [];
        if (packet.Length < NonceBytes + TagBytes) return false;
        var nonce = packet[..NonceBytes];
        var tag = packet.Slice(NonceBytes, TagBytes);
        var ciphertext = packet[(NonceBytes + TagBytes)..];
        var result = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, result);
            plaintext = result;
            return true;
        }
        catch (CryptographicException)
        {
            return false; // wrong key or tampered
        }
    }

    /// <summary>The number of bytes <see cref="EncryptInto"/> adds on top of the plaintext
    /// length (nonce + tag). Callers size their buffers and MTU budgets against this.</summary>
    public const int EncryptionOverheadBytes = NonceBytes + TagBytes; // 28

    /// <summary>Build a reusable AES-GCM cipher for a key. The caller owns it (it's IDisposable)
    /// and reuses it across many packets — far cheaper than constructing one per packet. AES-GCM
    /// is NOT thread-safe, so give each thread (each sender lane; the single receiver thread)
    /// its own.</summary>
    public static AesGcm CreateGcm(byte[] key) => new(key, TagBytes);

    /// <summary>Low-allocation encrypt straight into a destination span. Layout written:
    /// nonce(12) || tag(16) || ciphertext. Returns the number of bytes written
    /// (= plaintext.Length + <see cref="EncryptionOverheadBytes"/>). <paramref name="dst"/> must
    /// be at least that big. The nonce comes from <paramref name="nonces"/> — counter-based, so
    /// it CANNOT repeat on a long-lived key (see <see cref="NonceSequence"/>; 2026-07-26 audit:
    /// per-packet random nonces carry a birthday-collision bound a multi-day stream can reach).</summary>
    public static int EncryptInto(AesGcm gcm, NonceSequence nonces, ReadOnlySpan<byte> plaintext, Span<byte> dst)
    {
        var total = plaintext.Length + EncryptionOverheadBytes;
        if (dst.Length < total) throw new ArgumentException("Encrypt destination too small", nameof(dst));
        nonces.FillNext(dst[..NonceBytes]);
        gcm.Encrypt(dst[..NonceBytes], plaintext, dst.Slice(NonceBytes + TagBytes, plaintext.Length), dst.Slice(NonceBytes, TagBytes));
        return total;
    }

    /// <summary>The nonce generator for a hot-path encryptor: a random 4-byte prefix chosen at
    /// construction plus a 64-bit counter — the textbook fix the old per-packet-random comment
    /// pointed at. Uniqueness within one instance is arithmetic (the counter), not probabilistic;
    /// across instances (each sender lane, each session) the random prefix keeps streams apart.
    /// The receiver just reads the nonce from the packet, so this changes nothing on the wire.
    /// NOT thread-safe — one per encryptor, same ownership rule as the AesGcm itself.</summary>
    public sealed class NonceSequence
    {
        private readonly byte[] prefix = new byte[4];
        private ulong counter;

        public NonceSequence() => RandomNumberGenerator.Fill(prefix);

        /// <summary>Write the next 12-byte nonce: prefix(4) || counter(8), then advance.</summary>
        public void FillNext(Span<byte> nonce12)
        {
            prefix.CopyTo(nonce12);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(nonce12[4..], counter);
            counter++;
        }
    }

    /// <summary>Low-allocation decrypt of an <see cref="EncryptInto"/> packet into a destination
    /// span. Returns true and the plaintext length on success; false if the packet is too short,
    /// the destination too small, or the auth tag fails (wrong key / tampered).</summary>
    public static bool TryDecryptInto(AesGcm gcm, ReadOnlySpan<byte> packet, Span<byte> dst, out int written)
    {
        written = 0;
        if (packet.Length < EncryptionOverheadBytes) return false;
        var ctLen = packet.Length - EncryptionOverheadBytes;
        if (dst.Length < ctLen) return false;
        var nonce = packet[..NonceBytes];
        var tag = packet.Slice(NonceBytes, TagBytes);
        var ciphertext = packet.Slice(NonceBytes + TagBytes, ctLen);
        try
        {
            gcm.Decrypt(nonce, ciphertext, tag, dst[..ctLen]);
            written = ctLen;
            return true;
        }
        catch (CryptographicException)
        {
            return false; // wrong key or tampered
        }
    }

    /// <summary>Constant-time equality for two fingerprints (or any small byte spans). Avoids
    /// leaking, via timing, how much of a fingerprint matched.</summary>
    public static bool FingerprintsEqual(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) =>
        CryptographicOperations.FixedTimeEquals(a, b);

    /// <summary>Light, reversible scramble of a password for storage in the profile JSON. NOT
    /// encryption — just so the password isn't legible at a glance. Empty in, empty out.</summary>
    public static string Obfuscate(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        var data = Encoding.UTF8.GetBytes(plaintext);
        for (var i = 0; i < data.Length; i++) data[i] ^= ObfuscationKey[i % ObfuscationKey.Length];
        return Convert.ToBase64String(data);
    }

    /// <summary>Reverse <see cref="Obfuscate"/>. Returns "" for null/empty/garbage input.</summary>
    public static string Deobfuscate(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        try
        {
            var data = Convert.FromBase64String(stored);
            for (var i = 0; i < data.Length; i++) data[i] ^= ObfuscationKey[i % ObfuscationKey.Length];
            return Encoding.UTF8.GetString(data);
        }
        catch
        {
            return "";
        }
    }
}
