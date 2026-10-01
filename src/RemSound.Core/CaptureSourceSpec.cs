namespace RemSound.Core;

/// <summary>
/// Whether a capture source pulls audio via WASAPI loopback (rendering side of an output device,
/// e.g. system audio / a soundcard's playback) or via direct WASAPI capture (microphones,
/// line-ins, USB capture inputs).
/// </summary>
public enum CaptureKind
{
    Loopback,
    Input,
    /// <summary>Per-application capture: loopback of ONE process (and its child process tree) via the
    /// Windows process-loopback API (Windows 10 build 19041+). The <see cref="CaptureSourceSpec.DeviceId"/>
    /// carries the target PID as <c>"proc:&lt;pid&gt;"</c> (see <see cref="ProcessLoopbackId"/>). WASAPI-only
    /// — ASIO has no per-app concept.</summary>
    ProcessLoopback,
}

/// <summary>
/// Identifies one source the sender should mix into the outgoing stream. <see cref="Name"/> is
/// purely for diagnostic logging; <see cref="DeviceId"/> is a WASAPI MMDevice ID, a synthetic ASIO id
/// of the form <c>"asio:&lt;channel-pair-index&gt;"</c> (<see cref="AsioDeviceId"/>), or a
/// per-application id of the form <c>"proc:&lt;pid&gt;"</c> (<see cref="ProcessLoopbackId"/>).
/// </summary>
public sealed record CaptureSourceSpec(string DeviceId, CaptureKind Kind, string Name);

/// <summary>
/// Helpers for the synthetic ASIO device-id format used by both sender and receiver backends.
/// Each ASIO channel pair (stereo) is identified by its zero-based pair index — pair 0 is ASIO
/// channels 0+1, pair 1 is 2+3, etc. The driver itself isn't encoded in the id; only one ASIO
/// driver is active per session and it's configured separately.
/// </summary>
public static class AsioDeviceId
{
    public static string Format(int channelPair) => $"asio:{channelPair}";

    public static bool TryParse(string deviceId, out int channelPair)
    {
        channelPair = -1;
        if (string.IsNullOrEmpty(deviceId)) return false;
        if (!deviceId.StartsWith("asio:", StringComparison.OrdinalIgnoreCase)) return false;
        return int.TryParse(deviceId.AsSpan("asio:".Length), out channelPair) && channelPair >= 0;
    }
}

/// <summary>Synthetic device-id for a per-application (process-loopback) capture: <c>"proc:&lt;pid&gt;"</c>.
/// The PID is resolved fresh each time the app list reconciles, so the id is transient (an app that
/// restarts gets a new PID and a new spec) — selection is tracked by process NAME elsewhere.</summary>
public static class ProcessLoopbackId
{
    public static string Format(int processId) => $"proc:{processId}";

    /// <summary>Every program EXCEPT this process and its children: <c>"procx:&lt;pid&gt;"</c>. The service's boot ear
    /// (ServiceSendHost) - everything playing on the machine, reached per program, without RemSound's own keepalive.</summary>
    public static string FormatExcluding(int processId) => $"procx:{processId}";

    /// <summary>One program (and its children) - <c>"proc:"</c> only; an excluding id is not one program.</summary>
    public static bool TryParse(string deviceId, out int processId) =>
        TryParse(deviceId, out processId, out var excluding) && !excluding;

    /// <summary>Either form: <paramref name="excluding"/> says which.</summary>
    public static bool TryParse(string deviceId, out int processId, out bool excluding)
    {
        processId = -1;
        excluding = false;
        if (string.IsNullOrEmpty(deviceId)) return false;
        var prefix = deviceId.StartsWith("procx:", StringComparison.OrdinalIgnoreCase) ? "procx:"
            : deviceId.StartsWith("proc:", StringComparison.OrdinalIgnoreCase) ? "proc:" : null;
        if (prefix is null) return false;
        excluding = prefix == "procx:";
        return int.TryParse(deviceId.AsSpan(prefix.Length), out processId) && processId > 0;
    }
}
