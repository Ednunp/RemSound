using System.Net;
using RemSound.Core;
using RemSound.Receiver;
using RemSound.Sender;

namespace RemSound.App;

/// <summary>
/// Gives the send-only Windows service a REAL presence on the network, so a peer (e.g. a phone) can
/// discover it and connect to it — the thing the bare sender could never do. It reuses the exact same
/// components the interactive app uses, wired the same way, so its discovery, heartbeat, NAT-pinhole and
/// relay behaviour are identical to the app's (that's what makes it "one identity": both announce under
/// the machine name and pair through the relay the same way — see <see cref="PeerDiscoveryService"/>).
///
/// <list type="bullet">
/// <item><b>Discoverable</b> — <see cref="PeerDiscoveryService"/> broadcasts (and unicasts to the profile's
/// peers, for across-the-internet) an announcement under the machine name, advertising send-only.</item>
/// <item><b>Reachable</b> — <see cref="AudioReceiver"/>'s listener is bound to the well-known audio port so
/// peers can reach it; PLAYBACK stays OFF (send-only never plays received audio — the listener only carries
/// heartbeat/pairing).</item>
/// <item><b>Pairable</b> — <see cref="HeartbeatService"/> pings the peers, which proves reachability, opens
/// the NAT pinhole and drives relay pairing; replies return either on the listener (LAN) or the sender's
/// socket (relay), both routed into the heartbeat service.</item>
/// </list>
///
/// <para>Crucially it can be fully torn down (<see cref="Stop"/>) to a SHELL: when the interactive app is
/// present the service must vacate the network entirely — stop announcing, unbind the port, stop the
/// heartbeat — so the two never both broadcast or fight over the port. A brief dropout on that handover is
/// acceptable (Ed's call): only one of the two holds the network at a time.</para>
/// </summary>
internal sealed class ServiceNetworkPresence : IDisposable
{
    private readonly AudioSender sender;
    private readonly Action<string>? log;
    private readonly PeerDiscoveryService discovery = new();
    private readonly AudioReceiver receiver = new();
    private HeartbeatService? heartbeat;
    private bool running;
    private bool disposed;

    public ServiceNetworkPresence(AudioSender sender, Action<string>? log = null)
    {
        this.sender = sender;
        this.log = log;
    }

    public bool IsUp => running;

    /// <summary>Test seam: is the well-known-port listener actually bound?</summary>
    internal bool ListenerBound => receiver.IsListenerRunning;

    /// <summary>Current per-peer heartbeat health (reachable / stale / unreachable + age), so the host can
    /// gate the audio send on reachability — the same signal the interactive app uses. Empty when down.</summary>
    public IReadOnlyList<PeerHealth> PeerHealthSnapshot() => heartbeat?.GetAllPeerHealth() ?? Array.Empty<PeerHealth>();

    /// <summary>Bring the service up as a discoverable, reachable, send-only peer on <paramref name="port"/>,
    /// tracking <paramref name="endpoints"/> (the profile's peers) for heartbeat/pairing and unicast
    /// announcements. Idempotent: re-applies cleanly if already up. Never throws.</summary>
    public void Start(int port, IReadOnlyList<IPEndPoint> endpoints)
    {
        if (disposed) return;
        if (running) Stop();

        // Heartbeat pings go out through the sender's socket (sharing the audio NAT pinhole); replies come
        // back on the listener (LAN) or the sender's socket (relay). Send-only: we route ONLY heartbeat
        // packets — audio/format returns are ignored because we never play received audio.
        heartbeat = new HeartbeatService(m => log?.Invoke($"heartbeat: {m}"));
        heartbeat.SendTransport = sender.SendVia;

        receiver.OnHeartbeatReceived = (buf, len, remote) => heartbeat?.HandleInjectedPacket(buf, len, remote);
        sender.OnInboundPacket = (buf, len, remote) =>
        {
            if (len < RemPacket.HeaderSize) return;
            if (RemPacket.TryReadHeader(buf.AsSpan(0, len), out var type, out _, out _) && type == RemPacketType.Heartbeat)
                heartbeat?.HandleInjectedPacket(buf, len, remote);
            // Send-only: Format / Audio / KeepAlive returns are deliberately dropped — the service never
            // plays received audio, so there is no receive pipeline to feed.
        };

        // Make the sender's socket always-receiving so relay replies have somewhere to land.
        try { sender.StartReceiving(); } catch (Exception ex) { log?.Invoke($"service: sender receive-side failed {ex.GetType().Name}: {ex.Message}"); }

        // Bind the listener (heartbeat/pairing only — playback stays OFF for send-only).
        try
        {
            receiver.Start(port);
            receiver.SetPlaybackEnabled(false);
        }
        catch (Exception ex) { log?.Invoke($"service: listener bind failed {ex.GetType().Name}: {ex.Message}"); }

        heartbeat.SetTrackedPeers(endpoints);
        heartbeat.Start();

        // Announce ourselves: LAN broadcast plus a unicast to each configured peer, so a peer across the
        // internet (reached via the relay/Tailscale/port-forward) also learns we're here. Send-only.
        try
        {
            discovery.SetUnicastPeerAddresses(endpoints.Select(e => e.Address));
            discovery.Start(port, sendEnabled: true, receiveEnabled: false);
        }
        catch (Exception ex) { log?.Invoke($"service: discovery failed {ex.GetType().Name}: {ex.Message}"); }

        running = true;
        log?.Invoke($"service: network presence up on :{port} — discoverable + reachable to {endpoints.Count} peer(s), send-only");
    }

    /// <summary>Tear the presence all the way down to a SHELL: stop announcing, stop the heartbeat, unbind
    /// the listener, unwire the sender's receive-side. Leaves NO network footprint on the well-known port —
    /// this is what lets the interactive app take the network over cleanly. Never throws.</summary>
    public void Stop()
    {
        if (!running) return;
        running = false;
        try { discovery.Stop(); } catch { }
        try { heartbeat?.Stop(); heartbeat?.Dispose(); } catch { }
        heartbeat = null;
        try { receiver.OnHeartbeatReceived = null; } catch { }
        try { receiver.Stop(); } catch { }
        try { sender.OnInboundPacket = null; } catch { }
        log?.Invoke("service: network presence down (shell — the network is free for the interactive app)");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Stop();
        try { discovery.Dispose(); } catch { }
        try { receiver.Dispose(); } catch { }
    }
}
