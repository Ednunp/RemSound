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
    private readonly CancellationTokenSource cts = new();
    private Thread? worker;
    private ServiceSendHost? host;
    private RemSoundLog? log;
    private System.Threading.Timer? updateWatch;
    private volatile bool restartScheduled;

    public RemSoundService()
    {
        ServiceName = ServiceControl.ServiceName;
        CanStop = true;
        CanShutdown = true;
        CanPauseAndContinue = false;
        CanHandlePowerEvent = true;   // so we can re-open capture after the machine wakes from sleep
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        if (powerStatus is PowerBroadcastStatus.ResumeSuspend or PowerBroadcastStatus.ResumeAutomatic or PowerBroadcastStatus.ResumeCritical)
        {
            log?.Event("service: power resume — re-opening capture");
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
        // Record the running version + start time so the app's Service menu can show them — this is how a
        // self-update is visible (the version bumps and the start time is recent).
        try { ServiceStore.SaveStatus(new ServiceStore.ServiceStatus { Version = versionText, StartedUtc = DateTime.UtcNow }); } catch { }
        host = ServiceSendHost.FromConfig(msg => log?.Event(msg));
        worker = new Thread(() =>
        {
            try { host.RunLoop(cts.Token); }
            catch (Exception ex) { log?.Event($"service: run loop crashed {ex.GetType().Name}: {ex.Message}"); }
        })
        { IsBackground = true, Name = "remsound-service" };
        worker.Start();

        // Self-update: the auto-updater swaps the files in place but can't restart us (no admin). We run
        // as SYSTEM, so when a newer RemSound.exe lands next to us we restart onto it ourselves. Checked
        // on a slow timer (an update is rare); loop-safe (only fires on a strictly-newer on-disk version).
        updateWatch = new System.Threading.Timer(_ => CheckForUpdate(), null, TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(45));
    }

    private void CheckForUpdate()
    {
        if (restartScheduled) return;
        if (!ServiceUpdate.UpdateLanded()) return;
        restartScheduled = true;
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
