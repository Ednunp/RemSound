namespace RemSound.Core;

/// <summary>
/// How deep a cushion a WASAPI output should hold, sized from that card's own pull.
///
/// <para><b>Why it is a seam and not three lines inline.</b> Ed, 2026-09-09: the overnight slowdown
/// happens on WASAPI and never on ASIO. The lanes differ in one place — this stage — and within it
/// this rule is the only thing that decides how much latency the stage deliberately holds. If
/// something adds latency over a night without anybody asking, this is where it would come from.</para>
///
/// <para>Inline, it can only be tested by running a real sound card for hours. Extracted, a soak test
/// can put ten thousand windows of realistic jitter through it in milliseconds and assert the thing
/// that actually matters: that it settles once and then stops moving. That is a different claim from
/// "it computes the right number for one input", and it is the claim the field reports are about.</para>
///
/// <para><b>The rule.</b> A device cannot hold a buffer shallower than one of its own pulls — a card
/// that hands over 10 ms at a time sawtooths by 10 ms, an 18 ms Realtek by 18 — so the target is the
/// measured pull times a small margin. Hysteresis then holds it: only a genuine change in the card's
/// behaviour moves it, so ordinary averaging wobble never nudges the latency.</para>
///
/// <para><b>It reads the card and HOLDS.</b> It is deliberately not a load-reactive loop: it must never
/// climb on a CPU or network spike and drift back down when things go quiet, because that is exactly
/// the shape of a slow overnight creep.</para>
///
/// <para><b>Why it lives in Core.</b> The plugin sender's second-DAW lane (<c>PluginTrackSource</c>, in
/// RemSound.Sender) sizes its ring's cushion by the same rule against the driving DAW's pull, and Sender
/// and Receiver may not reference each other. That lane used to carry its own copy of these numbers;
/// one rule in one place is what stops the two drifting apart.</para>
/// </summary>
public static class AdaptiveCushionTarget
{
    /// <summary>Cushion ≈ this many times the card's pull size.</summary>
    public const double GulpMultiple = 1.2;
    /// <summary>Floor, for a device with a very small pull.</summary>
    public const int MinMs = 8;
    /// <summary>Ceiling, so a pathological pull measurement cannot run the latency away.</summary>
    public const int MaxMs = 50;
    /// <summary>Only a change of at least this much moves the target.</summary>
    public const int HysteresisMs = 2;
    /// <summary>What a stage holds before it has measured its card.</summary>
    public const int DefaultMs = 12;

    /// <summary>
    /// The target for the next window, given the one currently held and the card's average pull over
    /// the window that just ended.
    /// </summary>
    /// <param name="currentTargetMs">The target in force now.</param>
    /// <param name="averagePullMs">The card's average pull over the window, or zero if it was not
    /// measured — in which case the target is left exactly as it is. A window that measured nothing is
    /// not evidence that the card changed.</param>
    /// <returns>The target to hold for the next window.</returns>
    public static int Next(int currentTargetMs, double averagePullMs)
    {
        if (averagePullMs <= 0) return currentTargetMs;
        var candidate = (int)Math.Round(Math.Clamp(averagePullMs * GulpMultiple, MinMs, MaxMs));
        return Math.Abs(candidate - currentTargetMs) >= HysteresisMs ? candidate : currentTargetMs;
    }
}
