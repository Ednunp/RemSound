namespace RemSound.App;

/// <summary>
/// Where the release-signing key lives. Ed, 2026-09-13: move it out of RemSound and into the release script. The key
/// itself never shipped, but its folder on the publisher's machine was written into RemSound, so every copy carried it.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>
    /// NO COPY OF REMSOUND CARRIES THE SIGNING KEY'S FOLDER.
    ///
    /// <para>Read from the built assembly itself, so anything that puts the folder back — the signing verb, or a step in
    /// this suite — is seen. And with no key given, the signing verb refuses rather than going looking for one.</para>
    /// </summary>
    private static string? AuditNoCopyCarriesTheSigningKeyFolder()
    {
        // Built at run time, so this step does not put the very text it looks for into the assembly.
        var needle = string.Concat("rsound", " key");
        var assembly = typeof(SelfTest).Assembly.Location;
        Check(File.Exists(assembly), $"the built assembly must be readable to check it ({assembly})");
        var bytes = File.ReadAllBytes(assembly);
        foreach (var (encoding, name) in new[] { (System.Text.Encoding.Unicode, "UTF-16"), (System.Text.Encoding.UTF8, "UTF-8") })
            Check(bytes.AsSpan().IndexOf(encoding.GetBytes(needle)) < 0,
                $"RemSound must not carry the signing key's folder — \"{needle}\" is in {Path.GetFileName(assembly)} as {name} text");

        var zip = Path.Combine(Path.GetTempPath(), "remsound-signtest-" + Guid.NewGuid().ToString("N") + ".zip");
        var restoreKey = Environment.GetEnvironmentVariable("REMSOUND_SIGNING_KEY");
        try
        {
            File.WriteAllBytes(zip, [1, 2, 3, 4]);
            Environment.SetEnvironmentVariable("REMSOUND_SIGNING_KEY", null);
            var code = CommandLine.SignUpdateForTest(zip);
            Check(code == 3, $"with no signing key given, signing must refuse (exit 3), not look for a key somewhere (got exit {code})");
            Check(!File.Exists(zip + RemSound.Core.UpdateSignature.SignatureAssetSuffix), "and it must write no signature");
        }
        finally
        {
            Environment.SetEnvironmentVariable("REMSOUND_SIGNING_KEY", restoreKey);
            try { File.Delete(zip); File.Delete(zip + RemSound.Core.UpdateSignature.SignatureAssetSuffix); } catch { /* temp */ }
        }
        return $"{Path.GetFileName(assembly)} carries no key folder; with no key given, signing refuses";
    }

    /// <summary>
    /// THE RELEASE SCRIPT HANDS REMSOUND THE SIGNING KEY.
    ///
    /// <para>With the folder gone from RemSound, build-release.ps1 is the only thing that knows it. If it stopped
    /// passing it, a release would fail at the signing step.</para>
    /// </summary>
    private static string? AuditReleaseScriptHandsOverTheSigningKey()
    {
        var root = FindSourceRoot();
        if (root is null) return Skip("the source tree is not reachable (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var script = File.ReadAllText(Path.Combine(root, "build-release.ps1"));
        Check(script.Contains("[string]$SigningKey", StringComparison.Ordinal), "build-release.ps1 must take the signing key as -SigningKey");
        var handOver = script.IndexOf("$env:REMSOUND_SIGNING_KEY = $SigningKey", StringComparison.Ordinal);
        var sign = script.IndexOf("--sign-update", handOver < 0 ? 0 : handOver, StringComparison.Ordinal);
        Check(handOver >= 0 && sign > handOver,
            "build-release.ps1 must hand RemSound the signing key (REMSOUND_SIGNING_KEY) before it runs --sign-update");
        return "build-release.ps1 takes -SigningKey and hands it over before signing";
    }
}
