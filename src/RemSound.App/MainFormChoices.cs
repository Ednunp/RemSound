using System.Windows.Forms;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// CheckedListBox subclass that exposes the protected <c>RefreshItem</c> method publicly. Used
/// for the connectivity dialog's "Connected peers" list, where each row's text is updated in
/// place (live RTT, codec, direction) without re-adding items — that would destroy NVDA's row
/// focus on every tick.
/// </summary>
internal sealed class LiveCheckedListBox : CheckedListBox
{
    public void RefreshItemPublic(int index) => RefreshItem(index);
}

/// <summary>
/// User-facing choice that maps a friendly label to a codec + Opus frame size pair. The frame
/// size is in samples-per-channel at 48 kHz (v3.0 wire-format unit — see
/// <see cref="AudioFormatInfo"/>). 120 = 2.5 ms, 240 = 5 ms, 480 = 10 ms, 960 = 20 ms.
/// Only meaningful when Codec == Opus; for PCM it's ignored.
/// </summary>
internal sealed record CodecChoice(string Label, AudioTransportCodec Codec, int OpusFrameSamples)
{
    public override string ToString() => Label;
}

internal sealed record AudioDeviceChoice(string Name, string? DeviceId, CaptureKind Kind = CaptureKind.Loopback)
{
    /// <summary>True for the special "Use Windows default" pseudo-entry that sits at the top of the
    /// receive-output and send-input lists. When ticked, the current Windows default device is
    /// resolved live (Render for an output follower, Capture for an input follower — derived from
    /// <see cref="Kind"/>) and re-resolved whenever Windows' default changes. Its <see cref="DeviceId"/>
    /// is a sentinel, never a real endpoint id.</summary>
    public bool IsDefaultFollower { get; init; }

    public override string ToString() => Name;
}

internal sealed record RememberedPeerItem(string Entry)
{
    public override string ToString() => Entry;
}

/// <summary>
/// Live per-peer status surfaced in the connectivity dialog's listbox text. Mutated in place
/// each tick by MainForm.SyncAllDialogPeerLists, then ListBox.RefreshItem(i) is called on the
/// containing item so the visible label updates without rebuilding the listbox (which would
/// destroy NVDA focus on the row).
/// </summary>
internal sealed class PeerLineStatus
{
    public bool Connected;
    /// <summary>True when our sender is actively pushing audio at this peer.</summary>
    public bool Sending;
    /// <summary>True when audio is arriving from this peer's IP (a fresh receiver session exists).</summary>
    public bool Receiving;
    /// <summary>Codec label like "Opus 10ms", "Opus 20ms", "PCM". Null when not connected.</summary>
    public string? CodecLabel;
    /// <summary>Round-trip ping ms from heartbeat. Null when not connected or pending.</summary>
    public int? RttMs;
}

internal sealed class PeerListItem
{
    /// <summary>Optional resolver mapping a peer to the display name to show — e.g. a user-set friendly
    /// name. Set once by MainForm. When null (or it returns the machine name) behaviour is unchanged,
    /// so this covers the connected AND discovered lists in one place.</summary>
    public static Func<PeerAnnouncement, string>? DisplayNameProvider;

    public PeerAnnouncement Peer { get; }
    public PeerLineStatus Status { get; } = new();

    public PeerListItem(PeerAnnouncement peer) { Peer = peer; }

    /// <summary>
    /// Stable identity for signature-based listbox-rebuild detection. Does NOT include live
    /// status — that gets updated in-place via RefreshItem so NVDA focus survives tick updates.
    /// </summary>
    public string StableKey() => $"{Peer.InstanceId}:{Peer.Name}:{Peer.Address}:{Peer.AudioPort}";

    public override string ToString()
    {
        // Base label: "hostname (ip)" for discovered peers, just "ip" for manual-by-IP entries
        // (where hostname equals the IP address). Avoids "192.168.1.95 (192.168.1.95)" duplication.
        var addr = Peer.Address.ToString();
        var name = DisplayNameProvider?.Invoke(Peer) ?? Peer.Name;
        var basePart = name == addr ? addr : $"{name} ({addr})";

        if (!Status.Connected)
        {
            return basePart;
        }

        // Connected line — extra metadata after a dash. Comma-separated so NVDA reads naturally:
        //   "Andre's PC (1.2.3.4) — connected, Opus 10ms, send and receive, 32ms"
        var parts = new List<string> { "connected" };
        if (Status.CodecLabel is { Length: > 0 } codec) parts.Add(codec);

        var direction = (Status.Sending, Status.Receiving) switch
        {
            (true, true) => "send and receive",
            (true, false) => "send only",
            (false, true) => "receive only",
            _ => null,
        };
        if (direction is not null) parts.Add(direction);

        if (Status.RttMs is { } rtt) parts.Add($"{rtt}ms");

        return $"{basePart} — {string.Join(", ", parts)}";
    }
}
