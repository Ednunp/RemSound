using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// The audio cues: the profile's own and this computer's global sounds (2026-10-08, Ed: "one list of profile specific
/// sounds and one of global sounds", and "turn all audio cues off"). The window's own players read them through the
/// settings store, which knows which is which; this keeps the
/// sounds that live outside it (<see cref="ActiveCues"/>) and this computer's copy for before a profile opens
/// (<see cref="AppConfig.LastProfileCues"/>) in step, and plays the startup cue as the first profile opens.
/// </summary>
public sealed partial class MainForm
{
    /// <summary>Whether this run has played its startup cue. Once per run, as its first profile opens: a profile switch
    /// rebuilds the window but is not a start.</summary>
    private static bool startupCuePlayed;

    /// <summary>Put this profile's cues in force for the tick box, tab, help and key-click sounds and the plugin's help
    /// sounds, and keep this computer's copy in step, so the next run's profile picker - and a new profile - sound like the
    /// profile last used, as the logs follow it (MainForm.Logs).</summary>
    private void ApplyCuesInForce(CueSettings? cues = null)
    {
        cues ??= settings.CueSnapshot();
        ActiveCues.Use(cues);
        KeepComputerCuesInStep(cues);
    }

    private static void KeepComputerCuesInStep(CueSettings cues)
    {
        try
        {
            // The profile's own cues only: the global sounds are this computer's already.
            cues = cues.OnlyProfileCues();
            var cfg = AppConfig.Load();
            if (cfg.LastProfileCues is { } kept
                && System.Text.Json.JsonSerializer.Serialize(kept) == System.Text.Json.JsonSerializer.Serialize(cues)) return;
            cfg.LastProfileCues = cues.Clone();
            cfg.Save();
        }
        catch { /* harmless - the next profile to open sets it again */ }
    }

    /// <summary>The startup cue, as this run's first profile opens, if that profile has it on. Ed: "in reality you pick a
    /// profile and it plays the startup sound."</summary>
    private void PlayStartupCueOnce()
    {
        if (startupCuePlayed) return;
        startupCuePlayed = true;
        try
        {
            var cues = settings.CueSnapshot();
            var on = cues.IsOn(CueId.Startup);
            var path = on ? ActiveCues.FileOf(CueId.Startup, "start up.wav", cues) : null;
            var sound = path is null ? null : new CuePlayer(path);
            logFile.Event($"startup cue: {CueOutcome(on, sound)}");
            if (on) sound?.Play();
        }
        catch { /* a startup cue must never disturb startup */ }
    }

    /// <summary>The cues a new profile made from this window starts with: the ones in use here, unsaved changes and all.</summary>
    internal CueSettings CuesForANewProfile() => settings.CueSnapshot();

    /// <summary>"Mute all audio cues" changed in Preferences: in force at once, for every cue and the key clicks.</summary>
    internal static void ApplyMuteAllCues(bool muted)
    {
        CuePlayer.AllCuesMuted = muted;
        ActiveCues.ApplyKeyClicks();
    }

    /// <summary>Gate seams.</summary>
    internal static bool StartupCuePlayedForTest { get => startupCuePlayed; set => startupCuePlayed = value; }
    internal void PlayStartupCueOnceForTest() => PlayStartupCueOnce();
}
