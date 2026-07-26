using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Housekeeping for the <c>logs\</c> folder, driven by the Logging-tab preferences: report the
/// folder's total size (for the startup over-size warning), prune log files older than N days, and
/// delete every log on demand. All operations are best-effort — a locked or vanished file is
/// skipped, never thrown, because log housekeeping must never stop the app from running.
/// </summary>
internal static class LogMaintenance
{
    /// <summary>Total size in bytes of every <c>*.log</c> file in the logs folder. 0 if the folder
    /// doesn't exist yet or can't be read.</summary>
    public static long LogsFolderSizeBytes()
    {
        try
        {
            var dir = AppConfig.LogsDirectory;
            if (!Directory.Exists(dir)) return 0;
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(dir, "*.log"))
            {
                try { total += new FileInfo(file).Length; } catch { /* vanished mid-scan — skip */ }
            }
            return total;
        }
        catch { return 0; }
    }

    /// <summary>Delete <c>*.log</c> files last written more than <paramref name="days"/> days ago.
    /// The currently-open log (<paramref name="activeLogPath"/>) is always spared even if the clock
    /// makes it look old. Returns how many files were deleted.</summary>
    public static int PruneLogsOlderThan(int days, string? activeLogPath)
    {
        if (days < 1) return 0;
        var deleted = 0;
        try
        {
            var dir = AppConfig.LogsDirectory;
            if (!Directory.Exists(dir)) return 0;
            var cutoff = DateTime.Now.AddDays(-days);
            foreach (var file in Directory.EnumerateFiles(dir, "*.log"))
            {
                if (IsSamePath(file, activeLogPath)) continue;
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                    {
                        File.Delete(file);
                        deleted++;
                    }
                }
                catch { /* locked / in use — leave it */ }
            }
        }
        catch { /* folder enumeration failed — give up quietly */ }
        return deleted;
    }

    /// <summary>Delete every <c>*.log</c> file except the one currently being written
    /// (<paramref name="activeLogPath"/>, which is held open and can't be removed anyway). Returns
    /// how many files were deleted.</summary>
    public static int DeleteAllLogs(string? activeLogPath)
    {
        var deleted = 0;
        try
        {
            var dir = AppConfig.LogsDirectory;
            if (!Directory.Exists(dir)) return 0;
            foreach (var file in Directory.EnumerateFiles(dir, "*.log"))
            {
                if (IsSamePath(file, activeLogPath)) continue;
                try { File.Delete(file); deleted++; }
                catch { /* locked / in use — leave it */ }
            }
        }
        catch { /* give up quietly */ }
        return deleted;
    }

    /// <summary>Keep only the newest <paramref name="keep"/> crash reports (<c>crash-*.txt</c>) in
    /// <paramref name="dir"/>, deleting older ones. Crash files were the one log-folder artefact
    /// nothing ever cleaned (2026-07-26 resource audit — the *.log pruning never matched them), so
    /// they accumulated forever. Runs unconditionally at startup: unlike log pruning this is not
    /// opt-in, because ten reports diagnose a crash pattern just as well as a hundred. Returns how
    /// many were deleted. Best-effort throughout.</summary>
    public static int PruneCrashReports(string dir, int keep = 10)
    {
        var deleted = 0;
        try
        {
            if (!Directory.Exists(dir)) return 0;
            var crashes = Directory.EnumerateFiles(dir, "crash-*.txt")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(keep);
            foreach (var old in crashes)
            {
                try { old.Delete(); deleted++; } catch { /* locked — leave it */ }
            }
        }
        catch { /* give up quietly */ }
        return deleted;
    }

    private static bool IsSamePath(string a, string? b) =>
        !string.IsNullOrEmpty(b)
        && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
