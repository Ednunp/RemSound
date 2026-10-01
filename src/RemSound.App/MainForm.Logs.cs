using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Logs per profile (Ed, 2026-09-29: "some profiles have it others do not. because a test with andre should always have it
/// where as using with my laptop probably not"). The profile's choice is applied as its window opens and changed by the
/// Preferences tick box; a profile saved before it existed follows this computer's setting. This computer's setting
/// follows the profile last chosen, so what is logged before a profile loads - the start of the next run, a command-line
/// action - and the DAW plugin's own log go with the profile you last used.
/// </summary>
public sealed partial class MainForm
{
    /// <summary>The Preferences tick box: kept in the profile (unsaved until you save), in effect at once, and followed by this
    /// computer's setting and the plugin's pointer.</summary>
    private void ApplyLoggingFromPreferences(bool enabled)
    {
        settings.SaveEnableLogs(enabled);
        MarkProfileDirty();
        KeepComputerLoggingInStep(enabled);
        // Flip the gate live so the tick takes effect at once - writes simply stop or resume mid-flight.
        logFile.Enabled = enabled;
        try { AppConfig.WritePluginPointer(enabled); } catch { /* the plugin reads it when it next starts */ }
    }

    /// <summary>This computer's setting, set to the profile's when they differ.</summary>
    private static void KeepComputerLoggingInStep(bool on)
    {
        var cfg = AppConfig.Load();
        if (cfg.LoggingEnabled == on) return;
        cfg.LoggingEnabled = on;
        try { cfg.Save(); } catch { /* harmless - the next profile to open sets it again */ }
    }

    internal void ApplyLoggingFromPreferencesForTest(bool enabled) => ApplyLoggingFromPreferences(enabled);
}
