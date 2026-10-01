using System.Reflection;
using NAudio.Wave;
using NAudio.Wave.Asio;

namespace RemSound.Core;

/// <summary>
/// Asking an ASIO driver for a buffer size (the latency lab, 2026-09-26; a per-profile setting beside the driver choice
/// since 2026-09-28, <see cref="Profile.AsioBufferFrames"/>). NAudio's <see cref="AsioOut"/> has no way to
/// choose one: InitRecordAndPlayback calls AsioDriverExt.CreateBuffers(useMaxBufferSize: false), which always takes the
/// driver's PREFERRED size - whatever the vendor's control panel says, often 256 or 512 frames (5-11 ms at each end).
/// The preferred size is read from the driver's capability record, a public class with public fields, held by the
/// AsioDriverExt that AsioOut keeps in its private "driver" field. Setting BufferPreferredSize on that record before
/// the init makes NAudio create the buffers at our size. One private field is read; if a future NAudio renames it,
/// <see cref="CapabilitiesOf"/> returns null and the driver's own size is used, as before.
/// </summary>
public static class AsioBufferSizing
{
    private static readonly FieldInfo? DriverField =
        typeof(AsioOut).GetField("driver", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>The sizes a profile can ask for, in frames: 0 = the driver's own (the default, what RemSound always used).</summary>
    public static readonly int[] Choices = [0, 32, 64, 128, 256, 512];

    /// <summary>How a choice is shown: "Driver's own", or the frames with what they last at 48 kHz ("64 samples (1.3 ms)").</summary>
    public static string Describe(int frames) =>
        frames <= 0 ? "Driver's own" : $"{frames} samples ({frames * 1000.0 / SharedAsioDevice.SampleRate:0.0} ms)";

    /// <summary>The driver's capability record behind an AsioOut (sizes, latencies), or null if NAudio's layout has
    /// changed.</summary>
    public static AsioDriverCapability? CapabilitiesOf(AsioOut asio) =>
        (DriverField?.GetValue(asio) as AsioDriverExt)?.Capabilities;

    /// <summary>The nearest size the driver allows to <paramref name="requested"/>, by the ASIO rules: within
    /// min..max; a granularity of -1 means powers of two; a positive granularity means min + a multiple of it; 0 means
    /// only one size (min = max). Never below the request unless the request is above max.</summary>
    public static int Fit(int requested, int min, int max, int preferred, int granularity)
    {
        if (requested <= 0) return preferred;
        if (min <= 0 || max < min) return preferred;
        if (requested <= min) return min;
        if (requested >= max) return max;
        if (granularity == 0) return preferred;
        if (granularity < 0)
        {
            var p = 1;
            while (p < requested) p <<= 1;
            return Math.Clamp(p, min, max);
        }
        var steps = (requested - min + granularity - 1) / granularity;
        return Math.Min(max, min + steps * granularity);
    }

    /// <summary>Before AsioOut's init: set the size the driver will be asked for, from the profile's setting (0 = the
    /// driver's own). Returns a sentence for the log naming what the driver allows and what was asked.</summary>
    public static string ApplyBeforeInit(AsioOut asio, int requestedFrames) => Apply(CapabilitiesOf(asio), requestedFrames);

    /// <summary>The same, on the driver's capability record itself: with a size chosen, its preferred size becomes the
    /// nearest the driver allows, which is what NAudio's init then creates the buffers at; with "the driver's own" the
    /// record is not touched at all. Returns the log sentence.</summary>
    public static string Apply(AsioDriverCapability? cap, int requestedFrames)
    {
        if (cap is null)
            return requestedFrames > 0
                ? $"cannot choose a buffer size with this NAudio (its internals have changed) - using the driver's own"
                : "buffer size: the driver's own";
        var allowed = $"driver allows {cap.BufferMinSize}-{cap.BufferMaxSize} frames, granularity {cap.BufferGranularity}, prefers {cap.BufferPreferredSize}";
        if (requestedFrames <= 0) return $"{allowed}; using its preferred size (the profile's ASIO buffer size: driver's own)";
        var fitted = Fit(requestedFrames, cap.BufferMinSize, cap.BufferMaxSize, cap.BufferPreferredSize, cap.BufferGranularity);
        cap.BufferPreferredSize = fitted;
        return fitted == requestedFrames
            ? $"{allowed}; asking for {fitted} frames (the profile's ASIO buffer size)"
            : $"{allowed}; asked for {requestedFrames} frames, nearest it allows is {fitted} (the profile's ASIO buffer size)";
    }
}
