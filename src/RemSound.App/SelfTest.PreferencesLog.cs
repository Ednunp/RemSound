using RemSound.Core;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>PREFERENCES LOG (the live test's logs, 2026-09-29): every OK in Preferences logged every global hotkey as
    /// changed - "RemoteVolumeUpHotkey → RemSound.Core.HotkeyRecord → RemSound.Core.HotkeyRecord" - because a hotkey is held
    /// as an object and compared as one. Now a save that changes nothing logs nothing, and a changed hotkey is logged by its
    /// keys.</summary>
    private static string? PreferencesLogOnlyWhatChanged()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-preferences-log-" + Guid.NewGuid().ToString("N"));
        var restoreSink = UiChangeLog.Sink;
        var lines = new List<string>();
        try
        {
            using (AppConfig.UseThrowawayUserDataDirectory(scratch))
            {
                var cfg = AppConfig.Load();
                cfg.MetronomeHotkey = HotkeyRecord.From(new HotkeyInfo(Keys.F13, true, false, false));
                cfg.RemoteVolumeUpHotkey = HotkeyRecord.From(new HotkeyInfo(Keys.Up, true, true, false));
                cfg.Save();
                UiChangeLog.Sink = (what, value) => { lock (lines) lines.Add($"{what}: {value}"); };

                PreferencesDialog.RecordChangedPreferencesForTest(AppConfig.Load());
                Check(lines.Count == 0, $"a save that changes nothing must log nothing ({string.Join(" | ", lines)})");

                var changed = AppConfig.Load();
                changed.MetronomeHotkey = HotkeyRecord.From(new HotkeyInfo(Keys.F14, true, false, false));
                PreferencesDialog.RecordChangedPreferencesForTest(changed);
                Check(lines.Count == 1 && lines[0] == "preference: MetronomeHotkey: Control+F13 → Control+F14",
                    $"a changed hotkey must be logged once, by its keys ({string.Join(" | ", lines)})");
            }
        }
        finally
        {
            UiChangeLog.Sink = restoreSink;
        }
        return "a save that changed nothing logged nothing; a changed hotkey logged once, by its keys";
    }
}
