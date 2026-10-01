using System.Net;
using RemSound.Core;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>METRONOME TEMPO KEYS (Ed, 2026-09-29, after the live test: "global hotkeys for metronome tempo plus and
    /// minus"). Two keys, unset unless chosen, kept on this computer and listed in Keyboard shortcuts; each press moves the
    /// tempo a beat a minute through the window's own wiring, kept in the profile and logged; held at 30 and 300; the control
    /// channel's tempo up and down do the same; and following a master, the tempo is theirs and the keys change nothing.</summary>
    private static string? MetronomeTempoKeys()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-metronome-tempo-keys-" + Guid.NewGuid().ToString("N"));
        using (AppConfig.UseThrowawayUserDataDirectory(scratch))
        {
            var keys = new RemSoundSettingsStore("RemSound");
            Check(keys.LoadMetronomeTempoUpHotkey().IsUnset && keys.LoadMetronomeTempoDownHotkey().IsUnset, "both tempo keys are unset unless chosen");
            keys.SaveMetronomeTempoUpHotkey(new HotkeyInfo(Keys.F14, true, false, false));
            keys.SaveMetronomeTempoDownHotkey(new HotkeyInfo(Keys.F15, true, false, false));
            var reread = new RemSoundSettingsStore("RemSound");
            Check(reread.LoadMetronomeTempoUpHotkey() == new HotkeyInfo(Keys.F14, true, false, false)
                  && reread.LoadMetronomeTempoDownHotkey() == new HotkeyInfo(Keys.F15, true, false, false), "keys chosen are kept on this computer, each its own");
            var controller = new MainFormHotkeyController(keys, () => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { },
                () => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { });
            using var shortcuts = controller.BuildKeyboardShortcutsDialogForAudit();
            var rows = shortcuts.Controls.Cast<Control>().SelectMany(c => c.Controls.Cast<Control>().Prepend(c)).OfType<ListBox>()
                .SelectMany(l => l.Items.Cast<object>().Select(i => i.ToString() ?? "")).ToList();
            Check(rows.Any(r => r.StartsWith("Metronome tempo up", StringComparison.Ordinal) && r.EndsWith("Control+F14", StringComparison.Ordinal))
                  && rows.Any(r => r.StartsWith("Metronome tempo down", StringComparison.Ordinal) && r.EndsWith("Control+F15", StringComparison.Ordinal)),
                $"Keyboard shortcuts must list both tempo keys ({string.Join(" | ", rows.TakeLast(3))})");
        }

        var logged = new List<string>();
        MainForm? form = null;
        try
        {
            try { form = MetronomeWindow(logged); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            var main = form;
            var hotkeys = Require(FieldOf<MainFormHotkeyController>(main, "hotkeyController"), "the window's hotkey controller");
            var up = Require(hotkeys.MetronomeTempoUp, "the tempo up key's action must be wired");
            var down = Require(hotkeys.MetronomeTempoDown, "the tempo down key's action must be wired");
            int Bpm() => main.SettingsForTest.LoadMetronomeSettings().Bpm;

            var start = Bpm();
            up();
            Check(Bpm() == start + 1 && main.MetronomeForTest.Settings.Bpm == start + 1, $"UP: one press, one beat a minute faster, in the profile and the metronome ({start} to {Bpm()})");
            Check(main.UnsavedChangesForTest, "and the profile marked unsaved, as the window's OK marks it");
            Check(Logged(logged, "metronome tempo (the hotkey)", $"{start + 1} bpm"), "and logged");
            down();
            down();
            Check(Bpm() == start - 1, $"DOWN: two presses, two beats a minute slower ({Bpm()})");

            main.ChangeMetronomeSettingsForTest(s => s.Bpm = MetronomeTimeline.MaxBpm);
            up();
            Check(Bpm() == MetronomeTimeline.MaxBpm, $"held at {MetronomeTimeline.MaxBpm} ({Bpm()})");
            main.ChangeMetronomeSettingsForTest(s => s.Bpm = MetronomeTimeline.MinBpm);
            down();
            Check(Bpm() == MetronomeTimeline.MinBpm, $"held at {MetronomeTimeline.MinBpm} ({Bpm()})");

            main.ChangeMetronomeSettingsForTest(s => s.Bpm = 100);
            var control = new RemoteControlEngine(() => main);
            var saidUp = control.Execute("metronome tempo up");
            var saidDown = control.Execute("metronome tempo down");
            control.Execute("metronome tempo up");
            Check(saidUp.StartsWith("ok", StringComparison.Ordinal) && saidDown.StartsWith("ok", StringComparison.Ordinal) && Bpm() == 101,
                $"THE CONTROL CHANNEL: tempo up and down do what the keys do ({saidUp} / {saidDown}, {Bpm()})");
            Check(Logged(logged, "metronome tempo (the control channel)"), "and are logged as the control channel's");

            // Following a master: the tempo is theirs.
            main.MetronomeForTest.AnswerOffer(new MetronomeOffer(0x7e57, new IPEndPoint(IPAddress.Parse("198.51.100.80"), 47999), "Andre"), accept: true);
            Check(main.MetronomeForTest.Role == MetronomeRole.Follower, "the window must be following for this part");
            up();
            Check(Bpm() == 101, $"FOLLOWING: the key must change nothing - the tempo is the master's ({Bpm()})");
            Check(Logged(logged, "tempo up from the hotkey not taken", "Andre's"), "and the log must say why");
            main.MetronomeForTest.StopFollowing("test", sendLeave: false);
            main.LogForTest.EventTapForTest = null;
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
        }
        return "both keys unset unless chosen, kept and listed; a press moves the tempo a beat a minute into the profile and the log, held at "
             + "30 and 300; the control channel's up and down the same; a follower's keys change nothing, and the log says why";
    }
}
