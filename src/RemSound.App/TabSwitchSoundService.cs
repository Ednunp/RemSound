using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Plays a short cue whenever the user switches between tabs anywhere in RemSound - the main
/// window's tab strip and every tabbed dialog (Preferences, Recording settings, ...). Fired from
/// <see cref="QuietTabControl"/> on a selection change, gated on the tab control actually containing
/// focus, so a genuine user switch (arrow keys / Ctrl+Tab) clicks but the programmatic selection
/// done while building or restoring a window stays silent.
///
/// Shipped as numbered variants ("tab switch 1.wav", ...) and configured in Preferences exactly like the other cues,
/// carried by the profile like them (2026-10-08). Default on.
/// </summary>
internal static class TabSwitchSoundService
{
    private static CuePlayer? switchSound;
    // Cached at Reload() so Play() (fires on every focused tab change) reads one bool, not the settings. Reload() runs
    // whenever the cues in force change (ActiveCues.Use).
    private static bool enableTabSwitch;

    /// <summary>(Re)load the cue from the cues in force (<see cref="ActiveCues"/>), which calls this whenever they
    /// change.</summary>
    public static void Reload()
    {
        enableTabSwitch = ActiveCues.Current.IsOn(MainForm.CueId.TabSwitch);
        switchSound = ActiveCues.PlayerOf(MainForm.CueId.TabSwitch, "tab switch.wav");
    }

    public static void Play()
    {
        if (enableTabSwitch) switchSound?.Play();
    }
}
