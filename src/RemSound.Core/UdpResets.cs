using System.Net.Sockets;

namespace RemSound.Core;

/// <summary>
/// Windows' UDP "connection reset" quirk, and what RemSound's receive loops do about it.
///
/// <para>When a datagram we send is answered by an ICMP port-unreachable (a peer that has gone away; over Tailscale, any
/// ticked peer that is offline, because Tailscale answers for it), Windows reports it as WSAECONNRESET on the NEXT
/// <c>ReceiveFrom</c> on that socket - a receive that has nothing to do with the dead peer. With one dead peer pinged once
/// a second, and audio still going to it until it is pruned, that is a steady trickle of errors on the socket every
/// heartbeat reply comes back on. The receive loops treated a run of them as a stuck socket and backed off up to a second
/// per error (2026-08-23 audit, S9), so replies queued behind the sleeps, live peers were timed out as "stopped
/// answering", and the backoff showed up as 1.5-3 s "worst ping" readings. 2026-10-23, issue #36.</para>
///
/// <para>Two halves: <see cref="IgnoreIcmpResets"/> asks Windows not to report them at all (SIO_UDP_CONNRESET, and
/// SIO_UDP_NETRESET for the TTL-expired kind), and <see cref="IsPerDatagramNoise"/> tells the loops which errors, if they
/// get through anyway, say nothing about the socket and are skipped without a backoff.</para>
/// </summary>
public static class UdpResets
{
    // From mstcpip.h: _WSAIOW(IOC_VENDOR, 12) and _WSAIOW(IOC_VENDOR, 15).
    private const int SioUdpConnReset = unchecked((int)0x9800000C);
    private const int SioUdpNetReset = unchecked((int)0x9800000F);

    /// <summary>Turns off the ICMP-to-WSAECONNRESET/WSAENETRESET reporting on a UDP socket. Call once, after Bind.
    /// Returns whether Windows took the connection-reset half (the one that matters); a socket that refuses keeps
    /// working, and the receive loop's <see cref="IsPerDatagramNoise"/> still keeps the errors cheap.</summary>
    public static bool IgnoreIcmpResets(Socket socket)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var off = new byte[4];   // a BOOL of FALSE: do not report
        var took = false;
        try { socket.IOControl(SioUdpConnReset, off, null); took = true; } catch (SocketException) { }
        try { socket.IOControl(SioUdpNetReset, off, null); } catch (SocketException) { }
        return took;
    }

    /// <summary>An error that belongs to one datagram, not to the socket: an ICMP reset from a peer we sent to (connection
    /// or network/TTL), or one datagram too big for the buffer (that datagram is lost, the next one reads fine). The loop
    /// goes straight on to the next receive - no count, no backoff. Anything else is still counted and backed off from, so
    /// a socket that really is stuck stays visible (2026-08-23 audit, S9).</summary>
    public static bool IsPerDatagramNoise(SocketError error) =>
        error is SocketError.ConnectionReset or SocketError.NetworkReset or SocketError.MessageSize;
}
