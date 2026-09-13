using NAudio.CoreAudioApi;

namespace RemSound.Core;

/// <summary>
/// Asks a WASAPI device what its engine period is, so the latency estimate can stop assuming.
///
/// <para>The rule for turning that period into a latency lives beside it in <see cref="DeviceLatency"/>.
/// This type is just the NAudio-shaped way to get the number out of a device. It lives in Core because
/// both sides ask it — the capture backends in RemSound.Sender and the output backends in
/// RemSound.Receiver, which do not reference each other — and it used to exist as two identical copies,
/// one in each. Public because that is how Core's types reach Sender and Receiver; Core grants
/// InternalsVisibleTo only to the app.</para>
///
/// <para><b>Cost.</b> Read ONCE when a device is opened, on the thread doing the opening, never in
/// an audio callback and never per sample. It touches no audio buffer and changes no signal path —
/// strictly less work than the callback-gap timing it replaces, which ran on the audio thread.
/// A device that will not answer returns 0 and the caller falls back to its previous estimate.
/// 2026-08-24.</para>
/// </summary>
public static class DeviceLatencyProbe
{
    /// <summary>The device's shared-mode engine period in milliseconds, or 0 if it won't say.</summary>
    public static double EnginePeriodMs(MMDevice device)
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
    public static double CaptureLatencyMs(MMDevice device, int requestedBufferMs) =>
        DeviceLatency.CaptureMs(EnginePeriodMs(device), requestedBufferMs);

    /// <summary>How long audio waits in this render device. See <see cref="DeviceLatency.RenderMs"/>.</summary>
    public static double RenderLatencyMs(MMDevice device, int requestedBufferMs) =>
        DeviceLatency.RenderMs(EnginePeriodMs(device), requestedBufferMs);
}
