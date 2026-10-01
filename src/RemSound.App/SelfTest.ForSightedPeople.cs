using System.Windows.Forms;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// THE SECOND ROUND OF THE UI POLISH FOR SIGHTED PEOPLE (Ed agreed each, 2026-09-28 night): read-only notices that open
/// with nothing highlighted, the sentences NVDA alone needs taken off the screen, the colons Windows programs put after a
/// label, the EQ mode list readable while greyed out, and the main window's size and place remembered when asked. OK
/// before Cancel is LAYOUT K's, and the greyed-out list's colours are LAYOUT I's.
/// </summary>
internal static partial class SelfTest
{
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr PolishSendMessage(IntPtr hWnd, int msg, out int wParam, out int lParam);

    private const int PolishEmGetSel = 0x00B0;

    /// <summary>
    /// NOTICES OPEN WITH NOTHING SELECTED (the UI audit's finding 14). About, the server notice, the plugin updates notice,
    /// context help and "press a key" each put the keyboard in a read-only box, and Windows highlighted every word of it -
    /// a solid block of blue to a sighted person. Ed: "someone can read it with nvda no problem anyway". Each window is
    /// shown for real (on the gate's hidden desktop) so the box really takes the keyboard, which is when Windows would
    /// select it all; then the selection is read from the box itself.
    /// </summary>
    private static string? NoticesOpenWithNothingSelected()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        var cases = new (string Window, Func<Form> Make, Func<Form, TextBox> Box, string Name)[]
        {
            ("About", () => new AboutDialog(), f => AllControls(f).OfType<TextBox>().Single(), "Release notes (tab into and arrow to read)"),
            ("Connecting to a server", () => RelayNoticeDialog.Build(), f => AllControls(f).OfType<TextBox>().Single(), "Server notice"),
            ("Plugin updates", () => PluginFolderNoticeDialog.Build(), f => ((PluginFolderNoticeDialog)f).NoticeBox, "Plugin updates notice"),
            ("Context help", () => ContextHelp.Build("Audio jitter buffer in milliseconds", "audio-profile.jitter-buffer"), f => ((ContextHelpWindow)f).HelpText, "Help"),
            ("Press a key", () => MainFormHotkeyController.NewHotkeyCaptureForm(), f => ((HotkeyCaptureForm)f).BoxForTest, "Current hotkey"),
        };
        var seen = new List<string>();
        try
        {
            foreach (var (window, make, boxOf, name) in cases)
            {
                using var form = make();
                form.StartPosition = FormStartPosition.Manual;
                form.Show();
                for (var i = 0; i < 5; i++) Application.DoEvents();
                var box = boxOf(form);
                // Where the keyboard lands as the window opens, unhelped: the same box, with the same name, as before.
                Check(form.ActiveControl == box, $"{window}: the keyboard must land on the words as the window opens (it is on {form.ActiveControl?.AccessibleName ?? form.ActiveControl?.GetType().Name ?? "nothing"})");
                Check(box.AccessibleName == name, $"{window}: the words must keep the name a screen reader reads, \"{name}\" (it is \"{box.AccessibleName}\")");
                // The premise, made certain: the box has really taken the keyboard, which is when Windows selects everything.
                if (!box.Focused) { box.Focus(); for (var i = 0; i < 5; i++) Application.DoEvents(); }
                Check(box.Focused, $"{window}: premise: the words must have the keyboard, the moment Windows would highlight them");
                PolishSendMessage(box.Handle, PolishEmGetSel, out var start, out var end);
                Check(box.TextLength > 0, $"{window}: premise: the box must hold its words");
                Check(start == 0 && end == 0,
                    $"{window}: THE HIGHLIGHT: the words must open with nothing selected and the cursor at the start (selected {start} to {end} of {box.TextLength})");
                seen.Add(window);
                form.Close();
            }
        }
        finally { CuePlayer.GloballyMuted = restoreMuted; }
        return $"{string.Join(", ", seen)}: each opens on the same box with the same name, the cursor at the start and nothing selected";
    }

    /// <summary>
    /// REMEMBER THE MAIN WINDOW'S SIZE AND POSITION (Preferences, Appearance; Ed, 2026-09-28). Off by default, and off
    /// the window opens exactly as it always has. Ticked - through the control channel, as a person or a script would -
    /// the window keeps its size and place as it closes and goes back to them when the main window is made again, as
    /// after a restart or a profile switch; a place on a screen that has gone is pulled wholly onto one there is; and
    /// the window is never smaller than its minimum. Each window here is the real main window, made, loaded and closed
    /// the way the app does it; only the screen is the gate's stand-in (1366 x 728), so the answer is the same on any PC.
    /// </summary>
    private static string? MainWindowRemembersItsPlaceWhenAsked()
    {
        const string Box = "Remember the main window's size and position";
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        Windowless.InstallHook();
        var restoreArea = Dpi.WorkingAreaForTest;
        var screen = new Rectangle(0, 0, 1366, 728);
        Dpi.WorkingAreaForTest = screen;
        var restoreSeam = MainForm.PlacementAppliesForTest;
        var steps = new List<string>();
        try
        {
            var fresh = new AppConfig();
            Check(!fresh.RememberMainWindowPlacement && fresh.MainWindowPlacement is null, "remembering the main window must be off by default, with nothing kept");
            var start = AppConfig.Load();
            start.RememberMainWindowPlacement = false;
            start.MainWindowPlacement = null;
            start.Save();

            // The real main window, made and loaded the way showing it does, then moved and closed the way a person does.
            // Never shown, so nothing is seen; and with no headless hiding at work, which would pull every move it made
            // back off the screen. What each window logs is read from its own log as it writes.
            var windowLog = new List<string>();
            (Rectangle Opened, FormWindowState State, Size Minimum) OpenThenClose(Rectangle? moveTo)
            {
                var form = new MainForm(null, Profile.NewBlank(), null, null, headless: true);
                try
                {
                    Require(FieldOf<RemSoundLog>(form, "logFile"), "the main window's logFile was not found").EventTapForTest = l => { lock (windowLog) windowLog.Add(l); };
                    LayoutPrepare(form);
                    var opened = (form.Bounds, form.WindowState, form.MinimumSize);
                    if (moveTo is { } r) form.Bounds = r;
                    form.Close();
                    return opened;
                }
                finally { if (!form.IsDisposed) form.Dispose(); }
            }
            bool Logged(long mark, string words) => HeadlessRecords.Log.Since(mark).Any(l => l.Contains(words, StringComparison.Ordinal));
            int Mark() { lock (windowLog) return windowLog.Count; }
            bool Said(int from, string words) { lock (windowLog) return windowLog.Skip(from).Any(l => l.Contains(words, StringComparison.Ordinal)); }

            // Today's window, decided by the code that ran before this setting existed: the setting kept out of it.
            MainForm.PlacementAppliesForTest = false;
            var today = OpenThenClose(null).Opened;
            MainForm.PlacementAppliesForTest = true;

            // 1. OFF: nothing kept, and the window opens exactly as it always has.
            var off = OpenThenClose(new Rectangle(40, 30, 900, 650));
            Check(off.Opened == today, $"OFF: the window must open exactly as it always has ({off.Opened}, not {today})");
            Check(AppConfig.Load().MainWindowPlacement is null, "OFF: nothing may be kept when the window closes, so the next opens as always too");
            // Off means off, whatever the settings file holds (a hand-edited file, or one from a copy that remembered).
            var stale = AppConfig.Load();
            stale.MainWindowPlacement = new WindowPlacement(200, 50, 800, 600, true);
            stale.Save();
            var offWithFile = OpenThenClose(null);
            Check(offWithFile.Opened == today && offWithFile.State == FormWindowState.Normal,
                $"OFF: a size and place in the settings file must not be used while remembering is off ({offWithFile.Opened}, {offWithFile.State})");
            stale = AppConfig.Load();
            stale.MainWindowPlacement = null;
            stale.Save();
            steps.Add($"off: opens at today's {today.Width} x {today.Height} and keeps nothing");

            // Ticked or unticked the way a person does it: Options, Preferences, Appearance, through the control channel.
            void ThroughPreferences(bool on)
            {
                var pipe = "RemSound.control.selftest.place." + Guid.NewGuid().ToString("N")[..8];
                // A headless copy, as the control channel drives one: its log lines are kept where "log" reads them.
                Windowless.SetActiveForTest(true);
                var mark = HeadlessRecords.Log.Mark;
                var main = new MainForm(null, Profile.NewBlank(), null, null, headless: true);
                RemoteControlServer? server = null;
                try
                {
                    _ = main.Handle;
                    server = new RemoteControlServer(pipe, () => main, new RemoteControlEngine(() => main));
                    server.Start();
                    string Ask(string command)
                    {
                        var reply = RemoteControl.Send(command, pipe, out var reached, connectTimeoutMs: 5000);
                        Check(reached, $"the control channel did not answer \"{command}\": {reply}");
                        return reply;
                    }
                    DriveWhilePumping(main, () =>
                    {
                        Check(Ask("menu Options > Preferences").Contains("\"Preferences\"", StringComparison.Ordinal), "Options, Preferences must open Preferences");
                        Check(Ask("tab Appearance").StartsWith("ok", StringComparison.Ordinal), "the Appearance tab must be reachable by name");
                        var order = Ask("taborder");
                        Check(order.Contains($"\"{Box}\"", StringComparison.Ordinal), $"Tab on the Appearance tab must reach \"{Box}\" (tab order: {Head(order)})");
                        var was = Ask($"get \"{Box}\"");
                        Check(was.Contains("not ticked", StringComparison.Ordinal) == on && was.Contains("ticked", StringComparison.Ordinal),
                            $"the tick box must start {(on ? "unticked" : "ticked")} (got: {Head(was)})");
                        var set = Ask($"set \"{Box}\" {(on ? "on" : "off")}");
                        Check(set.StartsWith("ok", StringComparison.Ordinal), $"the control channel must {(on ? "tick" : "untick")} it (got: {set})");
                        Check(AppConfig.Load().RememberMainWindowPlacement == on, $"{(on ? "ticking" : "unticking")} it must save the setting on this computer");
                        Check(Ask("answer Close").StartsWith("ok", StringComparison.Ordinal), "Preferences closes with its own button");
                    }, TimeSpan.FromSeconds(60));
                    var said = on ? "preference: RememberMainWindowPlacement → off → on [" : "preference: RememberMainWindowPlacement → on → off [";
                    Check(WaitFor(() => Logged(mark, said), TimeSpan.FromSeconds(3)),
                        $"THE LOG: {(on ? "ticking" : "unticking")} it must be logged, stamped with the audio configuration: "
                        + string.Join(" | ", HeadlessRecords.Log.Since(mark).Where(l => l.Contains("preference", StringComparison.Ordinal)).DefaultIfEmpty("nothing")));
                }
                finally
                {
                    server?.Dispose();
                    // Closed with the setting as it now is, which is how a person's window would close after it.
                    try { if (!main.IsDisposed) main.Close(); } catch { /* teardown */ }
                    try { main.Dispose(); } catch { /* teardown */ }
                    Windowless.SetActiveForTest(false);
                }
            }

            // 2. Ticked.
            ThroughPreferences(on: true);
            steps.Add("ticked through the control channel, saved and logged");

            // 3. ON: kept as it closes, and the next main window - a restart, or a profile switch - opens there.
            var mark = Mark();
            var left = new Rectangle(150, 20, 900, 650);
            OpenThenClose(left);
            var kept = AppConfig.Load().MainWindowPlacement;
            Check(kept == new WindowPlacement(150, 20, 900, 650, false), $"ON: the window's size and place must be kept as it closes (kept: {kept?.ToString() ?? "nothing"})");
            Check(Said(mark, "main window: size and position remembered - 900 x 650 at 150, 20"), "THE LOG: keeping them must be logged");
            mark = Mark();
            var back = OpenThenClose(null);
            Check(back.Opened == left && back.State == FormWindowState.Normal, $"ON: the next main window must open where the last closed ({back.Opened}, {back.State}; wanted {left})");
            Check(Said(mark, "main window: size and position restored - 900 x 650 at 150, 20"), "THE LOG: going back to them must be logged");
            steps.Add("on: kept at close and restored by the next window");

            // Maximised: kept as maximised, with the size it goes back to.
            void Keep(WindowPlacement place) { var c = AppConfig.Load(); c.MainWindowPlacement = place; c.Save(); }
            Keep(new WindowPlacement(200, 100, 800, 600, true));
            var big = OpenThenClose(null);
            Check(big.State == FormWindowState.Maximized && big.Opened == new Rectangle(200, 100, 800, 600),
                $"ON: a window closed maximised must open maximised, over the size it goes back to ({big.State}, {big.Opened})");

            // 4. A screen that has gone: the place is pulled wholly onto the screen there is, at the size it had.
            mark = Mark();
            Keep(new WindowPlacement(3000, 1500, 900, 700, false));
            var pulled = OpenThenClose(null).Opened;
            Check(screen.Contains(pulled) && pulled.Size == new Size(900, 700),
                $"GONE: a place on a screen that is not there any more must be pulled wholly onto one that is ({pulled} on {screen})");
            Check(Said(mark, "moved to fit the screens there are now"), "THE LOG: moving it must be logged, with where it was kept");
            // Bigger than the screen now there: no bigger than the screen (the placing itself, as the window does it).
            var fitted = Dpi.PlaceOnAScreen(new Rectangle(-50, -40, 2500, 1600), new Size(680, 520), [screen]);
            Check(fitted == screen, $"TOO BIG: a window bigger than the screen there is now must fit it ({fitted} on {screen})");
            // Two screens, and the one it was on taken away (the placing itself, with two stand-in screens).
            var leftScreen = new Rectangle(0, 0, 1920, 1040);
            var rightScreen = new Rectangle(1920, 0, 1280, 984);
            var onRight = new Rectangle(2100, 200, 900, 700);
            Check(Dpi.PlaceOnAScreen(onRight, new Size(680, 520), [leftScreen, rightScreen]) == onRight,
                "TWO SCREENS: a place wholly on a screen that is still there must be kept exactly");
            var afterUnplug = Dpi.PlaceOnAScreen(onRight, new Size(680, 520), [leftScreen]);
            Check(leftScreen.Contains(afterUnplug) && afterUnplug.Size == onRight.Size,
                $"TWO SCREENS: with the right-hand screen gone, the window must come wholly onto the left one ({afterUnplug})");
            var halfOff = Dpi.PlaceOnAScreen(new Rectangle(1700, 500, 900, 700), new Size(680, 520), [leftScreen, rightScreen]);
            Check(rightScreen.Contains(halfOff) || leftScreen.Contains(halfOff), $"TWO SCREENS: a window across the join must end wholly on one screen ({halfOff})");
            steps.Add("a place on a screen that has gone is pulled wholly onto one there is");

            // 5. The minimum: kept smaller than it may be (an older copy, or a hand-edited file), it opens at its minimum - and
            // placed for that size, so a small window kept in the bottom right corner does not grow off the screen's edges.
            Keep(new WindowPlacement(1200, 600, 300, 200, false));
            var small = OpenThenClose(null);
            Check(small.Opened.Width >= small.Minimum.Width && small.Opened.Height >= small.Minimum.Height && small.Minimum.Width >= 680,
                $"MINIMUM: the window must never open below its minimum {small.Minimum.Width} x {small.Minimum.Height} ({small.Opened.Size})");
            Check(screen.Contains(small.Opened), $"MINIMUM: grown to its minimum, it must still be wholly on the screen ({small.Opened} on {screen})");
            steps.Add($"never below {small.Minimum.Width} x {small.Minimum.Height}");

            // 6. Unticked again: what was kept is forgotten, and the window opens as it always has.
            Check(AppConfig.Load().MainWindowPlacement is not null, "premise: something is kept before remembering is turned off");
            ThroughPreferences(on: false);
            Check(AppConfig.Load().MainWindowPlacement is null, "OFF AGAIN: unticking it must forget the size and place it kept");
            var offAtLast = OpenThenClose(new Rectangle(300, 50, 1000, 650)).Opened;
            Check(offAtLast == today, $"OFF AGAIN: the window must open as it always has ({offAtLast}, not {today})");
            Check(AppConfig.Load().MainWindowPlacement is null, "OFF AGAIN: and nothing is kept as it closes");
            steps.Add("off again: opens as always");
        }
        finally
        {
            MainForm.PlacementAppliesForTest = restoreSeam;
            Dpi.WorkingAreaForTest = restoreArea;
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
        }
        return string.Join("; ", steps);
    }
    // ============================================================================================================
    // What is shown is for sighted people (N3), and the colons Windows programs use (N4).
    // ============================================================================================================

    /// <summary>Every window the dialog audits reach, the password manager with a profile in it (its words differ when it
    /// has none), the main window, and the plugin's panel in the frame a DAW shows. Built, laid out, handed over one at a
    /// time and thrown away after.</summary>
    private static void EachWindowForSightedText(Action<string, Form> look)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-sighted-" + Guid.NewGuid().ToString("N"));
        using var scope = AppConfig.UseThrowawayUserDataDirectory(scratch);
        try
        {
            var windows = DialogFactories().ToList();
            windows.Add(("Profile passwords manager, with a profile", () => ProfilePasswordManagerDialog.Build(StoreWithOneProfileForTest()).Dialog));
            windows.Add(("Main window", () => new MainForm(null, Profile.NewBlank(), null, null, headless: true)));
            foreach (var (name, make) in windows)
            {
                using var form = make();
                LayoutPrepare(form);
                look(name, form);
            }
            using var panel = LayoutSamplePluginPanel();
            using var frame = RemSound.Plugin.RemSoundPlugin.BuildEditorFrame(panel, Dpi.Px(800), Dpi.Px(800));
            LayoutPrepare(frame);
            look("Plugin", frame);
        }
        finally { try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); } catch { /* throwaway */ } }
    }

    /// <summary>The sentences taken off the screen on 2026-09-28 because a sighted person can see what they say, each with
    /// the window it was in. NVDA must still have every one of them, on a control's name or description.</summary>
    internal static readonly (string Window, string Words)[] SpokenNotShownSentences =
    [
        ("Update install notice", "Press Skip or Postpone to choose otherwise."),
        ("Quick profile switch", "Arrow up and down to a profile, then press Enter to switch to it. Escape closes without changing."),
        ("Profile selection", "Select a profile and press Enter, or pick \"New profile\" to start a new one."),
        ("Install options", "Choose what to set up, then select Install."),
        ("Manage named peers", "The peers you've given a name to. Rename or delete any of them."),
        ("Keyboard shortcuts", "Arrow up and down to pick a shortcut."),
        ("Keyboard shortcuts", "(or the Clear this shortcut button)"),
        ("Keyboard shortcuts", "Escape closes the dialog."),
        ("Profile passwords manager, with a profile", "Each profile has its own password."),
        ("Profile passwords manager, with a profile", "Edit any box and press OK to save."),
    ];

    /// <summary>What only ever repeated, for the screen reader, what is on screen: a checklist's position and "Press
    /// Space", and the sentences above.</summary>
    private static readonly System.Text.RegularExpressions.Regex ScreenReaderOnlyWords = new(
        @"Press Space to toggle|Item \d+ of \d+\.|Press Skip or Postpone|Arrow up and down|then select Install|Edit any box and press OK|"
        + @"Rename or delete any of them|Escape closes|Each profile has its own password",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// WHAT IS SHOWN IS FOR SIGHTED PEOPLE (Ed, 2026-09-28): "assume you can remove any and all sentences, unless it
    /// genuinely gives information a sighted person would need but can't see". The line under a checklist - "not checked,
    /// Speakers. Item 1 of 4. Press Space to toggle." - repeated the list for NVDA; now it shows only under an EMPTY list,
    /// to say why it is empty, and the list keeps the words for NVDA as its description. The other sentences taken off
    /// the screen stay on a control's name or description, so NVDA still has every word. Every window is walked.
    /// </summary>
    private static string? ShownTextIsForSightedPeople()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        var withItems = 0;
        var empty = 0;
        var labels = 0;
        var kept = new HashSet<(string, string)>();
        try
        {
            EachWindowForSightedText((window, form) =>
            {
                var all = AllControls(form).ToList();
                // Nothing on screen that only the screen reader needs.
                foreach (var label in all.OfType<Label>())
                {
                    labels++;
                    var shown = label.Text ?? "";
                    var match = ScreenReaderOnlyWords.Match(shown);
                    Check(!match.Success, $"{window}: THE SCREEN: \"{Head(shown)}\" is shown, and \"{match.Value}\" is there only for the screen reader - "
                                          + "a sighted person can see what it says");
                }
                // Every sentence taken off the screen is still NVDA's, in the window it was in.
                foreach (var (inWindow, words) in SpokenNotShownSentences.Where(s => s.Window == window))
                {
                    Check(all.Any(c => (c.AccessibleName ?? "").Contains(words, StringComparison.Ordinal) || (c.AccessibleDescription ?? "").Contains(words, StringComparison.Ordinal)),
                        $"{window}: NVDA'S WORDS: \"{words}\" came off the screen, and must still be on a control's name or description for the screen reader");
                    kept.Add((inWindow, words));
                }
                // The checklists: the line under each (the label that follows the list in its wrapper, FormLayoutRows.AddCheckedListRow).
                foreach (var list in all.OfType<CheckedListBox>())
                {
                    if (list.Parent is not FlowLayoutPanel wrap) continue;
                    var kids = wrap.Controls.Cast<Control>().ToList();
                    var at = kids.IndexOf(list);
                    if (at + 1 >= kids.Count || kids[at + 1] is not Label status) continue;
                    var what = $"{window}: the {list.AccessibleName} list";
                    if (list.Items.Count > 0)
                    {
                        withItems++;
                        Check(status.Text.Length == 0 && status.Height == 0,
                            $"{what} has items, so nothing is shown under it (it shows \"{Head(status.Text)}\", {status.Height} px tall)");
                        Check((list.AccessibleDescription ?? "").EndsWith("Press Space to toggle.", StringComparison.Ordinal),
                            $"{what}: NVDA must still read the item, its tick and its place from the list's description (\"{list.AccessibleDescription}\")");
                    }
                    else
                    {
                        empty++;
                        // A list hidden in this set-up (and its line with it) is sized as it is shown; a shown one has its height now.
                        var tall = LayoutOwnVisible(status) ? status.Height : status.AutoSize ? status.GetPreferredSize(Size.Empty).Height : 0;
                        Check(status.Text.Length > 0 && tall > 0 && status.Text == list.AccessibleDescription,
                            $"{what} is empty, so the line under it says so, to see and to hear (it shows \"{status.Text}\", {tall} px; NVDA: \"{list.AccessibleDescription}\")");
                    }
                }
            });
            Check(kept.Count == SpokenNotShownSentences.Length,
                $"every window a sentence came off must be walked ({kept.Count} of {SpokenNotShownSentences.Length}: missing "
                + string.Join(", ", SpokenNotShownSentences.Where(s => !kept.Contains(s)).Select(s => s.Window).Distinct()) + ")");
            Check(withItems >= 3 && empty >= 3, $"the checklists must be walked both with items and empty ({withItems} with items, {empty} empty)");

            // A list that fills and empties as the app runs: the main window's own refill, which a device plugged in or a
            // driver chosen goes through, puts the line under it right each time.
            using var main = new MainForm(null, Profile.NewBlank(), null, null, headless: true);
            _ = main.Handle;
            var list = Require(FieldOf<CheckedListBox>(main, "sendOutputDevicesList"), "the main window's sendOutputDevicesList was not found");
            var line = Require(FieldOf<Label>(main, "sendOutputDevicesStatusLabel"), "the main window's sendOutputDevicesStatusLabel was not found");
            var sync = Require(typeof(MainForm).GetMethod("SyncDeviceCheckedListBox", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
                "MainForm.SyncDeviceCheckedListBox not found");
            void Fill(params string[] names) =>
                sync.Invoke(main, [list, names.Select((n, i) => new AudioDeviceChoice(n, "{sighted-" + i + "}")).ToList(), false]);
            Fill();
            Check(line.Text == CheckedListAccessibility.EmptyTextFor("output device") && line.Height > 0,
                $"EMPTIED: the line under the list must say it is empty (\"{line.Text}\", {line.Height} px)");
            Fill("Sample speakers", "Sample headphones");
            Check(line.Text.Length == 0 && line.Height == 0, $"FILLED: nothing may be shown under the list once it has items (\"{line.Text}\", {line.Height} px)");
            Check(list.AccessibleDescription == "not checked, Sample speakers. Item 1 of 2. Press Space to toggle.",
                $"FILLED: NVDA's words must be the list's, as they always were (\"{list.AccessibleDescription}\")");
            Fill();
            Check(line.Text == CheckedListAccessibility.EmptyTextFor("output device") && line.Height > 0,
                $"EMPTIED AGAIN: the line must come back (\"{line.Text}\", {line.Height} px)");
        }
        finally { CuePlayer.GloballyMuted = restoreMuted; }
        return $"{labels} labels shown in every window, none only for the screen reader; {withItems} checklists with items show nothing under them "
             + $"and {empty} empty ones say so; the {SpokenNotShownSentences.Length} sentences taken off the screen are all still NVDA's; a list "
             + "refilled as the app runs follows";
    }

    /// <summary>What a label names, when it names a box, list, drop-down, number or slider: its MnemonicTarget, or the
    /// input right after it. A sentence (ending with a full stop) is not a label, and neither is a unit after its box
    /// ("megabytes").</summary>
    private static Control? FieldNamedBy(Label label)
    {
        static bool IsField(Control? c) => c is TextBoxBase or ListControl or UpDownBase or TrackBar;
        var text = (label.Text ?? "").Trim();
        if (text.Length == 0 || text.EndsWith('.') || text.EndsWith('…')) return null;
        // A heading (the house headings are Semibold) or lines of text are not a field's label either.
        if (label.Font.Name == "Segoe UI Semibold" || text.Contains('\n')) return null;
        if (label is MnemonicLabel { MnemonicTarget: { } target } && IsField(target))
        {
            if (target.Parent == label.Parent && label.Parent!.Controls.GetChildIndex(label) > label.Parent.Controls.GetChildIndex(target)) return null;
            return target;
        }
        if (label is MnemonicLabel || label.Parent is null) return null;
        var siblings = label.Parent.Controls.Cast<Control>().ToList();
        var next = siblings.IndexOf(label) + 1 < siblings.Count ? siblings[siblings.IndexOf(label) + 1] : null;
        // The field may sit in a small panel of its own beside the label (the jitter buffer and artefact rows are built so).
        if (next is Panel { Controls.Count: > 0 } holder && holder.Controls.Cast<Control>().FirstOrDefault(IsField) is { } inside) next = inside;
        return IsField(next) ? next : null;
    }

    /// <summary>The colon faults in one window, as a sighted person would see them.</summary>
    private static List<string> ColonFaults(string window, Form form, ref int labelled, ref int captions)
    {
        var faults = new List<string>();
        static string Plain(string s) => s.Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&").Trim();
        foreach (var c in AllControls(form))
        {
            var text = c.Text ?? "";
            if (c is Label label && FieldNamedBy(label) is { } field)
            {
                labelled++;
                var shown = Plain(text);
                if (!shown.EndsWith(':'))
                    faults.Add($"{window}: \"{shown}\" names a {field.GetType().Name} and must end with a colon");
                else if (shown.Contains("(Alt+", StringComparison.Ordinal) && !System.Text.RegularExpressions.Regex.IsMatch(shown, @"\(Alt\+[^)]+\):$"))
                    faults.Add($"{window}: \"{shown}\": the colon goes after the Alt key, \"... (Alt+X):\"");
                // The colon is for the eye. The name a screen reader reads for the field does not gain one.
                if ((field.AccessibleName ?? "").TrimEnd().EndsWith(':'))
                    faults.Add($"{window}: the {field.GetType().Name} \"{field.AccessibleName}\" is read with its label's colon");
            }
            var caption = c switch
            {
                ButtonBase or GroupBox or TabPage => Plain(text),
                Label heading when heading.Font.Name == "Segoe UI Semibold" => Plain(text),
                _ => null,
            };
            if (caption is null || caption.Length == 0) continue;
            captions++;
            var bare = System.Text.RegularExpressions.Regex.Replace(caption, @"\s*\(Alt\+[^)]+\)\s*$", "");
            if (bare.EndsWith(':') || caption.EndsWith(':'))
                faults.Add($"{window}: the {c.GetType().Name} \"{caption}\" must not end with a colon - tick boxes, buttons, headings and tabs have none");
        }
        return faults;
    }

    /// <summary>
    /// COLONS AS WINDOWS PROGRAMS USE THEM (Ed, 2026-09-28; the UI audit found "...from other peers (Alt+N):" beside "Tab
    /// order (Alt+O)"). A label for a box, list, drop-down, number or slider ends with a colon, after its Alt key; a tick
    /// box, button, heading or tab has none; and the name a screen reader reads for the field is as it was, with no colon.
    /// Every window is walked, and a canary with one of each fault proves the walk can find them.
    /// </summary>
    private static string? LabelsEndWithAColon()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        var labelled = 0;
        var captions = 0;
        var faults = new List<string>();
        try
        {
            // The canary: a label with no colon, one with its colon before the Alt key, a tick box and a heading with one.
            using (var canary = new Form())
            {
                var box = new TextBox { AccessibleName = "Canary box" };
                var list = new ListBox { AccessibleName = "Canary list:" };
                canary.Controls.Add(new MnemonicLabel { Text = "Canary &box (Alt+B)", MnemonicTarget = box });
                canary.Controls.Add(box);
                canary.Controls.Add(new Label { Text = "Canary &list: (Alt+L)" });
                canary.Controls.Add(list);
                canary.Controls.Add(new AccessibleCheckBox { Text = "Canary tick (Alt+T):" });
                canary.Controls.Add(Theme.SectionHeader("Canary heading:"));
                int l = 0, k = 0;
                var found = ColonFaults("canary", canary, ref l, ref k);
                foreach (var plant in new[] { "\"Canary box (Alt+B)\" names a TextBox and must end with a colon", "\"Canary list: (Alt+L)\" names a ListBox and must end with a colon",
                                              "\"Canary list:\" is read with its label's colon", "\"Canary tick (Alt+T):\" must not end with a colon",
                                              "\"Canary heading:\" must not end with a colon" })
                    Check(found.Any(f => f.Contains(plant, StringComparison.Ordinal)),
                        $"the check itself is broken: the canary's planted fault ({plant}) was not found. It found: {string.Join(" | ", found.DefaultIfEmpty("nothing"))}");
            }
            EachWindowForSightedText((window, form) => faults.AddRange(ColonFaults(window, form, ref labelled, ref captions)));
        }
        finally { CuePlayer.GloballyMuted = restoreMuted; }
        Check(labelled > 60, $"the labels that name a field must be found in every window ({labelled})");
        Check(captions > 150, $"the tick boxes, buttons, headings and tabs must be found in every window ({captions})");
        Check(faults.Count == 0, $"{faults.Count} colon fault(s): {string.Join(" | ", faults.Take(15))}{(faults.Count > 15 ? " | ..." : "")}");
        return $"{labelled} labels that name a field each end \"...:\" after their Alt key, their fields read as before; {captions} tick boxes, buttons, headings and tabs have none";
    }

    /// <summary>
    /// THE EQ MODE LIST, GREYED OUT (finding 10; Ed, 2026-09-28: "please go ahead"). With no peer chosen it is disabled,
    /// and Windows drew its selected row grey on blue, which could not be read. It draws its own rows only while it is
    /// disabled - enabled, Windows draws it as any list - and what a screen reader reads of it is the same either way, and
    /// the same as a plain list's. The row is drawn for real here, and its colours measured from the pixels.
    /// </summary>
    private static string? EqModeListReadableWhileGreyedOut()
    {
        using var form = new MainForm(null, Profile.NewBlank(), null, null, headless: true);
        LayoutPrepare(form);
        var list = Require(FieldOf<ListBox>(form, "eqModeList"), "the main window's eqModeList was not found");
        Check(!list.Enabled && list.SelectedIndex >= 0, $"premise: with no peer chosen the EQ mode list is greyed out with a row selected (enabled {list.Enabled}, row {list.SelectedIndex})");
        Check(list.DrawMode == DrawMode.OwnerDrawFixed, $"GREYED OUT: RemSound must draw the list's rows itself (drawn by {list.DrawMode}), or Windows draws the selected one grey on blue");

        // What a screen reader reads: the list's name, role, rows and selection, as a plain list with the same rows reads.
        string Read(ListBox l)
        {
            var a = l.AccessibilityObject;
            var rows = Enumerable.Range(0, a.GetChildCount()).Select(i => a.GetChild(i)).Where(r => r is not null).ToList();
            return $"{a.Name}|{a.Role}|{string.Join(",", rows.Select(r => r!.Name))}|{string.Join(",", rows.Where(r => (r!.State & AccessibleStates.Selected) != 0).Select(r => r!.Name))}";
        }
        using var plain = new ListBox { AccessibleName = list.AccessibleName, IntegralHeight = false };
        plain.Items.AddRange(list.Items.Cast<object>().ToArray());
        form.Controls.Add(plain);
        _ = plain.Handle;
        plain.SelectedIndex = list.SelectedIndex;
        plain.Enabled = false;
        var greyed = Read(list);
        Check(greyed == Read(plain), $"GREYED OUT: a screen reader must read it as a plain list ({greyed} against {Read(plain)})");

        // The selected row as it is drawn: the darkest pixel is the words, the commonest the fill.
        var row = list.GetItemRectangle(list.SelectedIndex);
        using var picture = new Bitmap(Math.Max(1, row.Width), Math.Max(1, row.Height));
        using (var g = Graphics.FromImage(picture))
        {
            var onDraw = Require(typeof(ListBox).GetMethod("OnDrawItem", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
                "ListBox.OnDrawItem not found");
            onDraw.Invoke(list, [new DrawItemEventArgs(g, list.Font, new Rectangle(Point.Empty, row.Size), list.SelectedIndex,
                DrawItemState.Selected | DrawItemState.Disabled, SystemColors.GrayText, SystemColors.Highlight)]);
        }
        var pixels = new List<Color>();
        for (var x = 3; x < picture.Width - 3; x++)
            for (var y = 2; y < picture.Height - 2; y++) pixels.Add(picture.GetPixel(x, y));
        var fill = pixels.GroupBy(p => p.ToArgb()).OrderByDescending(p => p.Count()).First().First();
        var words = pixels.OrderByDescending(p => LayoutContrast(p, fill)).First();
        var ratio = LayoutContrast(words, fill);
        Check(ratio >= 4.5, $"GREYED OUT: the selected row's words must read at 4.5:1 at least - drawn {LayoutHex(words)} on {LayoutHex(fill)}, {ratio:0.00}:1");
        Check(LayoutContrast(fill, SystemColors.Highlight) > 1.5, $"GREYED OUT: the selected row must not be filled with the highlight blue it was unreadable on ({LayoutHex(fill)})");

        // Enabled, Windows draws it again - and a screen reader reads it the same.
        list.Enabled = true;
        Check(list.DrawMode == DrawMode.Normal, $"ENABLED: Windows must draw the list as any other ({list.DrawMode})");
        var enabled = Read(list);
        Check(enabled == greyed && list.Items.Count == 3 && list.AccessibleName == "EQ mode",
            $"ENABLED: the rows, the selection and the name must be as before ({enabled} against {greyed})");
        list.Enabled = false;
        Check(list.DrawMode == DrawMode.OwnerDrawFixed && Read(list) == greyed, "GREYED OUT AGAIN: drawn by RemSound once more, and read the same");
        return $"greyed out, the selected row is drawn {LayoutHex(words)} on {LayoutHex(fill)} ({ratio:0.00}:1) and read as a plain list; enabled, Windows draws it and it reads the same";
    }
}
