using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace RemSound.Core;

/// <summary>
/// A plugin instance's end of the link to the RemSound app.
///
/// <para><b>Never waiting is the whole design.</b> The DAW's audio thread must never wait on a socket,
/// so it does two things and nothing else: it drops its outgoing block in, and it takes the reply for
/// the block it is on out of the queue if that reply has come. Replies land on the bridge thread and
/// are filed behind it, each stamped with the DAW block it is for. If the app is slow for a moment the
/// DAW gets silence for one block — the same failure the network path already handles — instead of a
/// stall in somebody's session.</para>
///
/// <para><b>Allocation-free once running.</b> .NET has no real-time garbage collector, so an
/// allocation on the audio thread can stall every plugin in the DAW, not just this one. Every buffer
/// here is sized at construction and reused.</para>
///
/// <para><b>Asking is the heartbeat.</b> Each block sends one ClaimPeers carrying the frames just
/// consumed, the people on this track and the ask number. That claims them (so they leave the app's
/// speakers), keeps the claims alive, and asks for the next block, all in one message. Stop playing
/// and the claims lapse on their own — which is
/// the right direction to fail, since a peer stuck silent everywhere is far worse than one that comes
/// back through the speakers.</para>
/// </summary>
public sealed class PluginBridgeClient : IDisposable
{
    private readonly PluginBridgeLink link;
    private readonly IPEndPoint app;
    private readonly Guid instanceId;
    private readonly int instanceHash;

    // Replies, one slot each, between the bridge thread (writer) and the DAW's audio thread (reader).
    //
    // NOT a FIFO any more. This was a byte ring that the audio thread drained from the front, and what
    // it played was therefore "the oldest reply still queued" - which is whatever the queue's depth
    // happened to be, and the depth was an accident of the start: every call that found the ring empty
    // still sent its request, the late replies piled up, and two instances on the same peer sat exactly
    // that many blocks apart for the rest of the session (Anthony Reyers' logs, 2026-09-04: 896 frames
    // in one ring, 768 in the other). Trying to trim the pile turned out to be racy under Reaper's
    // bursts of blocks. So the queue is keyed instead: every reply carries the number of the DAW
    // block it is for (see PluginBridgeMessage.PeerAudioRound), the audio thread knows which block it
    // is on, and it plays THAT block's reply - a fixed lead behind the block it is asking for - or
    // silence if the reply has not come. Two instances asking in the same DAW block get the same block
    // number from the app, so they play the same audio by construction, whatever their queues hold.
    //
    // Still lock-free and still single-producer / single-consumer: the bridge thread only ever writes
    // the slot at `tail` and then publishes `tail`; the audio thread only ever reads slots below it
    // and advances `head`. Sized at construction; nothing here allocates on the audio thread.
    private const int SlotCount = 32;
    private sealed class Slot
    {
        public long Round;
        public int Floats;
        public readonly float[] Data = new float[PluginBridgeProtocol.MaxAudioBytes / sizeof(float)];
    }
    private readonly Slot[] slots = new Slot[SlotCount];
    private int head;   // audio thread advances
    private int tail;   // bridge thread advances

    // The ask this thread is on, 1-based, never reset - the mapping below stays valid across a stop
    // and start. The block number a reply carries minus the ask number it answers is a constant for
    // this instance (the app numbers one block per ask); learned from every reply, unknown until one.
    private int askNumber;
    private long blockOffset = long.MinValue;
    private volatile bool dropQueued;

    /// <summary>How many blocks behind its own ask an instance plays, shared by EVERY instance in
    /// this process so that two tracks stay on the same block by construction. Replies must land
    /// within this many blocks of their ask; a host that calls in bursts (Reaper: several blocks back
    /// to back per device period) needs the lead to cover a burst. Raised on the audio thread when a
    /// reply arrives late, one step at a time; lowered slowly when replies have been comfortably early
    /// for a while. The cost of the lead is that many blocks of delay on the receive path - the same
    /// few blocks the old queue held by accident, now the same for everybody.</summary>
    private static int leadBlocks = DefaultLeadBlocks;
    private const int DefaultLeadBlocks = 3;
    private const int MaxLeadBlocks = 12;
    private static long leadRaisedAtTicks;
    private static long leadWindowStartTicks;
    private static int leadWindowWorstLateness;
    private static readonly object leadGate = new();
    private long framesSkipped;

    /// <summary>The lead as it stands, in blocks. Process-wide.</summary>
    public static int LeadBlocks => Volatile.Read(ref leadBlocks);

    /// <summary>Gate seam: start from a known lead.</summary>
    internal static void ResetLeadForTest() { lock (leadGate) { leadBlocks = DefaultLeadBlocks; leadWindowWorstLateness = 0; leadRaisedAtTicks = 0; leadWindowStartTicks = 0; } }

    private readonly byte[] sendScratch = new byte[PluginBridgeProtocol.MaxAudioBytes];
    private readonly byte[] requestScratch = new byte[PluginBridgeProtocol.ClaimSetSizeWithAsk(PluginBridgeProtocol.MaxClaimedPeers)];

    // The people this instance is receiving. An ARRAY, swapped whole: the audio thread reads the
    // reference once per block and walks it, so it can never see a half-edited list. Never mutated in
    // place for the same reason.
    private volatile IPAddress[] receivingFrom = [];
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

    /// <summary>Blocks the DAW got less than a full reply for: the reply for that block had not come,
    /// or came back short, and the rest was left silent. A few at startup are normal — nothing has been
    /// answered yet. A number that keeps climbing means the app isn't keeping up, and it belongs in the
    /// status readout rather than being left as a mystery crackle.</summary>
    public long StarvedBlocks => Interlocked.Read(ref blocksStarved);

    public long ServedBlocks => Interlocked.Read(ref blocksServed);

    /// <summary>Frames of replies that were for blocks already behind us when their turn came - the
    /// pile a slow start leaves, dropped in one go the moment the numbering is known. A few hundred
    /// after a start are normal; a number that keeps climbing means replies are routinely later than
    /// the lead. See <see cref="ReadPeerBlock"/>.</summary>
    public long SkippedFrames => Interlocked.Read(ref framesSkipped);

    /// <summary>Counters for the log's once-a-second line. All lock-free adds, because the audio
    /// thread bumps them and must not take a lock to do it.</summary>
    public long SentBlocks => Interlocked.Read(ref blocksSent);
    public long BytesSent => Interlocked.Read(ref bytesSent);
    public long BytesReceived => Interlocked.Read(ref bytesReceived);

    /// <summary>How much audio is waiting for the DAW right now, in frames: the replies queued ahead
    /// of the block being played. Read on the log thread; a slot changing under it costs one wrong
    /// number in a log line, nothing more. Named for the byte ring this queue replaced; the name is
    /// kept because it is also the plugin log's column header.</summary>
    public int RingFrames
    {
        get
        {
            if (dropQueued) return 0;   // stopped: whatever is queued is for a set that has gone, and goes on the next call
            var h = Volatile.Read(ref head);
            var t = Volatile.Read(ref tail);
            var frames = 0;
            for (var i = h; i != t; i++) frames += slots[i & (SlotCount - 1)].Floats / PluginBridgeProtocol.WireChannels;
            return frames;
        }
    }

    public PluginBridgeClient(int appPort = PluginBridgeProtocol.DefaultPort, Guid? id = null)
    {
        instanceId = id ?? Guid.NewGuid();
        instanceHash = PluginBridgeProtocol.InstanceHash(instanceId);
        app = new IPEndPoint(IPAddress.Loopback, appPort);
        for (var i = 0; i < slots.Length; i++) slots[i] = new Slot();
        link = new PluginBridgeLink(0);   // any free loopback port; the app replies to wherever we bind
        link.MessageReceived += OnMessage;
    }

    /// <summary>Say hello and ask who we could receive. Safe to call again — a plugin left open while
    /// RemSound was restarted reconnects by simply asking again.
    ///
    /// <para>Carries this DAW's process id, so the app can tell "two tracks in Reaper" from "Reaper
    /// and Ableton". It matters on the SEND side: instances in one process are already summed into
    /// one block before they reach the app, and which instance carries that block can change when a
    /// plugin is switched to receive or unloaded. Without the process id the app would read that as
    /// one DAW leaving and another arriving, and hand the new one a buffer and a resampler it does
    /// not need — for audio coming from the same audio thread it was already getting.</para>
    ///
    /// <para>Local loopback only, and additive: an app that has never heard of this payload reads a
    /// Hello exactly as before, and a plugin that does not send it is grouped by instance as before.
    /// Nothing about the network protocol is involved.</para></summary>
    public void Hello()
    {
        Span<byte> payload = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(payload, Environment.ProcessId);
        link.Send(app, PluginBridgeMessage.Hello, instanceHash, null, payload);
    }

    /// <summary>One peer, or nobody. Kept for the single-peer callers; the list below is the real
    /// one.</summary>
    public void ReceiveFrom(IPAddress? peer) => SetReceivedPeers(peer is null ? [] : [peer]);

    /// <summary>
    /// Who this instance receives onto its track. Several people are allowed: the app reads each of
    /// them and returns one mixed block, so a track can carry a conversation rather than one voice.
    ///
    /// <para>Anybody dropped from the set is released AT ONCE rather than left to time out, so
    /// changing a track's sources doesn't leave the previous person mute in the app for five
    /// seconds.</para>
    ///
    /// <para><b>An unchanged set does nothing at all.</b> This is called from every parameter touch,
    /// which includes moving a level spinner, and it used to reset the ring every time — throwing away
    /// the jitter cushion and restarting it, so nudging the volume put a tick in the received audio.
    /// The log said so plainly ("now receiving X (was X)", three times while the window was open,
    /// 2026-09-01). The ring is also NOT reset when the set merely changes while already receiving:
    /// what is buffered is at most a few milliseconds of the previous mix, and a short gap is worse
    /// than a short tail.</para>
    /// </summary>
    public void SetReceivedPeers(IReadOnlyList<IPAddress>? peers)
    {
        var next = Normalise(peers);
        var previous = receivingFrom;
        if (SameSet(previous, next)) return;

        foreach (var gone in previous)
        {
            if (Contains(next, gone)) continue;
            link.Send(app, PluginBridgeMessage.ReleasePeer, instanceHash, gone, []);
        }
        receivingFrom = next;
        // Only when starting or stopping. See the note above for why a change of who does not.
        if (previous.Length == 0 || next.Length == 0) dropQueued = true;   // the audio thread empties the queue on its next call

        Notable?.Invoke(next.Length == 0
            ? (previous.Length == 0 ? "not receiving anybody" : $"released {Describe(previous)} - back to RemSound's own output")
            : $"now receiving {Describe(next)}" + (previous.Length == 0 ? "" : $" (was {Describe(previous)})"));
    }

    /// <summary>Distinct, IPv4, and no longer than the link carries. Duplicates matter more than they
    /// look: the app sums what it is asked for, so the same person twice would arrive 6 dB up.</summary>
    private static IPAddress[] Normalise(IReadOnlyList<IPAddress>? peers)
    {
        if (peers is null || peers.Count == 0) return [];
        var result = new List<IPAddress>(Math.Min(peers.Count, PluginBridgeProtocol.MaxClaimedPeers));
        foreach (var peer in peers)
        {
            if (peer is null) continue;
            if (peer.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
            if (result.Any(p => p.Equals(peer))) continue;
            result.Add(peer);
            if (result.Count >= PluginBridgeProtocol.MaxClaimedPeers) break;
        }
        return [.. result];
    }

    private static bool Contains(IPAddress[] set, IPAddress peer)
    {
        foreach (var member in set) if (member.Equals(peer)) return true;
        return false;
    }

    private static bool SameSet(IPAddress[] a, IPAddress[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++) if (!a[i].Equals(b[i])) return false;
        return true;
    }

    private static string Describe(IPAddress[] set)
        => set.Length switch { 0 => "nobody", 1 => set[0].ToString(), _ => $"{set.Length} peers: {string.Join(", ", set.Select(p => p.ToString()))}" };

    /// <summary>
    /// Called from the DAW's audio thread, once per block. Fills <paramref name="destination"/> with
    /// interleaved stereo from the claimed peers and asks the app for a block further on.
    ///
    /// <para>Order matters: take what has arrived FIRST, then ask. Asking first would tempt a
    /// reader into waiting for the answer, which is the one thing this thread must never do.</para>
    ///
    /// <para><b>Which block is played.</b> This call is ask number <c>n</c>, and the app will answer it
    /// with the number of the DAW block it belongs to, <c>n + offset</c>, where the offset is the
    /// constant learned from earlier replies. What is played now is the reply for block
    /// <c>n + offset - lead</c>: asked <c>lead</c> calls ago, and long since arrived. Anything queued
    /// for an earlier block is behind us and dropped; a reply that has not come is one block of
    /// silence and nothing after it. Every instance in this process uses the same lead, and two
    /// instances asking in the same DAW block get the same block number from the app, so two tracks
    /// on one peer play the same samples in the same block by construction - whatever each queue
    /// happened to hold at start.</para>
    /// </summary>
    /// <returns>Frames actually filled; the rest of the buffer is left silent.</returns>
    public int ReadPeerBlock(Span<float> destination, int frames)
    {
        var wanted = Math.Min(frames, destination.Length / PluginBridgeProtocol.WireChannels);
        if (wanted <= 0) return 0;
        var wantedFloats = wanted * PluginBridgeProtocol.WireChannels;
        destination[..wantedFloats].Clear();

        if (dropQueued)
        {
            // Started or stopped since the last call: whatever is queued is for a set that has gone.
            dropQueued = false;
            Volatile.Write(ref head, Volatile.Read(ref tail));
            Volatile.Write(ref blockOffset, long.MinValue);
        }

        var ask = ++askNumber;
        var offset = Volatile.Read(ref blockOffset);
        var filled = 0;
        var t = Volatile.Read(ref tail);
        var h = head;
        if (offset != long.MinValue)
        {
            var target = ask + offset - Volatile.Read(ref leadBlocks);
            // Replies arrive in ask order, so the queue is in block order: drop what is behind, play
            // the target if it is here, and leave anything beyond it for its own turn.
            while (h != t)
            {
                var slot = slots[h & (SlotCount - 1)];
                if (slot.Round < target)
                {
                    Interlocked.Add(ref framesSkipped, slot.Floats / PluginBridgeProtocol.WireChannels);
                    h++;
                    continue;
                }
                if (slot.Round == target)
                {
                    filled = Take(slot, destination, wantedFloats);
                    h++;
                }
                break;
            }
        }
        else if (h != t)
        {
            // Numbering not known yet - nothing has been answered since the start - or an app that
            // predates the numbering: play in arrival order, as this always did.
            filled = Take(slots[h & (SlotCount - 1)], destination, wantedFloats);
            h++;
        }
        Volatile.Write(ref head, h);

        if (filled < wanted) Interlocked.Increment(ref blocksStarved);
        else Interlocked.Increment(ref blocksServed);

        // Ask for the block this call is for. Also the claim and the heartbeat - see the note on
        // ClaimPeers. ONE message however many people are on this track: the app mixes them, so the
        // loopback traffic and the bridge thread's work do not multiply by the size of the set.
        var peers = receivingFrom;
        if (peers.Length > 0)
        {
            var length = PluginBridgeProtocol.WriteClaimSet(requestScratch, wanted, peers, ask);
            link.Send(app, PluginBridgeMessage.ClaimPeers, instanceHash, null, requestScratch.AsSpan(0, length));
        }
        return filled;
    }

    private static int Take(Slot slot, Span<float> destination, int wantedFloats)
    {
        var floats = Math.Min(slot.Floats, wantedFloats);
        slot.Data.AsSpan(0, floats).CopyTo(destination);
        return floats / PluginBridgeProtocol.WireChannels;
    }

    /// <summary>Called from the DAW's audio thread when this instance is SENDING: hand the track's
    /// block to the app, which puts it on the wire through the connection it already owns.</summary>
    public bool SendTrackBlock(ReadOnlySpan<float> samples)
    {
        var bytes = samples.Length * sizeof(float);
        if (bytes == 0 || bytes > PluginBridgeProtocol.MaxAudioBytes) return false;
        // One memcpy, not a per-sample call. This ran once per float on the DAW's audio thread — a
        // thousand calls for a 512-frame stereo block — to produce the identical bytes a straight copy
        // gives on any little-endian machine. WriteRing on the receive side already states that
        // assumption in as many words and reads the ring back as floats verbatim, so the two halves
        // of the same link were making opposite trades. 2026-08-24.
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples).CopyTo(sendScratch);
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
        switch (type)
        {
            case PluginBridgeMessage.PeerAudio:
                Interlocked.Add(ref bytesReceived, payload.Length);
                Enqueue(payload.Span, round: -1, askNumber: -1);
                break;
            case PluginBridgeMessage.PeerAudioRound:
                Interlocked.Add(ref bytesReceived, payload.Length);
                if (PluginBridgeProtocol.TryReadAudioRoundHeader(payload.Span, out var round, out var answered))
                    Enqueue(payload.Span[PluginBridgeProtocol.AudioRoundHeaderSize..], round, answered);
                break;
            case PluginBridgeMessage.PeerList:
                var updated = ParsePeerList(payload.Span);
                // Compare WHO, not how many. Counting alone missed the swap — somebody leaving as
                // somebody else joins keeps the count identical, so the log would say nothing at the
                // moment the track's peer list changed underneath the user. 2026-08-24.
                var changed = updated.Count != KnownPeers.Count
                              || !updated.Select(p => p.Item1.ToString()).SequenceEqual(KnownPeers.Select(p => p.Address.ToString()));
                KnownPeers = updated;
                if (changed) Notable?.Invoke($"peer list from RemSound: {updated.Count} peer(s) - {DescribePeers(updated)}");
                break;
        }
    }

    /// <summary>Bridge thread: file a reply that just arrived, in the next free slot. Lock-free
    /// producer side - see the slots. A full queue means the DAW is not reading, and the reply is
    /// dropped rather than written over something unread.
    ///
    /// <para>The payload is little-endian float, and so is every machine RemSound builds for
    /// (win-x64), so the bytes go into the slot verbatim and come back out as floats. That is the
    /// same assumption the receive path's ring already makes between the network and render
    /// threads.</para></summary>
    private void Enqueue(ReadOnlySpan<byte> payload, long round, int askNumber)
    {
        var floats = payload.Length / sizeof(float);
        floats -= floats % PluginBridgeProtocol.WireChannels;   // whole frames, or every later sample swaps channels
        if (floats <= 0) return;
        var t = tail;
        if (t - Volatile.Read(ref head) >= SlotCount) return;
        var slot = slots[t & (SlotCount - 1)];
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(payload[..(floats * sizeof(float))]).CopyTo(slot.Data);
        slot.Floats = floats;
        slot.Round = round;
        Volatile.Write(ref tail, t + 1);

        if (round >= 0 && askNumber >= 0)
        {
            // The numbering: this reply's block minus the ask it answers is this instance's constant.
            Volatile.Write(ref blockOffset, round - askNumber);
            // How late was it? The audio thread is on ask `asked`; this answers `askNumber`; the block
            // is played at ask `askNumber + lead`. Late means the lead did not cover the gap, and the
            // lead goes up one step - for everybody in the process, so the tracks move together.
            var lateness = Volatile.Read(ref this.askNumber) - askNumber;
            NoteLateness(lateness);
        }
    }

    /// <summary>The lead controller. One raise at a time, no more than a few a second, so a burst of
    /// late replies after a stall moves the lead one step rather than to the stall's length; one
    /// lowering per five quiet seconds, so a lead raised for a burst pattern that has gone comes down
    /// again. Cheap: a few reads and, rarely, a lock.</summary>
    private static void NoteLateness(int lateness)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var frequency = System.Diagnostics.Stopwatch.Frequency;
        var lead = Volatile.Read(ref leadBlocks);
        if (lateness >= lead)
        {
            if (lead >= MaxLeadBlocks) return;
            lock (leadGate)
            {
                if (leadBlocks >= MaxLeadBlocks) return;
                if (now - leadRaisedAtTicks < frequency / 5) return;   // one step per 200 ms at most
                leadRaisedAtTicks = now;
                leadBlocks++;
                leadWindowWorstLateness = lateness;
                leadWindowStartTicks = now;
            }
            return;
        }
        // Quiet: remember the worst lateness in the window, and lower the lead when the window ends
        // with room to spare.
        if (lateness > Volatile.Read(ref leadWindowWorstLateness)) Volatile.Write(ref leadWindowWorstLateness, lateness);
        var windowStart = Volatile.Read(ref leadWindowStartTicks);
        if (windowStart == 0) { Volatile.Write(ref leadWindowStartTicks, now); return; }
        if (now - windowStart < frequency * 5) return;
        lock (leadGate)
        {
            if (now - leadWindowStartTicks < frequency * 5) return;
            if (leadBlocks > DefaultLeadBlocks && leadWindowWorstLateness <= leadBlocks - 2) leadBlocks--;
            leadWindowWorstLateness = 0;
            leadWindowStartTicks = now;
        }
    }

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
