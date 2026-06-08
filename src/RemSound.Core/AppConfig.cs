using System.Text.Json;

namespace RemSound.Core;

/// <summary>How often the self-updater polls GitHub Releases for a newer build. Values are
/// stable: don't reorder; deserialisation reads the underlying int from <c>remsound.config.json</c>.</summary>
public enum UpdateCheckFrequency
{
    Never = 0,
    EveryHour = 1,
    Every6Hours = 2,
    Every24Hours = 3,
}

/// <summary>
/// App-level configuration that lives next to the exe as <c>remsound.config.json</c>.
/// Distinct from <see cref="Profile"/>: profiles are user-chosen sets of audio /
/// connectivity / device settings; the app config is the *meta* layer that holds
/// preferences that should be sticky regardless of which profile is loaded. Profiles are
/// per-setup; this file is per-installation.
///
/// What lives here:
///   * <see cref="ProfilesDirectory"/> — where the profile JSONs are read from.
///
/// (Pre-2026-05-11 also held <c>BothModeWarningSuppressed</c> — the "do not show me again"
/// tick on the WASAPI+ASIO latency popup. The popup was retired along with the audio-mode
/// listbox; old config JSONs that still contain the key just have it ignored.)
///
/// Persisted location: <c>&lt;exe&gt;\remsound.config.json</c>. If the file is missing or
/// malformed, defaults are used and the app behaves exactly as it did pre-2026-05-05
/// (per-machine subfolder under the exe). The file is only written when the user
/// explicitly changes a setting.
/// </summary>
public sealed class AppConfig
{
    /// <summary>Filesystem path to the directory the app should read profiles from. When
    /// null, RemSound uses the legacy default: <c>&lt;exe&gt;\profiles\&lt;machine&gt;\</c>.
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

    /// <summary>If true, RemSound writes a tab-separated diagnostic log to
    /// <c>&lt;exe&gt;\logs\</c>. Lives here (not in <see cref="Profile"/>) because logging
    /// is a debugging affordance for the installation, not a user-facing audio preference —
    /// switching profiles shouldn't accidentally re-enable a flood of writes the user had
    /// turned off, and a one-machine "yes log everything" decision shouldn't have to ride
    /// along on every saved profile. Default false: no log file is created until the user
    /// ticks <em>Enable logs</em> in the Preferences dialog.</summary>
    public bool LoggingEnabled { get; set; }

    /// <summary>If non-null and a profile with this title exists, RemSound skips the
    /// startup profile picker and loads this profile directly. Combine with
    /// <see cref="StartMinimised"/> + the Windows auto-start registry entry
    /// (see <c>StartupAutoStart</c>) to get a fully unattended boot-into-streaming flow.
    /// To re-show the picker temporarily, untick "Start with a specific profile" in the
    /// Startup behaviour dialog. Null = always show the picker (legacy behaviour).</summary>
    public string? StartWithProfileTitle { get; set; }

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

    /// <summary>If true (the default), RemSound runs an update check shortly after launch in
    /// addition to whatever <see cref="UpdateCheckFrequency"/> drives in the background. The
    /// startup check is what catches users who quit and re-open the app within the polling
    /// interval — without it they could miss an update for hours. Set to false to disable the
    /// startup check; the periodic timer (if set) still runs.</summary>
    public bool CheckForUpdatesOnStartup { get; set; } = true;

    /// <summary>If true, RemSound opens the About box (which leads with the latest release
    /// notes) once on the first launch AFTER an update has been installed, so the user sees
    /// "what's new" without going looking. Default false — opt-in. Detected by comparing the
    /// running version against <see cref="LastWhatsNewVersion"/> at launch, so it only fires
    /// when the version actually changed, never on an ordinary relaunch. On by default — it's a
    /// discoverability aid (see what changed), not a data-persistence toggle, so the usual
    /// "auto-options default off" rule doesn't really apply; users can untick it.</summary>
    public bool ShowWhatsNewAfterUpdate { get; set; } = true;

    /// <summary>The app version recorded at the last launch. Used only to detect "the version
    /// changed since last run" for <see cref="ShowWhatsNewAfterUpdate"/>. Null until first
    /// recorded, so a fresh install never counts as an update.</summary>
    public string? LastWhatsNewVersion { get; set; }

    /// <summary>If true, RemSound tries to open the audio port (UDP 47830) on the local router
    /// using UPnP / NAT-PMP / PCP, so peers on the public internet can reach this machine
    /// without manual port forwarding. Default false — the toggle opt-in only, because some
    /// networks (corporate, hostile shared) shouldn't have apps poking the router. When
    /// successful, RemSound surfaces the external address in the Preferences dialog so the
    /// user knows what to give peers. Falls back gracefully when the router doesn't support
    /// UPnP — RemSound just doesn't open anything.</summary>
    public bool UpnpEnabled { get; set; }

    /// <summary>UTC timestamp of the last successful update check. Used by the background
    /// update timer to space out polls across launches — if you set the frequency to
    /// "every 24 hours" and re-launch the app three times that day, it still hits the API
    /// only once. Null on a fresh install.</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }

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

    /// <summary>The config folder next to the exe (<c>&lt;exe&gt;\config\</c>). Holds the global
    /// config file and the <c>profiles\</c> subfolder. 2026-06-07: everything non-recording config
    /// moved in here from loose files beside the exe, so the install root stays tidy.</summary>
    public static string ConfigDirectory => Path.Combine(AppContext.BaseDirectory, "config");

    private static string ConfigPath => Path.Combine(ConfigDirectory, "global config.json");

    /// <summary>
    /// One-time, idempotent relocation of the pre-2026-06-07 layout into <c>config\</c>:
    ///   * <c>&lt;exe&gt;\remsound.config.json</c> → <c>&lt;exe&gt;\config\global config.json</c>
    ///   * <c>&lt;exe&gt;\profiles\</c>             → <c>&lt;exe&gt;\config\profiles\</c>
    /// Run once at startup BEFORE anything reads config or profiles. Each move only happens when
    /// the old item exists and the new one doesn't, so it's safe to call every launch and it
    /// upgrades anyone coming from an older build without losing a profile or a setting. A custom
    /// <see cref="ProfilesDirectory"/> is untouched — it isn't in the default location.
    /// </summary>
    /// <summary>What <see cref="MigrateLegacyLayoutIfNeeded"/> actually relocated this launch.
    /// <see cref="MovedAnything"/> is true only on the one launch where an upgrade's old files
    /// were found and moved — the caller uses it to show a one-time "your settings moved" notice.</summary>
    public readonly record struct LayoutMigrationResult(bool MovedGlobalConfig, bool MovedProfiles)
    {
        public bool MovedAnything => MovedGlobalConfig || MovedProfiles;
    }

    public static LayoutMigrationResult MigrateLegacyLayoutIfNeeded()
    {
        var movedGlobal = false;
        var movedProfiles = false;
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            var oldGlobal = Path.Combine(AppContext.BaseDirectory, "remsound.config.json");
            if (File.Exists(oldGlobal) && !File.Exists(ConfigPath))
            {
                File.Move(oldGlobal, ConfigPath);
                movedGlobal = true;
            }
            var oldProfiles = Path.Combine(AppContext.BaseDirectory, "profiles");
            var newProfiles = Path.Combine(ConfigDirectory, "profiles");
            if (Directory.Exists(oldProfiles) && !Directory.Exists(newProfiles))
            {
                Directory.Move(oldProfiles, newProfiles);
                movedProfiles = true;
            }
        }
        catch
        {
            // Best-effort: a failed move (permissions, file in use) just means the app falls
            // back to defaults / an empty profiles list rather than crashing on launch.
        }
        return new LayoutMigrationResult(movedGlobal, movedProfiles);
    }

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
        Directory.CreateDirectory(ConfigDirectory);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
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
