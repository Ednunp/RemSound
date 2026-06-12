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
    private static void Main(string[] args)
    {
        // The auto-updater relaunches a temp copy of the NEW RemSound.exe in this mode to swap the
        // new files over the install while the old copy exits (see UpdateApplier / RemSoundUpdater).
        // Handle it first and return: this process is the installer, not a normal launch, so it must
        // not touch the single-instance lock, audio devices, or the migration steps below.
        if (args.Length > 0 && Array.Exists(args, a => string.Equals(a, "--apply-update", StringComparison.OrdinalIgnoreCase)))
        {
            UpdateApplier.Run(args);
            return;
        }

        // SustainedLowLatency tells the GC to avoid full (gen 2) collections while audio is streaming.
        // Gen 0/1 collections still happen but are sub-millisecond; the long pauses that were causing
        // the receiver to fall behind in clusters of 4-5 underruns at a time were almost certainly
        // gen 2 sweeps. This trades a bit of memory headroom (the GC will hold on to garbage longer)
        // for dramatically more predictable timing — exactly the trade real-time audio wants.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        ApplicationConfiguration.Initialize();

        // Consolidate every older layout (loose files, or the interim config\ folder) into the single
        // "user settings and logs" folder before anything reads config/profiles/logs. Idempotent +
        // best-effort; upgrades users from any older build. Shown to the user once if files moved.
        var layoutMigration = RemSound.Core.AppConfig.MigrateLegacyLayoutIfNeeded();
        // Move the cue sounds into that folder too — seeded from the shipped defaults (see method).
        ConsolidateSounds();

        // Remove cue WAVs (and their .sfk peak files) left loose in the install ROOT by pre-
        // 2026-05-28 builds, where the cues lived next to RemSound.exe before they moved into
        // sounds\. An update copies the new sounds\ tree but never purges, so it never deletes
        // these orphans — they just linger in the root. Best-effort + idempotent:
        // a no-op once they're gone. 2026-06-08.
        CleanUpLegacyRootSounds();

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
                        ForegroundDialog.Show(owner => MessageBox.Show(
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

        // Best-effort: clear leftover update temp stages (and any relics of the old batch updater).
        // We hold the single-instance lock here, so only the live copy does this — no sibling race.
        RemSoundUpdater.CleanUpUpdateStages();

        // F1 anywhere = open the bundled manual. Installed *before* the first ShowDialog so
        // it works on the profile picker (the very first thing the user sees). The filter
        // is per-thread and modifier-aware: bare F1 only, so Shift/Ctrl/Alt+F1 stay free.
        HelpLauncher.Install();

        // One-time "your settings moved" notice — only the launch that actually relocated files
        // shows it (idempotent migration ⇒ MovedAnything is false on every later launch). Shown
        // here, after the guard and before the profile picker, so the user reads it once up front.
        if (layoutMigration.MovedAnything)
        {
            ShowLayoutMigrationNotice();
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
                // Skip the loading splash when this rebuild is a quick-profile-switch that's
                // staying in the tray — popping a splash up in front of the user's current app
                // defeats the point of keeping RemSound minimised, and the switch cue already
                // gave them feedback. Normal launches and visible switches still show it.
                var splash = MainForm.startNextInstanceMinimized ? null : AsioLoadingSplash.StartIfNeeded(profile);
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
                if (form.LoadBlankTemplateNext)
                {
                    // File → New profile: rebuild on a fresh blank template, no saved profile.
                    profile = Profile.NewBlank();
                    title = null;
                    nextPath = null;
                }
                else if (!string.IsNullOrEmpty(nextPath))
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
    /// into the new "user settings and logs" folder. Only called when a real migration happened. TaskDialog
    /// (not a hand-rolled Form) so a screen reader reads the whole message automatically.</summary>
    private static void ShowLayoutMigrationNotice()
    {
        var page = new TaskDialogPage
        {
            Caption = "RemSound files location",
            Heading = "Your RemSound files have moved into one folder",
            Text = "To keep the RemSound folder tidy and stop updates from ever touching your own files, "
                 + "this update moved everything this machine owns into a single folder inside RemSound "
                 + "called \"user settings and logs\":\n\n"
                 + "- Your settings (global config)\n"
                 + "- Your saved profiles\n"
                 + "- Your logs\n"
                 + "- Your cue sounds\n\n"
                 + "Nothing was lost and RemSound works exactly as before. From now on, RemSound updates "
                 + "leave that folder completely untouched. You will only see this message once.",
            Icon = TaskDialogIcon.Information,
            Buttons = { TaskDialogButton.OK },
            DefaultButton = TaskDialogButton.OK,
            AllowCancel = true,
        };
        // Give the notice a momentary top-most, foreground owner so it opens FRONT and CENTRE —
        // RemSound may have launched straight into the tray (auto-start / start-minimised), and a
        // parent-less TaskDialog can otherwise open behind everything where a screen-reader user
        // can't read it.
        try { ForegroundDialog.Show(owner => TaskDialog.ShowDialog(owner, page)); }
        catch { /* a notice must never stop RemSound from starting */ }
    }

    /// <summary>Delete cue WAVs and their .sfk peak files left loose in the install ROOT by
    /// pre-2026-05-28 builds (the cues moved into <c>sounds\</c> then; an update copies the new
    /// tree but never removes the old root copies). Best-effort and idempotent — runs
    /// every launch and no-ops once the orphans are gone. Only the known default cue names are
    /// touched, never anything else in the folder.</summary>
    private static void CleanUpLegacyRootSounds()
    {
        try
        {
            var root = AppContext.BaseDirectory;
            string[] cueBaseNames =
            {
                "connect", "disconnect", "record start", "record stop",
                "save", "profile", "profile menu open", "update",
            };
            foreach (var baseName in cueBaseNames)
            {
                foreach (var fileName in new[] { baseName + ".wav", baseName + ".sfk", baseName + ".wav.sfk" })
                {
                    try
                    {
                        var path = Path.Combine(root, fileName);
                        if (File.Exists(path)) File.Delete(path);
                    }
                    catch { /* a locked / unremovable file must never stop startup */ }
                }
            }
        }
        catch { /* never let cleanup disturb startup */ }
    }

    /// <summary>Consolidate the cue WAVs into the per-user sounds folder. The release ships the
    /// default cues in <c>&lt;exe&gt;\sounds\</c>; this copies any cue MISSING from the per-user
    /// <c>...\user settings and logs\sounds\</c> across (so a fresh install, or a release that adds a
    /// new cue, gets seeded) WITHOUT overwriting one already there (so the user's own cue files
    /// survive), then removes the shipped folder to keep the install root tidy. The app reads cues
    /// only from the per-user folder, which the updater leaves untouched — so a user's custom cue
    /// WAVs are no longer clobbered by an update. Best-effort + idempotent. 2026-06-10.</summary>
    private static void ConsolidateSounds()
    {
        try
        {
            var userSounds = AppConfig.SoundsDirectory;
            Directory.CreateDirectory(userSounds);
            var shippedSounds = Path.Combine(AppContext.BaseDirectory, "sounds");
            if (!Directory.Exists(shippedSounds)) return;
            foreach (var src in Directory.GetFiles(shippedSounds))
            {
                try
                {
                    var dest = Path.Combine(userSounds, Path.GetFileName(src));
                    if (!File.Exists(dest)) File.Copy(src, dest);
                }
                catch { /* one unreadable cue mustn't stop the rest */ }
            }
            try { Directory.Delete(shippedSounds, recursive: true); }
            catch { /* leave it if locked — the app reads the per-user copy anyway */ }
        }
        catch { /* never let cue consolidation disturb startup */ }
    }

    /// <summary>Play the startup cue once if the machine-wide setting is on. Resolves the WAV the
    /// same way the in-app cues do — a user-set custom path (machine-wide, in <see cref="AppConfig"/>)
    /// if it exists on disk, otherwise the bundled <c>sounds\start up.wav</c>. Read straight from
    /// AppConfig because no profile (and therefore no settings store) is loaded yet at this point
    /// in startup. Best-effort: a cue must never stop RemSound from starting.</summary>
    private static void PlayStartupCueIfEnabled()
    {
        try
        {
            var cfg = AppConfig.Load();
            if (!cfg.EnableStartupCue) return;
            var custom = cfg.StartupCueCustomPath;
            var path = !string.IsNullOrWhiteSpace(custom) && File.Exists(custom)
                ? custom
                : Path.Combine(AppConfig.SoundsDirectory, "start up.wav");
            if (!File.Exists(path)) return;
            new CuePlayer(path).Play();
        }
        catch { /* a startup cue must never disturb startup */ }
    }
}
