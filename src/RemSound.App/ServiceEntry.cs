using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Runs the one-shot Windows-service CLI verbs (<c>--run-service</c>, <c>--install-service</c>, …).
///
/// <para>Deliberately a SEPARATE class from <see cref="Program"/>. <see cref="RemSoundService"/> derives
/// from <c>ServiceBase</c> (in the <c>System.ServiceProcess</c> assembly), so if <c>Program.Main</c> named
/// it directly the JIT would have to load that assembly the instant <c>Main</c> is compiled — at the very
/// start of EVERY launch, before a single argument is read. On older Windows (Windows 7) that assembly
/// won't load under the .NET 10 runtime, so the app crashed before it could open a window (reported
/// 2026-07-14). Keeping every service-type reference in here, reached only once a service verb is confirmed
/// present (<see cref="Program.IsServiceInvocation"/>), means a normal launch never loads the service
/// assembly and starts exactly as before.</para>
/// </summary>
internal static class ServiceEntry
{
    /// <summary>Runs whichever service verb <paramref name="args"/> contains and returns the process exit
    /// code. <c>--run-service</c> blocks in the SCM dispatcher until Windows stops the service; the others
    /// do their elevated SCM work and return. The caller must have already confirmed a verb is present via
    /// <see cref="Program.IsServiceInvocation"/>.</summary>
    public static int Dispatch(string[] args)
    {
        if (Has(args, ServiceControl.RunVerb))
        {
            // Point the service's data (its log) at the machine-wide ProgramData location, next to its
            // profile — otherwise a headless SYSTEM service logs into the SYSTEM account's AppData, which
            // is near-impossible to find. (The profile itself always comes from ServiceStore.)
            AppConfig.SetUserDataDirectoryOverride(ServiceStore.Directory);
            ServiceStore.AppendServiceEvent("elevated run-service: starting host");
            try { RemSoundService.RunAsService(); }
            catch (Exception ex) { ServiceStore.AppendServiceEvent($"elevated run-service: THREW {ex.GetType().Name}: {ex.Message}"); throw; }
            return 0;
        }

        var verb = Has(args, ServiceControl.InstallVerb) ? "install"
            : Has(args, ServiceControl.UninstallVerb) ? "uninstall"
            : Has(args, ServiceControl.UpdateVerb) ? "update"
            : Has(args, ServiceControl.StartVerb) ? "start"
            : Has(args, ServiceControl.StopVerb) ? "stop"
            : null;
        if (verb is null) return 0;

        // Log to the always-on events log BEFORE touching the service machinery, and wrap the call: if
        // System.ServiceProcess can't load on this OS (the Win7 unknown), the exception is caught HERE and
        // its reason recorded — then rethrown so the crash file also captures the full stack. Either way we
        // learn WHY, with no logging toggle needed.
        ServiceStore.AppendServiceEvent($"elevated {verb}: starting (loading service machinery)");
        try
        {
            var rc = verb switch
            {
                "install" => ServiceControl.DoInstall(),
                "uninstall" => ServiceControl.DoUninstall(),
                "update" => ServiceControl.DoUpdate(),
                "start" => ServiceControl.DoStart(),
                "stop" => ServiceControl.DoStop(),
                _ => 0,
            };
            ServiceStore.AppendServiceEvent($"elevated {verb}: finished with code {rc}");
            return rc;
        }
        catch (Exception ex)
        {
            ServiceStore.AppendServiceEvent($"elevated {verb}: THREW {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    private static bool Has(string[] args, string flag) =>
        Array.Exists(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
}
