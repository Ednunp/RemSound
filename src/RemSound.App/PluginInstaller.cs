using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Puts the RemSound VST3 plugin where a DAW will find it, and takes it away again.
///
/// <para><b>Per-user, deliberately</b> (Ed, 2026-08-15: "a lot of people run it as portable"). The
/// system-wide plugin folder under Program Files needs administrator rights, and a portable copy of
/// RemSound that demands a UAC prompt to be useful isn't portable. Windows has supported a per-user
/// VST3 folder for exactly this, so installing needs no elevation at all and touches nothing outside
/// the user's own profile.</para>
///
/// <para><b>A menu item, not a script</b> — also Ed's call, and the right one: a folder of install
/// scripts is a command line wearing a hat, and this app's users navigate by screen reader.</para>
///
/// <para><b>Uninstall removes exactly what install added.</b> The plugin folder is shared with every
/// other plugin the user owns, so a wildcard sweep there would be catastrophic. Install writes a
/// MANIFEST of the files it placed, and uninstall removes only those, then removes the directory
/// only if it is empty. Anything it did not put there, it does not touch.</para>
///
/// <para><b>An app update refreshes it.</b> The installed plugin used to be refreshed only from the menu, so
/// after an update the DAW kept loading the old plugin — and the first time the plugin link changes, an old
/// plugin could no longer connect to the new app. The version that placed the files is stamped beside them,
/// and a launch that finds a different stamp copies its own plugin over. 2026-09-13 review.</para>
/// </summary>
internal static class PluginInstaller
{
    /// <summary>The per-user VST3 location every current DAW scans. No admin rights needed, and it
    /// survives a RemSound reinstall because it lives in the user profile, not next to the exe.</summary>
    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "Common", "VST3", "RemSound");

    /// <summary>Written at install time listing every file placed, so uninstall can be exact rather
    /// than enthusiastic. Kept inside our own subfolder alongside the files it describes.
    ///
    /// <para>Internal rather than private so the gate can tamper with it: the path-containment guard
    /// in <see cref="UninstallCore"/> had no test at all until 2026-08-24, and what it protects is
    /// somebody ELSE's plugin.</para></summary>
    internal const string ManifestName = "remsound-plugin-files.txt";

    /// <summary>Which RemSound build placed the files. Written beside them and listed in the manifest like any
    /// other placed file, so uninstall removes it too.</summary>
    internal const string VersionStampName = "remsound-plugin-version.txt";

    /// <summary>Where the built plugin files sit in a RemSound install — a <c>plugin\</c> folder next
    /// to the exe, populated by the build.</summary>
    public static string SourceDirectory => Path.Combine(AppContext.BaseDirectory, "plugin");

    public static bool IsInstalled() => IsInstalledAt(InstallDirectory);

    // Explicit-folder seams. The gate drives these against throwaway directories: a test that
    // installed into the user's REAL VST3 folder would be modifying their machine to prove a point.
    internal static bool IsInstalledAt(string dir) => File.Exists(Path.Combine(dir, ManifestName));
    internal static (bool Ok, string Message) InstallForTest(string source, string target, string version = "test") => InstallCore(source, target, version);
    internal static (bool Ok, string Message) UninstallForTest(string target) => UninstallCore(target);
    internal static (bool Refreshed, string? Message) RefreshIfStaleForTest(string source, string target, string version) =>
        RefreshIfStaleCore(source, target, version);

    /// <summary>Copy the plugin into the user's VST3 folder. Idempotent — running it again over an
    /// existing install refreshes the files and rewrites the manifest. Returns a plain-English result for
    /// the dialog.</summary>
    public static (bool Ok, string Message) Install() => InstallCore(SourceDirectory, InstallDirectory, CommandLine.AppVersion);

    /// <summary>Called at startup: if the plugin is installed and was placed by a different RemSound build, copy
    /// this build's plugin over it. Returns whether it refreshed, and a line for the log when something happened.
    /// A DAW that has the plugin loaded holds its files open; the copy then fails, nothing is half-replaced beyond
    /// what the copy managed, the stamp is not updated, and the next launch tries again.</summary>
    public static (bool Refreshed, string? Message) RefreshIfStale() =>
        RefreshIfStaleCore(SourceDirectory, InstallDirectory, CommandLine.AppVersion);

    private static (bool Refreshed, string? Message) RefreshIfStaleCore(string source, string target, string version)
    {
        if (!IsInstalledAt(target)) return (false, null);
        string? stamped = null;
        try
        {
            var stampPath = Path.Combine(target, VersionStampName);
            if (File.Exists(stampPath)) stamped = File.ReadAllText(stampPath).Trim();
        }
        catch { /* unreadable stamp: treat as stale */ }
        if (string.Equals(stamped, version, StringComparison.Ordinal)) return (false, null);

        var (ok, message) = InstallCore(source, target, version);
        return ok
            ? (true, $"DAW plugin: refreshed the installed plugin for RemSound {version} (it was placed by {stamped ?? "an older build"})")
            : (false, $"DAW plugin: the installed plugin is from {stamped ?? "an older build"} and could not be refreshed yet — {message}");
    }

    private static (bool Ok, string Message) InstallCore(string source, string target, string version)
    {
        try
        {
            if (!Directory.Exists(source))
                return (false, "The plugin files are missing from this copy of RemSound, so there is nothing to install.");

            var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
            if (files.Length == 0)
                return (false, "The plugin folder in this copy of RemSound is empty, so there is nothing to install.");

            Directory.CreateDirectory(target);
            var placed = new List<string>();
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(source, file);
                var destination = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
                placed.Add(relative);
            }

            // The stamp and the manifest go LAST: if the copy fails half way, neither is written, so a later
            // uninstall won't claim to have cleanly removed an install that never completed, and a later launch
            // still sees the plugin as out of date and tries again.
            File.WriteAllText(Path.Combine(target, VersionStampName), version);
            placed.Add(VersionStampName);
            File.WriteAllLines(Path.Combine(target, ManifestName), placed);
            return (true, $"The RemSound plugin is installed. Restart your music software and it will appear in its effects list. ({files.Length} files.)");
        }
        catch (Exception ex)
        {
            return (false, $"The plugin could not be installed: {ex.Message}");
        }
    }

    /// <summary>Remove exactly the files install placed. Never a wildcard sweep — the VST3 folder
    /// belongs to every plugin the user owns.</summary>
    public static (bool Ok, string Message) Uninstall() => UninstallCore(InstallDirectory);

    private static (bool Ok, string Message) UninstallCore(string target)
    {
        try
        {
            var manifestPath = Path.Combine(target, ManifestName);
            if (!File.Exists(manifestPath))
                return (false, "The RemSound plugin does not appear to be installed, so there is nothing to remove.");

            var removed = 0;
            foreach (var relative in File.ReadAllLines(manifestPath))
            {
                if (string.IsNullOrWhiteSpace(relative)) continue;
                var file = Path.Combine(target, relative);
                // Stay inside our own folder even if the manifest were tampered with — INSIDE it, not merely
                // sharing its first letters: VST3\RemSoundX belongs to somebody else.
                if (!PathContainment.IsInside(file, target)) continue;
                if (File.Exists(file)) { File.Delete(file); removed++; }
            }
            File.Delete(manifestPath);

            // Remove our folder only when nothing else has appeared in it.
            try { if (Directory.Exists(target) && Directory.GetFileSystemEntries(target).Length == 0) Directory.Delete(target); }
            catch { /* leaving an empty folder behind is harmless */ }

            return (true, $"The RemSound plugin has been removed. ({removed} files.) Restart your music software for it to disappear from its effects list.");
        }
        catch (Exception ex)
        {
            return (false, $"The plugin could not be removed: {ex.Message}");
        }
    }
}
