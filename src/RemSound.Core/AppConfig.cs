using System.Text.Json;

namespace RemSound.Core;

/// <summary>How often the self-updater polls GitHub Releases for a newer build. Values are
/// stable: don't reorder; deserialisation reads the underlying int from <c>global config.json</c>.</summary>
public enum UpdateCheckFrequency
{
    Never = 0,
    EveryHour = 1,
    Every6Hours = 2,
    Every24Hours = 3,
}

/// <summary>
/// What happens when somebody else ticks you. Ed, 2026-09-20. Values are stable: don't reorder;
/// deserialisation reads the underlying int from <c>global config.json</c>.
/// </summary>
public enum PeerAcceptMode
{
    /// <summary>Ask, with their name and where they are, and do nothing until the answer comes. The default.</summary>
    Prompt = 0,
    /// <summary>Tick them back the moment they tick you, so sound flows without either of you doing anything more.</summary>
    Automatic = 1,
    /// <summary>Nothing happens until you tick them yourself, on both sides. How RemSound has always worked.</summary>
    Manual = 2,
}

/// <summary>
/// App-level configuration, kept as <c>global config.json</c> in the user-data folder
/// (<c>&lt;exe&gt;\user settings and logs\</c>, see <see cref="UserDataDirectory"/>).
/// Distinct from <see cref="Profile"/>: profiles are user-chosen sets of audio /
/// connectivity / device settings; the app config is the *meta* layer that holds
/// preferences that should be sticky regardless of which profile is loaded — the profiles
/// folder, logging, cues, keyboard shortcuts, the remembered peers and applications, update
/// behaviour and so on. Profiles are per-setup; this file is per-installation.
///
/// If the file is missing or malformed, <see cref="Load"/> returns defaults. Keys an older build wrote
/// that this one no longer has — <c>BothModeWarningSuppressed</c>, retired with the WASAPI+ASIO latency
/// popup on 2026-05-11, for one — are ignored on load.
/// </summary>
public sealed class AppConfig
{
    /// <summary>Filesystem path to the directory the app should read profiles from. When
    /// null, RemSound uses the default,
    /// <c>&lt;exe&gt;\user settings and logs\profiles\&lt;machine&gt;\</c>
    /// (<see cref="ProfilesBaseDirectory"/> plus the machine name), and so does a folder that
    /// no longer exists (<see cref="CreateStore"/>).
    /// When set to an explicit folder, that folder IS the profiles folder — no per-machine
    /// subfolder is appended (the user picked it, they meant it; that also lets a user point
    /// at a Dropbox folder shared between machines).</summary>
    public string? ProfilesDirectory { get; set; }

    /// <summary>True if the user has ticked "do not show me this message again" on the
    /// confirmation popup that fires when Save (Ctrl+S / File → Save) successfully
    /// overwrites the currently-loaded profile. Lives here (not in Profile) so the
    /// preference sticks across profile switches — once you've decided you don't need
    /// the "Profile saved" nag, you don't expect it to come back when you load a
    /// different profile. The Save-As path doesn't use this flag: the Save-As dialog
    /// itself is the user-visible confirmation, so a follow-up popup is redundant.</summary>
    public bool SaveProfileConfirmationSuppressed { get; set; }

    /// <summary>True if the user has ticked "do not show me this message again" on the
    /// "you are saving onto a read-only profile" warning. Once ticked, Ctrl+S / File → Save
    /// on a read-only profile saves silently through the lock instead of warning first.
    /// Machine-local (not per-profile) so the preference sticks across profile switches; the
    /// prompt itself is the same wording on any read-only profile so a single dismissal
    /// applies everywhere. 2026-05-23 — semantic shift from v2.x: in v2.x the read-only lock
    /// hard-blocked explicit saves and this flag suppressed the explanatory "save was skipped"
    /// popup. In v3.0 the lock only suppresses the automatic "you have unsaved changes" prompt
    /// on close / profile switch; explicit Ctrl+S / File → Save now goes through with a
    /// one-time warning gated by this flag. The JSON key was renamed alongside the semantic
    /// change so users upgrading from v2.x see the new warning at least once — a v2.x
    /// suppression flag is no longer applicable and is silently discarded on load.</summary>
    public bool SaveOnReadOnlyWarningSuppressed { get; set; }

    /// <summary>If true, RemSound minimises to the system tray immediately after the main
    /// window finishes loading. Lets the user "boot up the machine and have RemSound
    /// already running quietly". Default false.</summary>
    public bool StartMinimised { get; set; }

    /// <summary>If true (the default), the main window shows a "Volume, pan and EQ for peers" tab
    /// (positioned before "Audio profile") for setting per-peer volume, panning and EQ. Toggled by
    /// "Show the volume, pan and EQ for peers tab" on the Preferences General tab. Machine-wide (a
    /// UI-visibility preference, like which tabs exist) — the shaping VALUES are saved per profile, in
    /// <see cref="Profile.PeerShaping"/>.</summary>
    public bool ShowPanEqTab { get; set; } = true;

    /// <summary>Which colour theme the window uses: "system" (follow Windows light/dark — the default),
    /// "light", or "dark". Read once at startup by Program.Main and applied via Application.SetColorMode;
    /// changing it takes effect on the next launch. Machine-wide, purely visual.</summary>
    public string ThemeMode { get; set; } = "system";

    /// <summary>The order of the main-window tabs, as tab keys ("connectivity", "audioio", "paneq",
    /// "audioprofile"). Null / incomplete falls back to the default order. Set from the Preferences
    /// Appearance tab. Machine-wide.</summary>
    public List<string>? MainTabOrder { get; set; }

    /// <summary>Whether the Discovered / Remembered peer lists appear on the Connectivity tab. Both on
    /// by default; toggled on the Preferences Appearance tab.</summary>
    public bool ShowDiscoveredPeers { get; set; } = true;
    public bool ShowRememberedPeers { get; set; } = true;

    /// <summary>The machine-wide "named peers" book — peers the user has deliberately renamed, keyed by
    /// the peer's stable identity (machine name, or address for a nameless manual peer). A name applies in
    /// every profile and shows wherever that peer appears. Managed via the Connectivity tab's Rename peer
    /// and the Options → Manage named peers dialog. Empty by default.</summary>
    public Dictionary<string, NamedPeer> NamedPeers { get; set; } = new();

    /// <summary>Machine-wide remembered PEER entries — ONE shared address book across all profiles
    /// (Ed, 2026-07: both remembered lists live in global, not the profile). Before this the list rode
    /// in each profile's JSON, so it was per-profile in practice; each old profile's legacy list is
    /// unioned in here ONCE (RemSoundSettingsStore.MigrateRememberedPeersToGlobal, gated by
    /// <see cref="RememberedPeersMigrated"/>). Null = none yet. Cleared from Preferences → General.</summary>
    public List<string>? RememberedPeers { get; set; }

    /// <summary>Set true after the one-time migration of a profile's legacy per-profile peers into
    /// <see cref="RememberedPeers"/>. Without this the migration re-ran every launch and re-unioned the
    /// profile file's stale copy — which silently resurrected peers the user had just cleared.</summary>
    public bool RememberedPeersMigrated { get; set; }

    /// <summary>Machine-wide remembered APPLICATION process names (lower-case) — the shared "apps I
    /// send" address book, companion to <see cref="RememberedPeers"/>. Before 2026-07-16 this only
    /// lived in the in-memory settings cache, which silently forgot the list on every app exit. Null =
    /// none yet. Cleared from Preferences → General.</summary>
    public List<string>? RememberedApplications { get; set; }

    /// <summary>If true (the default), RemSound plays the startup cue once, right after this
    /// copy wins the single-instance takeover and before the profile loads. Machine-wide (not
    /// per-<see cref="Profile"/>) because it fires before any profile — and its per-profile
    /// custom-cue dictionary — has been chosen. Like the connect/disconnect cues it's audible
    /// feedback, on by default; the user unticks "Startup sound" in Preferences to silence it.
    /// (The usual "auto-options default off" rule is about data-persistence toggles, not cues.)</summary>
    public bool EnableStartupCue { get; set; } = true;

    /// <summary>Optional custom WAV path for the startup cue. Null = the default startup sound from
    /// <see cref="SoundsDirectory"/> (<c>default sounds\</c> next to the exe). Machine-wide for the
    /// same reason as <see cref="EnableStartupCue"/>: the cue plays before any profile (and the
    /// per-profile custom-cue paths) is loaded, so it can't live on <see cref="Profile"/>.</summary>
    public string? StartupCueCustomPath { get; set; }

    /// <summary>The chosen default-sound FILENAME for each cue (e.g. "connect 2.wav"), keyed by
    /// the cue id (<c>MainForm.CueId</c>). The cue WAVs ship as numbered variants ("connect 1.wav",
    /// "connect 2.wav", ...); this records which one the user picked in Preferences. Machine-wide,
    /// so a user's preferred sound palette follows them across every profile. A cue absent from the
    /// dictionary uses the first available variant (the "1"s) by default. A per-profile custom WAV
    /// (<see cref="Profile.CustomCuePaths"/>) still overrides this choice.</summary>
    public Dictionary<string, string> DefaultCueSounds { get; set; } = new();

    /// <summary>If true (the default), typing into any edit field anywhere in RemSound plays a soft
    /// keyboard-click sound (one of several, picked at random), so a screen-reader user gets audible
    /// typing feedback. Password fields additionally play a distinct key sound at the same time.
    /// Machine-wide; the user unticks "Play keyboard clicks" in Preferences to silence it.</summary>
    public bool EnableKeyboardClicks { get; set; } = true;

    /// <summary>Whether a peer's pan and EQ (set in RemSound) are applied to the audio the VST plugin
    /// hands to the DAW. ON by default — "what you hear is what you get" is the least surprising
    /// behaviour, and it costs nothing: the shaping is per-sample, already in the read path the
    /// plugin pulls from, and adds no latency whatsoever. Turn it OFF to receive a peer RAW and do
    /// the EQ in the DAW instead — the same choice the recording feature already offers
    /// (raw vs "record what you hear"). Machine-wide: it describes how this machine's plugin
    /// behaves, not anything about a particular profile's peers.</summary>
    public bool ApplyPeerShapingToPlugin { get; set; } = true;

    /// <summary>Whether RemSound listens for VST plugin instances on this machine. ON by default,
    /// because a plugin that quietly does nothing until you find a hidden switch is worse than no
    /// plugin at all. Off closes the loopback port entirely — for anyone who doesn't use a DAW and
    /// would rather nothing was listening, and as the honest answer to "is RemSound opening a port I
    /// didn't ask for?". With it off the plugin says so plainly instead of sitting mute. Machine-wide:
    /// it describes this machine, not a profile.</summary>
    public bool EnableDawPluginLink { get; set; } = true;

    /// <summary>Per-cue enable flags for the machine-wide cues added 2026-06-13: the send/receive
    /// on/off toggle cues and the minimise(hide)/restore(show) cues. Machine-wide (like the startup
    /// cue) rather than per-profile - they're app-level feedback for an action, not a per-profile
    /// audio setting. All default on; the user unticks them in Preferences like any other cue.</summary>
    public bool EnableSendOnCue { get; set; } = true;
    public bool EnableSendOffCue { get; set; } = true;
    public bool EnableReceiveOnCue { get; set; } = true;
    public bool EnableReceiveOffCue { get; set; } = true;
    public bool EnableHideCue { get; set; } = true;
    public bool EnableShowCue { get; set; } = true;
    /// <summary>Tick / untick sounds played on every checkbox toggle anywhere in the app.</summary>
    public bool EnableCheckboxOnCue { get; set; } = true;
    public bool EnableCheckboxOffCue { get; set; } = true;
    /// <summary>Sound played whenever the user switches between tabs anywhere in the app (the main
    /// window's tab strip and every tabbed dialog). Machine-wide, default on.</summary>
    public bool EnableTabSwitchCue { get; set; } = true;

    /// <summary>Custom WAV overrides for the machine-wide cues above, keyed by cue id. The
    /// equivalent of <see cref="Profile.CustomCuePaths"/> but machine-wide, since these cues don't
    /// live on a profile. Empty = use the chosen default variant. The startup cue keeps its own
    /// <see cref="StartupCueCustomPath"/> field for backward compatibility.</summary>
    public Dictionary<string, string> MachineCueCustomPaths { get; set; } = new();

    /// <summary>If true, RemSound writes a tab-separated diagnostic log to <see cref="LogsDirectory"/>
    /// (<c>&lt;exe&gt;\user settings and logs\logs\</c>). Lives here (not in <see cref="Profile"/>)
    /// because logging is a debugging affordance for the installation, not a user-facing audio
    /// preference — switching profiles shouldn't accidentally re-enable a flood of writes the user
    /// had turned off, and a one-machine "yes log everything" decision shouldn't have to ride
    /// along on every saved profile. Default false: no log file is created until the user
    /// ticks <em>Enable logs</em> in the Preferences dialog.</summary>
    public bool LoggingEnabled { get; set; }

    /// <summary>If true, RemSound checks the total size of the <c>logs\</c> folder at startup and,
    /// when it exceeds <see cref="LogsFolderWarnThresholdMb"/> megabytes, shows a one-time warning so
    /// the user can prune or clear it. Off by default (opt-in, like the other behaviour toggles).
    /// Machine-local — a "watch my disk on this box" decision, not a per-profile audio setting.</summary>
    public bool WarnIfLogsFolderExceeds { get; set; }

    /// <summary>The size threshold in megabytes for <see cref="WarnIfLogsFolderExceeds"/>. Default
    /// 100 MB. Only consulted when that flag is on.</summary>
    public int LogsFolderWarnThresholdMb { get; set; } = 100;

    /// <summary>If true, RemSound deletes log files older than <see cref="PruneOldLogsDays"/> days
    /// from the <c>logs\</c> folder at startup. Off by default (opt-in). The currently-open log file
    /// is never a candidate (it's today's). Machine-local.</summary>
    public bool PruneOldLogs { get; set; }

    /// <summary>Age in days for <see cref="PruneOldLogs"/>: log files last written more than this many
    /// days ago are deleted at startup. Range 1–30, default 14. Only consulted when that flag is on.</summary>
    public int PruneOldLogsDays { get; set; } = 14;

    /// <summary>The keyboard shortcuts, machine-wide as of v4.4. Before v4.4 these lived on each
    /// <see cref="Profile"/>, so a shortcut set on one profile didn't apply on another (issue #14).
    /// They now live here — one set shared by every profile, loaded/saved via
    /// <see cref="RemSoundSettingsStore"/>'s Load*/Save* hotkey methods. Null = use the built-in default
    /// for that action.</summary>
    public HotkeyRecord? ReceiveMuteHotkey { get; set; }
    public HotkeyRecord? SendMuteHotkey { get; set; }
    public HotkeyRecord? TrayHotkey { get; set; }
    public HotkeyRecord? VolumeUpHotkey { get; set; }
    public HotkeyRecord? VolumeDownHotkey { get; set; }
    public HotkeyRecord? ToggleRecordingHotkey { get; set; }
    public HotkeyRecord? RemoteVolumeUpHotkey { get; set; }
    public HotkeyRecord? RemoteVolumeDownHotkey { get; set; }
    public HotkeyRecord? RemoteMuteToggleHotkey { get; set; }
    public HotkeyRecord? SystemVolumeUpHotkey { get; set; }
    public HotkeyRecord? SystemVolumeDownHotkey { get; set; }
    public HotkeyRecord? SystemMuteToggleHotkey { get; set; }
    public HotkeyRecord? QuickProfileSwitchHotkey { get; set; }
    public HotkeyRecord? SpeakStatusLineHotkey { get; set; }
    /// <summary>Global shortcut that toggles the "Enable volume, pan and EQ for all peers" master
    /// switch. Machine-wide and unset by default; NOT stored in any profile (unlike the shaping
    /// values). The user binds it in Keyboard shortcuts.</summary>
    public HotkeyRecord? ToggleAllPeerShapingHotkey { get; set; }

    /// <summary>If true, the "Use Windows default output" follower at the top of the received-sound
    /// output list is ticked at startup, so received sound plays to whatever Windows currently calls
    /// the default output and re-routes when that changes (plug in headphones, it follows). Unlike a
    /// specific device tick — which can go stale and play to the wrong card — a follower can never be
    /// wrong, so it's safe to persist. Default false (opt-in). <see cref="UseDefaultInputDevice"/> is
    /// the same idea for the send-input list.</summary>
    public bool UseDefaultOutputDevice { get; set; }
    public bool UseDefaultInputDevice { get; set; }
    /// <summary>Same idea for the "WASAPI audio outputs to send" (system-audio / loopback) list: when
    /// true, RemSound sends the system audio of whatever Windows currently uses as the default OUTPUT
    /// device, following it if the default changes. Machine-wide, opt-in, default false.</summary>
    public bool UseDefaultLoopbackSend { get; set; }

    /// <summary>If non-null and a profile with this title exists, RemSound skips the
    /// startup profile picker and loads this profile directly. Combine with
    /// <see cref="StartMinimised"/> + the Windows auto-start registry entry
    /// (see <c>StartupAutoStart</c>) to get a fully unattended boot-into-streaming flow.
    /// To re-show the picker temporarily, untick "Start with a specific profile" on the
    /// Preferences dialog's Startup behaviour tab. Null = always show the picker (the default).</summary>
    public string? StartWithProfileTitle { get; set; }

    /// <summary>How often (in minutes) RemSound auto-saves the current profile if it's NOT read-only and
    /// has unsaved changes. 0 = never (the default). Set in Preferences → General. The auto-save is
    /// SILENT — it never plays the save cue or shows the confirmation. Machine-wide.</summary>
    public int AutoSaveNonReadOnlyMinutes { get; set; }

    /// <summary>What happens when somebody else ticks you: ask (the default), tick them back at once, or nothing
    /// until you tick them yourself. Machine-wide, so it holds whichever profile is loaded. Set in Preferences →
    /// General. Somebody on a different password is never offered or accepted: their audio could not be played
    /// anyway, so there is nothing to agree to.</summary>
    public PeerAcceptMode AcceptPeerConnections { get; set; } = PeerAcceptMode.Prompt;

    // The send-only service's profile + settings live in the machine-wide RemSound.Core.ServiceStore
    // (ProgramData), NOT here — AppConfig is per-user, but the service runs as SYSTEM and needs the same
    // file the user wrote. (ServiceProfileName / ServiceLoggingEnabled were moved there 2026-07-12.)

    /// <summary>How often RemSound polls the GitHub Releases API for a newer build. Default
    /// <see cref="UpdateCheckFrequency.Every24Hours"/>. Set to <see cref="UpdateCheckFrequency.Never"/>
    /// to disable background checks entirely (the user can still trigger a manual check via
    /// the Preferences button or the Help menu).</summary>
    public UpdateCheckFrequency UpdateCheckFrequency { get; set; } = UpdateCheckFrequency.Every24Hours;

    /// <summary>If true, RemSound downloads and applies a new release without prompting:
    /// the running instance writes the new files to a staging folder, spawns a small
    /// detached helper that waits for the exe to exit, swaps in the new files, and restarts
    /// RemSound. Default false — the user gets a confirmation dialog before each install.</summary>
    public bool SilentlyInstallUpdates { get; set; }

    /// <summary>If true, AUTOMATIC update installs (background poll + startup check) only happen
    /// inside the daily local-time window below — so an update never kills someone's sound while
    /// they're using the machine; it lands overnight instead. Manual "Check for updates now" is
    /// not gated. Default false (house rule: persistence defaults off). See UpdateWindow (Core)
    /// for the semantics (start inclusive, end exclusive, wraps past midnight).</summary>
    public bool UpdateWindowEnabled { get; set; }

    /// <summary>Window start, minutes-of-day local time, 15-minute granularity. Default 01:00.</summary>
    public int UpdateWindowStartMinutes { get; set; } = 60;

    /// <summary>Window end, minutes-of-day local time, 15-minute granularity. Default 06:00.</summary>
    public int UpdateWindowEndMinutes { get; set; } = 360;

    /// <summary>If true (the default), RemSound runs an update check shortly after launch in
    /// addition to whatever <see cref="UpdateCheckFrequency"/> drives in the background. The
    /// startup check is what catches users who quit and re-open the app within the polling
    /// interval — without it they could miss an update for hours. Set to false to disable the
    /// startup check; the periodic timer (if set) still runs.</summary>
    public bool CheckForUpdatesOnStartup { get; set; } = true;

    /// <summary>If true, RemSound opens the About box (which leads with the latest release
    /// notes) once on the first launch AFTER an update has been installed, so the user sees
    /// "what's new" without going looking. Detected by a one-shot marker the updater writes only after
    /// a successful update, so it never fires on an ordinary relaunch. On by default — it's a
    /// discoverability aid (see what changed), not a data-persistence toggle, so the usual
    /// "auto-options default off" rule doesn't really apply; users can untick it.</summary>
    public bool ShowWhatsNewAfterUpdate { get; set; } = true;

    /// <summary>If true, RemSound tries to open the audio port (UDP 47830) on the local router
    /// using UPnP / NAT-PMP / PCP, so peers on the public internet can reach this machine
    /// without manual port forwarding. Default false — the toggle opt-in only, because some
    /// networks (corporate, hostile shared) shouldn't have apps poking the router. When
    /// successful, RemSound surfaces the external address in the Preferences dialog so the
    /// user knows what to give peers. Falls back gracefully when the router doesn't support
    /// UPnP — RemSound just doesn't open anything.</summary>
    public bool UpnpEnabled { get; set; }

    /// <summary>Most-recently-opened profile paths, newest first, capped at
    /// <see cref="MaxRecentProfiles"/>. Populated by <see cref="NoteRecentProfile"/> every
    /// time a profile is loaded, surfaced in the File → Recent profiles submenu. Stored as
    /// full paths so profiles saved outside the canonical profiles folder are also
    /// reachable (Save-As to an arbitrary path stays in the recents list).</summary>
    public List<string> RecentProfiles { get; set; } = new();

    /// <summary>Cap on how many entries we keep in <see cref="RecentProfiles"/>. Five is the
    /// most that fits comfortably as 1–5 single-digit mnemonics inside a submenu without
    /// the user needing to read the names to remember which row they want.</summary>
    public const int MaxRecentProfiles = 5;

    /// <summary>Push a profile path to the front of the recents list. Removes any existing
    /// entry that matches (case-insensitive) so a recently re-opened profile rises to the
    /// top instead of being duplicated. Caps the list at <see cref="MaxRecentProfiles"/>.
    /// Caller must <see cref="Save"/> after mutating.</summary>
    public void NoteRecentProfile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        RecentProfiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentProfiles.Insert(0, path);
        while (RecentProfiles.Count > MaxRecentProfiles)
        {
            RecentProfiles.RemoveAt(RecentProfiles.Count - 1);
        }
    }

    /// <summary>Friendly names of ASIO drivers RemSound must never touch — it won't probe them,
    /// won't list them in the driver picker, and won't open them for streaming. Global (not
    /// per-profile) because "this driver is broken on this machine" is about the hardware/driver
    /// install, not any one profile. Populated when the user answers "yes" to the Realtek-ASIO
    /// compatibility warning, or toggles the Options-menu entry. Matched case-insensitively.</summary>
    public List<string> DisabledAsioDrivers { get; set; } = new();

    /// <summary>Friendly names of ASIO drivers RemSound has already shown its compatibility warning
    /// for, so a user who answered "no, keep using it" isn't nagged on every launch. Independent of
    /// <see cref="DisabledAsioDrivers"/>: a driver can be warned-about-but-still-enabled.</summary>
    public List<string> AsioDriversWarnedAbout { get; set; } = new();

    /// <summary>This copy of RemSound's id in a relay group (GitHub #29, 2026-09-18). Made the first time it is needed
    /// and kept, so the same person keeps the same place in everyone else's app — and the volume and pan they gave
    /// them. The app and the send-only service share it: they are one person, never on the network at the same time.</summary>
    public string? RelayClientId { get; set; }

    /// <summary>Relay servers this machine has connected to, newest first, offered again in the Connectivity tab.
    /// Cleared from Preferences → General, beside the remembered peers and applications.</summary>
    public List<string> RememberedRelays { get; set; } = new();

    /// <summary>True once the user has ticked "don't show this again" on the notice shown when connecting to a
    /// relay. Machine-wide: it explains how relays work, not anything about one set of people.</summary>
    public bool RelayNoticeSuppressed { get; set; }

    /// <summary>The relay-group id, made and saved on first use. Never throws: if it cannot be saved, a fresh id still
    /// works for this run; it just won't be the same next time.</summary>
    public static Guid LoadOrCreateRelayClientId()
    {
        var config = Load();
        if (Guid.TryParse(config.RelayClientId, out var id) && id != Guid.Empty) return id;
        id = Guid.NewGuid();
        config.RelayClientId = id.ToString("D");
        try { config.Save(); } catch { /* this run still works */ }
        return id;
    }

    /// <summary>True if RemSound should refuse to interact with the named ASIO driver in any way.</summary>
    public bool IsAsioDriverDisabled(string? driverName) =>
        !string.IsNullOrWhiteSpace(driverName)
        && DisabledAsioDrivers.Exists(d => string.Equals(d, driverName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Disable or re-enable the named ASIO driver. Caller must <see cref="Save"/> after.</summary>
    public void SetAsioDriverDisabled(string driverName, bool disabled)
    {
        if (string.IsNullOrWhiteSpace(driverName)) return;
        DisabledAsioDrivers.RemoveAll(d => string.Equals(d, driverName, StringComparison.OrdinalIgnoreCase));
        if (disabled) DisabledAsioDrivers.Add(driverName);
    }

    /// <summary>True once the compatibility warning has been shown for this driver. Case-insensitive.</summary>
    public bool HasWarnedAboutAsioDriver(string driverName) =>
        AsioDriversWarnedAbout.Exists(d => string.Equals(d, driverName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Record that the compatibility warning has been shown for this driver (so we don't
    /// re-nag a user who chose to keep it). Caller must <see cref="Save"/> after.</summary>
    public void MarkAsioDriverWarned(string driverName)
    {
        if (string.IsNullOrWhiteSpace(driverName)) return;
        if (!HasWarnedAboutAsioDriver(driverName)) AsioDriversWarnedAbout.Add(driverName);
    }

    /// <summary>True if the named ASIO driver looks like a Realtek HD Audio ASIO driver (its name
    /// or description contains "Realtek"). Realtek's bundled ASIO driver (rthdasio64.dll) leaks OS
    /// handles on every open and is broadly known to misbehave with ASIO hosts; ASUS and other OEMs
    /// ship the same Realtek driver under their own branding, so we match "Realtek" anywhere in the
    /// name. RemSound uses this to proactively offer to disable the driver.</summary>
    public static bool IsRealtekAsioDriver(string? driverName) =>
        !string.IsNullOrWhiteSpace(driverName)
        && driverName.Contains("Realtek", StringComparison.OrdinalIgnoreCase);

    /// <summary>The single per-user folder next to the exe — <c>&lt;exe&gt;\user settings and logs\</c> —
    /// that holds EVERYTHING this machine's user owns: the global config file, the <c>profiles\</c>
    /// subfolder and <c>logs\</c>. One folder keeps the install root tidy and lets the auto-updater
    /// exclude it, leaving all user state untouched. Shipped cues live in <see cref="SoundsDirectory"/>.</summary>
    public const string UserDataFolderName = "user settings and logs";

    /// <summary>Process-wide override for <see cref="UserDataDirectory"/>. Null = the default
    /// folder next to the exe. Set once at startup from the <c>--config-dir</c> switch so the test
    /// suite (and a portable layout) can point ALL user state - config, profiles, logs, cue sounds -
    /// at an explicit throwaway folder without touching the user's real settings. Must be set before
    /// anything reads config/profiles/logs/sounds.</summary>
    private static string? _userDataDirectoryOverride;

    /// <summary>Redirect every user-state folder to <paramref name="path"/> for this process only.
    /// Call before any config read. Idempotent.</summary>
    public static void SetUserDataDirectoryOverride(string path)
    {
        if (!string.IsNullOrWhiteSpace(path)) _userDataDirectoryOverride = Path.GetFullPath(path);
    }

    /// <summary>Redirect ALL user state to a throwaway folder for the lifetime of the returned scope,
    /// then put it back. Exists so the self-test can drive controls that PERSIST (the Preferences
    /// dialog writes on change) without touching the real configuration — the gate must never edit
    /// the user's settings as a side effect of proving a checkbox works. Restores on dispose even if
    /// the test throws, and deletes the throwaway folder.</summary>
    public static IDisposable UseThrowawayUserDataDirectory(string path) => new UserDataScope(path);

    private sealed class UserDataScope : IDisposable
    {
        private readonly string? previous;
        private readonly string dir;
        public UserDataScope(string path)
        {
            previous = _userDataDirectoryOverride;
            dir = Path.GetFullPath(path);
            Directory.CreateDirectory(dir);
            _userDataDirectoryOverride = dir;
        }
        public void Dispose()
        {
            _userDataDirectoryOverride = previous;
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    public static string UserDataDirectory =>
        _userDataDirectoryOverride ?? Path.Combine(AppContext.BaseDirectory, UserDataFolderName);

    /// <summary>Where the per-machine log files are written.</summary>
    public static string LogsDirectory => Path.Combine(UserDataDirectory, "logs");

    /// <summary>A tiny file at a FIXED per-user location saying where this machine's RemSound keeps
    /// its user data, and whether logging is on.
    ///
    /// <para><b>Why it exists.</b> Everything above hangs off <see cref="AppContext.BaseDirectory"/>,
    /// which is correct for the app and wrong for the VST plugin: inside a DAW that is the DAW's own
    /// folder. The plugin therefore looked for the config somewhere RemSound has never been, found
    /// nothing, and wrote no log at all — so the one file a tester was asked to send never existed
    /// (Anthony Reyers, 2026-08-16).</para>
    ///
    /// <para>A pointer file rather than asking the app over the link, because the case where a log
    /// matters MOST is the one where the app is not answering.</para></summary>
    public static string PluginPointerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemSound", "plugin-home.txt");

    /// <summary>Record where this copy of RemSound lives, for any plugin running in a DAW. Called
    /// whenever the app starts and whenever the logging setting changes. Best-effort by design: a
    /// machine where this cannot be written still runs, it just leaves the plugin without a log.</summary>
    public static void WritePluginPointer(bool loggingEnabled)
    {
        try
        {
            var path = PluginPointerPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, [UserDataDirectory, loggingEnabled ? "logging=on" : "logging=off"]);
        }
        catch { /* the app must not fail to start because a hint file could not be written */ }
    }

    /// <summary>Read that pointer. Returns null when RemSound has never run on this machine.</summary>
    public static (string UserDataDirectory, bool LoggingEnabled)? ReadPluginPointer()
    {
        try
        {
            var lines = File.ReadAllLines(PluginPointerPath);
            if (lines.Length < 1 || string.IsNullOrWhiteSpace(lines[0])) return null;
            return (lines[0], lines.Length > 1 && lines[1].Contains("on", StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    /// <summary>Where the shipped DEFAULT cue WAVs live: a <c>default sounds\</c> folder next to the
    /// exe. This is part of the INSTALL, not user state — the auto-updater (and a dev republish)
    /// always overwrites it, so a changed default sound reaches every user, including existing ones.
    /// Deliberately NOT under <see cref="UserDataDirectory"/> and NOT redirected by <c>--config-dir</c>:
    /// these are shipped defaults, not per-user data. The user's OWN custom sounds are never stored
    /// here — they're explicit file paths (the Preferences "Browse" picker) that live in the user's
    /// own location, which the updater never touches. 2026-06-13: moved here out of the per-user
    /// <c>sounds\</c> folder, whose never-overwrite seeding meant a tweaked default could never land
    /// for anyone who already had the old one.</summary>
    public static string SoundsDirectory => Path.Combine(AppContext.BaseDirectory, "default sounds");

    /// <summary>The base profiles folder (ProfileStore appends the per-machine subfolder).</summary>
    public static string ProfilesBaseDirectory => Path.Combine(UserDataDirectory, "profiles");

    private static string ConfigPath => Path.Combine(UserDataDirectory, "global config.json");

    /// <summary>Reads the app config from disk. Always returns a non-null instance — a missing
    /// or malformed file becomes a defaults-only AppConfig rather than throwing.</summary>
    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new AppConfig();
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }
        catch
        {
            // Corrupt config file shouldn't keep RemSound from launching. Fall back to
            // defaults; the user can re-pick a folder via the dialog and we'll overwrite
            // the bad file on the next save.
            return new AppConfig();
        }
    }

    /// <summary>Writes this config to disk. Throws on filesystem failures (caller should
    /// surface a MessageBox — failure to persist a directory choice is user-visible).</summary>
    public void Save()
    {
        Directory.CreateDirectory(UserDataDirectory);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        // Atomic replace — write a temp then move it over, so a torn write (crash / power-loss /
        // the updater force-closing us mid-save) can't truncate the file and silently revert config
        // to defaults.
        var tmp = ConfigPath + ".tmp";
        try
        {
            File.WriteAllText(tmp, json);
            File.Move(tmp, ConfigPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
            throw;
        }
    }

    /// <summary>Convenience: build the appropriate <see cref="ProfileStore"/> for the
    /// current config. Falls back to the default store (per-machine subfolder) if the
    /// configured folder is missing, blank, or doesn't exist on disk.</summary>
    public ProfileStore CreateStore()
    {
        if (!string.IsNullOrWhiteSpace(ProfilesDirectory) && Directory.Exists(ProfilesDirectory))
        {
            return new ProfileStore(ProfilesDirectory);
        }
        return new ProfileStore();
    }
}
