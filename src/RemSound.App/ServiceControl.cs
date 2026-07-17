using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using RemSound.Core;

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
    public const string UpdateVerb = "--update-service";
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

    /// <summary>Installs the service. Must be run elevated. Copies the program to the service's OWN folder
    /// (<see cref="ServiceStore.BinDirectory"/>) and registers it to run from THERE — never from the app's
    /// install folder or a dev working copy — so it can't lock those files or block the app's auto-updater.
    /// Also grants the machine's authenticated users start/stop rights, so the service can be stopped with
    /// a plain <c>sc stop</c> (no admin, no app). Returns 0 on success. Idempotent-ish: already-installed
    /// reports success.</summary>
    public static int DoInstall()
    {
        if (IsInstalled()) return 0;
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return 2;
        var sourceDir = Path.GetDirectoryName(exe);
        if (string.IsNullOrEmpty(sourceDir)) return 2;

        // Copy the whole program (exe + DLLs + runtimes/ + default sounds/) into the service's own bin
        // folder, so the running service uses ITS copy, not the source it was installed from.
        try { CopyProgramTo(sourceDir, ServiceStore.BinDirectory); }
        catch (Exception ex) { ServiceStore.AppendServiceEvent($"install: copy program failed: {ex.GetType().Name}: {ex.Message}"); return 5; }

        var rc = RunSc(BuildCreateArgs(ServiceStore.BinExePath));
        if (rc != 0) return rc;
        // Best-effort description; failure here doesn't fail the install.
        RunSc($"description {ServiceName} \"{Description}\"");
        // Auto-restart on crash: without this a crashed service stays dead until reboot, which defeats
        // an always-on streamer. Restart 5s / 10s / then every 60s; reset the failure counter daily.
        RunSc(BuildFailureArgs());
        // Let a normal (non-admin) user start/stop it — otherwise stopping needs the app's UAC prompt.
        GrantUserStartStop();
        return 0;
    }

    /// <summary>Copies the program files from <paramref name="sourceDir"/> to <paramref name="destDir"/>,
    /// recursively, but NEVER the user-state folders (logs, profiles, config, recordings) — the service
    /// keeps its own state in ProgramData. Overwrites so a re-install refreshes the binaries.</summary>
    internal static void CopyProgramTo(string sourceDir, string destDir)
    {
        // Guard against copying a folder onto itself (re-install from the service bin folder).
        if (string.Equals(Path.GetFullPath(sourceDir).TrimEnd('\\'),
                          Path.GetFullPath(destDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return;

        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "user settings and logs", "logs", "recordings", "profiles", "config" };

        static void CopyDir(string src, string dst, HashSet<string> skip)
        {
            Directory.CreateDirectory(dst);
            foreach (var file in Directory.GetFiles(src))
                File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
            foreach (var dir in Directory.GetDirectories(src))
            {
                var name = Path.GetFileName(dir);
                if (skip.Contains(name)) continue;
                CopyDir(dir, Path.Combine(dst, name), skip);
            }
        }
        CopyDir(sourceDir, destDir, skipDirs);
    }

    /// <summary>Adds an ACE granting Authenticated Users start + stop + query on the service, so the
    /// service can be stopped/started without administrator rights (a plain <c>sc stop RemSoundService</c>
    /// or the app's Service menu without a UAC prompt). Reads the current security descriptor and inserts
    /// the ACE, so nothing already granted is lost. Best-effort — a failure just leaves the default
    /// (admin-only) rights in place.</summary>
    private static void GrantUserStartStop()
    {
        try
        {
            var sddl = RunScCapture($"sdshow {ServiceName}").Trim();
            var newSddl = AddUserStartStopAce(sddl);
            if (newSddl is null || string.Equals(newSddl, sddl, StringComparison.Ordinal)) return;
            var rc = RunSc($"sdset {ServiceName} {newSddl}");
            if (rc != 0) ServiceStore.AppendServiceEvent($"install: sdset (user start/stop) returned {rc}");
        }
        catch (Exception ex) { ServiceStore.AppendServiceEvent($"install: grant user start/stop failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>The ACE granting Authenticated Users start (RP) + stop (WP) + query status (LC) + read
    /// control (RC). Public-ish for the self-test.</summary>
    internal const string UserStartStopAce = "(A;;RPWPLCRC;;;AU)";

    /// <summary>Pure, testable: insert <see cref="UserStartStopAce"/> into a service SDDL's DACL (right
    /// after "D:" and any DACL flags, ahead of the first ACE and the SACL). Returns null for an SDDL that
    /// doesn't start with a DACL, and the input unchanged if the ACE is already present.</summary>
    internal static string? AddUserStartStopAce(string? sddl)
    {
        if (string.IsNullOrEmpty(sddl) || !sddl.StartsWith("D:", StringComparison.Ordinal)) return null;
        if (sddl.Contains(UserStartStopAce, StringComparison.OrdinalIgnoreCase)) return sddl; // already granted
        var firstAce = sddl.IndexOf('(');
        var sacl = sddl.IndexOf("S:", StringComparison.Ordinal);
        var insertAt = firstAce >= 0 && (sacl < 0 || firstAce < sacl) ? firstAce : (sacl >= 0 ? sacl : sddl.Length);
        return sddl.Insert(insertAt, UserStartStopAce);
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
        var rc = RunSc($"delete {ServiceName}");
        // Remove the service's own copy of the program (best-effort; a failure — e.g. a file still briefly
        // locked as the service exits — just leaves a stale bin folder, which a re-install overwrites).
        try { if (Directory.Exists(ServiceStore.BinDirectory)) Directory.Delete(ServiceStore.BinDirectory, recursive: true); }
        catch (Exception ex) { ServiceStore.AppendServiceEvent($"uninstall: could not remove bin folder: {ex.GetType().Name}: {ex.Message}"); }
        return rc;
    }

    /// <summary>Refreshes the service's OWN copy of the program with the CURRENTLY-running build, without
    /// an uninstall/reinstall: stop → overwrite the binaries in <see cref="ServiceStore.BinDirectory"/>
    /// from this exe's folder → start. Must be run elevated (writing into the admin-only service bin
    /// folder, and stop/start). Returns 0 on success. Used by the Service menu's "Update service" so a new
    /// build reaches the service in one UAC prompt.</summary>
    public static int DoUpdate()
    {
        if (!IsInstalled()) return DoInstall(); // not installed yet — a plain install does the copy too
        var exe = Environment.ProcessPath;
        var sourceDir = string.IsNullOrEmpty(exe) ? null : Path.GetDirectoryName(exe);
        if (string.IsNullOrEmpty(sourceDir)) return 2;

        try { DoStop(); } catch { /* best-effort — copy may still fail if files stay locked, handled below */ }
        try { CopyProgramTo(sourceDir, ServiceStore.BinDirectory); }
        catch (Exception ex)
        {
            ServiceStore.AppendServiceEvent($"update: copy program failed: {ex.GetType().Name}: {ex.Message}");
            try { DoStart(); } catch { /* leave it stopped rather than half-updated */ }
            return 5;
        }
        return DoStart();
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

    /// <summary>Runs sc.exe and returns its stdout (empty on failure). Used to read the service's security
    /// descriptor (<c>sdshow</c>) before amending it.</summary>
    private static string RunScCapture(string arguments)
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
            if (p is null) return "";
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(20000);
            return stdout;
        }
        catch { return ""; }
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
