using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace RemSound.Core;

/// <summary>Read one claimed peer's audio. Implemented by the receiver; a delegate rather than an
/// interface reference so Core does not have to know what a receiver is.</summary>
public delegate int PeerAudioReader(IPAddress peer, Span<float> destination, int frames);

/// <summary>
/// The app's end of the link to VST plugin instances.
///
/// <para><b>Why the app is the host.</b> Ed chose the architecture where RemSound keeps the one
/// connection and hands audio to and from plugins, over each plugin opening its own. The deciding
/// reason was the double-audio problem: if a plugin had its own connection, nothing in the app would
/// know to stop that peer also coming out of the speakers, and the user would hear the same person
/// twice, slightly apart. Here it solves itself — a claim arrives, the peer leaves the mix.</para>
///
/// <para><b>The DAW is the clock.</b> A plugin sends <see cref="PluginBridgeMessage.ClaimPeer"/> every
/// audio block carrying the number of frames it just consumed; we read exactly that many from that
/// peer and send them back. One message therefore does three jobs — it claims the peer, it is the
/// heartbeat that keeps the claim alive, and it is the request for the next block. Pumping on a timer
/// of our own instead would give the peer two clocks, and the ring between us would drift.</para>
///
/// <para><b>Nothing here blocks a DAW.</b> Requests are served on the bridge's receive thread and the
/// answer is fired back as a datagram; the plugin's audio thread reads from its own ring and never
/// waits on us. If we are slow, the plugin gets a short block — the same failure the network path
/// already handles — rather than a stall in someone's session.</para>
///
/// <para><b>Instances are forgotten if they go quiet.</b> A DAW that crashes cannot send Goodbye, and
/// a peer left claimed forever would be silent everywhere with no obvious way back. Claims lapse on
/// their own (see <see cref="PluginPeerClaims"/>); this sweeps the instance list to match.</para>
/// </summary>
public sealed class PluginBridgeHost : IDisposable
{
    /// <summary>Largest block we will serve in one message, in frames. Well past any sane DAW buffer;
    /// a request bigger than this is answered short rather than trusted.</summary>
    public const int MaxFramesPerRequest = 4096;

    private sealed class Instance
    {
        public required Guid Id { get; init; }
        public required IPEndPoint Endpoint { get; set; }
        public IPAddress? ClaimedPeer { get; set; }
        public DateTime LastSeenUtc { get; set; }
    }

    private readonly PluginBridgeLink link;
    private readonly PluginPeerClaims claims;
    private readonly PeerAudioReader? readPeer;
    private readonly object gate = new();
    private readonly Dictionary<int, Instance> instances = new();

    // Reused across requests — allocating a buffer per audio block would be exactly the mistake the
    // plugin side is careful to avoid, and this thread feeds it.
    private readonly float[] readScratch = new float[MaxFramesPerRequest * 2];
    private readonly byte[] sendScratch = new byte[MaxFramesPerRequest * 2 * sizeof(float)];

    /// <summary>The port plugins should talk to. Normally <see cref="PluginBridgeProtocol.DefaultPort"/>;
    /// the gate uses an OS-assigned one so a running RemSound is never disturbed by a test.</summary>
    public int Port => link.Port;

    /// <summary>The claim register, to hand to the receiver so claimed peers leave the mix.</summary>
    public PluginPeerClaims Claims => claims;

    /// <summary>Audio a plugin instance wants sent to the peers — one DAW track going out. Raised on
    /// the bridge thread with interleaved stereo at <see cref="PluginBridgeProtocol.WireSampleRate"/>.</summary>
    public event Action<Guid, ReadOnlyMemory<float>>? TrackAudioReceived;

    /// <summary>Where the peer list comes from when a plugin asks who it can receive. Set by the app;
    /// null means "no peers yet", which the plugin shows plainly rather than looking broken.</summary>
    public Func<IReadOnlyList<(IPAddress Address, string Name)>>? PeerListSource { get; set; }

    /// <summary>Raised when something worth writing down happens on the link. Fired on the bridge
    /// thread; the app turns these into log lines. Deliberately plain English — whoever reads this file
    /// is trying to work out why a track was silent, not trying to read our code.</summary>
    public event Action<string>? Notable;

    private long blocksServed, bytesServed, trackBlocks, trackBytes, unknownPeerRequests, shortReads;

    /// <summary>Counters for the app's once-a-second plugin line.</summary>
    public long BlocksServed => Interlocked.Read(ref blocksServed);
    public long BytesServed => Interlocked.Read(ref bytesServed);
    public long TrackBlocksReceived => Interlocked.Read(ref trackBlocks);
    public long TrackBytesReceived => Interlocked.Read(ref trackBytes);

    /// <summary>Blocks a plugin asked for that we could not fill. A few while a peer's buffer builds
    /// are normal; a number that climbs is the app failing to keep up, and it is the difference between
    /// diagnosing a crackle and guessing at it.</summary>
    public long ShortReads => Interlocked.Read(ref shortReads);

    /// <summary>Requests for a peer we have no audio for — a plugin pointed at somebody who has gone.
    /// Silent on the track, and invisible without this.</summary>
    public long UnknownPeerRequests => Interlocked.Read(ref unknownPeerRequests);

    /// <summary>Malformed datagrams on our port. Rising means something else is talking to it.</summary>
    public long MalformedReceived => link.MalformedReceived;

    /// <summary>One line describing the whole link right now, for the app's per-second log.</summary>
    public string DescribeForLog()
    {
        lock (gate)
        {
            var claimed = claims.ClaimedPeers();
            return $"instances={instances.Count} claimed={claimed.Count}"
                 + (claimed.Count == 0 ? "" : $" [{string.Join(", ", claimed)}]")
                 + $" blocksOut={BlocksServed} bytesOut={BytesServed}"
                 + $" blocksIn={TrackBlocksReceived} bytesIn={TrackBytesReceived}"
                 + $" short={ShortReads} unknownPeer={UnknownPeerRequests} malformed={MalformedReceived}";
        }
    }

    /// <summary>Is anything actually going on? The app uses this to keep a quiet log quiet — a line a
    /// second saying "nothing" would bury the session that matters.</summary>
    public bool HasActivity => InstanceCount > 0 || BlocksServed > 0 || TrackBlocksReceived > 0;

    /// <summary>How many plugin instances are talking to us right now — for the log, so "why is Andre
    /// silent in the app" is answerable from a file rather than by guesswork.</summary>
    public int InstanceCount { get { lock (gate) { return instances.Count; } } }

    public PluginBridgeHost(PeerAudioReader? readPeer, int port = PluginBridgeProtocol.DefaultPort, PluginPeerClaims? claims = null)
    {
        this.readPeer = readPeer;
        this.claims = claims ?? new PluginPeerClaims();
        link = new PluginBridgeLink(port);
        link.MessageReceived += OnMessage;
    }

    private void OnMessage(PluginBridgeMessage type, int hash, IPAddress? peer, ReadOnlyMemory<byte> payload, IPEndPoint from)
    {
        switch (type)
        {
            case PluginBridgeMessage.Hello:
                var isNew = !KnownInstance(hash);
                var instance = Touch(hash, from);
                SendPeerList(from, hash);
                if (isNew) Notable?.Invoke($"a plugin said hello (instance {Short(instance.Id)}, from port {from.Port})");
                else Notable?.Invoke($"plugin {Short(instance.Id)} said hello again - it will have reconnected after RemSound restarted");
                break;

            case PluginBridgeMessage.ClaimPeer:
                if (peer is null) return;
                ServeClaim(hash, from, peer, payload);
                break;

            case PluginBridgeMessage.ReleasePeer:
                ReleaseOne(hash, peer);
                break;

            case PluginBridgeMessage.TrackAudio:
                Touch(hash, from);
                DispatchTrackAudio(hash, payload);
                break;

            case PluginBridgeMessage.Goodbye:
                Forget(hash);
                break;

            default:
                break;
        }
    }

    /// <summary>A plugin instance is receiving this peer and wants its next block. Claim, heartbeat
    /// and request in one message.</summary>
    private void ServeClaim(int hash, IPEndPoint from, IPAddress peer, ReadOnlyMemory<byte> payload)
    {
        var instance = Touch(hash, from);

        // Switching a plugin from one peer to another must let the old one go, or it stays mute in the
        // app until the claim happens to time out — which reads as a fault, not a setting.
        if (instance.ClaimedPeer is not null && !instance.ClaimedPeer.Equals(peer))
        {
            claims.Release(instance.ClaimedPeer, instance.Id);
            Notable?.Invoke($"plugin {Short(instance.Id)} moved from {instance.ClaimedPeer} to {peer} - {instance.ClaimedPeer} is back on the speakers");
        }
        else if (instance.ClaimedPeer is null)
        {
            Notable?.Invoke($"plugin {Short(instance.Id)} took {peer} - that peer now plays on the DAW track instead of the speakers");
        }
        instance.ClaimedPeer = peer;
        claims.Claim(peer, instance.Id);

        if (readPeer is null || payload.Length < sizeof(int)) return;
        var frames = BinaryPrimitives.ReadInt32LittleEndian(payload.Span);
        if (frames <= 0) return;
        frames = Math.Min(frames, MaxFramesPerRequest);

        int produced;
        lock (readScratch)
        {
            produced = readPeer(peer, readScratch.AsSpan(0, frames * 2), frames);
            if (produced <= 0)
            {
                // Nothing for that peer at all: either their buffer is still filling, or the plugin is
                // pointed at somebody who has gone. Counted, not logged per block - at one block per
                // audio callback a line each would be thousands a second.
                Interlocked.Increment(ref unknownPeerRequests);
                return;
            }
            if (produced < frames) Interlocked.Increment(ref shortReads);
            var bytes = produced * 2 * sizeof(float);
            Buffer.BlockCopy(readScratch, 0, sendScratch, 0, bytes);
            if (link.Send(from, PluginBridgeMessage.PeerAudio, hash, peer, sendScratch.AsSpan(0, bytes)))
            {
                Interlocked.Increment(ref blocksServed);
                Interlocked.Add(ref bytesServed, bytes);
            }
        }
    }

    private void DispatchTrackAudio(int hash, ReadOnlyMemory<byte> payload)
    {
        var handler = TrackAudioReceived;
        if (handler is null || payload.Length < sizeof(float)) return;
        Guid id;
        lock (gate)
        {
            if (!instances.TryGetValue(hash, out var instance)) return;
            id = instance.Id;
        }
        var floats = new float[payload.Length / sizeof(float)];
        Buffer.BlockCopy(payload.ToArray(), 0, floats, 0, floats.Length * sizeof(float));
        Interlocked.Increment(ref trackBlocks);
        Interlocked.Add(ref trackBytes, payload.Length);
        handler(id, floats);
    }

    private void SendPeerList(IPEndPoint to, int hash)
    {
        var peers = PeerListSource?.Invoke() ?? [];
        var text = string.Join("\n", peers.Select(p => $"{p.Address}\t{p.Name}"));
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > PluginBridgeProtocol.MaxAudioBytes) bytes = bytes[..PluginBridgeProtocol.MaxAudioBytes];
        link.Send(to, PluginBridgeMessage.PeerList, hash, null, bytes);
    }

    private bool KnownInstance(int hash) { lock (gate) { return instances.ContainsKey(hash); } }

    private static string Short(Guid id) => id.ToString("N")[..8];

    private Instance Touch(int hash, IPEndPoint from)
    {
        lock (gate)
        {
            if (!instances.TryGetValue(hash, out var instance))
            {
                // The wire carries a 32-bit id to keep the header small; the claim register is keyed
                // by Guid. Minting one here on first sight keeps both honest without a bigger header.
                instance = new Instance { Id = Guid.NewGuid(), Endpoint = from };
                instances[hash] = instance;
            }
            instance.Endpoint = from;
            instance.LastSeenUtc = DateTime.UtcNow;
            SweepLocked();
            return instance;
        }
    }

    private void ReleaseOne(int hash, IPAddress? peer)
    {
        lock (gate)
        {
            if (!instances.TryGetValue(hash, out var instance)) return;
            var released = peer ?? instance.ClaimedPeer;
            if (released is not null) claims.Release(released, instance.Id);
            instance.ClaimedPeer = null;
            if (released is not null)
                Notable?.Invoke($"plugin {Short(instance.Id)} let {released} go - back on the speakers");
        }
    }

    /// <summary>A plugin unloading, or a DAW closing cleanly. Everything it held goes back to the
    /// speakers at once rather than after a timeout.</summary>
    private void Forget(int hash)
    {
        lock (gate)
        {
            if (!instances.Remove(hash, out var instance)) return;
            claims.ReleaseAll(instance.Id);
            Notable?.Invoke($"plugin {Short(instance.Id)} closed cleanly"
                + (instance.ClaimedPeer is null ? "" : $" - {instance.ClaimedPeer} is back on the speakers"));
        }
    }

    private void SweepLocked()
    {
        var cutoff = DateTime.UtcNow - PluginPeerClaims.ClaimTimeout;
        foreach (var (hash, instance) in instances.ToList())
        {
            if (instance.LastSeenUtc > cutoff) continue;
            instances.Remove(hash);
            claims.ReleaseAll(instance.Id);
            // No goodbye: the DAW was killed, or crashed. Worth a line - it is the difference between
            // "the plugin misbehaved" and "the host died", which look identical from the outside.
            Notable?.Invoke($"plugin {Short(instance.Id)} went quiet and timed out (no goodbye - the DAW probably closed abruptly)"
                + (instance.ClaimedPeer is null ? "" : $" - {instance.ClaimedPeer} is back on the speakers"));
        }
    }

    /// <summary>Test seam: run the expiry sweep without waiting for a message to arrive.</summary>
    internal void SweepForTest()
    {
        lock (gate) { SweepLocked(); }
    }

    public void Dispose()
    {
        link.MessageReceived -= OnMessage;
        link.Dispose();
        lock (gate)
        {
            foreach (var instance in instances.Values) claims.ReleaseAll(instance.Id);
            instances.Clear();
        }
    }
}
