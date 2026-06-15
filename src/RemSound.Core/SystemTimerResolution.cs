using System.Runtime.InteropServices;

namespace RemSound.Core;

/// <summary>
/// Holds the Windows system timer resolution at a fine grain (1 ms by default) for the lifetime of
/// this scope, via timeBeginPeriod / timeEndPeriod (winmm).
///
/// Why the audio engine needs this on its own, independent of the opt-in "Priority mode": a
/// real-time loop that paces itself with a Thread / WaitHandle wait is only as precise as the
/// system timer, whose DEFAULT resolution is ~15.6 ms. Windows wakes a waiting thread on the next
/// coarse tick, so a loop asking to run every 10 ms actually slips to ~16 ms — or ~31 ms when it
/// misses a tick. On a machine where nothing else happens to be holding a fine timer, that lumpy
/// wake-up made the receive producer feed audio to the output devices in chunky ~16–31 ms bursts
/// instead of smooth 10 ms frames — the desktop-render chunkiness behind Andre's overnight dropouts
/// and lag (diagnosed 2026-06-15: render-callback gaps clustered at 25–34 ms ≈ 2× a 15.6 ms tick).
/// Priority mode already requested 1 ms via the same call, but it's off by default and the audio
/// path must not depend on a user toggle to deliver smoothly; the producer / mix loops hold this
/// scope directly whenever a stream is live.
///
/// timeBeginPeriod is process-global and reference-counted by Windows, so multiple scopes (and a
/// concurrent Priority-mode request) nest safely — each Begin needs its own matching End. The cost
/// of a fine timer is a small increase in power draw, acceptable while audio is actively streaming.
/// Best-effort: a failed request just leaves the default resolution; nothing here throws into the
/// audio path.
/// </summary>
public sealed class SystemTimerResolution : IDisposable
{
    private readonly uint periodMs;
    private readonly bool acquired;

    public SystemTimerResolution(uint periodMs = 1)
    {
        this.periodMs = periodMs;
        try { acquired = timeBeginPeriod(periodMs) == 0; } // TIMERR_NOERROR == 0
        catch { acquired = false; }
    }

    public void Dispose()
    {
        if (!acquired) return;
        try { timeEndPeriod(periodMs); } catch { /* leave it — process exit reclaims it */ }
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uMilliseconds);
}
