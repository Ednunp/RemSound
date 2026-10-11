namespace RemSound.Core;

/// <summary>
/// A set of audio cue settings: each cue on or off, its chosen built-in sound or a file of the person's own, and the
/// keyboard clicks. Two lists of cues since 2026-10-08 (Ed: "one list of profile specific sounds and one of global
/// sounds"): the <see cref="GlobalCueIds"/> - profile saved, profile switched, profile menu open, update, and context help
/// opened and closed - are kept on the computer for every profile (<see cref="AppConfig.GlobalCues"/>); every other cue,
/// and the keyboard clicks, is the profile's own (<see cref="Profile.Cues"/>). One computer-wide switch,
/// <see cref="AppConfig.MuteAllCues"/>, turns them all off.
///
/// <para>A cue missing from a dictionary here has the defaults: on, its first built-in sound, no file of the person's
/// own. Keys are the cue ids (MainForm.CueId), which never change.</para>
/// </summary>
public sealed class CueSettings
{
    /// <summary>Cue id to on (true) or off (false). A cue that is not here is on.</summary>
    public Dictionary<string, bool> Enabled { get; set; } = new();

    /// <summary>Cue id to the full path of a sound file of the person's own, chosen with Browse.</summary>
    public Dictionary<string, string> CustomPaths { get; set; } = new();

    /// <summary>Cue id to the chosen built-in sound's file name, e.g. "connect 2.wav". A cue that is not here plays its
    /// first built-in sound.</summary>
    public Dictionary<string, string> Sounds { get; set; } = new();

    /// <summary>A soft click as you type in any edit field (the Audio cues tab's tick box). On by default.</summary>
    public bool KeyboardClicks { get; set; } = true;

    /// <summary>The global sounds, kept on the computer for every profile (Ed, 2026-10-08: "global sounds are: profile save,
    /// profile switch, profile menu open, update, context help open and close. the rest are profile specific").</summary>
    public static readonly IReadOnlySet<string> GlobalCueIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "save", "profile-switch", "profile-menu-open", "update", "help-open", "help-close",
    };

    /// <summary>Whether a cue is one of the global sounds, kept on the computer rather than in the profile.</summary>
    public static bool IsGlobal(string cueId) => GlobalCueIds.Contains(cueId);

    /// <summary>A copy holding only the profile's own cues, and the keyboard clicks: what a profile saves.</summary>
    public CueSettings OnlyProfileCues() => Filter(this, static id => !IsGlobal(id), KeyboardClicks);

    /// <summary>A copy holding only the global sounds: what the computer keeps for every profile.</summary>
    public CueSettings OnlyGlobalCues() => Filter(this, IsGlobal, true);

    /// <summary>One set from a profile's own cues and the computer's global ones: every cue as it plays now.</summary>
    public static CueSettings Combine(CueSettings profileCues, CueSettings globalCues)
    {
        var all = profileCues.OnlyProfileCues();
        var global = globalCues.OnlyGlobalCues();
        foreach (var (id, on) in global.Enabled) all.Enabled[id] = on;
        foreach (var (id, path) in global.CustomPaths) all.CustomPaths[id] = path;
        foreach (var (id, file) in global.Sounds) all.Sounds[id] = file;
        return all;
    }

    private static CueSettings Filter(CueSettings from, Func<string, bool> keep, bool keyboardClicks) => new()
    {
        Enabled = (from.Enabled ?? new()).Where(e => keep(e.Key)).ToDictionary(e => e.Key, e => e.Value),
        CustomPaths = (from.CustomPaths ?? new()).Where(e => keep(e.Key)).ToDictionary(e => e.Key, e => e.Value),
        Sounds = (from.Sounds ?? new()).Where(e => keep(e.Key)).ToDictionary(e => e.Key, e => e.Value),
        KeyboardClicks = keyboardClicks,
    };

    /// <summary>Whether the cue plays: on unless switched off.</summary>
    public bool IsOn(string cueId) => !Enabled.TryGetValue(cueId, out var on) || on;

    /// <summary>The person's own file for the cue, or null when it plays a built-in sound.</summary>
    public string? CustomPathOf(string cueId) =>
        CustomPaths.TryGetValue(cueId, out var path) && !string.IsNullOrWhiteSpace(path) ? path : null;

    /// <summary>A deep copy, so a profile, the window's settings and this computer's copy never share a dictionary.</summary>
    public CueSettings Clone() => new()
    {
        Enabled = new Dictionary<string, bool>(Enabled ?? new()),
        CustomPaths = new Dictionary<string, string>(CustomPaths ?? new()),
        Sounds = new Dictionary<string, string>(Sounds ?? new()),
        KeyboardClicks = KeyboardClicks,
    };

    /// <summary>The cue ids that a RemSound before 2026-10-08 kept on the computer, each with its switch in
    /// <see cref="AppConfig"/>. The startup cue's own file lived in <see cref="AppConfig.StartupCueCustomPath"/>; the others'
    /// in <see cref="AppConfig.MachineCueCustomPaths"/>.</summary>
    public static readonly IReadOnlyList<(string CueId, Func<AppConfig, bool> WasOn)> KeptOnTheComputerBefore =
    [
        ("startup", static c => c.EnableStartupCue),
        ("send-on", static c => c.EnableSendOnCue),
        ("send-off", static c => c.EnableSendOffCue),
        ("receive-on", static c => c.EnableReceiveOnCue),
        ("receive-off", static c => c.EnableReceiveOffCue),
        ("hide", static c => c.EnableHideCue),
        ("show", static c => c.EnableShowCue),
        ("checkbox-on", static c => c.EnableCheckboxOnCue),
        ("checkbox-off", static c => c.EnableCheckboxOffCue),
        ("push-to-talk-on", static c => c.EnablePushToTalkOnCue),
        ("push-to-talk-off", static c => c.EnablePushToTalkOffCue),
        ("server-connect", static c => c.EnableServerConnectCue),
        ("server-disconnect", static c => c.EnableServerDisconnectCue),
        ("tab-switch", static c => c.EnableTabSwitchCue),
        ("help-open", static c => c.EnableHelpOpenCue),
        ("help-close", static c => c.EnableHelpCloseCue),
    ];

    /// <summary>The cue ids a profile already carried before 2026-10-08, each with its switch on <see cref="Profile"/>.
    /// Connect and disconnect fall back to the older combined <see cref="Profile.MuteConnectionCues"/>, as they always
    /// did.</summary>
    public static readonly IReadOnlyList<(string CueId, Func<Profile, bool> WasOn)> KeptInTheProfileBefore =
    [
        ("connect", static p => p.EnableConnectCue ?? !p.MuteConnectionCues),
        ("disconnect", static p => p.EnableDisconnectCue ?? !p.MuteConnectionCues),
        ("record-start", static p => p.EnableRecordStartCue ?? true),
        ("record-stop", static p => p.EnableRecordStopCue ?? true),
        ("save", static p => p.EnableSaveCue ?? true),
        ("profile-switch", static p => p.EnableProfileSwitchCue ?? true),
        ("profile-menu-open", static p => p.EnableProfileMenuOpenCue ?? true),
        ("update", static p => p.EnableUpdateCue ?? true),
    ];

    /// <summary>
    /// The cue settings a RemSound before 2026-10-08 used with <paramref name="profile"/> on this computer: the profile's own
    /// eight cues as it saved them, and everything else - the other cues' switches and files, every cue's chosen built-in
    /// sound, the keyboard clicks - from this computer's settings, which every profile shared. So a profile opened for the
    /// first time after the update sounds exactly as it did before (Ed: "whatever they have will carry across into
    /// profiles"). With no profile, it is what this computer's settings alone gave.
    ///
    /// <para>Those computer-wide settings are no longer written, so this gives the same answer for every older profile,
    /// whichever is opened first and however the others have been changed since.</para>
    /// </summary>
    public static CueSettings FromBefore(Profile? profile, AppConfig cfg)
    {
        var cues = new CueSettings { KeyboardClicks = cfg.EnableKeyboardClicks };
        foreach (var (id, wasOn) in KeptOnTheComputerBefore) cues.Enabled[id] = wasOn(cfg);
        foreach (var (id, wasOn) in KeptInTheProfileBefore) cues.Enabled[id] = profile is null || wasOn(profile);

        // Files of the person's own. The computer's came first in the old lookup only when the profile had none for that
        // cue (MainForm.TryLoadCueSound), so the profile's are laid over them.
        foreach (var (id, path) in cfg.MachineCueCustomPaths ?? new())
            if (!string.IsNullOrWhiteSpace(path)) cues.CustomPaths[id] = path;
        if (!string.IsNullOrWhiteSpace(cfg.StartupCueCustomPath)) cues.CustomPaths["startup"] = cfg.StartupCueCustomPath!;
        foreach (var (id, path) in profile?.CustomCuePaths ?? new())
            if (!string.IsNullOrWhiteSpace(path)) cues.CustomPaths[id] = path;

        foreach (var (id, file) in cfg.DefaultCueSounds ?? new())
            if (!string.IsNullOrWhiteSpace(file)) cues.Sounds[id] = file;
        return cues;
    }
}
