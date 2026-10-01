using RemSound.Core;
namespace RemSound.App;

/// <summary>
/// What an update leaves in the install folder, and Preferences closing without moving your place (2026-09-25 sweep; Ed,
/// 2026-09-26: build them all before the release).
/// </summary>
internal static partial class SelfTest
{
    private static string? UpdateLeftoversArePutRight()
    {
        var dir = Path.Combine(Path.GetTempPath(), "remsound-leftovers-" + Guid.NewGuid().ToString("N"));
        var running = new Version(6, 0, 0, 0);
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? VersionOf(string path) => versions.GetValueOrDefault(Path.GetFileName(path));
        bool NotBusy(string _) => false;
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var name in new[] { "RemSound.dll", "RemSound.Core.dll", "RemSound.exe", "readme.html" })
                File.WriteAllText(Path.Combine(dir, name), name);
            versions["RemSound.dll"] = "6.0.0.0";
            versions["RemSound.Core.dll"] = "5.9.0.0";   // the update was cut off before this one was swapped
            versions["RemSound.exe"] = "6.0.0.0";

            // 1. Cut off half way: the backup is still there, with the old files in it.
            var backup = Path.Combine(dir, UpdateApplier.BackupFolderName);
            Directory.CreateDirectory(backup);
            File.WriteAllText(Path.Combine(backup, "RemSound.dll"), "old");
            var said = InstallRepair.CheckAtStart(dir, running, VersionOf, NotBusy);
            var marker = Path.Combine(dir, UpdateApplier.IncompleteMarkerName);
            Check(!Directory.Exists(backup),
                "THE FROZEN SERVICE: a backup left by a cut-off update must not stay where the service reads it as an update still going on");
            Check(File.Exists(marker) && File.ReadAllText(marker).Contains("cut off part way", StringComparison.Ordinal),
                $"THE UNTOLD BREAK: a cut-off update must be said at the next start ({string.Join(" | ", said)})");
            Check(Directory.GetDirectories(dir, UpdateApplier.KeptBackupPrefix + "*").Length == 1, "and the old files it moved aside kept");
            Check(InstallRepair.FilesNamedIn(File.ReadAllText(marker)).SequenceEqual(["RemSound.dll"]), "the note must name the files moved aside");

            // 2. Not whole yet (one of RemSound's own files is the old version): the note stays, and so do the old files.
            InstallRepair.CheckAtStart(dir, running, VersionOf, NotBusy);
            Check(File.Exists(marker) && Directory.GetDirectories(dir, UpdateApplier.KeptBackupPrefix + "*").Length == 1,
                "while the install is not whole the note must stay, and the old files with it");

            // Put right by unzipping RemSound over the folder: every file is the version running.
            versions["RemSound.Core.dll"] = "6.0.0.0";
            said = InstallRepair.CheckAtStart(dir, running, VersionOf, NotBusy);
            Check(!File.Exists(marker),
                $"THE ENDLESS NOTE: once the install is whole again the note must clear itself - unzipping over the folder did not clear it ({string.Join(" | ", said)})");
            Check(Directory.GetDirectories(dir, UpdateApplier.KeptBackupPrefix + "*").Length == 0,
                "THE PILE: the old files kept from the rollback must go once nothing needs them");

            // A named file missing is not whole, whatever the versions say.
            File.WriteAllText(marker, "x\r\n\r\nThese files are not as they were:\r\n  plugin\\RemSound.Plugin.vst3\r\n\r\nmore");
            InstallRepair.CheckAtStart(dir, running, VersionOf, NotBusy);
            Check(File.Exists(marker), "a file the note names that is not there must keep the note");

            // A folder with none of RemSound's program files in it cannot be judged whole.
            Check(!InstallRepair.IsWhole(Path.Combine(dir, UpdateApplier.BackupFolderName + "-none"), running, VersionOf, []) , "premise: an empty folder is never whole");

            // 3. While an update is being applied, nothing is touched.
            Directory.CreateDirectory(backup);
            File.WriteAllText(Path.Combine(backup, "RemSound.dll"), "live");
            File.Delete(marker);
            said = InstallRepair.CheckAtStart(dir, running, VersionOf, _ => true);
            Check(Directory.Exists(backup) && !File.Exists(marker),
                "THE LIVE SWAP: while an update is being applied its backup must be left exactly where it is");
            using (var held = new Mutex(initiallyOwned: true, InstallRepair.UpdaterMutexName))
                Check(InstallRepair.UpdaterBusy(dir), "the updater's lock must be seen as an update being applied");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* temp */ } }

        // And the window's start-up really does it, before it decides what to say (nobody to tell here: it says nothing).
        var startDir = Path.Combine(Path.GetTempPath(), "remsound-leftovers-start-" + Guid.NewGuid().ToString("N"));
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        MainForm? form = null;
        try
        {
            Directory.CreateDirectory(Path.Combine(startDir, UpdateApplier.BackupFolderName));
            File.WriteAllText(Path.Combine(startDir, UpdateApplier.BackupFolderName, "RemSound.dll"), "old");
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            form.UpdateNoteDirForTest = startDir;
            form.NobodyToAskForTest = () => true;
            form.TellAboutFailedUpdateForTest();
            Check(!Directory.Exists(Path.Combine(startDir, UpdateApplier.BackupFolderName)) && File.Exists(Path.Combine(startDir, UpdateApplier.IncompleteMarkerName)),
                "THE UNLOOKED FOLDER: starting RemSound must put right what a cut-off update left, before anything is said");
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            CuePlayer.GloballyMuted = restoreMuted;
            try { Directory.Delete(startDir, recursive: true); } catch { /* temp */ }
        }

        var root = FindSourceRoot();
        if (root is null) return Skip("the install folder is put right, but the source tree is not reachable to check the wiring (set REMSOUND_SOURCE_ROOT)");
        var applier = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "UpdateApplier.cs"));
        Check(applier.Contains("using var applying = TryHoldApplyingLock();", StringComparison.Ordinal), "the updater must hold its lock while it changes files");
        var window = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "MainForm.cs"));
        var tell = SourceMethodBody(window, "private void MaybeTellAboutFailedUpdate()");
        Check(tell.IndexOf("InstallRepair.CheckAtStart(", StringComparison.Ordinal) is > 0 and var check
              && check < tell.IndexOf("UpdateNoteToTell(dir)", StringComparison.Ordinal),
            "the start-up must put the install folder right before it decides what to say");
        return "a cut-off update's backup was kept aside and said; the note stayed while the install was not whole and cleared itself "
             + "once it was, taking the old files with it; a missing named file kept it; a live update was left alone";
    }

    /// <summary>Closing Preferences leaves the keyboard where it was. Until 2026-09-28 this called the tab layout directly -
    /// what closing Preferences is said to do - and never closed Preferences, so the close path's wiring was unproven (the
    /// review of every step). Now Preferences is opened from the real Options menu and closed with its own button through
    /// the control channel, on the hidden desktop: once with nothing changed, once with the pan/EQ tab switched.</summary>
    private static string? PreferencesClosingKeepsYourPlace()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        var saved = AppConfig.Load();
        var restoreShowEq = saved.ShowPanEqTab;
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        MainForm? form = null;
        RemoteControlServer? server = null;
        var pipe = "RemSound.control.selftest.prefsplace." + Guid.NewGuid().ToString("N")[..8];
        // Opening Preferences holds the command until it closes; the channel answers with what is on screen after this
        // wait. Three seconds by default - a second is plenty for Preferences to be up, and saves four seconds a run.
        var restoreWait = RemoteControl.CommandWait;
        RemoteControl.CommandWait = TimeSpan.FromSeconds(1);
        try
        {
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
            void OpenAndClose(Action? change)
            {
                DriveWhilePumping(main, () =>
                {
                    var opened = Ask("menu Options > Preferences");
                    Check(opened.Contains("\"Preferences\"", StringComparison.Ordinal), $"Options, Preferences must open Preferences (got: {Head(opened)})");
                    change?.Invoke();
                    Check(Ask("answer Close").StartsWith("ok", StringComparison.Ordinal), "Preferences closes with its own button");
                }, TimeSpan.FromSeconds(30));
                for (var k = 0; k < 5; k++) Application.DoEvents();
                Check(!Application.OpenForms.OfType<PreferencesDialog>().Any(), "premise: Preferences has closed");
            }

            form.ConnectedPeersListForTest.Select();
            Check(form.DeepestActiveControlForTest == form.ConnectedPeersListForTest, "premise: the keyboard is on Connected peers");
            var rebuilds = form.TabLayoutRebuildsForTest;
            OpenAndClose(null);
            Check(form.TabLayoutRebuildsForTest == rebuilds,
                "THE LOST PLACE: closing Preferences with nothing changed must leave the tabs alone - taking them down moved the keyboard onto the tab strip");
            Check(form.DeepestActiveControlForTest == form.ConnectedPeersListForTest,
                $"and the keyboard stays where it was ({form.DeepestActiveControlForTest?.AccessibleName ?? form.DeepestActiveControlForTest?.GetType().Name ?? "nowhere"})");

            // A real change on the Appearance tab: the tabs are rebuilt when Preferences closes, and the keyboard put back.
            OpenAndClose(() =>
            {
                Check(Ask("tab Appearance").StartsWith("ok", StringComparison.Ordinal), "the Appearance tab must be reachable by name");
                var set = Ask($"set \"Show the volume, pan and EQ for peers tab\" {(restoreShowEq ? "off" : "on")}");
                Check(set.StartsWith("ok", StringComparison.Ordinal), $"the pan/EQ tab box must take the change (got: {set})");
            });
            Check(form.TabLayoutRebuildsForTest == rebuilds + 1,
                $"closing Preferences after a change on the Appearance tab must rebuild the tabs - the close must apply it ({form.TabLayoutRebuildsForTest - rebuilds} rebuilds)");
            Check(form.DeepestActiveControlForTest == form.ConnectedPeersListForTest,
                $"THE LOST PLACE: after a real change the keyboard must be put back on the control it was on ({form.DeepestActiveControlForTest?.AccessibleName ?? form.DeepestActiveControlForTest?.GetType().Name ?? "nowhere"})");
        }
        finally
        {
            server?.Dispose();
            RemoteControl.CommandWait = restoreWait;
            try { form?.Dispose(); } catch { /* teardown */ }
            Windowless.SetActiveForTest(false);
            var restore = AppConfig.Load();
            restore.ShowPanEqTab = restoreShowEq;
            restore.Save();
            CuePlayer.GloballyMuted = restoreMuted;
        }
        return "Preferences opened from the Options menu and closed with its own button: with nothing changed the tabs were left alone "
             + "and the keyboard stayed on Connected peers; with the pan/EQ tab switched they were rebuilt and the keyboard put back";
    }
}
