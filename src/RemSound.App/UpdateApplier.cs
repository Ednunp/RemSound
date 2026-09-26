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
///   * if any step can't complete, the whole swap is ROLLED BACK from the backup, retrying each file
///     the same way, so a failed update normally leaves the old version intact. If even the rollback
///     can't put a file back, the backup is KEPT (never deleted), a note says which files are wrong
///     and where the old ones are, RemSound reads it out at every start, and the service's
///     self-update won't copy from the install until an update succeeds.
/// User data is untouched: only files present in the new release are written, so the
/// "user settings and logs" folder and recordings (which aren't in the release) are left alone.
///
/// Invoked by Program.Main when the command line carries <c>--apply-update</c>. Args:
///   --apply-update --update-source &lt;dir&gt; --update-target &lt;dir&gt; --update-wait-pid &lt;pid&gt;
///   --update-stage-root &lt;dir&gt; [--resume-profile &lt;title&gt;] [--update-no-restart]
/// Nothing passes --update-no-restart today; with it the files are swapped (or rolled back) and
/// RemSound is not started again.
/// </summary>
internal static class UpdateApplier
{
    // Not const: the self-test shortens them, so a locked file costs it a second, not a minute.
    internal static int CopyRetryAttempts = 60;         // ~ matches robocopy /R:60
    internal static int CopyRetryDelayMs = 1000;        // 1 s between attempts → up to ~60 s per file
    private const int ProcessExitWaitMs = 30000;        // wait up to 30 s for the old app to exit
    private const int PostExitSettleMs = 1000;          // let the OS release file handles after exit

    /// <summary>The folder inside the install that holds the old files during a swap. It exists only while a swap is under
    /// way or rolling back; the service's self-update waits for it to be gone (<see cref="ServiceUpdate.SwapInProgress"/>).</summary>
    internal const string BackupFolderName = "_update-backup";

    /// <summary>A backup that still holds old files nobody could put back is kept under this prefix plus the time, where the
    /// next update does not delete it and the service's self-update neither waits on it nor copies it.</summary>
    internal const string KeptBackupPrefix = BackupFolderName + "-kept-";

    /// <summary>Written after a failed update whose rollback put everything back: RemSound says so once, at its next
    /// start, then deletes it.</summary>
    internal const string FailureMarkerName = "update-failed.txt";

    /// <summary>Written after a failed update whose rollback could NOT put everything back: the install is not as it was.
    /// RemSound says so at every start, and the service's self-update will not copy from the install, until an update
    /// succeeds and deletes it.</summary>
    internal const string IncompleteMarkerName = "update-incomplete.txt";

    /// <summary>Self-test seam: called after each new file lands in the install, with its path.</summary>
    internal static Action<string>? AfterCopyForTest;

    public static void Run(string[] args)
    {
        var source = GetArg(args, "--update-source");
        var target = GetArg(args, "--update-target");
        var stageRoot = GetArg(args, "--update-stage-root");
        var pidText = GetArg(args, "--update-wait-pid");
        var resumeProfile = GetArg(args, "--resume-profile");
        var noRestart = Array.Exists(args, a => string.Equals(a, "--update-no-restart", StringComparison.OrdinalIgnoreCase));

        // Log to a single file in the install root (NOT the "user settings and logs" folder, which
        // belongs to the user and which the updater leaves alone). Persists for diagnosis; survives
        // the temp stage being cleaned up.
        var logPath = string.IsNullOrWhiteSpace(target)
            ? Path.Combine(Path.GetTempPath(), "RemSound-updater.log")
            : Path.Combine(target, "updater.log");
        void Log(string m) => AppendLog(logPath, m);

        // Held for as long as files are being changed, so a RemSound started meanwhile leaves the install alone
        // (InstallRepair: it would otherwise take the live backup for one a cut-off update left).
        using var applying = TryHoldApplyingLock();
        try
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
                throw new InvalidOperationException("missing --update-source / --update-target");

            Log($"=== apply-update started. source=[{source}] target=[{target}] pid=[{pidText}] ===");

            if (int.TryParse(pidText, out var pid) && pid > 0) WaitForExit(pid, Log);

            var backupDir = Path.Combine(target, BackupFolderName);
            // A backup left from before can hold the only copy of old files (an earlier update stopped half way, or its
            // rollback could not finish). It used to be deleted here without a look. Keep it if it has anything in it.
            KeepOrDropBackup(backupDir, target, Log);

            var moved = new List<(string backup, string dest)>(); // old file renamed aside → restore on rollback
            var created = new List<string>();                     // brand-new file (no old to restore) → delete on rollback
            try
            {
                SwapInNewFiles(source, target, backupDir, moved, created, Log);
            }
            catch (Exception swapEx)
            {
                Log($"SWAP FAILED: {swapEx.GetType().Name}: {swapEx.Message} — rolling back");
                var notRestored = RollBack(moved, created, Log);
                WhatsNewMarker.Consume(target); // a rolled-back update must never trigger "what's new"
                if (notRestored.Count == 0)
                {
                    TryDeleteDirectory(backupDir); // restored files were moved back out; drop the empty backup tree
                    WriteFailureMarker(target, logPath);
                    Log("rolled back to previous version" + (noRestart ? "" : "; restarting it"));
                }
                else
                {
                    // Some old files could not be put back. The backup used to be deleted here anyway, and the note said
                    // the old version was back "exactly as it was" (review 2026-09-25; Ed agreed the fix). Keep it, and
                    // say what is wrong and where the old files are.
                    var kept = KeepOrDropBackup(backupDir, target, Log);
                    WriteIncompleteMarker(target, notRestored, kept, logPath);
                    Log($"ROLLBACK INCOMPLETE: {notRestored.Count} file(s) could not be put back; the old files are kept in {kept ?? "(could not keep them)"}"
                        + (noRestart ? "" : "; restarting RemSound if it can"));
                }
                if (!noRestart) RestartApp(target, resumeProfile, Log); // old version restored intact — safe to relaunch
                CleanupStage(stageRoot, Log);
                return;
            }

            TryDeleteDirectory(backupDir);
            // Every file in the release is now in place, so an install an earlier update left broken is whole again.
            TryDeleteFile(Path.Combine(target, IncompleteMarkerName));
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
    /// rolls back. Only writes files that exist in the new release, so user data is left untouched.
    /// Internal (was private) so the self-test can pin the swap/rollback contract — a broken rollback
    /// is the one failure mode of the updater that bricks an install.</summary>
    internal static void SwapInNewFiles(string source, string target, string backupDir,
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
            AfterCopyForTest?.Invoke(dest);
            copiedCount++;
        }
        log($"swap complete: {copiedCount} files in, {moved.Count} replaced, {created.Count} new");
    }

    /// <summary>Restore the old files (and remove the partial new ones) after a failed swap, retrying each for as long as
    /// the swap itself would (it used to try once, so the same lock that failed the swap usually failed the rollback too).
    /// Returns the install paths that could NOT be put right: an empty list means the old version is back exactly.
    /// Internal for the self-test — see <see cref="SwapInNewFiles"/>.</summary>
    internal static List<string> RollBack(List<(string backup, string dest)> moved, List<string> created, Action<string> log)
    {
        var notRestored = new List<string>();
        foreach (var dest in created)
        {
            try { RetryFileOp(() => { if (File.Exists(dest)) File.Delete(dest); }, $"remove new {dest}"); }
            catch (Exception ex) { log($"rollback could not remove the new file {dest}: {ex.Message}"); notRestored.Add(dest); }
        }
        for (var i = moved.Count - 1; i >= 0; i--)
        {
            var (backup, dest) = moved[i];
            try
            {
                RetryFileOp(() => { if (File.Exists(dest)) File.Delete(dest); }, $"remove partial {dest}"); // the new copy, if it landed
                RetryFileOp(() => File.Move(backup, dest), $"restore {dest}");                               // put the old one back
            }
            catch (Exception ex) { log($"rollback could not restore {dest}: {ex.Message}"); notRestored.Add(dest); }
        }
        return notRestored;
    }

    /// <summary>A backup folder with files still in it is renamed to <see cref="KeptBackupPrefix"/> plus the time and kept;
    /// an empty one is deleted. Returns the kept folder, or null when there was nothing to keep. If it cannot be moved it is
    /// left where it is, never deleted, and that is returned.</summary>
    internal static string? KeepOrDropBackup(string backupDir, string target, Action<string> log)
    {
        try
        {
            if (!Directory.Exists(backupDir)) return null;
            if (!Directory.EnumerateFiles(backupDir, "*", SearchOption.AllDirectories).Any())
            {
                TryDeleteDirectory(backupDir);
                return null;
            }
            var stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmmss");
            var kept = Path.Combine(target, KeptBackupPrefix + stamp);
            for (var n = 2; Directory.Exists(kept); n++) kept = Path.Combine(target, KeptBackupPrefix + stamp + "-" + n);
            RetryFileOp(() => Directory.Move(backupDir, kept), "keep the backup");
            log($"kept the old files that are still in the backup: {kept}");
            return kept;
        }
        catch (Exception ex)
        {
            log($"could not keep the backup aside ({ex.Message}); it stays in {backupDir}");
            return Directory.Exists(backupDir) ? backupDir : null;
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

    /// <summary>Start RemSound.exe from <paramref name="target"/> with <c>--foreground</c>.
    /// <paramref name="profile"/> is not used here: after a successful swap the profile reaches the new copy
    /// through the resume sentinel written just before, and after a rollback no sentinel is written, so the
    /// old version opens the way it normally starts.</summary>
    private static void RestartApp(string target, string? profile, Action<string> log)
    {
        try
        {
            var exe = Path.Combine(target, "RemSound.exe");
            if (!File.Exists(exe)) { log($"cannot restart — {exe} missing"); return; }
            // Give the restarted copy the same foreground treatment as the post-install relaunch, so it
            // doesn't reopen BEHIND other windows where a blind user wouldn't notice it came back (the
            // old copy has already exited, so a fresh process has no foreground credit of its own).
            // --foreground makes it pull itself forward; the AllowSetForegroundWindow grant is
            // best-effort (this staged updater may not hold foreground rights to give away).
            var psi = new ProcessStartInfo { FileName = exe, WorkingDirectory = target, UseShellExecute = true };
            psi.ArgumentList.Add("--foreground");
            using var child = Process.Start(psi);
            if (child is not null) { try { AllowSetForegroundWindow(child.Id); } catch { } }
            log("RemSound restarted");
        }
        catch (Exception ex) { log($"could not restart RemSound: {ex.Message}"); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    /// <summary>Best-effort temp cleanup. We're running FROM the stage, so we can't delete our own
    /// exe's folder here — the restarted app finishes that on startup (see <see cref="RemSoundUpdater.CleanUpUpdateStages"/>).</summary>
    private static void CleanupStage(string? stageRoot, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(stageRoot)) return;
        try
        {
            foreach (var zip in Directory.GetFiles(stageRoot, "*.zip")) { try { File.Delete(zip); } catch { } }
        }
        catch (Exception ex) { log($"stage cleanup (partial): {ex.Message}"); }
    }

    /// <summary>The honest note for a rollback that could not put everything back: which files are wrong, where the old ones
    /// are, and how to put it right. RemSound reads it out at every start until an update succeeds.</summary>
    private static void WriteIncompleteMarker(string target, List<string> notRestored, string? kept, string logPath)
    {
        try
        {
            var files = string.Join("\r\n", notRestored.Select(f => "  " + Path.GetRelativePath(target, f)));
            File.WriteAllText(Path.Combine(target, IncompleteMarkerName),
                "RemSound could not finish updating, and could not put all of your previous version back.\r\n\r\n"
                + "These files are not as they were:\r\n" + files + "\r\n\r\n"
                + (kept is null
                    ? "The previous copies of those files could not be kept.\r\n\r\n"
                    : "Your previous copies of them are kept in:\r\n  " + kept + "\r\n\r\n")
                + "To put it right, try Help, Check for updates again, or download RemSound from\r\n"
                + "  https://github.com/Ednunp/RemSound/releases/latest\r\n"
                + "and unzip it over this folder. Until then the lock-screen service keeps the version it has.\r\n\r\n"
                + "Technical details are in:\r\n  " + logPath + "\r\n");
        }
        catch { /* the log has it */ }
    }

    private static Mutex? TryHoldApplyingLock()
    {
        try { return new Mutex(initiallyOwned: true, InstallRepair.UpdaterMutexName); }
        catch { return null; }   // the lock is a courtesy; the log's age covers an updater without it
    }

    /// <summary>The note for an update found cut off at start-up - the same note, in the same form, as one whose rollback
    /// could not finish, so it is said and cleared the same way (see <see cref="InstallRepair"/>).</summary>
    internal static void WriteInterruptedMarker(string target, List<string> movedAside, string? kept)
    {
        try
        {
            File.WriteAllText(Path.Combine(target, IncompleteMarkerName),
                "An update of RemSound was cut off part way - Windows shut down, or it was closed - so some of its files may be\r\n"
                + "from the new version and some from the old.\r\n\r\n"
                + "These files are not as they were:\r\n" + string.Join("\r\n", movedAside.Select(f => "  " + f)) + "\r\n\r\n"
                + (kept is null ? "" : "Your previous copies of them are kept in:\r\n  " + kept + "\r\n\r\n")
                + "To put it right, try Help, Check for updates again, or download RemSound from\r\n"
                + "  https://github.com/Ednunp/RemSound/releases/latest\r\n"
                + "and unzip it over this folder. Until then the lock-screen service keeps the version it has.\r\n");
        }
        catch { /* the log has it */ }
    }

    private static void WriteFailureMarker(string target, string logPath)
    {
        try
        {
            File.WriteAllText(Path.Combine(target, FailureMarkerName),
                "RemSound could not finish updating, so it put your previous version back exactly as it was.\r\n\r\n"
                + "Nothing is broken. You can try again from Help, Check for updates; it usually works on the\r\n"
                + "next attempt. Technical details are in:\r\n  " + logPath + "\r\n");
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

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }
}
