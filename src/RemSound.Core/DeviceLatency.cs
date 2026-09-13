namespace RemSound.Core;

/// <summary>
/// The arithmetic for turning a device's own reported period into the latency it actually adds.
///
/// <para>Lives in Core because BOTH sides need the identical rule — the capture backends are in
/// RemSound.Sender, the output backends in RemSound.Receiver, and Receiver does not reference
/// Sender. Duplicating the rule in two assemblies is how two answers to one question start
/// disagreeing. Pure functions with no NAudio dependency (SharedAsioDevice is the one type in Core that
/// touches NAudio), so the gate can pin the rule without opening a device.</para>
///
/// <para><b>Why any of this exists.</b> RemSound's latency estimate had five terms and four of them
/// were constants dressed as measurements. Capture added a flat 10 ms — the size RemSound ASKS
/// Windows for, not what the device delivers — because neither WASAPI capture backend reported a
/// period at all. Output took a measured callback gap, doubled it, then clamped up to a floor of
/// 10 ms, which pinned a fast card at 10 whatever the truth was. Ed's 2026-08-23 log shows
/// capture=10.0 on all 2,547 samples and render=10.0 on 2,541 of them, on hardware whose real
/// callback period was about 1.4 ms. 2026-08-24.</para>
/// </summary>
public static class DeviceLatency
{
    /// <summary>100-nanosecond units per millisecond — WASAPI reports periods in 100-ns ticks.</summary>
    public const double HnsPerMs = 10_000.0;

    /// <summary>Convert a WASAPI period expressed in 100-ns units to milliseconds. Zero in, zero out.</summary>
    public static double HnsToMs(long hundredNanosecondUnits) =>
        hundredNanosecondUnits > 0 ? hundredNanosecondUnits / HnsPerMs : 0;

    /// <summary>Convert a buffer size in frames to milliseconds at a sample rate. This is how an ASIO
    /// buffer size becomes a latency: audio accumulates for exactly one buffer before the callback.</summary>
    public static double FramesToMs(int frames, int sampleRate) =>
        frames > 0 && sampleRate > 0 ? frames * 1000.0 / sampleRate : 0;

    /// <summary>
    /// How long audio really waits in a capture device, given the buffer RemSound asked for and the
    /// period the device reports.
    ///
    /// <para>Windows will not give a shared-mode client a buffer shorter than the engine period, so
    /// asking for 10 ms on a device whose period is 21 ms gets you 21. The larger of the two is the
    /// honest answer — which is why a Realtek can legitimately report a HIGHER figure than the 10 ms
    /// constant it replaces. A number going up because it became true is the entire point.</para>
    ///
    /// <para>Returns 0 when the device would not state a period, so the caller keeps whatever
    /// estimate it was using before. Unknown must never masquerade as measured.</para>
    /// </summary>
    public static double CaptureMs(double reportedPeriodMs, int requestedBufferMs) =>
        reportedPeriodMs <= 0 ? 0 : Math.Max(reportedPeriodMs, Math.Max(1, requestedBufferMs));

    /// <summary>
    /// The same for an output device, doubled.
    ///
    /// <para>The doubling is deliberate and is not the assumption the old code made. On the output
    /// side the device plays one buffer while the app fills the next, so a sample handed over now
    /// waits up to two periods before it is heard. The old code doubled a measured callback GAP —
    /// which is scheduler jitter as much as device period — and then clamped the result up to 10 ms.
    /// This doubles the device's own stated period and applies no floor.</para>
    /// </summary>
    public static double RenderMs(double reportedPeriodMs, int requestedBufferMs)
    {
        var effective = CaptureMs(reportedPeriodMs, requestedBufferMs);
        return effective <= 0 ? 0 : effective * 2.0;
    }
}
