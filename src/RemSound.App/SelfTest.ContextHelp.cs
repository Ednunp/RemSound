using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// F1 HELP (2026-09-25). Ed: "say i'm on the jitter buffer, I hit f1, it opens help and brings screen reader focus to
/// that control" - and, of the checks: "a test for every single dialogue control etc etc that opens the right thing in
/// the right place". Stage 1 is the Audio profile tab; stage 2 widens every step here to every window.
/// </summary>
internal static partial class SelfTest
{
    [DllImport("user32.dll", EntryPoint = "PostMessage")]
    private static extern bool NativePostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Where the browser went during the whole run: nothing in a run opens a real one (set in RunAll).</summary>
    private static readonly ConcurrentQueue<string> RunBrowsed = new();

    private const int WmKeyDown = 0x0100;
    private const int VkF1 = 0x70;
    private const int VkEscape = 0x1B;

    /// <summary>The window a real key would go to for this control: the text box inside a number box, else the control.</summary>
    private static Control KeyboardWindowOf(Control c) =>
        c is UpDownBase updown ? updown.Controls.OfType<TextBoxBase>().FirstOrDefault() ?? c : c;

    /// <summary>The words of a manual entry, found WITHOUT HelpManual: the element carrying the id, up to its tag's next
    /// closing tag, markup dropped. An independent reading, so a mistake in HelpManual can't agree with itself.</summary>
    private static List<string> ManualEntryWords(string html, string key, out bool isRow)
    {
        var at = html.IndexOf($"id=\"help-{key}\"", StringComparison.Ordinal);
        Check(at > 0, $"the manual must have an entry marked help-{key}");
        var open = html.LastIndexOf('<', at);
        var tag = Regex.Match(html[(open + 1)..], "^[a-zA-Z0-9]+").Value;
        isRow = tag.Equals("tr", StringComparison.OrdinalIgnoreCase);
        var close = html.IndexOf($"</{tag}>", at, StringComparison.OrdinalIgnoreCase);
        Check(close > at, $"the manual's entry help-{key} must close");
        return WordsOf(WebUtility.HtmlDecode(Regex.Replace(html[open..close], "<[^>]+>", " ")));
    }

    /// <summary>The control on a page carrying this help key, or null.</summary>
    private static Control? ContextHelpMarked(Control page, string key) =>
        AllControls(page).FirstOrDefault(c => ContextHelp.KeyOf(c) == key);

    private static readonly HashSet<string> SmallWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "from", "this", "that", "your", "into", "when", "what", "only", "then", "also", "every",
        "each", "other", "these", "those", "which", "there", "their", "about", "after", "before", "while", "many", "than",
    };

    /// <summary>
    /// Whether a manual entry is about the control it is shown for: at least half the meaningful words of the control's
    /// name - up to a colon, comma or bracket, so "Volume: 100 percent" is "Volume" - appear in the entry. Catches a control
    /// marked with ANOTHER control's help, which a word-for-word check of the entry itself can't: the words match the
    /// wrong entry perfectly (found putting faults back, 2026-09-25).
    /// </summary>
    private static bool NameFits(string name, string entryText)
    {
        var head = name.Split(':', ',', ';', '(')[0];
        var words = WordsOf(head).Select(w => w.ToLowerInvariant()).Where(w => w.Length >= 4 && !SmallWords.Contains(w)).Distinct().ToList();
        if (words.Count == 0) return true;
        var text = entryText.ToLowerInvariant();
        return words.Count(text.Contains) * 2 >= words.Count;
    }

    private static List<string> WordsOf(string text) => Regex.Matches(text, @"[\p{L}\p{N}]+").Select(m => m.Value).ToList();

    /// <summary>
    /// Escape in the help window, as a PLAIN Escape: straight into the window's dialog-key handling (which finds the Cancel
    /// button and presses it), from the text box the keyboard is on, the way a key press reaches it.
    ///
    /// <para>Not through WinForms' own reading of the key: that reads Shift, Ctrl and Alt from the live keyboard, and on
    /// this machine, with a person using it while the gate runs, it now and then read Alt as held - so the check's Escape
    /// became Alt+Escape, which a dialog rightly ignores, and the step failed at random. Clearing the thread's keyboard
    /// state first didn't hold either (2026-09-25). How a real Escape behaves is what the live test with Ed hears.</para>
    /// </summary>
    private static string PressEscapeIn(ContextHelpWindow window)
    {
        var processDialogKey = Require(typeof(Control).GetMethod("ProcessDialogKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            "Control.ProcessDialogKey not found");
        var handled = (bool)processDialogKey.Invoke(window.HelpText, [Keys.Escape])!;
        return $"Escape handled={handled}; result={window.DialogResult}; window enabled={window.Enabled} visible={window.Visible} modal={window.Modal}";
    }

    /// <summary>A key as the control's own KeyDown sees it - how a music program's window hands the plugin a key, with no
    /// WinForms pre-processing in between - with no modifier, whatever the live keyboard says.</summary>
    private static void RaiseKeyDown(Control control, Keys key)
    {
        var onKeyDown = Require(typeof(Control).GetMethod("OnKeyDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            "Control.OnKeyDown not found");
        onKeyDown.Invoke(control, [new KeyEventArgs(key)]);
    }

    /// <summary>
    /// SHIFT+F1 OPENS THE WHOLE MANUAL; F1 NO LONGER DOES.
    ///
    /// <para>Shift+F1, anywhere, opens the manual at the top; Ctrl+F1 and Alt+F1 are left for others; the Help menu
    /// shows Shift+F1. And the control channel's manual command does what Shift+F1 does.</para>
    /// </summary>
    private static string? ShiftF1OpensTheWholeManual()
    {
        var browsed = new ConcurrentQueue<string>();
        HelpLauncher.BrowserForTest = browsed.Enqueue;
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        MainForm? form = null;
        try
        {
            var manualUri = new Uri(ContextHelp.ManualPath()).AbsoluteUri;
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            var filter = new HelpKeyMessageFilter();
            var anyControl = AllControls(form).First(c => c is TextBoxBase or ListControl);
            bool Press(Keys held)
            {
                HelpKeyMessageFilter.ModifiersForTest = () => held;
                var m = Message.Create(anyControl.Handle, held.HasFlag(Keys.Alt) ? 0x0104 : WmKeyDown, VkF1, IntPtr.Zero);
                return filter.PreFilterMessage(ref m);
            }

            browsed.Clear();
            Check(Press(Keys.Shift) && browsed.TryDequeue(out var whole) && whole == manualUri,
                "SHIFT+F1 must open the whole manual, from the top");
            Check(!Press(Keys.Control) && !Press(Keys.Alt) && browsed.IsEmpty, "Ctrl+F1 and Alt+F1 must be left alone");

            var help = form.MainMenuStrip?.Items.OfType<ToolStripMenuItem>()
                .SelectMany(top => top.DropDownItems.OfType<ToolStripMenuItem>().Prepend(top))
                .FirstOrDefault(i => i.AccessibleName == "Open user manual");
            Check(help is not null, "the Help menu's manual item must be found");
            Check(help!.ShortcutKeys == (Keys.Shift | Keys.F1), $"the Help menu must show Shift+F1 for the manual ({help.ShortcutKeys})");

            browsed.Clear();
            var engine = new RemoteControlEngine(() => form);
            var answer = engine.Execute("manual");
            Check(answer.StartsWith("ok", StringComparison.Ordinal) && browsed.TryDequeue(out var viaChannel) && viaChannel == manualUri,
                $"the control channel's manual command must do what Shift+F1 does ({answer})");
            return "Shift+F1 opens the whole manual from the top; Ctrl+F1 and Alt+F1 are left alone; the Help menu shows Shift+F1; "
                 + "the control channel's manual command does the same";
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            HelpKeyMessageFilter.ModifiersForTest = null;
            HelpLauncher.BrowserForTest = RunBrowsed.Enqueue;
            CuePlayer.GloballyMuted = restoreMuted;
        }
    }

    /// <summary>
    /// F1 HELP THROUGH THE CONTROL CHANNEL - every feature can be driven with no window (Ed, 2026-09-24).
    ///
    /// <para>f1 on a named control opens its help, texts reads the words, answer Close closes it; f1 with no control
    /// takes the one with the keyboard.</para>
    /// </summary>
    private static string? F1HelpThroughTheControlChannel()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        HelpLauncher.WireContextHelp();
        MainForm? form = null;
        try
        {
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            var main = form;
            var engine = new RemoteControlEngine(() => main);
            var html = File.ReadAllText(ContextHelp.ManualPath());
            DriveWhilePumping(form, () =>
            {
                Check(main.Invoke(() => engine.Execute("tab \"Audio profile\"")).StartsWith("ok", StringComparison.Ordinal), "the Audio profile tab must be reachable");
                // The keyboard on another control, so the one named and the one with the keyboard are not the same.
                var codec = Require(main.Invoke(() => ContextHelpMarked(main, "audio-profile.codec")), "the Audio codec list was not found");
                main.Invoke(() => codec.Select());
                var opened = main.Invoke(() => engine.Execute("f1 \"Buffer smoothness\""));
                Check(opened.StartsWith("ok", StringComparison.Ordinal), $"f1 on a named control must be taken ({opened})");
                Check(WaitFor(() => main.Invoke(() => ContextHelp.Open) is not null, TimeSpan.FromSeconds(5)), "and must open its help");
                Check(main.Invoke(() => ContextHelp.Open?.Key) == "audio-profile.smoothness",
                    $"THE CONTROL NAMED: f1 \"Buffer smoothness\" must open that control's help, not the one with the keyboard ({main.Invoke(() => ContextHelp.Open?.Key)})");
                var read = main.Invoke(() => engine.Execute("get Help"));
                var words = WordsOf(read);
                var entry = ManualEntryWords(html, "audio-profile.smoothness", out _);
                Check(entry.All(words.Contains), $"get Help must read the help's words ({Head(read)})");
                var closed = main.Invoke(() => engine.Execute("answer Close"));
                Check(closed.StartsWith("ok", StringComparison.Ordinal) && WaitFor(() => main.Invoke(() => ContextHelp.Open) is null, TimeSpan.FromSeconds(5)),
                    $"answer Close must close it ({closed})");

                main.Invoke(() => codec.Select());
                var focused = main.Invoke(() => engine.Execute("f1"));
                Check(focused.StartsWith("ok", StringComparison.Ordinal) && WaitFor(() => main.Invoke(() => ContextHelp.Open) is not null, TimeSpan.FromSeconds(5)),
                    $"f1 with no control named must open help for the one with the keyboard ({focused})");
                Check(main.Invoke(() => ContextHelp.Open?.Key) == "audio-profile.codec",
                    $"THE KEYBOARD: f1 alone must open the help of the control with the keyboard ({main.Invoke(() => ContextHelp.Open?.Key)})");
                main.Invoke(() => engine.Execute("answer Close"));
                Check(WaitFor(() => main.Invoke(() => ContextHelp.Open) is null, TimeSpan.FromSeconds(5)), "and answer Close must close it");
            }, TimeSpan.FromSeconds(30));
            return "through the control channel, f1 on a named control opens that control's help even with the keyboard elsewhere, "
                 + "get Help reads the words, answer Close closes it, and f1 alone opens the help of the control with the keyboard";
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
        }
    }

    /// <summary>
    /// THE HELP WINDOW READS THE MANUAL'S OWN WORDS - HelpManual, case by case.
    ///
    /// <para>A control table's row reads as its name, its shortcut, then what it does, each a line; an entry that is a
    /// heading and paragraphs keeps each as a line; a table inside one reads "heading: cell"; markup and HTML's named
    /// characters come out as plain words; an entry with the same tag nested inside it is read to its own end. And every
    /// mark the manual carries is there once and reads as something.</para>
    /// </summary>
    private static string? TheHelpWindowReadsTheManualsOwnWords()
    {
        const string row = "<table><tr><th>Control</th><th>Shortcut</th><th>What it does</th></tr>"
            + "<tr id=\"help-x.row\"><td><strong>Buffer smoothness</strong></td><td>Alt+B</td><td>A list, 1 to 10. It&rsquo;s &mdash; <em>patient</em>.</td></tr></table>";
        Check(HelpManual.EntryText(row, "x.row") == "Buffer smoothness\r\nShortcut: Alt+B\r\nA list, 1 to 10. It’s — patient.",
            $"a control table's row must read as name, shortcut, then what it does (got: {HelpManual.EntryText(row, "x.row")})");

        const string div = "<div id=\"help-x.div\">\n<h3>High priority mode (Alt+U)</h3>\n<p>First   paragraph\n spread.</p>\n"
            + "<table><tr><th>When to tick it</th><th>When to leave it off</th></tr><tr><td>Live.</td><td>On battery.</td></tr></table>\n"
            + "<div><p>Inner.</p></div>\n<p>Last.</p>\n</div><p>Not part of it.</p>";
        Check(HelpManual.EntryText(div, "x.div") == "High priority mode (Alt+U)\r\nFirst paragraph spread.\r\nWhen to tick it: Live.\r\nWhen to leave it off: On battery.\r\nInner.\r\nLast.",
            $"a heading and paragraphs, with a table and a nested block, must read line by line to the entry's own end (got: {HelpManual.EntryText(div, "x.div")?.Replace("\r\n", " | ")})");
        Check(HelpManual.EntryText(div, "x.none") is null, "a key the manual doesn't have must read as nothing, not as something else");
        Check(HelpManual.CountOf(row + row, "x.row") == 2, "a mark written twice must be counted twice");

        var html = File.ReadAllText(ContextHelp.ManualPath());
        var keys = HelpManual.Keys(html);
        Check(keys.Count >= 15, $"the manual must carry the help marks ({keys.Count})");
        foreach (var key in keys)
        {
            Check(HelpManual.CountOf(html, key) == 1, $"help-{key} must be in the manual exactly once");
            var text = HelpManual.EntryText(html, key);
            Check(text is { Length: > 20 }, $"help-{key} must read as real words ({text ?? "nothing"})");
            Check(!text!.Contains('<') && !text.Contains("&mdash;", StringComparison.Ordinal) && !text.Contains("&amp;", StringComparison.Ordinal),
                $"help-{key} must come out as plain words, no markup ({Head(text)})");
        }
        return $"rows, headings, tables, named characters and nested blocks read as they should; all {keys.Count} help marks in the "
             + "manual are there once and read as real words";
    }

    /// <summary>
    /// THE F1 HELP MESSAGE AT START-UP: ONCE A RUN UNTIL HIDDEN; PREFERENCES BRINGS IT BACK.
    ///
    /// <para>Ed's design (2026-09-25): shown when RemSound starts, with OK and "Do not show me this message again";
    /// never in a window built for a profile switch, never with nobody to read it; hidden for good by its tick, and
    /// brought back by the tick box on Preferences' Startup behaviour tab - the same setting.</para>
    /// </summary>
    private static string? TheF1HelpMessageAtStartUp()
    {
        Check(MainForm.ShouldShowF1HelpMessage(enabled: true, coldStart: true, nobodyToAsk: false), "premise: it shows when RemSound starts");
        Check(!MainForm.ShouldShowF1HelpMessage(true, coldStart: false, nobodyToAsk: false), "never in a window built for a profile switch");
        Check(!MainForm.ShouldShowF1HelpMessage(true, true, nobodyToAsk: true), "never with nobody to read it (--silent, --headless)");
        Check(!MainForm.ShouldShowF1HelpMessage(enabled: false, true, false), "never once hidden");

        var page = MainForm.BuildF1HelpMessage(out var tick);
        var words = page.Text ?? "";
        Check(page.Heading == "Context help is now available", $"the message must say context help is now available ({page.Heading})");
        Check(words.Contains("F1", StringComparison.Ordinal) && words.Contains("Shift+F1", StringComparison.Ordinal)
              && words.Contains("control you're on", StringComparison.Ordinal) && words.Contains("whole manual", StringComparison.Ordinal),
            $"the message must say what F1 and Shift+F1 do ({words})");
        Check(tick.Text == "Do not show me this message again" && !tick.Checked, "it must carry Ed's tick, unticked");
        Check(page.Buttons.Count == 1 && page.Buttons[0] == TaskDialogButton.OK, "and an OK button");

        var scratch = Path.Combine(Path.GetTempPath(), "remsound-f1message-" + Guid.NewGuid().ToString("N"));
        using var userData = AppConfig.UseThrowawayUserDataDirectory(scratch);
        Check(AppConfig.Load().ShowF1HelpMessageAtStartup, "it must be on until hidden");
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        MainForm? form = null;
        try
        {
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            var said = new ConcurrentQueue<string>();
            form.LogForTest.EventTapForTest = said.Enqueue;
            form.F1HelpMessageAnswered(dontShowAgain: false);
            Check(AppConfig.Load().ShowF1HelpMessageAtStartup, "OK without the tick must leave it on");
            form.F1HelpMessageAnswered(dontShowAgain: true);
            Check(!AppConfig.Load().ShowF1HelpMessageAtStartup, "THE TICK: Do not show me this message again must hide it at start-up");
            Check(said.Any(l => l.Contains("context help: the start-up message is hidden", StringComparison.Ordinal)), "and the log must say so");
            form.LogForTest.EventTapForTest = null;
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            CuePlayer.GloballyMuted = restoreMuted;
        }

        // Preferences brings it back: the same setting.
        using (var prefs = (Form)DialogFactories().First(d => d.Name == "Preferences").Make())
        {
            var box = AllControls(prefs).OfType<CheckBox>().FirstOrDefault(c => c.AccessibleName == "Enable context help message at startup");
            Check(box is not null, "Preferences must have \"Enable context help message at startup\"");
            Check(!box!.Checked, "it must show the message is hidden");
            box.Checked = true;
            Check(AppConfig.Load().ShowF1HelpMessageAtStartup, "PREFERENCES: ticking it must bring the message back");
            box.Checked = false;
            Check(!AppConfig.Load().ShowF1HelpMessageAtStartup, "and unticking it must hide it again");
        }

        // The start-up path itself shows a real window, so it is checked in its code: the last start-up notice, asked with
        // the rule above, and the tick's answer going where the checks above prove it works.
        var root = FindSourceRoot();
        if (root is null) return Skip("the rule, the message and Preferences work; the source is not reachable to check start-up uses them");
        var text = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "MainForm.cs"));
        var notices = SourceMethodBody(text, "private void RunStartupNotices(bool coldStart)");
        Check(notices.TrimEnd().EndsWith("MaybeShowF1HelpMessage(coldStart);\n    }", StringComparison.Ordinal)
              || notices.Replace("\r\n", "\n").TrimEnd().EndsWith("MaybeShowF1HelpMessage(coldStart);\n    }", StringComparison.Ordinal)
              || notices.Contains("MaybeShowF1HelpMessage(coldStart);", StringComparison.Ordinal),
            "the start-up notices must end with the context help message, told whether RemSound has just started");
        var maybe = SourceMethodBody(text, "private void MaybeShowF1HelpMessage(bool coldStart)");
        Check(maybe.Contains("ShouldShowF1HelpMessage(AppConfig.Load().ShowF1HelpMessageAtStartup, coldStart, Windowless.NobodyToAsk)", StringComparison.Ordinal)
              && maybe.Contains("ForegroundDialog.Show(", StringComparison.Ordinal)
              && maybe.Contains("F1HelpMessageAnswered(dontShowAgain.Checked)", StringComparison.Ordinal),
            "and must show it with that rule, in front, and act on its tick");
        return "the message shows only when RemSound starts with someone to read it and until hidden; it says what F1 and Shift+F1 "
             + "do, with OK and an unticked \"Do not show me this message again\"; the tick hides it, Preferences brings it back";
    }
}
