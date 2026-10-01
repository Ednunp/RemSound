using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// LAYOUT FOR SIGHTED PEOPLE (Ed, 2026-09-28): "make sure the UI is nice and modern for a sighted person, nicely formatted
/// etc... we should put something in the tests about this as well."
///
/// <para>The audit that day (ui-audit.md in its own copy) found the plugin's Status box cut in half inside every DAW, a
/// password dialog with no visible OK button, Packet size off the edge of the main window, labels floating halfway down
/// tall lists, and nothing at all sized for 150 % or 200 % display scaling. None of it could be heard, so none of it had
/// ever been noticed. These steps measure it instead: every window the other audits reach (the dialog list, every tab of
/// the main window at its opening and minimum sizes, every Preferences and Service profile tab, the plugin's panel in the
/// frame a DAW shows) is built, laid out and walked, control by control. Nothing is shown and nothing is heard: making
/// the window handles and laying them out is enough to measure them.</para>
///
/// <para>The checks, by the audit's letters. A: nothing is cut off by its container (scrolling containers may overflow the
/// way they scroll). B: every window fits a 1366 x 768 screen at 100 %, and a 1920 x 1080 one at 150 and 200 %. C: no two
/// visible controls overlap. D: text fits its control - fixed-size labels, buttons and tick boxes, menu bars, tab strips,
/// lists (the main window's device lists filled with 60-character sample names, so the answer does not depend on this
/// PC's sound cards), drop-downs, number boxes and read-only text boxes that do not scroll. E: all of that at 150 % and
/// 200 %. F: the plugin asks its host for the frame its panel needs. G: a label and its control are shown or hidden
/// together. H: a label beside a tall control is level with its top. I: text contrast in both colour themes, a greyed
/// out list's selected row included. J: every resizable window is usable at its minimum size. K: a dialog's commit
/// buttons share one row at the bottom right, OK before Cancel. M: the help page's stylesheet. L is not a check:
/// pictures of every window, when asked for, on a hidden desktop only.</para>
///
/// <para>Every check can fail, and proves it each run: a canary window with one planted fault per check is walked by the
/// same code, and each step requires its own fault to be found and named before it believes a clean result.</para>
///
/// <para><b>How 150 % and 200 % are checked, and how faithful that is.</b> This PC's display is 100 %, and changing
/// Windows' scaling is a system change, so E starts a RemSound self-test process for each scale with the app font at
/// 13.5 pt or 18 pt instead of 9 pt (at 96 DPI those are exactly the pixel heights 9 pt has at 144 and 192 DPI), the
/// fonts the code sizes itself and the menu font scaled the same, and <see cref="Dpi.EmulatedScaleForTest"/> set so every
/// size the code sets through <see cref="Dpi"/> grows by the same factor. RemSound is SystemAware with no
/// AutoScaleDimensions, so that is what a real 150 % display does to it. What it does NOT grow is Windows' own drawing:
/// tick-box squares, scroll bars, drop-down and spin-box arrows. The checks add that difference back where it matters
/// (a tick box needs its square's extra width, a list its scroll bar's, a drop-down its arrow's), so they err on the
/// strict side. Title bars are not checked, and nothing here has been seen on a real high-DPI screen.</para>
/// </summary>
internal static partial class SelfTest
{
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int LayoutGetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetThreadDesktop")] private static extern IntPtr LayoutGetThreadDesktop(uint threadId);
    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")] private static extern uint LayoutGetCurrentThreadId();
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode)]
    private static extern bool LayoutGetUserObjectInformation(IntPtr obj, int index, StringBuilder? info, int length, out int needed);
    [DllImport("user32.dll", EntryPoint = "PrintWindow")] private static extern bool LayoutPrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    private const int LayoutGwlStyle = -16;
    private const int LayoutWsVisible = 0x10000000;

    private static readonly MethodInfo LayoutCreateControl =
        typeof(Control).GetMethod("CreateControl", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(bool)], null)!;
    private static readonly MethodInfo LayoutOnLoad = typeof(Form).GetMethod("OnLoad", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>This process is one of the scaled or re-themed processes a layout step started (it checks, its parent
    /// judges).</summary>
    private static bool layoutChild;

    /// <summary>Where L puts its pictures (<c>--ui-pictures</c>), or null: none are made unless asked for.</summary>
    private static string? layoutPictures;

    private static LayoutSurvey? layoutSurvey;

    /// <summary>The main window's device lists get these, so D's answer does not depend on this PC's sound cards. Real
    /// names run this long: "Microphone Array (Intel Smart Sound Technology for Digital Microphones)" is 69.</summary>
    internal static readonly string[] LayoutSampleDeviceNames =
    [
        "Speakers (Realtek High Definition Audio with Dolby Atmos 7.1)",
        "Microphone Array (Intel Smart Sound Technology for Digital Mic)",
        "Line 3/4 (Focusrite Scarlett 18i20 3rd Gen USB Audio Driver)",
    ];

    private static void ConfigureLayout(string[] args)
    {
        layoutChild = ValueAfter(args, "--layout-scale") is not null || ValueAfter(args, "--layout-theme") is not null;
        layoutPictures = ValueAfter(args, "--ui-pictures");
        layoutSurvey = null;
    }

    // ============================================================================================================
    // The survey: every window built once, walked once, every check's findings kept apart by letter.
    // ============================================================================================================

    private sealed record LayoutFinding(char Check, string Where, string What)
    {
        public override string ToString() => $"{Where}: {What}";
    }

    private sealed class LayoutSurvey
    {
        public readonly List<LayoutFinding> Findings = [];
        public readonly List<LayoutFinding> Canary = [];
        public readonly List<string> Advisories = [];
        public readonly List<string> Errors = [];
        public readonly HashSet<string> Windows = new(StringComparer.Ordinal);
        public readonly List<string> Views = [];
        public int Controls, Resizable, TallRows, ContrastPairs, ButtonRows, OrderedButtonRows, SampleLists, MnemonicLabels, TextChecks, DisabledSelectedLists;
        public float Scale = 1f;
        public bool Dark;
        public Rectangle Screen;
        public string PluginFrame = "";
        public int DialogsExpected;
    }

    /// <summary>The screen a window must fit (check B): 1366 x 768 less a taskbar at 100 %, 1920 x 1080 less one at
    /// 150 % and 200 %.</summary>
    private static Rectangle LayoutScreenFor(float scale) =>
        scale <= 1.01f ? new Rectangle(0, 0, 1366, 728) : new Rectangle(0, 0, 1920, 1040);

    private static LayoutSurvey Survey() => layoutSurvey ??= RunLayoutSurvey();

    private static LayoutSurvey RunLayoutSurvey()
    {
        var s = new LayoutSurvey { Scale = Dpi.EmulatedScaleForTest, Dark = Theme.IsDark };
        s.Screen = LayoutScreenFor(s.Scale);
        var restoreArea = Dpi.WorkingAreaForTest;
        Dpi.WorkingAreaForTest = s.Screen;
        var restoreMuted = CuePlayer.GloballyMuted;
        var restoreChecks = CheckSoundService.Suppressed;
        CuePlayer.GloballyMuted = true;
        CheckSoundService.Suppressed = true;
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-layout-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var scope = AppConfig.UseThrowawayUserDataDirectory(scratch);
            var factories = DialogFactories();
            s.DialogsExpected = factories.Length;
            foreach (var (name, make) in factories)
            {
                Form? form = null;
                try { form = make(); }
                catch (Exception ex) { s.Errors.Add($"{name} could not be built: {ex.GetType().Name}: {ex.Message}"); continue; }
                try { SurveyWindow(s, name, form, s.Findings); }
                catch (Exception ex) { s.Errors.Add($"{name}: {ex.GetType().Name}: {ex.Message}"); }
                finally { try { form.Dispose(); } catch { /* audit copy */ } }
            }
            SurveyMainWindow(s);
            SurveyPluginFrame(s);
            SurveyCanaries(s);
        }
        finally
        {
            Dpi.WorkingAreaForTest = restoreArea;
            CuePlayer.GloballyMuted = restoreMuted;
            CheckSoundService.Suppressed = restoreChecks;
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); } catch { /* throwaway */ }
        }
        return s;
    }

    /// <summary>Make every handle and lay the window out, as showing it would, without showing it.</summary>
    private static void LayoutPrepare(Form form)
    {
        LayoutCreateControl.Invoke(form, [true]);
        try { LayoutOnLoad.Invoke(form, [EventArgs.Empty]); }
        catch (TargetInvocationException) { /* a Load handler that needs the window on screen; the layout is still made */ }
        form.PerformLayout();
        form.PerformLayout();
    }

    private static void LayoutSettle(Control root)
    {
        LayoutCreateControl.Invoke(root, [true]);
        root.PerformLayout();
        if (root.FindForm() is { } form) { form.PerformLayout(); form.PerformLayout(); }
    }

    private static bool LayoutOwnVisible(Control c) =>
        c.IsHandleCreated && (LayoutGetWindowLong(c.Handle, LayoutGwlStyle) & LayoutWsVisible) != 0;

    /// <summary>Containers the walk goes into. Never the private parts of a number box or a drop-down.</summary>
    private static bool LayoutIsContainer(Control c) =>
        c is Form or Panel or GroupBox or TabControl or SplitContainer or UserControl && c is not UpDownBase;

    /// <summary>The visible controls under a window, as a person sees them: only the selected page of a tab control.</summary>
    private static List<Control> LayoutVisibleControls(Control root)
    {
        var found = new List<Control>();
        void Walk(Control parent)
        {
            IEnumerable<Control> kids = parent is TabControl tc
                ? (tc.SelectedTab is { } page ? [page] : [])
                : parent.Controls.Cast<Control>();
            foreach (var c in kids)
            {
                if (!LayoutOwnVisible(c)) continue;
                found.Add(c);
                if (LayoutIsContainer(c)) Walk(c);
            }
        }
        Walk(root);
        return found;
    }

    private static string LayoutDescribe(Control c)
    {
        var name = !string.IsNullOrWhiteSpace(c.AccessibleName) ? c.AccessibleName : (c.Text ?? "").Replace("&", "");
        name = name.Replace("\r", " ").Replace("\n", " ");
        if (name.Length > 60) name = name[..57] + "...";
        if (name.Length > 0) return $"{c.GetType().Name} \"{name}\"";
        // A panel with no name of its own is known by what it holds.
        var first = c.Controls.Cast<Control>().FirstOrDefault();
        return first is null ? c.GetType().Name : $"{c.GetType().Name} holding {LayoutDescribe(first)}";
    }

    private static Rectangle LayoutScreenRect(Control c) =>
        c.Parent is { } p ? p.RectangleToScreen(c.Bounds) : c.RectangleToScreen(c.ClientRectangle);

    /// <summary>What a real display adds to a control's own Windows-drawn parts, which the emulation does not grow.</summary>
    private static int LayoutGlyphSlack(Control c, float k)
    {
        if (k <= 1.001f) return 0;
        return c is CheckBox or RadioButton ? (int)Math.Ceiling(13 * (k - 1)) : 0;
    }

    private static int LayoutScrollBarSlack(float k) =>
        k <= 1.001f ? 0 : (int)Math.Ceiling(SystemInformation.VerticalScrollBarWidth * (k - 1));

    // ------------------------------------------------------------------------------------------------------------

    private static void SurveyWindow(LayoutSurvey s, string name, Form form, List<LayoutFinding> into)
    {
        LayoutPrepare(form);
        s.SampleLists += LayoutFeedSampleDeviceNames(form);
        s.Windows.Add(name);
        CheckWindowFitsScreen(s, name, form, into);
        var tabs = LayoutVisibleControls(form).OfType<TabControl>().FirstOrDefault();
        var views = new List<(string Where, Action Select)>();
        if (tabs is null) views.Add((name, () => { }));
        else
            for (var i = 0; i < tabs.TabCount; i++)
            {
                var index = i;
                views.Add(($"{name} / {tabs.TabPages[i].Text} tab", () =>
                {
                    tabs.SelectedIndex = index;
                    LayoutSettle(tabs.TabPages[index]);
                }));
            }
        foreach (var (where, select) in views)
        {
            select();
            InspectView(s, form, where, into);
        }
        CheckButtonRow(s, name, form, into);
        CheckMinimumSize(s, name, form, views, into);
    }

    private static void CheckWindowFitsScreen(LayoutSurvey s, string where, Form form, List<LayoutFinding> into)
    {
        if (form.Width > s.Screen.Width || form.Height > s.Screen.Height)
            into.Add(new('B', where, $"the window is {form.Width} x {form.Height}, bigger than the {s.Screen.Width} x {s.Screen.Height} "
                                     + $"working area of the smallest screen it must fit at {s.Scale * 100:0} %"));
    }

    private static void CheckMinimumSize(LayoutSurvey s, string name, Form form, List<(string Where, Action Select)> views, List<LayoutFinding> into)
    {
        if (form.FormBorderStyle is not (FormBorderStyle.Sizable or FormBorderStyle.SizableToolWindow)) return;
        if (!form.TopLevel) return;
        s.Resizable++;
        if (form.MinimumSize.Width <= 0 || form.MinimumSize.Height <= 0)
        {
            into.Add(new('J', name, "the window can be resized but has no minimum size, so it can be dragged too small to use"));
            return;
        }
        var size = form.Size;
        try
        {
            form.Size = form.MinimumSize;
            form.PerformLayout();
            foreach (var (where, select) in views)
            {
                select();
                foreach (var c in LayoutVisibleControls(form)) CheckClipped(s, 'J', $"{where} at its minimum size {form.Width} x {form.Height}", c, into);
            }
        }
        finally
        {
            form.Size = size;
            form.PerformLayout();
        }
    }

    /// <summary>Checks A, C, D, G, H and I on one view of a window (one tab), and counts what it walked.</summary>
    private static void InspectView(LayoutSurvey s, Form form, string where, List<LayoutFinding> into)
    {
        s.Views.Add(where);
        var visible = LayoutVisibleControls(form);
        s.Controls += visible.Count;
        foreach (var c in visible)
        {
            CheckClipped(s, 'A', where, c, into);
            CheckTextFits(s, where, c, into);
            CheckContrast(s, where, c, into);
        }
        // C: siblings that overlap.
        foreach (var parent in visible.Where(LayoutIsContainer).Prepend(form))
        {
            var kids = parent.Controls.Cast<Control>().Where(k => k is not TabPage && LayoutOwnVisible(k)).ToList();
            for (var a = 0; a < kids.Count; a++)
                for (var b = a + 1; b < kids.Count; b++)
                {
                    var both = Rectangle.Intersect(kids[a].Bounds, kids[b].Bounds);
                    if (both.Width > 0 && both.Height > 0)
                        into.Add(new('C', where, $"{LayoutDescribe(kids[a])} and {LayoutDescribe(kids[b])} overlap by {both.Width} x {both.Height} "
                                                 + $"in their {LayoutDescribe(parent)}"));
                }
        }
        // G and H: every label that names a control, visible or not, under a container a person can see.
        var containers = visible.Where(LayoutIsContainer).Prepend(form).ToList();
        foreach (var label in containers.SelectMany(p => p.Controls.Cast<Control>()).OfType<MnemonicLabel>())
        {
            if (label.MnemonicTarget is not { } target) continue;
            s.MnemonicLabels++;
            var labelShown = LayoutOwnVisible(label);
            var targetShown = LayoutOwnVisible(target);
            for (var up = target.Parent; up is not null && up != label.Parent && targetShown; up = up.Parent)
                targetShown = LayoutOwnVisible(up);
            if (labelShown && !targetShown)
                into.Add(new('G', where, $"{LayoutDescribe(label)} is shown, but its {LayoutDescribe(target)} is hidden - a label with nothing beside it"));
            else if (!labelShown && targetShown && target.Parent is not null)
                into.Add(new('G', where, $"{LayoutDescribe(target)} is shown without its label {LayoutDescribe(label)}"));
            if (!labelShown || !targetShown) continue;

            // H: beside a control more than twice its own height, the label sits level with that control's top.
            var lr = LayoutScreenRect(label);
            var tr = LayoutScreenRect(target);
            var beside = tr.Left >= lr.Right - 2 && tr.Top < lr.Bottom && tr.Bottom > lr.Top;
            if (!beside || tr.Height <= 2 * lr.Height) continue;
            s.TallRows++;
            var drop = lr.Top - tr.Top;
            var most = (int)Math.Ceiling(8 * s.Scale);
            if (drop < 0 || drop > most)
                into.Add(new('H', where, $"{LayoutDescribe(label)} sits {drop} px below the top of its {tr.Height} px tall {LayoutDescribe(target)} "
                                         + $"(0 to {most} is level with it)"));
        }
    }

    /// <summary>A: cut off by its container. A scrolling container may overflow the way it is scrolling.</summary>
    private static void CheckClipped(LayoutSurvey s, char check, string where, Control c, List<LayoutFinding> into)
    {
        if (c.Parent is not { } p || p is TabControl) return;
        var room = p.ClientRectangle;
        var r = c.Bounds;
        bool scrollsAcross = false, scrollsDown = false;
        if (p is ScrollableControl { AutoScroll: true } sc)
        {
            scrollsAcross = sc.HorizontalScroll.Visible;
            scrollsDown = sc.VerticalScroll.Visible;
        }
        var right = r.Right + LayoutGlyphSlack(c, s.Scale) - room.Width;
        var parts = new List<string>();
        if (!scrollsAcross && right > 1) parts.Add($"{right} px off the right");
        if (!scrollsAcross && -r.Left > 1) parts.Add($"{-r.Left} px off the left");
        if (!scrollsDown && r.Bottom - room.Height > 1) parts.Add($"{r.Bottom - room.Height} px off the bottom");
        if (!scrollsDown && -r.Top > 1) parts.Add($"{-r.Top} px off the top");
        if (parts.Count > 0)
            into.Add(new(check, where, $"{LayoutDescribe(c)} is cut off by its {LayoutDescribe(p)}: {string.Join(", ", parts)}"));
    }

    private static int LayoutTextWidth(string text, Font font) =>
        TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;

    /// <summary>D: the words fit the control they are in.</summary>
    private static void CheckTextFits(LayoutSurvey s, string where, Control c, List<LayoutFinding> into)
    {
        var k = s.Scale;
        void Add(string what) => into.Add(new('D', where, $"{LayoutDescribe(c)} {what}"));
        // Squeezed to nothing: a box or list with no room to show anything at all.
        if (c is TextBoxBase or ListControl or Label or ButtonBase && (c.Width < 4 || c.Height < 4) && (c.Text ?? "").Length + (c is ListBox l0 ? l0.Items.Count : 0) > 0)
            Add($"has been squeezed to {c.Width} x {c.Height}, and shows nothing");
        switch (c)
        {
            case Label label when (label.Text ?? "").Length > 0:
            {
                s.TextChecks++;
                if (!label.AutoSize)
                {
                    var needs = label.GetPreferredSize(new Size(label.Width, 0));
                    if (needs.Height > label.Height + 1 || needs.Width > label.Width + 1)
                        Add($"is a fixed {label.Width} x {label.Height} but its words need {needs.Width} x {needs.Height}");
                }
                else
                {
                    var needs = label.GetPreferredSize(label.MaximumSize.Width > 0 ? new Size(label.MaximumSize.Width, 0) : Size.Empty);
                    if (label.Width + 1 < needs.Width || label.Height + 1 < needs.Height)
                        Add($"has been squeezed to {label.Width} x {label.Height}; its words need {needs.Width} x {needs.Height}");
                }
                break;
            }
            case ButtonBase button when (button.Text ?? "").Length > 0:
            {
                s.TextChecks++;
                var needs = button.GetPreferredSize(Size.Empty);
                // A tick box that sizes itself grows by its bigger square on a real display; whether there is room for that is
                // check A's question. One of a fixed size needs the room inside itself.
                var width = needs.Width + (button.AutoSize ? 0 : LayoutGlyphSlack(button, k));
                if (button.Width + 1 < width || button.Height + 1 < needs.Height)
                    Add($"is {button.Width} x {button.Height}; its words need {width} x {needs.Height}");
                break;
            }
            case ListBox list when !list.HorizontalScrollbar && list.Items.Count > 0:
            {
                s.TextChecks++;
                var tick = list is CheckedListBox ? 18 + (int)Math.Ceiling(13 * (k - 1)) : 0;
                var scrolls = list.Items.Count * Math.Max(1, list.ItemHeight) > list.ClientSize.Height;
                var room = list.ClientSize.Width - 4 - tick - (scrolls ? LayoutScrollBarSlack(k) : 0);
                var widest = list.Items.Cast<object>().Select(i => list.GetItemText(i) ?? "").OrderByDescending(t => LayoutTextWidth(t, list.Font)).First();
                var needs = LayoutTextWidth(widest, list.Font);
                if (needs > room) Add($"has {room} px for its items, and \"{widest}\" needs {needs}");
                break;
            }
            case ComboBox combo when combo.Items.Count > 0:
            {
                s.TextChecks++;
                var arrow = (int)Math.Ceiling(SystemInformation.VerticalScrollBarWidth * k);
                var widest = combo.Items.Cast<object>().Select(i => combo.GetItemText(i) ?? "").OrderByDescending(t => LayoutTextWidth(t, combo.Font)).First();
                var needs = LayoutTextWidth(widest, combo.Font);
                var room = combo.Width - arrow - 8;
                if (needs > room) Add($"has {room} px beside its arrow, and \"{widest}\" needs {needs}");
                var listRoom = combo.DropDownWidth - 6 - (combo.Items.Count > combo.MaxDropDownItems ? arrow : 0);
                if (needs > listRoom) Add($"opens a {combo.DropDownWidth} px list, and \"{widest}\" needs {needs}");
                break;
            }
            case NumericUpDown number:
            {
                s.TextChecks++;
                var format = "F" + number.DecimalPlaces.ToString(CultureInfo.InvariantCulture);
                var widest = new[] { number.Minimum, number.Maximum }.Select(v => v.ToString(format, CultureInfo.CurrentCulture))
                    .OrderByDescending(t => LayoutTextWidth(t, number.Font)).First();
                var needs = LayoutTextWidth(widest, number.Font);
                var room = number.Width - (int)Math.Ceiling(SystemInformation.VerticalScrollBarWidth * k) - 8;
                if (needs > room) Add($"has {room} px beside its arrows, and \"{widest}\" needs {needs}");
                break;
            }
            case TextBox { Multiline: true, ScrollBars: ScrollBars.None } box when box.TextLength > 0:
            {
                s.TextChecks++;
                var width = Math.Max(1, box.ClientSize.Width - 6);
                var needs = TextRenderer.MeasureText(box.Text, box.Font, new Size(width, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                if (needs.Height > box.ClientSize.Height + 2)
                    Add($"shows {box.ClientSize.Height} px of words that need {needs.Height}, and it has no scroll bar");
                break;
            }
            case TabControl { Multiline: false } tabs when tabs.TabCount > 0 && tabs.IsHandleCreated:
            {
                s.TextChecks++;
                var last = tabs.GetTabRect(tabs.TabCount - 1);
                if (last.Right > tabs.ClientSize.Width - 2)
                    Add($"cannot show all {tabs.TabCount} tabs: the last ends at {last.Right} px in a {tabs.ClientSize.Width} px strip, so some hide behind arrows");
                break;
            }
            case ToolStrip strip:
            {
                s.TextChecks++;
                foreach (ToolStripItem item in strip.Items)
                {
                    if (!item.Available) continue;
                    if (item.Placement != ToolStripItemPlacement.Main || item.Bounds.Right > strip.DisplayRectangle.Right + 1)
                        into.Add(new('D', where, $"the menu bar has no room for \"{(item.Text ?? "").Replace("&", "")}\""));
                }
                break;
            }
        }
    }

    // ---- I: contrast -------------------------------------------------------------------------------------------

    private static double LayoutLuminance(Color c)
    {
        static double Channel(int v)
        {
            var x = v / 255.0;
            return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    internal static double LayoutContrast(Color a, Color b)
    {
        var la = LayoutLuminance(a);
        var lb = LayoutLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static Color LayoutEffectiveBack(Control c)
    {
        for (var x = c; x is not null; x = x.Parent)
            if (x.BackColor.A == 255) return x.BackColor;
        return SystemColors.Control;
    }

    private static void CheckContrast(LayoutSurvey s, string where, Control c, List<LayoutFinding> into)
    {
        var textual = c is Label or ButtonBase or GroupBox or ListBox or TextBoxBase or NumericUpDown or ComboBox;
        if (!textual) return;
        if (c is ListBox { Enabled: false, SelectedIndex: >= 0 } disabledList)
        {
            // Finding 10 (Ed agreed, 2026-09-28): Windows draws a disabled list's selected row as grey on the blue highlight,
            // unreadable, so a list that can be greyed out with a row chosen draws its own rows while it is.
            s.DisabledSelectedLists++;
            if (disabledList is not ReadableWhenDisabledListBox || disabledList.DrawMode == DrawMode.Normal)
            {
                var r = LayoutContrast(SystemColors.GrayText, SystemColors.Highlight);
                into.Add(new('I', where, $"{LayoutDescribe(disabledList)} is greyed out with a row selected, and Windows draws that row grey on blue "
                                         + $"at {r:0.00}:1 - it must draw its own rows while disabled (finding 10)"));
                return;
            }
            foreach (var selected in new[] { true, false })
            {
                var (rowFore, rowBack) = ReadableWhenDisabledListBox.DisabledColours(disabledList, selected);
                var rowRatio = LayoutContrast(rowFore, rowBack);
                s.ContrastPairs++;
                if (rowRatio < 4.5)
                    into.Add(new('I', where, $"{LayoutDescribe(disabledList)} greyed out: its {(selected ? "selected" : "other")} rows are {LayoutHex(rowFore)} on "
                                             + $"{LayoutHex(rowBack)}, {rowRatio:0.00}:1, below 4.5:1 ({(s.Dark ? "dark" : "light")} theme)"));
            }
            return;
        }
        if (!c.Enabled) return;
        if (c is Label or ButtonBase or GroupBox && string.IsNullOrWhiteSpace(c.Text)) return;
        s.ContrastPairs++;
        var fore = c.ForeColor;
        var back = LayoutEffectiveBack(c);
        var large = c.Font.SizeInPoints >= 18 * s.Scale || (c.Font.Bold && c.Font.SizeInPoints >= 14 * s.Scale);
        var least = large ? 3.0 : 4.5;
        var ratio = LayoutContrast(fore, back);
        if (ratio < least)
            into.Add(new('I', where, $"{LayoutDescribe(c)}: text {LayoutHex(fore)} on {LayoutHex(back)} is {ratio:0.00}:1, below {least}:1 "
                                     + $"({(s.Dark ? "dark" : "light")} theme)"));
        // An enabled list's selected row is Windows' own highlight pair (white on the system blue, 4.5:1), which RemSound
        // does not choose, so it is not judged here.
    }

    private static string LayoutHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // ---- K: the commit buttons ---------------------------------------------------------------------------------

    private static void CheckButtonRow(LayoutSurvey s, string name, Form form, List<LayoutFinding> into)
    {
        var buttons = new[] { form.AcceptButton as Control, form.CancelButton as Control }
            .Where(b => b is not null && LayoutOwnVisible(b) && LayoutAncestorsShown(b, form)).Cast<Control>().Distinct().ToList();
        if (buttons.Count == 0) return;
        s.ButtonRows++;
        var client = form.RectangleToScreen(form.ClientRectangle);
        var rects = buttons.Select(LayoutScreenRect).ToList();
        if (rects.Count == 2 && !(rects[0].Top < rects[1].Bottom && rects[1].Top < rects[0].Bottom))
            into.Add(new('K', name, $"{LayoutDescribe(buttons[0])} and {LayoutDescribe(buttons[1])} are not on one row"));
        // The row they are in, as far as it goes: a dialog may keep a button of its own after them in the same row (the
        // order is Ed's decision), and the row is what should end at the right edge.
        var rowTop = rects.Min(r => r.Top);
        var rowBottom = rects.Max(r => r.Bottom);
        var rightmost = buttons.SelectMany(b => b.Parent?.Controls.Cast<Control>() ?? [])
            .Where(b => b is ButtonBase && LayoutOwnVisible(b)).Select(LayoutScreenRect)
            .Where(r => r.Top < rowBottom && r.Bottom > rowTop).Max(r => r.Right);
        var gap = client.Right - rightmost;
        // Right-aligned: the house 12 px padding, plus the row's and the button's own 3 px margins, and a little over.
        var reach = (int)Math.Ceiling(24 * s.Scale);
        if (gap > reach)
            into.Add(new('K', name, $"the commit buttons end {gap} px from the right edge - right-aligned is within {reach}"));
        var most = (int)Math.Ceiling(16 * s.Scale);
        if (rects.Min(r => r.Top) < client.Top + client.Height * 3 / 4)
            s.Advisories.Add($"{name}: the commit buttons are not in the bottom quarter of the window (advisory)");
        // OK before Cancel, as Windows orders them, on screen and for Tab alike (Ed, 2026-09-28; finding 11).
        if (buttons.Count == 2 && form.AcceptButton is Control ok && form.CancelButton is Control cancel && ok != cancel)
        {
            s.OrderedButtonRows++;
            if (LayoutScreenRect(ok).Left > LayoutScreenRect(cancel).Left)
                into.Add(new('K', name, $"{LayoutDescribe(cancel)} comes before {LayoutDescribe(ok)} on screen - Windows puts OK first, then Cancel"));
            var okAt = LayoutTabPosition(form, ok);
            var cancelAt = LayoutTabPosition(form, cancel);
            if (okAt < 0 || cancelAt < 0 || okAt > cancelAt)
                into.Add(new('K', name, $"{LayoutDescribe(cancel)} comes before {LayoutDescribe(ok)} for Tab (places {cancelAt} and {okAt}) - OK first, then Cancel"));
        }
        // Margins: the content's outer edges, 9 to 16 px in from the window's edges (advisory; RemSound mostly uses 12).
        var content = LayoutVisibleControls(form).Where(c => c is not (Panel or TabControl or GroupBox) || c is TabControl).ToList();
        if (content.Count > 0)
        {
            var left = content.Min(c => LayoutScreenRect(c).Left) - client.Left;
            var right = client.Right - content.Max(c => LayoutScreenRect(c).Right);
            var lo = (int)Math.Floor(9 * s.Scale);
            if (left < lo || left > most || right < lo || right > most)
                s.Advisories.Add($"{name}: content sits {left} px from the left edge and {right} px from the right ({lo} to {most} is the house margin; advisory)");
        }
    }

    /// <summary>Where <paramref name="target"/> comes as Tab moves through the window from its start (Windows' own walk,
    /// which follows each container's TabIndex), or -1.</summary>
    private static int LayoutTabPosition(Form form, Control target)
    {
        var at = 0;
        for (var c = form.GetNextControl(null, true); c is not null; c = form.GetNextControl(c, true), at++)
            if (c == target) return at;
        return -1;
    }

    private static bool LayoutAncestorsShown(Control c, Control top)
    {
        for (var up = c.Parent; up is not null && up != top; up = up.Parent)
        {
            if (!LayoutOwnVisible(up)) return false;
            if (up.Parent is TabControl tc && tc.SelectedTab != up) return false;
        }
        return true;
    }

    // ---- the main window ---------------------------------------------------------------------------------------

    private static void SurveyMainWindow(LayoutSurvey s)
    {
        MainForm form;
        try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
        catch (Exception ex) { s.Errors.Add($"the main window could not be built: {ex.GetType().Name}: {ex.Message}"); return; }
        try
        {
            LayoutScaleMenus(form);
            LayoutPrepare(form);
            s.SampleLists += LayoutFeedSampleDeviceNames(form);
            form.PerformLayout();
            const string name = "Main window";
            s.Windows.Add(name);
            CheckWindowFitsScreen(s, name, form, s.Findings);
            var tabs = Require(FieldOf<TabControl>(form, "mainTabControl"), "the main window's mainTabControl was not found");
            var views = new List<(string Where, Action Select)>();
            for (var i = 0; i < tabs.TabCount; i++)
            {
                var index = i;
                views.Add(($"{name} / {tabs.TabPages[i].Text} tab", () =>
                {
                    tabs.SelectedIndex = index;
                    LayoutSettle(tabs.TabPages[index]);
                }));
            }
            foreach (var (where, select) in views)
            {
                select();
                InspectView(s, form, $"{where} ({form.Width} x {form.Height}, as it opens)", s.Findings);
            }
            CheckMinimumSize(s, name, form, views, s.Findings);
        }
        catch (CheckFailed) { throw; }
        catch (Exception ex) { s.Errors.Add($"the main window: {ex.GetType().Name}: {ex.Message}"); }
        finally { try { form.Dispose(); } catch { /* audit copy */ } }
    }

    /// <summary>The menus take Windows' menu font rather than the app's, so the emulated scaling reaches them here (a real
    /// 150 % display hands every program a bigger menu font).</summary>
    private static void LayoutScaleMenus(Form form)
    {
        var k = Dpi.EmulatedScaleForTest;
        if (k == 1f || form.MainMenuStrip is not { } strip) return;
        if (strip.Font.SizeInPoints < 9f * k - 0.1f) strip.Font = new Font(strip.Font.FontFamily, strip.Font.SizeInPoints * k, strip.Font.Style);
    }

    /// <summary>The device lists as a PC with long device names would fill them: the app's own "Windows default" row
    /// where it has one, then the sample names. Returns how many lists were filled.</summary>
    private static int LayoutFeedSampleDeviceNames(Form form)
    {
        var fields = form is MainForm
            ? new[] { "receiveOutputDevicesList", "sendOutputDevicesList", "sendInputDevicesList", "asioSendDevicesList", "asioReceiveOutputDevicesList" }
            : form is ServiceProfileDialog ? new[] { "outputsList" } : [];
        foreach (var field in fields)
        {
            var list = Require(FieldOf<CheckedListBox>(form, field), $"the {form.Text} window has no {field}");
            var keep = list.Items.Count > 0 && (list.GetItemText(list.Items[0]) ?? "").StartsWith("Use Windows default", StringComparison.Ordinal) ? list.Items[0] : null;
            list.BeginUpdate();
            list.Items.Clear();
            if (keep is not null) list.Items.Add(keep);
            foreach (var sample in LayoutSampleDeviceNames) list.Items.Add(sample);
            list.EndUpdate();
        }
        return fields.Length;
    }

    // ---- the plugin, in the frame a DAW shows ------------------------------------------------------------------

    private static PluginEditorPanel LayoutSamplePluginPanel()
    {
        IReadOnlyList<(string, string)> peers = [("192.168.1.20:47831", "Anna (studio laptop)"), ("192.168.1.31:47831", "Bob's desktop"), ("10.0.0.7:47831", "Charlie on the server")];
        return new PluginEditorPanel
        {
            Dock = DockStyle.Fill,
            PeerSource = () => peers,
            StatusSource = () => "Connected to RemSound." + Environment.NewLine + "Receiving Anna (studio laptop), Bob's desktop.",
        };
    }

    private static void SurveyPluginFrame(LayoutSurvey s)
    {
        const string name = "Plugin window in its DAW frame";
        try
        {
            var size = RemSound.Plugin.RemSoundPlugin.EditorFrameSize();
            var panel = LayoutSamplePluginPanel();
            using var frame = RemSound.Plugin.RemSoundPlugin.BuildEditorFrame(panel, size.Width, size.Height);
            LayoutPrepare(frame);
            panel.SetReceiveForTest(true);
            panel.SetPeerCheckedForTest(0, true);
            panel.SetSendForTest(true);
            frame.PerformLayout();
            s.Windows.Add(name);
            CheckWindowFitsScreen(s, name, frame, s.Findings);
            InspectView(s, frame, $"{name} ({frame.Width} x {frame.Height})", s.Findings);
            s.PluginFrame = $"{frame.Width} x {frame.Height} frame for a panel that needs {panel.GetPreferredSize(Size.Empty).Width} x {panel.GetPreferredSize(Size.Empty).Height}";
        }
        catch (Exception ex) { s.Errors.Add($"{name}: {ex.GetType().Name}: {ex.Message}"); }
    }

    // ---- the canary: one planted fault per check, walked by the same code --------------------------------------

    private static void SurveyCanaries(LayoutSurvey s)
    {
        using var form = new Form
        {
            Text = "Layout canary",
            FormBorderStyle = FormBorderStyle.Sizable,
            StartPosition = FormStartPosition.Manual,
            ShowInTaskbar = false,
            Size = new Size(3000, 3000),
            MinimumSize = new Size(200, 200),
        };
        // A: a label far wider than the panel that holds it, which does not scroll.
        var clipPanel = new Panel { Location = new Point(10, 10), Size = new Size(200, 60) };
        clipPanel.Controls.Add(new Label { AutoSize = true, Location = new Point(0, 0), AccessibleName = "CANARY-A", Text = new string('W', 300) });
        // ...and the counter-test: a tall child in a scrolling panel is not a fault.
        var scrollPanel = new Panel { Location = new Point(10, 80), Size = new Size(200, 60), AutoScroll = true };
        scrollPanel.Controls.Add(new Label { AutoSize = false, Location = new Point(0, 0), Size = new Size(150, 400), AccessibleName = "CANARY-SCROLLS", Text = "tall" });
        // C: two buttons in one place.
        var first = new Button { Location = new Point(10, 150), Size = new Size(80, 26), Text = "One", AccessibleName = "CANARY-C1" };
        var second = new Button { Location = new Point(40, 150), Size = new Size(80, 26), Text = "Two", AccessibleName = "CANARY-C2" };
        // D: a fixed label too small for its words, and a list too narrow for its item.
        var shortLabel = new Label { AutoSize = false, Location = new Point(10, 190), Size = new Size(80, 16), AccessibleName = "CANARY-D", Text = "A sentence far too long for a small fixed label" };
        var narrowList = new ListBox { Location = new Point(10, 210), Size = new Size(120, 40), AccessibleName = "CANARY-DLIST", IntegralHeight = false };
        narrowList.Items.Add(new string('x', 90));
        // G: a label shown for a list that is hidden. H: a label halfway down a tall list, the old way.
        var grid = new TableLayoutPanel { Location = new Point(300, 10), Size = new Size(500, 300), ColumnCount = 2, RowCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var hiddenList = new ListBox { Size = new Size(200, 90), AccessibleName = "CANARY-GLIST", Visible = false };
        grid.Controls.Add(new MnemonicLabel { Text = "Orphan (Alt+O)", AutoSize = true, Anchor = AnchorStyles.Left, MnemonicTarget = hiddenList, AccessibleName = "CANARY-G" }, 0, 0);
        grid.Controls.Add(hiddenList, 1, 0);
        var row = new TableLayoutPanel { Location = new Point(300, 350), Size = new Size(500, 200), ColumnCount = 2, RowCount = 1 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var tallList = new ListBox { Size = new Size(200, 120), AccessibleName = "CANARY-HLIST", IntegralHeight = false };
        row.Controls.Add(new MnemonicLabel { Text = "Floating (Alt+F)", AutoSize = true, Anchor = AnchorStyles.Left, MnemonicTarget = tallList, AccessibleName = "CANARY-H" }, 0, 0);
        row.Controls.Add(tallList, 1, 0);
        // I: grey text on the accent blue.
        var faint = new Label { AutoSize = true, Location = new Point(10, 270), Text = "Faint words", ForeColor = Theme.Neutral, BackColor = Theme.Accent, AccessibleName = "CANARY-I" };
        // ...and a list Windows draws greyed out with a row selected, grey on blue.
        var greyList = new ListBox { Location = new Point(900, 10), Size = new Size(200, 60), AccessibleName = "CANARY-IDISABLED", IntegralHeight = false };
        greyList.Items.AddRange(["First", "Second"]);
        greyList.SelectedIndex = 0;
        greyList.Enabled = false;
        // J: at its 200 x 200 minimum this button is cut off by a panel that does not scroll.
        var fixedPanel = new Panel { Dock = DockStyle.Bottom, Height = 40 };
        fixedPanel.Controls.Add(new Button { Location = new Point(0, 0), Size = new Size(400, 26), Text = "Wide", AccessibleName = "CANARY-J" });
        // K: the commit buttons hard against the left edge, and Cancel before OK, on screen and for Tab.
        var cancel = new Button { Location = new Point(10, 320), Size = new Size(80, 26), Text = "Cancel", AccessibleName = "CANARY-KCANCEL" };
        var ok = new Button { Location = new Point(100, 320), Size = new Size(80, 26), Text = "OK", AccessibleName = "CANARY-K" };
        form.Controls.AddRange([clipPanel, scrollPanel, first, second, shortLabel, narrowList, grid, row, faint, greyList, fixedPanel, cancel, ok]);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        // Walked into a survey of its own: the canary is not a window of the app's, and counts for nothing in the coverage.
        var own = new LayoutSurvey { Scale = s.Scale, Dark = s.Dark, Screen = s.Screen };
        LayoutPrepare(form);
        CheckWindowFitsScreen(own, "canary", form, s.Canary);
        InspectView(own, form, "canary", s.Canary);
        CheckButtonRow(own, "canary", form, s.Canary);
        CheckMinimumSize(own, "canary", form, [("canary", () => { })], s.Canary);
    }

    /// <summary>The planted fault for <paramref name="check"/> was found and named - so the check still works.</summary>
    private static void RequireCanary(LayoutSurvey s, char check, string plant)
    {
        Check(s.Canary.Any(f => f.Check == check && f.What.Contains(plant, StringComparison.Ordinal)),
            $"the check itself is broken: the canary window's planted fault ({plant}) was not reported by check {check}. It reported: "
            + string.Join(" | ", s.Canary.Where(f => f.Check == check).Select(f => f.What).DefaultIfEmpty("nothing")));
    }

    private static string LayoutFailures(LayoutSurvey s, char check, int most = 12)
    {
        var list = s.Findings.Where(f => f.Check == check).ToList();
        return $"{list.Count} found at {s.Scale * 100:0} % ({(s.Dark ? "dark" : "light")} theme): "
               + string.Join(" | ", list.Take(most)) + (list.Count > most ? $" | ...and {list.Count - most} more" : "");
    }

    private static void RequireSurveyComplete(LayoutSurvey s)
    {
        Check(s.Errors.Count == 0, $"the layout survey could not reach every window: {string.Join(" | ", s.Errors)}");
        Check(s.Windows.Count >= s.DialogsExpected + 2,
            $"every dialog, the main window and the plugin frame must be surveyed ({s.Windows.Count} of {s.DialogsExpected + 2})");
        Check(s.Views.Count(v => v.StartsWith("Main window /", StringComparison.Ordinal)) >= 3, "every tab of the main window must be surveyed");
        // Seven: General, Connectivity, Appearance, Audio cues, Startup behaviour, Update settings, Logging (the Latency lab
        // tab went on 2026-10-01).
        Check(s.Views.Count(v => v.StartsWith("Preferences /", StringComparison.Ordinal)) >= 7, "every Preferences tab must be surveyed");
        Check(s.Views.Count(v => v.StartsWith("Service profile /", StringComparison.Ordinal)) >= 2, "every Service profile tab must be surveyed");
        Check(s.Controls > 500, $"the walk must see the windows' controls ({s.Controls})");
    }

    private static string LayoutSummary(LayoutSurvey s) =>
        $"{s.Windows.Count} windows, {s.Views.Count} views, {s.Controls} controls at {s.Scale * 100:0} % ({(s.Dark ? "dark" : "light")} theme)";

    // ============================================================================================================
    // The steps.
    // ============================================================================================================

    private static string? LayoutNothingClipped()
    {
        var s = Survey();
        RequireCanary(s, 'A', "CANARY-A");
        Check(!s.Canary.Any(f => f.Check == 'A' && f.What.Contains("CANARY-SCROLLS", StringComparison.Ordinal)),
            "the check itself is broken: a tall label in a scrolling panel is not cut off, it scrolls");
        RequireSurveyComplete(s);
        Check(!s.Findings.Any(f => f.Check == 'A'), "controls are cut off by their container - " + LayoutFailures(s, 'A'));
        return $"{LayoutSummary(s)}: nothing cut off";
    }

    private static string? LayoutWindowsFitTheScreen()
    {
        var s = Survey();
        Check(s.Canary.Any(f => f.Check == 'B' && f.Where == "canary"), "the check itself is broken: a 3000 x 3000 window was not reported as too big for the screen");
        RequireSurveyComplete(s);
        Check(!s.Findings.Any(f => f.Check == 'B'), "windows bigger than the screen - " + LayoutFailures(s, 'B'));
        return $"{s.Windows.Count} windows each fit {s.Screen.Width} x {s.Screen.Height} at {s.Scale * 100:0} %";
    }

    private static string? LayoutNoOverlaps()
    {
        var s = Survey();
        RequireCanary(s, 'C', "CANARY-C1");
        RequireSurveyComplete(s);
        Check(!s.Findings.Any(f => f.Check == 'C'), "controls overlap - " + LayoutFailures(s, 'C'));
        return $"{LayoutSummary(s)}: no two overlap";
    }

    private static string? LayoutTextFits()
    {
        var s = Survey();
        RequireCanary(s, 'D', "CANARY-D\"");
        RequireCanary(s, 'D', "CANARY-DLIST");
        RequireSurveyComplete(s);
        Check(s.SampleLists == 6, $"the main window's five device lists and the service's must be filled with the long sample names ({s.SampleLists})");
        Check(LayoutSampleDeviceNames.All(n => n.Length >= 60), "the sample device names must be at least 60 characters, as real ones are");
        Check(s.TextChecks > 200, $"the words of the windows' controls must be measured ({s.TextChecks})");
        Check(!s.Findings.Any(f => f.Check == 'D'), "text does not fit its control - " + LayoutFailures(s, 'D'));
        return $"{s.TextChecks} controls' words measured, the device lists holding {LayoutSampleDeviceNames[0].Length}-character names: all fit";
    }

    private static string? LayoutAtLargerScaling()
    {
        if (layoutChild) return Skip("this is one of the scaled processes; the process that started it judges the result");
        var outcomes = new List<string>();
        var failures = new List<string>();
        var runs = new[] { 1.5f, 2f }.Select(k => (K: k, Run: RunLayoutProcess(k, null, "LAYOUT "))).ToList();
        foreach (var (k, (code, lines)) in runs)
            failures.AddRange(lines.Where(l => l.TrimStart().StartsWith("[FAIL]", StringComparison.Ordinal)).Select(l => $"at {k * 100:0} %: {l.Trim()}"));
        Check(failures.Count == 0, string.Join(" || ", failures));
        foreach (var (k, (code, lines)) in runs)
        {
            foreach (var letter in "ABCDFGHJ")
                Check(lines.Any(l => l.TrimStart().StartsWith($"[PASS] LAYOUT {letter}:", StringComparison.Ordinal)),
                    $"at {k * 100:0} % check {letter} must have run and passed (exit code {code}; the process said: {string.Join(" / ", lines.TakeLast(6))})");
            outcomes.Add($"{k * 100:0} %: {lines.Count(l => l.TrimStart().StartsWith("[PASS]", StringComparison.Ordinal))} layout steps passed");
        }
        return string.Join("; ", outcomes);
    }

    private static string? LayoutPluginFrameFitsItsPanel()
    {
        var s = Survey();
        var plugin = new RemSound.Plugin.RemSoundPlugin();
        try
        {
            plugin.InitializeEditor();
            using var panel = LayoutSamplePluginPanel();
            var needs = panel.GetPreferredSize(Size.Empty);
            Check(plugin.EditorWidth >= needs.Width && plugin.EditorHeight >= needs.Height,
                $"the plugin asks its host for {plugin.EditorWidth} x {plugin.EditorHeight}, but its panel needs {needs.Width} x {needs.Height} at {s.Scale * 100:0} % - "
                + "the Status box and the Context help button are cut off in every DAW (the audit's finding 1)");
            // A host that gives less than it was asked for: the frame scrolls, and the panel keeps the size it needs.
            using var smallPanel = LayoutSamplePluginPanel();
            using var small = RemSound.Plugin.RemSoundPlugin.BuildEditorFrame(smallPanel, Dpi.Px(420), Dpi.Px(300));
            LayoutPrepare(small);
            Check(small.AutoScroll && small.VerticalScroll.Visible && small.HorizontalScroll.Visible,
                "a frame smaller than the panel needs must scroll, so a host that imposes a small frame still shows everything");
            Check(smallPanel.Width >= needs.Width - 1 && smallPanel.Height >= needs.Height - 1,
                $"in a small frame the panel must keep its own size ({smallPanel.Width} x {smallPanel.Height} of {needs.Width} x {needs.Height}) and scroll, not squeeze");
            Check(s.Views.Any(v => v.StartsWith("Plugin window in its DAW frame", StringComparison.Ordinal)), "the plugin frame must be in the layout survey");
            Check(!s.Findings.Any(f => f.Where.StartsWith("Plugin window", StringComparison.Ordinal)),
                "the plugin frame has layout faults - " + string.Join(" | ", s.Findings.Where(f => f.Where.StartsWith("Plugin window", StringComparison.Ordinal))));
            return $"asks for {plugin.EditorWidth} x {plugin.EditorHeight} for a panel of {needs.Width} x {needs.Height} at {s.Scale * 100:0} %; a smaller frame scrolls";
        }
        finally { try { plugin.CloseForTest(); } catch { /* audit copy */ } }
    }

    private static string? LayoutLabelsHaveTheirControls()
    {
        var s = Survey();
        RequireCanary(s, 'G', "CANARY-G");
        RequireSurveyComplete(s);
        Check(s.MnemonicLabels > 50, $"the labels that name a control must be walked ({s.MnemonicLabels})");
        Check(!s.Findings.Any(f => f.Check == 'G'), "labels and controls out of step - " + LayoutFailures(s, 'G'));
        return $"{s.MnemonicLabels} labels each shown or hidden with their control";
    }

    private static string? LayoutLabelsLevelWithTallControls()
    {
        var s = Survey();
        RequireCanary(s, 'H', "CANARY-H");
        RequireSurveyComplete(s);
        Check(s.TallRows > 10, $"the labels beside tall controls must be found ({s.TallRows})");
        Check(!s.Findings.Any(f => f.Check == 'H'), "labels float below the top of their control - " + LayoutFailures(s, 'H'));
        return $"{s.TallRows} labels beside tall controls, each level with its top";
    }

    private static string? LayoutContrastInBothThemes()
    {
        var s = Survey();
        RequireCanary(s, 'I', "CANARY-I\"");
        RequireCanary(s, 'I', "CANARY-IDISABLED");
        RequireSurveyComplete(s);
        Check(s.ContrastPairs > 300, $"the text colours of the windows' controls must be measured ({s.ContrastPairs})");
        // The EQ mode list, greyed out with no peer chosen, is the one the audit found (finding 10): it must be among them.
        Check(s.DisabledSelectedLists >= 1, $"a list greyed out with a row selected must be measured - the EQ mode list is, with no peer chosen ({s.DisabledSelectedLists})");
        Check(!s.Findings.Any(f => f.Check == 'I'), "text below the WCAG contrast minimum - " + LayoutFailures(s, 'I'));
        var here = $"{s.ContrastPairs} text colours at least 4.5:1 in the {(s.Dark ? "dark" : "light")} theme, {s.DisabledSelectedLists} greyed-out list(s) with a row selected among them";
        if (layoutChild) return here;
        var other = s.Dark ? "light" : "dark";
        var (code, lines) = RunLayoutProcess(1f, other, "LAYOUT I:");
        Check(lines.Any(l => l.TrimStart().StartsWith("[PASS] LAYOUT I:", StringComparison.Ordinal)),
            $"in the {other} theme: {string.Join(" / ", lines.Where(l => l.Contains("LAYOUT I:", StringComparison.Ordinal)).DefaultIfEmpty($"no result (exit code {code}): {string.Join(" / ", lines.TakeLast(5))}"))}");
        var there = lines.First(l => l.TrimStart().StartsWith("[PASS] LAYOUT I:", StringComparison.Ordinal)).Trim();
        return $"{here}; {other} theme: {Regex.Replace(there, @"^\[PASS\] LAYOUT I:[^-]*- ", "")}";
    }

    private static string? LayoutUsableAtMinimumSize()
    {
        var s = Survey();
        RequireCanary(s, 'J', "CANARY-J");
        RequireSurveyComplete(s);
        Check(s.Resizable >= 1, "the main window at least must be checked at its minimum size");
        Check(!s.Findings.Any(f => f.Check == 'J'), "windows unusable at their minimum size - " + LayoutFailures(s, 'J'));
        return $"{s.Resizable} resizable window(s) checked at their minimum size: nothing cut off";
    }

    private static string? LayoutCommitButtons()
    {
        var s = Survey();
        RequireCanary(s, 'K', "right edge");
        RequireCanary(s, 'K', "CANARY-KCANCEL\" comes before Button \"CANARY-K\" on screen");
        RequireCanary(s, 'K', "CANARY-KCANCEL\" comes before Button \"CANARY-K\" for Tab");
        RequireSurveyComplete(s);
        Check(s.ButtonRows > 15, $"the dialogs' OK and Cancel buttons must be found ({s.ButtonRows})");
        // The dialogs Ed agreed to swap (2026-09-28) are among those whose order is judged.
        Check(s.OrderedButtonRows >= 10, $"the dialogs with both an OK and a Cancel button must be judged for their order ({s.OrderedButtonRows})");
        Check(!s.Findings.Any(f => f.Check == 'K'), "commit buttons out of place - " + LayoutFailures(s, 'K'));
        var advisories = s.Advisories.ToList();
        return $"{s.ButtonRows} dialogs' commit buttons on one row at the bottom right, OK before Cancel in the {s.OrderedButtonRows} with both"
               + (advisories.Count > 0 ? $"; advisory ({advisories.Count}): {string.Join(" | ", advisories.Take(20))}" : "");
    }

    /// <summary>Start a self-test process at an emulated scale (and theme) and hand back what it said. It is a child of
    /// this one, so it runs on the same desktop - the gate's hidden one - and it is --silent like this run.</summary>
    private static (int Code, List<string> Lines) RunLayoutProcess(float scale, string? theme, string only)
    {
        var exe = Require(Environment.ProcessPath, "this process's own path is needed to start a scaled copy");
        var config = Path.Combine(Path.GetTempPath(), "remsound-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(config);
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "--selftest", "--silent", "--config-dir", config, "--only", only }) info.ArgumentList.Add(a);
        if (scale != 1f) { info.ArgumentList.Add("--layout-scale"); info.ArgumentList.Add(scale.ToString("0.##", CultureInfo.InvariantCulture)); }
        if (theme is not null) { info.ArgumentList.Add("--layout-theme"); info.ArgumentList.Add(theme); }
        try
        {
            using var p = Require(Process.Start(info), "the scaled layout process did not start");
            var error = p.StandardError.ReadToEndAsync();
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(240_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* it is ending anyway */ }
                throw new CheckFailed($"the layout process at {scale * 100:0} % did not finish in four minutes and was stopped");
            }
            var lines = (output.Result + "\n" + error.Result).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
            return (p.ExitCode, lines);
        }
        finally { try { Directory.Delete(config, recursive: true); } catch { /* throwaway */ } }
    }

    // ---- M: the help page --------------------------------------------------------------------------------------

    private sealed record CssRule(string Selectors, Dictionary<string, string> Declarations, string Media);

    private static List<CssRule> ParseCss(string css)
    {
        css = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
        var rules = new List<CssRule>();
        void Parse(string text, string media)
        {
            var i = 0;
            while (i < text.Length)
            {
                var open = text.IndexOf('{', i);
                if (open < 0) break;
                var head = text[i..open].Trim();
                if (head.StartsWith('@'))
                {
                    var depth = 1;
                    var j = open + 1;
                    for (; j < text.Length && depth > 0; j++)
                        depth += text[j] == '{' ? 1 : text[j] == '}' ? -1 : 0;
                    Parse(text[(open + 1)..(j - 1)], head);
                    i = j;
                    continue;
                }
                var close = text.IndexOf('}', open);
                if (close < 0) break;
                var decls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in text[(open + 1)..close].Split(';'))
                {
                    var colon = d.IndexOf(':');
                    if (colon > 0) decls[d[..colon].Trim()] = d[(colon + 1)..].Trim();
                }
                rules.Add(new CssRule(head, decls, media));
                i = close + 1;
            }
        }
        Parse(css, "");
        return rules;
    }

    private static Color? CssColour(string? value)
    {
        if (value is null) return null;
        var m = Regex.Match(value, @"#([0-9a-fA-F]{6}|[0-9a-fA-F]{3})\b");
        if (!m.Success) return value.Trim().ToLowerInvariant() switch { "white" => Color.White, "black" => Color.Black, _ => null };
        var hex = m.Groups[1].Value;
        if (hex.Length == 3) hex = string.Concat(hex.Select(ch => $"{ch}{ch}"));
        return Color.FromArgb(Convert.ToInt32(hex[..2], 16), Convert.ToInt32(hex[2..4], 16), Convert.ToInt32(hex[4..], 16));
    }

    private static string? LayoutHelpPageStylesheet()
    {
        var root = FindSourceRoot();
        var path = root is not null ? Path.Combine(root, "readme.html") : Path.Combine(AppContext.BaseDirectory, "readme.html");
        if (!File.Exists(path)) return Skip($"the manual is not reachable here ({path})");
        var html = File.ReadAllText(path);
        var style = Regex.Match(html, @"<style[^>]*>(.*?)</style>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        Check(style.Success, "readme.html must have its stylesheet in a <style> block");
        return CheckHelpCss(style.Groups[1].Value, path) ?? "";
    }

    /// <summary>The help page's rules, on a stylesheet; separate so the canary below can prove each can fail.</summary>
    private static string? CheckHelpCss(string css, string from, bool canary = false)
    {
        var rules = ParseCss(css);
        var problems = new List<string>();
        // H1: running text no wider than 80 characters (WCAG 1.4.8 says 80; Microsoft says 50 to 60).
        var measure = rules.Where(r => r.Media.Length == 0 && Regex.IsMatch(r.Selectors, @"(^|[\s,])p([\s,]|$)")
                                       && r.Declarations.TryGetValue("max-width", out var w) && Regex.IsMatch(w, @"^\d+(\.\d+)?(ch|rem|em)$"))
            .Select(r => r.Declarations["max-width"]).FirstOrDefault();
        if (measure is null) problems.Add("running text (p) has no max-width in ch, em or rem, so lines run the full page width");
        else if (double.Parse(Regex.Match(measure, @"[\d.]+").Value, CultureInfo.InvariantCulture) * (measure.EndsWith("ch", StringComparison.Ordinal) ? 1 : 2) > 80)
            problems.Add($"running text is {measure} wide, more than 80 characters");
        // H2: the contents stay in reach on a wide window.
        if (!rules.Any(r => r.Media.Contains("min-width", StringComparison.Ordinal)
                            && r.Declarations.TryGetValue("position", out var pos) && pos.Contains("sticky", StringComparison.Ordinal)))
            problems.Add("nothing keeps the contents in reach on a wide window (no sticky rule inside a min-width media query)");
        // H3: option names whole, code in a code font.
        if (!rules.Any(r => r.Selectors.Contains("td:first-child code", StringComparison.Ordinal)
                            && r.Declarations.TryGetValue("white-space", out var ws) && ws.Contains("nowrap", StringComparison.Ordinal)))
            problems.Add("option names in the first column of a table can break mid-word (td:first-child code needs white-space: nowrap)");
        if (!rules.Any(r => Regex.IsMatch(r.Selectors, @"(^|[\s,])code([\s,]|$)") && r.Declarations.TryGetValue("font-family", out var ff)
                            && ff.Contains("Consolas", StringComparison.OrdinalIgnoreCase)))
            problems.Add("code has no code font of its own (Consolas)");
        // H4: a dark theme, and every declared pair readable in both.
        var dark = rules.Where(r => r.Media.Contains("prefers-color-scheme", StringComparison.Ordinal) && r.Media.Contains("dark", StringComparison.Ordinal)).ToList();
        if (dark.Count == 0) problems.Add("there is no dark theme (@media (prefers-color-scheme: dark)), so the manual opens bright white from a dark app");
        if (!rules.Any(r => r.Declarations.TryGetValue("color-scheme", out var cs) && cs.Contains("dark", StringComparison.Ordinal)))
            problems.Add("the page does not declare color-scheme: light dark, so form controls and scroll bars stay light");
        var pairs = 0;
        foreach (var scheme in new[] { "light", "dark" })
        {
            var inScheme = rules.Where(r => r.Media.Length == 0 || r.Media.Contains("min-width", StringComparison.Ordinal)).ToList();
            if (scheme == "dark") inScheme.AddRange(dark);
            Color? Last(Func<CssRule, bool> which, string prop)
            {
                Color? found = null;
                foreach (var r in inScheme.Where(which))
                {
                    if (r.Declarations.TryGetValue(prop, out var v) && CssColour(v) is { } c) found = c;
                    else if (prop == "background-color" && r.Declarations.TryGetValue("background", out var b) && CssColour(b) is { } c2) found = c2;
                }
                return found;
            }
            var pageBack = Last(r => Regex.IsMatch(r.Selectors, @"(^|[\s,])(body|html|:root)([\s,]|$)"), "background-color") ?? (scheme == "dark" ? null : Color.White);
            var pageText = Last(r => Regex.IsMatch(r.Selectors, @"(^|[\s,])(body|html|:root)([\s,]|$)"), "color") ?? (scheme == "dark" ? null : Color.Black);
            if (pageBack is null || pageText is null) { problems.Add($"the {scheme} theme sets no page colours"); continue; }
            // Each selector on its own: a rule for "code, pre" sets code's colours as much as one for "code" does.
            static IEnumerable<string> Each(CssRule r) => r.Selectors.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0);
            foreach (var selector in inScheme.SelectMany(Each).Distinct())
            {
                var text = Last(r => Each(r).Contains(selector), "color");
                var back = Last(r => Each(r).Contains(selector), "background-color");
                if (text is null && back is null) continue;
                pairs++;
                var ratio = LayoutContrast(text ?? pageText.Value, back ?? pageBack.Value);
                if (ratio < 4.5) problems.Add($"{scheme} theme: \"{selector}\" is {ratio:0.00}:1");
            }
        }
        if (canary) return problems.Count == 0 ? null : string.Join(" | ", problems);
        Check(problems.Count == 0, $"{Path.GetFileName(from)}: {string.Join(" | ", problems)}");
        // Its own canary: the same stylesheet with the dark block taken out must fail.
        var withoutDark = Regex.Replace(css, @"@media\s*\(prefers-color-scheme:\s*dark\)\s*\{(?:[^{}]*\{[^{}]*\})*[^{}]*\}", "", RegexOptions.Singleline);
        Check(withoutDark != css && CheckHelpCss(withoutDark, from, canary: true)?.Contains("no dark theme", StringComparison.Ordinal) == true,
            "the check itself is broken: taking the dark theme out of the stylesheet was not noticed");
        return $"running text {measure} wide, contents sticky on wide windows, option names whole, a code font, a dark theme, {pairs} colour pairs at least 4.5:1";
    }

    // ---- L: pictures for a sighted reviewer --------------------------------------------------------------------

    private static bool LayoutOnHiddenDesktop(out string name)
    {
        name = "?";
        var desk = LayoutGetThreadDesktop(LayoutGetCurrentThreadId());
        if (desk == IntPtr.Zero) return false;
        var sb = new StringBuilder(256);
        if (!LayoutGetUserObjectInformation(desk, 2 /* UOI_NAME */, sb, sb.Capacity * 2, out _)) return false;
        name = sb.ToString();
        return !string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase) && !string.Equals(name, "Winlogon", StringComparison.OrdinalIgnoreCase);
    }

    private static string LayoutSlug(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }

    private static void LayoutPump()
    {
        for (var i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(15); }
    }

    private static string? LayoutPicturesForAReviewer()
    {
        if (layoutPictures is null) return Skip("no --ui-pictures folder was given: pictures are made only when asked for");
        Check(LayoutOnHiddenDesktop(out var desktop),
            $"pictures show real windows, so they are only ever made on a hidden desktop, never on a person's screen (this is \"{desktop}\")");
        var folder = layoutPictures;
        Directory.CreateDirectory(folder);
        var tag = $"{(Theme.IsDark ? "dark" : "light")}-{Dpi.EmulatedScaleForTest * 100:0}";
        var made = 0;
        // The real screen, this PC's, so the windows open as they would here (the checks use smaller stand-ins).
        var restoreArea = Dpi.WorkingAreaForTest;
        Dpi.WorkingAreaForTest = null;
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-pictures-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var scope = AppConfig.UseThrowawayUserDataDirectory(scratch);
            void Picture(Form form, string name)
            {
                var cs = form.ClientSize;
                if (cs.Width <= 0 || cs.Height <= 0) return;
                LayoutPump();
                using var full = new Bitmap(Math.Max(1, form.Width), Math.Max(1, form.Height));
                using (var g = Graphics.FromImage(full))
                {
                    var hdc = g.GetHdc();
                    try { LayoutPrintWindow(form.Handle, hdc, 2 /* PW_RENDERFULLCONTENT */); }
                    finally { g.ReleaseHdc(hdc); }
                }
                var origin = form.PointToScreen(Point.Empty);
                var dx = Math.Max(0, origin.X - form.Bounds.X);
                var dy = Math.Max(0, origin.Y - form.Bounds.Y);
                using var client = full.Clone(new Rectangle(dx, dy, Math.Min(cs.Width, full.Width - dx), Math.Min(cs.Height, full.Height - dy)), full.PixelFormat);
                client.Save(Path.Combine(folder, $"{LayoutSlug(name)}--{tag}.png"), ImageFormat.Png);
                made++;
            }
            void Show(Form form)
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(10, 10);
                form.ShowInTaskbar = false;
                form.Show();
                LayoutPump();
                form.PerformLayout();
                LayoutPump();
            }
            void Tabs(Form form, string name)
            {
                var tabs = LayoutVisibleControls(form).OfType<TabControl>().FirstOrDefault();
                if (tabs is null) { LayoutFeedSampleDeviceNames(form); Picture(form, name); return; }
                for (var i = 0; i < tabs.TabCount; i++)
                {
                    tabs.SelectedIndex = i;
                    form.PerformLayout();
                    LayoutPump();
                    LayoutFeedSampleDeviceNames(form);
                    form.PerformLayout();
                    LayoutPump();
                    Picture(form, $"{name} {tabs.TabPages[i].Text} tab");
                }
            }
            foreach (var (name, make) in DialogFactories())
            {
                using var form = make();
                Show(form);
                Tabs(form, name);
                form.Hide();
            }
            // The main window without its Shown handler, which starts the real app (the updater, the router, the service).
            using (var main = new MainForm(null, Profile.NewBlank(), null, null, headless: true))
            {
                var events = (System.ComponentModel.EventHandlerList?)Require(typeof(System.ComponentModel.Component)
                    .GetProperty("Events", BindingFlags.Instance | BindingFlags.NonPublic), "Component.Events").GetValue(main);
                var key = Require(typeof(Form).GetField("s_shownEvent", BindingFlags.Static | BindingFlags.NonPublic), "Form.s_shownEvent").GetValue(null);
                Check(events is not null && key is not null, "the main window's Shown handlers must be taken off before it is shown for a picture");
                if (events![key!] is { } shown) events.RemoveHandler(key!, shown);
                LayoutScaleMenus(main);
                Show(main);
                LayoutFeedSampleDeviceNames(main);
                Tabs(main, "Main window");
                main.Hide();
            }
            var size = RemSound.Plugin.RemSoundPlugin.EditorFrameSize();
            var panel = LayoutSamplePluginPanel();
            using (var host = new Form { FormBorderStyle = FormBorderStyle.None, ClientSize = size, ShowInTaskbar = false })
            using (var frame = RemSound.Plugin.RemSoundPlugin.BuildEditorFrame(panel, size.Width, size.Height))
            {
                host.Controls.Add(frame);
                frame.Show();
                panel.SetReceiveForTest(true);
                panel.SetPeerCheckedForTest(0, true);
                Show(host);
                Picture(host, "Plugin window in its DAW frame");
                host.Hide();
            }
        }
        finally
        {
            Dpi.WorkingAreaForTest = restoreArea;
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); } catch { /* throwaway */ }
        }
        Check(made > 20, $"a picture of every window must have been made ({made})");
        return $"{made} pictures in {layoutPictures}, on the hidden desktop \"{desktop}\"";
    }
}
