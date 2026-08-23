using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace RemSound.Core;

/// <summary>
/// A plugin instance's end of the link to the RemSound app.
///
/// <para><b>The ring is the whole design.</b> The DAW's audio thread must never wait on a socket, so
/// it does two things and nothing else: it drops its outgoing block in, and it takes whatever peer
/// audio has already arrived out. Replies land on the bridge thread and top the ring up behind it.
/// If the app is slow for a moment the DAW gets silence for one block — the same failure the network
/// path already handles — instead of a stall in somebody's session.</para>
///
/// <para><b>Allocation-free once running.</b> .NET has no real-time garbage collector, so an
/// allocation on the audio thread can stall every plugin in the DAW, not just this one. Every buffer
/// here is sized at construction and reused.</para>
///
/// <para><b>Asking is the heartbeat.</b> Each block sends one ClaimPeer carrying the frames just
/// consumed. That claims the peer (so it leaves the app's speakers), keeps the claim alive, and asks
/// for the next block, all in one message. Stop playing and the claim lapses on its own — which is
/// the right direction to fail, since a peer stuck silent everywhere is far worse than one that comes
/// back through the speakers.</para>
/// </summary>
public sealed class PluginBridgeClient : IDisposable
{
    private readonly PluginBridgeLink link;
    private readonly IPEndPoint app;
    private readonly Guid instanceId;
    private readonly int instanceHash;

    // The ring between the bridge thread (writer) and the DAW's audio thread (reader). Sized for a
    // generous DAW buffer plus headroom, so a late reply has somewhere to land rather than being lost.
    //
    // LOCK-FREE, and that is the point. This was a hand-rolled array guarded by a `lock (ringGate)`
    // taken on BOTH sides — so the DAW's audio thread could block waiting for the bridge's network
    // thread to finish a write, and if that thread were descheduled mid-write it would stall every
    // plugin in the session, not just this one. The class summary above already promised the
    // opposite: "The DAW's audio thread must never wait on a socket ... instead of a stall in
    // somebody's session." AudioRingBuffer is the single-producer / single-consumer ring RemSound
    // already uses between the network and render threads on the receive side — exactly this shape —
    // and it drops oldest on overflow just as the hand-rolled one did. 2026-08-23 audit, finding P1.
    private readonly AudioRingBuffer ring;

    private readonly byte[] sendScratch = new byte[PluginBridgeProtocol.MaxAudioBytes];
    private readonly byte[] requestScratch = new byte[sizeof(int)];

    private volatile IPAddress? receivingFrom;
    private long blocksServed, blocksStarved, blocksSent, bytesSent, bytesReceived;

    /// <summary>Raised when something worth writing down happens — connected, peer list arrived, the
    /// app went away. Fired on the bridge thread, never on the DAW's audio thread.</summary>
    public event Action<string>? Notable;

    /// <summary>Peers the app says are available, refreshed whenever we say hello. Empty until the
    /// app answers — which is also how "RemSound isn't running" shows up, stated plainly rather than
    /// as an empty box that looks broken.</summary>
    public IReadOnlyList<(IPAddress Address, string Name)> KnownPeers { get; private set; } = [];

    /// <summary>Has the app answered us at all? Drives the plugin's status line.</summary>
    public bool Connected { get; private set; }

    /// <summary>Blocks where the ring had nothing for the DAW. A few at startup are normal — the ring
    /// is filling. A number that keeps climbing means the app isn't keeping up, and it belongs in the
    /// status readout rather than being left as a mystery crackle.</summary>
    public long StarvedBlocks => Interlocked.Read(ref blocksStarved);

    public long ServedBlocks => Interlocked.Read(ref blocksServed);

    /// <summary>Counters for the log's once-a-second line. All lock-free adds, because the audio
    /// thread bumps them and must not take a lock to do it.</summary>
    public long SentBlocks => Interlocked.Read(ref blocksSent);
    public long BytesSent => Interlocked.Read(ref bytesSent);
    public long BytesReceived => Interlocked.Read(ref bytesReceived);

    /// <summary>How much audio is waiting for the DAW right now, in frames. The number that explains
    /// a crackle: near zero means the app is not keeping up, and climbing means it is over-supplying.</summary>
    public int RingFrames => ring.BufferedBytes / (sizeof(float) * PluginBridgeProtocol.WireChannels);

    public PluginBridgeClient(int appPort = PluginBridgeProtocol.DefaultPort, Guid? id = null, int ringFrames = 8192)
    {
        instanceId = id ?? Guid.NewGuid();
        instanceHash = PluginBridgeProtocol.InstanceHash(instanceId);
        app = new IPEndPoint(IPAddress.Loopback, appPort);
        ring = new AudioRingBuffer(ringFrames * PluginBridgeProtocol.WireChannels * sizeof(float));
        link = new PluginBridgeLink(0);   // any free loopback port; the app replies to wherever we bind
        link.MessageReceived += OnMessage;
    }

    /// <summary>Say hello and ask who we could receive. Safe to call again — a plugin left open while
    /// RemSound was restarted reconnects by simply asking again.</summary>
    public void Hello() => link.Send(app, PluginBridgeMessage.Hello, instanceHash, null, []);

    /// <summary>Which peer this instance receives, or null when it is sending instead. Changing it
    /// releases the old one at once, so switching a track's source doesn't leave the previous person
    /// mute in the app until a timeout expires.</summary>
    public void ReceiveFrom(IPAddress? peer)
    {
        var previous = receivingFrom;
        if (previous is not null && !previous.Equals(peer))
        {
            link.Send(app, PluginBridgeMessage.ReleasePeer, instanceHash, previous, []);
        }
        receivingFrom = peer;
        ring.Reset();
        Notable?.Invoke(peer is null
            ? (previous is null ? "not receiving anybody" : $"released {previous} - back to RemSound's own output")
            : $"now receiving {peer}" + (previous is null ? "" : $" (was {previous})"));
    }

    /// <summary>
    /// Called from the DAW's audio thread, once per block. Fills <paramref name="destination"/> with
    /// interleaved stereo from the claimed peer and asks the app for the next block.
    ///
    /// <para>Order matters: take what has arrived FIRST, then ask. Asking first would tempt a
    /// reader into waiting for the answer, which is the one thing this thread must never do.</para>
    /// </summary>
    /// <returns>Frames actually filled; the rest of the buffer is left silent.</returns>
    public int ReadPeerBlock(Span<float> destination, int frames)
    {
        var wanted = Math.Min(frames, destination.Length / PluginBridgeProtocol.WireChannels);
        if (wanted <= 0) return 0;
        var wantedFloats = wanted * PluginBridgeProtocol.WireChannels;
        destination[..wantedFloats].Clear();

        // Lock-free consumer read. Any shortfall is already silence because we cleared above, and
        // AudioRingBuffer zero-fills the tail as well; the return value counts only what genuinely
        // came out of the ring, which is what the caller needs to know.
        var filled = ring.ReadFloats(destination[..wantedFloats]) / PluginBridgeProtocol.WireChannels;

        if (filled < wanted) Interlocked.Increment(ref blocksStarved);
        else Interlocked.Increment(ref blocksServed);

        // Ask for the next block. Also the claim and the heartbeat — see the note on ClaimPeer.
        var peer = receivingFrom;
        if (peer is not null)
        {
            BinaryPrimitives.WriteInt32LittleEndian(requestScratch, wanted);
            link.Send(app, PluginBridgeMessage.ClaimPeer, instanceHash, peer, requestScratch);
        }
        return filled;
    }

    /// <summary>Called from the DAW's audio thread when this instance is SENDING: hand the track's
    /// block to the app, which puts it on the wire through the connection it already owns.</summary>
    public bool SendTrackBlock(ReadOnlySpan<float> samples)
    {
        var bytes = samples.Length * sizeof(float);
        if (bytes == 0 || bytes > PluginBridgeProtocol.MaxAudioBytes) return false;
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(sendScratch.AsSpan(i * sizeof(float)), samples[i]);
        }
        if (!link.Send(app, PluginBridgeMessage.TrackAudio, instanceHash, null, sendScratch.AsSpan(0, bytes))) return false;
        Interlocked.Increment(ref blocksSent);
        Interlocked.Add(ref bytesSent, bytes);
        return true;
    }

    private void OnMessage(PluginBridgeMessage type, int hash, IPAddress? peer, ReadOnlyMemory<byte> payload, IPEndPoint from)
    {
        if (hash != instanceHash) return;   // another instance's traffic; not ours to act on
        if (!Connected)
        {
            Connected = true;
            Notable?.Invoke("RemSound answered - connected");
        }
        LastHeardUtc = DateTime.UtcNow;
        switch (type)
        {
            case PluginBridgeMessage.PeerAudio:
                Interlocked.Add(ref bytesReceived, payload.Length);
                WriteRing(payload.Span);
                break;
            case PluginBridgeMessage.PeerList:
                var updated = ParsePeerList(payload.Span);
                var changed = updated.Count != KnownPeers.Count;
                KnownPeers = updated;
                if (changed) Notable?.Invoke($"peer list from RemSound: {updated.Count} peer(s) - {DescribePeers(updated)}");
                break;
        }
    }

    /// <summary>Bridge thread: top the ring up with a reply that just arrived. Lock-free producer
    /// side — see the <c>ring</c> field. Overflow drops the OLDEST samples, which is what the
    /// hand-rolled version did and the right trade: a ring that has overrun means we are behind, and
    /// keeping stale audio would hold that lateness forever.
    ///
    /// <para>The payload is little-endian float, and so is every machine RemSound builds for
    /// (win-x64), so the bytes go into the ring verbatim and come back out as floats. That is the
    /// same assumption the receive path's ring already makes between the network and render
    /// threads.</para></summary>
    private void WriteRing(ReadOnlySpan<byte> payload)
    {
        // Trim to whole floats: a truncated tail would shift every subsequent sample by a byte.
        var usable = payload.Length - (payload.Length % sizeof(float));
        if (usable <= 0) return;
        ring.Write(payload[..usable]);
    }

    /// <summary>When the app was last heard from. Turns "it stopped working" into a time.</summary>
    public DateTime LastHeardUtc { get; private set; } = DateTime.MinValue;

    private static string DescribePeers(IReadOnlyList<(IPAddress Address, string Name)> peers)
        => peers.Count == 0 ? "(none)" : string.Join(", ", peers.Select(p => $"{p.Name} [{p.Address}]"));

    private static IReadOnlyList<(IPAddress, string)> ParsePeerList(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0) return [];
        var list = new List<(IPAddress, string)>();
        foreach (var line in Encoding.UTF8.GetString(payload).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            var addressText = tab < 0 ? line : line[..tab];
            if (!IPAddress.TryParse(addressText, out var address)) continue;
            list.Add((address, tab < 0 ? addressText : line[(tab + 1)..]));
        }
        return list;
    }

    public void Dispose()
    {
        // Say goodbye before the socket goes: it returns any claimed peer to the app's speakers
        // immediately rather than after the timeout, which is the difference between closing a plugin
        // and wondering for five seconds whether you have broken something.
        try { link.Send(app, PluginBridgeMessage.Goodbye, instanceHash, null, []); } catch { }
        link.MessageReceived -= OnMessage;
        link.Dispose();
    }
}
