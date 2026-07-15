using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace RemSound.Core;

/// <summary>
/// A saved snapshot of every user-controllable RemSound setting. Replaces the old
/// machine-wide "settings" file. Profiles live as one JSON file per profile under
/// <c>&lt;exe&gt;\profiles\&lt;machine name&gt;\&lt;title&gt;.json</c> and are portable —
/// copying a profile JSON to another machine's profiles folder makes it appear in that
/// machine's selection list. Device IDs stored in a profile (sound cards, ASIO drivers)
/// that don't exist on the loading machine are silently ignored on apply, so a profile
/// can roam between machines with different hardware without erroring out.
///
/// Design point: profiles capture EVERY UI control state, including device ticks. The
/// previous design rule was to NOT persist device selections (start unticked every
/// session). Profiles deliberately override that — the whole point is one-click
/// restoration. If a user wants the old "start fresh" behaviour, they pick the blank
/// template at startup.
/// </summary>
public sealed class Profile
{
    /// <summary>Display title and filename stem (sanitised). Required.</summary>
    public string Title { get; set; } = "";

    /// <summary>If true, this profile is loaded for use but the app never writes the user's
    /// in-session changes back to disk: Ctrl+S / File → Save politely refuses (with a "use
    /// Save As instead" message), and FormClosing skips its usual "save changes?" prompt
    /// entirely. Whatever the user fiddled with this session is kept in memory until the
    /// app closes and then discarded; the file on disk stays exactly as it was. Off by
    /// default. Toggled per-profile via File → Lock profile (read-only). Use case: a
    /// "default" profile you want to live in and toggle send/receive on without the close
    /// prompt blocking shutdown — important for users who can't reach the prompt because
    /// they're remote, or because the screen reader has crashed, or because the laptop is
    /// hibernating. The flag is the *only* property the lock-toggle writes back to disk;
    /// any other in-session edits stay session-only. 2026-05-22.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>The profile's encryption password, stored LIGHTLY SCRAMBLED on disk (via
    /// <see cref="RemSoundCrypto.Obfuscate"/> — not real encryption, just so it isn't legible at
    /// a glance in a possibly-synced JSON file). Null/empty = no password set yet. Two peers can
    /// exchange audio only when their profile passwords match, because the audio key is derived
    /// from this. In memory the running value is the plain text; this field holds the scrambled
    /// form. Added 2026-05-31 for the always-on encryption feature.</summary>
    public string? Password { get; set; }

    // === Main form: send / receive ===
    public bool ReceiveAudioOn { get; set; }
    public bool SendAudioOn { get; set; }
    public int Volume { get; set; } = 100;
    public bool Muted { get; set; }

    // === Audio backend ===
    /// <summary>The ASIO driver this profile uses. <c>null</c> or empty means "no ASIO" —
    /// the form runs in WASAPI-only mode. Any other value selects an ASIO driver and puts
    /// the form into the WASAPI + ASIO independent-lane mode. There is no separate audio-mode
    /// field on the profile any more: the mode is derived from this name alone (2026-05-11
    /// cleanup retired the old AudioMode listbox and its persisted enum). Old profile JSONs
    /// that still contain <c>"AudioModeRaw"</c> or <c>"BothModeWarningSuppressed"</c> simply
    /// have those keys ignored on deserialisation.</summary>
    public string? AsioDriverName { get; set; }

    // === Selected devices (raw device IDs, not display names) ===
    public List<string> SelectedWasapiReceiveOutputs { get; set; } = [];
    public List<string> SelectedAsioReceiveOutputs { get; set; } = [];
    public List<string> SelectedWasapiSendOutputs { get; set; } = [];   // loopback (system audio)
    public List<string> SelectedWasapiSendInputs { get; set; } = [];    // microphones / line-ins
    public List<string> SelectedAsioSendInputs { get; set; } = [];

    // === WASAPI send mode: whole devices vs specific applications (issue #20) ===
    /// <summary>How the WASAPI send side captures system audio: <c>"devices"</c> (the classic
    /// "WASAPI audio outputs to send" loopback list — default) or <c>"applications"</c>
    /// (per-application process loopback — Windows 10 build 19041+ only). Chosen by the listbox on
    /// the Input/Output tab, right after the send section; the tab shows the matching list. Saved per
    /// profile so one setup can send whole devices and another send just a couple of apps. ASIO capture
    /// runs alongside either way. On a machine too old for process loopback this is forced back to
    /// "devices" on load.</summary>
    public string WasapiSendMode { get; set; } = "devices";
    /// <summary>In "applications" send mode: when true (the default) every app's audio is sent — i.e.
    /// exactly the same result as sending the whole system's default output. When false, only the apps
    /// named in <see cref="SelectedSendApplications"/> are sent. Mirrors the "Send all applications"
    /// master checkbox.</summary>
    public bool SendAllApplications { get; set; } = true;
    /// <summary>In "applications" send mode with <see cref="SendAllApplications"/> off: the process
    /// names (lower-case, no path/extension, e.g. "vlc", "firefox") whose audio to send. Tracked by
    /// NAME not PID so the selection survives an app restarting. Apps not currently running stay in the
    /// list (remembered) and start being captured again the moment they reappear.</summary>
    public List<string> SelectedSendApplications { get; set; } = [];

    /// <summary>The profile's REMEMBERED application names — apps the user has set this profile up to send,
    /// whether or not they're running right now (the "Remembered applications" list, mirroring remembered
    /// peers but profile-scoped). Includes apps that were added by name while closed. A subset of these is
    /// ticked/active at any time (that active subset is <see cref="SelectedSendApplications"/>); the rest
    /// stay remembered so they can be re-ticked later. Lower-case process names, no path/extension.</summary>
    public List<string> RememberedSendApplications { get; set; } = [];

    // === Connectivity & transport ===
    public int AudioPort { get; set; } = 47830;
    public int CodecRaw { get; set; } = (int)AudioTransportCodec.Pcm;
    /// <summary>Opus frame size in samples-per-channel at 48 kHz. 120 = 2.5 ms, 240 = 5 ms,
    /// 480 = 10 ms (default), 960 = 20 ms. Renamed from <c>OpusFrameMilliseconds</c> in the
    /// v3.0 wire-format refactor (2026-05-23). The JSON key is kept as
    /// <c>OpusFrameMilliseconds</c> for back-compat with v2.x profile files; on read,
    /// <see cref="RemSoundSettingsStore.LoadOpusFrameSamplesPerChannel"/> disambiguates the
    /// legacy integer-ms encoding (5/10/20) from the new sample-count encoding
    /// (120/240/480/960) using a sentinel: any persisted value &lt; 120 is treated as ms and
    /// multiplied by 48. v2.x readers loading a v3.x profile see e.g. 480 and clamp it to
    /// their accept-list (which only knows 10 or 20), defaulting silently to 10 ms.</summary>
    [JsonPropertyName("OpusFrameMilliseconds")]
    public int OpusFrameSamplesPerChannel { get; set; } = 480;
    public int SendRateRaw { get; set; } = (int)SendRate.Standard;
    public bool TightLatencyMode { get; set; }
    /// <summary>True if this profile asks Windows to keep the RemSound process in
    /// high-priority mode while it's running — CPU scheduling, power management, memory
    /// priority, working-set lock, and MMCSS thread priority all elevated. Off by default;
    /// the user opts in per profile when they want a "live session" feel where the
    /// cold-start CPU ramp doesn't audibly hurt latency. On laptops that drains the
    /// battery faster; on desktops it costs a couple of extra watts. See
    /// <c>PerformanceMode</c> in the App project for the full lever list. Saved per
    /// profile (not in AppConfig) because the right answer genuinely differs between
    /// profiles.</summary>
    public bool PriorityMode { get; set; }
    /// <summary>Legacy combined "mute connect/disconnect sounds" toggle. True suppresses
    /// both connect AND disconnect cues. Superseded 2026-05-15 by the four individual
    /// <c>Enable*Cue</c> flags below — the new flags take precedence when set. This field
    /// is preserved on the profile for backward compatibility with older builds that don't
    /// know about the per-cue flags; on first load the per-cue flags inherit from this
    /// (true → connect+disconnect cues disabled).</summary>
    public bool MuteConnectionCues { get; set; }

    /// <summary>Per-cue enable flags. Nullable so a missing entry in an older profile JSON
    /// falls back to the legacy <see cref="MuteConnectionCues"/> migration path; once the
    /// user touches the new UI we write a concrete <c>true</c>/<c>false</c> and the legacy
    /// field stops mattering. Defaults to "play the sound" (true) for both cases — the
    /// audio cues are part of the normal user feedback loop, not opt-in. 2026-05-15.
    ///
    /// EnableSaveCue / EnableProfileSwitchCue added 2026-05-28 alongside the new
    /// save.wav / profile.wav defaults. Same nullable + default-true semantics as the rest.</summary>
    public bool? EnableConnectCue { get; set; }
    public bool? EnableDisconnectCue { get; set; }
    public bool? EnableRecordStartCue { get; set; }
    public bool? EnableRecordStopCue { get; set; }
    public bool? EnableSaveCue { get; set; }
    public bool? EnableProfileSwitchCue { get; set; }
    /// <summary>When true (the default), the "profile menu open" cue plays as the Quick profile
    /// switch popup opens. Per-profile so each setup can mute it independently.</summary>
    public bool? EnableProfileMenuOpenCue { get; set; }
    /// <summary>Plays the update cue just before an update starts installing (manual or
    /// silent). Null = unset → defaults to on, so a silent background update still gives an
    /// audible heads-up. Added 2026-05-31.</summary>
    public bool? EnableUpdateCue { get; set; }

    /// <summary>Per-cue custom WAV file overrides, keyed by the well-known cue id (<c>connect</c>,
    /// <c>disconnect</c>, <c>record-start</c>, <c>record-stop</c>, <c>save</c>,
    /// <c>profile-switch</c>, <c>update</c>) and valued with the absolute filesystem path to the user's chosen
    /// WAV. Per-profile (moved here from AppConfig 2026-05-28) so a "live monitoring" profile
    /// can have one set of custom sounds and a "recording" profile a different set. Missing
    /// keys mean "use the default sound shipped in the sounds\ folder next to RemSound.exe".
    /// Empty dictionary on a fresh profile.</summary>
    public Dictionary<string, string> CustomCuePaths { get; set; } = new();
    public int MaxLatencyMs { get; set; } = 80;
    public int Smoothness { get; set; } = 3;
    public bool ContinuousAutoTuneEnabled { get; set; }
    public int ContinuousAutoTuneIntervalSec { get; set; } = 5;
    /// <summary>Per-route latency for the ASIO lane in AudioMode.BothIndependent. Default
    /// 10 ms because BothIndependent's value proposition is letting ASIO run at its native
    /// low latency; users who pick that mode almost always want ASIO closer to 10 than 80.
    /// Ignored in every classic mode.</summary>
    public int MaxLatencyMsAsio { get; set; } = 10;
    /// <summary>Continuous auto-tune toggle for the ASIO lane (BothIndependent only).
    /// Defaults false to match the WASAPI-lane default — symmetric off-by-default avoids
    /// the trap where the ASIO lane auto-inflates its target while WASAPI sits fixed at
    /// its slider, producing higher ASIO latency than WASAPI in the typical session.</summary>
    public bool ContinuousAutoTuneAsioEnabled { get; set; }
    // LoggingEnabled was retired from Profile — logging is a machine-local debug knob
    // (AppConfig.LoggingEnabled), not a per-profile setting. Old profile JSONs that still
    // contain "LoggingEnabled" just have the key ignored on load.
    /// <summary>Receiver-side concealment artifact, stored as raw int for JSON-stability
    /// across enum-reorderings. Defaults to <see cref="ConcealmentArtifact.NoiseBurst"/>
    /// (the cosine-tone variants were removed from the dropdown in Phase 3 cleanup —
    /// 2026-05-06 — but the enum values stay around so old profile JSONs still parse;
    /// the dialog coerces any cosine-tone value to NoiseBurst at load time).</summary>
    public int ConcealmentArtifactRaw { get; set; } = (int)ConcealmentArtifact.NoiseBurst;

    [JsonIgnore]
    public ConcealmentArtifact ConcealmentArtifact
    {
        get => (ConcealmentArtifact)ConcealmentArtifactRaw;
        set => ConcealmentArtifactRaw = (int)value;
    }

    // === Recording ===
    /// <summary>Recording source / format / attributes. The whole settings object is saved
    /// per profile so different profiles can record different things (a "long session"
    /// profile might record everything to MP3, a "monitoring" profile might not record at
    /// all but keep the dialog defaults sensible). The recording isn't running until the
    /// user explicitly triggers it via the Record menu; this just holds the configuration
    /// the recorder picks up when it starts.</summary>
    public RecordingSettings RecordingSettings { get; set; } = new();

    // === Peers ===
    public List<string> RememberedPeers { get; set; } = [];
    /// <summary>Peer addresses (IP or host[:port]) the user had ticked in the connected
    /// list at save time. On load, RemSound auto-connects to any of these that resolve.</summary>
    public List<string> SelectedConnectedPeers { get; set; } = [];

    /// <summary>When true, this profile is LOCKED to the exact peer addresses set above. RemSound uses
    /// only those addresses, never matches the other computer by the name it advertises on the network,
    /// and never switches to a different address it discovers — even if the set address stops working
    /// (the connection simply waits or drops rather than moving). Off by default. (#17)</summary>
    public bool LockPeerAddresses { get; set; }

    // === Per-peer volume, pan and EQ (jam mixing) ===
    /// <summary>Single master switch for this profile: apply each connected peer's saved VOLUME, PAN and
    /// EQ. Off by default. The values themselves live in <see cref="PeerShaping"/>; a peer can also be
    /// bypassed individually via <see cref="RemSound.Core.PeerShaping.Enabled"/>.</summary>
    public bool EnableAllPeerShaping { get; set; }

    /// <summary>Legacy separate master switches (pre-collapse). Kept only so older profile JSON still
    /// deserialises and migrates: on load, either being true turns the single master switch on.</summary>
    public bool EnablePanForPeers { get; set; }
    public bool EnableEqForPeers { get; set; }

    /// <summary>Per-peer pan + EQ, keyed by the SAME peer-entry string used in
    /// <see cref="SelectedConnectedPeers"/> (user text / discovery label / address:port). A peer with
    /// no entry gets centre pan and flat EQ. Empty on a fresh profile.</summary>
    public Dictionary<string, PeerShaping> PeerShaping { get; set; } = new();

    // === Hotkeys ===
    public HotkeyRecord? ReceiveMuteHotkey { get; set; }
    public HotkeyRecord? SendMuteHotkey { get; set; }
    public HotkeyRecord? TrayHotkey { get; set; }
    public HotkeyRecord? VolumeUpHotkey { get; set; }
    public HotkeyRecord? VolumeDownHotkey { get; set; }
    /// <summary>Global hotkey for start / stop recording. Toggles the same action as the
    /// Record menu's "Start recording / Stop recording" item and the in-app Ctrl+R, but
    /// works system-wide (RemSound doesn't need keyboard focus). Default unset — recording
    /// is uncommon enough that we don't claim a default chord that might clash with the
    /// user's other tools.</summary>
    public HotkeyRecord? ToggleRecordingHotkey { get; set; }
    /// <summary>Hotkey that sends a "raise volume" command to every connected peer that has
    /// "Accept remote volume commands" enabled. The local volume slider on this machine is
    /// NOT touched. Use case: I'm NVDA-Remote'd into another machine and want to nudge the
    /// listening volume on the laptop I'm physically at without breaking out of the session.</summary>
    public HotkeyRecord? RemoteVolumeUpHotkey { get; set; }
    /// <summary>Mirror of RemoteVolumeUpHotkey for "lower volume" commands.</summary>
    public HotkeyRecord? RemoteVolumeDownHotkey { get; set; }
    /// <summary>Hotkey that sends a "toggle receive mute" command to every connected peer.</summary>
    public HotkeyRecord? RemoteMuteToggleHotkey { get; set; }
    /// <summary>Hotkey that sends a "raise Windows default-output-device volume by one step"
    /// command to every connected peer that has Accept remote volume commands enabled. Each
    /// press bumps the receiving peer's Windows master volume by the OS native step (~2%) —
    /// same as pressing the keyboard volume key on the receiver. System-wide on the receiver:
    /// affects every app on that machine including its screen reader.</summary>
    public HotkeyRecord? SystemVolumeUpHotkey { get; set; }
    /// <summary>Mirror of SystemVolumeUpHotkey for the down direction.</summary>
    public HotkeyRecord? SystemVolumeDownHotkey { get; set; }
    /// <summary>Hotkey that sends a "toggle Windows default-output-device mute" command to
    /// every connected peer.</summary>
    public HotkeyRecord? SystemMuteToggleHotkey { get; set; }
    /// <summary>Global hotkey that opens the Quick profile switch popup — a list of all profiles you
    /// can arrow through and press Enter to switch to, from anywhere in Windows. Unset by default.</summary>
    public HotkeyRecord? QuickProfileSwitchHotkey { get; set; }
    /// <summary>Optional global hotkey that speaks the connection status line aloud through the active
    /// screen reader (issue #13). Unset by default. Screen-reader specific.</summary>
    public HotkeyRecord? SpeakStatusLineHotkey { get; set; }
    /// <summary>When true, this machine honours incoming Control packets from connected
    /// peers — adjusts the local volume slider or toggles mute. Default false: receiving
    /// remote control is opt-in even though the audio allow-list already gates who's
    /// connected. Lets a user have one profile that's controllable (home setup, single
    /// trusted peer) and another that's not (one-off jam session, public-ish peer).</summary>
    public bool AcceptRemoteVolumeCommands { get; set; }

    // === JSON-friendly accessors (so callers don't deal with the raw int casts) ===
    // AudioMode accessor + AudioModeRaw backing field retired 2026-05-11. The runtime mode is
    // now derived from AsioDriverName; there is no separate persisted enum.
    [JsonIgnore]
    public AudioTransportCodec Codec
    {
        get => (AudioTransportCodec)CodecRaw;
        set => CodecRaw = (int)value;
    }

    [JsonIgnore]
    public SendRate SendRate
    {
        get => (SendRate)SendRateRaw;
        set => SendRateRaw = (int)value;
    }

    /// <summary>Returns a defaults-only profile — same shape as the "blank template"
    /// the user picks at startup. Title is empty (caller assigns when saving).</summary>
    public static Profile NewBlank() => new();
}

/// <summary>JSON-serialisable hotkey representation. Mirrors <see cref="HotkeyInfo"/>
/// but stores Key as a string to keep the JSON robust to enum reorganisations.</summary>
public sealed class HotkeyRecord
{
    public string Key { get; set; } = "M";
    public bool Control { get; set; }
    public bool Shift { get; set; }
    public bool Alt { get; set; }

    public static HotkeyRecord From(HotkeyInfo hotkey) => new()
    {
        Key = hotkey.Key.ToString(),
        Control = hotkey.Control,
        Shift = hotkey.Shift,
        Alt = hotkey.Alt,
    };

    public HotkeyInfo ToHotkeyInfo()
    {
        if (!Enum.TryParse<Keys>(Key, out var parsedKey)) return HotkeyInfo.Default;
        var hotkey = new HotkeyInfo(parsedKey, Control, Shift, Alt);
        if (hotkey.IsUnset) return HotkeyInfo.Unset;
        return hotkey.IsValid ? hotkey : HotkeyInfo.Default;
    }
}
