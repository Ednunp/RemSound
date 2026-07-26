using System.Diagnostics;
using System.Net;

namespace RemSound.Core;

/// <summary>
/// Bidirectional UDP heartbeat: every selected peer is pinged once per second; pongs are
/// echoed back; the sender computes RTT against its own monotonic clock and tracks per-peer
/// reachability state.
///
/// SINGLE-PORT MODEL (2026-05-06):
///   This service no longer binds a UDP socket of its own. All heartbeat traffic flows on
///   the audio port (default 47830) — outbound via the audio sender's UDP socket (which is
///   the same NAT pinhole the audio packets use), inbound via the audio receiver's listener
///   (LAN: peer pings our audio port directly) or the audio sender's recv-side (WAN/relay:
///   pings come back through the relay on our sender's ephemeral source port). The App
///   forwards heartbeat packets from both sources into <see cref="HandleInjectedPacket"/>.
///
///   Why we collapsed audioPort+2 into the audio port:
///     * The +2 socket only existed because the audio receiver used to be bound on demand
///       (driven by the user's "Receive audio" tick), and heartbeats need a socket that's
///       bound regardless. Splitting <see cref="AudioReceiver.Start"/> from
///       <see cref="AudioReceiver.SetPlaybackEnabled"/> removed that gap — the listener
///       socket is bound for the duration of a connection.
///     * Asymmetric send-only / receive-only configs broke heartbeat under the old dual-
///       transport scheme (relay path drops the ping when the peer's audio port has no
///       listener). With the listener always bound and the heartbeat travelling on the
///       same port, the asymmetry disappears.
///     * One firewall rule, one router pinhole, one mental model.
///
/// Why 1 Hz cadence instead of the more common 20–25 s NAT-keepalive interval:
///   - Tiny packets (21 B), so 21 B/s is irrelevant overhead.
///   - Detects unreachability within ~3–5 s instead of 30+ s.
///   - 1 s ≪ NAT timeout (30 s+ on virtually all consumer routers), so keepalive role is
///     covered too.
///
/// RTT computation borrows the RTCP DLSR pattern (RFC 3550) in simplified form: the originator
/// stamps the Ping with its own Stopwatch.ElapsedMilliseconds; the responder echoes that value
/// verbatim in the Pong; the originator computes <c>now - pongPayload.originatorTickMs</c>
/// using only its own clock. No peer-clock sync needed.
/// </summary>
public sealed class HeartbeatService : IDisposable
{
    /// <summary>How often a Ping is sent to each tracked peer.</summary>
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(1);
    /// <summary>If the most recent Pong is younger than this, the peer is healthy.</summary>
    public static readonly TimeSpan HealthyWindow = TimeSpan.FromSeconds(2);
    /// <summary>If the most recent Pong is older than this, the peer is unreachable.</summary>
    public static readonly TimeSpan UnreachableWindow = TimeSpan.FromSeconds(5);

    private readonly Action<string>? onDiagnostic;
    private readonly object gate = new();
    private readonly Dictionary<string, PeerState> peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stopwatch monotonic = Stopwatch.StartNew();

    private CancellationTokenSource? cts;
    private Task? sendTask;
    private uint sequence;
    // Reusable outbound packet buffer for the once-per-second ping fan-out. Pre-2026-05-23
    // SendPings did `var bytes = packet.ToArray()` on every call (a 21-byte allocation +
    // GC header). Trivial in absolute terms — ~3 small allocations/sec/peer — but the
    // SendPings thread has only one writer so a single reused array is straightforward and
    // makes the pattern explicit. Item 14 of RemSoundefficiency.md.
    private readonly byte[] outboundPingBuffer = new byte[RemPacket.HeaderSize + RemPacket.HeartbeatPayloadSize];

    /// <summary>
    /// Outbound transport for heartbeat packets. REQUIRED — without it Start() succeeds but
    /// no pings are emitted. Wire it to <see cref="RemSound.Sender.AudioSender.SendVia"/>
    /// (or any equivalent UDP send delegate) so heartbeats share the audio sender's NAT
    /// pinhole. The bool return is the success indicator (true = sent, false = transport
    /// error / socket not bound). Pong replies route through the same transport.
    /// </summary>
    public Func<byte[], int, IPEndPoint, bool>? SendTransport { get; set; }

    public HeartbeatService(Action<string>? onDiagnostic = null)
    {
        this.onDiagnostic = onDiagnostic;
    }

    public bool IsRunning => sendTask is not null;

    public void Start()
    {
        lock (gate)
        {
            if (IsRunning) return;
            cts = new CancellationTokenSource();
            sendTask = Task.Run(() => SendLoop(cts.Token));
            onDiagnostic?.Invoke("started (single-port)");
        }
    }

    public void Stop()
    {
        lock (gate) StopInternal();
    }

    private void StopInternal()
    {
        try { cts?.Cancel(); } catch { /* ignore */ }
        try { sendTask?.Wait(TimeSpan.FromMilliseconds(500)); } catch { /* ignore */ }
        cts?.Dispose();
        cts = null;
        sendTask = null;
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Replaces the tracked peer set. Each endpoint is the peer's audio port — heartbeat
    /// targets the same port (single-port model). Removing a peer wipes its tracked state
    /// immediately; adding a new one starts in the Unknown state until the first Pong arrives.
    /// </summary>
    public void SetTrackedPeers(IEnumerable<IPEndPoint> audioEndpoints)
    {
        lock (gate)
        {
            var desired = new Dictionary<string, IPEndPoint>(StringComparer.OrdinalIgnoreCase);
            foreach (var ep in audioEndpoints)
            {
                desired[KeyFor(ep)] = ep;
            }

            // Remove peers that are no longer selected.
            foreach (var key in peers.Keys.Where(k => !desired.ContainsKey(k)).ToList())
            {
                peers.Remove(key);
            }

            // Add or update peers.
            foreach (var (key, ep) in desired)
            {
                if (!peers.TryGetValue(key, out var p))
                {
                    peers[key] = new PeerState { AudioEndpoint = ep };
                }
                else
                {
                    p.AudioEndpoint = ep;
                }
            }
        }
    }

    /// <summary>
    /// Snapshot of the current health state of every tracked peer. Safe to call from any thread.
    /// </summary>
    public IReadOnlyList<PeerHealth> GetAllPeerHealth()
    {
        lock (gate)
        {
            var nowUtc = DateTime.UtcNow;
            var result = new List<PeerHealth>(peers.Count);
            foreach (var p in peers.Values)
            {
                result.Add(SnapshotHealthLocked(p, nowUtc));
            }
            return result;
        }
    }

    /// <summary>
    /// One-line summary suitable for the snapshot log column or status label.
    /// "no peers" / "192.168.1.5: 24ms" / "192.168.1.5: 24ms, 192.168.1.6: unreachable 7s".
    /// </summary>
    public string GetHealthSummary()
    {
        var entries = GetAllPeerHealth();
        if (entries.Count == 0) return "no peers";
        return string.Join(", ", entries.Select(FormatPeer));

        static string FormatPeer(PeerHealth p) => p.State switch
        {
            PeerHealthState.Healthy when p.RttMs is { } rtt => $"{p.AudioEndpoint.Address}: {rtt}ms",
            PeerHealthState.Stale when p.AgeOfLastPong is { } age => $"{p.AudioEndpoint.Address}: stale {age.TotalSeconds:0.0}s",
            PeerHealthState.Unreachable when p.AgeOfLastPong is { } age => $"{p.AudioEndpoint.Address}: unreachable {age.TotalSeconds:0.0}s",
            _ => $"{p.AudioEndpoint.Address}: pending",
        };
    }

    private static string KeyFor(IPEndPoint ep) => $"{ep.Address}:{ep.Port}";

    private static PeerHealth SnapshotHealthLocked(PeerState p, DateTime nowUtc)
    {
        if (p.LastPongUtc is null)
        {
            // Never heard from. If we've been pinging for a while with no response, that's
            // "unreachable"; otherwise still "unknown / pending".
            if (p.FirstPingSentUtc is { } firstPing && nowUtc - firstPing > UnreachableWindow)
            {
                return new PeerHealth(p.AudioEndpoint, PeerHealthState.Unreachable, null, nowUtc - firstPing);
            }
            return new PeerHealth(p.AudioEndpoint, PeerHealthState.Unknown, null, null);
        }

        var age = nowUtc - p.LastPongUtc.Value;
        var state = age <= HealthyWindow
            ? PeerHealthState.Healthy
            : (age <= UnreachableWindow ? PeerHealthState.Stale : PeerHealthState.Unreachable);
        return new PeerHealth(p.AudioEndpoint, state, p.RttEwmaMs, age);
    }

    private async Task SendLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PingInterval, ct).ConfigureAwait(false);
                SendPings();
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (Exception ex)
        {
            onDiagnostic?.Invoke($"send loop ended: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void SendPings()
    {
        var transport = SendTransport;
        if (transport is null) return;

        List<PeerState> targets;
        lock (gate)
        {
            targets = peers.Values.ToList();
            var nowUtc = DateTime.UtcNow;
            foreach (var p in targets) p.FirstPingSentUtc ??= nowUtc;
        }

        // Build packet directly into the reusable outboundPingBuffer instead of stack-
        // allocating + ToArray(). Same wire format, no per-call allocation. SendPings runs
        // exclusively on the timer task — single writer — so no lock needed around the
        // reuse. streamId is fixed at 0xFFFF for heartbeats so it's distinguishable in any
        // future stream-aware filter; sequence increments locally per send.
        var seq = Interlocked.Increment(ref sequence);
        var tickMs = monotonic.ElapsedMilliseconds;
        var packetSpan = outboundPingBuffer.AsSpan();
        RemPacket.WriteHeader(packetSpan, RemPacketType.Heartbeat, 0xFFFF, seq);
        RemPacket.WriteHeartbeatPayload(packetSpan[RemPacket.HeaderSize..], HeartbeatKind.Ping, tickMs);

        foreach (var p in targets)
        {
            try
            {
                var ok = transport(outboundPingBuffer, outboundPingBuffer.Length, p.AudioEndpoint);
                onDiagnostic?.Invoke($"send seq={seq} to={p.AudioEndpoint} {(ok ? "ok" : "FAILED")}");
            }
            catch (Exception ex)
            {
                onDiagnostic?.Invoke($"send to {p.AudioEndpoint} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Inject a heartbeat packet that arrived on one of the App's other sockets (the audio
    /// receiver's listener for LAN, or the audio sender's recv-side for relay-return). This
    /// is the ONLY inbound path in single-port mode — the service no longer binds a socket
    /// of its own. Same processing as the old local-socket receive: parse, echo Pongs back
    /// via <see cref="SendTransport"/>, update RTT state on Pong arrival.
    /// </summary>
    public void HandleInjectedPacket(byte[] buffer, int length, IPEndPoint remote)
    {
        // Tighten the buffer to `length` so HandlePacket's spans don't read trailing bytes.
        if (length < buffer.Length)
        {
            var trimmed = new byte[length];
            Array.Copy(buffer, trimmed, length);
            HandlePacket(trimmed, remote);
        }
        else
        {
            HandlePacket(buffer, remote);
        }
    }

    private void HandlePacket(byte[] buffer, IPEndPoint remote)
    {
        if (!RemPacket.TryReadHeader(buffer, out var type, out _, out _)) return;
        if (type != RemPacketType.Heartbeat) return;
        var payload = buffer.AsSpan(RemPacket.HeaderSize);
        if (!RemPacket.TryReadHeartbeat(payload, out var kind, out var originatorTickMs)) return;

        if (kind == HeartbeatKind.Ping)
        {
            onDiagnostic?.Invoke($"recv ping from={remote}");

            // Echo the originator's timestamp back to them as a Pong. Reply target is the
            // remote source endpoint (whatever socket the ping came in on, that's where to
            // send the pong) — this works for both LAN-direct (peer's audio port) and
            // relay-return (relay's source port) without us needing to know which.
            Span<byte> reply = stackalloc byte[RemPacket.HeaderSize + RemPacket.HeartbeatPayloadSize];
            var seq = Interlocked.Increment(ref sequence);
            RemPacket.WriteHeader(reply, RemPacketType.Heartbeat, 0xFFFF, seq);
            RemPacket.WriteHeartbeatPayload(reply[RemPacket.HeaderSize..], HeartbeatKind.Pong, originatorTickMs);
            var bytes = reply.ToArray();

            try { SendTransport?.Invoke(bytes, bytes.Length, remote); }
            catch { /* UDP, ignore */ }
            return;
        }

        // Pong: compute RTT vs our own clock, update peer state. We expect this peer to be
        // tracked (we sent a ping that produced this pong) — but we match by IP only since
        // the source port of an incoming pong is the peer's outbound source port (NAT can
        // rewrite, and on LAN it's the peer's ephemeral sender port, not the audio port).
        var nowMs = monotonic.ElapsedMilliseconds;
        var rttMs = (int)Math.Max(0, nowMs - originatorTickMs);
        var nowUtc = DateTime.UtcNow;
        var matchedCount = 0;
        lock (gate)
        {
            foreach (var p in peers.Values)
            {
                if (!p.AudioEndpoint.Address.Equals(remote.Address)) continue;
                p.LastRttMs = rttMs;
                p.RttEwmaMs = p.RttEwmaMs is null ? rttMs : (int)(p.RttEwmaMs.Value * 0.7 + rttMs * 0.3);
                p.LastPongUtc = nowUtc;
                matchedCount++;
            }
        }
        // Diagnostic for the Pong path. matched=0 means we got a pong from an IP we don't
        // track (suspicious — possible loopback / echo), >0 is the normal case.
        onDiagnostic?.Invoke($"recv pong from={remote} rtt={rttMs}ms matched={matchedCount} origTickMs={originatorTickMs} nowMs={nowMs}");
    }

    /// <summary>Test seam: run the REAL health-state derivation (SnapshotHealthLocked) against a
    /// CONTROLLED clock, so the Healthy → Stale → Unreachable windows — the numbers every peer's armed
    /// state hangs off — are pinned without waiting real seconds in a test.</summary>
    internal static PeerHealth SnapshotHealthForTest(
        IPEndPoint endpoint, DateTime? lastPongUtc, DateTime? firstPingSentUtc, int? rttEwmaMs, DateTime nowUtc) =>
        SnapshotHealthLocked(new PeerState
        {
            AudioEndpoint = endpoint,
            LastPongUtc = lastPongUtc,
            FirstPingSentUtc = firstPingSentUtc,
            RttEwmaMs = rttEwmaMs,
        }, nowUtc);

    private sealed class PeerState
    {
        public IPEndPoint AudioEndpoint { get; set; } = null!;
        public DateTime? FirstPingSentUtc { get; set; }
        public DateTime? LastPongUtc { get; set; }
        public int? LastRttMs { get; set; }
        public int? RttEwmaMs { get; set; }
    }
}

public enum PeerHealthState
{
    Unknown,
    Healthy,
    Stale,
    Unreachable,
}

public sealed record PeerHealth(
    IPEndPoint AudioEndpoint,
    PeerHealthState State,
    int? RttMs,
    TimeSpan? AgeOfLastPong);
