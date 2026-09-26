namespace RemSound.App;

/// <summary>
/// What a --headless copy has just logged, played and said, kept in memory for the control channel's log, cues and
/// speech commands. A headless copy has no speakers and no screen reader to hear, and its log file may be switched off:
/// without these, whoever drives it could not tell a cue fired or a line was written (2026-09-24).
///
/// <para>Filled only while <see cref="Windowless.Active"/>. An ordinary run keeps nothing here.</para>
/// </summary>
internal static class HeadlessRecords
{
    internal static readonly RecentLines Log = new(500);
    internal static readonly RecentLines Cues = new(100);
    internal static readonly RecentLines Speech = new(100);

    /// <summary>A line for one of the records, stamped with the time, when this is a headless copy.</summary>
    internal static void Note(RecentLines into, string line)
    {
        if (!Windowless.Active) return;
        into.Add($"{DateTime.Now:HH:mm:ss.fff} {line}");
    }
}

/// <summary>The last few lines of something, oldest first. Safe to add to from any thread.</summary>
internal sealed class RecentLines(int capacity)
{
    private readonly Queue<string> lines = new();
    private long added;

    /// <summary>How many lines have ever been added: a mark to ask later for only what came after it.</summary>
    public long Mark { get { lock (lines) return added; } }

    /// <summary>The lines added since <paramref name="mark"/>, as far as they are still kept.</summary>
    public List<string> Since(long mark)
    {
        lock (lines)
        {
            var fresh = (int)Math.Min(lines.Count, Math.Max(0, added - mark));
            return lines.Skip(lines.Count - fresh).ToList();
        }
    }

    public void Add(string line)
    {
        lock (lines)
        {
            added++;
            lines.Enqueue(line);
            while (lines.Count > capacity) lines.Dequeue();
        }
    }

    /// <summary>The newest <paramref name="count"/> lines containing <paramref name="filter"/> (any, when it is empty), oldest first.</summary>
    public List<string> Last(int count, string filter = "")
    {
        lock (lines)
        {
            var matching = lines.Where(l => filter.Length == 0 || l.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            return matching.Skip(Math.Max(0, matching.Count - count)).ToList();
        }
    }

    public void Clear() { lock (lines) lines.Clear(); }
}
