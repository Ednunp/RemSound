using NAudio.CoreAudioApi;
using RemSound.Core;

namespace RemSound.Sender;

/// <summary>
/// Asks a WASAPI device what its engine period is, so the latency estimate can stop assuming.
///
/// <para>The rule for turning that period into a latency lives in <see cref="DeviceLatency"/> in
/// Core, because the output backends need the identical rule and they live in a different assembly.
/// This type is just the NAudio-shaped way to get the number out of a device.</para>
///
/// <para><b>Cost.</b> Read ONCE when a device is opened, on the thread doing the opening, never in
/// an audio callback and never per sample. It touches no audio buffer and changes no signal path —
/// strictly less work than the callback-gap timing it replaces, which ran on the audio thread.
/// A device that will not answer returns 0 and the caller falls back to its previous estimate.
/// 2026-08-24.</para>
/// </summary>
internal static class DeviceLatencyProbe
{
    /// <summary>The device's shared-mode engine period in milliseconds, or 0 if it won't say.</summary>
    internal static double EnginePeriodMs(MMDevice device)
    {
        try
        {
            return DeviceLatency.HnsToMs(device.AudioClient.DefaultDevicePeriod);
        }
        catch
        {
            // A device that refuses to describe itself is not an error worth surfacing — the caller
            // falls back to the previous estimate, which is what it would have used anyway.
            return 0;
        }
    }

    /// <summary>How long audio waits in this capture device. See <see cref="DeviceLatency.CaptureMs"/>.</summary>
    internal static double CaptureLatencyMs(MMDevice device, int requestedBufferMs) =>
        DeviceLatency.CaptureMs(EnginePeriodMs(device), requestedBufferMs);

    /// <summary>How long audio waits in this render device. See <see cref="DeviceLatency.RenderMs"/>.</summary>
    internal static double RenderLatencyMs(MMDevice device, int requestedBufferMs) =>
        DeviceLatency.RenderMs(EnginePeriodMs(device), requestedBufferMs);
}
