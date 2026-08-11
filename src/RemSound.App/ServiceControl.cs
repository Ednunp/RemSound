using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
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
    public const string StartVerb = "--start-service";
    public const string StopVerb = "--stop-service";
    public const string RunVerb = "--run-service";
    /// <summary>Re-records the invoking user as the service folder's owner and re-applies the folder
    /// lockdown. The self-heal for a folder hardened to the WRONG account (the 5.6 bug: the elevated
    /// helper recorded ITS OWN identity, which on a standard-user PC is the separate admin account whose
    /// password was typed at the UAC prompt — locking the real user out of their own service profile and
    /// logs, with every service self-update re-applying the stale lock).</summary>
    public const string RepairVerb = "--repair-service-access";

    /// <summary>Argument the non-elevated app appends to every elevated verb: <c>--as-user &lt;SID&gt;</c>,
    /// naming the person actually at the keyboard. The elevated helper must NOT ask its own token who the
    /// user is — under over-the-shoulder elevation that token belongs to whoever's admin password was
    /// typed, not the user (the root cause above). The non-elevated app's identity IS the interactive
    /// user, so it introduces them by SID and the elevated side records THAT.</summary>
    public const string AsUserArg = "--as-user";

    /// <summary>The validated <see cref="AsUserArg"/> SID for this elevated helper process, set once by
    /// ServiceEntry before dispatching a verb; null when absent/invalid (old caller, manual console run)
    /// — then <see cref="InstallingUserSid"/> falls back to the process identity as before.</summary>
    internal static string? ElevatedInvokerSid;

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

    /// <summary>Returned by <see cref="RunElevated"/> when the elevated helper didn't finish within the
    /// time limit (it hung, or the user left the UAC prompt sitting). Distinct from -1 (declined/failed).</summary>
    public const int ElevatedTimedOut = -2;

    /// <summary>How long to wait for an elevated one-shot verb to finish. Generous: it covers the UAC
    /// prompt, the file copy into the service bin, and the SCM calls. If it's exceeded, the helper is
    /// assumed stuck and we stop waiting rather than block the caller forever.</summary>
    private const int ElevatedTimeoutMs = 120000;

    /// <summary>Re-launch this exe elevated with <paramref name="verb"/>, wait (bounded), and return its
    /// exit code (0 = success). Returns -1 if the user declined the UAC prompt or elevation failed, or
    /// <see cref="ElevatedTimedOut"/> if it didn't finish in time. NEVER waits forever — a stuck helper
    /// must not be able to freeze the caller (that was the install-hang, 2026-07-17). Best called off the
    /// UI thread so even the bounded wait can't stall the window or its audio.</summary>
    public static int RunElevated(string verb)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return -1;
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = BuildElevatedArguments(verb),
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return -1;
            if (!p.WaitForExit(ElevatedTimeoutMs)) return ElevatedTimedOut;
            return p.ExitCode;
        }
        catch (Win32Exception) { return -1; } // user cancelled the UAC prompt
        catch { return -1; }
    }

    /// <summary>Pure, testable: the full command line for an elevated helper — the verb plus
    /// <see cref="AsUserArg"/> introducing THIS (non-elevated) process's user, when that identity is a
    /// real user account. See <see cref="AsUserArg"/> for why the identity must travel as an argument.</summary>
    internal static string BuildElevatedArguments(string verb)
    {
        string? sid = null;
        try { sid = WindowsIdentity.GetCurrent().User?.Value; } catch { }
        return IsValidUserSid(sid) ? $"{verb} {AsUserArg} {sid}" : verb;
    }

    /// <summary>True when <paramref name="sid"/> parses as a SID and denotes an actual user account —
    /// not SYSTEM / LocalService / NetworkService (S-1-5-18/19/20) and not a built-in group
    /// (S-1-5-32-*). Granting the service folder to one of those would either hand it to the service's
    /// own identity (defeating the lockdown) or to every member of a group (re-opening the audit hole),
    /// so such values are rejected and the caller falls back to its old behaviour.</summary>
    internal static bool IsValidUserSid(string? sid)
    {
        if (string.IsNullOrWhiteSpace(sid)) return false;
        try { _ = new SecurityIdentifier(sid); } catch { return false; }
        if (sid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20") return false;
        if (sid.StartsWith("S-1-5-32-", StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>Pure, testable: extract the <see cref="AsUserArg"/> value from a verb command line, or
    /// null when absent, valueless, or not a valid user SID (see <see cref="IsValidUserSid"/>).</summary>
    internal static string? ParseAsUserSid(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], AsUserArg, StringComparison.OrdinalIgnoreCase))
                return IsValidUserSid(args[i + 1]) ? args[i + 1] : null;
        return null;
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
        // If the non-elevated app introduced the real interactive user, record THAT identity before any
        // hardening — including the already-installed re-harden below, which would otherwise re-apply a
        // stale (possibly wrong-account) recorded owner forever.
        if (IsValidUserSid(ElevatedInvokerSid)) ServiceStore.SaveInstallingUserSid(ElevatedInvokerSid!);
        if (IsInstalled()) { HardenServiceDirectory(); return 0; } // re-harden pre-audit installs
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return 2;
        var sourceDir = Path.GetDirectoryName(exe);
        if (string.IsNullOrEmpty(sourceDir)) return 2;

        // Copy the whole program (exe + DLLs + runtimes/ + default sounds/) into the service's own bin
        // folder, so the running service uses ITS copy, not the source it was installed from.
        try { CopyProgramTo(sourceDir, ServiceStore.BinDirectory); }
        catch (Exception ex) { ServiceStore.AppendServiceEvent($"install: copy program failed: {ex.GetType().Name}: {ex.Message}"); return 5; }
        // Remember where the app lives, so the SYSTEM service can watch it and auto-update itself when the
        // app's auto-updater drops a newer build there (no UAC — see ServiceUpdate). Record the installing
        // user's SID alongside it, so the SYSTEM-side re-hardening grants the right account.
        ServiceStore.SaveAppSourcePath(sourceDir);
        ServiceStore.SaveInstallingUserSid(InstallingUserSid());

        var rc = RunSc(BuildCreateArgs(ServiceStore.BinExePath));
        if (rc != 0) return rc;
        // Best-effort description; failure here doesn't fail the install.
        RunSc($"description {ServiceName} \"{Description}\"");
        // Auto-restart on crash: without this a crashed service stays dead until reboot, which defeats
        // an always-on streamer. Restart 5s / 10s / then every 60s; reset the failure counter daily.
        RunSc(BuildFailureArgs());
        // Let a normal (non-admin) user start/stop it — otherwise stopping needs the app's UAC prompt.
        GrantUserStartStop();
        // The installing user's write access to the bin folder (drop test builds in without admin) comes
        // from the hardening below: the folder ACL grants them Modify, inherited by bin and its files
        // after the children reset. (A separate explicit bin grant existed until 5.8; the reset wiped it
        // anyway, so it was removed. Trust note: a user-writable folder whose contents run as SYSTEM is
        // the same posture as the auto-update copy; fine for this app, a hardened build would code-sign.)
        //
        // Close the cross-user escalation the 2026-07-26 security audit found: the SYSTEM service
        // trusts app-source.txt (which folder to self-update FROM), and if ANOTHER local user had
        // pre-created ProgramData\RemSound\service (e.g. by saving the service config dialog before
        // the install), Windows made them its owner with inheritable Full Control — letting them
        // repoint the file at a folder of theirs and get their code run as SYSTEM. Reset ownership
        // and the ACL here, while elevated, so only SYSTEM, Administrators and the installing user
        // remain. Must run AFTER the grants above (inheritance reset re-derives file ACLs).
        HardenServiceDirectory();
        return 0;
    }

    /// <summary>Take ownership of the service's ProgramData folder for Administrators and reset its
    /// ACL to exactly SYSTEM + Administrators (Full) + the recorded installing user (Modify — they
    /// save the service profile from the non-elevated dialog and drop test builds in bin). Removes
    /// any ACE another account picked up by creating the folder first; ownership must move too,
    /// because an owner can always rewrite the ACL back. Runs elevated (install) or as SYSTEM
    /// (self-update) — both hold take-ownership rights. When called as SYSTEM with no recorded
    /// installing-user SID (an install predating this), it SKIPS rather than lock the user out of
    /// their own no-admin workflow; the next elevated service action records the SID and hardens.
    /// Best-effort with loud logging.</summary>
    internal static void HardenServiceDirectory()
    {
        var sid = ServiceStore.LoadInstallingUserSid();
        if (sid is null)
        {
            var current = InstallingUserSid();
            if (current == "S-1-5-18")
            {
                ServiceStore.AppendServiceEvent("harden: no installing-user SID recorded and running as SYSTEM — deferred to the next elevated install/update");
                return;
            }
            sid = current;
            ServiceStore.SaveInstallingUserSid(sid);
        }
        var dir = ServiceStore.Directory;
        try { Directory.CreateDirectory(dir); } catch { /* the icacls below will report */ }
        // takeown /a → owner becomes the Administrators GROUP (not the current user); /r /d y recurses.
        var own = RunProcessCaptured("takeown.exe", $"/f \"{dir}\" /a /r /d y", 30000);
        if (!own.Started || !own.Exited || own.ExitCode != 0)
            ServiceStore.AppendServiceEvent($"harden: takeown on service dir returned {(own.Exited ? own.ExitCode : -1)}: {own.StdErr}");
        if (ApplyServiceDirAcl(dir, sid, ServiceStore.AppendServiceEvent))
            ServiceStore.AppendServiceEvent("harden: service folder ownership + ACL locked to SYSTEM/Administrators/installing user; files rebuilt as inherited; logs readable");
    }

    /// <summary>The complete lockdown sequence minus the takeown — factored out so the self-test can
    /// run the EXACT shipped commands against a scratch folder (reproduce → repair → verify, every
    /// build). Steps: (1) harden the folder itself (<see cref="BuildServiceDirAclArgs"/>); (2) rebuild
    /// all existing children as purely-inherited (<see cref="BuildResetChildrenArgs"/> — real file
    /// access again, stale ACEs gone, and the healer for the 5.6 file-wedging bug); (3) logs + the
    /// events file readable by every local account, read-only (the audit hole was WRITE access; taking
    /// everyone's READ just meant a user in trouble couldn't open their own logs — support case
    /// 2026-08-06). The profile stays locked to SYSTEM/Administrators/owner: it holds the obfuscated
    /// password. Returns true when every command succeeded; failures are reported and keep going.</summary>
    internal static bool ApplyServiceDirAcl(string dir, string sid, Action<string> report)
    {
        var ok = true;
        var acl = RunProcessCaptured("icacls.exe", BuildServiceDirAclArgs(dir, sid), 30000);
        if (!acl.Started || !acl.Exited || acl.ExitCode != 0)
        {
            ok = false;
            report($"harden: icacls on service dir returned {(acl.Exited ? acl.ExitCode : -1)}: {acl.StdErr}{acl.StdOut}");
        }
        bool hasChildren;
        try { hasChildren = Directory.EnumerateFileSystemEntries(dir).Any(); }
        catch { hasChildren = true; } // can't tell (we may lack list rights) — try; /C tolerates
        if (hasChildren)
        {
            var reset = RunProcessCaptured("icacls.exe", BuildResetChildrenArgs(dir), 30000);
            if (!reset.Started || !reset.Exited || reset.ExitCode != 0)
            {
                ok = false;
                report($"harden: children reset returned {(reset.Exited ? reset.ExitCode : -1)}: {reset.StdErr}{reset.StdOut}");
            }
        }
        try { Directory.CreateDirectory(Path.Combine(dir, "logs")); } catch { }
        var logsAcl = RunProcessCaptured("icacls.exe", BuildLogsReadAclArgs(dir), 30000);
        if (!logsAcl.Started || !logsAcl.Exited || logsAcl.ExitCode != 0)
        {
            ok = false;
            report($"harden: users-can-read-logs grant returned {(logsAcl.Exited ? logsAcl.ExitCode : -1)}: {logsAcl.StdErr}");
        }
        if (File.Exists(Path.Combine(dir, "service-events.log")))
            RunProcessCaptured("icacls.exe", BuildEventsLogReadAclArgs(dir), 30000);
        return ok;
    }

    /// <summary>Re-record the invoking user as the folder's owner and re-apply the lockdown — the
    /// elevated side of the one-click repair (Service menu, or offered automatically when the app finds
    /// it can no longer write the service folder). Also the recovery for installs bitten by the 5.6
    /// wrong-account recording. Returns 0 when the hardening commands all succeeded, 9 otherwise (the
    /// service events log has the detail either way).</summary>
    public static int DoRepairAccess()
    {
        if (IsValidUserSid(ElevatedInvokerSid)) ServiceStore.SaveInstallingUserSid(ElevatedInvokerSid!);
        HardenServiceDirectory();
        ServiceStore.AppendServiceEvent($"repair-access: completed (invoker sid {(ElevatedInvokerSid is null ? "not supplied - kept recorded owner" : "recorded")})");
        // HardenServiceDirectory is best-effort with loud logging; sanity-check the way the app will —
        // by writing. We're elevated (Administrators Full), so this proves the commands ran and the
        // folder isn't wedged; the app re-probes as the real user once the helper returns.
        return CanWriteDirectory(ServiceStore.Directory) ? 0 : 9;
    }

    /// <summary>Pure, testable: the icacls arguments that let every local account READ the service's
    /// logs subfolder, without granting any write. (RX) = read + traverse. The inheritable ACE on the
    /// folder propagates to the existing log files on apply (they're unprotected after the children
    /// reset) — no /T, which would stamp files with useless inherit-only ACEs (the 5.6 lesson).</summary>
    internal static string BuildLogsReadAclArgs(string dir) =>
        $"\"{Path.Combine(dir, "logs")}\" /grant \"*S-1-5-32-545:(OI)(CI)(RX)\"";

    /// <summary>Pure, testable: the icacls arguments that let every local account READ the always-on
    /// service events file (the first thing support asks for).</summary>
    internal static string BuildEventsLogReadAclArgs(string dir) =>
        $"\"{Path.Combine(dir, "service-events.log")}\" /grant \"*S-1-5-32-545:(RX)\"";

    /// <summary>Can the CURRENT process create a file in <paramref name="dir"/>? The app-side health
    /// probe: a missing folder counts as healthy (nothing to repair — it'll be created with the user as
    /// owner on first save). Probes with a real create-then-delete, because that's exactly what saving
    /// the service profile does; reading the ACL and predicting would just re-implement Windows, badly.</summary>
    internal static bool CanWriteDirectory(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return true;
            var probe = Path.Combine(dir, "access-probe.tmp");
            using (new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None)) { }
            try { File.Delete(probe); } catch { /* write proven; a stuck probe file is harmless */ }
            return true;
        }
        catch { return false; }
    }

    /// <summary>App-side health check: true when the interactive user can still write the service's
    /// ProgramData folder (or it doesn't exist yet). False = they've been locked out — the 5.6
    /// wrong-owner bug, or any outside interference — and the one-click repair should be offered.</summary>
    public static bool CurrentUserCanWriteServiceDir() => CanWriteDirectory(ServiceStore.Directory);

    /// <summary>Pure, testable: the icacls arguments that lock the service FOLDER down — the folder
    /// only, deliberately no /T. /inheritance:r strips inherited ACEs (ProgramData grants CREATOR
    /// OWNER full control — the exact hole); explicit grants only: SYSTEM + Administrators Full,
    /// installing user Modify.
    ///
    /// WHY no /T (the 5.6 file-wedging bug, found 2026-08-06 via a user report): these grants carry
    /// (OI)(CI), and on a FILE an ACE with inheritance flags is INHERIT-ONLY — it grants the file
    /// itself nothing. Sweeping /T therefore stamped every EXISTING file with /inheritance:r plus
    /// only inherit-only ACEs = an effectively empty ACL: unreadable and unwritable by everyone
    /// (user, admin, even SYSTEM — which is how a service ends up unable to read its own profile,
    /// and a user finds Notepad refusing their own logs). Existing children are instead cleaned by
    /// <see cref="BuildResetChildrenArgs"/>, which rebuilds them as purely-inherited from this
    /// folder's ACL — correct file access, and it strips any stale/planted explicit ACEs too.</summary>
    internal static string BuildServiceDirAclArgs(string dir, string installingUserSid) =>
        $"\"{dir}\" /inheritance:r /grant \"*S-1-5-18:(OI)(CI)F\" /grant \"*S-1-5-32-544:(OI)(CI)F\" /grant \"*{installingUserSid}:(OI)(CI)(M)\"";

    /// <summary>Pure, testable: the icacls arguments that rebuild every EXISTING child of the service
    /// folder as purely-inherited from the (just-hardened) folder ACL. /reset replaces each child's
    /// ACL with inherited ACEs only — files get real (not inherit-only) access again, and any explicit
    /// ACE another account picked up historically is removed. Also the healer for files wedged by the
    /// 5.6 bug (see <see cref="BuildServiceDirAclArgs"/>). /C continues past per-file errors.</summary>
    internal static string BuildResetChildrenArgs(string dir) =>
        $"\"{Path.Combine(dir, "*")}\" /reset /T /C";

    /// <summary>The SID to grant the no-admin service rights to: the interactive user the non-elevated
    /// app introduced via <see cref="AsUserArg"/> when present, else this process's own identity.
    /// The pass-through matters: an elevated helper's own token is whoever approved the UAC prompt,
    /// which under over-the-shoulder elevation (standard user + separate admin account) is NOT the
    /// person at the keyboard — recording that locked real users out of their service folder (the 5.6
    /// bug). Scoping the grants to ONE account instead of all Users/Authenticated-Users keeps the
    /// effortless stop/update workflow for that user while removing the "any account on this PC could
    /// replace a SYSTEM-run binary" escalation surface. Falls back to BUILTIN\Users only if no identity
    /// can be read at all.</summary>
    private static string InstallingUserSid()
    {
        if (IsValidUserSid(ElevatedInvokerSid)) return ElevatedInvokerSid!;
        try { return WindowsIdentity.GetCurrent().User?.Value ?? "S-1-5-32-545"; }
        catch { return "S-1-5-32-545"; }
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
            var newSddl = AddUserStartStopAce(sddl, InstallingUserSid());
            if (newSddl is null || string.Equals(newSddl, sddl, StringComparison.Ordinal)) return;
            var rc = RunSc($"sdset {ServiceName} {newSddl}");
            if (rc != 0) ServiceStore.AppendServiceEvent($"install: sdset (user start/stop) returned {rc}");
        }
        catch (Exception ex) { ServiceStore.AppendServiceEvent($"install: grant user start/stop failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>The ACE granting <paramref name="sid"/> start (RP) + stop (WP) + query status (LC) + read
    /// control (RC) on the service.</summary>
    internal static string UserStartStopAceFor(string sid) => $"(A;;RPWPLCRC;;;{sid})";

    /// <summary>Pure, testable: insert the start/stop ACE for <paramref name="sid"/> into a service SDDL's
    /// DACL (right after "D:" and any DACL flags, ahead of the first ACE and the SACL). Returns null for an
    /// SDDL that doesn't start with a DACL, and the input unchanged if the ACE is already present.</summary>
    internal static string? AddUserStartStopAce(string? sddl, string sid)
    {
        if (string.IsNullOrEmpty(sddl) || !sddl.StartsWith("D:", StringComparison.Ordinal)) return null;
        var ace = UserStartStopAceFor(sid);
        if (sddl.Contains(ace, StringComparison.OrdinalIgnoreCase)) return sddl; // already granted
        var firstAce = sddl.IndexOf('(');
        var sacl = sddl.IndexOf("S:", StringComparison.Ordinal);
        var insertAt = firstAce >= 0 && (sacl < 0 || firstAce < sacl) ? firstAce : (sacl >= 0 ? sacl : sddl.Length);
        return sddl.Insert(insertAt, ace);
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

    /// <summary>Verb for the native self-update helper (see <see cref="DoSelfUpdate"/>). Spawned BY the
    /// running service as SYSTEM, so no elevation is involved.</summary>
    public const string SelfUpdateVerb = "--service-selfupdate";

    /// <summary>The self-update worker: stop the service, copy the recorded app-source build into the
    /// service's own bin, start the service again — all in managed code. Replaces the old PowerShell
    /// restart script: where execution policy is enforced by Group Policy, the script's -ExecutionPolicy
    /// Bypass is IGNORED and the self-update silently died, stranding the service on the old build.
    /// Native code has no policy to fall foul of. Runs detached (spawned by the service just before the
    /// stop kills it); logs every step to the update log so the after-death part is still recorded.</summary>
    public static int DoSelfUpdate()
    {
        static void Log(string m)
        {
            try
            {
                Directory.CreateDirectory(ServiceStore.Directory);
                File.AppendAllText(ServiceStore.UpdateLogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  selfupdate: {m}\r\n");
            }
            catch { /* logging is best-effort */ }
        }
        try
        {
            Log($"stopping {ServiceName}");
            try
            {
                using var sc = new ServiceController(ServiceName);
                if (sc.Status != ServiceControllerStatus.Stopped)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                }
            }
            catch (Exception ex) { Log($"stop failed ({ex.GetType().Name}: {ex.Message}) — continuing"); }

            var appDir = ServiceStore.LoadAppSourcePath();
            if (!string.IsNullOrEmpty(appDir) && Directory.Exists(appDir))
            {
                // CopyProgramTo already excludes every user-state folder — same routine the installer uses.
                try { CopyProgramTo(appDir, ServiceStore.BinDirectory); Log("copied the new build into bin"); }
                catch (Exception ex) { Log($"COPY FAILED ({ex.GetType().Name}: {ex.Message}) — starting the existing build"); }
                // Re-assert the service-folder lockdown on every self-update (we're SYSTEM here, which
                // can take ownership too). This is how installs that predate the 2026-07-26 hardening
                // pick it up without a reinstall.
                try { HardenServiceDirectory(); } catch { /* logged inside; never block the update */ }
            }
            else
            {
                Log("no app-source recorded; restarting onto the existing bin");
            }

            try
            {
                using var sc = new ServiceController(ServiceName);
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                Log("service started");
                return 0;
            }
            catch (Exception ex) { Log($"START FAILED — {ex.GetType().Name}: {ex.Message}"); return 1; }
        }
        catch (Exception ex) { Log($"FATAL {ex.GetType().Name}: {ex.Message}"); return 2; }
    }

    /// <summary>Restart the service WITHOUT elevation, using the start/stop rights the installer granted
    /// the installing user (the SDDL ACE from <see cref="GrantUserStartStop"/>). Returns true when the
    /// service ends up Running. No UAC prompt, no elevated helper — so it's safe to run from a background
    /// thread after a profile save. Returns false (never throws) when the caller lacks rights, the service
    /// isn't installed, or a state wait times out; callers fall back to the elevated verbs then.
    /// <paramref name="serviceNameOverride"/> exists for the self-test (probe a non-existent name).</summary>
    public static bool TryRestartNoAdmin(string? serviceNameOverride = null)
    {
        try
        {
            using var sc = new ServiceController(serviceNameOverride ?? ServiceName);
            if (sc.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending))
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }
            else
            {
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
            return true;
        }
        catch { return false; }
    }

    /// <summary>Starts the service. Must be run elevated. Returns 0 on success.</summary>
    // Distinct failure codes for start/stop, so the dialog can say something useful instead of the
    // notorious bare "(code 1)". The full exception always goes to the service events log too.
    public const int StartStopTimedOut = 6;   // service didn't reach the target state in 15 s
    public const int StartStopScmRefused = 7; // the service manager refused (missing, disabled, ...)

    public static int DoStart() => StartStop(start: true);

    /// <summary>Stops the service. Must be run elevated. Returns 0 on success or if already stopped.</summary>
    public static int DoStop() => StartStop(start: false);

    private static int StartStop(bool start)
    {
        var label = start ? "start" : "stop";
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (start && sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending) return 0;
            if (!start && sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending) return 0;
            if (start) sc.Start(); else sc.Stop();
            sc.WaitForStatus(start ? ServiceControllerStatus.Running : ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            return 0;
        }
        catch (Exception ex)
        {
            // The WHY was swallowed here for two releases ("code 1", diagnosed blind on 2026-08-06);
            // now it's always in the events log, and the code tells the dialog which story to tell.
            ServiceStore.AppendServiceEvent($"elevated {label}: FAILED {ex.GetType().Name}: {ex.Message}"
                + (ex.InnerException is { } inner ? $" (inner: {inner.GetType().Name}: {inner.Message})" : ""));
            return ex is System.ServiceProcess.TimeoutException ? StartStopTimedOut
                : ex is InvalidOperationException ? StartStopScmRefused
                : 1;
        }
    }

    private static int RunSc(string arguments)
    {
        var r = RunProcessCaptured("sc.exe", arguments, 20000);
        return r.Started ? (r.Exited ? r.ExitCode : 3) : 4;
    }

    /// <summary>Runs sc.exe and returns its stdout (empty on failure). Used to read the service's security
    /// descriptor (<c>sdshow</c>) before amending it.</summary>
    private static string RunScCapture(string arguments) => RunProcessCaptured("sc.exe", arguments, 20000).StdOut;

    internal readonly record struct ProcResult(bool Started, bool Exited, int ExitCode, string StdOut, string StdErr);

    /// <summary>Run a console tool and capture its output SAFELY. Both stdout and stderr are drained
    /// CONCURRENTLY (async) while the process runs, then bounded by <paramref name="timeoutMs"/>. This is
    /// the fix for a real hang: reading one pipe to end before the other deadlocks whenever the child's
    /// output exceeds the ~4 KB pipe buffer — e.g. <c>icacls /T</c> over the service bin's 100+ files
    /// blocked writing stdout while we blocked reading stderr, hanging the elevated installer forever (and
    /// with it the app that was waiting on it). On timeout the child is killed (whole tree) so nothing is
    /// left stuck. Never throws.</summary>
    internal static ProcResult RunProcessCaptured(string fileName, string arguments, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return new ProcResult(false, false, -1, "", "");
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return new ProcResult(true, false, -1, Drain(outTask), Drain(errTask));
            }
            return new ProcResult(true, true, p.ExitCode, Drain(outTask), Drain(errTask));
        }
        catch { return new ProcResult(false, false, -1, "", ""); }

        static string Drain(Task<string> t)
        {
            try { return t.Wait(2000) ? t.Result : ""; } catch { return ""; }
        }
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
