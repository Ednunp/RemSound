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
/// so a successful install does the swap via a detached <c>cmd.exe</c> helper:
/// <list type="number">
///   <item>Download the release ZIP to <c>%TEMP%\RemSound-update-&lt;tag&gt;.zip</c>.</item>
///   <item>Extract to <c>&lt;exe&gt;\_update\</c>.</item>
///   <item>Write a one-shot batch file at <c>&lt;exe&gt;\_apply-update.cmd</c> that waits for
///         RemSound.exe to exit, robocopies the staged folder over the publish folder, deletes
///         the staging area, restarts RemSound.exe, and removes itself.</item>
///   <item>Start the batch with <c>CreateNoWindow</c> + detached, then call
///         <see cref="Application.Exit"/>.</item>
/// </list>
/// The batch survives RemSound's exit because <c>cmd.exe</c> is its own process. Robocopy's
/// retry/wait flags handle the brief moment between RemSound exit and the file unlock.
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
    /// sentinel. Written next to RemSound.exe by <see cref="DownloadAndStageInstallAsync"/>
    /// when the caller supplies a non-empty <c>activeProfileTitle</c>; read and deleted by
    /// <c>Program.Main</c> on the next startup. Lives in the install directory (not %TEMP%
    /// or %APPDATA%) because the helper batch's robocopy step needs to know to skip it —
    /// see the <c>/XF</c> list in <see cref="BuildInstallScript"/>.</summary>
    public const string ResumeProfileSentinelName = "_resume-after-update.txt";

    /// <summary>Download the update ZIP, stage it next to RemSound.exe, spawn the detached
    /// install helper, and ask the App to exit so the helper can take over. Returns true if
    /// the helper was launched (caller should Application.Exit immediately afterwards);
    /// false on any failure earlier in the pipeline. A false return leaves the running
    /// instance untouched.
    ///
    /// <paramref name="activeProfileTitle"/> — when non-empty, a one-shot sentinel file
    /// <see cref="ResumeProfileSentinelName"/> is written next to RemSound.exe just before
    /// the helper is launched. On the next startup, Program.Main reads it, loads that profile
    /// silently (skipping the picker), and deletes the sentinel. This makes a silent / manual
    /// update behave like the session never ended — the user is back in the same profile
    /// they were running, without having to remember which one it was. When null/empty, no
    /// sentinel is written and the post-update launch uses whatever startup behaviour
    /// AppConfig has configured.</summary>
    public async Task<bool> DownloadAndStageInstallAsync(UpdateInfo info, string? activeProfileTitle = null, CancellationToken token = default)
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var stagingDir = Path.Combine(baseDir, "_update");
            var zipPath = Path.Combine(Path.GetTempPath(), $"RemSound-update-{info.Tag}.zip");
            var batchPath = Path.Combine(baseDir, "_apply-update.cmd");
            var resumeSentinelPath = Path.Combine(baseDir, ResumeProfileSentinelName);

            // Tidy any leftover from a previous failed attempt before we start. Also clear
            // the failure marker — the new attempt starts clean and only re-creates the
            // marker if THIS run fails. The resume sentinel from a previous run (if any) is
            // also cleared here; if the caller supplies a profile title, the new sentinel is
            // written below after staging succeeds.
            TryDelete(zipPath);
            TryDeleteDirectory(stagingDir);
            TryDelete(Path.Combine(baseDir, "update-failed.txt"));
            TryDelete(resumeSentinelPath);

            Log?.Invoke($"updater: downloading {info.DownloadUrl}");
            await using (var src = await http.GetStreamAsync(info.DownloadUrl, token).ConfigureAwait(false))
            await using (var dst = File.Create(zipPath))
            {
                await src.CopyToAsync(dst, token).ConfigureAwait(false);
            }

            Log?.Invoke($"updater: extracting to {stagingDir}");
            Directory.CreateDirectory(stagingDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, stagingDir, overwriteFiles: true);

            // Some release zips wrap everything in a single top-level folder
            // (e.g. "RemSound-v1.1/RemSound.exe"). Flatten if that's the case so the
            // robocopy step copies the right tree over the install location.
            var stagingRoot = ResolveStagingRoot(stagingDir);

            Log?.Invoke($"updater: writing install helper {batchPath}");
            File.WriteAllText(batchPath, BuildInstallScript(stagingRoot, baseDir));

            // Write the resume-after-update sentinel so the post-restart launch loads the
            // same profile silently (no picker, no missed session). Only when the caller
            // supplied a title — blank-template sessions and explicit "no profile yet" cases
            // fall through to the normal startup logic.
            if (!string.IsNullOrWhiteSpace(activeProfileTitle))
            {
                try
                {
                    File.WriteAllText(resumeSentinelPath, activeProfileTitle);
                    Log?.Invoke($"updater: wrote resume sentinel for profile '{activeProfileTitle}'");
                }
                catch (Exception ex)
                {
                    // Sentinel is best-effort. If we can't write it (disk full, ACL change),
                    // the update still proceeds; the user gets the picker on relaunch.
                    Log?.Invoke($"updater: could not write resume sentinel: {ex.GetType().Name}: {ex.Message}");
                }
            }

            var pid = System.Environment.ProcessId;
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{batchPath}\" {pid}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = baseDir,
            };
            Log?.Invoke($"updater: launching install helper, parent PID {pid}");
            System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"updater: install failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>One-shot installer batch. Waits for the supplied PID to exit (so file locks
    /// release), robocopies the staged folder over the install folder, removes the staging
    /// area, restarts RemSound.exe, and self-deletes.
    ///
    /// History: v1.0 of this helper used <c>/R:5 /W:1</c> on robocopy and unconditionally
    /// restarted RemSound regardless of whether the copy actually succeeded. On
    /// Dropbox-installed copies this failed silently — Dropbox held write locks on the
    /// existing install files for ~10–30 seconds after extraction kicked the sync off, robocopy
    /// gave up after 5 seconds, and the helper relaunched the OLD binary. The user saw the same
    /// version after "update".
    ///
    /// v1.3 hardening (2026-05-15):
    ///   * Robocopy retries bumped to <c>/R:60 /W:1</c> — up to 60 seconds per file. Dropbox
    ///     locks reliably release inside that window.
    ///   * Robocopy exit code is captured and checked. Anything ≥ 8 is a true failure; the
    ///     helper writes <c>update-failed.txt</c> to the install dir with diagnostic detail,
    ///     does NOT relaunch the old binary, and leaves the staging folder intact so the
    ///     user (or a re-run of the updater) can recover. Codes 0–7 are robocopy's
    ///     "success-ish" range (0 = nothing changed, 1 = copied, 2 = extras, 3 = both, etc).
    ///   * Helper writes a step-by-step log to <c>_update-helper.log</c> in the install dir
    ///     for post-mortem when the copy goes wrong. Robocopy's own output is appended via
    ///     <c>/LOG+:</c>.
    ///
    /// 2026-05-18 changes:
    ///   * Robocopy now also excludes <c>remsound.config.json</c> (the user's machine-local
    ///     config) and the <c>logs</c> / <c>profiles</c> / <c>recordings</c> folders, so an
    ///     update can never overwrite the user's own state — only app files are replaced.
    ///   * On SUCCESS the helper now also deletes <c>_update-helper.log</c> and any stale
    ///     <c>update-failed.txt</c> (the <c>_update</c> folder was already removed), leaving
    ///     a tidy install folder. The FAILURE branch still keeps all of them for diagnosis.
    ///
    /// The helper is detached from RemSound at start time, so it survives the parent's exit.</summary>
    private static string BuildInstallScript(string stagingRoot, string installDir)
    {
        var helperLog = Path.Combine(installDir, "_update-helper.log");
        var failureMarker = Path.Combine(installDir, "update-failed.txt");
        var stagingDir = Path.Combine(installDir, "_update");
        var remsoundExe = Path.Combine(installDir, "RemSound.exe");
        // Robocopy source/destination, with any trailing directory separator stripped.
        // CRITICAL BUG FIX (2026-05-19): installDir is AppContext.BaseDirectory, which ends
        // in a backslash. A quoted path that ends in a backslash — "D:\dir\" — is mis-parsed
        // on the command line: the \" is read as an ESCAPED quote, so robocopy never receives
        // a valid destination argument and exits immediately with code 16 (no files copied).
        // That silently broke EVERY auto-update in every release to date. The robocopy line
        // below MUST use these trimmed forms, never the raw {installDir} / {stagingRoot}.
        var stagingArg = stagingRoot.TrimEnd('\\', '/');
        var installArg = installDir.TrimEnd('\\', '/');
        return $"""
        @echo off
        setlocal
        rem RemSound auto-installer helper. Generated by RemSoundUpdater. Self-deleting on success.
        set "PID=%~1"
        set "LOG={helperLog}"
        set "MARKER={failureMarker}"

        echo. >> "%LOG%"
        echo === %DATE% %TIME% update helper started, parent PID=%PID% === >> "%LOG%"
        echo %DATE% %TIME% install dir=[%~dp0] >> "%LOG%"

        :wait_loop
        tasklist /FI "PID eq %PID%" 2>nul | find "%PID%" >nul
        if not errorlevel 1 (
          timeout /t 1 /nobreak >nul
          goto wait_loop
        )

        echo %DATE% %TIME% parent exited, starting robocopy (R:60 W:1) >> "%LOG%"
        rem /XF + /XD keep the update from ever overwriting the USER's own state: everything under
        rem "user settings and logs" (global config, profiles, logs, sounds — including any custom cue
        rem WAVs the user dropped in) plus the legacy loose config. An update replaces APP files only.
        rem build-release.ps1 keeps those out of the release zip; this is the second line of defence so
        rem a bad zip still can't clobber them. The bare logs/profiles/recordings excludes stay for any
        rem older layout still mid-migration.
        robocopy "{stagingArg}" "{installArg}" /E /IS /IT /NFL /NDL /NJH /NJS /R:60 /W:1 /XF _apply-update.cmd /XF _update-helper.log /XF update-failed.txt /XF remsound.config.json /XF {ResumeProfileSentinelName} /XD logs profiles recordings _update "user settings and logs" /LOG+:"%LOG%"
        set "ROBO_EXIT=%ERRORLEVEL%"
        rem Guard against an empty exit code (e.g. robocopy never ran / ERRORLEVEL was clobbered):
        rem an empty %ROBO_EXIT% turns the GEQ test below into a parse error. Default it to a
        rem clear non-zero so the failure path is taken cleanly and logged with a real number.
        if not defined ROBO_EXIT set "ROBO_EXIT=99"
        echo %DATE% %TIME% robocopy exit=%ROBO_EXIT% >> "%LOG%"

        if %ROBO_EXIT% GEQ 8 (
          echo RemSound could not finish updating. > "%MARKER%"
          echo. >> "%MARKER%"
          echo The new version downloaded correctly, but RemSound could not >> "%MARKER%"
          echo replace its program files with it. Nothing is broken - your >> "%MARKER%"
          echo current version still works and has been left as it was. >> "%MARKER%"
          echo. >> "%MARKER%"
          echo What to do: >> "%MARKER%"
          echo. >> "%MARKER%"
          echo   1. Close RemSound completely. >> "%MARKER%"
          echo   2. Wait about 30 seconds. A file-syncing, backup or antivirus >> "%MARKER%"
          echo      program may have been using RemSound's files; this gives it >> "%MARKER%"
          echo      time to finish and let go of them. >> "%MARKER%"
          echo   3. Start RemSound again, open the Help menu, and choose >> "%MARKER%"
          echo      Check for updates to try once more. It usually works on the >> "%MARKER%"
          echo      second attempt. >> "%MARKER%"
          echo. >> "%MARKER%"
          echo If it still will not update: the new version's files are ready >> "%MARKER%"
          echo and waiting in the folder named _update, next to RemSound.exe. >> "%MARKER%"
          echo You can finish the update yourself by copying everything from >> "%MARKER%"
          echo inside that _update folder into this folder, replacing the older >> "%MARKER%"
          echo files when asked. >> "%MARKER%"
          echo. >> "%MARKER%"
          echo Once RemSound has updated successfully you can delete this file. >> "%MARKER%"
          echo Technical details for support are in _update-helper.log in this folder. >> "%MARKER%"
          rem Drop the resume-after-update sentinel on failure too — there's no restart
          rem happening, so a stale sentinel would mis-direct the user's next manual launch
          rem into auto-loading a profile they may have moved on from in the meantime.
          del "{installArg}\{ResumeProfileSentinelName}" 2>nul
          echo %DATE% %TIME% FAILURE: robocopy exit=%ROBO_EXIT%, update folder kept, NOT restarting RemSound >> "%LOG%"
          del "%~f0"
          exit /b %ROBO_EXIT%
        )

        rmdir /S /Q "{stagingDir}" 2>nul
        echo %DATE% %TIME% update applied OK, cleaning up and restarting RemSound >> "%LOG%"
        del "%MARKER%" 2>nul
        start "" "{remsoundExe}"
        rem Success cleanup: the staged _update folder is already gone (rmdir above). Now drop
        rem the helper log and the failure marker too, so a clean update leaves the install
        rem folder tidy with no _update / _update-helper.log / update-failed.txt left behind.
        rem (The FAILURE branch above deliberately keeps all of these for diagnosis.)
        rem The helper log is deleted last, after the final line is written to it.
        del "%LOG%" 2>nul
        del "%~f0"
        """;
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
