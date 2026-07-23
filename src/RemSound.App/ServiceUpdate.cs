using System.Diagnostics;
using System.Reflection;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Lets the running Windows service pick up an app update ON ITS OWN, with no admin prompt and no menu
/// click. The service runs from its own copy under ProgramData (so it never locks the app folder), and
/// the app's non-admin auto-updater can't touch that copy or restart the service. So instead the SERVICE
/// — which runs as SYSTEM and CAN write its own bin and restart itself — watches the folder the app was
/// installed from (recorded at install: <see cref="ServiceStore.LoadAppSourcePath"/>). When the app's
/// auto-updater drops a strictly-newer RemSound.exe there, the service copies that build into its own bin
/// and restarts onto it.
///
/// <para>Loop-safe: only fires when the app-folder version is STRICTLY newer than the running one, and any
/// uncertainty (folder unknown, file missing mid-swap, unparseable version) means "don't act". After the
/// copy+restart the running bin == the app version, so it never re-triggers.</para>
///
/// <para>Trust note: the service copies from a user-writable folder (the app's install location) and runs
/// it as SYSTEM. That is a local-privilege-escalation surface — a hardened build would code-sign the app
/// and verify the signature before copying. Accepted deliberately for this personal app.</para>
/// </summary>
internal static class ServiceUpdate
{
    /// <summary>Pure version comparison, unit-testable: is the on-disk version strictly newer than the
    /// running one? False on any missing/unparseable input (so we never restart on uncertainty).</summary>
    internal static bool IsNewer(Version? running, string? onDiskFileVersion)
    {
        if (running is null || string.IsNullOrWhiteSpace(onDiskFileVersion)) return false;
        return Version.TryParse(onDiskFileVersion, out var onDisk) && onDisk > running;
    }

    /// <summary>True when a strictly-newer RemSound.exe sits next to the running service binary (i.e. an
    /// update landed). Reads the on-disk exe's file version; never throws.</summary>
    public static bool UpdateLanded() => IsNewer(RunningVersion(), OnDiskVersion());

    /// <summary>The version of RemSound.exe in the recorded APP-SOURCE folder (the app's install location,
    /// which its auto-updater swaps in place), or null if the folder is unknown/unreadable.</summary>
    public static string? OnDiskVersion()
    {
        try
        {
            var appDir = ServiceStore.LoadAppSourcePath();
            if (string.IsNullOrEmpty(appDir)) return null;
            var appExe = Path.Combine(appDir, "RemSound.exe");
            return File.Exists(appExe) ? FileVersionInfo.GetVersionInfo(appExe).FileVersion : null;
        }
        catch { return null; }
    }

    public static Version? RunningVersion() => Assembly.GetExecutingAssembly().GetName().Version;

    /// <summary>Restart the service onto the new binary. Spawns a DETACHED helper — a copy of the NEW
    /// RemSound.exe running the <see cref="ServiceControl.SelfUpdateVerb"/> verb, as SYSTEM (inherited
    /// from the service) — which stops this service (exiting this process), copies the new build into the
    /// service bin, and starts the service again. All managed code: the previous PowerShell restart
    /// script silently died on machines where Group Policy enforces execution policy (Bypass is ignored
    /// there), stranding the service on the old build. The helper logs every step to the update log, so
    /// the part that runs after this process is gone is still recorded. Never throws; worst case the
    /// service picks up the update on the next reboot.</summary>
    public static void RestartSelf()
    {
        try
        {
            // Run the helper from the NEW build in the app-source folder — it must not execute from the
            // bin it's about to overwrite. Falls back to the bin exe when no app source is recorded (the
            // helper then just restarts the service without copying, so nothing conflicts).
            var appDir = ServiceStore.LoadAppSourcePath();
            var appExe = string.IsNullOrEmpty(appDir) ? null : Path.Combine(appDir, "RemSound.exe");
            var helperExe = appExe is not null && File.Exists(appExe) ? appExe : ServiceStore.BinExePath;
            var psi = new ProcessStartInfo
            {
                FileName = helperExe,
                Arguments = ServiceControl.SelfUpdateVerb,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(helperExe)!,
            };
            Process.Start(psi);
        }
        catch { /* best-effort; worst case the service picks up the update on next reboot */ }
    }
}
