using System.Net;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// The discovered list, written down (2026-10-01). Ed found Andre's PC in his discovered peers while neither of them was
/// on a server, and the log could not say how it got there: arrivals and departures were never logged. Now each is, with
/// where the peer was heard - on this computer's own network (broadcast reaches it), or not (so it announced itself
/// straight to this computer: over a VPN such as Tailscale, or the internet, which is what put Andre there - his copy
/// remembers Ed's address).
/// </summary>
public sealed partial class MainForm
{
    private readonly Dictionary<Guid, (string Name, IPAddress Address)> discoveryLogged = [];

    /// <summary>Log who joined, moved or left the discovered list since the last look. On the UI thread, from
    /// <see cref="RefreshKnownPeers"/>; quiet when nothing changed.</summary>
    private void LogDiscoveryChanges()
    {
        var now = discovery.Peers.ToDictionary(p => p.InstanceId, p => (p.Name, p.Address));
        IReadOnlyList<(IPAddress, IPAddress)>? subnets = null;
        string Where(IPAddress address)
        {
            subnets ??= OwnSubnetsForTest?.Invoke() ?? LocalNetworks.OwnSubnets();
            return LocalNetworks.IsOnOwnNetwork(address, subnets)
                ? "on this computer's own network"
                : "not on this computer's own networks, so it announces itself straight to this computer (over a VPN such as "
                  + "Tailscale, or the internet), because this computer or that one has the other's address";
        }
        foreach (var (id, (name, address)) in now)
        {
            if (!discoveryLogged.TryGetValue(id, out var was))
                logFile.Event($"discovery: \"{name}\" is announcing from {address} - {Where(address)}");
            else if (!Equals(was.Address, address))
                logFile.Event($"discovery: \"{name}\" now announces from {address} (was {was.Address}) - {Where(address)}");
        }
        foreach (var (id, (name, address)) in discoveryLogged)
            if (!now.ContainsKey(id))
                logFile.Event($"discovery: \"{name}\" ({address}) has stopped announcing and has left the discovered list");
        discoveryLogged.Clear();
        foreach (var (id, peer) in now) discoveryLogged[id] = peer;
    }

    /// <summary>Gate seams: this window's discovery, a refresh of the peer lists as an announcement brings, and the
    /// computer's own subnets in place of the real ones.</summary>
    internal PeerDiscoveryService DiscoveryForTest => discovery;
    internal void RefreshKnownPeersForTest() => RefreshKnownPeers();
    internal Func<IReadOnlyList<(IPAddress, IPAddress)>>? OwnSubnetsForTest;
}
