namespace RemSound.Receiver;

/// <summary>
/// A reading from the WASAPI output stage — the part of the receive path that ASIO does not have.
///
/// <para><b>Why this is worth surfacing on its own.</b> Ed, 2026-09-09: the overnight slowdown happens
/// on WASAPI and does not happen on ASIO. That is the strongest constraint in the whole investigation,
/// because the two lanes differ in exactly one place. ASIO pulls straight from the playout engine and
/// has nothing between the engine and the driver. WASAPI has this stage: a device buffer holding a
/// cushion, a target for that cushion sized from the card's own pull size, and a resampler trimming
/// the rate to hold it there.</para>
///
/// <para>Everything in that list can only drift or ratchet on the WASAPI side. So if something climbs
/// over a night, it climbs here — and until now these numbers existed only inside a diagnostic line
/// written every ten seconds, and only while a receiver was actually running. On a send-only machine
/// they were never written at all, which is why fourteen hours of log from 2026-09-08 could not answer
/// the question it was collected to answer.</para>
///
/// <para>Read on the UI thread from a live audio path, so every field is a plain value copied out
/// under no lock. A field being a tick stale does not matter for a report written every few
/// minutes.</para>
/// </summary>
/// <param name="DeviceName">The output this reading belongs to.</param>
/// <param name="BufferedMs">How much audio is sitting in the device buffer right now.</param>
/// <param name="TargetMs">The cushion this stage is trying to hold, sized from the card's pull.
/// THE NUMBER TO WATCH: it is held by hysteresis and should settle within the first minute and never
/// move again. A target that climbs over hours is latency being added with nothing asking for it.</param>
/// <param name="GulpMs">The card's average pull size over the last window, which is what TargetMs is
/// sized from. If the target climbs, this says whether the card's behaviour changed or the sizing did.</param>
/// <param name="ClockPpm">The measured difference between the machine's feed and the card's drain.
/// A real crystal difference is tens of ppm and stable. Hundreds, or a figure that wanders, is not a
/// clock.</param>
/// <param name="CorrectionPpm">The bounded nudge being applied to walk the cushion back to target.
/// Should hover near zero once settled; a correction pinned at its cap means the stage is fighting
/// something it cannot win against.</param>
public readonly record struct WasapiOutputStage(
    string DeviceName,
    double BufferedMs,
    int TargetMs,
    double GulpMs,
    double ClockPpm,
    double CorrectionPpm)
{
    /// <summary>Compact form for the long-run report — one output per pair of brackets.</summary>
    public override string ToString()
        => $"{DeviceName}: dev={BufferedMs:0}/{TargetMs}ms gulp={GulpMs:0}ms clock={ClockPpm:+0;-0}ppm corr={CorrectionPpm:+0;-0}ppm";
}
