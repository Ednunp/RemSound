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
    // The timestamps our last pings carried. A pong counts only if it answers one of them: in a relay group everyone's
    // pongs reach everyone, each carrying the clock of whoever pinged, and read against ours they are nonsense.
    private readonly long[] recentPingTicks = new long[16];
    private int recentPingNext;

    private CancellationTokenSource? cts;
    private Task? sendTask;
    private uint sequence;
    private readonly Dictionary<string, IPEndPoint> pingOnly = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>The relay behind a relay-group member's address (RelayGroupClient.RelayOf), or null. A member's pong
    /// counts for the relay we ping: in a group, the relay itself never answers, the people behind it do.</summary>
    public Func<IPEndPoint, IPEndPoint?>? RelayOfMember { get; set; }

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
    /// <summary>Addresses pinged every second like a peer, but not peers: no health is kept for them and they are never
    /// in <see cref="GetAllPeerHealth"/>, so nothing that reacts to a peer coming or going ever sees them.
    ///
    /// <para>For the relay (2026-09-24). A phone or older app waits at the relay in one of its two ordinary pair slots,
    /// and our ordinary heartbeat is what claims the slot beside it. Until 2026-09-20 the relay sat in our peer list, so
    /// it was always pinged; when it became a place of its own it was pinged only while somebody on it was ticked, and
    /// a PC alone on a server could never be paired with a waiting phone - the phone said "unreachable" for ever. The
    /// lock-screen service had kept the ping all along.</para></summary>
    /// <summary>Whether this service pings that address - a tracked peer, or a ping-only target such as the server we are
    /// on. Who we are talking to, for <see cref="AddrCheckEchoGate"/>.</summary>
    /// <summary>A ping from an address this copy is not pinging. Raised on the receive thread.</summary>
    public Action<IPEndPoint>? OnPingFromUntracked { get; set; }

    public bool Pings(IPAddress address)
    {
        lock (gate)
            return peers.Values.Any(p => p.AudioEndpoint.Address.Equals(address)) || pingOnly.Values.Any(p => p.Address.Equals(address));
    }

    public void SetPingOnlyTargets(IEnumerable<IPEndPoint> endpoints)
    {
        lock (gate)
        {
            pingOnly.Clear();
            foreach (var ep in endpoints) pingOnly[KeyFor(ep)] = ep;
        }
    }

    /// <summary>What <see cref="SetPingOnlyTargets"/> last set.</summary>
    public IReadOnlyList<IPEndPoint> PingOnlyTargets
    {
        get { lock (gate) return pingOnly.Values.ToList(); }
    }

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

    // Per person on a server: when each one's own pong last came, and its round trip (2026-09-25 sweep; Ed: per-person
    // cues). The server's own entry still counts them all together, which is what decides whether to send to it.
    private readonly Dictionary<IPAddress, (DateTime At, int RttMs)> memberPongs = new();
    private const int MaxMemberPongs = 1024;

    private void NoteMemberPongLocked(IPAddress member, DateTime nowUtc, int rttMs)
    {
        memberPongs[member] = (nowUtc, memberPongs.TryGetValue(member, out var was) ? (int)(was.RttMs * 0.7 + rttMs * 0.3) : rttMs);
        if (memberPongs.Count <= MaxMemberPongs) return;
        foreach (var old in memberPongs.Where(kv => nowUtc - kv.Value.At > UnreachableWindow).Select(kv => kv.Key).ToList()) memberPongs.Remove(old);
        if (memberPongs.Count > MaxMemberPongs) memberPongs.Clear();
    }

    /// <summary>The health of one person on a server, from their own pongs, by the same windows as everybody else's.
    /// Unreachable once the server has been pinged for a while and they have not answered. Safe from any thread.</summary>
    public PeerHealth MemberHealth(IPEndPoint member)
    {
        var relay = RelayOfMember?.Invoke(member);
        lock (gate)
        {
            var firstPing = relay is null ? null : peers.Values.FirstOrDefault(p => p.AudioEndpoint.Address.Equals(relay.Address))?.FirstPingSentUtc;
            var heard = memberPongs.TryGetValue(member.Address, out var pong);
            return SnapshotHealthLocked(new PeerState
            {
                AudioEndpoint = member,
                FirstPingSentUtc = firstPing,
                LastPongUtc = heard ? pong.At : null,
                RttEwmaMs = heard ? pong.RttMs : null,
            }, DateTime.UtcNow);
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
    }

    /// <summary>How one peer's health reads in that summary.
    ///
    /// <para>Lifted out of <see cref="GetHealthSummary"/> as a nested local function in 2026-08-24 so
    /// the gate can drive it. This string goes into the main status label, which a screen reader reads
    /// aloud, and into the diagnostics report — so a healthy peer formatted as "pending" tells Ed his
    /// link is broken when it is fine, and he has no way to see otherwise. Nothing tested it; the
    /// health STATES were pinned but the words the user actually hears were not.</para></summary>
    internal static string FormatPeer(PeerHealth p) => p.State switch
    {
        PeerHealthState.Healthy when p.RttMs is { } rtt => $"{p.AudioEndpoint.Address}: {rtt}ms",
        PeerHealthState.Stale when p.AgeOfLastPong is { } age => $"{p.AudioEndpoint.Address}: stale {age.TotalSeconds:0.0}s",
        PeerHealthState.Unreachable when p.AgeOfLastPong is { } age => $"{p.AudioEndpoint.Address}: unreachable {age.TotalSeconds:0.0}s",
        _ => $"{p.AudioEndpoint.Address}: pending",
    };

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
        List<IPEndPoint> extra;
        lock (gate)
        {
            targets = peers.Values.ToList();
            var nowUtc = DateTime.UtcNow;
            foreach (var p in targets) p.FirstPingSentUtc ??= nowUtc;
            // Pinged too, once, unless it is a peer already (the relay, once the person on it is ticked).
            extra = pingOnly.Where(kv => !peers.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
        }

        // Build packet directly into the reusable outboundPingBuffer instead of stack-
        // allocating + ToArray(). Same wire format, no per-call allocation. SendPings runs
        // exclusively on the timer task — single writer — so no lock needed around the
        // reuse. streamId is fixed at 0xFFFF for heartbeats so it's distinguishable in any
        // future stream-aware filter; sequence increments locally per send.
        var seq = Interlocked.Increment(ref sequence);
        var tickMs = monotonic.ElapsedMilliseconds;
        lock (gate)
        {
            recentPingTicks[recentPingNext] = tickMs;
            recentPingNext = (recentPingNext + 1) % recentPingTicks.Length;
        }
        var packetSpan = outboundPingBuffer.AsSpan();
        RemPacket.WriteHeader(packetSpan, RemPacketType.Heartbeat, 0xFFFF, seq);
        RemPacket.WriteHeartbeatPayload(packetSpan[RemPacket.HeaderSize..], HeartbeatKind.Ping, tickMs);

        foreach (var p in targets.Select(t => t.AudioEndpoint).Concat(extra).Select(ep => new PeerState { AudioEndpoint = ep }))
        {
            try
            {
                var ok = transport(outboundPingBuffer, outboundPingBuffer.Length, p.AudioEndpoint);
                // Failures only: a line per successful ping, once a second per peer, buried the log. 2026-09-13 review.
                if (!ok) onDiagnostic?.Invoke($"send seq={seq} to={p.AudioEndpoint} FAILED");
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
            // Somebody pinging whom this copy does not ping: said to the app, which may know them (a device ticked before,
            // back to listen). Answered below either way, as every ping is.
            if (OnPingFromUntracked is { } untracked && !Pings(remote.Address))
            {
                try { untracked(remote); } catch { /* the app's to handle; the pong still goes */ }
            }
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
        lock (gate)
        {
            if (Array.IndexOf(recentPingTicks, originatorTickMs) < 0) return;   // answers someone else's ping
        }
        var relayOfMember = RelayOfMember?.Invoke(remote);
        var matchAddress = relayOfMember?.Address ?? remote.Address;
        var nowMs = monotonic.ElapsedMilliseconds;
        var rttMs = (int)Math.Max(0, nowMs - originatorTickMs);
        var nowUtc = DateTime.UtcNow;
        var matchedCount = 0;
        lock (gate)
        {
            // And for that person on the server, their own: a member's pong reaches us only while each of us has ticked
            // the other, so it says whether THEY are there - where the server's entry says whether anybody is.
            if (relayOfMember is not null) NoteMemberPongLocked(remote.Address, nowUtc, rttMs);
            foreach (var p in peers.Values)
            {
                if (!p.AudioEndpoint.Address.Equals(matchAddress)) continue;
                p.RttEwmaMs = p.RttEwmaMs is null ? rttMs : (int)(p.RttEwmaMs.Value * 0.7 + rttMs * 0.3);
                p.LastPongUtc = nowUtc;
                matchedCount++;
            }
        }
        // A pong from an address we don't track (possible loopback / echo) is worth a line. A normal one, once a second per
        // peer, is not: those lines buried the log. 2026-09-13 review.
        if (matchedCount == 0)
            onDiagnostic?.Invoke($"recv pong from={remote} matched no tracked peer (rtt={rttMs}ms origTickMs={originatorTickMs} nowMs={nowMs})");
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
