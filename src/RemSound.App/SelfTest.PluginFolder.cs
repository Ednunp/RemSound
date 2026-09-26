using System.Text.RegularExpressions;
using RemSound.Core;
using RemSound.Plugin;

namespace RemSound.App;

/// <summary>
/// Where the DAW plugin goes (Ed, 2026-09-25): "give people a choice of where to install it. install in default location x,
/// or another button to browse for a location ... keep a record of where it went in remsound, so that when it updates, it
/// also updates the plugin in that location." A folder only an administrator can change asks Windows (Ed agreed); after a
/// folder of the person's own, Ed's message about where updates go; and the plugin plays the context help sounds chosen in
/// Preferences from copies beside it.
///
/// <para>Everything here happens under the run's own folder. A "protected" folder is one the check says can't be written,
/// and Windows' permission is the real helper run in this process with the exact arguments RemSound would re-launch itself
/// with - so the quoting, the folder and the sounds it is told are all proved, without a permission prompt.</para>
/// </summary>
internal static partial class SelfTest
{
    /// <summary>A command line split as Windows splits it for a program: quoted parts whole.</summary>
    private static string[] SplitArguments(string line) =>
        Regex.Matches(line, "\"([^\"]*)\"|(\\S+)").Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToArray();

    private static bool SameBytes(string a, string b) =>
        File.Exists(a) && File.Exists(b) && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));

    /// <summary>The machine settings this file's steps change, put back afterwards.</summary>
    private sealed record PluginFolderSettings(string? Folder, bool NoticeSuppressed, string? Declined, bool OpenCue, bool CloseCue, string? CustomOpen)
    {
        public static PluginFolderSettings Take()
        {
            var c = AppConfig.Load();
            return new(c.PluginFolder, c.PluginFolderNoticeSuppressed, c.PluginRefreshDeclinedVersion, c.EnableHelpOpenCue, c.EnableHelpCloseCue,
                c.MachineCueCustomPaths.TryGetValue(MainForm.CueId.HelpOpen, out var p) ? p : null);
        }

        public void Restore()
        {
            var c = AppConfig.Load();
            c.PluginFolder = Folder;
            c.PluginFolderNoticeSuppressed = NoticeSuppressed;
            c.PluginRefreshDeclinedVersion = Declined;
            c.EnableHelpOpenCue = OpenCue;
            c.EnableHelpCloseCue = CloseCue;
            if (CustomOpen is null) c.MachineCueCustomPaths.Remove(MainForm.CueId.HelpOpen);
            else c.MachineCueCustomPaths[MainForm.CueId.HelpOpen] = CustomOpen;
            c.Save();
        }
    }

    private static void EditConfig(Action<AppConfig> change)
    {
        var c = AppConfig.Load();
        change(c);
        c.Save();
    }

    /// <summary>
    /// PLUGIN FOLDER: installed where the person chooses, remembered, moved, updated and removed there; Windows asked only
    /// for a folder the person can't change; the help sounds follow Preferences; and --install-plugin takes a folder.
    /// </summary>
    private static string? PluginFolderIsRememberedMovedUpdatedAndRemoved()
    {
        var throwaway = Require(AppConfig.ThrowawayMachineDirectory, "the self-test must be a run that touches nothing real, with a machine folder of its own");
        if (!Directory.Exists(PluginInstaller.SourceDirectory))
            return Skip($"this build has no plugin folder to install from ({PluginInstaller.SourceDirectory})");
        var standard = PluginInstaller.StandardDirectory;
        Check(standard.StartsWith(throwaway, StringComparison.OrdinalIgnoreCase), $"premise: the standard place must be the run's own in a run that touches nothing real ({standard})");
        var realBefore = FolderFingerprint(PluginInstaller.RealInstallDirectory);

        var root = Path.Combine(throwaway, "plugin-folder-" + Guid.NewGuid().ToString("N")[..8]);
        var mine = Path.Combine(root, "My Plugins");                                  // a folder of the person's own, with a space
        var shared = Path.Combine(root, "Program Files", "Common Files", "VST3");     // one only an administrator can change
        Directory.CreateDirectory(mine);
        var settingsBefore = PluginFolderSettings.Take();
        var elevated = new List<string>();
        try
        {
            if (PluginInstaller.IsInstalledAt(standard)) PluginInstaller.UninstallForTest(standard);
            EditConfig(c => { c.PluginFolder = null; c.EnableHelpOpenCue = true; c.EnableHelpCloseCue = true; c.MachineCueCustomPaths.Remove(MainForm.CueId.HelpOpen); });

            // Its own RemSound folder inside whatever folder is picked - unless the folder picked is already that.
            Check(PluginInstaller.TargetFor(mine) == Path.Combine(mine, "RemSound"), "a chosen folder must get a RemSound folder of its own inside it, so its files never mix with other plugins'");
            Check(PluginInstaller.TargetFor(Path.Combine(mine, "RemSound") + "\\") == Path.Combine(mine, "RemSound"), "a folder that is already RemSound's own is used as it is");

            // 1. The standard place: not remembered as a choice, and the help sounds put beside it.
            var (ok, message) = PluginInstaller.InstallTo(standard);
            Check(ok && PluginInstaller.IsInstalledAt(standard), $"installing in the standard place must work: {message}");
            Check(message.Contains(standard, StringComparison.Ordinal), $"and say where it went (said: {message})");
            Check(AppConfig.Load().PluginFolder is null && PluginInstaller.InstallDirectory == standard, "the standard place is not remembered as a folder of the person's own");
            var (openSound, closeSound) = PluginInstaller.ChosenHelpSounds();
            Check(openSound is not null && closeSound is not null, "premise: this build must have the context help sounds to give the plugin");
            Check(SameBytes(Path.Combine(standard, PluginInstaller.HelpOpenSoundName), openSound!) && SameBytes(Path.Combine(standard, PluginInstaller.HelpCloseSoundName), closeSound!),
                "THE SOUNDS: the context help sounds chosen in Preferences must be beside the plugin, byte for byte");
            var manifest = File.ReadAllLines(Path.Combine(standard, PluginInstaller.ManifestName));
            Check(manifest.Contains(PluginInstaller.HelpOpenSoundName) && manifest.Contains(PluginInstaller.HelpCloseSoundName), "and listed, so Remove plugin takes them away too");

            // 2. Moved to a folder of the person's own: remembered, and the old copy removed.
            var target = PluginInstaller.TargetFor(mine);
            (ok, message) = PluginInstaller.InstallTo(target);
            Check(ok && PluginInstaller.IsInstalledAt(target), $"installing in a folder of the person's own must work: {message}");
            Check(AppConfig.Load().PluginFolder == target && PluginInstaller.InstallDirectory == target, "THE RECORD: the folder must be remembered, and from now on be where the plugin is");
            Check(!PluginInstaller.IsInstalledAt(standard) && !Directory.Exists(standard), "THE MOVE: the copy RemSound put in the standard place must be gone");
            Check(message.Contains("has been removed", StringComparison.Ordinal), $"and the message must say the old copy was removed (said: {message})");
            Check(elevated.Count == 0, "nothing may ask Windows for permission for folders the person can change");

            // 3. An update refreshes it there - and doesn't put one back in the standard place.
            File.WriteAllText(Path.Combine(target, PluginInstaller.VersionStampName), "5.0");
            Check(PluginInstaller.IsStaleAt(target, CommandLine.AppVersion), "premise: a plugin placed by 5.0 is out of date");
            Check(!PluginInstaller.RefreshNeedsPermission(target, CommandLine.AppVersion), "and a folder of the person's own needs no permission to update");
            var (refreshed, line) = PluginInstaller.RefreshIfStale();
            Check(refreshed && PluginInstaller.StampAt(target) == CommandLine.AppVersion, $"THE UPDATE: an update must refresh the plugin in the chosen folder ({line})");
            Check(!Directory.Exists(standard), "and not put a copy back in the standard place");

            // 4. The help sounds follow Preferences: off takes the copy away, a sound of one's own is copied, no change does nothing.
            EditConfig(c => c.EnableHelpCloseCue = false);
            Check(PluginInstaller.RefreshHelpSounds(), "switching the close sound off must change the plugin's copies");
            Check(!File.Exists(Path.Combine(target, PluginInstaller.HelpCloseSoundName)) && File.Exists(Path.Combine(target, PluginInstaller.HelpOpenSoundName)),
                "the close sound must be gone from beside the plugin, and the open sound left");
            Check(!PluginInstaller.RefreshHelpSounds(), "with nothing changed, nothing is copied");
            var custom = Path.Combine(root, "my help open.wav");
            File.WriteAllBytes(custom, MinimalWav());
            EditConfig(c => { c.MachineCueCustomPaths[MainForm.CueId.HelpOpen] = custom; c.EnableHelpCloseCue = true; });
            Check(PluginInstaller.RefreshHelpSounds(), "a sound of the person's own must change the copies");
            Check(SameBytes(Path.Combine(target, PluginInstaller.HelpOpenSoundName), custom) && File.Exists(Path.Combine(target, PluginInstaller.HelpCloseSoundName)),
                "the plugin must now have the person's own open sound, and the close sound back");

            // 5. A folder only an administrator can change: Windows is asked, once, and the real helper does the work.
            var protectedRoot = Path.Combine(root, "Program Files");
            PluginInstaller.CanWriteForTest = folder => !folder.StartsWith(protectedRoot, StringComparison.OrdinalIgnoreCase);
            PluginInstaller.ElevateForTest = arguments => { elevated.Add(arguments); return PluginInstaller.RunElevatedHelper(SplitArguments(arguments)); };
            var sharedTarget = PluginInstaller.TargetFor(shared);
            (ok, message) = PluginInstaller.InstallTo(sharedTarget);
            Check(ok && PluginInstaller.IsInstalledAt(sharedTarget), $"installing in a protected folder, with permission, must work: {message}");
            Check(elevated.Count == 1 && elevated[0].StartsWith($"{PluginInstaller.InstallToVerb} \"{sharedTarget}\"", StringComparison.Ordinal),
                $"THE PERMISSION: Windows must be asked once, to install there, the folder quoted whole (asked: {string.Join(" | ", elevated)})");
            Check(elevated[0].Contains($"{PluginInstaller.HelpOpenArg} \"{custom}\"", StringComparison.Ordinal),
                "the copy with permission must be told the person's sounds - it may run as another account, whose settings aren't theirs");
            Check(SameBytes(Path.Combine(sharedTarget, PluginInstaller.HelpOpenSoundName), custom), "and put the person's own sound beside the plugin");
            Check(AppConfig.Load().PluginFolder == sharedTarget && !PluginInstaller.IsInstalledAt(target), "remembered there, and moved out of the person's own folder");

            // A change of sound doesn't ask Windows: left until the next install or update.
            elevated.Clear();
            EditConfig(c => c.EnableHelpCloseCue = false);
            Check(!PluginInstaller.RefreshHelpSounds() && elevated.Count == 0, "a change of sound must not ask Windows for permission");
            Check(PluginInstaller.RefreshNeedsPermission(sharedTarget, "9.9"), "an update of a plugin in a protected folder needs permission, and start-up asks for it");

            // Permission refused: nothing changes.
            PluginInstaller.ElevateForTest = arguments => { elevated.Add(arguments); return -1; };
            (ok, message) = PluginInstaller.InstallTo(PluginInstaller.TargetFor(Path.Combine(protectedRoot, "Other")));
            Check(!ok && message.Contains("needs Windows' permission, and it wasn't given", StringComparison.Ordinal), $"a refused permission must be said plainly (said: {message})");
            Check(AppConfig.Load().PluginFolder == sharedTarget && PluginInstaller.IsInstalledAt(sharedTarget), "and change nothing");

            // Removing it from there asks Windows too, and takes every file RemSound put there.
            elevated.Clear();
            PluginInstaller.ElevateForTest = arguments => { elevated.Add(arguments); return PluginInstaller.RunElevatedHelper(SplitArguments(arguments)); };
            (ok, message) = PluginInstaller.Uninstall();
            Check(ok && !PluginInstaller.IsInstalledAt(sharedTarget), $"Remove plugin must remove it from the folder it is in: {message}");
            Check(elevated.Count == 1 && elevated[0] == $"{PluginInstaller.RemoveFromVerb} \"{sharedTarget}\"", $"asking Windows once, to remove it from there (asked: {string.Join(" | ", elevated)})");
            Check(!Directory.Exists(sharedTarget), "every file RemSound put there, the help sounds included, and then the empty folder");
            Check(AppConfig.Load().PluginFolder is null && PluginInstaller.InstallDirectory == standard, "and the folder is forgotten: the next install is in the standard place");

            // The helper takes a whole path or nothing.
            Check(PluginInstaller.RunElevatedHelper([PluginInstaller.InstallToVerb, "VST3"]) == 1 && PluginInstaller.RunElevatedHelper([PluginInstaller.InstallToVerb]) == 1,
                "the helper must refuse a folder that isn't a whole path");
            Check(PluginInstaller.IsElevatedHelper([PluginInstaller.RemoveFromVerb, sharedTarget]) && !PluginInstaller.IsElevatedHelper(["--install-plugin"]),
                "and start-up must know the helper from the scripts' command");

            // 6. The command line takes a folder, as the scripts pass it.
            PluginInstaller.CanWriteForTest = null;
            PluginInstaller.ElevateForTest = null;
            var said = new StringWriter();
            Check(CommandLine.TryRunMenuAction(["--install-plugin", mine], said, out var rc) && rc == 0, $"--install-plugin <folder> must install there: {said}");
            Check(PluginInstaller.IsInstalledAt(target) && AppConfig.Load().PluginFolder == target, "into its own RemSound folder, remembered");
            said = new StringWriter();
            Check(CommandLine.TryRunMenuAction(["--install-plugin", @"relative\folder"], said, out rc) && rc == 1 && said.ToString().Contains("not a whole folder path", StringComparison.Ordinal),
                $"a folder that isn't a whole path must be refused, plainly (said: {said})");
            Check(CommandLine.TryRunMenuAction(["--uninstall-plugin"], new StringWriter(), out rc) && rc == 0 && !PluginInstaller.IsInstalledAt(target),
                "--uninstall-plugin must remove it from the remembered folder");

            Check(FolderFingerprint(PluginInstaller.RealInstallDirectory) == realBefore,
                $"THE CLOBBER: the real VST3 folder changed during this step ({PluginInstaller.RealInstallDirectory})");
        }
        finally
        {
            PluginInstaller.CanWriteForTest = null;
            PluginInstaller.ElevateForTest = null;
            settingsBefore.Restore();
            try { Directory.Delete(root, recursive: true); } catch { /* the run's own folder goes anyway */ }
        }
        return "the standard place, a folder of the person's own and a protected one: each remembered, the old copy removed, an update "
             + "refreshed there, Windows asked only for the protected one (through the real helper, sounds and all) and refusal changed "
             + "nothing; the help sounds followed Preferences; --install-plugin took a folder and refused a partial one";
    }

    /// <summary>
    /// PLUGIN FOLDER: the menu asks where, with Windows' own folder picker for another folder; Ed's message follows a folder
    /// of the person's own until told not to; Reinstall says where it is now; an update asks once for permission to refresh
    /// a plugin in a protected folder.
    /// </summary>
    private static string? PluginFolderMenuAsksWhereAndSaysWhereUpdatesGo()
    {
        var throwaway = Require(AppConfig.ThrowawayMachineDirectory, "the self-test must be a run that touches nothing real");
        if (!Directory.Exists(PluginInstaller.SourceDirectory))
            return Skip($"this build has no plugin folder to install from ({PluginInstaller.SourceDirectory})");
        var standard = PluginInstaller.StandardDirectory;
        var realBefore = FolderFingerprint(PluginInstaller.RealInstallDirectory);
        var root = Path.Combine(throwaway, "plugin-menu-" + Guid.NewGuid().ToString("N")[..8]);
        var mine = Path.Combine(root, "My Plugins");
        Directory.CreateDirectory(mine);
        var target = PluginInstaller.TargetFor(mine);
        var settingsBefore = PluginFolderSettings.Take();

        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        MainForm? form = null;
        RemoteControlServer? server = null;
        var pipe = "RemSound.control.selftest.pluginfolder." + Guid.NewGuid().ToString("N")[..8];
        var elevated = new List<string>();
        var logStart = HeadlessRecords.Log.Mark;
        try
        {
            if (PluginInstaller.IsInstalledAt(standard)) PluginInstaller.UninstallForTest(standard);
            EditConfig(c => { c.PluginFolder = null; c.PluginFolderNoticeSuppressed = false; c.PluginRefreshDeclinedVersion = null; });
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            var main = form;
            server = new RemoteControlServer(pipe, () => main, new RemoteControlEngine(() => main));
            server.Start();
            string Ask(string command)
            {
                var reply = RemoteControl.Send(command, pipe, out var reached, connectTimeoutMs: 5000);
                Check(reached, $"the control channel did not answer \"{command}\": {reply}");
                return reply;
            }
            PluginInstallPlaceDialog? Place() => main.Invoke(() => Application.OpenForms.OfType<PluginInstallPlaceDialog>().FirstOrDefault());
            bool Showing(string words) => Ask("windows").Contains(words, StringComparison.Ordinal);
            void Ok(string what)
            {
                Check(WaitFor(() => Showing(what), TimeSpan.FromSeconds(30)), $"\"{what}\" must be said (windows: {Head(Ask("windows"))})");
                Check(Ask("answer OK").StartsWith("ok", StringComparison.Ordinal), $"and the message saying \"{what}\" closes with OK");
            }

            DriveWhilePumping(main, () =>
            {
                // Cancel changes nothing.
                var mark = HeadlessRecords.Log.Mark;
                Ask("menu DAW plugin > Install plugin");
                Check(WaitFor(() => Place() is not null, TimeSpan.FromSeconds(10)), "THE QUESTION: Install plugin must first ask where");
                var words = main.Invoke(() => Place()!.MessageBoxForTest.Text);
                Check(words.Contains(standard, StringComparison.Ordinal) && !words.Contains("installed now", StringComparison.Ordinal),
                    $"it must name the standard place, and say nothing of where it is now when it isn't installed (said: {words})");
                Check(main.Invoke(() => Place()!.StandardButton.AccessibleDescription) == standard, "the standard place's button must carry its path for a screen reader");
                Check(Ask("answer Cancel").StartsWith("ok", StringComparison.Ordinal), "Cancel must close it");
                Check(WaitFor(() => HeadlessRecords.Log.Since(mark).Any(l => l.Contains("vst plugin install: cancelled at the choice of folder", StringComparison.Ordinal)), TimeSpan.FromSeconds(5)),
                    "a cancelled choice must be logged");
                Check(!PluginInstaller.IsInstalledAt(standard) && !PluginInstaller.IsInstalledAt(target), "and install nothing");

                // Another folder, through Windows' own folder picker.
                Ask("menu DAW plugin > Install plugin");
                Check(WaitFor(() => Place() is not null, TimeSpan.FromSeconds(10)), "the question must open again");
                Ask("answer Choose another folder");
                Check(WaitFor(() => Windowless.NativeDialogs().Any(d => d.Title.Contains("RemSound DAW plugin", StringComparison.Ordinal)), TimeSpan.FromSeconds(30)),
                    "Choose another folder must open Windows' folder picker");
                // A picker ignores an answer that comes before it has filled itself in (see the remote control step).
                Thread.Sleep(1500);
                Check(Ask($"type {mine}").StartsWith("ok", StringComparison.Ordinal), "the folder must be typed into the picker");
                Check(Ask("answer Select Folder").StartsWith("ok", StringComparison.Ordinal), "and chosen");
                Ok($"is installed in {target}");
                Check(PluginInstaller.IsInstalledAt(target) && AppConfig.Load().PluginFolder == target, $"THE CHOICE: the plugin must be in the folder picked, remembered ({target})");

                // Ed's message, in his words, until told not to.
                Check(WaitFor(() => main.Invoke(() => Application.OpenForms.OfType<PluginFolderNoticeDialog>().Any()), TimeSpan.FromSeconds(10)),
                    "ED'S MESSAGE: after installing in a folder of the person's own, the plugin-updates message must show");
                var notice = main.Invoke(() => Application.OpenForms.OfType<PluginFolderNoticeDialog>().First().NoticeBox.Text);
                Check(notice == PluginFolderNoticeDialog.Words(target) && notice.StartsWith($"Please note: RemSound will now put any plugin updates in {target}. However,", StringComparison.Ordinal),
                    $"in Ed's words, naming the folder (said: {notice})");
                Check(!main.Invoke(() => Application.OpenForms.OfType<PluginFolderNoticeDialog>().First().DontShowAgainBox.Checked), "\"Do not show me this message again\" starts unticked");
                Check(Ask("click Do not show me this message again").StartsWith("ok", StringComparison.Ordinal), "and can be ticked");
                Check(Ask("answer OK").StartsWith("ok", StringComparison.Ordinal), "OK closes the message");
                Check(WaitFor(() => AppConfig.Load().PluginFolderNoticeSuppressed, TimeSpan.FromSeconds(5)), "ticked, it must be remembered");

                // Reinstall says where it is now; the standard place moves it back, with no message.
                mark = HeadlessRecords.Log.Mark;
                Ask("menu DAW plugin > Reinstall plugin");
                Check(WaitFor(() => Place() is not null, TimeSpan.FromSeconds(10)), "Reinstall plugin must ask where too");
                words = main.Invoke(() => Place()!.MessageBoxForTest.Text);
                Check(words.Contains($"It is installed now in {target}.", StringComparison.Ordinal), $"it must say where the plugin is now (said: {words})");
                Check(Ask("answer Install in the standard place").StartsWith("ok", StringComparison.Ordinal), "the standard place must be chosen by its button");
                Ok($"is installed in {standard}");
                Check(PluginInstaller.IsInstalledAt(standard) && !PluginInstaller.IsInstalledAt(target) && AppConfig.Load().PluginFolder is null,
                    "back in the standard place, the other copy removed, and no folder remembered");
                Thread.Sleep(300);
                Check(!main.Invoke(() => Application.OpenForms.OfType<PluginFolderNoticeDialog>().Any()), "the standard place needs no message about where updates go");

                // With the message hidden, a folder of one's own again shows none - and says so in the log.
                Ask("menu DAW plugin > Reinstall plugin");
                Check(WaitFor(() => Place() is not null, TimeSpan.FromSeconds(10)), "the question must open again");
                Ask("answer Choose another folder");
                Check(WaitFor(() => Windowless.NativeDialogs().Any(d => d.Title.Contains("RemSound DAW plugin", StringComparison.Ordinal)), TimeSpan.FromSeconds(30)), "the picker must open again");
                Thread.Sleep(1500);
                Ask($"type {mine}");
                Ask("answer Select Folder");
                Ok($"is installed in {target}");
                Check(WaitFor(() => HeadlessRecords.Log.Since(mark).Any(l => l.Contains("plugin-updates message is hidden (Do not show me this message again), so not shown", StringComparison.Ordinal)), TimeSpan.FromSeconds(5)),
                    "a hidden message must not show, and the log must say why");
                Check(!main.Invoke(() => Application.OpenForms.OfType<PluginFolderNoticeDialog>().Any()), "and it must not show");

                // An update of a plugin in a protected folder: asked once, Not now remembered for this version, Update now asks Windows.
                PluginInstaller.CanWriteForTest = folder => !folder.StartsWith(mine, StringComparison.OrdinalIgnoreCase);
                PluginInstaller.ElevateForTest = arguments => { elevated.Add(arguments); return PluginInstaller.RunElevatedHelper(SplitArguments(arguments)); };
                File.WriteAllText(Path.Combine(target, PluginInstaller.VersionStampName), "5.0");
                Check(PluginInstaller.RefreshNeedsPermission(target, CommandLine.AppVersion), "premise: out of date in a folder that needs permission");
                main.BeginInvoke(() => main.OfferProtectedPluginRefresh(target));
                Check(WaitFor(() => Showing("Update the DAW plugin?") || Showing("needs updating to match"), TimeSpan.FromSeconds(10)),
                    $"THE UPDATE QUESTION: start-up must ask to update a plugin in a protected folder (windows: {Head(Ask("windows"))})");
                Check(Ask("answer Not now").StartsWith("ok", StringComparison.Ordinal), "Not now must answer it");
                Check(WaitFor(() => AppConfig.Load().PluginRefreshDeclinedVersion == CommandLine.AppVersion, TimeSpan.FromSeconds(5)), "Not now must be remembered for this version");
                Check(PluginInstaller.StampAt(target) == "5.0" && elevated.Count == 0, "and change nothing, asking Windows nothing");
                mark = HeadlessRecords.Log.Mark;
                main.BeginInvoke(() => main.OfferProtectedPluginRefresh(target));
                Check(WaitFor(() => HeadlessRecords.Log.Since(mark).Any(l => l.Contains("not asked again", StringComparison.Ordinal)), TimeSpan.FromSeconds(5)),
                    "ONCE: the next start must not ask again for the same version, and must log why");
                Check(!Showing("needs updating to match"), "and must show no question");
                // The message about where updates go is switched back on here, so "an update shows no message" can fail.
                EditConfig(c => { c.PluginRefreshDeclinedVersion = null; c.PluginFolderNoticeSuppressed = false; });
                mark = HeadlessRecords.Log.Mark;
                main.BeginInvoke(() => main.OfferProtectedPluginRefresh(target));
                Check(WaitFor(() => Showing("needs updating to match"), TimeSpan.FromSeconds(10)), "after the next update it asks again");
                Check(Ask("answer Update now").StartsWith("ok", StringComparison.Ordinal), "Update now must answer it");
                Ok($"is installed in {target}");
                Check(elevated.Count == 1 && elevated[0].StartsWith($"{PluginInstaller.InstallToVerb} \"{target}\"", StringComparison.Ordinal),
                    $"Update now must ask Windows once, to install there (asked: {string.Join(" | ", elevated)})");
                Check(PluginInstaller.StampAt(target) == CommandLine.AppVersion, "and the plugin must now be this build's");
                Thread.Sleep(300);
                Check(!main.Invoke(() => Application.OpenForms.OfType<PluginFolderNoticeDialog>().Any())
                      && !HeadlessRecords.Log.Since(mark).Any(l => l.Contains("plugin-updates message", StringComparison.Ordinal)),
                    "an update is not a new choice of folder: no message about where updates go, even with the message switched on");
                PluginInstaller.CanWriteForTest = null;
                PluginInstaller.ElevateForTest = null;

                // Remove plugin takes it from the remembered folder.
                Ask("menu DAW plugin > Remove plugin");
                Ok("has been removed");
                Check(!PluginInstaller.IsInstalledAt(target) && AppConfig.Load().PluginFolder is null, "Remove plugin must remove it from the folder it is in, and forget the folder");
            }, TimeSpan.FromSeconds(240));

            // Each line is stamped with its time, so the words are looked for within it.
            var log = HeadlessRecords.Log.Since(logStart);
            Check(log.Any(l => l.Contains($"vst plugin install: chose {target} (a folder of the person's own)", StringComparison.Ordinal))
                  && log.Any(l => l.Contains($"vst plugin install: chose {standard} (the standard place)", StringComparison.Ordinal)),
                "THE LOG: each choice of folder must be logged, saying which kind it was");
            Check(log.Any(l => l.Contains("vst plugin: showing the plugin-updates message for ", StringComparison.Ordinal))
                  && log.Any(l => l.Contains("vst plugin: the plugin-updates message is hidden from now on", StringComparison.Ordinal)),
                "the message showing, and being hidden, must be logged");
            Check(log.Any(l => l.Contains("DAW plugin: updating the plugin in", StringComparison.Ordinal) && l.Contains("declined (Not now)", StringComparison.Ordinal))
                  && log.Any(l => l.Contains("vst plugin update: ok", StringComparison.Ordinal)),
                "the update being declined, and then done, must be logged");
            Check(FolderFingerprint(PluginInstaller.RealInstallDirectory) == realBefore,
                $"THE CLOBBER: the real VST3 folder changed during this step ({PluginInstaller.RealInstallDirectory})");
        }
        finally
        {
            PluginInstaller.CanWriteForTest = null;
            PluginInstaller.ElevateForTest = null;
            server?.Dispose();
            try { form?.Dispose(); } catch { /* teardown */ }
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
            settingsBefore.Restore();
            try { if (PluginInstaller.IsInstalledAt(standard)) PluginInstaller.UninstallForTest(standard); } catch { /* the run's own folder */ }
            try { Directory.Delete(root, recursive: true); } catch { /* the run's own folder goes anyway */ }
        }
        return "Install plugin asked where; Cancel changed nothing; Windows' folder picker chose a folder, which was remembered; Ed's message "
             + "showed in his words, was hidden when ticked, and not shown again; Reinstall said where it was and moved it back; an update in a "
             + "protected folder was asked once per version and done with Windows' permission; Remove took it from the folder it was in";
    }

    /// <summary>
    /// PLUGIN: inside a music program the context help sounds play from the copies beside the plugin, and none plays when a
    /// copy isn't there; inside RemSound itself the plugin leaves RemSound's own context help alone.
    /// </summary>
    private static string? PluginContextHelpSoundsAndItsOwnHost()
    {
        var saved = (ContextHelp.PlayOpenSound, ContextHelp.PlayCloseSound, ContextHelp.Log, ContextHelp.OpenManualInBrowser, ContextHelp.ManualPath);
        var folder = Path.Combine(Path.GetTempPath(), "remsound-plugin-sounds-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var plugin = new RemSoundPlugin { Host = new StubAudioHost() };
        try
        {
            // Inside RemSound (the gate is RemSound, as --plugin-window is): nothing of RemSound's is replaced.
            Check(RemSoundPlugin.InsideRemSound, "premise: the self-test runs inside RemSound itself");
            var ours = saved;
            plugin.WireContextHelp();
            Check(ContextHelp.PlayOpenSound == ours.PlayOpenSound && ContextHelp.PlayCloseSound == ours.PlayCloseSound && ContextHelp.Log == ours.Log
                  && ContextHelp.OpenManualInBrowser == ours.OpenManualInBrowser && ContextHelp.ManualPath == ours.ManualPath,
                "THE SAFEGUARD: inside RemSound the plugin must not take over RemSound's context help - its sounds, log, browser and manual");

            // Inside a music program: the sounds beside the plugin.
            RemSoundPlugin.InsideRemSoundForTest = false;
            RemSoundPlugin.FolderForTest = folder;
            plugin.WireContextHelp();
            Check(ContextHelp.ManualPath() == Path.Combine(folder, "readme.html"), "inside a music program the manual is the plugin's own copy");
            var before = PluginHelpSounds.PlaysForTest;
            ContextHelp.PlayOpenSound?.Invoke();
            ContextHelp.PlayCloseSound?.Invoke();
            Check(PluginHelpSounds.PlaysForTest == before, "with no sound beside the plugin (switched off in Preferences), none must play");
            File.WriteAllBytes(Path.Combine(folder, PluginHelpSounds.OpenName), MinimalWav());
            File.WriteAllBytes(Path.Combine(folder, PluginHelpSounds.CloseName), MinimalWav());
            ContextHelp.PlayOpenSound?.Invoke();
            Check(PluginHelpSounds.PlaysForTest == before + 1, "THE SOUND: the open sound beside the plugin must play when context help opens");
            ContextHelp.PlayCloseSound?.Invoke();
            Check(PluginHelpSounds.PlaysForTest == before + 2, "and the close sound when it closes");
            Check(PluginHelpSounds.OpenName == PluginInstaller.HelpOpenSoundName && PluginHelpSounds.CloseName == PluginInstaller.HelpCloseSoundName,
                "the plugin must look for the names RemSound puts beside it");
        }
        finally
        {
            RemSoundPlugin.InsideRemSoundForTest = null;
            RemSoundPlugin.FolderForTest = null;
            (ContextHelp.PlayOpenSound, ContextHelp.PlayCloseSound, ContextHelp.Log, ContextHelp.OpenManualInBrowser, ContextHelp.ManualPath) = saved;
            try { Directory.Delete(folder, recursive: true); } catch { /* temp */ }
        }
        return "inside RemSound the plugin left RemSound's context help alone; inside a music program the open and close sounds played from the "
             + "copies beside it, under the names RemSound gives them, and none played without them";
    }
}
