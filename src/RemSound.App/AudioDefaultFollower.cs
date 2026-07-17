using NAudio.CoreAudioApi;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// The "Use Windows default audio device, follows Windows changes" choice, shared by the main window
/// and the lock-screen service so both offer — and resolve — it identically. It is NOT a real endpoint:
/// it's a sentinel that both persist in <c>SelectedWasapiSendOutputs</c>, and that the send-spec
/// builders resolve to the CURRENT default render endpoint each time capture is (re)built. When Windows'
/// default output changes, the device-change notifier drives a rebuild and the new default is picked up.
///
/// <para>Kept in one place because the service must track the main app: the main window already had this
/// (its <c>DefaultLoopbackSendFollower</c> + <c>ResolveDefaultDeviceId</c>); the service reuses the very
/// same sentinel and resolver rather than inventing a parallel one that would diverge.</para>
/// </summary>
internal static class AudioDefaultFollower
{
    /// <summary>Sentinel device id for "follow the Windows default OUTPUT (loopback send)". Namespaced so
    /// it can never collide with a real WASAPI endpoint id (which look like "{0.0.0.00000000}.{guid}").</summary>
    internal const string LoopbackSendId = "__use-default-loopback-send__";

    internal static bool IsLoopbackSend(string? deviceId) =>
        string.Equals(deviceId, LoopbackSendId, StringComparison.OrdinalIgnoreCase);

    /// <summary>The follower list entry for the "WASAPI outputs to send" list. A fresh instance each call
    /// (list controls take ownership of their items).</summary>
    internal static AudioDeviceChoice LoopbackSendChoice() =>
        new("Use Windows default audio device, follows Windows changes", LoopbackSendId, CaptureKind.Loopback)
        { IsDefaultFollower = true };

    /// <summary>The current Windows default OUTPUT (render) endpoint id, or null if none. Convenience for
    /// callers that only follow the default output and shouldn't have to name a NAudio DataFlow.</summary>
    internal static string? ResolveDefaultRenderId() => ResolveDefaultDeviceId(DataFlow.Render);

    /// <summary>The current Windows default endpoint id for the given direction, or null if there isn't
    /// one (or it can't be read). Render = default speakers/output; Capture = default mic/input.</summary>
    internal static string? ResolveDefaultDeviceId(DataFlow flow)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia)) return null;
            using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            return device.ID;
        }
        catch { return null; }
    }
}
