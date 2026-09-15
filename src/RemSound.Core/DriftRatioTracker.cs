using System.Diagnostics;

namespace RemSound.Core;

/// <summary>
/// The shared clock-drift calculation for holding audio produced on one clock against a consumer running
/// on another. Every such place in RemSound uses it but one, the per-person playout on the receiving side,
/// which keeps its own loop on purpose (see the last paragraph).
///
/// <para><b>Why it is here.</b> Three places had their own copy of this arithmetic, character for
/// character: the receive side's device output (<c>MultiOutputPlayout.DriftResamplingProvider</c>),
/// the second-DAW lane in the plugin sender (<c>PluginTrackSource.HostLane</c>), and the capture-source
/// corrector added 2026-09-07. They existed separately only because the sender and the receiver may
/// not reference each other — the same reason <see cref="SharedAsioDevice"/> lives in Core.</para>
///
/// <para>Three copies of one control loop is three places to fix a bug and two chances to miss one,
/// and it had already cost something: the newest copy was written without the first-window discard,
/// the validity gate and the depth-bias clamp that the oldest one had learned the hard way. Folding
/// them together does not just remove duplication, it gives every caller the version that has been
/// proven in the field for months.</para>
///
/// <para><b>What it does NOT own.</b> Rings, resamplers, providers, priming, and how each caller picks
/// its target depth all stay with the caller — those genuinely differ. This owns the loop: measure
/// what the producer fed against what the consumer drained over a window, sanity-check it, smooth it,
/// add a bounded nudge toward the wanted cushion, and hand back a rate ratio.</para>
///
/// <para><b>The shape of the loop</b>, and every part of it is load-bearing:</para>
/// <list type="bullet">
///   <item><b>Feed-forward.</b> fed ÷ drained over the window IS the clock ratio between the two
///   sides. Above 1.0 means the producer is faster and the consumer must speed up to match.</item>
///   <item><b>The first window is discarded.</b> Start-up fill is not drift, and counting it reads as
///   an enormous bogus ratio on the very first measurement.</item>
///   <item><b>A measurement outside the sanity band is ignored entirely</b> rather than clamped into
///   it — a wild reading is a glitch (a stall, a device hiccup), not a clock, and feeding it in
///   through the smoother would drag the ratio for several windows afterwards.</item>
///   <item><b>Smoothing 70/30</b>, so no single window can swing the rate.</item>
///   <item><b>A bounded depth nudge.</b> Rate-matching alone holds the buffer wherever the start-up
///   transient left it — it stops the drift but never repays it. The depth term walks the cushion back
///   to where it should be, capped at 0.3 % so it stays a sub-audible pitch nudge and never a
///   click.</item>
/// </list>
///
/// <para><b>The one loop this does not replace.</b> <c>SessionPlayout</c>, each sender's own buffer on the
/// receiving side, keeps its own. Its window starts only once that sender's buffer has filled to target,
/// so start-up fill never reaches its first reading, and it uses that reading as it is: discarding it, as
/// this class does, would start its correction ten seconds later. It already ignores a wild reading, caps
/// its depth nudge at the same 0.3 %, and adds a fast approach after a deliberate slider move. Ed,
/// 2026-09-15: leave it as it is. The self-test drives it on a simulated clock and pins what it does.</para>
/// </summary>
public sealed class DriftRatioTracker
{
    /// <summary>Seconds per measurement window. Long enough that one lumpy callback cannot be
    /// mistaken for a clock error.</summary>
    public const double MeasurementWindowSec = 10.0;
    /// <summary>Weight given to the newest window when smoothing.</summary>
    public const double RatioSmoothingNew = 0.30;
    /// <summary>A measured ratio outside this band is a glitch, not a clock, and is discarded.</summary>
    public const double RatioMin = 0.95;
    public const double RatioMax = 1.05;
    /// <summary>A depth error is corrected over roughly this long.</summary>
    public const double DepthCorrectionSec = 15.0;
    /// <summary>Cap on the depth nudge: 0.3 % of rate, which stays under the ear.</summary>
    public const double MaxDepthBias = 0.003;

    private readonly int sampleRate;
    /// <summary>Overridable ONLY so a gate can measure a real correction in seconds rather than
    /// minutes. Every shipped caller passes the default, and the tests pin that.</summary>
    private readonly double windowSec;
    private long windowStartTicks;
    private long windowStartFed;
    private long windowStartDrained;
    private long lastFedDelta;
    private long lastDrainedDelta;
    private bool measurementPending;
    private bool firstWindowDone;
    private bool tracking;

    /// <summary>
    /// Opt-in: before tracking starts, require two consecutive valid readings that agree to within this
    /// many ppm, instead of taking the first valid reading whole.
    ///
    /// <para><b>Why.</b> Ed's Roger On was re-opened at 14:52:00 on 2026-09-10. The first window after
    /// it was discarded as start-up fill, exactly as designed — and the next raw readings were −999,
    /// then +998, then +800, then 0, then +2 ppm. A freshly opened WASAPI endpoint takes about thirty
    /// seconds to settle, and the start-up oscillation runs well past the single window the tracker
    /// throws away. The −999 was taken whole, the device drained too slowly on it, and the cushion sat
    /// at 30 ms against a 12 ms target for about forty seconds.</para>
    ///
    /// <para>Agreement is the right test rather than "discard N more": a settling endpoint swings from
    /// one side to the other, a real crystal reads the same window after window. With the gate, −999
    /// and +998 disagree, +998 and +800 disagree, +800 and 0 disagree, and 0 and +2 agree — tracking
    /// starts at +1 ppm, which is the truth.</para>
    ///
    /// <para><b>Bounded.</b> A link noisy enough that two windows never agree must not leave the stage
    /// uncorrected for ever. After <see cref="MaxAcquisitionWindows"/> disagreeing readings the tracker
    /// falls back to the old rule and takes the latest one — never worse than before.</para>
    ///
    /// <para>Null for every caller except the WASAPI output stage, whose endpoint is the thing that
    /// re-opens. The capture corrector and the plugin lane keep their proven behaviour exactly.</para>
    /// </summary>
    private readonly double? acquireAgreementPpm;

    /// <summary>After this many consecutive disagreeing readings the acquisition gate gives up and
    /// takes the latest reading, so a noisy link degrades to the old behaviour instead of no
    /// correction at all.</summary>
    public const int MaxAcquisitionWindows = 6;

    private double? pendingAcquisition;
    private int acquisitionReadings;

    public DriftRatioTracker(int sampleRate = 48000, double windowSec = MeasurementWindowSec, double? acquireAgreementPpm = null)
    {
        this.sampleRate = sampleRate;
        this.windowSec = windowSec;
        this.acquireAgreementPpm = acquireAgreementPpm;
    }

    /// <summary>The smoothed clock ratio on its own, without the depth nudge. This is the honest
    /// estimate of how the two crystals compare; <see cref="AppliedRatio"/> is what to actually run at.</summary>
    public double ClockRatio { get; private set; } = 1.0;

    /// <summary>The rate ratio to run the resampler at: the clock ratio plus the bounded depth nudge.
    /// Exactly 1.0 until a valid measurement has been made, so nothing is touched on a guess.</summary>
    public double AppliedRatio { get; private set; } = 1.0;

    /// <summary>The depth nudge currently folded in, for diagnostics.</summary>
    public double DepthCorrection { get; private set; }

    /// <summary>True once a measurement inside the sanity band has been accepted. Before that the
    /// tracker deliberately reports 1.0 and changes nothing.</summary>
    public bool IsTracking => tracking;

    /// <summary>Forget everything — conditions have changed enough that the lesson no longer holds
    /// (a new stream, a reopened device, a source going away and coming back).</summary>
    public void Reset()
    {
        windowStartTicks = 0;
        measurementPending = false;
        firstWindowDone = false;
        tracking = false;
        pendingAcquisition = null;
        acquisitionReadings = 0;
        ClockRatio = 1.0;
        AppliedRatio = 1.0;
        DepthCorrection = 0;
    }

    /// <summary>The agreement the WASAPI output stage asks for before it starts tracking a freshly
    /// opened endpoint. A settled crystal reads within a few ppm window to window; the start-up swing
    /// recorded on 2026-09-10 was about two thousand ppm peak to peak. See acquireAgreementPpm.</summary>
    public const double DeviceAcquisitionAgreementPpm = 100;

    /// <summary>
    /// Offer the tracker the running totals. Cheap to call on every block: it returns false and does
    /// nothing until a window has elapsed.
    /// </summary>
    /// <param name="fedBytes">Cumulative bytes the PRODUCER has written.</param>
    /// <param name="drainedBytes">Cumulative bytes the CONSUMER has taken.</param>
    /// <param name="depthFrames">How much is buffered right now, in frames.</param>
    /// <param name="targetFrames">The cushion the caller wants held, in frames.</param>
    /// <returns>True when the window closed and the ratio was recomputed.</returns>
    public bool Update(long fedBytes, long drainedBytes, int depthFrames, int targetFrames)
        => WindowClosed(fedBytes, drainedBytes) && ApplyMeasurement(depthFrames, targetFrames);

    /// <summary>
    /// Half of <see cref="Update"/>, for callers that must do something of their own AT the window
    /// boundary and BEFORE the depth term is worked out.
    ///
    /// <para>Both device-side callers size their wanted cushion from the consumer's average pull over
    /// the window just ended, so the target they hand to <see cref="ApplyMeasurement"/> does not exist
    /// until the window has closed. Splitting the call keeps their ordering exactly as it was — the
    /// alternative, feeding in a target sized off the PREVIOUS window, would have quietly changed the
    /// behaviour of the one copy of this loop that has been holding real buffers flat for months.</para>
    /// </summary>
    /// <returns>True when a window boundary was just crossed; call <see cref="ApplyMeasurement"/> next.</returns>
    public bool WindowClosed(long fedBytes, long drainedBytes)
    {
        var now = Stopwatch.GetTimestamp();
        if (windowStartTicks == 0)
        {
            windowStartTicks = now;
            windowStartFed = fedBytes;
            windowStartDrained = drainedBytes;
            return false;
        }

        var elapsedSec = (now - windowStartTicks) / (double)Stopwatch.Frequency;
        if (elapsedSec < windowSec) return false;

        lastFedDelta = fedBytes - windowStartFed;
        lastDrainedDelta = drainedBytes - windowStartDrained;
        windowStartTicks = now;
        windowStartFed = fedBytes;
        windowStartDrained = drainedBytes;
        measurementPending = true;
        return true;
    }

    /// <summary>Fold the window that <see cref="WindowClosed"/> just ended into the ratio. Does nothing
    /// unless a window really did just close, so a stray call cannot count one window twice.</summary>
    /// <returns>True when the ratio was recomputed and is worth acting on.</returns>
    public bool ApplyMeasurement(int depthFrames, int targetFrames)
    {
        if (!measurementPending) return false;
        measurementPending = false;

        // The first window is start-up fill, not drift. Counting it reads as a huge bogus ratio.
        if (!firstWindowDone) { firstWindowDone = true; return false; }
        if (lastFedDelta <= 0 || lastDrainedDelta <= 0) return false;

        var measured = (double)lastFedDelta / lastDrainedDelta;
        // Outside the band it is a glitch, not a clock — DISCARDED, not clamped. Clamping would feed
        // a wild reading into the smoother and drag the rate for windows afterwards.
        if (measured >= RatioMin && measured <= RatioMax)
        {
            if (tracking)
            {
                ClockRatio = (1.0 - RatioSmoothingNew) * ClockRatio + RatioSmoothingNew * measured;
            }
            else if (acquireAgreementPpm is { } agreement)
            {
                // ACQUISITION GATE. Do not start tracking on one reading; wait for two in a row that
                // agree. A settling endpoint swings from one side to the other, a real crystal does
                // not. See acquireAgreementPpm for the field readings that proved it.
                acquisitionReadings++;
                if (pendingAcquisition is { } previous && Math.Abs(measured - previous) * 1e6 <= agreement)
                {
                    ClockRatio = (measured + previous) / 2.0;
                    tracking = true;
                }
                else if (acquisitionReadings >= MaxAcquisitionWindows)
                {
                    // Never settled. Fall back to the old rule rather than leave the stage uncorrected
                    // for ever on a link too noisy for two windows to agree.
                    ClockRatio = measured;
                    tracking = true;
                }
                else
                {
                    pendingAcquisition = measured;
                }
            }
            else
            {
                ClockRatio = measured;
                tracking = true;
            }
        }

        if (!tracking) return false;   // nothing valid yet — leave the rate alone

        var depthError = depthFrames - targetFrames;
        DepthCorrection = Math.Clamp(depthError / (DepthCorrectionSec * sampleRate), -MaxDepthBias, MaxDepthBias);
        AppliedRatio = ClockRatio + DepthCorrection;
        return true;
    }

    /// <summary>Pretend the current window started <paramref name="seconds"/> ago, so the next
    /// <see cref="WindowClosed"/> call closes it. Public only because the gate lives in another
    /// assembly: real drift takes ten seconds per window to measure by design, and a test that waited
    /// that long for each direction would not get run. Nothing shipped calls this.</summary>
    public void RewindWindowStartForTest(double seconds)
        => windowStartTicks = Stopwatch.GetTimestamp() - (long)(seconds * Stopwatch.Frequency);
}
