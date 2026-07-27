using System.Security.Cryptography;

namespace RemSound.Core;

/// <summary>
/// Release-zip signature verification (2026-07-27, per the security audit: the updater previously
/// trusted whatever the GitHub release stream served — a compromised account could ship code to
/// every user silently). Every release zip is now signed at publish time (build-release.ps1) with
/// a private ECDSA P-256 key that lives ONLY on the publisher's machine; this class holds the
/// matching public key and the updater REFUSES any update whose signature is missing or does not
/// verify. Manual downloads from the release page are unaffected — this gates the automatic path.
/// The signature travels as a release asset named "&lt;zip-name&gt;.sig" (base64 of an ECDSA
/// SHA-256 signature over the raw zip bytes).
/// </summary>
public static class UpdateSignature
{
    /// <summary>The RemSound release-signing PUBLIC key (SubjectPublicKeyInfo PEM). The private
    /// half is NOT in the repo — it lives only with the publisher. Replacing this constant means
    /// shipping a release signed by BOTH keys' owner, i.e. only the publisher can rotate it.</summary>
    public const string PublicKeyPem = """
-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAENrzZmey3cvNxNyd6t55QQThTb3Zj
xR34nJr7egPq4f1Ff1IL5qA46nstniKZ3Zl6k+vcLWRr1oXzzdHvbIidcw==
-----END PUBLIC KEY-----
""";

    /// <summary>Suffix of the signature asset on a GitHub release: the zip asset's name + this.</summary>
    public const string SignatureAssetSuffix = ".sig";

    /// <summary>True when <paramref name="signatureBase64"/> is a valid signature over
    /// <paramref name="data"/> by the embedded release key. Never throws — malformed base64,
    /// wrong length, wrong key all simply return false.</summary>
    public static bool Verify(byte[] data, string signatureBase64) =>
        VerifyWithKey(data, signatureBase64, PublicKeyPem);

    /// <summary>Verification against an explicit public key — the seam the self-test uses to prove
    /// the mechanics (round-trip, tamper, wrong-key) with an ephemeral throwaway keypair.</summary>
    public static bool VerifyWithKey(byte[] data, string signatureBase64, string publicKeyPem)
    {
        try
        {
            var signature = Convert.FromBase64String(signatureBase64.Trim());
            using var ec = ECDsa.Create();
            ec.ImportFromPem(publicKeyPem);
            return ec.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Sign data with a private-key PEM — used by the publish pipeline (via --sign-update)
    /// and the self-test's if-the-key-is-present embed-matches-key check. Returns base64.</summary>
    public static string SignWithKey(byte[] data, string privateKeyPem)
    {
        using var ec = ECDsa.Create();
        ec.ImportFromPem(privateKeyPem);
        return Convert.ToBase64String(ec.SignData(data, HashAlgorithmName.SHA256));
    }
}
