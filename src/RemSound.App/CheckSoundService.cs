using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Plays a short tick / untick sound whenever the user checks or unchecks a checkbox anywhere in
/// RemSound - instant audible feedback of which way a box just went, which matters most in the busy
/// inputs/outputs lists. Two normal cues: CheckboxOn (check.wav) and CheckboxOff (uncheck.wav), each with numbered
/// variants + an off option in Preferences, like any other cue, and carried by the profile like them (2026-10-08).
///
/// Wired from <see cref="AccessibleCheckBox"/> and the device <c>LiveCheckedListBox</c>, both gated
/// on the control being focused - so a genuine user toggle clicks, but the bulk programmatic
/// checking done on profile load or by "uncheck all" stays silent.
/// </summary>
internal static class CheckSoundService
{
    private static CuePlayer? checkSound;
    private static CuePlayer? uncheckSound;
    // Cached at Reload() so Play() (which fires on the UI thread for every genuine checkbox toggle) reads two bools, not
    // the settings. Reload() runs whenever the cues in force change (ActiveCues.Use), so these stay current.
    private static bool enableCheckboxOn;
    private static bool enableCheckboxOff;

    /// <summary>When true, <see cref="Play"/> is a no-op. MainForm sets this around bulk programmatic
    /// control updates (profile load, "uncheck all", device-list refresh). The per-call Focused gate
    /// already silences MOST programmatic ticks, but it leaks when the box we tick in code happens to
    /// be the focused control on launch — which is exactly what made loading a profile blast a
    /// checkbox click. This flag closes that gap: only genuine user toggles ever click.</summary>
    public static bool Suppressed { get; set; }

    /// <summary>(Re)load the tick/untick sounds from the cues in force (<see cref="ActiveCues"/>), which calls this
    /// whenever they change.</summary>
    public static void Reload()
    {
        var cues = ActiveCues.Current;
        enableCheckboxOn = cues.IsOn(MainForm.CueId.CheckboxOn);
        enableCheckboxOff = cues.IsOn(MainForm.CueId.CheckboxOff);
        checkSound = ActiveCues.PlayerOf(MainForm.CueId.CheckboxOn, "check.wav");
        uncheckSound = ActiveCues.PlayerOf(MainForm.CueId.CheckboxOff, "uncheck.wav");
    }

    public static void Play(bool isChecked)
    {
        if (Suppressed) return;
        if (isChecked) { if (enableCheckboxOn) checkSound?.Play(); }
        else { if (enableCheckboxOff) uncheckSound?.Play(); }
    }
}
