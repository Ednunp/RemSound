using System.Drawing;
using System.Net;
using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App;

/// <summary>
/// The app batch of the 2026-09-25 sweep (Ed: "yes. fix everything that you suggested"), part two: the key clicks, the
/// ASIO splash, the "update could not be put back" note, the service's own update, F1 and the picker at start, help from
/// the tray menu, and the tidy-ups.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>THE KEY CLICKS: an output open only while clicks are on, and built again if its device goes away.</summary>
    private static string? KeyClicksOpenOnlyWhileOnAndComeBack()
    {
        var restoreEnabled = KeyClickService.Enabled;
        KeyClickService.PlayerFactoryForTest = KeyClickService.SilentPlayerForTest;
        try
        {
            KeyClickService.Enabled = false;
            Check(!KeyClickService.PlayerOpenForTest, "THE OPEN DEVICE: with key clicks off, no output may be held open all session");
            var before = KeyClickService.PlayerBuildsForTest;
            KeyClickService.Enabled = true;
            Check(KeyClickService.PlayerOpenForTest && KeyClickService.PlayerBuildsForTest == before + 1, "switched on, the output opens");

            using var box = new TextBox();
            _ = box.Handle;
            KeyClickService.KillPlayerForTest();   // its device went away
            KeyClickService.ForgetBackoffForTest();
            KeyClickService.KeyPressedForTest(box.Handle);
            Check(KeyClickService.PlayerBuildsForTest == before + 2,
                "THE LOST CLICKS: an output whose device went away must be opened again on the next key, not left dead until a restart");
            KeyClickService.KillPlayerForTest();
            KeyClickService.KeyPressedForTest(box.Handle);
            Check(KeyClickService.PlayerBuildsForTest == before + 2, "but not tried on every key: at most once every few seconds");

            KeyClickService.Enabled = false;
            Check(!KeyClickService.PlayerOpenForTest, "switched off, it closes");
        }
        finally
        {
            KeyClickService.Enabled = restoreEnabled;
            KeyClickService.PlayerFactoryForTest = null;
        }
        return "the key-click output was open only while clicks were on, was opened again on the next key after its device went away, "
             + "and not on every key after that";
    }

    /// <summary>THE SPLASH: dismissed before its window is up, it never comes up; and a start that fails still dismisses it.</summary>
    private static string? TheAsioSplashNeverSticks()
    {
        using var hold = new ManualResetEventSlim(false);
        AsioLoadingSplash.BeforeShowForTest = () => hold.Wait(TimeSpan.FromSeconds(10));
        AsioLoadingSplash? splash = null;
        try
        {
            splash = AsioLoadingSplash.StartForTest("Self-test splash");   // back after its two-second wait, the window not yet up
            splash.Dismiss();
            hold.Set();
            var ended = splash.EndedForTest(TimeSpan.FromSeconds(3));
            if (!ended) splash.ForceCloseForTest();
            Check(ended, "THE STUCK SPLASH: dismissed before its window was up, the splash must never come up - it stayed on top for good");
        }
        finally
        {
            AsioLoadingSplash.BeforeShowForTest = null;
            hold.Set();
        }
        var root = FindSourceRoot();
        if (root is null) return Skip("the splash was right, but the source tree is not reachable to check start-up's dismiss (set REMSOUND_SOURCE_ROOT)");
        var program = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "Program.cs"));
        Check(program.Contains("try { built = new MainForm(store, profile, title, nextPath); }", StringComparison.Ordinal)
              && program.Contains("finally { splash?.Dismiss(); }", StringComparison.Ordinal),
            "THE FAILED START: start-up must dismiss the splash whether or not the main window could be built");
        return "a splash dismissed before its window was up never came up, and start-up dismisses it even when the window fails to build";
    }

    /// <summary>THE NOTE THAT NEVER STOPS: "could not be put back fully" can now be answered.</summary>
    private static string? TheUpdateNoteCanBeAnswered()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        var dir = Path.Combine(Path.GetTempPath(), "remsound-update-note-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var marker = Path.Combine(dir, UpdateApplier.IncompleteMarkerName);
        File.WriteAllText(marker, "The last update could not be put back fully.");
        MainForm? form = null;
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        try
        {
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            var main = form;
            main.SomebodyToAnswerForTest = true;
            main.NobodyToAskForTest = () => false;
            main.UpdateNoteDirForTest = dir;
            void AskAndAnswer(string answer)
            {
                DriveWhilePumping(main, () =>
                {
                    main.BeginInvoke(main.TellAboutFailedUpdateForTest);
                    Check(WaitFor(() => main.Invoke(() => Application.OpenForms.OfType<HeadlessQuestionForm>().Any()), TimeSpan.FromSeconds(5)), "the note must be said");
                    main.Invoke(() =>
                    {
                        var box = Application.OpenForms.OfType<HeadlessQuestionForm>().First();
                        Check(box.Question.Contains(MainForm.UpdatePutRightQuestion, StringComparison.Ordinal),
                            "THE ENDLESS NOTE: it must ask whether the install has been put right, not only say it is broken");
                        var button = Require(EveryFocusableUnder(box).OfType<Button>().FirstOrDefault(b => b.Text.Replace("&", "") == answer), $"the {answer} button");
                        button.PerformClick();
                    });
                    Check(WaitFor(() => main.Invoke(() => !Application.OpenForms.OfType<HeadlessQuestionForm>().Any()), TimeSpan.FromSeconds(5)), "and close once answered");
                }, TimeSpan.FromSeconds(20));
            }
            AskAndAnswer("No");
            Check(File.Exists(marker), "No: it is said again next start");
            AskAndAnswer("Yes");
            Check(!File.Exists(marker), "THE WAY OUT: Yes must stop it being said - the note goes");
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }
        return "the note about an update that could not be put back asked whether it had been put right: No kept it, Yes removed it";
    }

    /// <summary>THE SERVICE'S OWN UPDATE: never over an unsettled folder, and a failed copy tried again and checked.</summary>
    private static string? TheServiceCopiesOnlyWhatItCanCheck()
    {
        var root = Path.Combine(Path.GetTempPath(), "remsound-service-copy-" + Guid.NewGuid().ToString("N"));
        var app = Path.Combine(root, "app");
        var restorePause = ServiceControl.SelfUpdateCopyPause;
        ServiceControl.SelfUpdateCopyPause = TimeSpan.FromMilliseconds(20);
        try
        {
            Directory.CreateDirectory(app);
            File.WriteAllBytes(Path.Combine(app, "RemSound.dll"), Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray());
            File.WriteAllText(Path.Combine(app, "readme.html"), "manual");
            File.WriteAllText(Path.Combine(app, UpdateApplier.FailureMarkerName), "the app's own last update failed");

            // A copy that throws part way: tried again, and proved.
            var bin = Path.Combine(root, "bin");
            var log = new List<string>();
            var calls = 0;
            var ok = ServiceControl.CopyAndCheck(app, bin, log.Add, (s, d) =>
            {
                calls++;
                if (calls == 1)
                {
                    Directory.CreateDirectory(d);
                    File.WriteAllBytes(Path.Combine(d, "RemSound.dll"), [1, 2, 3]);   // half-written
                    throw new IOException("the file is in use");
                }
                ServiceControl.CopyProgramTo(s, d);
            });
            Check(ok && calls == 2, $"THE HALF COPY: a copy that failed part way must be tried again and checked, not left half-written ({calls} tries: {string.Join(" | ", log)})");
            Check(!File.Exists(Path.Combine(bin, UpdateApplier.FailureMarkerName)), "the updater's notes about the app's folder are not program files");

            // One that never works: said so, plainly.
            log.Clear();
            ok = ServiceControl.CopyAndCheck(app, Path.Combine(root, "bin2"), log.Add, (_, _) => throw new IOException("the file is in use"));
            Check(!ok && log.Any(l => l.StartsWith($"COPY INCOMPLETE after {ServiceControl.SelfUpdateCopyAttempts} attempts", StringComparison.Ordinal)),
                $"a copy that never works must end in a plain COPY INCOMPLETE ({string.Join(" | ", log)})");

            // Installing from a portable copy carries no update notes either.
            var installed = Path.Combine(root, "installed");
            Directory.CreateDirectory(installed);
            Directory.CreateDirectory(Path.Combine(app, UpdateApplier.BackupFolderName));
            File.WriteAllText(Path.Combine(app, UpdateApplier.BackupFolderName, "RemSound.dll"), "old");
            AppInstaller.CopyProgramFilesForTest(app, installed);
            Check(File.Exists(Path.Combine(installed, "RemSound.dll")), "premise: the program is installed");
            Check(!File.Exists(Path.Combine(installed, UpdateApplier.FailureMarkerName)) && !Directory.Exists(Path.Combine(installed, UpdateApplier.BackupFolderName)),
                "THE INHERITED NOTE: installing a portable copy must not carry its own update's notes or backups into the install");

            // The service asks again when the helper did not restart it.
            var now = DateTime.UtcNow;
            Check(ServiceUpdate.StillWaitingOnRestart(now - TimeSpan.FromMinutes(1), now), "a restart asked for a minute ago is still waited on");
            Check(!ServiceUpdate.StillWaitingOnRestart(now - ServiceUpdate.RestartGiveUp - TimeSpan.FromSeconds(1), now),
                "THE STRANDED SERVICE: a restart that never happened must be asked for again, not waited on until the next reboot");
            Check(!ServiceUpdate.StillWaitingOnRestart(null, now), "and nothing asked for is nothing waited on");

            var source = FindSourceRoot();
            if (source is null) return Skip("the copying is right, but the source tree is not reachable to check the wiring (set REMSOUND_SOURCE_ROOT)");
            var control = File.ReadAllText(Path.Combine(source, "src", "RemSound.App", "ServiceControl.cs"));
            var service = File.ReadAllText(Path.Combine(source, "src", "RemSound.App", "RemSoundService.cs"));
            var refuse = control.IndexOf("NOT copying; the service carries on with the build it has", StringComparison.Ordinal);
            var stop = control.IndexOf("Log($\"stopping {ServiceName}\");", StringComparison.Ordinal);
            Check(refuse > 0 && stop > refuse && control.IndexOf("return 0;", refuse, StringComparison.Ordinal) < stop,
                "THE BROKEN COPY: an app folder that never settles must be left alone - the helper returns before stopping the service");
            Check(control.Contains("CopyAndCheck(appDir, ServiceStore.BinDirectory, Log);", StringComparison.Ordinal), "the self-update must copy through CopyAndCheck");
            Check(service.Contains("if (ServiceUpdate.StillWaitingOnRestart(restartAskedUtc, DateTime.UtcNow)) return;", StringComparison.Ordinal),
                "the service's update check must ask again once a restart has not happened");
        }
        finally
        {
            ServiceControl.SelfUpdateCopyPause = restorePause;
            try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        }
        return "a copy that failed part way was tried again and proved, one that never worked said COPY INCOMPLETE, a portable install "
             + "carried no update notes, an unsettled app folder is left alone, and the service asks again for a restart that never came";
    }

    /// <summary>F1 in the Start-menu uninstall window, the picker in front, and help from the tray menu.</summary>
    private static string? StartupWindowsAndTrayHelp()
    {
        var root = FindSourceRoot();
        if (root is null) return Skip("the source tree is not reachable to check start-up (set REMSOUND_SOURCE_ROOT)");
        var program = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "Program.cs"));
        var uninstall = program.IndexOf("string.Equals(a, \"--uninstall\"", StringComparison.Ordinal);
        var help = program.IndexOf("HelpLauncher.Install();", uninstall, StringComparison.Ordinal);
        var run = program.IndexOf("AppInstaller.RunUninstallStandalone();", uninstall, StringComparison.Ordinal);
        Check(uninstall > 0 && help > uninstall && help < run,
            "THE DEAD F1: the Start-menu uninstall window has context help, so F1 must be switched on before it opens");
        Check(program.Contains("ForegroundDialog.Show(_ => dialog.ShowDialog())", StringComparison.Ordinal),
            "THE HIDDEN PICKER: the profile picker must come up in front - after an update RemSound is started by the updater, not the person");

        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        MainForm? form = null;
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        try
        {
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            var main = form;
            var wired = ContextHelp.BeforeTrayMenuReopens;
            Check(wired is not null, "THE LOST KEYBOARD: the tray must put RemSound in front before its menu comes back after help");
            var calls = 0;
            ContextHelp.BeforeTrayMenuReopens = () => { calls++; wired?.Invoke(); };
            var tray = main.TrayControllerForTest.TrayMenuForTest;
            DriveWhilePumping(main, () =>
            {
                main.BeginInvoke(() =>
                {
                    tray.Show(Point.Empty);
                    var item = tray.Items.OfType<ToolStripMenuItem>().First(i => i.Available && ContextHelp.KeyOfItem(i) is not null);
                    item.Select();
                    ContextHelp.ShowForItem(item);
                });
                Check(WaitFor(() => main.Invoke(() => Application.OpenForms.OfType<ContextHelpWindow>().Any()), TimeSpan.FromSeconds(5)), "premise: help opens from the tray menu");
                main.Invoke(() => { foreach (var w in Application.OpenForms.OfType<ContextHelpWindow>().ToList()) w.Close(); });
                Check(WaitFor(() => calls == 1, TimeSpan.FromSeconds(5)), "THE LOST KEYBOARD: closing help must put RemSound in front, then open the tray menu again");
                main.Invoke(() => tray.Close());
            }, TimeSpan.FromSeconds(20));
            ContextHelp.BeforeTrayMenuReopens = wired;

            // Menus of closed windows let go of (a window's closing disposes its menus; each profile switch made two more).
            var first = new ContextMenuStrip();
            ContextHelp.TrackMenu(first);
            var tracked = ContextHelp.TrackedMenusForTest;
            first.Dispose();
            using (var next = new ContextMenuStrip())
            {
                ContextHelp.TrackMenu(next);
                Check(ContextHelp.TrackedMenusForTest == tracked,
                    $"THE PILE: a closed window's menus must be let go of, not kept for good ({tracked}, then {ContextHelp.TrackedMenusForTest})");
            }
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
        }
        return "F1 is on before the Start-menu uninstall window opens, the picker comes up in front, the tray menu comes back after help "
             + "with RemSound in front, and a closed window's menus are let go of";
    }

    private sealed class ThrowingProvider : NAudio.Wave.IWaveProvider
    {
        public NAudio.Wave.WaveFormat WaveFormat { get; } = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("two threads at one session");
    }

    /// <summary>INSTALL AND UNINSTALL: changes offered for saving first (Cancel stops it), then the normal close.</summary>
    private static string? InstallAndUninstallLeaveProperly()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        var dir = Path.Combine(Path.GetTempPath(), "remsound-installer-leave-" + Guid.NewGuid().ToString("N"));
        MainForm? form = null;
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        var left = 0;
        MainForm.LeaveForInstallerForTest = () => Interlocked.Increment(ref left);
        try
        {
            var store = new ProfileStore(dir);
            var profile = Profile.NewBlank();
            profile.Title = "Installer check";
            store.Save(profile);
            try { form = new MainForm(store, profile, "Installer check", store.PathFor("Installer check"), headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            var main = form;
            bool? Prepare(string answer)
            {
                bool? result = null;
                DriveWhilePumping(main, () =>
                {
                    main.BeginInvoke(() => result = main.PrepareToLeaveForInstallerForTest("installing RemSound on this PC", "install it"));
                    Check(WaitFor(() => main.Invoke(() => Application.OpenForms.OfType<HeadlessQuestionForm>().Any()), TimeSpan.FromSeconds(5)),
                        "THE LOST CHANGES: unsaved changes must be offered for saving before RemSound is installed or removed");
                    main.Invoke(() =>
                    {
                        var box = Application.OpenForms.OfType<HeadlessQuestionForm>().First();
                        Require(EveryFocusableUnder(box).OfType<Button>().FirstOrDefault(b => b.Text.Replace("&", "") == answer), $"the {answer} button").PerformClick();
                    });
                    WaitFor(() => main.Invoke(() => result is not null), TimeSpan.FromSeconds(5));
                }, TimeSpan.FromSeconds(20));
                return result;
            }
            main.MarkProfileDirtyForTest();
            Check(Prepare("Cancel") == false, "Cancel must stop the install, with nothing copied or removed");
            Check(main.UnsavedChangesForTest, "and the changes are still there");
            Check(Prepare("Yes") == true && !main.UnsavedChangesForTest, "Yes must save them and go ahead");

            main.LeaveForInstallerRunForTest("open the installed copy");
            Check(left == 1 && main.ClosingForInstallerForTest,
                "THE CUT-OFF: the installer must end with the window's normal close, which finishes a recording and tells plugins, not by ending the process");
        }
        finally
        {
            MainForm.LeaveForInstallerForTest = null;
            try { form?.Dispose(); } catch { /* teardown */ }
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }

        var root = FindSourceRoot();
        if (root is null) return Skip("the window side is right, but the source tree is not reachable to check the installer's order (set REMSOUND_SOURCE_ROOT)");
        var installer = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "AppInstaller.cs"));
        var install = SourceMethodBody(installer, "public static void RunInstall(");
        var uninstall = SourceMethodBody(installer, "public static void RunUninstallInProcess(");
        Check(install.IndexOf("prepareToLeave()", StringComparison.Ordinal) is > 0 and var ready
              && ready < install.IndexOf("CopyProgramFiles(source, target);", StringComparison.Ordinal),
            "the install must offer to save and finish a recording before it copies anything - the copy then carries both");
        Check(uninstall.IndexOf("prepareToLeave()", StringComparison.Ordinal) is > 0 and var readyToGo
              && readyToGo < uninstall.IndexOf("RemoveChosenComponents(", StringComparison.Ordinal),
            "the uninstall must offer to save before it removes anything");
        Check(install.Contains("if (leave is not null) leave(); else Environment.Exit(0);", StringComparison.Ordinal)
              && uninstall.Contains("if (leave is not null) leave(); else Environment.Exit(0);", StringComparison.Ordinal),
            "both must leave through the window's close when there is a window");
        var window = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "MainForm.cs"));
        Check(window.Contains("() => PrepareToLeaveForInstaller(\"installing RemSound on this PC\", \"install it\"), () => LeaveForInstaller(", StringComparison.Ordinal)
              && window.Contains("() => PrepareToLeaveForInstaller(\"uninstalling RemSound\", \"uninstall it\"), () => LeaveForInstaller(", StringComparison.Ordinal),
            "and the Options menu must hand both of them the window's own offer and close");
        var prepare = SourceMethodBody(window, "private bool PrepareToLeaveForInstaller(");
        Check(prepare.Contains("recordingController.Stop();", StringComparison.Ordinal), "a recording must be finished before anything is copied");
        return "unsaved changes were offered for saving before installing - Cancel stopped it, Yes saved and went on - and the "
             + "installer left through the window's normal close; the install and uninstall ask before they copy or remove anything";
    }

    /// <summary>The tidy-ups: each small, each able to fail.</summary>
    private static string? SweepTidyUpsHold()
    {
        // Ctrl+S: a "don't show again" that cannot be saved is not a failed profile save.
        var blocker = Path.Combine(AppConfig.UserDataDirectory, "global config.json.tmp");
        MainForm? form = null;
        try
        {
            Directory.CreateDirectory(blocker);   // the config's temp file cannot be written: its save throws
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            try { form.RememberSaveConfirmationSuppressedForTest(); }
            catch (Exception ex) { Check(false, $"THE FALSE FAILURE: the profile is already saved; a failure to remember \"don't show again\" must not reach the save ({ex.GetType().Name})"); }
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            try { Directory.Delete(blocker); } catch { /* ours */ }
        }

        // "Press a key": Tab moves, and Cancel can be pressed.
        using (var capture = new HotkeyCaptureForm())
        {
            _ = capture.Handle;
            capture.BoxForTest.Select();
            Check(!capture.KeyDownForTest(Keys.Tab), "THE TRAPPED TAB: Tab in the box must move on to Cancel, not be taken as a shortcut");
            Check(capture.KeyDownForTest(Keys.Control | Keys.Shift | Keys.K), "a real combination in the box is still taken");
            capture.CancelForTest.Select();
            Check(!capture.KeyDownForTest(Keys.Space), "THE DEAD BUTTON: on Cancel, Space must press it, not be taken as a shortcut");
            Check(capture.CancelButton == capture.CancelForTest, "and Escape on it cancels");
        }

        // The password manager says what it could not save.
        var dir = Path.Combine(Path.GetTempPath(), "remsound-passwords-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProfileStore(dir);
            var kept = Profile.NewBlank();
            kept.Title = "Kept";
            store.Save(kept);
            var gone = Profile.NewBlank();
            gone.Title = "Gone";
            store.Save(gone);
            File.Delete(store.PathFor("Gone"));
            var rows = new List<(string Title, string Original, TextBox Box)>
            {
                ("Kept", "", new TextBox { Text = "new-password-1" }),
                ("Gone", "", new TextBox { Text = "new-password-2" }),
            };
            var (changed, failed) = ProfilePasswordManagerDialog.SaveChanges(store, rows);
            Check(changed && failed.Count == 1 && failed[0].StartsWith("Gone", StringComparison.Ordinal),
                $"THE SILENT FAILURE: a password that could not be saved must be named, the others saved ({string.Join(" | ", failed)})");
            foreach (var (_, _, box) in rows) box.Dispose();
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* temp */ } }

        // A claimed peer's stream ids are forgotten with their streams - in every configuration.
        foreach (var configuration in AudioConfigurations.All)
        {
            var engine = EngineFor(configuration);
            var peer = new IPEndPoint(IPAddress.Parse("10.9.9.1"), 47830);
            var session = engine.GetOrCreateSession(peer, 7, 8 * 1024 * 1024);
            var claims = new PluginPeerClaims();
            claims.Claim(peer.Address, Guid.NewGuid());
            engine.SetPluginPeerClaims(claims);
            var bytes = new byte[480 * 2 * 4];
            session.Write(bytes);
            var dest = new float[480 * 2];
            engine.ReadClaimedPeer(peer.Address, dest, 480);
            Check(engine.ClaimedStreamOwnersForTest == 1, $"{configuration.Describe()}: premise: the plugin's read notes the stream as the peer's");
            engine.RemoveSession(peer, 7);
            Check(engine.ClaimedStreamOwnersForTest == 1,
                $"{configuration.Describe()}: a stream just gone stays the peer's for a while - pruned and opened again, it is still them");
            engine.ForgetStreamOwnersForTest(Environment.TickCount64 + (long)PlayoutEngine.StreamOwnerGrace.TotalMilliseconds + 1000);
            Check(engine.ClaimedStreamOwnersForTest == 0,
                $"{configuration.Describe()}: THE LEFTOVER ID: a stream gone for good must not stay marked as the claimed peer's - a later stream with the same id from somebody else was silenced everywhere");
        }

        // The ASIO render path: a read that throws gives a silent block, never an exception into the driver's callback.
        var thrower = new ThrowingProvider();
        var tapped = new byte[1024];
        Array.Fill(tapped, (byte)7);
        try { SharedAsioDevice.ReadThroughTapForTest(thrower, tapped); }
        catch (Exception ex) { Check(false, $"THE DRIVER CALLBACK: the shared device's output must not throw into ASIO ({ex.GetType().Name})"); }
        Check(tapped.All(b => b == 0), "and the block it could not read must be silence");
        var broadcast = new byte[480 * 4 * 4];
        Array.Fill(broadcast, (byte)7);
        try { AsioRenderBackend.ReadThroughBroadcastForTest(thrower, 4, broadcast); }
        catch (Exception ex) { Check(false, $"THE DRIVER CALLBACK: the ASIO output must not throw into the driver ({ex.GetType().Name})"); }
        Check(broadcast.All(b => b == 0), "and the block it could not read must be silence");

        // The "someone ticked us" table stays bounded.
        using (var receiver = new AudioReceiver())
        {
            receiver.OnUnselectedSenderHeard = (_, _) => { };
            var fingerprint = new byte[8];
            for (var i = 0; i < 1000; i++)
                receiver.UnselectedSenderForTest(new IPEndPoint(IPAddress.Parse($"198.18.{i / 250}.{i % 250 + 1}"), 47830), fingerprint);
            Check(receiver.UnselectedSenderNotedForTest <= AudioReceiver.UnselectedSenderCap,
                $"THE ENDLESS TABLE: a thousand addresses must not all be kept ({receiver.UnselectedSenderNotedForTest})");
        }
        return "a failure to remember \"don't show again\" stayed out of the profile save, Tab and Cancel work in \"press a key\", the "
             + "password manager named what it could not save, a claimed peer's stream ids went with their streams in all three "
             + "configurations, and the \"someone ticked us\" table stayed bounded";
    }
}
