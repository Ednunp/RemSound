using System.Diagnostics;
using System.Runtime;
using System.Windows.Forms;
using RemSound.Core;

namespace RemSound.App;

internal static class Program
{
    // The MainForm currently open in the loop below, or null between profile-switch
    // iterations. Tracked so the single-instance coordinator's activation callback (which
    // fires on a background thread when a second copy asks us to surface) can reach the live
    // window. volatile for cross-thread visibility; RestoreFromTray marshals to the UI thread.
    private static volatile MainForm? activeMainForm;

    private static bool HasArg(string[] args, string flag) =>
        Array.Exists(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when any Windows-service verb is present. Uses only the inlined const verb strings, so
    /// it references no service type and loads no service assembly — see the dispatch call in <see cref="Main"/>
    /// for why that matters on older Windows. When this is false (every normal launch) the service code is
    /// never reached.</summary>
    internal static bool IsServiceInvocation(string[] args) =>
        HasArg(args, ServiceControl.RunVerb) || HasArg(args, ServiceControl.InstallVerb)
        || HasArg(args, ServiceControl.UninstallVerb)
        || HasArg(args, ServiceControl.StartVerb) || HasArg(args, ServiceControl.StopVerb)
        || HasArg(args, ServiceControl.SelfUpdateVerb) || HasArg(args, ServiceControl.RepairVerb);

    // Writes an otherwise-fatal exception to a timestamped crash file in the logs folder, so a
    // "RemSound just disappeared, no dialog" report (#16) leaves a stack behind to diagnose instead
    // of nothing. Best-effort and self-contained — a crash handler must never throw.
    private static void WriteCrashReport(string source, Exception? ex)
    {
        try
        {
            var dir = AppConfig.LogsDirectory;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt");
            var report =
                $"RemSound crash report{Environment.NewLine}" +
                $"Version: {typeof(Program).Assembly.GetName().Version}{Environment.NewLine}" +
                $"Time:    {DateTime.Now:O}{Environment.NewLine}" +
                $"Source:  {source}{Environment.NewLine}{Environment.NewLine}" +
                (ex?.ToString() ?? "(no exception object)");
            File.WriteAllText(path, report);
            // And a line in the log itself, where whoever reads it is already looking.
            RemSoundLog.Current?.Event($"CRASH REPORT written to {Path.GetFileName(path)} — {source}: "
                + $"{ex?.GetType().Name}: {ex?.Message}");
        }
        catch { /* a crash handler must never throw */ }
    }

    /// <summary>
    /// Read the profile a switch asked for (File, Open; Recent profiles; quick switch). One that cannot be read - held open
    /// by Dropbox or a virus scan at that moment, a hand edit that broke it - becomes a new, blank, UNTITLED profile with no
    /// file. It used to become a blank profile under the profile's own title, and a window with that title works out its
    /// file from it: the next save, auto-save or "Yes" at exit wrote the blank settings, and an empty password, over the
    /// real file (2026-09-25 sweep).
    /// </summary>
    internal static (Profile Profile, string? Title, string? Path, string? Error) ReadProfileForSwitch(string path, string? title)
    {
        try
        {
            var json = File.ReadAllText(path);
            var profile = System.Text.Json.JsonSerializer.Deserialize<Profile>(json)
                          ?? throw new InvalidDataException("the file holds no profile");
            return (profile, !string.IsNullOrEmpty(title) ? title : System.IO.Path.GetFileNameWithoutExtension(path), path, null);
        }
        catch (Exception ex)
        {
            return (Profile.NewBlank(), null, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        // The auto-updater relaunches a temp copy of the NEW RemSound.exe in this mode to swap the
        // new files over the install while the old copy exits (see UpdateApplier / RemSoundUpdater).
        // Handle it first and return: this process is the installer, not a normal launch, so it must
        // not touch the single-instance lock, audio devices, or the start-up steps below.
        if (args.Length > 0 && Array.Exists(args, a => string.Equals(a, "--apply-update", StringComparison.OrdinalIgnoreCase)))
        {
            UpdateApplier.Run(args);
            return;
        }

        // Capture otherwise-fatal background-thread exceptions to a crash file, so a "RemSound just
        // vanished with no dialog" report (#16) leaves a stack behind instead of nothing. Best-effort.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrashReport("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        // And the WINDOW's own thread. WinForms catches an exception there itself and shows its
        // "Continue / Quit" box, which is why Ed saw an error on 2026-09-20 that left no trace anywhere:
        // this handler was never hooked, so the crash file and the log both knew nothing about it. The
        // report is written BEFORE the box appears, so it survives whichever button is pressed.
        Application.ThreadException += (_, e) =>
            WriteCrashReport("Application.ThreadException (the window's own thread)", e.Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashReport("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        // The DAW plugin's elevated helper: RemSound re-launched with Windows' permission to put the plugin in, or take it
        // out of, a folder only an administrator can change (the shared VST3 folder, 2026-09-25). It copies files and exits,
        // before the single-instance lock, the settings and the window.
        if (args.Length > 0 && PluginInstaller.IsElevatedHelper(args))
            Environment.Exit(PluginInstaller.RunElevatedHelper(args));

        // Windows-service verbs, handled before the single-instance lock and the UI start-up below.
        // The service is a SEPARATE role — it must never take the interactive single-instance lock — and
        // the elevated one-shot verbs (install/uninstall/start/stop) just do their SCM work and exit with
        // a status code. --run-service blocks in the SCM dispatcher until Windows stops the service.
        //
        // CRITICAL (2026-07-14): the dispatch lives in ServiceEntry, NOT inline here. RemSoundService
        // derives from ServiceBase, so naming it in THIS method's body would make the JIT load the
        // System.ServiceProcess assembly the instant Main is compiled — at the very start of every launch,
        // before any argument is read. On older Windows (Win7) that assembly won't load under .NET 10, so
        // the app crashed before it could open a window. The check below uses only inlined const verb
        // strings, and ServiceEntry.Dispatch is only reached when a service verb is genuinely present, so a
        // normal launch never touches that assembly.
        if (args.Length > 0 && IsServiceInvocation(args))
        {
            var serviceRc = ServiceEntry.Dispatch(args);
            // Terminate decisively. These verbs run in an elevated helper the app is waiting on; if any
            // library it touched left a non-background thread alive, a plain return would leave the process
            // lingering and the app blocked on it (part of the install-hang, 2026-07-17). Environment.Exit
            // guarantees the helper dies once its one-shot work is done. (--run-service returns here only
            // after the SCM has stopped the service, so exiting then is correct too.)
            Environment.Exit(serviceRc);
        }

        // --config-dir <folder> (test / portable isolation): redirect ALL user state - config,
        // profiles, logs - to an explicit folder for THIS process only. Applied first, before
        // anything reads or writes the default location, so a smoke test can run a real build
        // without touching the user's settings
        // (smoke-test brief, safety rule 1).
        if (CommandLine.TryGetConfigDir(args, out var configDir))
        {
            AppConfig.SetUserDataDirectoryOverride(configDir);
            // Including who this computer is on a server: a --config-dir run touches nothing real.
            AppConfig.SetMachineIdentityOverride(Path.Combine(configDir, "machine"));
        }

        // --silent: make this launch play no cue sounds at all (startup, connect, checkbox,
        // tab-switch, ...) and skip the front-most "missing sound file" warning. The automated test
        // harness passes it so its throwaway launches stay completely quiet instead of chiming a
        // startup cue (or popping a dialog) onto whoever happens to be at the screen. Set up front,
        // before consolidation or the startup cue can fire.
        if (Array.Exists(args, a => string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase)))
        {
            CuePlayer.GloballyMuted = true;
            Windowless.SilentLaunch = true;
        }

        // --headless: the whole app with no window, no sound and no speech, driven through the control channel
        // (Windowless, RemoteControl). Ed, 2026-09-24: "a way of driving remsound entirely without a window". The
        // sounds and the screen reader are switched off here, before anything can make either.
        var headlessRun = Array.Exists(args, a => string.Equals(a, "--headless", StringComparison.OrdinalIgnoreCase));
        if (headlessRun)
        {
            CuePlayer.GloballyMuted = true;
            ScreenReader.Suppressed = true;
        }

        // SustainedLowLatency tells the GC to avoid full (gen 2) collections while audio is streaming.
        // Gen 0/1 collections still happen but are sub-millisecond; the long pauses that were causing
        // the receiver to fall behind in clusters of 4-5 underruns at a time were almost certainly
        // gen 2 sweeps. This trades a bit of memory headroom (the GC will hold on to garbage longer)
        // for dramatically more predictable timing — exactly the trade real-time audio wants.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        ApplicationConfiguration.Initialize();

        // Before the first window of any kind: from here every window this thread makes is kept out of sight and focus.
        if (headlessRun) Windowless.Start();

        // The shared accessible controls take their sounds by injection so they carry no dependency
        // on the app's cue machinery (the VST plugin reuses the controls, not the app). Wire them
        // here so the APP behaves exactly as before.
        AccessibleCheckBox.PlayToggleSound = CheckSoundService.Play;
        QuietTabControl.PlayTabSwitchSound = TabSwitchSoundService.Play;
        PluginEditorPanel.WindowIcon = Theme.AppIcon;

        // Follow the user's chosen colour theme — "system" (match Windows light/dark) by default. This
        // is an experimental WinForms API; guarded so any failure just leaves the classic light theme
        // rather than stopping RemSound launching. Colours only — no effect on the screen reader.
        try
        {
            var mode = (AppConfig.Load().ThemeMode ?? "system").Trim().ToLowerInvariant();
#pragma warning disable WFO5001
            Application.SetColorMode(mode switch
            {
                "light" => SystemColorMode.Classic,
                "dark" => SystemColorMode.Dark,
                _ => SystemColorMode.System,
            });
#pragma warning restore WFO5001
        }
        catch { /* older runtime or API change — stay on the classic light theme */ }

        // --uninstall: launched from the Start-menu "Uninstall RemSound" shortcut or Windows
        // "Installed apps". Runs its own confirmation dialog and the wait-then-delete remover, then
        // exits. Handled here — after visual styles/theme are set so the dialog renders correctly, but
        // BEFORE the single-instance guard, because the installed copy it's removing may itself be
        // running (the remover force-closes it and retries the delete).
        if (Array.Exists(args, a => string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            // F1 here too: this window has context help of its own, and F1 did nothing in it (2026-09-25 sweep).
            HelpLauncher.WireContextHelp();
            HelpLauncher.Install();
            AppInstaller.RunUninstallStandalone();
            return;
        }

        // Command-line interface (Sensor-Readout-style). "Do-and-exit" commands (--help, --version,
        // --devices, --list-profiles, --list-named-peers, --selftest, --perftest, --diagnostics, --log,
        // --close, --control, and the developer verbs --plugin-window, --sign-update and --sign-server-release) run here —
        // before the single-instance lock and before the main window — and terminate the process.
        // Otherwise we collect launch overrides (--profile / --connect / --minimized) and continue the
        // normal GUI start below, applying them as we resolve the profile.
        var cliExit = CommandLine.Process(args, out var cli);
        if (cliExit is { } cliCode) Environment.Exit(cliCode);
        if (cli.StartMinimized) MainForm.startNextInstanceMinimized = true;
        // Headless takes the --minimized path exactly (no ASIO splash, parked in the tray), only never on screen.
        if (headlessRun) MainForm.startNextInstanceMinimized = true;

        // --foreground: the post-install relaunch passes this so the freshly-installed copy pulls
        // itself to the front and takes focus, instead of opening behind whatever's on top (a new
        // process has no "recent user input" credit, so Windows' foreground lock blocks it otherwise).
        if (Array.Exists(args, a => string.Equals(a, "--foreground", StringComparison.OrdinalIgnoreCase)))
            MainForm.forceForegroundOnStart = true;

        // --await-pid <pid>: the post-install relaunch passes the OLD (portable) copy's process id.
        // Wait for it to exit — releasing the single-instance lock — BEFORE we try to take that lock
        // just below, so the hand-over doesn't trip the "already running" guard. Bounded so a stale or
        // wrong pid can never hang startup.
        WaitForAwaitedProcess(args);

        // Single-instance guard. RemSound must never run as two copies at once: with the
        // auto-updater relaunching the app, a copy that didn't exit cleanly used to leave two
        // (then more) copies running, each playing received audio — Andre's "stacked and
        // stacked", deafening-audio runaway (2026-05-30). The lock makes that structurally
        // impossible. Acquired BEFORE anything user-visible so a second copy bows out (or
        // takes over a stuck one) before it ever touches audio devices or the network.
        using var instance = new SingleInstanceCoordinator();
        if (!instance.TryAcquire(TimeSpan.Zero))
        {
            // Nobody to ask in a headless run. Bow out; --control will say a copy without a control channel is running.
            if (headlessRun) return;
            // If we can't even ask the user (dialog failed to show), the safe answer is "don't
            // start a second copy" — bowing out is always safer than risking a duplicate.
            SingleInstanceDecision decision;
            try { decision = SingleInstanceDialog.Ask(); }
            catch { return; }

            switch (decision)
            {
                case SingleInstanceDecision.SwitchToRunning:
                    SingleInstanceCoordinator.SignalExistingToActivate();
                    return;
                case SingleInstanceDecision.Cancel:
                    return;
                case SingleInstanceDecision.ForceClose:
                    var cleared = SingleInstanceCoordinator.ForceCloseOtherInstances();
                    // Take the lock now the others should be gone. Allow a few seconds in case
                    // a killed copy is slow to release the abandoned mutex / its audio devices.
                    if (!instance.TryAcquire(TimeSpan.FromSeconds(5)))
                    {
                        ForegroundDialog.Show(owner => AppMessageBox.Show(
                            owner,
                            cleared
                                ? "RemSound closed the other copy but couldn't start cleanly. Please launch RemSound again."
                                : "RemSound couldn't close the copy that's already running — it may be running as administrator. Close it from Task Manager (or restart Windows), then try again.",
                            "RemSound is already running",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning));
                        return;
                    }
                    break;
            }
        }

        // We hold the single-instance lock — THIS copy has taken over (any stuck older copy was
        // force-closed just above). Play the one-shot startup cue here: after the take-over
        // decision is settled and before the profile picker/load, so if a copy was already
        // running you only hear it once the NEW process is in charge. The earlier "switch to the
        // running copy" / "cancel" paths returned before this point, so a copy that bowed out
        // never plays it. Fire-and-forget WaveOut, so it sounds even when we launch into the tray.
        PlayStartupCueIfEnabled();

        // We hold the single-instance lock. Listen for a later copy asking us to surface, and
        // route that request to whichever main window is open at the time.
        instance.StartActivationListener();
        instance.ActivateRequested += () => activeMainForm?.RestoreFromTray();
        // RemSound --close asks this copy to close the way File, Exit does before anything is forced. With no main
        // window yet (the profile picker is up) nothing happens here, and --close ends the process after its wait.
        instance.CloseRequested += () => activeMainForm?.CloseFromCommandLine();

        // The control channel, for a headless copy only, and only once it is THE copy (it holds the lock above). It
        // works on whatever windows are open, so the profile picker (no profile to start in) is driven the same way.
        RemoteControlServer? remoteControl = null;
        if (headlessRun)
        {
            var marshal = new Control();
            _ = marshal.Handle;
            // The log is the main window's, and the profile picker comes before it: hold what is said until then.
            var early = new List<string>();
            // The live main window once there is one; this helper before it (the profile picker). See the server.
            remoteControl = new RemoteControlServer(RemoteControl.DefaultPipeName,
                () => activeMainForm is { IsHandleCreated: true, IsDisposed: false } live ? live : marshal,
                new RemoteControlEngine(() => activeMainForm))
            {
                Log = line =>
                {
                    lock (early)
                    {
                        if (RemSoundLog.Current is not { } log) { early.Add(line); return; }
                        foreach (var held in early) log.Event(held + " (before the main window opened)");
                        early.Clear();
                        log.Event(line);
                    }
                },
            };
            remoteControl.Start();
        }
        using var remoteControlLifetime = remoteControl;

        // Hold the interactive-presence token for this copy's whole lifetime, so the send-only
        // lock-screen service (if installed) yields to us — it suspends its own sending while an
        // interactive RemSound is open. Windows releases the token automatically when this process
        // exits or crashes, so the service resumes on its own. Best-effort: null if it couldn't be
        // taken, in which case we simply run without announcing our presence.
        using var presenceHold = InteractivePresence.AcquireHold();

        // Best-effort: clear leftover update temp stages.
        // We hold the single-instance lock here, so only the live copy does this — no sibling race.
        RemSoundUpdater.CleanUpUpdateStages();
        // The scripts folder was renamed on 2026-09-24; an update brings the new one and would leave the old beside it.
        var retiredScripts = RetiredFiles.RemoveOldScriptsFolder(AppContext.BaseDirectory);
        // Cap the crash-report pile (keep the newest 10) — the *.log pruning never matched
        // crash-*.txt, so an unlucky install accumulated them forever. Unconditional, unlike the
        // opt-in log pruning: ten reports diagnose a pattern as well as a hundred.
        LogMaintenance.PruneCrashReports(AppConfig.LogsDirectory);

        // F1 anywhere = help for the control you're on; Shift+F1 = the whole manual (Ed, 2026-09-25). Installed *before*
        // the first ShowDialog so it works on the profile picker (the very first thing the user sees). The help window
        // lives in the shared library, so its sounds, its log lines and the browser come in from here.
        HelpLauncher.WireContextHelp();
        HelpLauncher.Install();

        // Audible typing feedback: a soft click on each keystroke in any edit field, plus a distinct
        // passkey sound on password fields. Machine-wide toggle (on by default). Installed app-wide
        // here - after the single-instance guard, before the profile picker - so it works on the
        // picker and every dialog. Best-effort: inert if the click sounds can't load.
        KeyClickService.Initialize(!headlessRun && AppConfig.Load().EnableKeyboardClicks);
        Application.ApplicationExit += (_, _) => KeyClickService.Shutdown();

        // Tick/untick sounds for checkbox toggles app-wide (CheckSoundService) and the tab-switch
        // cue (TabSwitchSoundService). Loaded here; reloaded by MainForm.ReloadAllCueSounds whenever
        // cue settings change in Preferences.
        CheckSoundService.Reload();
        TabSwitchSoundService.Reload();
        HelpSoundService.Reload();

        // Resolve the first profile, then run the window; the loop below re-opens it for a profile switch. An outer loop
        // for changing the profiles folder mid-session wrapped all of this until 2026-09-13 — nothing had set its flag
        // since the Manage Profiles dialog went.
        {
            var appConfig = AppConfig.Load();
            var store = appConfig.CreateStore();

            Profile? profile;
            string? title;
            // Auto-load shortcut: two paths.
            //
            //   (a) Resume-after-update sentinel — a one-shot file written by the updater
            //       just before it relaunches RemSound.exe (see RemSoundUpdater
            //       ResumeProfileSentinelName). Holds the title of whichever profile was
            //       loaded at the moment the update fired. If present, we load that profile
            //       silently and delete the sentinel — so a silent or mid-session update
            //       restores the same session the user was running, without dropping them
            //       at the picker. This takes precedence over StartWithProfileTitle because
            //       a mid-session update may have moved the user away from their configured
            //       startup profile.
            //
            //   (b) AppConfig.StartWithProfileTitle — the persistent "Start with a specific
            //       profile" preference set in Preferences. Loaded if (a)
            //       didn't fire. Combined with the Windows auto-start registry entry and the
            //       StartMinimised flag, it lets the user boot a machine and have RemSound
            //       up and streaming with no clicks.
            //
            // Either path falls through to the normal picker if the named profile no longer
            // exists (deleted since it was selected, or the profiles folder changed) so the
            // user isn't stuck.
            Profile? autoLoaded = null;
            string? autoLoadedTitle = null;

            // CLI overrides take precedence over the resume sentinel and the "start with profile"
            // setting. --profile loads a named profile; --connect with no --profile starts blank
            // (the requested peer is added to whatever profile loads, further below).
            if (cli.ProfileName is not null)
            {
                try { autoLoaded = store.Load(cli.ProfileName); if (autoLoaded is not null) autoLoadedTitle = cli.ProfileName; }
                catch { /* fall through to the normal resolution */ }
            }
            else if (cli.ForceBlankProfile)
            {
                autoLoaded = Profile.NewBlank();
                autoLoadedTitle = null;
            }

            var resumeSentinelPath = Path.Combine(AppContext.BaseDirectory, RemSoundUpdater.ResumeProfileSentinelName);
            string? resumeTitle = null;
            if (File.Exists(resumeSentinelPath))
            {
                try { resumeTitle = File.ReadAllText(resumeSentinelPath).Trim(); }
                catch { resumeTitle = null; }
                // Delete the sentinel unconditionally — it's a one-shot. If the load fails
                // below, the user gets the picker on this launch and a normal start next
                // time, rather than the sentinel re-firing on every relaunch forever.
                try { File.Delete(resumeSentinelPath); } catch { /* ignore */ }
            }

            if (autoLoaded is null && !string.IsNullOrWhiteSpace(resumeTitle))
            {
                try
                {
                    autoLoaded = store.Load(resumeTitle!);
                    if (autoLoaded is not null) autoLoadedTitle = resumeTitle;
                }
                catch { /* fall through to StartWithProfileTitle / picker */ }
            }

            if (autoLoaded is null && !string.IsNullOrWhiteSpace(appConfig.StartWithProfileTitle))
            {
                try
                {
                    autoLoaded = store.Load(appConfig.StartWithProfileTitle!);
                    if (autoLoaded is not null) autoLoadedTitle = appConfig.StartWithProfileTitle;
                }
                catch { /* fall back to picker */ }
            }

            if (autoLoaded is not null)
            {
                profile = autoLoaded;
                title = autoLoadedTitle;
            }
            else
            {
                using var dialog = new ProfileSelectionDialog(store);
                // In front: after an update RemSound is started again by the updater, not by the person, and Windows then
                // opened the picker behind whatever they were doing (2026-09-25 sweep).
                // A headless copy's picker stays out of sight (Windowless); the helper that brings a window to the front
                // would have left it centred on the screen.
                var picked = Windowless.Hiding ? dialog.ShowDialog() : ForegroundDialog.Show(_ => dialog.ShowDialog());
                if (picked != DialogResult.OK) return;
                // ProfileSelectionDialog can have changed the folder via its Browse button;
                // if so, it's already saved AppConfig and rebuilt its internal store. Pick up
                // its post-Browse store reference for the rest of the session.
                store = dialog.Store;
                profile = dialog.SelectedProfile;
                title = dialog.SelectedTitle;
            }

            // Apply --connect: add the requested peer address(es) to the loaded profile so MainForm
            // selects and connects to them on startup. Stored as plain address strings, the same
            // shape "Add peer by IP" produces (the audio port is the default unless one is given).
            if (cli.ConnectPeers.Count > 0 && profile is not null)
            {
                foreach (var ep in cli.ConnectPeers)
                {
                    var addr = ep.Port == RemPacket.DefaultPort ? ep.Address.ToString() : $"{ep.Address}:{ep.Port}";
                    if (!profile.RememberedPeers.Contains(addr)) profile.RememberedPeers.Add(addr);
                    if (!profile.SelectedConnectedPeers.Contains(addr)) profile.SelectedConnectedPeers.Add(addr);
                }
            }

            // Switch-profile loop: a profile switch (File, Open; Recent profiles; quick switch) or File, New closes the
            // form with the next profile named, and we re-open MainForm under it. Nothing named = the user closed the
            // window → exit.
            string? nextPath = null;
            while (true)
            {
                // Opening an ASIO driver is slow (1-3 s) and happens synchronously inside
                // MainForm construction. Show a "Loading audio driver" splash — on its own
                // thread, so it stays painted while this thread is busy — so startup doesn't
                // look hung. No-op for WASAPI-only profiles (construction is near-instant).
                // Skip the loading splash when this rebuild is a quick-profile-switch that's
                // staying in the tray — popping a splash up in front of the user's current app
                // defeats the point of keeping RemSound minimised, and the switch cue already
                // gave them feedback. Normal launches and visible switches still show it.
                var splash = MainForm.startNextInstanceMinimized ? null : AsioLoadingSplash.StartIfNeeded(profile);
                MainForm built;
                // Dismissed whatever happens: a window that failed to build left the splash on top of everything for good
                // (2026-09-25 sweep).
                try { built = new MainForm(store, profile, title, nextPath); }
                finally { splash?.Dismiss(); }
                using var form = built;
                // Expose the live window to the single-instance activation callback (a second
                // copy choosing "switch to the running copy" signals us to surface this form).
                activeMainForm = form;
                if (retiredScripts is not null) { RemSoundLog.Current?.Event("startup: " + retiredScripts); retiredScripts = null; }
                if (headlessRun)
                    RemSoundLog.Current?.Event($"headless: running with no window, sound or speech; driven through the control channel "
                        + @"\\.\pipe\" + RemoteControl.DefaultPipeName + " (RemSound --control help)");
                Application.Run(form);
                activeMainForm = null;

                // Every switch names the profile's file — a path that may be outside the active store's folder — so
                // the JSON is read directly from there.
                nextPath = form.NextProfilePathToLoad;
                var nextTitle = form.NextProfileTitleToLoad;
                if (form.LoadBlankTemplateNext)
                {
                    // File → New profile: rebuild on a fresh blank template, no saved profile.
                    profile = Profile.NewBlank();
                    title = null;
                    nextPath = null;
                }
                else if (!string.IsNullOrEmpty(nextPath))
                {
                    var wanted = !string.IsNullOrEmpty(nextTitle) ? nextTitle : Path.GetFileNameWithoutExtension(nextPath);
                    var read = ReadProfileForSwitch(nextPath, nextTitle);
                    (profile, title, nextPath) = (read.Profile, read.Title, read.Path);
                    if (read.Error is { } error)
                    {
                        // Carried into the next window's log (this loop has none of its own), and said to the person - a
                        // switch can start from the tray, so in front of everything.
                        MainForm.LineForNextLog = $"profile switch: could not read \"{wanted}\" ({error}) - opened a new, blank, untitled profile instead, so nothing is saved over that file";
                        if (!Windowless.NobodyToAsk)
                            ForegroundDialog.Show(owner => AppMessageBox.Show(owner,
                                $"RemSound could not read the profile \"{wanted}\", so it has opened a new, blank profile instead. "
                                + $"Your profile file has not been changed.\n\n{error}",
                                "RemSound", MessageBoxButtons.OK, MessageBoxIcon.Warning));
                    }
                }
                else
                {
                    return; // form closed normally — exit app
                }
            }
        }
    }

    /// <summary>If <c>--await-pid &lt;pid&gt;</c> is present (the post-install relaunch passes the old
    /// copy's id), block until that process exits so its single-instance lock is released before this
    /// copy tries to take it. Bounded to a few seconds so a stale/wrong pid can never hang startup.</summary>
    private static void WaitForAwaitedProcess(string[] args)
    {
        var idx = Array.FindIndex(args, a => string.Equals(a, "--await-pid", StringComparison.OrdinalIgnoreCase));
        if (idx < 0 || idx + 1 >= args.Length) return;
        if (!int.TryParse(args[idx + 1], out var pid)) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            p.WaitForExit(10000);
        }
        catch { /* already gone / invalid pid — nothing to wait for */ }
    }

    /// <summary>Play the startup cue once if the machine-wide setting is on. Resolves the WAV the
    /// same way the in-app cues do — a user-set custom path (machine-wide, in <see cref="AppConfig"/>)
    /// if it exists on disk, otherwise the bundled <c>default sounds\start up.wav</c>. Read straight from
    /// AppConfig because no profile (and therefore no settings store) is loaded yet at this point
    /// in startup. Best-effort: a cue must never stop RemSound from starting.</summary>
    private static void PlayStartupCueIfEnabled()
    {
        try
        {
            var cfg = AppConfig.Load();
            if (!cfg.EnableStartupCue) return;
            var custom = cfg.StartupCueCustomPath;
            // Custom override wins; otherwise the chosen default variant ("start up 1.wav" etc).
            var path = !string.IsNullOrWhiteSpace(custom) && File.Exists(custom)
                ? custom
                : CueSounds.ResolveDefaultPath(MainForm.CueId.Startup, "start up.wav", cfg);
            if (path is null || !File.Exists(path)) return;
            new CuePlayer(path).Play();
        }
        catch { /* a startup cue must never disturb startup */ }
    }
}
