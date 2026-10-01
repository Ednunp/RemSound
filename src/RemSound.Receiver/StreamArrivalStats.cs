using System.Diagnostics;

namespace RemSound.Receiver;

/// <summary>
/// One stream's own packet-arrival timing, for the latency lab's per-stream buffers (2026-09-26). The receiver's
/// long-standing arrival gap (<see cref="ReceiverDiagnostics.RecordPacketArrived"/>) is ONE timestamp for every packet
/// of every stream, so a smooth ASIO stream arriving every 2.5 ms was sized by a WASAPI stream's 10 ms lumps (proved
/// from Ed's log of 2026-09-24: the sender's ASIO lane emitted every 3 ms, its WASAPI lane every 10). This keeps the
/// gaps of one stream only.
///
/// <para><see cref="Record"/> runs on whichever thread handled the packet; <see cref="TakeSecond"/> and the rest run
/// once a second on the tuning thread, which alone owns the history.</para>
/// </summary>
internal sealed class StreamArrivalStats
{
    /// <summary>How many seconds of per-second worst gaps are kept - the same lookback the auto-tune uses.</summary>
    public const int LookbackSeconds = 15;

    private long lastTicks;
    private long maxGapTicks;
    private long packets;

    private readonly int[] history = new int[LookbackSeconds];
    private int historyCount;
    private int historyNext;

    /// <summary>One audio packet of this stream has arrived.</summary>
    public void Record() => RecordAt(RemSound.Core.AudioClock.GetTimestamp());

    /// <summary>One packet arrived at <paramref name="now"/> (Stopwatch ticks). The gate drives this with a made-up clock
    /// so a test of the sizing does not depend on how finely Windows can sleep.</summary>
    internal void RecordAt(long now)
    {
        var prev = Interlocked.Exchange(ref lastTicks, now);
        Interlocked.Increment(ref packets);
        if (prev == 0) return;
        var gap = now - prev;
        long seen;
        do
        {
            seen = Interlocked.Read(ref maxGapTicks);
            if (gap <= seen) return;
        }
        while (Interlocked.CompareExchange(ref maxGapTicks, gap, seen) != seen);
    }

    /// <summary>Close the current second: its worst gap goes into the history (a second with no packets adds
    /// nothing). Returns that second's worst gap in ms and its packet count.</summary>
    public (int MaxGapMs, long Packets) TakeSecond()
    {
        var gapTicks = Interlocked.Exchange(ref maxGapTicks, 0);
        var count = Interlocked.Exchange(ref packets, 0);
        var gapMs = (int)Math.Ceiling(gapTicks * 1000.0 / Stopwatch.Frequency);
        if (count > 0)
        {
            history[historyNext] = gapMs;
            historyNext = (historyNext + 1) % LookbackSeconds;
            if (historyCount < LookbackSeconds) historyCount++;
        }
        return (gapMs, count);
    }

    /// <summary>Seconds of history held (up to <see cref="LookbackSeconds"/>).</summary>
    public int SecondsHeld => historyCount;

    /// <summary>The worst gap over the lookback, and the second worst - the auto-tune's lone-spike rejection: one bad
    /// second must repeat before it counts. With a single second held, both are that second.</summary>
    public (int Peak, int Used) Gaps()
    {
        int peak = 0, second = 0;
        for (var i = 0; i < historyCount; i++)
        {
            var g = history[i];
            if (g > peak) { second = peak; peak = g; }
            else if (g > second) { second = g; }
        }
        return (peak, historyCount >= 2 ? second : peak);
    }
}
