using System.Diagnostics;

namespace RemSound.App;

/// <summary>
/// What an update can leave behind in the install folder, looked at each time RemSound starts (2026-09-25 sweep; Ed: build
/// them all before the release).
///
/// <para>Three things were left for good. An update cut off half way - Windows shutting down, the updater killed - left
/// <c>_update-backup</c> in the install, and nothing ever cleared it: nobody was told, and the lock-screen service read it
/// as an update still being applied and never updated from that folder again. The note about an update that could not be
/// put back stayed after the fixes it recommends (unzipping RemSound over the folder, or an update check that finds the
/// version already there). And the old files kept from such a rollback were never removed.</para>
///
/// <para>Now: a cut-off update is kept aside and said, in the same note, so the person hears it once at the next start and
/// the service waits; the note clears itself once every RemSound program file is the version running and every file it
/// names is there; and the kept old files go once no note remains. Nothing is touched while an update is being applied.</para>
/// </summary>
internal static class InstallRepair
{
    /// <summary>Held by <see cref="UpdateApplier"/> for as long as it is changing files, so nothing here touches a live swap.</summary>
    internal const string UpdaterMutexName = @"Global\RemSound.Updater.Applying";

    /// <summary>An updater that wrote to its log this recently may still be at work, even one from before the mutex.</summary>
    internal static readonly TimeSpan UpdaterQuietFor = TimeSpan.FromMinutes(2);

    /// <summary>Look at the install folder and put right what an update left. Returns what it did, for the log. Never
    /// throws.</summary>
    internal static List<string> CheckAtStart(string dir, Version? running, Func<string, string?> versionOf, Func<string, bool> updaterBusy)
    {
        var said = new List<string>();
        try
        {
            if (updaterBusy(dir)) { said.Add("install check: an update is being applied right now - left alone"); return said; }

            // 1. An update cut off half way.
            var backup = Path.Combine(dir, UpdateApplier.BackupFolderName);
            if (Directory.Exists(backup))
            {
                var files = Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(backup, f)).ToList();
                if (files.Count == 0)
                {
                    Directory.Delete(backup, recursive: true);
                    said.Add("install check: an empty update backup was left behind - removed");
                }
                else
                {
                    var kept = UpdateApplier.KeepOrDropBackup(backup, dir, said.Add);
                    UpdateApplier.WriteInterruptedMarker(dir, files, kept);
                    said.Add($"install check: an update was cut off part way ({files.Count} file(s) had been moved aside) - kept them in "
                        + $"{kept ?? "(could not keep them)"}, and wrote the note so it is said at start-up");
                }
            }

            // 2. The note, once the install is whole again.
            var marker = Path.Combine(dir, UpdateApplier.IncompleteMarkerName);
            if (File.Exists(marker) && IsWhole(dir, running, versionOf, FilesNamedIn(File.ReadAllText(marker))))
            {
                File.Delete(marker);
                said.Add($"install check: every RemSound file is version {running} and every file the note named is there - the note about the unfinished update is cleared");
            }

            // 5. Old files kept from a rollback, once nothing needs them.
            if (!File.Exists(marker))
            {
                foreach (var kept in Directory.GetDirectories(dir, UpdateApplier.KeptBackupPrefix + "*"))
                {
                    try
                    {
                        Directory.Delete(kept, recursive: true);
                        said.Add($"install check: the install is whole, so the old files kept in {Path.GetFileName(kept)} are removed");
                    }
                    catch (Exception ex) { said.Add($"install check: could not remove {Path.GetFileName(kept)}: {ex.Message}"); }
                }
            }
        }
        catch (Exception ex) { said.Add($"install check: {ex.GetType().Name}: {ex.Message}"); }
        return said;
    }

    /// <summary>Whole: RemSound's own program files are all there and all the version running, and every file the note
    /// named is there. Unknown - no program files to look at, or no running version - is not whole.</summary>
    internal static bool IsWhole(string dir, Version? running, Func<string, string?> versionOf, IReadOnlyList<string> named)
    {
        if (running is null || !Directory.Exists(dir)) return false;
        var own = Directory.GetFiles(dir, "RemSound*.dll").Append(Path.Combine(dir, "RemSound.exe")).Where(File.Exists).ToList();
        if (!own.Any(f => string.Equals(Path.GetFileName(f), "RemSound.dll", StringComparison.OrdinalIgnoreCase))) return false;
        foreach (var file in own)
        {
            if (!Version.TryParse(versionOf(file), out var version) || version != running) return false;
        }
        return named.All(rel => File.Exists(Path.Combine(dir, rel)));
    }

    /// <summary>The files a note names: the indented lines after "These files are not as they were:".</summary>
    internal static List<string> FilesNamedIn(string note)
    {
        var lines = note.Replace("\r\n", "\n").Split('\n');
        var at = Array.FindIndex(lines, l => l.StartsWith("These files are not as they were:", StringComparison.Ordinal));
        var named = new List<string>();
        if (at < 0) return named;
        for (var i = at + 1; i < lines.Length && lines[i].StartsWith("  ", StringComparison.Ordinal); i++) named.Add(lines[i].Trim());
        return named;
    }

    /// <summary>An updater at work: its lock held, or its log written in the last <see cref="UpdaterQuietFor"/>.</summary>
    internal static bool UpdaterBusy(string dir)
    {
        try
        {
            if (Mutex.TryOpenExisting(UpdaterMutexName, out var held)) { held.Dispose(); return true; }
        }
        catch (UnauthorizedAccessException) { return true; }   // exists, but not ours to open: somebody holds it
        var log = Path.Combine(dir, "updater.log");
        return File.Exists(log) && DateTime.UtcNow - File.GetLastWriteTimeUtc(log) < UpdaterQuietFor;
    }

    internal static string? FileVersionOf(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path).FileVersion; }
        catch { return null; }
    }
}
