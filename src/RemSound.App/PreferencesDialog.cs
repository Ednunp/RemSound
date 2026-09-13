using System.Net;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Preferences dialog. A six-tab dialog using the same accessible
/// <see cref="QuietTabControl"/> as the main window:
///   * General — Browse for RemSound profiles folder, the auto-save list, Accept remote volume
///     commands, UPnP automatic router port-forwarding (+ its status), and the two "clear
///     remembered list" buttons.
///   * Appearance — colour theme, show the volume/pan/EQ tab, main-window tab order, and the
///     discovered / remembered peer list toggles.
///   * Audio cues — the cue list (a plain list of cue names; arrowing previews each cue's
///     current sound), a "Choose sound" list whose "(none)" entry turns a cue off, the Play /
///     Browse actions, and the keyboard-clicks toggle.
///   * Startup behaviour — Start minimised, Start with Windows, Start with a specific profile
///     (+ the profile list). Moved here from the standalone Options-menu dialog.
///   * Update settings — startup-check toggle, frequency, manual check, silent-install, the
///     install time range, and show-what's-new.
///   * Logging — Enable logs, Write logs now, the logs-folder size warning, deleting old logs,
///     and Delete all logs.
///
/// All settings save through <see cref="RemSoundSettingsStore"/> or <see cref="AppConfig"/>
/// (Start with Windows: the Windows auto-start entry) on every change (no OK-to-commit).
/// Esc or Close dismisses.
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

    // "Auto save non-read only profiles" (2026-07-13). How often RemSound silently saves the current
    // profile if it's not read-only and has unsaved changes. A plain list (Ed asked for "a list") whose
    // rows map to minute intervals; row 0 = Never = off (the default). The save is silent — no cue.
    private readonly Label autoSaveLabel = new()
    {
        Text = "&Auto-save non-read-only profiles (Alt+A):",
        AccessibleName = "Auto-save non-read-only profiles",
        AutoSize = true,
        Padding = new Padding(0, 6, 0, 4),
    };
    private readonly ListBox autoSaveList = new()
    {
        IntegralHeight = false,
        Width = 360,
        Height = 132,
        AccessibleName = "Auto-save non-read-only profiles",
    };
    // Parallel to autoSaveList.Items: the minute interval each row means (0 = Never).
    private static readonly int[] AutoSaveMinuteOptions = { 0, 2, 5, 10, 15, 20, 30 };

    // Clear the GLOBAL (machine-wide) remembered lists — peers and applications are a shared address book
    // across all profiles, so these live in Preferences, not per-profile. Each asks for confirmation first.
    private readonly Button clearRememberedPeersButton = new()
    {
        Text = "Clear remembered &peers list...",
        AccessibleName = "Clear remembered peers list",
        AutoSize = true,
    };
    private readonly Button clearRememberedAppsButton = new()
    {
        Text = "Clear remembered applications &list...",
        AccessibleName = "Clear remembered applications list",
        AutoSize = true,
    };

    /// <summary>Test seam: the auto-save interval rows (minutes; 0 = Never), so a self-test can assert the
    /// list Ed asked for stays intact.</summary>
    internal static IReadOnlyList<int> AutoSaveMinuteOptionsForTest => AutoSaveMinuteOptions;

    // Audio cue UI (2026-05-28 revised after Ed's feedback that one-control-per-cue blew
    // out the tab order). A single list of cues — up/down arrows move between cues. Below it
    // sit the "Choose sound" list, then a Play button to preview and a Browse button to pick a
    // custom WAV. All act on whichever cue is currently selected in the list. The button labels
    // update live as the selection changes ("Play disconnect sound", "Browse for disconnect
    // sound...") so sighted and NVDA users alike know which cue they're about to act on. Tab
    // order in the cue section: cue list → Choose sound → Play → Browse → keyboard clicks
    // (five tab stops, not one per cue).
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

    // "Choose sound" — a second listbox under the cue list. The cue WAVs ship as numbered
    // variants ("connect 1.wav", "connect 2.wav", ...); after "(none)" and any file of your own,
    // this lists the variants for the cue currently selected in cueList. Arrowing onto a variant
    // previews it AND makes it the chosen default for that cue (machine-wide,
    // AppConfig.DefaultCueSounds). The count isn't hard-coded —
    // whatever "<base> <n>.wav" files exist are offered, so adding more sounds later needs no code.
    // "Choose sound" on screen AND to NVDA. The label used to say "Choose sound" while both names said
    // "Choose default sound", so a screen reader heard a different control from the one on screen.
    private readonly Label defaultSoundLabel = new()
    {
        Text = "Choose soun&d (Alt+D):",
        AccessibleName = "Choose sound",
        AutoSize = true,
        Padding = new Padding(0, 6, 0, 4),
    };

    private readonly ListBox defaultSoundList = new()
    {
        IntegralHeight = false,
        Height = 76,
        Width = 360,
        AccessibleName = "Choose sound",
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
    /// <see cref="DefaultFileName"/> is the base name of the built-in WAVs in <c>default sounds\</c>
    /// (shipped as numbered variants, "connect 1.wav" and so on). The Load/Save
    /// delegates close over the right backing store so the handlers don't need to know whether
    /// a row is per-profile (<see cref="RemSoundSettingsStore"/>) or machine-wide
    /// (<see cref="AppConfig"/> — the Startup cue and the nine send/receive, hide/show, checkbox
    /// and tab-switch cues).
    /// <see cref="IsProfileSetting"/> tells the handlers whether changing the row should flag
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
    // store while the machine-wide rows (Startup and the MachineRow cues) close over AppConfig.
    // Order = listbox order.
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
        // Startup row below, factored out for the other nine machine-wide cues.
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

    /// <summary>
    /// Persist a preference — and RECORD what the user just changed.
    ///
    /// <para>Ed, 2026-08-24: "change this control, does the right thing get logged." For this dialog
    /// the answer was no, twenty-one times over: every preference in it — the theme, the update
    /// window, the log-size warnings, auto-save, the tray and profile startup choices — persisted
    /// silently, so nothing a user changed here could be accounted for afterwards.</para>
    ///
    /// <para>Recorded by DIFFING what is on disk against what is about to be written, rather than by
    /// a hand-written line per control. This dialog has twenty-odd settings, and hand-written lists
    /// go stale — which is the whole reason we are here, since the existing logs were added by hand
    /// and never checked. A diff cannot drift: a preference added next month reports itself, named
    /// correctly, with nobody having to remember. Collections are skipped, because the cue-path map
    /// and the list orders would be noise rather than signal.</para>
    ///
    /// <para>It goes out through <see cref="UiChangeLog"/>, which the main window points at its own
    /// configuration-stamping logger — so a preference change lands in the log tagged with the audio
    /// configuration it was made in, without this dialog needing to know configurations exist.</para>
    /// </summary>
    private static void TrySaveConfig(AppConfig cfg)
    {
        RecordChangedPreferences(cfg);
        try { cfg.Save(); } catch { /* harmless — the choice just won't survive a restart */ }
    }

    private static void RecordChangedPreferences(AppConfig cfg)
    {
        if (UiChangeLog.Sink is null) return;   // no main window listening: nothing to record into
        try
        {
            var before = AppConfig.Load();
            foreach (var p in typeof(AppConfig).GetProperties())
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                var was = p.GetValue(before);
                var now = p.GetValue(cfg);
                // Collections compare by reference and would report a change on every single save;
                // their contents are their own story (peer lists, cue paths) and belong in their own
                // lines rather than as a rename of the whole collection.
                if (was is System.Collections.IEnumerable && was is not string) continue;
                if (Equals(was, now)) continue;
                UiChangeLog.Record($"preference: {p.Name}", $"{DescribePreference(was)} → {DescribePreference(now)}");
            }
        }
        catch { /* recording a change must never cost the user the change itself */ }
    }

    private static string DescribePreference(object? value) => value switch
    {
        null => "(unset)",
        bool b => b ? "on" : "off",
        _ => value.ToString() ?? "",
    };

    private readonly AccessibleCheckBox acceptRemoteVolumeBox = new()
    {
        // Alt+V, not Alt+A: "Auto-save non-read-only profiles" on the same tab already owns A, so Alt+A
        // from the auto-save list silently switched this security setting on or off. 2026-09-13 review.
        Text = "Accept remote &volume commands from peers (Alt+V)",
        AccessibleName = "Accept remote volume commands from peers",
        AutoSize = true,
    };

    // Update settings tab — startup-check checkbox, frequency dropdown, manual check button,
    // silent-install checkbox, install time range and show-what's-new. Its tab comes before the
    // Logging tab: "things related to the program staying current" before "things related to
    // diagnosing how it's running".
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

    // "Only install updates within this time range" (2026-07-26 feature): automatic installs are
    // deferred to a daily window — e.g. overnight — so an update never kills the sound while
    // someone's mid-session. Ticking enables the two time lists (hours in 15-minute steps).
    private readonly AccessibleCheckBox updateWindowBox = new()
    {
        Text = "Only install updates within this &time range",
        AccessibleName = "Only install updates within this time range",
        AutoSize = true,
    };
    private readonly ComboBox updateWindowStartBox = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 100,
        AccessibleName = "Update window start time",
    };
    private readonly ComboBox updateWindowEndBox = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 100,
        AccessibleName = "Update window end time",
    };

    // After an update installs and RemSound restarts, opening the About box once lets the user
    // see what changed. On by default (AppConfig.ShowWhatsNewAfterUpdate). 'h' mnemonic — 's' is
    // taken on this tab by "Check for updates on startup".
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

    private readonly AccessibleCheckBox showPanEqTabBox = new()
    {
        Text = "Show the volume, pan and E&Q for peers tab (Alt+Q)",
        AccessibleName = "Show the volume, pan and EQ for peers tab",
        AutoSize = true,
    };

    // Colour theme picker — "Match Windows (system)" / Light / Dark. Applied at startup via
    // Application.SetColorMode, so a change takes effect on the next launch. Purely visual.
    private readonly ComboBox themeBox = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 200,
        // The "takes effect next launch" hint is a separate grey label a screen reader never reaches, so the
        // name carries it too.
        AccessibleName = "Colour theme, takes effect next launch (Alt+T)",
    };

    // Appearance tab — reorder the main-window tabs, and toggle the Connectivity-tab peer lists.
    private readonly ListBox tabOrderList = new()
    {
        Width = 320,
        Height = 96,
        IntegralHeight = false,
        AccessibleName = "Tab order (Alt+O), press the move up and move down buttons to reorder",
    };
    private readonly Button moveTabUpButton = new() { Text = "Move &up (Alt+U)", AutoSize = true, AccessibleName = "Move tab up" };
    private readonly Button moveTabDownButton = new() { Text = "Move dow&n (Alt+N)", AutoSize = true, AccessibleName = "Move tab down" };
    private readonly AccessibleCheckBox enableDiscoveredPeersBox = new()
    {
        Text = "Enable the &discovered peers list on the Connectivity tab (Alt+D)",
        AccessibleName = "Enable the discovered peers list on the Connectivity tab",
        AutoSize = true,
    };
    private readonly AccessibleCheckBox enableRememberedPeersBox = new()
    {
        Text = "Enable the &remembered peers list on the Connectivity tab (Alt+R)",
        AccessibleName = "Enable the remembered peers list on the Connectivity tab",
        AutoSize = true,
    };

    // The main-window tabs shown in the reorder list (always all four, even if the pan/EQ tab is
    // currently hidden). Key matches AppConfig.MainTabOrder / MainForm.TabPageForKey.
    private static readonly (string Key, string Display)[] KnownTabs =
    [
        ("connectivity", "Connectivity"),
        ("audioio", "Audio inputs and outputs"),
        ("paneq", "Volume, pan and EQ for peers"),
        ("audioprofile", "Audio profile"),
    ];

    private sealed class TabOrderItem(string key, string display)
    {
        public string Key { get; } = key;
        public override string ToString() => display;
    }

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

    // --- Logging tab: log-folder housekeeping (2026-06-19). All machine-local (AppConfig). The two
    // spinners are greyed out until their enabling checkbox is ticked. Mnemonics on this tab: L, W,
    // S, M, D, Y, A — all distinct (tab scope is per-page, so reuse elsewhere is fine). ---
    private readonly AccessibleCheckBox warnIfLogsExceedBox = new()
    {
        Text = "Warn at startup if the logs folder is larger than (Alt+&S)",
        AccessibleName = "Warn at startup if the logs folder is larger than",
        AutoSize = true,
    };
    private readonly NumericUpDown logsSizeLimitBox = new()
    {
        Minimum = 1,
        Maximum = 100000,
        Increment = 10,
        Value = 100,
        Width = 90,
        AccessibleName = "Warn when the logs folder is larger than this many megabytes (Alt+M)",
    };
    private readonly MnemonicLabel logsSizeUnitLabel = new()
    {
        Text = "&megabytes",
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Padding = new Padding(6, 6, 0, 0),
    };
    private readonly AccessibleCheckBox pruneOldLogsBox = new()
    {
        Text = "Delete logs older than (Alt+&D)",
        AccessibleName = "Delete logs older than",
        AutoSize = true,
    };
    private readonly NumericUpDown pruneDaysBox = new()
    {
        Minimum = 1,
        Maximum = 30,
        Increment = 1,
        Value = 14,
        Width = 70,
        AccessibleName = "Delete logs older than this many days (Alt+Y)",
    };
    private readonly MnemonicLabel pruneDaysUnitLabel = new()
    {
        Text = "da&ys old",
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Padding = new Padding(6, 6, 0, 0),
    };
    private readonly Button deleteAllLogsButton = new()
    {
        Text = "Delete &all logs",
        AccessibleName = "Delete all logs",
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
        Text = "&Close",
        AutoSize = true,
        DialogResult = DialogResult.OK,
    };

    // The six-tab strip. Held as a field (not a constructor local) so OnShown can pick the first tab
    // and focus a control on it, and Ctrl+1..N can switch tabs — see OnShown for why NVDA needs that.
    private readonly QuietTabControl tabs = new() { Dock = DockStyle.Fill, TabIndex = 0, TabStop = true };

    /// <summary>True if the user changed a per-profile setting during this dialog session:
    /// Accept remote volume commands, or a per-profile cue (on/off or its sound file). The owner
    /// uses this to know whether to MarkProfileDirty after the dialog closes (those settings live
    /// on the Profile and need to flag a save-pending state).</summary>
    public bool ChangedAnyProfileSetting { get; private set; }

    private readonly Func<(RouterMappingStatus Status, IPEndPoint? External, string LastError)> getUpnpSnapshot;
    private EventHandler? upnpStatusSubscription;

    public PreferencesDialog(
        RemSoundSettingsStore settings,
        ProfileStore? profileStore,
        Func<bool> getLoggingEnabled,
        Action<bool> applyLoggingEnabled,
        Action writeLogsNow,
        Func<int> deleteAllLogs,
        Action checkForUpdatesNow,
        Action onUpdateFrequencyChanged,
        Action onAutoSaveIntervalChanged,
        Action onClearRememberedPeers,
        Action onClearRememberedApplications,
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
            RecordChangedPreferences(cfg);
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

        // Populate the cue listbox with the display names — order matches the cueRows array, and
        // the index of a selected row maps 1:1 to a CueRowDescriptor.
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
            RefreshDefaultSoundList();   // your new file gets its row in the Choose sound list, selected
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
                UiChangeLog.Record($"cue sound: {cue.CueId}", "built-in sound (your own file no longer used)");
                RefreshCueActionButtons();
                RefreshDefaultSoundList();   // the list must show the built-in sound now playing
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

        // "Choose sound" listbox — populated for the selected cue; arrowing it previews + chooses
        // (see OnDefaultSoundChosen). Initial fill for the cue selected at construction.
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
            UiChangeLog.Record("accept remote volume commands", acceptRemoteVolumeBox.Checked ? "on" : "off");
        };

        // Auto-save interval — machine-local, saved on change. Rows map 1:1 to AutoSaveMinuteOptions;
        // we select the row whose minutes match the saved value (falling back to Never). The owner's
        // onAutoSaveIntervalChanged re-applies the live timer so a change takes effect immediately.
        autoSaveList.Items.AddRange(new object[]
        {
            "Never", "Every 2 minutes", "Every 5 minutes", "Every 10 minutes",
            "Every 15 minutes", "Every 20 minutes", "Every 30 minutes",
        });
        var savedAutoSave = AppConfig.Load().AutoSaveNonReadOnlyMinutes;
        var autoSaveRow = Array.IndexOf(AutoSaveMinuteOptions, savedAutoSave);
        autoSaveList.SelectedIndex = autoSaveRow >= 0 ? autoSaveRow : 0;
        autoSaveList.SelectedIndexChanged += (_, _) =>
        {
            if (autoSaveList.SelectedIndex < 0) return;
            var cfg = AppConfig.Load();
            cfg.AutoSaveNonReadOnlyMinutes = AutoSaveMinuteOptions[autoSaveList.SelectedIndex];
            TrySaveConfig(cfg);
            onAutoSaveIntervalChanged();
        };
        autoSaveLabel.Click += (_, _) => autoSaveList.Focus();

        clearRememberedPeersButton.Click += (_, _) =>
        {
            if (MessageBox.Show(this,
                    "Clear the whole remembered peers list?\n\nThis empties the shared list of peers RemSound has remembered, for every profile. Peers you're actively connected to aren't affected, and any peer will simply be remembered again next time you connect to it.",
                    "RemSound", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                onClearRememberedPeers();
        };
        clearRememberedAppsButton.Click += (_, _) =>
        {
            if (MessageBox.Show(this,
                    "Clear the whole remembered applications list?\n\nThis empties the shared list of applications RemSound has remembered to send, for every profile. Apps you're actively sending aren't affected, and an app is remembered again the next time you tick it.",
                    "RemSound", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                onClearRememberedApplications();
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
        showPanEqTabBox.Checked = cfgForLoad.ShowPanEqTab;

        checkForUpdatesOnStartupBox.CheckedChanged += (_, _) =>
        {
            var cfg = AppConfig.Load();
            cfg.CheckForUpdatesOnStartup = checkForUpdatesOnStartupBox.Checked;
            TrySaveConfig(cfg);
        };
        updateFrequencyBox.SelectedIndexChanged += (_, _) =>
        {
            var cfg = AppConfig.Load();
            cfg.UpdateCheckFrequency = (UpdateCheckFrequency)updateFrequencyBox.SelectedIndex;
            TrySaveConfig(cfg);
            onUpdateFrequencyChanged();
        };
        silentlyInstallUpdatesBox.CheckedChanged += (_, _) =>
        {
            var cfg = AppConfig.Load();
            cfg.SilentlyInstallUpdates = silentlyInstallUpdatesBox.Checked;
            TrySaveConfig(cfg);
        };

        // Update install window: 96 quarter-hour slots in each list ("00:00" … "23:45"). The lists
        // only light up when the range is enabled; every change persists immediately like the
        // neighbouring update options. End at-or-before start wraps past midnight (22:00–06:00).
        for (var slot = 0; slot < UpdateWindow.SlotsPerDay; slot++)
        {
            var text = UpdateWindow.FormatMinutes(slot * UpdateWindow.SlotMinutes);
            updateWindowStartBox.Items.Add(text);
            updateWindowEndBox.Items.Add(text);
        }
        updateWindowBox.Checked = cfgForLoad.UpdateWindowEnabled;
        updateWindowStartBox.SelectedIndex = Math.Clamp(cfgForLoad.UpdateWindowStartMinutes / UpdateWindow.SlotMinutes, 0, UpdateWindow.SlotsPerDay - 1);
        updateWindowEndBox.SelectedIndex = Math.Clamp(cfgForLoad.UpdateWindowEndMinutes / UpdateWindow.SlotMinutes, 0, UpdateWindow.SlotsPerDay - 1);
        void SyncUpdateWindowEnabled()
        {
            updateWindowStartBox.Enabled = updateWindowBox.Checked;
            updateWindowEndBox.Enabled = updateWindowBox.Checked;
        }
        SyncUpdateWindowEnabled();
        void SaveUpdateWindow()
        {
            var cfg = AppConfig.Load();
            cfg.UpdateWindowEnabled = updateWindowBox.Checked;
            cfg.UpdateWindowStartMinutes = Math.Max(0, updateWindowStartBox.SelectedIndex) * UpdateWindow.SlotMinutes;
            cfg.UpdateWindowEndMinutes = Math.Max(0, updateWindowEndBox.SelectedIndex) * UpdateWindow.SlotMinutes;
            TrySaveConfig(cfg);
        }
        updateWindowBox.CheckedChanged += (_, _) => { SyncUpdateWindowEnabled(); SaveUpdateWindow(); };
        updateWindowStartBox.SelectedIndexChanged += (_, _) => SaveUpdateWindow();
        updateWindowEndBox.SelectedIndexChanged += (_, _) => SaveUpdateWindow();
        showWhatsNewAfterUpdateBox.Checked = cfgForLoad.ShowWhatsNewAfterUpdate;
        showWhatsNewAfterUpdateBox.CheckedChanged += (_, _) =>
        {
            var cfg = AppConfig.Load();
            cfg.ShowWhatsNewAfterUpdate = showWhatsNewAfterUpdateBox.Checked;
            TrySaveConfig(cfg);
        };
        checkForUpdatesNowButton.Click += (_, _) => checkForUpdatesNow();

        // UPnP toggle — persists immediately and tells MainForm to start / stop the mapper.
        // Status label refresh wires up below.
        upnpEnabledBox.CheckedChanged += (_, _) =>
        {
            var cfg = AppConfig.Load();
            cfg.UpnpEnabled = upnpEnabledBox.Checked;
            TrySaveConfig(cfg);
            applyUpnpEnabled(upnpEnabledBox.Checked);
            RefreshUpnpStatusLabel();
        };

        showPanEqTabBox.CheckedChanged += (_, _) =>
        {
            var cfg = AppConfig.Load();
            cfg.ShowPanEqTab = showPanEqTabBox.Checked;
            TrySaveConfig(cfg);
        };

        themeBox.Items.AddRange(["Match Windows (system)", "Light", "Dark"]);
        themeBox.SelectedIndex = (cfgForLoad.ThemeMode ?? "system").Trim().ToLowerInvariant() switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0,
        };
        themeBox.SelectedIndexChanged += (_, _) =>
        {
            var cfg = AppConfig.Load();
            cfg.ThemeMode = themeBox.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };
            TrySaveConfig(cfg);
        };
        var themeLabel = new MnemonicLabel { Text = "Colour &theme (Alt+T)", AutoSize = true, Anchor = AnchorStyles.Left, MnemonicTarget = themeBox };
        var themeHint = new Label { Text = "— takes effect next launch", AutoSize = true, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Left, Padding = new Padding(8, 4, 0, 0) };
        var themeRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
        themeRow.Controls.Add(themeLabel);
        themeRow.Controls.Add(themeBox);
        themeRow.Controls.Add(themeHint);

        // --- Appearance tab: reorder the main tabs, and toggle the Connectivity peer lists ---
        // Populate the reorder list from the saved order, normalised so all four tabs always appear.
        var orderedKeys = new List<string>();
        if (cfgForLoad.MainTabOrder is { } savedOrder)
            foreach (var k in savedOrder)
                if (Array.Exists(KnownTabs, t => t.Key == k) && !orderedKeys.Contains(k)) orderedKeys.Add(k);
        foreach (var (key, _) in KnownTabs)
            if (!orderedKeys.Contains(key)) orderedKeys.Add(key);
        foreach (var key in orderedKeys)
            tabOrderList.Items.Add(new TabOrderItem(key, Array.Find(KnownTabs, t => t.Key == key).Display));
        if (tabOrderList.Items.Count > 0) tabOrderList.SelectedIndex = 0;

        void SaveTabOrder()
        {
            var cfg = AppConfig.Load();
            cfg.MainTabOrder = tabOrderList.Items.Cast<TabOrderItem>().Select(t => t.Key).ToList();
            TrySaveConfig(cfg);
        }
        void MoveTab(int delta)
        {
            int i = tabOrderList.SelectedIndex;
            if (i < 0) return;
            int j = i + delta;
            if (j < 0 || j >= tabOrderList.Items.Count) return;
            var item = tabOrderList.Items[i];
            tabOrderList.Items.RemoveAt(i);
            tabOrderList.Items.Insert(j, item);
            tabOrderList.SelectedIndex = j;
            SaveTabOrder();
        }
        moveTabUpButton.Click += (_, _) => MoveTab(-1);
        moveTabDownButton.Click += (_, _) => MoveTab(1);

        var tabOrderLabel = new MnemonicLabel { Text = "Tab &order (Alt+O)", AutoSize = true, Anchor = AnchorStyles.Left, MnemonicTarget = tabOrderList };
        var tabOrderButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 0) };
        tabOrderButtons.Controls.Add(moveTabUpButton);
        tabOrderButtons.Controls.Add(moveTabDownButton);

        enableDiscoveredPeersBox.Checked = cfgForLoad.ShowDiscoveredPeers;
        enableDiscoveredPeersBox.CheckedChanged += (_, _) =>
        {
            var cfg = AppConfig.Load();
            cfg.ShowDiscoveredPeers = enableDiscoveredPeersBox.Checked;
            TrySaveConfig(cfg);
        };
        enableRememberedPeersBox.Checked = cfgForLoad.ShowRememberedPeers;
        enableRememberedPeersBox.CheckedChanged += (_, _) =>
        {
            var cfg = AppConfig.Load();
            cfg.ShowRememberedPeers = enableRememberedPeersBox.Checked;
            TrySaveConfig(cfg);
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
            // Said while the log is open: before logging goes off, after it comes on. 2026-09-13 review.
            if (!loggingBox.Checked) UiChangeLog.Record("logging", "off");
            applyLoggingEnabled(loggingBox.Checked);
            if (loggingBox.Checked) UiChangeLog.Record("logging", "on");
        };

        writeLogsNowButton.Click += (_, _) => writeLogsNow();

        // --- Logging housekeeping (machine-local; each control writes through to AppConfig on
        // change, like the Update-settings controls above). The two spinners follow their
        // checkbox's enabled state so they're only editable when the feature is on. ---
        warnIfLogsExceedBox.Checked = cfgForLoad.WarnIfLogsFolderExceeds;
        logsSizeLimitBox.Value = Math.Clamp(cfgForLoad.LogsFolderWarnThresholdMb, (int)logsSizeLimitBox.Minimum, (int)logsSizeLimitBox.Maximum);
        logsSizeLimitBox.Enabled = warnIfLogsExceedBox.Checked;
        logsSizeUnitLabel.MnemonicTarget = logsSizeLimitBox;
        warnIfLogsExceedBox.CheckedChanged += (_, _) =>
        {
            var cfg = AppConfig.Load();
            cfg.WarnIfLogsFolderExceeds = warnIfLogsExceedBox.Checked;
            TrySaveConfig(cfg);
            logsSizeLimitBox.Enabled = warnIfLogsExceedBox.Checked;
        };
        logsSizeLimitBox.ValueChanged += (_, _) =>
        {
            var cfg = AppConfig.Load();
            cfg.LogsFolderWarnThresholdMb = (int)logsSizeLimitBox.Value;
            TrySaveConfig(cfg);
        };

        pruneOldLogsBox.Checked = cfgForLoad.PruneOldLogs;
        pruneDaysBox.Value = Math.Clamp(cfgForLoad.PruneOldLogsDays, (int)pruneDaysBox.Minimum, (int)pruneDaysBox.Maximum);
        pruneDaysBox.Enabled = pruneOldLogsBox.Checked;
        pruneDaysUnitLabel.MnemonicTarget = pruneDaysBox;
        pruneOldLogsBox.CheckedChanged += (_, _) =>
        {
            var cfg = AppConfig.Load();
            cfg.PruneOldLogs = pruneOldLogsBox.Checked;
            TrySaveConfig(cfg);
            pruneDaysBox.Enabled = pruneOldLogsBox.Checked;
        };
        pruneDaysBox.ValueChanged += (_, _) =>
        {
            var cfg = AppConfig.Load();
            cfg.PruneOldLogsDays = (int)pruneDaysBox.Value;
            TrySaveConfig(cfg);
        };

        deleteAllLogsButton.Click += (_, _) =>
        {
            var confirm = new TaskDialogPage
            {
                Caption = "RemSound",
                Heading = "Delete all logs?",
                Text = "This permanently deletes every log file in the logs folder except the one currently in use. This cannot be undone.",
                Icon = TaskDialogIcon.Warning,
                Buttons = { TaskDialogButton.Yes, TaskDialogButton.No },
                DefaultButton = TaskDialogButton.No,
                AllowCancel = true,
            };
            if (TaskDialog.ShowDialog(this, confirm) != TaskDialogButton.Yes) return;
            var removed = deleteAllLogs();
            var done = new TaskDialogPage
            {
                Caption = "RemSound",
                Heading = "Logs deleted",
                Text = removed == 1 ? "Deleted 1 log file." : $"Deleted {removed} log files.",
                Icon = TaskDialogIcon.Information,
                Buttons = { TaskDialogButton.OK },
            };
            TaskDialog.ShowDialog(this, done);
        };

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

        // The install-window time range: start + end lists on one row, under the checkbox that
        // enables them. Labels carry the mnemonics and focus their list.
        var updateWindowRangeRow = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 0),
        };
        var updateWindowStartLabel = new MnemonicLabel { Text = "St&art time:", MnemonicTarget = updateWindowStartBox, AutoSize = true, Padding = new Padding(0, 6, 8, 0) };
        var updateWindowEndLabel = new MnemonicLabel { Text = "En&d time:", MnemonicTarget = updateWindowEndBox, AutoSize = true, Padding = new Padding(12, 6, 8, 0) };
        updateWindowRangeRow.Controls.Add(updateWindowStartLabel);
        updateWindowRangeRow.Controls.Add(updateWindowStartBox);
        updateWindowRangeRow.Controls.Add(updateWindowEndLabel);
        updateWindowRangeRow.Controls.Add(updateWindowEndBox);

        // Group the cue list, the Choose sound list, the two action buttons and the keyboard-clicks
        // checkbox into a single panel, the Audio cues tab's only row. The action buttons sit
        // side-by-side under the lists so they read as "buttons that act on the selected cue"
        // without taking up a second row of vertical space.
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
        // Tab order within the cue group: cue list -> Choose sound list -> Play/Browse ->
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

        // Logging tab rows: each spinner sits with its unit label on its own indented row beneath the
        // checkbox that enables it (number box first, then the "megabytes"/"days old" unit label —
        // matching the natural "...larger than [100] megabytes" reading order).
        var logsSizeRow = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(20, 2, 0, 0),
        };
        logsSizeRow.Controls.Add(logsSizeLimitBox);
        logsSizeRow.Controls.Add(logsSizeUnitLabel);
        var pruneDaysRow = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(20, 2, 0, 0),
        };
        pruneDaysRow.Controls.Add(pruneDaysBox);
        pruneDaysRow.Controls.Add(pruneDaysUnitLabel);

        // Six tabs (General, Appearance, Audio cues, Startup behaviour, Update settings, Logging),
        // accessible (QuietTabControl) like the main window. Ctrl+Tab / arrows on the
        // strip switch tabs; the active page's controls are the next tab stops. The control itself
        // is a field (declared above) so OnShown and Ctrl+1..N can reach it. Logging is its
        // own tab (2026-06-19); the two logging controls moved off the General tab to lead it.
        // Auto-save label + list stacked into one panel, so they read as a unit and sit as a single
        // row of the General tab directly after the "Browse for profiles folder" button, as Ed asked.
        var autoSavePanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
        };
        autoSavePanel.Controls.Add(autoSaveLabel);
        autoSavePanel.Controls.Add(autoSaveList);
        // The two "clear remembered list" actions sit at the END of the General tab (they're machine-wide
        // maintenance, not a per-profile setting). A header separates them from the settings above.
        var clearListsHeader = Theme.SectionHeader("Remembered lists (shared across all profiles)");
        tabs.TabPages.Add(MakeTab("General",
            browseProfilesFolderButton, autoSavePanel, acceptRemoteVolumeBox, upnpEnabledBox, upnpStatusLabel,
            clearListsHeader, clearRememberedPeersButton, clearRememberedAppsButton));
        tabs.TabPages.Add(MakeTab("Appearance",
            themeRow, showPanEqTabBox, tabOrderLabel, tabOrderList, tabOrderButtons,
            enableDiscoveredPeersBox, enableRememberedPeersBox));
        tabs.TabPages.Add(MakeTab("Audio cues", cueGroup));
        tabs.TabPages.Add(MakeTab("Startup behaviour",
            startMinimisedBox, startWithUserBox, startWithProfileBox, startupListPanel));
        tabs.TabPages.Add(MakeTab("Update settings",
            checkForUpdatesOnStartupBox, freqRow, checkForUpdatesNowButton, silentlyInstallUpdatesBox, updateWindowBox, updateWindowRangeRow, showWhatsNewAfterUpdateBox));
        tabs.TabPages.Add(MakeTab("Logging",
            loggingBox, writeLogsNowButton,
            warnIfLogsExceedBox, logsSizeRow,
            pruneOldLogsBox, pruneDaysRow,
            deleteAllLogsButton));

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

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Ctrl+1..N switch to that tab by its current position — matches the main window.
        if ((keyData & Keys.Modifiers) == Keys.Control)
        {
            int n = (keyData & Keys.KeyCode) switch
            {
                >= Keys.D1 and <= Keys.D9 => (keyData & Keys.KeyCode) - Keys.D1 + 1,
                >= Keys.NumPad1 and <= Keys.NumPad9 => (keyData & Keys.KeyCode) - Keys.NumPad1 + 1,
                _ => 0,
            };
            if (n >= 1 && n <= tabs.TabPages.Count)
            {
                tabs.SelectedIndex = n - 1;
                tabs.Focus();
                return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>When the dialog opens, select the first tab and land focus on the first real, NAMED
    /// leaf control inside it so NVDA announces the dialog and that control. Never the tab strip: the tab
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
    /// The last two happen inside <see cref="WinEventNotifier.AnnounceByFocusingLeaf"/>.
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
            RecordChangedPreferences(c); try { c.Save(); } catch (Exception ex) { ShowStartupWarning("Could not save Start minimised preference: " + ex.Message); }
        };

        startWithUserBox.CheckedChanged += (_, _) =>
        {
            if (suppressStartWithUserHandler) return;
            // Source of truth for auto-start is the registry, not AppConfig — flip it directly.
            var ok = startWithUserBox.Checked ? StartupAutoStart.TryEnable() : StartupAutoStart.TryDisable();
            if (ok) UiChangeLog.Record("start RemSound automatically when you sign in", startWithUserBox.Checked ? "on" : "off");
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
                        "You don't have any saved profiles yet. Save a profile first (File menu, Save as), then come back here and pick it.",
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
        RecordChangedPreferences(c); try { c.Save(); } catch (Exception ex) { ShowStartupWarning("Could not save the start-with-profile choice: " + ex.Message); }
    }

    private void ClearStartupProfileSelection()
    {
        var c = AppConfig.Load();
        c.StartWithProfileTitle = null;
        RecordChangedPreferences(c); try { c.Save(); } catch (Exception ex) { ShowStartupWarning("Could not save the start-with-profile choice: " + ex.Message); }
    }

    private void ShowStartupWarning(string message) =>
        MessageBox.Show(this, message, "Startup behaviour", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    /// <summary>Refresh the Play and Browse action buttons so their visible text and
    /// AccessibleName reflect the currently-selected cue. Called on every selection change
    /// in the cue listbox AND immediately after a Browse pick (the "(custom)" tag flips
    /// based on whether a custom path is set). When nothing is selected, both buttons get a
    /// generic label and are disabled so a stray click can't act on a stale index.</summary>
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

    /// <summary>Repopulate the "Choose sound" listbox for the currently-selected cue: "(none)", then a
    /// row for your own file if the cue has one (or had one while this dialog has been open), then its
    /// numbered built-in variants. Selects what the cue plays now — "(none)" when the cue is off. With
    /// no cue selected the list is left empty and disabled.</summary>
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

            // Row 0 is always "(none)" = this cue is off. When the cue has your own file — or had one
            // earlier while this dialog has been open — a row for it comes next, so the list says what
            // will actually play. The numbered variants follow.
            var customPath = cue.LoadCustomPath();
            if (!string.IsNullOrWhiteSpace(customPath)) ownFilesThisSession[cue.CueId] = customPath;
            defaultSoundList.Items.Add("(none)");
            ownFileRow = -1;
            if (ownFilesThisSession.TryGetValue(cue.CueId, out var ownFile))
            {
                ownFileRow = defaultSoundList.Items.Add($"Your own file: {Path.GetFileName(ownFile)}");
            }
            var firstVariantRow = defaultSoundList.Items.Count;
            foreach (var v in variants) defaultSoundList.Items.Add(CueSounds.VariantLabel(cue.DefaultFileName, v));

            // (none) when the cue is off; your own file when one is set, because that is what plays;
            // otherwise the chosen variant (default = first).
            var sel = 0;
            if (cue.LoadEnabled() && !string.IsNullOrWhiteSpace(customPath) && ownFileRow >= 0)
            {
                sel = ownFileRow;
            }
            else if (cue.LoadEnabled() && variants.Count > 0)
            {
                sel = firstVariantRow;
                var chosen = CueSounds.ResolveDefaultFileName(cue.CueId, cue.DefaultFileName, AppConfig.Load());
                if (chosen is not null)
                {
                    for (var i = 0; i < variants.Count; i++)
                    {
                        if (variants[i].Equals(chosen, StringComparison.OrdinalIgnoreCase)) { sel = firstVariantRow + i; break; }
                    }
                }
            }
            defaultSoundList.SelectedIndex = sel;
        }
        finally { suppressDefaultSoundPreview = false; }
    }

    /// <summary>Your own file for each cue, as it was when this dialog saw it. Kept so choosing a
    /// built-in sound — which replaces your file — can be undone by arrowing back onto your file's row
    /// before the dialog closes. Arrowing through the list to hear the choices must never lose anything.</summary>
    private readonly Dictionary<string, string> ownFilesThisSession = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The row in the sound list for the selected cue's own file, or -1 when it has none.</summary>
    private int ownFileRow = -1;

    /// <summary>The user arrowed onto / picked an entry in the sound list. "(none)" (row 0) turns the
    /// cue off; your own file's row turns it back on with your file; a built-in sound turns it on with
    /// that sound — replacing your own file, if there was one — and previews it. The running app
    /// re-reads it all when the dialog closes (MainForm.ReloadAllCueSounds).
    ///
    /// <para>Choosing a built-in sound used to change nothing you could hear while a custom file was set:
    /// it was saved, and previewed, and your file went on playing anyway, because a custom file always
    /// wins. The list now shows your file as a row of its own, and choosing anything else really does
    /// switch to it. 2026-09-13 review.</para></summary>
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
            // "(none)" — turn the cue off. No sound to preview. Your own file is kept for when it is
            // turned back on.
            cue.SaveEnabled(false);
            if (cue.IsProfileSetting) ChangedAnyProfileSetting = true;
            UiChangeLog.Record($"cue sound: {cue.CueId}", "off");
            RefreshCueActionButtons();
            return;
        }

        if (vi == ownFileRow && ownFilesThisSession.TryGetValue(cue.CueId, out var ownFile))
        {
            cue.SaveEnabled(true);
            if (!string.Equals(cue.LoadCustomPath(), ownFile, StringComparison.OrdinalIgnoreCase)) cue.SaveCustomPath(ownFile);
            if (cue.IsProfileSetting) ChangedAnyProfileSetting = true;
            UiChangeLog.Record($"cue sound: {cue.CueId}", $"your own file {Path.GetFileName(ownFile)}");
            RefreshCueActionButtons();
            try
            {
                if (File.Exists(ownFile)) new CuePlayer(ownFile).Play();
            }
            catch { /* a preview must never disturb the dialog */ }
            return;
        }

        var variantIndex = vi - (ownFileRow >= 0 ? ownFileRow + 1 : 1); // account for the rows above the variants
        if (variantIndex < 0 || variantIndex >= currentVariants.Count) return;
        var chosenFile = currentVariants[variantIndex];

        cue.SaveEnabled(true);
        // A built-in sound replaces your own file, or it would never be heard. Your file's row stays in
        // the list until the dialog closes, so arrowing back onto it undoes this.
        if (!string.IsNullOrWhiteSpace(cue.LoadCustomPath())) cue.SaveCustomPath(null);
        if (cue.IsProfileSetting) ChangedAnyProfileSetting = true;
        var cfg = AppConfig.Load();
        cfg.DefaultCueSounds[cue.CueId] = chosenFile;
        // Recorded by hand, because this one lands in a DICTIONARY and the property diff in
        // RecordChangedPreferences deliberately skips collections — a dictionary compares by
        // reference and would otherwise report a change on every save. Which cue sound is chosen is
        // exactly the sort of thing asked about later ("why does it make that noise now"), so it gets
        // its own line rather than falling into that exemption. 2026-08-24.
        UiChangeLog.Record($"cue sound: {cue.CueId}", chosenFile);
        TrySaveConfig(cfg);
        RefreshCueActionButtons();

        try
        {
            var path = Path.Combine(AppConfig.SoundsDirectory, chosenFile);
            if (File.Exists(path)) new CuePlayer(path).Play();
        }
        catch { /* a preview must never disturb the dialog */ }
    }

    // ---- Gate seams: the cue list and the Choose sound list, driven the way a user drives them ----
    internal int CueCountForTest => cueRows.Length;
    internal void SelectCueForTest(int index) => cueList.SelectedIndex = index;
    internal ListBox SoundListForTest => defaultSoundList;

    /// <summary>What Browse does with a file from outside the built-in sounds folder, without the picker.</summary>
    internal void UseOwnFileForTest(string path)
    {
        var cue = cueRows[cueList.SelectedIndex];
        cue.SaveEnabled(true);
        cue.SaveCustomPath(path);
        RefreshCueActionButtons();
        RefreshDefaultSoundList();
    }

    /// <summary>The file the selected cue would play if it fired now — the same resolution Play uses.</summary>
    internal string? SoundThatWouldPlayForTest() =>
        cueList.SelectedIndex is var i && i >= 0 && i < cueRows.Length ? ResolveCueFilePath(cueRows[i]) : null;

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
    /// override if set and on disk, otherwise the chosen built-in variant in <c>default sounds\</c>.
    /// Returns null when neither resolves to an existing file so the caller can stay silent.
    /// Mirrors the resolution order in MainForm.TryLoadCueSound — the Play button must
    /// preview exactly what the cue would play if it fired now. Reads through the row's own
    /// store (the settings cache for a profile cue, AppConfig for a machine-wide one) so we see
    /// whatever the user has changed in this dialog session, including custom paths not yet
    /// persisted to the profile JSON.</summary>
    private static string? ResolveCueFilePath(CueRowDescriptor cue)
    {
        var customPath = cue.LoadCustomPath();
        if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
        {
            return customPath;
        }
        // Otherwise the chosen default variant in default sounds\ ("connect 1.wav" / "connect 2.wav" / ...),
        // resolved the same way MainForm.TryLoadCueSound resolves it.
        var defaultPath = CueSounds.ResolveDefaultPath(cue.CueId, cue.DefaultFileName, AppConfig.Load());
        return defaultPath is not null && File.Exists(defaultPath) ? defaultPath : null;
    }

    /// <summary>Preview a cue's currently-configured WAV through the system default audio
    /// output. Plays asynchronously (CuePlayer.Play loads + plays on a thread-pool thread),
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
                "Pick one in the Choose sound list, or use the Browse button to choose your own WAV file.",
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
    /// previously-set custom path if one exists, falling back to the built-in default sounds\
    /// folder next to RemSound.exe. Picking a file from inside that folder is treated as
    /// "use the default" and the override is cleared rather than re-pointed at the same
    /// file (which would leave the user stuck with a stale copy if a future RemSound update
    /// replaces the default WAV). Writes through the row's own store (the profile for a profile
    /// cue, AppConfig for a machine-wide one); for a profile cue it also flips
    /// ChangedAnyProfileSetting so the save-prompt fires on the way out.</summary>
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

        // If the user picked a file inside the built-in default sounds\ folder, treat it as a "use
        // default" — clear the override rather than store the path. Avoids freezing the
        // user on a specific shipped-default file across updates.
        if (pickedFullPath.StartsWith(soundsFolderFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            cue.SaveCustomPath(null);
            UiChangeLog.Record($"cue sound: {cue.CueId}", $"built-in sound {Path.GetFileName(pickedFullPath)}");
        }
        else
        {
            cue.SaveCustomPath(pickedFullPath);
            UiChangeLog.Record($"cue sound: {cue.CueId}", $"your own file {Path.GetFileName(pickedFullPath)}");
        }
        // Machine-wide cues (Startup and the other AppConfig rows) aren't part of the profile — don't
        // arm the save prompt for them.
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
