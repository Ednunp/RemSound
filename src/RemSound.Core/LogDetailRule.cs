namespace RemSound.Core;

/// <summary>
/// WHEN A LINE MADE EVERY SECOND IS WRITTEN (2026-10-19, Jonathan Schuster's issue 35, "Make logging less verbose").
///
/// <para>The app's status row, diagnostic line, "streams" line and delay estimate, and the plugin's status row, are made
/// every second. Written every second they buried everything else: Ed's desktop wrote 35.7 MB in under five hours, 99.9%
/// of it those lines, while the events a reader is looking for came to 25 KB. Jonathan met a 24-million-character file
/// trying to find out why his laptop kept disconnecting.</para>
///
/// <para>What they are for decides when they are written. Slow problems - memory creeping, a buffer drifting overnight,
/// running totals - show just as well in one second's worth a minute. Fast ones - a gap, a peer dropping out - need every
/// second, but only around the moment. So one second goes out a minute, the last 30 seconds wait in memory, and when
/// something goes wrong (<see cref="Trouble"/>) they are written and the next 30 follow as they come. At most one of those
/// every ten minutes, so a machine breaking up all the time cannot write them non-stop; the minute lines still count what
/// happens in between, and the next one says how many it missed.</para>
///
/// <para>Not thread-safe: each log calls it holding its own write lock.</para>
/// </summary>
public sealed class LogDetailRule
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Before = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan After = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan BurstGap = TimeSpan.FromMinutes(10);
    /// <summary>The lines of one tick arrive within milliseconds of each other and ticks are about a second apart, so a
    /// line this soon after the last decision belongs to the same second and is treated the same way.</summary>
    private static readonly TimeSpan SameSecond = TimeSpan.FromMilliseconds(500);

    private readonly Queue<(DateTime At, string Line)> waiting = new();
    private DateTime secondStartedUtc = DateTime.MinValue;
    private bool writeThisSecond;
    private DateTime lastSampleUtc = DateTime.MinValue;
    private DateTime burstUntilUtc = DateTime.MinValue;
    private DateTime lastBurstUtc = DateTime.MinValue;
    private int missedSinceLastBurst;

    /// <summary>How many lines are waiting in memory (the gate's view).</summary>
    public int Waiting => waiting.Count;

    /// <summary>A line made this second, already stamped with its time: true when it is to be written now, false when it
    /// waits in case something goes wrong in the next 30 seconds.</summary>
    public bool Line(DateTime nowUtc, string line)
    {
        if (nowUtc - secondStartedUtc >= SameSecond || nowUtc < secondStartedUtc)
        {
            secondStartedUtc = nowUtc;
            var inBurst = nowUtc < burstUntilUtc;
            writeThisSecond = inBurst || lastSampleUtc == DateTime.MinValue || nowUtc - lastSampleUtc >= Every || nowUtc < lastSampleUtc;
            if (writeThisSecond && !inBurst)
            {
                lastSampleUtc = nowUtc;
                // Everything older than a line in the file leaves memory with it: a later burst writes only what came after
                // the last line written, so the file stays in time order.
                waiting.Clear();
            }
        }
        if (writeThisSecond) return true;
        waiting.Enqueue((nowUtc, line));
        while (waiting.Count > 0 && nowUtc - waiting.Peek().At > Before) waiting.Dequeue();
        return false;
    }

    /// <summary>Something went wrong. Null when nothing is to be written: already writing every second, or inside the gap
    /// since the last time (unless <paramref name="evenInsideTheGap"/> - the person asked). Otherwise the heading line's
    /// words and the waiting lines, oldest first, to write in that order; every line for the next 30 seconds is then
    /// written as it comes.</summary>
    public (string Heading, List<string> Lines)? Trouble(DateTime nowUtc, string why, bool evenInsideTheGap = false)
    {
        if (nowUtc < burstUntilUtc) return null;
        if (!evenInsideTheGap && lastBurstUtc != DateTime.MinValue && nowUtc >= lastBurstUtc && nowUtc - lastBurstUtc < BurstGap)
        {
            missedSinceLastBurst++;
            return null;
        }
        while (waiting.Count > 0 && nowUtc - waiting.Peek().At > Before) waiting.Dequeue();
        var seconds = waiting.Count > 0 ? (nowUtc - waiting.Peek().At).TotalSeconds : 0;
        var heading = $"detail: {why} - every second's lines from the {seconds:0} seconds before follow, then the next {After.TotalSeconds:0} as they come"
                    + (missedSinceLastBurst > 0
                        ? $" ({missedSinceLastBurst} more problem{(missedSinceLastBurst == 1 ? "" : "s")} since the last time, not written in full)"
                        : "");
        var lines = waiting.Select(w => w.Line).ToList();
        waiting.Clear();
        lastBurstUtc = nowUtc;
        burstUntilUtc = nowUtc + After;
        lastSampleUtc = nowUtc;
        secondStartedUtc = nowUtc;
        writeThisSecond = true;
        missedSinceLastBurst = 0;
        return (heading, lines);
    }

    /// <summary>Logging switched off: nothing waiting may reach the file later.</summary>
    public void Forget()
    {
        waiting.Clear();
        burstUntilUtc = DateTime.MinValue;
        writeThisSecond = false;
        secondStartedUtc = DateTime.MinValue;
    }
}
