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
/// </summary>
internal static class PluginInstaller
{
    /// <summary>The per-user VST3 location every current DAW scans. No admin rights needed, and it
    /// survives a RemSound reinstall because it lives in the user profile, not next to the exe.</summary>
    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "Common", "VST3", "RemSound");

    /// <summary>Written at install time listing every file placed, so uninstall can be exact rather
    /// than enthusiastic. Kept inside our own subfolder alongside the files it describes.</summary>
    private const string ManifestName = "remsound-plugin-files.txt";

    /// <summary>Where the built plugin files sit in a RemSound install — a <c>plugin\</c> folder next
    /// to the exe, populated by the build.</summary>
    public static string SourceDirectory => Path.Combine(AppContext.BaseDirectory, "plugin");

    public static bool IsInstalled() => IsInstalledAt(InstallDirectory);

    // Explicit-folder seams. The gate drives these against throwaway directories: a test that
    // installed into the user's REAL VST3 folder would be modifying their machine to prove a point.
    internal static bool IsInstalledAt(string dir) => File.Exists(Path.Combine(dir, ManifestName));
    internal static (bool Ok, string Message) InstallForTest(string source, string target) => InstallCore(source, target);
    internal static (bool Ok, string Message) UninstallForTest(string target) => UninstallCore(target);

    /// <summary>Copy the plugin into the user's VST3 folder. Idempotent — running it again over an
    /// existing install refreshes the files (which is exactly what a RemSound update wants to do)
    /// and rewrites the manifest. Returns a plain-English result for the dialog.</summary>
    public static (bool Ok, string Message) Install() => InstallCore(SourceDirectory, InstallDirectory);

    private static (bool Ok, string Message) InstallCore(string SourceDirectory, string InstallDirectory)
    {
        try
        {
            if (!Directory.Exists(SourceDirectory))
                return (false, "The plugin files are missing from this copy of RemSound, so there is nothing to install.");

            var files = Directory.GetFiles(SourceDirectory, "*", SearchOption.AllDirectories);
            if (files.Length == 0)
                return (false, "The plugin folder in this copy of RemSound is empty, so there is nothing to install.");

            Directory.CreateDirectory(InstallDirectory);
            var placed = new List<string>();
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(SourceDirectory, file);
                var target = Path.Combine(InstallDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
                placed.Add(relative);
            }

            // The manifest goes LAST: if the copy fails half way, there is no manifest, so a later
            // uninstall won't claim to have cleanly removed an install that never completed.
            File.WriteAllLines(Path.Combine(InstallDirectory, ManifestName), placed);
            return (true, $"The RemSound plugin is installed. Restart your music software and it will appear in its effects list. ({placed.Count} files.)");
        }
        catch (Exception ex)
        {
            return (false, $"The plugin could not be installed: {ex.Message}");
        }
    }

    /// <summary>Remove exactly the files install placed. Never a wildcard sweep — the VST3 folder
    /// belongs to every plugin the user owns.</summary>
    public static (bool Ok, string Message) Uninstall() => UninstallCore(InstallDirectory);

    private static (bool Ok, string Message) UninstallCore(string InstallDirectory)
    {
        try
        {
            var manifestPath = Path.Combine(InstallDirectory, ManifestName);
            if (!File.Exists(manifestPath))
                return (false, "The RemSound plugin does not appear to be installed, so there is nothing to remove.");

            var removed = 0;
            foreach (var relative in File.ReadAllLines(manifestPath))
            {
                if (string.IsNullOrWhiteSpace(relative)) continue;
                var target = Path.Combine(InstallDirectory, relative);
                // Stay inside our own folder even if the manifest were tampered with.
                if (!Path.GetFullPath(target).StartsWith(Path.GetFullPath(InstallDirectory), StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(target)) { File.Delete(target); removed++; }
            }
            File.Delete(manifestPath);

            // Remove our folder only when nothing else has appeared in it.
            try { if (Directory.Exists(InstallDirectory) && Directory.GetFileSystemEntries(InstallDirectory).Length == 0) Directory.Delete(InstallDirectory); }
            catch { /* leaving an empty folder behind is harmless */ }

            return (true, $"The RemSound plugin has been removed. ({removed} files.) Restart your music software for it to disappear from its effects list.");
        }
        catch (Exception ex)
        {
            return (false, $"The plugin could not be removed: {ex.Message}");
        }
    }
}
