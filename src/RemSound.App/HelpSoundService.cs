using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// The sounds F1 help makes as it opens and as it closes (Ed's own sounds, 2026-09-25: "when you hit f1, it should play
/// those sounds and escape closes and plays the close sounds"). Shipped as numbered variants ("help open 1.wav", ...) and
/// configured in Preferences exactly like the other cues, carried by the profile like them (2026-10-08). Default on. The
/// same shape as <see cref="TabSwitchSoundService"/>.
/// </summary>
internal static class HelpSoundService
{
    private static CuePlayer? openSound;
    private static CuePlayer? closeSound;
    // Cached at Reload() so a play reads one bool, not the settings.
    private static bool enableOpen;
    private static bool enableClose;

    /// <summary>(Re)load both cues from the cues in force (<see cref="ActiveCues"/>), which calls this whenever they
    /// change.</summary>
    public static void Reload()
    {
        enableOpen = ActiveCues.Current.IsOn(MainForm.CueId.HelpOpen);
        enableClose = ActiveCues.Current.IsOn(MainForm.CueId.HelpClose);
        openSound = ActiveCues.PlayerOf(MainForm.CueId.HelpOpen, "help open.wav");
        closeSound = ActiveCues.PlayerOf(MainForm.CueId.HelpClose, "help close.wav");
    }

    public static void PlayOpen()
    {
        if (enableOpen) openSound?.Play();
    }

    public static void PlayClose()
    {
        if (enableClose) closeSound?.Play();
    }

    /// <summary>The sound file one of the two cues plays with the cues in force - a sound of the person's own, or the
    /// chosen built-in one - or null when it is switched off, when "Mute all audio cues" is ticked, or when it has no file.
    /// The DAW plugin is given copies of these, since inside a music program it can't read RemSound's settings, so a null
    /// here takes the plugin's copy away and the plugin is quiet too.</summary>
    public static string? ChosenFile(bool open)
    {
        if (CuePlayer.AllCuesMuted) return null;
        var id = open ? MainForm.CueId.HelpOpen : MainForm.CueId.HelpClose;
        if (!ActiveCues.Current.IsOn(id)) return null;
        try { return ActiveCues.FileOf(id, open ? "help open.wav" : "help close.wav"); }
        catch { return null; }
    }
}
