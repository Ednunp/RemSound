using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RemSound.Core;

/// <summary>
/// UDP peer discovery. Each running instance announces itself on
/// <see cref="DefaultDiscoveryPort"/> every 1.5 s. Peers expire after 8 s of silence.
///
/// Announcements go out two ways:
///   • Broadcast on every connected LAN subnet (for same-network discovery — instant on
///     home/office wifi).
///   • Unicast to a configurable list of "known" IPs (for VPN/Tailscale/WAN discovery —
///     broadcast doesn't traverse VPN tunnels, so we explicitly send announcements to
///     remembered/manual peer addresses). The App keeps this list in sync via
///     <see cref="SetUnicastPeerAddresses"/>.
/// </summary>
public sealed class PeerDiscoveryService : IDisposable
{
    public const int DefaultDiscoveryPort = 47821;

    private readonly Guid instanceId = Guid.NewGuid();
    private readonly object gate = new();
    private readonly Dictionary<Guid, PeerAnnouncement> peers = [];
    // All source IPs each peer (by InstanceId) has recently announced from, with last-seen for expiry.
    // A multi-homed peer (LAN + VPN at once) announces from several interfaces; we keep them all so the
    // receiver can accept audio from whichever one a given packet egresses from (#18), not only the
    // last-seen address. Guarded by gate; pruned on the same 8 s window as `peers`.
    private readonly Dictionary<Guid, Dictionary<IPAddress, DateTime>> addressesById = [];
    private CancellationTokenSource? cts;
    private UdpClient? listener;
    private UdpClient? announcer;
    private Task? listenTask;
    private Task? announceTask;
    private int audioPort = RemPacket.DefaultPort;
    private bool canSend;
    private bool canReceive;
    private bool announceEnabled = true;
    // Snapshot of "send announcements directly to these IPs each tick" — typically the user's
    // remembered + manually-typed peer IPs. Replaced atomically; the announce loop reads the
    // reference once per tick. Volatile-write semantics via the assignment under the gate are
    // sufficient because we only ever swap the reference, never mutate in place.
    private IReadOnlyList<IPAddress> unicastTargets = [];
    // Cached broadcast addresses. Item 16 of RemSoundefficiency.md — pre-2026-05-23 we
    // recomputed these every 1.5 s by walking every network interface (NetworkInterface
    // .GetAllNetworkInterfaces is a real Win32 P/Invoke), allocating a HashSet, and iterating
    // unicast addresses. Network interfaces don't change on a 1.5 s cadence; cache the
    // result and invalidate only when Windows raises the NetworkAddressChanged event.
    // Reference-swap on update so the announce loop can read it without locking.
    private volatile IPAddress[] cachedBroadcastAddresses = [];
    private int broadcastCacheDirty = 1;  // 1 = needs rebuild, 0 = current. Int for Interlocked.
    private NetworkAddressChangedEventHandler? networkChangeHandler;

    public event Action? PeersChanged;

    public IReadOnlyList<PeerAnnouncement> Peers
    {
        get
        {
            lock (gate)
            {
                PruneExpiredPeers();
                return peers.Values.OrderBy(p => p.Name).ThenBy(p => p.Address.ToString()).ToList();
            }
        }
    }

    public void Start(int selectedAudioPort, bool sendEnabled, bool receiveEnabled)
    {
        Stop();
        audioPort = selectedAudioPort;
        canSend = sendEnabled;
        canReceive = receiveEnabled;
        announceEnabled = true;
        cts = new CancellationTokenSource();

        listener = new UdpClient(AddressFamily.InterNetwork);
        listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.EnableBroadcast = true;
        listener.Client.Bind(new IPEndPoint(IPAddress.Any, DefaultDiscoveryPort));

        announcer = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };

        // Subscribe to Windows network-change notifications so we know to rebuild the
        // broadcast-address cache. Without this we'd either have to re-walk all interfaces
        // every 1.5 s (the pre-2026-05-23 behaviour) or risk announcing on stale broadcast
        // addresses after a network change. The handler just flips the dirty flag — the
        // actual rebuild happens lazily the next time AnnounceLoop reads the cache.
        networkChangeHandler = (_, _) => Interlocked.Exchange(ref broadcastCacheDirty, 1);
        try { NetworkChange.NetworkAddressChanged += networkChangeHandler; }
        catch { /* harmless — caching just falls back to per-tick rebuild on first miss */ }

        listenTask = Task.Run(() => ListenLoop(cts.Token));
        announceTask = Task.Run(() => AnnounceLoop(cts.Token));
    }

    public void UpdateCapabilities(int selectedAudioPort, bool sendEnabled, bool receiveEnabled)
    {
        audioPort = selectedAudioPort;
        canSend = sendEnabled;
        canReceive = receiveEnabled;
        SendAnnouncement();
    }

    public void SetAnnounceEnabled(bool enabled)
    {
        announceEnabled = enabled;
        if (enabled) SendAnnouncement();
    }

    /// <summary>
    /// Updates the list of IP addresses that announcements should be unicast to in addition to
    /// LAN broadcast. The App calls this whenever its remembered+manual-peers set changes; the
    /// loop reads the latest snapshot on each tick.
    ///
    /// Why unicast at all: broadcast doesn't traverse VPNs (Tailscale, WireGuard, ZeroTier).
    /// To be discoverable over a VPN we have to explicitly announce to each known IP. Sending
    /// to a remembered peer that happens to be offline is harmless — UDP is fire-and-forget.
    /// </summary>
    public void SetUnicastPeerAddresses(IEnumerable<IPAddress> addresses)
    {
        unicastTargets = addresses.Distinct().ToList();
        SendAnnouncement();
    }

    public void Stop()
    {
        if (networkChangeHandler is not null)
        {
            try { NetworkChange.NetworkAddressChanged -= networkChangeHandler; }
            catch { /* ignore — best-effort unsubscribe */ }
            networkChangeHandler = null;
        }
        cts?.Cancel();
        listener?.Dispose();
        announcer?.Dispose();
        listener = null;
        announcer = null;
        cts?.Dispose();
        cts = null;
    }

    public void Dispose() => Stop();

    private async Task ListenLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await listener!.ReceiveAsync(token).ConfigureAwait(false);
                var json = Encoding.UTF8.GetString(result.Buffer);
                var message = JsonSerializer.Deserialize<DiscoveryMessage>(json);
                if (message is null || message.InstanceId == instanceId) continue;

                var peer = new PeerAnnouncement(
                    message.InstanceId,
                    string.IsNullOrWhiteSpace(message.Name) ? result.RemoteEndPoint.Address.ToString() : message.Name.Trim(),
                    message.AudioPort,
                    message.CanSend,
                    message.CanReceive,
                    DateTime.UtcNow,
                    result.RemoteEndPoint.Address);

                // Auto-add the source IP to our unicast targets so subsequent announcements go
                // back the way they came. This is what makes discovery bidirectional over a
                // VPN: A unicasts to B (because A had B remembered/manually-added) → B receives
                // it → B adds A to its own unicast list → B's announcements now reach A too,
                // even though A was never in B's remembered list. Without this, only the side
                // that had typed the other's IP would see the other.
                AddUnicastTarget(result.RemoteEndPoint.Address);

                bool changed;
                lock (gate)
                {
                    changed = !peers.TryGetValue(peer.InstanceId, out var existing)
                        || existing.Name != peer.Name
                        || existing.AudioPort != peer.AudioPort
                        || existing.CanSend != peer.CanSend
                        || existing.CanReceive != peer.CanReceive
                        || !Equals(existing.Address, peer.Address);
                    peers[peer.InstanceId] = peer;
                    // Remember this source IP for the peer (multi-homed senders announce from several).
                    if (!addressesById.TryGetValue(peer.InstanceId, out var addrs))
                    {
                        addrs = [];
                        addressesById[peer.InstanceId] = addrs;
                    }
                    addrs[peer.Address] = peer.LastSeenUtc;
                    PruneExpiredPeers();
                }
                if (changed) PeersChanged?.Invoke();
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch
            {
                try { await Task.Delay(500, token).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    private void AddUnicastTarget(IPAddress address)
    {
        // Idempotent — only swap the snapshot if this IP isn't already there. Avoids churning
        // the list on every received announcement (which is every 1.5 s per peer).
        var current = unicastTargets;
        if (current.Any(a => a.Equals(address))) return;
        var updated = current.ToList();
        updated.Add(address);
        unicastTargets = updated;
    }

    private async Task AnnounceLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            SendAnnouncement();
            try { await Task.Delay(1500, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void SendAnnouncement()
    {
        var currentAnnouncer = announcer;
        if (currentAnnouncer is null || !announceEnabled) return;

        var message = new DiscoveryMessage(instanceId, Environment.MachineName, audioPort, canSend, canReceive);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));

        // Broadcast to LAN — instant discovery on the same physical/wifi network. Each connected
        // NIC gets its own subnet broadcast (e.g. 192.168.1.255).
        foreach (var broadcastAddress in GetBroadcastAddresses())
        {
            try
            {
                currentAnnouncer.Send(bytes, bytes.Length, new IPEndPoint(broadcastAddress, DefaultDiscoveryPort));
            }
            catch
            {
                // Discovery is convenience. Audio still works without it.
            }
        }

        // Unicast to known peer IPs — covers Tailscale / VPN / WAN where broadcast doesn't
        // traverse the tunnel. Sending to an offline peer is silent fire-and-forget.
        foreach (var unicast in unicastTargets)
        {
            try
            {
                currentAnnouncer.Send(bytes, bytes.Length, new IPEndPoint(unicast, DefaultDiscoveryPort));
            }
            catch
            {
                // Same — discovery is best-effort.
            }
        }
    }

    /// <summary>Returns the cached broadcast-address array, rebuilding it only if the
    /// dirty flag has been set (initial state, or by the NetworkAddressChanged event).
    /// The original implementation walked every NIC on every announcement (~40 per minute);
    /// caching turns that into a single walk per network change. Item 16 of
    /// RemSoundefficiency.md. 2026-05-23.</summary>
    private IPAddress[] GetBroadcastAddresses()
    {
        // Fast path: cache is current.
        if (Volatile.Read(ref broadcastCacheDirty) == 0)
        {
            return cachedBroadcastAddresses;
        }
        // Slow path: rebuild. Atomic CAS clears the dirty flag before the rebuild so a
        // concurrent NetworkAddressChanged event sets it again rather than racing.
        Interlocked.Exchange(ref broadcastCacheDirty, 0);
        var addresses = new HashSet<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var unicast in ni.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null) continue;
                    var addr = unicast.Address.GetAddressBytes();
                    var mask = unicast.IPv4Mask.GetAddressBytes();
                    var bcast = new byte[4];
                    for (var i = 0; i < 4; i++) bcast[i] = (byte)(addr[i] | ~mask[i]);
                    addresses.Add(new IPAddress(bcast));
                }
            }
        }
        catch
        {
            // GetAllNetworkInterfaces can throw transiently on some configurations; the
            // limited-broadcast 255.255.255.255 still reaches LAN peers on most setups, so
            // fall back to just that rather than aborting discovery.
        }
        var snapshot = new IPAddress[addresses.Count];
        addresses.CopyTo(snapshot);
        cachedBroadcastAddresses = snapshot;
        return snapshot;
    }

    private void PruneExpiredPeers()
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-8);
        foreach (var peer in peers.Values.Where(p => p.LastSeenUtc < cutoff).ToList())
        {
            peers.Remove(peer.InstanceId);
        }
        // Expire per-interface source addresses on the same window, and drop any peer left with none.
        foreach (var (id, addrs) in addressesById.ToList())
        {
            foreach (var addr in addrs.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
            {
                addrs.Remove(addr);
            }
            if (addrs.Count == 0) addressesById.Remove(id);
        }
    }

    /// <summary>All source IPs a peer (by InstanceId) has announced from within the expiry window. A
    /// multi-homed peer announces from several; the receiver allow-lists all of them so audio egressing
    /// from any of the peer's interfaces is accepted rather than silently dropped (#18).</summary>
    public IReadOnlyCollection<IPAddress> GetKnownAddresses(Guid instanceId)
    {
        lock (gate)
        {
            PruneExpiredPeers();
            return addressesById.TryGetValue(instanceId, out var addrs) ? addrs.Keys.ToArray() : [];
        }
    }

    private sealed record DiscoveryMessage(Guid InstanceId, string Name, int AudioPort, bool CanSend, bool CanReceive);
}
