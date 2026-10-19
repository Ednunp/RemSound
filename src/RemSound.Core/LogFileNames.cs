namespace RemSound.Core;

/// <summary>
/// What the app's and the plugin's log files are called (2026-10-19, Jonathan Schuster's issue 35: "a bunch of files with
/// random IDs, the date is at the very end"). The date and time come first, so the folder sorts by when each session
/// started and the newest is last; then the computer; then, for the plugin, which instance. The process number, which
/// used to lead, only appears when two copies start in the same minute and would otherwise share a name.
/// </summary>
public static class LogFileNames
{
    /// <summary>The app's: <c>RemSound-2026-10-19-0849-ED_DT</c>.</summary>
    public static string AppStem(DateTime started) => $"RemSound-{started:yyyy-MM-dd-HHmm}-{Machine()}";

    /// <summary>The plugin's, one per instance, since a DAW loads every instance into one process:
    /// <c>RemSoundPlugin-2026-10-19-0849-ED_DT-2d570fff</c>.</summary>
    public static string PluginStem(DateTime started, string shortInstanceId) => $"RemSoundPlugin-{started:yyyy-MM-dd-HHmm}-{Machine()}-{shortInstanceId}";

    /// <summary>
    /// Make the file: <paramref name="stem"/>, then "-part2" and on for a session's later files. If that name is taken - a
    /// second copy started in the same minute, or a test making several logs in one process - the process number goes in,
    /// then a count. Never opens a file that already exists: a log must not write into somebody else's. Returns the stem
    /// it settled on, which the session passes back for its later parts so they carry the same name.
    /// </summary>
    public static (string Path, FileStream Stream, string Stem) CreateNew(string directory, string stem, int part)
    {
        var suffix = part > 1 ? $"-part{part}" : "";
        var pid = Environment.ProcessId;
        IOException? last = null;
        foreach (var middle in Middles(pid))
        {
            var path = System.IO.Path.Combine(directory, stem + middle + suffix + ".log");
            if (File.Exists(path)) continue;
            try { return (path, new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite), stem + middle); }
            catch (IOException ex) when (File.Exists(path)) { last = ex; }   // taken between the check and the open
        }
        throw last ?? new IOException($"every name for {stem} is taken in {directory}");
    }

    private static IEnumerable<string> Middles(int pid)
    {
        yield return "";
        yield return $"-{pid}";
        for (var n = 2; n <= 50; n++) yield return $"-{pid}-{n}";
    }

    private static string Machine()
    {
        var name = Environment.MachineName;
        foreach (var bad in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
        return name;
    }
}
