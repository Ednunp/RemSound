using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Self-updater. Polls the GitHub Releases API for the latest published version, compares it
/// to the running assembly's version, and (optionally) downloads and installs the new build.
///
/// Update install flow (Windows-only): RemSound.exe can't overwrite itself while it's running,
/// so a successful install hands off to a SEPARATE copy of the new RemSound.exe (see
/// <see cref="UpdateApplier"/>):
/// <list type="number">
///   <item>Download the release ZIP and extract it to a per-user temp stage OFF the install
///         folder (<c>&lt;LocalAppData&gt;\RemSound\update\&lt;guid&gt;\app</c>).</item>
///   <item>Launch the staged <c>RemSound.exe --apply-update …</c> from there, then exit.</item>
///   <item>That process waits for this one to fully exit, then back-up-and-swaps the new files
///         over the install in plain C# (retry + rename-aside, rolling back on any failure),
///         restarts RemSound, and the next launch clears the temp stage.</item>
/// </list>
/// Running the installer from temp means nothing in the install folder is locked by the updater
/// itself; doing the copy in C# rather than a generated batch + robocopy removes the whole class
/// of silent batch/robocopy failures the old helper hit on some machines.
///
/// The GitHub repo to poll is hard-coded — the App was designed to be redistributed from a
/// single canonical release stream, not to be re-pointed at a fork. If you need to publish
/// from a different repo, change <see cref="RepoOwner"/> / <see cref="RepoName"/>.
/// </summary>
internal sealed class RemSoundUpdater : IDisposable
{
    public const string RepoOwner = "Ednunp";
    public const string RepoName = "RemSound";

    /// <summary>Asset name on the GitHub release that the updater downloads. The release
    /// publisher's <c>gh release create</c> command must attach exactly this filename for
    /// the auto-install path to work; other assets in the release are ignored. The literal
    /// "{tag}" placeholder is replaced with the release's <c>tag_name</c> at runtime.</summary>
    public const string AssetNameTemplate = "RemSound-{tag}.zip";

    private static readonly HttpClient http = CreateClient();

    /// <summary>Sink for diagnostic lines — the App wires this to <c>logFile.Event</c> so an
    /// admin can see what the updater did (which version it saw, whether it downloaded, why
    /// an install attempt failed). Updater output never goes to a popup unless the user
    /// triggered a manual check.</summary>
    public Action<string>? Log { get; set; }

    public string CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public void Dispose()
    {
        // HttpClient is static and shared across the process; nothing to dispose here.
    }

    /// <summary>Hit the GitHub Releases API, parse the latest release, return a struct
    /// describing what was found. Returns null if the request fails (network down, rate
    /// limited, repo not found) or if the latest version is not newer than the running
    /// assembly. Caller decides whether to surface "you're up to date" vs silently doing
    /// nothing — both paths get null back.</summary>
    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken token = default)
    {
        try
        {
            // List releases — NOT /releases/latest. The repo also hosts the relay server's
            // own "server-vX.Y" releases, and /releases/latest is repo-wide: it hands back
            // whichever release is newest by date, server or client. A server release would
            // then be fed to ParseTag ("server-v2.3" -> a bogus 0.0.3) and the updater would
            // wrongly conclude "up to date". We pull the list and consider ONLY releases
            // whose tag is a RemSound client tag (see IsClientReleaseTag). 2026-05-18.
            // per_page=100 (vs the API default of 30): the repo holds both client (vX.Y) and
            // relay-server (server-vX.Y) releases, so a burst of server releases could push the
            // newest client release off a 30-item first page. 100 keeps it comfortably in view.
            var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases?per_page=100";
            Log?.Invoke($"updater: GET {url}");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var resp = await http.SendAsync(req, token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Log?.Invoke($"updater: HTTP {(int)resp.StatusCode} from GitHub");
                return new UpdateCheckFailed(FailureKind.HttpError, $"GitHub responded with HTTP {(int)resp.StatusCode}.");
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var releases = await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(stream, JsonOpts, token).ConfigureAwait(false);
            if (releases is null || releases.Count == 0)
            {
                Log?.Invoke("updater: releases list was empty");
                return new UpdateCheckFailed(FailureKind.HttpError, "GitHub returned an empty release list.");
            }

            // Highest-versioned RemSound client release. Skip drafts, prereleases, and any
            // tag that isn't a client tag (notably the server-vX.Y relay releases).
            GitHubRelease? release = null;
            var latest = new Version(0, 0, 0);
            foreach (var r in releases)
            {
                if (r.TagName is null || r.Draft || r.Prerelease) continue;
                if (!IsClientReleaseTag(r.TagName)) continue;
                var v = ParseTag(r.TagName);
                if (v > latest) { latest = v; release = r; }
            }
            if (release?.TagName is null)
            {
                Log?.Invoke("updater: no RemSound client release found in the releases list");
                return new UpdateCheckFailed(FailureKind.HttpError, "GitHub returned releases but none looked like a RemSound client release.");
            }

            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            Log?.Invoke($"updater: current={current.ToString(3)} latest={latest.ToString(3)} ({release.TagName})");
            if (latest <= current) return UpToDate.Instance;

            var expectedAsset = AssetNameTemplate.Replace("{tag}", release.TagName);
            var asset = release.Assets?.FirstOrDefault(a => string.Equals(a.Name, expectedAsset, StringComparison.OrdinalIgnoreCase));
            if (asset?.BrowserDownloadUrl is null)
            {
                Log?.Invoke($"updater: latest release has no asset named '{expectedAsset}'");
                return new UpdateCheckFailed(FailureKind.HttpError, $"The latest release page is missing the expected file '{expectedAsset}'.");
            }

            return new UpdateAvailable(new UpdateInfo(
                Tag: release.TagName,
                Version: latest,
                DownloadUrl: asset.BrowserDownloadUrl,
                ReleaseNotes: release.Body ?? "",
                ReleaseUrl: release.HtmlUrl ?? ""));
        }
        catch (Exception ex)
        {
            Log?.Invoke($"updater: check failed: {ex.GetType().Name}: {ex.Message}");
            return new UpdateCheckFailed(ClassifyFailure(ex), ex.Message);
        }
    }

    /// <summary>Maps a thrown exception from the GitHub HTTP call to a coarse-grained
    /// <see cref="FailureKind"/> the UI can hang an honest plain-English message off without
    /// quoting the underlying .NET exception type. Most "couldn't reach the server" errors
    /// fall into the network bucket; the SSL bucket is broken out separately because it has
    /// a specific cause and fix on Windows 7 (TLS 1.2 / SHA-2 Windows updates) that we want
    /// to point users at when we see it. 2026-05-28.</summary>
    private static FailureKind ClassifyFailure(Exception ex)
    {
        // Walk the exception chain — HttpRequestException is the outer wrapper; the actual
        // cause (System.Net.Security.AuthenticationException, IOException, SocketException,
        // etc) is in InnerException. Either layer might carry the diagnostic clue.
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            var typeName = e.GetType().Name;
            var msg = e.Message ?? "";
            if (typeName.Contains("Authentication", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("SSL", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("TLS", StringComparison.OrdinalIgnoreCase))
            {
                return FailureKind.SecureConnection;
            }
        }
        if (ex is TaskCanceledException) return FailureKind.Timeout;
        return FailureKind.NetworkUnreachable;
    }

    /// <summary>Filename of the one-shot "after the update restart, silently load this profile"
    /// sentinel. Written into the install folder by <see cref="UpdateApplier"/> just before it
    /// relaunches RemSound (the profile title is handed to it via <c>--resume-profile</c>); read
    /// and deleted by <c>Program.Main</c> on the next startup, so a silent or mid-session update
    /// drops the user back into the same profile they were running rather than at the picker.</summary>
    public const string ResumeProfileSentinelName = "_resume-after-update.txt";

    /// <summary>Filename of the one-shot "an update just succeeded — show what's new once" marker.
    /// Written into the install folder by <see cref="UpdateApplier"/> ONLY on a successful update
    /// (never on a failed/rolled-back one); read and deleted by MainForm on the next startup. This is
    /// the positive signal that drives the "what's new after an update" popup — so a FAILED update can't
    /// trigger it. (The old running-version-vs-saved-version compare could re-fire after a failure when
    /// its best-effort flag save lost a race during the update churn — that was the bug.)</summary>
    public const string WhatsNewMarkerName = "_whats-new-after-update.txt";

    /// <summary>Download the update ZIP, stage it to a per-user temp folder, and launch the new
    /// version's in-app installer (<see cref="UpdateApplier"/>) to take over once this process
    /// exits. Returns true if the installer was launched (caller should Application.Exit
    /// immediately afterwards); false on any failure earlier in the pipeline. A false return
    /// leaves the running instance — and the install — untouched.
    ///
    /// <paramref name="activeProfileTitle"/> — when non-empty, it's passed to the installer via
    /// <c>--resume-profile</c>; the installer writes the one-shot <see cref="ResumeProfileSentinelName"/>
    /// sentinel into the install folder just before it relaunches RemSound. On the next startup,
    /// Program.Main reads it, loads that profile silently (skipping the picker), and deletes the
    /// sentinel — so a silent / mid-session update behaves like the session never ended. When
    /// null/empty, no sentinel is written and the post-update launch uses whatever startup
    /// behaviour AppConfig has configured.</summary>
    public async Task<bool> DownloadAndStageInstallAsync(UpdateInfo info, string? activeProfileTitle = null, CancellationToken token = default)
    {
        try
        {
            var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Stage the new version into a LOCAL, per-user temp folder OUTSIDE the install (and off any
            // Dropbox/OneDrive folder the install might sit in). The new RemSound.exe is launched FROM
            // here to do the swap, so nothing in the install folder is locked by the updater itself, and
            // a sync engine can't hold the staged files mid-update.
            var stageRoot = Path.Combine(UpdateStageParentDir, Guid.NewGuid().ToString("N"));
            var appDir = Path.Combine(stageRoot, "app");
            var zipPath = Path.Combine(stageRoot, $"RemSound-update-{info.Tag}.zip");
            Directory.CreateDirectory(appDir);

            // This attempt starts clean: clear any stale failure marker / resume sentinel / what's-new
            // marker in the install.
            TryDelete(Path.Combine(installDir, "update-failed.txt"));
            TryDelete(Path.Combine(installDir, ResumeProfileSentinelName));
            TryDelete(Path.Combine(installDir, WhatsNewMarkerName));

            Log?.Invoke($"updater: downloading {info.DownloadUrl}");
            await using (var src = await http.GetStreamAsync(info.DownloadUrl, token).ConfigureAwait(false))
            await using (var dst = File.Create(zipPath))
            {
                await src.CopyToAsync(dst, token).ConfigureAwait(false);
            }

            Log?.Invoke($"updater: extracting to {appDir}");
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, appDir, overwriteFiles: true);

            // Some release zips wrap everything in a single top-level folder (e.g. "RemSound-v1.1/").
            // Flatten to the level that actually holds RemSound.exe.
            var appRoot = ResolveStagingRoot(appDir);

            var stagedExe = Path.Combine(appRoot, "RemSound.exe");
            if (!File.Exists(stagedExe))
            {
                Log?.Invoke("updater: staged RemSound.exe not found — aborting, install left untouched");
                TryDeleteDirectory(stageRoot);
                return false;
            }

            // Hand off to the NEW version's in-app installer (UpdateApplier). It waits for THIS process
            // to exit, then back-up-and-swaps the files over the install in C# and restarts RemSound.
            // ArgumentList quotes paths with spaces/odd characters correctly for us — no batch escaping.
            var pid = System.Environment.ProcessId;
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = stagedExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = appRoot,
            };
            psi.ArgumentList.Add("--apply-update");
            psi.ArgumentList.Add("--update-source");
            psi.ArgumentList.Add(appRoot);
            psi.ArgumentList.Add("--update-target");
            psi.ArgumentList.Add(installDir);
            psi.ArgumentList.Add("--update-wait-pid");
            psi.ArgumentList.Add(pid.ToString());
            psi.ArgumentList.Add("--update-stage-root");
            psi.ArgumentList.Add(stageRoot);
            if (!string.IsNullOrWhiteSpace(activeProfileTitle))
            {
                psi.ArgumentList.Add("--resume-profile");
                psi.ArgumentList.Add(activeProfileTitle);
            }

            Log?.Invoke($"updater: launching in-app installer from {appRoot}, parent PID {pid}");
            System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"updater: install failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>If the zip extracted to a single subfolder (typical when GitHub zips a tag),
    /// return that subfolder so the copy works from the inner level. Otherwise return the
    /// staging dir itself.</summary>
    private static string ResolveStagingRoot(string stagingDir)
    {
        var subdirs = Directory.GetDirectories(stagingDir);
        var files = Directory.GetFiles(stagingDir);
        if (files.Length == 0 && subdirs.Length == 1) return subdirs[0];
        return stagingDir;
    }

    /// <summary>Per-user, local temp parent for update staging: &lt;LocalAppData&gt;\RemSound\update.
    /// Deliberately OFF the install folder (which may be Dropbox/OneDrive-synced) and writable
    /// without admin, so the new RemSound.exe can run from here to swap files over the install.</summary>
    internal static string UpdateStageParentDir
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local)) local = Path.GetTempPath();
            return Path.Combine(local, "RemSound", "update");
        }
    }

    /// <summary>Best-effort cleanup of leftover update artefacts, called on a normal launch. The
    /// in-app installer can't delete the temp stage it's running from, so the next launch clears it;
    /// this also sweeps away relics of the OLD (pre-3.6) batch updater that staged into the install
    /// folder (the <c>_update</c> tree, <c>_apply-update.cmd</c>, <c>_update-helper.log</c>).</summary>
    public static void CleanUpUpdateStages()
    {
        try
        {
            var parent = UpdateStageParentDir;
            if (Directory.Exists(parent))
                foreach (var dir in Directory.GetDirectories(parent)) TryDeleteDirectory(dir);
        }
        catch { /* best-effort */ }

        try
        {
            var installDir = AppContext.BaseDirectory;
            TryDeleteDirectory(Path.Combine(installDir, "_update"));
            TryDelete(Path.Combine(installDir, "_apply-update.cmd"));
            TryDelete(Path.Combine(installDir, "_update-helper.log"));
        }
        catch { /* best-effort */ }
    }

    /// <summary>Parses a release tag like <c>v1.2</c> or <c>1.2.3</c> into a <see cref="Version"/>.
    /// Leading "v" is stripped. Missing minor/build parts get filled with zeros so the result
    /// always compares meaningfully against <see cref="Assembly.GetName"/>.Version.</summary>
    /// <summary>True if <paramref name="tag"/> is a RemSound client release tag — e.g.
    /// <c>v1.6</c>, <c>1.6</c>, <c>1.6.0</c> — rather than something else hosted in the same
    /// GitHub repo, notably the relay server's <c>server-vX.Y</c> releases. Test: after an
    /// optional leading <c>v</c>, the first character must be a digit. <c>server-v2.3</c>
    /// starts with 's' and is rejected; <c>v1.6</c> is accepted. The updater must filter on
    /// this because it lists all repo releases and the server publishes into the same repo.</summary>
    public static bool IsClientReleaseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var trimmed = tag.TrimStart('v', 'V').Trim();
        return trimmed.Length > 0 && char.IsDigit(trimmed[0]);
    }

    public static Version ParseTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return new Version(0, 0, 0);
        var trimmed = tag.TrimStart('v', 'V').Trim();
        var parts = trimmed.Split('.', '-', '+');
        var nums = new int[3];
        for (var i = 0; i < 3 && i < parts.Length; i++)
        {
            int.TryParse(parts[i], out nums[i]);
        }
        return new Version(nums[0], nums[1], nums[2]);
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        // GitHub rejects API requests without a User-Agent. The header doubles as a way for
        // their abuse team to contact us if our polling misbehaves at scale.
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RemSound-Updater", "1.0"));
        return c;
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* ignore */ } }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    }
}

/// <summary>What <see cref="RemSoundUpdater.CheckForUpdateAsync"/> returns when there's a
/// newer release available. <see cref="ReleaseNotes"/> is the raw Markdown body of the
/// release on GitHub — show it directly in a confirmation dialog if the install isn't
/// silent.</summary>
internal sealed record UpdateInfo(
    string Tag,
    Version Version,
    string DownloadUrl,
    string ReleaseNotes,
    string ReleaseUrl);

/// <summary>Discriminated result of an update check. Replaces the v3.1.x-and-earlier
/// "UpdateInfo?" return type, which conflated "no newer version available" with "couldn't
/// reach the server" — the user saw "you are running the latest version" in both cases,
/// even when the check had actually failed because (e.g.) the OS couldn't establish a
/// secure connection to GitHub. The caller pattern-matches on this and shows an honest
/// message for each outcome. 2026-05-28.</summary>
internal abstract record UpdateCheckResult;

/// <summary>A newer release is available. Carries the parsed <see cref="UpdateInfo"/> the
/// caller passes to <see cref="RemSoundUpdater.DownloadAndStageInstallAsync"/>.</summary>
internal sealed record UpdateAvailable(UpdateInfo Info) : UpdateCheckResult;

/// <summary>The check completed and the installed version is at or above the latest
/// release. Singleton — there's nothing to carry beyond the result type itself.</summary>
internal sealed record UpToDate : UpdateCheckResult
{
    public static readonly UpToDate Instance = new();
    private UpToDate() { }
}

/// <summary>The check could not complete. <see cref="Kind"/> is a coarse classifier the UI
/// uses to pick a plain-English message; <see cref="TechnicalDetail"/> is the raw exception
/// or HTTP-status message intended for log output and "what to send the developer" cases —
/// never put it in a user-facing dialog verbatim.</summary>
internal sealed record UpdateCheckFailed(FailureKind Kind, string TechnicalDetail) : UpdateCheckResult;

/// <summary>Why the update check couldn't complete. Lets the UI distinguish "your TLS stack
/// is too old to reach modern HTTPS servers" (a known and fixable Windows 7 issue) from
/// "your internet is down" so the message and any pointers we offer match the actual
/// problem.</summary>
internal enum FailureKind
{
    /// <summary>The HTTPS handshake itself failed — usually means the OS's TLS or
    /// certificate stack is too old. Most commonly seen on Windows 7 installs without
    /// the TLS 1.2 enablement update (KB3140245) and SHA-2 code signing support
    /// (KB4474419).</summary>
    SecureConnection,
    /// <summary>The HTTP call reached GitHub but got back an unexpected response (4xx /
    /// 5xx HTTP status, malformed JSON, empty release list, etc).</summary>
    HttpError,
    /// <summary>The HTTP call timed out.</summary>
    Timeout,
    /// <summary>Generic "couldn't reach the server" — DNS, socket, no internet.</summary>
    NetworkUnreachable,
}
