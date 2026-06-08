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

    [STAThread]
    private static void Main()
    {
        // SustainedLowLatency tells the GC to avoid full (gen 2) collections while audio is streaming.
        // Gen 0/1 collections still happen but are sub-millisecond; the long pauses that were causing
        // the receiver to fall behind in clusters of 4-5 underruns at a time were almost certainly
        // gen 2 sweeps. This trades a bit of memory headroom (the GC will hold on to garbage longer)
        // for dramatically more predictable timing — exactly the trade real-time audio wants.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        ApplicationConfiguration.Initialize();

        // Relocate any pre-2026-06-07 config/profiles into config\ before anything reads them.
        // Idempotent and best-effort; also upgrades users coming from an older build. The result
        // is shown to the user once (after the single-instance guard) if files actually moved.
        var layoutMigration = RemSound.Core.AppConfig.MigrateLegacyLayoutIfNeeded();

        // Single-instance guard. RemSound must never run as two copies at once: with the
        // auto-updater relaunching the app, a copy that didn't exit cleanly used to leave two
        // (then more) copies running, each playing received audio — Andre's "stacked and
        // stacked", deafening-audio runaway (2026-05-30). The lock makes that structurally
        // impossible. Acquired BEFORE anything user-visible so a second copy bows out (or
        // takes over a stuck one) before it ever touches audio devices or the network.
        using var instance = new SingleInstanceCoordinator();
        if (!instance.TryAcquire(TimeSpan.Zero))
        {
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
                        MessageBox.Show(
                            cleared
                                ? "RemSound closed the other copy but couldn't start cleanly. Please launch RemSound again."
                                : "RemSound couldn't close the copy that's already running — it may be running as administrator. Close it from Task Manager (or restart Windows), then try again.",
                            "RemSound is already running",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    break;
            }
        }

        // We hold the single-instance lock. Listen for a later copy asking us to surface, and
        // route that request to whichever main window is open at the time.
        instance.StartActivationListener();
        instance.ActivateRequested += () => activeMainForm?.RestoreFromTray();

        // F1 anywhere = open the bundled manual. Installed *before* the first ShowDialog so
        // it works on the profile picker (the very first thing the user sees). The filter
        // is per-thread and modifier-aware: bare F1 only, so Shift/Ctrl/Alt+F1 stay free.
        HelpLauncher.Install();

        // One-time "your settings moved" notice — only the launch that actually relocated files
        // shows it (idempotent migration ⇒ MovedAnything is false on every later launch). Shown
        // here, after the guard and before the profile picker, so the user reads it once up front.
        if (layoutMigration.MovedAnything)
        {
            ShowLayoutMigrationNotice(layoutMigration);
        }

        // Outer loop: lets ProfileManagementDialog change the profiles folder mid-session.
        // When that happens, MainForm sets ReloadFromScratch=true, we re-read AppConfig, build
        // a fresh ProfileStore, and re-show ProfileSelectionDialog so the user picks a profile
        // (or blank template) from the *new* folder. Inner loop handles the cheaper "switch to
        // a profile in the same folder" case.
        while (true)
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
            //       profile" preference set via the Startup behaviour dialog. Loaded if (a)
            //       didn't fire. Combined with the Windows auto-start registry entry and the
            //       StartMinimised flag, it lets the user boot a machine and have RemSound
            //       up and streaming with no clicks.
            //
            // Either path falls through to the normal picker if the named profile no longer
            // exists (deleted since it was selected, or the profiles folder changed) so the
            // user isn't stuck.
            Profile? autoLoaded = null;
            string? autoLoadedTitle = null;

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

            if (!string.IsNullOrWhiteSpace(resumeTitle))
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
                if (dialog.ShowDialog() != DialogResult.OK) return;
                // ProfileSelectionDialog can have changed the folder via its Browse button;
                // if so, it's already saved AppConfig and rebuilt its internal store. Pick up
                // its post-Browse store reference for the rest of the session.
                store = dialog.Store;
                profile = dialog.SelectedProfile;
                title = dialog.SelectedTitle;
            }

            // Switch-profile loop: when the user clicks "Switch to profile" in the Manage
            // Profiles dialog, the form sets NextProfileTitleToLoad and closes; we re-open
            // MainForm under the newly chosen profile. Null = user closed the form normally
            // → exit. ReloadFromScratch = the user changed the profiles FOLDER mid-session,
            // so we break out of this inner loop and let the outer loop redo the selection
            // dialog under the new folder.
            var reloadFromScratch = false;
            string? nextPath = null;
            while (true)
            {
                // Opening an ASIO driver is slow (1-3 s) and happens synchronously inside
                // MainForm construction. Show a "Loading audio driver" splash — on its own
                // thread, so it stays painted while this thread is busy — so startup doesn't
                // look hung. No-op for WASAPI-only profiles (construction is near-instant).
                var splash = AsioLoadingSplash.StartIfNeeded(profile);
                using var form = new MainForm(store, profile, title, nextPath);
                // Expose the live window to the single-instance activation callback (a second
                // copy choosing "switch to the running copy" signals us to surface this form).
                activeMainForm = form;
                splash?.Dismiss();
                Application.Run(form);
                activeMainForm = null;

                if (form.ReloadFromScratch)
                {
                    reloadFromScratch = true;
                    break;
                }

                // Path-based reload (File → Open profile from a path that may be outside
                // the active store's BaseDirectory) takes precedence — read JSON directly
                // from that path. Falls back to title-based store.Load when no path is set
                // (e.g. legacy switch-by-title flows that pre-date the path tracking).
                nextPath = form.NextProfilePathToLoad;
                var nextTitle = form.NextProfileTitleToLoad;
                if (!string.IsNullOrEmpty(nextPath))
                {
                    try
                    {
                        var json = File.ReadAllText(nextPath);
                        profile = System.Text.Json.JsonSerializer.Deserialize<Profile>(json) ?? Profile.NewBlank();
                        title = !string.IsNullOrEmpty(nextTitle)
                            ? nextTitle
                            : Path.GetFileNameWithoutExtension(nextPath);
                    }
                    catch
                    {
                        // Malformed / unreadable JSON. Fall back to blank template under
                        // whatever title we have, rather than crashing the loop.
                        profile = Profile.NewBlank();
                        title = !string.IsNullOrEmpty(nextTitle)
                            ? nextTitle
                            : Path.GetFileNameWithoutExtension(nextPath);
                        nextPath = null;
                    }
                }
                else if (!string.IsNullOrEmpty(nextTitle))
                {
                    title = nextTitle;
                    profile = store.Load(nextTitle) ?? Profile.NewBlank();
                }
                else
                {
                    return; // form closed normally — exit app
                }
            }

            if (!reloadFromScratch) return;
        }
    }

    /// <summary>One-time, Windows-native notice telling the user their config/profiles were moved
    /// into the new <c>config\</c> folder. Only called when a real migration happened. TaskDialog
    /// (not a hand-rolled Form) so a screen reader reads the whole message automatically.</summary>
    private static void ShowLayoutMigrationNotice(RemSound.Core.AppConfig.LayoutMigrationResult migration)
    {
        var moved = new System.Collections.Generic.List<string>();
        if (migration.MovedGlobalConfig) moved.Add("- Your settings are now in: config\\global config.json");
        if (migration.MovedProfiles) moved.Add("- Your saved profiles are now in: config\\profiles\\");

        var page = new TaskDialogPage
        {
            Caption = "RemSound settings location",
            Heading = "Your settings now live in a \"config\" folder",
            Text = "To keep the RemSound folder tidy, this update moved your existing settings into a new "
                 + "\"config\" folder inside RemSound:\n\n"
                 + string.Join("\n", moved)
                 + "\n\nNothing was lost and RemSound works exactly as before. You will only see this message once.",
            Icon = TaskDialogIcon.Information,
            Buttons = { TaskDialogButton.OK },
            DefaultButton = TaskDialogButton.OK,
            AllowCancel = true,
        };
        try { TaskDialog.ShowDialog(page); }
        catch { /* a notice must never stop RemSound from starting */ }
    }
}
