using NAudio.Wave;
using RemSound.Core;

namespace RemSound.Sender;

/// <summary>
/// The simulator's way into the capture side (2026-09-30, the simulator review's second improvement). Set, opening a
/// WASAPI capture source asks <see cref="Open"/> first: a simulated device (any <see cref="IWaveIn"/>) is used as the
/// real one would be - through push mode for a single source, or a <see cref="CaptureSource"/> in the mixing engine
/// with its ring, resampler and drift corrector. <see cref="ManualMixTick"/> leaves the mixing engine without a mix
/// thread of its own, so the simulator runs each 10 ms tick on its own clock. Both are machine-wide: a simulation that
/// sets them runs alone. Null and false everywhere else.
/// </summary>
internal static class CaptureDevicesForTest
{
    /// <summary>The device to capture from for a spec, or null to open the real one.</summary>
    internal static Func<CaptureSourceSpec, IWaveIn?>? Open;

    /// <summary>The mixing engine's ticks are run by the caller (<see cref="MixingEngine.MixTickForTest"/>).</summary>
    internal static bool ManualMixTick;

    /// <summary>A simulated device may say how much capture latency it has, as a real device's engine period does.</summary>
    internal interface IReportsLatency
    {
        double ReportedInputLatencyMs { get; }
    }
}
