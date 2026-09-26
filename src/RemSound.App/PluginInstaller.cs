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
    /// <summary>Where the plugin is: the folder chosen when it was installed somewhere else (Ed, 2026-09-25, remembered
    /// in <see cref="AppConfig.PluginFolder"/>), or the standard place.</summary>
    public static string InstallDirectory => AppConfig.Load().PluginFolder is { Length: > 0 } chosen ? chosen : StandardDirectory;

    /// <summary>The standard place: the per-user VST3 location every current DAW scans. No admin rights needed, and it
    /// survives a RemSound reinstall because it lives in the user profile, not next to the exe.</summary>
    public static string StandardDirectory => AppConfig.ThrowawayMachineDirectory is { } throwaway
        // A run that touches nothing real (the self-test, a --config-dir start) installs into its own folder: a test that
        // chose Install plugin or Remove plugin from the menu would otherwise replace or delete the plugin every DAW on
        // the computer loads (2026-09-24).
        ? Path.Combine(throwaway, "VST3", "RemSound")
        : RealInstallDirectory;

    /// <summary>The folder the plugin goes in when <paramref name="chosen"/> is picked: a RemSound folder inside it, so its
    /// files never mix with other plugins' - unless the folder picked is already RemSound's own.</summary>
    internal static string TargetFor(string chosen)
    {
        var trimmed = chosen.TrimEnd('\\', '/');
        return string.Equals(Path.GetFileName(trimmed), "RemSound", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : Path.Combine(trimmed, "RemSound");
    }

    /// <summary>Whether two folder paths are the same folder.</summary>
    internal static bool SameFolder(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The context help sounds the plugin plays inside a DAW, which can't reach RemSound's own settings: the
    /// sounds chosen in Preferences are copied beside it under these names, or left out when that sound is off.</summary>
    internal const string HelpOpenSoundName = "help open.wav";
    internal const string HelpCloseSoundName = "help close.wav";

    /// <summary>The elevated helper verbs: RemSound re-launched with Windows' permission to put the plugin in, or take it
    /// out of, a folder only an administrator can write. They do the copy and nothing else - the settings are the caller's.</summary>
    internal const string InstallToVerb = "--plugin-install-to";
    internal const string RemoveFromVerb = "--plugin-remove-from";

    /// <summary>The elevated helper is told which context help sounds to put beside the plugin: Windows may run it as a
    /// different account (an administrator's, when the person signed in is not one), whose settings are not theirs.</summary>
    internal const string HelpOpenArg = "--help-open";
    internal const string HelpCloseArg = "--help-close";

    /// <summary>Gate seams: whether a folder can be written without Windows' permission, and the elevated helper (given
    /// the arguments RemSound would be re-launched with) returning its exit code - 0 done, -1 permission refused.</summary>
    internal static Func<string, bool>? CanWriteForTest;
    internal static Func<string, int>? ElevateForTest;

    /// <summary>Whether this user can write the plugin into <paramref name="target"/> without asking Windows.</summary>
    internal static bool CanWrite(string target)
    {
        if (CanWriteForTest is { } seam) return seam(target);
        try
        {
            Directory.CreateDirectory(target);
            var probe = Path.Combine(target, ".remsound-write-check-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    /// <summary>The arguments RemSound is re-launched with, with Windows' permission, to run one plugin helper verb on one
    /// folder. Paths are quoted whole; a Windows path can't hold a quote, so none can break out.</summary>
    internal static string ElevatedArguments(string verb, string folder)
    {
        var line = $"{verb} \"{folder.TrimEnd('\\', '/')}\"";
        if (verb == InstallToVerb)
        {
            var (open, close) = ChosenHelpSounds();
            if (open is not null) line += $" {HelpOpenArg} \"{open}\"";
            if (close is not null) line += $" {HelpCloseArg} \"{close}\"";
        }
        return line;
    }

    /// <summary>Whether this launch is the plugin's elevated helper. Checked first thing in Main, like the service verbs.</summary>
    internal static bool IsElevatedHelper(string[] args) =>
        Array.Exists(args, a => a.Equals(InstallToVerb, StringComparison.OrdinalIgnoreCase) || a.Equals(RemoveFromVerb, StringComparison.OrdinalIgnoreCase));

    /// <summary>Re-launch RemSound with Windows' permission to run one plugin helper verb on one folder, and wait (bounded).
    /// 0 done, 1 failed, -1 permission refused or not possible, -2 took too long.</summary>
    private static int RunElevated(string verb, string folder)
    {
        var arguments = ElevatedArguments(verb, folder);
        if (ElevateForTest is { } seam) return seam(arguments);
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return -1;
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = exe,
            Arguments = arguments,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return -1;
            if (!p.WaitForExit(120000)) return -2;
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception) { return -1; }   // the permission prompt was declined
        catch { return -1; }
    }

    /// <summary>The elevated helper's own work: put the plugin in, or take it out of, the folder named after
    /// <see cref="InstallToVerb"/> or <see cref="RemoveFromVerb"/>, in the copy of RemSound Windows started with
    /// permission. It copies and removes files and nothing else - remembering the folder is the caller's. 0 done, 1 failed.</summary>
    internal static int RunElevatedHelper(string[] args)
    {
        string? After(string flag)
        {
            var i = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : null;
        }
        var install = Array.Exists(args, a => a.Equals(InstallToVerb, StringComparison.OrdinalIgnoreCase));
        var folder = After(install ? InstallToVerb : RemoveFromVerb);
        // A whole path only: a relative one would land wherever Windows started the helper.
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder)) return 1;
        string? Sound(string flag) => After(flag) is { } p && Path.IsPathFullyQualified(p) && File.Exists(p) ? p : null;
        var (ok, _) = install
            ? InstallCore(SourceDirectory, folder, CommandLine.AppVersion, (Sound(HelpOpenArg), Sound(HelpCloseArg)))
            : UninstallCore(folder);
        return ok ? 0 : 1;
    }

    /// <summary>
    /// Install the plugin into <paramref name="target"/> - the standard place or a folder the person chose - and remember
    /// it, so updates go there and Remove plugin takes it from there. Moving it removes the copy RemSound put in the old
    /// folder (only RemSound's own files: the manifest). A folder only an administrator can write asks Windows for
    /// permission (Ed agreed, 2026-09-25).
    /// </summary>
    public static (bool Ok, string Message) InstallTo(string target)
    {
        var previous = InstallDirectory;
        var hadCopy = IsInstalledAt(previous);
        (bool Ok, string Message) result;
        var installed = $"The RemSound plugin is installed in {target}. Restart your music software and it will appear in its list of effects.";
        if (CanWrite(target))
        {
            result = InstallCore(SourceDirectory, target, CommandLine.AppVersion);
            if (result.Ok) result = (true, installed);
        }
        else
        {
            var rc = RunElevated(InstallToVerb, target);
            result = rc switch
            {
                0 => (true, installed),
                -1 => (false, $"The plugin was not installed: putting it in {target} needs Windows' permission, and it wasn't given."),
                -2 => (false, "The plugin was not installed: the copy with Windows' permission took too long to finish."),
                _ => (false, $"The plugin could not be installed in {target}."),
            };
        }
        if (!result.Ok) return result;

        var cfg = AppConfig.Load();
        cfg.PluginFolder = SameFolder(target, StandardDirectory) ? null : target;
        try { cfg.Save(); } catch { /* the plugin is in place either way; the next install remembers */ }

        if (hadCopy && !SameFolder(previous, target))
        {
            var (removed, _) = CanWrite(previous) ? UninstallCore(previous) : (RunElevated(RemoveFromVerb, previous) == 0, "");
            result = removed
                ? (true, result.Message + $" The copy RemSound had put in {previous} has been removed.")
                : (true, result.Message + $" The copy RemSound had put in {previous} could not be removed; remove it yourself if your music software lists RemSound twice.");
        }
        return result;
    }

    /// <summary>The DAW plugin menu's Install, into wherever it is now (the standard place unless it was put elsewhere).
    /// The menu asks where first; this is the command line's and the scripts' plain install.</summary>
    public static (bool Ok, string Message) Install() => InstallTo(InstallDirectory);

    /// <summary><c>--install-plugin [folder]</c>, the scripts' install: into <paramref name="folder"/> (its own RemSound
    /// folder inside it) when one is given, where it is now when not. The menu's own code, as the scripts must be.</summary>
    public static (bool Ok, string Message) InstallFromCommandLine(string? folder) =>
        string.IsNullOrWhiteSpace(folder) ? Install()
        : !Path.IsPathFullyQualified(folder) ? (false, $"\"{folder}\" is not a whole folder path, such as C:\\Program Files\\Common Files\\VST3, so the plugin was not installed.")
        : InstallTo(TargetFor(folder));

    /// <summary>Put the context help sounds chosen in Preferences beside an installed plugin - or take one away when that
    /// sound is switched off - so the plugin, which can't read RemSound's settings, follows them. Called at install, at
    /// every refresh, and when the cue settings change. A folder that needs Windows' permission is left until the next
    /// install or update, rather than asking for permission over a sound. Returns whether anything changed.</summary>
    public static bool RefreshHelpSounds() => RefreshHelpSoundsAt(InstallDirectory);

    internal static bool RefreshHelpSoundsAt(string target)
    {
        if (!IsInstalledAt(target) || !CanWrite(target)) return false;
        try { return PlaceHelpSounds(target); } catch { return false; }
    }

    /// <summary>The context help sound files chosen in Preferences, or null for one that is switched off.</summary>
    internal static (string? Open, string? Close) ChosenHelpSounds()
    {
        var cfg = AppConfig.Load();
        return (HelpSoundService.ChosenFile(open: true, cfg), HelpSoundService.ChosenFile(open: false, cfg));
    }

    private static bool PlaceHelpSounds(string target, (string? Open, string? Close)? sounds = null)
    {
        var (open, close) = sounds ?? ChosenHelpSounds();
        var changed = false;
        foreach (var (name, chosen) in new[] { (HelpOpenSoundName, open), (HelpCloseSoundName, close) })
        {
            var destination = Path.Combine(target, name);
            if (chosen is null)
            {
                if (File.Exists(destination)) { File.Delete(destination); changed = true; }
                continue;
            }
            if (File.Exists(destination) && new FileInfo(destination).Length == new FileInfo(chosen).Length
                && File.ReadAllBytes(destination).AsSpan().SequenceEqual(File.ReadAllBytes(chosen))) continue;
            File.Copy(chosen, destination, overwrite: true);
            changed = true;
        }
        return changed;
    }

    /// <summary>The real per-user VST3 folder, whatever run this is.</summary>
    public static string RealInstallDirectory => Path.Combine(
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

    /// <summary>Called at startup: if the plugin is installed and was placed by a different RemSound build, copy
    /// this build's plugin over it. Returns whether it refreshed, and a line for the log when something happened.
    /// A DAW that has the plugin loaded holds its files open; the copy then fails, nothing is half-replaced beyond
    /// what the copy managed, the stamp is not updated, and the next launch tries again.</summary>
    public static (bool Refreshed, string? Message) RefreshIfStale() =>
        RefreshIfStaleCore(SourceDirectory, InstallDirectory, CommandLine.AppVersion);

    /// <summary>The build that placed the plugin in <paramref name="target"/>, or null when unreadable or not there.</summary>
    internal static string? StampAt(string target)
    {
        try
        {
            var stampPath = Path.Combine(target, VersionStampName);
            return File.Exists(stampPath) ? File.ReadAllText(stampPath).Trim() : null;
        }
        catch { return null; }
    }

    /// <summary>Whether the plugin is installed and was placed by a different RemSound build: RemSound has been updated
    /// since, and the plugin needs to follow it.</summary>
    internal static bool IsStaleAt(string target, string version) =>
        IsInstalledAt(target) && !string.Equals(StampAt(target), version, StringComparison.Ordinal);

    /// <summary>Whether bringing the plugin up to this build needs Windows' permission first: it is out of date, in a
    /// folder this user can't change. Start-up asks the person then, rather than failing quietly at every launch.</summary>
    internal static bool RefreshNeedsPermission(string target, string version) => IsStaleAt(target, version) && !CanWrite(target);

    private static (bool Refreshed, string? Message) RefreshIfStaleCore(string source, string target, string version)
    {
        if (!IsStaleAt(target, version)) return (false, null);
        var stamped = StampAt(target);

        var (ok, message) = InstallCore(source, target, version);
        return ok
            ? (true, $"DAW plugin: refreshed the installed plugin for RemSound {version} (it was placed by {stamped ?? "an older build"})")
            : (false, $"DAW plugin: the installed plugin is from {stamped ?? "an older build"} and could not be refreshed yet — {message}");
    }

    private static (bool Ok, string Message) InstallCore(string source, string target, string version, (string? Open, string? Close)? helpSounds = null)
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

            // The context help sounds, as chosen in Preferences. Listed in the manifest whether or not either is there
            // now, so a later Remove plugin takes away whatever a later change of sound put in.
            try { PlaceHelpSounds(target, helpSounds); } catch { /* the plugin works without its help sounds */ }
            placed.Add(HelpOpenSoundName);
            placed.Add(HelpCloseSoundName);

            // The stamp and the manifest go LAST: if the copy fails half way, neither is written, so a later
            // uninstall won't claim to have cleanly removed an install that never completed, and a later launch
            // still sees the plugin as out of date and tries again.
            File.WriteAllText(Path.Combine(target, VersionStampName), version);
            placed.Add(VersionStampName);
            File.WriteAllLines(Path.Combine(target, ManifestName), placed);
            return (true, $"The RemSound plugin is installed. Restart your music software and it will appear in its list of effects. ({files.Length} files.)");
        }
        catch (Exception ex)
        {
            return (false, $"The plugin could not be installed: {ex.Message}");
        }
    }

    /// <summary>Remove exactly the files install placed, from wherever the plugin is. Never a wildcard sweep — the VST3
    /// folder belongs to every plugin the user owns. A folder only an administrator can write asks Windows first. Once
    /// removed, the folder is forgotten: the next install is in the standard place unless another is chosen.</summary>
    public static (bool Ok, string Message) Uninstall()
    {
        var target = InstallDirectory;
        (bool Ok, string Message) result;
        if (!IsInstalledAt(target) || CanWrite(target)) result = UninstallCore(target);
        else
        {
            var rc = RunElevated(RemoveFromVerb, target);
            result = rc == 0
                ? (true, "The RemSound plugin has been removed. Restart your music software for it to disappear from its list of effects.")
                : (false, rc == -1
                    ? $"The plugin was not removed: taking it out of {target} needs Windows' permission, and it wasn't given."
                    : $"The plugin could not be removed from {target}.");
        }
        if (result.Ok)
        {
            var cfg = AppConfig.Load();
            if (cfg.PluginFolder is not null)
            {
                cfg.PluginFolder = null;
                try { cfg.Save(); } catch { /* best effort */ }
            }
        }
        return result;
    }

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

            return (true, $"The RemSound plugin has been removed. ({removed} files.) Restart your music software for it to disappear from its list of effects.");
        }
        catch (Exception ex)
        {
            return (false, $"The plugin could not be removed: {ex.Message}");
        }
    }
}
