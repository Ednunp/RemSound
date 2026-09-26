using System.Security.Cryptography;
using RemSound.Core;

namespace RemSound.Receiver;

/// <summary>
/// Decrypts incoming audio payloads with the key derived from the local profile's password.
/// One instance is shared by every <see cref="StreamSession"/>. Two threads use it: the network listener's, and the
/// sender socket's, which carries audio arriving from a server. So each thread decrypts into a buffer of its own, and the
/// cipher - which is not safe for two at once - is used by one at a time. It shared one buffer until 2026-09-25, and one
/// peer's audio could be overwritten by another's. The cipher is rebuilt only when the key reference changes (a password
/// change). 2026-05-31.
/// </summary>
internal sealed class AudioDecryptor : IDisposable
{
    private AesGcm? gcm;
    private byte[]? keyCached;
    private readonly object cipherGate = new();
    // One per receive thread, sized for the largest decrypted frame (Opus 20 ms / PCM 5 ms are well under this).
    [ThreadStatic] private static byte[]? threadScratch;

    /// <summary>True once a password/key is set — without one, nothing can be decrypted and all
    /// audio is dropped (encryption is mandatory).</summary>
    public bool HasKey => gcm is not null;

    /// <summary>Rebuild the cipher if the key reference changed. Call on the network thread
    /// before decrypting. Pushing a new array (not mutating in place) is what signals a change.</summary>
    public void EnsureKey(byte[]? key)
    {
        lock (cipherGate)
        {
            if (ReferenceEquals(key, keyCached)) return;
            gcm?.Dispose();
            gcm = key is null ? null : RemSoundCrypto.CreateGcm(key);
            keyCached = key;
        }
    }

    /// <summary>Decrypt a ciphertext payload into this thread's own scratch. Returns the plaintext span (a view into
    /// it, valid until this thread's next call) or an empty span on failure — wrong key (password mismatch), tampered
    /// packet, or no key set. Safe from any thread.</summary>
    public ReadOnlySpan<byte> TryDecrypt(ReadOnlySpan<byte> ciphertext)
    {
        var scratch = threadScratch ??= new byte[8192];
        int len;
        lock (cipherGate)
        {
            if (gcm is null || !RemSoundCrypto.TryDecryptInto(gcm, ciphertext, scratch, out len)) return default;
        }
        return scratch.AsSpan(0, len);
    }

    public void Dispose()
    {
        lock (cipherGate)
        {
            gcm?.Dispose();
            gcm = null;
            keyCached = null;
        }
    }
}
