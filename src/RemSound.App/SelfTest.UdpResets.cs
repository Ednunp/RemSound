using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RemSound.Core;
using RemSound.Sender;

namespace RemSound.App;

/// <summary>
/// 2026-10-23 (issue #36), from a 2-hour session over Tailscale with an offline peer ticked: every ping and audio packet to that peer
/// came back as an ICMP port-unreachable, Windows turned each into WSAECONNRESET on the next receive on the sender's
/// socket, and the receive loop took the trickle for a stuck socket - "sender inbound socket error x2048: ConnectionReset
/// - backing off" - so the live peer's pongs waited behind the backoff and it was dropped for not answering. See
/// <see cref="UdpResets"/>.
/// </summary>
internal static partial class SelfTest
{
    private static string? OfflinePeerResetsDoNotStallTheServerPath()
    {
        // A loopback port nobody is on: what an offline peer looks like to Windows.
        IPEndPoint DeadPort()
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return (IPEndPoint)s.LocalEndPoint!;
        }
        SocketError? AfterSendingToTheDead(bool ignoreResets)
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            if (ignoreResets) Check(UdpResets.IgnoreIcmpResets(s), "Windows must accept SIO_UDP_CONNRESET on a bound UDP socket");
            s.ReceiveTimeout = 300;
            s.SendTo(new byte[1], DeadPort());
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            try { s.ReceiveFrom(new byte[16], ref from); return null; }
            catch (SocketException ex) { return ex.SocketErrorCode; }
        }

        // The premise, on a plain socket: the reset is real here, or the rest proves nothing.
        var plain = AfterSendingToTheDead(ignoreResets: false);
        Check(plain == SocketError.ConnectionReset,
            $"premise: on a plain UDP socket, a send to a closed loopback port must surface as ConnectionReset on the next receive (got {plain?.ToString() ?? "a datagram"})");
        var quiet = AfterSendingToTheDead(ignoreResets: true);
        Check(quiet == SocketError.TimedOut,
            $"with UdpResets.IgnoreIcmpResets the same receive must just wait for data - no reset (got {quiet?.ToString() ?? "a datagram"})");

        // The real sender: a burst to a dead peer, then a live peer's packets must come straight through.
        var said = new List<string>();
        using var sender = new AudioSender();
        sender.Diagnostic = line => { lock (said) said.Add(line); };
        var arrived = 0;
        sender.OnInboundPacket = (_, _, _) => Interlocked.Increment(ref arrived);
        sender.StartReceiving();
        var dead = DeadPort();
        var payload = new byte[RemPacket.HeaderSize + 9];
        for (var i = 0; i < 200; i++) sender.SendRaw(payload, payload.Length, dead);
        using var live = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var to = new IPEndPoint(IPAddress.Loopback, sender.LocalPortForTest);
        var worstMs = 0.0;
        for (var i = 0; i < 20; i++)
        {
            var before = Volatile.Read(ref arrived);
            var sent = Stopwatch.GetTimestamp();
            live.Send(payload, payload.Length, to);
            Check(WaitUntil(() => Volatile.Read(ref arrived) > before, 1000),
                $"THE STALL: packet {i + 1} from a live peer must reach the sender's receive loop after 200 sends to an offline one");
            worstMs = Math.Max(worstMs, Stopwatch.GetElapsedTime(sent).TotalMilliseconds);
            if (i % 2 == 0) sender.SendRaw(payload, payload.Length, dead);   // and the dead one keeps being pinged
        }
        Check(worstMs < 100, $"and promptly - a backoff sleep would show here as hundreds of ms (worst {worstMs:0} ms)");
        string[] lines;
        lock (said) lines = said.Where(l => l.Contains("socket error", StringComparison.Ordinal)).ToArray();
        Check(lines.Length == 0, $"nothing about an offline peer may be logged as a socket error ({string.Join(" | ", lines)})");

        // The audit's S9 stays: the errors that do mean a stuck socket are still counted and backed off from.
        foreach (var noise in new[] { SocketError.ConnectionReset, SocketError.NetworkReset, SocketError.MessageSize })
            Check(UdpResets.IsPerDatagramNoise(noise), $"{noise} belongs to one datagram and must be skipped without a backoff");
        foreach (var stuck in new[] { SocketError.InvalidArgument, SocketError.NetworkDown, SocketError.NoBufferSpaceAvailable, SocketError.NotSocket })
            Check(!UdpResets.IsPerDatagramNoise(stuck), $"{stuck} can mean a stuck socket and must still be counted and backed off from (audit S9)");

        return $"an offline peer's resets never reach the receive loop; 20 packets from a live one came through after 200 sends to the dead one, worst {worstMs:0} ms; S9's backoff kept for real errors";
    }
}
