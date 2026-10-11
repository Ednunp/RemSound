using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// The cue settings in force right now, for the sounds that live outside the main window: the tick box, tab and help sounds,
/// the key clicks and the DAW plugin's help sounds (2026-10-08). The open profile's, put here by the main window as it
/// opens a profile and whenever its cues change; before a profile is chosen - the profile picker, a command-line action -
/// the last profile's (<see cref="AppConfig.CuesBeforeAProfile"/>).
/// </summary>
internal static class ActiveCues
{
    private static CueSettings current = new();

    /// <summary>The settings in force. Read-only by convention: change them through <see cref="Use"/>.</summary>
    public static CueSettings Current => current;

    /// <summary>False in a headless copy until a person takes it over (<see cref="Windowless.HandOver"/>): key clicks never
    /// play in a copy nobody is at.</summary>
    public static bool ClicksAllowed { get; set; } = true;

    /// <summary>Put <paramref name="cues"/> in force, and bring every sound that reads them into step.</summary>
    public static void Use(CueSettings cues)
    {
        current = cues.Clone();
        CheckSoundService.Reload();
        TabSwitchSoundService.Reload();
        HelpSoundService.Reload();
        ApplyKeyClicks();
    }

    /// <summary>Key clicks on or off as the settings, "Mute all audio cues" and <see cref="ClicksAllowed"/> say. Opening the
    /// click player is done only on a change, as switching it on rebuilds the player.</summary>
    public static void ApplyKeyClicks()
    {
        var want = ClicksAllowed && current.KeyboardClicks && !CuePlayer.AllCuesMuted;
        if (KeyClickService.Enabled != want) KeyClickService.Enabled = want;
    }

    /// <summary>The file a cue plays with <paramref name="cues"/> (the settings in force when null): the person's own, if
    /// it is on disk, otherwise its chosen built-in sound. Null when neither is there. Whether the cue is on is not asked.</summary>
    public static string? FileOf(string cueId, string defaultFileName, CueSettings? cues = null)
    {
        cues ??= current;
        var custom = cues.CustomPathOf(cueId);
        if (custom is not null && File.Exists(custom)) return custom;
        var path = CueSounds.ResolveDefaultPath(cueId, defaultFileName, cues);
        return path is not null && File.Exists(path) ? path : null;
    }

    /// <summary>A player for the cue's file with the settings in force, or null when it has none.</summary>
    public static CuePlayer? PlayerOf(string cueId, string defaultFileName)
    {
        try { return FileOf(cueId, defaultFileName) is { } path ? new CuePlayer(path) : null; }
        catch { return null; }
    }
}
