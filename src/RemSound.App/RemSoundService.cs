using System.ServiceProcess;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// The RemSound Windows service (send-only lock-screen streaming). Hosts <see cref="ServiceSendHost"/>
/// on a background thread; the host's own RunLoop yields to the interactive app via
/// <see cref="InteractivePresence"/>. Started by the SCM when Program.cs is launched with
/// <see cref="ServiceControl.RunVerb"/>.
/// </summary>
public sealed class RemSoundService : ServiceBase
{
    /// <summary>How long the service keeps its own log files. It has no preferences window, so this is fixed.</summary>
    internal const int ServiceLogKeepDays = 30;

    /// <summary>One wake produces more than one resume notification — typically ResumeAutomatic, then ResumeSuspend
    /// once somebody is at the machine. Only the first within this window is acted on.</summary>
    internal const long ResumeDebounceMs = 10_000;

    private readonly CancellationTokenSource cts = new();
    private Thread? worker;
    private ServiceSendHost? host;
    private RemSoundLog? log;
    private System.Threading.Timer? updateWatch;
    private DateTime? restartAskedUtc;       // when this service last asked to be restarted onto a newer build
    private long lastResumeHandledMs;
    /// <summary>The app folder's RemSound.exe version at the previous update poll — see <see cref="ServiceUpdate.ReadyToApply"/>.</summary>
    private string? onDiskVersionAtLastPoll;

    public RemSoundService()
    {
        ServiceName = ServiceControl.ServiceName;
        CanStop = true;
        CanShutdown = true;
        CanPauseAndContinue = false;
        CanHandlePowerEvent = true;   // so we can re-open capture after the machine wakes from sleep
    }

    /// <summary>Pure and testable: act on this resume notification? The first of a wake, yes; a second arriving within
    /// <see cref="ResumeDebounceMs"/>, no. Each used to run a full stop-and-reapply of its own, back to back.</summary>
    internal static bool ShouldHandleResume(long nowMs, long lastHandledMs) =>
        lastHandledMs == 0 || nowMs - lastHandledMs >= ResumeDebounceMs;

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        if (powerStatus is PowerBroadcastStatus.ResumeSuspend or PowerBroadcastStatus.ResumeAutomatic or PowerBroadcastStatus.ResumeCritical)
        {
            var now = Environment.TickCount64;
            if (!ShouldHandleResume(now, Interlocked.Read(ref lastResumeHandledMs)))
            {
                log?.Event($"service: power resume ({powerStatus}) — this wake is already being handled");
                return true;
            }
            Interlocked.Exchange(ref lastResumeHandledMs, now);
            log?.Event($"service: power resume ({powerStatus}) — re-opening capture");
            try { host?.ReopenAfterResume(); } catch (Exception ex) { log?.Event($"service: resume re-open failed {ex.GetType().Name}: {ex.Message}"); }
        }
        return true;
    }

    protected override void OnStart(string[] args)
    {
        log = new RemSoundLog { Enabled = SafeServiceLogging() };
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        var versionText = version is null ? "?" : $"{version.Major}.{version.Minor}";
        log.Event($"service: OnStart, version {versionText}");
        // The service's own logs and crash reports are tidied like the app's. The service never runs the app's
        // startup housekeeping, so they used to pile up for good. 2026-09-13 review.
        if (TidyServiceLogs(log.Path) is { } tidied) log.Event(tidied);
        // Record the running version + start time so the app's Service menu can show them — this is how a
        // self-update is visible (the version bumps and the start time is recent).
        try { ServiceStore.SaveStatus(new ServiceStore.ServiceStatus { Version = versionText, StartedUtc = DateTime.UtcNow }); } catch { }
        // If this start is the completion of a self-update restart, close the loop in the update log.
        try { if (ServiceStore.ConsumeUpdatePending()) ServiceStore.AppendUpdateLog($"update complete: now running version {versionText}"); } catch { }
        // Startup volume (Additional service options): unmute + set the default output's level,
        // either on the first start after each boot or on every start. Before the host spins up so
        // the machine is audible by the time audio flows.
        StartupVolume.ApplyIfConfigured(msg => log?.Event(msg));
        host = ServiceSendHost.FromConfig(msg => log?.Event(msg));
        worker = new Thread(() =>
        {
            try { host.RunLoop(cts.Token); }
            catch (Exception ex) { log?.Event($"service: run loop crashed {ex.GetType().Name}: {ex.Message}"); }
        })
        { IsBackground = true, Name = "remsound-service" };
        worker.Start();

        // Self-update: the auto-updater swaps the files in place but can't restart us (no admin). We run
        // as SYSTEM, so when a newer RemSound.exe lands in the app folder we restart onto it ourselves. Checked
        // on a slow timer (an update is rare); loop-safe (only fires on a strictly-newer on-disk version), and
        // only once the updater has finished its swap (ServiceUpdate.ReadyToApply).
        updateWatch = new System.Threading.Timer(_ => CheckForUpdate(), null, TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(45));
    }

    /// <summary>Remove the service's log files older than <see cref="ServiceLogKeepDays"/> days and all but the newest crash
    /// reports, sparing the log being written. Returns a line for the log when anything went, else null. Never throws —
    /// housekeeping must never stop the service starting.</summary>
    internal static string? TidyServiceLogs(string? activeLogPath)
    {
        try
        {
            var logs = LogMaintenance.PruneLogsOlderThan(ServiceLogKeepDays, activeLogPath);
            var crashes = LogMaintenance.PruneCrashReports(AppConfig.LogsDirectory);
            return logs + crashes > 0
                ? $"service: log housekeeping — removed {logs} log(s) older than {ServiceLogKeepDays} days and {crashes} old crash report(s)"
                : null;
        }
        catch { return null; }
    }

    private void CheckForUpdate()
    {
        if (ServiceUpdate.StillWaitingOnRestart(restartAskedUtc, DateTime.UtcNow)) return;
        var onDisk = ServiceUpdate.OnDiskVersion();
        var ready = ServiceUpdate.ReadyToApply(ServiceUpdate.RunningVersion(), onDisk, onDiskVersionAtLastPoll, ServiceUpdate.SwapInProgress());
        onDiskVersionAtLastPoll = onDisk;
        if (!ready) return;
        restartAskedUtc = DateTime.UtcNow;
        var running = ServiceUpdate.RunningVersion();
        var runningText = running is null ? "?" : $"{running.Major}.{running.Minor}";
        // Always-on update log (not gated on the service-logging toggle) — updates are rare + important.
        ServiceStore.AppendUpdateLog($"update detected: newer RemSound.exe ({onDisk}) found, running {runningText} — restarting to update");
        ServiceStore.SetUpdatePending();
        log?.Event("service: a newer RemSound version was installed — restarting to update");
        ServiceUpdate.RestartSelf();
    }

    protected override void OnStop()
    {
        log?.Event("service: OnStop");
        try { updateWatch?.Dispose(); } catch { }
        try { cts.Cancel(); } catch { }
        try { worker?.Join(5000); } catch { }
        try { host?.Dispose(); } catch { }
        // Close the log LAST, once everything above has had its say. Without this the service's log
        // never got its "log stopped" line, so a file that ends mid-sentence read the same whether the
        // service shut down cleanly or was killed — and telling those two apart is most of what the
        // log is for. (AutoFlush is on, so nothing was ever lost; only the ending.) 2026-08-24.
        try { log?.Dispose(); } catch { }
        log = null;
    }

    protected override void OnShutdown() => OnStop();

    private static bool SafeServiceLogging()
    {
        try { return ServiceStore.LoadLoggingEnabled(); }
        catch { return false; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { try { cts.Dispose(); } catch { } }
        base.Dispose(disposing);
    }

    /// <summary>Blocks in the SCM dispatcher until the service is stopped. Called from Program.cs
    /// when launched with the run verb.</summary>
    public static void RunAsService() => ServiceBase.Run(new RemSoundService());
}
