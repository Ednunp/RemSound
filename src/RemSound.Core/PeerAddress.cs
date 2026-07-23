using System.Net;
using System.Net.Sockets;

namespace RemSound.Core;

/// <summary>
/// ONE home for peer-address handling. The "host[:port]" split and the resolve-preferring-IPv4 rule
/// used to live as byte-identical private copies in the main window AND the service host (plus two more
/// resolve copies in the peer paths) — exactly the kind of duplication that silently drifts. Both sides
/// now call this. Bare numeric strings are hosts (no port); a second colon means an IPv6 literal, which
/// the manual peer field doesn't formally support yet, so the whole text is treated as the host rather
/// than mis-parsed as host:port.
/// </summary>
public static class PeerAddress
{
    /// <summary>Parse "host:port" or just "host". Returns (host, null) when no valid port is present.
    /// Ports outside 1–65535 are not ports.</summary>
    public static (string Host, int? Port) Split(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (text ?? string.Empty, null);
        text = text.Trim();
        var colon = text.LastIndexOf(':');
        if (colon <= 0 || colon == text.Length - 1) return (text, null);
        var host = text[..colon];
        if (host.Contains(':')) return (text, null); // IPv6 literal — the whole thing is the host
        return int.TryParse(text[(colon + 1)..], out var port) && port is >= 1 and <= 65535
            ? (host, port)
            : (text, null);
    }

    /// <summary>The address-family preference every resolve path shares: IPv4 first (the wire format and
    /// discovery are IPv4 today), otherwise whatever came back.</summary>
    public static IPAddress? PreferIPv4(IPAddress[]? addresses) =>
        addresses is null || addresses.Length == 0
            ? null
            : addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];

    /// <summary>Resolve a peer entry's HOST part to an address (literal IPs short-circuit DNS).
    /// Null on any failure — callers skip unresolvable peers.</summary>
    public static IPAddress? ResolveHost(string entry)
    {
        var (host, _) = Split(entry);
        if (IPAddress.TryParse(host, out var direct)) return direct;
        try { return PreferIPv4(Dns.GetHostAddresses(host)); }
        catch { return null; }
    }

    /// <summary>Async twin of <see cref="ResolveHost"/> for UI callers.</summary>
    public static async Task<IPAddress?> ResolveHostAsync(string entry)
    {
        var (host, _) = Split(entry);
        if (IPAddress.TryParse(host, out var direct)) return direct;
        try { return PreferIPv4(await Dns.GetHostAddressesAsync(host).ConfigureAwait(false)); }
        catch { return null; }
    }
}
