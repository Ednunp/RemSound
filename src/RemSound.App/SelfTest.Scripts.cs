using System.Text.RegularExpressions;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// The scripts beside the exe (the "Install and Uninstall Scripts" folder). Ed, 2026-09-24: an install and uninstall
/// script for the DAW plugin and the service, and start and stop for the service, "just in case a person can't for
/// whatever reason get to their application. It should behave the same way as if they had done it from the
/// application."
///
/// <para>So a script is only ever as good as the command it calls, and these prove every call is one RemSound answers,
/// runs the menu's own code in the menu's own order, and leaves the same lines in the same logs. The service is never
/// really installed here: the state and the elevated helper are stand-ins, and the service log is captured, so a gate
/// run touches nothing real.</para>
/// </summary>
internal static partial class SelfTest
{
    private static readonly string[] ShippedScripts =
    [
        "Install .NET for RemSound.cmd", "Install .NET for RemSound.ps1",
        "Install DAW plugin.cmd", "Uninstall DAW plugin.cmd",
        "Install service.cmd", "Uninstall service.cmd", "Start service.cmd", "Stop service.cmd",
    ];

    /// <summary>
    /// SCRIPTS: every script calls a command RemSound answers, and does what its menu item does.
    /// </summary>
    private static string? ScriptsDoWhatTheirMenuItemsDo()
    {
        var root = FindSourceRoot() ?? Skip("the source tree isn't reachable (set REMSOUND_SOURCE_ROOT), so the scripts can't be read");
        var folder = Path.Combine(root, RetiredFiles.ScriptsFolder);
        Check(Directory.Exists(folder), $"the \"{RetiredFiles.ScriptsFolder}\" folder is missing from the source tree");
        Check(!Directory.Exists(Path.Combine(root, RetiredFiles.OldScriptsFolder)), $"the old \"{RetiredFiles.OldScriptsFolder}\" folder must be gone");
        foreach (var name in ShippedScripts)
            Check(File.Exists(Path.Combine(folder, name)), $"\"{name}\" is missing from \"{RetiredFiles.ScriptsFolder}\"");
        var unexpected = Directory.GetFiles(folder).Select(Path.GetFileName).Where(n => !ShippedScripts.Contains(n)).ToList();
        Check(unexpected.Count == 0, $"a script nobody declared here: {string.Join(", ", unexpected)} - add it to ShippedScripts and give it a check");

        var savedInstall = PluginCommandLine.Install;
        var savedUninstall = PluginCommandLine.Uninstall;
        var savedQuery = ServiceCommandLine.Query;
        var savedElevated = ServiceCommandLine.RunElevated;
        var savedServiceLog = ServiceCommandLine.AppendServiceEvent;
        var savedTap = CommandLineLog.TapForTest;
        var installs = 0;
        var removes = 0;
        var installFolders = new List<string?>();
        var state = ServiceState.NotInstalled;
        var elevated = new List<string>();
        var serviceLog = new List<string>();
        var appLog = new List<string>();
        var loggingBefore = false;
        try
        {
            PluginCommandLine.Install = folder => { installs++; installFolders.Add(folder); return (true, "The RemSound plugin is installed. (self-test)"); };
            PluginCommandLine.Uninstall = () => { removes++; return (true, "The RemSound plugin is removed. (self-test)"); };
            ServiceCommandLine.Query = () => state;
            ServiceCommandLine.RunElevated = verb =>
            {
                elevated.Add(verb);
                state = verb == ServiceControl.InstallVerb ? ServiceState.Stopped
                    : verb == ServiceControl.StartVerb ? ServiceState.Running
                    : verb == ServiceControl.StopVerb ? ServiceState.Stopped
                    : verb == ServiceControl.UninstallVerb ? ServiceState.NotInstalled
                    : state;
                return 0;
            };
            ServiceCommandLine.AppendServiceEvent = serviceLog.Add;
            CommandLineLog.TapForTest = appLog.Add;
            // With the log file on (this run's throwaway settings), so what reaches the FILE can be read back below.
            var logCfg = AppConfig.Load();
            loggingBefore = logCfg.LoggingEnabled;
            logCfg.LoggingEnabled = true;
            logCfg.Save();
            CommandLineLog.ResetForTest();

            // What each script must end up asking RemSound for, in order. Run in an order a person might, so each
            // finds the service in the state its menu item needs.
            var expected = new (string Script, string[] Elevated, int Installs, int Removes)[]
            {
                // One call for each place it offers: the standard place, the shared folder, and a folder typed in.
                ("Install DAW plugin.cmd", [], 3, 0),
                ("Uninstall DAW plugin.cmd", [], 0, 1),
                ("Install service.cmd", [ServiceControl.InstallVerb, ServiceControl.StartVerb], 0, 0),
                ("Stop service.cmd", [ServiceControl.StopVerb], 0, 0),
                ("Start service.cmd", [ServiceControl.StartVerb], 0, 0),
                ("Uninstall service.cmd", [ServiceControl.UninstallVerb], 0, 0),
            };
            foreach (var (script, wantElevated, wantInstalls, wantRemoves) in expected)
            {
                var text = File.ReadAllText(Path.Combine(folder, script));
                Check(!Regex.IsMatch(text, "(?<!\r)\n"), $"{script} must have Windows line endings: a batch file with bare line feeds can jump to the wrong label");
                Check(text.Contains(@"set ""REMSOUND=%~dp0..\RemSound.exe""", StringComparison.Ordinal), $"{script} must find RemSound.exe in the folder above it, wherever that is");
                var calls = Regex.Matches(text, "^\"%REMSOUND%\" (.+?)\\s*$", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
                Check(calls.Count > 0, $"{script} calls nothing");
                elevated.Clear();
                installFolders.Clear();
                installs = removes = 0;
                foreach (var call in calls)
                {
                    var output = new StringWriter();
                    // As cmd would hand them over: its variables filled in, each quoted path one argument.
                    var expanded = call.Replace("%LOCALAPPDATA%", @"C:\Users\Someone\AppData\Local", StringComparison.OrdinalIgnoreCase)
                                       .Replace("%CommonProgramFiles%", @"C:\Program Files\Common Files", StringComparison.OrdinalIgnoreCase)
                                       .Replace("%FOLDER%", @"D:\My Plugins", StringComparison.OrdinalIgnoreCase);
                    Check(!expanded.Contains('%'), $"{script} calls \"{call}\" with a variable this check doesn't know");
                    var callArgs = Regex.Matches(expanded, "\"([^\"]*)\"|(\\S+)").Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToArray();
                    Check(CommandLine.TryRunMenuAction(callArgs, output, out var rc),
                        $"{script} calls \"{call}\", which RemSound doesn't answer - it would start RemSound normally instead");
                    Check(rc == 0, $"{script}: \"{call}\" came back {rc}: {output}");
                }
                Check(elevated.SequenceEqual(wantElevated), $"{script} must ask for {string.Join(", then ", wantElevated.DefaultIfEmpty("nothing elevated"))}, and asked for {string.Join(", then ", elevated.DefaultIfEmpty("nothing"))}");
                Check(installs == wantInstalls && removes == wantRemoves, $"{script} must install the plugin {wantInstalls} time(s) and remove it {wantRemoves} (did {installs} and {removes})");
                if (script == "Install DAW plugin.cmd")
                    Check(installFolders.SequenceEqual([@"C:\Users\Someone\AppData\Local\Programs\Common\VST3", @"C:\Program Files\Common Files\VST3", @"D:\My Plugins"]),
                        $"Install DAW plugin.cmd must offer the standard place, the shared VST3 folder and a folder typed in, each passed whole (passed: {string.Join(" | ", installFolders)})");
            }
            // The standard place the script names is the one RemSound calls standard.
            Check(PluginInstaller.SameFolder(PluginInstaller.TargetFor(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Common", "VST3")), PluginInstaller.RealInstallDirectory),
                "the script's standard place must be where RemSound puts the plugin as standard");
            var placeScript = File.ReadAllText(Path.Combine(folder, "Install DAW plugin.cmd"));
            Check(placeScript.Contains("choice /c 1234", StringComparison.Ordinal) && placeScript.Contains("if errorlevel 4 goto cancelled", StringComparison.Ordinal),
                "Install DAW plugin.cmd must ask where, answered with 1 to 4, 4 cancelling");

            // The questions the menu asks are the scripts' to ask, in the menu's words, with a number to press.
            var install = File.ReadAllText(Path.Combine(folder, "Install service.cmd"));
            Check(ServiceActionText.InstallQuestion.StartsWith("Install the RemSound send-only service?", StringComparison.Ordinal)
                  && install.Contains("Install the RemSound send-only service?", StringComparison.Ordinal)
                  && install.Contains("choice /c 12", StringComparison.Ordinal),
                "Install service.cmd must ask the menu's question first, answered with 1 or 2");
            Check(install.Contains("Do you want to start it now?", StringComparison.Ordinal) && ServiceActionText.StartNowQuestion.Contains("Do you want to start it now?", StringComparison.Ordinal),
                "and, like the menu, offer to start it once it is installed");
            var uninstall = File.ReadAllText(Path.Combine(folder, "Uninstall service.cmd"));
            Check(uninstall.Contains("Uninstall the RemSound service?", StringComparison.Ordinal) && uninstall.Contains("choice /c 12", StringComparison.Ordinal),
                "Uninstall service.cmd must ask the menu's question first, answered with 1 or 2");

            // The same lines in the same logs as the menu.
            Check(serviceLog.Contains("install requested (elevated)") && serviceLog.Contains("install finished: code 0 (success)"),
                $"the service's own log must get the lines the menu writes (got: {string.Join(" | ", serviceLog)})");
            Check(appLog.Any(l => l.StartsWith("vst plugin install: ok - ", StringComparison.Ordinal)) && appLog.Any(l => l.StartsWith("vst plugin remove: ok - ", StringComparison.Ordinal)),
                "the app's log must get the plugin lines the menu writes");
            Check(appLog.Any(l => l.StartsWith("service: install requested (elevated)", StringComparison.Ordinal)), "and the service lines");
            // In ONE file, the command's own: a new file per line lost the second line whenever it came within the same
            // second, and scattered the rest over a file each (found 2026-09-25).
            var cliLog = CommandLineLog.PathForTest;
            var cliText = cliLog is null ? "" : ReadSharedText(cliLog);
            Check(cliText.Contains("vst plugin install: ok", StringComparison.Ordinal) && cliText.Contains("service: install requested (elevated)", StringComparison.Ordinal)
                  && cliText.Contains("service: install finished", StringComparison.Ordinal),
                $"a command's lines must all reach one log file, the later ones included (file: {cliLog ?? "none"})");

            // What the menu greys out is said instead, and nothing is asked of Windows.
            state = ServiceState.Running;
            elevated.Clear();
            var said = new StringWriter();
            Check(ServiceCommandLine.Run("start", said) == ServiceCommandLine.NothingToDo && elevated.Count == 0 && said.ToString().Contains("already running", StringComparison.Ordinal),
                "starting a running service must say so and ask Windows for nothing");
            state = ServiceState.NotInstalled;
            Check(ServiceCommandLine.Run("uninstall", new StringWriter()) == ServiceCommandLine.NothingToDo && elevated.Count == 0, "and uninstalling one that isn't there");

            // Declined permission reads exactly as the menu reads it.
            state = ServiceState.Running;
            ServiceCommandLine.RunElevated = _ => -1;
            said = new StringWriter();
            Check(ServiceCommandLine.Run("stop", said) == 1 && said.ToString().Contains(ServiceActionText.Describe("stop", -1).Message, StringComparison.Ordinal),
                "a declined permission must fail, in the menu's words");
            Check(ServiceCommandLine.Run("status", said = new StringWriter()) == 0 && said.ToString().StartsWith("The RemSound service is installed and running", StringComparison.Ordinal),
                $"status must say how the service is (got: {said})");
            Check(ServiceCommandLine.Run("dance", new StringWriter()) == 1, "and an unknown action is refused");
        }
        finally
        {
            PluginCommandLine.Install = savedInstall;
            PluginCommandLine.Uninstall = savedUninstall;
            ServiceCommandLine.Query = savedQuery;
            ServiceCommandLine.RunElevated = savedElevated;
            ServiceCommandLine.AppendServiceEvent = savedServiceLog;
            CommandLineLog.TapForTest = savedTap;
            CommandLineLog.ResetForTest();
            var restoreCfg = AppConfig.Load();
            restoreCfg.LoggingEnabled = loggingBefore;
            restoreCfg.Save();
        }

        // The real ones are the menu's own: the plugin through PluginInstaller, the service through the elevated helper,
        // and the menu's messages from the same place the command line takes them.
        Check(PluginCommandLine.Install.Method.DeclaringType == typeof(PluginInstaller) && PluginCommandLine.Install.Method.Name == nameof(PluginInstaller.InstallFromCommandLine),
            "--install-plugin must run PluginInstaller.InstallFromCommandLine, the menu's own code");
        Check(PluginCommandLine.Uninstall.Method.DeclaringType == typeof(PluginInstaller) && PluginCommandLine.Uninstall.Method.Name == nameof(PluginInstaller.Uninstall),
            "--uninstall-plugin must run PluginInstaller.Uninstall");
        var actions = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "CommandLineActions.cs"));
        Check(actions.Contains("RunElevated = verb => ServiceControl.RunElevated(verb);", StringComparison.Ordinal),
            "--service must run the menu's elevated helper, ServiceControl.RunElevated");
        var mainForm = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "MainForm.cs"));
        Check(mainForm.Contains("ServiceActionText.Describe(label, rc)", StringComparison.Ordinal) && mainForm.Contains("ServiceActionText.InstallQuestion", StringComparison.Ordinal)
              && !mainForm.Contains("was cancelled, or administrator rights were declined", StringComparison.Ordinal),
            "the Service menu must take its words from ServiceActionText, the same place the command line does, so they can't drift apart");
        return "all six scripts call commands RemSound answers and ask for exactly what their menu items do, in order, with the menu's "
            + "questions and the menu's log lines; already-so is said, not done; a declined permission reads as the menu reads it";
    }

    /// <summary>
    /// SCRIPTS: the old "Install Scripts" folder is tidied away after an update, and nothing anybody else put there goes.
    /// </summary>
    private static string? OldScriptsFolderIsTidiedAway()
    {
        var app = Path.Combine(Path.GetTempPath(), "remsound-retired-" + Guid.NewGuid().ToString("N"));
        try
        {
            var old = Path.Combine(app, RetiredFiles.OldScriptsFolder);
            var now = Path.Combine(app, RetiredFiles.ScriptsFolder);
            Directory.CreateDirectory(old);
            File.WriteAllText(Path.Combine(old, "Install .NET for RemSound.cmd"), "old");
            File.WriteAllText(Path.Combine(old, "Install .NET for RemSound.ps1"), "old");

            Check(RetiredFiles.RemoveOldScriptsFolder(app) is null && File.Exists(Path.Combine(old, "Install .NET for RemSound.cmd")),
                "with no new folder beside it (an older copy), the old folder must be left alone");

            Directory.CreateDirectory(now);
            File.WriteAllText(Path.Combine(old, "my own notes.txt"), "not RemSound's");
            var said = RetiredFiles.RemoveOldScriptsFolder(app);
            Check(!File.Exists(Path.Combine(old, "Install .NET for RemSound.cmd")) && !File.Exists(Path.Combine(old, "Install .NET for RemSound.ps1")),
                "RemSound's own old scripts must go once the new folder is there");
            Check(File.Exists(Path.Combine(old, "my own notes.txt")) && Directory.Exists(old), "but a file somebody else put there must stay, and its folder with it");
            Check(said is not null && said.Contains("keeping the folder", StringComparison.Ordinal), $"and the log line must say so (got: {said})");

            File.Delete(Path.Combine(old, "my own notes.txt"));
            File.WriteAllText(Path.Combine(old, "Install .NET for RemSound.cmd"), "old");
            RetiredFiles.RemoveOldScriptsFolder(app);
            Check(!Directory.Exists(old), "with only RemSound's files in it, the old folder must go entirely");
        }
        finally
        {
            try { Directory.Delete(app, recursive: true); } catch { /* teardown */ }
        }
        return "the old scripts folder goes once the new one is beside it, a file somebody else put there stays, and an older copy is left alone";
    }
}
