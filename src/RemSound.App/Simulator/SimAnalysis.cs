namespace RemSound.App.Simulator;

/// <summary>
/// Listens to what came out of a simulation. The signal went in as LEFT = a steady 1 kHz tone at half scale, RIGHT = a
/// click every 500 ms. From the clicks: the true delay, input to output, and how it moved. From the tone: every
/// break-up (a millisecond that went quiet), every click (a jump between samples that a clean 1 kHz sine cannot make),
/// pitch wobble (cents, per 100 ms) and noise (how far each 100 ms is from a perfect sine).
/// </summary>
internal static class SimAnalysis
{
    internal sealed record Findings(
        int ClicksHeard,
        double DelayMedianMs, double DelayMinMs, double DelayMaxMs, double DelayFirstMs, double DelayLastMs,
        int Glitches, int QuietMs, int StepClicks,
        double WorstPitchCents, double MedianSnrDb, double WorstSnrDb,
        double AnalysedSeconds, string? FirstGlitchAt, double PitchP95Cents = double.NaN, double PitchMedianCents = double.NaN)
    {
        /// <summary>Heard, not a single break-up or click, and the tone still a tone everywhere: its worst 100 ms at least
        /// 15 dB above the noise (Opus at 2.5 ms sits near 40; anything garbled falls far below).</summary>
        public bool Clean => ClicksHeard > 0 && Glitches == 0 && WorstSnrDb > 15;

        /// <summary>The same by the tone alone, for sound that carries no delay clicks (a one-channel device's): listened to,
        /// not a single break-up or click, and the tone a tone everywhere.</summary>
        public bool CleanTone => AnalysedSeconds > 0 && Glitches == 0 && WorstSnrDb > 15;
    }

    private const int Rate = Simulation.SampleRate;

    /// <summary>Sound in the clicks' channel is not a click when, within half a millisecond of its peak, it swings the other
    /// way by more than this share of the peak.</summary>
    private const float NotAClickAbove = 0.55f;

    /// <summary>Every delay click in the right channel: the frame it is at, when that frame is heard, and how long after it
    /// was sent that is (the clicks are sent every <see cref="Simulation.ClickEverySec"/> on the sender's clock).</summary>
    public static List<(int Frame, double HeardSec, double DelayMs)> Clicks(IReadOnlyList<float> interleaved,
        IReadOnlyList<(int Frame, double Time)> blockTimes, double sendRate)
    {
        var frames = interleaved.Count / 2;
        double TimeOf(int frame)
        {
            int lo = 0, hi = blockTimes.Count - 1;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (blockTimes[mid].Frame <= frame) lo = mid; else hi = mid - 1;
            }
            return blockTimes.Count == 0 ? 0 : blockTimes[lo].Time + (frame - blockTimes[lo].Frame) / (double)Rate;
        }
        var clickInterval = Simulation.ClickEverySec * Rate / sendRate;   // seconds between clicks, on the sender's clock
        var clicks = new List<(int, double, double)>();
        for (var f = 0; f < frames; f++)
        {
            if (Math.Abs(interleaved[2 * f + 1]) < 0.3f) continue;
            // The loudest sample within a millisecond is the click.
            var best = f;
            for (var g = f; g < Math.Min(frames, f + 48); g++)
                if (Math.Abs(interleaved[2 * g + 1]) > Math.Abs(interleaved[2 * best + 1])) best = g;
            // A click goes one way: up, with at most a little ringing after a codec or a resampler has had it (measured
            // over every codec: nothing the other way over 0.39 of it). Sound that swings both ways is not a click. The
            // noise an underrun is covered with goes out on both channels at the louder one's level (0.70 to 0.89 the
            // other way), and in the clicks' channel it was taken for a click - a "delay" of anything up to half a second,
            // and the true click after it skipped (found by the music-like signal, 2026-09-30). Nor is a tone: a
            // one-channel device's sound is on both channels, and its tone was being timed as if it were clicks.
            var peak = interleaved[2 * best + 1];
            var otherWay = 0f;
            for (var g = Math.Max(0, best - 24); g < Math.Min(frames, best + 24); g++)
                if (interleaved[2 * g + 1] * peak < 0) otherWay = Math.Max(otherWay, Math.Abs(interleaved[2 * g + 1]));
            if (otherWay > NotAClickAbove * Math.Abs(peak))
            {
                f = best + 24;
                continue;
            }
            var heard = TimeOf(best);
            var sentAt = Math.Floor(heard / clickInterval) * clickInterval;
            clicks.Add((best, heard, (heard - sentAt) * 1000));
            f = best + Rate / 5; // 200 ms before looking again
        }
        return clicks;
    }

    /// <param name="interleaved">Output, stereo interleaved.</param>
    /// <param name="blockTimes">For each output block: its first frame, and the virtual time that frame is heard.</param>
    /// <param name="sendRate">The sending card's real sample rate (drift included), which timed the clicks.</param>
    /// <param name="expectedToneRatio">The tone's frequency as it should come out, relative to 1 kHz (drift moves it).</param>
    /// <param name="countFromFrame">For a long run listened to a stretch at a time (2026-09-28): only what happens from this
    /// frame on is counted - the audio before it is the tail of the stretch already counted, there so every stretch starts
    /// mid-stream with its clicks found. The tone is listened to from here too, click or no click: a stretch the stream has
    /// already started in has no silent start to skip, and one that went silent must be heard to.</param>
    /// <param name="countToFrame">...and only up to this frame (the next stretch counts the rest).</param>
    public static Findings Analyse(IReadOnlyList<float> interleaved, IReadOnlyList<(int Frame, double Time)> blockTimes, double sendRate,
        double expectedToneRatio = 1.0, int countFromFrame = 0, int countToFrame = int.MaxValue)
    {
        var frames = interleaved.Count / 2;
        double TimeOf(int frame)
        {
            int lo = 0, hi = blockTimes.Count - 1;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (blockTimes[mid].Frame <= frame) lo = mid; else hi = mid - 1;
            }
            return blockTimes.Count == 0 ? 0 : blockTimes[lo].Time + (frame - blockTimes[lo].Frame) / (double)Rate;
        }

        // ---- the clicks, and the delay ----
        var clicks = Clicks(interleaved, blockTimes, sendRate);
        var firstClickFrame = clicks.Count > 0 ? clicks[0].Frame : -1;
        var delays = clicks.Where(c => c.Frame >= countFromFrame && c.Frame < countToFrame).Select(c => c.DelayMs).ToList();

        // ---- the tone ----
        // From a fifth of a second after the first click; or, with no clicks to go by (a one-channel device carries none),
        // after the tone itself begins.
        var toneBegins = -1;
        if (countFromFrame <= 0 && firstClickFrame < 0)
            for (var f = 0; f < frames && toneBegins < 0; f++)
                if (Math.Abs(interleaved[2 * f]) > 0.5f * Simulation.ToneLevel) toneBegins = f;
        var start = countFromFrame > 0 ? countFromFrame : firstClickFrame >= 0 ? firstClickFrame + Rate / 5 : toneBegins >= 0 ? toneBegins + Rate / 5 : frames;
        var end = (int)Math.Min(frames - Rate / 10, (long)countToFrame);
        var maxStep = 2.5 * Simulation.ToneLevel * 2 * Math.PI * Simulation.ToneHz * expectedToneRatio / Rate;
        var expectedRms = Simulation.ToneLevel / Math.Sqrt(2);
        var events = new List<int>();
        var quietMs = 0;
        var stepClicks = 0;
        for (var b = start; b + 48 <= end; b += 48)
        {
            double sum = 0;
            for (var i = b; i < b + 48; i++) sum += interleaved[2 * i] * interleaved[2 * i];
            if (Math.Sqrt(sum / 48) < 0.1 * expectedRms) { quietMs++; events.Add(b); }
        }
        for (var i = Math.Max(start, 1); i < end; i++)
        {
            if (Math.Abs(interleaved[2 * i] - interleaved[2 * i - 2]) > maxStep) { stepClicks++; events.Add(i); }
        }
        // A gap too short, or placed too awkwardly, to empty a whole millisecond block: a millisecond of silence that starts
        // and ends on the tone's zero crossings and straddles two blocks leaves each block half full and makes no step, and
        // went unheard (found by the analyser's own step, 2026-09-28). A clean tone at half scale is within 0.01 of zero for
        // under a sample at each crossing, so a quarter of a millisecond that near zero is a gap wherever it falls.
        var nearZero = 0;
        for (var i = start; i < end; i++)
        {
            if (Math.Abs(interleaved[2 * i]) < 0.01f) { if (++nearZero == Rate / 4000) events.Add(i - nearZero + 1); }
            else nearZero = 0;
        }
        events.Sort();
        var glitches = 0;
        var lastEvent = -Rate; // well before the first sample, without the overflow int.MinValue gave (it hid a lone glitch)
        foreach (var e in events)
        {
            if (e - lastEvent > Rate / 100) glitches++; // events within 10 ms are one glitch
            lastEvent = e;
        }

        // Pitch and noise, 100 ms at a time, over windows with no glitch in them.
        var window = Rate / 10;
        var pitches = new List<double>();
        var snrs = new List<double>();
        var f0 = Simulation.ToneHz * expectedToneRatio;
        for (var w = start; w + window <= end; w += window)
        {
            var hasGlitch = events.Any(e => e >= w && e < w + window);
            // Upward zero crossings, interpolated.
            double first = -1, last = -1;
            var crossings = 0;
            for (var i = w + 1; i < w + window; i++)
            {
                var a = interleaved[2 * i - 2];
                var c = interleaved[2 * i];
                if (a < 0 && c >= 0)
                {
                    var at = i - 1 + a / (a - c);
                    if (first < 0) first = at;
                    last = at;
                    crossings++;
                }
            }
            if (crossings >= 3 && !hasGlitch)
            {
                var hz = (crossings - 1) / ((last - first) / Rate);
                pitches.Add(1200 * Math.Log2(hz / f0));
            }
            // Least-squares fit of a sine and cosine at the measured (or expected) pitch, plus DC.
            var fit = crossings >= 3 ? (crossings - 1) / ((last - first) / Rate) : f0;
            double ss = 0, cc = 0, sc = 0, xs = 0, xc = 0, x2 = 0;
            for (var i = 0; i < window; i++)
            {
                var ph = 2 * Math.PI * fit * i / Rate;
                double s1 = Math.Sin(ph), c1 = Math.Cos(ph), x = interleaved[2 * (w + i)];
                ss += s1 * s1; cc += c1 * c1; sc += s1 * c1; xs += x * s1; xc += x * c1; x2 += x * x;
            }
            var det = ss * cc - sc * sc;
            if (det > 0 && x2 > 0)
            {
                var a1 = (xs * cc - xc * sc) / det;
                var b1 = (xc * ss - xs * sc) / det;
                var fitted = a1 * xs + b1 * xc;
                var residual = Math.Max(x2 - fitted, 1e-12);
                snrs.Add(10 * Math.Log10(Math.Max(fitted, 1e-12) / residual));
            }
        }

        var firstDelay = delays.Count > 0 ? delays[0] : double.NaN;
        var lastDelay = delays.Count > 0 ? delays[^1] : double.NaN;
        delays.Sort();
        string? firstGlitchAt = events.Count > 0 ? $"{TimeOf(events[0]):0.000} s" : null;
        return new Findings(
            delays.Count,
            delays.Count > 0 ? delays[delays.Count / 2] : double.NaN,
            delays.Count > 0 ? delays[0] : double.NaN,
            delays.Count > 0 ? delays[^1] : double.NaN,
            firstDelay, lastDelay,
            glitches, quietMs, stepClicks,
            pitches.Count > 0 ? pitches.Max(Math.Abs) : double.NaN,
            snrs.Count > 0 ? snrs.OrderBy(x => x).ElementAt(snrs.Count / 2) : double.NaN,
            snrs.Count > 0 ? snrs.Min() : double.NaN,
            Math.Max(0, (end - start) / (double)Rate),
            firstGlitchAt,
            // The pitch most of the time: the worst 100 ms alone can be one slice beside a repaired gap.
            pitches.Count > 0 ? pitches.Select(Math.Abs).OrderBy(x => x).ElementAt(Math.Min(pitches.Count - 1, (int)(pitches.Count * 0.95))) : double.NaN,
            pitches.Count > 0 ? pitches.Select(Math.Abs).OrderBy(x => x).ElementAt(pitches.Count / 2) : double.NaN);
    }
}
