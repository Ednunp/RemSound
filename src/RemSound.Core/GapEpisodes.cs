namespace RemSound.Core;

/// <summary>
/// RUNS OF AUDIBLE GAPS (2026-10-22, issue 35). 6.2.2 wrote a burst of detail for a gap at most once every ten minutes, so
/// a machine with a steady trickle of gaps - Ed's desktop, all day - sat at that limit all day and every burst told the
/// same story. Now a run of gaps is one EPISODE: it starts with the first gap after a quiet spell (a capture of the
/// seconds around it), gets a fresh capture only if a gap comes that is clearly longer than any before it, says once an
/// hour that it is still going, and ends after <see cref="QuietToEnd"/> without a gap with one summary line: how long, how
/// many, the longest, and the stream most of them were on.
/// </summary>
public sealed class GapEpisodes
{
    public static readonly TimeSpan QuietToEnd = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan StillGoingEvery = TimeSpan.FromHours(1);
    /// <summary>How much longer than the longest so far a gap must be to count as the run getting worse (a fresh capture).</summary>
    public const int WorseByMs = 20;

    public enum Kind { Started, Worse, StillGoing, Ended }
    public sealed record Change(Kind Kind, string Words);

    private bool active;
    private DateTime started, lastGap, lastStillGoing;
    private long gaps;
    private int longestMs;
    private readonly Dictionary<string, long> byStream = new(StringComparer.Ordinal);

    public bool Active => active;

    /// <summary>One second: the new gaps, the longest of them in ms (0 when not known), and which stream each was on.
    /// Returns what changed, in words for the log.</summary>
    public List<Change> Second(DateTime now, long newGaps, int longestGapMs, IReadOnlyDictionary<string, long>? gapsByStream = null)
    {
        var changes = new List<Change>();
        if (newGaps <= 0)
        {
            if (active && now - lastGap >= QuietToEnd)
            {
                changes.Add(new Change(Kind.Ended, $"gaps ended after {Duration(lastGap - started)}: {Count(gaps)}{Longest()}{MostOn()}"));
                active = false;
                byStream.Clear();
            }
            else if (active && now - lastStillGoing >= StillGoingEvery) StillGoing(now, changes);
            return changes;
        }

        if (gapsByStream is not null)
            foreach (var (stream, n) in gapsByStream)
                if (n > 0) byStream[stream] = byStream.GetValueOrDefault(stream) + n;
        if (!active)
        {
            active = true;
            started = lastStillGoing = now;
            gaps = newGaps;
            longestMs = longestGapMs;
            lastGap = now;
            changes.Add(new Change(Kind.Started, $"gaps started: {Count(newGaps)} this second{Longest()}{MostOn()}"));
            return changes;
        }
        gaps += newGaps;
        lastGap = now;
        if (longestGapMs >= longestMs + WorseByMs && longestMs > 0)
            changes.Add(new Change(Kind.Worse, $"gaps got worse: a {longestGapMs} ms gap, the longest in this run (it was {longestMs} ms)"));
        if (longestGapMs > longestMs) longestMs = longestGapMs;
        if (now - lastStillGoing >= StillGoingEvery) StillGoing(now, changes);
        return changes;
    }

    private void StillGoing(DateTime now, List<Change> changes)
    {
        lastStillGoing = now;
        changes.Add(new Change(Kind.StillGoing, $"gaps still going after {Duration(now - started)}: {Count(gaps)} so far{Longest()}{MostOn()}"));
    }

    private static string Count(long n) => $"{n:#,0} gap{(n == 1 ? "" : "s")}";
    private string Longest() => longestMs > 0 ? $", the longest {longestMs} ms" : "";
    private string MostOn()
    {
        if (byStream.Count == 0) return "";
        var top = byStream.MaxBy(kv => kv.Value);
        return byStream.Count == 1 ? $", on {top.Key}" : $", most on {top.Key} ({top.Value:#,0})";
    }

    /// <summary>A length of time in words: "12 seconds", "14 minutes", "2 hours 5 minutes".</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        var s = (int)Math.Round(span.TotalSeconds);
        if (s < 90) return $"{s} second{(s == 1 ? "" : "s")}";
        var m = (int)Math.Round(span.TotalMinutes);
        if (m < 90) return $"{m} minutes";
        var h = (int)span.TotalHours;
        var rest = span.Minutes;
        return rest == 0 ? $"{h} hour{(h == 1 ? "" : "s")}" : $"{h} hour{(h == 1 ? "" : "s")} {rest} minute{(rest == 1 ? "" : "s")}";
    }
}
