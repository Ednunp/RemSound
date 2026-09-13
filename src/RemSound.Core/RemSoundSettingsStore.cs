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

    public AudioTransportCodec LoadCodec(AudioTransportCodec defaultValue = AudioTransportCodec.Pcm) =>
        Try(() => Load()?.Codec) ?? defaultValue;

    public void SaveCodec(AudioTransportCodec value)
    {
        var s = Load() ?? new Settings();
        s.Codec = value;
        Save(s);
    }

    /// <summary>Loads the Opus frame size in samples-per-channel at 48 kHz. Default 480 = 10 ms, the
    /// size from before the codec list offered only 960 and 120; MainForm.ResolveCodecIndex shows
    /// anything but 120 as the 960 choice.
    /// Migration path: profiles written by v2.x stored milliseconds (5/10/20) in the same JSON
    /// field; values &lt; 120 are interpreted as legacy ms and converted (×48 → samples). The
    /// ranges don't overlap (max legitimate ms = 60, min legitimate samples = 120), so the
    /// disambiguation is unambiguous.</summary>
    public int LoadOpusFrameSamplesPerChannel(int defaultValue = 480) =>
        Try(() => Load()?.OpusFrameSamplesPerChannel is int v ? NormalizeOpusFrameSamples(v) : (int?)null) ?? defaultValue;

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

    /// <summary>Disambiguates a persisted Opus frame-size value between the legacy v2.x
    /// integer-milliseconds storage (5/10/20) and the v3.x samples-per-channel storage
    /// (120/240/480/960). Values &lt; 120 are legacy ms; ≥ 120 are samples. See
    /// <see cref="LoadOpusFrameSamplesPerChannel"/>.</summary>
    private static int NormalizeOpusFrameSamples(int persisted)
    {
        if (persisted < 120) return persisted * 48; // legacy ms → samples at 48 kHz
        return persisted;
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
    public bool LoadPriorityMode(bool defaultValue = false) =>
        Try(() => Load()?.PriorityMode) ?? defaultValue;

    public void SavePriorityMode(bool value)
    {
        var s = Load() ?? new Settings();
        s.PriorityMode = value;
        Save(s);
    }

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

    // === Per-cue enable flags (2026-05-15) ===
    // Each cue sound has its own enable toggle, on the Preferences dialog's Audio cues tab. The
    // legacy MuteConnectionCues profile flag (2026-05-06) used to gate both connect AND
    // disconnect — when the new flags are absent (null cache + null profile), the load
    // helpers fall back to the legacy value as a migration step. Once the user touches
    // any per-cue toggle, that flag's load returns the explicit value directly and the
    // legacy field becomes irrelevant for that cue.

    public bool LoadEnableConnectCue()
    {
        var s = Load();
        if (s?.EnableConnectCue is bool v) return v;
        // Legacy fallback: an older profile with MuteConnectionCues=true was muting both
        // connect AND disconnect at once. Honour that intent on first load.
        if (s?.MuteConnectionCues == true) return false;
        return true;
    }

    public void SaveEnableConnectCue(bool value)
    {
        var s = Load() ?? new Settings();
        s.EnableConnectCue = value;
        Save(s);
    }

    public bool LoadEnableDisconnectCue()
    {
        var s = Load();
        if (s?.EnableDisconnectCue is bool v) return v;
        if (s?.MuteConnectionCues == true) return false;
        return true;
    }

    public void SaveEnableDisconnectCue(bool value)
    {
        var s = Load() ?? new Settings();
        s.EnableDisconnectCue = value;
        Save(s);
    }

    public bool LoadEnableRecordStartCue() =>
        Try(() => Load()?.EnableRecordStartCue) ?? true;

    public void SaveEnableRecordStartCue(bool value)
    {
        var s = Load() ?? new Settings();
        s.EnableRecordStartCue = value;
        Save(s);
    }

    public bool LoadEnableRecordStopCue() =>
        Try(() => Load()?.EnableRecordStopCue) ?? true;

    public void SaveEnableRecordStopCue(bool value)
    {
        var s = Load() ?? new Settings();
        s.EnableRecordStopCue = value;
        Save(s);
    }

    public bool LoadEnableSaveCue() =>
        Try(() => Load()?.EnableSaveCue) ?? true;

    public void SaveEnableSaveCue(bool value)
    {
        var s = Load() ?? new Settings();
        s.EnableSaveCue = value;
        Save(s);
    }

    public bool LoadEnableProfileSwitchCue() =>
        Try(() => Load()?.EnableProfileSwitchCue) ?? true;

    public void SaveEnableProfileSwitchCue(bool value)
    {
        var s = Load() ?? new Settings();
        s.EnableProfileSwitchCue = value;
        Save(s);
    }

    public bool LoadEnableProfileMenuOpenCue() =>
        Try(() => Load()?.EnableProfileMenuOpenCue) ?? true;

    public void SaveEnableProfileMenuOpenCue(bool value)
    {
        var s = Load() ?? new Settings();
        s.EnableProfileMenuOpenCue = value;
        Save(s);
    }

    public bool LoadEnableUpdateCue() =>
        Try(() => Load()?.EnableUpdateCue) ?? true;

    public void SaveEnableUpdateCue(bool value)
    {
        var s = Load() ?? new Settings();
        s.EnableUpdateCue = value;
        Save(s);
    }

    /// <summary>The user's custom WAV path for a given cue, or null when they're using the
    /// default. Per-profile (lives on <see cref="Profile.CustomCuePaths"/>) so different
    /// profiles can carry different cue palettes.</summary>
    public string? LoadCustomCuePath(string cueId)
    {
        return Try(() =>
        {
            var s = Load();
            if (s?.CustomCuePaths is { } dict && dict.TryGetValue(cueId, out var path)
                && !string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
            return (string?)null;
        });
    }

    /// <summary>Set a custom WAV path for a given cue. Pass null or empty to clear the
    /// override (the cue reverts to its shipped default in <c>default sounds\</c>,
    /// <see cref="AppConfig.SoundsDirectory"/>).</summary>
    public void SaveCustomCuePath(string cueId, string? path)
    {
        var s = Load() ?? new Settings();
        s.CustomCuePaths ??= new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(path))
        {
            s.CustomCuePaths.Remove(cueId);
        }
        else
        {
            s.CustomCuePaths[cueId] = path;
        }
        Save(s);
    }

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
            // Keyboard shortcuts are no longer per-profile (v4.4) — they live in AppConfig now and are
            // not carried through the profile cache. Profile's HotkeyRecord fields remain only so old
            // profile JSONs still deserialise; they're ignored.
            AcceptRemoteVolumeCommands = profile.AcceptRemoteVolumeCommands,
            MaxLatencyMs = profile.MaxLatencyMs,
            Codec = profile.Codec,
            OpusFrameSamplesPerChannel = profile.OpusFrameSamplesPerChannel,
            ContinuousAutoTuneEnabled = profile.ContinuousAutoTuneEnabled,
            ContinuousAutoTuneIntervalSec = profile.ContinuousAutoTuneIntervalSec,
            MaxLatencyMsAsio = profile.MaxLatencyMsAsio,
            ContinuousAutoTuneAsioEnabled = profile.ContinuousAutoTuneAsioEnabled,
            // RememberedPeers no longer rides the cache — it's machine-wide in AppConfig now; the
            // profile's legacy copy is unioned in below (migration) instead of replacing anything.
            AsioDriverName = profile.AsioDriverName,
            SendRate = profile.SendRate,
            TightLatencyMode = profile.TightLatencyMode,
            PriorityMode = profile.PriorityMode,
            LockPeerAddresses = profile.LockPeerAddresses,
            Smoothness = profile.Smoothness,
            ConcealmentArtifact = (ConcealmentArtifact)profile.ConcealmentArtifactRaw,
            MuteConnectionCues = profile.MuteConnectionCues,
            EnableConnectCue = profile.EnableConnectCue,
            EnableDisconnectCue = profile.EnableDisconnectCue,
            EnableRecordStartCue = profile.EnableRecordStartCue,
            EnableRecordStopCue = profile.EnableRecordStopCue,
            EnableSaveCue = profile.EnableSaveCue,
            EnableProfileSwitchCue = profile.EnableProfileSwitchCue,
            EnableProfileMenuOpenCue = profile.EnableProfileMenuOpenCue,
            EnableUpdateCue = profile.EnableUpdateCue,
            // Defensive copy so cache mutations don't leak into the in-memory Profile graph
            // (and vice-versa). Profile is loaded once at startup; the cache evolves through
            // the session and is written back via CopyTo on save.
            CustomCuePaths = profile.CustomCuePaths is null ? new() : new Dictionary<string, string>(profile.CustomCuePaths),
            RecordingSettings = profile.RecordingSettings?.Clone() ?? new RecordingSettings(),
        };
        MigrateRememberedPeersToGlobal(profile);
    }

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
        if (s.MaxLatencyMs is int ml) profile.MaxLatencyMs = ml;
        if (s.Codec is AudioTransportCodec c) profile.Codec = c;
        if (s.OpusFrameSamplesPerChannel is int op) profile.OpusFrameSamplesPerChannel = op;
        if (s.ContinuousAutoTuneEnabled is bool cae) profile.ContinuousAutoTuneEnabled = cae;
        if (s.ContinuousAutoTuneIntervalSec is int cai) profile.ContinuousAutoTuneIntervalSec = cai;
        if (s.MaxLatencyMsAsio is int mla) profile.MaxLatencyMsAsio = mla;
        if (s.ContinuousAutoTuneAsioEnabled is bool cata) profile.ContinuousAutoTuneAsioEnabled = cata;
        // Write the CURRENT machine-wide peers book into the profile on save. The global AppConfig copy
        // is the authority now; this snapshot just keeps older builds (which read the profile's list)
        // working against the same profile file, and makes the load-time migration union a no-op.
        profile.RememberedPeers = new List<string>(LoadRememberedPeers());
        profile.AsioDriverName = s.AsioDriverName;
        if (s.SendRate is SendRate sr) profile.SendRate = sr;
        if (s.TightLatencyMode is bool tl) profile.TightLatencyMode = tl;
        if (s.PriorityMode is bool pm) profile.PriorityMode = pm;
        if (s.LockPeerAddresses is bool lpa) profile.LockPeerAddresses = lpa;
        if (s.Smoothness is int sm) profile.Smoothness = sm;
        if (s.ConcealmentArtifact is ConcealmentArtifact ca) profile.ConcealmentArtifactRaw = (int)ca;
        if (s.MuteConnectionCues is bool mc) profile.MuteConnectionCues = mc;
        // Per-cue enable flags — copy through verbatim (nullable on both sides, so an
        // unset flag in the cache stays unset on the profile, letting the legacy
        // MuteConnectionCues path govern that cue on the next load).
        profile.EnableConnectCue = s.EnableConnectCue;
        profile.EnableDisconnectCue = s.EnableDisconnectCue;
        profile.EnableRecordStartCue = s.EnableRecordStartCue;
        profile.EnableRecordStopCue = s.EnableRecordStopCue;
        profile.EnableSaveCue = s.EnableSaveCue;
        profile.EnableProfileSwitchCue = s.EnableProfileSwitchCue;
        profile.EnableProfileMenuOpenCue = s.EnableProfileMenuOpenCue;
        profile.EnableUpdateCue = s.EnableUpdateCue;
        profile.CustomCuePaths = s.CustomCuePaths is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(s.CustomCuePaths);
        if (s.RecordingSettings is RecordingSettings rs) profile.RecordingSettings = rs.Clone();
    }

    private sealed class Settings
    {
        public bool? AcceptRemoteVolumeCommands { get; set; }
        public int? MaxLatencyMs { get; set; }
        public AudioTransportCodec? Codec { get; set; }
        // Renamed 2026-05-23 (v3.0). Was OpusFrameMilliseconds; value semantic shifted to
        // samples-per-channel at 48 kHz. The cache is in-memory only — no JSON migration is
        // needed here; on-disk migration happens in Profile via [JsonPropertyName].
        public int? OpusFrameSamplesPerChannel { get; set; }
        public bool? ContinuousAutoTuneEnabled { get; set; }
        public int? ContinuousAutoTuneIntervalSec { get; set; }
        // The ASIO lane's slider and auto-tune switch, used when an ASIO driver is chosen
        // (BothIndependent). MaxLatencyMs above governs the other slider — the WASAPI lane with a
        // driver chosen, the Mixed route without one — so existing profiles kept their slider value
        // on upgrade. The auto-tune companion lets the user opt either lane in or out independently.
        public int? MaxLatencyMsAsio { get; set; }
        public bool? ContinuousAutoTuneAsioEnabled { get; set; }
        // RememberedPeers / RememberedApplications retired from this cache 2026-07-16 — both books are
        // machine-wide in AppConfig now (this cache is intra-process only, so applications were being
        // forgotten on every exit and peers were per-profile in practice).
        public string? AsioDriverName { get; set; }
        public SendRate? SendRate { get; set; }
        public bool? TightLatencyMode { get; set; }
        public bool? PriorityMode { get; set; }
        public bool? LockPeerAddresses { get; set; }
        public int? Smoothness { get; set; }
        public ConcealmentArtifact? ConcealmentArtifact { get; set; }
        public bool? MuteConnectionCues { get; set; }
        // Per-cue enable flags (2026-05-15). Nullable so an absent value in the loaded
        // profile falls back to the legacy MuteConnectionCues migration path.
        public bool? EnableConnectCue { get; set; }
        public bool? EnableDisconnectCue { get; set; }
        public bool? EnableRecordStartCue { get; set; }
        public bool? EnableRecordStopCue { get; set; }
        public bool? EnableSaveCue { get; set; }
        public bool? EnableProfileSwitchCue { get; set; }
        public bool? EnableProfileMenuOpenCue { get; set; }
        public bool? EnableUpdateCue { get; set; }
        public Dictionary<string, string>? CustomCuePaths { get; set; }
        public RecordingSettings? RecordingSettings { get; set; }
    }

}
