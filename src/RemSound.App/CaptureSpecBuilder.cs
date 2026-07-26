using RemSound.Core;
using RemSound.Sender;

namespace RemSound.App;

/// <summary>
/// The WASAPI send-spec assembly shared by the main window and the send-only service — the very rule
/// the standing "service must mirror the app's send behaviour" applies to, which previously existed as
/// two hand-mirrored copies (MainForm.ApplySendSources and ServiceSendHost.BuildSendSpecs). Devices
/// mode: loopback specs with the "Use Windows default" sentinel resolved to the LIVE default render
/// endpoint and duplicates collapsed. Applications mode: one process-loopback spec per running PID of
/// each chosen app name (child processes ride along via the include-tree capture). The app layers its
/// WASAPI inputs and ASIO pairs on top; the service deliberately adds neither.
/// </summary>
internal static class CaptureSpecBuilder
{
    /// <summary>Devices mode. <paramref name="followedDefaultId"/> reports the endpoint the follower
    /// resolved to (null when not following, or no default exists) — the app compares it on device
    /// changes to know when the Windows default moved and a re-route is due.</summary>
    internal static List<CaptureSourceSpec> BuildOutputSpecs(
        IEnumerable<(string Id, string Name)> outputs, out string? followedDefaultId)
    {
        var specs = new List<CaptureSourceSpec>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        followedDefaultId = null;
        foreach (var (id, name) in outputs)
        {
            if (AudioDefaultFollower.IsLoopbackSend(id))
            {
                var def = AudioDefaultFollower.ResolveDefaultRenderId();
                followedDefaultId = def;
                if (def is not null && added.Add(def))
                    specs.Add(new CaptureSourceSpec(def, CaptureKind.Loopback, "Windows default audio device"));
            }
            else if (added.Add(id))
            {
                specs.Add(new CaptureSourceSpec(id, CaptureKind.Loopback, name));
            }
        }
        return specs;
    }

    /// <summary>Applications mode. Apps not currently running contribute nothing until they reappear —
    /// the reconcile timer and the session-start watcher keep re-applying, so they're caught on open.</summary>
    internal static List<CaptureSourceSpec> BuildApplicationSpecs(IEnumerable<string> appNames)
    {
        var specs = new List<CaptureSourceSpec>();
        foreach (var name in appNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var pid in AudioAppEnumerator.PidsForProcessName(name))
            {
                specs.Add(new CaptureSourceSpec(ProcessLoopbackId.Format(pid), CaptureKind.ProcessLoopback, name));
            }
        }
        return specs;
    }
}
