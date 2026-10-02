using System.Net;
using System.Reflection;
using RemSound.Core;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>METRONOME VOLUME KEYS AND SHORTCUTS (Ed, 2026-10-01: "there should be a volume control in metronome and that
    /// should have global hotkeys. there should be a little mini hotkeys in the metronome settings for just the metronome
    /// hotkeys", then "put a note in hotkeys for metronome just like the push to talk ones that they can also be defined in
    /// global hotkeys settings"; and "the unmute myself button should have a global hot key"). The two volume keys and the
    /// unmute-myself key: unset unless chosen, kept on this computer, listed in Keyboard shortcuts. A volume press moves the
    /// click 5 points through the window's own wiring, into the profile and the log, held at 0 and 100, and still the
    /// follower's own; the control channel's the same. The Metronome settings window lists the five metronome keys under a
    /// label saying they are also in Keyboard shortcuts: Enter changes one, Del clears one, OK keeps them on this computer and
    /// in Keyboard shortcuts, Cancel keeps nothing.</summary>
    private static string? MetronomeVolumeKeysAndShortcuts()
    {
        static List<string> ShortcutRows(MainFormHotkeyController controller)
        {
            using var shortcuts = controller.BuildKeyboardShortcutsDialogForAudit();
            return shortcuts.Controls.Cast<Control>().SelectMany(c => c.Controls.Cast<Control>().Prepend(c)).OfType<ListBox>()
                .SelectMany(l => l.Items.Cast<object>().Select(i => i.ToString() ?? "")).ToList();
        }
        var volumeUpKey = new HotkeyInfo(Keys.F16, true, false, false);
        var volumeDownKey = new HotkeyInfo(Keys.F17, true, false, false);
        var hearMyselfKey = new HotkeyInfo(Keys.F18, true, false, false);
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-metronome-volume-keys-" + Guid.NewGuid().ToString("N"));
        using (AppConfig.UseThrowawayUserDataDirectory(scratch))
        {
            var keys = new RemSoundSettingsStore("RemSound");
            Check(keys.LoadMetronomeVolumeUpHotkey().IsUnset && keys.LoadMetronomeVolumeDownHotkey().IsUnset && keys.LoadHearMyselfHotkey().IsUnset,
                "the volume keys and the unmute-myself key are unset unless chosen");
            keys.SaveMetronomeVolumeUpHotkey(volumeUpKey);
            keys.SaveMetronomeVolumeDownHotkey(volumeDownKey);
            keys.SaveHearMyselfHotkey(hearMyselfKey);
            var reread = new RemSoundSettingsStore("RemSound");
            Check(reread.LoadMetronomeVolumeUpHotkey() == volumeUpKey && reread.LoadMetronomeVolumeDownHotkey() == volumeDownKey
                  && reread.LoadHearMyselfHotkey() == hearMyselfKey, "keys chosen are kept on this computer, each its own");
            var controller = new MainFormHotkeyController(keys, () => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { },
                () => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { });
            var rows = ShortcutRows(controller);
            Check(rows.Contains("Metronome volume up: Control+F16") && rows.Contains("Metronome volume down: Control+F17")
                  && rows.Contains("Unmute or mute myself to test round trip on server: Control+F18"),
                $"Keyboard shortcuts must list all three ({string.Join(" | ", rows.TakeLast(4))})");
            Check(controller.MetronomeKeys.Count == 5 && controller.MetronomeKeys[3] == volumeUpKey && controller.MetronomeKeys[4] == volumeDownKey,
                "and the volume keys are the metronome's fourth and fifth, as its settings window lists them");
        }

        var logged = new List<string>();
        MainForm? form = null;
        try
        {
            try { form = MetronomeWindow(logged); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            var main = form;
            var hotkeys = Require(FieldOf<MainFormHotkeyController>(main, "hotkeyController"), "the window's hotkey controller");
            var up = Require(hotkeys.MetronomeVolumeUp, "the volume up key's action must be wired");
            var down = Require(hotkeys.MetronomeVolumeDown, "the volume down key's action must be wired");
            Require(hotkeys.ToggleHearMyself, "the unmute-myself key's action must be wired");
            int Volume() => main.SettingsForTest.LoadMetronomeSettings().Volume;

            main.ChangeMetronomeSettingsForTest(s => s.Volume = 70);
            up();
            Check(Volume() == 75 && main.MetronomeForTest.Settings.Volume == 75, $"UP: one press, 5 points louder, in the profile and the metronome ({Volume()})");
            Check(main.UnsavedChangesForTest, "and the profile marked unsaved, as the window's OK marks it");
            Check(Logged(logged, "metronome volume (the hotkey)", "volume 75 %"), "and logged");
            down();
            down();
            Check(Volume() == 65, $"DOWN: two presses, 10 points quieter ({Volume()})");
            main.ChangeMetronomeSettingsForTest(s => s.Volume = 100);
            up();
            Check(Volume() == 100, $"held at 100 ({Volume()})");
            main.ChangeMetronomeSettingsForTest(s => s.Volume = 0);
            down();
            Check(Volume() == 0, $"held at 0 ({Volume()})");

            main.ChangeMetronomeSettingsForTest(s => s.Volume = 50);
            var control = new RemoteControlEngine(() => main);
            var saidUp = control.Execute("metronome volume up");
            Check(saidUp.StartsWith("ok", StringComparison.Ordinal) && Volume() == 55, $"THE CONTROL CHANNEL: volume up does what the key does ({saidUp}, {Volume()})");
            Check(Logged(logged, "metronome volume (the control channel)"), "and is logged as the control channel's");
            Check(control.Execute("metronome volume 40").StartsWith("ok", StringComparison.Ordinal) && Volume() == 40, "and a volume can be set outright");
            Check(control.Execute("metronome volume 101").StartsWith("error", StringComparison.Ordinal) && Volume() == 40, "but not past 100");

            // Following a master: the volume is still your own.
            main.MetronomeForTest.AnswerOffer(new MetronomeOffer(0x7e58, new IPEndPoint(IPAddress.Parse("198.51.100.81"), 47999), "Andre"), accept: true);
            Check(main.MetronomeForTest.Role == MetronomeRole.Follower, "the window must be following for this part");
            up();
            Check(Volume() == 45, $"FOLLOWING: your own clicks' volume stays yours to change ({Volume()})");
            main.MetronomeForTest.StopFollowing("test", sendLeave: false);

            // The settings window's own list of the metronome's keys.
            var before = hotkeys.MetronomeKeys.ToArray();
            var newVolumeUp = new HotkeyInfo(Keys.F19, true, true, false);
            var asked = 0;
            using (var dialog = new MetronomeSettingsDialog(main.SettingsForTest.LoadMetronomeSettings(), null, [],
                       shortcutKeys: before, chooseKey: _ => { asked++; return newVolumeUp; }))
            {
                var list = dialog.KeysListForTest;
                var items = list.Items.Cast<object>().Select(i => i.ToString()).ToList();
                Check(items.SequenceEqual(MetronomeSettingsDialog.KeyNames.Select((n, i) => $"{n}: {before[i]}")),
                    $"THE WINDOW: it must list the metronome's five keys, each with its key ({string.Join(" | ", items)})");
                var labels = AllControls(dialog).OfType<Label>().Select(l => l.Text.Replace("&", "")).ToList();
                Check(labels.Contains("Metronome shortcut keys - also in Keyboard shortcuts (Alt+K):"),
                    $"and its label must say the same keys are in Keyboard shortcuts, as push to talk's key does ({string.Join(" | ", labels)})");
                Check(list.AccessibleName == "Metronome shortcut keys, also in Keyboard shortcuts", $"and the screen reader must hear it too ({list.AccessibleName})");
                list.SelectedIndex = 3;
                dialog.ChangeSelectedKeyForTest();
                Check(asked == 1 && dialog.ShortcutKeys[3] == newVolumeUp && list.Items[3]?.ToString() == $"Volume up: {newVolumeUp}" && list.SelectedIndex == 3,
                    $"ENTER: the key chosen must replace the row's, which stays picked ({list.Items[3]})");
                list.SelectedIndex = 1;
                typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(list, [new KeyEventArgs(Keys.Delete)]);
                Check(dialog.ShortcutKeys[1].IsUnset && list.Items[1]?.ToString() == "Tempo up a beat a minute: (not set)",
                    $"DEL: the row picked must be cleared ({list.Items[1]})");
                Check(hotkeys.MetronomeKeys.SequenceEqual(before), "nothing is kept before OK");
                typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog.OkButtonForTest, [EventArgs.Empty]);
                var expectedChanged = before[1].IsUnset ? new[] { 3 } : [1, 3];
                Check(dialog.ChangedKeys.SequenceEqual(expectedChanged), $"OK: exactly the changed keys are reported ({string.Join(",", dialog.ChangedKeys)})");
                main.KeepMetronomeKeysForTest(dialog);
            }
            Check(hotkeys.MetronomeKeys[3] == newVolumeUp && hotkeys.MetronomeVolumeUpHotkey == newVolumeUp && hotkeys.MetronomeKeys[1].IsUnset,
                "and they are the hotkeys now");
            Check(main.SettingsForTest.LoadMetronomeVolumeUpHotkey() == newVolumeUp && main.SettingsForTest.LoadMetronomeTempoUpHotkey().IsUnset,
                "kept on this computer");
            Check(ShortcutRows(hotkeys).Contains($"Metronome volume up: {newVolumeUp}"), "and Keyboard shortcuts shows the same key");
            Check(Logged(logged, "ui: metronome shortcut: Volume up", newVolumeUp.ToString()), "and the log says so");

            var kept = hotkeys.MetronomeKeys.ToArray();
            using (var cancelled = new MetronomeSettingsDialog(main.SettingsForTest.LoadMetronomeSettings(), null, [],
                       shortcutKeys: kept, chooseKey: _ => new HotkeyInfo(Keys.F20, true, true, false)))
            {
                cancelled.KeysListForTest.SelectedIndex = 4;
                cancelled.ChangeSelectedKeyForTest();
                Check(cancelled.ChangedKeys.Count == 0, "CANCEL: without OK, no key is reported changed");
            }
            Check(hotkeys.MetronomeKeys.SequenceEqual(kept), "and none is kept");
            main.LogForTest.EventTapForTest = null;
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
        }
        return "the volume keys and the unmute-myself key unset unless chosen, kept and listed; a volume press moves the click 5 "
             + "points into the profile and the log, held at 0 and 100, a follower's own; the control channel the same; the "
             + "settings window lists the five metronome keys, says they are also in Keyboard shortcuts, Enter changes, Del "
             + "clears, OK keeps them as hotkeys, Cancel keeps nothing";
    }
}
