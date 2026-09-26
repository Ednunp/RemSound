using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// The DAW plugin menu's Install and Remove, chosen from the real menu through the control channel, the way a person
/// chooses them - in a run that touches nothing real, so into the run's own folder and never the VST3 folder every DAW on
/// the computer loads from. Until 2026-09-24 the install folder ignored the throwaway settings, so neither item could be
/// tested at all without replacing or deleting the real plugin.
/// </summary>
internal static partial class SelfTest
{
    private static string? PluginMenuInstallsAndRemovesInTheRunsOwnFolder()
    {
        var throwaway = AppConfig.ThrowawayMachineDirectory;
        Check(throwaway is not null, "the self-test must be a run that touches nothing real, with a machine folder of its own");
        var target = PluginInstaller.InstallDirectory;
        Check(target.StartsWith(throwaway!, StringComparison.OrdinalIgnoreCase),
            $"in a run that touches nothing real the plugin must install under the run's own folder, not {target}");
        Check(!string.Equals(Path.GetFullPath(target), Path.GetFullPath(PluginInstaller.RealInstallDirectory), StringComparison.OrdinalIgnoreCase),
            "and never into the real VST3 folder");
        if (!Directory.Exists(PluginInstaller.SourceDirectory))
            return Skip($"this build has no plugin folder to install from ({PluginInstaller.SourceDirectory})");
        var realBefore = FolderFingerprint(PluginInstaller.RealInstallDirectory);

        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        MainForm? form = null;
        RemoteControlServer? server = null;
        var pipe = "RemSound.control.selftest.plugin." + Guid.NewGuid().ToString("N")[..8];
        var appLog = new List<string>();
        try
        {
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            var main = form;
            main.LogForTest.EventTapForTest = l => { lock (appLog) appLog.Add(l); };
            server = new RemoteControlServer(pipe, () => main, new RemoteControlEngine(() => main));
            server.Start();
            string Ask(string command)
            {
                var reply = RemoteControl.Send(command, pipe, out var reached, connectTimeoutMs: 5000);
                Check(reached, $"the control channel did not answer \"{command}\": {reply}");
                return reply;
            }

            // The message comes once the copy is done, off the window's thread (Windows may be asking for permission).
            void SaidThenOk(string words, string what)
            {
                Check(WaitFor(() => Ask("windows").Contains(words, StringComparison.Ordinal), TimeSpan.FromSeconds(30)),
                    $"{what} (windows: {Head(Ask("windows"))})");
                Check(Ask("answer OK").StartsWith("ok", StringComparison.Ordinal), "and that message closes with OK");
            }

            DriveWhilePumping(main, () =>
            {
                // Install plugin asks where first (2026-09-25); the standard place is the run's own folder here.
                var asking = Ask("menu DAW plugin > Install plugin");
                Check(asking.Contains("Install the DAW plugin", StringComparison.Ordinal), $"choosing Install plugin must ask where (got: {Head(asking)})");
                Check(Ask("answer Install in the standard place").StartsWith("ok", StringComparison.Ordinal), "and the standard place can be chosen");
                SaidThenOk("is installed", "choosing Install plugin must say it is installed");
                Check(PluginInstaller.IsInstalledAt(target), $"the plugin must now be installed in the run's own folder ({target})");
                var menu = Ask("menu DAW plugin");
                Check(menu.Contains("Reinstall plugin", StringComparison.Ordinal),
                    $"once installed, the menu must offer Reinstall rather than Install (got: {Head(menu)})");

                Ask("menu DAW plugin > Remove plugin");
                SaidThenOk("has been removed", "choosing Remove plugin must say it has been removed");
                Check(!PluginInstaller.IsInstalledAt(target), "and the plugin must be gone from the run's own folder");
            }, TimeSpan.FromSeconds(60));

            lock (appLog)
            {
                Check(appLog.Any(l => l.StartsWith("vst plugin install: ok", StringComparison.Ordinal)), "the install must be in the app's log");
                Check(appLog.Any(l => l.StartsWith("vst plugin remove: ok", StringComparison.Ordinal)), "and so must the removal");
            }
            Check(FolderFingerprint(PluginInstaller.RealInstallDirectory) == realBefore,
                $"THE CLOBBER: the real VST3 folder changed during this step ({PluginInstaller.RealInstallDirectory})");
        }
        finally
        {
            server?.Dispose();
            try { form?.Dispose(); } catch { /* teardown */ }
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
        }
        return "Install plugin and Remove plugin, chosen from the real menu, installed into the run's own folder, offered Reinstall, "
             + "removed it again, said so and logged it - and the real VST3 folder every DAW loads from was not touched";
    }

    /// <summary>Every file in a folder with its size and time, or "absent".</summary>
    private static string FolderFingerprint(string folder) =>
        !Directory.Exists(folder)
            ? "absent"
            : string.Join("|", Directory.GetFiles(folder, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Select(f => { var i = new FileInfo(f); return $"{Path.GetRelativePath(folder, f)}:{i.Length}:{i.LastWriteTimeUtc.Ticks}"; }));
}
