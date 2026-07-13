using System.Diagnostics;
using System.Reflection;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Lets the running Windows service pick up an app update on its own. The interactive auto-updater
/// swaps the install files in place (rename-aside, which a running service tolerates) but has no admin
/// rights to restart the service — so instead the SERVICE, which runs as SYSTEM and DOES have the rights,
/// notices that a newer RemSound.exe has landed next to it and restarts itself onto the new binary.
///
/// <para>Loop-safe by construction: it only restarts when the on-disk version is STRICTLY newer than the
/// running one, and any uncertainty (file missing mid-swap, unparseable version) means "don't restart".
/// After the restart the new process's on-disk == running, so it never re-triggers.</para>
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

    /// <summary>The version string of the RemSound.exe sitting next to the service, or null if unreadable.</summary>
    public static string? OnDiskVersion()
    {
        try
        {
            var onDiskExe = Path.Combine(AppContext.BaseDirectory, "RemSound.exe");
            return File.Exists(onDiskExe) ? FileVersionInfo.GetVersionInfo(onDiskExe).FileVersion : null;
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
            var dir = ServiceStore.Directory;
            System.IO.Directory.CreateDirectory(dir);
            var script = Path.Combine(dir, "restart.ps1");
            var log = ServiceStore.UpdateLogPath;
            var svc = ServiceControl.ServiceName;
            var content =
                "$ts = { (Get-Date).ToString('yyyy-MM-dd HH:mm:ss') }\r\n" +
                $"Add-Content -LiteralPath '{log}' -Value \"$(& $ts)  restarter: stopping {svc}\"\r\n" +
                $"Stop-Service -Name {svc} -Force -ErrorAction SilentlyContinue\r\n" +
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
