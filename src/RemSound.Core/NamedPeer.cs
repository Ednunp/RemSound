namespace RemSound.Core;

/// <summary>One entry in the machine-wide "named peers" book — a peer the user has deliberately given
/// a friendly name. Keyed in <see cref="AppConfig.NamedPeers"/> by the peer's stable identity (its
/// machine name, or its address for a nameless manual peer). Only renamed peers are recorded; unnamed
/// machines are never added, so the book stays small. Last address / last-seen are updated whenever the
/// peer connects, for the Manage named peers dialog.</summary>
public sealed class NamedPeer
{
    /// <summary>The peer's machine name (for display). May equal the address for a manual-by-IP peer.</summary>
    public string MachineName { get; set; } = "";

    /// <summary>The user's chosen friendly name. Never blank for a stored entry.</summary>
    public string FriendlyName { get; set; } = "";

    /// <summary>The address this peer last connected from, or null if not seen since it was named.</summary>
    public string? LastAddress { get; set; }

    /// <summary>When this peer was last seen connected (UTC), or default if not seen since named.</summary>
    public DateTime LastSeenUtc { get; set; }
}
