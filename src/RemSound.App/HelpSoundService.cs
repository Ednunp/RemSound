using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// The sounds F1 help makes as it opens and as it closes (Ed's own sounds, 2026-09-25: "when you hit f1, it should play
/// those sounds and escape closes and plays the close sounds"). Machine-wide cues (<see cref="AppConfig.EnableHelpOpenCue"/>,
/// <see cref="AppConfig.EnableHelpCloseCue"/>), shipped as numbered variants ("help open 1.wav", ...) and configured in
/// Preferences exactly like the other cues. Default on. The same shape as <see cref="TabSwitchSoundService"/>.
/// </summary>
internal static class HelpSoundService
{
    private static CuePlayer? openSound;
    private static CuePlayer? closeSound;
    // Cached at Reload() so a play doesn't re-read the config file for one bool.
    private static bool enableOpen;
    private static bool enableClose;

    /// <summary>(Re)load both cues from the current cue configuration. Call at startup and whenever cue settings change,
    /// alongside <see cref="TabSwitchSoundService.Reload"/>.</summary>
    public static void Reload()
    {
        var cfg = AppConfig.Load();
        enableOpen = cfg.EnableHelpOpenCue;
        enableClose = cfg.EnableHelpCloseCue;
        openSound = LoadCue(MainForm.CueId.HelpOpen, "help open.wav", cfg);
        closeSound = LoadCue(MainForm.CueId.HelpClose, "help close.wav", cfg);
    }

    public static void PlayOpen()
    {
        if (enableOpen) openSound?.Play();
    }

    public static void PlayClose()
    {
        if (enableClose) closeSound?.Play();
    }

    private static CuePlayer? LoadCue(string cueId, string defaultFile, AppConfig cfg)
    {
        try { return PathOf(cueId, defaultFile, cfg) is { } path ? new CuePlayer(path) : null; }
        catch { return null; }
    }

    /// <summary>The sound file one of the two cues plays as Preferences has it - a sound of the person's own, or the chosen
    /// default - or null when it is switched off or has no file. The DAW plugin is given copies of these, since inside a
    /// music program it can't read RemSound's settings.</summary>
    public static string? ChosenFile(bool open, AppConfig cfg)
    {
        if (!(open ? cfg.EnableHelpOpenCue : cfg.EnableHelpCloseCue)) return null;
        try { return open ? PathOf(MainForm.CueId.HelpOpen, "help open.wav", cfg) : PathOf(MainForm.CueId.HelpClose, "help close.wav", cfg); }
        catch { return null; }
    }

    private static string? PathOf(string cueId, string defaultFile, AppConfig cfg)
    {
        string? path = cfg.MachineCueCustomPaths.TryGetValue(cueId, out var custom) && File.Exists(custom)
            ? custom
            : CueSounds.ResolveDefaultPath(cueId, defaultFile, cfg);
        return path is not null && File.Exists(path) ? path : null;
    }
}
