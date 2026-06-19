using System.Windows.Forms;

namespace RemSound.Core;

/// <summary>
/// In-memory cache of UI/runtime preferences. As of 2026-05-02 this no longer persists to
/// disk — RemSound's persistence layer is the profile system (<see cref="Profile"/> /
/// <see cref="ProfileStore"/>), and this class is just an intra-process holding area that
/// the active profile populates on app startup and reads back from when the user saves a
/// profile. Old <c>configs/</c> folders from prior builds are ignored. Constructor still
/// takes an <c>appName</c> for backwards compatibility but it's unused.
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
        s.MaxLatencyMs = Math.Clamp(value, 1, 500);
        Save(s);
    }

    /// <summary>
    /// Per-route latency settings used only in BothIndependent audio mode. The existing
    /// <see cref="LoadMaxLatencyMs"/> / <see cref="SaveMaxLatencyMs"/> govern the WASAPI lane
    /// (which is what the existing slider has always controlled — every classic mode reads
    /// it the same way pre-Stage-4.5). The ASIO companion below stores the ASIO lane's
    /// target. Default 10 ms because the whole point of the new mode is to let ASIO run at
    /// its native low latency; if the user has picked BothIndependent they almost certainly
    /// want ASIO closer to 10 than to 80.
    /// </summary>
    public int LoadMaxLatencyMsAsio(int defaultValue = 10) =>
        Try(() => Load()?.MaxLatencyMsAsio is int v ? Math.Clamp(v, 5, 500) : (int?)null) ?? defaultValue;

    public void SaveMaxLatencyMsAsio(int value)
    {
        var s = Load() ?? new Settings();
        s.MaxLatencyMsAsio = Math.Clamp(value, 1, 500);
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

    /// <summary>Loads the Opus frame size in samples-per-channel at 48 kHz. Default 480 = 10 ms.
    /// Migration path: profiles written by v2.x stored milliseconds (5/10/20) in the same JSON
    /// field; values &lt; 120 are interpreted as legacy ms and converted (×48 → samples). The
    /// ranges don't overlap (max legitimate ms = 60, min legitimate samples = 120), so the
    /// disambiguation is unambiguous.</summary>
    public int LoadOpusFrameSamplesPerChannel(int defaultValue = 480) =>
        Try(() => Load()?.OpusFrameSamplesPerChannel is int v ? NormalizeOpusFrameSamples(v) : (int?)null) ?? defaultValue;

    /// <summary>Saves the Opus frame size in samples-per-channel at 48 kHz. Accepts the four
    /// standard-Opus RESTRICTED_LOWDELAY values (120/240/480/960); anything else collapses to
    /// 480 (= 10 ms), the safe default.</summary>
    public void SaveOpusFrameSamplesPerChannel(int value)
    {
        var s = Load() ?? new Settings();
        s.OpusFrameSamplesPerChannel = value switch
        {
            960 => 960,  // 20 ms
            480 => 480,  // 10 ms
            240 => 240,  // 5 ms — not exposed in the dropdown but reachable via Tight rate
            120 => 120,  // 2.5 ms experimental
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

    public int LoadContinuousAutoTuneIntervalSec(int defaultValue = 5) =>
        Try(() => Load()?.ContinuousAutoTuneIntervalSec is int v && v >= 5 && v <= 60 ? v : (int?)null) ?? defaultValue;

    public void SaveContinuousAutoTuneIntervalSec(int value)
    {
        var s = Load() ?? new Settings();
        s.ContinuousAutoTuneIntervalSec = Math.Clamp(value, 5, 60);
        Save(s);
    }

    public IReadOnlyList<string> LoadRememberedPeers() =>
        Try(() => Load()?.RememberedPeers?
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        ?? [];

    public void SaveRememberedPeers(IEnumerable<string> peers)
    {
        var s = Load() ?? new Settings();
        s.RememberedPeers = peers
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Save(s);
    }

    // LoggingEnabled lives in AppConfig now — it's a machine-local debug knob, not a
    // per-profile setting. LoadLoggingEnabled / SaveLoggingEnabled were retired here;
    // callers go to AppConfig.LoggingEnabled directly.

    /// <summary>
    /// Audio mode is derived from whether an ASIO driver is selected. Pre-2026-05-11 this was
    /// a user-facing setting with its own listbox; now the UI is simpler — the user just picks
    /// an ASIO driver (or "(none)" to disable ASIO) and the mode follows. A real driver chosen
    /// means BothIndependent (WASAPI + ASIO running side by side, each at its own latency);
    /// no driver means WasapiOnly. The AudioMode field still exists on the persisted Settings
    /// JSON purely for backward compat with old profiles — its value is ignored on load. The
    /// matching SaveAudioMode setter was deleted with the listbox in the 2026-05-11 cleanup;
    /// callers that used to invoke it have been removed.
    /// </summary>
    public AudioMode LoadAudioMode(AudioMode defaultValue = AudioMode.WasapiOnly) =>
        string.IsNullOrWhiteSpace(LoadAsioDriverName()) ? AudioMode.WasapiOnly : AudioMode.BothIndependent;

    // BothModeWarningSuppressed used to live here. Moved to AppConfig (remsound.config.json,
    // machine-local) on 2026-05-07 — a "do not show me this again" decision shouldn't be
    // tied to which profile is active. The accessors were removed; callers go to AppConfig
    // directly. Profile.BothModeWarningSuppressed is left in place to deserialise old JSONs
    // (one-shot migrated to AppConfig in MainForm's constructor).

    public SendRate LoadSendRate(SendRate defaultValue = SendRate.Standard) =>
        Try(() => Load()?.SendRate is SendRate v ? v : (SendRate?)null) ?? defaultValue;

    public void SaveSendRate(SendRate value)
    {
        var s = Load() ?? new Settings();
        s.SendRate = value;
        Save(s);
    }

    /// <summary>Tight-latency mode toggle. Sender-side only as of 2026-05-06 (the receiver no
    /// longer has a resampler to bypass). In WasapiOnly + single source mode the sender swaps
    /// from the timer-driven MixingEngine to the audio-clock-locked PushModeWasapiBackend; in
    /// AsioOnly + PCM mode the sender emits one packet per ASIO callback instead of accumulating
    /// to the chosen frame size. Saves a few ms of send-side latency at the cost of brief
    /// clicks if the link can't keep up. Off by default.</summary>
    public bool LoadTightLatencyMode(bool defaultValue = false) =>
        Try(() => Load()?.TightLatencyMode) ?? defaultValue;

    public void SaveTightLatencyMode(bool value)
    {
        var s = Load() ?? new Settings();
        s.TightLatencyMode = value;
        Save(s);
    }

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

    /// <summary>Suppresses the connect/disconnect sound cues that play when a peer's health
    /// transitions to/from Healthy. Off by default — cues are on. Saved per-profile so users
    /// who don't want them in a given setup don't have to remember to mute every session.
    /// 2026-05-06.</summary>
    public bool LoadMuteConnectionCues(bool defaultValue = false) =>
        Try(() => Load()?.MuteConnectionCues) ?? defaultValue;

    public void SaveMuteConnectionCues(bool value)
    {
        var s = Load() ?? new Settings();
        s.MuteConnectionCues = value;
        Save(s);
    }

    // === Per-cue enable flags (2026-05-15) ===
    // Each cue sound has its own enable toggle, surfaced in the Preferences dialog as a
    // CheckedListBox. The legacy MuteConnectionCues above used to gate both connect AND
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
    /// override (the cue reverts to the bundled default in <c>sounds\</c>).</summary>
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

    /// <summary>How aggressively the receiver pulls the playout queue back to the user's
    /// target latency under network jitter. 1 = stupid aggressive (~10 % playback rate change,
    /// audible pitch shift on drift, sub-second recovery). 10 = perfectly smooth (gentle
    /// controller, no audible artefacts, slow recovery — buffer can creep up over a long
    /// session). Lower is faster-but-less-stable, like the latency slider. Default is 3 —
    /// quite aggressive but not the extreme; user dials down for tighter, up for smoother.</summary>
    public int LoadSmoothness(int defaultValue = 3) =>
        Try(() => Load()?.Smoothness is int v ? Math.Clamp(v, 1, 10) : (int?)null) ?? defaultValue;

    public void SaveSmoothness(int value)
    {
        var s = Load() ?? new Settings();
        s.Smoothness = Math.Clamp(value, 1, 10);
        Save(s);
    }

    /// <summary>Receiver-side concealment artifact pick. See <see cref="ConcealmentArtifact"/>
    /// for what each value sounds like. Default is <see cref="ConcealmentArtifact.NoiseBurst"/>
    /// — the cosine-tone defaults were removed in Phase 3 cleanup (they sounded harsh on
    /// orchestral content). Old profiles holding a CosineTone* enum value still load fine;
    /// the dialog dropdown coerces them to NoiseBurst on display.</summary>
    public ConcealmentArtifact LoadConcealmentArtifact(ConcealmentArtifact defaultValue = ConcealmentArtifact.NoiseBurst) =>
        Try(() => Load()?.ConcealmentArtifact is ConcealmentArtifact v ? v : (ConcealmentArtifact?)null) ?? defaultValue;

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

    // 2026-05-02: persistence moved out of this class. RemSound now manages settings via the
    // profile system (RemSound.Core.Profile / ProfileStore), and the settings store has become
    // a per-process in-memory cache that the active profile populates on load and reads back
    // from on save. Disk IO from this class is intentionally a no-op now: the old configs/
    // folder is no longer written to. If a configs/ folder exists from a previous build, it's
    // ignored — users are expected to re-create their setup as a Profile via the new dialog.
    private Settings cache = new();

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
            RememberedPeers = profile.RememberedPeers is null ? null : new List<string>(profile.RememberedPeers),
            AsioDriverName = profile.AsioDriverName,
            // Profile.AudioModeRaw and Profile.BothModeWarningSuppressed are no longer carried
            // through the settings cache. Both fields are retired (2026-05-07 / 2026-05-11);
            // mode is derived from AsioDriverName and the Both-mode warning popup is gone.
            SendRate = profile.SendRate,
            TightLatencyMode = profile.TightLatencyMode,
            PriorityMode = profile.PriorityMode,
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
        if (s.RememberedPeers is { } rp) profile.RememberedPeers = new List<string>(rp);
        profile.AsioDriverName = s.AsioDriverName;
        // AudioMode and BothModeWarningSuppressed are not copied — both Profile fields were
        // retired in the 2026-05-11 cleanup. Mode is derived from AsioDriverName and the
        // popup that owned the suppression flag is gone.
        if (s.SendRate is SendRate sr) profile.SendRate = sr;
        if (s.TightLatencyMode is bool tl) profile.TightLatencyMode = tl;
        if (s.PriorityMode is bool pm) profile.PriorityMode = pm;
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
        // Per-route latency settings used in AudioMode.BothIndependent only. MaxLatencyMs
        // above continues to govern the WASAPI lane (= the only lane in classic modes), so
        // existing profiles keep their current slider value untouched on upgrade. Asio
        // companion below holds the ASIO lane's slider; the auto-tune enable companion lets
        // the user opt either lane in or out independently.
        public int? MaxLatencyMsAsio { get; set; }
        public bool? ContinuousAutoTuneAsioEnabled { get; set; }
        public List<string>? RememberedPeers { get; set; }
        public string? AsioDriverName { get; set; }
        // AudioMode and BothModeWarningSuppressed both retired from this cache. Mode is
        // derived from AsioDriverName via LoadAudioMode; the Both-mode warning popup is gone.
        public SendRate? SendRate { get; set; }
        public bool? TightLatencyMode { get; set; }
        public bool? PriorityMode { get; set; }
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
