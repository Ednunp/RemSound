using System.Net;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Preferences dialog. A four-tab dialog (2026-06-13 overhaul) using the same accessible
/// <see cref="QuietTabControl"/> as the main window:
///   * General — Browse for RemSound profiles folder, Accept remote volume commands, UPnP
///     automatic router port-forwarding, and Enable logs / Write logs now.
///   * Audio cues — the cue list (a plain list of cue names; arrowing previews each cue's
///     current sound), a "Choose sound" list whose "(none)" entry turns a cue off, the Play /
///     Browse actions, and the keyboard-clicks toggle.
///   * Startup behaviour — Start minimised, Start with Windows, Start with a specific profile
///     (+ the profile list). Moved here from the standalone Options-menu dialog.
///   * Update settings — startup-check toggle, frequency, manual check, silent-install, and
///     show-what's-new.
///
/// All settings save through <see cref="RemSoundSettingsStore"/> or <see cref="AppConfig"/>
/// on every change (no OK-to-commit). Esc or Close dismisses.
///
/// Reachable via the Options → Preferences menu item or Ctrl+P from the main window.
/// </summary>
internal sealed class PreferencesDialog : Form
{
    private readonly Button browseProfilesFolderButton = new()
    {
        Text = "&Browse for RemSound profiles folder...",
        AccessibleName = "Browse for RemSound profiles folder",
        AutoSize = true,
    };

    // Audio cue UI (2026-05-28 revised after Ed's feedback that one-control-per-cue blew
    // out the tab order). Back to a single CheckedListBox — up/down arrows move between
    // cues, Space toggles enable, exactly as it always was. Two buttons sit BELOW the list:
    // a Play button to preview, and a Browse button to pick a custom WAV. Both act on
    // whichever cue is currently selected in the list. Their labels update live as the
    // selection changes ("Play disconnect sound", "Browse for disconnect sound...") so
    // sighted and NVDA users alike know which cue they're about to act on. Tab order in
    // the cue section is just: list → Play → Browse (three tab stops, not eighteen).
    private readonly Label cueListLabel = new()
    {
        Text = "Audio cue sou&nds (Alt+N):",
        AccessibleName = "Audio cue sounds",
        AutoSize = true,
        Padding = new Padding(0, 6, 0, 4),
    };

    // A plain list of cue names (no tickboxes any more - on/off is the "(none)" entry in the sound
    // list below). Arrowing it previews the cue's currently-configured sound.
    private readonly ListBox cueList = new()
    {
        IntegralHeight = false,
        Height = 130,
        Width = 360,
        AccessibleName = "Audio cue sounds",
    };

    private readonly Button playSelectedCueButton = new()
    {
        AutoSize = true,
        Padding = new Padding(6, 2, 6, 2),
    };

    private readonly Button browseSelectedCueButton = new()
    {
        AutoSize = true,
        Padding = new Padding(6, 2, 6, 2),
    };

    // "Choose default sound" — a second listbox under the cue checklist. The cue WAVs ship as
    // numbered variants ("connect 1.wav", "connect 2.wav", ...); this lists the variants for the
    // cue currently selected in cueList. Arrowing it previews each variant AND makes it the chosen
    // default for that cue (machine-wide, AppConfig.DefaultCueSounds). The count isn't hard-coded —
    // whatever "<base> <n>.wav" files exist are offered, so adding more sounds later needs no code.
    private readonly Label defaultSoundLabel = new()
    {
        Text = "Choose default soun&d (Alt+D):",
        AccessibleName = "Choose default sound",
        AutoSize = true,
        Padding = new Padding(0, 6, 0, 4),
    };

    private readonly ListBox defaultSoundList = new()
    {
        IntegralHeight = false,
        Height = 76,
        Width = 360,
        AccessibleName = "Choose default sound",
    };

    // The variant filenames currently shown in defaultSoundList, index-aligned with its items, so a
    // selected index maps back to the WAV to persist + preview.
    private IReadOnlyList<string> currentVariants = Array.Empty<string>();
    // Set while we repopulate / programmatically select the list, so it doesn't fire a preview.
    private bool suppressDefaultSoundPreview;

    // Keyboard-click typing feedback toggle (machine-wide, on by default). Ed asked for it right
    // after the cue Browse button. Drives KeyClickService.Enabled live.
    private readonly AccessibleCheckBox keyboardClicksBox = new()
    {
        Text = "Play keyboard clicks when typing into any edit field (Alt+&K)",
        AccessibleName = "Play keyboard clicks when typing into any edit field",
        AutoSize = true,
    };

    /// <summary>Describes one cue row in the list. <see cref="DisplayName"/> is the listbox
    /// text; <see cref="CueId"/> is the well-known key from <see cref="MainForm.CueId"/>;
    /// <see cref="DefaultFileName"/> is the bundled WAV in <c>sounds\</c>. The Load/Save
    /// delegates close over the right backing store so the handlers don't need to know whether
    /// a row is per-profile (<see cref="RemSoundSettingsStore"/>) or machine-wide
    /// (<see cref="AppConfig"/> — the Startup cue, which fires before any profile loads).
    /// <see cref="IsProfileSetting"/> tells the handlers whether toggling the row should flag
    /// a pending profile save; machine-wide rows persist immediately and never do.</summary>
    private sealed record CueRowDescriptor(
        string DisplayName,
        string CueId,
        string DefaultFileName,
        bool IsProfileSetting,
        Func<bool> LoadEnabled,
        Action<bool> SaveEnabled,
        Func<string?> LoadCustomPath,
        Action<string?> SaveCustomPath);

    // Built per-dialog (not static) so the per-profile rows can close over the live `settings`
    // store while the Startup row closes over machine-wide AppConfig. Order = listbox order.
    private readonly CueRowDescriptor[] cueRows;

    private static CueRowDescriptor[] BuildCueRows(RemSoundSettingsStore settings)
    {
        CueRowDescriptor ProfileRow(string name, string id, string file,
            Func<RemSoundSettingsStore, bool> load, Action<RemSoundSettingsStore, bool> save) =>
            new(name, id, file, true,
                () => load(settings), v => save(settings, v),
                () => settings.LoadCustomCuePath(id), p => settings.SaveCustomCuePath(id, p));

        // Machine-wide cue (enable flag + custom path in AppConfig, not the profile). Persists
        // immediately and never flags a profile save (IsProfileSetting=false). Same shape as the
        // Startup row below, factored out for the send/receive/hide/show cues.
        CueRowDescriptor MachineRow(string name, string id, string file,
            Func<AppConfig, bool> loadEnabled, Action<AppConfig, bool> saveEnabled) =>
            new(name, id, file, false,
                () => loadEnabled(AppConfig.Load()),
                v => { var c = AppConfig.Load(); saveEnabled(c, v); TrySaveConfig(c); },
                () => AppConfig.Load().MachineCueCustomPaths.TryGetValue(id, out var p) ? p : null,
                p => { var c = AppConfig.Load(); if (string.IsNullOrWhiteSpace(p)) c.MachineCueCustomPaths.Remove(id); else c.MachineCueCustomPaths[id] = p!; TrySaveConfig(c); });

        return
        [
            ProfileRow("Connect sound", MainForm.CueId.Connect, "connect.wav",
                s => s.LoadEnableConnectCue(), (s, v) => s.SaveEnableConnectCue(v)),
            ProfileRow("Disconnect sound", MainForm.CueId.Disconnect, "disconnect.wav",
                s => s.LoadEnableDisconnectCue(), (s, v) => s.SaveEnableDisconnectCue(v)),
            ProfileRow("Recording start sound", MainForm.CueId.RecordStart, "record start.wav",
                s => s.LoadEnableRecordStartCue(), (s, v) => s.SaveEnableRecordStartCue(v)),
            ProfileRow("Recording stop sound", MainForm.CueId.RecordStop, "record stop.wav",
                s => s.LoadEnableRecordStopCue(), (s, v) => s.SaveEnableRecordStopCue(v)),
            ProfileRow("Profile saved sound", MainForm.CueId.Save, "save.wav",
                s => s.LoadEnableSaveCue(), (s, v) => s.SaveEnableSaveCue(v)),
            ProfileRow("Profile switched sound", MainForm.CueId.ProfileSwitch, "profile.wav",
                s => s.LoadEnableProfileSwitchCue(), (s, v) => s.SaveEnableProfileSwitchCue(v)),
            ProfileRow("Profile menu open sound", MainForm.CueId.ProfileMenuOpen, "profile menu open.wav",
                s => s.LoadEnableProfileMenuOpenCue(), (s, v) => s.SaveEnableProfileMenuOpenCue(v)),
            ProfileRow("Update sound", MainForm.CueId.Update, "update.wav",
                s => s.LoadEnableUpdateCue(), (s, v) => s.SaveEnableUpdateCue(v)),
            // Startup cue — machine-wide (AppConfig), because it plays before a profile is loaded.
            // Persists immediately on change and never flags a profile save (IsProfileSetting=false).
            new("Startup sound", MainForm.CueId.Startup, "start up.wav", false,
                () => AppConfig.Load().EnableStartupCue,
                v => { var c = AppConfig.Load(); c.EnableStartupCue = v; TrySaveConfig(c); },
                () => AppConfig.Load().StartupCueCustomPath,
                p => { var c = AppConfig.Load(); c.StartupCueCustomPath = p; TrySaveConfig(c); }),
            // Send/receive toggle + minimise(hide)/restore(show) cues (machine-wide). The Receive
            // file bases use the spelling of the shipped files ("recieve ...") so variant discovery
            // matches; the display names use the correct spelling.
            MachineRow("Send turned on sound", MainForm.CueId.SendOn, "send on.wav",
                c => c.EnableSendOnCue, (c, v) => c.EnableSendOnCue = v),
            MachineRow("Send turned off sound", MainForm.CueId.SendOff, "send off.wav",
                c => c.EnableSendOffCue, (c, v) => c.EnableSendOffCue = v),
            MachineRow("Receive turned on sound", MainForm.CueId.ReceiveOn, "recieve on.wav",
                c => c.EnableReceiveOnCue, (c, v) => c.EnableReceiveOnCue = v),
            MachineRow("Receive turned off sound", MainForm.CueId.ReceiveOff, "recieve off.wav",
                c => c.EnableReceiveOffCue, (c, v) => c.EnableReceiveOffCue = v),
            MachineRow("Minimise (hide) sound", MainForm.CueId.Hide, "minimise.wav",
                c => c.EnableHideCue, (c, v) => c.EnableHideCue = v),
            MachineRow("Restore (show) sound", MainForm.CueId.Show, "maximise.wav",
                c => c.EnableShowCue, (c, v) => c.EnableShowCue = v),
            // Played on every checkbox tick / untick across the whole app (CheckSoundService).
            MachineRow("Checkbox ticked sound", MainForm.CueId.CheckboxOn, "check.wav",
                c => c.EnableCheckboxOnCue, (c, v) => c.EnableCheckboxOnCue = v),
            MachineRow("Checkbox unticked sound", MainForm.CueId.CheckboxOff, "uncheck.wav",
                c => c.EnableCheckboxOffCue, (c, v) => c.EnableCheckboxOffCue = v),
            // Played whenever the user switches tabs anywhere in the app (TabSwitchSoundService).
            // Display name is Ed's "switch tabs"; the shipped files are "tab switch 1.wav" etc, so
            // the base filename here is "tab switch.wav" for variant discovery to match.
            MachineRow("Switch tabs sound", MainForm.CueId.TabSwitch, "tab switch.wav",
                c => c.EnableTabSwitchCue, (c, v) => c.EnableTabSwitchCue = v),
        ];
    }

    private static void TrySaveConfig(AppConfig cfg)
    {
        try { cfg.Save(); } catch { /* harmless — the choice just won't survive a restart */ }
    }

    private readonly AccessibleCheckBox acceptRemoteVolumeBox = new()
    {
        Text = "Accept remote volume commands from peers (Alt+&A)",
        AccessibleName = "Accept remote volume commands from peers",
        AutoSize = true,
    };

    // Update settings — startup-check checkbox, frequency dropdown, manual check button,
    // silent-install checkbox. Sits above the logging row so users meet it during setup; the
    // canonical order in the dialog is "things related to the program staying current" before
    // "things related to diagnosing how it's running".
    private readonly AccessibleCheckBox checkForUpdatesOnStartupBox = new()
    {
        Text = "Check for updates on &startup",
        AccessibleName = "Check for updates on startup",
        AutoSize = true,
    };

    private readonly Label updateFrequencyLabel = new()
    {
        // "Then check every" — reads as a continuation of the startup-check checkbox above,
        // so the user understands the dropdown controls the *background* poll cadence, not
        // the launch behaviour.
        Text = "Then check every (Alt+&U):",
        AccessibleName = "Then check every",
        AutoSize = true,
    };

    private readonly ComboBox updateFrequencyBox = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 200,
        AccessibleName = "Then check every (Alt+U)",
    };

    private readonly Button checkForUpdatesNowButton = new()
    {
        Text = "Check for updates &now",
        AccessibleName = "Check for updates now",
        AutoSize = true,
    };

    private readonly AccessibleCheckBox silentlyInstallUpdatesBox = new()
    {
        Text = "Silently &install updates when available",
        AccessibleName = "Silently install updates when available",
        AutoSize = true,
    };

    // After an update installs and RemSound restarts, opening the About box once lets the user
    // see what changed. Off by default (opt-in). 'h' mnemonic — 'w' is taken by "Write logs now".
    private readonly AccessibleCheckBox showWhatsNewAfterUpdateBox = new()
    {
        Text = "S&how what's new after each update",
        AccessibleName = "Show what's new after each update",
        AutoSize = true,
    };

    // UPnP — automatic router port-forwarding via Mono.Nat. Off by default. The status label
    // is updated live from the RouterPortMapper.StatusChanged event so the user sees the
    // discovery result inline without having to close and reopen the dialog.
    private readonly AccessibleCheckBox upnpEnabledBox = new()
    {
        Text = "Automatically open my router for incoming connections (UPnP) (Alt+&O)",
        AccessibleName = "Automatically open my router for incoming connections via UPnP",
        AutoSize = true,
    };

    private readonly Label upnpStatusLabel = new()
    {
        Text = "",
        AccessibleName = "UPnP status",
        AutoSize = true,
        Padding = new Padding(20, 0, 0, 4),
    };

    private readonly AccessibleCheckBox loggingBox = new()
    {
        Text = "Enable &logs",
        AccessibleName = "Enable logs",
        AutoSize = true,
    };

    private readonly Button writeLogsNowButton = new()
    {
        Text = "&Write logs now",
        AccessibleName = "Write logs now",
        AutoSize = true,
    };

    // Startup behaviour (moved here from the Options-menu StartupBehaviourDialog, 2026-06-13). These
    // live on their own tab; their Alt-letters are isolated per tab so reusing M/A/P/L is fine.
    private readonly AccessibleCheckBox startMinimisedBox = new()
    {
        Text = "Start minimised to tray (Alt+&M)",
        AccessibleName = "Start minimised to tray",
        AutoSize = true,
    };
    private readonly AccessibleCheckBox startWithUserBox = new()
    {
        Text = "Start RemSound automatically when this user logs in (Alt+&A)",
        AccessibleName = "Start RemSound automatically when this user logs in",
        AutoSize = true,
    };
    private readonly AccessibleCheckBox startWithProfileBox = new()
    {
        Text = "Start with a specific profile (Alt+&P)",
        AccessibleName = "Start with a specific profile",
        AutoSize = true,
    };
    private readonly Label startupProfileListLabel = new()
    {
        Text = "Profile to start with (Alt+&L):",
        AutoSize = true,
        AccessibleName = "Profile to start with",
    };
    private readonly ListBox startupProfileList = new()
    {
        IntegralHeight = false,
        Width = 360,
        Height = 120,
        AccessibleName = "Profile to start with",
    };
    private bool suppressStartWithUserHandler;

    private readonly Button closeButton = new()
    {
        Text = "Close",
        AutoSize = true,
        DialogResult = DialogResult.OK,
    };

    // The four-tab strip. Held as a field (not a constructor local) so OnShown can land focus
    // on it when the dialog opens — see the OnShown override for why that's needed for NVDA.
    private readonly QuietTabControl tabs = new() { Dock = DockStyle.Fill, TabIndex = 0, TabStop = true };

    /// <summary>True if the user toggled Mute cues or Accept remote during this dialog
    /// session. The owner uses this to know whether to MarkProfileDirty after the dialog
    /// closes (since both settings live on Profile and need to flag a save-pending state).</summary>
    public bool ChangedAnyProfileSetting { get; private set; }

    private readonly Func<(RouterMappingStatus Status, IPEndPoint? External, string LastError)> getUpnpSnapshot;
    private EventHandler? upnpStatusSubscription;

    public PreferencesDialog(
        RemSoundSettingsStore settings,
        ProfileStore? profileStore,
        Func<bool> getLoggingEnabled,
        Action<bool> applyLoggingEnabled,
        Action writeLogsNow,
        Action checkForUpdatesNow,
        Action onUpdateFrequencyChanged,
        Action<bool> applyUpnpEnabled,
        Func<(RouterMappingStatus Status, IPEndPoint? External, string LastError)> getUpnpSnapshot,
        Action<EventHandler> subscribeUpnpStatusChanged,
        Action<EventHandler> unsubscribeUpnpStatusChanged)
    {
        this.getUpnpSnapshot = getUpnpSnapshot;
        cueRows = BuildCueRows(settings);

        Text = "Preferences";
        // Explicitly a dialog so the spoken title is clean and screen readers treat it as a dialog
        // (ShowDialog already exposes UIA IsDialog on .NET 7+; this is harmless reinforcement).
        AccessibleRole = AccessibleRole.Dialog;
        AccessibleName = "Preferences";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;
        ClientSize = new Size(580, 640);

        // 1st row — Browse for profiles folder. Same FolderBrowserDialog the startup
        // ProfileSelectionDialog uses; the choice is persisted to AppConfig.ProfilesDirectory
        // and applied on next launch (mid-session reload would force a re-pick of profile
        // which is more disruption than the change is worth — users restart RemSound when
        // they want to switch folders).
        browseProfilesFolderButton.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog
            {
                Description = "Choose a folder for RemSound profiles",
                UseDescriptionForTitle = true,
                SelectedPath = profileStore?.BaseDirectory ?? AppContext.BaseDirectory,
                ShowNewFolderButton = true,
            };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            if (string.IsNullOrWhiteSpace(picker.SelectedPath)) return;
            var cfg = AppConfig.Load();
            cfg.ProfilesDirectory = picker.SelectedPath;
            try
            {
                cfg.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not save app config: {ex.Message}",
                    "RemSound", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            MessageBox.Show(this,
                $"Profiles folder updated to:\n\n{picker.SelectedPath}\n\nThe new folder will be used next time RemSound launches.",
                "Profiles folder updated", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };

        // Populate the cue listbox — order matches the cueRows array, and the index of a
        // selected row maps 1:1 to a CueRowDescriptor. Each row's ticked state is loaded via
        // the descriptor (per-profile cues from the settings store; the Startup cue from AppConfig).
        cueList.Items.Clear();
        foreach (var c in cueRows)
        {
            cueList.Items.Add(c.DisplayName);
        }
        if (cueList.Items.Count > 0) cueList.SelectedIndex = 0;

        // Selection change: refresh the action-button labels and the sound list, and preview the
        // cue's current sound so arrowing the list lets the user hear each cue. (Enable/disable is
        // no longer a tickbox here - it's the "(none)" entry in the sound list.)
        cueList.SelectedIndexChanged += (_, _) =>
        {
            RefreshCueActionButtons();
            RefreshDefaultSoundList();
            PreviewSelectedCueCurrentSound();
        };
        RefreshCueActionButtons();

        playSelectedCueButton.Click += (_, _) =>
        {
            if (cueList.SelectedIndex < 0 || cueList.SelectedIndex >= cueRows.Length) return;
            OnPlayClicked(cueRows[cueList.SelectedIndex]);
        };
        browseSelectedCueButton.Click += (_, _) =>
        {
            if (cueList.SelectedIndex < 0 || cueList.SelectedIndex >= cueRows.Length) return;
            OnBrowseClicked(browseSelectedCueButton, cueRows[cueList.SelectedIndex]);
            RefreshCueActionButtons();
        };

        // Right-click "Use default sound" context menu lives on the Browse button. It acts
        // on whichever cue is currently selected — same as a left click. Disabled when no
        // override is set so it can't accidentally do nothing.
        var browseCtx = new ContextMenuStrip();
        var useDefaultItem = new ToolStripMenuItem("Use default sound");
        useDefaultItem.Click += (_, _) =>
        {
            if (cueList.SelectedIndex < 0 || cueList.SelectedIndex >= cueRows.Length) return;
            var cue = cueRows[cueList.SelectedIndex];
            if (cue.LoadCustomPath() is not null)
            {
                cue.SaveCustomPath(null);
                if (cue.IsProfileSetting) ChangedAnyProfileSetting = true;
                RefreshCueActionButtons();
            }
        };
        browseCtx.Opening += (_, _) =>
        {
            if (cueList.SelectedIndex < 0 || cueList.SelectedIndex >= cueRows.Length)
            {
                useDefaultItem.Enabled = false;
                useDefaultItem.Text = "Use default sound";
            }
            else
            {
                var cue = cueRows[cueList.SelectedIndex];
                useDefaultItem.Enabled = cue.LoadCustomPath() is not null;
                useDefaultItem.Text = $"Use default {cue.DisplayName.ToLowerInvariant()}";
                useDefaultItem.AccessibleName = useDefaultItem.Text;
            }
        };
        browseCtx.Items.Add(useDefaultItem);
        browseSelectedCueButton.ContextMenuStrip = browseCtx;

        cueListLabel.Click += (_, _) => cueList.Focus();

        // "Choose default sound" listbox — populated for the selected cue, arrowing it previews +
        // chooses the default variant. Initial fill for the cue selected at construction.
        defaultSoundLabel.Click += (_, _) => defaultSoundList.Focus();
        defaultSoundList.SelectedIndexChanged += (_, _) => OnDefaultSoundChosen();
        RefreshDefaultSoundList();

        // Keyboard-click typing feedback toggle (machine-wide; drives KeyClickService live).
        keyboardClicksBox.Checked = AppConfig.Load().EnableKeyboardClicks;
        keyboardClicksBox.CheckedChanged += (_, _) =>
        {
            var c = AppConfig.Load();
            c.EnableKeyboardClicks = keyboardClicksBox.Checked;
            TrySaveConfig(c);
            KeyClickService.Enabled = keyboardClicksBox.Checked;
        };

        acceptRemoteVolumeBox.Checked = settings.LoadAcceptRemoteVolumeCommands();
        acceptRemoteVolumeBox.CheckedChanged += (_, _) =>
        {
            settings.SaveAcceptRemoteVolumeCommands(acceptRemoteVolumeBox.Checked);
            ChangedAnyProfileSetting = true;
        };

        // Update settings — wired against AppConfig directly since they're machine-local.
        // The frequency combo's index maps 1:1 to the UpdateCheckFrequency enum so reordering
        // either side stays in lockstep.
        updateFrequencyBox.Items.AddRange(new object[] { "Never", "Every hour", "Every 6 hours", "Every 24 hours" });
        var cfgForLoad = AppConfig.Load();
        checkForUpdatesOnStartupBox.Checked = cfgForLoad.CheckForUpdatesOnStartup;
        updateFrequencyBox.SelectedIndex = (int)cfgForLoad.UpdateCheckFrequency;
        silentlyInstallUpdatesBox.Checked = cfgForLoad.SilentlyInstallUpdates;
        upnpEnabledBox.Checked = cfgForLoad.UpnpEnabled;

        checkForUpdatesOnStartupBox.CheckedChanged += (_, _) =>
        {
            var cfg = AppConfig.Load();
            cfg.CheckForUpdatesOnStartup = checkForUpdatesOnStartupBox.Checked;
            try { cfg.Save(); } catch { /* harmless — choice just won't survive a restart */ }
        };
        updateFrequencyBox.SelectedIndexChanged += (_, _) =>
        {
            var cfg = AppConfig.Load();
            cfg.UpdateCheckFrequency = (UpdateCheckFrequency)updateFrequencyBox.SelectedIndex;
            try { cfg.Save(); } catch { /* harmless — choice just won't survive a restart */ }
            onUpdateFrequencyChanged();
        };
        silentlyInstallUpdatesBox.CheckedChanged += (_, _) =>
        {
            var cfg = AppConfig.Load();
            cfg.SilentlyInstallUpdates = silentlyInstallUpdatesBox.Checked;
            try { cfg.Save(); } catch { /* harmless */ }
        };
        showWhatsNewAfterUpdateBox.Checked = cfgForLoad.ShowWhatsNewAfterUpdate;
        showWhatsNewAfterUpdateBox.CheckedChanged += (_, _) =>
        {
            var cfg = AppConfig.Load();
            cfg.ShowWhatsNewAfterUpdate = showWhatsNewAfterUpdateBox.Checked;
            try { cfg.Save(); } catch { /* harmless */ }
        };
        checkForUpdatesNowButton.Click += (_, _) => checkForUpdatesNow();

        // UPnP toggle — persists immediately and tells MainForm to start / stop the mapper.
        // Status label refresh wires up below.
        upnpEnabledBox.CheckedChanged += (_, _) =>
        {
            var cfg = AppConfig.Load();
            cfg.UpnpEnabled = upnpEnabledBox.Checked;
            try { cfg.Save(); } catch { /* harmless */ }
            applyUpnpEnabled(upnpEnabledBox.Checked);
            RefreshUpnpStatusLabel();
        };

        // Live UPnP status — the RouterPortMapper raises StatusChanged from a thread-pool
        // thread, so marshal back onto the UI thread before touching the label. Subscribe
        // on show and unsubscribe on close to avoid leaking the handler past the dialog.
        upnpStatusSubscription = (_, _) =>
        {
            if (IsDisposed) return;
            try { BeginInvoke(new Action(RefreshUpnpStatusLabel)); }
            catch (ObjectDisposedException) { /* dialog already gone — ignore */ }
            catch (InvalidOperationException) { /* handle not created — ignore */ }
        };
        subscribeUpnpStatusChanged(upnpStatusSubscription);
        FormClosed += (_, _) =>
        {
            if (upnpStatusSubscription is not null)
            {
                try { unsubscribeUpnpStatusChanged(upnpStatusSubscription); }
                catch { /* shutdown — ignore */ }
                upnpStatusSubscription = null;
            }
        };
        RefreshUpnpStatusLabel();

        loggingBox.Checked = getLoggingEnabled();
        loggingBox.CheckedChanged += (_, _) =>
        {
            // Machine-local setting (AppConfig.LoggingEnabled). applyLoggingEnabled writes
            // through immediately and flips the live gate, so closing the dialog needs no
            // further action. NOT a profile setting — do NOT touch ChangedAnyProfileSetting
            // or we'll trigger a spurious "save profile?" prompt on exit when the user
            // toggled nothing else.
            applyLoggingEnabled(loggingBox.Checked);
        };

        writeLogsNowButton.Click += (_, _) => writeLogsNow();

        closeButton.Click += (_, _) => Close();

        // Wire up the Startup behaviour tab (moved here from the old Options-menu dialog).
        WireStartupBehaviour(profileStore);

        // Group the frequency label + combo on one FlowLayoutPanel row so the visible label
        // sits inline next to the combo while keeping the combo as the focusable target.
        var freqRow = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 0),
        };
        updateFrequencyLabel.Padding = new Padding(0, 6, 8, 0);
        freqRow.Controls.Add(updateFrequencyLabel);
        freqRow.Controls.Add(updateFrequencyBox);

        // Group the cue label + list + the two action buttons into a single panel that
        // occupies one row in the outer layout. The action buttons sit side-by-side under
        // the list so they read as "buttons that act on the list above" without taking up
        // a second row of vertical space.
        var cueGroup = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 6,
        };
        cueGroup.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 6; i++) cueGroup.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var cueActions = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 0),
            TabIndex = 4,
        };
        cueActions.Controls.Add(playSelectedCueButton);
        cueActions.Controls.Add(browseSelectedCueButton);
        // Tab order within the cue group: cue checklist -> default-sound list -> Play/Browse ->
        // keyboard-clicks checkbox. Labels are skipped (not tab stops).
        defaultSoundList.TabIndex = 2;
        keyboardClicksBox.TabIndex = 5;
        cueGroup.Controls.Add(cueListLabel, 0, 0);
        cueGroup.Controls.Add(cueList, 0, 1);
        cueGroup.Controls.Add(defaultSoundLabel, 0, 2);
        cueGroup.Controls.Add(defaultSoundList, 0, 3);
        cueGroup.Controls.Add(cueActions, 0, 4);
        cueGroup.Controls.Add(keyboardClicksBox, 0, 5);

        // Startup profile list (label + list), shown only when "start with a specific profile" is on.
        var startupListPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(20, 0, 0, 0),
        };
        startupListPanel.Controls.Add(startupProfileListLabel);
        startupListPanel.Controls.Add(startupProfileList);

        // Four tabs, accessible (QuietTabControl) like the main window. Ctrl+Tab / arrows on the
        // strip switch tabs; the active page's controls are the next tab stops. The control itself
        // is a field (declared above) so OnShown can focus it when the dialog opens.
        tabs.TabPages.Add(MakeTab("General",
            browseProfilesFolderButton, acceptRemoteVolumeBox, upnpEnabledBox, upnpStatusLabel, loggingBox, writeLogsNowButton));
        tabs.TabPages.Add(MakeTab("Audio cues", cueGroup));
        tabs.TabPages.Add(MakeTab("Startup behaviour",
            startMinimisedBox, startWithUserBox, startWithProfileBox, startupListPanel));
        tabs.TabPages.Add(MakeTab("Update settings",
            checkForUpdatesOnStartupBox, freqRow, checkForUpdatesNowButton, silentlyInstallUpdatesBox, showWhatsNewAfterUpdateBox));

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 0, 12, 12),
        };
        buttons.Controls.Add(closeButton);

        Controls.Add(tabs);
        Controls.Add(buttons);

        // Stack the given controls vertically in a tab body (auto-size rows + a spacer), mirroring
        // the original single-panel layout so Dock=Fill children like the cue group still fit.
        static TabPage MakeTab(string title, params Control[] controls)
        {
            var page = new TabPage(title);
            var body = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                ColumnCount = 1,
                RowCount = controls.Length + 1,
                AutoScroll = true,
            };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var i = 0; i < controls.Length; i++) body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            for (var i = 0; i < controls.Length; i++) body.Controls.Add(controls[i], 0, i);
            page.Controls.Add(body);
            return page;
        }

        AcceptButton = closeButton;
        CancelButton = closeButton;

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                Close();
                e.SuppressKeyPress = true;
                e.Handled = true;
            }
        };
    }

    /// <summary>When the dialog opens, land focus on the first real, NAMED leaf control inside the
    /// active tab page so NVDA announces the dialog and that control. Never the tab strip: the tab
    /// control is a <see cref="QuietTabControl"/> whose own accessible object is deliberately
    /// role-less and nameless (so NVDA reads the tab item, not a redundant "tab control"), and
    /// focusing THAT on open gave NVDA nothing to announce — which is exactly why Preferences opened
    /// silent until you moved. Focusing a named leaf (the first General-tab control) gives NVDA
    /// something to speak, and because ShowDialog exposes the form as a dialog (UIA IsDialog, .NET 7+)
    /// it then reads the whole dialog.
    ///
    /// Three load-bearing details, confirmed against the dotnet/winforms + NVDA issue trackers and
    /// matching the pattern Andre's Sensor Readout uses (it focuses a real list/textbox in Shown):
    ///   * Deferred via BeginInvoke so it runs after the dialog's accessibility tree is live.
    ///   * ActiveControl=null FIRST, so leaf.Focus() is a genuine focus CHANGE and actually raises the
    ///     focus event (without the transition WinForms can treat focus as unchanged and stay silent).
    ///   * NotifyFocus re-fires the MSAA focus event as belt-and-braces.
    /// Ctrl+Tab still switches tabs from inside the page.</summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        BeginInvoke(new Action(() =>
        {
            if (IsDisposed) return;
            if (tabs.TabCount > 0) tabs.SelectedIndex = 0;
            WinEventNotifier.AnnounceByFocusingLeaf(this, tabs.SelectedTab, tabs);
        }));
    }

    /// <summary>Wire up the Startup behaviour tab (moved here from StartupBehaviourDialog): load the
    /// current state, populate the profile list, and persist each change immediately to AppConfig /
    /// the Windows auto-start registry entry, exactly as the old dialog did.</summary>
    private void WireStartupBehaviour(ProfileStore? store)
    {
        var cfg = AppConfig.Load();
        startMinimisedBox.Checked = cfg.StartMinimised;
        startWithUserBox.Checked = StartupAutoStart.IsEnabled;
        var hasProfile = !string.IsNullOrWhiteSpace(cfg.StartWithProfileTitle);
        startWithProfileBox.Checked = hasProfile;

        if (store is not null)
        {
            foreach (var title in store.ListProfileTitles()) startupProfileList.Items.Add(title);
        }
        if (hasProfile && cfg.StartWithProfileTitle is { } savedTitle)
        {
            var idx = startupProfileList.Items.IndexOf(savedTitle);
            if (idx >= 0) startupProfileList.SelectedIndex = idx;
        }
        UpdateStartupProfileListVisibility();

        startMinimisedBox.CheckedChanged += (_, _) =>
        {
            var c = AppConfig.Load();
            c.StartMinimised = startMinimisedBox.Checked;
            try { c.Save(); } catch (Exception ex) { ShowStartupWarning("Could not save Start minimised preference: " + ex.Message); }
        };

        startWithUserBox.CheckedChanged += (_, _) =>
        {
            if (suppressStartWithUserHandler) return;
            // Source of truth for auto-start is the registry, not AppConfig — flip it directly.
            var ok = startWithUserBox.Checked ? StartupAutoStart.TryEnable() : StartupAutoStart.TryDisable();
            if (!ok)
            {
                MessageBox.Show(this,
                    "RemSound could not change the auto-start setting in the Windows registry. The setting did not change. (This usually means a policy or another security tool is blocking it.)",
                    "Auto-start change failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                var actual = StartupAutoStart.IsEnabled;
                if (startWithUserBox.Checked != actual)
                {
                    suppressStartWithUserHandler = true;
                    try { startWithUserBox.Checked = actual; }
                    finally { suppressStartWithUserHandler = false; }
                }
            }
        };

        startWithProfileBox.CheckedChanged += (_, _) =>
        {
            UpdateStartupProfileListVisibility();
            if (startWithProfileBox.Checked)
            {
                if (startupProfileList.Items.Count == 0)
                {
                    MessageBox.Show(this,
                        "You don't have any saved profiles yet. Save a profile first (File menu -> Save profile as), then come back here and pick it.",
                        "No saved profiles", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    startWithProfileBox.Checked = false;
                    return;
                }
                if (startupProfileList.SelectedIndex < 0) startupProfileList.SelectedIndex = 0;
                CommitStartupProfileSelection();
            }
            else
            {
                ClearStartupProfileSelection();
            }
        };

        startupProfileList.SelectedIndexChanged += (_, _) =>
        {
            if (!startWithProfileBox.Checked || startupProfileList.SelectedIndex < 0) return;
            CommitStartupProfileSelection();
        };
        startupProfileListLabel.Click += (_, _) => startupProfileList.Focus();
    }

    private void UpdateStartupProfileListVisibility()
    {
        var visible = startWithProfileBox.Checked;
        startupProfileListLabel.Visible = visible;
        startupProfileList.Visible = visible;
    }

    private void CommitStartupProfileSelection()
    {
        if (startupProfileList.SelectedItem is not string title || string.IsNullOrWhiteSpace(title)) return;
        var c = AppConfig.Load();
        c.StartWithProfileTitle = title;
        try { c.Save(); } catch (Exception ex) { ShowStartupWarning("Could not save the start-with-profile choice: " + ex.Message); }
    }

    private void ClearStartupProfileSelection()
    {
        var c = AppConfig.Load();
        c.StartWithProfileTitle = null;
        try { c.Save(); } catch (Exception ex) { ShowStartupWarning("Could not save the start-with-profile choice: " + ex.Message); }
    }

    private void ShowStartupWarning(string message) =>
        MessageBox.Show(this, message, "Startup behaviour", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    /// <summary>Refresh the Play and Browse action buttons so their visible text and
    /// AccessibleName reflect the currently-selected cue. Called on every selection change
    /// in the cue listbox AND immediately after a Browse pick (the "(custom)" tag flips
    /// based on whether a custom path is set). When the selection is empty — e.g. the
    /// listbox briefly clears during a profile reload — both buttons get a generic label
    /// and are disabled so a stray click can't act on a stale index.</summary>
    private void RefreshCueActionButtons()
    {
        var idx = cueList.SelectedIndex;
        if (idx < 0 || idx >= cueRows.Length)
        {
            playSelectedCueButton.Text = "&Play selected sound";
            playSelectedCueButton.AccessibleName = "Play selected sound";
            playSelectedCueButton.Enabled = false;
            browseSelectedCueButton.Text = "&Browse for selected sound...";
            browseSelectedCueButton.AccessibleName = "Browse for selected sound";
            browseSelectedCueButton.Enabled = false;
            return;
        }

        var cue = cueRows[idx];
        playSelectedCueButton.Enabled = true;
        playSelectedCueButton.Text = $"&Play {cue.DisplayName.ToLowerInvariant()}";
        playSelectedCueButton.AccessibleName = $"Play {cue.DisplayName.ToLowerInvariant()}";

        var customPath = cue.LoadCustomPath();
        browseSelectedCueButton.Enabled = true;
        if (string.IsNullOrWhiteSpace(customPath))
        {
            browseSelectedCueButton.Text = $"&Browse for {cue.DisplayName.ToLowerInvariant()}...";
            browseSelectedCueButton.AccessibleName = $"Browse for {cue.DisplayName.ToLowerInvariant()}, currently using the default sound";
        }
        else
        {
            var filename = Path.GetFileName(customPath);
            browseSelectedCueButton.Text = $"&Browse for {cue.DisplayName.ToLowerInvariant()}... (custom)";
            browseSelectedCueButton.AccessibleName = $"Browse for {cue.DisplayName.ToLowerInvariant()}, currently using your file {filename}";
        }
    }

    /// <summary>Repopulate the "Choose default sound" listbox for the currently-selected cue with
    /// its numbered variants, and select whichever variant is the active default. Disabled (with a
    /// note) when the cue has no built-in sounds on disk.</summary>
    private void RefreshDefaultSoundList()
    {
        suppressDefaultSoundPreview = true;
        try
        {
            defaultSoundList.Items.Clear();
            currentVariants = Array.Empty<string>();
            var idx = cueList.SelectedIndex;
            if (idx < 0 || idx >= cueRows.Length)
            {
                defaultSoundList.Enabled = false;
                return;
            }
            var cue = cueRows[idx];
            var variants = CueSounds.Variants(cue.DefaultFileName);
            defaultSoundLabel.Text = "Choose soun&d (Alt+D):";
            defaultSoundList.Enabled = true;
            currentVariants = variants;

            // Row 0 is always "(none)" = this cue is off. The numbered variants follow, offset by one.
            defaultSoundList.Items.Add("(none)");
            foreach (var v in variants) defaultSoundList.Items.Add(CueSounds.VariantLabel(cue.DefaultFileName, v));

            // (none) selected only when the cue is off; otherwise the chosen variant (default = first).
            var sel = 0;
            if (cue.LoadEnabled() && variants.Count > 0)
            {
                sel = 1;
                var chosen = CueSounds.ResolveDefaultFileName(cue.CueId, cue.DefaultFileName, AppConfig.Load());
                if (chosen is not null)
                {
                    for (var i = 0; i < variants.Count; i++)
                    {
                        if (variants[i].Equals(chosen, StringComparison.OrdinalIgnoreCase)) { sel = i + 1; break; }
                    }
                }
            }
            defaultSoundList.SelectedIndex = sel;
        }
        finally { suppressDefaultSoundPreview = false; }
    }

    /// <summary>The user arrowed onto / picked an entry in the sound list. "(none)" (row 0) turns the
    /// cue off; any other row turns it on and records that variant as the cue's sound, then previews
    /// it. The running app re-reads it all when the dialog closes (MainForm.ReloadAllCueSounds).</summary>
    private void OnDefaultSoundChosen()
    {
        // A programmatic re-fill must not persist or preview.
        if (suppressDefaultSoundPreview) return;
        var idx = cueList.SelectedIndex;
        var vi = defaultSoundList.SelectedIndex;
        if (idx < 0 || idx >= cueRows.Length || vi < 0) return;
        var cue = cueRows[idx];

        if (vi == 0)
        {
            // "(none)" — turn the cue off. No sound to preview.
            cue.SaveEnabled(false);
            if (cue.IsProfileSetting) ChangedAnyProfileSetting = true;
            RefreshCueActionButtons();
            return;
        }

        var variantIndex = vi - 1; // account for the (none) row
        if (variantIndex < 0 || variantIndex >= currentVariants.Count) return;
        var chosenFile = currentVariants[variantIndex];

        cue.SaveEnabled(true);
        if (cue.IsProfileSetting) ChangedAnyProfileSetting = true;
        var cfg = AppConfig.Load();
        cfg.DefaultCueSounds[cue.CueId] = chosenFile;
        TrySaveConfig(cfg);
        RefreshCueActionButtons();

        try
        {
            var path = Path.Combine(AppConfig.SoundsDirectory, chosenFile);
            if (File.Exists(path)) new CuePlayer(path).Play();
        }
        catch { /* a preview must never disturb the dialog */ }
    }

    /// <summary>Preview the currently-selected cue's current sound (custom or chosen default), so
    /// arrowing the cue list lets the user hear each cue. A cue set to "(none)" plays nothing.</summary>
    private void PreviewSelectedCueCurrentSound()
    {
        if (suppressDefaultSoundPreview) return;
        var idx = cueList.SelectedIndex;
        if (idx < 0 || idx >= cueRows.Length) return;
        var cue = cueRows[idx];
        if (!cue.LoadEnabled()) return; // "(none)" — silent
        try
        {
            var path = ResolveCueFilePath(cue);
            if (path is not null) new CuePlayer(path).Play();
        }
        catch { /* a preview must never disturb the dialog */ }
    }

    /// <summary>Resolves the WAV file currently configured for a cue: the user's custom
    /// override if set and on disk, otherwise the bundled default in <c>sounds\</c>.
    /// Returns null when neither resolves to an existing file (typical for save.wav /
    /// profile.wav before the project owner supplies them) so the caller can stay silent.
    /// Mirrors the resolution order in MainForm.TryLoadCueSound — the Play button must
    /// preview exactly what the cue would play if it fired now. Reads through the settings
    /// cache so we see whatever the user has changed in this dialog session, including
    /// custom paths not yet persisted to the profile JSON.</summary>
    private static string? ResolveCueFilePath(CueRowDescriptor cue)
    {
        var customPath = cue.LoadCustomPath();
        if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
        {
            return customPath;
        }
        // Otherwise the chosen default variant in sounds\ ("connect 1.wav" / "connect 2.wav" / ...),
        // resolved the same way MainForm.TryLoadCueSound resolves it.
        var defaultPath = CueSounds.ResolveDefaultPath(cue.CueId, cue.DefaultFileName, AppConfig.Load());
        return defaultPath is not null && File.Exists(defaultPath) ? defaultPath : null;
    }

    /// <summary>Preview a cue's currently-configured WAV through the system default audio
    /// output. Plays asynchronously (SoundPlayer.Play loads + plays on a thread-pool thread),
    /// so the dialog stays responsive even if the file is briefly slow to load. When no file
    /// resolves — e.g. a cue without a default WAV and no custom path — show a small popup
    /// so the user knows why nothing happened, rather than silently doing nothing and
    /// leaving them wondering whether the Play button worked.</summary>
    private void OnPlayClicked(CueRowDescriptor cue)
    {
        var path = ResolveCueFilePath(cue);
        if (path is null)
        {
            MessageBox.Show(this,
                $"No sound is currently configured for the {cue.DisplayName.ToLowerInvariant()}. " +
                $"Use the Browse button on this row to pick a WAV file.",
                "RemSound", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            // CuePlayer (NAudio) rather than System.Media.SoundPlayer so the preview copes with
            // any format — including 24-bit / 96 kHz files the basic player can't handle.
            new CuePlayer(path).Play();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Could not play {Path.GetFileName(path)}: {ex.Message}",
                "RemSound", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Open a WAV file picker for the given cue. The picker defaults to the user's
    /// previously-set custom path if one exists, falling back to the bundled sounds\ folder
    /// next to RemSound.exe — so picking a file from inside that folder is treated as
    /// "use the default" and the override is cleared rather than re-pointed at the same
    /// file (which would leave the user stuck with a stale copy if a future RemSound update
    /// replaces the default WAV). Writes through the settings cache, since custom cue paths
    /// are per-profile — clearing here also flips ChangedAnyProfileSetting so the save-prompt
    /// fires on the way out.</summary>
    private void OnBrowseClicked(Button btn, CueRowDescriptor cue)
    {
        var soundsFolder = AppConfig.SoundsDirectory;
        var existing = cue.LoadCustomPath();
        var initialDir = !string.IsNullOrWhiteSpace(existing) && File.Exists(existing)
            ? Path.GetDirectoryName(existing) ?? soundsFolder
            : soundsFolder;
        using var picker = new OpenFileDialog
        {
            Title = $"Choose a WAV file for {cue.DisplayName}",
            Filter = "WAV files (*.wav)|*.wav",
            CheckFileExists = true,
            InitialDirectory = initialDir,
            DereferenceLinks = true,
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        if (string.IsNullOrWhiteSpace(picker.FileName)) return;

        var pickedFullPath = Path.GetFullPath(picker.FileName);
        var soundsFolderFullPath = Path.GetFullPath(soundsFolder);

        // If the user picked a file inside the bundled sounds\ folder, treat it as a "use
        // default" — clear the override rather than store the path. Avoids freezing the
        // user on a specific shipped-default file across updates.
        if (pickedFullPath.StartsWith(soundsFolderFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            cue.SaveCustomPath(null);
        }
        else
        {
            cue.SaveCustomPath(pickedFullPath);
        }
        // The Startup cue is machine-wide, not part of the profile — don't arm the save prompt.
        if (cue.IsProfileSetting) ChangedAnyProfileSetting = true;
        // Refresh the visible action-button labels so the "(custom)" tag appears or
        // disappears right away. Belt-and-braces: the caller also refreshes, but doing it
        // here makes the function self-consistent.
        RefreshCueActionButtons();
    }

    /// <summary>Pull the latest UPnP snapshot and update the inline status label. Always
    /// called on the UI thread (either inline from a change handler or marshaled in from
    /// the StatusChanged subscription).</summary>
    private void RefreshUpnpStatusLabel()
    {
        var (status, external, lastError) = getUpnpSnapshot();
        // Skip the label entirely while UPnP is off — keeps the dialog quiet for users who
        // don't care, and stops the "Disabled" string from showing up next to an unticked
        // box (which would just read as redundant noise to NVDA).
        if (!upnpEnabledBox.Checked)
        {
            upnpStatusLabel.Text = "";
            upnpStatusLabel.AccessibleName = "UPnP status";
            return;
        }
        var text = status switch
        {
            RouterMappingStatus.Disabled => "Status: not yet started.",
            RouterMappingStatus.Searching => "Status: searching for a router that supports UPnP / NAT-PMP / PCP...",
            RouterMappingStatus.Mapped => external is not null
                ? $"Status: router port opened. Peers can reach you at {external.Address}:{external.Port}."
                : "Status: router port opened.",
            RouterMappingStatus.NoRouterFound => "Status: no router with UPnP / NAT-PMP / PCP found. Check that the feature is enabled on your router, or forward UDP 47830 manually.",
            RouterMappingStatus.CgnatDetected => external is not null
                ? $"Status: the router opened the port, but the external address ({external.Address}) is on a carrier-grade NAT — peers on the public internet will not be able to reach you. Consider Tailscale or the relay instead."
                : "Status: the router opened the port, but you are behind a carrier-grade NAT — peers on the public internet will not be able to reach you. Consider Tailscale or the relay instead.",
            RouterMappingStatus.MappingFailed => string.IsNullOrEmpty(lastError)
                ? "Status: the router rejected the port-mapping request."
                : $"Status: the router rejected the port-mapping request — {lastError}",
            _ => "",
        };
        upnpStatusLabel.Text = text;
        upnpStatusLabel.AccessibleName = string.IsNullOrEmpty(text) ? "UPnP status" : text;
    }
}
