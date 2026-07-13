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

    public RemSoundService()
    {
        ServiceName = ServiceControl.ServiceName;
        CanStop = true;
        CanShutdown = true;
        CanPauseAndContinue = false;
    }

    protected override void OnStart(string[] args)
    {
        log = new RemSoundLog { Enabled = SafeServiceLogging() };
        log.Event("service: OnStart");
        host = ServiceSendHost.FromConfig(msg => log?.Event(msg));
        worker = new Thread(() =>
        {
            try { host.RunLoop(cts.Token); }
            catch (Exception ex) { log?.Event($"service: run loop crashed {ex.GetType().Name}: {ex.Message}"); }
        })
        { IsBackground = true, Name = "remsound-service" };
        worker.Start();
    }

    protected override void OnStop()
    {
        log?.Event("service: OnStop");
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
