using System.Windows.Forms;

namespace RemSound.Core;

/// <summary>
/// In-memory cache of UI/runtime preferences. As of 2026-05-02 the per-profile settings here no
/// longer persist to disk themselves — RemSound's persistence layer is the profile system
/// (<see cref="Profile"/> / <see cref="ProfileStore"/>), and this class is an intra-process holding
/// area that the active profile populates on app startup and reads back from when the user saves a
/// profile. The machine-wide exceptions go straight to <see cref="AppConfig"/> instead: the keyboard
/// shortcuts and the remembered peers and applications. Old <c>configs/</c> folders from prior builds
/// are ignored. The constructor's <c>appName</c> is unused; it stays because MainForm and the gate
/// both pass one.
/// </summary>
public sealed class RemSoundSettingsStore
{
    public RemSoundSettingsStore(string appName) { }

    // Keyboard shortcuts are MACHINE-WIDE as of v4.4 — one set shared by every profile, stored in
    // AppConfig (issue #14: before v4.4 they lived on each Profile, so a shortcut set on one profile
    // didn't apply on another). The Load*/Save* method names are unchanged, so the hotkey controller
    // didn't need touching; only the backing store moved from the per-profile cache to AppConfig. The
    // built-in defaults below are unchanged.
    private static void SaveGlobalHotkey(Action<AppConfig> set)
    {
        var c = AppConfig.Load();
        set(c);
        try { c.Save(); } catch { /* best-effort, like the app's other AppConfig writes */ }
    }

    public HotkeyInfo LoadReceiveMuteHotkey() =>
        AppConfig.Load().ReceiveMuteHotkey?.ToHotkeyInfo() ?? new HotkeyInfo(Keys.R, true, true, true);
    public void SaveReceiveMuteHotkey(HotkeyInfo hotkey) => SaveGlobalHotkey(c => c.ReceiveMuteHotkey = HotkeyRecord.From(hotkey));

    public HotkeyInfo LoadSendMuteHotkey() =>
        AppConfig.Load().SendMuteHotkey?.ToHotkeyInfo() ?? new HotkeyInfo(Keys.S, true, true, true);
    public void SaveSendMuteHotkey(HotkeyInfo hotkey) => SaveGlobalHotkey(c => c.SendMuteHotkey = HotkeyRecord.From(hotkey));

    public HotkeyInfo LoadTrayHotkey() =>
        AppConfig.Load().TrayHotkey?.ToHotkeyInfo() ?? new HotkeyInfo(Keys.F10, true, true, false);
    public void SaveTrayHotkey(HotkeyInfo hotkey) => SaveGlobalHotkey(c => c.TrayHotkey = HotkeyRecord.From(hotkey));

    public HotkeyInfo LoadVolumeUpHotkey() =>
        AppConfig.Load().VolumeUpHotkey?.ToHotkeyInfo() ?? HotkeyInfo.Unset;
    public void SaveVolumeUpHotkey(HotkeyInfo hotkey) => SaveGlobalHotkey(c => c.VolumeUpHotkey = HotkeyRecord.From(hotkey));

    public HotkeyInfo LoadVolumeDownHotkey() =>
        AppConfig.Load().VolumeDownHotkey?.ToHotkeyInfo() ?? HotkeyInfo.Unset;
    public void SaveVolumeDownHotkey(HotkeyInfo hotkey) => SaveGlobalHotkey(c => c.VolumeDownHotkey = HotkeyRecord.From(hotkey));

    /// <summary>The push to talk key. Default Scroll Lock on its own (Ed, 2026-09-28); may be unset.</summary>
    public HotkeyInfo LoadPushToTalkHotkey() =>
        AppConfig.Load().PushToTalkHotkey?.ToHotkeyInfo(allowSingleKey: true) ?? PushToTalkDefaultKey;
    public void SavePushToTalkHotkey(HotkeyInfo hotkey) => SaveGlobalHotkey(c => c.PushToTalkHotkey = HotkeyRecord.From(hotkey));
    public static HotkeyInfo PushToTalkDefaultKey { get; } = new(Keys.Scroll, false, false, false);

    /// <summary>Start or stop the metronome (mute or unmute while following a master). Unset unless chosen.</summary>
    public HotkeyInfo LoadMetronomeHotkey() =>
        AppConfig.Load().MetronomeHotkey?.ToHotkeyInfo() ?? HotkeyInfo.Unset;
    public void SaveMetronomeHotkey(HotkeyInfo hotkey) => SaveGlobalHotkey(c => c.MetronomeHotkey = HotkeyRecord.From(hotkey));

    /// <summary>The metronome's tempo up or down a beat a minute. Unset unless chosen.</summary>
    public HotkeyInfo LoadMetronomeTempoUpHotkey() =>
        AppConfig.Load().MetronomeTempoUpHotkey?.ToHotkeyInfo() ?? HotkeyInfo.Unset;
    public void SaveMetronomeTempoUpHotkey(HotkeyInfo hotkey) => SaveGlobalHotkey(c => c.MetronomeTempoUpHotkey = HotkeyRecord.From(hotkey));
    public HotkeyInfo LoadMetronomeTempoDownHotkey() =>
        AppConfig.Load().MetronomeTempoDownHotkey?.ToHotkeyInfo() ?? HotkeyInfo.Unset;
    public void SaveMetronomeTempoDownHotkey(HotkeyInfo hotkey) => SaveGlobalHotkey(c => c.MetronomeTempoDownHotkey = HotkeyRecord.From(hotkey));

    /// <summary>The metronome's volume up or down 5 points. Unset unless chosen.</summary>
    public HotkeyInfo LoadMetronomeVolumeUpHotkey() =>
        AppConfig.Load().MetronomeVolumeUpHotkey?.ToHotkeyInfo() ?? HotkeyInfo.Unset;
    public void SaveMetronomeVolumeUpHotkey(HotkeyInfo hotkey) => SaveGlobalHotkey(c => c.MetronomeVolumeUpHotkey = HotkeyRecord.From(hotkey));
    public HotkeyInfo LoadMetronomeVolumeDownHotkey() =>
        AppConfig.Load().MetronomeVolumeDownHotkey?.ToHotkeyInfo() ?? HotkeyInfo.Unset;
    public void SaveMetronomeVolumeDownHotkey(HotkeyInfo hotkey) => SaveGlobalHotkey(c => c.MetronomeVolumeDownHotkey = HotkeyRecord.From(hotkey));

    /// <summary>Unmute or mute yourself to test the round trip on a server. Unset unless chosen.</summary>
    public HotkeyInfo LoadHearMyselfHotkey() =>
        AppConfig.Load().HearMyselfHotkey?.ToHotkeyInfo() ?? HotkeyInfo.Unset;
    public void SaveHearMyselfHotkey(HotkeyInfo hotkey) => SaveGlobalHotkey(c => c.HearMyselfHotkey = HotkeyRecord.From(hotkey));

    /// <summary>Mute or unmute received audio on this computer. Unset unless chosen.</summary>
    public HotkeyInfo LoadMuteReceivedHotkey() =>
        AppConfig.Load().MuteReceivedHotkey?.ToHotkeyInfo() ?? HotkeyInfo.Unset;
    public void SaveMuteReceivedHotkey(HotkeyInfo hotkey) => SaveGlobalHotkey(c => c.MuteReceivedHotkey = HotkeyRecord.From(hotkey));

    public HotkeyInfo LoadToggleRecordingHotkey() =>
        AppConfig.Load().ToggleRecordingHotkey?.ToHotkeyInfo() ?? HotkeyInfo.Unset;
    public void SaveToggleRecordingHotkey(HotkeyInfo hotkey) => SaveGlobalHotkey(c => c.ToggleRecordingHotkey = HotkeyRecord.From(hotkey));

    public HotkeyInfo LoadRemoteVolumeUpHotkey() =>
        AppConfig.Load().RemoteVolumeUpHotkey?.ToHotkeyInfo() ?? HotkeyInfo.Unset;
    public void SaveRemoteVolumeUpHotkey(HotkeyInfo hotkey) => SaveGlobalHotkey(c => c.RemoteVolumeUpHotkey = HotkeyRecord.From(hotkey));

    public HotkeyInfo LoadRemoteVolumeDownHotkey() =>
        AppConfig.Load().RemoteVolumeDownHotkey?.ToHotkeyInfo() ?? HotkeyInfo.Unset;
    public void SaveRemoteVolumeDownHotkey(HotkeyInfo hotkey) => SaveGlobalHotkey(c => c.RemoteVolumeDownHotkey = HotkeyRecord.From(hotkey));

    public HotkeyInfo LoadRemoteMuteToggleHotkey() =>
        AppConfig.Load().RemoteMuteToggleHotkey?.ToHotkeyInfo() ?? HotkeyInfo.Unset;
    public void SaveRemoteMuteToggleHotkey(HotkeyInfo hotkey) => SaveGlobalHotkey(c => c.RemoteMuteToggleHotkey = HotkeyRecord.From(hotkey));

    public HotkeyInfo LoadSystemVolumeUpHotkey() =>
        AppConfig.Load().SystemVolumeUpHotkey?.ToHotkeyInfo() ?? HotkeyInfo.Unset;
    public void SaveSystemVolumeUpHotkey(HotkeyInfo hotkey) => SaveGlobalHotkey(c => c.SystemVolumeUpHotkey = HotkeyRecord.From(hotkey));

    public HotkeyInfo LoadSystemVolumeDownHotkey() =>
        AppConfig.Load().SystemVolumeDownHotkey?.ToHotkeyInfo() ?? HotkeyInfo.Unset;
    public void SaveSystemVolumeDownHotkey(HotkeyInfo hotkey) => SaveGlobalHotkey(c => c.SystemVolumeDownHotkey = HotkeyRecord.From(hotkey));

    public HotkeyInfo LoadSystemMuteToggleHotkey() =>
        AppConfig.Load().SystemMuteToggleHotkey?.ToHotkeyInfo() ?? HotkeyInfo.Unset;
    public void SaveSystemMuteToggleHotkey(HotkeyInfo hotkey) => SaveGlobalHotkey(c => c.SystemMuteToggleHotkey = HotkeyRecord.From(hotkey));

    public HotkeyInfo LoadQuickProfileSwitchHotkey() =>
        AppConfig.Load().QuickProfileSwitchHotkey?.ToHotkeyInfo() ?? HotkeyInfo.Unset;
    public void SaveQuickProfileSwitchHotkey(HotkeyInfo hotkey) => SaveGlobalHotkey(c => c.QuickProfileSwitchHotkey = HotkeyRecord.From(hotkey));

    public HotkeyInfo LoadSpeakStatusLineHotkey() =>
        AppConfig.Load().SpeakStatusLineHotkey?.ToHotkeyInfo() ?? HotkeyInfo.Unset;
    public void SaveSpeakStatusLineHotkey(HotkeyInfo hotkey) => SaveGlobalHotkey(c => c.SpeakStatusLineHotkey = HotkeyRecord.From(hotkey));

    public HotkeyInfo LoadToggleAllPeerShapingHotkey() =>
        AppConfig.Load().ToggleAllPeerShapingHotkey?.ToHotkeyInfo() ?? HotkeyInfo.Unset;
    public void SaveToggleAllPeerShapingHotkey(HotkeyInfo hotkey) => SaveGlobalHotkey(c => c.ToggleAllPeerShapingHotkey = HotkeyRecord.From(hotkey));

    public bool LoadAcceptRemoteVolumeCommands(bool defaultValue = false) =>
        Try(() => Load()?.AcceptRemoteVolumeCommands) ?? defaultValue;

    public void SaveAcceptRemoteVolumeCommands(bool value)
    {
        var s = Load() ?? new Settings();
        s.AcceptRemoteVolumeCommands = value;
        Save(s);
    }

    /// <summary>The Web listener window's two settings (2026-10-10), the profile's. The password comes back in plain text.</summary>
    public bool LoadWebListenEnabled() => Try(() => Load()?.WebListenEnabled) ?? false;
    public string LoadWebListenPassword() => RemSoundCrypto.Deobfuscate(Try(() => Load()?.WebListenPassword));
    public bool LoadWebListenAutoStart() => Try(() => Load()?.WebListenAutoStart) ?? false;

    public void SaveWebListen(bool enabled, string password, bool autoStart)
    {
        var s = Load() ?? new Settings();
        s.WebListenEnabled = enabled;
        s.WebListenPassword = string.IsNullOrEmpty(password) ? null : RemSoundCrypto.Obfuscate(password);
        s.WebListenAutoStart = autoStart;
        Save(s);
    }

    public int LoadMaxLatencyMs(int defaultValue = 80) =>
        Try(() => Load()?.MaxLatencyMs is int v ? Math.Clamp(v, 5, 500) : (int?)null) ?? defaultValue;

    public void SaveMaxLatencyMs(int value)
    {
        var s = Load() ?? new Settings();
        s.MaxLatencyMs = Math.Clamp(value, 5, 500); // match LoadMaxLatencyMs's [5,500] so stored == effective
        Save(s);
    }

    /// <summary>
    /// The ASIO lane's latency target, used only when an ASIO driver is chosen (BothIndependent).
    /// <see cref="LoadMaxLatencyMs"/> / <see cref="SaveMaxLatencyMs"/> govern the other slider: the
    /// WASAPI lane when an ASIO driver is chosen, the Mixed route when none is (WasapiOnly). Default
    /// 10 ms because the whole point of the ASIO lane is to run at its native low latency; a user who
    /// has chosen an ASIO driver almost certainly wants it closer to 10 than to 80.
    /// </summary>
    public int LoadMaxLatencyMsAsio(int defaultValue = 10) =>
        Try(() => Load()?.MaxLatencyMsAsio is int v ? Math.Clamp(v, 5, 500) : (int?)null) ?? defaultValue;

    public void SaveMaxLatencyMsAsio(int value)
    {
        var s = Load() ?? new Settings();
        s.MaxLatencyMsAsio = Math.Clamp(value, 5, 500); // match LoadMaxLatencyMsAsio's [5,500]
        Save(s);
    }

    /// <summary>Continuous auto-tune enabled for the ASIO lane. Defaults false to match the
    /// WASAPI-lane default — having one lane auto-adjusting and the other fixed produces
    /// confusingly asymmetric latency where the auto-tuning lane sits noticeably higher
    /// because it's reacting to network jitter the fixed lane just rides through. User can
    /// enable per lane explicitly; in BothIndependent both lanes' enable checkboxes are
    /// visible side-by-side.</summary>
    public bool LoadContinuousAutoTuneAsioEnabled(bool defaultValue = false) =>
        Try(() => Load()?.ContinuousAutoTuneAsioEnabled) ?? defaultValue;

    public void SaveContinuousAutoTuneAsioEnabled(bool value)
    {
        var s = Load() ?? new Settings();
        s.ContinuousAutoTuneAsioEnabled = value;
        Save(s);
    }

    public bool LoadAutoTuneLowestDelay() => Try(() => Load()?.AutoTuneLowestDelay) ?? false;

    /// <summary>This profile's logs: on, off, or null when it has never chosen (it then follows this computer's setting).</summary>
    public bool? LoadEnableLogs() => Try(() => Load()?.EnableLogs);

    public void SaveEnableLogs(bool value)
    {
        var s = Load() ?? new Settings();
        s.EnableLogs = value;
        Save(s);
    }

    public void SaveAutoTuneLowestDelay(bool value)
    {
        var s = Load() ?? new Settings();
        s.AutoTuneLowestDelay = value;
        Save(s);
    }

    // The fallbacks when nothing can be loaded follow a new profile's: Opus live latency (2026-09-28).
    public AudioTransportCodec LoadCodec(AudioTransportCodec defaultValue = AudioTransportCodec.Opus) =>
        Try(() => Load()?.Codec) ?? defaultValue;

    public void SaveCodec(AudioTransportCodec value)
    {
        var s = Load() ?? new Settings();
        s.Codec = value;
        Save(s);
    }

    /// <summary>Loads the Opus frame size in samples-per-channel at 48 kHz. Default 480 = 10 ms, the
    /// size from before the codec list offered only 960 and 120; MainForm.ResolveCodecIndex shows
    /// anything but 120 as the 960 choice.</summary>
    public int LoadOpusFrameSamplesPerChannel(int defaultValue = 120) =>
        Try(() => Load()?.OpusFrameSamplesPerChannel is int v ? v : (int?)null) ?? defaultValue;

    /// <summary>Saves the Opus frame size in samples-per-channel at 48 kHz. Accepts
    /// 120/240/480/960; anything else collapses to 480 (= 10 ms). The codec list only ever saves 960
    /// or 120: the smaller frame Tight rate gives is applied to the encoder, never saved here.</summary>
    public void SaveOpusFrameSamplesPerChannel(int value)
    {
        var s = Load() ?? new Settings();
        s.OpusFrameSamplesPerChannel = value switch
        {
            960 => 960,  // 20 ms — "broadcast quality" in the codec list
            480 => 480,  // 10 ms — older profiles
            240 => 240,  // 5 ms
            120 => 120,  // 2.5 ms — "live latency" in the codec list
            _ => 480,
        };
        Save(s);
    }

    public bool LoadContinuousAutoTuneEnabled(bool defaultValue = false) =>
        Try(() => Load()?.ContinuousAutoTuneEnabled) ?? defaultValue;

    public void SaveContinuousAutoTuneEnabled(bool value)
    {
        var s = Load() ?? new Settings();
        s.ContinuousAutoTuneEnabled = value;
        Save(s);
    }

    /// <summary>How often continuous auto-tune re-checks, in seconds.
    ///
    /// <para><b>The floor is 3, not 5, because 3 is what the dropdown OFFERS.</b> It used to clamp
    /// and validate at 5, so picking "3 seconds" worked perfectly for the session and then silently
    /// reverted on reload - saved as 5 going out, and rejected as out of range coming back. A control
    /// that offers a value the store refuses is a broken control (Ed, 2026-08-22: "why the hell can I
    /// not save the auto tune check delay as 3 seconds?").</para>
    ///
    /// <para>Safe for older builds reading the same profile: they reject anything below 5 and fall
    /// back to the default, so a 3 written here degrades to 5 there rather than breaking anything.</para></summary>
    public int LoadContinuousAutoTuneIntervalSec(int defaultValue = 5) =>
        Try(() => Load()?.ContinuousAutoTuneIntervalSec is int v && v >= MinAutoTuneIntervalSec && v <= 60 ? v : (int?)null) ?? defaultValue;

    public void SaveContinuousAutoTuneIntervalSec(int value)
    {
        var s = Load() ?? new Settings();
        s.ContinuousAutoTuneIntervalSec = Math.Clamp(value, MinAutoTuneIntervalSec, 60);
        Save(s);
    }

    /// <summary>The shortest interval the dropdown offers. Pinned here so the store and the control
    /// can never drift apart again.</summary>
    public const int MinAutoTuneIntervalSec = 3;

    // Remembered peers + remembered applications are MACHINE-WIDE, backed by AppConfig (the same
    // persistent per-machine file the global hotkeys use) — NOT the in-memory settings cache. They used
    // to go through the cache: peers survived only because each profile's JSON carried a copy (making
    // them per-profile in practice, against Ed's "both lists are global" ask), and applications didn't
    // survive an app exit AT ALL — the cache is intra-process only. 2026-07-16 fix.

    public IReadOnlyList<string> LoadRememberedPeers() =>
        Try(() => AppConfig.Load().RememberedPeers?
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        ?? [];

    public void SaveRememberedPeers(IEnumerable<string> peers)
    {
        var c = AppConfig.Load();
        c.RememberedPeers = peers
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        try { c.Save(); } catch { /* best-effort, like the app's other AppConfig writes */ }
    }

    /// <summary>The relay servers this machine has connected to, newest first, so the Connectivity tab can offer them
    /// again. Machine-wide like remembered peers: a relay is somewhere this computer goes, not part of one profile.</summary>
    public IReadOnlyList<string> LoadRememberedRelays() =>
        Try(() => AppConfig.Load().RememberedRelays?
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        ?? [];

    public void SaveRememberedRelays(IEnumerable<string> relays)
    {
        var c = AppConfig.Load();
        c.RememberedRelays = relays
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        try { c.Save(); } catch { /* best-effort, like the app's other AppConfig writes */ }
    }

    /// <summary>GLOBAL remembered application names (lower-case process names) — the shared "apps I send"
    /// list, machine-wide like <see cref="LoadRememberedPeers"/>, not per-profile.</summary>
    public IReadOnlyList<string> LoadRememberedApplications() =>
        Try(() => AppConfig.Load().RememberedApplications?
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        ?? [];

    public void SaveRememberedApplications(IEnumerable<string> apps)
    {
        var c = AppConfig.Load();
        c.RememberedApplications = apps
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        try { c.Save(); } catch { /* best-effort, like the app's other AppConfig writes */ }
    }

    // LoggingEnabled lives in AppConfig now — it's a machine-local debug knob, not a
    // per-profile setting. LoadLoggingEnabled / SaveLoggingEnabled were retired here;
    // callers go to AppConfig.LoggingEnabled directly.

    /// <summary>
    /// Audio mode is derived from whether an ASIO driver is selected. Pre-2026-05-11 this was
    /// a user-facing setting with its own listbox; now the UI is simpler — the user just picks
    /// an ASIO driver (or "(none)" to disable ASIO) and the mode follows. A real driver chosen
    /// means BothIndependent (WASAPI + ASIO running side by side, each at its own latency);
    /// no driver means WasapiOnly. No mode is stored anywhere: an old profile's
    /// <c>AudioModeRaw</c> key is ignored on load, and <paramref name="defaultValue"/> is ignored
    /// too. The gate calls this by name through reflection (SelfTest.Audit), so keep the name.
    /// </summary>
    public AudioMode LoadAudioMode(AudioMode defaultValue = AudioMode.WasapiOnly) =>
        string.IsNullOrWhiteSpace(LoadAsioDriverName()) ? AudioMode.WasapiOnly : AudioMode.BothIndependent;

    public SendRate LoadSendRate(SendRate defaultValue = SendRate.Standard) =>
        Try(() => Load()?.SendRate is SendRate v ? v : (SendRate?)null) ?? defaultValue;

    public void SaveSendRate(SendRate value)
    {
        var s = Load() ?? new Settings();
        s.SendRate = value;
        Save(s);
    }

    // Tight-latency mode ("Lock to audio clock") is ALWAYS ON as of 2026-07-17 — no longer a user option,
    // so the Load/Save accessors were removed. The app forces the sender tight at startup and the service
    // per stream. The Settings cache still round-trips Profile.TightLatencyMode for file compatibility.

    /// <summary>Priority mode for the current profile. When true, the App's
    /// <c>PerformanceMode</c> helper drives every Win32 lever that elevates a process's
    /// scheduling and memory behaviour: EcoQoS opt-out, kernel power request, High
    /// priority class, 1&nbsp;ms scheduler quantum, memory priority, working-set lock,
    /// and MMCSS thread priority. Off by default; the user opts in per profile.</summary>
    /// <summary>When true, this profile is locked to the exact peer addresses the user set — RemSound
    /// uses only those, never follows the other computer by its advertised name and never switches to a
    /// discovered address. Off by default; opt in per profile. (#17)</summary>
    public bool LoadLockPeerAddresses(bool defaultValue = false) =>
        Try(() => Load()?.LockPeerAddresses) ?? defaultValue;

    public void SaveLockPeerAddresses(bool value)
    {
        var s = Load() ?? new Settings();
        s.LockPeerAddresses = value;
        Save(s);
    }

    // === Audio cues: the profile's own, and the global sounds (2026-10-08) ===
    // Ed: "one list of profile specific sounds and one of global sounds. global sounds are: profile save, profile switch,
    // profile menu open, update, context help open and close. the rest are profile specific." The profile's own ride in
    // this cache's CueSettings, read from the profile on load (or worked out from what an older RemSound used with it) and
    // written back on save; the global sounds (CueSettings.GlobalCueIds) are this computer's, in AppConfig.GlobalCues,
    // written at once, like the other machine-wide accessors above. Every per-cue method below routes by the cue id, so its
    // many callers need not know which kind a cue is.

    private CueSettings Cues() => cache.Cues ??= new CueSettings();

    /// <summary>The global sounds as this computer has them: before any profile has filled them, what its older settings
    /// gave (every cue on with its first sound when it cannot say).</summary>
    private static CueSettings GlobalCues()
    {
        try { var cfg = AppConfig.Load(); return cfg.GlobalCues ?? CueSettings.FromBefore(null, cfg).OnlyGlobalCues(); }
        catch { return new CueSettings(); }
    }

    /// <summary>Change the global sounds, kept on this computer for every profile, at once.</summary>
    private static void ChangeGlobalCues(Action<CueSettings> change)
    {
        try
        {
            var cfg = AppConfig.Load();
            var global = cfg.GlobalCues?.Clone() ?? CueSettings.FromBefore(null, cfg).OnlyGlobalCues();
            change(global);
            cfg.GlobalCues = global.OnlyGlobalCues();
            cfg.Save();
        }
        catch { /* harmless - the change just won't survive a restart */ }
    }

    /// <summary>Where a cue's settings live: the global sounds on this computer, every other cue in the profile.</summary>
    private CueSettings Holding(string cueId) => CueSettings.IsGlobal(cueId) ? GlobalCues() : Cues();

    private void Change(string cueId, Action<CueSettings> change)
    {
        if (CueSettings.IsGlobal(cueId)) ChangeGlobalCues(change);
        else change(Cues());
    }

    /// <summary>Whether a cue plays: on unless switched off, in the profile or, for a global sound, on this computer.</summary>
    public bool LoadCueEnabled(string cueId) => Try(() => (bool?)Holding(cueId).IsOn(cueId)) ?? true;

    /// <summary>Switch a cue on or off: in the profile, or for a global sound, on this computer at once.</summary>
    public void SaveCueEnabled(string cueId, bool on) => Change(cueId, c => c.Enabled[cueId] = on);

    /// <summary>The cue's chosen built-in sound file name, or null for its first one.</summary>
    public string? LoadCueSound(string cueId) =>
        Try(() => Holding(cueId).Sounds.TryGetValue(cueId, out var file) && !string.IsNullOrWhiteSpace(file) ? file : null);

    /// <summary>Choose a cue's built-in sound; null goes back to its first one.</summary>
    public void SaveCueSound(string cueId, string? fileName) => Change(cueId, c =>
    {
        if (string.IsNullOrWhiteSpace(fileName)) c.Sounds.Remove(cueId);
        else c.Sounds[cueId] = fileName;
    });

    /// <summary>The keyboard clicks while typing, in this profile.</summary>
    public bool LoadKeyboardClicks() => Try(() => (bool?)Cues().KeyboardClicks) ?? true;

    public void SaveKeyboardClicks(bool on) => Cues().KeyboardClicks = on;

    /// <summary>Every cue as it plays now - this profile's own with this computer's global sounds - for the players and,
    /// cut back to the profile's own, this computer's copy (<see cref="AppConfig.LastProfileCues"/>).</summary>
    public CueSettings CueSnapshot() => CueSettings.Combine(Cues(), GlobalCues());

    /// <summary>Put a set of the profile's own cues in place: a new profile's, which start from the last profile's. Any
    /// global sounds in <paramref name="cues"/> are left to this computer's own.</summary>
    public void UseCues(CueSettings cues) => cache.Cues = (cues ?? throw new ArgumentNullException(nameof(cues))).OnlyProfileCues();

    public bool LoadEnableConnectCue() => LoadCueEnabled("connect");
    public void SaveEnableConnectCue(bool value) => SaveCueEnabled("connect", value);
    public bool LoadEnableDisconnectCue() => LoadCueEnabled("disconnect");
    public void SaveEnableDisconnectCue(bool value) => SaveCueEnabled("disconnect", value);
    public bool LoadEnableRecordStartCue() => LoadCueEnabled("record-start");
    public void SaveEnableRecordStartCue(bool value) => SaveCueEnabled("record-start", value);
    public bool LoadEnableRecordStopCue() => LoadCueEnabled("record-stop");
    public void SaveEnableRecordStopCue(bool value) => SaveCueEnabled("record-stop", value);
    public bool LoadEnableSaveCue() => LoadCueEnabled("save");
    public void SaveEnableSaveCue(bool value) => SaveCueEnabled("save", value);
    public bool LoadEnableProfileSwitchCue() => LoadCueEnabled("profile-switch");
    public void SaveEnableProfileSwitchCue(bool value) => SaveCueEnabled("profile-switch", value);
    public bool LoadEnableProfileMenuOpenCue() => LoadCueEnabled("profile-menu-open");
    public void SaveEnableProfileMenuOpenCue(bool value) => SaveCueEnabled("profile-menu-open", value);
    public bool LoadEnableUpdateCue() => LoadCueEnabled("update");
    public void SaveEnableUpdateCue(bool value) => SaveCueEnabled("update", value);

    /// <summary>The cue's file of the person's own, or null when it plays a built-in sound.</summary>
    public string? LoadCustomCuePath(string cueId) => Try(() => Holding(cueId).CustomPathOf(cueId));

    /// <summary>Set a cue's file of the person's own. Null or empty clears it, so the cue plays its chosen built-in sound
    /// from <c>default sounds\</c> (<see cref="AppConfig.SoundsDirectory"/>).</summary>
    public void SaveCustomCuePath(string cueId, string? path) => Change(cueId, c =>
    {
        if (string.IsNullOrWhiteSpace(path)) c.CustomPaths.Remove(cueId);
        else c.CustomPaths[cueId] = path;
    });

    /// <summary>The whole recording-settings bag for the current profile. Loaded as a
    /// CLONE so callers can mutate the returned object without inadvertently writing
    /// back to the cache. Save flushes the object atomically.</summary>
    public RecordingSettings LoadRecordingSettings() =>
        (Try(() => Load()?.RecordingSettings) ?? new RecordingSettings()).Clone();

    public void SaveRecordingSettings(RecordingSettings value)
    {
        var s = Load() ?? new Settings();
        s.RecordingSettings = value?.Clone() ?? new RecordingSettings();
        Save(s);
    }

    /// <summary>The profile's push to talk settings, as a copy. Off when the profile has none.</summary>
    public PushToTalkSettings LoadPushToTalkSettings() =>
        (Try(() => Load()?.PushToTalk) ?? new PushToTalkSettings()).Clone();

    public void SavePushToTalkSettings(PushToTalkSettings value)
    {
        var s = Load() ?? new Settings();
        s.PushToTalk = value?.Clone() ?? new PushToTalkSettings();
        Save(s);
    }

    /// <summary>The profile's metronome settings, as a copy. The defaults when the profile has none.</summary>
    public MetronomeSettings LoadMetronomeSettings() =>
        (Try(() => Load()?.Metronome) ?? new MetronomeSettings()).Clone();

    public void SaveMetronomeSettings(MetronomeSettings value)
    {
        var s = Load() ?? new Settings();
        s.Metronome = value?.Clone() ?? new MetronomeSettings();
        Save(s);
    }

    /// <summary>How eagerly the receiver trims a playout buffer that has grown past the target
    /// latency. The trim DROPS the oldest audio — a brief click — and never changes playback speed.
    /// 1 = trim as soon as the buffer is a couple of packets over target (glued to target, frequent
    /// clicks); each step up adds margin before the trim fires; 10 = no trim at all, so the buffer can
    /// creep up over a long session. In normal operation the drift corrector keeps the buffer near
    /// target and the trim rarely fires (SessionPlayout.ReadFloats). Default is 3; user dials down for
    /// tighter, up for smoother.</summary>
    public int LoadSmoothness(int defaultValue = 3) =>
        Try(() => Load()?.Smoothness is int v ? Math.Clamp(v, 1, 10) : (int?)null) ?? defaultValue;

    public void SaveSmoothness(int value)
    {
        var s = Load() ?? new Settings();
        s.Smoothness = Math.Clamp(value, 1, 10);
        Save(s);
    }

    /// <summary>Receiver-side concealment artifact pick. See <see cref="ConcealmentArtifact"/>
    /// for what each value sounds like. Default is <see cref="ConcealmentArtifact.NoiseBurst"/>.
    /// The cosine tones were taken out of the choice (they sounded harsh on orchestral content), and
    /// a profile still holding one loads as NoiseBurst — what the dropdown already showed. Until
    /// 2026-09-13 it loaded as it was, so the receiver played the tone while the dropdown said
    /// "Noise burst".</summary>
    public ConcealmentArtifact LoadConcealmentArtifact(ConcealmentArtifact defaultValue = ConcealmentArtifact.NoiseBurst) =>
        WithRetiredTonesAsNoiseBurst(
            Try(() => Load()?.ConcealmentArtifact is ConcealmentArtifact v ? v : (ConcealmentArtifact?)null) ?? defaultValue);

    /// <summary>The retired cosine tones become <see cref="ConcealmentArtifact.NoiseBurst"/>; every other value is kept.</summary>
    public static ConcealmentArtifact WithRetiredTonesAsNoiseBurst(ConcealmentArtifact value) =>
        value is ConcealmentArtifact.CosineToneShort or ConcealmentArtifact.CosineToneLow ? ConcealmentArtifact.NoiseBurst : value;

    public void SaveConcealmentArtifact(ConcealmentArtifact value)
    {
        var s = Load() ?? new Settings();
        s.ConcealmentArtifact = value;
        Save(s);
    }

    // ResamplerBypassWhenTight (load/save + Settings field) removed 2026-05-06 in Phase 3
    // cleanup. The receiver no longer has a resampler in the steady-state path, so the
    // bypass switch had nothing left to toggle. Existing profile JSON with the old key
    // is silently ignored by the deserialiser.

    public string? LoadAsioDriverName() => Try(() => Load()?.AsioDriverName);

    public void SaveAsioDriverName(string? value)
    {
        var s = Load() ?? new Settings();
        s.AsioDriverName = string.IsNullOrWhiteSpace(value) ? null : value;
        Save(s);
    }

    /// <summary>The ASIO buffer size this profile asks its driver for, in frames: 0 = the driver's own (the default, and
    /// what anything not on offer falls back to - a hand-edited profile must not be asked of a driver). See
    /// <see cref="Profile.AsioBufferFrames"/>.</summary>
    public int LoadAsioBufferFrames() =>
        Try(() => Load()?.AsioBufferFrames is int v && Array.IndexOf(AsioBufferSizing.Choices, v) >= 0 ? v : (int?)null) ?? 0;

    public void SaveAsioBufferFrames(int frames)
    {
        var s = Load() ?? new Settings();
        s.AsioBufferFrames = Array.IndexOf(AsioBufferSizing.Choices, frames) >= 0 ? frames : 0;
        Save(s);
    }

    private static T? Try<T>(Func<T?> action) where T : class
    {
        try { return action(); } catch { return null; }
    }

    private static T? Try<T>(Func<T?> action, T? unused = null) where T : struct
    {
        try { return action(); } catch { return null; }
    }

    // 2026-05-02: profile-setting persistence moved out of this class. RemSound manages those via
    // the profile system (RemSound.Core.Profile / ProfileStore); this cache is per-process, populated
    // from the active profile on load and read back on save, and never touches disk itself. The old
    // configs/ folder is no longer written or read. (The machine-wide accessors above write
    // AppConfig directly.)
    private Settings cache = new();

    /// <summary>The whole in-memory settings as text, for the gate's "did driving this control change anything" check.</summary>
    internal string SnapshotForTest() => System.Text.Json.JsonSerializer.Serialize(cache);

    private Settings? Load() => cache;

    private void Save(Settings settings) => cache = settings;

    /// <summary>Replace the in-memory settings cache from a loaded <see cref="Profile"/>.
    /// Called once at app startup after the user picks a profile (or never, if they pick
    /// the blank template — in which case defaults remain).</summary>
    public void ApplyProfile(Profile profile)
    {
        if (profile is null) throw new ArgumentNullException(nameof(profile));
        cache = new Settings
        {
            // Keyboard shortcuts are not per-profile (since v4.4) — they live in AppConfig and are not
            // carried through the profile cache.
            AcceptRemoteVolumeCommands = profile.AcceptRemoteVolumeCommands,
            WebListenEnabled = profile.WebListenEnabled,
            WebListenPassword = profile.WebListenPassword,
            WebListenAutoStart = profile.WebListenAutoStart,
            MaxLatencyMs = profile.MaxLatencyMs,
            Codec = profile.Codec,
            OpusFrameSamplesPerChannel = profile.OpusFrameSamplesPerChannel,
            ContinuousAutoTuneEnabled = profile.ContinuousAutoTuneEnabled,
            ContinuousAutoTuneIntervalSec = profile.ContinuousAutoTuneIntervalSec,
            MaxLatencyMsAsio = profile.MaxLatencyMsAsio,
            ContinuousAutoTuneAsioEnabled = profile.ContinuousAutoTuneAsioEnabled,
            AutoTuneLowestDelay = profile.AutoTuneLowestDelay,
            EnableLogs = profile.EnableLogs,
            // RememberedPeers no longer rides the cache — it's machine-wide in AppConfig now; the
            // profile's legacy copy is unioned in below (migration) instead of replacing anything.
            AsioDriverName = profile.AsioDriverName,
            AsioBufferFrames = profile.AsioBufferFrames,
            SendRate = profile.SendRate,
            TightLatencyMode = profile.TightLatencyMode,
            LockPeerAddresses = profile.LockPeerAddresses,
            Smoothness = profile.Smoothness,
            ConcealmentArtifact = (ConcealmentArtifact)profile.ConcealmentArtifactRaw,
            // The profile's own cues, a copy so the cache and the loaded profile never share a dictionary. A profile an
            // older RemSound saved has none: its cues are what that RemSound used with it - its own eight, everything else
            // from this computer's older settings, which nothing writes any more (2026-10-08).
            Cues = CuesOf(profile).OnlyProfileCues(),
            RecordingSettings = profile.RecordingSettings?.Clone() ?? new RecordingSettings(),
            PushToTalk = profile.PushToTalk?.Clone() ?? new PushToTalkSettings(),
            Metronome = profile.Metronome?.Clone() ?? new MetronomeSettings(),
        };
        MigrateRememberedPeersToGlobal(profile);
        FillGlobalCuesOnce(profile);
    }

    /// <summary>The first profile a RemSound with the two lists of sounds opens on this computer fills its global sounds
    /// from what that profile played (2026-10-08) - an older profile's own eight, or the cues of one saved with every cue
    /// in it - so nothing sounds different after the update. Once only: from then on the global sounds are this
    /// computer's, changed on the Audio cues tab.</summary>
    private static void FillGlobalCuesOnce(Profile profile)
    {
        try
        {
            var cfg = AppConfig.Load();
            if (cfg.GlobalCues is not null) return;
            cfg.GlobalCues = CuesOf(profile).OnlyGlobalCues();
            cfg.Save();
        }
        catch { /* best-effort, like the app's other AppConfig writes */ }
    }

    /// <summary>Every cue as <paramref name="profile"/> played it: its own, or for a profile an older RemSound saved,
    /// what that RemSound used with it.</summary>
    private static CueSettings CuesOf(Profile profile) => profile.Cues?.Clone() ?? CuesBefore(profile);

    /// <summary>ONE-TIME migration for the peers list going machine-wide (2026-07-16): profiles written by
    /// older builds carry their own remembered-peers list, so the first opened profile that has peers has
    /// them UNIONED into the AppConfig book (nothing lost, nothing overwritten), and a marker
    /// (<see cref="AppConfig.RememberedPeersMigrated"/>) then stops it re-running — otherwise a cleared
    /// list would be resurrected from the profile file on the next launch.</summary>
    private static void MigrateRememberedPeersToGlobal(Profile profile)
    {
        try
        {
            var c = AppConfig.Load();
            // ONE-TIME only. Re-running every launch re-unioned the profile file's stale copy, which
            // resurrected peers the user had just cleared in Preferences (the global store was emptied
            // but the profile JSON still held them). The marker stops that.
            if (c.RememberedPeersMigrated) return;
            if (profile.RememberedPeers is not { Count: > 0 } legacy) return; // nothing to migrate yet; try again with a profile that has peers
            var current = c.RememberedPeers ?? [];
            c.RememberedPeers = current
                .Concat(legacy.Where(static v => !string.IsNullOrWhiteSpace(v)).Select(static v => v.Trim()))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            c.RememberedPeersMigrated = true;
            c.Save();
        }
        catch { /* best-effort, like the app's other AppConfig writes */ }
    }

    /// <summary>The cues an older RemSound used with <paramref name="profile"/> (<see cref="CueSettings.FromBefore"/>), or if
    /// this computer's settings cannot be read, every cue on with its first sound.</summary>
    private static CueSettings CuesBefore(Profile profile)
    {
        try { return CueSettings.FromBefore(profile, AppConfig.Load()); }
        catch { return new CueSettings(); }
    }

    /// <summary>Copies the current in-memory settings cache into a Profile. Note: this only
    /// covers the fields the settings store has historically known about — the device-tick
    /// state, send/receive checkbox state, audio port, volume slider, and selected-peer
    /// state live on the form itself and are gathered by the form when saving a profile.</summary>
    public void CopyTo(Profile profile)
    {
        if (profile is null) throw new ArgumentNullException(nameof(profile));
        var s = cache;
        // Keyboard shortcuts moved to AppConfig (machine-wide) in v4.4 — no longer written to profiles.
        if (s.AcceptRemoteVolumeCommands is bool arvc) profile.AcceptRemoteVolumeCommands = arvc;
        if (s.WebListenEnabled is bool wle) profile.WebListenEnabled = wle;
        profile.WebListenPassword = s.WebListenPassword;
        if (s.WebListenAutoStart is bool wla) profile.WebListenAutoStart = wla;
        if (s.MaxLatencyMs is int ml) profile.MaxLatencyMs = ml;
        if (s.Codec is AudioTransportCodec c) profile.Codec = c;
        if (s.OpusFrameSamplesPerChannel is int op) profile.OpusFrameSamplesPerChannel = op;
        if (s.ContinuousAutoTuneEnabled is bool cae) profile.ContinuousAutoTuneEnabled = cae;
        if (s.ContinuousAutoTuneIntervalSec is int cai) profile.ContinuousAutoTuneIntervalSec = cai;
        if (s.MaxLatencyMsAsio is int mla) profile.MaxLatencyMsAsio = mla;
        if (s.ContinuousAutoTuneAsioEnabled is bool cata) profile.ContinuousAutoTuneAsioEnabled = cata;
        if (s.AutoTuneLowestDelay is bool atld) profile.AutoTuneLowestDelay = atld;
        if (s.EnableLogs is bool logs) profile.EnableLogs = logs;
        // Write the CURRENT machine-wide peers book into the profile on save. The global AppConfig copy
        // is the authority now; this snapshot just keeps older builds (which read the profile's list)
        // working against the same profile file, and makes the load-time migration union a no-op.
        profile.RememberedPeers = new List<string>(LoadRememberedPeers());
        profile.AsioDriverName = s.AsioDriverName;
        if (s.AsioBufferFrames is int abf) profile.AsioBufferFrames = abf;
        if (s.SendRate is SendRate sr) profile.SendRate = sr;
        if (s.TightLatencyMode is bool tl) profile.TightLatencyMode = tl;
        if (s.LockPeerAddresses is bool lpa) profile.LockPeerAddresses = lpa;
        if (s.Smoothness is int sm) profile.Smoothness = sm;
        if (s.ConcealmentArtifact is ConcealmentArtifact ca) profile.ConcealmentArtifactRaw = (int)ca;
        // The profile's own cues; the global sounds are this computer's. The older per-cue fields are left unset, so the
        // saved file no longer carries them.
        profile.Cues = (s.Cues ?? new CueSettings()).OnlyProfileCues();
        if (s.RecordingSettings is RecordingSettings rs) profile.RecordingSettings = rs.Clone();
        if (s.PushToTalk is PushToTalkSettings ptt) profile.PushToTalk = ptt.Clone();
        if (s.Metronome is MetronomeSettings metronome) profile.Metronome = metronome.Clone();
    }

    private sealed class Settings
    {
        public bool? AcceptRemoteVolumeCommands { get; set; }
        public bool? WebListenEnabled { get; set; }
        /// <summary>Scrambled, as the profile keeps it.</summary>
        public string? WebListenPassword { get; set; }
        public bool? WebListenAutoStart { get; set; }
        public int? MaxLatencyMs { get; set; }
        public AudioTransportCodec? Codec { get; set; }
        // Renamed 2026-05-23 (v3.0). Was OpusFrameMilliseconds; the value is samples-per-channel at
        // 48 kHz. The cache is in-memory only; on disk the profile keeps the old JSON key.
        public int? OpusFrameSamplesPerChannel { get; set; }
        public bool? ContinuousAutoTuneEnabled { get; set; }
        public int? ContinuousAutoTuneIntervalSec { get; set; }
        // The ASIO lane's slider and auto-tune switch, used when an ASIO driver is chosen
        // (BothIndependent). MaxLatencyMs above governs the other slider — the WASAPI lane with a
        // driver chosen, the Mixed route without one — so existing profiles kept their slider value
        // on upgrade. The auto-tune companion lets the user opt either lane in or out independently.
        public int? MaxLatencyMsAsio { get; set; }
        public bool? ContinuousAutoTuneAsioEnabled { get; set; }
        public bool? AutoTuneLowestDelay { get; set; }
        public bool? EnableLogs { get; set; }
        // RememberedPeers / RememberedApplications retired from this cache 2026-07-16 — both books are
        // machine-wide in AppConfig now (this cache is intra-process only, so applications were being
        // forgotten on every exit and peers were per-profile in practice).
        public string? AsioDriverName { get; set; }
        public int? AsioBufferFrames { get; set; }
        public SendRate? SendRate { get; set; }
        public bool? TightLatencyMode { get; set; }
        public bool? LockPeerAddresses { get; set; }
        public int? Smoothness { get; set; }
        public ConcealmentArtifact? ConcealmentArtifact { get; set; }
        // The profile's own audio cues (2026-10-08). Null only on a cache no profile or new profile has filled yet, which
        // treats every cue as on with its first sound.
        public CueSettings? Cues { get; set; }
        public RecordingSettings? RecordingSettings { get; set; }
        public PushToTalkSettings? PushToTalk { get; set; }
        public MetronomeSettings? Metronome { get; set; }
    }

}
