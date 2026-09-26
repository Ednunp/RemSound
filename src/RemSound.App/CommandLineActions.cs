using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// What the Service menu says, in one place, so the menu and the command line (and the scripts that call it) say
/// exactly the same thing. Plain strings only: nothing here touches the service machinery, so the main window can use
/// it on every Windows.
/// </summary>
internal static class ServiceActionText
{
    internal const string InstallQuestion =
        "Install the RemSound send-only service? It will start automatically at boot and stream your service profile whenever you're not using RemSound normally.\n\nWindows will ask for administrator permission.";

    internal const string UninstallQuestion = "Uninstall the RemSound service?\n\nWindows will ask for administrator permission.";

    internal const string StartNowQuestion =
        "The RemSound service was installed. Do you want to start it now?\n\nIt will also start automatically at every boot.";

    /// <summary>How an elevated service action came out: whether it worked, the word the logs use, and the message a
    /// person is shown.</summary>
    internal static (bool Ok, string Outcome, string Message) Describe(string label, int rc)
    {
        var outcome = rc == 0 ? "success"
            : rc == -1 ? "cancelled/declined"
            : rc == ServiceControl.ElevatedTimedOut ? "timed out"
            : "failed";
        if (rc == 0)
            return (true, outcome, label == "access repair"
                ? "Service folder access repaired. Your account owns the service folder again."
                : $"Service {label} succeeded.");
        if (rc == -1)
            return (false, outcome, $"Service {label} was cancelled, or administrator rights were declined.");
        if (rc == ServiceControl.ElevatedTimedOut)
            return (false, outcome, $"Service {label} is taking longer than expected and hasn't finished yet. It may still complete on its own — check the Service menu status in a moment.");
        // Say what the code MEANS — "(code 1)" alone cost a support round-trip on 2026-08-06. The service events log
        // always has the underlying exception now (View service log shows it).
        var why = rc switch
        {
            ServiceControl.StartStopTimedOut => "The service did not respond within 15 seconds.",
            ServiceControl.StartStopScmRefused => "Windows refused — the service may be missing or disabled.",
            9 => "The repair commands ran but the folder still isn't writable.",
            _ => "",
        };
        var hint = label is "start" or "install"
            ? " The service log usually says why — Service menu, View service log. If this keeps happening, try 'Repair service folder access' in the Service menu."
            : " The service log usually says why — Service menu, View service log.";
        return (false, outcome, $"Service {label} failed (code {rc}). {why}{hint}");
    }
}

/// <summary>
/// <c>RemSound --install-plugin</c> and <c>--uninstall-plugin</c>: the DAW plugin menu's Install and Remove, from the
/// command line. Ed, 2026-09-24: scripts "just in case a person can't for whatever reason get to their application.
/// It should behave the same way as if they had done it from the application." So this is the menu's own code,
/// <see cref="PluginInstaller"/>, with the menu's own message and the menu's own log line.
/// </summary>
internal static class PluginCommandLine
{
    /// <summary>Gate seams: where the plugin comes from and goes to. The real ones are the menu's.</summary>
    internal static Func<string?, (bool Ok, string Message)> Install = PluginInstaller.InstallFromCommandLine;
    internal static Func<(bool Ok, string Message)> Uninstall = PluginInstaller.Uninstall;

    /// <summary>0 when it worked, 1 when it didn't. <paramref name="folder"/>: where to install, for --install-plugin
    /// [folder]; null for where it is now.</summary>
    internal static int Run(bool install, TextWriter output, string? folder = null)
    {
        var (ok, message) = install ? Install(folder) : Uninstall();
        output.WriteLine(message);
        CommandLineLog.Event($"vst plugin {(install ? "install" : "remove")}: {(ok ? "ok" : "FAILED")} - {message} (from the command line)");
        return ok ? 0 : 1;
    }
}

/// <summary>
/// <c>RemSound --service install|uninstall|start|stop|status</c>: the Service menu from the command line, for the
/// scripts beside RemSound and for anyone who can't reach the window. The same elevated helper the menu runs (so
/// Windows asks for administrator permission in the same way), the same messages, the same lines in the service's
/// own log. The questions the menu asks first are the script's to ask; this does what it is told.
/// </summary>
internal static class ServiceCommandLine
{
    /// <summary>What was done, as the scripts read it: 0 done, 1 failed or refused, 3 nothing to do (already so).</summary>
    internal const int NothingToDo = 3;

    /// <summary>Gate seams: the service's state, and running the elevated helper. The real ones are the menu's.</summary>
    internal static Func<ServiceState> Query = ServiceControl.Query;
    internal static Func<string, int> RunElevated = verb => ServiceControl.RunElevated(verb);
    internal static Action<string> AppendServiceEvent = ServiceStore.AppendServiceEvent;

    internal static int Run(string? action, TextWriter output)
    {
        var (verb, label) = (action ?? "").Trim().ToLowerInvariant() switch
        {
            "install" => (ServiceControl.InstallVerb, "install"),
            "uninstall" or "remove" => (ServiceControl.UninstallVerb, "uninstall"),
            "start" => (ServiceControl.StartVerb, "start"),
            "stop" => (ServiceControl.StopVerb, "stop"),
            "status" => ("", "status"),
            _ => ("", ""),
        };
        if (label.Length == 0)
        {
            output.WriteLine("RemSound --service needs one of: install, uninstall, start, stop, status.");
            return 1;
        }

        ServiceState state;
        try { state = Query(); }
        catch (Exception ex)
        {
            output.WriteLine($"The service can't be reached on this PC: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        if (label == "status")
        {
            output.WriteLine("The RemSound service is " + Describe(state) + VersionNote(state) + ".");
            return 0;
        }

        // The menu greys out what doesn't apply; here it is said instead.
        var installed = state != ServiceState.NotInstalled;
        var already = label switch
        {
            "install" when installed => "The RemSound service is already installed.",
            "uninstall" when !installed => "The RemSound service isn't installed, so there is nothing to uninstall.",
            "start" when !installed => "The RemSound service isn't installed. Install it first.",
            "start" when state is ServiceState.Running or ServiceState.StartPending => "The RemSound service is already running.",
            "stop" when !installed => "The RemSound service isn't installed.",
            "stop" when state is ServiceState.Stopped or ServiceState.StopPending => "The RemSound service isn't running.",
            _ => null,
        };
        if (already is not null)
        {
            output.WriteLine(already);
            return NothingToDo;
        }

        CommandLineLog.Event($"service: {label} requested (elevated), from the command line");
        AppendServiceEvent($"{label} requested (elevated)");
        output.WriteLine($"Asking Windows for administrator permission to {label} the service...");
        var rc = RunElevated(verb);
        var (ok, outcome, message) = ServiceActionText.Describe(label, rc);
        CommandLineLog.Event($"service: {label} finished with code {rc} ({outcome}), from the command line");
        AppendServiceEvent($"{label} finished: code {rc} ({outcome})");
        output.WriteLine(message);
        return ok ? 0 : 1;
    }

    internal static string Describe(ServiceState s) => s switch
    {
        ServiceState.NotInstalled => "not installed",
        ServiceState.Running => "installed and running",
        ServiceState.Stopped => "installed and stopped",
        ServiceState.StartPending => "starting",
        ServiceState.StopPending => "stopping",
        _ => "in an unknown state",
    };

    private static string VersionNote(ServiceState s)
    {
        try
        {
            if (s is ServiceState.Running or ServiceState.Stopped && ServiceStore.LoadStatus() is { Version: { } ver })
                return $", version {ver}";
        }
        catch { /* the status file is a nicety */ }
        return "";
    }
}

/// <summary>
/// The app's own log, for a command-line action: the same file set and the same Logging switch as the window, so a
/// plugin installed by script is recorded exactly where one installed from the menu is.
/// </summary>
internal static class CommandLineLog
{
    /// <summary>Gate seam: see what was written.</summary>
    internal static Action<string>? TapForTest;

    // One log file for the whole command, opened on its first line. A new one per line made a file per line, and a second
    // line in the same second collided with the first file's name and was lost - "requested" logged, "finished" not
    // (found 2026-09-25).
    private static RemSoundLog? log;
    private static readonly object gate = new();

    internal static void Event(string line)
    {
        TapForTest?.Invoke(line);
        try
        {
            if (!AppConfig.Load().LoggingEnabled) return;
            lock (gate)
            {
                if (log is null)
                {
                    log = new RemSoundLog { Enabled = true };
                    AppDomain.CurrentDomain.ProcessExit += (_, _) => { lock (gate) { log?.Dispose(); log = null; } };
                }
                log.Event(line);
            }
        }
        catch { /* a log that can't be written must not fail the action */ }
    }

    /// <summary>Gate seam: the file this command's lines went to, if any were written; and a fresh start for the next.</summary>
    internal static string? PathForTest { get { lock (gate) return log?.Path; } }
    internal static void ResetForTest() { lock (gate) { log?.Dispose(); log = null; } }
}

/// <summary>
/// Tidies what an earlier version left next to the exe. The scripts folder was "Install Scripts" until 2026-09-24,
/// when it became "Install and Uninstall Scripts"; an update copies the new folder in and would leave the old one
/// beside it. Only the files RemSound put there are removed, and the folder only if that empties it.
/// </summary>
internal static class RetiredFiles
{
    internal const string OldScriptsFolder = "Install Scripts";
    internal const string ScriptsFolder = "Install and Uninstall Scripts";
    private static readonly string[] OldScripts = ["Install .NET for RemSound.cmd", "Install .NET for RemSound.ps1"];

    /// <summary>Returns what was removed, for the log. Never throws.</summary>
    internal static string? RemoveOldScriptsFolder(string appDirectory)
    {
        try
        {
            var old = Path.Combine(appDirectory, OldScriptsFolder);
            if (!Directory.Exists(old) || !Directory.Exists(Path.Combine(appDirectory, ScriptsFolder))) return null;
            var removed = 0;
            foreach (var name in OldScripts)
            {
                var file = Path.Combine(old, name);
                if (File.Exists(file)) { File.Delete(file); removed++; }
            }
            var emptied = !Directory.EnumerateFileSystemEntries(old).Any();
            if (emptied) Directory.Delete(old);
            return $"removed {removed} old script(s) from \"{OldScriptsFolder}\"" + (emptied ? " and the folder" : ", keeping the folder for the files somebody else put there");
        }
        catch (Exception ex) { return $"could not tidy \"{OldScriptsFolder}\": {ex.Message}"; }
    }
}
