using System.Drawing.Drawing2D;
using RemSound.Core;

namespace RemSound.App;

/// <summary>A purely-decorative frequency-response graph for the selected peer's EQ. Draws the dialled
/// EQ shape (all three modes feed the same curve) so a sighted onlooker sees the classic EQ picture.
/// It is NOT focusable and carries no accessible content — NVDA skips it entirely; every actual control
/// stays the sliders / list / buttons around it. RemSound has no sighted primary users, so this is a
/// low-cost nicety, not part of the interaction.</summary>
internal sealed class EqCurveControl : Panel
{
    private PeerShaping? shaping;

    // Log-frequency axis: 20 Hz .. 20 kHz.
    private const double MinHz = 20.0;
    private const double MaxHz = 20000.0;
    private const float RangeDb = PeerEqBands.MaxGainDb;   // curve spans -12..+12 dB

    public EqCurveControl()
    {
        TabStop = false;                       // never in the keyboard tab order
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        // No accessible name/role — leave it invisible to screen readers.
        AccessibleName = "";
    }

    /// <summary>Point the curve at a peer's shaping (or null for flat) and repaint.</summary>
    public void SetResponse(PeerShaping? s)
    {
        shaping = s;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int w = Math.Max(1, ClientSize.Width);
        int h = Math.Max(1, ClientSize.Height);

        bool dark = BackColor.GetBrightness() < 0.5f;
        using var bg = new SolidBrush(dark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(245, 245, 245));
        g.FillRectangle(bg, 0, 0, w, h);

        // Grid: 0 dB centre line plus ±half-range guides.
        using var gridPen = new Pen(dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(210, 210, 210));
        using var zeroPen = new Pen(dark ? Color.FromArgb(110, 110, 110) : Color.FromArgb(170, 170, 170));
        float midY = h / 2f;
        g.DrawLine(gridPen, 0, h * 0.25f, w, h * 0.25f);
        g.DrawLine(gridPen, 0, h * 0.75f, w, h * 0.75f);
        g.DrawLine(zeroPen, 0, midY, w, midY);

        // Build the response polyline: one point per horizontal pixel.
        var pts = new PointF[w];
        double logMin = Math.Log10(MinHz);
        double logMax = Math.Log10(MaxHz);
        for (int x = 0; x < w; x++)
        {
            double frac = w <= 1 ? 0 : (double)x / (w - 1);
            double freq = Math.Pow(10, logMin + frac * (logMax - logMin));
            float db = ResponseDb(freq);
            float clamped = Math.Clamp(db, -RangeDb, RangeDb);
            // +db upward: y = mid - (db/range)*halfHeight
            float y = midY - (clamped / RangeDb) * (h / 2f - 4f);
            pts[x] = new PointF(x, y);
        }

        Color curveColor = dark ? Color.FromArgb(90, 200, 255) : Color.FromArgb(0, 120, 200);
        if (w >= 2)
        {
            // Soft translucent fill between the curve and the 0 dB line, then the line on top.
            var fill = new PointF[pts.Length + 2];
            Array.Copy(pts, fill, pts.Length);
            fill[^2] = new PointF(w - 1, midY);
            fill[^1] = new PointF(0, midY);
            using (var fillBrush = new SolidBrush(Color.FromArgb(38, curveColor)))
                g.FillPolygon(fillBrush, fill);
            using var curvePen = new Pen(curveColor, 2f);
            g.DrawLines(curvePen, pts);
        }
    }

    /// <summary>Approximate combined EQ response in dB at one frequency. Cosmetic only — analytic bell /
    /// shelf shapes summed in dB, close enough to the real biquad response for a picture.</summary>
    private float ResponseDb(double freq)
    {
        var s = shaping;
        if (s is null) return 0f;

        double total = 0;
        switch (s.EqMode)
        {
            case PeerEqMode.Parametric16Band:
                foreach (var band in s.ParametricBands)
                {
                    if (band is null || MathF.Abs(band.GainDb) < 0.05f) continue;
                    PeerEqBands.ParametricToPeaking(band.StartHz, band.EndHz, out float centre, out float q);
                    total += Bell(freq, centre, q) * band.GainDb;
                }
                break;

            case PeerEqMode.Advanced10Band:
                for (int i = 0; i < PeerEqBands.Advanced.Length; i++)
                {
                    float gain = i < s.AdvancedBandsDb.Length ? s.AdvancedBandsDb[i] : 0f;
                    if (MathF.Abs(gain) < 0.05f) continue;
                    total += Bell(freq, PeerEqBands.Advanced[i].Freq, 1.4f) * gain;
                }
                break;

            default: // Simple3Band: bass low-shelf, mids peak, treble high-shelf
                float bass = s.SimpleBandsDb.Length > 0 ? s.SimpleBandsDb[0] : 0f;
                float mids = s.SimpleBandsDb.Length > 1 ? s.SimpleBandsDb[1] : 0f;
                float treble = s.SimpleBandsDb.Length > 2 ? s.SimpleBandsDb[2] : 0f;
                total += LowShelf(freq, PeerEqBands.Simple[0].Freq) * bass;
                total += Bell(freq, PeerEqBands.Simple[1].Freq, 0.9f) * mids;
                total += HighShelf(freq, PeerEqBands.Simple[2].Freq) * treble;
                break;
        }
        return (float)total;
    }

    // Gaussian bell in log-frequency, peak 1.0 at f0. Width from Q (higher Q = narrower).
    private static double Bell(double f, double f0, double q)
    {
        double sigmaOct = 1.0 / (2.0 * Math.Max(0.1, q));
        double sigmaLn = sigmaOct * Math.Log(2.0);
        double x = Math.Log(f / f0) / Math.Max(1e-6, sigmaLn);
        return Math.Exp(-0.5 * x * x);
    }

    private static double LowShelf(double f, double f0) => 1.0 / (1.0 + Math.Pow(f / f0, 2.0));
    private static double HighShelf(double f, double f0) => 1.0 / (1.0 + Math.Pow(f0 / f, 2.0));
}
