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

    private static string? PreferencesClosingKeepsYourPlace()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        var saved = AppConfig.Load();
        var restoreShowEq = saved.ShowPanEqTab;
        MainForm? form = null;
        try
        {
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            form.ConnectedPeersListForTest.Select();
            Check(form.DeepestActiveControlForTest == form.ConnectedPeersListForTest, "premise: the keyboard is on Connected peers");
            var rebuilds = form.TabLayoutRebuildsForTest;
            form.ApplyMainTabLayoutForTest();   // what closing Preferences does, with nothing changed
            Check(form.TabLayoutRebuildsForTest == rebuilds,
                "THE LOST PLACE: closing Preferences with nothing changed must leave the tabs alone - taking them down moved the keyboard onto the tab strip");
            Check(form.DeepestActiveControlForTest == form.ConnectedPeersListForTest, "and the keyboard stays where it was");

            // A real change: the tabs are rebuilt, and the keyboard put back.
            saved.ShowPanEqTab = !restoreShowEq;
            saved.Save();
            form.ApplyMainTabLayoutForTest();
            Check(form.TabLayoutRebuildsForTest == rebuilds + 1, "premise: a change on the Appearance tab rebuilds the tabs");
            Check(form.DeepestActiveControlForTest == form.ConnectedPeersListForTest,
                "THE LOST PLACE: after a real change the keyboard must be put back on the control it was on");
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            var restore = AppConfig.Load();
            restore.ShowPanEqTab = restoreShowEq;
            restore.Save();
            CuePlayer.GloballyMuted = restoreMuted;
        }
        return "closing Preferences with nothing changed left the tabs alone and the keyboard on Connected peers; a real change rebuilt "
             + "them and put the keyboard back";
    }
}
