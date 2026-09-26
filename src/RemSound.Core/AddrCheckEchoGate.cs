using System.Net;

namespace RemSound.Core;

/// <summary>
/// Whether to answer a server's address check (packet type 10) - shared by the app and the lock-screen service
/// (2026-09-25 sweep; Ed: "yes all 10").
///
/// <para>Both answered EVERY address check, from anyone, straight back to where it came from. Two RemSound computers each
/// do that, so one forged packet - A's address sent to B - bounced between them for ever, thousands of times a second
/// each way, until one was closed; and every bounce also asked the window whether that "peer" was a server. A server only
/// ever checks an address that has sent it something, so the answer goes only to somebody this copy is talking to: a
/// peer it pings (every ticked peer, and the server it is on). And at most once a second to each.</para>
/// </summary>
public sealed class AddrCheckEchoGate
{
    private readonly object gate = new();
    private readonly Dictionary<string, long> lastEchoMs = new(StringComparer.Ordinal);
    private readonly Action<string>? log;
    private long refused;
    private long refusedLoggedAtMs = long.MinValue;
    private string? lastRefusedFrom;

    public static readonly TimeSpan EchoInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan RefusalLogInterval = TimeSpan.FromMinutes(1);

    public AddrCheckEchoGate(Action<string>? log = null) => this.log = log;

    /// <summary>Gate seam: how many address checks have been refused.</summary>
    public long RefusedCount => Interlocked.Read(ref refused);

    /// <summary>Answer this one? <paramref name="talkingTo"/> says whether this copy is talking to that address.</summary>
    public bool ShouldEcho(IPEndPoint remote, Func<IPAddress, bool> talkingTo, long nowMs)
    {
        if (!talkingTo(remote.Address))
        {
            Interlocked.Increment(ref refused);
            ReportRefusalsIfDue(remote, nowMs);
            return false;
        }
        lock (gate)
        {
            var key = remote.ToString();
            if (lastEchoMs.TryGetValue(key, out var last) && nowMs - last < (long)EchoInterval.TotalMilliseconds) return false;
            if (lastEchoMs.Count >= 256) lastEchoMs.Clear();
            lastEchoMs[key] = nowMs;
            return true;
        }
    }

    /// <summary>A line at most once a minute: how many were refused, and from where last - so a flood is on record without
    /// writing a line for each packet of it.</summary>
    private void ReportRefusalsIfDue(IPEndPoint remote, long nowMs)
    {
        string? line = null;
        lock (gate)
        {
            lastRefusedFrom = remote.ToString();
            if (refusedLoggedAtMs != long.MinValue && nowMs - refusedLoggedAtMs < (long)RefusalLogInterval.TotalMilliseconds) return;
            refusedLoggedAtMs = nowMs;
            line = $"addr-check: not answered - {Interlocked.Read(ref refused)} address check(s) so far from addresses this copy is not talking to (latest from {lastRefusedFrom})";
        }
        log?.Invoke(line);
    }
}
