using System.Net;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Preferences dialog. Holds settings that aren't profile-management actions in their own
/// right:
///   * Browse for RemSound profiles folder — picks the directory the profile picker scans
///     next launch.
///   * Audio cue sounds — per-cue enable list (connect, disconnect, recording start/stop).
///     One CheckedListBox; ticked items play, unticked are silent. Replaced the old single
///     "Mute connect/disconnect sounds" toggle (2026-05-15) when recording start/stop cues
///     were added — a CheckedListBox scales to future cues without dialog re-layout. Label
///     gained the "Audio" prefix on 2026-05-21 to disambiguate from the underlying engine's
///     "buffer cues" and "ASIO cues" diagnostic terms, which look the same in writing.
///   * Accept remote volume commands from peers — opt-in for the remote-control feature.
///   * Update settings — startup-check toggle, frequency, manual check, silent-install
///     toggle. Layout deliberately reads top-to-bottom as the question the user is
///     answering: "Check for updates on startup? (yes/no) Then, in the background, every?
///     (interval) When one's found? (silent install / ask first)".
///   * UPnP — optional automatic router port-forwarding via Mono.Nat. Off by default; when
///     ticked, we kick off discovery and surface the result + external address inline.
///   * Enable logs + Write logs now.
///
/// Startup behaviour was previously a button here that opened <see cref="StartupBehaviourDialog"/>;
/// it's now a top-level Options menu item in its own right (2026-05-15 menu reorg).
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

    private readonly CheckedListBox cueList = new()
    {
        CheckOnClick = true,
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

    /// <summary>Describes one cue. <see cref="DisplayName"/> ends up in the listbox row;
    /// <see cref="CueId"/> is the well-known key from <see cref="MainForm.CueId"/>; the
    /// LoadEnabled / SaveEnabled pair routes the checkbox state to the right
    /// <see cref="RemSoundSettingsStore"/> getter/setter so we don't need a hard-coded
    /// switch on index.</summary>
    private sealed record CueRowDescriptor(
        string DisplayName,
        string CueId,
        Func<RemSoundSettingsStore, bool> LoadEnabled,
        Action<RemSoundSettingsStore, bool> SaveEnabled);

    private static readonly CueRowDescriptor[] CueRows =
    [
        new("Connect sound", MainForm.CueId.Connect,
            s => s.LoadEnableConnectCue(), (s, v) => s.SaveEnableConnectCue(v)),
        new("Disconnect sound", MainForm.CueId.Disconnect,
            s => s.LoadEnableDisconnectCue(), (s, v) => s.SaveEnableDisconnectCue(v)),
        new("Recording start sound", MainForm.CueId.RecordStart,
            s => s.LoadEnableRecordStartCue(), (s, v) => s.SaveEnableRecordStartCue(v)),
        new("Recording stop sound", MainForm.CueId.RecordStop,
            s => s.LoadEnableRecordStopCue(), (s, v) => s.SaveEnableRecordStopCue(v)),
        new("Profile saved sound", MainForm.CueId.Save,
            s => s.LoadEnableSaveCue(), (s, v) => s.SaveEnableSaveCue(v)),
        new("Profile switched sound", MainForm.CueId.ProfileSwitch,
            s => s.LoadEnableProfileSwitchCue(), (s, v) => s.SaveEnableProfileSwitchCue(v)),
        new("Profile menu open sound", MainForm.CueId.ProfileMenuOpen,
            s => s.LoadEnableProfileMenuOpenCue(), (s, v) => s.SaveEnableProfileMenuOpenCue(v)),
        new("Update sound", MainForm.CueId.Update,
            s => s.LoadEnableUpdateCue(), (s, v) => s.SaveEnableUpdateCue(v)),
    ];

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

    private readonly Button closeButton = new()
    {
        Text = "Close",
        AutoSize = true,
        DialogResult = DialogResult.OK,
    };

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

        Text = "Preferences";
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

        // Populate the cue listbox — order matches the CueRows array, and the index of a
        // selected row maps 1:1 to a CueRowDescriptor. Each row's ticked state is loaded
        // from the active profile's per-cue enable flag via the descriptor.
        cueList.Items.Clear();
        foreach (var c in CueRows)
        {
            cueList.Items.Add(c.DisplayName, c.LoadEnabled(settings));
        }
        if (cueList.Items.Count > 0) cueList.SelectedIndex = 0;
        cueList.ItemCheck += (_, e) =>
        {
            // ItemCheck fires BEFORE the visual state actually flips; e.NewValue is what
            // it's about to become, so the persisted value matches what the user just
            // clicked.
            if (e.Index < 0 || e.Index >= CueRows.Length) return;
            var nowEnabled = e.NewValue == CheckState.Checked;
            CueRows[e.Index].SaveEnabled(settings, nowEnabled);
            ChangedAnyProfileSetting = true;
        };

        // Selection changes update the two action buttons' labels so they always tell the
        // user which cue they're about to act on. Refreshed eagerly at construction time
        // for the initial selection too.
        cueList.SelectedIndexChanged += (_, _) => RefreshCueActionButtons(settings);
        RefreshCueActionButtons(settings);

        playSelectedCueButton.Click += (_, _) =>
        {
            if (cueList.SelectedIndex < 0 || cueList.SelectedIndex >= CueRows.Length) return;
            OnPlayClicked(CueRows[cueList.SelectedIndex], settings);
        };
        browseSelectedCueButton.Click += (_, _) =>
        {
            if (cueList.SelectedIndex < 0 || cueList.SelectedIndex >= CueRows.Length) return;
            OnBrowseClicked(browseSelectedCueButton, CueRows[cueList.SelectedIndex], settings);
            RefreshCueActionButtons(settings);
        };

        // Right-click "Use default sound" context menu lives on the Browse button. It acts
        // on whichever cue is currently selected — same as a left click. Disabled when no
        // override is set so it can't accidentally do nothing.
        var browseCtx = new ContextMenuStrip();
        var useDefaultItem = new ToolStripMenuItem("Use default sound");
        useDefaultItem.Click += (_, _) =>
        {
            if (cueList.SelectedIndex < 0 || cueList.SelectedIndex >= CueRows.Length) return;
            var cue = CueRows[cueList.SelectedIndex];
            if (settings.LoadCustomCuePath(cue.CueId) is not null)
            {
                settings.SaveCustomCuePath(cue.CueId, null);
                ChangedAnyProfileSetting = true;
                RefreshCueActionButtons(settings);
            }
        };
        browseCtx.Opening += (_, _) =>
        {
            if (cueList.SelectedIndex < 0 || cueList.SelectedIndex >= CueRows.Length)
            {
                useDefaultItem.Enabled = false;
                useDefaultItem.Text = "Use default sound";
            }
            else
            {
                var cue = CueRows[cueList.SelectedIndex];
                useDefaultItem.Enabled = settings.LoadCustomCuePath(cue.CueId) is not null;
                useDefaultItem.Text = $"Use default {cue.DisplayName.ToLowerInvariant()}";
                useDefaultItem.AccessibleName = useDefaultItem.Text;
            }
        };
        browseCtx.Items.Add(useDefaultItem);
        browseSelectedCueButton.ContextMenuStrip = browseCtx;

        cueListLabel.Click += (_, _) => cueList.Focus();

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

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 13,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 12; i++) panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // Tab order top-to-bottom: browse-profiles-folder → cue list → Play selected →
        // Browse for selected → accept remote → check-on-startup → frequency → check-now →
        // silent install → UPnP → enable logs → write logs now → close. The cue section is
        // three tab stops total: the list itself (where up/down navigates between cues and
        // Space toggles enable), then the two action buttons that operate on whichever cue
        // is currently selected in the list.
        browseProfilesFolderButton.TabIndex = 0;
        cueList.TabIndex = 1;
        playSelectedCueButton.TabIndex = 2;
        browseSelectedCueButton.TabIndex = 3;
        acceptRemoteVolumeBox.TabIndex = 4;
        checkForUpdatesOnStartupBox.TabIndex = 5;
        updateFrequencyBox.TabIndex = 6;
        checkForUpdatesNowButton.TabIndex = 7;
        silentlyInstallUpdatesBox.TabIndex = 8;
        showWhatsNewAfterUpdateBox.TabIndex = 9;
        upnpEnabledBox.TabIndex = 10;
        loggingBox.TabIndex = 11;
        writeLogsNowButton.TabIndex = 12;
        closeButton.TabIndex = 13;

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
            RowCount = 3,
        };
        cueGroup.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        cueGroup.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cueGroup.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cueGroup.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var cueActions = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 0),
        };
        cueActions.Controls.Add(playSelectedCueButton);
        cueActions.Controls.Add(browseSelectedCueButton);
        cueGroup.Controls.Add(cueListLabel, 0, 0);
        cueGroup.Controls.Add(cueList, 0, 1);
        cueGroup.Controls.Add(cueActions, 0, 2);

        panel.Controls.Add(browseProfilesFolderButton, 0, 0);
        panel.Controls.Add(cueGroup, 0, 1);
        panel.Controls.Add(acceptRemoteVolumeBox, 0, 2);
        panel.Controls.Add(checkForUpdatesOnStartupBox, 0, 3);
        panel.Controls.Add(freqRow, 0, 4);
        panel.Controls.Add(checkForUpdatesNowButton, 0, 5);
        panel.Controls.Add(silentlyInstallUpdatesBox, 0, 6);
        panel.Controls.Add(showWhatsNewAfterUpdateBox, 0, 7);
        panel.Controls.Add(upnpEnabledBox, 0, 8);
        panel.Controls.Add(upnpStatusLabel, 0, 9);
        panel.Controls.Add(loggingBox, 0, 10);
        panel.Controls.Add(writeLogsNowButton, 0, 11);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 0, 12, 12),
        };
        buttons.Controls.Add(closeButton);

        Controls.Add(panel);
        Controls.Add(buttons);

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

    /// <summary>Refresh the Play and Browse action buttons so their visible text and
    /// AccessibleName reflect the currently-selected cue. Called on every selection change
    /// in the cue listbox AND immediately after a Browse pick (the "(custom)" tag flips
    /// based on whether a custom path is set). When the selection is empty — e.g. the
    /// listbox briefly clears during a profile reload — both buttons get a generic label
    /// and are disabled so a stray click can't act on a stale index.</summary>
    private void RefreshCueActionButtons(RemSoundSettingsStore settings)
    {
        var idx = cueList.SelectedIndex;
        if (idx < 0 || idx >= CueRows.Length)
        {
            playSelectedCueButton.Text = "&Play selected sound";
            playSelectedCueButton.AccessibleName = "Play selected sound";
            playSelectedCueButton.Enabled = false;
            browseSelectedCueButton.Text = "&Browse for selected sound...";
            browseSelectedCueButton.AccessibleName = "Browse for selected sound";
            browseSelectedCueButton.Enabled = false;
            return;
        }

        var cue = CueRows[idx];
        playSelectedCueButton.Enabled = true;
        playSelectedCueButton.Text = $"&Play {cue.DisplayName.ToLowerInvariant()}";
        playSelectedCueButton.AccessibleName = $"Play {cue.DisplayName.ToLowerInvariant()}";

        var customPath = settings.LoadCustomCuePath(cue.CueId);
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

    /// <summary>Resolves the WAV file currently configured for a cue: the user's custom
    /// override if set and on disk, otherwise the bundled default in <c>sounds\</c>.
    /// Returns null when neither resolves to an existing file (typical for save.wav /
    /// profile.wav before the project owner supplies them) so the caller can stay silent.
    /// Mirrors the resolution order in MainForm.TryLoadCueSound — the Play button must
    /// preview exactly what the cue would play if it fired now. Reads through the settings
    /// cache so we see whatever the user has changed in this dialog session, including
    /// custom paths not yet persisted to the profile JSON.</summary>
    private static string? ResolveCueFilePath(CueRowDescriptor cue, RemSoundSettingsStore settings)
    {
        var customPath = settings.LoadCustomCuePath(cue.CueId);
        if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
        {
            return customPath;
        }
        // Default WAV filename is built from the cue ID — same convention as MainForm.
        // The dictionary kept here makes the mapping explicit and lets us pretty-print
        // "record start" / "record stop" with the space rather than the cue ID's hyphen.
        var defaultFileName = cue.CueId switch
        {
            MainForm.CueId.Connect => "connect.wav",
            MainForm.CueId.Disconnect => "disconnect.wav",
            MainForm.CueId.RecordStart => "record start.wav",
            MainForm.CueId.RecordStop => "record stop.wav",
            MainForm.CueId.Save => "save.wav",
            MainForm.CueId.ProfileSwitch => "profile.wav",
            MainForm.CueId.ProfileMenuOpen => "profile menu open.wav",
            MainForm.CueId.Update => "update.wav",
            _ => null,
        };
        if (defaultFileName is null) return null;
        var defaultPath = Path.Combine(AppContext.BaseDirectory, "sounds", defaultFileName);
        return File.Exists(defaultPath) ? defaultPath : null;
    }

    /// <summary>Preview a cue's currently-configured WAV through the system default audio
    /// output. Plays asynchronously (SoundPlayer.Play loads + plays on a thread-pool thread),
    /// so the dialog stays responsive even if the file is briefly slow to load. When no file
    /// resolves — e.g. a cue without a default WAV and no custom path — show a small popup
    /// so the user knows why nothing happened, rather than silently doing nothing and
    /// leaving them wondering whether the Play button worked.</summary>
    private void OnPlayClicked(CueRowDescriptor cue, RemSoundSettingsStore settings)
    {
        var path = ResolveCueFilePath(cue, settings);
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
    private void OnBrowseClicked(Button btn, CueRowDescriptor cue, RemSoundSettingsStore settings)
    {
        var soundsFolder = Path.Combine(AppContext.BaseDirectory, "sounds");
        var existing = settings.LoadCustomCuePath(cue.CueId);
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
            settings.SaveCustomCuePath(cue.CueId, null);
        }
        else
        {
            settings.SaveCustomCuePath(cue.CueId, pickedFullPath);
        }
        ChangedAnyProfileSetting = true;
        // Refresh the visible action-button labels so the "(custom)" tag appears or
        // disappears right away. Belt-and-braces: the caller also refreshes, but doing it
        // here makes the function self-consistent.
        RefreshCueActionButtons(settings);
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
