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
/// <para>Trust note: the service copies from a user-writable folder and runs it as SYSTEM — the same trust
/// posture as the previous in-place scheme. Acceptable for this personal app; a hardened build would
/// code-sign and verify before copying.</para>
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

    /// <summary>Restart the service onto the new binary. Spawns a DETACHED PowerShell (as SYSTEM, inherited
    /// from the service) that stops this service — which exits this process — then starts it again, so the
    /// SCM launches the freshly-installed exe. The script LOGS its own stop/start outcome to the update log,
    /// so even the part that runs after this process is gone (and any failed start) is recorded. Never
    /// throws; worst case the service picks up the update on the next reboot.</summary>
    public static void RestartSelf()
    {
        try
        {
            var appDir = ServiceStore.LoadAppSourcePath();
            var binDir = ServiceStore.BinDirectory;
            var dir = ServiceStore.Directory;
            System.IO.Directory.CreateDirectory(dir);
            var script = Path.Combine(dir, "restart.ps1");
            var log = ServiceStore.UpdateLogPath;
            var svc = ServiceControl.ServiceName;
            // robocopy the new build into bin, minus user-state; exit codes 0-7 are success (8+ = failure).
            var copyLine = string.IsNullOrEmpty(appDir)
                ? "$rc = 0  # no app-source recorded; restart onto whatever is already in bin"
                : $"robocopy \"{appDir}\" \"{binDir}\" /E /XD \"user settings and logs\" logs recordings profiles config /XF \"global config.json\" remsound.config.json /R:2 /W:1 | Out-Null; $rc = $LASTEXITCODE";
            var content =
                "$ts = { (Get-Date).ToString('yyyy-MM-dd HH:mm:ss') }\r\n" +
                $"Add-Content -LiteralPath '{log}' -Value \"$(& $ts)  restarter: stopping {svc}\"\r\n" +
                $"Stop-Service -Name {svc} -Force -ErrorAction SilentlyContinue\r\n" +
                copyLine + "\r\n" +
                $"Add-Content -LiteralPath '{log}' -Value \"$(& $ts)  restarter: copied new build (robocopy code $rc)\"\r\n" +
                $"if ($rc -ge 8) {{ Add-Content -LiteralPath '{log}' -Value \"$(& $ts)  restart: COPY FAILED (code $rc) - starting existing build\" }}\r\n" +
                $"try {{ Start-Service -Name {svc} -ErrorAction Stop; $r = 'restart: service started' }} catch {{ $r = 'restart: START FAILED - ' + $_.Exception.Message }}\r\n" +
                $"Add-Content -LiteralPath '{log}' -Value \"$(& $ts)  $r\"\r\n";
            File.WriteAllText(script, content);
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi);
        }
        catch { /* best-effort; worst case the service picks up the update on next reboot */ }
    }
}
