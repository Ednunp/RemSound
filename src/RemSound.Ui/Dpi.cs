using System.Windows.Forms;

namespace RemSound.App;

/// <summary>
/// DISPLAY SCALING, ONE SIZE AT A TIME (the sighted-user UI audit of 2026-09-28, finding 5).
///
/// <para>RemSound is SystemAware and no window records AutoScaleDimensions, so at 150 % or 200 % Windows makes every font
/// bigger and WinForms leaves every pixel size set in code exactly as written. Text outgrew its boxes everywhere. Every
/// width, height, client size, padding and margin a window sets in code goes through here instead, and comes out in the
/// display's own pixels. Text-bearing controls keep AutoSize and size themselves.</para>
///
/// <para>Why not the one-line switch (AutoScaleMode.Dpi with AutoScaleDimensions of 96 on every window)? It was tried in
/// the audit's copy and over-scaled: an auto-sized button, which only ever grows, grew twice, and some rows of the main
/// window came out blank. Per size is dull, and exact.</para>
///
/// <para>Built on <see cref="Control.LogicalToDeviceUnits(int)"/>, which WinForms provides on every Windows it runs on,
/// Windows 7 included - no newer Windows API is touched. The measuring control never gets a window handle, so it is only
/// arithmetic on the DPI WinForms took at start-up: the system DPI in the app, and whatever the host set in a DAW.</para>
///
/// <para>Nothing here changes what a screen reader reads, the tab order or any Alt key: sizes only.</para>
/// </summary>
internal static class Dpi
{
    private static Control? probe;

    /// <summary>Gate seam: the display scaling the layout checks pretend to have, on top of the real one. At 1 (always,
    /// outside the gate) it does nothing. The layout checks run a process at 1.5 and at 2 with the app font scaled the
    /// same, which is what a real 150 % or 200 % display does to a SystemAware program (see SelfTest.Layout.cs).</summary>
    internal static float EmulatedScaleForTest { get; set; } = 1f;

    /// <summary>Gate seam: the screen's working area, for the check that every window fits a smaller screen than the gate
    /// PC's. Null (always, outside the gate) means the real screen the person is working on.</summary>
    internal static Rectangle? WorkingAreaForTest { get; set; }

    /// <summary>The scale factor in use: 1 at 100 %, 1.5 at 150 %.</summary>
    internal static float Scale => Px(1000) / 1000f;

    /// <summary>A size in 96-DPI pixels, in this display's pixels.</summary>
    internal static int Px(int logical)
    {
        if (logical == 0) return 0;
        probe ??= new Control();
        var device = probe.LogicalToDeviceUnits(logical);
        return EmulatedScaleForTest == 1f ? device : (int)Math.Round(device * EmulatedScaleForTest, MidpointRounding.AwayFromZero);
    }

    internal static Size Px(int width, int height) => new(Px(width), Px(height));

    internal static Size Px(Size logical) => Px(logical.Width, logical.Height);

    /// <summary>Padding or a margin, the same on every side.</summary>
    internal static Padding Pad(int all) => new(Px(all));

    internal static Padding Pad(int left, int top, int right, int bottom) => new(Px(left), Px(top), Px(right), Px(bottom));

    /// <summary>A font size in points for a font the code sizes itself. Points already follow the display, so this only
    /// carries the gate's emulated scaling; outside the gate it is the number given.</summary>
    internal static float Pt(float points) => points * EmulatedScaleForTest;

    /// <summary>The working area of the screen the person is on (or the gate's stand-in).</summary>
    internal static Rectangle WorkingArea()
    {
        if (WorkingAreaForTest is { } stand) return stand;
        try { return Screen.FromPoint(Cursor.Position).WorkingArea; }
        catch { return Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 728); }
    }

    /// <summary>A window size no bigger than the working area, so no window opens with its edges or buttons off the
    /// screen. Content that no longer fits scrolls, where the window has scrolling pages.</summary>
    internal static Size FitToScreen(Size wanted)
    {
        var area = WorkingArea();
        return new Size(Math.Min(wanted.Width, area.Width), Math.Min(wanted.Height, area.Height));
    }

    /// <summary>The working areas of the screens there are now (or the gate's stand-in).</summary>
    internal static IReadOnlyList<Rectangle> ScreensNow()
    {
        if (WorkingAreaForTest is { } stand) return [stand];
        try { return Screen.AllScreens.Select(s => s.WorkingArea).ToList(); }
        catch { return [WorkingArea()]; }
    }

    /// <summary>
    /// A window's remembered bounds, put wholly on a screen there is now: the one it was mostly on, or the nearest when it
    /// is on none (a monitor taken away, or a smaller one in its place). As big as it was, but no bigger than that screen
    /// and never below <paramref name="minimum"/>. Screen.AllScreens is there on Windows 7 too.
    /// </summary>
    internal static Rectangle PlaceOnAScreen(Rectangle saved, Size minimum, IReadOnlyList<Rectangle>? screens = null)
    {
        var areas = screens is { Count: > 0 } ? screens : ScreensNow();
        var middle = new Point(saved.X + saved.Width / 2, saved.Y + saved.Height / 2);
        static long Overlap(Rectangle a, Rectangle b) { var i = Rectangle.Intersect(a, b); return (long)i.Width * i.Height; }
        static long Away(Rectangle a, Point p)
        {
            long dx = Math.Max(0, Math.Max(a.Left - p.X, p.X - (a.Right - 1)));
            long dy = Math.Max(0, Math.Max(a.Top - p.Y, p.Y - (a.Bottom - 1)));
            return dx * dx + dy * dy;
        }
        var area = areas.OrderByDescending(a => Overlap(a, saved)).ThenBy(a => Away(a, middle)).First();
        var width = Math.Max(minimum.Width, Math.Min(saved.Width, area.Width));
        var height = Math.Max(minimum.Height, Math.Min(saved.Height, area.Height));
        var x = Math.Clamp(saved.X, area.Left, Math.Max(area.Left, area.Right - width));
        var y = Math.Clamp(saved.Y, area.Top, Math.Max(area.Top, area.Bottom - height));
        return new Rectangle(x, y, width, height);
    }
}
