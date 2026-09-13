using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Notices that the machine was asleep (or the UI thread stalled) from the gap between two ticks.
///
/// <para><b>Why the power event is not enough.</b> Ed's laptop, 2026-09-10: it woke at 14:51:40 and
/// the auto-tune acted on that very second — raising the WASAPI buffer from 35 to 65 ms on 119
/// underruns that had piled up while the Roger On was unplugged before sleep. Windows' Resume
/// notification did not arrive until 14:51:43, three seconds later. Anything hung off the power
/// event alone runs after the damage is done.</para>
///
/// <para>A tick knows when it last ran. After a hibernate the gap is the whole sleep — 98 seconds
/// that afternoon — and no timer-driven tick in this app legitimately waits anywhere near that long,
/// so the gap itself is the earliest reliable signal there is. A UI stall long enough to trip it is
/// treated the same way, deliberately: evidence gathered across a ten-second freeze is no better
/// than evidence gathered across a sleep.</para>
/// </summary>
internal sealed class SuspendDetector
{
    /// <summary>A gap this long between ticks means the machine was not running normally. The busiest
    /// caller ticks every second and the slowest every few seconds, so this is several missed ticks,
    /// not scheduling noise.</summary>
    public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(10);

    private DateTime lastUtc = DateTime.MinValue;

    /// <summary>Note that a tick ran now.</summary>
    /// <returns>The gap since the previous tick when it shows a suspension or stall; otherwise null.
    /// The very first tick is never a wake — there is nothing to compare it with — and a clock that
    /// moved backwards is not one either.</returns>
    public TimeSpan? NoteTick(DateTime nowUtc)
    {
        var previous = lastUtc;
        lastUtc = nowUtc;
        if (previous == DateTime.MinValue) return null;
        var gap = nowUtc - previous;
        return gap >= Threshold ? gap : null;
    }

    /// <summary>Pretend the previous tick happened <paramref name="ago"/> before now, so the gate can
    /// drive a wake without sleeping the machine.</summary>
    internal void RewindForTest(TimeSpan ago) => lastUtc = DateTime.UtcNow - ago;
}

/// <summary>
/// Per-lane "do not believe the evidence right now" windows for the auto-tune.
///
/// <para><b>What went wrong without it.</b> The same afternoon, two separate transitions were read by
/// the tuner as network jitter. First the wake, above. Then Ed plugged the Roger On back in: Windows
/// invalidated it mid-enumeration, RemSound re-opened it four seconds later, and that outage arrived in
/// the tuner as a 57 ms render-callback gap — recommended 80 ms, applied 80 ms. Neither was jitter.
/// Both were the device path changing under the audio, and both left WASAPI up to 45 ms deeper for the
/// minute it took the protected descent to walk back.</para>
///
/// <para><b>What a hold does.</b> While a lane is held, the per-second tick does not add readings to
/// that lane's evidence windows, and the tuner does not tick that lane at all — neither raising nor
/// descending. The descent is deliberately NOT sped up afterwards (that is a standing rule in this
/// project); the point is that the false raise never happens, so there is nothing to descend from.</para>
///
/// <para>Extending a hold never shortens it: a later, shorter request cannot cut an earlier, longer
/// one off early.</para>
/// </summary>
internal sealed class TuneEvidenceHold
{
    /// <summary>After the ticks stopped for a while — a sleep, or RemSound freezing. Long enough for the
    /// gap to be read and thrown away before the tuner can see it.</summary>
    public static readonly TimeSpan AfterWake = TimeSpan.FromSeconds(15);

    /// <summary>After the audio has been restarted on a wake. Freshly opened devices take a few callbacks
    /// to settle into their real period, and the gap from opening them must not reach the tuner.</summary>
    public static readonly TimeSpan AfterBackendReinit = TimeSpan.FromSeconds(10);

    /// <summary>Re-applied on every attempt while an output is being re-opened, so a device that takes
    /// a while to come back stays held for as long as it takes.</summary>
    public static readonly TimeSpan WhileReopening = TimeSpan.FromSeconds(10);

    /// <summary>After the output has come back. The render gap the outage left behind is still sitting
    /// in the diagnostics accumulator and is read on the next tick — the hold has to outlast that
    /// read, or the 57 ms reaches the tuner anyway.</summary>
    public static readonly TimeSpan AfterRecovery = TimeSpan.FromSeconds(10);

    private static readonly RenderRoute[] AllTunedLanes = [RenderRoute.WasapiLane, RenderRoute.AsioLane, RenderRoute.Mixed];

    private readonly DateTime[] heldUntilUtc = new DateTime[8];

    public void Hold(RenderRoute route, DateTime untilUtc)
    {
        var i = (int)route & 7;
        if (untilUtc > heldUntilUtc[i]) heldUntilUtc[i] = untilUtc;
    }

    public void Hold(IEnumerable<RenderRoute> routes, DateTime untilUtc)
    {
        foreach (var r in routes) Hold(r, untilUtc);
    }

    public void HoldAll(DateTime untilUtc) => Hold(AllTunedLanes, untilUtc);

    public bool IsHeld(RenderRoute route, DateTime nowUtc) => nowUtc < heldUntilUtc[(int)route & 7];

    public bool AnyHeld(DateTime nowUtc)
    {
        foreach (var r in AllTunedLanes) if (IsHeld(r, nowUtc)) return true;
        return false;
    }
}

/// <summary>
/// Which tuner lanes an output problem invalidates.
///
/// <para>Per output KIND, not per configuration, because that is what actually carries the fault. A
/// WASAPI output failing invalidates the WASAPI lane — and the Mixed lane, which is the route the
/// tuner drives in a WASAPI-only configuration, so the fix covers all three configurations rather than
/// just the both-lanes one it was found in. An ASIO output going missing invalidates the ASIO lane and
/// nothing else: Ed's Audient never moved while the Roger On was being pulled in and out, and its
/// evidence was perfectly good the whole time.</para>
///
/// <para>Only WASAPI outputs report a live fault — ASIO has no WASAPI endpoint to invalidate — so an
/// ASIO problem arrives here only as a ticked output that is not open.</para>
/// </summary>
internal static class OutputFaultLanes
{
    public static RenderRoute[] For(bool wasapiOutputFaulted, IEnumerable<string> missingOutputIds)
    {
        var wasapi = wasapiOutputFaulted;
        var asio = false;
        foreach (var id in missingOutputIds)
        {
            if (AsioDeviceId.TryParse(id, out _)) asio = true;
            else wasapi = true;
        }
        var lanes = new List<RenderRoute>(3);
        if (wasapi) { lanes.Add(RenderRoute.WasapiLane); lanes.Add(RenderRoute.Mixed); }
        if (asio) lanes.Add(RenderRoute.AsioLane);
        return lanes.ToArray();
    }
}
