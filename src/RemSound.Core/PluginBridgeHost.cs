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
/// <para><b>The DAW is the clock.</b> A plugin sends <see cref="PluginBridgeMessage.ClaimPeers"/> every
/// audio block carrying the number of frames it just consumed and the people on its track; we read
/// exactly that many frames of each of them, sum them, and send the block back. One message therefore
/// does three jobs — it claims the peers, it is the heartbeat that keeps the claims alive, and it is
/// the request for the next block. Pumping on a timer of our own instead would give each peer two
/// clocks, and what we send would drift against the DAW's blocks.</para>
///
/// <para><b>Nothing here blocks a DAW.</b> Requests are served on the bridge's receive thread and the
/// answer is fired back as a datagram; the plugin's audio thread plays whatever reply has already
/// arrived and never waits on us. If we are slow, the plugin plays silence for that block — the same
/// failure the network path already handles — rather than a stall in someone's session.</para>
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
        /// <summary>Everybody this instance is putting on its track. Usually one; several when a track
        /// is carrying a conversation. Swapped whole rather than edited, so a reader never sees it
        /// half-changed.</summary>
        public IPAddress[] ClaimedPeers { get; set; } = [];
        /// <summary>The same set as it arrived on the wire, kept purely to compare against the next
        /// request without parsing it. The set is identical on all but a handful of blocks, and this
        /// runs once per block per instance, so the normal path allocates nothing at all.</summary>
        public byte[] ClaimedRaw { get; set; } = [];
        public DateTime LastSeenUtc { get; set; }
        /// <summary>Which DAW this instance lives in. Every instance in one host process shares this
        /// id; it defaults to the instance's own id for a plugin that never told us its process, so
        /// an older plugin still behaves exactly as it used to.</summary>
        public Guid Group { get; set; }
    }

    private readonly PluginBridgeLink link;
    private readonly PluginPeerClaims claims;
    private readonly PeerAudioReader? readPeer;
    private readonly object gate = new();
    private readonly Dictionary<int, Instance> instances = new();
    /// <summary>One id per DAW process that has said hello, so the send path can treat a host as a
    /// host. See <see cref="PluginBridgeClient.Hello"/> for why the process id is worth carrying.</summary>
    private readonly Dictionary<int, Guid> hostGroups = new();

    /// <summary>Which DAW block a host process is on, for the round number stamped on replies. Every
    /// instance in the process asking once is one block; an instance asking again is the next. The
    /// per-peer rounds in ReadShared do the same for positions; this one is what the plugin can
    /// compare across instances, whatever peers each is carrying.</summary>
    private sealed class BlockCounter
    {
        public long Number = 1;
        public readonly HashSet<Guid> Asked = [];
    }
    private readonly Dictionary<Guid, BlockCounter> blockCounters = new();

    // Reused across requests — allocating a buffer per audio block would be exactly the mistake the
    // plugin side is careful to avoid, and this thread feeds it.
    private readonly float[] readScratch = new float[MaxFramesPerRequest * 2];
    // Where the set is summed before it goes out. Separate from readScratch because each peer is read
    // into that one and then added into this.
    private readonly float[] mixScratch = new float[MaxFramesPerRequest * 2];
    private readonly byte[] sendScratch = new byte[MaxFramesPerRequest * 2 * sizeof(float) + PluginBridgeProtocol.AudioRoundHeaderSize];

    /// <summary>The port plugins should talk to. Normally <see cref="PluginBridgeProtocol.DefaultPort"/>;
    /// the gate uses an OS-assigned one so a running RemSound is never disturbed by a test.</summary>
    public int Port => link.Port;

    /// <summary>The claim register, to hand to the receiver so claimed peers leave the mix.</summary>
    public PluginPeerClaims Claims => claims;

    /// <summary>Audio a plugin instance wants sent to the peers — one DAW track going out. Raised on
    /// the bridge thread with interleaved stereo at <see cref="PluginBridgeProtocol.WireSampleRate"/>.
    ///
    /// <para>The Guid identifies the DAW, not the instance: tracks inside one host are summed in the
    /// plugin, on the host's own audio thread where they are already sample-aligned, so what arrives
    /// here is one stream per DAW however many tracks are feeding it.</para></summary>
    public event Action<Guid, ReadOnlyMemory<float>>? TrackAudioReceived;

    /// <summary>Where the peer list comes from when a plugin asks who it can receive. Set by the app;
    /// null means "no peers yet", which the plugin shows plainly rather than looking broken.</summary>
    public Func<IReadOnlyList<(IPAddress Address, string Name)>>? PeerListSource { get; set; }

    /// <summary>Raised when something worth writing down happens on the link. Fired on the bridge
    /// thread; the app turns these into log lines. Deliberately plain English — whoever reads this file
    /// is trying to work out why a track was silent, not trying to read our code.</summary>
    public event Action<string>? Notable;

    private long blocksServed, bytesServed, trackBlocks, trackBytes, unknownPeerRequests, shortReads, trackBlocksDropped;

    /// <summary>Track audio that arrived and had nowhere to go. Non-zero means the plugin's send side
    /// is working and the APP's side is not — a distinction the counters could not previously make.</summary>
    public long TrackBlocksDropped => Interlocked.Read(ref trackBlocksDropped);

    /// <summary>Counters for the app's once-a-second plugin line.</summary>
    public long BlocksServed => Interlocked.Read(ref blocksServed);
    public long BytesServed => Interlocked.Read(ref bytesServed);
    public long TrackBlocksReceived => Interlocked.Read(ref trackBlocks);
    public long TrackBytesReceived => Interlocked.Read(ref trackBytes);

    /// <summary>Blocks a plugin asked for that we could not fill. A few while a peer's buffer builds
    /// are normal; a number that climbs is the app failing to keep up, and it is the difference between
    /// diagnosing a crackle and guessing at it.</summary>
    public long ShortReads => Interlocked.Read(ref shortReads);

    /// <summary>Requests that got no audio back from anybody in the set — their buffers are still
    /// filling, or the plugin is pointed at people who have gone. Silent on the track, and invisible
    /// without this. The name cannot tell those two apart; it matches the <c>unknownPeer=</c> key in
    /// the log line, which is kept as it is.</summary>
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
                 + $" short={ShortReads} unknownPeer={UnknownPeerRequests} malformed={MalformedReceived}"
                 + (TrackBlocksDropped > 0 ? $" blocksInDropped={TrackBlocksDropped}" : "");
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
                // How long it had been quiet, read BEFORE Touch resets it. A plugin now says hello
                // every couple of seconds so that its peer list stays live with no window open, and
                // logging every one of those would be a line per instance per two seconds — burying
                // the one that matters, which is a plugin coming back after a silence.
                var quietFor = LastSeenAge(hash);
                var instance = Touch(hash, from);
                NoteHostProcess(instance, payload.Span);
                SendPeerList(from, hash);
                if (quietFor is null) Notable?.Invoke($"a plugin said hello (instance {Short(instance.Id)}, from port {from.Port})");
                else if (quietFor > TimeSpan.FromSeconds(5))
                    Notable?.Invoke($"plugin {Short(instance.Id)} said hello again after {quietFor.Value.TotalSeconds:0}s quiet "
                                  + "- it will have reconnected after RemSound restarted");
                break;

            case PluginBridgeMessage.ClaimPeer:
                if (peer is null) return;
                ServeClaimOne(hash, from, peer, payload);
                break;

            case PluginBridgeMessage.ClaimPeers:
                ServeClaimSet(hash, from, payload);
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

    /// <summary>The single-peer request. Folded onto the same path as the set, so there is one
    /// implementation of claiming and reading rather than two that can drift apart.</summary>
    private void ServeClaimOne(int hash, IPEndPoint from, IPAddress peer, ReadOnlyMemory<byte> payload)
    {
        if (payload.Length < sizeof(int)) { Touch(hash, from); return; }
        var frames = BinaryPrimitives.ReadInt32LittleEndian(payload.Span);
        Span<byte> raw = stackalloc byte[4];
        if (!peer.TryWriteBytes(raw, out var written) || written != 4) return;
        Serve(hash, from, raw, frames, askNumber: -1);
    }

    /// <summary>Several peers onto one track. Claim, heartbeat and request in one message, exactly as
    /// the single-peer form, for however many people the instance is carrying.</summary>
    private void ServeClaimSet(int hash, IPEndPoint from, ReadOnlyMemory<byte> payload)
    {
        if (!PluginBridgeProtocol.TryReadClaimSet(payload.Span, out var frames, out var addresses, out var askNumber))
        {
            Touch(hash, from);
            return;
        }
        Serve(hash, from, addresses, frames, askNumber);
    }

    /// <summary>
    /// A plugin instance is receiving these people and wants its next block. Claim, heartbeat and
    /// request in one message.
    ///
    /// <para>Everybody in the set is read and SUMMED into one reply. The alternative, a message per
    /// peer each way, multiplies the loopback traffic and this thread's work by the size of the set,
    /// on the one thread that feeds a DAW. What it would buy is a per-peer trim inside the plugin, and
    /// that already exists in the app: RemSound's per-peer volume, pan and EQ run inside the session
    /// read this calls, so they shape what lands on the track.</para>
    /// </summary>
    private void Serve(int hash, IPEndPoint from, ReadOnlySpan<byte> addresses, int frames, int askNumber)
    {
        var instance = Touch(hash, from);
        var peers = UpdateClaims(instance, addresses);
        if (peers.Length == 0 || readPeer is null || frames <= 0) return;
        frames = Math.Min(frames, MaxFramesPerRequest);

        // Which DAW block this ask is for. Counted whether or not there is audio to answer with, so a
        // silent block still moves the count on and the numbering stays one per block.
        long block;
        lock (gate)
        {
            if (!blockCounters.TryGetValue(instance.Group, out var counter)) blockCounters[instance.Group] = counter = new BlockCounter();
            if (counter.Asked.Contains(instance.Id))
            {
                counter.Number++;
                counter.Asked.Clear();
            }
            counter.Asked.Add(instance.Id);
            block = counter.Number;
        }

        int produced;
        lock (readScratch)
        {
            var wantedFloats = frames * 2;
            var mix = mixScratch.AsSpan(0, wantedFloats);
            mix.Clear();
            produced = 0;

            foreach (var peer in peers)
            {
                // Reading a peer CONSUMES their jitter buffer, so several instances all reading it took
                // alternate slices and each ended up with a stuttering fraction of the stream while the
                // ring ran dry underneath every one of them. The claim register always allowed more than
                // one instance on a peer ("a main track and a monitor send, say") but the read path could
                // not honour it. So the peer is read ONCE, into a ring carried by position, and every
                // claimant reads the same positions out of it — see ReadShared.
                var got = ReadShared(instance.Group, instance.Id, peer, readScratch.AsSpan(0, wantedFloats), frames);
                if (got <= 0) continue;

                // NOT divided by the size of the set. Everybody getting quieter the moment somebody
                // joined the track would be a gain change nobody asked for, and unlike the send
                // direction the DAW fader is downstream of this plugin and can pull the lot down.
                var summed = got * 2;
                for (var i = 0; i < summed; i++) mix[i] += readScratch[i];
                if (got > produced) produced = got;
            }

            if (produced <= 0)
            {
                // Nothing for anybody in the set: either their buffers are still filling, or the plugin
                // is pointed at people who have gone. Counted, not logged per block - at one block per
                // audio callback a line each would be thousands a second.
                Interlocked.Increment(ref unknownPeerRequests);
                return;
            }
            if (produced < frames) Interlocked.Increment(ref shortReads);
            var bytes = produced * 2 * sizeof(float);
            // No peer named on the reply: it is a mix of the set, not any one person's audio, and
            // naming one of them in the header would be a lie the next reader has to work out.
            // A plugin that numbered its ask gets the block number back in front of the samples; an
            // older one gets the samples alone, as it always did.
            bool sent;
            if (askNumber >= 0)
            {
                PluginBridgeProtocol.WriteAudioRoundHeader(sendScratch, block, askNumber);
                Buffer.BlockCopy(mixScratch, 0, sendScratch, PluginBridgeProtocol.AudioRoundHeaderSize, bytes);
                sent = link.Send(from, PluginBridgeMessage.PeerAudioRound, hash, null, sendScratch.AsSpan(0, PluginBridgeProtocol.AudioRoundHeaderSize + bytes));
            }
            else
            {
                Buffer.BlockCopy(mixScratch, 0, sendScratch, 0, bytes);
                sent = link.Send(from, PluginBridgeMessage.PeerAudio, hash, null, sendScratch.AsSpan(0, bytes));
            }
            if (sent)
            {
                Interlocked.Increment(ref blocksServed);
                Interlocked.Add(ref bytesServed, bytes);
            }
        }
    }

    /// <summary>
    /// Bring an instance's claims in line with what it just asked for, and hand back the set to read.
    ///
    /// <para>The fast path is the point: the bytes are compared against the previous request and, when
    /// they match, which is every block but the handful where the user changed something, nothing is
    /// parsed, nothing is allocated, and the claim register is simply re-heartbeated.</para>
    ///
    /// <para>Anybody dropped is released here rather than left to time out, or they stay mute in the
    /// app for five seconds after being taken off a track, which reads as a fault.</para>
    /// </summary>
    private IPAddress[] UpdateClaims(Instance instance, ReadOnlySpan<byte> addresses)
    {
        IPAddress[] peers;
        lock (gate)
        {
            if (!addresses.SequenceEqual(instance.ClaimedRaw))
            {
                var count = addresses.Length / 4;
                var next = new IPAddress[count];
                for (var i = 0; i < count; i++) next[i] = new IPAddress(addresses.Slice(i * 4, 4));

                foreach (var previous in instance.ClaimedPeers)
                {
                    if (Array.Exists(next, p => p.Equals(previous))) continue;
                    claims.Release(previous, instance.Id);
                    // Anything a sibling had queued for us belongs to somebody we are no longer
                    // carrying. Serving it now would put a few milliseconds of the wrong human on the
                    // track.
                    DropShared(instance.Id, previous);
                    Notable?.Invoke($"plugin {Short(instance.Id)} let {previous} go - back on the speakers");
                }
                foreach (var added in next)
                {
                    if (Array.Exists(instance.ClaimedPeers, p => p.Equals(added))) continue;
                    Notable?.Invoke($"plugin {Short(instance.Id)} took {added} - that peer now plays on the DAW track instead of the speakers");
                }

                instance.ClaimedPeers = next;
                instance.ClaimedRaw = addresses.ToArray();
            }
            peers = instance.ClaimedPeers;
        }

        // The heartbeat, outside our own lock: Claim takes the register's.
        foreach (var peer in peers) claims.Claim(peer, instance.Id);
        return peers;
    }

    // ---- One read per peer, shared BY POSITION, so every track hears the same sample at the same
    // ---- moment ------------------------------------------------------------------------------------
    //
    // Reading a peer drains their jitter buffer. One reader is the design; two readers silently split
    // the stream between them. The first version of this handed a sibling's read to the other
    // claimants through a per-claimant QUEUE, which solved the splitting and left a subtler fault
    // behind: whether a claimant got this round's block or the previous one depended on whether its
    // request happened to arrive before or after the instance that did the read. That order is fixed
    // by where the plugins sit in the DAW's FX chains, so one track ended up permanently one audio
    // buffer behind the other — 2,7 ms at a 128-frame buffer, which is not heard as an echo but is
    // heard as comb filtering the moment both tracks are audible together. Anthony Reyers heard it
    // and put it at "around 2/3 ms" before being told what it was (2026-09-01).
    //
    // So the queues are gone. Instead there is ONE ring per peer carrying an absolute frame position,
    // and each claimant keeps a cursor into it. Whoever asks first pulls from the peer and appends;
    // everybody then reads the same positions. Two tracks on the same person are therefore
    // sample-aligned by construction rather than bounded to within a buffer, and it no longer matters
    // who asks first.
    //
    // A cursor PER INSTANCE turned out to be one step short of that. When the first instance to ask
    // found the peer's buffer momentarily empty, it got nothing and its cursor stayed; the second asked
    // a moment later, the packet had arrived, and it moved on - one block apart, for good, with nothing
    // to bring them back (Anthony Reyers, 2026-09-04: "still not always in sync"). So the unit is now a
    // ROUND per DAW and peer: the block being served, and who has had it. Everybody in a round gets the
    // same block from the same position, a short or empty pull is short or empty for the whole round,
    // and an instance asking again is the DAW moving on. A new instance simply joins the round in
    // progress, so it is aligned from its first sample and there is no special case for joining. Two
    // DAWs are two sets of rounds; instances in one DAW are grouped by the process id their Hello
    // carries.

    /// <summary>Smallest shared ring, in floats. Sized from the REAL block a host asked for rather
    /// than from <see cref="MaxFramesPerRequest"/>: four max-size blocks would be 128 KB each, which
    /// lands on the large-object heap, and thirty of those is four megabytes of pinned garbage to hold
    /// audio nobody is late for. Four blocks of a real 128- or 512-frame buffer is a few tens of
    /// kilobytes and stays off the LOH.</summary>
    private const int SharedMinimumFloats = 8192;

    /// <summary>What has been read from one peer lately, and where it sits in that peer's stream.
    /// <see cref="Start"/> is the absolute frame index of the first frame held.</summary>
    private sealed class SharedStream
    {
        public required float[] Data { get; set; }
        public long Start { get; set; }
        public long End { get; set; }
        public int HeldFrames => (int)(End - Start);
    }

    private readonly object sharedGate = new();
    private readonly Dictionary<IPAddress, SharedStream> sharedStreams = new();
    /// <summary>The block a DAW's instances are being served, per peer. See ReadShared.</summary>
    private sealed class Round
    {
        /// <summary>Absolute position of the round's first frame in the peer's stream.</summary>
        public long Start;
        /// <summary>What the round's first asker asked for; the pull is sized from it.</summary>
        public int Frames;
        /// <summary>How much of the block the ring could give, or -1 until somebody has pulled.
        /// Everybody in the round gets this many, even if more has arrived since.</summary>
        public int Available = -1;
        /// <summary>Who has been served this round. One of them asking again is the DAW moving on.</summary>
        public readonly HashSet<Guid> Served = [];
    }

    /// <summary>The round in progress, per DAW and peer.</summary>
    private readonly Dictionary<(Guid Group, IPAddress Peer), Round> rounds = new();
    /// <summary>Which instances of a DAW are on a peer at all, so its rounds go when the last of them
    /// lets go, and the peer's ring goes when the last DAW does.</summary>
    private readonly Dictionary<(Guid Group, IPAddress Peer), HashSet<Guid>> members = new();
    private static readonly Guid TestDaw = Guid.NewGuid();

    /// <summary>
    /// Read one peer for one claimant, pulling from the peer only when this claimant has caught up
    /// with everything already read.
    ///
    /// <para>Serialised by the caller (everything runs under the <c>readScratch</c> lock), and the
    /// shared state is guarded anyway because instances come and go on other paths.</para>
    /// </summary>
    /// <returns>Frames delivered; 0 when the peer has nothing at all.</returns>
    private int ReadShared(Guid group, Guid instance, IPAddress peer, Span<float> destination, int frames)
    {
        if (readPeer is null || frames <= 0) return 0;
        var wantedFloats = frames * 2;
        if (destination.Length < wantedFloats) return 0;

        // ONE lock for the whole read. The caller serialises everything through readScratch, but a
        // promise made in another method is not a guarantee, and the cost here is nothing: readPeer
        // never touches this gate, so there is no lock ordering to get wrong.
        lock (sharedGate)
        {
            SharedStream stream;
            if (!sharedStreams.TryGetValue(peer, out stream!))
            {
                stream = new SharedStream { Data = new float[Math.Max(SharedMinimumFloats, wantedFloats * 4)] };
                sharedStreams[peer] = stream;
            }
            else if (stream.Data.Length < wantedFloats * 2)
            {
                // A host with a bigger buffer than the ring was built for. Rare, and off the audio
                // thread, but it must not silently deliver short blocks for ever.
                var grown = new float[Math.Max(SharedMinimumFloats, wantedFloats * 4)];
                Array.Copy(stream.Data, grown, stream.HeldFrames * 2);
                stream.Data = grown;
            }

            var key = (group, peer);
            if (!members.TryGetValue(key, out var set)) members[key] = set = [];
            set.Add(instance);
            if (!rounds.TryGetValue(key, out var round))
            {
                // A DAW's first round on this peer starts at the LIVE EDGE. Starting at the oldest
                // frame still held would begin every track a few blocks in the past and leave it there.
                rounds[key] = round = new Round { Start = stream.End, Frames = frames };
            }
            else if (round.Served.Contains(instance))
            {
                // This instance has had this round's block: the DAW has moved on. The next round starts
                // where this one ended, so a short round leaves nothing out and an empty one is retried
                // at the same position.
                round.Start += Math.Max(0, round.Available);
                round.Frames = frames;
                round.Available = -1;
                round.Served.Clear();
            }
            // Fallen further behind than the ring holds: rejoin the live stream rather than work
            // through a backlog. Same trade the queue made when it overflowed.
            if (round.Start < stream.Start) { round.Start = stream.Start; round.Available = -1; }

            if (round.Available < 0)
            {
                // The round's first asker pulls what its block needs beyond what is already held. For
                // everybody after them there is nothing to pull, and the peer's buffer is not touched.
                var missing = (int)(round.Start + round.Frames - stream.End);
                if (missing > 0)
                {
                    var held = stream.HeldFrames;
                    if ((held + missing) * 2 > stream.Data.Length)
                    {
                        // Make room at the tail by dropping the oldest, and carry Start with it so the
                        // rounds still point at the frames they think they do.
                        var dropFrames = held + missing - (stream.Data.Length / 2);
                        if (dropFrames >= held) { stream.Start = stream.End; held = 0; }
                        else
                        {
                            Array.Copy(stream.Data, dropFrames * 2, stream.Data, 0, (held - dropFrames) * 2);
                            stream.Start += dropFrames;
                            held -= dropFrames;
                        }
                        if (round.Start < stream.Start) round.Start = stream.Start;
                        missing = (int)(round.Start + round.Frames - stream.End);
                    }
                    if (missing > 0)
                    {
                        var got = readPeer(peer, stream.Data.AsSpan(held * 2, missing * 2), missing);
                        if (got > 0) stream.End += got;
                    }
                }
                round.Available = (int)Math.Max(0, Math.Min(round.Frames, stream.End - round.Start));
            }

            round.Served.Add(instance);
            var give = Math.Min(frames, round.Available);
            if (give <= 0) return 0;
            var offset = (int)(round.Start - stream.Start) * 2;
            stream.Data.AsSpan(offset, give * 2).CopyTo(destination);
            return give;
        }
    }

    /// <summary>An instance has stopped carrying one person. It leaves that peer's rounds, and the
    /// peer's shared ring goes once nobody in any DAW is left on them.</summary>
    private void DropShared(Guid id, IPAddress peer)
    {
        lock (sharedGate)
        {
            foreach (var key in members.Keys.Where(k => k.Peer.Equals(peer)).ToList()) ForgetMemberLocked(key, id);
            if (!members.Keys.Any(k => k.Peer.Equals(peer))) sharedStreams.Remove(peer);
        }
    }

    /// <summary>An instance has gone. It leaves every round it was in.</summary>
    private void DropShared(Guid id)
    {
        lock (sharedGate)
        {
            foreach (var key in members.Keys.ToList()) ForgetMemberLocked(key, id);
            foreach (var peer in sharedStreams.Keys.ToList())
            {
                if (!members.Keys.Any(k => k.Peer.Equals(peer))) sharedStreams.Remove(peer);
            }
        }
    }

    private void ForgetMemberLocked((Guid Group, IPAddress Peer) key, Guid id)
    {
        if (!members.TryGetValue(key, out var set) || !set.Remove(id)) return;
        if (rounds.TryGetValue(key, out var round)) round.Served.Remove(id);
        if (set.Count == 0)
        {
            members.Remove(key);
            rounds.Remove(key);
        }
    }

    /// <summary>Gate seam: the shared read itself, driven directly. Two claimants asking in either
    /// order must come away with the SAME samples, and that is a property of this method rather than
    /// of the sockets around it - testing it through the link would be testing datagram arrival
    /// order. Instances are in one DAW unless the test names another.</summary>
    internal int ReadSharedForTest(Guid instance, IPAddress peer, Span<float> destination, int frames, Guid? daw = null)
        => ReadShared(daw ?? TestDaw, instance, peer, destination, frames);

    private void DispatchTrackAudio(int hash, ReadOnlyMemory<byte> payload)
    {
        if (payload.Length < sizeof(float)) return;

        // COUNT IT FIRST, even with nobody listening. The counters said blocksIn=0 while the plugin
        // reported 24,652 blocks sent, which reads as "the audio never arrived" — when in fact it
        // arrived and was dropped on the floor because the app never subscribed. A diagnostic that
        // cannot tell those two apart sends the next person hunting the wrong fault (2026-08-22).
        Interlocked.Increment(ref trackBlocks);
        Interlocked.Add(ref trackBytes, payload.Length);

        var handler = TrackAudioReceived;
        if (handler is null)
        {
            if (Interlocked.Increment(ref trackBlocksDropped) == 1)
                Notable?.Invoke("a plugin is sending its track, but nothing in the app is taking that audio - "
                              + "the blocks are counted and dropped (blocksInDropped)");
            return;
        }
        Guid id;
        lock (gate)
        {
            if (!instances.TryGetValue(hash, out var instance)) return;
            // The DAW, not the track. Which instance in a host carries the summed block can change
            // (somebody switches a plugin to receive, or closes one), and the send path must not read
            // that as one DAW leaving and another arriving.
            id = instance.Group;
        }
        // ONE copy, not two. This was `Buffer.BlockCopy(payload.ToArray(), ...)` — a byte[] allocated
        // and copied purely to be copied again into the float[]. Two allocations and two copies per
        // audio block, on the bridge thread, in a file whose own buffers are pooled because
        // "allocating a buffer per audio block would be exactly the mistake the plugin side is careful
        // to avoid". The remaining float[] stays: the handler is given the audio to keep, so it cannot
        // be a shared scratch. 2026-08-24.
        var floats = new float[payload.Length / sizeof(float)];
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(payload.Span)[..floats.Length].CopyTo(floats);
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

    /// <summary>A Hello told us which process this instance lives in. Instances sharing a process
    /// share a group id. A Hello with no process id (an older plugin) leaves the instance in its own
    /// group, which is exactly the behaviour there was before.</summary>
    private void NoteHostProcess(Instance instance, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < sizeof(int)) return;
        var processId = BinaryPrimitives.ReadInt32LittleEndian(payload);
        if (processId <= 0) return;
        lock (gate)
        {
            if (!hostGroups.TryGetValue(processId, out var group))
            {
                group = Guid.NewGuid();
                hostGroups[processId] = group;
            }
            instance.Group = group;
        }
    }

    /// <summary>How long since we last heard from this instance, or null if we never have.</summary>
    private TimeSpan? LastSeenAge(int hash)
    {
        lock (gate)
        {
            return instances.TryGetValue(hash, out var instance) ? DateTime.UtcNow - instance.LastSeenUtc : null;
        }
    }

    private static string Short(Guid id) => id.ToString("N")[..8];

    private static string Describe(IPAddress[] peers)
        => peers.Length == 1 ? $"{peers[0]} is" : $"{string.Join(", ", peers.Select(p => p.ToString()))} are";

    private Instance Touch(int hash, IPEndPoint from)
    {
        lock (gate)
        {
            if (!instances.TryGetValue(hash, out var instance))
            {
                // The wire carries a 32-bit id to keep the header small; the claim register is keyed
                // by Guid. Minting one here on first sight keeps both honest without a bigger header.
                var id = Guid.NewGuid();
                instance = new Instance { Id = id, Endpoint = from, Group = id };
                instances[hash] = instance;
            }
            instance.Endpoint = from;
            instance.LastSeenUtc = DateTime.UtcNow;
            SweepLocked();
            return instance;
        }
    }

    /// <summary>A plugin letting one person go, or all of them when it names nobody.</summary>
    private void ReleaseOne(int hash, IPAddress? peer)
    {
        lock (gate)
        {
            if (!instances.TryGetValue(hash, out var instance)) return;
            var released = peer is null ? instance.ClaimedPeers : Array.FindAll(instance.ClaimedPeers, p => p.Equals(peer));
            foreach (var one in released)
            {
                claims.Release(one, instance.Id);
                DropShared(instance.Id, one);
                Notable?.Invoke($"plugin {Short(instance.Id)} let {one} go - back on the speakers");
            }
            instance.ClaimedPeers = peer is null ? [] : Array.FindAll(instance.ClaimedPeers, p => !p.Equals(peer));
            // The raw copy is the fast-path comparison for the NEXT request; leaving a stale one here
            // would let a repeat of the same set slip past without being claimed again.
            instance.ClaimedRaw = [];
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
            DropShared(instance.Id);
            Notable?.Invoke($"plugin {Short(instance.Id)} closed cleanly"
                + (instance.ClaimedPeers.Length == 0 ? "" : $" - {Describe(instance.ClaimedPeers)} back on the speakers"));
        }
    }

    private void SweepLocked()
    {
        var cutoff = DateTime.UtcNow - PluginPeerClaims.ClaimTimeout;

        // Look before copying. This runs from Touch, which runs on EVERY message — so once per audio
        // block per instance — and `instances.ToList()` allocated each time, on the bridge thread that
        // feeds a DAW. In the steady state nothing is ever stale, so the copy existed purely to be
        // thrown away. The copy is still needed when something IS stale, because the loop removes from
        // the dictionary it is walking. 2026-08-24.
        var anyStale = false;
        foreach (var instance in instances.Values)
        {
            if (instance.LastSeenUtc > cutoff) continue;
            anyStale = true;
            break;
        }
        if (!anyStale) return;

        foreach (var (hash, instance) in instances.ToList())
        {
            if (instance.LastSeenUtc > cutoff) continue;
            instances.Remove(hash);
            claims.ReleaseAll(instance.Id);
            DropShared(instance.Id);
            // No goodbye: the DAW was killed, or crashed. Worth a line - it is the difference between
            // "the plugin misbehaved" and "the host died", which look identical from the outside.
            Notable?.Invoke($"plugin {Short(instance.Id)} went quiet and timed out (no goodbye - the DAW probably closed abruptly)"
                + (instance.ClaimedPeers.Length == 0 ? "" : $" - {Describe(instance.ClaimedPeers)} back on the speakers"));
        }
    }

    /// <summary>Expire anything that has gone quiet, without waiting for a message to arrive.
    ///
    /// <para>The app calls this every second. Sweeping only when a plugin talks to us meant that a
    /// DAW killed outright — the one case with no goodbye — left its instance in the list until
    /// something else happened to come in. Recovery then rode on whichever code path next asked
    /// whether a peer was claimed, which is why the peer sometimes came back and sometimes did not
    /// (Anthony Reyers, 2026-08-16: intermittent, "the worst possible bug"). A peer returning to the
    /// speakers must not depend on a race.</para></summary>
    public void Sweep()
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
            lock (sharedGate) { rounds.Clear(); members.Clear(); sharedStreams.Clear(); }
        }
    }
}
