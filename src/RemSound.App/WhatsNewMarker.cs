namespace RemSound.App;

/// <summary>
/// The one-shot "an update just succeeded" marker that drives the "what's new" popup. The in-app
/// updater (<see cref="UpdateApplier"/>) writes it into the install folder ONLY when a swap completes
/// successfully — never on a failed/rolled-back update — and MainForm consumes it (shows About once,
/// then deletes it) on the next launch. Because the signal is positive and written only on success, a
/// failed update can't re-trigger what's-new, which the old version-compare flag could.
///
/// Kept as a tiny seam (rather than inline file calls) so the consume contract is unit-testable: write
/// it into a throwaway folder, assert Exists, Consume once (true), then Exists is false and a second
/// Consume is false. See the SelfTest "what's-new marker" case.
/// </summary>
internal static class WhatsNewMarker
{
    private static string PathFor(string baseDir) => Path.Combine(baseDir, RemSoundUpdater.WhatsNewMarkerName);

    /// <summary>True if the success marker is present in <paramref name="baseDir"/> (the install folder).</summary>
    public static bool Exists(string baseDir)
    {
        try { return File.Exists(PathFor(baseDir)); }
        catch { return false; }
    }

    /// <summary>Write the marker into <paramref name="baseDir"/>. Called by the updater on success only.</summary>
    public static void Write(string baseDir)
    {
        File.WriteAllText(PathFor(baseDir),
            "One-shot marker: RemSound finished a successful update. \"What's new\" shows once on the next\r\n"
            + "launch, then this file is deleted. Safe to delete.\r\n");
    }

    /// <summary>Delete the marker if present, so what's-new shows exactly once. Returns true if a marker
    /// was there (and is now gone); a delete is reliable in a way the old best-effort config-save wasn't.</summary>
    public static bool Consume(string baseDir)
    {
        try
        {
            var path = PathFor(baseDir);
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch { return false; }
    }
}
