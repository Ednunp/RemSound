using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NAudio.CoreAudioApi;
using RemSound.Core;
using RemSound.Receiver;
using RemSound.Sender;

namespace RemSound.App;

/// <summary>
/// Main RemSound window. Designed for keyboard / NVDA use.
///
/// UX shape (matches the older RSound app the user asked us to preserve):
///   * Auto-connects on Shown — no Connect button. Discovery starts immediately.
///   * "Connectivity and transport" button opens a settings + peers dialog.
///   * Main form keeps just: mode (send/receive), receive device, volume,
///     send capture devices (CheckedListBox + status label), other actions, status.
///   * Every CheckedListBox has an adjacent status label that announces
///     the focused item, its checked state, position, and "Press Space to toggle".
///   * Knob changes flow live to the audio engine — no engine restarts.
/// </summary>
public sealed partial class MainForm : Form
{
    private const string AppName = "RemSound";

    // Engines and helpers
    private readonly PeerDiscoveryService discovery = new();
    private readonly AudioSender sender = new();
    private readonly AudioReceiver receiver = new();
    private readonly RemSoundSettingsStore settings = new(AppName);
    private readonly RemSoundLog logFile = new();
    private readonly RemSoundUpdater updater = new();
    // Background timer that fires the periodic update-poll. Interval comes from
    // AppConfig.UpdateCheckFrequency; "Never" stops the timer entirely. Re-armed by
    // ApplyUpdateCheckTimer whenever the user changes the frequency in Preferences.
    private readonly System.Windows.Forms.Timer updateCheckTimer = new();
    private readonly MainFormHotkeyController hotkeyController;
    private readonly MainFormTrayController trayController;
    private readonly RecordingController recordingController;
    // Hook into Windows sleep/resume so the audio backend gets rebuilt after wake. USB
    // audio devices (ASIO / WASAPI) commonly come back in a degraded post-resume state
    // where the pipeline runs but no sound actually comes out of the interface — restarting
    // the backend on resume clears it. Subscribed in the constructor, disposed in FormClosing.
    private readonly PowerResumeHandler powerResumeHandler;
    // Optional UPnP / NAT-PMP / PCP router-port opener (Mono.Nat under the hood). Off by
    // default; the user opts in via the "Automatically open my router for incoming
    // connections" tick in Preferences (AppConfig.UpnpEnabled). Started lazily in Shown
    // when the flag is on, restarted from OnSystemResume so a sleep-drop on the router's
    // NAT table is recovered automatically, and stopped in FormClosing.
    private readonly RouterPortMapper routerPortMapper;
    // Menu items for the Record menu kept as fields so RecordingStateChanged can flip
    // the visible text + accessibility name between "Start recording" and "Stop recording"
    // without rebuilding the menu.
    private ToolStripMenuItem? startStopRecordingMenuItem;
    // Held so PopulateRecentProfilesMenu can clear + repopulate it on every DropDownOpening
    // (and once during construction so it's not empty before the first open).
    private ToolStripMenuItem? recentProfilesMenu;

    // --- Main form controls ---
    // Two standalone CheckBoxes for the Send / Receive toggles. Modern .NET (.NET 10) raises
    // UIA state-change notifications on CheckBox.Checked changes, so NVDA reliably announces
    // "checked" / "not checked" for both spacebar toggles and programmatic toggles (hotkeys,
    // tray menu). Replaced an earlier CheckedListBox-based approach that was used to work
    // around older WinForms accessibility issues.
    // Plain WinForms CheckBox configured exactly like the working RSound.old build:
    //   * Field initializer sets only AutoSize and the bare Text (no ampersand).
    //   * Text (with mnemonic) and AccessibleName are then re-assigned in the constructor body.
    //     This two-step pattern matches the old code byte-for-byte; setting them in the field
    //     initializer alone was enough to break NVDA state-change announcements.
    //   * Each box is wrapped in its own FlowLayoutPanel before being placed in the
    //     TableLayoutPanel cell — same as the old code.
    private readonly AccessibleCheckBox receiveAudioCheckbox = new() { Text = "Receive audio", AutoSize = true };
    private readonly AccessibleCheckBox sendMyAudioCheckbox = new() { Text = "Send my audio", AutoSize = true };
    private readonly TrackBar volumeBar = new() { Minimum = 0, Maximum = 100, TickFrequency = 10, Value = 100, Width = 200 };
    // Receive output device. Pre-selected to the system default at startup; user can override
    // for the session. Selection is NOT persisted — next session starts on default again.
    private readonly CheckedListBox receiveOutputDevicesList = new() { CheckOnClick = true, Width = 430, Height = 90 };
    private readonly Label receiveOutputDevicesStatusLabel = new() { AutoSize = true, Text = "No output device selected." };
    // Capture devices the user has ticked for sending. Two lists — render-side outputs (loopback
    // capture: system audio / soundcard playback) and capture-side inputs (mics, line-ins). Both
    // are summed into one outgoing stream by the sender's MixingEngine. Intentionally NOT
    // persisted: every session starts with everything unticked and no audio sent. The user
    // re-ticks once per session. Stops any device-routing surprise (a card unplugged between
    // runs, IDs changing, etc.).
    private readonly CheckedListBox sendOutputDevicesList = new() { CheckOnClick = true, Width = 430, Height = 90 };
    private readonly Label sendOutputDevicesStatusLabel = new() { AutoSize = true, Text = "No output device selected." };
    private readonly CheckedListBox sendInputDevicesList = new() { CheckOnClick = true, Width = 430, Height = 90 };
    private readonly Label sendInputDevicesStatusLabel = new() { AutoSize = true, Text = "No input device selected." };
    // Per-application WASAPI send (issue #20). The WASAPI send side captures EITHER whole output
    // devices (sendOutputDevicesList above — the classic behaviour) OR the audio of specific running
    // applications, chosen by sendModeList on the Input/Output tab right after the "Send my audio"
    // checkbox. Windows 10 build 19041+ only: on older Windows the chooser row is hidden and the mode
    // is forced to "devices". The app list is tracked by process NAME (so a selection survives an app
    // restart) and reconciled on sendAppsReconcileTimer so apps dropping in and out don't pile up.
    // Unlike the device lists this selection IS persisted — per profile (WasapiSendMode /
    // SelectedSendApplications) — matching how Ed wants a profile to remember its whole send setup.
    // There is deliberately NO "send all applications" option here (Ed removed it 2026-07-16): picking
    // the applications mode means picking specific apps; whole-system audio is what devices mode is for.
    // (The SERVICE still has its own send-all concept — a headless lock-screen sender wants system audio —
    // so Profile.SendAllApplications stays for ServiceProfileDialog/ServiceSendHost.)
    private readonly ListBox sendModeList = new() { Width = 430, Height = 38, IntegralHeight = false };
    private MnemonicLabel? sendModeLabel;
    // "Currently active applications" — the apps running right now (like Discovered peers), PLUS any
    // ticked app that isn't running (shown "(not running)") so the user can always find and untick it.
    private readonly CheckedListBox sendAppsList = new() { CheckOnClick = true, Width = 430, Height = 90 };
    private readonly Label sendAppsStatusLabel = new() { AutoSize = true, Text = "No application selected." };
    private MnemonicLabel? sendAppsLabel;
    // "Remembered applications" — the GLOBAL remembered-apps address book (like Remembered peers). Tick one
    // here and it's marked to send; the instant it goes live it also appears (ticked) in the active list.
    private readonly CheckedListBox rememberedAppsList = new() { CheckOnClick = true, Width = 430, Height = 90 };
    private readonly Label rememberedAppsStatusLabel = new() { AutoSize = true, Text = "No remembered application." };
    private MnemonicLabel? rememberedAppsLabel;
    // The authoritative set of app process-names to send — ticked in EITHER list. Both lists render their
    // checkboxes from this and toggling either updates it. Saved per-profile as SelectedSendApplications.
    private readonly HashSet<string> selectedSendApps = new(StringComparer.OrdinalIgnoreCase);
    private System.Windows.Forms.Timer? sendAppsReconcileTimer;
    // Instant capture-on-start: fires the moment a ticked app opens an audio session, so we begin
    // capturing it right at the start (the poll below is only a backstop). Live only while sending
    // specific apps. lastSendAppPidSignature debounces the re-apply so we only reconfigure the engine
    // when the ticked apps' actual process ids change (an app opened or closed).
    private RemSound.Sender.AudioSessionStartWatcher? sessionStartWatcher;
    private string? lastSendAppPidSignature;
    // Guards the sendModeList / sendAppsList handlers while we
    // programmatically repopulate them (mode switch, profile apply, reconcile) so those handlers
    // don't fire MarkProfileDirty or trigger re-entrant rebuilds on our own writes.
    private bool suppressSendAppEvents;
    // The two sendModeList rows, in order. Index 0 = whole audio devices (classic), 1 = applications.
    private const int SendModeDevicesIndex = 0;
    private const int SendModeApplicationsIndex = 1;
    // ASIO-side lists. Always present in the form but hidden when ASIO is disabled. The two
    // lists are independent of the WASAPI ones — the user can tick any combination across all
    // five lists. Sender mixes WASAPI capture + ASIO capture into one outgoing stream;
    // receiver fans rendered audio to WASAPI outputs + ASIO outputs in parallel. This lets
    // someone use a WASAPI mic and an ASIO instrument input together, or send out to a WASAPI
    // headset alongside ASIO studio monitors.
    private readonly CheckedListBox asioSendDevicesList = new() { CheckOnClick = true, Width = 430, Height = 90 };
    private readonly Label asioSendDevicesStatusLabel = new() { AutoSize = true, Text = "No ASIO send channel selected." };
    private readonly CheckedListBox asioReceiveOutputDevicesList = new() { CheckOnClick = true, Width = 430, Height = 90 };
    private readonly Label asioReceiveOutputDevicesStatusLabel = new() { AutoSize = true, Text = "No ASIO receive channel selected." };
    // One-press "clear every device tick" — sits above the ASIO driver picker on the I/O tab.
    // The device lists can get long; this is the quick reset when you've lost track of what's on.
    private readonly Button uncheckAllDevicesButton = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    // Labels paired with the ASIO lists; held as fields so the layout can show/hide them as a
    // unit when the user toggles "Enable ASIO".
    private MnemonicLabel? asioSendDevicesLabel;
    private MnemonicLabel? asioReceiveOutputDevicesLabel;
    // Mnemonic label for the driver picker, held as a field so we can show/hide it together
    // with the driver listbox when the audio mode changes. Created in BuildAudioIOTab only
    // when there is at least one ASIO driver installed; null on machines with no ASIO drivers
    // (the driver picker is omitted entirely in that case).
    private MnemonicLabel? asioDriverLabel;
    // Tabbed UI scaffolding — 2026-05-06 refactor. The form's content panel is now a TabControl
    // with four logical sections; status (healthLabel/statusLabel) sits in a footer below the
    // tabs so the user always sees connection health regardless of which tab is active.
    //
    // Navigation (the standard Windows / NVDA-friendly pattern):
    //   * Arrow Left/Right when the tab strip has focus → cycle tabs (NVDA announces each).
    //   * Ctrl+Tab / Ctrl+Shift+Tab from anywhere on the form → cycle tabs.
    //   * Tab key from the strip → focus enters the active page's first control.
    //   * Tab past the last page control → focus moves to the status footer / form chrome.
    //
    // The TabControl is TabIndex=0 + TabStop=true so a fresh Tab from the form's chrome
    // lands on the strip first. We deliberately do NOT auto-focus a control inside the
    // active page on SelectedIndexChanged — that competes with arrow-key navigation (every
    // arrow press would yank focus off the strip into a page control, and the next arrow
    // would go to that control instead of cycling the next tab). Ed reported "bounces
    // about" with the previous always-auto-focus design; removed the handler.
    //
    // Alt+letter shortcuts are gated per-tab inside ProcessCmdKey so a shortcut never
    // auto-jumps the user across tabs.
    // TabControl + TabPage accessibility: deliberately default everything (no AccessibleName,
    // no AccessibleRole, no SelectedIndexChanged hook). Andre's working accessible-readout
    // app uses just `new TabPage(text)` and that's it — NVDA reads the active tab name
    // correctly via the framework's built-in MSAA exposure. Past attempts to "improve" this
    // (custom AccessibleName, dynamic sync on tab change, AccessibleRole.None) all made it
    // worse: extra "main sections", "tab control" double-reads, "pane" prefixes. The
    // standard pattern wins. 2026-05-06.
    // TabControl accessibility: the "tab control" prefix Ed kept hearing is from .NET 10
    // WinForms' UIA exposure — it deliberately reports TabControl as a Tab control type
    // with TabItem children, and NVDA announces both. Microsoft removed the opt-out
    // (Switch.UseLegacyAccessibilityFeatures) for .NET Core / 5+ / 10. Andre's app reads
    // cleanly because it's .NET Framework 4.x where the older WinForms accessibility
    // implementation exposes less detail.
    //
    // QuietTabControl below is a Hail Mary: subclass TabControl, override its
    // AccessibleObject to return a non-Tab role so NVDA reads less context. Risk:
    // dotnet/winforms#11831 throws InvalidOperationException on .NET 8/9 when overriding
    // CreateAccessibilityInstance — may or may not be fixed in .NET 10. If it throws at
    // runtime, fall back to the bare TabControl and accept the announcement.
    private readonly TabControl mainTabControl = new QuietTabControl { Dock = DockStyle.Fill };
    private readonly TabPage connectivityTabPage = new("Connectivity");
    private readonly TabPage audioIOTabPage = new("Audio inputs and outputs");
    private readonly TabPage audioProfileTabPage = new("Audio profile");

    // === Volume, pan and EQ for peers tab — shown only when AppConfig.ShowPanEqTab is on. ===
    private readonly TabPage panEqTabPage = new("Volume, pan and EQ for peers");
    private readonly AccessibleCheckBox enableAllPeerShapingBox = new() { Text = "Enable volume, pan and &EQ for all peers (Alt+E)", AccessibleName = "Enable volume, pan and EQ for all peers", AutoSize = true };
    // A checklist: ticking a peer applies your shaping to them (a per-peer bypass), and the peer the
    // cursor is on is the one the controls below edit.
    private readonly CheckedListBox panEqPeerList = new() { Width = 430, Height = 90, IntegralHeight = false, AccessibleName = "Peers (Alt+U)" };
    private readonly TrackBar volumeSlider = new() { Minimum = 0, Maximum = 100, Value = 100, SmallChange = 1, LargeChange = 10, TickFrequency = 25, Width = 320 };
    private readonly TrackBar panSlider = new() { Minimum = 0, Maximum = 100, Value = 50, SmallChange = 1, LargeChange = 10, TickFrequency = 25, Width = 320 };
    private readonly Button resetPeerEqButton = new() { Text = "Set peer E&Q to default (Alt+Q)", AutoSize = true, AccessibleName = "Set peer EQ to default" };
    private readonly ListBox eqModeList = new() { Width = 320, Height = 58, IntegralHeight = false, AccessibleName = "EQ mode" };
    private readonly FlowLayoutPanel eqBandsPanel = new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
    // Parametric-mode controls (built into eqBandsPanel when the 16-band mode is active).
    private readonly Button addBandButton = new() { Text = "&Add band (Alt+A)", AutoSize = true, AccessibleName = "Add band" };
    private readonly Button deleteBandButton = new() { Text = "&Delete band (Alt+D)", AutoSize = true, AccessibleName = "Delete band" };
    private readonly ListBox parametricBandList = new() { Width = 430, Height = 150, IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended, AccessibleName = "Bands (Alt+B), left and right arrow change the selected band's gain" };
    // Purely-visual EQ response graph (invisible to NVDA). See EqCurveControl.
    private readonly EqCurveControl eqCurve = new() { Width = 430, Height = 110, Margin = new Padding(0, 8, 0, 0) };
    // Working copy of the active profile's per-peer shaping, keyed by peer address string. Loaded on
    // profile apply, saved by BuildCurrentProfile, mutated live as the user moves the controls.
    private Dictionary<string, PeerShaping> peerShaping = new();
    // Address string of the peer currently selected in panEqPeerList (the one the controls edit).
    private string? selectedShapingKey;
    // The band-gain sliders currently shown; rebuilt when the mode or selected peer changes.
    private readonly List<TrackBar> eqBandSliders = new();
    // True while we're pushing a peer's saved values INTO the controls, so those programmatic changes
    // don't fire the apply/dirty handlers back at us.
    private bool loadingPanEqControls;
    // Signature of the peer list last rendered, so the 1 Hz refresh only rebuilds it on a real change.
    private string lastPanEqPeerSignature = "";
    // profilesPrefsTabPage retired 2026-05-08 — its contents now live on the File menu.

    // connectivityTransportButton + ShowConnectivityTransportDialog removed in Phase 2/3 of
    // the 2026-05-06 UI refactor. Connectivity and audio-profile controls now live inline on
    // their respective tabs; there's nothing to bridge to.
    // 2026-05-11 audio-mode listbox retired. The mode is now derived from the ASIO driver
    // picker below: "(none)" → WasapiOnly, any driver → BothIndependent. The classic mixed-Both
    // and AsioOnly modes are no longer reachable from the UI; their enum values survive in
    // RemSound.Core.AudioMode for backward-compat deserialisation of old profile JSONs only.
    // ListBox (not ComboBox) so the user can arrow up/down to change drivers without having to
    // click or open a dropdown. Selecting a row immediately fires SelectedIndexChanged, which
    // re-applies the backend and refreshes the channel-pair lists below. Both Andre (Komplete
    // Audio) and Ed got confused by the combo's open/close interaction; a plain list with
    // sticky selection is unambiguous for sighted users and screen-reader users alike.
    // First item is always the "(none)" sentinel (NoAsioDriverSentinel below) — selecting it
    // means "no ASIO driver, run WASAPI-only". Real driver names follow.
    private readonly ListBox asioDriverBox = new() { Width = 280, Height = 80, IntegralHeight = false };
    /// <summary>Visible label of the "no ASIO driver" sentinel row in <see cref="asioDriverBox"/>.
    /// Equality against this string is how the code distinguishes "user has chosen WASAPI-only"
    /// from "user has selected a real driver". Kept as a constant so the visible text and the
    /// equality check can never drift apart.</summary>
    private const string NoAsioDriverSentinel = "(none)";
    /// <summary>True when at least one ASIO driver was detected at startup. Set once in the
    /// constructor; <see cref="BuildAudioIOTab"/> reads it to decide whether to render the
    /// driver picker at all. On a machine with no ASIO drivers installed the picker (and its
    /// "Driver (Alt+D):" label) are omitted entirely — there is nothing to switch to.</summary>
    private bool hasAnyAsioDriverInstalled;
    // Profile-management buttons retired 2026-05-08 — these actions live in File menu now.
    // The methods (SaveProfileAs / UpdateExistingProfile) are still here; they're called from
    // the menu item Click handlers in BuildFileMenu.
    // A colour cue for connection health (green streaming / amber idle / grey disconnected) beside the
    // health text. Purely visual — invisible to NVDA; the health text is unchanged.
    private readonly StatusDot healthDot = new();
    private readonly Label healthLabel = new() { Text = "Health: disconnected", AutoSize = true };
    private readonly Label statusLabel = new() { Text = "Disconnected", AutoSize = true };

    // --- Audio profile tab controls (Phase 2 refactor: these were previously in the
    // Connectivity & transport dialog as "dialog*" mirrors of hidden form-fields. Now they
    // are the canonical UI live on the Audio profile tab, no mirrors required.) ---
    private readonly ComboBox codecBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360, AccessibleName = "Audio codec (Alt+C)" };
    private readonly ListBox sendRateBox = new() { Width = 240, Height = 40, IntegralHeight = false, AccessibleName = "Packet size (Alt+P)" };
    // Min 1 ms is intentionally aggressive — for LAN/localhost users who want to push it.
    // Values below ~10 ms cause audible crackling on any network with real jitter.
    private readonly NumericUpDown maxLatencyBox = new() { Minimum = 1, Maximum = 500, Increment = 1, Value = 80, Width = 90, AccessibleName = "Audio latency in milliseconds (Alt+L)" };
    // One-shot "Tune latency for best sound" button retired — continuous auto-tune covers
    // the same job, and the manual button confused users by sitting next to the auto-tune
    // checkbox doing almost the same thing in a less convenient one-shot shape.
    private readonly AccessibleCheckBox continuousTuneBox = new() { Text = "Continuous auto-tune latency", AutoSize = true };
    private readonly ComboBox continuousIntervalBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90, AccessibleName = "Auto-tune latency interval (Alt+I)" };
    // Label for continuousIntervalBox. Held as a field (rather than a local in
    // BuildAudioReceiveGroupContents) so UpdateBothIndependentVisibility can rewrite the
    // text and mnemonic when the user flips audio mode — the interval governs both lanes'
    // auto-tune ticks in BothIndependent, and the label needs to say so. Initialised in
    // BuildAudioReceiveGroupContents alongside the other receive-side controls; visibility
    // is shared with the WASAPI row (always shown when the row is shown).
    private Label? continuousIntervalLabel;
    // BothIndependent-mode companion controls. Created up front so SelectedIndexChanged
    // handlers can be wired alongside the originals; they live in their own TableLayoutPanel
    // row that toggles Visible=true only when the audio mode is BothIndependent. The labels
    // and mnemonics on the *existing* controls are re-written at mode-switch time so they
    // become the WASAPI-lane controls (Alt+W / Alt+Y) and these new ASIO controls take over
    // the simpler Alt+L / Alt+T mnemonics — ASIO is the "headline" lane in the new mode
    // (the reason a user picked it) so it gets the more memorable shortcuts.
    private readonly NumericUpDown maxLatencyAsioBox = new() { Minimum = 1, Maximum = 500, Increment = 1, Value = 10, Width = 90, AccessibleName = "ASIO latency in milliseconds (Alt+I)" };
    private readonly AccessibleCheckBox continuousTuneAsioBox = new() { Text = "Continuous auto-tune ASIO latency", AutoSize = true };
    private readonly ListBox smoothnessBox = new() { Width = 420, Height = 200, IntegralHeight = false, AccessibleName = "Buffer smoothness (Alt+B)" };
    private readonly ListBox artefactBox = new() { Width = 420, Height = 60, IntegralHeight = false, AccessibleName = "Artefact sound type (Alt+A) — controls how audio gaps sound" };
    // Priority mode (per profile). Sits as the first control on the Audio profile tab,
    // ungrouped above the two GroupBoxes, so it's the first thing focus lands on when the
    // user Tabs into the tab. Toggling marks the profile dirty (the setting lives in
    // Profile, not AppConfig) and flips every PerformanceMode lever in one shot — CPU
    // scheduling, Windows power management, memory priority, working-set lock, and
    // MMCSS thread priority. Label deliberately mentions both "CPU" and "Windows
    // performance settings" because the toggle reaches well past just CPU scheduling.
    private readonly AccessibleCheckBox priorityModeBox = new()
    {
        Text = "&Use CPU and Windows performance settings in high priority mode (Alt+U)",
        AccessibleName = "Use CPU and Windows performance settings in high priority mode",
        AutoSize = true,
    };

    // --- Connectivity tab controls (Phase 2 refactor) ---
    private readonly LiveCheckedListBox connectedPeersList = new() { CheckOnClick = true, Width = 430, Height = 90, AccessibleName = "Connected peers (Alt+C)" };
    private readonly Label connectedPeersStatus = new() { AutoSize = true, Text = "No peer connected." };
    private readonly CheckedListBox discoveredPeersList = new() { CheckOnClick = true, Width = 430, Height = 90, AccessibleName = "Discovered peers (Alt+D)" };
    private readonly Label discoveredPeersStatus = new() { AutoSize = true, Text = "No peer discovered." };
    private readonly CheckedListBox rememberedPeersList = new() { CheckOnClick = true, Width = 430, Height = 90, AccessibleName = "Remembered peers (Alt+R)" };
    private readonly Label rememberedPeersStatus = new() { AutoSize = true, Text = "No remembered peer selected." };
    private readonly Button manualAddButton = new() { Text = "Add peer by IP (Alt+&A)", AutoSize = true, AccessibleName = "Add peer by IP" };
    // Read-only details for the peer highlighted in the connected list, and a button to give it a name.
    private readonly TextBox peerDetailsBox = new() { Multiline = true, ReadOnly = true, Width = 430, Height = 132, ScrollBars = ScrollBars.Vertical, AccessibleName = "Peer details (Alt+E)" };
    private readonly Button renamePeerButton = new() { Text = "Rena&me peer (Alt+M)", AutoSize = true, AccessibleName = "Rename peer" };
    // The Discovered / Remembered row labels, captured so those rows can be hidden when the user turns
    // the lists off on the Preferences Appearance tab.
    private MnemonicLabel? discoveredPeersLabel;
    private MnemonicLabel? rememberedPeersLabel;
    // Per-profile "pin to exact addresses" toggle (#17). When ticked, RefreshKnownPeers skips the
    // discovered-peer merge and the address-follow, so the profile's peers stay exactly as the user set.
    private readonly AccessibleCheckBox lockPeerAddressesBox = new()
    {
        Text = "Lock to these exact peer addresses, no matter what — never follow names or switch (Alt+&L)",
        AccessibleName = "Lock to these exact peer addresses, no matter what; never follow names or switch address",
        AutoSize = true,
    };
    // loggingBox + writeLogsNowButton field instances retired 2026-05-08 — both controls
    // now live inside PreferencesDialog. The form-level logFile.Enabled gate is set
    // directly from the settings store at startup (see ApplyLoggingEnabled).
    // Read-only multiline TextBox at the end of the Connectivity tab. Tab into it to read
    // a live snapshot of connection status (peers / pings / uptime / byte rates). Updates
    // every status-tick (1 Hz) but ONLY when the user is NOT focused on the box — that way
    // NVDA reads it once when the user lands, doesn't re-announce mid-read. Signature
    // short-circuit so the actual Text setter only fires when content changes (NVDA pattern
    // matches the peer-list refresh).
    private readonly TextBox statusReadout = new()
    {
        Multiline = true,
        ReadOnly = true,
        TabStop = true,
        Width = 460,
        Height = 110,
        BorderStyle = BorderStyle.FixedSingle,
        ScrollBars = ScrollBars.Vertical,
        AccessibleName = "Connection status (Alt+S)",
    };
    private string lastStatusReadoutText = string.Empty;
    // For computing byte-rate deltas. Sampled at each status tick; first tick has no
    // prior baseline so the rate shows as 0.
    private long lastStatusTxBytes;
    private long lastStatusRxBytes;
    private DateTime lastStatusSampleUtc = DateTime.MinValue;
    // Tracks when the FIRST healthy-peer transition happened in the current "connected"
    // span. Cleared when no peers are healthy. Used for the uptime line.
    private DateTime? statusConnectedSinceUtc;
    // Time of the last "speak status" hotkey press, for double-press-to-copy detection.
    private DateTime lastSpeakStatusPressUtc = DateTime.MinValue;
    // Process CPU-time at the last status sample, for the status box's CPU-usage line. Cached Process
    // handle so the once-a-second status tick doesn't allocate a new one each time.
    private TimeSpan lastStatusCpuTime;
    private readonly System.Diagnostics.Process statusSelfProcess = System.Diagnostics.Process.GetCurrentProcess();
    // Per-list state (used by sync helpers — was per-method in the old dialog).
    private bool suppressConnectedCheck;
    private bool suppressDiscoveredCheck;
    private bool suppressRememberedCheck;
    /// <summary>True while ANY checkable list is being (un)checked programmatically — a device-list
    /// refresh (<see cref="suppressDeviceCheckChange"/>) or a rebuild of one of the three peer lists
    /// (each fires ItemCheck for every pre-checked row it adds). The tick/untick CUE must stay silent
    /// for all of these, not just device-list changes: on startup the connected-peers list is focused
    /// (see the Shown handler's FocusListControl call), so a saved-peer reconnect rebuilding that list
    /// would otherwise click a checkbox sound at launch. Only a genuine user toggle — no flag set —
    /// should click.</summary>
    private bool SuppressingCheckSounds =>
        suppressDeviceCheckChange || suppressConnectedCheck || suppressDiscoveredCheck || suppressRememberedCheck;
    private string lastConnectedListSignature = string.Empty;
    private string lastDiscoveredListSignature = string.Empty;
    private string lastRememberedListSignature = string.Empty;
    // Local audio bind port. Was a user-editable spinner; removed from the UI on 2026-05-01.
    // Unified on 2026-05-05: receiver bind, LAN peer-to-peer dials, and the relay all use a
    // single canonical port (RemPacket.DefaultPort = 47830). New manual peers without an
    // explicit ":port" suffix default to that, so users never have to type a port for any
    // common case — Tailscale, LAN, or a relay server.
    private const int LocalAudioPort = RemPacket.DefaultPort;
    // The Enable-logs UI is in PreferencesDialog now. Runtime state is logFile.Enabled.

    // --- Continuous auto-tune state (mirror controls live in the dialog) ---
    private readonly System.Windows.Forms.Timer continuousTuneTimer = new();
    private readonly Queue<int> recentMaxGaps = new();
    // Last observed value of receiver.SessionsOpenedCount. When this number increases between
    // SNAP ticks, a new StreamSession has just opened — the recent-gap and render-callback
    // queues contain measurements taken before the new session started (potentially including
    // a multi-second cross-session arrival gap), so we flush them and bump
    // lastSourceChangeUtc to defer the next auto-tune tick. Without this, the auto-tune would
    // see the stale gap and recommend an absurd latency target that prevents the new session
    // from ever arming. See the matching diagnostics.ResetGapMeasurements() inside
    // AudioReceiver.HandleFormat. 2026-05-11 fix.
    private long lastObservedSessionsOpenedCount;
    // Parallel rolling window of measured render-callback gaps. Auto-tune previously assumed a
    // hardcoded 10ms render period (sized for shared-mode WASAPI), which over-estimated the
    // recommendation by 8ms+ on ASIO with small buffers (real callback period ~1ms). Tracking
    // the actual measurement lets the formula reflect reality. Same window length as the gap
    // queue so they share the lookback discipline.
    private readonly Queue<int> recentRenderCbGaps = new();
    private const int RecentMaxGapWindowSeconds = 60;
    private DateTime lastUserSliderMoveUtc = DateTime.MinValue;
    private bool suppressUserSliderMoveTracking; // true while continuous tune is changing the slider
    private bool continuousTuneEnabled;
    private int continuousTuneIntervalSec = 5;
    private long lastObservedUnderrunCount;
    private HeartbeatService? heartbeatService;
    // Tracks whether each peer was last considered CONNECTED, for the connect/disconnect cues.
    // "Connected" now means audio is actually flowing OR the heartbeat is healthy — not the
    // heartbeat alone (see DetectAndAnnouncePeerHealthTransitions). The bool, rather than the
    // raw health state, gives natural hysteresis: once connected we stay connected until audio
    // genuinely stops AND the heartbeat goes unreachable, so a heartbeat blip while audio keeps
    // playing never fires a false disconnect cue. 2026-05-31 rewrite.
    private readonly Dictionary<string, bool> peerConnectedState = new(StringComparer.OrdinalIgnoreCase);
    private CuePlayer? connectSound;
    private CuePlayer? disconnectSound;
    // Recording start/stop cues. Played via SoundPlayer to the default Windows output —
    // same path as connect/disconnect. They don't pass through our recording taps (those
    // sit on the internal sender mix bus and receiver render path), so they don't appear
    // in normal recordings. A user who has a WASAPI loopback of the same output device as
    // a capture source would still get them, but that's their loopback configuration, not
    // anything the recorder is doing.
    private CuePlayer? recordStartSound;
    private CuePlayer? recordStopSound;
    // Profile-save and profile-switch cues, added 2026-05-28 alongside the move of all
    // default WAVs into a sounds\ subfolder. Save fires after a successful File → Save /
    // Save As; Profile fires immediately after a profile finishes loading in MainForm.
    private CuePlayer? saveSound;
    private CuePlayer? profileSwitchSound;
    private CuePlayer? profileMenuOpenSound;
    private CuePlayer? updateSound;
    // Machine-wide cues (2026-06-13): send/receive toggled on/off, and minimise(hide)/restore(show).
    private CuePlayer? sendOnSound;
    private CuePlayer? sendOffSound;
    private CuePlayer? receiveOnSound;
    private CuePlayer? receiveOffSound;
    private CuePlayer? hideSound;
    private CuePlayer? showSound;
    // Labels for the three send/receive device lists, captured at layout time so they can be
    // re-titled when the user toggles between WASAPI mode (Windows devices) and ASIO mode
    // (driver channel pairs). null until BuildLayout has run.
    private MnemonicLabel? sendOutputDevicesLabel;
    private MnemonicLabel? sendInputDevicesLabel;
    private MnemonicLabel? receiveOutputDevicesLabel;
    // Set when the user ticks/unticks a source. Auto-tune skips for one interval afterward so the
    // brief settling jitter on a newly-added capture doesn't bias the recommendation upward.
    private DateTime lastSourceChangeUtc = DateTime.MinValue;


    private readonly Dictionary<CheckedListBox, int> lastFocusedListIndices = [];

    private readonly System.Windows.Forms.Timer statusTimer = new() { Interval = 1000 };
    // Periodic silent auto-save of the current profile (Preferences → General → "auto save non-read only
    // profiles"). Off by default; when enabled it fires every N minutes and saves the active profile only
    // if it's a real saved profile, NOT read-only, and has unsaved changes — WITHOUT the save cue. Interval
    // and enable/disable come from AppConfig.AutoSaveNonReadOnlyMinutes via ApplyAutoSaveTimer().
    private readonly System.Windows.Forms.Timer autoSaveTimer = new();
    // Device-list refresh. As of v3.4 this is EVENT-DRIVEN, not polled: an
    // AudioDeviceChangeNotifier registers for Windows audio endpoint-change notifications and
    // pokes this timer when the device set actually changes (USB hot-plug / unplug, default-device
    // change). The timer then acts as a one-shot DEBOUNCE — a burst of add/remove/default-changed
    // callbacks collapses into a single RefreshAudioDeviceLists ~750 ms after the last one, so the
    // listboxes (and NVDA) are only touched when something truly changed, and zero work happens
    // while nothing is being plugged or unplugged. If notification registration ever fails we fall
    // back to the pre-v3.4 periodic poll (deviceRefreshOneShot = false, 3 s). This replaces the old
    // 3-second poll that re-enumerated every WASAPI device — and re-opened the ASIO driver — on
    // every tick regardless of whether anything had changed.
    private readonly System.Windows.Forms.Timer deviceRefreshTimer = new() { Interval = 750 };
    // True when deviceRefreshTimer is a one-shot debounce (notification-driven — the normal case);
    // false when it's the periodic-poll fallback. Controls whether the Tick handler stops the timer.
    private bool deviceRefreshOneShot = true;
    // Windows audio endpoint-change notifier — drives the debounced device-list refresh. Null until
    // wired in the constructor; disposed in FormClosing (which unregisters the COM callback).
    private AudioDeviceChangeNotifier? deviceChangeNotifier;

    // The "Use Windows default ..." follower entries sit at the top of the receive-output and
    // send-input lists. Ticked, the current Windows default device is resolved live (and re-resolved
    // when Windows' default changes). A synthetic sentinel DeviceId that can never collide
    // with a real endpoint id (which looks like "{0.0.0.00000000}.{guid}").
    private static readonly AudioDeviceChoice DefaultOutputFollower =
        new("Use Windows default audio device, follows Windows changes", "__use-default-output__", CaptureKind.Loopback) { IsDefaultFollower = true };
    private static readonly AudioDeviceChoice DefaultInputFollower =
        new("Use Windows default audio device, follows Windows changes", "__use-default-input__", CaptureKind.Input) { IsDefaultFollower = true };
    // "Use Windows default" for the SEND side's "WASAPI audio outputs to send" (system-audio/loopback)
    // list: loopback-capture whatever Windows currently uses as the default OUTPUT and follow it. Its own
    // sentinel + persisted flag, distinct from the receive-output follower (which plays TO the default).
    // The loopback-send follower is shared with the service (AudioDefaultFollower) so both offer and
    // resolve the exact same sentinel; the receive-output and send-input followers above stay local.
    private static readonly AudioDeviceChoice DefaultLoopbackSendFollower = AudioDefaultFollower.LoopbackSendChoice();
    // The Windows-default device id we last routed to while following, per direction. A default-device
    // change doesn't change the device SET, so the list-sync wouldn't catch it — we compare against
    // these to spot it and re-route (see ReapplyIfFollowedDefaultChanged).
    private string? lastFollowedDefaultOutputId;
    private string? lastFollowedDefaultInputId;
    private string? lastFollowedDefaultLoopbackId;

    // Receive-output device IDs the user/profile selected — kept even while a device is unplugged,
    // so a card that returns is silently re-ticked and re-opened (issue #5: recover after USB
    // unplug). Receive-only: the send lists deliberately don't persist selection (AudioDeviceCatalog).
    private readonly HashSet<string> rememberedReceiveOutputIds = new(StringComparer.OrdinalIgnoreCase);
    // Debounce timer for ASIO driver listbox selection. See SelectedIndexChanged handler
    // wiring for the full rationale. 300 ms is long enough to coalesce arrow-key bursts
    // (NVDA users typically press a few keys in quick succession to scan through items),
    // short enough that a deliberate selection feels responsive. Auto-stop on Tick.
    private readonly System.Windows.Forms.Timer asioDriverChangeDebounce = new() { Interval = 300 };

    // Session memory of which ASIO channel pairs were ticked, PER DRIVER NAME (send + receive).
    // Ticks are deliberately cleared on a driver swap — pair N is a different physical channel on a
    // different card, so raw ticks must never survive the swap (see the clear in the debounce handler).
    // But clearing alone meant switching AWAY and BACK left you silent until you re-ticked by hand
    // (Ed, 2026-07-26: EVO → ReaRoute → EVO = no audio). This map restores each driver's OWN ticks
    // when you return to it, so audio resumes by itself — safety and convenience both.
    private readonly Dictionary<string, (int[] Send, int[] Recv)> asioTicksByDriver = new(StringComparer.OrdinalIgnoreCase);
    private string sendOutputDevicesSignature = string.Empty;
    private string sendInputDevicesSignature = string.Empty;
    private string receiveOutputDevicesSignature = string.Empty;
    private string asioSendDevicesSignature = string.Empty;
    private string asioReceiveOutputDevicesSignature = string.Empty;
    private string? cachedAsioProbeDriverName;
    private AsioDriverProbeResult? cachedAsioProbeResult;
    private bool cachedAsioProbeFailed;
    // ASIO drivers RemSound must never touch (e.g. the handle-leaking Realtek ASIO driver),
    // mirrored from AppConfig.DisabledAsioDrivers at startup so the device refresh can check
    // without disk I/O. Updated when the user disables/enables via the warning or Options menu.
    private readonly HashSet<string> disabledAsioDrivers = new(StringComparer.OrdinalIgnoreCase);
    // Realtek ASIO drivers found installed at startup (name contains "Realtek"). Drives the
    // one-time compatibility warning and the Options-menu enable/disable toggle.
    private List<string> realtekAsioDriverNames = new();
    private ToolStripMenuItem? realtekAsioToggleItem;
    // True while we're rebuilding a CheckedListBox programmatically — suppresses the per-item
    // ItemCheck handler so re-adding pre-checked items doesn't fire ApplyAudioRuntime per item.
    private bool suppressDeviceCheckChange;
    private bool connected;
    private DateTime connectedSinceUtc = DateTime.MinValue;
    private DateTime lastSnapshotUtc = DateTime.MinValue;
    private DateTime lastCaptureZeroLogUtc = DateTime.MinValue;

    // Full set of selected send endpoints (one per resolved peer address). The heartbeat pings
    // ALL of these; the audio sender is armed with the subset that isn't long-unreachable — see
    // RefreshAudioReceivers. Stored so the per-tick re-filter doesn't re-resolve addresses.
    private IPEndPoint[] allSendEndpoints = [];
    // Cached "ip:port|ip:port" signature of the endpoints currently armed for AUDIO, so the
    // per-tick refresh only calls SetReceivers when the armed set actually changes. null forces
    // a re-push (set when the selected-peer set changes).
    private string? activeAudioReceiverSignature;
    // How long an endpoint must be continuously unreachable before we stop sending the audio
    // stream to it. Well beyond the heartbeat's 5s UnreachableWindow so a transient blip never
    // interrupts audio to a healthy peer. The endpoint stays in the heartbeat's tracked set, so
    // when it recovers it's automatically re-armed. Stops the "peer hostname resolves to a live
    // LAN IP plus a dead Tailscale IP, so we upload the whole stream twice" waste.
    private static readonly TimeSpan AudioPruneUnreachableAfter = TimeSpan.FromSeconds(30);
    private bool firstCaptureCallbackLogged;
    private bool firstSenderPacketLogged;
    private bool firstReceiverPacketLogged;

    // Counter for SnapshotLogIfDue's periodic native-memory reaper. Increments once per
    // snapshot tick (~1 Hz) and triggers a forced gen2 + finalizer flush every 300 ticks
    // (~5 minutes). See the inline comment in SnapshotLogIfDue for the full rationale.
    private int nativeReaperTickCount;

    // Previous-tick values for the per-second deltas surfaced in the diag log line. Each is
    // the receiver-side cumulative counter snapshot at the previous SnapshotLogIfDue tick;
    // subtracting from the current value gives "how many fired this second". Only read when
    // DiagnosticsGate.Enabled (i.e. logs on); otherwise SnapshotLogIfDue early-outs before
    // touching these.
    // prevDiagDriftDrops / prevDiagDriftReps removed 2026-05-23. Drift drop/repeat counters
    // were dead since the Phase-4 fixed-ratio resampler design (always zero); diag columns
    // are gone too.
    private long prevDiagConceal;
    private long prevDiagShortRead;
    private long prevDiagDeviceGulp;
    private long prevDiagTrimFires;
    // Wire-level packet-sequence tracking deltas. Detects packet reordering, loss, or
    // duplication on the UDP path between sender and receiver. On a healthy LAN all three
    // failure counters should stay at zero; any non-zero delta in the diag log is a smoking
    // gun for transport-layer-induced pops.
    private long prevDiagWireInOrder;
    private long prevDiagWireMissed;
    private long prevDiagWireReordered;
    private long prevDiagWireDuplicated;
    // Per-second delta for the sender's hard-clamp clipping counter. A non-zero clipΔ means
    // the mix bus was producing samples whose magnitude exceeded 1.0 and got clamped. Clipping
    // itself doesn't create steps but is a signal that the input is hot enough that something
    // could be saturating.
    private long prevDiagClippedSamples;
    // Per-second GC delta. .NET tracks cumulative collection counts per generation; we
    // remember the previous tick's values and emit gen-0 / gen-1 / gen-2 deltas in the diag
    // log so a click-event correlation analysis can spot when a GC pause coincided with a
    // receive-side arrival-gap spike. Gen-2 in particular implies a multi-millisecond stall
    // that's a plausible click source. 2026-05-21.
    private int prevDiagGc0Count;
    private int prevDiagGc1Count;
    private int prevDiagGc2Count;
    // Per-process CPU% / memory / allocation / GC meter — drained once per second by the
    // diag emitter. New 2026-05-22, item 1 + 3 of RemSoundefficiency.md. Carries no cost
    // when logs are off because the diag emitter is itself gated.
    private readonly ProcessSelfMeter processSelfMeter = new();

    // Profile system (2026-05-02). The active profile (if any) was selected at app start and
    // populated `settings` with its values BEFORE the constructor body runs (see ApplyProfile
    // below). Control-level state (device ticks, send/receive checkboxes, audio port, volume
    // slider, ticked peers) is applied later in OnShown via ApplyPendingProfileToControls()
    // because the device lists aren't populated until then. NextProfileTitleToLoad is read by
    // Program.cs after the form closes; non-null means "user clicked Switch in Manage profiles —
    // re-launch the form under that profile."
    private ProfileStore? profileStore;
    // Headless/test construction: when true the constructor builds the full window (every tab, control
    // and menu) but SKIPS the calls that touch the OS — registering global hotkeys, starting the status /
    // device-refresh timers, the device-change notifier, and the audio-backend mode switch. The
    // disruptive startup work (Connect + sockets, UPnP, update check) already lives in the Shown handler,
    // which never fires when a test constructs the form without showing it. Defaults false, so the real
    // app's startup path is byte-for-byte unchanged. Lets the self-test audit the whole main window.
    private readonly bool headless;
    private string? currentProfileTitle;
    // True when the active profile has its ReadOnly flag set. Drives three behaviours:
    //   * The window title gets a " (read-only)" suffix so NVDA / sighted users see
    //     immediately that changes won't persist.
    //   * Ctrl+S / File → Save politely refuses (with a "use Save As instead" message).
    //   * OnFormClosing skips the unsaved-changes prompt entirely — that's the whole
    //     point of read-only mode, so a profile you live in and toggle send/receive
    //     on doesn't block shutdown with a dialog you can't reach (NVDA crashed, remote
    //     session dropped, machine hibernating).
    // 2026-05-22 — Andre's request: he toggles send/receive on his default profile and
    // it shouldn't block shutdown when his screen reader can't reach the dirty-prompt.
    // Toggled via File → Lock profile (read-only) and persisted on the profile JSON.
    private bool currentProfileReadOnly;
    // The active profile's encryption password, in PLAIN text (the profile JSON stores it
    // lightly scrambled — see RemSoundCrypto.Obfuscate). "" = no password set. Two peers can
    // exchange audio only when their profile passwords match. Set from the loaded profile in
    // the constructor, changed via File → Change this profile's password, and carried back into
    // every save by BuildCurrentProfile. 2026-05-31 (always-on encryption, in development).
    private string currentProfilePassword = "";
    // The AES key + fingerprint derived from currentProfilePassword, cached so the slow key
    // derivation only runs when the password actually changes. Pushed down to the sender and
    // receiver by RecomputeAudioCrypto. Null when no password is set (then no audio flows).
    private byte[]? currentAudioKey;
    private byte[]? currentAudioFingerprint;
    private string? lastDerivedPassword;
    // Re-entrancy guard for the "you need a password to stream" gate, so programmatically
    // un-ticking the send/receive box (when the user cancels the password prompt) doesn't
    // re-fire the gate. And a record of which peers we've already warned about a password
    // mismatch / out-of-date version, so the warning shows once per change, not every second.
    private bool suppressStreamingPasswordGate;
    private readonly Dictionary<System.Net.IPAddress, PeerSecurityStatus> lastSecurityWarned = new();
    // The actual menu item — kept as a field so profile-load (or read-only toggle) can
    // sync .Checked without rebuilding the menu. CheckOnClick lets the menu item flip
    // itself on every click; the CheckedChanged handler reads the new value and runs
    // OnLockProfileToggled.
    private ToolStripMenuItem? lockProfileMenuItem;
    // Guards CheckedChanged on lockProfileMenuItem against the programmatic sync that
    // happens on profile-load — without it, loading a profile that's read-only would
    // re-fire the toggle handler and re-persist the flag pointlessly.
    private bool suppressLockProfileToggleHandler;
    /// <summary>Full filesystem path of the active profile's JSON file. Tracked separately
    /// from <see cref="currentProfileTitle"/> because Save As (2026-05-10) lets the user
    /// write a profile to an arbitrary path outside <see cref="ProfileStore.BaseDirectory"/>.
    /// Save / Rename operate on this path so they update / rename the file the user is
    /// actually editing — not whatever happens to be in BaseDirectory under the same name.
    /// Null on Blank template.</summary>
    private string? currentProfilePath;
    private Profile? pendingProfile;
    public string? NextProfileTitleToLoad { get; private set; }

    /// <summary>Set by File → New profile. Program.cs's relaunch loop checks this FIRST and, when
    /// true, rebuilds the form on a fresh blank template (<see cref="Profile.NewBlank"/>, no title)
    /// instead of loading a saved profile. It's the only way to reach a blank template mid-session
    /// — the fix for being unable to create a new profile when "start with a specific profile"
    /// boots the user straight past the picker (issue #6).</summary>
    public bool LoadBlankTemplateNext { get; private set; }
    /// <summary>Full path of the next profile to load, set when the user opens a file via
    /// File → Open profile. Program.cs prefers this over <see cref="NextProfileTitleToLoad"/>
    /// when non-null — it deserialises the JSON from this exact path, not from the active
    /// store's base directory. Lets Open profile work for files saved outside that folder.</summary>
    public string? NextProfilePathToLoad { get; private set; }
    // Set true by MarkProfileDirty() when the user actively changes something. Used as a
    // fast-path hint — we still do the JSON diff at close to be sure, but this lets us skip
    // the diff entirely when no user action has happened. Cleared on save and on profile load.
    private bool unsavedChanges;
    // Skip MarkProfileDirty calls while we're programmatically applying a loaded profile.
    private bool applyingProfile;
    // Set true the instant an auto-update hands off and we're about to exit to let the
    // installer restart us. While this is set, the close path skips EVERY prompt — the
    // update is a deliberate, unattended action and no dialog (least of all the unsaved-
    // changes prompt, whose default button is Cancel) may be allowed to abort the restart.
    // This was the second link in Andre's runaway: a stray Enter/Escape on the save prompt
    // cancelled the update's exit, so the new version never came up cleanly.
    private bool updatingInProgress;
    // Set once an install has actually begun (download + stage + helper launch). Guards against
    // firing the installer twice from a single process: the ~4 s startup check and the periodic
    // background poll can both surface the same release in quick succession, and without this
    // each would stage and spawn its own helper. Cross-PROCESS duplication is prevented by the
    // single-instance lock in Program.Main; this is the within-process half of that protection.
    private bool updateInstallStarted;
    /// <summary>Set when the user changed the profiles FOLDER (not just switched profile)
    /// via the Manage Profiles dialog. Program.cs reads this after the form closes; if true,
    /// it re-runs the entire profile selection flow under the new folder rather than the
    /// cheap "switch within current folder" path. Mutually exclusive with
    /// <see cref="NextProfileTitleToLoad"/> in practice.</summary>
    public bool ReloadFromScratch { get; private set; }

    /// <summary>Bring this window to the front, restoring it from the system tray if it's
    /// parked there. Called when the user launches a SECOND copy of RemSound and the single-
    /// instance guard chooses "switch to the running copy" — the second copy signals this one
    /// to surface instead of starting another process. Safe to call from any thread: it
    /// marshals to the UI thread itself. Reuses the tray controller's Restore (Show +
    /// SetForegroundWindow), which works whether the window is in the tray or just behind
    /// other windows.</summary>
    public void RestoreFromTray()
    {
        if (IsDisposed) return;
        try
        {
            if (InvokeRequired) { BeginInvoke(new Action(RestoreFromTray)); return; }
            trayController.Restore();
            // Land focus on a real, named control on whichever tab is showing, so NVDA announces
            // something when the window comes back from the tray. Without this, focus rests on the
            // QuietTabControl (deliberately role-less / nameless) and a screen reader has nothing to
            // read, so the window surfaces silently — the same issue the Preferences dialog had.
            // Deferred so it runs after the show/foreground settles.
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed) return;
                WinEventNotifier.AnnounceByFocusingLeaf(this, mainTabControl.SelectedTab, mainTabControl);
            }));
        }
        catch { /* best-effort — surfacing the window is a convenience, not load-critical */ }
    }

    public MainForm() : this(null, null, null, null) { }

    public MainForm(ProfileStore? profileStore, Profile? profile, string? loadedTitle, string? loadedPath = null, bool headless = false)
    {
        this.profileStore = profileStore;
        this.headless = headless;
        currentProfileTitle = loadedTitle;
        // Resolve the active profile's full path from whichever bit of info Program.cs
        // passed in. If a path was explicitly given (Open-from-arbitrary-folder flow),
        // honour it. Otherwise infer from the store's BaseDirectory + sanitised title.
        // Null when on Blank template (no file to track).
        if (!string.IsNullOrEmpty(loadedPath))
        {
            currentProfilePath = loadedPath;
        }
        else if (profileStore is not null && !string.IsNullOrEmpty(loadedTitle))
        {
            currentProfilePath = profileStore.PathFor(loadedTitle);
        }
        // Track the loaded profile in the machine-local recents list so the File → Recent
        // profiles submenu can offer it next time. Skipped for the blank-template case
        // (currentProfilePath stays null when no profile was loaded). 2026-05-15.
        if (!string.IsNullOrEmpty(currentProfilePath))
        {
            try
            {
                var cfg = AppConfig.Load();
                cfg.NoteRecentProfile(currentProfilePath);
                cfg.Save();
            }
            catch { /* benign — recents tracking is a convenience, not load-critical */ }
        }
        pendingProfile = profile;
        // Carry the profile's ReadOnly flag through to the in-memory tracking field. Blank
        // template (profile == null) implicitly starts as not-read-only; users still have
        // the menu toggle available if they want to lock the working state mid-session.
        currentProfileReadOnly = profile?.ReadOnly ?? false;
        // Unscramble the profile's stored password into the in-memory plain-text working value.
        currentProfilePassword = RemSoundCrypto.Deobfuscate(profile?.Password);
        // Push the profile's settings-shaped fields (codec, hotkeys, smoothness, etc.) into
        // the in-memory settings cache BEFORE the rest of the constructor body reads from it.
        // Control states (device ticks, checkboxes, volume) come later in OnShown.
        if (profile is not null) settings.ApplyProfile(profile);

        // BothModeWarningSuppressed migration removed 2026-05-11. The popup it suppressed
        // (the classic-Both ~45 ms latency warning) is gone with the audio-mode listbox, so
        // there's nothing to suppress any more. Old profile JSONs that still contain the
        // field deserialise with it ignored.

        Text = FormatWindowTitle(loadedTitle);
        Width = 640;
        Height = 600;
        MinimumSize = new Size(560, 520);
        StartPosition = FormStartPosition.CenterScreen;
        if (Theme.AppIcon is { } appIcon) Icon = appIcon;   // window title bar + Alt+Tab + taskbar
        // No AccessibleName / AccessibleRole on the form. Andre's accessible app does not
        // set these and NVDA reads cleanly there; setting them here was over-engineering.

        // Set the checkbox visible Text (with mnemonic) AND AccessibleName here in the
        // constructor body. The working RSound.old build used this two-step pattern; setting
        // these inline in the field initializer was enough to break NVDA state-change
        // announcements on toggle.
        // Explicit "(Alt+letter)" suffix on every shortcut-bearing label so both sighted users
        // and NVDA see/hear the shortcut consistently. The previous WinForms `&letter` mnemonic
        // auto-derivation was unreliable in our layout (FlowLayoutPanel-wrapped lists broke
        // the framework's label-to-control association heuristic). ProcessCmdKey handles every
        // activation explicitly. Keeping visible label and AccessibleName identical, per Ed's
        // "labels are one phrase used twice" rule.
        receiveAudioCheckbox.Text = "Receive audio (Alt+&R)";
        receiveAudioCheckbox.AccessibleName = "Receive audio";
        sendMyAudioCheckbox.Text = "Send my audio (Alt+&S)";
        sendMyAudioCheckbox.AccessibleName = "Send my audio";

        hotkeyController = new MainFormHotkeyController(
            settings,
            () => sendMyAudioCheckbox.Checked = !sendMyAudioCheckbox.Checked,
            () => receiveAudioCheckbox.Checked = !receiveAudioCheckbox.Checked,
            ToggleTrayFromHotkey,
            () => NudgeVolume(+5),
            () => NudgeVolume(-5),
            // Global Start / Stop recording. Same ToggleRecording path the Record menu item
            // and the in-app Ctrl+R use — the hotkey just makes it work without RemSound
            // having keyboard focus.
            ToggleRecording,
            // Three remote-control hotkeys: each one transmits a Control packet to all
            // currently-tracked peers via the audio sender's NAT pinhole. The receiving peer
            // applies the change locally if it has Profile.AcceptRemoteVolumeCommands on.
            // See SendRemoteControl for the dispatch detail.
            () => SendRemoteControl(RemoteControlKind.VolumeUp, +5),
            () => SendRemoteControl(RemoteControlKind.VolumeDown, -5),
            () => SendRemoteControl(RemoteControlKind.MuteToggle, 0),
            // Three Windows-global-volume hotkeys: each press makes connected peers nudge
            // their Windows default-output-device master volume by one OS-native step (~2%).
            // delta=0 — system commands ignore the delta byte, the per-press step size is
            // fixed by Windows. Hold the hotkey for bigger jumps.
            () => SendRemoteControl(RemoteControlKind.SystemVolumeUp, 0),
            () => SendRemoteControl(RemoteControlKind.SystemVolumeDown, 0),
            () => SendRemoteControl(RemoteControlKind.SystemMuteToggle, 0),
            ShowQuickProfileSwitch,
            // Speak the status line aloud through the active screen reader (issue #13). Screen-reader
            // specific; the global hotkey is unset by default (the user binds it in Keyboard shortcuts).
            SpeakStatusLine,
            // Toggle the "Enable volume, pan and EQ for all peers" master switch. Flipping .Checked
            // routes through the CheckedChanged handler (re-applies shaping) and, being an
            // AccessibleCheckBox, announces the new state to NVDA when the window is focused. Unset
            // by default; the user binds it in Keyboard shortcuts.
            () => enableAllPeerShapingBox.Checked = !enableAllPeerShapingBox.Checked);
        // Pipe hotkey controller diagnostics into the main log so we can see, e.g.,
        // "capture send-system-volume-down: OK = Ctrl+Shift+Alt+J" and
        // "register send-system-volume-down: FAILED = Ctrl+Shift+Alt+J (Win32 error 1409:
        // another app or another RemSound process already registered this combo)".
        // The user gets the regular MessageBox warning on registration failure; the log
        // captures the cause so we can debug without guessing.
        hotkeyController.Log = msg => logFile.Event($"hotkey: {msg}");
        // Hotkey edits via the Keyboard shortcuts dialog need to mark the profile dirty
        // so the close-without-saving prompt fires. The dirty flag is only set by direct
        // UI handlers in MainForm; the controller is its own object so it can't reach
        // MarkProfileDirty without being told how. Without this hook the user would change
        // a binding, close, get no prompt, launch again — and find their new binding
        // wasn't in the profile JSON. (The settings cache holds it, but the cache is
        // copied to the profile only on Save / Update, not on close.)
        hotkeyController.OnHotkeyChanged = () =>
        {
            MarkProfileDirty();
            // Keep the spoken "press X anywhere" hints in sync with the new binding.
            UpdateHotkeyAnnouncements();
        };
        trayController = new MainFormTrayController(
            this,
            // getSending / toggleSending — the tray's "Enable sending" checkable item reads
            // and toggles the main-window send checkbox. Toggle (not set-to-true) so right-
            // clicking it twice doesn't get the user stuck on. 2026-05-28 redesign.
            getSending: () => sendMyAudioCheckbox.Checked,
            toggleSending: () => sendMyAudioCheckbox.Checked = !sendMyAudioCheckbox.Checked,
            getReceiving: () => receiveAudioCheckbox.Checked,
            toggleReceiving: () => receiveAudioCheckbox.Checked = !receiveAudioCheckbox.Checked,
            // Profiles submenu — list of recent profile file paths read live from AppConfig
            // every time the submenu opens, so newly-loaded profiles appear immediately.
            getRecentProfilePaths: () => AppConfig.Load().RecentProfiles,
            switchToProfile: path => SwitchToRecentProfile(path),
            // Tooltip builder — called once just before the tray icon first becomes visible
            // (in Minimize) so the shell's NIM_ADD sees the current live state instead of a
            // stale "starting up" string. Subsequent updates ride on the 1 Hz snapshot tick.
            buildTooltip: BuildTrayTooltip,
            exit: Close);

        recordingController = new RecordingController(
            sender,
            receiver,
            settings,
            msg => logFile.Event($"recorder: {msg}"));
        recordingController.RecordingStateChanged += UpdateStartStopRecordingMenuLabel;
        // Supply the connected peers when a split recording starts, so it can make one track per peer.
        recordingController.ConnectedPeersProvider = () =>
            selectedPeerEndpoints
                .Select(kv => (kv.Value.Address, selectedPeerLabels.GetValueOrDefault(kv.Key, kv.Value.Address.ToString())))
                .ToList();

        // Load the machine-wide named-peers book and make every peer list resolve display names through
        // it. Set before any list is built so names show from the first render.
        var startupCfg = AppConfig.Load();
        namedPeers = new Dictionary<string, NamedPeer>(startupCfg.NamedPeers ?? new(), StringComparer.OrdinalIgnoreCase);
        // Migrate the legacy flat friendly-name map (pre-registry configs) into the book, once.
        if (namedPeers.Count == 0 && startupCfg.PeerFriendlyNames is { Count: > 0 } legacy)
        {
            foreach (var (k, v) in legacy)
                if (!string.IsNullOrWhiteSpace(v)) namedPeers[k] = new NamedPeer { MachineName = k, FriendlyName = v };
            if (namedPeers.Count > 0) SaveNamedPeers();
        }
        PeerListItem.DisplayNameProvider = ResolvePeerDisplayName;

        // --- Set accessibility names ---
        // For these four controls the keyboard shortcut is included explicitly in both the
        // visible label (set in BuildLayout) and the AccessibleName, instead of relying on the
        // WinForms `&letter` auto-derivation. The auto-derivation went wrong because the lists
        // are wrapped in a FlowLayoutPanel, which breaks the framework's "label associated with
        // the next focusable" heuristic. ProcessCmdKey is what actually performs the focus
        // change. Per Ed's working rule "labels are one phrase used twice", visible text and
        // AccessibleName here are kept identical.
        // 2026-05-08 NVDA-announce fix — embed "(Alt+X)" in AccessibleName for non-CheckBox
        // controls. The framework's auto-derivation of KeyboardShortcut from a labelled-by
        // MnemonicLabel is unreliable inside FlowLayoutPanel-wrapped rows (sometimes picks
        // up the wrong row's label, sometimes finds nothing). Putting the shortcut in the
        // AccessibleName text guarantees NVDA announces it consistently right after the
        // control name. CheckBoxes own their own &-mnemonic via their Text and don't need
        // the suffix in AccessibleName — they're left as bare names.
        volumeBar.AccessibleName = "Master volume for received audio (Alt+V)";
        receiveOutputDevicesList.AccessibleName = "WASAPI outputs for received audio (Alt+3)";
        receiveOutputDevicesStatusLabel.AccessibleName = "Selected receive output device status";
        sendOutputDevicesList.AccessibleName = "WASAPI audio outputs to send (Alt+4)";
        sendOutputDevicesStatusLabel.AccessibleName = "Selected output device status";
        sendInputDevicesList.AccessibleName = "WASAPI audio inputs to send (Alt+5)";
        sendInputDevicesStatusLabel.AccessibleName = "Selected input device status";
        asioReceiveOutputDevicesList.AccessibleName = "ASIO outputs for received audio (Alt+1)";
        asioReceiveOutputDevicesStatusLabel.AccessibleName = "Selected ASIO receive channel status";
        asioSendDevicesList.AccessibleName = "ASIO audio inputs to send (Alt+2)";
        asioSendDevicesStatusLabel.AccessibleName = "Selected ASIO send channel status";
        // Per-application send controls (issue #20). Listbox + list carry the "(Alt+N)" suffix like the
        // other non-CheckBox controls; the master checkbox owns its own &-mnemonic via its Text.
        sendModeList.AccessibleName = "How to send WASAPI audio (Alt+6)";
        sendAppsList.AccessibleName = "Currently active applications (Alt+8)";
        sendAppsStatusLabel.AccessibleName = "Active application status";
        rememberedAppsList.AccessibleName = "Remembered applications (Alt+9)";
        rememberedAppsStatusLabel.AccessibleName = "Remembered application status";
        // Keyboard shortcuts / Minimise to tray / Save / Save as buttons retired 2026-05-08
        // (now File menu items in BuildFileMenu).
        asioDriverBox.AccessibleName = "ASIO driver (Alt+D)";

        // "Uncheck all inputs and outputs on all soundcards" — clears every device tick in one
        // press. Button owns its own &-mnemonic (Alt+U), so AccessibleName stays clean per Ed's
        // mnemonic convention.
        uncheckAllDevicesButton.Text = "Uncheck all inputs and outputs on all soundcards and set ASIO driver to none (Alt+&U)";
        uncheckAllDevicesButton.AccessibleName = "Uncheck all inputs and outputs on all soundcards and set ASIO driver to none";
        uncheckAllDevicesButton.Click += (_, _) => UncheckAllDevices();

        // Populate ASIO driver list at startup. Discovers all ASIO drivers via NAudio + a
        // registry scan covering 32-bit + 64-bit + HKLM + HKCU views (some drivers register in
        // unusual places). The "(none)" sentinel is always row 0 so the user can return to
        // WASAPI-only without uninstalling drivers; if no real drivers are found at all, the
        // driver picker is hidden entirely in BuildAudioIOTab and the form runs WASAPI-only.
        var asioDriverNames = AsioDeviceProbe.EnumerateDriverNames();
        hasAnyAsioDriverInstalled = asioDriverNames.Count > 0;
        // Mirror the per-machine "never touch this driver" list (global config) so the picker can
        // hide disabled drivers and the device refresh can skip them without disk I/O.
        var realtekStartupConfig = AppConfig.Load();
        disabledAsioDrivers.Clear();
        foreach (var d in realtekStartupConfig.DisabledAsioDrivers) disabledAsioDrivers.Add(d);
        // Realtek's bundled ASIO driver leaks OS handles on every open — flag any installed Realtek
        // ASIO driver so OnShown can offer to disable it and the Options menu can toggle it.
        realtekAsioDriverNames = asioDriverNames.Where(n => AppConfig.IsRealtekAsioDriver(n)).ToList();
        logFile.Event($"asio drivers enumerated at startup: [{string.Join(", ", asioDriverNames.Select(n => $"\"{n}\""))}]"
            + (disabledAsioDrivers.Count > 0 ? $"; disabled in RemSound: [{string.Join(", ", disabledAsioDrivers)}]" : "")
            + (realtekAsioDriverNames.Count > 0 ? $"; realtek detected: [{string.Join(", ", realtekAsioDriverNames)}]" : ""));
        asioDriverBox.Items.Add(NoAsioDriverSentinel);
        foreach (var name in asioDriverNames)
        {
            if (disabledAsioDrivers.Contains(name)) continue; // hidden — RemSound won't touch it
            asioDriverBox.Items.Add(name);
        }

        // Restore the previously-chosen driver if it's still installed; otherwise land on the
        // "(none)" sentinel. We deliberately do NOT auto-pick the first real driver — the user
        // opts in by arrowing down to a driver row themselves. This is the "driver dropdown
        // IS the mode switch" design (2026-05-11): default off, explicit user action turns
        // ASIO on.
        var savedDriver = settings.LoadAsioDriverName();
        if (!string.IsNullOrWhiteSpace(savedDriver) && asioDriverBox.Items.Contains(savedDriver!))
        {
            asioDriverBox.SelectedItem = savedDriver;
        }
        else
        {
            asioDriverBox.SelectedIndex = 0; // "(none)"
        }

        // Debounced driver-change. Each SelectedIndexChanged restarts the timer; the actual
        // apply runs once 300 ms after the user stops moving. Reasons:
        //   1. Arrowing through 5 drivers to read their names should not tear down + reopen
        //      the COM object 5 times — single-client drivers can get confused by rapid
        //      open/close churn. Timer collapses the burst into one apply at the end.
        //   2. Each apply auto-unticks the ASIO send/receive channel rows (see comment in
        //      the timer Tick handler) — we don't want to thrash that on every arrow press.
        asioDriverBox.SelectedIndexChanged += (_, _) =>
        {
            asioDriverChangeDebounce.Stop();
            asioDriverChangeDebounce.Start();
        };
        asioDriverChangeDebounce.Tick += (_, _) =>
        {
            asioDriverChangeDebounce.Stop();
            var selected = asioDriverBox.SelectedItem as string;
            // Translate the "(none)" sentinel into a real null at the settings boundary so
            // the rest of the app sees the legacy "no ASIO driver chosen" shape.
            var newDriver = string.Equals(selected, NoAsioDriverSentinel, StringComparison.Ordinal) ? null : selected;
            var previousDriver = settings.LoadAsioDriverName();
            settings.SaveAsioDriverName(newDriver);
            var driverActuallyChanged = !string.Equals(previousDriver, newDriver, StringComparison.OrdinalIgnoreCase);
            if (driverActuallyChanged) MarkProfileDirty();
            if (driverActuallyChanged) ClearAsioProbeCache();

            // When the driver actually changes (including switching to/from "(none)"), clear
            // ASIO ticks. The synthetic device-id "asio:N" is a pair-index into whichever
            // driver is loaded; pair 2 of the Audient is a different physical channel from
            // pair 2 of the Komplete. If we let the old ticks survive a driver swap, the
            // wrong channels would be captured/rendered until the user noticed and re-ticked.
            // Before clearing, remember the OUTGOING driver's ticks so returning to it can
            // restore them (see asioTicksByDriver) — per-driver memory keeps the safety
            // property while making "switch away and back" resume audio on its own.
            if (driverActuallyChanged)
            {
                if (!string.IsNullOrWhiteSpace(previousDriver))
                {
                    asioTicksByDriver[previousDriver!] = (
                        SnapshotAsioTicks(asioSendDevicesList),
                        SnapshotAsioTicks(asioReceiveOutputDevicesList));
                }
                try
                {
                    suppressDeviceCheckChange = true;
                    for (var i = 0; i < asioSendDevicesList.Items.Count; i++) asioSendDevicesList.SetItemChecked(i, false);
                    for (var i = 0; i < asioReceiveOutputDevicesList.Items.Count; i++) asioReceiveOutputDevicesList.SetItemChecked(i, false);
                }
                finally { suppressDeviceCheckChange = false; }
            }

            // The audio mode is now derived from whether a driver is selected — re-applying
            // here switches sender/receiver between WasapiOnly and BothIndependent as needed.
            // UpdateBothIndependentVisibility refreshes the ASIO-lane latency row, and
            // ApplyContinuousTuneTimer re-evaluates which auto-tune lanes need ticking.
            UpdateBothIndependentVisibility();
            ApplyContinuousTuneTimer();
            ApplyAsioMode();

            // Returning to a driver we remember: re-tick ITS pairs (the lists were just rebuilt for it)
            // and re-apply, so audio resumes without the user re-ticking by hand. A driver we've not
            // seen this session restores nothing — same as before.
            if (driverActuallyChanged && !string.IsNullOrWhiteSpace(newDriver)
                && asioTicksByDriver.TryGetValue(newDriver!, out var remembered)
                && (remembered.Send.Length > 0 || remembered.Recv.Length > 0))
            {
                try
                {
                    suppressDeviceCheckChange = true;
                    RestoreAsioTicks(asioSendDevicesList, remembered.Send);
                    RestoreAsioTicks(asioReceiveOutputDevicesList, remembered.Recv);
                }
                finally { suppressDeviceCheckChange = false; }
                logFile.Event($"asio ticks restored for \"{newDriver}\": send pairs=[{string.Join(",", remembered.Send)}], receive pairs=[{string.Join(",", remembered.Recv)}]");
                ApplyAudioRuntime();
                ApplyReceiveDevices();
            }
        };
        healthLabel.AccessibleName = "Connection health";
        statusLabel.AccessibleName = "Status";
        codecBox.AccessibleName = "Audio codec (Alt+C)";
        maxLatencyBox.AccessibleName = "Audio latency in milliseconds (Alt+L)";

        // --- Populate static choices ---
        // Three transport choices, ordered most-tolerant-of-bad-networks to most-demanding:
        //   * PCM 48K 24-bit       — uncompressed, ~2.3 Mbps
        //   * Opus broadcast quality — 20 ms frame (960 samples/ch at 48 kHz), loss tolerant
        //   * Opus live latency      — 2.5 ms frame (120 samples/ch at 48 kHz), 8× the packet
        //                              rate of broadcast quality, for jamming / live monitoring
        // The 10 ms middle option (480 samples/ch) that lived here in v2.x has been retired —
        // it sat between the other two without a clear use case (saved only 5 ms over 20 ms
        // and gave up loss tolerance for no clearly audible win). Frame size on the wire is
        // samples-per-channel at 48 kHz (v3.0 unit). Labels avoid numbers and ms jargon per
        // the manual's "use case in words" convention; the per-peer status line surfaces the
        // actual ms figure for users who want to verify.
        codecBox.Items.AddRange(new object[]
        {
            new CodecChoice("PCM 48K 24 bit — uncompressed", AudioTransportCodec.Pcm, 0),
            new CodecChoice("Opus, broadcast quality — loss tolerant", AudioTransportCodec.Opus, 960),
            new CodecChoice("Opus, live latency — for jamming and monitoring", AudioTransportCodec.Opus, 120),
        });
        codecBox.SelectedIndex = ResolveCodecIndex(settings.LoadCodec(), settings.LoadOpusFrameSamplesPerChannel());
        var initialCodec = (CodecChoice)codecBox.SelectedItem!;
        sender.ConfigureCodec(initialCodec.Codec, EffectiveOpusFrameSamples(initialCodec.Codec, initialCodec.OpusFrameSamples, settings.LoadSendRate()));
        sender.SetSendRate(settings.LoadSendRate());

        // Relay-mode plumbing. The sender's UDP socket is always-receiving from form construction
        // onwards: in LAN peer-to-peer no inbound traffic arrives at this socket (LAN peers send
        // direct to the receiver's well-known port), but in relay mode this is where audio and
        // heartbeat replies show up — they come back through the NAT pinhole opened by the first
        // outbound packet from this socket. We dispatch by packet type to the right pipeline.
        sender.OnInboundPacket = (buffer, length, remote) =>
        {
            if (length < RemPacket.HeaderSize) return;
            if (!RemPacket.TryReadHeader(buffer.AsSpan(0, length), out var type, out _, out _)) return;
            if (type == RemPacketType.Heartbeat)
            {
                heartbeatService?.HandleInjectedPacket(buffer, length, remote);
            }
            else
            {
                // Format / Audio / KeepAlive — feed into the receiver's existing pipeline as if
                // it had arrived on the well-known port. Allow-list, session creation, decoder,
                // and playout all work unchanged — they don't know or care which socket the
                // packet came in on.
                receiver.InjectExternalPacket(buffer, length, remote);
            }
        };
        sender.StartReceiving();
        // Tight-latency mode is now sender-side only (per-callback PCM emission in ASIO mode).
        // The receiver-side hook was removed in the 2026-05-06 cleanup since the resampler is
        // no longer in the receive path. The dialog checkbox label still says "Lock to audio
        // clock" but only affects the sender now.
        // Lock to audio clock is always on now (no longer a user option) — put the sender into
        // tight-latency mode unconditionally.
        sender.SetTightLatency(true);
        logFile.Event($"tight latency at startup: on (always) (audio mode={settings.LoadAudioMode()})");

        // Priority mode (per-profile) is SCOPED to actual streaming now — see
        // EvaluatePriorityModeScope on the 1 Hz status tick. Nothing to engage at construction:
        // the levers come up within a tick of audio moving and drop after the quiet hold-down.
        // (Pre-2026-07-26 this applied every lever here and held them for the whole app
        // lifetime, keeping an idle-in-tray machine awake and off deep power states.)
        // Native-rate passthrough is automatic now (driven by codec, not a user setting):
        // PCM+single-source-WASAPI-push = pass capture-device rate through to the wire;
        // Opus = always pre-resample to 48 kHz (encoder is locked at 48 k); MixingEngine /
        // ASIO sender = always 48 kHz on the wire. Nothing for the user to toggle.
        receiver.SetSmoothness(settings.LoadSmoothness());
        receiver.SetConcealmentArtifact(settings.LoadConcealmentArtifact());
        // Continuous auto-tune state — UI lives in the Connectivity & transport dialog.
        continuousTuneEnabled = settings.LoadContinuousAutoTuneEnabled();
        continuousTuneIntervalSec = settings.LoadContinuousAutoTuneIntervalSec();

        maxLatencyBox.Value = Math.Clamp(settings.LoadMaxLatencyMs(), (int)maxLatencyBox.Minimum, (int)maxLatencyBox.Maximum);
        // Select-all-on-focus for the numeric spinners. Fixes the WinForms default where typing
        // a new value into a NumericUpDown that already shows "80" produces "8010" instead of
        // "10". The Enter event fires when the control receives focus (keyboard or click); we
        // post a select-all to it so the cursor lands on a fully-selected value, and any
        // typed digits replace the selection. Applies to both the form and dialog instances.
        SelectAllOnFocus(maxLatencyBox);
        // Push the slider's value to the receiver. In classic modes that's the Mixed route
        // (legacy behaviour); in BothIndependent the slider drives the WasapiLane route. The
        // ASIO-lane initial push happens later in WireBothIndependentControls once the
        // companion control has been created and its loaded value applied.
        receiver.SetMaxLatencyMsFor(MaxLatencyBoxRoute, (int)maxLatencyBox.Value);

        // Apply the user's "enable logs" preference to the log gate. Logging is a
        // machine-local debug knob stored in AppConfig (default off) — switching profiles
        // doesn't change it. RemSoundLog defers actually creating the file in
        // <exe>\logs\ until the first write arrives while Enabled is true, so an idle "off"
        // setting produces zero filesystem traffic. The Preferences dialog's Enable-logs
        // checkbox writes through to both AppConfig.LoggingEnabled and logFile.Enabled when
        // the user toggles it.
        logFile.Enabled = AppConfig.Load().LoggingEnabled;
        // DiagnosticsGate gates the engine's hot-path instrumentation (sender/receiver
        // max-time probes, spike detector, callback-gap timers) so the audio threads pay
        // zero cost when nobody is going to read the numbers. It's ON whenever either the
        // Enable-logs checkbox is on OR continuous auto-tune is on (auto-tune needs the
        // same per-second diag data the log emits). Real initial value is set after the
        // settings cache has finished loading; see the call further down. We seed it false
        // here so any early probe fires before the settings load are a no-op.
        DiagnosticsGate.Enabled = false;
        if (logFile.Enabled) AppendLogEntry("logging enabled at startup");

        // Sender diagnostic events (capture started, errors, etc.) get written to the log file.
        sender.Diagnostic = msg => logFile.Event($"sender: {msg}");
        receiver.Diagnostic = msg => logFile.Event($"receiver: {msg}");

        // Pre-load all cue sounds so the first playback isn't delayed by file I/O. Default
        // WAVs are deployed to a sounds\ subfolder under RemSound.exe (see RemSound.App
        // .csproj Content rules); the per-cue custom-path overrides in AppConfig.CustomCuePaths
        // are honoured by TryLoadCueSound when set.
        TryLoadCueSound(CueId.Connect, "connect.wav", out connectSound);
        TryLoadCueSound(CueId.Disconnect, "disconnect.wav", out disconnectSound);
        TryLoadCueSound(CueId.RecordStart, "record start.wav", out recordStartSound);
        TryLoadCueSound(CueId.RecordStop, "record stop.wav", out recordStopSound);
        TryLoadCueSound(CueId.Save, "save.wav", out saveSound);
        TryLoadCueSound(CueId.ProfileSwitch, "profile.wav", out profileSwitchSound);
        TryLoadCueSound(CueId.ProfileMenuOpen, "profile menu open.wav", out profileMenuOpenSound);
        TryLoadCueSound(CueId.Update, "update.wav", out updateSound);
        TryLoadCueSound(CueId.SendOn, "send on.wav", out sendOnSound);
        TryLoadCueSound(CueId.SendOff, "send off.wav", out sendOffSound);
        TryLoadCueSound(CueId.ReceiveOn, "recieve on.wav", out receiveOnSound);
        TryLoadCueSound(CueId.ReceiveOff, "recieve off.wav", out receiveOffSound);
        TryLoadCueSound(CueId.Hide, "minimise.wav", out hideSound);
        TryLoadCueSound(CueId.Show, "maximise.wav", out showSound);

        LoadAudioDevices();
        // Apply persisted ASIO mode from settings — switches sender/receiver backends so the
        // device-list refresh below populates with the right kind of entries (WASAPI endpoints
        // or ASIO channel pairs).
        ApplyAsioMode();

        // --- Wire main-form events ---
        receiveAudioCheckbox.CheckedChanged += (_, _) => OnStreamingCheckboxChanged(receiveAudioCheckbox);
        sendMyAudioCheckbox.CheckedChanged += (_, _) => OnStreamingCheckboxChanged(sendMyAudioCheckbox);
        // Send/receive have their own dedicated cue sounds (send/receive turned on/off). When that
        // dedicated cue is on, only it plays - suppress the generic checkbox tick/untick on these two.
        // When the dedicated cue is "(none)", these return false and the checkbox sound plays as normal.
        sendMyAudioCheckbox.SuppressCheckSound = on => on ? AppConfig.Load().EnableSendOnCue : AppConfig.Load().EnableSendOffCue;
        receiveAudioCheckbox.SuppressCheckSound = on => on ? AppConfig.Load().EnableReceiveOnCue : AppConfig.Load().EnableReceiveOffCue;
        volumeBar.Scroll += (_, _) => { receiver.Volume = volumeBar.Value / 100f; MarkProfileDirty(); };
        WireCheckedListAccessibility(receiveOutputDevicesList, receiveOutputDevicesStatusLabel, "receive output device");
        receiveOutputDevicesList.ItemCheck += (_, e) =>
        {
            if (suppressDeviceCheckChange) return;
            // "Use Windows default" is exclusive: while it's on, specific cards can't be ticked.
            if (AudioDefaultFollower.VetoRealDeviceCheck(receiveOutputDevicesList, e)) return;
            if (receiveOutputDevicesList.Items[e.Index] is AudioDeviceChoice c)
            {
                if (c.IsDefaultFollower)
                {
                    // Machine-wide preference (AppConfig), not part of the profile.
                    var on = e.NewValue == CheckState.Checked;
                    PersistUseDefaultDevice(output: true, on);
                    // Turning it on clears the specific outputs and locks them out (the veto above).
                    if (on) BeginInvoke(new Action(() => UntickAllExceptDefaultFollower(receiveOutputDevicesList, output: true)));
                }
                else if (c.DeviceId is { } rid)
                {
                    // Track the user's intent so a card that's later unplugged is re-ticked + re-opened
                    // when it returns (issue #5). See ReapplyRememberedReceiveOutputs.
                    if (e.NewValue == CheckState.Checked) rememberedReceiveOutputIds.Add(rid);
                    else rememberedReceiveOutputIds.Remove(rid);
                    MarkProfileDirty();
                }
            }
            BeginInvoke(ApplyReceiveDevices);
        };
        WireCheckedListAccessibility(sendOutputDevicesList, sendOutputDevicesStatusLabel, "output device");
        WireCheckedListAccessibility(sendInputDevicesList, sendInputDevicesStatusLabel, "input device");
        sendOutputDevicesList.ItemCheck += (_, args) =>
        {
            if (suppressDeviceCheckChange) return;
            // "Use Windows default" is exclusive: while it's on, specific cards can't be ticked.
            if (AudioDefaultFollower.VetoRealDeviceCheck(sendOutputDevicesList, args)) return;
            BeginInvoke(ApplyAudioRuntime);
            if (sendOutputDevicesList.Items[args.Index] is AudioDeviceChoice { IsDefaultFollower: true })
            {
                // "Use Windows default output for loopback" is a machine-wide preference (AppConfig),
                // not part of the profile — a follower can never go stale. Turning it on clears the
                // specific outputs and locks them out (the veto above) so only the default is captured.
                var on = args.NewValue == CheckState.Checked;
                PersistUseDefaultLoopbackSend(on);
                if (on) BeginInvoke(new Action(() => UntickAllExceptDefaultFollower(sendOutputDevicesList, output: false)));
                return;
            }
            MarkProfileDirty();
        };
        sendInputDevicesList.ItemCheck += (_, args) =>
        {
            if (suppressDeviceCheckChange) return;
            // "Use Windows default" is exclusive: while it's on, specific mics can't be ticked.
            if (AudioDefaultFollower.VetoRealDeviceCheck(sendInputDevicesList, args)) return;
            logFile.Event($"ui: capture (WASAPI mic) '{sendInputDevicesList.Items[args.Index]}' {(args.NewValue == CheckState.Checked ? "ticked" : "unticked")}");
            BeginInvoke(ApplyAudioRuntime);
            if (sendInputDevicesList.Items[args.Index] is AudioDeviceChoice { IsDefaultFollower: true })
            {
                // Machine-wide preference (AppConfig), not part of the profile — and no mic-block check
                // (the default mic could be anything; the runtime apply handles a blocked default).
                var on = args.NewValue == CheckState.Checked;
                PersistUseDefaultDevice(output: false, on);
                // Turning it on clears the specific mics and locks them out (the veto above).
                if (on) BeginInvoke(new Action(() => UntickAllExceptDefaultFollower(sendInputDevicesList, output: false)));
                return;
            }
            MarkProfileDirty();
            // Heads-up when the user ticks a WASAPI mic ON but Windows is blocking desktop-app
            // microphone access — capture would open but silently send nothing. Deferred so the
            // tick commits first and the modal doesn't re-enter ItemCheck. Skipped while a profile
            // is being applied: the startup check (MaybeWarnMicBlockedOnStartup) owns the warning
            // then, so a launch into a blocked-mic profile doesn't pop it twice.
            if (!applyingProfile && args.NewValue == CheckState.Checked && IsMicrophoneBlockedByWindowsPrivacy())
            {
                BeginInvoke(new Action(WarnMicrophoneBlockedByWindowsPrivacy));
            }
        };
        // ASIO list accessibility + ItemCheck handlers — same patterns as the WASAPI ones.
        WireCheckedListAccessibility(asioReceiveOutputDevicesList, asioReceiveOutputDevicesStatusLabel, "ASIO receive output channel");
        WireCheckedListAccessibility(asioSendDevicesList, asioSendDevicesStatusLabel, "ASIO send channel");
        asioReceiveOutputDevicesList.ItemCheck += (_, _) => { if (!suppressDeviceCheckChange) { BeginInvoke(ApplyReceiveDevices); MarkProfileDirty(); } };
        asioSendDevicesList.ItemCheck += (_, args) => { if (!suppressDeviceCheckChange) { logFile.Event($"ui: capture (ASIO) '{asioSendDevicesList.Items[args.Index]}' {(args.NewValue == CheckState.Checked ? "ticked" : "unticked")}"); BeginInvoke(ApplyAudioRuntime); MarkProfileDirty(); } };
        // Profile-management button click wirings retired 2026-05-08 — File menu items now
        // call SaveProfileAs() / UpdateExistingProfile() / hotkeyController.ShowKeyboardShortcutsDialog
        // / trayController.Minimize() directly. See BuildFileMenu.

        // --- Settings shared with dialog ---
        codecBox.SelectedIndexChanged += (_, _) =>
        {
            if (codecBox.SelectedItem is CodecChoice item)
            {
                settings.SaveCodec(item.Codec);
                if (item.Codec == AudioTransportCodec.Opus) settings.SaveOpusFrameSamplesPerChannel(item.OpusFrameSamples);
                var effectiveSamples = EffectiveOpusFrameSamples(item.Codec, item.OpusFrameSamples, settings.LoadSendRate());
                sender.ConfigureCodec(item.Codec, effectiveSamples);
                logFile.Event($"codec changed to {item.Codec}{(item.Codec == AudioTransportCodec.Opus ? $" {effectiveSamples / 48.0:0.##}ms" : "")}");
                MarkProfileDirty();
            }
        };
        maxLatencyBox.ValueChanged += (_, _) =>
        {
            // Track when the user (vs continuous auto-tune) moved the slider, so the auto-tune
            // can defer to the user's intent for a few seconds before adjusting again.
            // suppressUserSliderMoveTracking is set by both continuous auto-tune AND the manual
            // one-shot tune button while they're driving the slider — anything where the user
            // didn't physically move the control. We use the same flag to take the soft path
            // through the receiver: auto-tune lowers don't drain (drift corrector handles it),
            // so the slider can drift down silently when conditions improve. Manual user
            // lowers still drain, since the user is asking for an immediate, responsive change.
            var fromAutoTune = suppressUserSliderMoveTracking;
            if (!fromAutoTune)
            {
                lastUserSliderMoveUtc = DateTime.UtcNow;
                // When continuous auto-tune is currently enabled, the latency value is
                // effectively runtime state (auto-tune will overwrite whatever the user sets
                // anyway), so don't dirty the profile on latency changes — matches the user's
                // mental model that "auto-tune on = latency is automatic, not a saved setting".
                // Toggling the auto-tune checkbox itself still dirties (handled separately on
                // the checkbox CheckedChanged), so a profile that goes from auto-tune-off to
                // auto-tune-on is still flagged as needing a save. 2026-05-06.
                if (!continuousTuneEnabled) MarkProfileDirty();
            }
            settings.SaveMaxLatencyMs((int)maxLatencyBox.Value);
            // Route the value to whichever route this slider is currently driving. In every
            // classic mode that's Mixed (the legacy behaviour — single-knob world). In
            // BothIndependent it's WasapiLane: the slider has been re-labeled "WASAPI
            // latency" and the user is adjusting only the WASAPI side of the wire.
            var sliderRoute = MaxLatencyBoxRoute;
            if (fromAutoTune)
            {
                receiver.SetMaxLatencyMsSoftFor(sliderRoute, (int)maxLatencyBox.Value);
            }
            else
            {
                receiver.SetMaxLatencyMsFor(sliderRoute, (int)maxLatencyBox.Value);
            }
        };
        // Logging-enabled toggle wiring lives in PreferencesDialog now (it constructs its
        // own Enable-logs checkbox and writes through via the applyLoggingEnabled callback
        // we pass it from OpenPreferencesDialog).

        // --- Discovery ---
        discovery.PeersChanged += () => BeginInvoke(RefreshKnownPeers);

        // Continuous auto-tune timer — checkbox/combo live in the dialog and update our state
        // fields directly. The timer reads from those fields; we just (re)apply it here.
        continuousTuneTimer.Tick += (_, _) => ContinuousTuneTick();
        ApplyContinuousTuneTimer();

        // Self-updater background poll. Frequency lives in AppConfig.UpdateCheckFrequency
        // (the user picks Never / hourly / 6-hour / 24-hour in Preferences). The updater
        // logs its activity through the same RemSoundLog gate as everything else.
        updater.Log = msg => logFile.Event($"updater: {msg}");
        updateCheckTimer.Tick += (_, _) => CheckForUpdatesInBackground();
        ApplyUpdateCheckTimer();

        // --- Status / health ticker ---
        statusTimer.Tick += (_, _) =>
        {
            // Belt-and-braces: this is a 1 Hz UI tick — a transient WinForms hiccup (e.g. a
            // stale-index ItemArray throw during a churny peer-list rebuild) must never take
            // the whole app down with a crash dialog. Log and ride it out; the next tick
            // recovers. The individual Sync* methods are also hardened (see SafeSelectedItem).
            try
            {
                EvaluatePriorityModeScope();
                UpdateStatus();
                SnapshotLogIfDue();
                EnsureRequestedAudioRunning();
                // Refresh the Connectivity tab's peer lists from the same 1 Hz tick — replaces
                // the dialog's old 1.5 s dedicated refresh timer. Each Sync* helper short-circuits
                // when its signature is unchanged so NVDA isn't spammed with re-announcements.
                SyncAllPeerLists();
            }
            catch (Exception ex)
            {
                AppendLogEntry($"status tick: {ex.GetType().Name}: {ex.Message}");
            }
        };

        // --- Hot-swap device watcher ---
        deviceRefreshTimer.Tick += (_, _) =>
        {
            if (deviceRefreshOneShot) deviceRefreshTimer.Stop(); // debounce: one refresh per change burst
            RefreshAudioDeviceLists();
        };

        BuildLayout();
        LoadRememberedPeersFromSettings();
        // Seed the discovery service's unicast hint list with any remembered peer IPs so that,
        // the moment we start announcing, those addresses get directly contacted (bridges
        // Tailscale/VPN where broadcast doesn't traverse).
        PushDiscoveryUnicastHints();
        if (!headless) hotkeyController.Initialize(this);
        // Announce each configurable global hotkey on the control / menu item it drives, so NVDA
        // reads "… press Control+Shift+Alt+R anywhere" when you land on it.
        UpdateHotkeyAnnouncements();

        // Hook system sleep/resume so we can rebuild the audio backend after wake (USB
        // audio devices often come back wedged). The handler routes back through
        // OnSystemResume on a background thread; that marshals to the UI thread.
        powerResumeHandler = new PowerResumeHandler(OnSystemResume, msg => logFile.Event($"power: {msg}"));

        // Build the UPnP router-port opener up-front but don't start it — Shown decides
        // whether to invoke Start() based on AppConfig.UpnpEnabled. Constructing the field
        // here (rather than lazily on tick) keeps the field non-null so the Preferences
        // dialog can subscribe to StatusChanged without us juggling instance lifetimes.
        routerPortMapper = new RouterPortMapper(msg => logFile.Event($"upnp: {msg}"));

        FormClosing += (_, _) =>
        {
            // Stop AND dispose each timer. A WinForms Timer is a Component, not a Control, so base
            // Form.Dispose never reaches it; Stop() only kills the WM_TIMER, leaving the timer's
            // message-only window handle to be freed at GC finalization. MainForm is rebuilt on every
            // profile switch, so disposing here releases those handles deterministically each time.
            statusTimer.Stop(); statusTimer.Dispose();
            autoSaveTimer.Stop(); autoSaveTimer.Dispose();
            deviceRefreshTimer.Stop(); deviceRefreshTimer.Dispose();
            continuousTuneTimer.Stop(); continuousTuneTimer.Dispose();
            updateCheckTimer.Stop(); updateCheckTimer.Dispose();
            deferredUpdateTimer.Stop(); deferredUpdateTimer.Dispose();
            asioDriverChangeDebounce.Stop(); asioDriverChangeDebounce.Dispose();
            try { sendAppsReconcileTimer?.Stop(); sendAppsReconcileTimer?.Dispose(); } catch { }
            DisposeSessionStartWatcher();
            try { processSelfMeter.Dispose(); } catch { }
            try { deviceChangeNotifier?.Dispose(); } catch { }
            try { powerResumeHandler?.Dispose(); } catch { }
            // NOTE: routerPortMapper.Dispose() is NOT called here — it's moved into the bounded
            // background teardown below. Its Stop() does a SYNCHRONOUS DeletePortMap call to the router,
            // which blocks (or hangs) when the router is slow/unresponsive; on the UI thread that froze the
            // close, so enabling UPnP made RemSound impossible to shut (Andre, 2026-07-18).
            // Reverse every Win32 lever PerformanceMode applied. The kernel would clean
            // these up on process exit anyway, but doing it explicitly releases the power
            // request handle and matches our timeBeginPeriod with a timeEndPeriod.
            try { PerformanceMode.Apply(false, msg => logFile.Event(msg)); } catch { /* harmless */ }
            try { discovery.Dispose(); } catch { }
            try { heartbeatService?.Dispose(); } catch { }

            // Audio dispose can hang for many seconds on certain ASIO drivers (Audient is the
            // confirmed offender — it takes 10–20 s to release on close in test logs). Run
            // sender.Dispose() and receiver.Dispose() on a background thread with a hard
            // timeout. If they don't finish in 2 seconds we stop waiting and let the rest of
            // the form-close path run; the OS reclaims any audio resources on process exit.
            // Worst case the user sees a brief tray-icon stutter; before this they saw a
            // ~16 s frozen window before the form went away.
            // The UPnP router teardown (DeletePortMap — a synchronous call to a possibly-unresponsive
            // router) belongs here too: like the ASIO dispose it can block for many seconds, and on the UI
            // thread it froze the close. Run both OFF the UI thread, in parallel, under one hard timeout.
            var slowTeardown = Task.Run(() =>
            {
                var upnp = Task.Run(() => { try { routerPortMapper?.Dispose(); } catch { /* ignore */ } });
                var audio = Task.Run(() =>
                {
                    try { sender.Dispose(); } catch { /* ignore */ }
                    try { receiver.Dispose(); } catch { /* ignore */ }
                });
                try { Task.WaitAll(upnp, audio); } catch { /* ignore */ }
            });
            if (!slowTeardown.Wait(TimeSpan.FromSeconds(3)))
            {
                try { logFile.Event("close: UPnP/audio teardown taking >3s; letting process exit reclaim"); } catch { }
            }

            hotkeyController.Dispose();
            ScreenReader.Shutdown();
            trayController.Dispose();
            logFile.Dispose();
        };

        Shown += (_, _) =>
        {
            if (!connected) Connect();
            // Apply control-state portion of the loaded profile (device ticks, send/receive
            // checkboxes, audio port, volume, ticked peers). Done here AFTER device lists are
            // populated by LoadAudioDevices(). Settings-shaped fields (codec, hotkeys, etc.)
            // were already pushed into the in-memory settings cache in the constructor.
            ApplyPendingProfileToControls();
            // The profile-switch cue is played ON CLICK by the switch entry points (Recent menu,
            // quick switch, File open) — NOT here. A fresh launch into the first profile must stay
            // silent: hearing the switch cue and then the connect cue at startup is confusing
            // (Ed, 2026-06-08). So the rebuilt form never replays it.
            // Andre's app gets focus inside the active tab page for free because his form is
            // a MODAL DIALOG (ShowDialog) — WinForms' modal-dialog focus semantics walk the
            // chain TabControl → active TabPage → first child. Our form is the main window,
            // not a modal dialog, and that walk doesn't always reach a child — focus can rest
            // on the TabControl itself, which makes NVDA announce "tab control" before
            // anything else. One explicit Focus() call here mimics Andre's effective behaviour
            // without otherwise changing the tab control. NOT a tab-change handler — no
            // auto-jumping when the user arrows between tabs, only on first show.
            BeginInvoke(() => FocusListControl(connectedPeersList));

            // First-launch pass: warn about any cue that's switched on but whose sound file is
            // missing (deferred so it lands after the window is fully up and can surface).
            BeginInvoke(CheckForMissingEnabledCues);

            // "Start minimised" is a COLD-BOOT preference: drop straight to the tray when the app
            // first launches. It must NOT apply when the user deliberately creates a new profile or
            // switches profiles — those relaunch the window through the Program.cs loop. Those paths
            // already set startNextInstanceMinimized to "stay in the tray only if we were already
            // there", which is the right intent; but ORing in the global StartMinimised used to
            // override that and hide the window on every new-profile / switch, which read to the user
            // as a crash (issue #12). So gate StartMinimised to the genuine first launch, and let
            // relaunches honour only the explicit per-instance flag.
            // BeginInvoke so the minimise happens *after* Shown completes (otherwise the form-show +
            // form-hide collide and some virtual-machine drivers throw a redraw exception). The
            // pending-profile apply path above is unaffected — settings/devices/peers are already
            // wired up before we hide the window.
            var coldStart = isFirstLaunch;
            isFirstLaunch = false;
            var minimizeThisInstance = startNextInstanceMinimized || (coldStart && AppConfig.Load().StartMinimised);
            startNextInstanceMinimized = false;
            // Consume the one-shot post-install foreground flag now, whichever branch we take below, so
            // it can't leak into a later profile-switch relaunch. "Start minimised" WINS over it: a user
            // who chose to boot into the tray wants the just-installed copy in the tray too — we only
            // pull the window to the front when we're NOT minimising (else it can open behind others).
            var forcePostInstallForeground = forceForegroundOnStart;
            forceForegroundOnStart = false;
            if (minimizeThisInstance)
            {
                // playCue:false — starting up in the tray (StartMinimised / --minimized) isn't the
                // user choosing to minimise, so it must not sound the "minimise" cue.
                BeginInvoke(() => trayController.Minimize(playCue: false));
            }
            else if (forcePostInstallForeground)
            {
                logFile.Event("installer: post-install relaunch — bringing the window to the foreground");
                // Deferred so it runs after Shown settles, then yanks the window to the front so the
                // just-installed copy isn't left hiding behind other windows. Try again a moment later:
                // a freshly-launched process routinely loses the very first foreground race (the OS is
                // still settling which window owns the foreground just after the old copy exited).
                BeginInvoke(() =>
                {
                    ForceWindowToForeground();
                    var attempts = 0;
                    var retry = new System.Windows.Forms.Timer { Interval = 250 };
                    retry.Tick += (_, _) =>
                    {
                        attempts++;
                        // Stop once we genuinely own the foreground, or after a few tries.
                        var won = !IsDisposed && GetForegroundWindow() == Handle;
                        if (IsDisposed || attempts >= 5 || won)
                        {
                            retry.Stop();
                            retry.Dispose();
                            logFile.Event($"installer: foreground grab done after {attempts} retries, gotForeground={won}");
                            return;
                        }
                        ForceWindowToForeground();
                    };
                    retry.Start();
                });
            }

            // Kick off UPnP discovery if the user has the box ticked. Off by default; the
            // mapper itself coalesces redundant Start() calls so a re-enter via Shown after
            // a sleep cycle is harmless. Run on a thread-pool thread because
            // NatUtility.StartDiscovery() (Mono.Nat 3.0.4) sets up SSDP sockets on every
            // network interface and CAN BLOCK FOR TENS OF SECONDS, or indefinitely, on
            // unusual network setups (multiple adapters, VPNs, hostile firewalls, routers
            // that swallow SSDP). Calling it on the UI thread freezes the WinForms message
            // pump — Andre's v3.0 hang was this exact pattern. The status label still
            // updates correctly because StatusChanged fires on the mapper's own thread and
            // the PreferencesDialog handler BeginInvokes back to the UI thread. 2026-05-23.
            var startupCfg = AppConfig.Load();
            if (startupCfg.UpnpEnabled)
            {
                Task.Run(() =>
                {
                    try { routerPortMapper.Start(); }
                    catch (Exception ex) { logFile.Event($"upnp: start failed: {ex.GetType().Name}: {ex.Message}"); }
                });
            }

            // Startup update check — separate from the periodic timer because users who
            // launch RemSound, find an update, and stay running for less than the timer
            // interval would otherwise miss the release entirely. Default on. The
            // background-poll path handles both silent install and the user-prompt flow.
            // Skipped on a --silent (automated/throwaway) launch: a test instance must never pop an
            // "update available" prompt or, worse, silently download/install + restart mid-test.
            if (startupCfg.CheckForUpdatesOnStartup && !CuePlayer.GloballyMuted)
            {
                // Defer a few seconds so the network stack, audio engine, and any device
                // hot-swap has settled before we touch GitHub. The visible cue (silent-
                // install notice dialog) appears inside the check path, so a small delay
                // is invisible to the user.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
                        if (IsDisposed) return;
                        BeginInvoke(new Action(CheckForUpdatesOnStartup));
                    }
                    catch (Exception ex)
                    {
                        logFile.Event($"updater: startup check scheduling failed: {ex.GetType().Name}: {ex.Message}");
                    }
                });
            }

            // Post-launch notices, shown ONE AT A TIME via a single BeginInvoke that runs them in
            // sequence — NOT one BeginInvoke per notice. Separate BeginInvokes NEST: the second
            // dialog opens inside the first's modal message loop, the two stack on top of each
            // other, and that nesting tangles their modal state so the boxes stop closing cleanly
            // (the bug where the what's-new About box wouldn't close after the Realtek warning).
            // RunStartupNotices shows each notice, waits for the user to close it, THEN shows the
            // next — every one modal to the main window, never nested.
            BeginInvoke(new Action(RunStartupNotices));
        };

        // Headless test build stops here: no timers, no OS device-change registration. Everything above
        // has built the full window (tabs, controls, menus) for the self-test to walk.
        if (headless) return;

        statusTimer.Start();

        // Silent periodic auto-save (opt-in, off by default). Event wiring here; the interval and whether
        // it runs at all are set by ApplyAutoSaveTimer() from the saved preference, and re-applied live
        // when the user changes it in Preferences.
        autoSaveTimer.Tick += (_, _) => AutoSaveCurrentProfileIfDue();
        ApplyAutoSaveTimer();

        // Hot-plug detection is event-driven (see the deviceRefreshTimer comment): register for
        // Windows audio endpoint-change notifications and refresh the device lists only when the
        // device set actually changes. If that registration fails, fall back to the pre-v3.4
        // periodic poll.
        try
        {
            deviceChangeNotifier = new AudioDeviceChangeNotifier(OnAudioEndpointsChanged);
        }
        catch (Exception ex)
        {
            logFile.Event($"device-change notifier failed, using periodic poll: {ex.GetType().Name}: {ex.Message}");
            deviceRefreshOneShot = false;
            deviceRefreshTimer.Interval = 3000;
        }
        // Kick one refresh shortly after launch to populate the ASIO channel lists (LoadAudioDevices
        // only fills the WASAPI lists). After this it's notification-driven; in one-shot mode the
        // Tick handler stops the timer, in the poll fallback it's the first of the periodic ticks.
        deviceRefreshTimer.Start();
    }

    /// <summary>
    /// Runs the post-launch notices one at a time — each ShowDialog blocks until the user closes it,
    /// so the next never opens on top of a still-open one. Order: the what's-new About box (after an
    /// update), then the Realtek-ASIO compatibility warning. The config-migration notice is handled
    /// separately in Program.Main (shown before the profile picker), so it's already outside this
    /// sequence and can't stack with these.
    /// </summary>
    private void RunStartupNotices()
    {
        if (IsDisposed) return;
        MaybeOfferKeyboardShortcutImport();
        if (IsDisposed) return;
        MaybeShowWhatsNewAfterUpdate();
        if (IsDisposed) return;
        MaybeRunLogHousekeeping();
        if (IsDisposed) return;
        MaybeWarnAboutRealtekAsio();
        if (IsDisposed) return;
        MaybeWarnMicBlockedOnStartup();
        if (IsDisposed) return;
        MaybeWarnWeakPassword();
    }

    /// <summary>Startup warning for a profile whose password is too weak to stream under the 5.6 rule.
    /// Runs from the SETTLED post-launch notice sequence — after the window is fully shown and
    /// activated, one dialog at a time — NOT from the mid-connect crypto path where an earlier version
    /// fired it and left a blind user trapped behind a dialog NVDA couldn't reach (Ed, 2026-07-27).
    /// From here <see cref="ForegroundDialog"/> pulls it to the front (even from the tray) with clean
    /// focus, exactly like the mic-blocked and Realtek warnings that already work with NVDA. Only fires
    /// when the profile is actually set up to stream (send or receive on) — otherwise there's no audio
    /// to block and the guided prompt on the first streaming tick is enough. The status line carries a
    /// standing reminder after this is dismissed.</summary>
    private void MaybeWarnWeakPassword()
    {
        if (IsDisposed || CuePlayer.GloballyMuted) return; // never on a --silent/automated launch
        if (!WeakPasswordBlocksAudio(currentProfilePassword, currentAudioKey is not null)) return;
        if (!IsSendEnabled && !IsReceiveEnabled) return;   // not trying to stream → nothing blocked yet
        ForegroundDialog.Show(owner => MessageBox.Show(owner,
            "RemSound has increased its security level, so this profile's password must be strengthened "
                + "to meet the new password rules. Until it is, no audio will pass.\n\n"
                + "Use at least 8 characters — three unrelated words with a number, like kettle9tiger42moon, works well.\n\n"
                + "To change it: open the File menu and choose “Change this profile's password”. Use the "
                + "same new password on every machine you connect with.",
            "RemSound — password needs strengthening",
            MessageBoxButtons.OK, MessageBoxIcon.Warning));
    }

    /// <summary>Startup log-folder housekeeping driven by the Logging-tab preferences. Both steps are
    /// opt-in (off by default): first prune logs older than the configured age, then warn if the
    /// folder still exceeds the configured size. Best-effort — failures never block launch.</summary>
    private void MaybeRunLogHousekeeping()
    {
        if (IsDisposed) return;
        AppConfig cfg;
        try { cfg = AppConfig.Load(); }
        catch { return; }

        if (cfg.PruneOldLogs)
        {
            var removed = LogMaintenance.PruneLogsOlderThan(cfg.PruneOldLogsDays, logFile.Path);
            if (removed > 0)
                logFile.Event($"log housekeeping: pruned {removed} log(s) older than {cfg.PruneOldLogsDays} day(s)");
        }

        if (cfg.WarnIfLogsFolderExceeds)
        {
            var bytes = LogMaintenance.LogsFolderSizeBytes();
            var limitBytes = (long)cfg.LogsFolderWarnThresholdMb * 1024 * 1024;
            if (bytes > limitBytes)
            {
                var mb = bytes / (1024.0 * 1024.0);
                logFile.Event($"log housekeeping: logs folder {mb:0.#} MB exceeds {cfg.LogsFolderWarnThresholdMb} MB threshold — warning user");
                var page = new TaskDialogPage
                {
                    Caption = "RemSound",
                    Heading = "Logs folder is getting large",
                    Text = $"The RemSound logs folder is using about {mb:0} MB, which is over your {cfg.LogsFolderWarnThresholdMb} MB warning size.\n\n"
                         + "You can clear old logs from Preferences, on the Logging tab.",
                    Icon = TaskDialogIcon.Warning,
                    Buttons = { TaskDialogButton.OK },
                    AllowCancel = true,
                };
                try { ForegroundDialog.Show(owner => TaskDialog.ShowDialog(owner, page)); }
                catch (Exception ex) { logFile.Event($"log housekeeping: warn dialog failed: {ex.GetType().Name}: {ex.Message}"); }
            }
        }
    }

    /// <summary>One-time upgrade flow (replaces the v4.4 reset notice): keyboard shortcuts moved from
    /// per-profile to machine-wide storage (issue #14). Offers upgraders the choice of copying their
    /// shortcuts from one of their existing profiles (still readable in the profile files) or starting
    /// fresh. Only offered to people coming straight from a PRE-v4.4 version (where shortcuts were still
    /// per-profile), to spare them the reset — anyone who already went through v4.4's reset is left
    /// alone (re-offering would only annoy them), as is a fresh install or a user with no saved shortcuts
    /// to import. Runs BEFORE <see cref="MaybeShowWhatsNewAfterUpdate"/>, which overwrites the
    /// LastWhatsNewVersion we read to tell upgraders apart from fresh installs.</summary>
    private void MaybeOfferKeyboardShortcutImport()
    {
        if (IsDisposed) return;
        AppConfig cfg;
        try { cfg = AppConfig.Load(); }
        catch { return; }
        if (cfg.KeyboardShortcutsImportOffered) return;

        // Leave the v4.4 crowd alone. v4.4's reset set KeyboardShortcutsGlobalNoticeShown for everyone
        // who ran it; those people have already re-done their shortcuts, so re-offering an import would
        // only annoy them. We only want to catch people coming straight from a PRE-v4.4 version (where
        // shortcuts were still per-profile), before they lose anything.
        if (cfg.KeyboardShortcutsGlobalNoticeShown) { MarkShortcutImportOffered(); return; }

        // Only relevant to upgraders (a previous version ran here, so LastWhatsNewVersion is set) who
        // actually have old per-profile shortcuts to bring across.
        var isUpgrade = !string.IsNullOrEmpty(cfg.LastWhatsNewVersion);
        if (!isUpgrade) { MarkShortcutImportOffered(); return; }

        var titles = ProfilesWithSavedShortcuts();
        if (titles.Count == 0) { MarkShortcutImportOffered(); return; }

        logFile.Event($"keyboard shortcuts: offering import from {titles.Count} profile(s) with saved shortcuts");
        try
        {
            using var dlg = new KeyboardShortcutImportDialog(titles);
            var result = ForegroundDialog.Show(owner => dlg.ShowDialog(owner));
            if (result != DialogResult.OK) return;  // dismissed (Escape) — offer again next launch

            if (dlg.ChosenProfileTitle is { } title)
            {
                var imported = ImportShortcutsFromProfile(title);
                logFile.Event($"keyboard shortcuts: imported {imported} shortcut(s) from profile \"{title}\"");
                hotkeyController.ReloadAndReRegisterAll();
            }
            else
            {
                logFile.Event("keyboard shortcuts: user chose to start fresh with the defaults");
            }
            MarkShortcutImportOffered();
        }
        catch (Exception ex)
        {
            logFile.Event($"keyboard shortcuts import failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void MarkShortcutImportOffered()
    {
        try { var c = AppConfig.Load(); c.KeyboardShortcutsImportOffered = true; c.Save(); }
        catch { /* harmless — at worst the offer shows again next launch */ }
    }

    /// <summary>Titles of profiles that have at least one customised keyboard shortcut saved in their
    /// file (the only ones worth importing — unchanged shortcuts were stored as null).</summary>
    private List<string> ProfilesWithSavedShortcuts()
    {
        var result = new List<string>();
        if (profileStore is null) return result;
        try
        {
            foreach (var title in profileStore.ListProfileTitles())
            {
                try
                {
                    if (profileStore.Load(title) is { } p && ProfileHasAnyShortcut(p)) result.Add(title);
                }
                catch { /* skip an unreadable profile */ }
            }
        }
        catch { /* enumeration failed — offer nothing */ }
        return result;
    }

    private static bool ProfileHasAnyShortcut(Profile p) =>
        p.ReceiveMuteHotkey is not null || p.SendMuteHotkey is not null || p.TrayHotkey is not null
        || p.VolumeUpHotkey is not null || p.VolumeDownHotkey is not null || p.ToggleRecordingHotkey is not null
        || p.RemoteVolumeUpHotkey is not null || p.RemoteVolumeDownHotkey is not null || p.RemoteMuteToggleHotkey is not null
        || p.SystemVolumeUpHotkey is not null || p.SystemVolumeDownHotkey is not null || p.SystemMuteToggleHotkey is not null
        || p.QuickProfileSwitchHotkey is not null || p.SpeakStatusLineHotkey is not null;

    /// <summary>Copy a profile's saved (non-null) keyboard shortcuts into the machine-wide store, via the
    /// settings store's now-global Save* methods. Returns how many were copied; shortcuts the profile
    /// never customised (null) are left at the global default.</summary>
    private int ImportShortcutsFromProfile(string title)
    {
        if (profileStore is null || profileStore.Load(title) is not { } p) return 0;
        var n = 0;
        void Copy(HotkeyRecord? rec, Action<HotkeyInfo> save) { if (rec is not null) { save(rec.ToHotkeyInfo()); n++; } }
        Copy(p.ReceiveMuteHotkey, settings.SaveReceiveMuteHotkey);
        Copy(p.SendMuteHotkey, settings.SaveSendMuteHotkey);
        Copy(p.TrayHotkey, settings.SaveTrayHotkey);
        Copy(p.VolumeUpHotkey, settings.SaveVolumeUpHotkey);
        Copy(p.VolumeDownHotkey, settings.SaveVolumeDownHotkey);
        Copy(p.ToggleRecordingHotkey, settings.SaveToggleRecordingHotkey);
        Copy(p.RemoteVolumeUpHotkey, settings.SaveRemoteVolumeUpHotkey);
        Copy(p.RemoteVolumeDownHotkey, settings.SaveRemoteVolumeDownHotkey);
        Copy(p.RemoteMuteToggleHotkey, settings.SaveRemoteMuteToggleHotkey);
        Copy(p.SystemVolumeUpHotkey, settings.SaveSystemVolumeUpHotkey);
        Copy(p.SystemVolumeDownHotkey, settings.SaveSystemVolumeDownHotkey);
        Copy(p.SystemMuteToggleHotkey, settings.SaveSystemMuteToggleHotkey);
        Copy(p.QuickProfileSwitchHotkey, settings.SaveQuickProfileSwitchHotkey);
        Copy(p.SpeakStatusLineHotkey, settings.SaveSpeakStatusLineHotkey);
        return n;
    }

    /// <summary>Show the About box once after a SUCCESSFUL in-app update, if the user opted in. Driven by
    /// a one-shot marker the updater writes only on success (<see cref="RemSoundUpdater.WhatsNewMarkerName"/>
    /// via <see cref="WhatsNewMarker"/>) — NOT by a running-version-vs-saved-version compare, which could
    /// re-fire after a FAILED update when its best-effort flag save lost a race during the update churn
    /// (that was the bug). The marker is consumed (deleted) here exactly once. Separately records
    /// LastWhatsNewVersion as the "a version has run here" signal the keyboard-shortcut import offer uses
    /// to tell an upgrade from a fresh install. 2026-06-23.</summary>
    private void MaybeShowWhatsNewAfterUpdate()
    {
        if (IsDisposed) return;
        var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";

        var justUpdated = WhatsNewMarker.Exists(AppContext.BaseDirectory);
        var cfg = AppConfig.Load();

        if (justUpdated && cfg.ShowWhatsNewAfterUpdate)
        {
            try
            {
                logFile.Event($"what's new: opening About after a successful update (now v{current})");
                using var dlg = new AboutDialog();
                ForegroundDialog.Show(owner => dlg.ShowDialog(owner));
            }
            catch (Exception ex)
            {
                logFile.Event($"what's new: failed to open About: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Consume the marker so what's-new shows exactly once. Deleted whether or not we showed it, and
        // only ever present after a genuine success — a failed update simply has nothing here to re-trigger.
        if (justUpdated && !WhatsNewMarker.Consume(AppContext.BaseDirectory))
        {
            logFile.Event("what's new: could not delete the update marker (will re-show next launch)");
        }

        // Record "a version has run on this machine" for the upgrade-vs-fresh-install detection used by
        // MaybeOfferKeyboardShortcutImport. Best-effort; no longer drives the what's-new popup.
        if (cfg.LastWhatsNewVersion != current)
        {
            try
            {
                var fresh = AppConfig.Load();
                fresh.LastWhatsNewVersion = current;
                fresh.Save();
            }
            catch { /* harmless */ }
        }
    }

    // ===================== UI layout =====================

    private void BuildLayout()
    {
        // === Menu bar + tabbed root layout ===
        // Top: MenuStrip with the File menu (replaces the old Profiles & preferences tab —
        // profile-management actions and the cross-cutting preferences live here now).
        // Middle: TabControl with 3 pages (Connectivity, Audio I/O, Audio profile).
        // Bottom: status footer (healthLabel + statusLabel), always visible.
        //
        // 2026-05-08 refactor: dropped the fourth tab. Save / Save as / Open / Rename /
        // Min-to-tray / Keyboard shortcuts / Preferences / Exit now live in the menu bar
        // with single-press accelerators (Ctrl+S / Ctrl+K / Ctrl+P / Alt+M) instead of
        // requiring a Tab-stop journey to a dedicated tab. Mute cues + Accept remote vol +
        // Startup behaviour are now under File → Preferences (Ctrl+P).
        var rootLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
        };
        rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // menu
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // tabs
        rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // status footer

        BuildConnectivityTab();
        BuildAudioIOTab();
        BuildAudioProfileTab();
        BuildPanEqTab();

        // Add the tabs in the user's saved order (and honour the "show pan/EQ tab" toggle).
        ApplyMainTabLayout();
        // No SelectedIndexChanged handler. No focus management on tab change. Andre's
        // accessible app does ZERO event hooking on TabControl — relies entirely on
        // default WinForms + NVDA behaviour. Per Ed's repeated request: arrow keys cycle
        // tabs (focus on strip), NVDA announces the tab name as the active selection
        // changes, no auto-jumping into the page contents.

        var menu = BuildFileMenu();
        rootLayout.Controls.Add(menu, 0, 0);
        rootLayout.Controls.Add(mainTabControl, 0, 1);

        // Status footer — always visible.
        var statusPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(8, 4, 8, 4),
        };
        statusPanel.Controls.Add(healthDot);
        statusPanel.Controls.Add(healthLabel);
        statusPanel.Controls.Add(new Label { Text = "  ", AutoSize = true });
        statusPanel.Controls.Add(statusLabel);
        rootLayout.Controls.Add(statusPanel, 0, 2);

        SetTabOrder();
        Controls.Add(rootLayout);
        // The MenuStrip is added LAST so it claims the form's MainMenuStrip property. Without
        // this, the form may not auto-handle Alt-keystroke focus into the menu bar.
        MainMenuStrip = menu;
    }

    /// <summary>Build the File menu and wire each item to its action. Single-press
    /// accelerators are set via ShortcutKeys on the menu items so they fire from anywhere
    /// in the form. Alt+M (Minimise) is NOT set as a ShortcutKeys binding — it goes through
    /// ProcessCmdKey instead, gated per-tab so the Audio I/O tab's Alt+M (Audio mode) wins
    /// when that tab is active.</summary>
    private MenuStrip BuildFileMenu()
    {
        var menu = new MenuStrip { Dock = DockStyle.Top };
        var fileMenu = new ToolStripMenuItem("&File") { AccessibleName = "File menu" };
        var helpMenu = new ToolStripMenuItem("&Help") { AccessibleName = "Help menu" };

        // New profile — starts a fresh blank template as a new unsaved session. Lives at the top of
        // the File menu (the conventional New / Open / Save order) and, crucially, is reachable even
        // when "start with a specific profile" boots straight past the picker (issue #6). Mnemonic
        // Alt+F, W ('w' — N is taken by Minimise) plus the conventional Ctrl+N global shortcut.
        var newProfileItem = new ToolStripMenuItem("Ne&w profile")
        {
            ShortcutKeys = Keys.Control | Keys.N,
            AccessibleName = "New profile",
        };
        newProfileItem.Click += (_, _) => NewProfile();

        var openItem = new ToolStripMenuItem("&Open profile...")
        {
            ShortcutKeys = Keys.Control | Keys.O,
            AccessibleName = "Open profile",
        };
        openItem.Click += (_, _) => OpenProfileFromPicker();

        // Recent profiles submenu. Populated dynamically on drop-down so the latest list is
        // always shown — AppConfig.RecentProfiles is the source of truth and gets mutated on
        // every profile load. Each item gets a 1..5 single-digit mnemonic so the user can
        // pick a recent without having to read it: Alt+F, R, 1 jumps to the most recent;
        // Alt+F, R, 2 to the second-most-recent, etc.
        recentProfilesMenu = new ToolStripMenuItem("&Recent profiles")
        {
            AccessibleName = "Recent profiles",
        };
        recentProfilesMenu.DropDownOpening += (_, _) => PopulateRecentProfilesMenu();
        // Seed the submenu so it isn't visibly empty before the first DropDownOpening fires.
        PopulateRecentProfilesMenu();

        var saveItem = new ToolStripMenuItem("&Save")
        {
            ShortcutKeys = Keys.Control | Keys.S,
            AccessibleName = "Save profile",
        };
        saveItem.Click += (_, _) => SaveOrSaveAs();

        var saveAsItem = new ToolStripMenuItem("Save &as...")
        {
            AccessibleName = "Save profile as",
        };
        saveAsItem.Click += (_, _) => SaveProfileAs();

        var renameItem = new ToolStripMenuItem("Rena&me current profile...")
        {
            AccessibleName = "Rename current profile",
        };
        renameItem.Click += (_, _) => RenameCurrentProfile();

        // Lock profile (read-only). When checked, the active profile is loaded for use but
        // never written back: Save / Ctrl+S politely refuses (with a "use Save As" message)
        // and FormClosing skips the unsaved-changes prompt entirely. Andre's request — he
        // toggles send/receive on his default profile and doesn't want a save prompt
        // blocking shutdown when his screen reader can't reach it. Off by default; the
        // flag is per-profile (stored in the profile JSON) so different profiles can
        // independently choose lock vs editable.
        //
        // CheckOnClick = true makes WinForms flip the .Checked state on every click and
        // NVDA reads "Lock profile read-only, checked / not checked". The mnemonic Alt+F, L
        // doesn't collide with any existing File-menu letter (O / R / S / A / M / N / X
        // are in use).
        lockProfileMenuItem = new ToolStripMenuItem("&Lock profile (read-only)")
        {
            AccessibleName = "Lock profile read-only",
            CheckOnClick = true,
            Checked = currentProfileReadOnly,
        };
        lockProfileMenuItem.CheckedChanged += (_, _) =>
        {
            if (suppressLockProfileToggleHandler) return;
            OnLockProfileToggled(lockProfileMenuItem.Checked);
        };

        // Change this profile's encryption password. Alt+F, P — 'p' is free in the File menu
        // (O / R / S / A / M / L / N / X are taken). Opens a small dialog showing the current
        // password (in plain text, so a screen reader can read it) with OK / Cancel.
        var changePasswordItem = new ToolStripMenuItem("Change this profile's &password...")
        {
            AccessibleName = "Change this profile's password",
        };
        changePasswordItem.Click += (_, _) => ChangeProfilePassword();

        var minimiseItem = new ToolStripMenuItem("Mi&nimise to tray")
        {
            // No global ShortcutKeys binding — the in-app menu mnemonic (Alt+F → N now —
            // moved off M because the Rename item took the M slot in the 2026-05-15 menu
            // reorg) plus the configurable "Show or hide window" hotkey cover this. Pre-
            // 2026-05-11 Alt+M was gated per-tab via ProcessCmdKey because the Audio I/O
            // tab had an "Audio mode" listbox that used Alt+M; that listbox is gone now
            // so the gating was retired.
            AccessibleName = "Minimise to tray",
        };
        minimiseItem.Click += (_, _) => trayController.Minimize();

        var exitItem = new ToolStripMenuItem("E&xit")
        {
            AccessibleName = "Exit RemSound",
        };
        exitItem.Click += (_, _) => Close();

        fileMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            newProfileItem,
            openItem,
            recentProfilesMenu,
            saveItem,
            saveAsItem,
            renameItem,
            lockProfileMenuItem,
            changePasswordItem,
            new ToolStripSeparator(),
            minimiseItem,
            new ToolStripSeparator(),
            exitItem,
        });

        // === Options menu (new, 2026-05-15) ===
        // Holds all the "configure the app" entry points that used to be scattered across
        // the File menu (Keyboard shortcuts, Preferences) and the Record menu (Recording
        // settings). Startup behaviour is also here as its own top-level item rather than
        // hiding inside Preferences as it did before. Reads as a natural sequence:
        // recording-specific → input config → startup → general prefs.
        //
        // Mnemonic Alt+O — natural for "Options". Required moving the Record menu off of
        // Alt+O (it's now Alt+K — see comment in BuildRecordMenu); the trade reads more
        // naturally for users because "Options" is exactly what's in the menu.
        var optionsMenu = new ToolStripMenuItem("&Options") { AccessibleName = "Options menu" };

        var recordingSettingsItem = new ToolStripMenuItem("Recording &settings...")
        {
            AccessibleName = "Recording settings",
        };
        recordingSettingsItem.Click += (_, _) => OpenRecordingSettingsDialog();

        var keyboardItem = new ToolStripMenuItem("&Keyboard shortcuts...")
        {
            ShortcutKeys = Keys.Control | Keys.K,
            AccessibleName = "Keyboard shortcuts",
        };
        keyboardItem.Click += (_, _) => hotkeyController.ShowKeyboardShortcutsDialog(this);

        // Startup behaviour moved into the Preferences dialog (its own tab) on 2026-06-13; the
        // Options-menu item and the standalone StartupBehaviourDialog are retired.

        var prefsItem = new ToolStripMenuItem("&Preferences...")
        {
            ShortcutKeys = Keys.Control | Keys.P,
            AccessibleName = "Preferences",
        };
        prefsItem.Click += (_, _) => OpenPreferencesDialog();

        // Password manager — list every profile with its password, edit any of them in one place.
        // 'w' mnemonic (pass&words) is free in the Options menu (s / K / t / P are taken).
        var profilePasswordsItem = new ToolStripMenuItem("Profile pass&words...")
        {
            AccessibleName = "Profile passwords",
        };
        profilePasswordsItem.Click += (_, _) => OpenProfilePasswordManager();

        // Manage the machine-wide named-peers book. 'n' mnemonic (ma&nage/&named) is free in Options.
        var manageNamedPeersItem = new ToolStripMenuItem("Manage &named peers...")
        {
            AccessibleName = "Manage named peers",
        };
        manageNamedPeersItem.Click += (_, _) => ShowManageNamedPeersDialog();

        // Realtek ASIO enable/disable toggle — only present when a Realtek ASIO driver is actually
        // installed. Lets the user reverse the disable decision (or disable a driver they kept).
        ToolStripMenuItem? realtekToggle = null;
        if (realtekAsioDriverNames.Count > 0)
        {
            // AccessibleName is set (alongside Text) by UpdateRealtekAsioMenuItemText so the screen
            // reader hears "Enable"/"Disable", matching what's shown — never "Toggle".
            realtekToggle = new ToolStripMenuItem();
            realtekToggle.Click += (_, _) => ToggleRealtekAsio();
            realtekAsioToggleItem = realtekToggle;
            UpdateRealtekAsioMenuItemText();
        }

        // Install / uninstall RemSound as a proper per-user Windows app. The single item flips to
        // "Uninstall…" when this copy IS the installed one. Copies files to %LOCALAPPDATA%\Programs
        // (no admin), with optional shortcuts and login auto-start. Modelled on Andre's Sensor Readout.
        ToolStripMenuItem installItem;
        if (AppInstaller.IsInstalledCopy)
        {
            installItem = new ToolStripMenuItem("&Uninstall RemSound from this PC...")
            {
                AccessibleName = "Uninstall RemSound from this PC",
            };
            installItem.Click += (_, _) => AppInstaller.RunUninstallInProcess(this, msg => logFile.Event($"installer: {msg}"));
        }
        else
        {
            installItem = new ToolStripMenuItem("&Install RemSound on this PC...")
            {
                AccessibleName = "Install RemSound on this PC",
            };
            installItem.Click += (_, _) => AppInstaller.RunInstall(this, msg => logFile.Event($"installer: {msg}"));
        }

        var optionItems = new List<ToolStripItem>
        {
            recordingSettingsItem,
            keyboardItem,
            profilePasswordsItem,
            manageNamedPeersItem,
        };
        if (realtekToggle is not null) optionItems.Add(realtekToggle);
        optionItems.Add(new ToolStripSeparator());
        optionItems.Add(installItem);
        optionItems.Add(prefsItem);
        optionsMenu.DropDownItems.AddRange(optionItems.ToArray());

        // Help menu — separate from File so users with their hand on Alt + arrow keys can
        // walk straight to it. F1 is the global "open the manual" key; the menu mirrors it
        // for users who prefer mouse / arrow navigation.
        var helpItem = new ToolStripMenuItem("&Help")
        {
            ShortcutKeys = Keys.F1,
            AccessibleName = "Open user manual",
        };
        helpItem.Click += (_, _) => HelpLauncher.OpenManual();

        var checkForUpdatesItem = new ToolStripMenuItem("&Check for updates")
        {
            AccessibleName = "Check for updates",
        };
        checkForUpdatesItem.Click += (_, _) => CheckForUpdatesManually();

        var aboutItem = new ToolStripMenuItem("&About RemSound")
        {
            AccessibleName = "About RemSound",
        };
        aboutItem.Click += (_, _) =>
        {
            using var dialog = new AboutDialog();
            dialog.ShowDialog(this);
        };

        helpMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            helpItem,
            checkForUpdatesItem,
            aboutItem,
        });

        var recordMenu = BuildRecordMenu();

        // Order: File / Record / Options / Help. Options sits between Record and Help per
        // user request — left-to-right reads file-management → recording-tasks → config →
        // help, which is the natural sequence for someone walking the menu bar with Alt
        // and the arrow keys.
        menu.Items.Add(fileMenu);
        menu.Items.Add(recordMenu);
        // The Service menu is shown on EVERY Windows now (2026-07-14) so a Win7 user can actually try it —
        // we don't yet know whether the service works there, and the only way to find out is to let it try.
        // This is launch-safe: BUILDING the menu references no service type (the verb strings are inlined
        // consts and the handlers are method-group lambdas), so System.ServiceProcess is NOT loaded at
        // window construction on any OS — proven by the "main window builds without loading the service
        // assembly" self-test. That assembly loads only when the user OPENS the menu (the DropDownOpening
        // status query) or runs an action, both of which are wrapped in try/catch, so if it can't load on
        // Win7 the menu degrades to "status unavailable" instead of crashing. If Win7 turns out unable to
        // run the service, re-gate this with OperatingSystem.IsWindowsVersionAtLeast(10, 0).
        menu.Items.Add(BuildServiceMenu());
        menu.Items.Add(optionsMenu);
        menu.Items.Add(helpMenu);
        return menu;
    }

    /// <summary>The Service menu: configure the send-only lock-screen service's profile, and
    /// install / uninstall / start / stop it. The status line and item enabling refresh each time the
    /// menu opens. Install/uninstall/start/stop re-launch RemSound elevated (one UAC prompt each);
    /// querying status needs no elevation.</summary>
    private ToolStripMenuItem BuildServiceMenu()
    {
        // Mnemonic is Alt+J, NOT Alt+S: the always-visible "Send my audio" checkbox already owns Alt+S, and
        // a visible control beats a top-level menu for the same Alt key (that's why Alt+S didn't open this
        // menu). J is unused anywhere in the main window, so it opens the menu reliably from every tab —
        // same reason the Record menu uses "(Alt+&K)". Every letter in "Service" (S/e/r/v/i/c) collides with
        // a control (Send/Receive/Volume/…). The "Menu shortcuts don't clash with controls" self-test guards this.
        var serviceMenu = new ToolStripMenuItem("Service (Alt+&J)") { AccessibleName = "Service menu" };
        var status = new ToolStripMenuItem("Service: …") { Enabled = false, AccessibleName = "Service status" };
        var configure = new ToolStripMenuItem("&Configure service profile...") { AccessibleName = "Configure service profile" };
        configure.Click += (_, _) => ConfigureServiceProfile();
        var install = new ToolStripMenuItem("&Install service") { AccessibleName = "Install service" };
        install.Click += (_, _) => ServiceAction(ServiceControl.InstallVerb, "install", confirm: true);
        var uninstall = new ToolStripMenuItem("&Uninstall service") { AccessibleName = "Uninstall service" };
        uninstall.Click += (_, _) => ServiceAction(ServiceControl.UninstallVerb, "uninstall", confirm: true);
        var start = new ToolStripMenuItem("S&tart service") { AccessibleName = "Start service" };
        start.Click += (_, _) => ServiceAction(ServiceControl.StartVerb, "start", confirm: false);
        var stop = new ToolStripMenuItem("Sto&p service") { AccessibleName = "Stop service" };
        stop.Click += (_, _) => ServiceAction(ServiceControl.StopVerb, "stop", confirm: false);
        var activityLog = new ToolStripMenuItem("&View service log") { AccessibleName = "View service log" };
        activityLog.Click += (_, _) => OpenServiceLog();
        var updateLog = new ToolStripMenuItem("View service update &log") { AccessibleName = "View service update log" };
        updateLog.Click += (_, _) => OpenServiceUpdateLog();

        serviceMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            status, new ToolStripSeparator(),
            configure, new ToolStripSeparator(),
            install, uninstall, start, stop, new ToolStripSeparator(),
            activityLog, updateLog,
        });
        serviceMenu.DropDownOpening += (_, _) =>
        {
            // Never let a status-query failure crash the menu (and with it the app). The menu only appears
            // on Win10+ where the service assembly loads fine, but a defensive net here is cheap insurance.
            try
            {
                var state = ServiceControl.Query();
                status.Text = "Service: " + DescribeServiceState(state);
                // Surface the running version + when it (re)started, so a self-update is visible at a glance.
                if (state is ServiceState.Running or ServiceState.Stopped && ServiceStore.LoadStatus() is { Version: { } ver } st)
                {
                    status.Text += $" — version {ver}";
                    if (state == ServiceState.Running && st.StartedUtc != default) status.Text += $", running since {DescribeAgo(st.StartedUtc)}";
                }
                var installed = state != ServiceState.NotInstalled;
                install.Enabled = !installed;
                uninstall.Enabled = installed;
                start.Enabled = installed && state is ServiceState.Stopped;
                stop.Enabled = installed && state is ServiceState.Running;
            }
            catch (Exception ex)
            {
                // Show the reason right in the status line (a screen reader reads it), AND record it to the
                // always-on service events log — so even with app logging off (e.g. a Win7 tester) we learn
                // WHY the service machinery couldn't load, not just that it didn't.
                status.Text = $"Service: unavailable — {ex.GetType().Name}: {ex.Message}";
                logFile.Event($"service menu: status query failed {ex.GetType().Name}: {ex.Message}");
                ServiceStore.AppendServiceEvent($"menu status query FAILED: {ex.GetType().Name}: {ex.Message}");
            }
        };
        return serviceMenu;
    }

    /// <summary>Opens the service's DIAGNOSTIC log — the "what is the service doing / why isn't it sending"
    /// activity log, which records the streaming decision on every resume (e.g. "streaming N sources to M
    /// peers", or "profile has no WASAPI send sources"). Distinct from the update log (self-updates only).
    /// The log is written only while service logging is enabled (Configure service profile → Logging), so if
    /// there's nothing here yet, that's the first thing to turn on.</summary>
    private void OpenServiceLog()
    {
        // Prefer the service's runtime diagnostic log (only exists if the service actually ran with logging
        // on); otherwise fall back to the ALWAYS-ON service events log, which records every menu/install/
        // start/stop and any failure reason — so there's a trail to view even if nobody enabled logging.
        var path = ServiceStore.NewestLogFile();
        if (path is null && File.Exists(ServiceStore.ServiceEventsLogPath)) path = ServiceStore.ServiceEventsLogPath;
        if (path is null)
        {
            var msg = ServiceStore.LoadLoggingEnabled()
                ? "No service log yet. The service writes one once it starts with logging on — start (or restart) the service, then check back here. (The service events log appears here too once you install/start it.)"
                : "No service log yet. Once you install or start the service, its events (and any failure reason) are recorded here automatically. For the fuller runtime log, also turn on logging: Service menu → Configure service profile → Logging tab.";
            MessageBox.Show(this, msg, AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, $"Could not open the service log ({path}): {ex.Message}", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void OpenServiceUpdateLog()
    {
        var path = ServiceStore.UpdateLogPath;
        if (!File.Exists(path))
        {
            MessageBox.Show(this, "No service update log yet — it's written the first time the service updates itself. (For what the service is doing day to day, use \"View service log\" instead.)", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, $"Could not open the update log ({path}): {ex.Message}", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private static string DescribeAgo(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} h ago";
        return $"{(int)span.TotalDays} d ago";
    }

    private static string DescribeServiceState(ServiceState s) => s switch
    {
        ServiceState.NotInstalled => "not installed",
        ServiceState.Running => "installed, running",
        ServiceState.Stopped => "installed, stopped",
        ServiceState.StartPending => "starting…",
        ServiceState.StopPending => "stopping…",
        _ => "unknown",
    };

    /// <summary>Opens the modal service-profile editor, then persists the result: saves the reserved
    /// service profile, points AppConfig at it, and stores the machine-wide service-logging choice. If
    /// the service is running, restarts it so the edits take effect.</summary>
    private void ConfigureServiceProfile()
    {
        // The service profile lives in the machine-wide ServiceStore (ProgramData), NOT the user's
        // profiles folder — so it's readable by the SYSTEM service and fully isolated from the picker,
        // recents and password manager. Migrate a profile left in the old (user-folder) location by the
        // earlier design so a user who configured it before doesn't lose their settings.
        var current = ServiceStore.LoadProfile();
        if (current is null && profileStore is not null)
            try { current = profileStore.Load(ServiceControl.ServiceProfileTitle); } catch { /* none */ }
        current ??= Profile.NewBlank();

        using var dlg = new ServiceProfileDialog(current, ServiceStore.LoadLoggingEnabled());
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            ServiceStore.SaveProfile(dlg.Result);
            ServiceStore.SaveLoggingEnabled(dlg.ServiceLoggingEnabled);
            logFile.Event($"service: profile saved (service logging {(dlg.ServiceLoggingEnabled ? "on" : "off")})");
            // Remove any stray copy the old design left in the user's profiles folder.
            try { profileStore?.Delete(ServiceControl.ServiceProfileTitle); } catch { /* best-effort */ }
            var restartNeeded = ServiceControl.Query() == ServiceState.Running;
            if (restartNeeded)
            {
                // Restart OFF the UI thread — the old inline RunElevated stop+start pair here blocked the
                // window (and its audio) through two UAC prompts; same bug class as the install hang.
                // No-UAC first: the installer granted this account start/stop rights, so a plain SCM
                // restart normally needs no elevation at all. Elevated verbs are the fallback (service
                // installed by a different account, grant missing). Only a FAILURE is reported back;
                // success needs no second popup.
                ServiceStore.AppendServiceEvent("restart requested (service profile changed)");
                Task.Run(() =>
                {
                    var ok = ServiceControl.TryRestartNoAdmin();
                    if (!ok)
                    {
                        ServiceControl.RunElevated(ServiceControl.StopVerb);
                        ok = ServiceControl.RunElevated(ServiceControl.StartVerb) == 0;
                    }
                    ServiceStore.AppendServiceEvent(ok
                        ? "restart finished (new service profile is live)"
                        : "restart FAILED after profile change");
                    if (ok || IsDisposed) return;
                    try
                    {
                        BeginInvoke(new Action(() => MessageBox.Show(this,
                            "The service profile was saved, but the running service could not be restarted to pick it up. Use the Service menu to stop and start it.",
                            AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning)));
                    }
                    catch { /* window closing */ }
                });
            }
            MessageBox.Show(this,
                restartNeeded
                    ? "Service profile saved. The running service is restarting to pick it up."
                    : "Service profile saved.",
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save the service profile: {ex.Message}", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ServiceAction(string verb, string label, bool confirm)
    {
        if (confirm)
        {
            var msg = verb == ServiceControl.InstallVerb
                ? "Install the RemSound send-only service? It will start automatically at boot and stream your service profile whenever you're not using RemSound normally.\n\nWindows will ask for administrator permission."
                : "Uninstall the RemSound service?\n\nWindows will ask for administrator permission.";
            if (MessageBox.Show(this, msg, AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        }
        RunServiceVerbAsync(verb, label);
    }

    /// <summary>Run an elevated service verb OFF the UI thread and report the outcome on it. A slow or
    /// stuck helper must never freeze the window or its audio — that was the install hang: the UI thread
    /// sat in WaitForExit while the pipe-deadlocked installer never returned, so the app locked up and
    /// streaming died. The UI stays live; we report when the helper comes back or the wait times out.</summary>
    private void RunServiceVerbAsync(string verb, string label)
    {
        logFile.Event($"service: {label} requested (elevated)");
        ServiceStore.AppendServiceEvent($"{label} requested (elevated)");
        Task.Run(() =>
        {
            var rc = ServiceControl.RunElevated(verb);
            if (IsDisposed) return;
            try { BeginInvoke(new Action(() => ReportServiceActionResult(label, rc))); } catch { /* window closing */ }
        });
    }

    private void ReportServiceActionResult(string label, int rc)
    {
        var outcome = rc == 0 ? "success"
            : rc == -1 ? "cancelled/declined"
            : rc == ServiceControl.ElevatedTimedOut ? "timed out"
            : "failed";
        logFile.Event($"service: {label} finished with code {rc} ({outcome})");
        ServiceStore.AppendServiceEvent($"{label} finished: code {rc} ({outcome})");
        if (rc == 0)
        {
            // After a successful install, offer to start it now — it otherwise only starts at the next
            // boot, so a first-time user would see nothing happen.
            if (label == "install")
            {
                var startNow = MessageBox.Show(this,
                    "The RemSound service was installed. Do you want to start it now?\n\nIt will also start automatically at every boot.",
                    AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (startNow == DialogResult.Yes) RunServiceVerbAsync(ServiceControl.StartVerb, "start");
                return;
            }
            MessageBox.Show(this, $"Service {label} succeeded.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else if (rc == -1)
            MessageBox.Show(this, $"Service {label} was cancelled, or administrator rights were declined.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else if (rc == ServiceControl.ElevatedTimedOut)
            MessageBox.Show(this, $"Service {label} is taking longer than expected and hasn't finished yet. It may still complete on its own — check the Service menu status in a moment.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else
            MessageBox.Show(this, $"Service {label} failed (code {rc}).", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>Rebuild the Recent profiles submenu from <see cref="AppConfig.RecentProfiles"/>.
    /// Called once during menu construction (so it's not visibly empty before the first
    /// open) and on every DropDownOpening so the latest list is always shown. Entries that
    /// reference a profile file that no longer exists on disk are skipped — the path stays
    /// in the AppConfig list (it might come back, e.g. external drive remount) but doesn't
    /// clutter the menu.
    ///
    /// Mnemonic / numeric-pick convention: each item is prefixed with "&N" where N is 1..5
    /// for the position. Pressing the digit while the submenu is open selects that item.
    /// The most-recently-opened profile is &1 (top); oldest in the list is &5 (bottom).</summary>
    private void PopulateRecentProfilesMenu()
    {
        if (recentProfilesMenu is null) return;
        recentProfilesMenu.DropDownItems.Clear();
        var cfg = AppConfig.Load();
        var slot = 1;
        foreach (var path in cfg.RecentProfiles)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (!File.Exists(path)) continue; // skip missing files; keep in storage in case they reappear
            var title = Path.GetFileNameWithoutExtension(path);
            // Visible Text has the &1..&5 mnemonic for number-key access; AccessibleName is
            // just the profile name so NVDA reads the menu item naturally rather than
            // prefixing every entry with "Recent profile N:" (which was the original cut
            // and Ed flagged it as noisy / unwanted).
            var item = new ToolStripMenuItem($"&{slot} {title}")
            {
                AccessibleName = title,
                // Stash the path on the menu item so the click handler doesn't depend on
                // closure capture of the loop variable.
                Tag = path,
            };
            item.Click += (s, _) =>
            {
                var sender = (ToolStripMenuItem)s!;
                var profilePath = (string)sender.Tag!;
                SwitchToRecentProfile(profilePath);
            };
            recentProfilesMenu.DropDownItems.Add(item);
            slot++;
            if (slot > AppConfig.MaxRecentProfiles) break;
        }
        if (recentProfilesMenu.DropDownItems.Count == 0)
        {
            recentProfilesMenu.DropDownItems.Add(new ToolStripMenuItem("(No recent profiles)")
            {
                Enabled = false,
                AccessibleName = "No recent profiles",
            });
        }
    }

    // Carried across the close-and-relaunch profile switch (static so the NEXT MainForm instance,
    // built by Program.Main after this one closes, can read it). A switch done while RemSound was
    // in the tray should land back in the tray; internal so Program.Main can also skip the
    // "loading audio driver" splash in that case.
    internal static bool startNextInstanceMinimized;

    // Set from the --foreground switch, which the post-install relaunch passes. Makes the next
    // MainForm pull itself to the front and take focus even past Windows' foreground lock, so the
    // freshly-installed copy doesn't open behind other windows and leave the user hunting for it.
    // One-shot: consumed (cleared) the first time a window honours it.
    internal static bool forceForegroundOnStart;

    // True until the first MainForm of the process has shown its window. Distinguishes a genuine
    // cold launch (where the "Start minimised" preference applies) from an in-session new-profile or
    // profile-switch relaunch (where it must NOT — those would otherwise hide the window and look
    // like a crash, issue #12). Flipped false in the first OnShown.
    private static bool isFirstLaunch = true;

    /// <summary>Switch to the profile at <paramref name="path"/> via the same close-and-relaunch
    /// flow OpenProfileFromPicker uses. The active profile gets pushed to the front of the
    /// recents list by the next MainForm constructor when it sees the loaded path.</summary>
    private void SwitchToRecentProfile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (string.Equals(path, currentProfilePath, StringComparison.OrdinalIgnoreCase)) return; // already loaded
        if (!File.Exists(path))
        {
            MessageBox.Show(this,
                $"Profile file no longer exists:\n\n{path}\n\nIt'll be removed from the Recent profiles list.",
                "Recent profile", MessageBoxButtons.OK, MessageBoxIcon.Information);
            // Trim the dead entry out of the recents list so the user doesn't keep seeing it.
            var cfg = AppConfig.Load();
            cfg.RecentProfiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            try { cfg.Save(); } catch { /* benign — list will be re-pruned at next attempt */ }
            return;
        }
        var title = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(title)) return;
        // Play the switch cue NOW, on click, for immediate feedback — CuePlayer.Play is fire-and-
        // forget on its own thread + device, so it survives the form rebuild that follows. Covers
        // BOTH the Recent-profiles menu and the quick-switch popup (both route through here). The
        // rebuilt form deliberately does NOT replay it, so startup into the first profile is silent.
        if (settings.LoadEnableProfileSwitchCue())
        {
            profileSwitchSound?.Play();
        }
        NextProfilePathToLoad = path;
        NextProfileTitleToLoad = title;
        // If the switch was triggered while the window was minimised / in the tray (the quick-
        // switch hotkey can fire from anywhere), keep the rebuilt instance in the tray too rather
        // than popping the window up in front of whatever the user is doing.
        startNextInstanceMinimized = !Visible || WindowState == FormWindowState.Minimized;
        AppendLogEntry($"profile switch via Recent profiles: \"{title}\" from {path}");
        Close();
    }

    /// <summary>
    /// Opens the Quick profile switch popup (bound to the global quick-switch hotkey): an
    /// NVDA-friendly, foreground-activated list of every profile with the current one marked.
    /// Plays the "profile menu open" cue as it appears (honouring its mute toggle); choosing a
    /// profile reloads into it — which plays the profile-switch cue on the relaunch. Escape, Close,
    /// or picking the already-current profile does nothing.
    /// </summary>
    private void ShowQuickProfileSwitch()
    {
        try
        {
            var store = profileStore;
            if (store is null) return;
            var titles = store.ListProfileTitles();
            if (titles.Count == 0) return;

            var entries = new List<QuickProfileSwitchDialog.ProfileEntry>(titles.Count);
            foreach (var title in titles)
            {
                var path = store.PathFor(title);
                var isCurrent = !string.IsNullOrEmpty(currentProfilePath)
                    && string.Equals(path, currentProfilePath, StringComparison.OrdinalIgnoreCase);
                entries.Add(new QuickProfileSwitchDialog.ProfileEntry(title, path, isCurrent));
            }

            if (settings.LoadEnableProfileMenuOpenCue())
            {
                profileMenuOpenSound?.Play();
            }

            var chosen = QuickProfileSwitchDialog.Show(entries);
            if (!string.IsNullOrEmpty(chosen))
            {
                // SwitchToRecentProfile plays the switch cue on click, keeps the window in the
                // tray if it was there, and no-ops if the chosen profile is already current.
                SwitchToRecentProfile(chosen);
            }
        }
        catch (Exception ex)
        {
            logFile.Event($"quick profile switch failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>File → New profile. Starts a fresh blank template as a new unsaved session — the way
    /// to create a profile from scratch even when "start with a specific profile" boots the user
    /// straight past the picker (issue #6). Offers to save the current profile first if it has
    /// unsaved changes, then hands off to Program.cs's loop via <see cref="LoadBlankTemplateNext"/>.</summary>
    private void NewProfile()
    {
        // Don't silently lose unsaved work when abandoning the current session for a blank one.
        if (unsavedChanges && profileStore is not null && !currentProfileReadOnly)
        {
            var result = MessageBox.Show(this,
                "You have unsaved changes to your current profile. Save them before starting a new profile?\n\n" +
                "Yes — save, then start a new profile.\nNo — discard the changes and start a new profile.\nCancel — stay where you are.",
                "RemSound — unsaved changes",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button3);
            if (result == DialogResult.Cancel) return;
            if (result == DialogResult.Yes)
            {
                if (string.IsNullOrEmpty(currentProfileTitle))
                {
                    // Current session is itself a blank template — needs a name before it can be saved.
                    var saveTitle = ProfileSaveAsPrompt.Show(this, profileStore, null);
                    if (string.IsNullOrEmpty(saveTitle)) return; // cancelled the name prompt → abort the whole thing
                    SaveProfileTo(saveTitle, showConfirmation: false);
                }
                else
                {
                    SaveProfileTo(currentProfileTitle, showConfirmation: false);
                }
            }
            // No → fall through, discarding the unsaved changes.
        }

        // No profile-switch cue here — deliberately. Unlike Recent/Open (which play it on click),
        // opening a fresh blank template should be silent; the switch sound feels wrong for "start
        // new" (Ed, 2026-06-11). The rebuilt form never replays the cue either (see OnShown), so the
        // entire New-profile path stays quiet.
        LoadBlankTemplateNext = true;
        // Stay in the tray if we were there, mirroring the quick-switch behaviour.
        startNextInstanceMinimized = !Visible || WindowState == FormWindowState.Minimized;
        AppendLogEntry("new profile: loading blank template");
        Close();
    }

    /// <summary>Build the Record menu — Start/stop recording (toggling label), recording
    /// settings dialog, open the configured folder, and change the configured folder.
    /// Ctrl+R is the global toggle so the user can start/stop without going through the
    /// menu. Profile-dirty flag is set when the user changes the folder or the settings
    /// inside the sub-dialog because both live on the profile.</summary>
    private ToolStripMenuItem BuildRecordMenu()
    {
        // Record menu uses Alt+K. The natural "R" letter is taken on the main form by the
        // Receive audio checkbox; "O" is now claimed by the Options menu (2026-05-15
        // reorg). K isn't a letter in "Record", so we surface the mnemonic explicitly in
        // the title: "Record (Alt+K)" with the K underlined. The visible hint keeps the
        // chord discoverable for keyboard-only users despite the unusual letter choice.
        //
        // This collides with the Lock-to-audio-clock checkbox on the Audio profile tab
        // which used to take Alt+K — the menu always wins at the form's top level, so the
        // checkbox loses its mnemonic and stays Tab-reachable only. The (Alt+&K) hint on
        // that checkbox's text was removed below to avoid a misleading prompt.
        var recordMenu = new ToolStripMenuItem("Record (Alt+&K)") { AccessibleName = "Record menu" };

        // Start/Stop uses Alt+R — matches the Ctrl+R global toggle so the same letter does
        // the same job from either entry point. The "&" position shifts when the label flips
        // (Sta&rt → Stop &recording) so the underline stays on an R in both states. See
        // UpdateStartStopRecordingMenuLabel for the runtime label flip.
        startStopRecordingMenuItem = new ToolStripMenuItem("Sta&rt recording")
        {
            ShortcutKeys = Keys.Control | Keys.R,
            AccessibleName = "Start recording",
        };
        startStopRecordingMenuItem.Click += (_, _) => ToggleRecording();

        var openFolderItem = new ToolStripMenuItem("&Open current recordings folder")
        {
            AccessibleName = "Open current recordings folder",
        };
        openFolderItem.Click += (_, _) => recordingController.OpenCurrentFolder(this);

        var changeFolderItem = new ToolStripMenuItem("&Change recordings folder...")
        {
            AccessibleName = "Change recordings folder",
        };
        changeFolderItem.Click += (_, _) =>
        {
            if (recordingController.ChangeFolder(this)) MarkProfileDirty();
        };

        // Recording settings used to live here as the third item with Alt+S; in the
        // 2026-05-15 menu reorg it moved out to the Options menu so all of the "configure
        // the app" affordances live together. The Record menu now only carries the start /
        // stop toggle plus the two folder operations — actions you perform AT recording
        // time, not configuration.
        recordMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            startStopRecordingMenuItem,
            new ToolStripSeparator(),
            openFolderItem,
            changeFolderItem,
        });

        return recordMenu;
    }

    /// <summary>Toggle the recording state. Single source of truth for both Ctrl+R and the
    /// menu-item click — both paths route through here so the start/stop transition is
    /// handled consistently. The state-change event fires UpdateStartStopRecordingMenuLabel
    /// which rewrites the menu item text.</summary>
    private void ToggleRecording()
    {
        if (recordingController.IsRecording)
        {
            // Stop the recorder FIRST, then play the cue. SoundPlayer goes through the
            // default Windows output device — separate from the internal taps the recorder
            // listens on — so the cue isn't in the file regardless of ordering, but
            // stopping first means a user with a WASAPI-loopback-of-default-output capture
            // source won't catch the tail of the cue either.
            recordingController.Stop();
            if (settings.LoadEnableRecordStopCue()) recordStopSound?.Play();
        }
        else
        {
            // Symmetric: play the start cue BEFORE the recorder turns on, for the same
            // loopback-courtesy reason. The cue is short (~0.4 s), so any subjective lag
            // between "I pressed Ctrl+R" and "audio starts being captured" is well under
            // the cue itself.
            if (settings.LoadEnableRecordStartCue()) recordStartSound?.Play();
            recordingController.Start();
        }
    }

    /// <summary>Reflect the recording state in the menu item label. NVDA reads the text +
    /// AccessibleName, both flipped here so users on screen readers hear the new state
    /// straight away. Marshalled to the UI thread because the recorder's finish callback
    /// can fire from its writer thread when Stop() is called from there.</summary>
    private void UpdateStartStopRecordingMenuLabel(bool nowRecording)
    {
        void Apply()
        {
            if (startStopRecordingMenuItem is null) return;
            // Mnemonic stays on an "R" in both states: "Sta&rt recording" (Alt+R activates
            // the R in Start) when not recording, "Stop &recording" (Alt+R activates the R
            // in recording) when recording. Same keystroke does the same job in both states
            // — matches the Ctrl+R global toggle.
            startStopRecordingMenuItem.Text = nowRecording ? "Stop &recording" : "Sta&rt recording";
            startStopRecordingMenuItem.AccessibleName = nowRecording ? "Stop recording" : "Start recording";
        }
        if (InvokeRequired) BeginInvoke(Apply);
        else Apply();
    }

    /// <summary>Open the recording settings dialog. On OK, write the settings back through
    /// <see cref="RemSoundSettingsStore"/> and flag the profile dirty if anything changed.
    /// The dialog reads its initial state from the same store, so settings persist across
    /// re-opens until the user explicitly saves the profile.</summary>
    private void OpenRecordingSettingsDialog()
    {
        using var dialog = new RecordingSettingsDialog(settings.LoadRecordingSettings());
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        settings.SaveRecordingSettings(dialog.Result);
        if (dialog.ChangedAnything) MarkProfileDirty();
    }

    /// <summary>Show a file-picker rooted at the profiles folder; on selection, schedule a
    /// switch to that profile (same close-and-relaunch flow as the old Switch button).</summary>
    private void OpenProfileFromPicker()
    {
        if (profileStore is null) return;
        using var dialog = new OpenFileDialog
        {
            Title = "Open profile",
            Filter = "RemSound profiles (*.json)|*.json",
            InitialDirectory = profileStore.BaseDirectory,
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var pickedPath = dialog.FileName;
        var picked = Path.GetFileNameWithoutExtension(pickedPath);
        if (string.IsNullOrEmpty(picked)) return;
        if (string.Equals(pickedPath, currentProfilePath, StringComparison.OrdinalIgnoreCase)) return; // already loaded
        // Switch cue on click (same rationale as SwitchToRecentProfile).
        if (settings.LoadEnableProfileSwitchCue())
        {
            profileSwitchSound?.Play();
        }
        // Always pass the full path through. Program.cs deserialises directly from this
        // path, so profiles saved outside the active BaseDirectory still load correctly.
        NextProfilePathToLoad = pickedPath;
        NextProfileTitleToLoad = picked;
        AppendLogEntry($"profile open requested: \"{picked}\" from {pickedPath}");
        Close();
    }

    /// <summary>Ctrl+S / File → Save behaviour: if a profile is currently loaded, overwrite
    /// it; if we're on the blank template (no current profile), fall through to Save as.
    ///
    /// Read-only profiles: the lock suppresses the automatic "you have unsaved changes"
    /// prompt on close / profile switch (the user has declared "anything I changed this
    /// session is throwaway"), but it does NOT block explicit Ctrl+S / File → Save — if the
    /// user asks to save on purpose, the save goes through. First time they do this we show
    /// a one-time warning explaining the situation, with a "Do not show again" tick so the
    /// warning self-suppresses for power users. Changed 2026-05-23 from the v2.x hard-block
    /// behaviour after Ed's feedback that the lock should protect against accident, not
    /// against intent.</summary>
    private void SaveOrSaveAs()
    {
        if (currentProfileReadOnly)
        {
            if (!AppConfig.Load().SaveOnReadOnlyWarningSuppressed)
            {
                if (!ShowSaveOnReadOnlyWarningDialog()) return;
            }
            // Read-only profiles always have a title — read-only is meaningless on the blank
            // template — so we go straight to UpdateExistingProfile without the
            // string-null-check that the unlocked path needs.
            UpdateExistingProfile();
            return;
        }
        if (string.IsNullOrEmpty(currentProfileTitle)) SaveProfileAs();
        else UpdateExistingProfile();
    }

    /// <summary>Native TaskDialog warning the user that they're about to overwrite a profile
    /// marked read-only. Returns true if the user confirmed the save, false if they
    /// cancelled. Verification checkbox lets the user suppress future occurrences via
    /// <see cref="AppConfig.SaveOnReadOnlyWarningSuppressed"/>; same shape as
    /// <see cref="ShowSaveConfirmationDialog"/>. NVDA reads the heading + body + checkbox
    /// as part of the normal tab order. 2026-05-23 (rewrite of the v2.x hard-block dialog).
    /// </summary>
    private bool ShowSaveOnReadOnlyWarningDialog()
    {
        var verification = new TaskDialogVerificationCheckBox("Do not show me this message again");
        var saveButton = new TaskDialogButton("&Save anyway");
        var cancelButton = new TaskDialogButton("&Cancel") { AllowCloseDialog = true };
        var page = new TaskDialogPage
        {
            Caption = AppName,
            Heading = "Saving onto a read-only profile",
            Text = "You're about to save changes onto a profile that's marked as read-only. "
                + "RemSound allows this because you asked to save on purpose — the lock only "
                + "stops the automatic \"save your changes?\" prompt; it doesn't stop you "
                + "saving when you mean to.\n\n"
                + "Click Save anyway to overwrite this profile, or Cancel and use "
                + "File → Save as... if you'd rather save your changes to a new profile.",
            Icon = TaskDialogIcon.Warning,
            Verification = verification,
            Buttons = { saveButton, cancelButton },
            DefaultButton = cancelButton,
            AllowCancel = true,
        };
        var clicked = TaskDialog.ShowDialog(this, page);
        if (verification.Checked)
        {
            var cfg = AppConfig.Load();
            cfg.SaveOnReadOnlyWarningSuppressed = true;
            try { cfg.Save(); } catch { /* harmless — preference just won't persist */ }
            AppendLogEntry("save-on-read-only warning suppressed by user");
        }
        return clicked == saveButton;
    }

    /// <summary>Rename the currently-active profile JSON on disk. No-op on the blank
    /// template (nothing to rename). Renames update window title + active-profile state
    /// in place — no reload required.</summary>
    private void RenameCurrentProfile()
    {
        if (profileStore is null) return;
        if (string.IsNullOrEmpty(currentProfileTitle))
        {
            MessageBox.Show(this, "There is no active profile to rename. Use File → Save as to save the current state under a name first.",
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var oldTitle = currentProfileTitle;
        // Rename uses the simple text-prompt dialog (no overwrite check — pass store=null —
        // because rename has its own conflict path: profileStore.Rename returns false when
        // the new name already exists, and we surface a popup below).
        var newTitle = ProfileSaveAsPrompt.Show(
            this,
            store: null,
            defaultName: oldTitle,
            dialogTitle: "Rename profile",
            promptLabel: "Please enter a new name for your profile:");
        if (string.IsNullOrWhiteSpace(newTitle) || string.Equals(newTitle, oldTitle, StringComparison.Ordinal)) return;

        // Rename in the directory the profile actually lives in, NOT in BaseDirectory. The
        // active profile may have been Save-As'd to an arbitrary path on a previous step,
        // and Rename has to follow it. Falls back to BaseDirectory only when we somehow
        // don't have a path tracked (shouldn't happen if currentProfileTitle is non-empty).
        var oldPath = currentProfilePath ?? profileStore.PathFor(oldTitle);
        var directory = Path.GetDirectoryName(oldPath) ?? profileStore.BaseDirectory;
        // Re-encode the new title via PathFor's sanitiser so file-invalid characters get
        // stripped consistently with how every other save path names files.
        var sanitisedNewName = Path.GetFileName(profileStore.PathFor(newTitle));
        var newPath = Path.Combine(directory, sanitisedNewName);

        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            // Same filename after sanitisation — nothing to do.
            return;
        }
        if (File.Exists(newPath))
        {
            MessageBox.Show(this,
                $"A profile file named \"{sanitisedNewName}\" already exists in:\n\n{directory}\n\nChoose a different name.",
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            if (File.Exists(oldPath))
            {
                File.Move(oldPath, newPath);
            }
            else
            {
                // Old file is gone (someone deleted it externally). Just write a fresh copy
                // under the new name so the active profile still has a backing file.
                var profile = BuildCurrentProfile(newTitle);
                File.WriteAllText(newPath, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not rename \"{oldTitle}\" to \"{newTitle}\":\n\n{ex.Message}",
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        currentProfileTitle = newTitle;
        currentProfilePath = newPath;
        Text = FormatWindowTitle(newTitle);
        AccessibleName = Text;
        AppendLogEntry($"renamed profile \"{oldTitle}\" → \"{newTitle}\" (path: {newPath})");
    }

    /// <summary>Show the Preferences dialog. After it closes, mark the profile dirty if
    /// the user toggled either of the two profile-bound preferences (mute cues / accept
    /// remote vol). Startup behaviour persists outside of the profile so it doesn't
    /// trigger the dirty flag.</summary>
    private void OpenPreferencesDialog()
    {
        using var dialog = new PreferencesDialog(
            settings,
            profileStore,
            getLoggingEnabled: () => logFile.Enabled,
            applyLoggingEnabled: enabled =>
            {
                // Persist the user's choice to AppConfig — it's machine-local, not part of
                // the profile, so switching profiles doesn't change it.
                var cfg = AppConfig.Load();
                cfg.LoggingEnabled = enabled;
                try { cfg.Save(); } catch { /* harmless — choice just won't survive a restart */ }
                // Flip the gate live so the user's tick takes effect immediately. No need to
                // restart the app or reopen the log file — writes simply stop / resume mid-flight.
                logFile.Enabled = enabled;
                // Engine instrumentation rides on logging OR auto-tune — auto-tune needs the
                // same per-second diag data the log line emits, so disabling logs alone must
                // not starve auto-tune.
                UpdateDiagnosticsGate();
            },
            writeLogsNow: () => logFile.Event("user requested write logs now"),
            deleteAllLogs: () =>
            {
                // Spare the log we're currently writing (it's held open and can't be removed anyway).
                var removed = LogMaintenance.DeleteAllLogs(logFile.Path);
                logFile.Event($"user deleted all logs from Preferences ({removed} file(s) removed)");
                return removed;
            },
            checkForUpdatesNow: () => CheckForUpdatesManually(),
            onUpdateFrequencyChanged: ApplyUpdateCheckTimer,
            onAutoSaveIntervalChanged: ApplyAutoSaveTimer,
            onClearRememberedPeers: () =>
            {
                settings.SaveRememberedPeers(Array.Empty<string>());
                rememberedPeerInstanceIds.Clear();
                RefreshKnownPeers();
                logFile.Event("remembered peers list cleared (Preferences)");
            },
            onClearRememberedApplications: () =>
            {
                settings.SaveRememberedApplications(Array.Empty<string>());
                logFile.Event("remembered applications list cleared (Preferences)");
            },
            applyUpnpEnabled: enabled =>
            {
                // The persist already happened in the dialog; this callback only flips the
                // live RouterPortMapper. Start/Stop both run on a thread-pool thread because
                // NatUtility's discovery + socket teardown CAN BLOCK FOR TENS OF SECONDS, or
                // indefinitely, on unusual network setups (multiple adapters, VPNs, hostile
                // firewalls). Doing that on the UI thread here would freeze the
                // Preferences dialog AND every other UI element until the call returned —
                // Andre's v3.0 hang was triggered from this exact handler. See the longer
                // explanation on the startup-time UPnP block in OnShown. 2026-05-23.
                if (enabled)
                {
                    Task.Run(() =>
                    {
                        try { routerPortMapper.Start(); }
                        catch (Exception ex) { logFile.Event($"upnp: start from prefs failed: {ex.GetType().Name}: {ex.Message}"); }
                    });
                }
                else
                {
                    Task.Run(() =>
                    {
                        try { routerPortMapper.Stop(); }
                        catch (Exception ex) { logFile.Event($"upnp: stop from prefs failed: {ex.GetType().Name}: {ex.Message}"); }
                    });
                }
            },
            getUpnpSnapshot: () => (routerPortMapper.Status, routerPortMapper.ExternalEndpoint, routerPortMapper.LastError),
            subscribeUpnpStatusChanged: handler => routerPortMapper.StatusChanged += handler,
            unsubscribeUpnpStatusChanged: handler => routerPortMapper.StatusChanged -= handler);
        dialog.ShowDialog(this);
        if (dialog.ChangedAnyProfileSetting) MarkProfileDirty();
        // Appearance-tab changes (tab order, show pan/EQ tab, show discovered/remembered lists) apply now.
        ApplyMainTabLayout();
        RefreshConnectivityListVisibility();
        var appearanceCfg = AppConfig.Load();
        logFile.Event($"appearance applied: theme={appearanceCfg.ThemeMode}, tabs=[{string.Join(", ", mainTabControl.TabPages.Cast<TabPage>().Select(t => t.Text))}], discovered-list={appearanceCfg.ShowDiscoveredPeers}, remembered-list={appearanceCfg.ShowRememberedPeers}");
        // The Preferences dialog includes per-cue Browse buttons that can change custom
        // WAV paths in AppConfig.CustomCuePaths. Reload the cached SoundPlayer instances
        // here unconditionally — cheap, only six small files, and guarantees the next
        // play uses whatever the user just picked without waiting for the next launch.
        ReloadAllCueSounds();
    }

    /// <summary>User pressed "Check for updates" (Help menu or Preferences button). Always
    /// runs the check noisily — i.e. surfaces "you're up to date", "vX.Y is available", or
    /// "couldn't reach the server" via a MessageBox, regardless of the Silently-install
    /// setting. Silent install only applies to background polls. Caller is on the UI thread.
    /// </summary>
    private async void CheckForUpdatesManually()
    {
        var result = await updater.CheckForUpdateAsync().ConfigureAwait(true);
        switch (result)
        {
            case UpToDate:
                MessageBox.Show(this,
                    $"You are running the latest version (v{updater.CurrentVersion}).",
                    "Check for updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;

            case UpdateCheckFailed failure:
                ShowUpdateCheckFailedDialog(failure);
                return;

            case UpdateAvailable available:
                var info = available.Info;
                var summary = string.IsNullOrWhiteSpace(info.ReleaseNotes)
                    ? $"RemSound {info.Tag} is available. Install now?"
                    : $"RemSound {info.Tag} is available.\n\n{TruncateForDialog(info.ReleaseNotes)}\n\nInstall now?";
                var choice = MessageBox.Show(this, summary, "Update available",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
                if (choice != DialogResult.Yes) return;
                await InstallUpdateAsync(info).ConfigureAwait(true);
                return;
        }
    }

    /// <summary>Show a plain-English MessageBox explaining why an update check couldn't
    /// complete. The wording is tailored to the <see cref="FailureKind"/>: the SecureConnection
    /// case (which is most often Windows 7 lacking TLS 1.2 / SHA-2 updates) gets pointers to
    /// the specific Microsoft KBs that fix it; other failures get a friendlier "check your
    /// internet" message. The user is also offered the manual zip-install fallback URL so they
    /// can recover without us debugging their machine. The technical detail goes to the log,
    /// not the dialog. 2026-05-28.</summary>
    private void ShowUpdateCheckFailedDialog(UpdateCheckFailed failure)
    {
        var (heading, body) = failure.Kind switch
        {
            FailureKind.SecureConnection => (
                "Couldn't reach the update server (secure connection failed)",
                "RemSound couldn't make a secure connection to GitHub to check for an update.\n\n"
                + "This is almost always because the Windows install is missing one or both of these official Microsoft updates that enable modern secure connections:\n\n"
                + "  • KB3140245 — turns on TLS 1.2 support.\n"
                + "  • KB4474419 — updates the trusted certificate list (SHA-2 support).\n\n"
                + "Both are free and won't break anything else. Run Windows Update (Control Panel → Windows Update) and install whatever it offers. Once those are in, Check for updates should work normally.\n\n"
                + "If you'd rather install the latest version by hand: go to https://github.com/Ednunp/RemSound/releases/latest, download the zip, close RemSound, and extract the zip over your RemSound folder. That works regardless of the secure-connection issue."),

            FailureKind.NetworkUnreachable => (
                "Couldn't reach the update server",
                "RemSound couldn't reach GitHub to check for an update. This is usually a network problem — check your internet connection, then try Check for updates again.\n\n"
                + "If you'd rather install the latest version by hand: go to https://github.com/Ednunp/RemSound/releases/latest, download the zip, close RemSound, and extract the zip over your RemSound folder."),

            FailureKind.Timeout => (
                "Update check timed out",
                "RemSound's request to GitHub took too long to respond. This is usually a slow or congested network — try Check for updates again in a minute or two.\n\n"
                + "If you'd rather install the latest version by hand: go to https://github.com/Ednunp/RemSound/releases/latest, download the zip, close RemSound, and extract the zip over your RemSound folder."),

            _ => (
                "Couldn't check for updates",
                "RemSound's request to GitHub didn't get the response it expected, so it can't tell whether a newer version is available. Try Check for updates again later.\n\n"
                + "If you'd rather install the latest version by hand: go to https://github.com/Ednunp/RemSound/releases/latest, download the zip, close RemSound, and extract the zip over your RemSound folder."),
        };
        MessageBox.Show(this, body, heading, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>Background-poll path. Runs on a timer tick; surfaces nothing unless an update
    /// is available, then either silently installs (per <see cref="AppConfig.SilentlyInstallUpdates"/>)
    /// or pops the same confirmation dialog the manual path uses. "No update available" is a
    /// silent no-op — the user already chose to delegate scheduling to the timer.</summary>
    private async void CheckForUpdatesInBackground()
    {
        var result = await updater.CheckForUpdateAsync().ConfigureAwait(true);
        // Persist the timestamp so cross-launch scheduling can space the next poll out.
        try
        {
            var cfg = AppConfig.Load();
            cfg.LastUpdateCheckUtc = DateTime.UtcNow;
            cfg.Save();
        }
        catch { /* timestamp persistence is best-effort */ }
        // Background polls stay silent on both UpToDate and UpdateCheckFailed — the user
        // delegated scheduling to the timer and a failure here isn't actionable from where
        // they are. The next poll re-checks. The failure detail has already gone to the log
        // via the updater's Log callback.
        if (result is not UpdateAvailable available) return;
        var info = available.Info;
        if (AutoInstallDeferredByWindow(info.Tag, "background poll")) return;
        if (AppConfig.Load().SilentlyInstallUpdates)
        {
            // Notice the user before the app vanishes and the helper takes over. Hidden from
            // the periodic-poll path on the assumption the user knows they ticked "silently
            // install"; the startup path is the noisy one (see CheckForUpdatesOnStartup).
            await InstallUpdateAsync(info).ConfigureAwait(true);
            return;
        }
        var summary = string.IsNullOrWhiteSpace(info.ReleaseNotes)
            ? $"RemSound {info.Tag} is available. Install now?"
            : $"RemSound {info.Tag} is available.\n\n{TruncateForDialog(info.ReleaseNotes)}\n\nInstall now?";
        var choice = ForegroundDialog.Show(owner => MessageBox.Show(owner, summary, "Update available",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1));
        if (choice == DialogResult.Yes) await InstallUpdateAsync(info).ConfigureAwait(true);
    }

    /// <summary>Startup-poll path. Fired ~4 s after the main window finishes loading when
    /// <see cref="AppConfig.CheckForUpdatesOnStartup"/> is true. Distinct from
    /// <see cref="CheckForUpdatesInBackground"/> because the startup case is where the
    /// "you launched the app and it's already installing an update" surprise is loudest —
    /// silent install here is preceded by a brief notice dialog so the user sees the version
    /// number and understands why the app is about to vanish. The non-silent path uses the
    /// same MessageBox flow as the background and manual paths so the user-visible question
    /// stays consistent.</summary>
    private async void CheckForUpdatesOnStartup()
    {
        UpdateCheckResult result;
        try
        {
            result = await updater.CheckForUpdateAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            logFile.Event($"updater: startup check threw unexpectedly: {ex.GetType().Name}: {ex.Message}");
            return;
        }
        try
        {
            var cfg = AppConfig.Load();
            cfg.LastUpdateCheckUtc = DateTime.UtcNow;
            cfg.Save();
        }
        catch { /* harmless */ }
        // Log each outcome distinctly so a failed check (TLS error, network down, GitHub
        // 5xx) doesn't get filed as "up to date" — that's the misclassification Tech Singer's
        // log from 2026-05-28 demonstrated, where a Win7 SSL handshake failure quietly logged
        // as "up to date" instead of the real cause.
        if (result is UpdateCheckFailed failure)
        {
            logFile.Event($"updater: startup check failed ({failure.Kind}): {failure.TechnicalDetail}");
            return;
        }
        if (result is UpToDate)
        {
            logFile.Event($"updater: startup check — up to date (v{updater.CurrentVersion})");
            return;
        }
        if (result is not UpdateAvailable available) return;
        var info = available.Info;
        logFile.Event($"updater: startup check found {info.Tag}");
        if (AutoInstallDeferredByWindow(info.Tag, "startup check")) return;
        if (AppConfig.Load().SilentlyInstallUpdates)
        {
            // Heads-up the user before we exit and the helper takes over. The notice is its
            // own dialog so NVDA reads "RemSound is installing version X" before focus moves;
            // a MessageBox would force the user to dismiss it, which defeats the point of
            // "silent" install. UpdateInstallNoticeDialog auto-dismisses after a short
            // countdown but lets the user pick Install now / Skip / Postpone before then.
            using var notice = new UpdateInstallNoticeDialog(info);
            var choice = ForegroundDialog.Show(owner => notice.ShowDialog(owner));
            switch (choice)
            {
                case DialogResult.OK:
                    // "Install now" — same as the countdown elapsing.
                    await InstallUpdateAsync(info).ConfigureAwait(true);
                    break;
                case DialogResult.Ignore:
                    // "Skip this version" — log and leave the user be; the next startup
                    // check will probably find the same version and ask again. We don't
                    // persist a skip list because release tempo is low enough that the
                    // user can dismiss once or twice without resenting it.
                    logFile.Event($"updater: user skipped {info.Tag} from startup notice");
                    break;
                case DialogResult.Cancel:
                default:
                    // "Postpone" / closed dialog — silent install at the next opportunity
                    // (timer tick or next launch).
                    logFile.Event($"updater: user postponed {info.Tag} from startup notice");
                    break;
            }
            return;
        }
        // Non-silent: same prompt the background poll uses.
        var summary = string.IsNullOrWhiteSpace(info.ReleaseNotes)
            ? $"RemSound {info.Tag} is available. Install now?"
            : $"RemSound {info.Tag} is available.\n\n{TruncateForDialog(info.ReleaseNotes)}\n\nInstall now?";
        var pick = ForegroundDialog.Show(owner => MessageBox.Show(owner, summary, "Update available",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1));
        if (pick == DialogResult.Yes) await InstallUpdateAsync(info).ConfigureAwait(true);
    }

    /// <summary>Download the new release, stage it, spawn the install helper and exit. On
    /// any failure shows a MessageBox and stays running — partial installs leave the app
    /// untouched.</summary>
    private async Task InstallUpdateAsync(UpdateInfo info)
    {
        // Pass the currently-loaded profile title so the updater drops a resume-after-update
        // sentinel; the relaunched RemSound.exe will pick this up in Program.Main and silently
        // re-open the same profile, skipping the picker. Without this, a silent or
        // mid-session update would drop the session AND leave the user back at the picker —
        // the session never resumes by itself. Null/empty title (blank template, no profile
        // saved yet) skips the sentinel and the relaunch falls through to normal startup.
        // Don't let two near-simultaneous checks both stage an install and spawn two helpers.
        if (updateInstallStarted)
        {
            logFile.Event($"updater: install already in progress; ignoring repeat request for {info.Tag}");
            return;
        }
        updateInstallStarted = true;

        // Update cue — an audible heads-up that an update is going in, played just before it
        // starts. Fires for every install path (manual, background-silent, startup-silent), so
        // a silent background update isn't completely silent: the user hears that RemSound is
        // about to close and update. Played before the download so it sounds well before the
        // app vanishes; the download comfortably outlasts the short cue. Honours the per-profile
        // EnableUpdateCue flag set in Preferences.
        if (settings.LoadEnableUpdateCue()) updateSound?.Play();

        var ok = await updater.DownloadAndStageInstallAsync(info, currentProfileTitle).ConfigureAwait(true);
        if (!ok)
        {
            // Nothing was staged — allow a later attempt rather than wedging the updater off
            // for the rest of the session.
            updateInstallStarted = false;
            ForegroundDialog.Show(owner => MessageBox.Show(owner,
                $"Could not download or stage the update. Try again later, or visit the release page in your browser:\n\n{info.ReleaseUrl}",
                "Update failed", MessageBoxButtons.OK, MessageBoxIcon.Warning));
            return;
        }
        // The helper is staged and launched. We MUST now exit cleanly so it can replace our
        // files and restart us — set updatingInProgress so the close path skips every prompt;
        // no dialog (least of all the unsaved-changes prompt) may be allowed to cancel this.
        updatingInProgress = true;
        logFile.Event($"updater: install helper launched for {info.Tag}, exiting");
        Application.Exit();
    }

    /// <summary>Clamp the release notes to a reasonable dialog-friendly length so the
    /// MessageBox doesn't push off-screen. Full notes always live in About and on the
    /// GitHub release page.</summary>
    private static string TruncateForDialog(string s)
    {
        const int max = 600;
        if (s.Length <= max) return s;
        return s[..max] + "\n…";
    }

    // One-shot retry for an update found OUTSIDE the install window: without it, a 24-hourly
    // poll could keep landing outside the window and defer the same update for days. Armed by
    // AutoInstallDeferredByWindow to fire shortly after the window next opens.
    private readonly System.Windows.Forms.Timer deferredUpdateTimer = new();
    private bool deferredUpdateTimerWired;

    /// <summary>The "only install updates within this time range" gate (Preferences). Applies to
    /// AUTOMATIC installs only — the background poll and the startup check; a manual "Check for
    /// updates now" is the user asking by hand and is never gated. When the window is closed,
    /// logs, arms the retry for when it opens, and returns true (caller bails out).</summary>
    private bool AutoInstallDeferredByWindow(string tag, string source)
    {
        var cfg = AppConfig.Load();
        if (!cfg.UpdateWindowEnabled) return false;
        var now = DateTime.Now.TimeOfDay;
        if (UpdateWindow.IsWithin(now, cfg.UpdateWindowStartMinutes, cfg.UpdateWindowEndMinutes)) return false;

        var wait = UpdateWindow.UntilNextStart(now, cfg.UpdateWindowStartMinutes) + TimeSpan.FromMinutes(1);
        if (!deferredUpdateTimerWired)
        {
            deferredUpdateTimerWired = true;
            deferredUpdateTimer.Tick += (_, _) =>
            {
                deferredUpdateTimer.Stop();
                logFile.Event("updater: install window opened — re-running the deferred update check");
                CheckForUpdatesInBackground();
            };
        }
        deferredUpdateTimer.Stop();
        deferredUpdateTimer.Interval = (int)Math.Clamp(wait.TotalMilliseconds, 60_000, int.MaxValue);
        deferredUpdateTimer.Start();
        logFile.Event($"updater: {source} found {tag}, but it's outside the install window "
            + $"({UpdateWindow.FormatMinutes(cfg.UpdateWindowStartMinutes)}–{UpdateWindow.FormatMinutes(cfg.UpdateWindowEndMinutes)}) "
            + $"— deferred; retrying in {wait.TotalMinutes:0} min when the window opens");
        return true;
    }

    /// <summary>Apply (or stop) the background update-poll timer based on
    /// <see cref="AppConfig.UpdateCheckFrequency"/>. Called at startup and whenever the user
    /// changes the dropdown in Preferences. The first tick fires after one interval — we
    /// don't immediately probe GitHub on every app launch because that's both rude and
    /// would race with the Profile-load + audio-engine startup the user actually cares
    /// about.</summary>
    private void ApplyUpdateCheckTimer()
    {
        updateCheckTimer.Stop();
        var freq = AppConfig.Load().UpdateCheckFrequency;
        var intervalMs = freq switch
        {
            UpdateCheckFrequency.EveryHour => 60 * 60 * 1000,
            UpdateCheckFrequency.Every6Hours => 6 * 60 * 60 * 1000,
            UpdateCheckFrequency.Every24Hours => 24 * 60 * 60 * 1000,
            _ => 0,
        };
        if (intervalMs <= 0) return;
        updateCheckTimer.Interval = intervalMs;
        updateCheckTimer.Start();
    }

    /// <summary>Connectivity tab — peer lists (connected/discovered/remembered), manual-add,
    /// logging toggle and write-logs-now. Wires per-list ItemCheck/KeyDown handlers, status
    /// labels, and binds the lists to the existing peer-state dictionaries via the Sync*
    /// helpers below. Phase 2 of the 2026-05-06 refactor; previously these controls lived
    /// inside ShowConnectivityTransportDialog and the form had a "Connectivity and transport"
    /// bridge button.</summary>
    private void BuildConnectivityTab()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 2,
            RowCount = 6,
            AutoScroll = true,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // === Peer lists wiring ===
        WireCheckedListAccessibility(connectedPeersList, connectedPeersStatus, "connected peer");
        WireCheckedListAccessibility(discoveredPeersList, discoveredPeersStatus, "discovered peer");
        WireCheckedListAccessibility(rememberedPeersList, rememberedPeersStatus, "remembered peer");

        // Connected list: items are always checked. Unchecking disconnects.
        connectedPeersList.ItemCheck += (_, args) =>
        {
            if (suppressConnectedCheck) return;
            BeginInvoke(() =>
            {
                if (args.NewValue == CheckState.Unchecked
                    && args.Index >= 0 && args.Index < connectedPeersList.Items.Count
                    && connectedPeersList.Items[args.Index] is PeerListItem item)
                {
                    DeselectPeer(item.Peer.InstanceId);
                }
                SyncAllPeerLists();
                ApplyAudioRuntime();
            });
        };
        connectedPeersList.KeyDown += (_, args) =>
        {
            if (args.KeyCode == Keys.Delete && connectedPeersList.SelectedItem is PeerListItem selected)
            {
                var prevIndex = connectedPeersList.SelectedIndex;
                DeselectPeer(selected.Peer.InstanceId);
                SyncAllPeerLists();
                FocusListItemAfterDelete(connectedPeersList, prevIndex);
                ApplyAudioRuntime();
                args.Handled = true;
                args.SuppressKeyPress = true;
            }
            // F2 renames the highlighted peer — the Windows Explorer idiom.
            else if (args.KeyCode == Keys.F2)
            {
                OnRenamePeer();
                args.Handled = true;
                args.SuppressKeyPress = true;
            }
        };

        // Discovered list: items are unchecked. Checking connects + auto-remembers. Delete
        // suppressed (discovered peers go away when their broadcaster does).
        discoveredPeersList.ItemCheck += (_, args) =>
        {
            if (suppressDiscoveredCheck) return;
            if (args.NewValue != CheckState.Checked) return;
            BeginInvoke(() =>
            {
                if (args.Index >= 0 && args.Index < discoveredPeersList.Items.Count
                    && discoveredPeersList.Items[args.Index] is PeerListItem item)
                {
                    SelectPeer(item.Peer);
                    EnsurePeerRemembered(item.Peer);
                }
                SyncAllPeerLists();
                ApplyAudioRuntime();
            });
        };
        discoveredPeersList.KeyDown += (_, args) =>
        {
            if (args.KeyCode == Keys.Delete) { args.Handled = true; args.SuppressKeyPress = true; }
        };

        // Remembered list: items are unchecked (connected ones hide). Check reconnects, Delete forgets.
        rememberedPeersList.ItemCheck += (_, args) =>
        {
            if (suppressRememberedCheck) return;
            if (args.NewValue != CheckState.Checked) return;
            BeginInvoke(async () =>
            {
                if (args.Index >= 0 && args.Index < rememberedPeersList.Items.Count
                    && rememberedPeersList.Items[args.Index] is RememberedPeerItem item)
                {
                    PeerAnnouncement? toSelect = null;
                    if (rememberedPeerInstanceIds.TryGetValue(item.Entry, out var existingId)
                        && knownPeers.TryGetValue(existingId, out var known))
                    {
                        toSelect = known;
                    }
                    else
                    {
                        var address = await ResolvePeerAddressAsync(item.Entry);
                        if (address is not null)
                        {
                            var peer = CreateManualPeer(item.Entry, address);
                            manualPeers[peer.InstanceId] = peer;
                            rememberedPeerInstanceIds[item.Entry] = peer.InstanceId;
                            toSelect = peer;
                        }
                    }
                    if (toSelect is not null) SelectPeer(toSelect);
                }
                RefreshKnownPeers();
                SyncAllPeerLists();
                ApplyAudioRuntime();
            });
        };
        rememberedPeersList.KeyDown += (_, args) =>
        {
            if (args.KeyCode == Keys.Delete)
            {
                var prevIndex = rememberedPeersList.SelectedIndex;
                var deletedLabel = (rememberedPeersList.SelectedItem as RememberedPeerItem)?.ToString();
                RemoveSelectedRememberedPeer(rememberedPeersList);
                SyncAllPeerLists();
                if (deletedLabel is not null) FocusAndAnnounceAfterDelete(rememberedPeersList, deletedLabel, prevIndex);
                else FocusListItemAfterDelete(rememberedPeersList, prevIndex);
                args.Handled = true;
                args.SuppressKeyPress = true;
            }
        };

        // === Manual add + Write logs now ===
        manualAddButton.Click += async (_, _) =>
        {
            var entry = ManualPeerPrompt.Show(this);
            if (string.IsNullOrWhiteSpace(entry)) return;
            await AddManualPeerAsync(entry);
            SyncAllPeerLists();
            BeginInvoke(() => FocusListControl(connectedPeersList));
        };
        // Logging controls retired from this tab 2026-05-08 — they now live in the
        // Preferences dialog (File → Preferences, Ctrl+P) as the last two items.

        // === Layout ===
        // Tab order (row order = add order): 0 "Peers" header; 1 connected peers; 2 details box; 3 rename;
        // 4 add-by-IP; 5 discovered; 6 remembered; 7 lock toggle; 8 status. The "Peers" header is a
        // visual grouping only. Add-by-IP sits with the connected-peer actions, per Ed's requested order.
        panel.RowCount = 9;

        var peersHeader = Theme.SectionHeader("Peers");
        panel.Controls.Add(peersHeader, 0, 0);
        panel.SetColumnSpan(peersHeader, 2);

        FormLayoutRows.AddCheckedListRow(panel, 1, "Connected peers (Alt+&C)", connectedPeersList, connectedPeersStatus, FocusListControl);

        // Details of, and a rename for, the peer highlighted in the connected list above.
        var detailsLabel = new MnemonicLabel { Text = "Peer d&etails (Alt+E)", AutoSize = true, Anchor = AnchorStyles.Left, MnemonicTarget = peerDetailsBox };
        detailsLabel.Click += (_, _) => peerDetailsBox.Focus();
        panel.Controls.Add(detailsLabel, 0, 2);
        panel.Controls.Add(peerDetailsBox, 1, 2);
        renamePeerButton.Click += (_, _) => OnRenamePeer();
        panel.Controls.Add(renamePeerButton, 1, 3);
        connectedPeersList.SelectedIndexChanged += (_, _) => UpdatePeerDetails();

        discoveredPeersLabel = FormLayoutRows.AddCheckedListRow(panel, 4, "Discovered peers (Alt+&D)", discoveredPeersList, discoveredPeersStatus, FocusListControl);
        rememberedPeersLabel = FormLayoutRows.AddCheckedListRow(panel, 5, "Remembered peers (Alt+&R)", rememberedPeersList, rememberedPeersStatus, FocusListControl);

        // Add a peer by address, then the lock toggle — the manual / advanced options after the lists.
        panel.Controls.Add(new Label { Text = "Manual peer", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 6);
        panel.Controls.Add(manualAddButton, 1, 6);

        lockPeerAddressesBox.Checked = settings.LoadLockPeerAddresses();
        lockPeerAddressesBox.CheckedChanged += (_, _) =>
        {
            settings.SaveLockPeerAddresses(lockPeerAddressesBox.Checked);
            MarkProfileDirty();
            logFile.Event($"lock peer addresses: {(lockPeerAddressesBox.Checked ? "on" : "off")}");
        };
        panel.Controls.Add(lockPeerAddressesBox, 0, 7);
        panel.SetColumnSpan(lockPeerAddressesBox, 2);

        // Connection status readout — last row, tab-into-able.
        var statusLabel = new MnemonicLabel { Text = "Connection status (Alt+&S)", AutoSize = true, Anchor = AnchorStyles.Left, MnemonicTarget = statusReadout };
        statusLabel.Click += (_, _) => statusReadout.Focus();
        panel.Controls.Add(statusLabel, 0, 8);
        panel.Controls.Add(statusReadout, 1, 8);
        UpdatePeerDetails();

        // Tab order — the AUTHORITATIVE setting for this tab (SetTabOrder no longer touches it). The
        // three lists are each wrapped in a FlowLayoutPanel by AddCheckedListRow, so the WRAPPER (the
        // list's Parent) is what sorts among the tab's controls; setting the list's own TabIndex only
        // orders it inside its wrapper (where it's alone) and does nothing to the overall order — that
        // was the long-standing bug. Set the wrappers and the directly-added controls in sequence:
        // connected, details, rename, discovered, remembered, add-by-IP, lock, status.
        if (connectedPeersList.Parent is { } connectedWrap) connectedWrap.TabIndex = 0;
        peerDetailsBox.TabIndex = 1;
        renamePeerButton.TabIndex = 2;
        if (discoveredPeersList.Parent is { } discoveredWrap) discoveredWrap.TabIndex = 3;
        if (rememberedPeersList.Parent is { } rememberedWrap) rememberedWrap.TabIndex = 4;
        manualAddButton.TabIndex = 5;
        lockPeerAddressesBox.TabIndex = 6;
        statusReadout.TabIndex = 7;

        // Initial render so the box has content the moment the user tabs into it.
        RefreshStatusReadout();

        connectivityTabPage.Controls.Add(panel);
        // Initial population so screen readers see something on first open.
        SyncAllPeerLists();
        RefreshConnectivityListVisibility();
    }

    /// <summary>Shows or hides the Discovered / Remembered peer rows to match the Appearance-tab
    /// preferences. Hiding both a row's label and its list wrapper collapses the row. Called at build
    /// and after Preferences close.</summary>
    private void RefreshConnectivityListVisibility()
    {
        var cfg = AppConfig.Load();
        SetConnectivityRowVisible(discoveredPeersLabel, discoveredPeersList, cfg.ShowDiscoveredPeers);
        SetConnectivityRowVisible(rememberedPeersLabel, rememberedPeersList, cfg.ShowRememberedPeers);
    }

    private static void SetConnectivityRowVisible(Control? label, Control list, bool visible)
    {
        if (label is not null) label.Visible = visible;
        if (list.Parent is not null) list.Parent.Visible = visible;   // the FlowLayoutPanel wrapper
    }

    /// <summary>Shows/hides a control together with its FlowLayoutPanel row wrapper so the whole
    /// table row collapses (rather than leaving an empty gap). Used by the send-mode switch.</summary>
    private static void SetRowControlVisible(Control control, bool visible)
    {
        control.Visible = visible;
        if (control.Parent is not null) control.Parent.Visible = visible;
    }

    /// <summary>Audio I/O tab — full content. All the existing main-form audio controls
    /// (mode, ASIO driver, send/receive checkboxes, device lists, volume) live here.</summary>
    private void BuildAudioIOTab()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 2,
            RowCount = 13,
            AutoScroll = true,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // 2026-05-11 mnemonic refresh — Ed's spec for the Audio I/O tab:
        //   ASIO driver              → Alt+D   (drives audio mode: "(none)" = WASAPI-only,
        //                                       any real driver = WASAPI + ASIO)
        //   Set volume               → Alt+V (unchanged)
        //   ASIO outputs (receive)   → Alt+1
        //   ASIO inputs  (send)      → Alt+2
        //   WASAPI outputs (receive) → Alt+3
        //   WASAPI outputs (send)    → Alt+4
        //   WASAPI inputs  (send)    → Alt+5
        //   Receive Alt+R, Send Alt+S — unchanged.
        //
        // The pre-2026-05-11 "Audio mode" listbox (Alt+M) is gone — selecting a driver here
        // brings the ASIO half of the form to life; selecting "(none)" hides it again. On
        // machines with no ASIO drivers installed the driver picker is hidden entirely (there
        // is nothing to switch to) and the form runs WASAPI-only.
        // Row 0: "Uncheck all inputs and outputs on all soundcards", spanning both columns,
        // sitting just above the ASIO driver picker. Always present (independent of ASIO).
        panel.Controls.Add(uncheckAllDevicesButton, 0, 0);
        panel.SetColumnSpan(uncheckAllDevicesButton, 2);

        if (hasAnyAsioDriverInstalled)
        {
            asioDriverLabel = new MnemonicLabel { Text = "ASIO driver (Alt+&D)", AutoSize = true, Anchor = AnchorStyles.Left, MnemonicTarget = asioDriverBox };
            asioDriverLabel.Click += (_, _) => asioDriverBox.Focus();
            panel.Controls.Add(asioDriverLabel, 0, 1);
            panel.Controls.Add(asioDriverBox, 1, 1);
        }
        else
        {
            // Reserve the row but keep both cells empty. We could collapse the row entirely,
            // but leaving it as a no-op AutoSize row keeps the rest of the row indices stable
            // with the original layout (each subsequent control still lives in row N).
        }

        // Each checkbox wrapped in its own FlowLayoutPanel — required for NVDA state-change
        // announcements to fire reliably (a CheckBox directly in a TableLayoutPanel cell
        // suppresses them; the FlowLayoutPanel wrapper restores the announcement chain).
        var receiveCheckboxPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        receiveCheckboxPanel.Controls.Add(receiveAudioCheckbox);
        panel.Controls.Add(receiveCheckboxPanel, 1, 2);
        receiveOutputDevicesLabel = FormLayoutRows.AddCheckedListRow(panel, 3, "WASAPI outputs for received audio (Alt+&3)", receiveOutputDevicesList, receiveOutputDevicesStatusLabel, FocusListControl);
        asioReceiveOutputDevicesLabel = FormLayoutRows.AddCheckedListRow(panel, 4, "ASIO outputs for received audio (Alt+&1)", asioReceiveOutputDevicesList, asioReceiveOutputDevicesStatusLabel, FocusListControl);
        FormLayoutRows.AddRow(panel, 5, "Master volume for received audio (Alt+&V)", volumeBar, FocusControl);
        var sendCheckboxPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        sendCheckboxPanel.Controls.Add(sendMyAudioCheckbox);
        panel.Controls.Add(sendCheckboxPanel, 1, 6);

        // Row 7: "how to send WASAPI audio" chooser — audio devices (the classic loopback list) or
        // specific applications. Sits right after "Send my audio". Built manually (like the ASIO
        // driver row) so we keep the label reference to collapse the whole row on old Windows.
        BuildSendModeRow(panel, 7);

        // Row 8 (devices mode): the classic WASAPI outputs-to-send loopback list.
        sendOutputDevicesLabel = FormLayoutRows.AddCheckedListRow(panel, 8, "WASAPI audio outputs to send (Alt+&4)", sendOutputDevicesList, sendOutputDevicesStatusLabel, FocusListControl);

        // Rows 9-10 (applications mode): TWO app lists — currently-active (running now, plus any ticked
        // app that isn't, so it can always be unticked) and remembered (the global address book, minus
        // whatever is ticked), mirroring the peers lists. No "send all applications" toggle — picking this
        // mode means picking specific apps; whole-system audio is devices mode's job (Ed, 2026-07-16).
        sendAppsLabel = FormLayoutRows.AddCheckedListRow(panel, 9, "Currently active applications (Alt+&8)", sendAppsList, sendAppsStatusLabel, FocusListControl);
        rememberedAppsLabel = FormLayoutRows.AddCheckedListRow(panel, 10, "Remembered applications (Alt+&9)", rememberedAppsList, rememberedAppsStatusLabel, FocusListControl);

        // Rows 11-12: the remaining send lists.
        sendInputDevicesLabel = FormLayoutRows.AddCheckedListRow(panel, 11, "WASAPI audio inputs to send (Alt+&5)", sendInputDevicesList, sendInputDevicesStatusLabel, FocusListControl);
        asioSendDevicesLabel = FormLayoutRows.AddCheckedListRow(panel, 12, "ASIO audio inputs to send (Alt+&2)", asioSendDevicesList, asioSendDevicesStatusLabel, FocusListControl);

        WireSendModeControls();

        audioIOTabPage.Controls.Add(panel);
    }

    /// <summary>Builds the "how to send WASAPI audio" chooser row: a two-item listbox (whole sound
    /// devices vs specific applications) preceded by its mnemonic label. Built by hand rather than via
    /// FormLayoutRows so we retain <see cref="sendModeLabel"/> to collapse the whole row on Windows too
    /// old for per-application capture.</summary>
    private void BuildSendModeRow(TableLayoutPanel panel, int row)
    {
        sendModeLabel = new MnemonicLabel
        {
            Text = "How to send WASAPI audio (Alt+&6)",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            MnemonicTarget = sendModeList,
        };
        sendModeLabel.Click += (_, _) => FocusControl(sendModeList);
        sendModeList.AccessibleName = "How to send WASAPI audio (Alt+6)";
        sendModeList.Items.Clear();
        sendModeList.Items.Add("Send whole audio devices");   // SendModeDevicesIndex
        sendModeList.Items.Add("Send specific applications");  // SendModeApplicationsIndex
        sendModeList.SelectedIndex = SendModeDevicesIndex;
        panel.Controls.Add(sendModeLabel, 0, row);
        var wrapper = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            TabStop = false,
        };
        wrapper.Controls.Add(sendModeList);
        panel.Controls.Add(wrapper, 1, row);
    }

    /// <summary>Wires the send-mode chooser and the two app lists, sets up the reconcile timer, and
    /// applies the initial visibility. On Windows older than the process-loopback API the whole
    /// applications path is hidden and the mode is pinned to devices.</summary>
    private void WireSendModeControls()
    {
        WireCheckedListAccessibility(sendAppsList, sendAppsStatusLabel, "application");
        WireCheckedListAccessibility(rememberedAppsList, rememberedAppsStatusLabel, "remembered application");

        sendModeList.SelectedIndexChanged += (_, _) =>
        {
            if (suppressSendAppEvents) return;
            ApplySendModeVisibility();
            if (sendModeList.SelectedIndex == SendModeApplicationsIndex) ReconcileSendAppsList();
            MarkProfileDirty();
            ApplySendSources();
        };

        // Both app lists toggle the SAME send set: ticking an app in either the active or the remembered
        // list adds it; unticking removes it. The other list re-renders to match, exactly like the peers
        // lists. Defer to after the check state settles.
        void WireAppList(CheckedListBox list)
        {
            list.ItemCheck += (_, args) =>
            {
                if (suppressSendAppEvents) return;
                if (list.Items[args.Index] is not AudioAppChoice choice) return;
                var nowChecked = args.NewValue == CheckState.Checked;
                BeginInvoke(() => OnSendAppToggled(choice.ProcessName, nowChecked));
            };
        }
        WireAppList(sendAppsList);
        WireAppList(rememberedAppsList);

        // Delete on a remembered application forgets it — the same affordance the remembered-peers list
        // has had all along (issue #26; the two remembered lists should feel identical). Items in this
        // list are by definition not ticked (ticked apps show in the Active list), so deleting one only
        // edits the machine-wide remembered set; no capture change is implied.
        rememberedAppsList.KeyDown += (_, args) =>
        {
            if (args.KeyCode != Keys.Delete) return;
            args.Handled = true;
            args.SuppressKeyPress = true;
            if (rememberedAppsList.SelectedItem is not AudioAppChoice choice) return;
            var prevIndex = rememberedAppsList.SelectedIndex;
            var deletedLabel = choice.ToString() ?? choice.ProcessName; // announce what the row read as
            RemoveRememberedApplication(choice.ProcessName);
            FocusAndAnnounceAfterDelete(rememberedAppsList, deletedLabel, prevIndex);
        };

        // Reconcile the app list on a slow timer so entries appear/disappear as apps open and close,
        // without ever piling up (each pass releases every session object — see AudioAppEnumerator).
        // Only ticks while the applications list is actually visible.
        sendAppsReconcileTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        sendAppsReconcileTimer.Tick += (_, _) =>
        {
            if (sendModeList.SelectedIndex != SendModeApplicationsIndex) return;
            // Backstop for the instant watcher: catch an app opening/closing even when this tab isn't
            // showing, so a remembered app still starts being captured. The list redraw only matters when
            // the list is actually on screen.
            RefreshSendAppCapture();
            if (sendAppsList.Visible) ReconcileSendAppsList();
        };

        ApplySendModeVisibility();
    }

    /// <summary>Shows the device list in devices mode and the app checkbox + list in applications mode,
    /// collapsing the other. On unsupported Windows the chooser itself is hidden and devices mode is
    /// forced. Also starts/stops the reconcile timer with the applications view.</summary>
    private void ApplySendModeVisibility()
    {
        var supported = ProcessLoopbackCapture.IsSupported;

        // The chooser row only makes sense where applications mode is possible.
        if (sendModeLabel is not null) sendModeLabel.Visible = supported;
        SetRowControlVisible(sendModeList, supported);
        // Items.Count > 0 guard: this method is called once EARLY in the constructor (via ApplyAsioMode)
        // before the Input/Output tab has populated sendModeList, so the list can still be empty here.
        // Setting SelectedIndex on an empty ListBox throws ArgumentOutOfRangeException. On Windows 10+ the
        // branch is skipped anyway (process-loopback IS supported), which hid the bug — but on Windows 7
        // (unsupported) it crashed the app at launch (issue #22). When the list is empty there's nothing to
        // reset; it's created selecting Devices, and this runs again (line ~3611) once the list is built.
        if (!supported && sendModeList.Items.Count > 0 && sendModeList.SelectedIndex != SendModeDevicesIndex)
        {
            suppressSendAppEvents = true;
            try { sendModeList.SelectedIndex = SendModeDevicesIndex; }
            finally { suppressSendAppEvents = false; } // a throw must not leave events suppressed for good
        }

        var appsMode = supported && sendModeList.SelectedIndex == SendModeApplicationsIndex;

        // Devices mode → show the loopback outputs list; applications mode → hide it.
        if (sendOutputDevicesLabel is not null) sendOutputDevicesLabel.Visible = !appsMode;
        SetRowControlVisible(sendOutputDevicesList, !appsMode);

        // Applications mode shows both app lists (active + remembered); devices mode collapses them.
        if (sendAppsLabel is not null) sendAppsLabel.Visible = appsMode;
        SetRowControlVisible(sendAppsList, appsMode);
        if (rememberedAppsLabel is not null) rememberedAppsLabel.Visible = appsMode;
        SetRowControlVisible(rememberedAppsList, appsMode);

        // Populate the lists whenever they're on screen, and keep the reconcile poll + the instant
        // session-start watcher running the whole time we're in applications mode — a ticked app must be
        // caught from its very start even when the user is looking at another tab.
        if (appsMode)
        {
            ReconcileSendAppsList();
            sendAppsReconcileTimer?.Start();
            EnsureSessionStartWatcher();
        }
        else
        {
            sendAppsReconcileTimer?.Stop();
            DisposeSessionStartWatcher();
        }
    }

    /// <summary>A send-app checkbox was toggled in EITHER list: update the one shared send set, remember the
    /// app globally when ticked, re-apply the capture, and re-render both lists so they stay in lock-step
    /// (tick it in Remembered and it shows ticked in Active the instant it's live). Mirrors the peers lists.</summary>
    private void OnSendAppToggled(string name, bool nowChecked)
    {
        if (suppressSendAppEvents || string.IsNullOrWhiteSpace(name)) return;
        if (nowChecked) selectedSendApps.Add(name); else selectedSendApps.Remove(name);
        if (nowChecked)
        {
            var remembered = settings.LoadRememberedApplications().ToList();
            if (!remembered.Any(e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase)))
            {
                remembered.Add(name);
                settings.SaveRememberedApplications(remembered);
            }
        }
        MarkProfileDirty();
        ApplySendSources();
        ReconcileSendAppsList();   // re-render both lists to reflect the new shared selection
    }

    /// <summary>Refreshes BOTH send-app lists from the current state. Active list = apps running right
    /// now PLUS every ticked app (a ticked app that closed stays visible, marked "(not running)", so the
    /// user can always find and untick it). Remembered list = the global remembered-apps address book
    /// MINUS whatever is ticked — ticking an app "moves" it to the active list; unticking drops it back
    /// into remembered (Ed's design, 2026-07-16). Every item is ticked iff it's in
    /// <see cref="selectedSendApps"/>.</summary>
    private void ReconcileSendAppsList()
    {
        if (!ProcessLoopbackCapture.IsSupported) return;

        var running = AudioAppEnumerator.Snapshot();
        var runningNames = new HashSet<string>(running.Select(a => a.ProcessName), StringComparer.OrdinalIgnoreCase);

        // Active = running apps (alphabetical), then any ticked app that ISN'T running appended after.
        var activeChoices = running
            .Select(a => new AudioAppChoice(a.ProcessName, a.DisplayName, running: true))
            .ToList();
        activeChoices.AddRange(selectedSendApps
            .Where(n => !runningNames.Contains(n))
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .Select(n => new AudioAppChoice(n, n, running: false)));

        // Remembered = the global address book minus the ticked apps, sorted by name.
        var rememberedChoices = settings.LoadRememberedApplications()
            .Where(n => !selectedSendApps.Contains(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .Select(name =>
            {
                var live = running.FirstOrDefault(a => string.Equals(a.ProcessName, name, StringComparison.OrdinalIgnoreCase));
                return new AudioAppChoice(name, live?.DisplayName ?? name, running: runningNames.Contains(name));
            }).ToList();

        suppressSendAppEvents = true;
        try
        {
            ReconcileAppListInPlace(sendAppsList, activeChoices);
            ReconcileAppListInPlace(rememberedAppsList, rememberedChoices);
        }
        finally
        {
            suppressSendAppEvents = false;
        }
        UpdateCheckedListStatus(sendAppsList, sendAppsStatusLabel, "application");
        UpdateCheckedListStatus(rememberedAppsList, rememberedAppsStatusLabel, "remembered application");
    }

    /// <summary>Forget one app from the machine-wide remembered-applications list and re-render both app
    /// lists. Backs the Delete key on the remembered list (issue #26) — the peers list has the same
    /// affordance. Internal so the self-test can drive the real removal path headlessly.</summary>
    internal void RemoveRememberedApplication(string processName)
    {
        var remaining = settings.LoadRememberedApplications()
            .Where(n => !string.Equals(n, processName, StringComparison.OrdinalIgnoreCase));
        settings.SaveRememberedApplications(remaining);
        logFile.Event($"ui: remembered application '{processName}' deleted from the remembered list");
        ReconcileSendAppsList();
    }

    /// <summary>Test seam: reconcile now and return the two send-app lists' rows — the active list's
    /// process names, which of them are ticked, and the remembered list's process names. Lets the
    /// self-test pin the list semantics (ticked apps leave Remembered; a ticked app that isn't running
    /// still shows in Active so it can be unticked) against the REAL reconcile logic.</summary>
    internal (string[] ActiveRows, string[] ActiveChecked, string[] RememberedRows) SnapshotAppListsForTest()
    {
        ReconcileSendAppsList();
        return (
            sendAppsList.Items.OfType<AudioAppChoice>().Select(c => c.ProcessName).ToArray(),
            sendAppsList.CheckedItems.OfType<AudioAppChoice>().Select(c => c.ProcessName).ToArray(),
            rememberedAppsList.Items.OfType<AudioAppChoice>().Select(c => c.ProcessName).ToArray());
    }

    /// <summary>Reconcile a CheckedListBox to exactly <paramref name="choices"/> with MINIMAL mutation:
    /// a row already showing the right app with the right tick state is left completely untouched, so the
    /// row the user is sitting on is not destroyed and re-announced by NVDA when the reconcile timer fires
    /// or a toggle rebuilds the list. Only differing rows are replaced, and only
    /// surplus rows past the end are removed. This is what killed the "double read the top checkbox" glitch
    /// — a full Clear()+re-add used to recreate the very row that had just been toggled. Every app row is
    /// ticked from the shared send set. Caller must
    /// hold <see cref="suppressSendAppEvents"/> (SetItemChecked would otherwise re-enter ItemCheck).</summary>
    private void ReconcileAppListInPlace(CheckedListBox list, IReadOnlyList<AudioAppChoice> choices)
    {
        list.BeginUpdate();
        try
        {
            // Trim rows that no longer have a counterpart (from the end, so surviving indices don't shift).
            while (list.Items.Count > choices.Count) list.Items.RemoveAt(list.Items.Count - 1);

            for (var i = 0; i < choices.Count; i++)
            {
                var c = choices[i];
                var wantChecked = selectedSendApps.Contains(c.ProcessName);

                if (i < list.Items.Count)
                {
                    // Replace only when the row's identity or visible label actually changed — leaving a
                    // matching row in place is what prevents the spurious re-read.
                    var existing = list.Items[i] as AudioAppChoice;
                    var sameRow = existing is not null
                        && string.Equals(existing.ProcessName, c.ProcessName, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(existing.ToString(), c.ToString(), StringComparison.Ordinal);
                    if (!sameRow) list.Items[i] = c;
                    if (list.GetItemChecked(i) != wantChecked) list.SetItemChecked(i, wantChecked);
                }
                else
                {
                    var idx = list.Items.Add(c);
                    if (wantChecked) list.SetItemChecked(idx, true);
                }
            }
        }
        finally
        {
            list.EndUpdate();
        }
    }

    /// <summary>Re-resolve the ticked apps' current process ids and, if they changed (an app opened or
    /// closed), re-apply the send sources so a remembered app STARTS being captured the instant it opens
    /// (or stops when it closes). Cheap: only touches the engine when the resolved PID set actually
    /// changes. Runs regardless of which tab is showing, so a remembered app is caught even when you're not
    /// looking at the list — this is the fix for "a saved app that launches later never gets captured".</summary>
    private void RefreshSendAppCapture()
    {
        if (!connected || suppressSendAppEvents) return;
        if (!ProcessLoopbackCapture.IsSupported) return;
        if (sendModeList.SelectedIndex != SendModeApplicationsIndex) return;
        var sig = ComputeSendAppPidSignature(CheckedSendApplicationNames(), AudioAppEnumerator.PidsForProcessName);
        if (sig == lastSendAppPidSignature) return;
        lastSendAppPidSignature = sig;
        ApplyAudioRuntime();
    }

    /// <summary>Pure, testable: a stable signature of the ticked apps' current process ids. Changes exactly
    /// when a ticked app opens or closes a process — which is when the send capture needs re-applying.</summary>
    internal static string ComputeSendAppPidSignature(IEnumerable<string> checkedNames, Func<string, IReadOnlyList<int>> pidsFor)
        => string.Join("|", checkedNames
            .SelectMany(name => pidsFor(name).Select(pid => $"{name}:{pid}"))
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase));

    /// <summary>Session-start callback (COM thread): an app just opened an audio session. Marshal to the UI
    /// thread and re-check our ticked apps — RefreshSendAppCapture no-ops if the new session isn't one of
    /// ours, and begins capturing immediately if it is. Best-effort.</summary>
    private void OnAppSessionStarted(int pid)
    {
        try { if (!IsDisposed) BeginInvoke((Action)RefreshSendAppCapture); } catch { /* form gone */ }
    }

    /// <summary>Stand up the session-start watcher while we're sending specific applications, so a remembered
    /// app is captured the instant it starts. No-op if already running or process-loopback isn't supported.</summary>
    private void EnsureSessionStartWatcher()
    {
        if (sessionStartWatcher is not null || !ProcessLoopbackCapture.IsSupported) return;
        try { sessionStartWatcher = new RemSound.Sender.AudioSessionStartWatcher(OnAppSessionStarted, m => logFile.Event($"session-start: {m}")); }
        catch (Exception ex) { logFile.Event($"session-start watcher unavailable: {ex.GetType().Name}: {ex.Message}"); }
    }

    private void DisposeSessionStartWatcher()
    {
        try { sessionStartWatcher?.Dispose(); } catch { }
        sessionStartWatcher = null;
    }

    /// <summary>Restores the WASAPI send mode and the ticked app names from a loaded profile (the main
    /// window's "Send all applications" master toggle was removed 2026-07-16). Remembered apps that aren't
    /// running right now are seeded into the list (ticked, marked "not running") so they resume capture the
    /// moment they reappear. On Windows too old for process loopback the mode is forced back to devices.</summary>
    private void RestoreSendModeFromProfile(Profile p)
    {
        suppressSendAppEvents = true;
        try
        {
            var wantApps = ProcessLoopbackCapture.IsSupported
                && string.Equals(p.WasapiSendMode, "applications", StringComparison.OrdinalIgnoreCase);
            sendModeList.SelectedIndex = wantApps ? SendModeApplicationsIndex : SendModeDevicesIndex;

            // The profile's active apps become the shared send set; ReconcileSendAppsList (below) then
            // renders both lists (active + remembered) from it.
            selectedSendApps.Clear();
            foreach (var name in (p.SelectedSendApplications ?? new())
                         .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
                selectedSendApps.Add(name);
        }
        finally
        {
            suppressSendAppEvents = false;
        }

        ApplySendModeVisibility();
        if (sendModeList.SelectedIndex == SendModeApplicationsIndex) ReconcileSendAppsList();
        RememberCheckedApps();
    }

    /// <summary>Add the currently-ticked app names to the GLOBAL remembered-applications list (the shared
    /// "apps I send" address book) — so a tick in any profile remembers the app for all of them, mirroring
    /// how remembered peers work. Cleared from Preferences → General.</summary>
    private void RememberCheckedApps()
    {
        var names = CheckedSendApplicationNames();
        if (names.Count == 0) return;
        var remembered = settings.LoadRememberedApplications().ToList();
        var added = false;
        foreach (var n in names)
            if (!remembered.Any(e => string.Equals(e, n, StringComparison.OrdinalIgnoreCase))) { remembered.Add(n); added = true; }
        if (added) settings.SaveRememberedApplications(remembered);
    }

    /// <summary>The process names the user has ticked in the app list (lower-case, no extension).</summary>
    private List<string> CheckedSendApplicationNames() => selectedSendApps.ToList();

    /// <summary>One row in the "Applications to send" list. Identity is the process NAME; the display
    /// adds a "(not running)" hint for a remembered-but-closed app.</summary>
    private sealed class AudioAppChoice
    {
        public string ProcessName { get; }
        private readonly string display;
        private readonly bool running;
        public AudioAppChoice(string processName, string displayName, bool running)
        {
            ProcessName = processName;
            display = displayName;
            this.running = running;
        }
        public override string ToString() => running ? display : $"{display} (not running)";
    }

    /// <summary>Audio profile tab — split into two GroupBox sections so NVDA announces the
    /// section name when focus first crosses into it. Send-side group: codec, packet size,
    /// lock to audio clock. Receive-side group: latency + auto-tune controls, buffer
    /// smoothness, artefact. Inside each group, focus traversal is the natural top-to-bottom
    /// order; crossing the boundary triggers NVDA's grouping-name announcement on the first
    /// child of the entered group. GroupBox `Text` is also the accessible name (single-source
    /// label rule); no `&` mnemonic since GroupBox isn't focusable. Phase 3 of the refactor;
    /// previously these controls lived inside ShowConnectivityTransportDialog as "dialog*"
    /// mirrors of hidden form-fields.</summary>
    private void BuildAudioProfileTab()
    {
        // Outer layout: one column, three rows. Row 0 is the Full-CPU-speed checkbox — the
        // first thing the user lands on when they Tab into the tab, deliberately ungrouped
        // and at the top so it can't be missed. Rows 1 and 2 are the existing Audio send
        // parameters / Audio receive parameters GroupBoxes. AutoScroll on so the tab page
        // handles overflow rather than the inner groups clipping their contents.
        var outerPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 3,
            AutoScroll = true,
        };
        outerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outerPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outerPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outerPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // Wrap the checkbox in its own FlowLayoutPanel — same NVDA-friendly pattern the
        // form's other top-level checkboxes use (a bare CheckBox in a TableLayoutPanel
        // cell suppresses some state-change announcements; the FlowLayoutPanel restores
        // the announcement chain).
        var priorityModePanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        priorityModePanel.Controls.Add(priorityModeBox);
        priorityModeBox.Checked = settings.LoadPriorityMode();
        priorityModeBox.CheckedChanged += (_, _) =>
        {
            settings.SavePriorityMode(priorityModeBox.Checked);
            // Re-evaluate the streaming-scoped levers right away: unticking releases them
            // immediately; ticking engages them now if audio is moving (else on the next tick
            // once it is). See EvaluatePriorityModeScope.
            EvaluatePriorityModeScope();
            MarkProfileDirty();
        };

        var sendGroup = new GroupBox
        {
            Text = "Audio send parameters",
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(8, 4, 8, 8),
        };
        var receiveGroup = new GroupBox
        {
            Text = "Audio receive parameters",
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(8, 4, 8, 8),
        };

        BuildAudioSendGroupContents(sendGroup);
        BuildAudioReceiveGroupContents(receiveGroup);

        outerPanel.Controls.Add(priorityModePanel, 0, 0);
        outerPanel.Controls.Add(sendGroup, 0, 1);
        outerPanel.Controls.Add(receiveGroup, 0, 2);
        audioProfileTabPage.Controls.Add(outerPanel);
    }

    /// <summary>An item in the Pan-and-EQ peer picker. Keyed by peer address string (the same key the
    /// per-profile <see cref="PeerShaping"/> dictionary uses, and what the receiver routes DSP by).</summary>
    private sealed class PanEqPeerItem(string label, System.Net.IPAddress address, string key)
    {
        public string Label { get; } = label;
        public System.Net.IPAddress Address { get; } = address;
        public string Key { get; } = key;
        public override string ToString() => Label;
    }

    /// <summary>Builds the "Volume, pan and EQ for peers" tab: one master switch, a checklist of
    /// connected peers (tick = shape that peer), then the selected peer's volume + pan sliders, a
    /// reset-EQ button, an EQ-mode picker (3-band simple / 12-band graphic / 16-band parametric) and
    /// that mode's controls, plus a purely-visual response curve. Every control acts on the peer the
    /// cursor is on, applies in real time, and is saved per profile. See <see cref="PeerDspChain"/> /
    /// <see cref="PeerShaping"/>.</summary>
    private void BuildPanEqTab()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 9, AutoScroll = true };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        enableAllPeerShapingBox.CheckedChanged += (_, _) => { if (!loadingPanEqControls) { logFile.Event($"shaping: master switch {(enableAllPeerShapingBox.Checked ? "on" : "off")} ({peerShaping.Count} peer(s) with saved shaping)"); MarkProfileDirty(); ApplyAllPeerShaping(); } };
        panEqPeerList.SelectedIndexChanged += (_, _) => OnPanEqPeerSelected();
        panEqPeerList.ItemCheck += OnPeerShapeToggled;
        // First-letter navigation on some Windows configs can accidentally toggle the checkbox; do the
        // letter-nav ourselves and swallow the default (per the workspace CheckedListBox guidance).
        panEqPeerList.KeyDown += OnPeerListKeyDown;
        volumeSlider.ValueChanged += (_, _) => OnVolumeChanged();
        panSlider.ValueChanged += (_, _) => OnPanChanged();
        resetPeerEqButton.Click += (_, _) => OnResetPeerEq();
        addBandButton.Click += (_, _) => OnAddParametricBand();
        deleteBandButton.Click += (_, _) => OnDeleteParametricBands();
        parametricBandList.KeyDown += OnParametricBandListKeyDown;
        eqModeList.Items.Add("3 band simple EQ");
        eqModeList.Items.Add("12 band advanced graphic EQ");
        eqModeList.Items.Add("16 band parametric EQ");
        eqModeList.SelectedIndexChanged += (_, _) => OnEqModeChanged();

        var peerLabel = new MnemonicLabel { Text = "Peers (Alt+&U)", AutoSize = true, MnemonicTarget = panEqPeerList };
        var volumeLabel = new MnemonicLabel { Text = "Vo&lume (Alt+L)", AutoSize = true, MnemonicTarget = volumeSlider };
        var panLabel = new MnemonicLabel { Text = "Pa&n (Alt+N)", AutoSize = true, MnemonicTarget = panSlider };
        var modeLabel = new MnemonicLabel { Text = "EQ &mode (Alt+M)", AutoSize = true, MnemonicTarget = eqModeList };

        var volumeRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        volumeRow.Controls.Add(volumeLabel);
        volumeRow.Controls.Add(volumeSlider);
        var panRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        panRow.Controls.Add(panLabel);
        panRow.Controls.Add(panSlider);
        var modeRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        modeRow.Controls.Add(modeLabel);
        modeRow.Controls.Add(eqModeList);

        panel.Controls.Add(enableAllPeerShapingBox, 0, 0);
        panel.Controls.Add(peerLabel, 0, 1);
        panel.Controls.Add(panEqPeerList, 0, 2);
        panel.Controls.Add(volumeRow, 0, 3);
        panel.Controls.Add(panRow, 0, 4);
        panel.Controls.Add(resetPeerEqButton, 0, 5);
        panel.Controls.Add(modeRow, 0, 6);
        panel.Controls.Add(eqBandsPanel, 0, 7);
        panel.Controls.Add(eqCurve, 0, 8);
        panEqTabPage.Controls.Add(panel);

        RefreshPanEqPeerList();
        OnPanEqPeerSelected();
    }

    // The main-window tabs' stable keys, in the default order. The user can reorder them on the
    // Preferences Appearance tab; "paneq" is only shown when the "show pan/EQ tab" preference is on.
    private static readonly string[] DefaultTabOrder = ["connectivity", "audioio", "paneq", "audioprofile"];

    private TabPage? TabPageForKey(string key) => key switch
    {
        "connectivity" => connectivityTabPage,
        "audioio" => audioIOTabPage,
        "paneq" => panEqTabPage,
        "audioprofile" => audioProfileTabPage,
        _ => null,
    };

    /// <summary>Rebuilds the main tab strip in the user's saved order, dropping the pan/EQ tab if that
    /// preference is off. Preserves the current selection. Called at build and after Preferences close.</summary>
    private void ApplyMainTabLayout()
    {
        var cfg = AppConfig.Load();
        var order = NormalizeTabOrder(cfg.MainTabOrder);
        bool showEq = cfg.ShowPanEqTab;
        var selected = mainTabControl.SelectedTab;
        mainTabControl.SuspendLayout();
        try
        {
            mainTabControl.TabPages.Clear();
            foreach (var key in order)
            {
                if (key == "paneq" && !showEq) continue;
                if (TabPageForKey(key) is { } page) mainTabControl.TabPages.Add(page);
            }
            if (selected is not null && mainTabControl.TabPages.Contains(selected))
                mainTabControl.SelectedTab = selected;
        }
        finally { mainTabControl.ResumeLayout(); }
    }

    /// <summary>Cleans a saved tab order: keep only known keys (in saved order, de-duplicated), then
    /// append any known keys the saved list was missing, so the result always has all four.</summary>
    private static List<string> NormalizeTabOrder(List<string>? saved)
    {
        var result = new List<string>();
        if (saved is not null)
            foreach (var k in saved)
                if (Array.IndexOf(DefaultTabOrder, k) >= 0 && !result.Contains(k)) result.Add(k);
        foreach (var k in DefaultTabOrder)
            if (!result.Contains(k)) result.Add(k);
        return result;
    }

    /// <summary>Rebuilds the peer picker from the currently-connected peers, one row per address. Runs
    /// each tick but only rebuilds on a real change (so NVDA focus survives). On a change it also
    /// re-pushes every connected peer's shaping, so a freshly-connected peer picks up its saved pan/EQ.</summary>
    private void RefreshPanEqPeerList()
    {
        var desired = new List<PanEqPeerItem>();
        var seen = new HashSet<string>();
        foreach (var (id, ep) in selectedPeerEndpoints)
        {
            var key = ep.Address.ToString();
            if (!seen.Add(key)) continue;
            var label = selectedPeerLabels.GetValueOrDefault(id, key);
            desired.Add(new PanEqPeerItem(label, ep.Address, key));
        }
        desired = desired.OrderBy(d => d.Label).ThenBy(d => d.Key).ToList();
        var signature = string.Join("|", desired.Select(d => d.Key + "=" + d.Label));
        if (signature == lastPanEqPeerSignature) return;
        lastPanEqPeerSignature = signature;

        var prevKey = (panEqPeerList.SelectedItem as PanEqPeerItem)?.Key ?? selectedShapingKey;
        loadingPanEqControls = true;
        try
        {
            panEqPeerList.BeginUpdate();
            panEqPeerList.Items.Clear();
            int idx = -1;
            foreach (var d in desired)
            {
                // Tick reflects the peer's saved Enabled flag (a per-peer bypass). Add(item, isChecked)
                // sets the initial state without raising ItemCheck.
                bool ticked = GetShaping(d.Key)?.Enabled ?? true;
                int i = panEqPeerList.Items.Add(d, ticked);
                if (d.Key == prevKey) idx = i;
            }
            if (idx < 0 && panEqPeerList.Items.Count > 0) idx = 0;
            if (idx >= 0) panEqPeerList.SelectedIndex = idx;
            panEqPeerList.EndUpdate();
        }
        finally { loadingPanEqControls = false; }

        ApplyAllPeerShaping();
    }

    /// <summary>Loads the peer selected in the picker into the pan / mode / band controls.</summary>
    private void OnPanEqPeerSelected()
    {
        selectedShapingKey = (panEqPeerList.SelectedItem as PanEqPeerItem)?.Key;
        bool enabled = selectedShapingKey is not null;
        loadingPanEqControls = true;
        try
        {
            // Read-only for display: use the existing shaping if any, else a throwaway default. Do NOT
            // GetOrCreateShaping here — merely selecting/scrolling a peer would then insert a no-op entry
            // into the saved profile for every peer the user only glanced at. The actual edit handlers
            // (pan/volume/mode/band) call GetOrCreateShaping, so an entry is created only on a real change.
            var s = GetShaping(selectedShapingKey) ?? new PeerShaping();
            volumeSlider.Value = Math.Clamp((int)Math.Round(s.Volume * 100f), 0, 100);
            UpdateVolumeAccessibleName();
            panSlider.Value = Math.Clamp((int)Math.Round(s.Pan * 50f) + 50, 0, 100);
            UpdatePanAccessibleName();
            eqModeList.SelectedIndex = (int)s.EqMode;   // enum values line up with the picker rows
            RebuildEqBandSliders();
            volumeSlider.Enabled = enabled;
            panSlider.Enabled = enabled;
            resetPeerEqButton.Enabled = enabled;
            eqModeList.Enabled = enabled;
            UpdateEqCurve();
        }
        finally { loadingPanEqControls = false; }
    }

    // The three EQ-mode picker rows map one-to-one onto the PeerEqMode enum values.
    private static PeerEqMode ModeForIndex(int index) => index switch
    {
        2 => PeerEqMode.Parametric16Band,
        1 => PeerEqMode.Advanced10Band,
        _ => PeerEqMode.Simple3Band,
    };

    /// <summary>Fired when the user ticks/unticks a peer in the checklist — flips that peer's per-peer
    /// bypass and re-applies its shaping immediately.</summary>
    private void OnPeerShapeToggled(object? sender, ItemCheckEventArgs e)
    {
        if (loadingPanEqControls) return;
        if (panEqPeerList.Items[e.Index] is not PanEqPeerItem item) return;
        GetOrCreateShaping(item.Key).Enabled = e.NewValue == CheckState.Checked;
        MarkProfileDirty();
        // The check state isn't committed until after this event returns, so defer the re-apply.
        BeginInvoke(() => ApplyPeerShaping(item.Key));
    }

    // Manual first-letter navigation so a letter key never toggles the tick (workspace guidance).
    private void OnPeerListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Modifiers != Keys.None) return;
        char c = (char)e.KeyValue;
        if (!char.IsLetterOrDigit(c)) return;
        int count = panEqPeerList.Items.Count;
        if (count == 0) return;
        int start = panEqPeerList.SelectedIndex;
        for (int step = 1; step <= count; step++)
        {
            int i = (start + step) % count;
            if (panEqPeerList.Items[i]?.ToString() is string t && t.Length > 0
                && char.ToUpperInvariant(t[0]) == char.ToUpperInvariant(c))
            {
                panEqPeerList.SelectedIndex = i;
                break;
            }
        }
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private PeerShaping? GetShaping(string? key) => key is not null && peerShaping.TryGetValue(key, out var s) ? s : null;

    private PeerShaping GetOrCreateShaping(string? key)
    {
        if (key is null) return NormalizeBands(new PeerShaping());
        if (!peerShaping.TryGetValue(key, out var s)) { s = new PeerShaping(); peerShaping[key] = s; }
        return NormalizeBands(s);
    }

    // Grow the band-gain arrays to the current band counts (e.g. a profile saved when the advanced EQ
    // had 10 bands, now 12) so the new bands can be read and stored. New slots are 0 dB (flat).
    private static PeerShaping NormalizeBands(PeerShaping s)
    {
        if (s.SimpleBandsDb.Length < PeerEqBands.Simple.Length)
        {
            var a = s.SimpleBandsDb; Array.Resize(ref a, PeerEqBands.Simple.Length); s.SimpleBandsDb = a;
        }
        if (s.AdvancedBandsDb.Length < PeerEqBands.Advanced.Length)
        {
            var a = s.AdvancedBandsDb; Array.Resize(ref a, PeerEqBands.Advanced.Length); s.AdvancedBandsDb = a;
        }
        return s;
    }

    private void OnPanChanged()
    {
        if (loadingPanEqControls || selectedShapingKey is null) return;
        GetOrCreateShaping(selectedShapingKey).Pan = Math.Clamp((panSlider.Value - 50) / 50f, -1f, 1f);
        UpdatePanAccessibleName();
        ApplyPeerShaping(selectedShapingKey);
        MarkProfileDirty();
    }

    private void UpdatePanAccessibleName()
    {
        int v = panSlider.Value;
        string desc = v == 50 ? "centre" : v < 50 ? $"{(50 - v) * 2} percent left" : $"{(v - 50) * 2} percent right";
        panSlider.AccessibleName = $"Pan: {desc}";
    }

    private void OnVolumeChanged()
    {
        if (loadingPanEqControls || selectedShapingKey is null) return;
        GetOrCreateShaping(selectedShapingKey).Volume = Math.Clamp(volumeSlider.Value / 100f, 0f, 1f);
        UpdateVolumeAccessibleName();
        ApplyPeerShaping(selectedShapingKey);
        MarkProfileDirty();
    }

    private void UpdateVolumeAccessibleName() => volumeSlider.AccessibleName = $"Volume: {volumeSlider.Value} percent";

    private void OnEqModeChanged()
    {
        if (loadingPanEqControls || selectedShapingKey is null) return;
        var mode = ModeForIndex(eqModeList.SelectedIndex);
        GetOrCreateShaping(selectedShapingKey).EqMode = mode;
        logFile.Event($"shaping: EQ mode → {mode} for {selectedShapingKey}");
        RebuildEqBandSliders();
        ApplyPeerShaping(selectedShapingKey);
        UpdateEqCurve();
        MarkProfileDirty();
    }

    private void OnResetPeerEq()
    {
        if (selectedShapingKey is null) return;
        var s = GetOrCreateShaping(selectedShapingKey);
        Array.Clear(s.SimpleBandsDb);
        Array.Clear(s.AdvancedBandsDb);
        s.ParametricBands.Clear();   // reset clears the 16-band parametric list too
        RebuildEqBandSliders();
        ApplyPeerShaping(selectedShapingKey);
        UpdateEqCurve();
        MarkProfileDirty();
    }

    /// <summary>Rebuilds the band sliders for the current EQ mode + selected peer, loading their gains.</summary>
    private void RebuildEqBandSliders()
    {
        loadingPanEqControls = true;
        try
        {
            eqBandsPanel.SuspendLayout();
            // Clear the panel. The three parametric controls are persistent members reused across
            // rebuilds — remove but never dispose them; everything else (slider rows, the bands label)
            // is freshly built each time and disposed here.
            var persistent = new Control[] { addBandButton, deleteBandButton, parametricBandList };
            while (eqBandsPanel.Controls.Count > 0)
            {
                var c = eqBandsPanel.Controls[0];
                eqBandsPanel.Controls.RemoveAt(0);
                if (Array.IndexOf(persistent, c) < 0) c.Dispose();
            }
            eqBandSliders.Clear();

            var s = GetShaping(selectedShapingKey);
            var mode = ModeForIndex(eqModeList.SelectedIndex);
            if (mode == PeerEqMode.Parametric16Band)
            {
                BuildParametricPanel();
                eqBandsPanel.ResumeLayout();
                return;
            }

            var bands = mode == PeerEqMode.Advanced10Band ? PeerEqBands.Advanced : PeerEqBands.Simple;
            var gains = s is null ? null : (mode == PeerEqMode.Advanced10Band ? s.AdvancedBandsDb : s.SimpleBandsDb);

            for (int i = 0; i < bands.Length; i++)
            {
                float gainDb = gains is not null && i < gains.Length ? gains[i] : 0f;
                var slider = new TrackBar
                {
                    Minimum = 0,
                    Maximum = 100,
                    Value = Math.Clamp((int)Math.Round(gainDb / PeerEqBands.MaxGainDb * 50f) + 50, 0, 100),
                    SmallChange = 1,
                    LargeChange = 10,
                    TickFrequency = 25,
                    Width = 300,
                    Tag = i,
                    Enabled = selectedShapingKey is not null,
                };
                UpdateBandAccessibleName(slider, bands[i].Label);
                slider.ValueChanged += (_, _) => OnBandChanged(slider);
                var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
                row.Controls.Add(new MnemonicLabel { Text = bands[i].Label, AutoSize = true, MnemonicTarget = slider });
                row.Controls.Add(slider);
                eqBandsPanel.Controls.Add(row);
                eqBandSliders.Add(slider);
            }
            eqBandsPanel.ResumeLayout();
        }
        finally { loadingPanEqControls = false; }
    }

    private void OnBandChanged(TrackBar slider)
    {
        if (loadingPanEqControls || selectedShapingKey is null || slider.Tag is not int i) return;
        var s = GetOrCreateShaping(selectedShapingKey);
        var mode = ModeForIndex(eqModeList.SelectedIndex);
        var gains = mode == PeerEqMode.Advanced10Band ? s.AdvancedBandsDb : s.SimpleBandsDb;
        var bands = mode == PeerEqMode.Advanced10Band ? PeerEqBands.Advanced : PeerEqBands.Simple;
        if (i >= 0 && i < gains.Length)
        {
            gains[i] = (slider.Value - 50) / 50f * PeerEqBands.MaxGainDb;
            UpdateBandAccessibleName(slider, bands[i].Label);
        }
        ApplyPeerShaping(selectedShapingKey);
        UpdateEqCurve();
        MarkProfileDirty();
    }

    private static void UpdateBandAccessibleName(TrackBar slider, string label)
    {
        float db = (slider.Value - 50) / 50f * PeerEqBands.MaxGainDb;
        slider.AccessibleName = $"{label}: {FormatGainDb(db)}";
    }

    // dB spoken as words — most NVDA users run with punctuation off and would never hear a "+" sign,
    // so a boost must say "plus". ("minus" comes through on its own, but we spell both for symmetry.)
    // The graphic sliders read whole dB; the parametric list keeps up to one decimal (e.g. 1.5 dB).
    private static string FormatGainDb(float db)
    {
        if (MathF.Abs(db) < 0.5f) return "flat";
        return db > 0 ? $"plus {db:0} dB" : $"minus {MathF.Abs(db):0} dB";
    }

    private static string FormatGainDbPrecise(float db)
    {
        if (MathF.Abs(db) < 0.05f) return "flat";
        return db > 0 ? $"plus {db:0.#} dB" : $"minus {MathF.Abs(db):0.#} dB";
    }

    /// <summary>Whether a given peer is shaped right now: the profile-wide master switch AND that peer's
    /// own tick (per-peer bypass) both have to be on.</summary>
    private bool ShapingActiveFor(string? key)
        => enableAllPeerShapingBox.Checked && (GetShaping(key)?.Enabled ?? true);

    /// <summary>Builds one peer's DSP chain (honouring the master switch and the peer's own tick) and
    /// pushes it to the receiver, so the change is heard immediately. A null chain (nothing to do)
    /// clears any prior one.</summary>
    private void ApplyPeerShaping(string? key)
    {
        if (key is null) return;
        var addr = ResolvePeerAddress(key);
        if (addr is null) return;
        var chain = PeerDspChain.Build(GetShaping(key), ShapingActiveFor(key));
        receiver.SetPeerDsp(addr, chain);
    }

    /// <summary>Pushes shaping for every currently-connected peer. Used when the master switch flips or a
    /// profile loads / the connected set changes.</summary>
    private void ApplyAllPeerShaping()
    {
        var seen = new HashSet<string>();
        foreach (var (_, ep) in selectedPeerEndpoints)
        {
            var key = ep.Address.ToString();
            if (!seen.Add(key)) continue;
            var chain = PeerDspChain.Build(GetShaping(key), ShapingActiveFor(key));
            receiver.SetPeerDsp(ep.Address, chain);
        }
    }

    private System.Net.IPAddress? ResolvePeerAddress(string? key)
    {
        if (key is null) return null;
        foreach (var (_, ep) in selectedPeerEndpoints)
            if (ep.Address.ToString() == key) return ep.Address;
        return System.Net.IPAddress.TryParse(key, out var addr) ? addr : null;
    }

    // === 16-band parametric EQ ===

    /// <summary>Populates <see cref="eqBandsPanel"/> with the parametric controls (Add band, the band
    /// list, Delete band). The three controls are persistent members reused across rebuilds.</summary>
    private void BuildParametricPanel()
    {
        var bandsLabel = new MnemonicLabel { Text = "Bands (Alt+&B)", AutoSize = true, MnemonicTarget = parametricBandList };
        eqBandsPanel.Controls.Add(addBandButton);
        eqBandsPanel.Controls.Add(bandsLabel);
        eqBandsPanel.Controls.Add(parametricBandList);
        eqBandsPanel.Controls.Add(deleteBandButton);
        RefreshParametricBandList();
    }

    /// <summary>Rebuilds the band list from the selected peer, sorted bass→treble, each row spelling its
    /// dB in words. Also enables/disables Add (capped at 16 bands) and Delete.</summary>
    private void RefreshParametricBandList()
    {
        loadingPanEqControls = true;
        try
        {
            parametricBandList.BeginUpdate();
            parametricBandList.Items.Clear();
            var s = GetShaping(selectedShapingKey);
            if (s is not null)
            {
                foreach (var band in s.ParametricBands.OrderBy(b => b.StartHz).ThenBy(b => b.EndHz))
                    parametricBandList.Items.Add(new ParametricBandItem(band));
            }
            parametricBandList.EndUpdate();
            int count = s?.ParametricBands.Count ?? 0;
            bool havePeer = selectedShapingKey is not null;
            parametricBandList.Enabled = havePeer;
            addBandButton.Enabled = havePeer && count < PeerEqBands.ParametricMaxBands;
            deleteBandButton.Enabled = havePeer && count > 0;
        }
        finally { loadingPanEqControls = false; }
    }

    private void OnAddParametricBand()
    {
        if (selectedShapingKey is null) return;
        var s = GetOrCreateShaping(selectedShapingKey);
        if (s.ParametricBands.Count >= PeerEqBands.ParametricMaxBands) return;

        // Live preview: while the dialog is open, apply the in-progress band on top of the saved bands
        // so the peer's sound changes as the user moves the values; null reverts to the saved shaping.
        void Preview(ParametricBand? band)
        {
            var saved = GetShaping(selectedShapingKey);
            var addr = ResolvePeerAddress(selectedShapingKey);
            if (saved is null || addr is null) return;
            if (band is null) { ApplyPeerShaping(selectedShapingKey); return; }
            var temp = new PeerShaping
            {
                Enabled = saved.Enabled,
                Pan = saved.Pan,
                Volume = saved.Volume,
                EqMode = PeerEqMode.Parametric16Band,
                ParametricBands = new List<ParametricBand>(saved.ParametricBands) { band },
            };
            receiver.SetPeerDsp(addr, PeerDspChain.Build(temp, ShapingActiveFor(selectedShapingKey)));
        }

        using var dlg = new AddBandDialog(Preview);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            s.ParametricBands.Add(dlg.Result);
            logFile.Event($"shaping: added parametric band {dlg.Result.StartHz:0}-{dlg.Result.EndHz:0} Hz {dlg.Result.GainDb:0.#} dB for {selectedShapingKey} ({s.ParametricBands.Count} band(s))");
            RefreshParametricBandList();
            MarkProfileDirty();
        }
        ApplyPeerShaping(selectedShapingKey);   // settle on the real saved shaping either way
        UpdateEqCurve();
    }

    private void OnDeleteParametricBands()
    {
        if (selectedShapingKey is null) return;
        var s = GetShaping(selectedShapingKey);
        if (s is null || parametricBandList.SelectedItems.Count == 0) return;
        int firstIdx = parametricBandList.SelectedIndex;
        var toRemove = parametricBandList.SelectedItems.Cast<ParametricBandItem>().Select(x => x.Band).ToList();
        foreach (var band in toRemove) s.ParametricBands.Remove(band);
        logFile.Event($"shaping: deleted {toRemove.Count} parametric band(s) for {selectedShapingKey} ({s.ParametricBands.Count} left)");
        RefreshParametricBandList();
        // Put focus on whatever now occupies the first removed slot so NVDA announces it.
        if (parametricBandList.Items.Count > 0)
            parametricBandList.SelectedIndex = Math.Clamp(firstIdx, 0, parametricBandList.Items.Count - 1);
        ApplyPeerShaping(selectedShapingKey);
        UpdateEqCurve();
        MarkProfileDirty();
    }

    private void OnParametricBandListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Delete)
        {
            OnDeleteParametricBands();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        // Left / Right nudge the selected band(s) gain by half a dB — quick on-the-fly editing, heard
        // in real time. Up / Down still move between bands as normal.
        if ((e.KeyCode == Keys.Left || e.KeyCode == Keys.Right)
            && selectedShapingKey is not null && parametricBandList.SelectedItems.Count > 0)
        {
            float delta = e.KeyCode == Keys.Right ? 0.5f : -0.5f;
            foreach (var it in parametricBandList.SelectedItems.Cast<ParametricBandItem>())
                it.Band.GainDb = Math.Clamp(it.Band.GainDb + delta, -PeerEqBands.MaxGainDb, PeerEqBands.MaxGainDb);
            parametricBandList.Invalidate();   // redraw the rows with their updated dB (ToString reads live)
            ApplyPeerShaping(selectedShapingKey);
            UpdateEqCurve();
            MarkProfileDirty();
            // Speak the new gain through the screen reader as it moves — a UIA notification (NVDA reads
            // it natively; not an extra speech layer). A plain listbox item won't announce on its own.
            if (parametricBandList.SelectedItem is ParametricBandItem focused)
            {
                parametricBandList.AccessibilityObject.RaiseAutomationNotification(
                    System.Windows.Forms.Automation.AutomationNotificationKind.ActionCompleted,
                    System.Windows.Forms.Automation.AutomationNotificationProcessing.MostRecent,
                    FormatGainDbPrecise(focused.Band.GainDb));
            }
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void UpdateEqCurve() => eqCurve.SetResponse(GetShaping(selectedShapingKey));

    // One list row per parametric band, e.g. "200 Hz to 2000 Hz, plus 3 dB". Holds the band by
    // reference so Delete can remove the exact object.
    private sealed class ParametricBandItem(ParametricBand band)
    {
        public ParametricBand Band { get; } = band;
        public override string ToString() => $"{Band.StartHz:0} Hz to {Band.EndHz:0} Hz, {FormatGainDbPrecise(Band.GainDb)}";
    }

    /// <summary>Send-side controls: codec + packet size on row 0, lock-to-audio-clock on
    /// row 1. The codec and packet-size combo share a row because they're tightly coupled
    /// (changing the codec resets the meaningful packet sizes). Lock-to-clock is a sender-
    /// side toggle whose label varies by audio mode (WASAPI vs ASIO vs Both).</summary>
    private void BuildAudioSendGroupContents(GroupBox group)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            AutoSize = true,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // === Row 0: codec + packet size ===
        // Packet size: per-packet audio frame the sender chops into. Smaller = lower send-side
        // accumulator latency at the cost of doubling packet rate (more sensitive to USB /
        // network hiccups). Renamed from "Send rate" 2026-05-02 — the label confused users
        // into thinking it was a bandwidth knob.
        sendRateBox.Items.Clear();
        sendRateBox.Items.Add("Standard (5 ms PCM, 10/20 ms Opus)");
        sendRateBox.Items.Add("Small (2.5 ms PCM, 5/10 ms Opus, LAN only)");
        sendRateBox.SelectedIndex = (int)settings.LoadSendRate();
        sendRateBox.SelectedIndexChanged += (_, _) =>
        {
            var newRate = (SendRate)sendRateBox.SelectedIndex;
            settings.SaveSendRate(newRate);
            sender.SetSendRate(newRate);
            ApplySendRateToOpus(newRate);
            MarkProfileDirty();
        };
        // 2026-05-08 mnemonic refresh per Ed's spec:
        //   Audio codec (renamed from "Transport codec") → Alt+C  (was Alt+T)
        //   Packet size                                  → Alt+P  (was Alt+S)
        var codecAndSendLabel = new Label { Text = "Audio codec (Alt+&C) / Packet size (Alt+&P)", AutoSize = true, Anchor = AnchorStyles.Left };
        codecAndSendLabel.Click += (_, _) => FocusControl(codecBox);
        var codecRowPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        codecRowPanel.Controls.Add(codecBox);
        codecRowPanel.Controls.Add(new Label { Text = "  Packet size: ", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
        codecRowPanel.Controls.Add(sendRateBox);
        panel.Controls.Add(codecAndSendLabel, 0, 0);
        panel.Controls.Add(codecRowPanel, 1, 0);

        // "Lock to audio clock" (sender uses the WASAPI capture event for timing instead of a Stopwatch
        // tick) is now ALWAYS ON and is no longer a user option (Ed, 2026-07-17: nobody ever runs with it
        // off — off just adds delay). The sender is put into tight-latency mode unconditionally at startup;
        // the old per-profile checkbox is gone.

        group.Controls.Add(panel);
    }

    /// <summary>Receive-side controls: latency spinner + tune button + continuous-tune toggle
    /// + interval combo on row 0; smoothness list on row 1; artefact combo (with hint) on
    /// row 2. Tab order within the group flows naturally top-down. The tune-button hookup
    /// uses TuneLatencyAsync via the cancellation token field.</summary>
    private void BuildAudioReceiveGroupContents(GroupBox group)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            AutoSize = true,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // === Row 0: ASIO latency row — VISIBLE ONLY IN BOTHINDEPENDENT MODE ===
        // In BothIndependent the WASAPI and ASIO lanes have independent targets. The ASIO row
        // sits above the WASAPI row so it's first in tab order (ASIO is the "headline" lane
        // a user picks the new mode for) and takes the simpler Alt+L / Alt+T mnemonics — when
        // the user enters BothIndependent the WASAPI row's labels mutate to "WASAPI latency
        // (Alt+W)" / "Continuous auto-tune WASAPI (Alt+Y)", surrendering L/T to ASIO. In every
        // classic mode this row is hidden via UpdateBothIndependentVisibility and the WASAPI
        // row keeps the original "Audio latency (Alt+L)" labels.
        asioLatencyLabel = new Label { Text = "AS&IO latency in milliseconds (Alt+I)", AutoSize = true, Anchor = AnchorStyles.Left };
        asioLatencyLabel.Click += (_, _) => FocusControl(maxLatencyAsioBox);
        SelectAllOnFocus(maxLatencyAsioBox);
        maxLatencyAsioBox.Value = Math.Clamp(settings.LoadMaxLatencyMsAsio(), (int)maxLatencyAsioBox.Minimum, (int)maxLatencyAsioBox.Maximum);
        continuousTuneAsioBox.Text = "Continuous auto-tune ASIO latency (Alt+&T)";
        continuousTuneAsioBox.AccessibleName = "Continuous auto-tune ASIO latency";
        continuousTuneAsioBox.Checked = settings.LoadContinuousAutoTuneAsioEnabled();
        asioDelayContainer = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
        };
        asioDelayContainer.Controls.Add(maxLatencyAsioBox);
        asioDelayContainer.Controls.Add(continuousTuneAsioBox);
        panel.Controls.Add(asioLatencyLabel, 0, 0);
        panel.Controls.Add(asioDelayContainer, 1, 0);

        // === Row 1: WASAPI / classic latency row ===
        // Labels and mnemonics mutate based on audio mode — see UpdateBothIndependentVisibility.
        //   Classic modes: "Audio latency (Alt+L)" / "Continuous auto-tune latency (Alt+T)"
        //   BothIndependent: "WASAPI latency (Alt+W)" / "Continuous auto-tune WASAPI (Alt+Y)"
        // The interval dropdown stays attached to this row in both modes; one interval setting
        // governs both lanes' auto-tune ticks (separate intervals would be more knobs than
        // value).
        wasapiLatencyLabel = new Label { Text = "Audio latency in milliseconds (Alt+&L)", AutoSize = true, Anchor = AnchorStyles.Left };
        wasapiLatencyLabel.Click += (_, _) => FocusControl(maxLatencyBox);
        SelectAllOnFocus(maxLatencyBox);
        continuousTuneBox.Text = "Continuous auto-tune latency (Alt+&T)";
        continuousTuneBox.AccessibleName = "Continuous auto-tune latency";
        continuousTuneBox.Checked = continuousTuneEnabled;
        // 3 seconds added 2026-05-06 alongside the lookback shortening — the new combination
        // lets users dial in tighter latency on calm networks much faster (each tick samples
        // then potentially lowers, so 3s ticks × 5ms/tick = 1.7ms/sec descent).
        continuousIntervalBox.Items.Clear();
        continuousIntervalBox.Items.AddRange(new object[] { "3 seconds", "5 seconds", "10 seconds", "15 seconds", "30 seconds" });
        continuousIntervalBox.SelectedIndex = continuousTuneIntervalSec switch { 3 => 0, 5 => 1, 15 => 3, 30 => 4, _ => 2 };
        // Enable the interval combo whenever EITHER lane's auto-tune is on — the single
        // interval value governs both lanes' tick rates (see comment at row-1 docstring).
        // Previously this only followed the WASAPI checkbox, which made the combo grey out
        // in BothIndependent mode when only ASIO auto-tune was ticked, even though the
        // timer was running and the interval was being honoured for the ASIO lane.
        continuousIntervalBox.Enabled = AnyAutoTuneEnabled();
        // Label text is set by UpdateBothIndependentVisibility — it differs between classic
        // modes (single lane → "Auto-tune latency interval") and BothIndependent
        // (two lanes → "Auto-tune interval (WASAPI + ASIO)") to make explicit that the same
        // dropdown drives both lanes' tick cadence in the latter case.
        continuousIntervalLabel = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(8, 6, 0, 0) };
        var delayContainer = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
        };
        delayContainer.Controls.Add(maxLatencyBox);
        delayContainer.Controls.Add(continuousTuneBox);
        delayContainer.Controls.Add(continuousIntervalLabel);
        delayContainer.Controls.Add(continuousIntervalBox);
        panel.Controls.Add(wasapiLatencyLabel, 0, 1);
        panel.Controls.Add(delayContainer, 1, 1);

        continuousTuneBox.CheckedChanged += (_, _) =>
        {
            continuousTuneEnabled = continuousTuneBox.Checked;
            settings.SaveContinuousAutoTuneEnabled(continuousTuneEnabled);
            continuousIntervalBox.Enabled = AnyAutoTuneEnabled();
            ApplyContinuousTuneTimer();
            MarkProfileDirty();
        };
        continuousIntervalBox.SelectedIndexChanged += (_, _) =>
        {
            continuousTuneIntervalSec = continuousIntervalBox.SelectedIndex switch { 0 => 3, 1 => 5, 3 => 15, 4 => 30, _ => 10 };
            settings.SaveContinuousAutoTuneIntervalSec(continuousTuneIntervalSec);
            ApplyContinuousTuneTimer();
            MarkProfileDirty();
        };

        // === Row 1: Buffer smoothness ===
        smoothnessBox.Items.Clear();
        smoothnessBox.Items.Add("10 — smoothest, no clicks, longest delay");
        smoothnessBox.Items.Add("9");
        smoothnessBox.Items.Add("8");
        smoothnessBox.Items.Add("7");
        smoothnessBox.Items.Add("6");
        smoothnessBox.Items.Add("5");
        smoothnessBox.Items.Add("4");
        smoothnessBox.Items.Add("3 — default, brief clicks");
        smoothnessBox.Items.Add("2");
        smoothnessBox.Items.Add("1 — tightest delay, frequent clicks");
        // Map int smoothness ↔ list index: index 0 = 10, index 9 = 1.
        smoothnessBox.SelectedIndex = Math.Clamp(10 - settings.LoadSmoothness(), 0, 9);
        smoothnessBox.SelectedIndexChanged += (_, _) =>
        {
            if (smoothnessBox.SelectedIndex < 0) return;
            var newSmoothness = 10 - smoothnessBox.SelectedIndex;
            settings.SaveSmoothness(newSmoothness);
            receiver.SetSmoothness(newSmoothness);
            logFile.Event($"buffer smoothness changed to {newSmoothness}");
            MarkProfileDirty();
        };
        var smoothnessLabel = new Label { Text = "Buffer smoothness (Alt+&B)", AutoSize = true, Anchor = AnchorStyles.Left };
        smoothnessLabel.Click += (_, _) => FocusControl(smoothnessBox);
        panel.Controls.Add(smoothnessLabel, 0, 2);
        panel.Controls.Add(smoothnessBox, 1, 2);

        // === Row 2: Artefact ===
        artefactBox.Items.Clear();
        artefactBox.Items.Add("Noise burst (default) — broadband shhh, blends into music");
        artefactBox.Items.Add("Click — no concealment, raw zero-fill click");
        var loadedArtifact = settings.LoadConcealmentArtifact();
        artefactBox.SelectedIndex = loadedArtifact == ConcealmentArtifact.Click ? 1 : 0;
        artefactBox.SelectedIndexChanged += (_, _) =>
        {
            if (artefactBox.SelectedIndex < 0) return;
            var newArtifact = artefactBox.SelectedIndex == 1
                ? ConcealmentArtifact.Click
                : ConcealmentArtifact.NoiseBurst;
            settings.SaveConcealmentArtifact(newArtifact);
            receiver.SetConcealmentArtifact(newArtifact);
            logFile.Event($"concealment artifact changed to {newArtifact}");
            MarkProfileDirty();
        };
        var artefactLabel = new Label { Text = "Artefact sound type (Alt+&A)", AutoSize = true, Anchor = AnchorStyles.Left };
        artefactLabel.Click += (_, _) => FocusControl(artefactBox);
        var artefactHint = new Label
        {
            Text = "Use this to change the way audio artefacts sound when they appear (e.g. on brief network or buffer hiccups). Changes take effect immediately.",
            AutoSize = false,
            Width = 420,
            Height = 36,
            Anchor = AnchorStyles.Left,
        };
        var artefactContainer = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill };
        artefactContainer.Controls.Add(artefactHint);
        artefactContainer.Controls.Add(artefactBox);
        panel.Controls.Add(artefactLabel, 0, 3);
        panel.Controls.Add(artefactContainer, 1, 3);

        // Wire ASIO companion control event handlers and apply initial visibility now that
        // every element exists. After this method returns the panel is ready to dock into
        // its parent groupbox.
        WireBothIndependentControls();
        UpdateBothIndependentVisibility();

        group.Controls.Add(panel);
    }

    /// <summary>Calls all three peer-list sync helpers in one go. Wired into the existing
    /// status timer (1 Hz) so the Connectivity tab stays current with discovery / heartbeat
    /// state without needing its own dedicated timer.</summary>
    private void SyncAllPeerLists()
    {
        SyncConnectedList();
        SyncDiscoveredList();
        SyncRememberedList();
        RefreshPanEqPeerList();
        RefreshStatusReadout();
    }

    /// <summary>Updates the Connection-status read-only TextBox at the bottom of the
    /// Connectivity tab. Skips the actual Text-set when (a) the user is currently focused on
    /// the box (so NVDA isn't disrupted while reading), or (b) the freshly-computed text
    /// matches the last-rendered text (avoids redundant work and any chance of NVDA stutter).
    /// 2026-05-06.</summary>
    private void RefreshStatusReadout()
    {
        var text = ComputeStatusText();
        if (text == lastStatusReadoutText) return;
        lastStatusReadoutText = text;
        // Don't disrupt the user mid-read. The text we computed is already cached so the
        // next tick will pick it up if the user moves focus away.
        if (statusReadout.Focused) return;
        statusReadout.Text = text;
    }

    /// <summary>Speak the current connection status aloud through the active screen reader, via Tolk
    /// (<see cref="ScreenReader"/>). Answers issue #13: NVDA sometimes can't see the status readout
    /// ("no status line found"), so this reads it on demand. Triggered only by a user-set global hotkey
    /// (screen-reader only, unset by default) — being a system-wide hotkey it works from anywhere. The
    /// status is read line by line (the multi-line text is passed straight to the screen reader, which
    /// pauses at each line) rather than collapsed into a run-on sentence. A quick DOUBLE press copies
    /// the same text to the clipboard instead, so it can be shared. (Andre's suggestion, 2026-06-24.)</summary>
    private void SpeakStatusLine()
    {
        var now = DateTime.UtcNow;
        var withinDoublePressWindow = now - lastSpeakStatusPressUtc <= TimeSpan.FromMilliseconds(600);
        lastSpeakStatusPressUtc = now;

        var text = statusReadout.Text;
        if (string.IsNullOrWhiteSpace(text)) text = "No status information available.";

        // A quick second press copies the status to the clipboard instead of reading it again, so it can
        // be shared. This is a deliberate double-tap, not a key repeat — a single press never reaches here.
        if (withinDoublePressWindow)
        {
            // Second quick press: copy the status to the clipboard so it can be shared. Clipboard access
            // needs the UI thread — this runs on it (the hotkey is marshalled onto the owner form).
            try
            {
                Clipboard.SetText(text);
                ScreenReader.Speak("RemSound status copied to the clipboard.");
            }
            catch (Exception ex)
            {
                logFile.Event($"speak status: clipboard copy failed: {ex.GetType().Name}: {ex.Message}");
                ScreenReader.Speak("Could not copy the status to the clipboard.");
            }
            return;
        }

        // Single press: read it out, line by line (the screen reader pauses at each line break).
        ScreenReader.Speak(text);
    }

    private string ComputeStatusText()
    {
        // Compute byte-rates from delta since last sample. First call has no baseline so
        // the rate shows as 0; second and subsequent calls produce a real number.
        var nowUtc = DateTime.UtcNow;
        var txBytes = sender.BytesSent;
        var rxBytes = receiver.BytesReceived;
        statusSelfProcess.Refresh();
        var cpuTime = statusSelfProcess.TotalProcessorTime;
        var workingSetBytes = statusSelfProcess.WorkingSet64;
        double txKbs = 0, rxKbs = 0, cpuPercent = 0;
        if (lastStatusSampleUtc != DateTime.MinValue)
        {
            var elapsed = (nowUtc - lastStatusSampleUtc).TotalSeconds;
            if (elapsed > 0)
            {
                txKbs = (txBytes - lastStatusTxBytes) / 1024.0 / elapsed;
                rxKbs = (rxBytes - lastStatusRxBytes) / 1024.0 / elapsed;
                // CPU as a share of the whole machine (the Task Manager number): the process CPU-time
                // delta over wall-clock, divided by the logical-core count.
                var cpuDelta = (cpuTime - lastStatusCpuTime).TotalSeconds;
                cpuPercent = cpuDelta / elapsed / Math.Max(1, Environment.ProcessorCount) * 100.0;
                if (cpuPercent < 0) cpuPercent = 0;
            }
        }
        lastStatusSampleUtc = nowUtc;
        lastStatusTxBytes = txBytes;
        lastStatusRxBytes = rxBytes;
        lastStatusCpuTime = cpuTime;

        // Healthy peers from heartbeat. Map each to its display label (the user-friendly
        // name from selectedPeerLabels, falling back to the address).
        var healthy = new List<(string Label, int? RttMs)>();
        if (heartbeatService is { } hb)
        {
            foreach (var ph in hb.GetAllPeerHealth())
            {
                if (ph.State != PeerHealthState.Healthy) continue;
                // Find a label by walking selectedPeerEndpoints for a matching address+port.
                string? label = null;
                foreach (var (id, ep) in selectedPeerEndpoints)
                {
                    if (ep.Address.Equals(ph.AudioEndpoint.Address) && ep.Port == ph.AudioEndpoint.Port)
                    {
                        label = selectedPeerLabels.GetValueOrDefault(id);
                        break;
                    }
                }
                label ??= ph.AudioEndpoint.ToString();
                int? rtt = ph.RttMs is { } r ? RoundToFive(r) : null;
                healthy.Add((label, rtt));
            }
        }

        // Update the connected-since timestamp based on whether we have any healthy peers.
        if (healthy.Count > 0)
        {
            statusConnectedSinceUtc ??= nowUtc;
        }
        else
        {
            statusConnectedSinceUtc = null;
        }

        // Build the readout, one line per piece of information. Uses CRLF so the TextBox
        // multiline rendering is correct on Windows + readable to NVDA.
        var sb = new System.Text.StringBuilder();
        if (healthy.Count == 0)
        {
            sb.AppendLine("Not connected to any peer.");
        }
        else
        {
            sb.AppendLine($"Connected to {healthy.Count} peer{(healthy.Count == 1 ? "" : "s")}.");
            foreach (var (label, rtt) in healthy)
            {
                var rttStr = rtt is { } r ? $"{r} ms" : "unknown";
                sb.AppendLine($"  {label}: ping {rttStr}");
            }
        }

        if (statusConnectedSinceUtc is { } since)
        {
            var span = nowUtc - since;
            sb.AppendLine($"Uptime: {FormatUptime(span)}.");
        }
        else
        {
            sb.AppendLine("Uptime: 0 seconds.");
        }

        sb.AppendLine($"Receiving {rxKbs:0.0} kB/s; sending {txKbs:0.0} kB/s.");
        sb.AppendLine($"Total received {FormatDataSize(receiver.BytesReceived)}; sent {FormatDataSize(sender.BytesSent)}.");
        sb.Append($"CPU usage {cpuPercent:0}% and memory {FormatDataSize(workingSetBytes)}.");
        return sb.ToString();
    }

    /// <summary>A running data total as MB, switching to GB once it reaches a gigabyte — "935.8 MB",
    /// "4.5 GB" — so a big figure reads more naturally (Andre's suggestion). Binary units, matching the
    /// 1048576-byte MB used elsewhere.</summary>
    private static string FormatDataSize(long bytes)
    {
        const double mb = 1048576.0;
        const double gb = 1073741824.0;
        return bytes >= gb ? $"{bytes / gb:0.0} GB" : $"{bytes / mb:0.0} MB";
    }

    private static string FormatUptime(TimeSpan span)
    {
        if (span.TotalSeconds < 1) return "0 seconds";
        if (span.TotalMinutes < 1) return $"{(int)span.TotalSeconds} second{((int)span.TotalSeconds == 1 ? "" : "s")}";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} minute{((int)span.TotalMinutes == 1 ? "" : "s")} {span.Seconds} second{(span.Seconds == 1 ? "" : "s")}";
        return $"{(int)span.TotalHours} hour{((int)span.TotalHours == 1 ? "" : "s")} {span.Minutes} minute{(span.Minutes == 1 ? "" : "s")}";
    }

    /// <summary>
    /// Reads <c>list.SelectedItem</c> without the IndexOutOfRangeException WinForms' internal
    /// ItemArray throws when <c>SelectedIndex</c> is briefly left pointing past the item array.
    /// That happens during churny peer-list rebuilds (peer reboots, rapid reconnects): the
    /// 1 Hz Sync* tick read <c>SelectedItem</c> — whose getter blindly does Items[SelectedIndex]
    /// — and crashed the whole app from a timer callback. Bounds-check the index ourselves,
    /// the same defensive pattern the ItemCheck handlers already use. 2026-05-15.
    /// </summary>
    private static object? SafeSelectedItem(ListBox list)
    {
        var i = list.SelectedIndex;
        return i >= 0 && i < list.Items.Count ? list.Items[i] : null;
    }

    private void SyncConnectedList()
    {
        var desired = new List<(PeerListItem Item, Guid Id)>();
        foreach (var (id, ep) in selectedPeerEndpoints)
        {
            if (knownPeers.TryGetValue(id, out var known))
            {
                desired.Add((new PeerListItem(known), id));
            }
            else
            {
                var label = selectedPeerLabels.GetValueOrDefault(id, ep.Address.ToString());
                // Don't call a peer "offline" just because DISCOVERY briefly lost sight of it —
                // if audio is still arriving from it, or its heartbeat is healthy, it's plainly
                // still connected. Discovery beacons are easy to miss for a second; the audio
                // stream and heartbeat are the real signal. Only tag "(offline)" when it's gone
                // by every measure. 2026-06-02 (Ed's "offline but still sending audio" report).
                var stillThere = receiver.IsAudioFlowingFrom(ep.Address, TimeSpan.FromSeconds(3))
                    || IsEndpointHeartbeatHealthy(ep);
                var suffix = stillThere ? "" : OfflineMarker;
                var ghost = new PeerAnnouncement(id, $"{label}{suffix}", ep.Port, true, true, DateTime.UtcNow, ep.Address);
                desired.Add((new PeerListItem(ghost), id));
            }
        }
        desired = desired.OrderBy(d => d.Item.Peer.Name).ThenBy(d => d.Item.Peer.Address.ToString()).ToList();

        // Signature is stable identity only (peer id + name + address + port). Live status
        // (connected, codec, direction, RTT) is NOT in the signature — it gets updated in
        // place via RefreshItem so NVDA focus on a row survives tick updates.
        var signature = string.Join("|", desired.Select(d => d.Item.StableKey()));
        if (signature != lastConnectedListSignature)
        {
            lastConnectedListSignature = signature;
            var selectedId = SafeSelectedItem(connectedPeersList) is PeerListItem si ? si.Peer.InstanceId : Guid.Empty;
            suppressConnectedCheck = true;
            try
            {
                connectedPeersList.BeginUpdate();
                connectedPeersList.Items.Clear();
                var idx = -1;
                foreach (var d in desired)
                {
                    var i = connectedPeersList.Items.Add(d.Item, isChecked: true);
                    if (selectedId == d.Id) idx = i;
                }
                if (idx >= 0) connectedPeersList.SelectedIndex = idx;
                connectedPeersList.EndUpdate();
            }
            finally { suppressConnectedCheck = false; }
        }

        UpdateConnectedListLiveStatus();
    }

    private void UpdateConnectedListLiveStatus()
    {
        var healthByAddress = new Dictionary<string, PeerHealth>();
        if (heartbeatService is not null)
        {
            foreach (var ph in heartbeatService.GetAllPeerHealth())
            {
                healthByAddress[ph.AudioEndpoint.Address.ToString()] = ph;
            }
        }
        var sendingNow = connected && IsSendEnabled && sender.IsRunning;
        var codecLabel = FormatCodecLabel(sender.Codec, sender.OpusFrameSamplesPerChannel);

        for (int i = 0; i < connectedPeersList.Items.Count; i++)
        {
            if (connectedPeersList.Items[i] is not PeerListItem item) continue;
            var s = item.Status;
            var prevText = item.ToString();

            var addrKey = item.Peer.Address.ToString();
            var ph = healthByAddress.GetValueOrDefault(addrKey);
            var isHealthy = ph is { State: PeerHealthState.Healthy };

            s.Connected = isHealthy;
            s.Sending = isHealthy && sendingNow;
            s.Receiving = isHealthy && receiver.IsRunning && receiver.IsReceivingFromAddress(item.Peer.Address);
            s.CodecLabel = isHealthy ? codecLabel : null;
            s.RttMs = isHealthy && ph is { RttMs: { } rtt }
                ? RoundToFive(rtt)
                : null;

            // Keep the friendly-name label fresh for the pan/EQ list, status line and recordings, and
            // track when this peer first went healthy for the "connected for" line.
            selectedPeerLabels[item.Peer.InstanceId] = ResolvePeerDisplayName(item.Peer);
            if (isHealthy) { if (!peerConnectedSinceUtc.ContainsKey(item.Peer.InstanceId)) peerConnectedSinceUtc[item.Peer.InstanceId] = DateTime.UtcNow; }
            else peerConnectedSinceUtc.Remove(item.Peer.InstanceId);

            // For a NAMED peer, record where and when we last saw it (for Manage named peers). Only
            // an address change forces a disk write; the timestamp is flushed on close.
            if (isHealthy && namedPeers.TryGetValue(PeerIdentityKey(item.Peer), out var np))
            {
                var addr = item.Peer.Address.ToString();
                if (np.LastAddress != addr) { np.LastAddress = addr; namedPeersDirty = true; }
                np.LastSeenUtc = DateTime.UtcNow;
            }

            if (item.ToString() != prevText)
            {
                connectedPeersList.RefreshItemPublic(i);
            }
        }
        if (namedPeersDirty) { namedPeersDirty = false; SaveNamedPeers(); }
        UpdatePeerDetails();
    }

    // === Peer identity, friendly names, and the details box ===

    /// <summary>The stable key a friendly name is stored under: the peer's machine name when it's a real
    /// name, otherwise its address (the manual-by-IP case, where Name equals the address string).</summary>
    private static string PeerIdentityKey(PeerAnnouncement peer)
    {
        var addr = peer.Address.ToString();
        return string.IsNullOrWhiteSpace(peer.Name) || peer.Name == addr ? addr : peer.Name;
    }

    /// <summary>The name to show for a peer: its custom friendly name if set, else its machine name
    /// (which for a manual-by-IP peer is the address). Used by every peer list via
    /// <see cref="PeerListItem.DisplayNameProvider"/>.</summary>
    private string ResolvePeerDisplayName(PeerAnnouncement peer)
    {
        var name = namedPeers.TryGetValue(PeerIdentityKey(peer), out var np) && !string.IsNullOrWhiteSpace(np.FriendlyName)
            ? np.FriendlyName
            : peer.Name;
        // SyncConnectedList decorates a discovery-lost "ghost" peer's Name with a transient " (offline)"
        // for the connected list. That decoration must NEVER be persisted as the peer's label — this
        // method feeds selectedPeerLabels (the status readout, pan/EQ list, recordings), which is
        // re-stored every status tick, so a baked-in " (offline)" compounds into
        // "NAME (offline)(offline)(offline)…". Strip it here, the single point where a display name is
        // resolved for storage, so the marker stays a once-only, live decoration.
        return StripOfflineMarker(name);
    }

    private const string OfflineMarker = " (offline)";
    private static string StripOfflineMarker(string name) =>
        name.Contains(OfflineMarker, StringComparison.Ordinal) ? name.Replace(OfflineMarker, "") : name;

    private PeerListItem? SelectedConnectedPeer() => SafeSelectedItem(connectedPeersList) as PeerListItem;

    /// <summary>Refreshes the read-only details box for whichever peer is highlighted in the connected
    /// list. Runs on selection change and each status tick (so "connected for" and ping stay live).</summary>
    private void UpdatePeerDetails()
    {
        var item = SelectedConnectedPeer();
        renamePeerButton.Enabled = item is not null;
        var text = item is null ? "Select a connected peer to see its details." : BuildPeerDetailsText(item);
        if (peerDetailsBox.Text != text) peerDetailsBox.Text = text;
    }

    private string BuildPeerDetailsText(PeerListItem item)
    {
        var peer = item.Peer;
        var lines = new List<string>();
        var display = ResolvePeerDisplayName(peer);
        var machine = string.IsNullOrWhiteSpace(peer.Name) || peer.Name == peer.Address.ToString() ? null : peer.Name;

        if (!string.Equals(display, machine, StringComparison.Ordinal) && !string.Equals(display, peer.Address.ToString(), StringComparison.Ordinal))
            lines.Add($"Name: {display}");
        lines.Add($"Machine name: {machine ?? "(unknown — added by address)"}");
        lines.Add($"Address: {peer.Address}");

        if (peerConnectedSinceUtc.TryGetValue(peer.InstanceId, out var since))
            lines.Add($"Connected for: {DescribeDuration(DateTime.UtcNow - since)}");

        if (item.Status.Connected)
            lines.Add(item.Status.RttMs is { } rtt ? $"Link: healthy, ping {rtt} ms" : "Link: healthy");
        else
            lines.Add("Link: not connected");

        lines.Add("Sending: " + DescribeSending(peer));
        lines.Add("Receiving your audio: " + (item.Status.Sending ? "yes" : "no"));

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>"2 devices on ASIO at 48 kHz, Opus" — built from the live receive streams (each stream is
    /// one capture device; its lane tells us WASAPI vs ASIO). Falls back gracefully when we're not
    /// receiving that peer.</summary>
    private string DescribeSending(PeerAnnouncement peer)
    {
        var formats = receiver.ActiveFormatsFromAddress(peer.Address);
        if (formats.Count == 0)
            return peer.CanSend ? "yes — turn on Receive audio to see the details" : "no";

        string deviceWord = formats.Count == 1 ? "1 device" : $"{formats.Count} devices";

        var apis = formats.Select(f => f.Lane == RenderRoute.AsioLane ? "ASIO" : "WASAPI").Distinct().OrderBy(a => a).ToList();
        string apiPart = apis.Count == 1 ? $" on {apis[0]}" : $" on {string.Join(" and ", apis)}";

        var rates = formats.Select(f => f.SampleRate).Distinct().ToList();
        string ratePart = rates.Count == 1 ? $" at {rates[0]} Hz" : "";

        var codecs = formats.Select(f => ((AudioTransportCodec)f.Codec) == AudioTransportCodec.Opus ? "Opus" : "PCM").Distinct().ToList();
        string codecPart = codecs.Count == 1 ? $", {codecs[0]}" : "";

        return $"{deviceWord}{apiPart}{ratePart}{codecPart}";
    }

    private static string DescribeDuration(TimeSpan d)
    {
        if (d.TotalSeconds < 60) return $"{(int)d.TotalSeconds} seconds";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} minute{((int)d.TotalMinutes == 1 ? "" : "s")}";
        int hours = (int)d.TotalHours;
        int mins = d.Minutes;
        return mins == 0 ? $"{hours} hour{(hours == 1 ? "" : "s")}" : $"{hours} hour{(hours == 1 ? "" : "s")} {mins} minute{(mins == 1 ? "" : "s")}";
    }

    private void OnRenamePeer()
    {
        var item = SelectedConnectedPeer();
        if (item is null) return;
        var peer = item.Peer;
        var key = PeerIdentityKey(peer);
        var machineForDisplay = string.IsNullOrWhiteSpace(peer.Name) || peer.Name == peer.Address.ToString()
            ? peer.Address.ToString()
            : peer.Name;
        namedPeers.TryGetValue(key, out var current);

        using var dlg = new RenamePeerDialog(machineForDisplay, current?.FriendlyName);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        ApplyFriendlyName(key, machineForDisplay, peer.Address.ToString(), dlg.FriendlyName);
    }

    /// <summary>Store (or clear) a peer's friendly name in the machine-wide book, persist it, and
    /// refresh every place a peer name shows. A blank/cleared name removes the entry entirely.</summary>
    private void ApplyFriendlyName(string identityKey, string machineName, string? address, string? name)
    {
        logFile.Event(string.IsNullOrWhiteSpace(name)
            ? $"named peer: cleared name for {identityKey}"
            : $"named peer: {identityKey} → \"{name.Trim()}\"");
        if (string.IsNullOrWhiteSpace(name))
        {
            namedPeers.Remove(identityKey);
        }
        else
        {
            if (!namedPeers.TryGetValue(identityKey, out var np)) { np = new NamedPeer(); namedPeers[identityKey] = np; }
            np.MachineName = string.IsNullOrWhiteSpace(machineName) ? identityKey : machineName;
            np.FriendlyName = name.Trim();
            if (!string.IsNullOrWhiteSpace(address)) { np.LastAddress = address; np.LastSeenUtc = DateTime.UtcNow; }
        }
        SaveNamedPeers();

        // Refresh everywhere: connected/discovered lists rebuild their labels, the pan/EQ list re-reads
        // its peer names, and the details box updates.
        lastPanEqPeerSignature = "";
        SyncAllPeerLists();
        RefreshPanEqPeerList();
        UpdatePeerDetails();
    }

    /// <summary>Writes the in-memory named-peers book to the machine-wide config, and clears the legacy
    /// flat map so it isn't written back.</summary>
    private void SaveNamedPeers()
    {
        try
        {
            var cfg = AppConfig.Load();
            cfg.NamedPeers = new Dictionary<string, NamedPeer>(namedPeers, StringComparer.OrdinalIgnoreCase);
            cfg.PeerFriendlyNames = new();
            cfg.Save();
        }
        catch (Exception ex) { logFile.Event($"named peers: failed to save: {ex.Message}"); }
    }

    /// <summary>Options → Manage named peers. Lists the peers the user has renamed, with their machine
    /// name and where/when last seen, and lets them rename (F2 / button) or delete (Del / button) any.</summary>
    private void ShowManageNamedPeersDialog()
    {
        using var dialog = new Form
        {
            Text = "Manage named peers",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            KeyPreview = true,
            ClientSize = new Size(600, 420),
        };

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 5 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(Theme.Heading("Named peers"), 0, 0);

        var intro = new Label
        {
            Text = "The peers you've given a name to. Rename or delete any of them. Deleting forgets the "
                 + "name only — the peer still connects as normal, under its machine name.",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            Margin = new Padding(0, 0, 0, 8),
        };
        root.Controls.Add(intro, 0, 1);

        // Plain label with '&' focuses the next control in tab order (the list).
        var peersLabel = new Label { Text = "&Peers", AutoSize = true, Anchor = AnchorStyles.Left };
        root.Controls.Add(peersLabel, 0, 2);

        var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, AccessibleName = "Peers (Alt+P)", TabIndex = 0 };
        root.Controls.Add(list, 0, 3);

        var renameBtn = new Button { Text = "&Rename (Alt+R)", AutoSize = true, AccessibleName = "Rename", TabIndex = 1 };
        var deleteBtn = new Button { Text = "&Delete (Alt+D)", AutoSize = true, AccessibleName = "Delete", TabIndex = 2 };
        var closeBtn = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.OK, TabIndex = 3 };
        var buttonRow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        buttonRow.Controls.Add(renameBtn);
        buttonRow.Controls.Add(deleteBtn);
        buttonRow.Controls.Add(closeBtn);
        root.Controls.Add(buttonRow, 0, 4);

        dialog.Controls.Add(root);
        dialog.AcceptButton = closeBtn;
        dialog.CancelButton = closeBtn;

        void Refresh()
        {
            var prev = list.SelectedIndex;
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var kv in namedPeers.OrderBy(k => k.Value.FriendlyName, StringComparer.CurrentCultureIgnoreCase))
                list.Items.Add(new NamedPeerItem(kv.Key, kv.Value));
            list.EndUpdate();
            if (list.Items.Count > 0) list.SelectedIndex = Math.Clamp(prev < 0 ? 0 : prev, 0, list.Items.Count - 1);
            renameBtn.Enabled = deleteBtn.Enabled = list.Items.Count > 0;
        }

        void RenameSelected()
        {
            if (list.SelectedItem is not NamedPeerItem it) return;
            using var rename = new RenamePeerDialog(it.Peer.MachineName, it.Peer.FriendlyName);
            if (rename.ShowDialog(dialog) != DialogResult.OK) return;
            ApplyFriendlyName(it.Key, it.Peer.MachineName, it.Peer.LastAddress, rename.FriendlyName);
            Refresh();
        }

        void DeleteSelected()
        {
            if (list.SelectedItem is not NamedPeerItem it) return;
            logFile.Event($"named peer: deleted \"{it.Peer.FriendlyName}\" ({it.Key})");
            namedPeers.Remove(it.Key);
            SaveNamedPeers();
            lastPanEqPeerSignature = "";
            SyncAllPeerLists();
            RefreshPanEqPeerList();
            UpdatePeerDetails();
            Refresh();
        }

        renameBtn.Click += (_, _) => RenameSelected();
        deleteBtn.Click += (_, _) => DeleteSelected();
        list.DoubleClick += (_, _) => RenameSelected();
        list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F2) { RenameSelected(); e.Handled = e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = e.SuppressKeyPress = true; }
        };
        dialog.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { dialog.DialogResult = DialogResult.OK; dialog.Close(); }
        };

        Refresh();
        dialog.Load += (_, _) => list.Focus();
        dialog.ShowDialog(this);
    }

    // One row in the Manage named peers list: "Andre's desktop — ANDRE-DESKTOP — last seen 8 Jul 2026, 100.72.4.13".
    private sealed class NamedPeerItem(string key, NamedPeer peer)
    {
        public string Key { get; } = key;
        public NamedPeer Peer { get; } = peer;
        public override string ToString()
        {
            string seen = Peer.LastSeenUtc == default
                ? "not seen yet"
                : $"last seen {Peer.LastSeenUtc.ToLocalTime():d MMM yyyy}"
                  + (string.IsNullOrWhiteSpace(Peer.LastAddress) ? "" : $", {Peer.LastAddress}");
            return $"{Peer.FriendlyName} — {Peer.MachineName} — {seen}";
        }
    }

    private void SyncDiscoveredList()
    {
        // Discovered = peers seen by discovery NOT currently connected, AND NOT manual peers
        // (manual peers were added by user typing an IP — they aren't really "discovered").
        var desired = new List<(PeerListItem Item, Guid Id)>();
        foreach (var peer in knownPeers.Values
            .Where(p => !selectedPeerEndpoints.ContainsKey(p.InstanceId))
            .Where(p => !manualPeers.ContainsKey(p.InstanceId))
            .OrderBy(p => p.Name).ThenBy(p => p.Address.ToString()))
        {
            desired.Add((new PeerListItem(peer), peer.InstanceId));
        }

        var signature = string.Join("|", desired.Select(d => d.Item.ToString()));
        if (signature == lastDiscoveredListSignature) return;
        lastDiscoveredListSignature = signature;

        var selectedId = SafeSelectedItem(discoveredPeersList) is PeerListItem si ? si.Peer.InstanceId : Guid.Empty;
        suppressDiscoveredCheck = true;
        try
        {
            discoveredPeersList.BeginUpdate();
            discoveredPeersList.Items.Clear();
            var idx = -1;
            foreach (var d in desired)
            {
                var i = discoveredPeersList.Items.Add(d.Item, isChecked: false);
                if (selectedId == d.Id) idx = i;
            }
            if (idx >= 0) discoveredPeersList.SelectedIndex = idx;
            discoveredPeersList.EndUpdate();
        }
        finally { suppressDiscoveredCheck = false; }
    }

    private void SyncRememberedList()
    {
        // Hide entries whose mapped peer is currently connected — they live in Connected
        // until disconnection, then reappear here.
        var hiddenEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (entry, id) in rememberedPeerInstanceIds)
        {
            if (selectedPeerEndpoints.ContainsKey(id)) hiddenEntries.Add(entry);
        }

        var entries = settings.LoadRememberedPeers()
            .Where(e => !hiddenEntries.Contains(e))
            .ToList();

        var signature = string.Join("|", entries);
        if (signature == lastRememberedListSignature) return;
        lastRememberedListSignature = signature;

        var selectedEntry = SafeSelectedItem(rememberedPeersList) is RememberedPeerItem si ? si.Entry : null;
        suppressRememberedCheck = true;
        try
        {
            rememberedPeersList.BeginUpdate();
            rememberedPeersList.Items.Clear();
            var idx = -1;
            foreach (var entry in entries)
            {
                var item = new RememberedPeerItem(entry);
                var i = rememberedPeersList.Items.Add(item, isChecked: false);
                if (entry == selectedEntry) idx = i;
            }
            if (idx >= 0) rememberedPeersList.SelectedIndex = idx;
            rememberedPeersList.EndUpdate();
        }
        finally { suppressRememberedCheck = false; }
    }

    /// <summary>Profiles &amp; preferences tab — list of saved profiles with inline Switch /
    /// Rename / Delete buttons + Save / Save-as + the mute-cues checkbox + remote-volume
    /// opt-in + Keyboard-shortcuts + Minimise-to-tray. Phase 4 of the refactor; the old
    /// "Manage profiles" dialog (and the ProfileManagementDialog.cs file) is gone.</summary>
    // BuildProfilesPrefsTab and its companion UI methods (UpdateCurrentProfileLabel,
    // RefreshProfilesList, SwitchSelectedProfile, RenameSelectedProfile,
    // DeleteSelectedProfile) were deleted on 2026-05-08 when the fourth tab was retired.
    // The same actions now live on the File menu:
    //   * Switch profile        →  File → Open profile          (OpenProfileFromPicker)
    //   * Rename profile        →  File → Rename current profile (RenameCurrentProfile)
    //   * Save / Save as        →  File → Save / Save as
    //   * Delete profile        →  removed from app UI; users can delete via the OS file
    //                              picker's right-click menu (File → Open profile shows the
    //                              folder; right-click any entry → Delete).
    //   * Mute cues / Accept remote / Startup behaviour → File → Preferences (Ctrl+P).
    //   * Keyboard shortcuts    →  File → Keyboard shortcuts (Ctrl+K).
    //   * Minimise to tray      →  File → Minimise to tray (Alt+M, gated per-tab so the
    //                              Audio I/O tab's Audio mode mnemonic wins on that tab).

    // FocusFirstControlOnActiveTab was removed in the arrow-key fix. The original intent —
    // landing on something useful after a tab change — turned out to defeat the standard
    // tab-strip navigation: every SelectedIndexChanged would yank focus off the strip,
    // breaking arrow-key cycling and NVDA's tab-announcement chain. WinForms' built-in
    // behaviour (focus stays on strip until user presses Tab) is what we want.

    // FocusFirstChildOnActiveTab removed — caused unwanted "jumping into the box" on tab
    // change. Andre's app doesn't do this; we shouldn't either. Arrow keys cycle tabs with
    // focus staying on the strip; user presses Tab once to enter the active page.

    private void SetTabOrder()
    {
        // Andre's accessible app sets no TabIndex on the TabControl itself — defaults work.
        // Tab order is set per-tab now (each TabPage has its own focus traversal). Keeping
        // the existing relative order from the pre-tab single-form layout so the user's
        // muscle memory is preserved.
        //
        // Connectivity tab order lives in BuildConnectivityTab now — it has to set the list WRAPPERS'
        // TabIndex (the lists are nested in FlowLayoutPanels), which this central method can't reach
        // cleanly. Don't set it here or it fights the correct settings there.
        // Audio I/O tab. The driver picker is row 0 when present (a real driver chosen here
        // is what enables ASIO). Audio-mode listbox retired 2026-05-11.
        asioDriverBox.TabIndex = 0;
        receiveAudioCheckbox.TabIndex = 1;
        receiveOutputDevicesList.TabIndex = 2;
        asioReceiveOutputDevicesList.TabIndex = 3;
        volumeBar.TabIndex = 4;
        sendMyAudioCheckbox.TabIndex = 5;
        sendModeList.TabIndex = 6;               // how-to-send chooser, right after "Send my audio"
        sendOutputDevicesList.TabIndex = 7;      // devices mode
        sendAppsList.TabIndex = 8;                 // applications mode — currently active
        rememberedAppsList.TabIndex = 9;           // applications mode — remembered
        sendInputDevicesList.TabIndex = 10;
        asioSendDevicesList.TabIndex = 11;
        // Profiles & preferences tab retired 2026-05-08 — the controls that used to live
        // there have moved to the File menu (Open/Save/Save as/Rename/etc.) and the
        // Preferences dialog (Mute cues / Accept remote vol / Startup behaviour).
    }

    /// <summary>True if the given control is on the currently-selected tab. Used by
    /// <see cref="ProcessCmdKey"/> to gate Alt+letter shortcuts so they only fire when the
    /// target is on the visible tab — pressing Alt+L on the Connectivity tab does NOT auto-
    /// switch to the Audio profile tab and focus the latency spinner. The user has to first
    /// Ctrl+Tab to the right tab. This is the explicit per-tab shortcut isolation rule.</summary>
    private bool IsControlOnActiveTab(Control? c)
    {
        if (c is null) return false;
        var active = mainTabControl.SelectedTab;
        if (active is null) return false;
        for (var p = c.Parent; p is not null; p = p.Parent)
        {
            if (ReferenceEquals(p, active)) return true;
        }
        return false;
    }

    private bool IsSendEnabled => sendMyAudioCheckbox.Checked;
    private bool IsReceiveEnabled => receiveAudioCheckbox.Checked;

    // ===================== Connectivity / lifecycle =====================

    private void Connect()
    {
        if (connected) return;
        connected = true;
        connectedSinceUtc = DateTime.UtcNow;
        try
        {
            discovery.Start(LocalAudioPort, IsSendEnabled, IsReceiveEnabled);
            logFile.Event("discovery started");
        }
        catch (Exception ex)
        {
            AppendLogEntry($"discovery failed: {ex.Message}");
            logFile.Event($"discovery failed: {ex.Message}");
        }

        // Heartbeat starts as soon as we connect, regardless of send/receive state. That way
        // RTT and reachability are measured even when the user has both audio toggles off,
        // and the moment they tick a peer the heartbeat picks them up.
        //
        // Single-port mode (2026-05-06): the heartbeat service no longer binds a UDP socket.
        // Outbound pings/pongs route through the audio sender's socket (sender.SendVia, sharing
        // the audio NAT pinhole on the audio port). Inbound heartbeats arrive on either of two
        // App-owned sockets and are forwarded into HandleInjectedPacket:
        //   * The audio receiver's listener (LAN — peers send heartbeat to our audio port).
        //   * The audio sender's recv-side via OnInboundPacket (relay-return path).
        // Because the receiver's listener is bound for the duration of the connection (split
        // from the playback gate, see AudioReceiver.SetPlaybackEnabled), heartbeat works even
        // when "Receive audio" is off — no separate +2 port needed any more.
        try
        {
            heartbeatService = new HeartbeatService(msg => logFile.Event($"heartbeat: {msg}"));
            heartbeatService.SendTransport = sender.SendVia;
            receiver.OnHeartbeatReceived = (buffer, length, remote) =>
                heartbeatService.HandleInjectedPacket(buffer, length, remote);
            // Remote-control handler (volume up/down, mute toggle from a connected peer).
            // Hooks into the same single-port receive path: the audio receiver's listener
            // sees the Control packet, parses it, and fires this delegate. We marshal back
            // onto the UI thread to mutate volumeBar / mute state.
            receiver.OnRemoteControlReceived = HandleRemoteControlPacket;
            // Relay address-proof (2026-07-27): echo the relay's cookie back verbatim so it can
            // verify this address really receives — the proof that keeps us forwardable once the
            // relay enforces. Echo-to-source is self-limiting (one small reply per challenge,
            // never larger than what arrived), so answering unconditionally is safe.
            receiver.OnAddrCheckReceived = (packet, length, remote) =>
            {
                try { sender.SendVia(packet, length, remote); }
                catch (Exception ex) { logFile.Event($"addr-check echo to {remote} failed: {ex.GetType().Name}: {ex.Message}"); }
            };
            heartbeatService.Start();
        }
        catch (Exception ex)
        {
            AppendLogEntry($"heartbeat failed to start: {ex.Message}");
            logFile.Event($"heartbeat failed to start: {ex.Message}");
        }

        // Single-port mode: bind the audio receiver's listener socket immediately on connect,
        // independent of the user's "Receive audio" tick. The listener carries heartbeat
        // packets even when audio playback is off; ApplyAudioRuntime below toggles playback
        // separately via SetPlaybackEnabled. Without this, heartbeats sent to our audio port
        // would hit a closed socket and the peer would see us as unreachable until the user
        // ticked Receive.
        try
        {
            receiver.Start(LocalAudioPort);
            logFile.Event($"receiver listener started port={LocalAudioPort}");
        }
        catch (Exception ex)
        {
            AppendLogEntry($"receiver listener failed to start: {ex.Message}");
            logFile.Event($"receiver listener failed to start: {ex.Message}");
        }

        RefreshKnownPeers();
        ApplyAudioRuntime();
        UpdateStatus();
    }

    // --- Priority-mode scoping (2026-07-26 resource audit) -------------------------------------
    // The opt-in Priority mode's levers (keep-awake, High priority, EcoQoS opt-out, fine timer,
    // working-set lock) used to engage at profile load and hold for the WHOLE app lifetime — a
    // machine with RemSound idle in the tray was kept awake and off deep power states for
    // nothing. They now engage only while audio is actually moving (send armed to at least one
    // peer, or received audio hitting a live session) and release after a quiet hold-down, so
    // brief silences and re-arms never flap the levers. The service already scoped this way; the
    // app now matches. The audio loops' own fine-timer scopes (SystemTimerResolution — the
    // overnight-lag fix) are independent of Priority mode and deliberately untouched.
    internal static readonly TimeSpan PriorityModeHoldDown = TimeSpan.FromSeconds(30);
    private DateTime lastStreamActivityUtc = DateTime.MinValue;
    private bool priorityModeEngaged;

    /// <summary>Pure decision core, pinned by the self-test: engage while the toggle is on AND
    /// stream activity happened within the hold-down; everything else releases.</summary>
    internal static bool PriorityModeShouldEngage(bool priorityModeOn, DateTime lastActivityUtc, DateTime nowUtc) =>
        priorityModeOn && nowUtc - lastActivityUtc < PriorityModeHoldDown;

    private void EvaluatePriorityModeScope()
    {
        var sendActive = connected && sender.IsRunning && !string.IsNullOrEmpty(activeAudioReceiverSignature);
        var receiveActive = connected && receiver.AnyRecentAudio(TimeSpan.FromSeconds(3));
        if (sendActive || receiveActive) lastStreamActivityUtc = DateTime.UtcNow;

        var want = PriorityModeShouldEngage(settings.LoadPriorityMode(), lastStreamActivityUtc, DateTime.UtcNow);
        if (want == priorityModeEngaged) return;
        priorityModeEngaged = want;
        logFile.Event(want
            ? "priority mode engaging (streaming active)"
            : "priority mode releasing (no stream activity for the hold-down)");
        PerformanceMode.Apply(want, msg => logFile.Event(msg));
    }

    private void HandleCapabilityChange()
    {
        if (!connected) return;
        discovery.UpdateCapabilities(LocalAudioPort, IsSendEnabled, IsReceiveEnabled);
        ApplyAudioRuntime();
    }

    private void EnsureRequestedAudioRunning()
    {
        if (!connected) return;

        var wantSend = IsSendEnabled;
        var wantReceive = IsReceiveEnabled;
        if ((wantSend && !sender.IsRunning) || (wantReceive && !receiver.IsRunning))
        {
            ApplyAudioRuntime();
        }
    }

    private void ApplyAudioRuntime()
    {
        if (!connected) return;

        var endpoints = SelectedSendEndpoints().ToArray();
        allSendEndpoints = endpoints;
        // Single-port heartbeat: tracked peers' audio endpoints ARE the heartbeat target.
        // HeartbeatService sends via sender.SendVia (wired in Connect) so heartbeat shares
        // the audio NAT pinhole on the audio port — no separate socket, no +2 port. The
        // heartbeat tracks the FULL set so a recovered endpoint is detected and re-armed.
        heartbeatService?.SetTrackedPeers(endpoints);
        // Arm the audio sender with the full set initially (nothing is known-dead yet). The
        // 1 Hz tick (RefreshAudioReceivers) then drops any endpoint that stays unreachable,
        // so we don't blast the stream at a dead address. Clear the cached signature so the
        // refresh re-pushes against the new peer set.
        activeAudioReceiverSignature = null;
        RefreshAudioReceivers();

        // Push the current profile-password key + fingerprint down to the sender and receiver so
        // audio is encrypted/decrypted with it. Cheap when the password hasn't changed.
        RecomputeAudioCrypto();

        // Sender does NOT depend on a peer being currently online. As long as the user has ticked
        // "Send my audio" AND a capture device, we keep capturing and emitting UDP. If no peer is
        // selected, packets just go nowhere; the moment a peer is ticked, packets start flowing.
        // Either machine can start first; either machine can disappear and reappear; nothing
        // teardowns. UDP doesn't care.
        //
        // No fallback to the system default capture device — if the user hasn't ticked anything,
        // we send nothing. Avoids the "wrong source captured silently" failure mode.
        var wantReceive = IsReceiveEnabled;
        // Note: wantSend is driven by IsSendEnabled alone, NOT by HasCheckedSendDevice. If the
        // user has the "send my audio" toggle on but has unticked all devices for a moment
        // (typical mid-edit state), we keep the sender RUNNING with empty specs rather than
        // tearing it down and rebuilding. The reason: tearing the engine down closes the ASIO
        // driver, and Audient's driver (plus a couple of others) hangs for ~5 seconds when
        // closed and reopened in quick succession, which freezes RemSound and previously took
        // the laptop process down with it. Empty specs are handled gracefully — MixingEngine
        // keeps its mix task running over zero sources (produces silence), AsioCaptureBackend
        // keeps the driver open with zero active channel pairs (callbacks fire harmlessly).
        // The sender only actually stops when the user toggles off "send my audio" itself.
        var wantSend = IsSendEnabled;

        try
        {
            // Single-port model: the receiver's listener socket is bound at Connect time and
            // stays bound for the connection's lifetime (so heartbeats keep flowing regardless
            // of the playback toggle). The "Receive audio" checkbox now only gates playback.
            // Push the device list and allow-list before enabling playback, so the very first
            // packets after enable have correct routing.
            if (wantReceive && !receiver.IsRunning)
            {
                ApplyReceiveDevices();
                PushAllowedReceiveSenders();
                receiver.SetPlaybackEnabled(true);
                logFile.Event("receiver playback enabled");
            }
            else if (!wantReceive && receiver.IsRunning)
            {
                receiver.SetPlaybackEnabled(false);
                logFile.Event("receiver playback disabled");
            }

            if (wantSend && !sender.IsRunning)
            {
                ApplySendSources();
                sender.Start();
                logFile.Event($"sender started codec={sender.Codec} sources=[{sender.CaptureDeviceName}] peers=[{string.Join(",", endpoints.Select(e => e.ToString()))}]");
            }
            else if (wantSend && sender.IsRunning)
            {
                // Already running — user may have ticked/unticked devices in either list. Push
                // the new spec list down; sender restarts the mixer transparently if the set changed.
                ApplySendSources();
            }
            else if (!wantSend && sender.IsRunning)
            {
                sender.Stop();
                logFile.Event("sender stopped");
            }
        }
        catch (Exception ex)
        {
            AppendLogEntry($"audio runtime error: {ex.Message}");
            logFile.Event($"audio runtime error: {ex.Message}");
        }
    }

    /// <summary>
    /// Re-arm the audio sender's destination list from the full selected-peer set
    /// (<see cref="allSendEndpoints"/>), dropping any endpoint the heartbeat reports as
    /// continuously unreachable for longer than <see cref="AudioPruneUnreachableAfter"/>. The
    /// heartbeat keeps pinging dropped endpoints (they stay in SetTrackedPeers), so a recovered
    /// endpoint is automatically re-added on a later tick. This stops RemSound blasting the full
    /// audio stream at a dead address — e.g. a peer hostname resolving to both a live LAN IP and
    /// a long-dead Tailscale IP, where half the upload went into a black hole. Called once per
    /// second from <see cref="SnapshotLogIfDue"/>; only touches the sender when the armed set
    /// actually changes, so it's cheap to run per tick.
    /// </summary>
    private void RefreshAudioReceivers()
    {
        if (!connected) return;
        var all = allSendEndpoints;

        // Decide which selected peers to actually TRANSMIT audio to. We arm only peers that are
        // genuinely reachable — anyone whose heartbeat has been Unreachable for longer than
        // AudioPruneUnreachableAfter is dropped from the send set, so RemSound never streams audio
        // into a dead address. The heartbeat keeps pinging EVERY selected peer regardless (it runs
        // off SetTrackedPeers, not this set), so the instant a peer comes back it is re-armed.
        //
        // Carve-out: a peer we are actively RECEIVING audio from stays armed even if its heartbeat
        // reads Unreachable — that covers an asymmetric path where audio flows but the heartbeat
        // round-trip doesn't, so a working stream is never cut.
        // The prune itself is the shared Core rule (PeerArming) — same code the service arms with. The
        // app's receiving-carve-out rides in as the keepAnyway predicate.
        var armed = all.Length > 0 && heartbeatService is { } hb
            ? PeerArming.ComputeArmedEndpoints(all, hb.GetAllPeerHealth(), AudioPruneUnreachableAfter,
                keepAnyway: addr => receiver.IsAudioFlowingFrom(addr, TimeSpan.FromSeconds(3)))
            : all;

        // There used to be a "never silence EVERY peer" safety net here that re-armed the whole
        // set when pruning would leave nobody. Removed 2026-06-12: when NO peer is reachable we
        // must send NOTHING, not blast audio at every dead address. Otherwise someone connected to
        // a single peer that goes offline keeps uploading the full stream into the void — exactly
        // a-singer's issue #8 ("Not connected to any peer ... sending 51.1 kB/s ... sent 2116.5 MB"
        // after the only receiver was switched off hours earlier). The heartbeat still probes all
        // peers, so the moment one answers again it is re-armed and audio resumes on its own.

        var signature = PeerArming.Signature(armed);
        if (signature == activeAudioReceiverSignature) return;
        activeAudioReceiverSignature = signature;
        sender.SetReceivers(armed);
        var pruned = all.Length - armed.Length;
        if (armed.Length == 0)
        {
            logFile.Event($"audio receivers updated: 0 active — no reachable peer, not sending (heartbeat still probing {all.Length})");
        }
        else if (pruned > 0)
        {
            logFile.Event($"audio receivers updated: {armed.Length} active, {pruned} pruned (unreachable >{AudioPruneUnreachableAfter.TotalSeconds:0}s); heartbeat still probing all");
        }
        else
        {
            logFile.Event($"audio receivers updated: {armed.Length} active");
        }
    }

    /// <summary>True when the WASAPI send side is in "specific applications" mode (and the OS supports
    /// it). In that mode the loopback-outputs list is ignored in favour of the app selection.</summary>
    private bool AppsModeActive() =>
        ProcessLoopbackCapture.IsSupported && sendModeList.SelectedIndex == SendModeApplicationsIndex;

    /// <summary>True when applications mode will actually send something: at least one app is ticked.</summary>
    private bool HasAppModeSend() =>
        AppsModeActive() && selectedSendApps.Count > 0;

    private bool HasCheckedSendDevice() =>
        (!AppsModeActive() && sendOutputDevicesList.CheckedItems.OfType<AudioDeviceChoice>().Any(c => c.DeviceId is not null))
        || sendInputDevicesList.CheckedItems.OfType<AudioDeviceChoice>().Any(c => c.DeviceId is not null)
        || asioSendDevicesList.CheckedItems.OfType<AudioDeviceChoice>().Any(c => c.DeviceId is not null)
        || HasAppModeSend();

    private void ApplySendSources()
    {
        // Build the unified spec list from all three send-side lists. The CompositeCaptureBackend
        // splits this set internally into WASAPI specs (sent to MixingEngine) and ASIO specs
        // (sent to AsioCaptureBackend). Both run in parallel and their outputs are summed.
        // The WASAPI outputs-or-apps portion is the SHARED builder (CaptureSpecBuilder) — the same code
        // the service assembles its specs with, so the two can no longer drift apart.
        var appsMode = ProcessLoopbackCapture.IsSupported && sendModeList.SelectedIndex == SendModeApplicationsIndex;
        string? followedDefaultLoopback = null;
        var specs = appsMode
            ? CaptureSpecBuilder.BuildApplicationSpecs(CheckedSendApplicationNames())
            : CaptureSpecBuilder.BuildOutputSpecs(
                sendOutputDevicesList.CheckedItems.OfType<AudioDeviceChoice>()
                    .Where(c => c.DeviceId is not null)
                    .Select(c => (c.DeviceId!, c.Name)),
                out followedDefaultLoopback);
        lastFollowedDefaultLoopbackId = followedDefaultLoopback;
        var addedInputIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? followedDefaultInput = null;
        foreach (var item in sendInputDevicesList.CheckedItems.OfType<AudioDeviceChoice>())
        {
            if (item.IsDefaultFollower)
            {
                // Resolve the current Windows default capture device live.
                followedDefaultInput = ResolveDefaultDeviceId(NAudio.CoreAudioApi.DataFlow.Capture);
                if (!string.IsNullOrEmpty(followedDefaultInput) && addedInputIds.Add(followedDefaultInput))
                {
                    specs.Add(new CaptureSourceSpec(followedDefaultInput, CaptureKind.Input, "Windows default audio device"));
                }
            }
            else if (item.DeviceId is { } id && addedInputIds.Add(id))
            {
                specs.Add(new CaptureSourceSpec(id, CaptureKind.Input, item.Name));
            }
        }
        lastFollowedDefaultInputId = followedDefaultInput;
        foreach (var item in asioSendDevicesList.CheckedItems.OfType<AudioDeviceChoice>())
        {
            // ASIO channels have no Loopback/Input distinction — Kind is irrelevant for ASIO
            // (AsioDeviceId.TryParse routes by id format, not by Kind). Use Input for symmetry.
            if (item.DeviceId is { } id) specs.Add(new CaptureSourceSpec(id, CaptureKind.Input, item.Name));
        }
        sender.Configure(specs);
        // Tell the auto-tune to ignore the next tick AND throw away the rolling window — newly-
        // added captures take a moment to fill their first ring buffer, and that initial-fill
        // jitter shouldn't bias the recommendation. The window-clear is the load-bearing piece;
        // without it a single big-gap entry keeps the recommendation pinned for ~30 s.
        InvalidateAutoTuneHistory();
    }

    // ===================== Devices =====================

    private void LoadAudioDevices()
    {
        try
        {
            var outputs = AudioDeviceCatalog.LoadOutputs();
            var inputs = AudioDeviceCatalog.LoadInputs();

            // All three lists start UNCHECKED every session. No persisted selection — by design.
            // The user re-ticks once per session, avoiding the "wrong-device-still-selected"
            // failure mode after a card unplug or ID change.
            sendOutputDevicesSignature = SyncDeviceCheckedListBox(sendOutputDevicesList, WithDefaultFollower(outputs, DefaultLoopbackSendFollower));
            sendInputDevicesSignature = SyncDeviceCheckedListBox(sendInputDevicesList, WithDefaultFollower(inputs, DefaultInputFollower));
            receiveOutputDevicesSignature = SyncDeviceCheckedListBox(receiveOutputDevicesList, WithDefaultFollower(outputs, DefaultOutputFollower));
            // Re-tick the "Use Windows default" followers from the saved preference. They don't ride the
            // per-session "all unticked" rule above — a follower can never go stale (it always resolves
            // to the current default), so persisting it is the whole point of the feature.
            RestoreDefaultFollowerChecks();

            // Ground-truth log so we can definitively see the device list and initial check state
            // each launch — diagnoses any "device was checked at startup" mystery.
            var outputList = string.Join(", ", outputs.Select(d => $"\"{d.Name}\""));
            var inputList = string.Join(", ", inputs.Select(d => $"\"{d.Name}\""));
            logFile.Event($"device load: {outputs.Count} active render devices [{outputList}]; {inputs.Count} active capture devices [{inputList}]; all lists initial check state: unchecked");
        }
        catch (Exception ex)
        {
            AppendLogEntry($"could not enumerate devices: {ex.Message}");
        }
    }

    /// <summary>
    /// Called (on a COM thread) by <see cref="deviceChangeNotifier"/> whenever Windows reports an
    /// audio endpoint change. Marshals to the UI thread and (re)starts the debounce timer, so a
    /// burst of add / remove / default-changed callbacks collapses into a single refresh.
    /// </summary>
    private void OnAudioEndpointsChanged()
    {
        if (IsDisposed) return;
        logFile.Event("device-event: Windows reported an audio endpoint change (refresh queued)");
        try
        {
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed) return;
                deviceRefreshTimer.Stop();
                deviceRefreshTimer.Start();
                // The default render device may have changed — re-point the session-start watcher at it so
                // it keeps hearing new app sessions on whatever device apps now play to.
                sessionStartWatcher?.Rehook();
            }));
        }
        catch { /* handle gone / form closing — nothing to refresh */ }
    }

    /// <summary>
    /// Re-enumerates active audio endpoints and rebuilds any list whose set of devices changed.
    /// As of v3.4 this is driven by <see cref="deviceChangeNotifier"/> (Windows endpoint-change
    /// notifications), debounced through <see cref="deviceRefreshTimer"/>, rather than polled — so
    /// it runs only when the device set actually changes. Each list is rebuilt only when its
    /// (id, name) signature changes — the no-op fast path leaves NVDA's focus and the listbox state
    /// untouched. Check state is preserved by DeviceId across rebuilds; if a checked device
    /// disappeared, the relevant runtime <c>Apply*</c> is called so the engine sees the change.
    /// </summary>
    private void RefreshAudioDeviceLists()
    {
        // WASAPI lists are always populated from the Windows audio device catalogue — they're
        // visible regardless of ASIO state. ASIO lists are populated from the chosen driver's
        // channel-pair info, but only if ASIO is enabled with a valid driver; otherwise empty.
        IReadOnlyList<AudioDeviceChoice> wasapiOutputs;
        IReadOnlyList<AudioDeviceChoice> wasapiInputs;
        IReadOnlyList<AudioDeviceChoice> asioInputChoices = [];
        IReadOnlyList<AudioDeviceChoice> asioOutputChoices = [];
        // True if ASIO mode is on AND a driver is configured AND probing it just failed this
        // tick. Used to skip the asio list sync below — without this guard, a transient probe
        // failure (most commonly during hibernate entry or resume, when the USB stack is being
        // torn down or rebuilt) silently clears the user's ASIO tick, and on the next refresh
        // when the probe succeeds the list re-populates EMPTY of checks because the tick state
        // was lost in the previous clear. Net symptom: receiver-side audio falls silent after
        // resume even though all "audio backend re-initialised" log lines look fine.
        // 2026-05-22 — traced to a real overnight repro: SNAP at 23:37:33 had ReceiveDevice
        // = "ASIO 1/2"; SNAP at 23:37:34 (one second later, mid-hibernate-entry) had "(none)";
        // resume at 06:32:06 then opened the audio backend but the asio receive list was empty
        // so AsioRenderBackend.SetOutputDevices got an empty pairs list and silently returned
        // without opening the AsioOut — the AsioLane sessions queued packets into a ring with
        // no consumer (bufMs grew to 970+ ms, TrimDropBytes climbed into the millions).
        var asioProbeAttemptedAndFailed = false;
        try
        {
            wasapiOutputs = AudioDeviceCatalog.LoadOutputs();
            wasapiInputs = AudioDeviceCatalog.LoadInputs();

            var currentMode = settings.LoadAudioMode();
            if (ModeUsesAsio(currentMode) && settings.LoadAsioDriverName() is { } asioDriver && !string.IsNullOrWhiteSpace(asioDriver) && !disabledAsioDrivers.Contains(asioDriver))
            {
                var info = GetCachedAsioProbeInfo(asioDriver, out var probeFailed);
                if (info is not null)
                {
                    LogAsioChannelNamesIfChanged(asioDriver, info);
                    asioInputChoices = BuildAsioChannelPairChoices(asioDriver, info.InputChannelNames);
                    asioOutputChoices = BuildAsioChannelPairChoices(asioDriver, info.OutputChannelNames);
                }
                else if (probeFailed)
                {
                    // Probe came back -1/-1 — driver is configured but can't enumerate right
                    // now. Treat as transient; preserve current list state and try again on
                    // the next tick. The legitimate "driver is genuinely gone" cases (user
                    // selected "(none)", or settings.LoadAsioDriverName() returned null/empty)
                    // take the outer-if's else branch and correctly produce an empty list
                    // that DOES sync (clearing the UI), so removing a driver from the system
                    // still wipes the ticks as expected.
                    asioProbeAttemptedAndFailed = true;
                }
            }
        }
        catch (Exception ex)
        {
            logFile.Event($"device refresh failed: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        var sendOutputChanged = MaybeSyncList(sendOutputDevicesList, WithDefaultFollower(wasapiOutputs, DefaultLoopbackSendFollower), ref sendOutputDevicesSignature);
        var sendInputChanged = MaybeSyncList(sendInputDevicesList, WithDefaultFollower(wasapiInputs, DefaultInputFollower), ref sendInputDevicesSignature);
        var receiveOutputChanged = MaybeSyncList(receiveOutputDevicesList, WithDefaultFollower(wasapiOutputs, DefaultOutputFollower), ref receiveOutputDevicesSignature);
        bool asioSendChanged;
        bool asioReceiveChanged;
        if (asioProbeAttemptedAndFailed)
        {
            // Skip both asio list syncs. Crucially do NOT update the signature fields — leaving
            // them unchanged means the NEXT successful probe will still see "signature differs"
            // and re-sync the lists with the freshly-probed channel pairs, restoring tick state
            // by DeviceId from whatever was preserved in the UI.
            asioSendChanged = false;
            asioReceiveChanged = false;
        }
        else
        {
            asioSendChanged = MaybeSyncList(asioSendDevicesList, asioInputChoices, ref asioSendDevicesSignature);
            asioReceiveChanged = MaybeSyncList(asioReceiveOutputDevicesList, asioOutputChoices, ref asioReceiveOutputDevicesSignature);
        }

        if (sendOutputChanged || sendInputChanged || asioSendChanged)
        {
            ApplyAudioRuntime();
        }
        if (receiveOutputChanged) ReapplyRememberedReceiveOutputs();
        if (receiveOutputChanged || asioReceiveChanged)
        {
            ApplyReceiveDevices();
        }
        // A default-device change leaves the device SET unchanged, so the syncs above don't fire — but
        // if we're following the Windows default, the target device just moved. Catch that and re-route.
        ReapplyIfFollowedDefaultChanged();
    }

    private void ClearAsioProbeCache()
    {
        cachedAsioProbeDriverName = null;
        cachedAsioProbeResult = null;
        cachedAsioProbeFailed = false;
    }

    private AsioDriverProbeResult? GetCachedAsioProbeInfo(string driverName, out bool probeFailed)
    {
        probeFailed = false;
        if (string.Equals(cachedAsioProbeDriverName, driverName, StringComparison.OrdinalIgnoreCase))
        {
            if (cachedAsioProbeResult is not null) return cachedAsioProbeResult;
            if (cachedAsioProbeFailed)
            {
                probeFailed = true;
                return null;
            }
        }

        // Opening some ASIO drivers is not a passive metadata read. On Andre's Realtek
        // driver (rthdasio64.dll), every AsioOut construction leaks Event+Mutant handles.
        // The 3-second device refresh timer only needs stable channel metadata, so probe
        // once per selected driver and reuse the result until the driver changes or resume
        // forces a backend refresh.
        var info = AsioDeviceProbe.ProbeDriverInfo(driverName);
        cachedAsioProbeDriverName = driverName;
        if (info.InputChannelCount >= 0 && info.OutputChannelCount >= 0)
        {
            cachedAsioProbeResult = info;
            cachedAsioProbeFailed = false;
            return info;
        }

        cachedAsioProbeResult = null;
        cachedAsioProbeFailed = true;
        probeFailed = true;
        return null;
    }

    /// <summary>Reads Windows' microphone privacy settings and returns true when desktop apps — or
    /// THIS app specifically — are blocked from the mic. When blocked, WASAPI capture still opens but
    /// returns pure silence; ASIO bypasses this gate entirely, which is why the same mic can be live
    /// in ASIO yet dead here. Checks, in order: the per-user and machine top-level gate and the
    /// NonPackaged (all desktop apps) gate; a per-app Deny aimed at this exe under
    /// <c>NonPackaged\&lt;exe&gt;</c> (the case the old single-value check missed, where one app is
    /// denied while the top-level value still says Allow); and a Group-Policy / MDM force-deny
    /// (<c>LetAppsAccessMicrophone=2</c>, which the Settings UI doesn't even show). Best-effort — any
    /// failure returns false so a registry hiccup never stops the user enabling their mic.</summary>
    private static bool IsMicrophoneBlockedByWindowsPrivacy()
    {
        try
        {
            const string consent = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
            const string nonPackaged = consent + @"\NonPackaged";
            return IsConsentDenied(Microsoft.Win32.Registry.CurrentUser, consent)
                || IsConsentDenied(Microsoft.Win32.Registry.CurrentUser, nonPackaged)
                || IsConsentDenied(Microsoft.Win32.Registry.LocalMachine, consent)
                || IsConsentDenied(Microsoft.Win32.Registry.LocalMachine, nonPackaged)
                || IsThisExeDeniedUnderNonPackaged(Microsoft.Win32.Registry.CurrentUser, nonPackaged)
                || IsThisExeDeniedUnderNonPackaged(Microsoft.Win32.Registry.LocalMachine, nonPackaged)
                || IsMicPolicyForceDenied();
        }
        catch
        {
            return false;
        }
    }

    private static bool IsConsentDenied(Microsoft.Win32.RegistryKey root, string subKey)
    {
        using var key = root.OpenSubKey(subKey);
        // The ConsentStore "Value" is a REG_SZ "Allow"/"Deny". `as string` yields null for any other
        // type or a missing value, so anything that isn't an explicit "Deny" reads as allowed.
        return string.Equals(key?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True if a per-app override under NonPackaged denies THIS exe specifically (its child
    /// key is named by the exe's full path with '\' replaced by '#'). We match by exe filename rather
    /// than reconstructing the exact encoding, so a deny on a different app never false-warns us.</summary>
    private static bool IsThisExeDeniedUnderNonPackaged(Microsoft.Win32.RegistryKey root, string nonPackagedKey)
    {
        var exeName = System.IO.Path.GetFileName(Environment.ProcessPath ?? "");
        if (string.IsNullOrEmpty(exeName)) return false;
        using var key = root.OpenSubKey(nonPackagedKey);
        if (key is null) return false;
        foreach (var childName in key.GetSubKeyNames())
        {
            if (childName.IndexOf(exeName, StringComparison.OrdinalIgnoreCase) < 0) continue;
            using var child = key.OpenSubKey(childName);
            if (string.Equals(child?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>True if a Group Policy / MDM rule force-denies microphone access to apps
    /// (<c>HKLM\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy\LetAppsAccessMicrophone == 2</c>). This
    /// sits below the Settings toggles, so the user can't see or undo it without policy access — and
    /// the old check never looked here at all.</summary>
    private static bool IsMicPolicyForceDenied()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy");
        return key?.GetValue("LetAppsAccessMicrophone") is int v && v == 2;
    }

    /// <summary>One-shot, OK-only message telling the user Windows is blocking desktop-app mic
    /// access (so their mic would send silence) and exactly which two toggles to turn on. Shown
    /// when they tick a WASAPI mic on while the block is in place.</summary>
    private void WarnMicrophoneBlockedByWindowsPrivacy()
    {
        ForegroundDialog.Show(owner => MessageBox.Show(owner,
            "Windows is currently blocking desktop apps from using your microphone, so RemSound can "
                + "switch the mic on but will only send silence - the people you're connected to won't "
                + "hear you.\n\n"
                + "To fix it, open Windows Settings, go to Privacy & security, then Microphone, and turn "
                + "ON both of these:\n\n"
                + "    - Microphone access\n"
                + "    - Let desktop apps access your microphone\n\n"
                + "Then your mic will work. This doesn't affect sound you receive - only sending your "
                + "own microphone.",
            "Windows is blocking your microphone",
            MessageBoxButtons.OK, MessageBoxIcon.Warning));
    }

    /// <summary>On profile load, if a WASAPI microphone is already ticked but Windows is blocking
    /// desktop-app mic access, show the warning once — the per-tick warning only fires on a fresh
    /// tick, so this covers a profile that loads with the mic already on. Same OK-only message.</summary>
    private void MaybeWarnMicBlockedOnStartup()
    {
        if (IsDisposed) return;
        var blocked = IsMicrophoneBlockedByWindowsPrivacy();
        var anyWasapiMicChecked = false;
        for (var i = 0; i < sendInputDevicesList.Items.Count; i++)
        {
            if (sendInputDevicesList.GetItemChecked(i)) { anyWasapiMicChecked = true; break; }
        }
        // Log the detector's verdict every startup so a silent-mic session is no longer ambiguous —
        // we can see whether RemSound thought Windows was blocking the mic, not just whether it warned.
        logFile.Event($"mic-privacy: windows-blocks-desktop-mic={blocked} wasapiMicTicked={anyWasapiMicChecked}");
        // A --silent (automated/throwaway) launch logs the verdict but never pops the warning dialog.
        if (blocked && anyWasapiMicChecked && !CuePlayer.GloballyMuted) WarnMicrophoneBlockedByWindowsPrivacy();
    }

    /// <summary>
    /// On startup, if a Realtek ASIO driver is installed and we haven't already disabled it or
    /// shown the warning, offer (once) to disable it — Realtek's ASIO driver leaks OS handles on
    /// every open. "Yes" adds it to the global never-touch list; "No" is remembered so we don't nag.
    /// Shown from the Shown handler so the TaskDialog has a visible owner window.
    /// </summary>
    private void MaybeWarnAboutRealtekAsio()
    {
        // A --silent (automated/throwaway) launch must not pop this warning - its TaskDialog plays
        // the Windows warning ding even when nobody can see the dialog (it's on a minimized test
        // instance that's then auto-closed). The decision belongs to a real user at a real launch.
        if (CuePlayer.GloballyMuted) return;
        if (realtekAsioDriverNames.Count == 0) return;
        var cfg = AppConfig.Load();
        var changed = false;
        foreach (var driver in realtekAsioDriverNames)
        {
            if (cfg.IsAsioDriverDisabled(driver)) continue;     // already disabled
            if (cfg.HasWarnedAboutAsioDriver(driver)) continue; // already asked; user kept it
            var disable = ShowRealtekAsioWarning(driver);
            cfg.MarkAsioDriverWarned(driver);
            changed = true;
            if (disable)
            {
                cfg.SetAsioDriverDisabled(driver, true);
                disabledAsioDrivers.Add(driver);
                RemoveDisabledDriverFromPicker(driver);
                logFile.Event($"realtek asio disabled in RemSound via startup warning: \"{driver}\"");
            }
            else
            {
                logFile.Event($"realtek asio kept (user declined disable): \"{driver}\"");
            }
        }
        if (changed)
        {
            try { cfg.Save(); } catch { /* best-effort */ }
            UpdateRealtekAsioMenuItemText();
        }
    }

    private bool ShowRealtekAsioWarning(string driver)
    {
        var page = new TaskDialogPage
        {
            Caption = "RemSound — ASIO driver warning",
            Heading = "A Realtek ASIO driver was detected",
            Text = $"RemSound has detected you have a Realtek ASIO driver installed (\"{driver}\").\n\n"
                 + "This driver is known to cause compatibility issues with ASIO software, including "
                 + "RemSound — it leaks system resources and can make audio unstable.\n\n"
                 + "Would you like to disable it in RemSound? RemSound will then never touch this "
                 + "driver.\n\n"
                 + "Whichever you choose now, you can change it at any time: the Options menu has an "
                 + "\"Enable / Disable Realtek ASIO driver\" item that turns this driver on or off for "
                 + "RemSound whenever you like.",
            Icon = TaskDialogIcon.Warning,
        };
        var yes = new TaskDialogButton("&Yes, disable it (recommended)");
        var no = new TaskDialogButton("&No, keep using it");
        page.Buttons.Add(yes);
        page.Buttons.Add(no);
        page.DefaultButton = yes;
        return ForegroundDialog.Show(owner => TaskDialog.ShowDialog(owner, page)) == yes;
    }

    /// <summary>Options-menu handler: flip every installed Realtek ASIO driver between disabled and
    /// enabled in RemSound. If any are currently disabled, the action re-enables them all; otherwise
    /// it disables them all.</summary>
    private void ToggleRealtekAsio()
    {
        if (realtekAsioDriverNames.Count == 0) return;
        var anyDisabled = realtekAsioDriverNames.Exists(d => disabledAsioDrivers.Contains(d));
        var disable = !anyDisabled;
        var cfg = AppConfig.Load();
        foreach (var driver in realtekAsioDriverNames)
        {
            cfg.SetAsioDriverDisabled(driver, disable);
            cfg.MarkAsioDriverWarned(driver);
            if (disable)
            {
                disabledAsioDrivers.Add(driver);
                RemoveDisabledDriverFromPicker(driver);
            }
            else
            {
                disabledAsioDrivers.Remove(driver);
                AddDriverToPickerIfMissing(driver);
            }
        }
        try { cfg.Save(); } catch { /* best-effort */ }
        UpdateRealtekAsioMenuItemText();
        logFile.Event($"realtek asio {(disable ? "disabled" : "enabled")} in RemSound via Options menu: [{string.Join(", ", realtekAsioDriverNames)}]");
    }

    private void UpdateRealtekAsioMenuItemText()
    {
        if (realtekAsioToggleItem is null || realtekAsioDriverNames.Count == 0) return;
        var anyDisabled = realtekAsioDriverNames.Exists(d => disabledAsioDrivers.Contains(d));
        // Set BOTH the visible Text (with the mnemonic) and the AccessibleName (no mnemonic) to the
        // same Enable/Disable wording, so the screen reader reads exactly what's shown — never "Toggle".
        realtekAsioToggleItem.Text = anyDisabled
            ? "&Enable Realtek ASIO driver in RemSound"
            : "&Disable Realtek ASIO driver in RemSound";
        realtekAsioToggleItem.AccessibleName = anyDisabled
            ? "Enable Realtek ASIO driver in RemSound"
            : "Disable Realtek ASIO driver in RemSound";
    }

    private void RemoveDisabledDriverFromPicker(string driver)
    {
        var idx = asioDriverBox.Items.IndexOf(driver);
        if (idx < 0) return;
        var wasSelected = string.Equals(asioDriverBox.SelectedItem as string, driver, StringComparison.OrdinalIgnoreCase);
        asioDriverBox.Items.RemoveAt(idx);
        if (wasSelected)
        {
            asioDriverBox.SelectedIndex = 0; // "(none)" — back to WASAPI-only
            settings.SaveAsioDriverName(null);
            ClearAsioProbeCache();
        }
    }

    private void AddDriverToPickerIfMissing(string driver)
    {
        if (!asioDriverBox.Items.Contains(driver)) asioDriverBox.Items.Add(driver);
    }

    /// <summary>
    /// Builds <see cref="AudioDeviceChoice"/> entries for ASIO channel pairs (stereo) using the
    /// driver's own per-channel names, prefixed with the driver name. The
    /// <see cref="AudioDeviceChoice.DeviceId"/> uses the synthetic <c>"asio:&lt;pair&gt;"</c>
    /// format that <see cref="AsioCaptureBackend"/> and <see cref="AsioRenderBackend"/> parse.
    ///
    /// Label format: <c>"&lt;driverName&gt; — Pair N (channels A/B): &lt;lname&gt; / &lt;rname&gt;"</c>.
    /// Driver name first so NVDA announces "Audient EVO 8 — …" up front and there's no
    /// ambiguity about which card's channels you're picking. Pair number gives anchor context
    /// when the per-channel names are terse. If the left and right names share a common stem
    /// ending in L/R or 1/2 we collapse them ("Main Output L"/"Main Output R" → "Main Output L/R").
    /// </summary>
    private static IReadOnlyList<AudioDeviceChoice> BuildAsioChannelPairChoices(string driverName, IReadOnlyList<string> channelNames)
    {
        var pairCount = channelNames.Count / 2;
        var choices = new List<AudioDeviceChoice>(pairCount);
        for (var i = 0; i < pairCount; i++)
        {
            var lName = channelNames[i * 2];
            var rName = channelNames[i * 2 + 1];
            var combined = TryCollapsePairLabel(lName, rName) ?? $"{lName} / {rName}";
            var label = $"{driverName} — Pair {i + 1} (channels {i * 2 + 1}/{i * 2 + 2}): {combined}";
            choices.Add(new AudioDeviceChoice(label, AsioDeviceId.Format(i), CaptureKind.Loopback));
        }
        return choices;
    }

    /// <summary>
    /// Try to collapse "Main Output L" / "Main Output R" → "Main Output L/R", and similar
    /// patterns ending in "1"/"2" or "Left"/"Right". Returns null if the names don't share a
    /// common stem we can collapse cleanly — caller falls back to "Left / Right" form.
    /// </summary>
    private static string? TryCollapsePairLabel(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal)) return left;

        // Walk back from the end to find the divergence point — if the only difference is the
        // last character (and it's a known L/R pattern), collapse. Otherwise null.
        var commonLen = 0;
        var min = Math.Min(left.Length, right.Length);
        while (commonLen < min && left[commonLen] == right[commonLen]) commonLen++;
        if (commonLen == 0) return null;
        var stem = left[..commonLen].TrimEnd();
        var ldiff = left[commonLen..];
        var rdiff = right[commonLen..];
        if ((ldiff == "L" && rdiff == "R") || (ldiff == "1" && rdiff == "2") ||
            (ldiff == "Left" && rdiff == "Right") || (ldiff == "left" && rdiff == "right"))
        {
            return $"{stem} {ldiff}/{rdiff}";
        }
        return null;
    }

    private string lastLoggedAsioChannelSignature = string.Empty;

    /// <summary>
    /// Logs ASIO channel names once (and re-logs if they change because the driver was swapped).
    /// Helpful for diagnosing "the names don't look like the WASAPI ones" issues — we can see
    /// exactly what the ASIO driver is reporting and decide if our label-building is at fault
    /// or the driver is just terse.
    /// </summary>
    private void LogAsioChannelNamesIfChanged(string driverName, AsioDriverProbeResult info)
    {
        var sig = $"{driverName}|in:{string.Join(",", info.InputChannelNames)}|out:{string.Join(",", info.OutputChannelNames)}";
        if (sig == lastLoggedAsioChannelSignature) return;
        lastLoggedAsioChannelSignature = sig;
        logFile.Event($"asio channel names for \"{driverName}\": inputs=[{string.Join(", ", info.InputChannelNames.Select(n => $"\"{n}\""))}] outputs=[{string.Join(", ", info.OutputChannelNames.Select(n => $"\"{n}\""))}]");
    }

    /// <summary>
    /// Sync wrapper around <see cref="SyncDeviceCheckedListBox"/> that compares against the
    /// stored signature and only rebuilds on change. Returns true when the list was rebuilt.
    /// </summary>
    private bool MaybeSyncList(CheckedListBox list, IReadOnlyList<AudioDeviceChoice> devices, ref string lastSignature)
    {
        var signature = ComputeDeviceSignature(devices);
        if (signature == lastSignature) return false;
        SyncDeviceCheckedListBox(list, devices);
        lastSignature = signature;
        return true;
    }

    /// <summary>
    /// Rebuilds the list of devices in a CheckedListBox, preserving check state by DeviceId
    /// and SelectedIndex by DeviceId where possible. Returns the (newly-computed) signature
    /// of the device set so callers can stash it. Suppresses the per-item ItemCheck handler
    /// during the rebuild so existing handlers don't fire spuriously while we re-add items.
    /// </summary>
    private string SyncDeviceCheckedListBox(CheckedListBox list, IReadOnlyList<AudioDeviceChoice> devices)
    {
        var signature = ComputeDeviceSignature(devices);
        var checkedIds = new HashSet<string>(
            list.CheckedItems.OfType<AudioDeviceChoice>().Where(c => c.DeviceId is not null).Select(c => c.DeviceId!),
            StringComparer.OrdinalIgnoreCase);
        var selectedId = (list.SelectedItem as AudioDeviceChoice)?.DeviceId;

        suppressDeviceCheckChange = true;
        try
        {
            list.BeginUpdate();
            list.Items.Clear();
            var idx = -1;
            for (var i = 0; i < devices.Count; i++)
            {
                var d = devices[i];
                var isChecked = d.DeviceId is not null && checkedIds.Contains(d.DeviceId);
                list.Items.Add(d, isChecked);
                if (selectedId is not null && d.DeviceId == selectedId) idx = i;
            }
            if (idx >= 0) list.SelectedIndex = idx;
            list.EndUpdate();
        }
        finally
        {
            suppressDeviceCheckChange = false;
        }
        return signature;
    }

    private static string ComputeDeviceSignature(IReadOnlyList<AudioDeviceChoice> devices) =>
        string.Join(";", devices.Select(d => $"{d.DeviceId}|{d.Name}"));

    private void ApplyReceiveDevices()
    {
        // Combine WASAPI device-ids and ASIO synthetic-ids into one list. The
        // CompositeRenderBackend splits them internally and feeds each child the right subset.
        var ids = new List<string>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? followedDefault = null;
        foreach (var c in receiveOutputDevicesList.CheckedItems.OfType<AudioDeviceChoice>())
        {
            if (c.IsDefaultFollower)
            {
                // Resolve the current Windows default render device live.
                followedDefault = ResolveDefaultDeviceId(NAudio.CoreAudioApi.DataFlow.Render);
                if (!string.IsNullOrEmpty(followedDefault) && added.Add(followedDefault)) ids.Add(followedDefault);
            }
            else if (!string.IsNullOrEmpty(c.DeviceId) && added.Add(c.DeviceId))
            {
                ids.Add(c.DeviceId);
            }
        }
        lastFollowedDefaultOutputId = followedDefault;
        foreach (var c in asioReceiveOutputDevicesList.CheckedItems.OfType<AudioDeviceChoice>())
        {
            if (!string.IsNullOrEmpty(c.DeviceId) && added.Add(c.DeviceId)) ids.Add(c.DeviceId);
        }
        receiver.SetOutputDevices(ids);
    }

    // ===================== "Use Windows default device" follower support =====================

    /// <summary>Returns a new list with <paramref name="follower"/> at the top, followed by
    /// <paramref name="devices"/>. Used to put the "Use Windows default ..." entry first in the
    /// receive-output and send-input lists.</summary>
    private static IReadOnlyList<AudioDeviceChoice> WithDefaultFollower(IReadOnlyList<AudioDeviceChoice> devices, AudioDeviceChoice follower)
    {
        var list = new List<AudioDeviceChoice>(devices.Count + 1) { follower };
        list.AddRange(devices);
        return list;
    }

    /// <summary>The id of the current Windows default endpoint for the given direction, or null if
    /// there isn't one / it can't be read. Best-effort.</summary>
    // Shared with the service via AudioDefaultFollower so "the current Windows default" means the same
    // thing in both. Thin delegate kept so the existing call sites don't all have to change.
    private static string? ResolveDefaultDeviceId(NAudio.CoreAudioApi.DataFlow flow) =>
        AudioDefaultFollower.ResolveDefaultDeviceId(flow);

    private static bool IsFollowerChecked(CheckedListBox list) =>
        list.CheckedItems.OfType<AudioDeviceChoice>().Any(c => c.IsDefaultFollower);

    /// <summary>When following the Windows default and that default device changes, the device SET is
    /// unchanged so the list-sync doesn't fire — so re-route here. Called from OnAudioEndpointsChanged,
    /// which the change-notifier raises (among other things) on a default-device change.</summary>
    private void ReapplyIfFollowedDefaultChanged()
    {
        if (IsFollowerChecked(receiveOutputDevicesList)
            && ResolveDefaultDeviceId(NAudio.CoreAudioApi.DataFlow.Render) != lastFollowedDefaultOutputId)
        {
            ApplyReceiveDevices();
        }
        if (IsFollowerChecked(sendInputDevicesList)
            && ResolveDefaultDeviceId(NAudio.CoreAudioApi.DataFlow.Capture) != lastFollowedDefaultInputId)
        {
            ApplySendSources();
        }
        // Send-side loopback follower tracks the default OUTPUT (Render) — re-route when it moves.
        if (IsFollowerChecked(sendOutputDevicesList)
            && ResolveDefaultDeviceId(NAudio.CoreAudioApi.DataFlow.Render) != lastFollowedDefaultLoopbackId)
        {
            ApplySendSources();
        }
    }

    private static void PersistUseDefaultDevice(bool output, bool on)
    {
        try
        {
            var c = AppConfig.Load();
            if (output) c.UseDefaultOutputDevice = on; else c.UseDefaultInputDevice = on;
            c.Save();
        }
        catch { /* harmless — choice just won't survive a restart */ }
    }

    private static void PersistUseDefaultLoopbackSend(bool on)
    {
        try { var c = AppConfig.Load(); c.UseDefaultLoopbackSend = on; c.Save(); }
        catch { /* harmless — choice just won't survive a restart */ }
    }

    private void RestoreDefaultFollowerChecks()
    {
        AppConfig cfg;
        try { cfg = AppConfig.Load(); }
        catch { return; }
        SetFollowerChecked(receiveOutputDevicesList, cfg.UseDefaultOutputDevice);
        SetFollowerChecked(sendInputDevicesList, cfg.UseDefaultInputDevice);
        SetFollowerChecked(sendOutputDevicesList, cfg.UseDefaultLoopbackSend);
    }

    private void SetFollowerChecked(CheckedListBox list, bool on)
    {
        if (!on) return;
        suppressDeviceCheckChange = true;
        try
        {
            for (var i = 0; i < list.Items.Count; i++)
            {
                if (list.Items[i] is AudioDeviceChoice { IsDefaultFollower: true })
                {
                    list.SetItemChecked(i, true);
                    break;
                }
            }
            // The default is exclusive: with it on, no specific card may stay ticked. Enforce it at load
            // too, so a profile/config that somehow carries both doesn't come up with both ticked.
            AudioDefaultFollower.UncheckRealDevices(list);
        }
        finally { suppressDeviceCheckChange = false; }
    }

    private void UntickAllExceptDefaultFollower(CheckedListBox list, bool output)
    {
        var anyChanged = false;
        suppressDeviceCheckChange = true;
        try
        {
            for (var i = 0; i < list.Items.Count; i++)
            {
                if (list.Items[i] is AudioDeviceChoice c && !c.IsDefaultFollower && list.GetItemChecked(i))
                {
                    list.SetItemChecked(i, false);
                    if (output && c.DeviceId is { } id) rememberedReceiveOutputIds.Remove(id);
                    anyChanged = true;
                }
            }
        }
        finally { suppressDeviceCheckChange = false; }
        if (!anyChanged) return;
        if (output) ApplyReceiveDevices(); else ApplyAudioRuntime();
    }

    /// <summary>A receive-output card that was unplugged drops out of the WASAPI list (and its tick
    /// with it). When it returns, re-tick it from <see cref="rememberedReceiveOutputIds"/> so audio
    /// resumes automatically (issue #5). Only RE-ticks present-but-unticked remembered devices; it
    /// never unticks — that's a deliberate user action handled in the ItemCheck handler. Receive-only
    /// on purpose: the send lists keep their "re-tick each session" behaviour (see AudioDeviceCatalog).</summary>
    private void ReapplyRememberedReceiveOutputs()
    {
        if (rememberedReceiveOutputIds.Count == 0) return;
        var changed = false;
        suppressDeviceCheckChange = true;
        try
        {
            for (var i = 0; i < receiveOutputDevicesList.Items.Count; i++)
            {
                if (receiveOutputDevicesList.Items[i] is not AudioDeviceChoice c || c.DeviceId is null) continue;
                if (rememberedReceiveOutputIds.Contains(c.DeviceId) && !receiveOutputDevicesList.GetItemChecked(i))
                {
                    receiveOutputDevicesList.SetItemChecked(i, true);
                    changed = true;
                }
            }
        }
        finally { suppressDeviceCheckChange = false; }
        if (changed) logFile.Event("receive output: re-ticked a returning device from the remembered selection");
    }

    /// <summary>
    /// Applies the audio-backend mode derived from the current ASIO driver choice. Two effective
    /// modes after the 2026-05-11 cleanup:
    ///   * WasapiOnly:      no ASIO driver selected. WASAPI lists shown, ASIO lists hidden,
    ///                      fast path active.
    ///   * BothIndependent: an ASIO driver is selected. All five lists shown; WASAPI and ASIO
    ///                      run as two parallel lanes each at their own native latency.
    /// On every call, list visibility is refreshed and any ticks in now-hidden lists are wiped
    /// so they don't contribute ghost specs to the next ApplyAudioRuntime push.
    /// </summary>
    /// <summary>True if this audio-mode runs an ASIO backend. BothIndependent does; WasapiOnly
    /// does not. The legacy AudioMode.Both and AudioMode.AsioOnly values can only arrive here
    /// from an old persisted profile JSON; they're treated as ASIO-using so deserialisation
    /// stays graceful but no UI path can produce them any more.</summary>
    private static bool ModeUsesAsio(AudioMode mode) =>
        mode == AudioMode.AsioOnly || mode == AudioMode.Both || mode == AudioMode.BothIndependent;

    // ===================== BothIndependent companion controls =====================
    //
    // The ASIO-lane latency row created in BuildAudioReceiveGroupContents. These four refs
    // live at class scope so UpdateBothIndependentVisibility can hide/show the row whenever
    // the audio mode changes, and so WireBothIndependentControls can attach event handlers
    // once the form is built.
    private Label? asioLatencyLabel;
    private Label? wasapiLatencyLabel;
    private FlowLayoutPanel? asioDelayContainer;

    /// <summary>
    /// Attaches the ValueChanged / CheckedChanged handlers for the ASIO-lane companion
    /// controls. Called once from BuildAudioReceiveGroupContents after both rows exist.
    /// </summary>
    private void WireBothIndependentControls()
    {
        // ASIO latency spinner. Persists to settings + pushes to the receiver's per-route
        // setter so the audio thread sees the new target on the next Read. Soft-set on the
        // receiver (no drain) — drift correction will shrink the buffer naturally on a
        // lower; raising is silent by definition.
        maxLatencyAsioBox.ValueChanged += (_, _) =>
        {
            var value = (int)maxLatencyAsioBox.Value;
            var fromAutoTune = suppressUserAsioSliderMoveTracking;
            if (!fromAutoTune)
            {
                lastUserAsioSliderMoveUtc = DateTime.UtcNow;
                // When auto-tune is on, the slider value is runtime state (auto-tune will
                // overwrite it). Don't dirty the profile for those changes — matches the
                // user's mental model of "auto-tune on = latency is automatic, not saved".
                if (!settings.LoadContinuousAutoTuneAsioEnabled()) MarkProfileDirty();
            }
            settings.SaveMaxLatencyMsAsio(value);
            // Soft path on auto-tune (no drain, drift corrector handles the lower); hard
            // path on a user-initiated change (immediate, responsive).
            if (fromAutoTune)
            {
                receiver.SetMaxLatencyMsSoftFor(RenderRoute.AsioLane, value);
            }
            else
            {
                receiver.SetMaxLatencyMsFor(RenderRoute.AsioLane, value);
            }
        };

        continuousTuneAsioBox.CheckedChanged += (_, _) =>
        {
            settings.SaveContinuousAutoTuneAsioEnabled(continuousTuneAsioBox.Checked);
            // The interval combo is shared between both lanes — keep it enabled whenever
            // either lane's auto-tune is on. Without this, ticking ASIO auto-tune (in
            // BothIndependent) left the interval combo greyed out and made the recheck
            // cadence invisible to the user even though it was actively in effect.
            continuousIntervalBox.Enabled = AnyAutoTuneEnabled();
            ApplyContinuousTuneTimer();
            MarkProfileDirty();
        };

        // Push initial value to the receiver so the per-route state matches the persisted
        // slider value even before any audio flows.
        receiver.SetMaxLatencyMsSoftFor(RenderRoute.AsioLane, (int)maxLatencyAsioBox.Value);
    }

    /// <summary>
    /// Toggles visibility of the BothIndependent-only ASIO row and rewrites the WASAPI row's
    /// labels and mnemonics based on the current audio mode. In classic modes the WASAPI row
    /// reverts to its legacy "Audio latency (Alt+L)" / "Continuous auto-tune latency (Alt+T)"
    /// shape and the ASIO row is hidden. In BothIndependent the ASIO row is shown above the
    /// WASAPI row (first in tab order) and the WASAPI row's labels become "WASAPI latency
    /// (Alt+W)" / "Continuous auto-tune WASAPI (Alt+Y)" so the two sets of mnemonics don't
    /// collide. Idempotent — call from anywhere the audio mode might have changed.
    /// </summary>
    private void UpdateBothIndependentVisibility()
    {
        if (asioLatencyLabel is null || wasapiLatencyLabel is null || asioDelayContainer is null) return;
        var inBothIndependent = settings.LoadAudioMode() == AudioMode.BothIndependent;
        asioLatencyLabel.Visible = inBothIndependent;
        asioDelayContainer.Visible = inBothIndependent;
        maxLatencyAsioBox.Visible = inBothIndependent;
        continuousTuneAsioBox.Visible = inBothIndependent;
        // Mode change may have changed which auto-tune flags count toward "any enabled":
        // leaving BothIndependent drops the ASIO lane's checkbox from consideration, and
        // entering it brings it back. Re-evaluate so the shared interval combo's Enabled
        // state tracks reality after every mode flip.
        continuousIntervalBox.Enabled = AnyAutoTuneEnabled();
        if (inBothIndependent)
        {
            wasapiLatencyLabel.Text = "WASAPI latency in milliseconds (Alt+&W)";
            maxLatencyBox.AccessibleName = "WASAPI latency in milliseconds (Alt+W)";
            continuousTuneBox.Text = "Continuous auto-tune WASAPI latency (Alt+&Y)";
            continuousTuneBox.AccessibleName = "Continuous auto-tune WASAPI latency";
            // The interval combo drives ticks for BOTH lanes' auto-tunes — each lane
            // independently lands wherever its own algorithm decides (40 ms WASAPI / 20 ms
            // ASIO is fine), but the cadence dropdown is shared. Make that explicit in the
            // label so a user looking at the WASAPI row doesn't assume the interval only
            // applies there.
            if (continuousIntervalLabel is not null)
            {
                continuousIntervalLabel.Text = "Auto-tune interval — WASAPI and ASIO (Alt+&I)";
            }
            continuousIntervalBox.AccessibleName = "Auto-tune interval for WASAPI and ASIO (Alt+I)";
        }
        else
        {
            wasapiLatencyLabel.Text = "Audio latency in milliseconds (Alt+&L)";
            maxLatencyBox.AccessibleName = "Audio latency in milliseconds (Alt+L)";
            continuousTuneBox.Text = "Continuous auto-tune latency (Alt+&T)";
            continuousTuneBox.AccessibleName = "Continuous auto-tune latency";
            // Classic mode — single lane, original label is unambiguous.
            if (continuousIntervalLabel is not null)
            {
                continuousIntervalLabel.Text = "Auto-tune latency interval (Alt+&I)";
            }
            continuousIntervalBox.AccessibleName = "Auto-tune latency interval (Alt+I)";
        }
    }

    // Tracks the last time the user moved the ASIO slider — auto-tune defers tuning for one
    // tick afterward so the user's deliberate change isn't immediately overridden. Parallels
    // lastUserSliderMoveUtc which serves the same role for the WASAPI / classic slider.
    private DateTime lastUserAsioSliderMoveUtc = DateTime.MinValue;

    /// <summary>True if this audio-mode runs a WASAPI backend. Today only AsioOnly excludes
    /// it; everything else (WasapiOnly, BothIndependent, the legacy Both) shows the WASAPI
    /// device lists. Kept as a predicate so a future mode addition just needs to update the
    /// expression rather than every call site.</summary>
    private static bool ModeUsesWasapi(AudioMode mode) => mode != AudioMode.AsioOnly;

    // ModeFromListIndex / ListIndexFromMode retired 2026-05-11 — there is no audio-mode
    // listbox any more, so there are no indices to translate. The audio mode is derived
    // directly from settings.LoadAudioMode(), which itself reads back the ASIO driver name
    // ("none" → WasapiOnly, anything else → BothIndependent).

    private void ApplyAsioMode()
    {
        var requestedMode = settings.LoadAudioMode();
        var driver = settings.LoadAsioDriverName();
        var resolvedMode = requestedMode;
        // Sanity: an ASIO mode without a driver demotes to WasapiOnly. Should be unreachable
        // through normal UI flow (the listbox is disabled when there are no drivers).
        if (ModeUsesAsio(requestedMode) && string.IsNullOrWhiteSpace(driver))
        {
            resolvedMode = AudioMode.WasapiOnly;
        }

        var asioDriverArg = ModeUsesAsio(resolvedMode) ? driver : null;
        try
        {
            // In a headless test build, don't switch the real audio backends (which can open an ASIO
            // driver); the list visibility below is UI-only and still runs so the mode toggle is testable.
            if (!headless)
            {
                sender.SetAudioMode(resolvedMode, asioDriverArg);
                receiver.SetAudioMode(resolvedMode, asioDriverArg);
            }
            logFile.Event(resolvedMode == AudioMode.WasapiOnly
                ? "audio backend: WASAPI only (fast path)"
                : $"audio backend: WASAPI + ASIO driver \"{asioDriverArg}\" (independent lanes, no mix)");
        }
        catch (Exception ex)
        {
            logFile.Event($"backend switch failed: {ex.GetType().Name}: {ex.Message}");
        }

        // List visibility per mode. BothIndependent shows both WASAPI and ASIO lists — user
        // needs to assign devices to each lane. WasapiOnly hides the ASIO lists.
        var wasapiListsVisible = ModeUsesWasapi(resolvedMode);
        var asioListsVisible = ModeUsesAsio(resolvedMode);
        // Driver picker stays visible whenever at least one ASIO driver is installed — that
        // way the user can turn ASIO on (by picking a driver) or off (by selecting "(none)")
        // without it disappearing on them. BuildAudioIOTab already omits the picker entirely
        // on machines with zero ASIO drivers (hasAnyAsioDriverInstalled false), in which case
        // both the listbox and its label are null-or-hidden and these lines are no-ops.
        asioDriverBox.Visible = hasAnyAsioDriverInstalled;
        if (asioDriverLabel is not null) asioDriverLabel.Visible = hasAnyAsioDriverInstalled;
        receiveOutputDevicesList.Visible = wasapiListsVisible;
        receiveOutputDevicesStatusLabel.Visible = wasapiListsVisible;
        if (receiveOutputDevicesLabel is not null) receiveOutputDevicesLabel.Visible = wasapiListsVisible;
        sendOutputDevicesList.Visible = wasapiListsVisible;
        sendOutputDevicesStatusLabel.Visible = wasapiListsVisible;
        if (sendOutputDevicesLabel is not null) sendOutputDevicesLabel.Visible = wasapiListsVisible;
        sendInputDevicesList.Visible = wasapiListsVisible;
        sendInputDevicesStatusLabel.Visible = wasapiListsVisible;
        if (sendInputDevicesLabel is not null) sendInputDevicesLabel.Visible = wasapiListsVisible;
        asioReceiveOutputDevicesList.Visible = asioListsVisible;
        asioReceiveOutputDevicesStatusLabel.Visible = asioListsVisible;
        if (asioReceiveOutputDevicesLabel is not null) asioReceiveOutputDevicesLabel.Visible = asioListsVisible;
        asioSendDevicesList.Visible = asioListsVisible;
        asioSendDevicesStatusLabel.Visible = asioListsVisible;
        if (asioSendDevicesLabel is not null) asioSendDevicesLabel.Visible = asioListsVisible;

        // Force list refresh — ASIO list content depends on which driver is loaded.
        asioSendDevicesSignature = string.Empty;
        asioReceiveOutputDevicesSignature = string.Empty;
        RefreshAudioDeviceLists();

        // Clear ticks in hidden lists so they don't contribute ghost specs. Track whether we
        // actually wiped anything for the log line; the re-apply below runs unconditionally
        // because the new backend instance has no source/output state regardless.
        var wipedSomething = false;
        try
        {
            suppressDeviceCheckChange = true;
            if (!wasapiListsVisible)
            {
                for (var i = 0; i < receiveOutputDevicesList.Items.Count; i++)
                    if (receiveOutputDevicesList.GetItemChecked(i)) { receiveOutputDevicesList.SetItemChecked(i, false); wipedSomething = true; }
                for (var i = 0; i < sendOutputDevicesList.Items.Count; i++)
                    if (sendOutputDevicesList.GetItemChecked(i)) { sendOutputDevicesList.SetItemChecked(i, false); wipedSomething = true; }
                for (var i = 0; i < sendInputDevicesList.Items.Count; i++)
                    if (sendInputDevicesList.GetItemChecked(i)) { sendInputDevicesList.SetItemChecked(i, false); wipedSomething = true; }
            }
            if (!asioListsVisible)
            {
                for (var i = 0; i < asioSendDevicesList.Items.Count; i++)
                    if (asioSendDevicesList.GetItemChecked(i)) { asioSendDevicesList.SetItemChecked(i, false); wipedSomething = true; }
                for (var i = 0; i < asioReceiveOutputDevicesList.Items.Count; i++)
                    if (asioReceiveOutputDevicesList.GetItemChecked(i)) { asioReceiveOutputDevicesList.SetItemChecked(i, false); wipedSomething = true; }
            }
        }
        finally { suppressDeviceCheckChange = false; }
        // Always re-apply send sources and receive outputs after a mode change. The new
        // composite instance was built fresh — even if no ticks got wiped (e.g. WasapiOnly →
        // Both, where existing WASAPI ticks survive), the new backend has empty internal state
        // and needs the current spec/device list pushed to it. Without this, a user mid-session
        // who picks a different audio mode would silently lose their receive output and have
        // to re-tick to get audio back.
        ApplyAudioRuntime();
        ApplyReceiveDevices();
        // The block above force-set the WASAPI send-list visibility for the new mode. Re-assert the
        // per-application send view on top so the loopback-outputs list stays hidden (and the app list
        // shown) when we're in applications mode — ASIO toggling must not resurrect the wrong list.
        ApplySendModeVisibility();
        if (wipedSomething) logFile.Event($"audio mode change wiped now-hidden device ticks");
    }

    /// <summary>
    /// Unchecks every input and output on every soundcard — WASAPI and ASIO, send and receive —
    /// in a single press. Idempotent: it only ever clears ticks, so pressing it again when things
    /// are already unchecked is a harmless no-op. Provided because the device lists can run long
    /// enough that it's hard to remember what's selected; this is the quick "start from nothing".
    /// Suppresses the per-item ItemCheck handler during the sweep, then applies the now-empty
    /// selection once so audio actually stops, marks the profile dirty, and resets the status
    /// labels so a screen reader hears the cleared state.
    /// </summary>
    private void UncheckAllDevices()
    {
        var lists = new[]
        {
            receiveOutputDevicesList, asioReceiveOutputDevicesList,
            sendOutputDevicesList, sendInputDevicesList, asioSendDevicesList,
        };
        try
        {
            suppressDeviceCheckChange = true;
            foreach (var list in lists)
                for (var i = 0; i < list.Items.Count; i++)
                    if (list.GetItemChecked(i)) list.SetItemChecked(i, false);
        }
        finally { suppressDeviceCheckChange = false; }

        // Also reset the ASIO driver to "(none)" (row 0): the button now does a full reset to a
        // clean WASAPI-only, nothing-selected state. Setting the index fires the driver-change
        // handler, which saves the change and rebuilds the engine in WASAPI-only mode.
        if (asioDriverBox.SelectedIndex > 0) asioDriverBox.SelectedIndex = 0;

        ApplyAudioRuntime();
        ApplyReceiveDevices();
        MarkProfileDirty();

        receiveOutputDevicesStatusLabel.Text = "No output device selected.";
        sendOutputDevicesStatusLabel.Text = "No output device selected.";
        sendInputDevicesStatusLabel.Text = "No input device selected.";
        asioReceiveOutputDevicesStatusLabel.Text = "No ASIO receive channel selected.";
        asioSendDevicesStatusLabel.Text = "No ASIO send channel selected.";

        logFile.Event("user pressed 'Uncheck all inputs and outputs on all soundcards'");
    }

    /// <summary>
    /// Called by <see cref="PowerResumeHandler"/> on a background thread after the system has
    /// woken from sleep / hibernate (plus a short USB-settle delay). Marshals onto the UI
    /// thread and runs the audio-backend re-init. Swallows the form-already-torn-down race —
    /// the handler can fire just as the app is being closed.
    /// </summary>
    private void OnSystemResume()
    {
        try
        {
            if (IsDisposed) return;
            BeginInvoke(ReinitAudioBackendsForResume);
        }
        catch (ObjectDisposedException) { /* form torn down — nothing to do */ }
        catch (InvalidOperationException) { /* handle not created yet — same */ }
    }

    /// <summary>
    /// Runs on the UI thread. Closes and reopens the audio backend on both sides (receiver
    /// render and sender capture) so any post-sleep wedged state in the USB audio drivers is
    /// cleared. Shows the audio-driver splash on its own thread while the reset happens, so
    /// the user sees "Reconnecting to audio driver…" instead of a frozen window.
    ///
    /// Implementation note: the receiver's <see cref="RemSound.Receiver.AudioReceiver.SetAudioMode"/>
    /// always tears down and rebuilds its render backend, which is exactly the reset we
    /// want. The sender's <see cref="RemSound.Sender.AudioSender.SetAudioMode"/> persists
    /// its ASIO driver across same-driver calls (to avoid an expensive reopen on every
    /// device-tick change) — so we explicitly bounce the sender through <c>WasapiOnly</c>
    /// first to force the ASIO driver to be disposed, then <see cref="ApplyAsioMode"/>
    /// puts both sides back to the real configuration. The net effect is a full close-and-
    /// reopen on both sides; same code path as a manual driver re-pick from the picker.
    /// </summary>
    private void ReinitAudioBackendsForResume()
    {
        if (IsDisposed) return;
        var mode = settings.LoadAudioMode();
        var driver = settings.LoadAsioDriverName();
        logFile.Event($"power: re-initialising audio backend after system resume (mode={mode}, driver={driver ?? "(none)"})");

        var splash = AsioLoadingSplash.StartIfAsioDriverName(driver, "Reconnecting to audio driver, please wait...");
        try
        {
            // Force the sender's persistent ASIO driver to be disposed by bouncing through
            // WasapiOnly. Skipped when there's no ASIO in the current mode — nothing to dispose.
            if (mode != AudioMode.WasapiOnly && !string.IsNullOrWhiteSpace(driver))
            {
                try { sender.SetAudioMode(AudioMode.WasapiOnly, null); }
                catch (Exception ex) { logFile.Event($"power: sender WasapiOnly bounce failed: {ex.GetType().Name}: {ex.Message}"); }
            }
            // ApplyAsioMode re-applies sender + receiver mode, refreshes device lists, and
            // re-pushes the audio-runtime + receive-device configuration. The receiver's
            // SetAudioMode call inside it does an unconditional render-backend rebuild; the
            // sender's, post-bounce, recreates its persistent ASIO from scratch.
            ClearAsioProbeCache();
            ApplyAsioMode();
            logFile.Event("power: audio backend re-initialised");

            // Re-poke the router. UPnP/NAT-PMP mappings often survive a sleep, but cheap
            // routers and ISP-supplied combo boxes sometimes drop their NAT table — easier
            // to just rediscover than to guess. Refresh() is a no-op if UPnP is off. Run on
            // a thread-pool thread for the same reason as the other UPnP entry points: the
            // NatUtility teardown + restart inside Refresh() can block for tens of seconds
            // on unusual networks, and we're on the UI thread during the resume handler.
            // 2026-05-23.
            if (AppConfig.Load().UpnpEnabled)
            {
                Task.Run(() =>
                {
                    try { routerPortMapper.Refresh(); }
                    catch (Exception ex) { logFile.Event($"upnp: refresh-on-resume failed: {ex.GetType().Name}: {ex.Message}"); }
                });
            }
        }
        catch (Exception ex)
        {
            logFile.Event($"power: audio backend re-init failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            splash?.Dismiss();
        }
    }

    // ===================== Mode-change warnings =====================

    // ShowBothModeWarning + its TaskDialog retired 2026-05-11. The popup warned about the
    // ~45 ms latency penalty of classic mixed-Both mode. Classic Both is no longer reachable
    // from the UI (only WasapiOnly and BothIndependent are produced now, both fast-path), so
    // the warning has nothing to fire on. AppConfig.BothModeWarningSuppressed is kept on disk
    // for backward-compat — old config files still deserialise, new code just ignores it.

    /// <summary>
    /// Confirmation popup after Save (Ctrl+S / File → Save) overwrites the current profile.
    /// Native TaskDialog, NVDA reads
    /// the heading + body automatically, verification checkbox is part of the tab order so
    /// "Do not show me this again" is reachable without a mouse. Once ticked the preference
    /// lives in <c>remsound.config.json</c> as
    /// <see cref="AppConfig.SaveProfileConfirmationSuppressed"/>; it's only consulted from
    /// the in-place Save path — Save As never reaches here (its own dialog is the
    /// confirmation).
    /// </summary>
    private void ShowSaveConfirmationDialog(string title)
    {
        var verification = new TaskDialogVerificationCheckBox("Do not show me this message again");
        var page = new TaskDialogPage
        {
            Caption = AppName,
            Heading = "Profile saved",
            Text = $"\"{title}\" has been saved.",
            Icon = TaskDialogIcon.Information,
            Verification = verification,
            Buttons = { TaskDialogButton.OK },
            DefaultButton = TaskDialogButton.OK,
            AllowCancel = true,
        };

        TaskDialog.ShowDialog(this, page);

        if (verification.Checked)
        {
            var cfg = AppConfig.Load();
            cfg.SaveProfileConfirmationSuppressed = true;
            cfg.Save();
            logFile.Event("save-profile confirmation suppressed by user (saved to remsound.config.json)");
        }
    }

    // ===================== Status / log =====================

    private void UpdateStatus()
    {
        var since = connected ? (DateTime.UtcNow - connectedSinceUtc).ToString(@"h\:mm\:ss") : "0:00:00";
        var sendText = sender.IsRunning
            ? $"sending {sender.PacketsSent} packets ({sender.BytesSent / 1024} KB) codec={sender.Codec} from \"{sender.CaptureDeviceName}\""
            : (IsSendEnabled && !HasCheckedSendDevice() ? "not sending — tick a capture device" : "not sending");
        var receiveText = receiver.IsRunning
            ? $"receiving {receiver.PacketsReceived} packets, buffer {receiver.CurrentBufferMs} ms (target {receiver.TargetLatencyMs} ms), underruns {receiver.Underruns}, drops {receiver.Drops} on \"{receiver.OutputDeviceName}\""
            : "not receiving";
        var peerCount = knownPeers.Count;
        var hbSummary = heartbeatService?.GetHealthSummary() ?? "no peers";
        // Weak-password block surfaced here, NON-modally, so NVDA reads it without a startup dialog
        // trap (2026-07-27). Leads the status line so it's the first thing spoken.
        var weakPrefix = WeakPasswordBlocksAudio(currentProfilePassword, currentAudioKey is not null)
            ? "No audio: this profile's password is too weak to protect it — change it in the File menu, “Change this profile's password”, on every machine you connect with. "
            : "";
        statusLabel.Text = $"{weakPrefix}Connected for {since}. {peerCount} peer(s) known. {sendText}. {receiveText}. Heartbeat: {hbSummary}.";
        bool streaming = connected && (sender.IsRunning || receiver.IsRunning);
        healthLabel.Text = connected
            ? streaming ? "Health: streaming" : "Health: idle"
            : "Health: disconnected";
        healthDot.SetColor(!connected ? Theme.Neutral : streaming ? Theme.Healthy : Theme.Warning);
    }

    private void SnapshotLogIfDue()
    {
        if (DateTime.UtcNow - lastSnapshotUtc < TimeSpan.FromMilliseconds(950)) return;
        lastSnapshotUtc = DateTime.UtcNow;
        // Prune any sessions on the receiver that haven't received packets in a while. This is
        // serialised on the network-thread lock inside the receiver, so doing it from the UI
        // tick is safe.
        receiver.PruneIdleSessions();
        // Re-evaluate which selected peers are reachable enough to receive the audio stream.
        // Drops long-unreachable endpoints from the high-rate send list (heartbeat keeps probing
        // them so they auto-recover). Cheap no-op when nothing changed. 1 Hz is plenty.
        RefreshAudioReceivers();
        // Refresh the tray icon's hover tooltip so it reflects the current peer count and
        // send / receive routing (WASAPI / ASIO / both). 1 Hz cadence is fine — the user is
        // hovering, not staring at a counter — and BuildTrayTooltip is allocation-cheap.
        trayController.SetTooltip(BuildTrayTooltip());
        // Surface any password mismatch / out-of-date peer the receiver has spotted (once per
        // change). Cheap when everything matches.
        CheckPeerSecurity();
        // Periodic native-memory reaper. SustainedLowLatency GC mode (set in Program.Main)
        // explicitly avoids gen2 collections to keep audio scheduling smooth — but that same
        // suppression means finalizers for IDisposable wrappers that didn't get explicit
        // Dispose calls also never run. Most paths have been fixed (StreamSession,
        // OpusEncoderState, AudioRecorder all call decoder/encoder Dispose now), but this
        // serves as a belt-and-braces backstop for any future code path we forget to wire,
        // and for cleaning up any per-call native scratch allocations that Concentus.Native
        // (or any other library) might accumulate. Forced gen2 every 5 minutes (300 ticks at
        // 1 Hz) on a background thread so the gen2 work doesn't hitch the UI thread; audio
        // threads are separate and unaffected. Andre's v3.0.1 receive session showed the
        // unmanaged working set climbing 83 MB → 3.5 GB over 23 hours; this caps it.
        nativeReaperTickCount++;
        if (nativeReaperTickCount >= 300)
        {
            nativeReaperTickCount = 0;
            Task.Run(() =>
            {
                try
                {
                    GC.Collect(2, GCCollectionMode.Optimized, blocking: true, compacting: false);
                    GC.WaitForPendingFinalizers();
                }
                catch { /* GC pass is best-effort — never let it crash the snapshot tick */ }
            });
        }
        // Detect peer health transitions and play connect/disconnect cues.
        DetectAndAnnouncePeerHealthTransitions();
        // If neither logs nor auto-tune is active we have nothing to do — neither audience
        // wants the diag work. When logs are off but auto-tune is on (the gate is on for
        // auto-tune), we fall through and run the snapshot+drain so the auto-tune feed
        // (recentMaxGaps / recentRenderCbGaps at the bottom of this method) gets fresh
        // data. The logFile.Snapshot and logFile.Event calls below are themselves cheap
        // no-ops when logFile.Enabled is false, so we don't need to wrap individual writes.
        if (!DiagnosticsGate.Enabled) return;

        // (The per-minute HandleTypeProbe walk that lived here — added 2026-06-07 to name the leaking
        // handle type in the Realtek/WASAPI investigation — was retired in the 2026-07-19 legacy sweep
        // with that investigation closed. Git history has it if a handle hunt ever needs it back.)

        // SNAP latency columns: in classic modes the legacy MaxLatencyMs / TargetLatencyMs
        // pair holds the only route's value (Mixed). In BothIndependent we map them to the
        // WASAPI lane (= the lane the existing slider drives) and emit the ASIO lane in the
        // appended ASIO columns. That keeps the existing columns meaningful — they still
        // represent "what the main slider shows" — and the appended columns expose the
        // second lane to anyone reading the log file.
        var inBothIndependent = settings.LoadAudioMode() == AudioMode.BothIndependent;
        var primaryMaxMs = inBothIndependent ? receiver.MaxLatencyMsFor(RenderRoute.WasapiLane) : receiver.MaxLatencyMs;
        var primaryTargetMs = inBothIndependent ? receiver.TargetLatencyMsFor(RenderRoute.WasapiLane) : receiver.TargetLatencyMs;
        var asioMaxMs = inBothIndependent ? receiver.MaxLatencyMsFor(RenderRoute.AsioLane) : 0;
        var asioTargetMs = inBothIndependent ? receiver.TargetLatencyMsFor(RenderRoute.AsioLane) : 0;
        logFile.Snapshot(
            connected: connected,
            sendRunning: sender.IsRunning,
            receiveRunning: receiver.IsRunning,
            codec: SnapshotCodecLabel(),
            maxLatencyMs: primaryMaxMs,
            targetLatencyMs: primaryTargetMs,
            bufferMs: receiver.CurrentBufferMs,
            senderPackets: sender.PacketsSent,
            senderBytes: sender.BytesSent,
            senderDevice: sender.CaptureDeviceName,
            receiverPackets: receiver.PacketsReceived,
            receiverBytes: receiver.BytesReceived,
            underruns: receiver.Underruns,
            drops: receiver.Drops,
            receiveDevice: receiver.OutputDeviceName,
            heartbeat: heartbeatService?.GetHealthSummary() ?? "no peers",
            opusFecRecoveries: receiver.OpusFecRecoveries,
            opusUnrecoveredGaps: receiver.OpusUnrecoveredGaps,
            maxLatencyMsAsio: asioMaxMs,
            targetLatencyMsAsio: asioTargetMs);

        // First-of-kind events make it easy to see in the log where the chain breaks.
        if (sender.IsRunning)
        {
            if (!firstCaptureCallbackLogged && sender.CaptureCallbacks > 0)
            {
                firstCaptureCallbackLogged = true;
                logFile.Event($"first capture callback received ({sender.CaptureBytes} bytes, format {sender.CaptureFormatDescription ?? "?"})");
            }
            if (!firstSenderPacketLogged && sender.PacketsSent > 0)
            {
                firstSenderPacketLogged = true;
                logFile.Event($"first packet sent ({sender.BytesSent} bytes total)");
            }
            // If capture isn't producing samples, repeat the warning every 5 s so it's visible.
            if (sender.CaptureCallbacks == 0 && DateTime.UtcNow - lastCaptureZeroLogUtc > TimeSpan.FromSeconds(5))
            {
                lastCaptureZeroLogUtc = DateTime.UtcNow;
                var err = sender.LastCaptureError;
                logFile.Event($"sender running but no capture callbacks yet (device=\"{sender.CaptureDeviceName}\", format=\"{sender.CaptureFormatDescription ?? "?"}\", error=\"{err ?? "none"}\")");
            }
        }
        else
        {
            firstCaptureCallbackLogged = false;
            firstSenderPacketLogged = false;
        }

        // Diag block runs if EITHER side is active. The original gate was `receiver.IsRunning`
        // only, which was correct for the typical bidirectional case but silently dropped the
        // diag line on send-only machines (no receiver bound, but the sender's capture-callback
        // gap is exactly what we want to log there). Adding `|| sender.IsRunning` lets the
        // send-only branch below actually emit.
        if (receiver.IsRunning || sender.IsRunning)
        {
            if (receiver.IsRunning && !firstReceiverPacketLogged && receiver.PacketsReceived > 0)
            {
                firstReceiverPacketLogged = true;
                logFile.Event($"first packet received ({receiver.BytesReceived} bytes total)");
            }

            // Sub-second diagnostics — tells us what's actually happening at audio-rate
            // resolution rather than guessing from a 1 Hz buffer reading. Look for:
            //   bufMin near 0 or  maxGapMs > 30  →  network burstiness or thread starvation
            //   bufAvg << target                 →  clock drift, adaptive rate should compensate
            //   inputRate drifting from 48000    →  adaptive rate is actively compensating
            //   maxReadMs much bigger than 15    →  WASAPI is gulping more than expected
            var diag = receiver.IsRunning ? receiver.TakeDiagnosticsSnapshot() : default;
            // Pull sendCbGapMs unconditionally so it always resets cleanly between log emissions.
            // We log it on whichever line we end up emitting — the receiver's diag line if the
            // receiver has activity, otherwise a sender-only line. Skipping the call when the
            // receiver is idle would leave the sender's max growing forever, never resetting.
            var sendCbGapMs = sender.TakeMaxCaptureCallbackGapMs();
            if (diag.BufferSampleCount > 0 || diag.RenderReadCount > 0)
            {
                // pcmRej / pcmDiscard let us see if PCM frames are being lost in assembly
                // (out-of-order parts, mismatched parts, partial frame discarded). Both are
                // cumulative since the stream session started — non-zero growing values during
                // a steady-state run indicate the network/USB stack is jumbling PCM packet pairs.
                // sendCbGapMs = sender's worst capture-callback gap since the last log.
                // High value here (e.g. > 10 ms with ASIO buffer ≤ 5 ms) means the LOCAL
                // capture path stalled — GC pause, USB driver hiccup, scheduler delay. The
                // emitted audio will contain a discontinuity at that moment, which the peer
                // can't detect (no packets lost, just audio with a hole). When this metric
                // and the receiver's own maxGapMs both spike together, suspect the network;
                // when only sendCbGapMs spikes, suspect this machine's audio stack.
                // renderCbGapMs = worst gap between consecutive audio-render callbacks on THIS
                // machine. Healthy = sub-ms variance from the audio buffer's natural period
                // (e.g. ~5 ms for a 256-sample ASIO buffer at 48 kHz). Spikes here mean Windows
                // scheduled the audio-output thread late, which causes the audio device's
                // hardware buffer to underrun even though RemSound's playout buffer was full —
                // RemSound's "Underruns" counter would NOT see this, so it can be the smoking
                // gun for clicks-with-everything-else-clean.
                //
                // Drop-cause split (Codex's catch — the legacy `Drops` rolled up several
                // unrelated mechanisms):
                //   trimB    = bytes deliberately dropped by the smoothness-knob click-trim
                //   trimN    = number of times that trim fired (so we can see frequency)
                //   drainB   = bytes dropped on a one-shot drain (knob change)
                //   ovfB     = ringbuffer-overflow / catastrophic-cap drops (everything else)
                //   pktRej   = malformed/unknown-type packets we rejected at the network edge
                var trimBytes = receiver.TrimDropBytes;
                var trimFires = receiver.TrimFireCount;
                var drainBytes = receiver.DrainDropBytes;
                var ovfBytes = receiver.RingbufferOverflowDropBytes;
                var pktRej = receiver.PacketsRejectedMalformed;
                // Diag legend (post-Phase-3 cleanup):
                //   driftDrop / driftRep = Phase-2 drift correction counters. Each event = one
                //     stereo frame dropped (sender clock faster) or repeated (sender clock
                //     slower) = 21 µs of audio at 48 kHz with crossfade smoothing, designed to
                //     be inaudible. Healthy: one or the other slowly climbing at a few/sec rate.
                //   trimB / trimN / drainB / ovfB = the click-trim safety net + drain on knob
                //     change + ringbuffer overflow. All should stay near zero in normal
                //     operation now that the drift corrector handles steady drift.
                //   spikesN = adaptive second-derivative outlier count. Music-content invariant.
                //     >0 = real anomalous samples in RemSound's output. ~0 = clean output.
                //   sampleStepMax = raw peak step magnitude (false-positive prone on bright
                //     music; informational only).
                // driftDrops / driftReps / driftAccumulator readings removed 2026-05-23 along
                // with their dead accessors. The Phase-4 fixed-ratio resampler design never
                // increments those counters; the columns were always zero. filteredErrorFrames
                // below is the still-useful "where the buffer is sitting on average" signal —
                // computed every Read by the active LP filter.
                var concealNow = receiver.ConcealmentFires;
                var shortReadNow = receiver.ShortReadFires;
                var concealDelta = concealNow - prevDiagConceal; prevDiagConceal = concealNow;
                var shortReadDelta = shortReadNow - prevDiagShortRead; prevDiagShortRead = shortReadNow;
                // Device-gulp short-reads this second — the inaudible, on-target partial reads the
                // cause-aware auto-tune ignores. High devGulpΔ alongside a near-zero concealΔ is the
                // onboard-Realtek chunky-render-callback fingerprint we built the split to catch.
                var deviceGulpNow = receiver.DeviceGulpUnderruns;
                var deviceGulpDeltaDiag = deviceGulpNow - prevDiagDeviceGulp; prevDiagDeviceGulp = deviceGulpNow;
                var trimDelta = trimFires - prevDiagTrimFires; prevDiagTrimFires = trimFires;
                // Live state — current LP-filtered drift error. Negative = buffer running below
                // target on average; positive = above.
                var filteredErrorFrames = receiver.FilteredDriftErrorFrames;
                // 2026-05-11 added timing-split metrics:
                //   emitMs    = sender's worst time-in-OnMixedSamples (encode + scratch + send)
                //   sndCallMs = sender's worst time-in-udp.Client.SendTo (kernel send only)
                //   rxDispMs  = receiver's worst time-in-onPacket dispatch (after kernel receive)
                // If observed maxGapMs is large but all three of these are sub-ms, the variance
                // is between sender SendTo-return and receiver ReceiveFrom-return — i.e. the
                // network or the kernel TX/RX path. If one of them spikes alongside maxGapMs,
                // that's where our code is taking the time.
                var emitMs = sender.TakeMaxEmitMs();
                var sendCallMs = sender.TakeMaxSendCallMs();
                var rxDispatchMs = receiver.TakeMaxOnPacketMs();
                // rxNetGapMs = worst inter-packet arrival gap at the user-space UDP socket.
                // Distinct from maxGapMs (which is measured at the per-stream-session level
                // after decode + assembly): this one is the raw "did ReceiveFrom return on
                // time" timing, with no per-session bookkeeping in between. A spike here
                // when the sender's sendCbGapMs is small fingers the OS/network path between
                // sender and receiver — NIC IRQ servicing, scheduler not waking the receive
                // thread, kernel batching, GC pause — rather than the sender stalling or
                // RemSound's own decode/dispatch chain. 2026-05-21.
                var rxNetGapMs = receiver.TakeMaxInterPacketGapMs();
                // fanCacheMs reading + column removed 2026-05-23. The FanOutSource was retired
                // mid-May (each lane reads its own filtered PlayoutEngine source directly); the
                // measurement always returned 0 and surfaced an unhelpful diag column.
                // GC pressure delta. .NET's GC.CollectionCount is cumulative; subtracting the
                // previous tick gives the per-second collection count per generation. Gen-0
                // collections are cheap (microseconds); Gen-1 takes longer; Gen-2 / LOH can
                // pause the runtime for many milliseconds, which is enough to explain a
                // 30–50 ms rxNetGapMs spike in isolation. Read directly here — GC.CollectionCount
                // is essentially free, no need to gate further. 2026-05-21.
                var gc0Now = GC.CollectionCount(0);
                var gc1Now = GC.CollectionCount(1);
                var gc2Now = GC.CollectionCount(2);
                var gc0Delta = gc0Now - prevDiagGc0Count; prevDiagGc0Count = gc0Now;
                var gc1Delta = gc1Now - prevDiagGc1Count; prevDiagGc1Count = gc1Now;
                var gc2Delta = gc2Now - prevDiagGc2Count; prevDiagGc2Count = gc2Now;
                // Process-wide self-meter (item 1 + 3 of RemSoundefficiency.md). Single
                // snapshot covers CPU%, managed heap MB, working set MB, allocation rate.
                var selfMeter = processSelfMeter.Take();
                // Per-thread work-time (item 2 of RemSoundefficiency.md). Each is the
                // milliseconds of CPU that thread (or thread group) consumed in the last
                // second; in a clean steady-state session they should all be small. The
                // four categories follow the request: capture, send, receive, render.
                // captureMs covers ASIO + WASAPI capture bodies and the MixingEngine tick;
                // sendMs is encode + sendto on the audio thread; recvMs is the network
                // thread's packet handler; renderMs is the audio render thread's mix +
                // limiter + pack work.
                var captureMs = sender.TakeCaptureWorkMs();
                var sendMs = sender.TakeSendWorkMs();
                var recvMs = receiver.TakeReceiveWorkMs();
                var renderMs = receiver.TakeRenderWorkMs();
                // Per-stage discontinuity probes. Compare these to localise where in the
                // pipeline a click is introduced:
                //   stepPreEnc   = sender's float buffer just before encoding. Non-zero =
                //                  the input ALREADY has discontinuities (capture-side issue).
                //   stepPostDec  = receiver's float buffer just after PCM/Opus decode. If this
                //                  is significantly larger than stepPreEnc, the wire codec
                //                  roundtrip introduced steps.
                //   stepPostRing = receiver's float buffer just out of the ring (before
                //                  resampler). Roughly equal to stepPostDec in steady state;
                //                  bigger here means the ring buffer is fishy.
                //   stepPostRsm  = receiver's float buffer just out of the resampler. Bigger
                //                  here than stepPostRing fingers the resampler integration.
                //   sampleStepMax= the final output buffer (after volume + limiter), the
                //                  legacy spot the diag already tracked.
                // Per-lane pre-encode probes (2026-05-15) — split so BothIndependent mode
                // can show which lane is producing the discontinuity, free of the cross-
                // stream artefact that the old shared probe registered when both lanes'
                // callbacks interleaved into one probe's lastL/R carry.
                //
                // 2026-05-21 — also surface the cross-buffer (boundary) vs within-buffer
                // (content) split for every probe. A non-zero combined step combined with a
                // near-zero within-buffer reading means the click is at a buffer / packet
                // boundary (lost or duplicated sample, pipeline glitch); a non-zero
                // within-buffer reading with a near-zero cross-buffer reading means it's a
                // sharp transient inside one buffer (real audio content, system sound). All
                // probe drains here go through the XB/WB pair and recompute the combined
                // max from the split values — calling Take*Step() AND the split methods on
                // the same probe in the same drain window would double-drain.
                var stepPreEncWasXB = sender.TakeMaxPreEncodeStepWasapiLaneCrossBuffer();
                var stepPreEncWasWB = sender.TakeMaxPreEncodeStepWasapiLaneWithinBuffer();
                var stepPreEncWas = stepPreEncWasXB > stepPreEncWasWB ? stepPreEncWasXB : stepPreEncWasWB;
                var stepPreEncAsiXB = sender.TakeMaxPreEncodeStepAsioLaneCrossBuffer();
                var stepPreEncAsiWB = sender.TakeMaxPreEncodeStepAsioLaneWithinBuffer();
                var stepPreEncAsi = stepPreEncAsiXB > stepPreEncAsiWB ? stepPreEncAsiXB : stepPreEncAsiWB;
                var stepPreEnc = stepPreEncWas > stepPreEncAsi ? stepPreEncWas : stepPreEncAsi;
                var stepRawCapXB = sender.TakeMaxSenderRawCaptureStepCrossBuffer();
                var stepRawCapWB = sender.TakeMaxSenderRawCaptureStepWithinBuffer();
                var stepRawCap = stepRawCapXB > stepRawCapWB ? stepRawCapXB : stepRawCapWB;
                var capPeak = sender.TakeMaxSenderPreEncodePeak();
                var sndAudFr = sender.TakeSenderAudioFramesSent();
                var clippedNow = sender.ClippedSampleCount;
                var clippedDelta = clippedNow - prevDiagClippedSamples; prevDiagClippedSamples = clippedNow;
                var stepPostDecXB = receiver.TakeMaxPostDecodeStepCrossBuffer();
                var stepPostDecWB = receiver.TakeMaxPostDecodeStepWithinBuffer();
                var stepPostDec = stepPostDecXB > stepPostDecWB ? stepPostDecXB : stepPostDecWB;
                var stepPostRingXB = receiver.TakeMaxPostRingReadStepCrossBuffer();
                var stepPostRingWB = receiver.TakeMaxPostRingReadStepWithinBuffer();
                var stepPostRing = stepPostRingXB > stepPostRingWB ? stepPostRingXB : stepPostRingWB;
                var stepPostRsmXB = receiver.TakeMaxPostResamplerStepCrossBuffer();
                var stepPostRsmWB = receiver.TakeMaxPostResamplerStepWithinBuffer();
                var stepPostRsm = stepPostRsmXB > stepPostRsmWB ? stepPostRsmXB : stepPostRsmWB;
                // Wire-level packet-sequence stats. wireInOrderΔ is the count of packets that
                // arrived with the sequence we expected this second. wireMissΔ / wireReordΔ /
                // wireDupΔ are the smoking-gun counters — any non-zero value here means the
                // UDP path between sender and receiver dropped, reordered, or duplicated
                // packets, and that on the PCM path translates directly into audible pops.
                var wireInOrderNow = receiver.WireInOrderCount;
                var wireMissedNow = receiver.WireMissedCount;
                var wireReorderedNow = receiver.WireReorderedCount;
                var wireDuplicatedNow = receiver.WireDuplicatedCount;
                var wireInOrderDelta = wireInOrderNow - prevDiagWireInOrder; prevDiagWireInOrder = wireInOrderNow;
                var wireMissedDelta = wireMissedNow - prevDiagWireMissed; prevDiagWireMissed = wireMissedNow;
                var wireReorderedDelta = wireReorderedNow - prevDiagWireReordered; prevDiagWireReordered = wireReorderedNow;
                var wireDuplicatedDelta = wireDuplicatedNow - prevDiagWireDuplicated; prevDiagWireDuplicated = wireDuplicatedNow;

                logFile.Event($"diag bufAvg={diag.BufferAvgMs}ms bufMin={diag.BufferMinMs}ms bufMax={diag.BufferMaxMs}ms " +
                    $"maxGapMs={diag.MaxArrivalGapMs} sendCbGapMs={sendCbGapMs} renderCbGapMs={diag.MaxRenderCallbackGapMs} maxReadMs={diag.MaxRenderReadMs} reads={diag.RenderReadCount} " +
                    $"emitMs={emitMs} sndCallMs={sendCallMs} rxDispMs={rxDispatchMs} rxNetGapMs={rxNetGapMs} " +
                    $"gc0Δ={gc0Delta} gc1Δ={gc1Delta} gc2Δ={gc2Delta} " +
                    $"cpu={selfMeter.CpuPercentOneCore:0.0}% memMB={selfMeter.ManagedHeapMb:0.0} wsMB={selfMeter.WorkingSetMb:0.0} allocKBps={selfMeter.AllocatedKbPerSecond:0.0} " +
                    $"privMB={selfMeter.PrivateBytesMb:0.0} gcHeapMB={selfMeter.GcHeapMb:0.0} gcFragMB={selfMeter.GcFragmentedMb:0.0} gcCommitMB={selfMeter.GcCommittedMb:0.0} handles={selfMeter.HandleCount} threads={selfMeter.ThreadCount} " +
                    $"captureMs={captureMs:0.0} sendMs={sendMs:0.0} recvMs={recvMs:0.0} renderMs={renderMs:0.0} " +
                    $"trimB={trimBytes} trimN={trimFires} trimΔ={trimDelta} drainB={drainBytes} ovfB={ovfBytes} pktRej={pktRej} " +
                    $"concealΔ={concealDelta} shortReadΔ={shortReadDelta} devGulpΔ={deviceGulpDeltaDiag} " +
                    $"filtErr={filteredErrorFrames:0.0}f " +
                    $"capPeak={capPeak:0.000} sndAudFrΔ={sndAudFr} stepRawCap={stepRawCap:0.000} stepPreEnc={stepPreEnc:0.000} stepPreEncWas={stepPreEncWas:0.000} stepPreEncAsi={stepPreEncAsi:0.000} stepPostDec={stepPostDec:0.000} stepPostRing={stepPostRing:0.000} stepPostRsm={stepPostRsm:0.000} " +
                    $"stepRawCapXB={stepRawCapXB:0.000} stepRawCapWB={stepRawCapWB:0.000} " +
                    $"stepPreEncWasXB={stepPreEncWasXB:0.000} stepPreEncWasWB={stepPreEncWasWB:0.000} " +
                    $"stepPreEncAsiXB={stepPreEncAsiXB:0.000} stepPreEncAsiWB={stepPreEncAsiWB:0.000} " +
                    $"stepPostDecXB={stepPostDecXB:0.000} stepPostDecWB={stepPostDecWB:0.000} " +
                    $"stepPostRingXB={stepPostRingXB:0.000} stepPostRingWB={stepPostRingWB:0.000} " +
                    $"stepPostRsmXB={stepPostRsmXB:0.000} stepPostRsmWB={stepPostRsmWB:0.000} " +
                    $"clipΔ={clippedDelta} sampleStepMax={diag.MaxOutputSampleStep:0.000} spikesN={diag.EnvelopeSpikeCount} " +
                    $"wireOkΔ={wireInOrderDelta} wireMissΔ={wireMissedDelta} wireReordΔ={wireReorderedDelta} wireDupΔ={wireDuplicatedDelta} " +
                    $"pcmRej={receiver.PcmFrameRejections} pcmDiscard={receiver.PcmFrameDiscardedPartials}");
            }
            else if (sender.IsRunning)
            {
                // Send-only machine (no receive output ticked). Emit a sender-side diag line so
                // sendCbGapMs is visible — that's the most important metric on a send-only box,
                // since it tells us whether THIS machine's capture path is stalling. Without
                // this branch, send-only sessions logged zero diag info.
                // stepPreEnc included so the send-only machine's pre-encode discontinuity
                // probe is visible — needed for the laptop→desktop direction where the laptop
                // is the source and we want to see if the audio coming OUT of the capture
                // already has steps before it touches the wire.
                var emitMs = sender.TakeMaxEmitMs();
                var sendCallMs = sender.TakeMaxSendCallMs();
                // Per-lane pre-encode probes — see the full-diag comment above for the
                // rationale (per-lane fixes the cross-stream artefact in BothIndependent).
                // 2026-05-21: drain XB / WB separately so we can localise click events at
                // the buffer boundary (cross-buffer) vs within-buffer (real content). The
                // combined step is just the larger of the two for back-compat readers.
                var stepPreEncWasXB = sender.TakeMaxPreEncodeStepWasapiLaneCrossBuffer();
                var stepPreEncWasWB = sender.TakeMaxPreEncodeStepWasapiLaneWithinBuffer();
                var stepPreEncWas = stepPreEncWasXB > stepPreEncWasWB ? stepPreEncWasXB : stepPreEncWasWB;
                var stepPreEncAsiXB = sender.TakeMaxPreEncodeStepAsioLaneCrossBuffer();
                var stepPreEncAsiWB = sender.TakeMaxPreEncodeStepAsioLaneWithinBuffer();
                var stepPreEncAsi = stepPreEncAsiXB > stepPreEncAsiWB ? stepPreEncAsiXB : stepPreEncAsiWB;
                var stepPreEnc = stepPreEncWas > stepPreEncAsi ? stepPreEncWas : stepPreEncAsi;
                // Raw-capture step: now per-backend (each backend owns its own probe). The
                // accessor returns max across all backends. PushModeWasapiBackend has been
                // wired to feed this probe as of 2026-05-15; pull-mode MixingEngine returns 0.
                var stepRawCapXB = sender.TakeMaxSenderRawCaptureStepCrossBuffer();
                var stepRawCapWB = sender.TakeMaxSenderRawCaptureStepWithinBuffer();
                var stepRawCap = stepRawCapXB > stepRawCapWB ? stepRawCapXB : stepRawCapWB;
                var clippedNow = sender.ClippedSampleCount;
                var clippedDelta = clippedNow - prevDiagClippedSamples; prevDiagClippedSamples = clippedNow;
                // Per-second GC delta on the send-only side too. A send stall caused by a
                // gen-2 pause on the SENDER would have a different signature in the SNAP
                // log than one caused by a receive-side pause — they'd show up here even
                // though no receiver activity is happening on this machine.
                var gc0Now = GC.CollectionCount(0);
                var gc1Now = GC.CollectionCount(1);
                var gc2Now = GC.CollectionCount(2);
                var gc0Delta = gc0Now - prevDiagGc0Count; prevDiagGc0Count = gc0Now;
                var gc1Delta = gc1Now - prevDiagGc1Count; prevDiagGc1Count = gc1Now;
                var gc2Delta = gc2Now - prevDiagGc2Count; prevDiagGc2Count = gc2Now;
                // Process self-meter + per-thread work-time on the send-only side too.
                // captureMs covers the WASAPI / ASIO callback bodies; sendMs is the encode
                // + sendto work; recvMs / renderMs stay at 0 (no playback on this machine
                // by definition for the send-only branch). See item 1, 2, 3 of
                // RemSoundefficiency.md.
                var selfMeter = processSelfMeter.Take();
                var captureMs = sender.TakeCaptureWorkMs();
                var sendMs = sender.TakeSendWorkMs();
                // capPeak = loudest sample reaching the encoder; sndAudFrΔ = audio frames that
                // actually left the socket this second. Together they split "mic captured but
                // nothing sent" (drop at encode/encrypt) from "mic captured and sent" — the
                // measurement the "mic only works in ASIO" report needs, now on the talker side too.
                var capPeak = sender.TakeMaxSenderPreEncodePeak();
                var sndAudFr = sender.TakeSenderAudioFramesSent();
                logFile.Event(
                    $"sender-diag sendCbGapMs={sendCbGapMs} emitMs={emitMs} sndCallMs={sendCallMs} " +
                    $"capPeak={capPeak:0.000} sndAudFrΔ={sndAudFr} " +
                    $"stepPreEnc={stepPreEnc:0.000} stepPreEncWas={stepPreEncWas:0.000} stepPreEncAsi={stepPreEncAsi:0.000} stepRawCap={stepRawCap:0.000} " +
                    $"stepRawCapXB={stepRawCapXB:0.000} stepRawCapWB={stepRawCapWB:0.000} " +
                    $"stepPreEncWasXB={stepPreEncWasXB:0.000} stepPreEncWasWB={stepPreEncWasWB:0.000} " +
                    $"stepPreEncAsiXB={stepPreEncAsiXB:0.000} stepPreEncAsiWB={stepPreEncAsiWB:0.000} " +
                    $"gc0Δ={gc0Delta} gc1Δ={gc1Delta} gc2Δ={gc2Delta} " +
                    $"cpu={selfMeter.CpuPercentOneCore:0.0}% memMB={selfMeter.ManagedHeapMb:0.0} wsMB={selfMeter.WorkingSetMb:0.0} allocKBps={selfMeter.AllocatedKbPerSecond:0.0} " +
                    $"privMB={selfMeter.PrivateBytesMb:0.0} gcHeapMB={selfMeter.GcHeapMb:0.0} gcFragMB={selfMeter.GcFragmentedMb:0.0} gcCommitMB={selfMeter.GcCommittedMb:0.0} handles={selfMeter.HandleCount} threads={selfMeter.ThreadCount} " +
                    $"captureMs={captureMs:0.0} sendMs={sendMs:0.0} " +
                    $"clipΔ={clippedDelta} packets={sender.PacketsSent} captureCallbacks={sender.CaptureCallbacks}");
            }

            // Synthesised end-to-end one-way latency estimate. Sums:
            //   * sender_accumulator: half the codec frame size (avg packet wait)
            //   * wire_one_way: lowest active peer's heartbeat RTT / 2
            //   * receiver_queue: bufAvg from diag (the real measured queue depth, 0 on send-only)
            //   * render_buffer: rough estimate per audio mode
            // Logged whenever either side is active so we capture the latency picture even when
            // the local machine is send-only.
            if ((diag.BufferSampleCount > 0 || diag.RenderReadCount > 0) || sender.IsRunning)
            {
                var senderAccumulatorMs = SenderAccumulatorEstimateMs();
                var wireOneWayMs = LowestPeerRttMs() / 2.0;
                var renderBufferMs = RenderBufferEstimateMs();
                var totalMs = senderAccumulatorMs + wireOneWayMs + diag.BufferAvgMs + renderBufferMs;
                logFile.Event($"latency-probe estimated one-way ≈ {totalMs:0.0}ms " +
                    $"(send-accum={senderAccumulatorMs:0.0}, wire={wireOneWayMs:0.0}, recv-queue={diag.BufferAvgMs}, render={renderBufferMs:0.0})");
            }

            // If a new stream session opened since the last SNAP tick, flush the gap windows.
            // The diag.MaxArrivalGapMs we're about to enqueue is bounded inside this tick by
            // ReceiverDiagnostics.ResetGapMeasurements() (called from AudioReceiver when the
            // session opens), but any previously-queued entries are stale relative to the new
            // session. Bumping lastSourceChangeUtc also makes the auto-tune defer for one
            // interval, letting the new session's measurements populate the window before any
            // recommendation fires.
            var openCount = receiver.SessionsOpenedCount;
            if (openCount > lastObservedSessionsOpenedCount)
            {
                lastObservedSessionsOpenedCount = openCount;
                recentMaxGaps.Clear();
                recentRenderCbGaps.Clear();
                lastSourceChangeUtc = DateTime.UtcNow;
            }

            // Push this second's max-gap reading into the rolling window the continuous
            // auto-tune samples from. Capped at RecentMaxGapWindowSeconds entries so older
            // readings naturally fall out as conditions evolve.
            if (diag.PacketCount > 0)
            {
                recentMaxGaps.Enqueue(diag.MaxArrivalGapMs);
                while (recentMaxGaps.Count > RecentMaxGapWindowSeconds) recentMaxGaps.Dequeue();
                // Mirror window for actual render-callback period. Same windowing so they age
                // out together; auto-tune uses the max of this for an honest formula.
                recentRenderCbGaps.Enqueue(diag.MaxRenderCallbackGapMs);
                while (recentRenderCbGaps.Count > RecentMaxGapWindowSeconds) recentRenderCbGaps.Dequeue();
            }
        }
        else
        {
            firstReceiverPacketLogged = false;
        }
    }

    private void AppendLogEntry(string message)
    {
        // No on-form log box now (kept just-in-status-line). Leaving this method to make the call sites
        // future-proof; if we re-add a visible log box, AppendLogEntry is the single hook point.
        logFile.Event(message);
    }

    // ===================== Tray =====================

    private void ToggleTrayFromHotkey()
    {
        BeginInvoke(() => trayController.Toggle());
    }

    /// <summary>
    /// Most Alt+letter shortcuts are wired via the WinForms `&amp;` mnemonic on the relevant
    /// control's Text (Buttons, CheckBoxes) or its paired Label (ListBoxes, NumericUpDowns,
    /// ComboBoxes — see <see cref="MnemonicLabel"/> for the label-→target dispatch). The
    /// framework's built-in ProcessMnemonic walk handles those automatically: when the user
    /// presses Alt+letter, only controls on the visible tab respond, which gives us per-tab
    /// shortcut isolation as a free side-effect of how WinForms scopes mnemonics.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Ctrl+1..9 — switch to the main tab at that position. Positions are LIVE: they shift as the
        // user reorders tabs (Preferences → Appearance) and the pan/EQ tab may be hidden, so the number
        // always means "the Nth tab as it currently appears". Same idea as Andre's readout app.
        if ((keyData & Keys.Modifiers) == Keys.Control)
        {
            int tabNum = (keyData & Keys.KeyCode) switch
            {
                >= Keys.D1 and <= Keys.D9 => (keyData & Keys.KeyCode) - Keys.D1 + 1,
                >= Keys.NumPad1 and <= Keys.NumPad9 => (keyData & Keys.KeyCode) - Keys.NumPad1 + 1,
                _ => 0,
            };
            if (tabNum >= 1 && tabNum <= mainTabControl.TabPages.Count)
            {
                mainTabControl.SelectedIndex = tabNum - 1;
                mainTabControl.Focus();   // focus the tab strip so NVDA reads the new tab, like Ctrl+Tab
                return true;
            }
        }

        // Defensive gate for the global menu shortcuts that change state (Ctrl+R = toggle
        // recording, Ctrl+S = save profile). The default WinForms behaviour fires these
        // shortcuts any time the form has keyboard focus — which technically includes the
        // case where another tool (NVDA Remote in send-keys mode, an automation script,
        // etc.) calls SetForegroundWindow on us and then SendInput a keystroke a few
        // milliseconds later. The form receives focus + the keystroke arrives + the menu
        // shortcut fires, all without the user touching anything.
        //
        // The gate adds two extra requirements before we let these shortcuts run:
        //   1. The OS-level foreground window must be us. Same check the base class
        //      effectively makes, but explicit so the intent is documented.
        //   2. At least RecentActivationGuardMs must have elapsed since we last became
        //      activated. Programmatic SetForegroundWindow + SendInput typically runs in
        //      under 50 ms; a human Alt+Tabbing in then pressing Ctrl+R can't physically
        //      do it inside 250 ms.
        // If the gate fails we consume the keystroke (return true) so the menu shortcut
        // doesn't fire, log a diagnostic, and silently ignore it. The user can still drive
        // the same actions via the Alt+R / Alt+F menu chord which inherently requires the
        // multi-step menu-open interaction and isn't vulnerable to drive-by injection.
        if (keyData == (Keys.Control | Keys.R) || keyData == (Keys.Control | Keys.S))
        {
            if (!IsWindowAvailableForGatedShortcut())
            {
                logFile.Event($"shortcut ignored (window not in interactive state): {keyData}");
                return true; // consumed; don't let MenuStrip see it
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // UTC time the form last became activated. Compared against UtcNow when a gated
    // shortcut fires to reject keystrokes that arrive within the RecentActivationGuardMs
    // window after a window-activation — the signature of a drive-by injection.
    private DateTime lastActivatedAtUtc = DateTime.MinValue;
    private const int RecentActivationGuardMs = 250;

    protected override void OnActivated(EventArgs e)
    {
        lastActivatedAtUtc = DateTime.UtcNow;
        base.OnActivated(e);
    }

    /// <summary>Defensive gate for global menu shortcuts that change state. See the comment
    /// in <see cref="ProcessCmdKey"/> for the full rationale.</summary>
    private bool IsWindowAvailableForGatedShortcut()
    {
        if (!Visible || WindowState == FormWindowState.Minimized) return false;
        if ((DateTime.UtcNow - lastActivatedAtUtc).TotalMilliseconds < RecentActivationGuardMs) return false;
        return GetForegroundWindow() == Handle;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>Bring this window to the very front and give it focus, robust against Windows'
    /// foreground lock. A freshly-launched process (the post-install relaunch) has no recent user
    /// input to its name, so a bare <c>SetForegroundWindow</c> is refused and the window opens behind
    /// whatever's on top. Briefly attaching our input queue to the current foreground thread lets the
    /// call through; the TopMost blip nudges the Z-order too. Best-effort — never throws.</summary>
    private void ForceWindowToForeground()
    {
        try
        {
            if (IsDisposed) return;
            // Capture who's in front FIRST. If we called Show()/Activate() before reading this,
            // GetForegroundWindow could already return our own handle — and then we'd skip the
            // thread-attach below, which is the part that actually lets SetForegroundWindow win.
            var fgWnd = GetForegroundWindow();
            var myThread = GetCurrentThreadId();
            var fgThread = fgWnd == IntPtr.Zero ? myThread : GetWindowThreadProcessId(fgWnd, out _);

            // Share input state with whatever currently owns the foreground, so Windows treats our
            // SetForegroundWindow as coming from the active thread and allows it past the lock.
            var attached = fgThread != myThread && AttachThreadInput(fgThread, myThread, true);
            try
            {
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                Show();
                BringToFront();
                SetForegroundWindow(Handle);
                Activate();
                Focus();
            }
            finally
            {
                if (attached) AttachThreadInput(fgThread, myThread, false);
            }
        }
        catch { /* best-effort — worst case the window is visible but not focused */ }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);


    // ===================== Profile system =====================

    /// <summary>Called once from Shown after device lists are populated. Applies the
    /// control-state portion of the loaded profile (device ticks, send/receive checkboxes,
    /// volume) — settings-shaped fields were applied earlier in the constructor via
    /// settings.ApplyProfile(). Devices in the profile that don't exist on this machine
    /// are silently skipped (the matching CheckedListBox simply won't have them ticked).</summary>
    private void ApplyPendingProfileToControls()
    {
        if (pendingProfile is null) return;
        var p = pendingProfile;
        applyingProfile = true;
        // Silence the generic checkbox tick/untick for the whole bulk apply. The per-box Focused
        // gate isn't enough on launch: the box we tick in code is often the focused control, so it
        // would click. This + the applyingProfile guard on the send/receive cue keep a profile load
        // down to just the startup and (real) connect cues.
        CheckSoundService.Suppressed = true;
        try
        {
            // Volume first — affects what's audible during the rest of this method.
            volumeBar.Value = Math.Clamp(p.Volume, volumeBar.Minimum, volumeBar.Maximum);
            // Push volume + mute to the engine. Assigning .Value does NOT fire the Scroll handler, so
            // without this a profile saved at e.g. 50% would show 50% but play at full volume until
            // the slider was nudged. Mute is restored from the saved state for the same reason.
            receiver.Volume = volumeBar.Value / 100f;
            receiver.IsMuted = p.Muted;

            // Tick checkboxes. Order matters: setting Checked fires runtime apply paths
            // (Connect/Disconnect) so the side-effect cascade has to happen here, not in
            // the constructor where the engines aren't fully wired up yet.
            ApplyTicksToList(receiveOutputDevicesList, p.SelectedWasapiReceiveOutputs);
            // Seed remembered receive-output intent from the profile so a selected card that's
            // absent now (or unplugged later) is re-ticked + re-opened when it appears (issue #5).
            rememberedReceiveOutputIds.Clear();
            foreach (var rid in p.SelectedWasapiReceiveOutputs) rememberedReceiveOutputIds.Add(rid);
            ApplyTicksToList(asioReceiveOutputDevicesList, p.SelectedAsioReceiveOutputs);
            ApplyTicksToList(sendOutputDevicesList, p.SelectedWasapiSendOutputs);
            ApplyTicksToList(sendInputDevicesList, p.SelectedWasapiSendInputs);
            ApplyTicksToList(asioSendDevicesList, p.SelectedAsioSendInputs);
            RestoreSendModeFromProfile(p);

            receiveAudioCheckbox.Checked = p.ReceiveAudioOn;
            sendMyAudioCheckbox.Checked = p.SendAudioOn;

            // Re-establish previously-connected peers. Each entry is re-resolved + re-selected
            // exactly as if the user had typed it into the manual-peer field. Discovered peers
            // (no longer reachable / different IP) just fail gracefully — no popup.
            ReconnectSavedPeers(p.SelectedConnectedPeers);
            // Per-peer pan/EQ: adopt this profile's saved shaping + the two master enables. The peer
            // picker and the DSP re-apply on the next tick (once the peers above have reconnected);
            // clearing the signature makes RefreshPanEqPeerList rebuild and re-push for this profile.
            peerShaping = p.PeerShaping is null ? new() : new(p.PeerShaping);
            loadingPanEqControls = true;
            // Single master switch now. Migrate older profiles: either legacy flag being on turns it on.
            try { enableAllPeerShapingBox.Checked = p.EnableAllPeerShaping || p.EnablePanForPeers || p.EnableEqForPeers; }
            finally { loadingPanEqControls = false; }
            lastPanEqPeerSignature = "";
        }
        catch (Exception ex)
        {
            AppendLogEntry($"profile apply: error applying \"{p.Title}\": {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // Don't re-apply on subsequent device-list refreshes. The user's later ticks are
            // captured by save-profile from current control state; we don't keep pulling from
            // the original profile forever.
            pendingProfile = null;
            applyingProfile = false;
            CheckSoundService.Suppressed = false;
        }
    }

    /// <summary>Tick the items in <paramref name="list"/> whose DeviceId appears in
    /// <paramref name="wantedIds"/>. Items not in the wanted set are unticked. Items in the
    /// wanted set that don't exist on this machine are silently dropped (this is how the
    /// profile system handles missing-hardware portability).</summary>
    private static void ApplyTicksToList(CheckedListBox list, IReadOnlyList<string> wantedIds)
    {
        if (list.Items.Count == 0) return;
        var wanted = new HashSet<string>(wantedIds, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < list.Items.Count; i++)
        {
            if (list.Items[i] is not AudioDeviceChoice choice || choice.DeviceId is null) continue;
            var shouldBeChecked = wanted.Contains(choice.DeviceId);
            if (list.GetItemChecked(i) != shouldBeChecked)
            {
                list.SetItemChecked(i, shouldBeChecked);
            }
        }
    }

    /// <summary>Window title shows the active profile name explicitly so the user knows what
    /// they're editing. Format: "RemSound — Active profile: My profile name" (loaded) or
    /// "RemSound — New profile" (a fresh, unsaved session). Read-only profiles get a " (read-only)" suffix so
    /// NVDA announces the lock state on every title change and sighted users see it at a
    /// glance — important context that "anything I change here won't be saved".</summary>
    private string FormatWindowTitle(string? loadedTitle)
    {
        var readOnlySuffix = currentProfileReadOnly ? " (read-only)" : "";
        return string.IsNullOrEmpty(loadedTitle)
            ? $"{AppName} — New profile{readOnlySuffix}"
            : $"{AppName} — Active profile: {loadedTitle}{readOnlySuffix}";
    }

    /// <summary>Update existing profile button. Overwrites the active profile with current
    /// state. No prompt — user explicitly chose this button to commit. Hidden when no
    /// profile is loaded.</summary>
    private void UpdateExistingProfile()
    {
        if (profileStore is null || string.IsNullOrEmpty(currentProfileTitle))
        {
            // Defensive — button should be hidden in this case.
            return;
        }
        SaveProfileTo(currentProfileTitle);
    }

    /// <summary>Save profile as button. Always prompts for a (new) name. From a blank
    /// template this is the only way to create the first profile; from a loaded profile this
    /// forks a copy under a new name and switches to that copy as the active profile.</summary>
    private void SaveProfileAs()
    {
        if (profileStore is null)
        {
            MessageBox.Show(this, "Profile system not active in this run.", "RemSound",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Real Windows Save As dialog (2026-05-10) — picks an arbitrary path with the standard
        // filename + folder picker, instead of the previous text-only "Profile name" prompt.
        // Default folder is the active profiles folder. Saving inside that folder produces a
        // profile that's loadable from File → Open profile next launch; saving outside is an
        // export the user is responsible for managing (RemSound only auto-discovers profiles
        // in AppConfig.ProfilesDirectory, so external saves don't appear in the picker).
        using var dialog = new SaveFileDialog
        {
            Title = "Save profile as",
            Filter = "RemSound profiles (*.json)|*.json",
            DefaultExt = "json",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = profileStore.BaseDirectory,
            FileName = string.IsNullOrEmpty(currentProfileTitle) ? "" : currentProfileTitle + ".json",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var path = dialog.FileName;
        var title = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(title)) return;

        try
        {
            var profile = BuildCurrentProfile(title);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);

            currentProfileTitle = title;
            currentProfilePath = path;
            // Save As always produces an editable copy — even if the source profile was
            // read-only. Anything else would be surprising: the user picked Save As
            // specifically to fork, and they reasonably expect the fork to be editable
            // without having to hunt for the menu toggle. The original (locked) profile on
            // disk is untouched; this is purely about the new file and the in-memory state.
            currentProfileReadOnly = false;
            if (lockProfileMenuItem is not null)
            {
                suppressLockProfileToggleHandler = true;
                try { lockProfileMenuItem.Checked = false; }
                finally { suppressLockProfileToggleHandler = false; }
            }
            Text = FormatWindowTitle(title);
            AccessibleName = Text;
            AppendLogEntry($"profile saved: \"{title}\" → {path}");
            unsavedChanges = false;
            // A freshly created profile has no password yet, and encryption is always on — so
            // ask for one now and write it straight into the file we just saved. OK requires a
            // non-empty password (requireNonEmpty); Cancel still leaves it passwordless and the
            // streaming gate will ask again when needed.
            if (string.IsNullOrEmpty(currentProfilePassword))
            {
                var pw = ProfilePasswordDialog.Show(title, "", requireNonEmpty: true);
                if (!string.IsNullOrEmpty(pw))
                {
                    currentProfilePassword = pw;
                    RecomputeAudioCrypto();
                    PersistPasswordOnly(pw);
                    AppendLogEntry($"profile password set on creation for \"{title}\"");
                }
            }
            // No confirmation popup here. The Save-As dialog the user just dismissed is itself
            // the explicit, user-driven "I am saving to this path" — a follow-up "Saved." popup
            // is pure friction (one more Enter press, one more NVDA read of the same fact).
            // The window title updates to the new name, the file appears on disk, and the
            // baseline diff resets — all the silent affordances the user actually needs.
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save profile: {ex.Message}", "RemSound",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Build a Profile POCO from the current MainForm control state. Used by the
    /// Save / Save as / SerializeCurrentStateAsProfile paths so the snapshotting logic lives
    /// in one place.</summary>
    private Profile BuildCurrentProfile(string title)
    {
        var profile = new Profile { Title = title };
        settings.CopyTo(profile);
        // Carry the active read-only (lock) flag into the saved snapshot. Without this line a
        // deliberate save of a locked profile — e.g. the "save through the lock" flow Andre
        // uses — would write the profile back with ReadOnly defaulting to false, silently
        // UNLOCKING it on disk. Next launch the profile would no longer be read-only, the
        // unsaved-changes prompt would start firing again, and (worse) that prompt could block
        // an unattended auto-update from restarting. The lock flag must survive every save.
        profile.ReadOnly = currentProfileReadOnly;
        // Likewise the encryption password (stored scrambled) — carried through every save so a
        // routine Save never wipes it (same bug class the ReadOnly line above fixes).
        profile.Password = RemSoundCrypto.Obfuscate(currentProfilePassword);
        profile.Volume = volumeBar.Value;
        profile.Muted = receiver.IsMuted;
        profile.ReceiveAudioOn = receiveAudioCheckbox.Checked;
        profile.SendAudioOn = sendMyAudioCheckbox.Checked;
        profile.SelectedWasapiReceiveOutputs = ExtractCheckedDeviceIds(receiveOutputDevicesList);
        profile.SelectedAsioReceiveOutputs = ExtractCheckedDeviceIds(asioReceiveOutputDevicesList);
        profile.SelectedWasapiSendOutputs = ExtractCheckedDeviceIds(sendOutputDevicesList);
        profile.SelectedWasapiSendInputs = ExtractCheckedDeviceIds(sendInputDevicesList);
        profile.SelectedAsioSendInputs = ExtractCheckedDeviceIds(asioSendDevicesList);
        // WASAPI send mode (whole devices vs specific applications) — persisted per profile.
        profile.WasapiSendMode = sendModeList.SelectedIndex == SendModeApplicationsIndex ? "applications" : "devices";
        // SendAllApplications is deliberately NOT written here — the main window no longer has that
        // concept (Ed removed it 2026-07-16). The field itself stays on Profile for the SERVICE, whose
        // headless lock-screen use case is exactly whole-system audio.
        profile.SelectedSendApplications = CheckedSendApplicationNames();
        profile.SelectedConnectedPeers = GatherSelectedPeerEntries();
        profile.EnableAllPeerShaping = enableAllPeerShapingBox.Checked;
        profile.PeerShaping = peerShaping;
        return profile;
    }

    /// <summary>Test-only: push a profile INTO the real controls and read it straight back OUT, so a
    /// self-test can prove every persisted control both loads and saves correctly. Headless forms only
    /// (a real form would try to reconnect peers etc.); pass a profile with no peers.</summary>
    internal Profile ApplyThenCaptureForTest(Profile input)
    {
        pendingProfile = input;
        settings.ApplyProfile(input);
        // Suppress the interactive "set a password to stream" gate that a real user apply would show —
        // it would pop a modal dialog and hang the headless test. We only care about control round-trip.
        suppressStreamingPasswordGate = true;
        try { ApplyPendingProfileToControls(); }
        finally { suppressStreamingPasswordGate = false; }
        return BuildCurrentProfile(input.Title);
    }

    /// <summary>Common save body — gathers all current state into a Profile and writes it.
    /// On success, becomes the active profile (sets currentProfileTitle, updates window
    /// title, refreshes button visibility, and shows a confirmation popup).</summary>
    private void SaveProfileTo(string title) => SaveProfileTo(title, showConfirmation: true);

    private void SaveProfileTo(string title, bool showConfirmation) => SaveProfileTo(title, showConfirmation, playCue: true);

    private void SaveProfileTo(string title, bool showConfirmation, bool playCue)
    {
        if (profileStore is null) return;
        try
        {
            SaveCurrentStateToProfileFile(title);
            AppendLogEntry($"profile saved: \"{title}\"");
            // Save cue (2026-05-28): fires after any successful save — Save AND Save As, since
            // both routes funnel through this single method. Honours the EnableSaveCue per-
            // profile flag; the cue is silent if the user has unticked it in Preferences or if
            // sounds\save.wav doesn't exist and no custom override has been set. Auto-save passes
            // playCue: false so it never interrupts the user with a save sound.
            if (playCue && settings.LoadEnableSaveCue()) saveSound?.Play();
            unsavedChanges = false;
            if (showConfirmation && !AppConfig.Load().SaveProfileConfirmationSuppressed)
            {
                // Explicit confirmation. Without this the only feedback is the silent
                // baseline-diff reset; sighted users miss it, screen-reader users only catch
                // it on the next focus event. TaskDialog (not MessageBox) so we can attach a
                // "Do not show me this again" verification checkbox — NVDA reads the checkbox
                // as part of the dialog tab order, and once ticked the preference persists in
                // remsound.config.json. Suppressed entirely when invoked from the close-
                // confirmation flow (the user already confirmed save+exit; extra Enter = friction).
                ShowSaveConfirmationDialog(title);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save profile: {ex.Message}", "RemSound",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Applies the "auto save non-read only profiles" preference to the timer: stops it when the
    /// setting is Never (0 minutes), otherwise sets the interval and starts it. Called at launch and again
    /// whenever the user changes the setting in Preferences, so the change takes effect immediately.</summary>
    internal void ApplyAutoSaveTimer() => ApplyAutoSaveTimer(AppConfig.Load().AutoSaveNonReadOnlyMinutes);

    /// <summary>Overload taking the interval directly, so a self-test can drive it without touching the
    /// real config.</summary>
    internal void ApplyAutoSaveTimer(int minutes)
    {
        autoSaveTimer.Stop();
        if (minutes <= 0) return; // Never
        autoSaveTimer.Interval = minutes * 60 * 1000;
        autoSaveTimer.Start();
    }

    /// <summary>Timer tick: silently save the active profile, but ONLY if it's a real saved profile, is NOT
    /// read-only, and actually has unsaved changes. Uses the shared save path with the cue and the
    /// confirmation dialog suppressed, so it's completely unobtrusive — no sound, no popup.</summary>
    private void AutoSaveCurrentProfileIfDue()
    {
        if (!ShouldAutoSave(profileStore is not null, currentProfileTitle, currentProfileReadOnly, unsavedChanges)) return;
        SaveProfileTo(currentProfileTitle!, showConfirmation: false, playCue: false);
    }

    /// <summary>Pure guard for the periodic auto-save (unit-testable). Only a real, saved profile that is
    /// NOT read-only and has unsaved changes may be auto-saved — a blank template, a read-only profile, or
    /// an unchanged one is left alone.</summary>
    internal static bool ShouldAutoSave(bool hasStore, string? currentTitle, bool readOnly, bool dirty)
        => hasStore && !string.IsNullOrEmpty(currentTitle) && !readOnly && dirty;

    // Test seams for the auto-save timer (headless): confirm ApplyAutoSaveTimer turns it on/off and sets
    // the interval from AppConfig.AutoSaveNonReadOnlyMinutes.
    internal bool AutoSaveTimerEnabledForTest => autoSaveTimer.Enabled;
    internal int AutoSaveTimerIntervalForTest => autoSaveTimer.Interval;

    /// <summary>Builds a Profile from the current control state and writes it via the store.
    /// Doesn't touch UI feedback — that's the caller's job. Throws on store failure.</summary>
    private void SaveCurrentStateToProfileFile(string title)
    {
        if (profileStore is null) return;
        var profile = BuildCurrentProfile(title);
        // If the active profile has a tracked path (set by Save As or by startup load),
        // write to that exact location — even if it's outside BaseDirectory. Otherwise
        // (no path tracked, e.g. blank-template-direct-save edge case) fall through to the
        // store's BaseDirectory-relative save.
        if (!string.IsNullOrEmpty(currentProfilePath))
        {
            var dir = Path.GetDirectoryName(currentProfilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(currentProfilePath, json);
        }
        else
        {
            profileStore.Save(profile);
            currentProfilePath = profileStore.PathFor(title);
        }
        currentProfileTitle = title;
        Text = FormatWindowTitle(title);
        AccessibleName = Text;
    }

    /// <summary>Mark the profile as having unsaved user changes. No-op while a profile is
    /// being applied programmatically (otherwise loading a profile would immediately mark
    /// itself dirty). Hooked from peer (de)selection plus a few other key paths; the close
    /// path also does a JSON-state diff as a safety net to catch settings we forgot to hook.</summary>
    private void MarkProfileDirty()
    {
        if (applyingProfile) return;
        unsavedChanges = true;
    }

    /// <summary>Handle the user ticking / unticking File → Lock profile (read-only). Updates
    /// the in-memory flag, refreshes the window title's "(read-only)" suffix, and persists
    /// the new value to the profile JSON on disk via <see cref="PersistReadOnlyFlagOnly"/>.
    /// We MUST persist immediately because the very next user action might be the close
    /// (the whole point of the feature is that close is unattended); waiting for an explicit
    /// Save would defeat the point. 2026-05-22 — Andre's request.</summary>
    private void OnLockProfileToggled(bool readOnly)
    {
        currentProfileReadOnly = readOnly;
        Text = FormatWindowTitle(currentProfileTitle);
        AccessibleName = Text;
        PersistReadOnlyFlagOnly(readOnly);
        AppendLogEntry($"profile read-only flag set to {readOnly} for \"{currentProfileTitle ?? "(blank template)"}\"");
    }

    /// <summary>Write JUST the ReadOnly flag back to the profile file on disk, without
    /// touching any of the user's in-session edits. Used by <see cref="OnLockProfileToggled"/>
    /// so toggling lock-state writes the flag immediately but leaves every other unsaved
    /// change exactly as-is — without this carve-out, unlocking a profile that has unsaved
    /// edits would either have to ignore them (losing user intent) or flush them (defeating
    /// "the lock writes the lock, nothing else"). Approach: read the profile JSON, deserialise,
    /// flip ONE field, re-serialise, write back. Blank-template case (no path) is a silent
    /// no-op — there's no file to update, and the user's lock state lives in memory until
    /// they Save As, at which point Save As builds a fresh Profile and writes whatever
    /// flag the in-memory state has.</summary>
    private void PersistReadOnlyFlagOnly(bool readOnly)
    {
        if (string.IsNullOrEmpty(currentProfilePath)) return;
        if (!File.Exists(currentProfilePath)) return;
        try
        {
            var json = File.ReadAllText(currentProfilePath);
            var profile = JsonSerializer.Deserialize<Profile>(json);
            if (profile is null) return;
            if (profile.ReadOnly == readOnly) return;  // no change, skip the rewrite
            profile.ReadOnly = readOnly;
            var newJson = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(currentProfilePath, newJson);
        }
        catch (Exception ex)
        {
            // Don't bother the user with a MessageBox for a flag-write failure — they'd just
            // see "couldn't persist the lock flag" with no actionable detail. Log and move
            // on; the in-memory state already reflects the toggle, so the current session
            // works correctly. Next launch the file's flag wins, but a single failed write
            // is rare enough that it's not worth a dialog.
            AppendLogEntry($"failed to persist read-only flag: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>File → Change this profile's password. Shows the current password (plain text,
    /// for the screen reader) in a dialog; on OK, updates the in-memory value and writes JUST
    /// the password back to the profile file straight away — same immediate-persist approach as
    /// the lock flag, since the password needs to be there next time the profile is loaded.
    /// Requires a saved profile (a password is meaningless on the blank template, which has no
    /// file to attach it to). 2026-05-31.</summary>
    private void ChangeProfilePassword()
    {
        if (string.IsNullOrEmpty(currentProfileTitle) || string.IsNullOrEmpty(currentProfilePath))
        {
            MessageBox.Show(this,
                "There's no saved profile to attach a password to yet. Save the current setup as a profile first (File → Save as), then set its password.",
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var entered = ProfilePasswordDialog.Show(currentProfileTitle, currentProfilePassword);
        if (entered is null) return; // cancelled
        currentProfilePassword = entered;
        RecomputeAudioCrypto();
        PersistPasswordOnly(entered);
        AppendLogEntry($"profile password changed for \"{currentProfileTitle}\" (now {(entered.Length == 0 ? "cleared" : "set")})");
    }

    /// <summary>Write JUST the (scrambled) password back to the profile file, leaving every
    /// other in-session edit untouched — the same carve-out <see cref="PersistReadOnlyFlagOnly"/>
    /// uses for the lock flag, so changing the password doesn't silently flush unrelated unsaved
    /// changes. Read the JSON, set one field, write it back. Blank-template (no path) is a no-op.</summary>
    private void PersistPasswordOnly(string plaintextPassword)
    {
        if (string.IsNullOrEmpty(currentProfilePath)) return;
        if (!File.Exists(currentProfilePath)) return;
        try
        {
            var json = File.ReadAllText(currentProfilePath);
            var profile = JsonSerializer.Deserialize<Profile>(json);
            if (profile is null) return;
            profile.Password = RemSoundCrypto.Obfuscate(plaintextPassword);
            var newJson = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(currentProfilePath, newJson);
        }
        catch (Exception ex)
        {
            AppendLogEntry($"failed to persist profile password: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Derive the audio key + fingerprint from the current profile password and push them
    /// to the sender and receiver. No/weak password → null key → no audio flows (encryption is
    /// mandatory). Called on password change, when audio is (re)configured, and on the streaming
    /// gate. 2026-05-31.
    ///
    /// The 5.6 PBKDF2 raise (600k, run twice) costs up to ~1 s on old hardware, so the FIRST use of
    /// a strong password this session is derived OFF the UI thread — otherwise a blind user gets a
    /// full NVDA freeze on profile load / first connect (2026-07-27 review). The key is held null
    /// until it lands (audio simply waits — mandatory encryption means it never streams keyless), a
    /// generation guard drops a stale result if the password changed again meanwhile, and the fast
    /// cases (unchanged / cached / empty / weak — no PBKDF2) still apply synchronously so nothing
    /// races on the common path.</summary>
    private void RecomputeAudioCrypto()
    {
        var pw = currentProfilePassword;
        if (pw == lastDerivedPassword)
        {
            PushAudioCrypto();   // no change — re-push the cached key/fp (or null)
            return;
        }
        lastDerivedPassword = pw;
        var gen = ++cryptoGeneration;
        // The password genuinely changed — re-arm the one-shot weak-password explanation so a SECOND
        // weak-password profile switched to in the same session is still explained (not just the first).
        weakPasswordExplained = false;

        // Fast path: null/empty/weak (resolves to (null,null) with no PBKDF2), or an already-cached
        // strong password. Apply synchronously — no freeze possible. Also covers the pre-window
        // case where the form has no handle yet (BeginInvoke would throw), so startup stays correct.
        if (RemSoundCrypto.IsCached(pw) || !IsHandleCreated)
        {
            (currentAudioKey, currentAudioFingerprint) = RemSoundCrypto.ForPlainPassword(pw);
            PushAudioCrypto();
            ExplainWeakPasswordIfNeeded(pw);
            return;
        }

        // Slow path: a strong password not yet derived this session. Hold the key null (audio waits)
        // and do the PBKDF2 on a worker; apply on the UI thread when it lands, unless superseded.
        currentAudioKey = null;
        currentAudioFingerprint = null;
        PushAudioCrypto();
        logFile.Event("audio crypto: deriving key off-thread (strong password, first use this session)");
        var pwLocal = pw;
        Task.Run(() =>
        {
            RemSoundCrypto.Prewarm(pwLocal); // the ~1 s PBKDF2, off the UI thread
            try
            {
                BeginInvoke(() =>
                {
                    if (gen != cryptoGeneration) return; // a newer password change won; drop this result
                    (currentAudioKey, currentAudioFingerprint) = RemSoundCrypto.ForPlainPassword(pwLocal); // now a cache hit
                    PushAudioCrypto();
                    logFile.Event("audio crypto: key ready");
                });
            }
            catch { /* form closed / no handle — nothing to apply to */ }
        });
    }

    private void PushAudioCrypto()
    {
        sender.AudioKey = currentAudioKey;
        sender.AudioFingerprint = currentAudioFingerprint;
        receiver.AudioKey = currentAudioKey;
        receiver.AudioFingerprint = currentAudioFingerprint;
    }

    /// <summary>Since 5.6 the derivation rule refuses a WEAK password (key comes back null). This runs
    /// automatically on profile load / auto-connect, so it must NOT pop a modal: a startup dialog that
    /// steals focus before NVDA can reach it — and drags the window out of the tray to show it — locked
    /// a blind user out completely (Ed, 2026-07-27, "it will fuck over all nvda users"). The weak state
    /// is surfaced NON-modally and persistently in the status line instead (see <see cref="UpdateStatus"/>
    /// and <see cref="WeakPasswordBlocksAudio"/>), which NVDA reads at the user's own pace. The guided
    /// modal prompt is reserved for the user-INITIATED streaming tick (<see cref="EnsureStreamingPassword"/>),
    /// where the user just pressed a key so focus is clean and the dialog is reachable. Here: log once.</summary>
    private void ExplainWeakPasswordIfNeeded(string? pw)
    {
        if (string.IsNullOrEmpty(pw) || currentAudioKey is not null || weakPasswordExplained) return;
        weakPasswordExplained = true;
        logFile.Event("audio crypto: profile password fails the 5.6 strength rule — no audio until it's changed (shown in the status line)");
    }

    /// <summary>Pure, testable: is audio blocked purely because the current profile password is too
    /// weak (as opposed to no password, or a strong password still deriving off-thread)? Drives the
    /// status-line warning. A strong password mid-derive has a null key too, but Critique returns null
    /// for it, so this stays false there — it fires only for a genuinely guessable password.</summary>
    internal static bool WeakPasswordBlocksAudio(string? password, bool haveKey) =>
        !string.IsNullOrEmpty(password) && !haveKey && PasswordStrength.Critique(password) is not null;

    // Bumped on every password change so a slow background derive that finishes AFTER a newer change
    // knows to discard its now-stale result (see RecomputeAudioCrypto).
    private int cryptoGeneration;
    // One-shot flag for the weak-password explanation — the dialog must not re-fire on every profile
    // reapply within a session (the log line still records each derivation refusal). Reset on a
    // profile switch so a SECOND weak-password profile in one session is still explained.
    private bool weakPasswordExplained;

    /// <summary>The "you need a password before any audio can flow" gate. Called when the user
    /// ticks Send my audio or Receive audio. If the active profile has no password, prompt for
    /// one. Since 5.6 (Ed, 2026-07-27) an EXISTING password that fails the strength rule is gated
    /// the same way: the user is told why, in plain words, and audio waits until a stronger one is
    /// set — grandfathering weak passwords forever would have made the derivation-cost raise
    /// theatre, and everyone is already coordinating a password-compatible update in this release.
    /// If they give an acceptable password, set it (and offer to save it to the profile); if they
    /// cancel, un-tick the box. Returns true if streaming may proceed.</summary>
    private bool EnsureStreamingPassword(AccessibleCheckBox box)
    {
        if (!box.Checked) return true;                           // turning OFF never needs a password
        var weakAdvice = PasswordStrength.Critique(currentProfilePassword ?? "");
        if (!string.IsNullOrEmpty(currentProfilePassword) && weakAdvice is null) return true; // have one and it passes

        var label = string.IsNullOrEmpty(currentProfileTitle) ? "this session" : currentProfileTitle;
        if (weakAdvice is not null && !string.IsNullOrEmpty(currentProfilePassword))
        {
            // Tell the user WHY the password prompt is about to appear with their old password in
            // it — a bare dialog would read as a bug to someone whose password worked yesterday.
            var page = new TaskDialogPage
            {
                Caption = "RemSound — password needs strengthening",
                Heading = "Your password must be strengthened",
                Text = "RemSound has increased its security level, so this profile's password must be "
                     + $"strengthened before audio can flow. {weakAdvice}",
                Icon = TaskDialogIcon.Warning,
                Buttons = { TaskDialogButton.OK },
                DefaultButton = TaskDialogButton.OK,
                AllowCancel = true,
            };
            ForegroundDialog.Show(owner => TaskDialog.ShowDialog(owner, page));
        }
        var entered = ProfilePasswordDialog.Show(label, currentProfilePassword ?? "", requireNonEmpty: true, requireStrong: true);
        if (string.IsNullOrEmpty(entered))
        {
            // No acceptable password → can't stream. Put the box back without re-firing this gate.
            suppressStreamingPasswordGate = true;
            try { box.Checked = false; }
            finally { suppressStreamingPasswordGate = false; } // a throw must not disable the gate for good
            return false;
        }
        currentProfilePassword = entered;
        RecomputeAudioCrypto();
        // Offer to remember it on the profile (if we're on a saved one).
        if (!string.IsNullOrEmpty(currentProfileTitle) && !string.IsNullOrEmpty(currentProfilePath))
        {
            var save = ForegroundDialog.Show(owner => MessageBox.Show(owner,
                $"Save this password to profile \"{currentProfileTitle}\" so you don't have to type it next time?",
                AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question));
            if (save == DialogResult.Yes) PersistPasswordOnly(currentProfilePassword);
        }
        return true;
    }

    /// <summary>Send/receive checkbox handler with the password gate in front of it. Replaces the
    /// bare HandleCapabilityChange + MarkProfileDirty wiring.</summary>
    private void OnStreamingCheckboxChanged(AccessibleCheckBox box)
    {
        if (suppressStreamingPasswordGate) return;
        if (!EnsureStreamingPassword(box)) return;
        HandleCapabilityChange();
        MarkProfileDirty();
        // Audible feedback for the toggle, whether the user clicked the checkbox or pressed the
        // mute hotkey (the hotkey flips .Checked, which routes through here too). Skipped while a
        // profile is being applied programmatically — loading a profile that has send/receive on
        // shouldn't blast the cues; only a genuine user toggle should.
        if (!applyingProfile) PlayStreamToggleCue(box);
    }

    /// <summary>Play the send/receive turned-on / turned-off cue for a streaming checkbox toggle.
    /// Machine-wide cues (enable flags in AppConfig); silent when the cue is unticked or absent.</summary>
    private void PlayStreamToggleCue(AccessibleCheckBox box)
    {
        var on = box.Checked;
        var cfg = AppConfig.Load();
        if (box == sendMyAudioCheckbox)
        {
            if (on) { if (cfg.EnableSendOnCue) sendOnSound?.Play(); }
            else { if (cfg.EnableSendOffCue) sendOffSound?.Play(); }
        }
        else if (box == receiveAudioCheckbox)
        {
            if (on) { if (cfg.EnableReceiveOnCue) receiveOnSound?.Play(); }
            else { if (cfg.EnableReceiveOffCue) receiveOffSound?.Play(); }
        }
    }

    /// <summary>Play the minimise(hide) or restore(show) cue. Called by the tray controller when the
    /// window actually transitions to/from hidden. Machine-wide enable flags; silent when unticked
    /// or the cue WAV is absent.</summary>
    public void PlayWindowVisibilityCue(bool show)
    {
        var cfg = AppConfig.Load();
        if (show) { if (cfg.EnableShowCue) showSound?.Play(); }
        else { if (cfg.EnableHideCue) hideSound?.Play(); }
    }

    /// <summary>True while a peer-security warning dialog is on screen — set so the 1 Hz status
    /// tick that raises it can't re-enter and stack (or churn the UI under) a second one.</summary>
    private bool securityWarningShowing;

    /// <summary>Once a second, surface any password mismatch / out-of-date peer the receiver has
    /// detected from peers' advertised fingerprints — once per change, not every tick — so a
    /// silent encrypted stream is never an unexplained mystery.</summary>
    private void CheckPeerSecurity()
    {
        if (securityWarningShowing) return; // a warning is already up — don't re-enter or stack
        foreach (var kv in receiver.GetPeerSecurityStatuses())
        {
            var addr = kv.Key;
            var status = kv.Value;
            if (!IsSelectedPeerAddress(addr)) continue;
            if (status is PeerSecurityStatus.Secure or PeerSecurityStatus.Unknown)
            {
                lastSecurityWarned.Remove(addr);
                continue;
            }
            if (lastSecurityWarned.TryGetValue(addr, out var warned) && warned == status) continue;
            lastSecurityWarned[addr] = status;
            AppendLogEntry($"security: {status} with {addr}");
            var msg = status == PeerSecurityStatus.PasswordMismatch
                ? $"You and {addr} have different passwords, so no audio will pass between you.\n\nMake sure you've both set the same password (File → Change this profile's password)."
                : $"{addr} is running an older version of RemSound that can't connect securely. They need to update before audio can flow between you.";
            // Show it front-and-centre, and FREEZE the 1 Hz status tick while it's up. This dialog is
            // raised FROM that tick; left running, the tick keeps firing into the modal loop and
            // re-runs the peer-list rebuild (SyncAllPeerLists) UNDER the dialog, which knocks it out
            // of the foreground — it "flashed away before I could click OK" (Ed, 2026-06-11). The
            // timer-stop + re-entry guard make exactly one warning show and stay put until dismissed.
            securityWarningShowing = true;
            statusTimer.Stop();
            try
            {
                ForegroundDialog.Show(owner =>
                    MessageBox.Show(owner, msg, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning));
            }
            finally
            {
                statusTimer.Start();
                securityWarningShowing = false;
            }
            return; // one warning per tick; a second affected peer surfaces on the next tick
        }
    }

    private bool IsSelectedPeerAddress(System.Net.IPAddress addr) =>
        selectedPeerEndpoints.Values.Any(e => e.Address.Equals(addr));

    /// <summary>Options → Profile passwords. Opens the password-manager list; if the user changed
    /// any password, re-sync the active profile's password from disk (the manager wrote it there)
    /// and re-derive the audio key so the live session uses the new password immediately.</summary>
    private void OpenProfilePasswordManager()
    {
        if (profileStore is null)
        {
            MessageBox.Show(this, "Profile system not active in this run.", AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var changed = ProfilePasswordManagerDialog.Show(this, profileStore);
        if (!changed) return;
        if (!string.IsNullOrEmpty(currentProfilePath) && File.Exists(currentProfilePath))
        {
            try
            {
                var profile = JsonSerializer.Deserialize<Profile>(File.ReadAllText(currentProfilePath));
                currentProfilePassword = RemSoundCrypto.Deobfuscate(profile?.Password);
                RecomputeAudioCrypto();
                AppendLogEntry("active profile password refreshed from the password manager");
            }
            catch { /* benign — worst case the change applies on next load */ }
        }
    }

    private static List<string> ExtractCheckedDeviceIds(CheckedListBox list)
    {
        var result = new List<string>();
        for (var i = 0; i < list.Items.Count; i++)
        {
            if (!list.GetItemChecked(i)) continue;
            if (list.Items[i] is AudioDeviceChoice choice && !string.IsNullOrEmpty(choice.DeviceId))
            {
                result.Add(choice.DeviceId);
            }
        }
        return result;
    }

    /// <summary>Collect the currently-connected peers as their original entry text (the
    /// user's typed string, e.g. "remote.ednun.com:47830" or "192.168.1.2"). Stored in the
    /// profile so a profile reload re-resolves the hostname (in case the IP has changed)
    /// and reconnects via the same code path the user uses for manual peer adds. Falls back
    /// to "address:port" when we don't have the original text — happens for peers that
    /// arrived via discovery rather than a manual add.</summary>
    private List<string> GatherSelectedPeerEntries()
    {
        var result = new List<string>();
        foreach (var (instanceId, endpoint) in selectedPeerEndpoints)
        {
            // Preferred: original text the user typed (preserves hostnames vs IPs).
            string? entry = null;
            foreach (var (text, id) in rememberedPeerInstanceIds)
            {
                if (id == instanceId) { entry = text; break; }
            }
            if (string.IsNullOrEmpty(entry))
            {
                // Fall back to the discovery label, then to address:port literal.
                if (selectedPeerLabels.TryGetValue(instanceId, out var label) && !string.IsNullOrWhiteSpace(label))
                {
                    entry = label;
                }
                else
                {
                    entry = $"{endpoint.Address}:{endpoint.Port}";
                }
            }
            if (!result.Contains(entry, StringComparer.OrdinalIgnoreCase)) result.Add(entry);
        }
        return result;
    }

    /// <summary>Re-establish the connections that were active when the profile was saved.
    /// Mirrors <see cref="AddManualPeerAsync"/> but quieter — failures (DNS, empty entry)
    /// log to the diagnostic file instead of popping a MessageBox, because we don't want
    /// a startup-time profile load to fire several modal dialogs at the user. Selected
    /// peers that resolve become connected exactly as if the user had typed them.</summary>
    private void ReconnectSavedPeers(IReadOnlyList<string> entries)
    {
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            _ = ReconnectOneSavedPeerAsync(entry);
        }
    }

    private async Task ReconnectOneSavedPeerAsync(string entry)
    {
        try
        {
            var address = await ResolvePeerAddressAsync(entry);
            if (address is null)
            {
                AppendLogEntry($"profile reconnect: could not resolve \"{entry}\"; skipping");
                return;
            }
            var rememberedEntries = settings.LoadRememberedPeers()
                .Select(static value => value.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            rememberedEntries.Add(entry.Trim());
            settings.SaveRememberedPeers(rememberedEntries);

            var peer = CreateManualPeer(entry, address);
            manualPeers[peer.InstanceId] = peer;
            rememberedPeerInstanceIds[entry.Trim()] = peer.InstanceId;
            SelectPeer(peer, fromProfileRestore: true);
            PushDiscoveryUnicastHints();
            logFile.Event($"profile reconnect: \"{entry}\" → {address}:{peer.AudioPort}");
            RefreshKnownPeers();
            // CRITICAL: SelectPeer alone only updates the receiver's allow-list and the
            // selectedPeerEndpoints dictionary; it does NOT engage the audio sender's outbound
            // peer list. Without this ApplyAudioRuntime call, profile-restored peers showed
            // up "ticked" in the UI but the sender never actually transmitted to them, leaving
            // the user staring at "pending" heartbeat for ~40-60 s until the relay's stale-slot
            // timeout expired (or until the user manually unticked + re-ticked, which DOES
            // route through the runtime apply). Observed in logs from 2026-05-05.
            ApplyAudioRuntime();
        }
        catch (Exception ex)
        {
            AppendLogEntry($"profile reconnect: \"{entry}\" failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // OpenManageProfilesDialog and ProfileManagementDialog removed in Phase 4 of the
    // 2026-05-06 UI refactor. Profile management lives inline on the Profiles & preferences
    // tab — see BuildProfilesPrefsTab + SwitchSelectedProfile / RenameSelectedProfile /
    // DeleteSelectedProfile.

    /// <summary>Builds the live tooltip shown over the system-tray icon — sums up peer count
    /// and send / receive routing into a single readable line. Kept under the 127-character
    /// NotifyIcon limit by construction; the tray controller truncates with an ellipsis as a
    /// belt-and-braces if a future addition ever pushes it over.
    ///
    /// Examples:
    ///   * RemSound — not connected
    ///   * RemSound — recording for 2:34 — not connected
    ///   * RemSound — 2 peers, sending (WASAPI), receiving (WASAPI)
    ///   * RemSound — recording for 1:23:45, 2 peers, sending (WASAPI), receiving (WASAPI)
    /// </summary>
    private string BuildTrayTooltip()
    {
        // Healthy-peer count. Heartbeats define "connected" — a peer ticked in the list but
        // never reachable doesn't count, because the user cares about who they can actually
        // talk to right now, not who they intend to.
        var healthyPeers = 0;
        if (heartbeatService is not null)
        {
            foreach (var ph in heartbeatService.GetAllPeerHealth())
            {
                if (ph.State == PeerHealthState.Healthy) healthyPeers++;
            }
        }
        // Recording status — a plain "recording" flag while a capture is running, with no
        // elapsed timer. A live timer would have to rewrite the tooltip every second, which
        // fights the tray icon's re-stamp-on-change behaviour: it would either flicker the
        // icon once a second or leave a screen reader announcing a stale time next to the live
        // one. Recording starting and stopping are real state changes that re-stamp cleanly;
        // the second-by-second count is intentionally left to the main window. Slots in right
        // after the "RemSound" leader so it reads as a status on the app itself rather than a
        // property of the peer list.
        string? recordingPart = recordingController.IsRecording ? "recording" : null;
        if (healthyPeers == 0 && !sendMyAudioCheckbox.Checked && !receiveAudioCheckbox.Checked)
        {
            return recordingPart is null
                ? "RemSound — not connected"
                : $"RemSound — {recordingPart} — not connected";
        }

        var parts = new List<string> { "RemSound" };
        if (recordingPart is not null) parts.Add(recordingPart);
        var peerText = healthyPeers switch
        {
            0 => "no peers",
            1 => "1 peer",
            _ => $"{healthyPeers} peers",
        };
        parts.Add(peerText);

        // Direction lines — only added when the corresponding direction is actually on. The
        // lane label (WASAPI / ASIO / WASAPI + ASIO) is derived from which device-list ticks
        // are active, NOT from the audio-mode setting alone: in BothIndependent a user can
        // still have ticked WASAPI inputs only, in which case the tray should honestly say
        // "WASAPI" rather than "WASAPI + ASIO".
        if (sendMyAudioCheckbox.Checked)
        {
            var hasWasapiSend = AnyChecked(sendInputDevicesList)
                || (!AppsModeActive() && AnyChecked(sendOutputDevicesList))
                || HasAppModeSend();
            var hasAsioSend = AnyChecked(asioSendDevicesList);
            parts.Add($"sending ({DescribeLanes(hasWasapiSend, hasAsioSend)})");
        }
        if (receiveAudioCheckbox.Checked)
        {
            var hasWasapiReceive = AnyChecked(receiveOutputDevicesList);
            var hasAsioReceive = AnyChecked(asioReceiveOutputDevicesList);
            parts.Add($"receiving ({DescribeLanes(hasWasapiReceive, hasAsioReceive)})");
        }
        return string.Join(", ", parts);
    }

    /// <summary>Convenience for BuildTrayTooltip — true if the given CheckedListBox has at
    /// least one ticked item. Defensive against null lists (the layout builders run async on
    /// startup, so the tooltip refresh CAN fire one tick before they exist).</summary>
    private static bool AnyChecked(CheckedListBox? list)
    {
        if (list is null) return false;
        return list.CheckedItems.Count > 0;
    }

    /// <summary>Pretty-print "WASAPI", "ASIO", "WASAPI + ASIO", or "no devices" depending
    /// on which of the two flags are set. "no devices" covers the awkward case where the
    /// user has the send/receive checkbox on but hasn't ticked anything for the engine to
    /// chew on — better to say so in the tooltip than imply silent activity.</summary>
    private static string DescribeLanes(bool wasapi, bool asio) =>
        (wasapi, asio) switch
        {
            (true, true) => "WASAPI + ASIO",
            (true, false) => "WASAPI",
            (false, true) => "ASIO",
            _ => "no devices",
        };

    /// <summary>Well-known cue identifiers used as keys into
    /// <see cref="AppConfig.CustomCuePaths"/>. Stable strings — don't rename without writing
    /// a migration, because users' Preferences-set custom paths are stored under these keys
    /// in <c>remsound.config.json</c>. Centralised here so the Preferences dialog and the
    /// MainForm load path agree on the spellings.</summary>
    internal static class CueId
    {
        public const string Connect = "connect";
        public const string Disconnect = "disconnect";
        public const string RecordStart = "record-start";
        public const string RecordStop = "record-stop";
        public const string Save = "save";
        public const string ProfileSwitch = "profile-switch";
        public const string ProfileMenuOpen = "profile-menu-open";
        public const string Update = "update";
        // Startup cue is special: it plays from Program.cs before any profile loads, so its
        // enable flag and custom-path live machine-wide in AppConfig, not the per-profile
        // settings store. This id is still used by the Preferences cue list for display/keying.
        public const string Startup = "startup";
        // Send/receive toggle + minimise(hide)/restore(show) cues (2026-06-13). Also machine-wide
        // (enable flags + custom paths in AppConfig) - app-level feedback, not per-profile audio.
        public const string SendOn = "send-on";
        public const string SendOff = "send-off";
        public const string ReceiveOn = "receive-on";
        public const string ReceiveOff = "receive-off";
        public const string Hide = "hide";
        public const string Show = "show";
        // Played on every checkbox tick/untick across the whole app (CheckSoundService).
        public const string CheckboxOn = "checkbox-on";
        public const string CheckboxOff = "checkbox-off";
        // Played whenever the user switches tabs anywhere in the app (TabSwitchSoundService,
        // fired from QuietTabControl). The shipped WAVs are "tab switch 1.wav" etc.
        public const string TabSwitch = "tab-switch";
    }

    /// <summary>Load one cue sound. Resolution order:
    /// (1) if the active profile has a custom path for <paramref name="cueId"/> AND the
    ///     referenced file exists, use that — the user-supplied override.
    /// (2) otherwise the default WAV in <c>sounds\</c> next to RemSound.exe, named
    ///     <paramref name="defaultFileName"/>.
    /// (3) otherwise null — the cue silently doesn't play. New cues without a shipped
    ///     default WAV (e.g. save.wav and profile.wav before the project owner supplies
    ///     them) land here and the rest of the app keeps working.
    ///
    /// Custom paths are per-profile (changed from machine-wide in v3.0.3 development) so
    /// each profile can carry its own cue palette. The settings cache mirrors the active
    /// profile's CustomCuePaths dictionary and is the runtime source of truth.</summary>
    private void TryLoadCueSound(string cueId, string defaultFileName, out CuePlayer? player, AppConfig? cfg = null)
    {
        player = null;
        try
        {
            // Load the config once per call (or reuse the caller's — ReloadAllCueSounds passes one shared
            // instance for all 14 cues instead of each cue re-reading + re-parsing the file from disk).
            cfg ??= AppConfig.Load();
            string? path = null;
            var customPath = settings.LoadCustomCuePath(cueId);
            if (string.IsNullOrWhiteSpace(customPath)
                && cfg.MachineCueCustomPaths.TryGetValue(cueId, out var machinePath))
            {
                // Machine-wide cues (send/receive/hide/show) keep their custom override in AppConfig.
                customPath = machinePath;
            }
            if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
            {
                path = customPath;
                logFile.Event($"cue sound '{cueId}': using custom path {customPath}");
            }
            else
            {
                // The cue ships as numbered variants ("connect 1.wav", "connect 2.wav", ...);
                // resolve the machine-wide chosen default (or the first variant) for this cue.
                var defaultPath = CueSounds.ResolveDefaultPath(cueId, defaultFileName, cfg);
                if (defaultPath is not null && File.Exists(defaultPath))
                {
                    path = defaultPath;
                }
                else
                {
                    logFile.Event($"cue sound '{cueId}': no default variant found in sounds\\ and no custom override set — cue will be silent");
                    return;
                }
            }
            // CuePlayer reads + plays the file on demand (NAudio), so no pre-load step — and it
            // copes with any format, including the 96 kHz / 24-bit cue WAVs and custom user files.
            player = new CuePlayer(path);
        }
        catch (Exception ex)
        {
            logFile.Event($"cue sound load failed for '{cueId}': {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Re-load all cue sounds. Called by PreferencesDialog after the user picks a
    /// new custom WAV for any cue — re-runs <see cref="TryLoadCueSound"/> for the lot so
    /// the cached SoundPlayer instances point at the right file from the next play onward.
    /// </summary>
    public void ReloadAllCueSounds()
    {
        // Load the machine config ONCE and pass it to all 14 cues, instead of each cue (twice) re-reading
        // and re-deserializing the config file — this runs on the UI thread on every Preferences close.
        var cfg = AppConfig.Load();
        TryLoadCueSound(CueId.Connect, "connect.wav", out connectSound, cfg);
        TryLoadCueSound(CueId.Disconnect, "disconnect.wav", out disconnectSound, cfg);
        TryLoadCueSound(CueId.RecordStart, "record start.wav", out recordStartSound, cfg);
        TryLoadCueSound(CueId.RecordStop, "record stop.wav", out recordStopSound, cfg);
        TryLoadCueSound(CueId.Save, "save.wav", out saveSound, cfg);
        TryLoadCueSound(CueId.ProfileSwitch, "profile.wav", out profileSwitchSound, cfg);
        TryLoadCueSound(CueId.ProfileMenuOpen, "profile menu open.wav", out profileMenuOpenSound, cfg);
        TryLoadCueSound(CueId.Update, "update.wav", out updateSound, cfg);
        TryLoadCueSound(CueId.SendOn, "send on.wav", out sendOnSound, cfg);
        TryLoadCueSound(CueId.SendOff, "send off.wav", out sendOffSound, cfg);
        TryLoadCueSound(CueId.ReceiveOn, "recieve on.wav", out receiveOnSound, cfg);
        TryLoadCueSound(CueId.ReceiveOff, "recieve off.wav", out receiveOffSound, cfg);
        TryLoadCueSound(CueId.Hide, "minimise.wav", out hideSound, cfg);
        TryLoadCueSound(CueId.Show, "maximise.wav", out showSound, cfg);
        // The app-wide checkbox tick/untick and tab-switch sounds live in their own services; keep
        // them in step.
        CheckSoundService.Reload();
        TabSwitchSoundService.Reload();
        // After a reload (e.g. the user changed a cue in Preferences), warn about any cue that's
        // switched on but whose sound file is missing. Skipped during construction (no window yet);
        // OnShown does the first-launch pass.
        CheckForMissingEnabledCues();
    }

    private readonly HashSet<string> reportedMissingCues = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>For every cue that's switched ON but whose sound file couldn't be found (player is
    /// null - e.g. a custom WAV was deleted, or a chosen sound is gone), turn the cue off and tell
    /// the user, front-most, once per cue. They re-enable it by choosing a sound in Preferences.</summary>
    private void CheckForMissingEnabledCues()
    {
        if (!IsHandleCreated) return; // need a window so the warning can surface
        var cfg = AppConfig.Load();
        (CuePlayer? Player, bool Enabled, Action Disable, string Name, string When)[] cues =
        {
            (connectSound, settings.LoadEnableConnectCue(), () => settings.SaveEnableConnectCue(false), "connect", "a peer connects"),
            (disconnectSound, settings.LoadEnableDisconnectCue(), () => settings.SaveEnableDisconnectCue(false), "disconnect", "a peer disconnects"),
            (recordStartSound, settings.LoadEnableRecordStartCue(), () => settings.SaveEnableRecordStartCue(false), "recording start", "you start recording"),
            (recordStopSound, settings.LoadEnableRecordStopCue(), () => settings.SaveEnableRecordStopCue(false), "recording stop", "you stop recording"),
            (saveSound, settings.LoadEnableSaveCue(), () => settings.SaveEnableSaveCue(false), "profile saved", "you save a profile"),
            (profileSwitchSound, settings.LoadEnableProfileSwitchCue(), () => settings.SaveEnableProfileSwitchCue(false), "profile switched", "you switch profile"),
            (profileMenuOpenSound, settings.LoadEnableProfileMenuOpenCue(), () => settings.SaveEnableProfileMenuOpenCue(false), "profile menu open", "the quick profile menu opens"),
            (updateSound, settings.LoadEnableUpdateCue(), () => settings.SaveEnableUpdateCue(false), "update", "an update is about to install"),
            (sendOnSound, cfg.EnableSendOnCue, () => SetMachineCueEnabled(c => c.EnableSendOnCue = false), "send turned on", "you turn sending on"),
            (sendOffSound, cfg.EnableSendOffCue, () => SetMachineCueEnabled(c => c.EnableSendOffCue = false), "send turned off", "you turn sending off"),
            (receiveOnSound, cfg.EnableReceiveOnCue, () => SetMachineCueEnabled(c => c.EnableReceiveOnCue = false), "receive turned on", "you turn receiving on"),
            (receiveOffSound, cfg.EnableReceiveOffCue, () => SetMachineCueEnabled(c => c.EnableReceiveOffCue = false), "receive turned off", "you turn receiving off"),
            (hideSound, cfg.EnableHideCue, () => SetMachineCueEnabled(c => c.EnableHideCue = false), "minimise", "RemSound minimises to the tray"),
            (showSound, cfg.EnableShowCue, () => SetMachineCueEnabled(c => c.EnableShowCue = false), "restore", "RemSound is restored from the tray"),
        };
        foreach (var c in cues)
        {
            if (c.Enabled && c.Player is null)
            {
                c.Disable();
                ReportMissingCueSound(c.Name, c.When);
            }
        }
    }

    private static void SetMachineCueEnabled(Action<AppConfig> set)
    {
        var c = AppConfig.Load();
        set(c);
        try { c.Save(); } catch { /* harmless — the cue is silent regardless this session */ }
    }

    /// <summary>Surface the "couldn't find a cue's sound file" message, front-most even when RemSound
    /// is minimised, once per cue per session. The cue has already been turned off by the caller.</summary>
    private void ReportMissingCueSound(string name, string when)
    {
        if (!reportedMissingCues.Add(name)) return;
        logFile.Event($"cue sound '{name}': enabled but file missing — cue turned off, informing the user");
        // A --silent (automated) launch turns the cue off quietly and never pops a dialog at the user.
        if (CuePlayer.GloballyMuted) return;
        BeginInvoke(() =>
        {
            try { RestoreFromTray(); } catch { /* surfacing is best-effort */ }
            MessageBox.Show(this,
                $"RemSound was unable to find the {name} sound file used when {when}. " +
                "RemSound has set this particular audio cue to not play for now, until a new sound file is specified.",
                "RemSound — missing sound file", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        });
    }

    /// <summary>
    /// Compares current peer-health states to the last-seen states and plays a connect /
    /// disconnect cue on the relevant transitions. Driven from the 1 Hz snapshot tick. Rules:
    ///   • Any state → Healthy: play connect cue (first connection, or a stale/unreachable peer
    ///     came back).
    ///   • Healthy or Stale → Unreachable: play disconnect cue. We deliberately do NOT fire a
    ///     disconnect cue for Unknown → Unreachable — that's "we typed an address but never got
    ///     a single heartbeat reply", which is a connect-failed event, not a connect-then-lost
    ///     event. Playing a disconnect ding for a peer that never connected is jarring and was
    ///     observed at jam-session start when the relay/peer hadn't paired yet.
    ///   • Tracked peer disappeared from the list (deselected): play disconnect if the peer was
    ///     Healthy at the last observation — quiet otherwise.
    /// Stale is ignored (it's a transient between Healthy and Unreachable).
    /// </summary>
    private void DetectAndAnnouncePeerHealthTransitions()
    {
        if (heartbeatService is null) return;
        var current = heartbeatService.GetAllPeerHealth();

        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // How recently audio must have arrived to count the peer as "audibly connected". Audio
        // arrives hundreds of times a second, so a 3-second gap is a genuine interruption, not
        // jitter. Disconnect also requires the heartbeat to be Unreachable (5 s of no reply), so
        // the heartbeat is the slower gate for a real, total loss.
        var audioWindow = TimeSpan.FromSeconds(3);
        foreach (var ph in current)
        {
            var key = $"{ph.AudioEndpoint.Address}:{ph.AudioEndpoint.Port}";
            seenKeys.Add(key);

            var audioFlowing = receiver.IsAudioFlowingFrom(ph.AudioEndpoint.Address, audioWindow);
            // Connected the moment audio arrives OR the heartbeat is solidly healthy; lost only
            // when audio has stopped AND the heartbeat has gone unreachable. The middle ground
            // (heartbeat Stale, or audio briefly paused) holds the previous state — hysteresis,
            // so a heartbeat blip while audio keeps playing never fires a false disconnect.
            var isConnected = audioFlowing || ph.State == PeerHealthState.Healthy;
            var isLost = !audioFlowing && ph.State == PeerHealthState.Unreachable;
            var wasConnected = peerConnectedState.TryGetValue(key, out var w) && w;

            if (isConnected && !wasConnected)
            {
                var enabled = settings.LoadEnableConnectCue();
                if (enabled) connectSound?.Play();
                logFile.Event($"peer connected: {ph.AudioEndpoint} (audio={audioFlowing}, heartbeat={ph.State}) — connect cue {CueOutcome(enabled, connectSound)}");
                peerConnectedState[key] = true;
            }
            else if (isLost && wasConnected)
            {
                var enabled = settings.LoadEnableDisconnectCue();
                if (enabled) disconnectSound?.Play();
                logFile.Event($"peer disconnected: {ph.AudioEndpoint} (audio stopped, heartbeat={ph.State}) — disconnect cue {CueOutcome(enabled, disconnectSound)}");
                peerConnectedState[key] = false;
            }
            else if (!peerConnectedState.ContainsKey(key))
            {
                // First sighting and neither clearly connected nor lost (e.g. address typed but
                // no audio/pong yet) — seed the state without playing a cue.
                peerConnectedState[key] = isConnected;
            }
        }

        // Peers that vanished from tracking entirely (user deselected). Play disconnect only if
        // they were connected when last seen — a peer that never connected stays quiet.
        foreach (var key in peerConnectedState.Keys.Where(k => !seenKeys.Contains(k)).ToList())
        {
            if (peerConnectedState[key])
            {
                var enabled = settings.LoadEnableDisconnectCue();
                if (enabled) disconnectSound?.Play();
                logFile.Event($"peer disconnected: {key} (deselected while connected) — disconnect cue {CueOutcome(enabled, disconnectSound)}");
            }
            peerConnectedState.Remove(key);
        }
    }

    /// <summary>Describes what actually happened to a cue, for honest logging: "played", "muted
    /// in settings", or "enabled but sound not loaded" — so the log never claims a cue rang when
    /// no sound came out. 2026-06-02.</summary>
    private static string CueOutcome(bool enabled, CuePlayer? sound) =>
        !enabled ? "muted in settings" : sound is null ? "enabled but sound not loaded" : "played";

    /// <summary>
    /// Append each configurable global hotkey to the accessible description of the control or menu
    /// item it drives, so NVDA reads e.g. "Start recording … press Control+Shift+Alt+R anywhere"
    /// when you land on it. Unset hotkeys clear the hint. Re-run whenever a hotkey is rebound (via
    /// hotkeyController.OnHotkeyChanged) so the announcement always matches the current binding.
    /// NVDA reads AccessibleDescription after the name/role/state by default. Hotkeys with no
    /// dedicated on-screen control (tray show/hide, the remote-control and system-volume keys) have
    /// no place to announce and are simply listed in the Keyboard shortcuts dialog.
    /// </summary>
    private void UpdateHotkeyAnnouncements()
    {
        sendMyAudioCheckbox.AccessibleDescription = DescribeHotkey(hotkeyController.SendMuteHotkey);
        receiveAudioCheckbox.AccessibleDescription = DescribeHotkey(hotkeyController.ReceiveMuteHotkey);
        enableAllPeerShapingBox.AccessibleDescription = DescribeHotkey(hotkeyController.ToggleAllPeerShapingHotkey);
        if (startStopRecordingMenuItem is not null)
        {
            startStopRecordingMenuItem.AccessibleDescription = DescribeHotkey(hotkeyController.ToggleRecordingHotkey);
        }
        // The received-sound volume slider is driven by two hotkeys (up and down).
        var up = hotkeyController.VolumeUpHotkey;
        var down = hotkeyController.VolumeDownHotkey;
        var parts = new List<string>(2);
        if (!up.IsUnset) parts.Add($"press {up} anywhere for volume up");
        if (!down.IsUnset) parts.Add($"press {down} anywhere for volume down");
        volumeBar.AccessibleDescription = parts.Count == 0 ? "" : string.Join("; ", parts);
    }

    private static string DescribeHotkey(HotkeyInfo hotkey) =>
        hotkey.IsUnset ? "" : $"press {hotkey} anywhere";

    private void NudgeVolume(int deltaPercent)
    {
        BeginInvoke(() =>
        {
            var newValue = Math.Clamp(volumeBar.Value + deltaPercent, volumeBar.Minimum, volumeBar.Maximum);
            if (newValue == volumeBar.Value) return;
            volumeBar.Value = newValue;
            receiver.Volume = volumeBar.Value / 100f;
        });
    }

    /// <summary>
    /// Send a remote-control Control packet to every currently-tracked peer. Triggered by the
    /// global hotkeys configured in the Keyboard shortcuts dialog. The local volume / mute
    /// state on THIS machine is deliberately not touched — only peers that have ticked their
    /// "Accept remote volume commands from peers" box honour the request. Use case: I'm
    /// NVDA-Remote'd into another PC and want to nudge listening volume on the laptop I'm
    /// physically at without breaking out of the session.
    /// </summary>
    /// <param name="kind">VolumeUp / VolumeDown / MuteToggle.</param>
    /// <param name="delta">Percent-point delta (signed). Ignored for MuteToggle.</param>
    private void SendRemoteControl(RemoteControlKind kind, sbyte delta)
    {
        if (!connected) return;
        // No password → no key → no remote control, same mandatory-encryption rule as audio. The
        // command is SEALED with the audio key (2026-07-26 security audit): only a password-holder
        // can drive a peer's volume — a forged source IP is no longer enough.
        if (currentAudioKey is not { } key)
        {
            logFile.Event($"remote-control NOT sent (no profile password set) kind={kind}");
            return;
        }
        var endpoints = SelectedSendEndpoints();
        if (endpoints.Length == 0) return;

        var sealedPayload = ControlSealing.Seal(key, kind, delta, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var bytes = new byte[RemPacket.HeaderSize + sealedPayload.Length];
        // streamId 0xFFFE for control packets (heartbeat already uses 0xFFFF). Distinct value
        // makes diag logs easier to read; the receiver doesn't actually filter on it.
        var seq = unchecked((uint)Interlocked.Increment(ref remoteControlSequence));
        RemPacket.WriteHeader(bytes, RemPacketType.Control, 0xFFFE, seq);
        sealedPayload.CopyTo(bytes, RemPacket.HeaderSize);

        var sentTo = 0;
        foreach (var ep in endpoints)
        {
            try
            {
                if (sender.SendVia(bytes, bytes.Length, ep)) sentTo++;
            }
            catch (Exception ex)
            {
                logFile.Event($"remote-control send to {ep} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        logFile.Event($"remote-control sent kind={kind} delta={delta} seq={seq} peers={sentTo}/{endpoints.Length}");
    }

    private int remoteControlSequence;
    // Receiver-side gate for sealed control commands: authenticates against the profile key, bounds
    // clock skew, and blocks replays of captured packets. UI-thread only (see the BeginInvoke above).
    private readonly ControlReceiveGuard remoteControlGuard = new();

    /// <summary>
    /// Handler for incoming Control packets. Runs on the network thread — marshal to UI before
    /// touching controls. Gates on (a) the user's <see cref="Profile.AcceptRemoteVolumeCommands"/>
    /// preference and (b) the audio allow-list (the sender must already be a ticked peer).
    /// We deliberately don't gate on receive-audio-enabled: the volume / mute state is meaningful
    /// even when playback is currently off, because the next time the user enables receive
    /// they'll hear it at the right level.
    /// </summary>
    private void HandleRemoteControlPacket(byte[] sealedPayload, IPEndPoint remote)
    {
        // Marshal to the UI thread FIRST. The allow-list scan reads selectedPeerEndpoints — a plain
        // Dictionary owned and mutated by the UI thread — so enumerating it here on the receiver's
        // network thread races a concurrent peer tick/untick (a caught "collection was modified" that
        // silently drops the remote command). Running the whole check on the UI thread removes the race
        // (and gives the replay guard single-threaded access).
        BeginInvoke(() =>
        {
            // Allow-list match by IP only — the sender's source port is their ephemeral outbound,
            // not their announced audio port. This is a coarse first gate; the REAL authentication
            // is the seal check below (source IPs are forgeable, the audio key is not).
            var allowed = false;
            foreach (var ep in selectedPeerEndpoints.Values)
            {
                if (ep.Address.Equals(remote.Address)) { allowed = true; break; }
            }
            if (!allowed)
            {
                logFile.Event($"remote-control IGNORED (not in allow-list) from={remote}");
                return;
            }
            if (!settings.LoadAcceptRemoteVolumeCommands())
            {
                logFile.Event($"remote-control IGNORED (Accept remote volume commands is off) from={remote}");
                return;
            }
            // Authenticate: the command must decrypt with OUR profile key (proves the sender knows
            // the password), be fresh, and not be a replayed capture. 2026-07-26 security audit —
            // spoofing an allowed IP was previously enough to drive system volume/mute, and muting
            // a blind user's machine mutes their screen reader.
            if (currentAudioKey is not { } key)
            {
                logFile.Event($"remote-control IGNORED (no profile password set) from={remote}");
                return;
            }
            if (!remoteControlGuard.TryAccept(key, sealedPayload, DateTime.UtcNow, out var kind, out var delta, out var why))
            {
                logFile.Event($"remote-control REJECTED ({why}) from={remote}");
                return;
            }

            switch (kind)
            {
                case RemoteControlKind.VolumeUp:
                case RemoteControlKind.VolumeDown:
                    var nudge = kind == RemoteControlKind.VolumeUp
                        ? Math.Abs((int)delta)        // positive
                        : -Math.Abs((int)delta);      // negative
                    var newValue = Math.Clamp(volumeBar.Value + nudge, volumeBar.Minimum, volumeBar.Maximum);
                    if (newValue != volumeBar.Value)
                    {
                        volumeBar.Value = newValue;
                        receiver.Volume = volumeBar.Value / 100f;
                    }
                    logFile.Event($"remote-control APPLIED kind={kind} delta={delta} new-volume={volumeBar.Value} from={remote}");
                    break;
                case RemoteControlKind.MuteToggle:
                    receiver.IsMuted = !receiver.IsMuted;
                    logFile.Event($"remote-control APPLIED kind=MuteToggle muted={receiver.IsMuted} from={remote}");
                    break;
                case RemoteControlKind.SystemVolumeUp:
                    {
                        var ok = SystemVolumeHelper.TryStepUp();
                        var st = SystemVolumeHelper.TryReadState();
                        logFile.Event($"remote-control APPLIED kind=SystemVolumeUp ok={ok} state={(st is { } v ? $"{(int)(v.scalar * 100)}%{(v.mute ? " MUTED" : "")}" : "?")} from={remote}");
                        break;
                    }
                case RemoteControlKind.SystemVolumeDown:
                    {
                        var ok = SystemVolumeHelper.TryStepDown();
                        var st = SystemVolumeHelper.TryReadState();
                        logFile.Event($"remote-control APPLIED kind=SystemVolumeDown ok={ok} state={(st is { } v ? $"{(int)(v.scalar * 100)}%{(v.mute ? " MUTED" : "")}" : "?")} from={remote}");
                        break;
                    }
                case RemoteControlKind.SystemMuteToggle:
                    {
                        var ok = SystemVolumeHelper.TryToggleMute();
                        var st = SystemVolumeHelper.TryReadState();
                        logFile.Event($"remote-control APPLIED kind=SystemMuteToggle ok={ok} state={(st is { } v ? $"{(int)(v.scalar * 100)}%{(v.mute ? " MUTED" : "")}" : "?")} from={remote}");
                        break;
                    }
            }
        });
    }

    // === Latency probe helpers ===

    /// <summary>Average send-side accumulator wait — half the active codec frame size. PCM
    /// 5 ms → 2.5 ms typical; PCM 2.5 ms → 1.25 ms; Opus 20 ms → 10 ms; tight-latency PCM in
    /// AsioOnly bypasses the accumulator entirely so this estimate is an upper bound there.</summary>
    private double SenderAccumulatorEstimateMs()
    {
        if (codecBox.SelectedItem is not CodecChoice item) return 2.5;
        var rate = settings.LoadSendRate();
        if (item.Codec == AudioTransportCodec.Opus)
        {
            // EffectiveOpusFrameSamples is samples-per-channel at 48 kHz; ÷ 48 → ms, ÷ 2 → half-frame.
            return EffectiveOpusFrameSamples(item.Codec, item.OpusFrameSamples, rate) / 96.0;
        }
        // PCM. Lock to audio clock is always on now, so ASIO-only means per-callback emission.
        if (settings.LoadAudioMode() == AudioMode.AsioOnly)
        {
            return 0.5; // per-callback ASIO send → ~one ASIO buffer, hard to know without driver introspection
        }
        return rate == SendRate.Tight ? 1.25 : 2.5;
    }

    /// <summary>Lowest healthy peer's heartbeat RTT. Used as the wire-time estimate. Returns 0
    /// if no peers are healthy.</summary>
    private double LowestPeerRttMs()
    {
        if (heartbeatService is null) return 0;
        var min = double.MaxValue;
        foreach (var ph in heartbeatService.GetAllPeerHealth())
        {
            if (ph.State != PeerHealthState.Healthy || ph.RttMs is not { } rtt) continue;
            if (rtt < min) min = rtt;
        }
        return min == double.MaxValue ? 0 : min;
    }

    /// <summary>Rough render-side buffer estimate. WASAPI shared-mode is ~10 ms typical.
    /// BothIndependent has no tee — both lanes run at their native callback rate — so the
    /// worse of the two governs perceived delay. ASIO depends on driver buffer settings
    /// we don't query, but is always lower than WASAPI in practice, so the WASAPI estimate
    /// is what governs in both modes.</summary>
    private double RenderBufferEstimateMs() => 10;

    /// <summary>
    /// Translates a codec choice + the user's Send Rate into the effective Opus frame size in
    /// samples-per-channel at 48 kHz. PCM frame size is set separately in AudioSender.SetSendRate.
    /// Standard returns the codec's natural frame; Tight halves it (Opus 960 → 480 → 240 → 120
    /// floored). Floor is 120 samples = 2.5 ms = standard libopus's RESTRICTED_LOWDELAY minimum.
    /// </summary>
    // The rule itself lives in Core (AudioTransportRules) — shared with the service, which must never
    // depend on this Form. Thin wrapper kept for the existing call sites.
    internal static int EffectiveOpusFrameSamples(AudioTransportCodec codec, int opusFrameSamples, SendRate rate) =>
        AudioTransportRules.EffectiveOpusFrameSamples(codec, opusFrameSamples, rate);

    /// <summary>
    /// Short codec label for the per-peer line in the connectivity dialog. e.g. "PCM",
    /// "Opus 10ms", "Opus 20ms", "Opus 2.5ms". Input is samples-per-channel at 48 kHz; the
    /// label derives ms from samples / 48 with up to one decimal place. Uses the same
    /// EffectiveOpusFrameSamples the encoder uses so the label reflects the actually-encoded
    /// frame size, not the codec menu choice.
    /// </summary>
    private static string FormatCodecLabel(AudioTransportCodec codec, int opusFrameSamples)
    {
        return codec switch
        {
            AudioTransportCodec.Opus => $"Opus {Math.Max(1, opusFrameSamples) / 48.0:0.##}ms",
            AudioTransportCodec.Pcm => "PCM",
            _ => codec.ToString(),
        };
    }

    /// <summary>
    /// Codec value for the SNAP log's Codec column. Reports the codec actually in use rather
    /// than the dormant send-codec setting: when receiving, the incoming stream's wire codec
    /// (what we're decoding); when sending only, the send codec; and "tx=…/rx=…" when a
    /// full-duplex node is sending and receiving with different codecs. This fixes the old
    /// behaviour where a receive-only node logged its idle send setting (e.g. "Pcm") while
    /// actually decoding "PCM over Opus" — which is exactly what masked a receive session as
    /// PCM during the 2026-06 memory-leak investigation.
    /// </summary>
    private string SnapshotCodecLabel()
    {
        var rx = receiver.IsRunning ? receiver.ActiveReceiveCodec : null;
        if (rx is AudioTransportCodec rxCodec)
        {
            return sender.IsRunning && sender.Codec != rxCodec
                ? $"tx={sender.Codec} rx={rxCodec}"
                : rxCodec.ToString();
        }
        return sender.Codec.ToString();
    }

    /// <summary>Snap an integer to the nearest 5. Used to keep RTT chatter in the per-peer
    /// listbox line low — single-millisecond drift no longer re-announces under NVDA.</summary>
    private static int RoundToFive(int value) => ((value + 2) / 5) * 5;

    /// <summary>Re-applies the codec/Opus-frame setting after the user changes Send Rate. The
    /// PCM frame size is updated by AudioSender.SetSendRate directly; for Opus we have to
    /// re-init the encoder via ConfigureCodec.</summary>
    private void ApplySendRateToOpus(SendRate rate)
    {
        if (codecBox.SelectedItem is CodecChoice item && item.Codec == AudioTransportCodec.Opus)
        {
            var effectiveSamples = EffectiveOpusFrameSamples(item.Codec, item.OpusFrameSamples, rate);
            sender.ConfigureCodec(item.Codec, effectiveSamples);
            logFile.Event($"send rate changed to {rate} → Opus frame {effectiveSamples / 48.0:0.##}ms");
        }
        else
        {
            logFile.Event($"send rate changed to {rate} (PCM)");
        }
    }

    private static int ResolveCodecIndex(AudioTransportCodec codec, int opusFrameSamples)
    {
        if (codec == AudioTransportCodec.Pcm) return 0;
        // Opus 120 (2.5 ms — live latency) = index 2. Anything else (including the retired
        // 10 ms middle (480) and the never-exposed 5 ms (240)) collapses to index 1
        // (broadcast quality / 20 ms), the safer default — losing a little latency is the
        // less surprising outcome on upgrade than losing loss tolerance. v2.x profiles that
        // saved OpusFrameMilliseconds=10 (which the settings store migrates to 480 samples
        // via the <120 sentinel) land here on the broadcast side; users who specifically
        // want low latency re-pick "live latency" from the dropdown.
        return opusFrameSamples switch
        {
            120 => 2,
            _ => 1,
        };
    }

    // ===================== Auto-tune =====================

    /// <summary>(Re)configures the continuous-tune timer based on the current checkbox / combo
    /// state held in <see cref="continuousTuneEnabled"/> / <see cref="continuousTuneIntervalSec"/>.
    /// Called whenever either changes (in the dialog) or at startup. The timer fires when
    /// either lane has auto-tune enabled — in classic modes that's just the single WASAPI/
    /// Mixed flag; in BothIndependent either WASAPI or ASIO being on is enough to keep the
    /// timer running. The per-route filtering inside the tick gates which sliders actually
    /// move.</summary>
    /// <summary>True if either lane's continuous auto-tune is enabled. Used by the shared
    /// interval combo's Enabled state — the combo governs both lanes' tick rates, so it
    /// should be usable as long as at least one lane wants ticking. Reading from the live
    /// checkbox states keeps this consistent with the lane's checkbox even before the
    /// CheckedChanged handlers have updated the persisted setting.</summary>
    private bool AnyAutoTuneEnabled()
    {
        var inBothIndependent = settings.LoadAudioMode() == AudioMode.BothIndependent;
        var asioOn = inBothIndependent && continuousTuneAsioBox.Checked;
        return continuousTuneEnabled || asioOn;
    }

    private void ApplyContinuousTuneTimer()
    {
        continuousTuneTimer.Stop();
        var inBothIndependent = settings.LoadAudioMode() == AudioMode.BothIndependent;
        var asioEnabled = inBothIndependent && settings.LoadContinuousAutoTuneAsioEnabled();
        // Auto-tune needs the per-second diag snapshot (arrival-gap and render-callback-gap
        // history) to make its recommendation. Make sure the engine's instrumentation is on
        // whenever either lane's continuous tune is active, even if the Enable-logs checkbox
        // is off.
        UpdateDiagnosticsGate();
        if (!continuousTuneEnabled && !asioEnabled) return;
        continuousTuneTimer.Interval = Math.Max(1000, continuousTuneIntervalSec * 1000);
        continuousTuneTimer.Start();
    }

    /// <summary>Recompute <see cref="DiagnosticsGate.Enabled"/> from every reason the engine
    /// might need its instrumentation on: the user-facing Enable-logs checkbox, plus either
    /// continuous-auto-tune toggle. Auto-tune reads <c>diag.MaxArrivalGapMs</c> /
    /// <c>diag.MaxRenderCallbackGapMs</c> from the per-second snapshot to size the latency
    /// target, so its data has to keep flowing even when logs are off; the user shouldn't
    /// have to enable logging just to make auto-tune work.</summary>
    private void UpdateDiagnosticsGate()
    {
        var asioContinuous = settings.LoadAudioMode() == AudioMode.BothIndependent
            && settings.LoadContinuousAutoTuneAsioEnabled();
        DiagnosticsGate.Enabled = logFile.Enabled || continuousTuneEnabled || asioContinuous;
    }

    /// <summary>Which route the legacy "Audio latency / WASAPI latency" slider operates on.
    /// In classic modes that's the Mixed route (only sessions in play). In BothIndependent
    /// the slider has been relabeled to "WASAPI latency" and drives the WasapiLane route.</summary>
    private RenderRoute MaxLatencyBoxRoute =>
        settings.LoadAudioMode() == AudioMode.BothIndependent ? RenderRoute.WasapiLane : RenderRoute.Mixed;

    /// <summary>
    /// Continuous-tune tick. Computes a recommended target from the rolling max-gap window and
    /// adjusts the slider, with several robustness rules learned from real-world testing:
    ///
    ///   1. **Max over a long lookback window.** Earlier we used p95 of the recent few seconds,
    ///      but with very few samples that's mathematically the same as the max anyway, and
    ///      bad events aged out of the window in seconds — so auto-tune could drop the target
    ///      below the level that had just earned the user a pop. Now we take the worst gap
    ///      across the last <see cref="LookbackSeconds"/> seconds, so a bad event keeps target
    ///      elevated long enough to cover the long-tail of the same disturbance.
    ///   2. **Cap auto-tune recommendations at <see cref="AutoTuneRecommendationCapMs"/>.** Beyond
    ///      that the user is in "I want a huge buffer for terrible network" territory — they can
    ///      drag the slider there manually; the auto-tuner shouldn't go there on its own.
    ///   3. **Asymmetric step.** Raising the target on observed jitter happens immediately. Lowering
    ///      is rate-limited to <see cref="MaxDecreasePerTickMs"/> per tick so a brief good window
    ///      doesn't undo the protection a bad event just earned us.
    ///   4. **Skip tuning while underruns are growing.** If the buffer is currently underrunning,
    ///      the system isn't in steady state. Tuning now would react to broken stats.
    ///   5. **Skip if the user just touched the slider** — see <see cref="lastUserSliderMoveUtc"/>.
    /// </summary>
    private void ContinuousTuneTick()
    {
        if (!receiver.IsRunning) return;
        var frameMs = receiver.ActiveStreamFrameMs;
        if (frameMs is null) return;
        if (recentMaxGaps.Count < 2) return;
        // Same deferral when the source list changed: the freshly-added capture's first packets
        // can land slightly off-cadence as its ring buffer fills, and we don't want that
        // transient to influence the recommendation. Applied to every per-route tick.
        var intervalSec = continuousTuneIntervalSec;
        if (DateTime.UtcNow - lastSourceChangeUtc < TimeSpan.FromSeconds(intervalSec)) return;

        // Dispatch per route. Classic modes drive only the Mixed route (the legacy single-knob
        // world). BothIndependent ticks both routes — each respecting its own enable flag,
        // slider, last-user-move timestamp and underrun delta — so the WASAPI lane's distress
        // can't make the ASIO lane's auto-tune defer (and vice versa).
        if (settings.LoadAudioMode() == AudioMode.BothIndependent)
        {
            // Skip ticking a lane that has no active sessions. The shared recentMaxGaps
            // window is populated by every incoming packet regardless of lane, so without
            // this gate a route with no audio would still react to the OTHER route's
            // gap signal and silently inflate its target before any of its own audio has
            // arrived.
            if (continuousTuneEnabled && receiver.HasSessionsForRoute(RenderRoute.WasapiLane))
            {
                TickRoute(RenderRoute.WasapiLane, maxLatencyBox, "WASAPI",
                    ref lastObservedUnderrunCount, ref lastObservedDeviceGulpCount, ref suppressUserSliderMoveTracking,
                    lastUserSliderMoveUtc, intervalSec, frameMs.Value);
            }
            if (settings.LoadContinuousAutoTuneAsioEnabled() && receiver.HasSessionsForRoute(RenderRoute.AsioLane))
            {
                TickRoute(RenderRoute.AsioLane, maxLatencyAsioBox, "ASIO",
                    ref lastObservedUnderrunCountAsio, ref lastObservedDeviceGulpCountAsio, ref suppressUserAsioSliderMoveTracking,
                    lastUserAsioSliderMoveUtc, intervalSec, frameMs.Value);
            }
        }
        else
        {
            if (continuousTuneEnabled)
            {
                TickRoute(RenderRoute.Mixed, maxLatencyBox, "",
                    ref lastObservedUnderrunCount, ref lastObservedDeviceGulpCount, ref suppressUserSliderMoveTracking,
                    lastUserSliderMoveUtc, intervalSec, frameMs.Value);
            }
        }
    }

    // Per-route auto-tune-tick state. lastObservedUnderrunCount + suppress flag are the
    // existing single-route fields; the *Asio variants below are their BothIndependent
    // counterparts. The ref-pass into TickRoute keeps the existing field-update semantics
    // (atomic delta computation, suppress-flag lifecycle) for both routes without needing
    // a heap-allocated state object on the hot path.
    private long lastObservedUnderrunCountAsio;
    private bool suppressUserAsioSliderMoveTracking;
    // Per-route "device-gulp underruns at last tick" — the inaudible, more-buffer-won't-fix
    // partial short-reads the cause-aware skip gate deliberately ignores. Tracked only so the
    // auto-tune log can show how many were ignored; shared between Mixed and the WASAPI lane the
    // same way lastObservedUnderrunCount is.
    private long lastObservedDeviceGulpCount;
    private long lastObservedDeviceGulpCountAsio;

    /// <summary>
    /// Per-route auto-tune tick body. Same algorithm as the pre-2026-05-11 single-route
    /// version, generalised to operate on a route + slider pair passed by the caller. The
    /// gap and render-callback histories (<see cref="recentMaxGaps"/> /
    /// <see cref="recentRenderCbGaps"/>) are still shared across routes — the network signal
    /// is one signal, both lanes ride the same UDP socket — but the underrun delta, the
    /// last-user-slider-move timestamp, and the slider itself are per-route so each lane
    /// settles at its own native latency. Logs include the route name so the diagnostic
    /// trail makes which lane was tuned obvious.
    /// </summary>
    private void TickRoute(
        RenderRoute route,
        NumericUpDown slider,
        string routeLabel,
        ref long lastObservedUnderruns,
        ref long lastObservedDeviceGulps,
        ref bool suppressFlag,
        DateTime lastUserMoveUtc,
        int intervalSec,
        int frameMs)
    {
        // Render period was a hardcoded 10ms here (sized for shared-mode WASAPI). On ASIO
        // with a small buffer (32 samples = 0.67ms callback) the real value is 1-2ms, and
        // the constant inflated every recommendation by 8ms+ for ASIO users. Now derived
        // from the actual render-callback measurements over the same lookback as the gap
        // measurement.
        const int RenderPeriodFloorMs = 2;
        const int SafetyMarginMs = 5;
        const int HysteresisMs = 5;
        const int AutoTuneRecommendationCapMs = 200;
        const int MaxDecreasePerTickMs = 5;
        const int LookbackSeconds = 15;

        // Defer to user's manual change — wait at least one tick interval before overriding.
        if (DateTime.UtcNow - lastUserMoveUtc < TimeSpan.FromSeconds(intervalSec)) return;

        // Per-route underrun delta — but the CAUSE-AWARE kind (2026-06-13). We gate on
        // tune-blocking underruns only: the full-empty / producer-starved short-reads that
        // genuinely mean "the buffer is too thin". A steady trickle of inaudible device-gulp
        // partials — a chunky onboard-Realtek render callback asking for an oversized block on an
        // otherwise on-target ring — is deliberately NOT counted here, so it can no longer pin the
        // target high forever by making every tick skip. The recommendation below still folds in
        // the render-callback gap, so even when we're free to lower we can never lower below what
        // the device structurally needs; it just settles to that floor instead of overshooting up.
        var currentUnderruns = route == RenderRoute.Mixed ? receiver.TuneBlockingUnderruns : receiver.TuneBlockingUnderrunsFor(route);
        var underrunDelta = currentUnderruns - lastObservedUnderruns;
        lastObservedUnderruns = currentUnderruns;
        // Device-gulp delta is tracked for the diagnostic trail only — it never gates.
        var currentDeviceGulps = route == RenderRoute.Mixed ? receiver.DeviceGulpUnderruns : receiver.DeviceGulpUnderrunsFor(route);
        var deviceGulpDelta = currentDeviceGulps - lastObservedDeviceGulps;
        lastObservedDeviceGulps = currentDeviceGulps;
        if (underrunDelta > 0)
        {
            // Route label slots into the message body when present, omitted entirely in classic
            // modes so the legacy "continuous auto-tune: skipping (N new underruns...)" wording
            // is preserved bit-for-bit. The trailing-space + colon ordering is what gave the
            // pre-fix line its weird "continuous auto-tune : skipping" formatting when the
            // label was empty. devGulp shows how many inaudible device-gulp partials were ignored
            // this tick — a high devGulp with a small underrunDelta is the Realtek fingerprint.
            var prefix = string.IsNullOrEmpty(routeLabel) ? "continuous auto-tune" : $"continuous auto-tune {routeLabel}";
            logFile.Event($"{prefix}: skipping ({underrunDelta} new underruns since last tick, devGulp={deviceGulpDelta} ignored)");
            return;
        }

        var sampleCount = Math.Min(LookbackSeconds, recentMaxGaps.Count);
        var skip = recentMaxGaps.Count - sampleCount;
        // Track the TWO highest arrival-gap seconds, not just the worst. A single transient spike —
        // one bad second from an OS/driver hiccup that doesn't recur — used to drive the whole
        // recommendation (one 1046ms gap pushed the buffer straight to the 200ms cap and shed a big
        // trim burst). Using the SECOND-highest requires the jitter to persist across >=2 seconds
        // before it counts, while still honouring sustained jitter at full speed. The true peak is
        // still logged for diagnosis; we fall back to the single value when there's only one sample.
        int gapPeak = 0, gapSecond = 0;
        var i = 0;
        foreach (var gap in recentMaxGaps)
        {
            if (i++ < skip) continue;
            if (gap > gapPeak) { gapSecond = gapPeak; gapPeak = gap; }
            else if (gap > gapSecond) { gapSecond = gap; }
        }
        var observedGap = sampleCount >= 2 ? gapSecond : gapPeak;

        // Same lone-spike rejection for the render-callback gap.
        int rcbPeak = RenderPeriodFloorMs, rcbSecond = RenderPeriodFloorMs;
        var rcbSkip = recentRenderCbGaps.Count - sampleCount;
        var rcbI = 0;
        foreach (var rcb in recentRenderCbGaps)
        {
            if (rcbI++ < rcbSkip) continue;
            if (rcb > rcbPeak) { rcbSecond = rcbPeak; rcbPeak = rcb; }
            else if (rcb > rcbSecond) { rcbSecond = rcb; }
        }
        var observedRenderCb = sampleCount >= 2 ? rcbSecond : rcbPeak;

        var codecFloor = (int)Math.Ceiling(1.5 * frameMs);
        var jitterBased = observedGap + observedRenderCb + SafetyMarginMs;
        var recommended = Math.Max(codecFloor, jitterBased);
        var capped = Math.Min(recommended, AutoTuneRecommendationCapMs);
        var current = (int)slider.Value;

        int target;
        if (capped > current)
        {
            target = capped;
        }
        else
        {
            target = Math.Max(capped, current - MaxDecreasePerTickMs);
        }

        var clamped = Math.Clamp(target, (int)slider.Minimum, (int)slider.Maximum);
        if (Math.Abs(clamped - current) < HysteresisMs) return;

        suppressFlag = true;
        try
        {
            slider.Value = clamped;
        }
        finally
        {
            suppressFlag = false;
        }
        var logPrefix = string.IsNullOrEmpty(routeLabel) ? "continuous auto-tune" : $"continuous auto-tune {routeLabel}";
        logFile.Event($"{logPrefix}: gap-max={gapPeak}ms gap-used={observedGap}ms renderCb={observedRenderCb}ms over {sampleCount}s recommended={recommended}ms capped={capped}ms prev={current}ms applied={clamped}ms frame={frameMs}ms devGulp={deviceGulpDelta}");
    }

    // UpdateTuneButtonEnabled + TuneLatencyAsync retired alongside the one-shot Tune button.
    // The continuous auto-tune toggle on the Audio profile tab is the live successor.

    // ===================== Accessibility helpers (CheckedListBox status labels) =====================

    private void WireCheckedListAccessibility(CheckedListBox list, Label statusLabel, string itemKind)
    {
        // Tick/untick sound for the inputs/outputs AND peer lists. Gated on the list being focused so
        // a real user click/spacebar clicks, but EVERY programmatic (un)check stays silent — via the
        // list-focus gate, CheckSoundService.Suppressed during profile apply, and SuppressingCheckSounds
        // which covers both the device-list mutations and the three peer-list rebuilds (each rebuild
        // fires ItemCheck for its pre-checked rows). Without the peer-list half, a saved-peer reconnect
        // rebuilding the focused connected-peers list at startup would click a checkbox sound. Only a
        // genuine user toggle (no suppression flag set) should click.
        list.ItemCheck += (_, e) =>
        {
            if (list.Focused && !SuppressingCheckSounds) CheckSoundService.Play(e.NewValue == CheckState.Checked);
        };
        list.SelectedIndexChanged += (_, _) =>
        {
            if (list.SelectedIndex >= 0) lastFocusedListIndices[list] = list.SelectedIndex;
            UpdateCheckedListStatus(list, statusLabel, itemKind);
        };
        list.Enter += (_, _) => RestoreListFocus(list, statusLabel, itemKind);
        list.GotFocus += (_, _) => RestoreListFocus(list, statusLabel, itemKind);
        list.MouseDown += (_, args) =>
        {
            var index = list.IndexFromPoint(args.Location);
            if (index >= 0)
            {
                list.SelectedIndex = index;
                lastFocusedListIndices[list] = index;
            }
        };
        // First-letter navigation: highlights the matching item without ever toggling its check.
        // Default CheckedListBox key handling has been observed to (sometimes) toggle the check
        // when a single-letter prefix uniquely matches one item. Bypass that by handling KeyDown
        // ourselves and suppressing the default key processing for letters/digits. Spacebar still
        // falls through to the default handler so users can still toggle with Space.
        list.KeyDown += (_, args) =>
        {
            if (args.Modifiers != Keys.None) return;
            char ch;
            if (args.KeyCode >= Keys.A && args.KeyCode <= Keys.Z)
                ch = (char)('a' + (args.KeyCode - Keys.A));
            else if (args.KeyCode >= Keys.D0 && args.KeyCode <= Keys.D9)
                ch = (char)('0' + (args.KeyCode - Keys.D0));
            else if (args.KeyCode >= Keys.NumPad0 && args.KeyCode <= Keys.NumPad9)
                ch = (char)('0' + (args.KeyCode - Keys.NumPad0));
            else return;

            var startIdx = list.SelectedIndex < 0 ? 0 : list.SelectedIndex + 1;
            for (var offset = 0; offset < list.Items.Count; offset++)
            {
                var idx = (startIdx + offset) % list.Items.Count;
                var text = list.Items[idx]?.ToString() ?? string.Empty;
                if (text.Length > 0 && char.ToLowerInvariant(text[0]) == ch)
                {
                    list.SelectedIndex = idx;
                    break;
                }
            }
            // Always swallow letter/digit keys so the default handler can't toggle anything.
            args.Handled = true;
            args.SuppressKeyPress = true;
        };
        list.ItemCheck += (_, args) =>
        {
            void Update()
            {
                if (list.IsDisposed || statusLabel.IsDisposed) return;
                var checkedNow = args.NewValue == CheckState.Checked;
                UpdateCheckedListStatus(list, statusLabel, itemKind, args.Index, checkedNow);
            }

            if (list.IsHandleCreated) list.BeginInvoke((MethodInvoker)Update);
            else Update();
        };
        UpdateCheckedListStatus(list, statusLabel, itemKind);
    }

    private void RestoreListFocus(CheckedListBox list, Label statusLabel, string itemKind)
    {
        if (list.Items.Count == 0) { UpdateCheckedListStatus(list, statusLabel, itemKind); return; }
        var target = list.SelectedIndex >= 0
            ? list.SelectedIndex
            : lastFocusedListIndices.TryGetValue(list, out var saved) ? Math.Clamp(saved, 0, list.Items.Count - 1) : 0;

        void Restore()
        {
            if (list.IsDisposed || list.Items.Count == 0) return;
            target = Math.Clamp(target, 0, list.Items.Count - 1);
            list.SelectedIndex = target;
            list.TopIndex = Math.Max(0, target);
            lastFocusedListIndices[list] = target;
            UpdateCheckedListStatus(list, statusLabel, itemKind);
            // Force-fire EVENT_OBJECT_FOCUS once the SelectedIndex and AccessibleDescription
            // have been set, so NVDA re-announces the list with its current item state. This is
            // the same load-bearing pattern that fixed the CheckBox state-change announcement.
            WinEventNotifier.NotifyFocus(list);
        }

        if (list.IsHandleCreated) list.BeginInvoke((MethodInvoker)Restore);
        else Restore();
    }

    // Delegates to the ONE shared builder (CheckedListAccessibility.ApplyStatus) so the main
    // window and the dialogs speak word-for-word identical status text — including the
    // remembered-apps empty-state lifecycle line. (These used to be two hand-kept copies that
    // drifted, 2026-07-27.)
    private static void UpdateCheckedListStatus(CheckedListBox list, Label statusLabel, string itemKind, int? overrideIndex = null, bool? overrideChecked = null)
        => CheckedListAccessibility.ApplyStatus(list, statusLabel, itemKind, overrideIndex, overrideChecked);

    /// <summary>
    /// Makes a NumericUpDown's text content fully selected whenever the control receives focus,
    /// so the user's first typed digit replaces the existing value rather than being inserted
    /// into it. Without this, tabbing into a spinner showing "80" and typing "10" produces
    /// "8010" — the WinForms default that nobody wants. Hooks both Enter (keyboard / Tab) and
    /// the underlying TextBox's GotFocus (mouse-click into the field). The Select(0, length)
    /// targets the inner TextBox via NumericUpDown.Select.
    /// </summary>
    private static void SelectAllOnFocus(NumericUpDown box)
    {
        void SelectAll() => box.Select(0, box.Text.Length);
        box.Enter += (_, _) => SelectAll();
        // The inner TextBox's own GotFocus also fires when the user clicks directly into the
        // text portion of the spinner. Subscribe defensively to it as well.
        foreach (Control c in box.Controls)
        {
            if (c is TextBox tb)
            {
                tb.GotFocus += (_, _) => SelectAll();
                break;
            }
        }
    }

    private void FocusControl(Control control)
    {
        if (!control.CanFocus) return;
        control.Focus();
        if (control is ComboBox combo && combo.Items.Count > 0 && combo.SelectedIndex < 0) combo.SelectedIndex = 0;
        // Same defensive pre-select for ListBox so NVDA reads the current item on first focus
        // (otherwise an unselected list is announced as just "list" with no item).
        if (control is ListBox listBox && listBox.Items.Count > 0 && listBox.SelectedIndex < 0) listBox.SelectedIndex = 0;
        // 2026-05-06: removed the WinEventNotifier.NotifyFocus(control) call here. It was
        // forcing NVDA to re-announce on every Focus() — and I now suspect that's why NVDA
        // sometimes reads "tab control" before the focused control: the explicit focus
        // event triggers a fresh role-context announcement. Andre's app doesn't fire any
        // such events. Trying without it.
    }

    private void FocusListControl(CheckedListBox list)
    {
        // Pre-select an item BEFORE calling Focus(). The previous order was Focus() → then
        // RestoreListFocus → BeginInvoke → SelectedIndex = N. That defers the selection past
        // NVDA's first focus-event announcement, so NVDA reads only the list's name and not
        // the current item. Setting SelectedIndex synchronously here means the focus event
        // fires with the list already pointing at item N, so NVDA reads "<name>, list, item N
        // of M: <text>, <state>" in one go.
        var statusLabel = list == sendOutputDevicesList
            ? sendOutputDevicesStatusLabel
            : list == sendInputDevicesList
                ? sendInputDevicesStatusLabel
                : list == receiveOutputDevicesList
                    ? receiveOutputDevicesStatusLabel
                    : list == asioSendDevicesList
                        ? asioSendDevicesStatusLabel
                        : list == asioReceiveOutputDevicesList
                            ? asioReceiveOutputDevicesStatusLabel
                            : new Label();
        var itemKind = list == sendOutputDevicesList
            ? "output device"
            : list == sendInputDevicesList
                ? "input device"
                : list == receiveOutputDevicesList
                    ? "receive output device"
                    : list == asioSendDevicesList
                        ? "ASIO send channel"
                        : list == asioReceiveOutputDevicesList
                            ? "ASIO receive channel"
                            : "item";
        if (list.Items.Count > 0 && list.SelectedIndex < 0)
        {
            var target = lastFocusedListIndices.TryGetValue(list, out var saved)
                ? Math.Clamp(saved, 0, list.Items.Count - 1)
                : 0;
            list.SelectedIndex = target;
            list.TopIndex = Math.Max(0, target);
            lastFocusedListIndices[list] = target;
        }
        UpdateCheckedListStatus(list, statusLabel, itemKind);
        list.Focus();
        WinEventNotifier.NotifyFocus(list);
    }

    /// <summary>Prompt the user to save unsaved profile changes before exiting. Skipped when
    /// the close is a profile-switch / folder-change reload (Program.cs handles re-launching
    /// the form on the new profile, and we don't want to nag during that handoff). The
    /// MessageBox is Yes/No/Cancel: Yes = save (save-as flow on blank template), No = exit
    /// without saving, Cancel = stay in the form.</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Skip the prompt during profile-switch handoff or forced reload — those are
        // controlled close paths where the user has already confirmed their intent via the
        // management dialog, and the MainForm gets reconstructed under the new profile
        // immediately afterwards.
        //
        // Also skip the prompt when the active profile is read-only — the whole point of
        // read-only mode (Andre's request, 2026-05-22) is that the user has explicitly
        // declared "anything I changed this session is throwaway, don't save it and don't
        // ask me about it". Without this branch the dirty-prompt would block shutdown on
        // a profile where the user wants exactly the opposite: silent exit. Crucially this
        // is what unblocks NVDA-less or remote-session-dropped shutdowns from deadlocking
        // on a dialog the user can't reach.
        var skipPrompt = !string.IsNullOrEmpty(NextProfileTitleToLoad) || ReloadFromScratch
            || LoadBlankTemplateNext || currentProfileReadOnly || updatingInProgress;

        if (!skipPrompt && profileStore is not null && unsavedChanges)
        {
            // Originally this also did a JSON-state diff against a baseline snapshot as a
            // backstop for hooks we forgot to wire. Removed 2026-05-05 because it caused
            // false-positive prompts: continuous auto-tune routinely nudges MaxLatencyMs while
            // the user just listens, and the diff would catch those auto-internal changes as
            // "user changes". Now we trust the dirty flag exclusively. The risk of missing a
            // hook (false-NEGATIVE — user changes something via an unhooked path, no prompt
            // on close) is acceptable; the previous false-POSITIVE behaviour was nagging.
            {
                var result = MessageBox.Show(this,
                    "You have unsaved changes to your profile. Save them before exiting?\n\n" +
                    "Yes — save and exit.\nNo — exit without saving.\nCancel — keep RemSound open.",
                    "RemSound — unsaved changes",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button3);

                if (result == DialogResult.Cancel)
                {
                    e.Cancel = true;
                    return; // stay; don't fire base.OnFormClosing or the cleanup chain.
                }
                if (result == DialogResult.Yes)
                {
                    if (string.IsNullOrEmpty(currentProfileTitle))
                    {
                        // Blank template — need a name. Save-as prompt; if the user cancels
                        // the prompt, treat that as "I changed my mind, don't exit either".
                        var title = ProfileSaveAsPrompt.Show(this, profileStore, null);
                        if (string.IsNullOrEmpty(title))
                        {
                            e.Cancel = true;
                            return;
                        }
                        SaveProfileTo(title, showConfirmation: false);
                    }
                    else
                    {
                        SaveProfileTo(currentProfileTitle, showConfirmation: false);
                    }
                }
                // result == No falls through to a normal close.
            }
        }

        // Stop any active recording before the engines tear down. The recorder will flush
        // its queue and close the file cleanly. Done here (rather than in Dispose) because
        // we want the on-disk file finalised before the form closes, so opening the
        // recordings folder right after exit shows the file at its full size.
        try { recordingController.Stop(); } catch { /* recording cleanup is best-effort */ }

        // Flush named-peers last-seen timestamps (updated in memory each tick, only saved to disk on
        // address change during the session) so the Manage named peers dialog shows fresh times next run.
        if (namedPeers.Count > 0) SaveNamedPeers();

        base.OnFormClosing(e);
    }
}
