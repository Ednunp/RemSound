using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// CONTEXT HELP ON EVERY CONTROL IN EVERY WINDOW (stage 2, 2026-09-25). Ed: "I want you to create a test for every single
/// dialogue control etc etc that opens the right thing in the right place".
/// </summary>
internal static partial class SelfTest
{
    private const int WmKeyUp = 0x0101;

    /// <summary>Select the tab page a control sits on, all the way up, so it is the one showing.</summary>
    private static void ShowPageOf(Control control)
    {
        for (var c = control.Parent; c is not null; c = c.Parent)
            if (c is TabPage page && page.Parent is TabControl tabs) tabs.SelectedTab = page;
    }

    /// <summary>Whether a control is showing by its own settings and every parent's, as a window never on screen reports.</summary>
    private static bool ShowingBySetting(Control control)
    {
        for (Control? c = control; c is not null && c is not Form; c = c.Parent)
            if (c is not TabPage && !IsSetVisibleForAltAudit(c)) return false;
        return true;
    }

    /// <summary>
    /// CONTEXT HELP: F1 ON EVERY CONTROL IN EVERY WINDOW OPENS ITS OWN HELP, IN THE RIGHT PLACE.
    ///
    /// <para>Every control a person can reach: the main window's every tab in all three audio configurations, every
    /// dialog, every item of the menus and the tray menu, and the DAW plugin's window. For each, the real F1: through the
    /// app's own key filter with the keyboard on the control (for a menu item, with its menu open), and for the plugin
    /// straight to the control, as a music program delivers it. The help must open with the manual's own entry for that
    /// control word for word (read by an independent reading of readme.html), titled with its name, the keyboard on the
    /// words with all of them selected, the open sound playing; Escape must close it with the close sound and the
    /// keyboard back on the control (a menu open again on the item). In each window the manual button is pressed once and
    /// must open the manual at that exact entry. A greyed-out control can't be reached, so F1 can't be pressed on it: its
    /// help is checked for the right words without the key. Then the fallbacks and the sounds turned off.</para>
    /// </summary>
    private static string? ContextHelpOnEveryControl()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        var browsed = new ConcurrentQueue<string>();
        HelpLauncher.BrowserForTest = browsed.Enqueue;
        HelpLauncher.WireContextHelp();
        HelpSoundService.Reload();
        HelpKeyMessageFilter.ModifiersForTest = () => Keys.None;
        MainForm? form = null;
        var roundTrips = 0;
        var byWordsOnly = 0;
        var menuItems = 0;
        var buttons = 0;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var manualPath = ContextHelp.ManualPath();
            Check(File.Exists(manualPath), $"premise: the manual must be beside RemSound ({manualPath})");
            var html = File.ReadAllText(manualPath);
            var manualUri = new Uri(manualPath).AbsoluteUri;

            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            var main = form;
            var filter = new HelpKeyMessageFilter();
            var said = new ConcurrentQueue<string>();
            form.LogForTest.EventTapForTest = said.Enqueue;
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var tabs = Require(Require(typeof(MainForm).GetField("mainTabControl", flags), "MainForm.mainTabControl not found").GetValue(form) as TabControl,
                "MainForm.mainTabControl is not a tab control");

            ContextHelpWindow? OpenHelp() => main.Invoke(() => ContextHelp.Open);
            bool PressF1(Control on) => main.Invoke(() =>
            {
                var m = Message.Create(KeyboardWindowOf(on).Handle, WmKeyDown, VkF1, IntPtr.Zero);
                return filter.PreFilterMessage(ref m);
            });
            bool CueSince(long mark, string file) => HeadlessRecords.Cues.Since(mark).Any(l => l.Contains(file, StringComparison.OrdinalIgnoreCase));
            Control? ActiveIn(Form window) => main.Invoke(() =>
            {
                Control? at = window.ActiveControl;
                while (at is ContainerControl { ActiveControl: { } inner } && inner != at) at = inner;
                return at is { Parent: UpDownBase up } ? up : at;
            });
            void WordsAre(string what, string key, string text)
            {
                var expected = ManualEntryWords(html, key, out var isRow);
                var words = WordsOf(text);
                if (isRow) words.Remove("Shortcut");
                var missing = expected.GroupBy(w => w).Where(g => words.Count(w => w == g.Key) < g.Count()).Select(g => g.Key).ToList();
                var extra = words.Distinct().Where(w => !expected.Contains(w)).ToList();
                Check(missing.Count == 0 && extra.Count == 0 && (!isRow || words.SequenceEqual(expected)),
                    $"{what}: THE WORDS: the help must be the manual's own entry for {key}, word for word (missing: {string.Join(" ", missing.Take(8))}; "
                    + $"not in the manual: {string.Join(" ", extra.Take(8))}; shows: {Head(text)})");
            }
            string Spoken(Control c) => Regex.Replace(
                !string.IsNullOrWhiteSpace(c.AccessibleName) ? c.AccessibleName! : c.Text.Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&"),
                @"\s*\(Alt\+.*$", "").Trim();

            // The window that is open: its words and where they came from, the keyboard, the selection.
            void TheHelpIsRight(string what, string key, string title)
            {
                var w = OpenHelp()!;
                var (shownTitle, text, shownKey, onText, selection, length) = main.Invoke(() =>
                {
                    var r = (long)ContextHelp.SendMessage(w.HelpText.Handle, ContextHelp.EM_GETSEL, IntPtr.Zero, IntPtr.Zero);
                    return (w.Text, w.HelpText.Text, w.Key, w.ActiveControl == w.HelpText,
                            ((int)(r & 0xFFFF), (int)((r >> 16) & 0xFFFF)), w.HelpText.TextLength);
                });
                Check(shownKey == key, $"{what}: THE RIGHT THING: the help shown must be its own ({shownKey}, wanted {key})");
                Check(NameFits(title, text), $"{what}: THE RIGHT THING: the help shown must be about \"{title}\" - it reads: {Head(text)}");
                Check(HelpManual.CountOf(html, key) == 1, $"{what}: THE RIGHT PLACE: help-{key} must be in the manual exactly once");
                WordsAre(what, key, text);
                Check(shownTitle == $"Context help: {title}", $"{what}: the window must be named for it (\"{shownTitle}\")");
                Check(onText, $"{what}: the keyboard must land on the help words");
                Check(selection == (0, length), $"{what}: every word must be selected, so the screen reader reads it all ({selection.Item1}-{selection.Item2} of {length})");
                keys.Add(key);
            }
            void PressTheManualButton(string what, string key)
            {
                var w = OpenHelp()!;
                browsed.Clear();
                main.Invoke(() => w.OpenInManualButton.PerformClick());
                Check(WaitFor(() => OpenHelp() is null, TimeSpan.FromSeconds(5)), $"{what}: the manual button must close the help");
                Check(browsed.TryDequeue(out var target) && target == $"{manualUri}#help-{key}",
                    $"{what}: THE PLACE: the manual button must open the manual at help-{key} (went to {target ?? "nowhere"})");
                buttons++;
                Settle();
            }
            // Let the window finish what closing help set going - the keyboard put back on the control, the focus and
            // activation messages that follow - before the next key. A person's next key comes seconds later; the check's
            // came within a millisecond, into the middle of it, and the next help then sometimes ignored its Escape.
            void Settle()
            {
                for (var i = 0; i < 3; i++) main.Invoke(Application.DoEvents);
            }
            void Escape(string what)
            {
                var w = OpenHelp()!;
                var how = main.Invoke(() => PressEscapeIn(w));
                Check(WaitFor(() => OpenHelp() is null, TimeSpan.FromSeconds(5)), $"{what}: ESCAPE must close the help ({how})");
                Settle();
            }

            // One control, the whole way round. The window's own record of its active control is moved off the control
            // first (the key still goes to the control), so only context help putting the keyboard back can pass.
            void RoundTrip(Form window, Control control, string scope, Control? elsewhere, bool pressButton)
            {
                var key = ContextHelp.KeyOf(control);
                var title = Spoken(control);
                var what = $"{scope}: \"{title}\"";
                Check(key is not null, $"{what} must have context help of its own");
                main.Invoke(() => { ShowPageOf(control); (elsewhere ?? control).Select(); });
                var mark = HeadlessRecords.Cues.Mark;
                DriveWhilePumping(main, () =>
                {
                    Check(PressF1(control) && WaitFor(() => OpenHelp() is not null, TimeSpan.FromSeconds(5)), $"{what}: THE HELP: F1 must open its help");
                    TheHelpIsRight(what, key!, title);
                    Check(CueSince(mark, "help open"), $"{what}: the help-open sound must play");
                    if (pressButton)
                    {
                        PressTheManualButton(what, key!);
                        Check(PressF1(control) && WaitFor(() => OpenHelp() is not null, TimeSpan.FromSeconds(5)), $"{what}: F1 must open its help again");
                    }
                    var beforeEscape = HeadlessRecords.Cues.Mark;
                    Escape(what);
                    Check(CueSince(beforeEscape, "help close"), $"{what}: the help-close sound must play when it closes");
                }, TimeSpan.FromSeconds(40));
                if (elsewhere is not null)
                {
                    var active = ActiveIn(window);
                    Check(ReferenceEquals(active, control), $"{what}: after Escape the keyboard must be back on it (it is on {(active is null ? "nothing" : Spoken(active))})");
                }
                roundTrips++;
            }

            // A control nobody can reach just now (greyed out, or hidden in this set-up): the right words, without the key.
            void WordsWithoutTheKey(Control control, string scope)
            {
                var key = ContextHelp.KeyOf(control);
                var what = $"{scope}: \"{Spoken(control)}\" (by its words)";
                Check(key is not null, $"{what} must have context help of its own");
                using var w = ContextHelp.Build(Spoken(control), key!);
                Check(w.Key == key, $"{what}: the help built must be its own");
                WordsAre(what, key!, w.HelpText.Text);
                Check(NameFits(Spoken(control), w.HelpText.Text), $"{what}: THE RIGHT THING: the help must be about it - it reads: {Head(w.HelpText.Text)}");
                keys.Add(key!);
                byWordsOnly++;
            }

            // ---- The main window: every tab, all three configurations. A control met again, with the same help and the
            // same name, in a later configuration is checked for its words rather than pressed again - the key press and
            // the window are the same code, and three presses of each took three minutes. What differs between the
            // configurations (the ASIO controls, names that change) is pressed in the configuration that shows it.
            var pressed = new HashSet<(string Key, string Name)>();
            foreach (var configuration in AudioConfigurations.All)
            {
                SetAudioConfigurationForTest(form, configuration);
                InvokeWindowMethodForAltAudit(form, "UpdateBothIndependentVisibility");
                InvokeWindowMethodForAltAudit(form, "UpdateAutoTuneIntervalWording");
                foreach (var autoTune in AllControls(form).OfType<CheckBox>().Where(b => (b.AccessibleName ?? "").Contains("auto-tune", StringComparison.OrdinalIgnoreCase)))
                    form.Invoke(() => autoTune.Checked = true);
                var here = 0;
                foreach (TabPage page in tabs.TabPages)
                {
                    var first = true;
                    foreach (var control in EveryFocusableUnder(page))
                    {
                        if (ShowingBySetting(control) && control.Enabled
                            && pressed.Add((ContextHelp.KeyOf(control) ?? "", Spoken(control))))
                        {
                            RoundTrip(form, control, $"{configuration.Describe()} / {page.Text}", tabs, pressButton: first);
                            first = false;
                        }
                        else WordsWithoutTheKey(control, $"{configuration.Describe()} / {page.Text}");
                        here++;
                    }
                }
                Check(here >= 40, $"{configuration.Describe()}: premise - the main window's controls must be found ({here})");
            }

            // ---- The parametric EQ's controls, which exist only while that mode is chosen.
            ShowEqModeForTest(form, 2);
            var panEqPage = tabs.TabPages.Cast<TabPage>().First(p => ContextHelp.KeyOf(p) == "pan-eq");
            var parametric = EveryFocusableUnder(panEqPage).Where(c => (ContextHelp.KeyOf(c) ?? "").StartsWith("pan-eq.", StringComparison.Ordinal)
                && ContextHelp.KeyOf(c) is "pan-eq.add-band" or "pan-eq.band-list" or "pan-eq.delete-band").ToList();
            Check(parametric.Count == 3, $"premise: the parametric EQ's three controls must be found ({parametric.Count})");
            foreach (var control in parametric)
            {
                if (ShowingBySetting(control) && control.Enabled) RoundTrip(form, control, "parametric EQ", tabs, pressButton: false);
                else WordsWithoutTheKey(control, "parametric EQ");
            }
            ShowEqModeForTest(form, 0);

            // ---- The row of tabs, and something with no help of its own: the tab's.
            DriveWhilePumping(form, () =>
            {
                main.Invoke(() => tabs.SelectedIndex = 0);
                var firstTab = main.Invoke(() => tabs.SelectedTab!);
                Check(PressF1(tabs) && WaitFor(() => OpenHelp() is not null, TimeSpan.FromSeconds(5)), "F1 on the row of tabs must open help");
                Check(main.Invoke(() => OpenHelp()!.Key) == ContextHelp.KeyOf(firstTab), "F1 on the row of tabs must give the help of the tab showing");
                Escape("the row of tabs");
            }, TimeSpan.FromSeconds(30));
            var profilePage = main.Invoke(() => tabs.TabPages.Cast<TabPage>().First(p => ContextHelp.KeyOf(p) == "audio-profile"));
            var stray = new TextBox { AccessibleName = "A box with no help of its own" };
            form.Invoke(() => { profilePage.Controls.Add(stray); tabs.SelectedTab = profilePage; });
            try
            {
                DriveWhilePumping(form, () =>
                {
                    Check(PressF1(stray) && WaitFor(() => OpenHelp() is not null, TimeSpan.FromSeconds(5)), "F1 on something with no help of its own must still open help");
                    var w = OpenHelp()!;
                    Check(main.Invoke(() => w.Key) == "audio-profile" && main.Invoke(() => w.Text) == "Context help: Audio profile tab",
                        $"THE FALLBACK: it must show its tab's help, named for the tab ({main.Invoke(() => w.Text)}, {main.Invoke(() => w.Key)})");
                    Escape("the fallback");
                }, TimeSpan.FromSeconds(30));
            }
            finally { form.Invoke(() => { profilePage.Controls.Remove(stray); stray.Dispose(); }); }
            Check(said.Any(l => l.Contains("\"A box with no help of its own\" - nothing of its own, showing \"Audio profile tab\"", StringComparison.Ordinal)),
                "and the log must say help of its own was missing, and whose was shown instead");

            // ---- Every dialog.
            var dialogs = 0;
            foreach (var (name, make) in DialogFactories())
            {
                if (name is "Context help window" or "Headless question") continue;
                using var dialog = make();
                _ = dialog.Handle;
                var controls = EveryFocusableUnder(dialog);
                var reachable = controls.Where(c => ShowingBySetting(c) && c.Enabled).ToList();
                var first = true;
                foreach (var control in controls)
                {
                    if (reachable.Contains(control))
                    {
                        var elsewhere = reachable.FirstOrDefault(c => !ReferenceEquals(c, control));
                        RoundTrip(dialog, control, $"dialog \"{name}\"", elsewhere, pressButton: first);
                        first = false;
                    }
                    else WordsWithoutTheKey(control, $"dialog \"{name}\"");
                }
                dialogs++;
            }
            Check(dialogs >= 20, $"premise: every dialog must be reached ({dialogs})");
            // The passwords window lists a box per saved profile; the audits' copy has none.
            using (var passwords = ProfilePasswordManagerDialog.Build(StoreWithOneProfileForTest()).Dialog)
            {
                _ = passwords.Handle;
                var boxes = EveryFocusableUnder(passwords).Where(c => ContextHelp.KeyOf(c) == "dialog.profile-passwords-manager.password").ToList();
                Check(boxes.Count >= 1, "premise: with a saved profile, the passwords window must have a password box");
                var other = EveryFocusableUnder(passwords).FirstOrDefault(c => !boxes.Contains(c) && c.Enabled);
                foreach (var box in boxes)
                {
                    if (ShowingBySetting(box) && box.Enabled) RoundTrip(passwords, box, "dialog \"Profile passwords manager\"", other, pressButton: false);
                    else WordsWithoutTheKey(box, "dialog \"Profile passwords manager\"");
                }
            }

            // ---- The menus and the tray menu: F1 with the menu open on the item.
            // One menu item, found by `find` AFTER `open` has run: a submenu can rebuild itself as it opens (Recent profiles,
            // the tray's Profiles), so an item held from an earlier opening may be one it has thrown away.
            void MenuRoundTrip(Func<ToolStripItem?> find, string scope, Action open, Action close)
            {
                main.Invoke(open);
                var item = Require(main.Invoke(find), $"{scope} menu: an item was not found after its menu opened");
                var key = ContextHelp.KeyOfItem(item);
                var title = ContextHelp.NameOfItem(item);
                var what = $"{scope} menu: \"{title}\"";
                Check(key is not null, $"{what} must have context help of its own");
                if (!main.Invoke(() => item.Enabled))
                {
                    // Greyed out ("No recent profiles", the service status line): the keyboard can't land on it, so F1 can't
                    // be pressed on it. Its help is checked for the right words.
                    main.Invoke(close);
                    using var built = ContextHelp.Build(title, key!);
                    Check(built.Key == key, $"{what}: the help built must be its own");
                    WordsAre($"{what} (greyed out)", key!, built.HelpText.Text);
                    Check(NameFits(title, built.HelpText.Text), $"{what}: THE RIGHT THING: the help must be about it - it reads: {Head(built.HelpText.Text)}");
                    keys.Add(key!);
                    byWordsOnly++;
                    return;
                }
                var mark = HeadlessRecords.Cues.Mark;
                DriveWhilePumping(main, () =>
                {
                    Check(main.Invoke(() => ReferenceEquals(ContextHelp.OpenMenuItem(), item)), $"{what}: premise - the menu must be open on the item");
                    Check(PressF1(form) && WaitFor(() => OpenHelp() is not null, TimeSpan.FromSeconds(5)), $"{what}: THE HELP: F1 with the menu open must open the item's help");
                    TheHelpIsRight(what, key!, title);
                    Check(CueSince(mark, "help open"), $"{what}: the help-open sound must play");
                    if (scope == "tray")
                    {
                        // From the tray the main window may be hidden: the help must come up in front of everything, owned by
                        // the top-most window ForegroundDialog makes, as every question from the tray does.
                        var where = main.Invoke(() =>
                        {
                            var owner = NativeGetWindow(OpenHelp()!.Handle, GW_OWNER);
                            return Control.FromHandle(owner) is Form { TopMost: true, ShowInTaskbar: false } ? "in front" : $"owned by {owner}";
                        });
                        Check(where == "in front", $"{what}: FROM THE TRAY: help must open in front of everything ({where})");
                    }
                    Escape(what);
                    // Back on the item - found again by its text, since the menu may have rebuilt itself as it reopened.
                    Check(WaitFor(() => main.Invoke(() => ContextHelp.OpenMenuItem() is { } on && on.Text == item.Text
                                                          && ContextHelp.KeyOfItem(on) == key && on.Owner is not null), TimeSpan.FromSeconds(3)),
                        $"{what}: after Escape the menu must be open again with the keyboard on the item");
                    main.Invoke(close);
                }, TimeSpan.FromSeconds(30));
                menuItems++;
            }
            List<string> NamesIn(Func<ToolStripDropDownItem> of) => main.Invoke(() =>
                of().DropDownItems.OfType<ToolStripMenuItem>().Where(i => i.Available).Select(i => i.Text ?? "").ToList());
            ToolStripMenuItem? Named(ToolStripDropDownItem inMenu, string text) =>
                inMenu.DropDownItems.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Text == text);

            // A recent profile, so the menus that list them - Recent profiles, and the tray's Profiles - have one to test,
            // whatever ran before. Put back afterwards.
            var recentBefore = AppConfig.Load().RecentProfiles.ToList();
            var recentStore = StoreWithOneProfileForTest();
            var recentPath = recentStore.PathFor("Audit profile");
            { var cfg = AppConfig.Load(); cfg.RecentProfiles.RemoveAll(r => string.Equals(r, recentPath, StringComparison.OrdinalIgnoreCase)); cfg.RecentProfiles.Insert(0, recentPath); cfg.Save(); }

            // Read through the record every log feeds in a headless run: the dialog audits build a second main window (for
            // Manage named peers), and the last window made owns "the current log" from then on.
            var menuLogMark = HeadlessRecords.Log.Mark;
            var menuBar = Require(form.MainMenuStrip, "the main window has no menu bar");
            try
            {
                foreach (ToolStripMenuItem top in menuBar.Items.OfType<ToolStripMenuItem>().ToList())
                {
                    if (!top.Available) continue;
                    MenuRoundTrip(() => top, "main window's", () => top.ShowDropDown(), () => top.HideDropDown());
                    main.Invoke(() => top.ShowDropDown());
                    var names = NamesIn(() => top);
                    main.Invoke(() => top.HideDropDown());
                    foreach (var name in names)
                    {
                        MenuRoundTrip(() => Named(top, name), "main window's", () => { top.ShowDropDown(); Named(top, name)?.Select(); }, () => top.HideDropDown());
                        main.Invoke(() => { top.ShowDropDown(); Named(top, name)?.ShowDropDown(); });
                        var innerNames = main.Invoke(() => Named(top, name) is { HasDropDownItems: true } sub ? NamesIn(() => sub) : []);
                        main.Invoke(() => top.HideDropDown());
                        foreach (var innerName in innerNames)
                            MenuRoundTrip(() => Named(top, name) is { } sub ? Named(sub, innerName) : null, "main window's",
                                () => { top.ShowDropDown(); if (Named(top, name) is { } sub) { sub.ShowDropDown(); Named(sub, innerName)?.Select(); } },
                                () => top.HideDropDown());
                    }
                }
                var tray = form.TrayControllerForTest.TrayMenuForTest;
                ToolStripMenuItem? InTray(string text) => tray.Items.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Text == text);
                var trayNames = main.Invoke(() => tray.Items.OfType<ToolStripMenuItem>().Where(i => i.Available).Select(i => i.Text ?? "").ToList());
                foreach (var name in trayNames)
                {
                    MenuRoundTrip(() => InTray(name), "tray", () => { tray.Show(Point.Empty); InTray(name)?.Select(); }, () => tray.Close());
                    main.Invoke(() => { tray.Show(Point.Empty); InTray(name)?.ShowDropDown(); });
                    var innerNames = main.Invoke(() => InTray(name) is { HasDropDownItems: true } sub ? NamesIn(() => sub) : []);
                    main.Invoke(() => tray.Close());
                    foreach (var innerName in innerNames)
                        MenuRoundTrip(() => InTray(name) is { } sub ? Named(sub, innerName) : null, "tray",
                            () => { tray.Show(Point.Empty); if (InTray(name) is { } sub) { sub.ShowDropDown(); Named(sub, innerName)?.Select(); } },
                            () => tray.Close());
                }
            }
            finally
            {
                var cfg = AppConfig.Load();
                cfg.RecentProfiles.Clear();
                cfg.RecentProfiles.AddRange(recentBefore);
                cfg.Save();
            }
            Check(menuItems >= 45, $"premise: the menu items must be reached, a recent profile's included ({menuItems})");

            // ---- The plugin window: F1 straight to each control, as a music program delivers it, and the Help button.
            using (var host = new Form { Text = "Plugin host", ShowInTaskbar = false })
            {
                var panel = new PluginEditorPanel { Dock = DockStyle.Fill };
                host.Controls.Add(panel);
                host.Show();   // off-screen and out of focus, as every window in a headless run is; a button in a window never shown can't be pressed
                var pluginControls = EveryFocusableUnder(panel);
                Check(pluginControls.Count >= 9, $"premise: the plugin window's controls must be found ({pluginControls.Count})");
                foreach (var control in pluginControls)
                {
                    var key = ContextHelp.KeyOf(control);
                    var title = Spoken(control);
                    var what = $"plugin window: \"{title}\"";
                    Check(key is not null, $"{what} must have context help of its own");
                    if (!control.Enabled) { WordsWithoutTheKey(control, "plugin window"); continue; }
                    var mark = HeadlessRecords.Cues.Mark;
                    DriveWhilePumping(main, () =>
                    {
                        main.BeginInvoke(() => RaiseKeyDown(control, Keys.F1));
                        Check(WaitFor(() => OpenHelp() is not null, TimeSpan.FromSeconds(5)), $"{what}: THE HELP: F1 as the control's own KeyDown sees it, as a music program hands it over, must open its help");
                        TheHelpIsRight(what, key!, title);
                        Escape(what);
                    }, TimeSpan.FromSeconds(30));
                    roundTrips++;
                }
                DriveWhilePumping(main, () =>
                {
                    main.BeginInvoke(() => panel.HelpButtonForTest.PerformClick());   // not Invoke: the help it opens waits for Escape
                    Check(WaitFor(() => OpenHelp() is not null, TimeSpan.FromSeconds(5)), "the plugin's Context help button must open help");
                    var (text, shownKey) = main.Invoke(() => (OpenHelp()!.HelpText.Text, OpenHelp()!.Key));
                    Check(shownKey == "plugin.window", $"its manual button must open the plugin window's part of the manual ({shownKey})");
                    foreach (var k in PluginEditorPanel.WindowHelpKeys)
                    {
                        var entry = WordsOf(HelpManual.EntryText(html, k) ?? "");
                        var shown = WordsOf(text);
                        Check(entry.Count > 3 && entry.All(shown.Contains), $"the Context help button must show every control's help, {k} included");
                    }
                    Escape("the plugin's Context help button");
                }, TimeSpan.FromSeconds(30));
            }

            // ---- The sounds turned off, as Preferences turns them off: help opens and closes in silence.
            using (AppConfig.UseThrowawayUserDataDirectory(Path.Combine(Path.GetTempPath(), "remsound-helpcues-" + Guid.NewGuid().ToString("N"))))
            {
                var cfg = AppConfig.Load();
                cfg.EnableHelpOpenCue = false;
                cfg.EnableHelpCloseCue = false;
                cfg.Save();
                HelpSoundService.Reload();
                var smooth = Require(ContextHelpMarked(form, "audio-profile.smoothness"), "the Buffer smoothness list was not found");
                var quiet = HeadlessRecords.Cues.Mark;
                DriveWhilePumping(form, () =>
                {
                    main.Invoke(() => ShowPageOf(smooth));
                    Check(PressF1(smooth) && WaitFor(() => OpenHelp() is not null, TimeSpan.FromSeconds(5)), "with its sounds off, help must still open");
                    Escape("with its sounds off");
                }, TimeSpan.FromSeconds(30));
                Check(!CueSince(quiet, "help open") && !CueSince(quiet, "help close"), "TURNED OFF: with the help sounds off in Preferences, neither may play");
            }
            HelpSoundService.Reload();

            // ---- A window with no context help anywhere: the whole manual, as F1 always did.
            using (var bare = new Form { Text = "No help here" })
            {
                var box = new TextBox();
                bare.Controls.Add(box);
                _ = bare.Handle;
                browsed.Clear();
                var taken = main.Invoke(() => { var m = Message.Create(box.Handle, WmKeyDown, VkF1, IntPtr.Zero); return filter.PreFilterMessage(ref m); });
                Check(taken && browsed.TryDequeue(out var whole) && whole == manualUri, "F1 where nothing has help must open the whole manual, from the top");
            }

            Check(said.Any(l => l.StartsWith("context help: F1 on \"", StringComparison.Ordinal)), "the log must say what F1 was pressed on and which help it showed");
            Check(HeadlessRecords.Log.Since(menuLogMark).Any(l => l.Contains("context help: F1 on the menu item \"", StringComparison.Ordinal)),
                "and for menu items too");
            Check(said.Any(l => l.Contains("context help: opening the manual in the browser at ", StringComparison.Ordinal)), "and where the manual was opened");
            form.LogForTest.EventTapForTest = null;

            return $"{roundTrips} F1 round trips on controls (the main window's tabs in all three configurations, {dialogs} dialogs, the "
                 + $"plugin window) and {menuItems} on menu items with the menu open: each opens its own manual entry word for word, "
                 + "named for it, the keyboard on the words, all selected, the sounds playing; Escape closes it with the keyboard "
                 + $"back on the control or the menu open again on the item. {buttons} manual buttons open their exact entries; "
                 + $"{byWordsOnly} greyed-out or hidden controls have the right words; {keys.Count} different entries. The plugin's "
                 + "Context help button shows them all; fallbacks, silence when turned off, and the whole manual where there is no help";
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            HelpKeyMessageFilter.ModifiersForTest = null;
            HelpLauncher.BrowserForTest = RunBrowsed.Enqueue;
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
        }
    }
}
