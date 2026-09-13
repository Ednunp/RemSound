using System.Net;

namespace RemSound.Core;

/// <summary>
/// Which peers a VST plugin instance has taken over, so their audio does NOT also come out of the
/// app's speakers.
///
/// <para><b>The problem</b> (Ed, 2026-08-15): "how are we going to deal with the sound of a peer
/// coming through remsound and also through the vst? having both playing would be bad." The app owns
/// the one connection; a plugin instance in the DAW says "I'll take Andre onto this track". Without
/// this, Andre plays twice — once from the app's output device and once from the DAW — slightly out
/// of step with each other, which sounds like a fault and is maddening to diagnose.</para>
///
/// <para><b>The rule:</b> a claimed peer is routed to the plugin INSTEAD of the device mix, never as
/// well. Claiming is automatic — a plugin instance set to receive a peer claims it, and drops the
/// claim when it is removed, bypassed or the DAW closes. There is no user decision to get wrong,
/// which is the point: "or else it's going to be damn confusing".</para>
///
/// <para><b>Claims are per-peer, not per-instance-count.</b> Two instances receiving the same peer
/// (a main track and a monitor send, say) both get the audio and the peer stays muted in the app
/// until BOTH let go — reference-counted, so removing one plugin doesn't suddenly bring the peer
/// back through the speakers underneath the other.</para>
///
/// <para><b>Fail-safe direction matters.</b> If a plugin dies without releasing — the DAW crashed,
/// the process was killed — the claim must expire, or the user is left with a peer that is silent
/// everywhere and no obvious way to get them back. Claims therefore carry a heartbeat and lapse
/// after <see cref="ClaimTimeout"/>; silence-forever is the worse failure, so we err towards the
/// audio coming back.</para>
/// </summary>
public sealed class PluginPeerClaims
{
    /// <summary>How long a claim survives without a heartbeat. Comfortably longer than the plugin's
    /// refresh interval, short enough that a crashed DAW doesn't leave a peer mute for long.</summary>
    public static readonly TimeSpan ClaimTimeout = TimeSpan.FromSeconds(5);

    private readonly object gate = new();
    // address -> (claim id -> last heartbeat). Reference-counted by instance so two plugins on the
    // same peer both have to let go before it returns to the speakers.
    private readonly Dictionary<IPAddress, Dictionary<Guid, DateTime>> claims = new();

    /// <summary>A plugin instance takes (or renews) a peer. Idempotent: calling it every tick is the
    /// intended use — that IS the heartbeat.</summary>
    public void Claim(IPAddress peer, Guid instanceId, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        lock (gate)
        {
            if (!claims.TryGetValue(peer, out var byInstance))
            {
                byInstance = new Dictionary<Guid, DateTime>();
                claims[peer] = byInstance;
            }
            byInstance[instanceId] = now;
        }
    }

    /// <summary>A plugin instance lets a peer go — removed from the track, bypassed, or switched to
    /// sending. The peer returns to the app's speakers once nobody else holds it.</summary>
    public void Release(IPAddress peer, Guid instanceId)
    {
        lock (gate)
        {
            if (!claims.TryGetValue(peer, out var byInstance)) return;
            byInstance.Remove(instanceId);
            if (byInstance.Count == 0) claims.Remove(peer);
        }
    }

    /// <summary>Drop every claim held by one instance, wherever it pointed. For a plugin being
    /// disposed: it must not have to remember which peer it had.</summary>
    public void ReleaseAll(Guid instanceId)
    {
        lock (gate)
        {
            foreach (var addr in claims.Keys.ToList())
            {
                claims[addr].Remove(instanceId);
                if (claims[addr].Count == 0) claims.Remove(addr);
            }
        }
    }

    /// <summary>Is this peer currently taken by a plugin — i.e. must the app NOT play it to its own
    /// outputs? Expired claims are swept here, so a dead DAW can never mute a peer permanently.</summary>
    public bool IsClaimed(IPAddress peer, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        lock (gate)
        {
            if (!claims.TryGetValue(peer, out var byInstance)) return false;
            foreach (var (id, seen) in byInstance.ToList())
            {
                if (now - seen > ClaimTimeout) byInstance.Remove(id);
            }
            if (byInstance.Count == 0) { claims.Remove(peer); return false; }
            return true;
        }
    }

    /// <summary>How many instances hold this peer. Read by the gate, to prove that two tracks on one
    /// person are both counted and that one letting go leaves the other's claim standing. The log line
    /// reports the claimed set instead (<see cref="ClaimedPeers"/>, via
    /// <see cref="PluginBridgeHost.DescribeForLog"/>).</summary>
    public int ClaimCount(IPAddress peer)
    {
        lock (gate)
        {
            return claims.TryGetValue(peer, out var byInstance) ? byInstance.Count : 0;
        }
    }

    /// <summary>Every peer currently claimed. Snapshot; safe to enumerate.</summary>
    public IReadOnlyList<IPAddress> ClaimedPeers()
    {
        lock (gate)
        {
            return claims.Keys.ToList();
        }
    }
}
