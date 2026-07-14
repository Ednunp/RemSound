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
            RemSoundService.RunAsService();
            return 0;
        }
        if (Has(args, ServiceControl.InstallVerb)) return ServiceControl.DoInstall();
        if (Has(args, ServiceControl.UninstallVerb)) return ServiceControl.DoUninstall();
        if (Has(args, ServiceControl.StartVerb)) return ServiceControl.DoStart();
        if (Has(args, ServiceControl.StopVerb)) return ServiceControl.DoStop();
        return 0;
    }

    private static bool Has(string[] args, string flag) =>
        Array.Exists(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
}
