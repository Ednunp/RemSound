using System.Diagnostics;

namespace RemSound.App;

/// <summary>
/// In-process update installer — the robust replacement for the old generated cmd.exe + robocopy
/// helper, modelled on how a battle-tested portable app does it.
///
/// It runs as a SEPARATE RemSound.exe process launched from a TEMP copy of the NEW version (so
/// nothing in the install folder is locked by the updater itself), waits for the old app to FULLY
/// exit (a real process-handle wait, not a task-list poll), then back-up-and-swaps the new files
/// over the install in plain C#:
///   * each existing target file is RENAMED aside into a backup folder first (renaming survives a
///     lock that an overwrite can't), then the new file is copied into place;
///   * every move/copy retries for a while, to ride out a transient lock (e.g. Dropbox / OneDrive
///     finishing a sync) — the equivalent of robocopy's /R:60, but it can't silently swallow the
///     exit code;
///   * if any step can't complete, the whole swap is ROLLED BACK from the backup, so a failed
///     update never leaves a broken half-installed folder — the old version is restored intact.
/// User data is untouched: only files present in the new release are written, so the
/// "user settings and logs" folder and recordings (which aren't in the release) are left alone.
///
/// Invoked by Program.Main when the command line carries <c>--apply-update</c>. Args:
///   --apply-update --update-source &lt;dir&gt; --update-target &lt;dir&gt; --update-wait-pid &lt;pid&gt;
///   --update-stage-root &lt;dir&gt; [--resume-profile &lt;title&gt;] [--update-no-restart]
/// </summary>
internal static class UpdateApplier
{
    private const int CopyRetryAttempts = 60;          // ~ matches robocopy /R:60
    private const int CopyRetryDelayMs = 1000;          // 1 s between attempts → up to ~60 s per file
    private const int ProcessExitWaitMs = 30000;        // wait up to 30 s for the old app to exit
    private const int PostExitSettleMs = 1000;          // let the OS release file handles after exit

    public static void Run(string[] args)
    {
        var source = GetArg(args, "--update-source");
        var target = GetArg(args, "--update-target");
        var stageRoot = GetArg(args, "--update-stage-root");
        var pidText = GetArg(args, "--update-wait-pid");
        var resumeProfile = GetArg(args, "--resume-profile");
        var noRestart = Array.Exists(args, a => string.Equals(a, "--update-no-restart", StringComparison.OrdinalIgnoreCase));

        // Log to a single file in the install root (NOT the "user settings and logs" folder — the
        // updater runs before the new version's folder-migration, so it must not pre-create that
        // folder). Persists for diagnosis; survives the temp stage being cleaned up.
        var logPath = string.IsNullOrWhiteSpace(target)
            ? Path.Combine(Path.GetTempPath(), "RemSound-updater.log")
            : Path.Combine(target, "updater.log");
        void Log(string m) => AppendLog(logPath, m);

        try
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
                throw new InvalidOperationException("missing --update-source / --update-target");

            Log($"=== apply-update started. source=[{source}] target=[{target}] pid=[{pidText}] ===");

            if (int.TryParse(pidText, out var pid) && pid > 0) WaitForExit(pid, Log);

            var backupDir = Path.Combine(target, "_update-backup");
            TryDeleteDirectory(backupDir);

            var moved = new List<(string backup, string dest)>(); // old file renamed aside → restore on rollback
            var created = new List<string>();                     // brand-new file (no old to restore) → delete on rollback
            try
            {
                SwapInNewFiles(source, target, backupDir, moved, created, Log);
            }
            catch (Exception swapEx)
            {
                Log($"SWAP FAILED: {swapEx.GetType().Name}: {swapEx.Message} — rolling back");
                RollBack(moved, created, Log);
                TryDeleteDirectory(backupDir); // restored files were moved back out; drop the empty backup tree
                WriteFailureMarker(target, logPath);
                WhatsNewMarker.Consume(target); // a rolled-back update must never trigger "what's new"
                Log("rolled back to previous version" + (noRestart ? "" : "; restarting it"));
                if (!noRestart) RestartApp(target, resumeProfile, Log); // old version restored intact — safe to relaunch
                CleanupStage(stageRoot, Log);
                return;
            }

            TryDeleteDirectory(backupDir);
            WriteResumeSentinel(target, resumeProfile, Log);
            // Positive one-shot signal that the update genuinely succeeded — drives the next launch's
            // "what's new" popup. Written ONLY here, on the success path.
            try { WhatsNewMarker.Write(target); Log("wrote what's-new marker"); }
            catch (Exception ex) { Log($"could not write what's-new marker: {ex.Message}"); }
            Log("apply-update OK" + (noRestart ? "" : " — restarting RemSound"));
            if (!noRestart) RestartApp(target, resumeProfile, Log);
            CleanupStage(stageRoot, Log);
        }
        catch (Exception ex)
        {
            Log($"apply-update FATAL: {ex.GetType().Name}: {ex.Message}");
            try
            {
                System.Windows.Forms.MessageBox.Show(
                    "RemSound could not finish updating, and has left your current version exactly as it was.\n\n"
                        + "Please reopen RemSound and try Help → Check for updates again.\n\n" + ex.Message,
                    "RemSound update", System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Warning);
            }
            catch { /* headless / no message loop — the log has it */ }
        }
    }

    /// <summary>Wait for the parent RemSound to fully exit so its files release. A real wait on the
    /// process handle, then a short settle for the OS to drop the exe's image lock.</summary>
    private static void WaitForExit(int pid, Action<string> log)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            log($"waiting for parent PID {pid} to exit");
            if (!p.WaitForExit(ProcessExitWaitMs)) log($"parent PID {pid} still running after {ProcessExitWaitMs} ms — proceeding anyway");
            else log("parent exited");
        }
        catch
        {
            log($"parent PID {pid} already gone");
        }
        Thread.Sleep(PostExitSettleMs);
    }

    /// <summary>Rename each existing target file aside into the backup folder, then copy the new
    /// one in. Throws if a file genuinely can't be replaced after the retry window — the caller
    /// rolls back. Only writes files that exist in the new release, so user data is left untouched.</summary>
    private static void SwapInNewFiles(string source, string target, string backupDir,
        List<(string backup, string dest)> moved, List<string> created, Action<string> log)
    {
        var srcFull = Path.GetFullPath(source);
        var copiedCount = 0;
        foreach (var srcFile in Directory.GetFiles(srcFull, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(srcFull, srcFile);
            var dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            if (File.Exists(dest))
            {
                var bak = Path.Combine(backupDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(bak)!);
                RetryFileOp(() => File.Move(dest, bak), $"move-aside {rel}"); // frees + backs up the old file
                moved.Add((bak, dest));
                RetryFileOp(() => File.Copy(srcFile, dest, overwrite: true), $"copy {rel}");
            }
            else
            {
                RetryFileOp(() => File.Copy(srcFile, dest, overwrite: true), $"new {rel}");
                created.Add(dest);
            }
            copiedCount++;
        }
        log($"swap complete: {copiedCount} files in, {moved.Count} replaced, {created.Count} new");
    }

    /// <summary>Restore the old files (and remove the partial new ones) after a failed swap.</summary>
    private static void RollBack(List<(string backup, string dest)> moved, List<string> created, Action<string> log)
    {
        foreach (var dest in created)
        {
            try { if (File.Exists(dest)) File.Delete(dest); } catch { /* best-effort */ }
        }
        for (var i = moved.Count - 1; i >= 0; i--)
        {
            var (backup, dest) = moved[i];
            try
            {
                if (File.Exists(dest)) File.Delete(dest); // remove the partial new copy if it landed
                File.Move(backup, dest);                  // put the old one back
            }
            catch (Exception ex) { log($"rollback could not restore {dest}: {ex.Message}"); }
        }
    }

    /// <summary>Run a file operation, retrying while the target is locked (a sync/AV/handle still
    /// closing). Renaming-aside already beats most locks; this rides out the transient ones.</summary>
    private static void RetryFileOp(Action op, string what)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { op(); return; }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < CopyRetryAttempts)
            {
                Thread.Sleep(CopyRetryDelayMs);
            }
        }
    }

    private static void WriteResumeSentinel(string target, string? profile, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(profile)) return;
        try
        {
            File.WriteAllText(Path.Combine(target, RemSoundUpdater.ResumeProfileSentinelName), profile);
            log($"wrote resume sentinel for profile '{profile}'");
        }
        catch (Exception ex) { log($"could not write resume sentinel: {ex.Message}"); }
    }

    private static void RestartApp(string target, string? profile, Action<string> log)
    {
        try
        {
            var exe = Path.Combine(target, "RemSound.exe");
            if (!File.Exists(exe)) { log($"cannot restart — {exe} missing"); return; }
            Process.Start(new ProcessStartInfo { FileName = exe, WorkingDirectory = target, UseShellExecute = true });
            log("RemSound restarted");
        }
        catch (Exception ex) { log($"could not restart RemSound: {ex.Message}"); }
    }

    /// <summary>Best-effort temp cleanup. We're running FROM the stage, so we can't delete our own
    /// exe's folder here — the restarted app finishes that on startup (see Program.CleanUpUpdateStages).</summary>
    private static void CleanupStage(string? stageRoot, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(stageRoot)) return;
        try
        {
            foreach (var zip in Directory.GetFiles(stageRoot, "*.zip")) { try { File.Delete(zip); } catch { } }
        }
        catch (Exception ex) { log($"stage cleanup (partial): {ex.Message}"); }
    }

    private static void WriteFailureMarker(string target, string logPath)
    {
        try
        {
            File.WriteAllText(Path.Combine(target, "update-failed.txt"),
                "RemSound could not finish updating, so it put your previous version back exactly as it was.\r\n\r\n"
                + "Nothing is broken. Reopen RemSound and try Help → Check for updates again; it usually works\r\n"
                + "on the next attempt. Technical details are in:\r\n  " + logPath + "\r\n\r\n"
                + "You can delete this file once RemSound has updated successfully.\r\n");
        }
        catch { /* marker is best-effort */ }
    }

    private static void AppendLog(string path, string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch { /* never let logging break the update */ }
    }

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best-effort */ }
    }
}
