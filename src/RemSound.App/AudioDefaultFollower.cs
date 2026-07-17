using System.Windows.Forms;
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

    // --- exclusivity: "use the default" locks out the specific cards in the same list ---
    // Ticking the follower means "just use whatever Windows uses" — so the specific device ticks are
    // cleared and cannot be re-ticked until the follower is turned off again (Ed, 2026-07-17). This
    // replaced the old optional "shall I untick the others?" prompt.

    /// <summary>Whether the list's "Use Windows default" follower entry is currently ticked.</summary>
    internal static bool IsFollowerChecked(CheckedListBox list) =>
        list.CheckedItems.OfType<AudioDeviceChoice>().Any(c => c.IsDefaultFollower);

    /// <summary>Call at the top of a list's ItemCheck handler. If the user is trying to TICK a specific
    /// (non-follower) device while the follower is on, force it back to unticked and return true — the
    /// caller should then stop processing that event. Never vetoes the follower itself, an untick, or a
    /// tick made while the follower is off.</summary>
    internal static bool VetoRealDeviceCheck(CheckedListBox list, ItemCheckEventArgs e)
    {
        if (e.NewValue != CheckState.Checked) return false;
        if (list.Items[e.Index] is not AudioDeviceChoice choice || choice.IsDefaultFollower) return false;
        if (!IsFollowerChecked(list)) return false;
        e.NewValue = CheckState.Unchecked;
        return true;
    }

    /// <summary>Untick every specific (non-follower) device in the list, leaving the follower as-is.
    /// Returns true if anything changed. The caller MUST already be suppressing its ItemCheck handler
    /// (SetItemChecked re-raises ItemCheck) — this method does not toggle any suppression flag itself,
    /// so it nests safely inside an already-suppressed load.</summary>
    internal static bool UncheckRealDevices(CheckedListBox list)
    {
        var changed = false;
        for (var i = 0; i < list.Items.Count; i++)
        {
            if (list.Items[i] is AudioDeviceChoice { IsDefaultFollower: false } && list.GetItemChecked(i))
            {
                list.SetItemChecked(i, false);
                changed = true;
            }
        }
        return changed;
    }

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
