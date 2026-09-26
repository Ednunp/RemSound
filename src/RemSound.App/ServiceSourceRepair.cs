using System.Diagnostics;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Points an installed service at THIS installed copy for its updates, when it is watching a folder no update will ever
/// reach.
///
/// <para>Found 2026-09-24 checking the update from 5.9. "Install RemSound on this PC" in 5.3 to 5.9 ran the service's
/// elevated helper from the portable copy, so the service recorded the PORTABLE folder as the one to watch. Updates land in
/// the installed copy, so the service watched a folder that never changed - or, once the portable folder was deleted,
/// nothing at all - and stayed on 5.9 for good. 6.0's installer points it at the right folder, but only when it installs;
/// nothing put right a service installed before.</para>
///
/// <para>Deliberately free of the Windows service library: this runs from the main window's start-up, which must never load
/// it (Windows 7 can't). It asks the service's own records instead - the folder it watches, and the copy of the program in
/// its bin, which is there only when a service was installed. No administrator rights: the account that installed the
/// service can write its folder, which is what 6.0's installer relies on too; any other account simply can't, and nothing
/// changes.</para>
/// </summary>
internal static class ServiceSourceRepair
{
    /// <summary>Pure: the folder the service should follow instead, or null to leave it alone. Only an INSTALLED copy takes
    /// a service over, and only when the service watches nothing, a folder that is gone, or a folder holding an older
    /// RemSound than this one. A service watching a same-or-newer copy follows a copy somebody chose, and is left be.</summary>
    internal static string? FolderToAdopt(string? recorded, string thisFolder, string? thisVersion, bool thisIsInstalledCopy,
        bool serviceProgramPresent, Func<string, string?> versionOf)
    {
        if (!thisIsInstalledCopy || !serviceProgramPresent) return null;
        if (string.IsNullOrWhiteSpace(recorded)) return thisFolder;
        if (AppInstaller.SameFolder(recorded, thisFolder)) return null;
        var theirs = versionOf(recorded);
        if (theirs is null) return thisFolder;
        return Version.TryParse(theirs, out var t) && Version.TryParse(thisVersion, out var mine) && t < mine ? thisFolder : null;
    }

    /// <summary>The file version of the RemSound.exe in a folder, or null when the folder or the exe is gone.</summary>
    internal static string? ExeVersionIn(string folder)
    {
        try
        {
            var exe = Path.Combine(folder, "RemSound.exe");
            return File.Exists(exe) ? FileVersionInfo.GetVersionInfo(exe).FileVersion : null;
        }
        catch { return null; }
    }

    /// <summary>Run once at start-up, off the UI thread. Never throws; says what it did in the log.</summary>
    internal static void RunAtStartup(Action<string> log)
    {
        try
        {
            // A run that touches nothing real (the self-test, a --config-dir start) never repoints the real service.
            if (AppConfig.ThrowawayMachineDirectory is not null) return;
            var thisFolder = AppContext.BaseDirectory;
            var recorded = ServiceStore.LoadAppSourcePath();
            var adopt = FolderToAdopt(recorded, thisFolder, ExeVersionIn(thisFolder), AppInstaller.IsInstalledCopy,
                File.Exists(ServiceStore.BinExePath), ExeVersionIn);
            if (adopt is null) return;
            ServiceStore.SaveAppSourcePath(adopt);
            log(AppInstaller.SameFolder(ServiceStore.LoadAppSourcePath(), adopt)
                ? $"service: now follows this installed copy for its updates (it was following \"{recorded ?? "nothing"}\", which no update reaches)"
                : "service: it follows a folder no update reaches, but this account can't change that - the account that installed the service can, by opening RemSound once");
        }
        catch (Exception ex) { log($"service: could not check which copy it follows ({ex.GetType().Name}: {ex.Message})"); }
    }
}
