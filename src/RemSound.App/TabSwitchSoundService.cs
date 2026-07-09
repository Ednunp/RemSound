using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Plays a short cue whenever the user switches between tabs anywhere in RemSound - the main
/// window's tab strip and every tabbed dialog (Preferences, Recording settings, ...). Fired from
/// <see cref="QuietTabControl"/> on a selection change, gated on the tab control actually containing
/// focus, so a genuine user switch (arrow keys / Ctrl+Tab) clicks but the programmatic selection
/// done while building or restoring a window stays silent.
///
/// Machine-wide cue (<see cref="AppConfig.EnableTabSwitchCue"/>), shipped as numbered variants
/// ("tab switch 1.wav", ...) and configured in Preferences exactly like the other cues. Default on.
/// </summary>
internal static class TabSwitchSoundService
{
    private static CuePlayer? switchSound;
    // Cached at Reload() so Play() (fires on every focused tab change) doesn't re-read + re-deserialize
    // the whole config file just to fetch one bool. Reload() runs whenever cue settings change.
    private static bool enableTabSwitch;

    /// <summary>When true, <see cref="Play"/> is a no-op. Reserved for bulk programmatic tab changes
    /// the focus gate doesn't already cover; mirrors <see cref="CheckSoundService.Suppressed"/>.</summary>
    public static bool Suppressed { get; set; }

    /// <summary>(Re)load the cue from the current cue configuration. Call at startup and whenever cue
    /// settings change, alongside <see cref="CheckSoundService.Reload"/>.</summary>
    public static void Reload()
    {
        var cfg = AppConfig.Load();
        enableTabSwitch = cfg.EnableTabSwitchCue;
        switchSound = LoadCue(MainForm.CueId.TabSwitch, "tab switch.wav", cfg);
    }

    public static void Play()
    {
        if (Suppressed) return;
        if (enableTabSwitch) switchSound?.Play();
    }

    private static CuePlayer? LoadCue(string cueId, string defaultFile, AppConfig cfg)
    {
        try
        {
            string? path = cfg.MachineCueCustomPaths.TryGetValue(cueId, out var custom) && File.Exists(custom)
                ? custom
                : CueSounds.ResolveDefaultPath(cueId, defaultFile, cfg);
            return path is not null && File.Exists(path) ? new CuePlayer(path) : null;
        }
        catch { return null; }
    }
}
