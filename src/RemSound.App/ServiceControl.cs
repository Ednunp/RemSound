using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;

namespace RemSound.App;

/// <summary>Coarse state of the RemSound Windows service, for the Service menu's status line.</summary>
public enum ServiceState { NotInstalled, Stopped, Running, StartPending, StopPending, Unknown }

/// <summary>
/// Installs, removes, starts, stops and queries the send-only RemSound Windows service. Creation and
/// deletion go through <c>sc.exe</c>; start/stop through <see cref="ServiceController"/>. All of those
/// need administrator rights, so the interactive app performs them by re-launching itself ELEVATED with
/// a one-shot CLI verb (<c>--install-service</c> etc.) — one UAC prompt per action. Only status queries
/// are unprivileged, so the menu's status line needs no prompt.
/// </summary>
public static class ServiceControl
{
    public const string ServiceName = "RemSoundService";
    public const string DisplayName = "RemSound send-only service";

    /// <summary>Reserved title of the profile the service streams from. Edited only through the service
    /// config dialog and filtered out of the normal profile picker so it can't be loaded by accident.
    /// Single source of truth lives in <see cref="RemSound.Core.ProfileStore.ReservedServiceProfileTitle"/>.</summary>
    public const string ServiceProfileTitle = RemSound.Core.ProfileStore.ReservedServiceProfileTitle;
    public const string Description =
        "Streams this machine's audio to its RemSound peers without a logged-in user (lock screen). " +
        "Send-only; yields to the interactive RemSound app while it is open.";

    /// <summary>CLI verb the elevated instance runs to do the privileged work. Kept here so the menu and
    /// the Program.cs dispatcher agree.</summary>
    public const string InstallVerb = "--install-service";
    public const string UninstallVerb = "--uninstall-service";
    public const string StartVerb = "--start-service";
    public const string StopVerb = "--stop-service";
    public const string RunVerb = "--run-service";

    /// <summary>Current service state. Never throws — returns <see cref="ServiceState.Unknown"/> on any
    /// error. Unprivileged, so safe to poll from the UI without elevation.</summary>
    public static ServiceState Query()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status switch
            {
                ServiceControllerStatus.Running => ServiceState.Running,
                ServiceControllerStatus.Stopped => ServiceState.Stopped,
                ServiceControllerStatus.StartPending => ServiceState.StartPending,
                ServiceControllerStatus.StopPending => ServiceState.StopPending,
                _ => ServiceState.Unknown,
            };
        }
        catch (InvalidOperationException) { return ServiceState.NotInstalled; } // no such service
        catch { return ServiceState.Unknown; }
    }

    public static bool IsInstalled() => Query() != ServiceState.NotInstalled;

    // ---- UI-side (unprivileged): re-launch self elevated to do the work --------------------------

    /// <summary>Re-launch this exe elevated with <paramref name="verb"/>, wait, and return its exit code
    /// (0 = success). Returns -1 if the user declined the UAC prompt or elevation failed.</summary>
    public static int RunElevated(string verb)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return -1;
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = verb,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return -1;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Win32Exception) { return -1; } // user cancelled the UAC prompt
        catch { return -1; }
    }

    // ---- Elevated-side (called from Program.cs when running an --xxx-service verb) ---------------

    /// <summary>Creates the service (auto-start) pointing at this exe with <see cref="RunVerb"/>. Must be
    /// run elevated. Returns 0 on success. Idempotent-ish: if it already exists, reports success.</summary>
    public static int DoInstall()
    {
        if (IsInstalled()) return 0;
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return 2;

        var rc = RunSc(BuildCreateArgs(exe));
        if (rc != 0) return rc;
        // Best-effort description; failure here doesn't fail the install.
        RunSc($"description {ServiceName} \"{Description}\"");
        // Auto-restart on crash: without this a crashed service stays dead until reboot, which defeats
        // an always-on streamer. Restart 5s / 10s / then every 60s; reset the failure counter daily.
        RunSc(BuildFailureArgs());
        return 0;
    }

    /// <summary>The sc.exe "failure" args that make the service auto-restart on a crash. Pure, so a
    /// self-test can verify the format.</summary>
    internal static string BuildFailureArgs() =>
        $"failure {ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/60000";

    /// <summary>Stops (if running) and deletes the service. Must be run elevated. Returns 0 on success or
    /// if it wasn't installed.</summary>
    public static int DoUninstall()
    {
        if (!IsInstalled()) return 0;
        try { DoStop(); } catch { /* best-effort */ }
        return RunSc($"delete {ServiceName}");
    }

    /// <summary>Starts the service. Must be run elevated. Returns 0 on success.</summary>
    public static int DoStart()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending) return 0;
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
            return 0;
        }
        catch { return 1; }
    }

    /// <summary>Stops the service. Must be run elevated. Returns 0 on success or if already stopped.</summary>
    public static int DoStop()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending) return 0;
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            return 0;
        }
        catch { return 1; }
    }

    private static int RunSc(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return 2;
            p.WaitForExit(20000);
            return p.HasExited ? p.ExitCode : 3;
        }
        catch { return 4; }
    }

    /// <summary>Builds the exact sc.exe "create" argument string for a given exe path. Pure and
    /// side-effect-free so a self-test can verify the fiddly quoting without touching the SCM.
    ///
    /// <para><c>depend= Audiosrv/AudioEndpointBuilder</c> makes the service start as EARLY as it usefully
    /// can: the Windows Audio and Audio Endpoint Builder services must be running for WASAPI capture to
    /// find any audio at all, so Windows launches RemSound the instant they're ready (at boot, before
    /// login) rather than at some arbitrary later point. Starting it BEFORE the audio services isn't
    /// possible — there'd be no endpoints to capture — and there's no sound to miss before audio is up.</para></summary>
    internal static string BuildCreateArgs(string exePath) =>
        $"create {ServiceName} binPath= \"\\\"{exePath}\\\" {RunVerb}\" start= auto depend= Audiosrv/AudioEndpointBuilder DisplayName= \"{DisplayName}\"";
}
