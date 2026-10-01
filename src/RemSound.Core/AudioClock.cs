using System.Diagnostics;

namespace RemSound.Core;

/// <summary>
/// The clock the audio path keeps time by where time changes what is heard - the buffer's trim glide and drift
/// windows, and each stream's packet-arrival gaps (2026-09-26, for the simulator). It is the Stopwatch, always, except
/// while the simulator runs: then the simulator's own virtual clock stands in, so a minute of simulated audio can run
/// in about a second and come out the same every time. Diagnostics timings stay on the real Stopwatch.
///
/// <para>Per thread (2026-09-28): a simulation runs everything on the one thread that set its clock, so two simulations on
/// two threads - the gate's overnight runs, hours of virtual time each - keep a clock each. Every other thread, and the
/// app always, reads the Stopwatch.</para>
/// </summary>
public static class AudioClock
{
    [ThreadStatic] private static Func<long>? simulated;
    [ThreadStatic] private static DateTime simulatedUtcAnchor;
    [ThreadStatic] private static long simulatedTicksAnchor;

    /// <summary>Now, in Stopwatch ticks (<see cref="Stopwatch.Frequency"/> per second).</summary>
    public static long GetTimestamp() => simulated?.Invoke() ?? Stopwatch.GetTimestamp();

    /// <summary>Now as a UTC date: <see cref="DateTime.UtcNow"/>, always, except while a simulation's clock is in force on
    /// this thread - then the wall-clock moment the simulation started plus the simulated time since. The receiver ages
    /// its idle sessions by this (2026-09-28, the performance suite), so the simulator can see a peer that stops sending
    /// pruned four seconds later in ITS time, and churn thousands of peers through the prune in virtual time. The app never
    /// sets a simulated clock, so there it is the wall clock, exactly as before.</summary>
    public static DateTime UtcNow
    {
        get
        {
            var clock = simulated;
            if (clock is null) return DateTime.UtcNow;
            var elapsedTicks = clock() - simulatedTicksAnchor;
            return simulatedUtcAnchor.AddTicks((long)(elapsedTicks * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency)));
        }
    }

    /// <summary>Whether a simulation's clock is in force.</summary>
    public static bool IsSimulated => simulated is not null;

    /// <summary>Put a simulation's clock in force on this thread (null puts the real one back). Only the simulator calls
    /// this, from the one thread it runs everything on.</summary>
    public static void UseSimulated(Func<long>? clock)
    {
        simulated = clock;
        if (clock is null) return;
        simulatedUtcAnchor = DateTime.UtcNow;
        simulatedTicksAnchor = clock();
    }
}
