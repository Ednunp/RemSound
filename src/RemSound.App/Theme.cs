using System.Drawing.Drawing2D;

namespace RemSound.App;

/// <summary>Central visual style for RemSound. The app font is set globally (csproj
/// ApplicationDefaultFont) and dark/light is the user's Colour theme choice in Preferences — match
/// Windows (the default), Light or Dark — applied by Application.SetColorMode in Program.Main, so
/// this only holds the extra touches: the window icon, an accent colour, section-header +
/// dialog-heading label factories, and health-status colours. All of it is visual only — it changes no control types and no
/// accessibility wiring, so the screen-reader experience is untouched.</summary>
internal static class Theme
{
    private static Icon? appIcon;
    private static bool triedIcon;

    /// <summary>The RemSound window icon, loaded once from the embedded multi-size .ico so title bars,
    /// the taskbar, Alt+Tab and the tray icon all render crisply. Null if it can't be loaded.</summary>
    public static Icon? AppIcon
    {
        get
        {
            if (!triedIcon)
            {
                triedIcon = true;
                try
                {
                    var asm = typeof(Theme).Assembly;
                    var name = Array.Find(asm.GetManifestResourceNames(),
                        n => n.EndsWith("remsound.ico", StringComparison.OrdinalIgnoreCase));
                    if (name is not null)
                    {
                        using var s = asm.GetManifestResourceStream(name);
                        if (s is not null) appIcon = new Icon(s);
                    }
                }
                catch { appIcon = null; }
            }
            return appIcon;
        }
    }

    /// <summary>True when the system window colour is dark (brightness below one half). Cosmetic
    /// heuristic — used to pick contrasting accent/health colours. <see cref="EqCurveControl"/> makes
    /// its own brightness test on its BackColor and does not use this.</summary>
    public static bool IsDark => SystemColors.Window.GetBrightness() < 0.5f;

    /// <summary>Accent used for section headings (<see cref="SectionHeader"/>). Lighter blue in dark
    /// mode. The EQ curve draws in its own colours, not this one.</summary>
    public static Color Accent => IsDark ? Color.FromArgb(88, 166, 255) : Color.FromArgb(0, 99, 177);

    public static Color Healthy => IsDark ? Color.FromArgb(70, 200, 100) : Color.FromArgb(24, 140, 56);
    public static Color Warning => IsDark ? Color.FromArgb(232, 184, 64) : Color.FromArgb(176, 120, 0);
    public static Color Bad => IsDark ? Color.FromArgb(240, 100, 100) : Color.FromArgb(190, 40, 40);
    public static Color Neutral => SystemColors.GrayText;

    /// <summary>The Windows type ramp's heading weight and sizes (UI audit 2026-09-28, finding 13): Segoe UI Semibold at its
    /// regular style - asking for Bold as well drew a synthetic extra-bold - at 14 px ("Body Strong", 10.5 pt) for a
    /// section header and 20 px ("Subtitle", 15 pt) for a dialog's heading. Microsoft: "Bold and Italic styles are not
    /// part of the Windows type ramp. Use Semibold instead of Bold."</summary>
    public static Font SectionHeaderFont() => new("Segoe UI Semibold", Dpi.Pt(10.5f), FontStyle.Regular);

    public static Font HeadingFont() => new("Segoe UI Semibold", Dpi.Pt(15f), FontStyle.Regular);

    /// <summary>A semibold, accent-coloured section header — groups a run of rows on the busier tabs. Not a
    /// tab stop and carries no shortcut; it's static text the screen reader simply reads as a heading.</summary>
    public static Label SectionHeader(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = SectionHeaderFont(),
        ForeColor = Accent,
        Margin = Dpi.Pad(0, 10, 0, 2),
        Anchor = AnchorStyles.Left,
    };

    /// <summary>A semibold heading label for the top of a dialog.</summary>
    public static Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = HeadingFont(),
        Margin = Dpi.Pad(0, 0, 0, 8),
        Anchor = AnchorStyles.Left,
    };
}

/// <summary>A small filled circle used as a colour cue (e.g. connection health) beside a text label. It
/// is deliberately invisible to the screen reader — not a tab stop, empty accessible name, Graphic role
/// — so the neighbouring text (which NVDA does read) remains the single source of truth.</summary>
internal sealed class StatusDot : Control
{
    private Color dot = SystemColors.GrayText;

    public StatusDot()
    {
        TabStop = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        Size = Dpi.Px(12, 12);
        Margin = Dpi.Pad(0, 5, 6, 0);
        AccessibleName = "";
    }

    public void SetColor(Color c)
    {
        if (dot == c) return;
        dot = c;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        int d = Math.Min(Width, Height) - 2;
        using var b = new SolidBrush(dot);
        e.Graphics.FillEllipse(b, (Width - d) / 2f, (Height - d) / 2f, d, d);
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new HiddenAcc(this);

    private sealed class HiddenAcc(Control owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.Graphic;
        public override string? Name { get => ""; set { } }
    }
}

/// <summary>
/// A list that stays readable while it is greyed out (UI audit 2026-09-28, finding 10; Ed agreed 2026-09-28). Windows
/// draws the selected row of a disabled list as grey words on the blue highlight, 1.15:1 in the light theme and 1.52:1 in
/// the dark, so "3 band simple EQ" could not be seen. While it is disabled this list draws its own rows: grey words on
/// the window's colour, and the selected row on the dialog grey with a grey edge, each at least 4.5:1. While it is
/// enabled Windows draws it, exactly as any other list. Only how it looks changes: the items, the selection and the name
/// a screen reader reads are the list's own either way.
/// </summary>
internal sealed class ReadableWhenDisabledListBox : ListBox
{
    /// <summary>The words and the fill of a row while the list is greyed out.</summary>
    internal static (Color Fore, Color Back) DisabledColours(ListBox list, bool selected)
    {
        var back = selected ? SystemColors.Control : list.BackColor;
        return (ReadableGrey(back), back);
    }

    /// <summary>Windows' grey-text colour, moved toward the ordinary text colour only as far as it takes to read at 4.5:1
    /// on <paramref name="back"/>: the grey alone is 4.33:1 on the dark theme's list colour.</summary>
    private static Color ReadableGrey(Color back)
    {
        var grey = SystemColors.GrayText;
        var text = SystemColors.WindowText;
        for (var step = 0; step <= 10; step++)
        {
            var t = step / 10f;
            var c = Color.FromArgb((int)(grey.R + (text.R - grey.R) * t), (int)(grey.G + (text.G - grey.G) * t), (int)(grey.B + (text.B - grey.B) * t));
            if (Contrast(c, back) >= 4.6) return c;
        }
        return text;
    }

    /// <summary>The WCAG contrast ratio of two colours.</summary>
    private static double Contrast(Color a, Color b)
    {
        static double Channel(int v)
        {
            var x = v / 255.0;
            return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        // Owner-drawn only while greyed out; a change of drawing remakes the list's window, which keeps its items and selection.
        if (!Enabled && DrawMode == DrawMode.Normal)
        {
            // The rows keep the height Windows gave them.
            var rows = IsHandleCreated ? GetItemHeight(0) : Font.Height;
            DrawMode = DrawMode.OwnerDrawFixed;
            ItemHeight = Math.Clamp(rows, 1, 255);
        }
        else if (Enabled && DrawMode != DrawMode.Normal) DrawMode = DrawMode.Normal;
        base.OnEnabledChanged(e);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        base.OnDrawItem(e);
        if (e.Index < 0 || e.Index >= Items.Count) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        var (fore, back) = DisabledColours(this, selected);
        using (var fill = new SolidBrush(back)) e.Graphics.FillRectangle(fill, e.Bounds);
        if (selected)
        {
            using var edge = new Pen(fore);
            e.Graphics.DrawRectangle(edge, e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1);
        }
        var words = Rectangle.Inflate(e.Bounds, -Dpi.Px(2), 0);
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, words, fore, back,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }
}
