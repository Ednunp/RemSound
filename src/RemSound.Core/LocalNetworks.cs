using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RemSound.Core;

/// <summary>
/// Whether an address is on one of this computer's own networks - the same subnet as one of its IPv4 addresses on an
/// interface that is up. For the log (2026-10-01): a peer in the discovered list that is NOT on any of them cannot have
/// been heard by broadcast, so it announced itself straight to this computer - over a VPN such as Tailscale, or the
/// internet (Ed found Andre's PC in his discovered list with no server involved, and the log could not say why).
/// A VPN that gives this computer a single address (Tailscale's /32) puts none of its peers on "this computer's own
/// network", which is the right answer: broadcast does not cross it.
/// </summary>
public static class LocalNetworks
{
    /// <summary>This computer's own IPv4 subnets right now: each address on an interface that is up, with its mask.</summary>
    public static IReadOnlyList<(IPAddress Address, IPAddress Mask)> OwnSubnets()
    {
        var found = new List<(IPAddress, IPAddress)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && unicast.IPv4Mask is { } mask)
                        found.Add((unicast.Address, mask));
            }
        }
        catch (NetworkInformationException) { /* no answer from Windows: no subnets, so nothing is called local */ }
        return found;
    }

    /// <summary>Whether <paramref name="address"/> is on one of this computer's own networks right now.</summary>
    public static bool IsOnOwnNetwork(IPAddress address) => IsOnOwnNetwork(address, OwnSubnets());

    /// <summary>Whether <paramref name="address"/> is on one of <paramref name="subnets"/>.</summary>
    public static bool IsOnOwnNetwork(IPAddress address, IEnumerable<(IPAddress Address, IPAddress Mask)> subnets)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var target = address.GetAddressBytes();
        foreach (var (own, mask) in subnets)
        {
            var o = own.GetAddressBytes();
            var m = mask.GetAddressBytes();
            if (o.Length != 4 || m.Length != 4) continue;
            var same = true;
            for (var i = 0; i < 4 && same; i++) same = (target[i] & m[i]) == (o[i] & m[i]);
            if (same) return true;
        }
        return false;
    }
}
