using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// CONTEXT HELP, EVERYWHERE (stage 2, 2026-09-25). Ed: "we do it for every window every tab every control etc." - and the
/// status boxes too ("build help controls for things like the status boxes i.e. the total latency box").
/// </summary>
internal static partial class SelfTest
{
    /// <summary>Every control a person can Tab to under a parent, shown or not: a control hidden until something is ticked
    /// is still one somebody reaches. Tab pages are all walked, not only the one showing.</summary>
    private static List<Control> EveryFocusableUnder(Control parent)
    {
        var found = new List<Control>();
        void Walk(Control p)
        {
            foreach (Control c in p.Controls)
            {
                if (c is UpDownBase or ListControl or TextBoxBase or ButtonBase or TrackBar)
                {
                    if (c.TabStop) found.Add(c);
                    continue;
                }
                if (c is TabControl tabs)
                {
                    foreach (TabPage page in tabs.TabPages) Walk(page);
                    continue;
                }
                Walk(c);
            }
        }
        Walk(parent);
        return found;
    }

    /// <summary>Put the pan and EQ tab into one EQ mode (0 simple, 1 advanced, 2 parametric) and build its controls, as
    /// choosing the mode does with a peer selected - the parametric controls only exist while that mode is showing.</summary>
    private static void ShowEqModeForTest(MainForm form, int mode)
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var list = Require(Require(typeof(MainForm).GetField("eqModeList", flags), "MainForm.eqModeList not found").GetValue(form) as ListBox,
            "MainForm.eqModeList is not a list");
        list.SelectedIndex = mode;
        InvokeWindowMethodForAltAudit(form, "RebuildEqBandSliders");
    }

    /// <summary>Every item in a menu, however deep, but not the separators.</summary>
    private static IEnumerable<ToolStripItem> EveryMenuItem(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
        {
            if (item is ToolStripSeparator) continue;
            yield return item;
            if (item is ToolStripDropDownItem { HasDropDownItems: true } dropDown)
                foreach (var inner in EveryMenuItem(dropDown.DropDownItems)) yield return inner;
        }
    }

    /// <summary>
    /// THE UPDATE NOTICE'S COUNTDOWN WAITS WHILE CONTEXT HELP IS OPEN.
    ///
    /// <para>The notice that an update is about to install counts down and installs by itself. F1 on one of its buttons
    /// opened help while the clock ran on, so reading what "Postpone" does could end in the install starting the moment
    /// the help closed (found drafting its help, 2026-09-25). The real notice, shown, with the real F1.</para>
    /// </summary>
    private static string? TheUpdateCountdownWaitsForContextHelp()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        HelpLauncher.WireContextHelp();
        HelpKeyMessageFilter.ModifiersForTest = () => Keys.None;
        try
        {
            using var notice = new UpdateInstallNoticeDialog(
                new UpdateInfo("v9.9", new Version(9, 9, 0), "https://example.invalid/x.zip", "notes", "https://example.invalid/rel"));
            var postpone = Require(AllControls(notice).OfType<Button>().FirstOrDefault(b => b.AccessibleName == "Postpone"), "the Postpone button was not found");
            var filter = new HelpKeyMessageFilter();
            var results = new List<string>();
            notice.Show();
            DriveWhilePumping(notice, () =>
            {
                var start = notice.Invoke(() => notice.SecondsRemainingForTest);
                Check(WaitFor(() => notice.Invoke(() => notice.SecondsRemainingForTest) < start, TimeSpan.FromSeconds(3)),
                    "premise: the countdown must be running");
                notice.Invoke(() => { var m = Message.Create(postpone.Handle, WmKeyDown, VkF1, IntPtr.Zero); return filter.PreFilterMessage(ref m); });
                Check(WaitFor(() => notice.Invoke(() => ContextHelp.Open) is not null, TimeSpan.FromSeconds(5)), "F1 on Postpone must open its help");
                var paused = notice.Invoke(() => notice.SecondsRemainingForTest);
                Thread.Sleep(2600);
                var after = notice.Invoke(() => notice.SecondsRemainingForTest);
                Check(after == paused && !notice.Invoke(() => notice.IsDisposed) && notice.Invoke(() => notice.DialogResult) == DialogResult.None,
                    $"THE CLOCK: while context help is open the countdown must wait ({paused} became {after})");
                var w = notice.Invoke(() => ContextHelp.Open!);
                notice.Invoke(() => PressEscapeIn(w));
                Check(WaitFor(() => notice.Invoke(() => ContextHelp.Open) is null, TimeSpan.FromSeconds(5)), "Escape must close the help");
                Check(WaitFor(() => notice.Invoke(() => notice.SecondsRemainingForTest) < paused, TimeSpan.FromSeconds(3)),
                    "and the countdown must carry on from where it waited");
                results.Add($"{paused}");
            }, TimeSpan.FromSeconds(40));
            notice.Invoke(() => notice.Close());
            return "the update notice's countdown waits while context help is open on one of its buttons, and carries on when it closes";
        }
        finally
        {
            HelpKeyMessageFilter.ModifiersForTest = null;
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
        }
    }

    /// <summary>
    /// GATE GUARD: EVERY CONTROL, MENU ITEM AND TAB HAS CONTEXT HELP OF ITS OWN, AND EVERY MARK IS IN THE MANUAL ONCE.
    ///
    /// <para>The main window with every tab, in all three audio configurations (some controls exist in only one); every
    /// dialog the audits know; the main menus and the tray menu; and the DAW plugin's window. A control added later without
    /// context help fails here, so it can't be forgotten. And the other way: a mark in the manual that nothing uses is a
    /// stale entry, and fails too.</para>
    /// </summary>
    private static string? EveryControlHasContextHelp()
    {
        var html = File.ReadAllText(ContextHelp.ManualPath());
        var missing = new List<string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var controls = 0;
        var items = 0;
        string Where(Control c) => $"{c.FindForm()?.Text ?? "?"} / {ContextHelp.NameOf(c)} ({c.GetType().Name}{(string.IsNullOrEmpty(c.Name) ? "" : " " + c.Name)})";
        void Need(Control c, string scope)
        {
            controls++;
            if (ContextHelp.KeyOf(c) is { } key) used.Add(key);
            else missing.Add($"{scope}: {Where(c)}");
        }
        void NeedItem(ToolStripItem item, string scope)
        {
            items++;
            if (ContextHelp.KeyOfItem(item) is { } key) used.Add(key);
            else missing.Add($"{scope} menu: {ContextHelp.NameOfItem(item)}");
        }

        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        MainForm? form = null;
        try
        {
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var tabs = Require(Require(typeof(MainForm).GetField("mainTabControl", flags), "MainForm.mainTabControl not found").GetValue(form) as TabControl,
                "MainForm.mainTabControl is not a tab control");
            foreach (var configuration in AudioConfigurations.All)
            {
                SetAudioConfigurationForTest(form, configuration);
                InvokeWindowMethodForAltAudit(form, "UpdateBothIndependentVisibility");
                foreach (TabPage page in tabs.TabPages)
                {
                    controls++;
                    if (ContextHelp.KeyOf(page) is { } pageKey) used.Add(pageKey); else missing.Add($"main window: the {page.Text} tab itself");
                }
                var onWindow = EveryFocusableUnder(form).Distinct().ToList();
                Check(onWindow.Count >= 40, $"{configuration.Describe()}: premise - the main window's controls must be found ({onWindow.Count})");
                foreach (var c in onWindow) Need(c, $"main window ({configuration.Describe()})");
            }
            // The parametric EQ's controls, which exist only in that mode.
            ShowEqModeForTest(form, 2);
            var panEq = tabs.TabPages.Cast<TabPage>().First(p => ContextHelp.KeyOf(p) == "pan-eq");
            foreach (var c in EveryFocusableUnder(panEq)) Need(c, "main window (parametric EQ)");
            ShowEqModeForTest(form, 0);
            if (form.MainMenuStrip is { } menuBar)
                foreach (var item in EveryMenuItem(menuBar.Items)) NeedItem(item, "main window's");
            foreach (var item in EveryMenuItem(form.TrayControllerForTest.TrayMenuForTest.Items)) NeedItem(item, "tray");
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            CuePlayer.GloballyMuted = restoreMuted;
        }

        foreach (var (name, make) in DialogFactories())
        {
            // The help's own window: F1 does nothing in it. And the stand-in a hidden copy shows for a message box, which
            // nobody at a screen ever sees.
            if (name is "Context help window" or "Headless question") continue;
            using var dialog = make();
            foreach (var c in EveryFocusableUnder(dialog)) Need(c, $"dialog \"{name}\"");
        }
        // The passwords window lists a box per saved profile; the audits' copy has none, so build one with a profile.
        using (var passwords = ProfilePasswordManagerDialog.Build(StoreWithOneProfileForTest()).Dialog)
            foreach (var c in EveryFocusableUnder(passwords)) Need(c, "dialog \"Profile passwords manager\" (with a profile)");
        using (var plugin = new PluginEditorPanel())
            foreach (var c in EveryFocusableUnder(plugin)) Need(c, "plugin window");
        // Where the plugin's Context help button opens the manual: the whole window's part of it.
        used.Add("plugin.window");

        // One line per control missing, however many configurations it was missed in.
        var distinct = missing.Select(m => System.Text.RegularExpressions.Regex.Replace(m, @" \((WASAPI only|ASIO only|WASAPI and ASIO[^)]*|both[^)]*)\)", ""))
            .Distinct().ToList();
        Check(distinct.Count == 0, $"{distinct.Count} control(s) and menu item(s) have no context help of their own:\n    " + string.Join("\n    ", distinct));

        var badEntries = used.Where(k => HelpManual.CountOf(html, k) != 1 || (HelpManual.EntryText(html, k)?.Length ?? 0) < 20)
            .Select(k => $"{k} (in the manual {HelpManual.CountOf(html, k)} time(s))").ToList();
        Check(badEntries.Count == 0, $"these marks must each be in the manual exactly once, with real words: {string.Join(", ", badEntries)}");
        var stale = HelpManual.Keys(html).Where(k => !used.Contains(k)).ToList();
        Check(stale.Count == 0, $"these marks in the manual belong to nothing in RemSound any more: {string.Join(", ", stale)}");
        return $"{controls} controls and tabs and {items} menu items, across the main window in all three configurations, every dialog, "
             + $"the menus, the tray menu and the plugin window, each with context help of its own; {used.Count} different entries, "
             + "each in the manual exactly once, and none in the manual left over";
    }
}
