using System.Drawing.Drawing2D;

namespace RemSound.App;

/// <summary>Central visual style for RemSound. The app font is set globally (csproj
/// ApplicationDefaultFont) and dark/light follows the OS (Application.SetColorMode in Program.Main), so
/// this only holds the extra touches: an accent colour, section-header + dialog-heading label factories,
/// and health-status colours. All of it is visual only — it changes no control types and no
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

    /// <summary>True when the app is currently showing on a dark background. Cosmetic heuristic
    /// (mirrors <see cref="EqCurveControl"/>) — used to pick contrasting accent/health colours.</summary>
    public static bool IsDark => SystemColors.Window.GetBrightness() < 0.5f;

    /// <summary>Accent used for section headings and the EQ curve. Lighter blue in dark mode.</summary>
    public static Color Accent => IsDark ? Color.FromArgb(88, 166, 255) : Color.FromArgb(0, 99, 177);

    public static Color Healthy => IsDark ? Color.FromArgb(70, 200, 100) : Color.FromArgb(24, 140, 56);
    public static Color Warning => IsDark ? Color.FromArgb(232, 184, 64) : Color.FromArgb(176, 120, 0);
    public static Color Bad => IsDark ? Color.FromArgb(240, 100, 100) : Color.FromArgb(190, 40, 40);
    public static Color Neutral => SystemColors.GrayText;

    /// <summary>A bold, accent-coloured section header — groups a run of rows on the busier tabs. Not a
    /// tab stop and carries no shortcut; it's static text the screen reader simply reads as a heading.</summary>
    public static Label SectionHeader(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 9.75f, FontStyle.Bold),
        ForeColor = Accent,
        Margin = new Padding(0, 10, 0, 2),
        Anchor = AnchorStyles.Left,
    };

    /// <summary>A bold heading label for the top of a dialog.</summary>
    public static Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold),
        Margin = new Padding(0, 0, 0, 8),
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
        Size = new Size(12, 12);
        Margin = new Padding(0, 5, 6, 0);
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
