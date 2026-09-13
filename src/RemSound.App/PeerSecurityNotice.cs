namespace RemSound.App;

/// <summary>
/// What to say when a peer's format packet carries no password fingerprint.
///
/// <para><b>Why the old wording was wrong.</b> It said, in a modal dialog: "<i>they</i> are running an
/// older version of RemSound that can't connect securely. They need to update." That is one possible
/// cause and no longer the likely one. The condition is simply "no fingerprint arrived", which is also
/// what a current build sends when the other person has no password set on their profile.</para>
///
/// <para>The tester on 2026-09-08 hit exactly that. Both machines were on 5.9.0, and his log said
/// PeerNeedsUpdate. Anyone following that dialog would have gone off checking versions that were
/// already identical. A diagnostic that names the wrong cause is worse than one that says nothing,
/// because it spends the user's time and their trust.</para>
/// </summary>
internal static class PeerSecurityNotice
{
    /// <summary>Both causes, likeliest first, each with something to do about it.</summary>
    public static string NoFingerprintMessage(string peer)
        => $"{peer} isn't sending a password fingerprint, so audio can't pass securely between you.\n\n"
         + "There are two reasons this happens. Most often they simply have no password set on their "
         + "profile — ask them to set one (File → Change this profile's password) matching yours. Less "
         + "often they are on a version of RemSound old enough to predate encryption, in which case "
         + "they need to update.\n\n"
         + "Check the password first: it is by far the more common of the two, and you can both see it "
         + "straight away.";
}
