using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// The relay's auto-updater checks a release's signature before installing it. Ed, 2026-09-13: it installed whatever
/// server release appeared on GitHub, with full control of its machine, without checking the release came from him.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>
    /// THE RELAY UPDATER INSTALLS ONLY A RELEASE SIGNED BY THE RELEASE KEY.
    ///
    /// <para>Its own check is driven in bash with openssl, the tools a Raspberry Pi has: a release signed by the key it
    /// trusts is accepted, and a changed file, another key's signature, garbage and no signature at all are refused. The
    /// key it trusts must be the app's release key, and the check must come before anything touches the running
    /// relay.</para>
    /// </summary>
    private static string? AuditRelayUpdaterInstallsOnlySignedReleases()
    {
        var root = FindSourceRoot();
        if (root is null) return Skip("the source tree is not reachable (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var text = File.ReadAllText(Path.Combine(root, "server", "remsound-relay-update.sh")).Replace("\r", "");

        Check(OnlyKeyText(RelayUpdaterPublicKey(text)) == OnlyKeyText(UpdateSignature.PublicKeyPem),
            "the relay updater must trust the same release key the app checks its own updates with");
        var main = text.IndexOf("\nmain() {", StringComparison.Ordinal);
        Check(main >= 0, "the relay updater's main() is gone — this check is watching nothing");
        int At(string s) => text.IndexOf(s, main, StringComparison.Ordinal);
        var refuseUnsigned = At("has no signature");
        var verify = At("verify_release_signature \"$tarball\"");
        Check(refuseUnsigned > main && refuseUnsigned < At("log \"downloading $asset_url\""),
            "the relay updater must refuse a release with no signature before it downloads anything");
        Check(verify > main && verify < At("tar -xzf") && verify < At("snapshot_backup") && verify < At("systemctl stop"),
            "the relay updater must check the signature before it unpacks, backs up or stops anything");

        var bash = FindGitBash();
        if (bash is null)
            return Skip("the updater trusts the release key and checks before installing, but no bash (Git for Windows) was found here to run its check");

        var dir = Path.Combine(Path.GetTempPath(), "remsound-relaysig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var bytes = new byte[4096];
            new Random(2026).NextBytes(bytes);
            var file = Path.Combine(dir, "remsound-server-v9.9.tar.gz");
            File.WriteAllBytes(file, bytes);
            var tampered = (byte[])bytes.Clone();
            tampered[100] ^= 0x01;
            File.WriteAllBytes(Path.Combine(dir, "tampered.tar.gz"), tampered);
            File.WriteAllText(Path.Combine(dir, "pub.pem"), key.ExportSubjectPublicKeyInfoPem());
            File.WriteAllText(file + ".sig", UpdateSignature.SignDerWithKey(bytes, key.ExportECPrivateKeyPem()));
            File.WriteAllText(Path.Combine(dir, "other.sig"), UpdateSignature.SignDerWithKey(bytes, other.ExportECPrivateKeyPem()));
            File.WriteAllText(Path.Combine(dir, "garbage.sig"), "this is not a signature");

            const string Release = "\"$D/remsound-server-v9.9.tar.gz\"";
            const string Pub = "\"$D/pub.pem\"";
            var output = RunRelayUpdaterCheck(bash, text, dir,
                $"verify_release_signature {Release} \"$D/remsound-server-v9.9.tar.gz.sig\" {Pub}; echo \"genuine=$?\"; "
              + $"verify_release_signature \"$D/tampered.tar.gz\" \"$D/remsound-server-v9.9.tar.gz.sig\" {Pub}; echo \"tampered=$?\"; "
              + $"verify_release_signature {Release} \"$D/other.sig\" {Pub}; echo \"otherkey=$?\"; "
              + $"verify_release_signature {Release} \"$D/garbage.sig\" {Pub}; echo \"garbage=$?\"; "
              + $"verify_release_signature {Release} \"$D/missing.sig\" {Pub}; echo \"missing=$?\"");
            if (output.Contains("no-openssl", StringComparison.Ordinal))
                return Skip("the updater trusts the release key and checks before installing, but this bash has no openssl to run its check");
            Check(Regex.IsMatch(output, @"\bgenuine=0\b"),
                $"a release signed by the key the relay trusts must be accepted (bash said: {ShortTail(output)})");
            foreach (var (label, what) in new[]
            {
                ("tampered", "a file changed after signing"),
                ("otherkey", "another key's signature"),
                ("garbage", "a signature that is garbage"),
                ("missing", "a release with no signature file"),
            })
                Check(Regex.IsMatch(output, $@"\b{label}=[1-9]"), $"the relay updater must refuse {what} (bash said: {ShortTail(output)})");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* temp */ } }
        return "the relay updater trusts the release key and checks before touching the relay; a genuine signature passes, "
             + "and a changed file, another key, garbage and no signature are refused";
    }

    /// <summary>
    /// SERVER RELEASES ARE SIGNED THE WAY THE RELAY CHECKS.
    ///
    /// <para>--sign-server-release must write a signature that the relay's own check accepts, with the key built into
    /// the updater, and must refuse when no key is given. The release key is only on the publisher's machine:
    /// run-tests.ps1 hands it over there, and the real-signature half skips anywhere else.</para>
    /// </summary>
    private static string? AuditServerReleasesAreSignedTheWayTheRelayChecks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "remsound-serversign-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "remsound-server-v9.9.tar.gz");
        var restoreKey = Environment.GetEnvironmentVariable("REMSOUND_SIGNING_KEY");
        try
        {
            Directory.CreateDirectory(dir);
            var bytes = new byte[2048];
            new Random(909).NextBytes(bytes);
            File.WriteAllBytes(file, bytes);

            Environment.SetEnvironmentVariable("REMSOUND_SIGNING_KEY", null);
            var refused = CommandLine.SignServerReleaseForTest(file);
            Environment.SetEnvironmentVariable("REMSOUND_SIGNING_KEY", restoreKey);
            Check(refused == 3 && !File.Exists(file + UpdateSignature.SignatureAssetSuffix),
                $"with no signing key given, signing a server release must refuse and write nothing (exit {refused})");

            if (string.IsNullOrWhiteSpace(restoreKey) || !File.Exists(restoreKey))
                return Skip("signing refuses without a key; the release key is not on this machine, so no real server-release signature was made and checked (run-tests.ps1 hands it over where it is)");
            var code = CommandLine.SignServerReleaseForTest(file);
            Check(code == 0 && File.Exists(file + UpdateSignature.SignatureAssetSuffix),
                $"with the release key given, signing a server release must write its signature (exit {code})");

            var root = FindSourceRoot();
            if (root is null) return Skip("the signature was written, but the source tree is not reachable to check it with the relay's own updater");
            var bash = FindGitBash();
            if (bash is null) return Skip("the signature was written, but no bash (Git for Windows) was found here to check it with the relay's own updater");
            var text = File.ReadAllText(Path.Combine(root, "server", "remsound-relay-update.sh")).Replace("\r", "");
            File.WriteAllText(Path.Combine(dir, "release-public-key.pem"), RelayUpdaterPublicKey(text) + "\n");
            var output = RunRelayUpdaterCheck(bash, text, dir,
                "verify_release_signature \"$D/remsound-server-v9.9.tar.gz\" \"$D/remsound-server-v9.9.tar.gz.sig\" \"$D/release-public-key.pem\"; echo \"real=$?\"");
            if (output.Contains("no-openssl", StringComparison.Ordinal))
                return Skip("the signature was written, but this bash has no openssl to check it with the relay's own updater");
            Check(Regex.IsMatch(output, @"\breal=0\b"),
                $"a server release signed with the release key must pass the relay's own check, with the key built into the updater (bash said: {ShortTail(output)})");
            return "signing refuses without a key; with the release key it writes a signature the relay's own check accepts";
        }
        finally
        {
            Environment.SetEnvironmentVariable("REMSOUND_SIGNING_KEY", restoreKey);
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }
    }

    /// <summary>Git for Windows' bash, if it is installed.</summary>
    private static string? FindGitBash()
    {
        foreach (var candidate in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
            @"C:\Program Files\Git\bin\bash.exe",
        })
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    /// <summary>
    /// THE RELAY UPDATER READS A LONG RELEASE LIST, AND FINDS THE SERVER RELEASE IN IT.
    ///
    /// <para>It handed the whole GitHub release list to python as one command-line argument. Linux refuses a single
    /// argument over 32 memory pages - 128 KB on most machines, 512 KB on a Pi 5 - and the list, every release with its
    /// notes, was already 197 KB: on most machines the hourly check failed every time, saying only "no upgrade attempted"
    /// (found 2026-09-25; Ed's Pi 5 still had room). And it read thirty releases, so a
    /// server release behind thirty of the app's own would never have been seen. Driven here in bash, with the real
    /// function, over a list of a hundred releases several hundred KB long with the newest server release deep inside
    /// it. (Windows refuses an argument over 32 KB, so the old way fails here even sooner.)</para>
    /// </summary>
    private static string? AuditRelayUpdaterReadsALongReleaseList()
    {
        var root = FindSourceRoot();
        if (root is null) return Skip("the source tree is not reachable (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var text = File.ReadAllText(Path.Combine(root, "server", "remsound-relay-update.sh")).Replace("\r", "");
        Check(text.Contains("per_page=100", StringComparison.Ordinal), "the relay updater must read a hundred releases a page, not thirty");
        var bash = FindGitBash();
        if (bash is null) return Skip("the updater asks for a hundred releases, but no bash (Git for Windows) was found here to run its reading of them");

        var dir = Path.Combine(Path.GetTempPath(), "remsound-relaylist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            object Release(string tag, bool server) => new Dictionary<string, object>
            {
                ["tag_name"] = tag,
                ["draft"] = false,
                ["prerelease"] = false,
                ["body"] = new string('n', 4000),
                ["assets"] = server
                    ? new object[]
                    {
                        new Dictionary<string, string> { ["name"] = $"remsound-server-{tag["server-".Length..]}.tar.gz", ["browser_download_url"] = $"https://example.invalid/{tag}.tar.gz" },
                        new Dictionary<string, string> { ["name"] = $"remsound-server-{tag["server-".Length..]}.tar.gz.sig", ["browser_download_url"] = $"https://example.invalid/{tag}.tar.gz.sig" },
                    }
                    : new object[] { new Dictionary<string, string> { ["name"] = "RemSound.zip", ["browser_download_url"] = "https://example.invalid/app.zip" } },
            };
            var releases = new List<object>();
            for (var i = 0; i < 97; i++)
            {
                if (i == 60) releases.Add(Release("server-v2.11", server: true));   // deep inside, behind sixty app releases
                releases.Add(Release($"v6.0.{97 - i}", server: false));
            }
            releases.Add(Release("server-v2.10", server: true));
            releases.Add(Release("server-v2.9", server: true));
            var json = System.Text.Json.JsonSerializer.Serialize(releases);
            File.WriteAllText(Path.Combine(dir, "releases.json"), json);
            Check(json.Length > 200_000, $"premise: the list must be past Linux's 128 KB limit for one argument ({json.Length:N0} bytes)");

            // curl hands back the list (to the file it is told, or its output); python3 is Windows' own Python here.
            const string script =
                "command -v py >/dev/null 2>&1 || { echo no-python; exit 0; }; "
                + "curl() { local out=\"\"; while [[ $# -gt 0 ]]; do if [[ \"$1\" == \"-o\" ]]; then out=\"$2\"; shift 2; else shift; fi; done; "
                + "if [[ -n \"$out\" ]]; then cat \"$D/releases.json\" > \"$out\"; else cat \"$D/releases.json\"; fi; }; "
                + "python3() { py -3 \"$@\"; }; log() { echo \"LOG: $*\" >&2; }; "
                + "found=\"$(get_latest_release)\"; rc=$?; echo \"$found\"; echo \"rc=$rc\"";
            var output = RunRelayUpdaterCheck(bash, text, dir, script);
            if (output.Contains("no-python", StringComparison.Ordinal)) return Skip("no Python here to run the updater's reading of the list");
            if (output.Contains("no-openssl", StringComparison.Ordinal)) return Skip("this bash has no openssl, which the updater's check helper insists on");
            var lines = output.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Check(Regex.IsMatch(output, @"\brc=0\b") && lines.FirstOrDefault() == "server-v2.11",
                $"THE SILENCE: over a release list of {json.Length:N0} bytes the updater must still find the newest server release, "
                + $"server-v2.11, sixty releases in (bash said: {ShortTail(output)})");
            Check(lines.Any(l => l.EndsWith("server-v2.11.tar.gz.sig", StringComparison.Ordinal)), "and its signature, which it will not install without");
            return $"over a release list of {json.Length:N0} bytes the updater finds server-v2.11 behind sixty app releases, with its signature";
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* temp */ } }
    }

    /// <summary>
    /// THE RELAY UPDATER REFUSES AN OLD RELEASE PUT UP UNDER A NEW NAME.
    ///
    /// <para>The signature covers the release, but its version came only from its name on GitHub, which is not signed.
    /// Anybody able to publish a release but without the key could put an old signed one up as, say, server-v99: the
    /// relay went back to the old code and, believing it had v99, never took a real update again (review 2026-09-25).
    /// It now installs a release only when the VERSION file inside it names the same release, checked before anything
    /// touches the running relay. Driven here in bash with the real function.</para>
    /// </summary>
    private static string? AuditRelayUpdaterRefusesAnOldReleaseUnderANewName()
    {
        var root = FindSourceRoot();
        if (root is null) return Skip("the source tree is not reachable (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var text = File.ReadAllText(Path.Combine(root, "server", "remsound-relay-update.sh")).Replace("\r", "");
        var main = text.IndexOf("\nmain() {", StringComparison.Ordinal);
        Check(main >= 0, "the relay updater's main() is gone — this check is watching nothing");
        int At(string s) => text.IndexOf(s, main, StringComparison.Ordinal);
        var check = At("release_is_version \"$staging\" \"$latest_tag\"");
        Check(check > At("tar -xzf") && check < At("snapshot_backup") && check < At("systemctl stop") && check < At("install_from_staging \"$staging\""),
            "the relay updater must check the release's own version after unpacking it and before it backs up, stops or installs anything");
        var version = File.ReadAllText(Path.Combine(root, "server", "VERSION")).Trim();
        Check(Regex.IsMatch(version, @"^server-v\d+(\.\d+)+$"),
            $"server/VERSION must name the release it is built into, as server-vN.N, or the relay will refuse it (it says \"{version}\")");

        var bash = FindGitBash();
        if (bash is null) return Skip("the updater checks the release's own version, but no bash (Git for Windows) was found here to run it");
        var dir = Path.Combine(Path.GetTempPath(), "remsound-relayversion-" + Guid.NewGuid().ToString("N"));
        try
        {
            void Release(string name, string? versionFile)
            {
                Directory.CreateDirectory(Path.Combine(dir, name));
                if (versionFile is not null) File.WriteAllText(Path.Combine(dir, name, "VERSION"), versionFile);
            }
            Release("genuine", "server-v2.12 \r\n");   // a stray space and a Windows line ending
            Release("old", "server-v2.6\n");
            Release("prefix", "server-v2.1\n");
            Release("empty", "  \n");
            Release("none", null);
            var output = RunRelayUpdaterCheck(bash, text, dir,
                "release_is_version \"$D/genuine\" server-v2.12; echo \"genuine=$?\"; "
              + "release_is_version \"$D/old\" server-v99; echo \"old=$?\"; "
              + "release_is_version \"$D/prefix\" server-v2.12; echo \"prefix=$?\"; "
              + "release_is_version \"$D/empty\" server-v2.12; echo \"empty=$?\"; "
              + "release_is_version \"$D/none\" server-v2.12; echo \"none=$?\"");
            if (output.Contains("no-openssl", StringComparison.Ordinal))
                return Skip("the updater checks the release's own version, but this bash has no openssl, which its check helper insists on");
            Check(Regex.IsMatch(output, @"\bgenuine=0\b"),
                $"a release whose own VERSION names its tag must be accepted, stray space and Windows line ending and all (bash said: {ShortTail(output)})");
            foreach (var (label, what) in new[]
            {
                ("old", "an old release (server-v2.6) put up as server-v99"),
                ("prefix", "a release that says server-v2.1 put up as server-v2.12"),
                ("empty", "a release with an empty VERSION file"),
                ("none", "a release with no VERSION file"),
            })
                Check(Regex.IsMatch(output, $@"\b{label}=[1-9]"), $"the relay updater must refuse {what} (bash said: {ShortTail(output)})");
            return $"the updater checks a release's own version before touching the relay: it accepts its own name and refuses an "
                 + $"old release under a new name, a near miss, an empty VERSION and none; server/VERSION says {version}";
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* temp */ } }
    }

    /// <summary>Source an LF copy of the relay updater in bash, with <c>$D</c> set to <paramref name="dir"/>, and run
    /// <paramref name="script"/>. Returns what bash printed. Prints "no-openssl" and stops when openssl is missing.</summary>
    private static string RunRelayUpdaterCheck(string bash, string updaterText, string dir, string script)
    {
        File.WriteAllText(Path.Combine(dir, "updater.sh"), updaterText.Replace("\r", ""));
        var start = new ProcessStartInfo(bash)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("source \"$D/updater.sh\"; set +e; command -v openssl >/dev/null 2>&1 || { echo no-openssl; exit 0; }; " + script);
        start.Environment["D"] = dir.Replace('\\', '/');
        using var process = Process.Start(start) ?? throw new InvalidOperationException("bash did not start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return "timed out";
        }
        return stdout.Result + stderr.Result;
    }

    /// <summary>The PEM public key block written into the relay updater, or "".</summary>
    private static string RelayUpdaterPublicKey(string updaterText)
    {
        const string Begin = "-----BEGIN PUBLIC KEY-----", End = "-----END PUBLIC KEY-----";
        var begin = updaterText.IndexOf(Begin, StringComparison.Ordinal);
        var end = begin < 0 ? -1 : updaterText.IndexOf(End, begin, StringComparison.Ordinal);
        return end < 0 ? "" : updaterText[begin..(end + End.Length)];
    }

    private static string OnlyKeyText(string pem) => new(pem.Where(c => !char.IsWhiteSpace(c)).ToArray());

    private static string ShortTail(string text) => text.Length <= 240 ? text.Trim() : "…" + text[^240..].Trim();
}
