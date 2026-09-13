namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>
    /// A MISSING PASSWORD FINGERPRINT MUST NAME BOTH CAUSES, THE LIKELIER FIRST.
    ///
    /// <para>A tester, 2026-09-08: his log said "PeerNeedsUpdate" and the app raised a dialog telling him
    /// the far end was on an older version. Both machines were on 5.9.0. The condition is only "no
    /// fingerprint arrived", which is also what a CURRENT build sends when that person has no password set.
    /// A diagnostic that names the wrong cause costs the user's time and their trust, so this one has to
    /// name both, likeliest first.</para>
    ///
    /// <para><b>Not per-configuration.</b> The fingerprint travels in the format packet, upstream of every
    /// output choice, and is identical in all three audio configurations.</para>
    /// </summary>
    private static string? AuditMissingFingerprintNamesBothCauses()
    {
        var security = PeerSecurityNotice.NoFingerprintMessage("10.0.0.5");
        Check(security.Contains("password", StringComparison.OrdinalIgnoreCase),
            $"the no-fingerprint message must name the likely cause — no password set (got \"{security}\")");
        Check(security.Contains("update", StringComparison.OrdinalIgnoreCase),
            "it must still mention the old-build case, which is real even if it is now the rarer one");
        Check(security.IndexOf("password", StringComparison.OrdinalIgnoreCase)
              < security.IndexOf("update", StringComparison.OrdinalIgnoreCase),
            "the LIKELIER cause must come first — the old wording led with the version, and a tester on the same "
            + "version as his peer went looking for a version problem that did not exist");
        Check(security.Contains("10.0.0.5", StringComparison.Ordinal),
            "the message must name WHICH peer, or it is unactionable when several are selected");

        return "a missing fingerprint names the password cause before the version one, and says which peer";
    }
}
