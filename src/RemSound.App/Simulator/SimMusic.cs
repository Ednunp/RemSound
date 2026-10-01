namespace RemSound.App.Simulator;

/// <summary>
/// A MUSIC-LIKE SIGNAL, and the listening that goes with it (2026-09-30, the simulator review's eighth improvement).
///
/// <para>The steady tone says exactly when something broke, and nothing about how much it would be heard: a gap is a
/// gap. Music is not steady - it has notes that start sharply, notes that die away, quiet passages and rests - and the
/// same 5 ms gap is plain in the middle of a held note, slight under a dying one, and nothing at all in a rest. So
/// beside the tone there is this: a four-second bar, the same every time, on the left channel (the delay clicks stay on
/// the right). Three plucked notes that start sharply and die away, a held note, a quiet note 24 dB down, and a rest.</para>
///
/// <para>The listening knows the bar, so it knows what should be coming out at every moment (the clicks say how late).
/// It compares the level actually heard, 2.5 ms at a time, with what the bar has there, and finds three kinds of fault:
/// a DIP (the sound went missing or dropped), ADDED SOUND (something louder than the music has there - the noise of a
/// repair, heard best in a rest, which a steady tone can never show), and a CLICK (a jump between samples the bar
/// cannot make). Each fault is placed - a loud passage, a quiet passage, a rest; on a note's attack or not - and given
/// how deep it went and how loud the thing lost or added was, against the loudest passage. From those: plain to hear,
/// or slight.</para>
/// </summary>
internal static class SimMusic
{
    /// <summary>One note of the bar: when it starts and how long it is (seconds into the bar), its pitch, its level (peak,
    /// full scale 1), and whether it is plucked (dies away) or held.</summary>
    public sealed record Note(double Start, double Length, double Hz, double Level, bool Plucked);

    public const double BarSeconds = 4;

    /// <summary>The bar. From 3.3 s through the first tenth of the next bar: a rest. Every note starts a tenth of a second
    /// after a delay click (they come every half second on the other channel), never on one: a codec codes the two
    /// channels together, and a note's attack should be heard for itself, not beside the loudest thing in the run.</summary>
    public static readonly IReadOnlyList<Note> Bar =
    [
        new(0.10, 0.45, 440.00, 0.50, Plucked: true),
        new(0.60, 0.45, 554.37, 0.50, Plucked: true),
        new(1.10, 0.45, 659.25, 0.50, Plucked: true),
        new(1.60, 0.90, 880.00, 0.40, Plucked: false),
        new(2.60, 0.70, 220.00, 0.03, Plucked: false),
    ];

    private const double AttackSec = 0.002;    // every note reaches its level in 2 ms: a sharp start
    private const double ReleaseSec = 0.010;   // and is let go over 10 ms, so its end is not itself a click
    private const double DecaySec = 0.15;      // a plucked note falls to a third every 150 ms
    private const int Rate = Simulation.SampleRate;

    /// <summary>The level of the loudest passage: a note at half scale, as the listening measures level (RMS; a note is its
    /// pitch and a fifth as much of the octave above).</summary>
    public static readonly double FullLevel = 0.5 * Math.Sqrt((0.8 * 0.8 + 0.2 * 0.2) / 2);

    /// <summary>The bar's sound at a moment of its own time (seconds; it repeats every <see cref="BarSeconds"/>).</summary>
    public static float Sample(double t)
    {
        var inBar = t - Math.Floor(t / BarSeconds) * BarSeconds;
        foreach (var n in Bar)
        {
            var x = inBar - n.Start;
            if (x < 0 || x >= n.Length) continue;
            var envelope = Math.Min(1, x / AttackSec) * Math.Min(1, (n.Length - x) / ReleaseSec);
            if (n.Plucked) envelope *= Math.Exp(-x / DecaySec);
            var phase = 2 * Math.PI * n.Hz * x;
            return (float)(n.Level * envelope * (0.8 * Math.Sin(phase) + 0.2 * Math.Sin(2 * phase)));
        }
        return 0f;
    }

    public enum Kind { Dip, Added, Click }

    /// <summary>One fault heard in the music.</summary>
    /// <param name="AtSec">When it was heard.</param>
    /// <param name="Ms">About how long it lasted.</param>
    /// <param name="Passage">"a loud passage", "a quiet passage" or "a rest".</param>
    /// <param name="OnAttack">It fell on a note's start.</param>
    /// <param name="DepthDb">A dip: how far below what the music has there the level fell (negative). Otherwise 0.</param>
    /// <param name="FaultDb">How loud the thing lost (a dip) or added (added sound, a click) was, against the loudest passage.</param>
    /// <param name="Audible">"plain" or "slight".</param>
    public sealed record Fault(double AtSec, double Ms, Kind Kind, string Passage, bool OnAttack, double DepthDb, double FaultDb, string Audible)
    {
        public override string ToString() =>
            $"at {AtSec:0.000} s {Kind switch { Kind.Dip => $"a dip of {Ms:0} ms, {-DepthDb:0} dB deep", Kind.Added => $"{Ms:0} ms of added sound", _ => "a click" }}"
            + $" in {Passage}{(OnAttack ? ", on a note's attack" : "")}; what was {(Kind == Kind.Dip ? "lost" : "added")} is {Math.Abs(FaultDb):0} dB {(FaultDb > 0.5 ? "above" : "below")} the loudest passage: {Audible}{(Audible == "plain" ? " to hear" : "")}";
    }

    /// <summary>What the listening found.</summary>
    /// <param name="JudgedSeconds">How much of the music could be judged: not a stretch where the delay was moving too fast
    /// to say what should be coming out.</param>
    public sealed record Findings(int ClicksHeard, double DelayMedianMs, IReadOnlyList<Fault> Faults, double JudgedSeconds)
    {
        public int Plain => Faults.Count(f => f.Audible == "plain");
        public int Slight => Faults.Count(f => f.Audible == "slight");
        public int Dips => Faults.Count(f => f.Kind == Kind.Dip);
        public int Added => Faults.Count(f => f.Kind == Kind.Added);
        public int Clicks => Faults.Count(f => f.Kind == Kind.Click);
        public int InLoud => Faults.Count(f => f.Passage == LoudPassage);
        public int InQuiet => Faults.Count(f => f.Passage == QuietPassage);
        public int InRests => Faults.Count(f => f.Passage == Rest);
        /// <summary>Heard, and not one fault.</summary>
        public bool Clean => ClicksHeard > 0 && JudgedSeconds > 0 && Faults.Count == 0;

        public string Describe() => ClicksHeard == 0 ? "NOTHING HEARD"
            : Faults.Count == 0 ? $"the music whole ({JudgedSeconds:0.0} s judged)"
            : $"{Faults.Count} faults in the music - {Plain} plain to hear, {Slight} slight; {Dips} dips, {Added} of added sound, {Clicks} clicks; "
              + $"{InLoud} in loud passages, {InQuiet} in quiet ones, {InRests} in rests; first: {Faults[0]}";
    }

    public const string LoudPassage = "a loud passage";
    public const string QuietPassage = "a quiet passage";
    public const string Rest = "a rest";

    // The listening's measures.
    private const int Window = 120;                 // 2.5 ms of level
    private const int Hop = 48;                     // taken every millisecond
    private const double GridSec = 0.0005;          // what the bar has, worked out every half millisecond
    private const double LevelFloor = 0.004;        // 37 dB below the loudest passage: under this there is nothing to lose
    private const double DipBelow = 0.6;            // a dip: under 0.6 of the least the music has there
    private const double AddedAbove = 2.0;          // added sound: over twice the most the music has there (and the floor)
    private const double JumpAbove = 2.5;           // a click: a jump over 2.5 times the biggest the music makes there
    private const double JumpFloor = 0.02;
    private const double ToleranceSec = 0.003;      // how far out the placing may be: the delay between clicks, the codec's own smear
    private const double UnjudgedBeyondSec = 0.020; // the delay moving faster than this between two clicks: nothing can be said
    private const double LoudFromDb = -12;          // a passage within 12 dB of the loudest is loud

    private static readonly Lazy<(double[] Level, double[] Step)> Reference = new(() =>
    {
        var count = (int)Math.Round(BarSeconds / GridSec);
        var level = new double[count];
        var step = new double[count];
        for (var i = 0; i < count; i++)
        {
            double sum = 0, biggest = 0;
            var before = Sample(i * GridSec - 1.0 / Rate);
            for (var k = 0; k < Window; k++)
            {
                var x = Sample(i * GridSec + k / (double)Rate);
                sum += x * x;
                biggest = Math.Max(biggest, Math.Abs(x - before));
                before = x;
            }
            level[i] = Math.Sqrt(sum / Window);
            step[i] = biggest;
        }
        return (level, step);
    });

    /// <summary>What the bar has around a moment of its own time, give or take <paramref name="tolerance"/>: the least and
    /// the most level, and the biggest jump between two samples.</summary>
    private static (double Least, double Most, double Step) BarAround(double musicTime, double tolerance)
    {
        var (level, step) = Reference.Value;
        var count = level.Length;
        var centre = (long)Math.Round(musicTime / GridSec);
        var reach = (int)Math.Ceiling(tolerance / GridSec);
        double least = double.MaxValue, most = 0, jump = 0;
        for (var d = -reach; d <= reach; d++)
        {
            var i = (int)(((centre + d) % count + count) % count);
            least = Math.Min(least, level[i]);
            most = Math.Max(most, level[i]);
            jump = Math.Max(jump, step[i]);
        }
        return (least, most, jump);
    }

    private static bool OnAnAttack(double musicTime)
    {
        var inBar = musicTime - Math.Floor(musicTime / BarSeconds) * BarSeconds;
        return Bar.Any(n => inBar >= n.Start - 0.005 && inBar < n.Start + 0.030);
    }

    /// <param name="interleaved">Output, stereo interleaved: the music on the left, the delay clicks on the right.</param>
    /// <param name="blockTimes">For each output block: its first frame, and the time that frame is heard.</param>
    /// <param name="sendRate">The sending card's real sample rate (drift included), which timed the music and the clicks.</param>
    /// <param name="fromSecond">Judge from this second of the output on.</param>
    public static Findings Analyse(IReadOnlyList<float> interleaved, IReadOnlyList<(int Frame, double Time)> blockTimes, double sendRate, double fromSecond = 0)
    {
        var frames = interleaved.Count / 2;
        var clicks = SimAnalysis.Clicks(interleaved, blockTimes, sendRate);
        if (clicks.Count == 0) return new Findings(0, double.NaN, [], 0);
        var delays = clicks.Select(c => c.DelayMs).OrderBy(d => d).ToList();

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

        var start = Math.Max((int)(fromSecond * Rate), clicks[0].Frame + Rate / 5);
        var end = frames - Rate / 10;
        var marked = new List<(int Frame, Kind Kind, double DepthDb, double Fault, double Most, double MusicTime)>();
        var judged = 0;
        var nearest = 0;
        for (var b = Math.Max(start, 1); b + Window <= end; b += Hop)
        {
            while (nearest + 1 < clicks.Count && Math.Abs(clicks[nearest + 1].Frame - b) <= Math.Abs(clicks[nearest].Frame - b)) nearest++;
            var click = clicks[nearest];
            // The delay here is the nearest click's; where it is moving, the placing is out by up to the move.
            var other = click.Frame <= b ? (nearest + 1 < clicks.Count ? clicks[nearest + 1] : click) : nearest > 0 ? clicks[nearest - 1] : click;
            var moving = Math.Abs(other.DelayMs - click.DelayMs) / 1000;
            if (moving > UnjudgedBeyondSec) continue;
            var tolerance = ToleranceSec + moving;
            var musicTime = (TimeOf(b) - click.DelayMs / 1000) * sendRate / Rate;
            var (least, most, step) = BarAround(musicTime, tolerance);

            double sum = 0, jump = 0;
            for (var i = b; i < b + Window; i++)
            {
                var x = interleaved[2 * i];
                sum += x * x;
                jump = Math.Max(jump, Math.Abs(x - interleaved[2 * i - 2]));
            }
            var level = Math.Sqrt(sum / Window);
            judged++;
            if (least >= LevelFloor && level < DipBelow * least)
                marked.Add((b, Kind.Dip, 20 * Math.Log10(Math.Max(level, 1e-5) / least), least, most, musicTime));
            else if (level > AddedAbove * most + LevelFloor)
                marked.Add((b, Kind.Added, 0, level, most, musicTime));
            else if (jump > JumpAbove * step + JumpFloor)
                // A jump's loudness: as a level, against the loudest passage's peak.
                marked.Add((b, Kind.Click, 0, jump * FullLevel / 0.5, most, musicTime));
        }

        // Marks within 10 ms of each other are one fault: a dip if any of it is a dip, else added sound, else a click.
        var faults = new List<Fault>();
        for (var i = 0; i < marked.Count;)
        {
            var j = i;
            while (j + 1 < marked.Count && marked[j + 1].Frame - marked[j].Frame <= Rate / 100) j++;
            var part = marked.GetRange(i, j - i + 1);
            var kind = part.Any(m => m.Kind == Kind.Dip) ? Kind.Dip : part.Any(m => m.Kind == Kind.Added) ? Kind.Added : Kind.Click;
            var ofKind = part.Where(m => m.Kind == kind).ToList();
            var most = part.Max(m => m.Most);
            var passage = most < LevelFloor ? Rest : 20 * Math.Log10(most / FullLevel) >= LoudFromDb ? LoudPassage : QuietPassage;
            var ms = (part[^1].Frame - part[0].Frame) * 1000.0 / Rate + 1;
            var depthDb = kind == Kind.Dip ? ofKind.Min(m => m.DepthDb) : 0;
            var faultDb = 20 * Math.Log10(Math.Max(ofKind.Max(m => m.Fault), 1e-6) / FullLevel);
            faults.Add(new Fault(TimeOf(part[0].Frame), ms, kind, passage, part.Any(m => OnAnAttack(m.MusicTime)), depthDb, faultDb,
                HowAudible(kind, passage, faultDb, ms)));
            i = j + 1;
        }
        return new Findings(clicks.Count, delays[delays.Count / 2], faults, judged * (double)Hop / Rate);
    }

    /// <summary>Plain to hear, or slight. A dip in a loud passage of 2 ms or more is plain; a shorter one, or one in a quiet
    /// passage, slight (and in a rest there is nothing to lose). Added sound is plain when it is within 30 dB of the loudest
    /// passage - nothing in the music is covering it, or it would not have been found. A click is plain within 20 dB of the
    /// loudest passage, or within 30 dB where the music is not loud.</summary>
    internal static string HowAudible(Kind kind, string passage, double faultDb, double ms) => kind switch
    {
        Kind.Dip => passage == LoudPassage && ms >= 2 ? "plain" : "slight",
        Kind.Added => faultDb >= -30 ? "plain" : "slight",
        _ => faultDb >= -20 || (passage != LoudPassage && faultDb >= -30) ? "plain" : "slight",
    };
}
