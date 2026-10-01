namespace RemSound.Core;

/// <summary>
/// The latency lab's "keep dropouts rare" auto-tune for the ASIO output (2026-09-26). The long-standing auto-tune sizes the
/// buffer to cover the worst gap between packets in the last 15 seconds, so on a machine that takes its packets in lumps
/// it sat at 24-32 ms while Ed, listening to music, heard it clean at 8 (live test, 2026-09-26). This one works the way
/// Jamulus does: it looks for the smallest buffer at which audible gaps stay rare, by COUNTING them.
///
/// <para>A gap is one run of short reads in the output (an ASIO callback is only 0.67 ms, so one 5 ms gap is about eight
/// short callbacks - counting callbacks is what made the old figures look alarming while the music sounded clean). It is
/// fed once a second with the gaps that second, and answers with the buffer to use:</para>
/// <list type="bullet">
/// <item>Three gaps in one second, or four in twenty seconds (about Jamulus's "one every five seconds"): up 2 ms at once
/// (a deeper buffer never clicks).</item>
/// <item>A gap, but fewer than that: hold.</item>
/// <item>Until it has met its first gap: two quiet seconds halve the distance to the floor - big steps while it is far
/// above (Ed, 2026-09-28: 40 minutes from 500 ms was absurd; half a minute to a minute). The buffer follows the way every
/// lowered target does: a snap from far above, then a click-free drain.</item>
/// <item>From its first gap on, feeling for the edge: five seconds without a gap, down 1 ms - or thirty seconds after it
/// last had to go up, so it does not bounce straight back into the edge.</item>
/// </list>
/// </summary>
public sealed class DropoutTuner
{
    public const int WindowSeconds = 20;
    public const int RaiseWhenGapsInWindow = 4;
    public const int RaiseWhenGapsInOneSecond = 3;
    public const int RaiseStepMs = 2;
    public const int QuietSecondsToLower = 5;
    public const int QuietSecondsToLowerAfterRaise = 30;
    /// <summary>Quiet seconds before a big step down, while it has not yet met a gap.</summary>
    public const int QuietSecondsToStepBig = 2;
    private bool metGap;

    private readonly Queue<int> window = new();
    private int gapsInWindow;
    private int quietSeconds;
    private bool sinceRaise;
    private int nextRaiseMs = RaiseStepMs;

    /// <summary>The largest single raise: when gaps keep coming straight after a raise, each raise doubles (2, 4, 8, 16 ms),
    /// so a machine that stalls hard is covered in a few steps instead of twenty. Raising never clicks.</summary>
    public const int MaxRaiseStepMs = 16;

    /// <summary>Why the last decision was made, for the log.</summary>
    public string LastReason { get; private set; } = "";

    /// <summary>Forget everything (a new session, the switch turned on, the user moving the buffer).</summary>
    public void Reset()
    {
        window.Clear();
        gapsInWindow = 0;
        quietSeconds = 0;
        sinceRaise = false;
        metGap = false;
        nextRaiseMs = RaiseStepMs;
        LastReason = "";
    }

    /// <summary>One second: <paramref name="gapsThisSecond"/> audible gaps, the buffer now, and its limits. Returns the buffer
    /// to use from here.</summary>
    public int Tick(int gapsThisSecond, int currentMs, int floorMs, int ceilingMs)
    {
        window.Enqueue(gapsThisSecond);
        gapsInWindow += gapsThisSecond;
        while (window.Count > WindowSeconds) gapsInWindow -= window.Dequeue();
        if (gapsThisSecond > 0) metGap = true;

        if (gapsThisSecond >= RaiseWhenGapsInOneSecond || gapsInWindow >= RaiseWhenGapsInWindow)
        {
            // Still in trouble within the wait after the last raise: that raise was not enough, so go up further this time.
            var step = sinceRaise ? nextRaiseMs : RaiseStepMs;
            nextRaiseMs = Math.Min(MaxRaiseStepMs, step * 2);
            LastReason = gapsThisSecond >= RaiseWhenGapsInOneSecond
                ? $"{gapsThisSecond} gaps in one second - up {step} ms"
                : $"{gapsInWindow} gaps in {window.Count} s - up {step} ms";
            window.Clear();
            gapsInWindow = 0;
            quietSeconds = 0;
            sinceRaise = true;
            return Math.Min(ceilingMs, currentMs + step);
        }
        if (gapsThisSecond > 0)
        {
            quietSeconds = 0;
            LastReason = "a gap, still rare - holding";
            return currentMs;
        }
        quietSeconds++;
        if (!metGap)
        {
            // Far above, not yet near any edge: halve the distance to the floor every couple of quiet seconds.
            if (quietSeconds < QuietSecondsToStepBig || currentMs <= floorMs) return currentMs;
            var next = Math.Max(floorMs, currentMs - Math.Max(1, (currentMs - floorMs) / 2));
            LastReason = $"{quietSeconds} s without a gap, none met yet - down {currentMs - next} ms";
            quietSeconds = 0;
            return next;
        }
        var needed = sinceRaise ? QuietSecondsToLowerAfterRaise : QuietSecondsToLower;
        if (quietSeconds >= needed && currentMs > floorMs)
        {
            LastReason = $"{quietSeconds} s without a gap - down 1 ms";
            quietSeconds = 0;
            sinceRaise = false;
            nextRaiseMs = RaiseStepMs;
            return currentMs - 1;
        }
        return currentMs;
    }
}
