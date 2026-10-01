using System.Net;
using RemSound.Core;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>DISCOVERY LOG (2026-10-01). Ed found Andre's PC in his discovered peers with neither of them on a server, and
    /// the log could not say how: the discovered list's comings and goings were never written down. Each peer that appears,
    /// moves or leaves is logged once, with where it was heard - on this computer's own network, or not, in which case it
    /// announced itself straight to this computer (over a VPN such as Tailscale, or the internet). Tailscale gives each
    /// computer a single address, so a Tailscale peer is never on this computer's own network.</summary>
    private static string? DiscoveryIsLogged()
    {
        IReadOnlyList<(IPAddress, IPAddress)> own =
        [
            (IPAddress.Parse("192.168.1.95"), IPAddress.Parse("255.255.255.0")),
            (IPAddress.Parse("100.64.0.7"), IPAddress.Parse("255.255.255.255")),
        ];
        Check(LocalNetworks.IsOnOwnNetwork(IPAddress.Parse("192.168.1.15"), own), "a laptop on the same /24 must be on this computer's own network");
        Check(!LocalNetworks.IsOnOwnNetwork(IPAddress.Parse("100.64.12.34"), own), "another computer's Tailscale address must not be: Tailscale gives each a /32");
        Check(!LocalNetworks.IsOnOwnNetwork(IPAddress.Parse("203.0.113.50"), own), "nor an internet address");
        Check(LocalNetworks.IsOnOwnNetwork(IPAddress.Parse("192.168.1.15").MapToIPv6(), own), "an IPv4 address written as IPv6 must read as itself");
        Check(!LocalNetworks.IsOnOwnNetwork(IPAddress.Parse("192.168.1.15"), []), "with no subnets known, nothing is local");
        Check(LocalNetworks.OwnSubnets().All(s => s.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork),
            "this computer's own subnets must be IPv4 ones");

        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        MainForm? form = null;
        var logged = new List<string>();
        try
        {
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            form.OwnSubnetsForTest = () => own;
            form.LogForTest.EventTapForTest = line => { lock (logged) logged.Add(line); };
            string[] Logged()
            {
                lock (logged)
                {
                    var lines = logged.Where(l => l.StartsWith("discovery: ", StringComparison.Ordinal)).ToArray();
                    logged.Clear();
                    return lines;
                }
            }

            var discovery = form.DiscoveryForTest;
            var laptop = new PeerAnnouncement(Guid.NewGuid(), "LAPTOP", 47830, true, true, DateTime.UtcNow, IPAddress.Parse("192.168.1.15"));
            var studio = new PeerAnnouncement(Guid.NewGuid(), "STUDIO-PC", 47830, true, true, DateTime.UtcNow, IPAddress.Parse("100.64.12.34"));
            discovery.RecordForTest(laptop);
            discovery.RecordForTest(studio);
            form.RefreshKnownPeersForTest();
            var arrived = Logged();
            Check(arrived.Length == 2, $"two peers appearing must be two lines ({string.Join(" | ", arrived)})");
            Check(arrived.Contains("discovery: \"LAPTOP\" is announcing from 192.168.1.15 - on this computer's own network"),
                $"the laptop on the same network must be logged as on it ({string.Join(" | ", arrived)})");
            Check(arrived.Any(l => l.StartsWith("discovery: \"STUDIO-PC\" is announcing from 100.64.12.34 - not on this computer's own networks", StringComparison.Ordinal)
                                   && l.Contains("Tailscale", StringComparison.Ordinal)),
                $"a peer over Tailscale must be logged as announcing itself straight here ({string.Join(" | ", arrived)})");

            discovery.RecordForTest(laptop with { LastSeenUtc = DateTime.UtcNow });
            form.RefreshKnownPeersForTest();
            Check(Logged().Length == 0, "the same peers announcing again must log nothing");

            discovery.RecordForTest(laptop with { Address = IPAddress.Parse("192.168.1.16"), LastSeenUtc = DateTime.UtcNow });
            form.RefreshKnownPeersForTest();
            var moved = Logged();
            Check(moved.Length == 1 && moved[0].StartsWith("discovery: \"LAPTOP\" now announces from 192.168.1.16 (was 192.168.1.15)", StringComparison.Ordinal),
                $"a peer announcing from a new address must be logged once ({string.Join(" | ", moved)})");

            discovery.BackdateForTest(studio.InstanceId, TimeSpan.FromSeconds(30));
            form.RefreshKnownPeersForTest();
            var left = Logged();
            Check(left.Length == 1 && left[0] == "discovery: \"STUDIO-PC\" (100.64.12.34) has stopped announcing and has left the discovered list",
                $"a peer gone quiet must be logged once as leaving, and nobody else ({string.Join(" | ", left)})");
            return "the subnet sums right (a /24 neighbour local; a Tailscale /32, the internet and an empty list not); a peer appearing, "
                 + "moving and leaving each logged once with where it was heard; the same announcement again logs nothing";
        }
        finally
        {
            if (form is not null) { form.LogForTest.EventTapForTest = null; form.OwnSubnetsForTest = null; }
            try { form?.Dispose(); } catch { /* teardown */ }
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
        }
    }
}
