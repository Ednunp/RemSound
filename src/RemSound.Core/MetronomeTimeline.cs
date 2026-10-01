using System.Diagnostics;

namespace RemSound.Core;

/// <summary>A metronome's time signature: beats in a bar over the note that is one beat. The beat is the lower number's note
/// (6/8 at 120 is eighth notes at 120 a minute), and the first beat of every bar is accented.</summary>
public readonly record struct MetronomeSignature(byte BeatsPerBar, byte BeatUnit)
{
    /// <summary>What the settings window offers, in its order (Ed, 2026-09-28).</summary>
    public static readonly MetronomeSignature[] Offered =
        [new(2, 4), new(3, 4), new(4, 4), new(5, 4), new(6, 8), new(7, 8), new(9, 8), new(12, 8)];

    public static MetronomeSignature Default => new(4, 4);

    /// <summary>Anything a peer may send: 1 to 32 beats a bar, a power-of-two note from a whole note to a 32nd.</summary>
    public bool IsValid => BeatsPerBar is >= 1 and <= 32 && BeatUnit is 1 or 2 or 4 or 8 or 16 or 32;

    public override string ToString() => $"{BeatsPerBar}/{BeatUnit}";

    public static bool TryParse(string? text, out MetronomeSignature signature)
    {
        signature = Default;
        var parts = (text ?? "").Trim().Split('/');
        if (parts.Length != 2 || !byte.TryParse(parts[0], out var beats) || !byte.TryParse(parts[1], out var unit)) return false;
        var parsed = new MetronomeSignature(beats, unit);
        if (!parsed.IsValid) return false;
        signature = parsed;
        return true;
    }
}

/// <summary>
/// THE METRONOME'S TIMELINE (Ed, 2026-09-28: "all your metronomes start at the same time ... they all have to stay in
/// synch"). A deterministic timeline on one clock - the master's, or this machine's for a solo metronome - in microseconds:
/// beat n is at <c>AnchorTimeUs + (n - AnchorBeat) * 60e6 / bpm</c>. Nothing about it accumulates: every beat is worked
/// out from the anchor, so a follower that joins late computes the current beat and is simply in time.
///
/// <para>The anchor is always a bar line, so the first beat of every bar is <c>(n - AnchorBeat) mod beatsPerBar == 0</c>.
/// Start puts beat 0 <see cref="LeadUs"/> ahead, so the message reaches everyone in time, even over the internet. A tempo or
/// signature change takes effect at the next bar line at least that far ahead, and the beat count carries on through it.
/// Every change raises <see cref="Version"/>, which is what a follower orders them by.</para>
/// </summary>
public sealed record MetronomeTimeline(uint Version, bool Running, uint MilliBpm, byte BeatsPerBar, byte BeatUnit, long AnchorBeat, long AnchorTimeUs)
{
    public const int MinBpm = 30;
    public const int MaxBpm = 300;
    public const int DefaultBpm = 120;

    /// <summary>How far ahead a start or a change lands: time for the message to reach every follower, over the internet
    /// too, and for every output's own delay (the first click has to be placed before its moment comes).</summary>
    public const long LeadUs = 400_000;

    public double BeatIntervalUs => 60_000_000_000.0 / MilliBpm;

    public MetronomeSignature Signature => new(BeatsPerBar, BeatUnit);

    /// <summary>Whole beats a minute, for the window and the log.</summary>
    public double Bpm => MilliBpm / 1000.0;

    /// <summary>A tempo in range, a real signature: what a follower accepts from the wire.</summary>
    public bool IsValid => MilliBpm is >= MinBpm * 1000 and <= MaxBpm * 1000 && Signature.IsValid;

    public double BeatTimeUs(long beat) => AnchorTimeUs + (beat - AnchorBeat) * BeatIntervalUs;

    /// <summary>The first beat at or after a moment (beats before the anchor are counted the same way; whether they belong
    /// to this timeline is the caller's question).</summary>
    public long BeatAtOrAfter(double timeUs) => AnchorBeat + (long)Math.Ceiling((timeUs - AnchorTimeUs) / BeatIntervalUs - 1e-9);

    /// <summary>Where a beat falls in its bar, from 1.</summary>
    public int BeatInBar(long beat) => (int)(((beat - AnchorBeat) % BeatsPerBar + BeatsPerBar) % BeatsPerBar) + 1;

    /// <summary>The first beat of a bar is the accent.</summary>
    public bool IsAccent(long beat) => BeatInBar(beat) == 1;

    public static MetronomeTimeline Stopped(uint milliBpm = DefaultBpm * 1000, MetronomeSignature? signature = null, uint version = 0)
    {
        var sig = signature ?? MetronomeSignature.Default;
        return new MetronomeTimeline(version, false, milliBpm, sig.BeatsPerBar, sig.BeatUnit, 0, 0);
    }

    /// <summary>Start: beat 0 <see cref="LeadUs"/> from now. Already running, nothing changes.</summary>
    public MetronomeTimeline Start(long nowUs) => Running ? this : this with { Version = Version + 1, Running = true, AnchorBeat = 0, AnchorTimeUs = nowUs + LeadUs };

    public MetronomeTimeline Stop() => Running ? this with { Version = Version + 1, Running = false } : this;

    /// <summary>A new tempo or signature. Stopped, it is simply kept for the next start. Running, it takes effect at the first
    /// bar line at least <see cref="LeadUs"/> ahead, the beat count carrying on through it. The same values: nothing changes.</summary>
    public MetronomeTimeline Change(long nowUs, uint milliBpm, MetronomeSignature signature)
    {
        if (milliBpm == MilliBpm && signature == Signature) return this;
        if (!Running) return this with { Version = Version + 1, MilliBpm = milliBpm, BeatsPerBar = signature.BeatsPerBar, BeatUnit = signature.BeatUnit };
        var beat = BeatAtOrAfter(nowUs + LeadUs);
        var intoBar = ((beat - AnchorBeat) % BeatsPerBar + BeatsPerBar) % BeatsPerBar;
        if (intoBar != 0) beat += BeatsPerBar - intoBar;
        return new MetronomeTimeline(Version + 1, true, milliBpm, signature.BeatsPerBar, signature.BeatUnit, beat, (long)Math.Round(BeatTimeUs(beat)));
    }

    public string Describe() => Running
        ? $"playing {Bpm:0.###} bpm {Signature}, beat {AnchorBeat} at {AnchorTimeUs / 1000.0:0.0} ms (version {Version})"
        : $"stopped, {Bpm:0.###} bpm {Signature} (version {Version})";
}

/// <summary>
/// One machine's clock against the master's: <c>master = local + OffsetUs + Skew * (local - RefLocalUs)</c>. The identity
/// for the master itself and for a solo metronome. Immutable, so the audio threads can read a whole one at once.
/// </summary>
public sealed record MetronomeClockMap(double OffsetUs, double Skew, double RefLocalUs)
{
    public static MetronomeClockMap Identity { get; } = new(0, 0, 0);

    public double LocalToMaster(double localUs) => localUs + OffsetUs + Skew * (localUs - RefLocalUs);

    public double MasterToLocal(double masterUs) => (masterUs - OffsetUs + Skew * RefLocalUs) / (1 + Skew);
}

/// <summary>This machine's monotonic clock in microseconds (the Stopwatch), without the overflow a plain
/// <c>ticks * 1e6 / frequency</c> reaches after a few weeks of uptime.</summary>
public static class MetronomeClock
{
    private static readonly long Frequency = Stopwatch.Frequency;

    public static long NowUs() => ToUs(Stopwatch.GetTimestamp());

    public static long ToUs(long ticks) => ticks / Frequency * 1_000_000 + ticks % Frequency * 1_000_000 / Frequency;
}

/// <summary>
/// THE CLOCK ESTIMATE, NTP's method (PLAN-metronome-2026-09-28, "Timing" 2). A follower sends a request stamped t0 on its own
/// clock; the master stamps t1 as the datagram is read and t2 just before it replies; the follower stamps t3 as the reply
/// is read. Then offset = ((t1 - t0) + (t2 - t3)) / 2 (master minus local) and delay = (t3 - t0) - (t2 - t1).
///
/// <para>Queueing only ever ADDS delay, and a sample delayed on one leg only is wrong by half the extra, so the offset is
/// the one from the smallest-delay sample of the last 32 - a burst of late replies is never chosen. Nor does one push the
/// good samples out: a reply far later than the best (<see cref="OutlierFactor"/>) is kept out of the 32 unless
/// <see cref="OutliersToAccept"/> come in a row, which is the path itself having changed. The two clocks also run
/// at slightly different rates (tens of ppm between two crystals, a millisecond every half a minute or so), which a least-
/// squares fit of the best sample in each 8 s block, over up to five minutes, measures; the chosen sample's offset is then
/// carried to now at that rate. Until the fit has 16 s of blocks the rate is taken as equal, and only samples of the last
/// 8 s are chosen from, so an unknown rate can move the answer by no more than 8 s of it.</para>
///
/// <para>What no method can remove is a path that is slower one way than the other: that shifts the answer by half the
/// difference. Well under a millisecond on a LAN; a few over the internet.</para>
/// </summary>
public sealed class MetronomeClockEstimator
{
    public const int RecentSamples = 32;
    public const long BlockUs = 8_000_000;
    public const long FitWindowUs = 300_000_000;
    /// <summary>Until the rate is known, only samples this recent are chosen from.</summary>
    public const long UnfittedAgeUs = 8_000_000;
    /// <summary>A reply this late is not a clock reading (a stall, a re-sent packet).</summary>
    public const long MaxDelayUs = 2_000_000;
    /// <summary>A reply more than this many times the best delay (and at least 2 ms over it) is an outlier.</summary>
    public const double OutlierFactor = 2;
    public const int OutliersToAccept = 16;
    private int outliersInARow;

    private readonly (double T, double Offset, double Delay)[] recent = new (double, double, double)[RecentSamples];
    private int recentCount;
    private int recentNext;
    private readonly List<(long Block, double T, double Offset, double Delay)> blocks = [];
    private double skew;
    private bool fitted;

    /// <summary>Samples taken since the last reset.</summary>
    public int Samples { get; private set; }

    /// <summary>The clocks' rate difference, in parts per million (0 until measured).</summary>
    public double SkewPpm => skew * 1e6;

    public bool SkewMeasured => fitted;

    /// <summary>The smallest round trip among the samples the estimate chooses from, in microseconds.</summary>
    public double BestDelayUs { get; private set; }

    /// <summary>Enough to play by: four samples.</summary>
    public bool Settled => Samples >= 4;

    public MetronomeClockMap Map { get; private set; } = MetronomeClockMap.Identity;

    public void Reset()
    {
        recentCount = recentNext = 0;
        outliersInARow = 0;
        blocks.Clear();
        skew = 0;
        fitted = false;
        Samples = 0;
        BestDelayUs = 0;
        Map = MetronomeClockMap.Identity;
    }

    /// <summary>One exchange. False (and nothing learnt) when it cannot be a real one: a negative or absurd delay.</summary>
    public bool Add(long t0, long t1, long t2, long t3)
    {
        double delay = (t3 - t0) - (double)(t2 - t1);
        if (delay < 0 || delay > MaxDelayUs || t3 < t0) return false;
        var offset = ((t1 - t0) + (double)(t2 - t3)) / 2;
        var t = (t0 + (double)t3) / 2;
        Samples++;
        var ringBest = double.MaxValue;
        for (var i = 0; i < recentCount; i++) ringBest = Math.Min(ringBest, recent[i].Delay);
        var outlier = recentCount >= 4 && delay > Math.Max(ringBest * OutlierFactor, ringBest + 2000);
        outliersInARow = outlier ? outliersInARow + 1 : 0;
        if (!outlier || outliersInARow >= OutliersToAccept)
        {
            recent[recentNext] = (t, offset, delay);
            recentNext = (recentNext + 1) % RecentSamples;
            if (recentCount < RecentSamples) recentCount++;
        }

        var block = (long)Math.Floor(t / BlockUs);
        var last = blocks.Count - 1;
        if (last >= 0 && blocks[last].Block == block)
        {
            if (delay < blocks[last].Delay) blocks[last] = (block, t, offset, delay);
        }
        else blocks.Add((block, t, offset, delay));
        while (blocks.Count > 0 && t - blocks[0].T > FitWindowUs) blocks.RemoveAt(0);
        FitSkew();
        Choose(t);
        return true;
    }

    /// <summary>The rate: a least-squares line through each block's best sample, leaving out blocks whose best is well
    /// over the best of all (a whole block of late replies would pull the line by half its lateness).</summary>
    private void FitSkew()
    {
        if (blocks.Count < 3 || blocks[^1].T - blocks[0].T < 2 * BlockUs) return;
        var minDelay = blocks.Min(b => b.Delay);
        var limit = minDelay + Math.Max(1000, 0.25 * minDelay);
        double n = 0, sx = 0, sy = 0, sxx = 0, sxy = 0;
        var t0 = blocks[0].T;
        foreach (var b in blocks)
        {
            if (b.Delay > limit) continue;
            var x = b.T - t0;
            n++; sx += x; sy += b.Offset; sxx += x * x; sxy += x * b.Offset;
        }
        var den = n * sxx - sx * sx;
        if (n < 3 || den <= 0) return;
        var slope = (n * sxy - sx * sy) / den;
        // Two crystals are never 1,000 ppm apart; a slope that says so is noise over too short a span.
        if (Math.Abs(slope) > 1e-3) return;
        skew = slope;
        fitted = true;
    }

    private void Choose(double now)
    {
        var bestIndex = -1;
        for (var i = 0; i < recentCount; i++)
        {
            if (!fitted && now - recent[i].T > UnfittedAgeUs) continue;
            // The smallest delay; of equal ones the newest, which has least of the rate still to carry.
            if (bestIndex < 0 || recent[i].Delay < recent[bestIndex].Delay
                || recent[i].Delay == recent[bestIndex].Delay && recent[i].T > recent[bestIndex].T) bestIndex = i;
        }
        if (bestIndex < 0) return;
        var best = recent[bestIndex];
        BestDelayUs = best.Delay;
        Map = new MetronomeClockMap(best.Offset, fitted ? skew : 0, best.T);
    }

    public string Describe() =>
        $"offset {Map.LocalToMaster(Map.RefLocalUs) - Map.RefLocalUs:0} us, best delay {BestDelayUs / 1000.0:0.000} ms, "
        + $"skew {(fitted ? $"{SkewPpm:+0.0;-0.0} ppm" : "not yet measured")}, {Samples} samples";
}
