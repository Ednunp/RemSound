using System.Diagnostics;
using System.Net;
using NAudio.Wave;
using RemSound.Core;
using RemSound.Receiver;
using RemSound.Sender;

namespace RemSound.App.Simulator;

/// <summary>What a peer sends: music-like content (three tones and a little noise, which keeps Opus as busy as real
/// material does), the analysable 1 kHz tone with its timing clicks, or silence.</summary>
internal enum PerfSignal { Busy, ToneAndClicks, Silence }

/// <summary>Which half of the path a run drives: the whole of it, the sending end alone (its packets go nowhere), or the
/// receiving end alone (fed a recording of a sending end's packets).</summary>
internal enum PerfPart { Whole, SenderAlone, ReceiverAlone }

/// <summary>Where the rig's product calls are counted: the sending end's capture blocks, the receiver taking each kind of
/// packet, a flood the test injects, the output cards' reads, the WASAPI output stage's producer, and the once-a-second
/// work the main window asks of the receiver.</summary>
internal enum PerfCost { Send, DeliverAudio, DeliverFormat, DeliverOther, Flood, Render, Producer, EverySecond }

/// <summary>One performance run's set-up. The cards and buffers are the simulator's usual ones: ASIO at 32 frames and a
/// 10 ms buffer, WASAPI at 10 ms blocks and a 40 ms buffer, the WASAPI output stage in the path as the app has it.</summary>
internal sealed record PerfRigOptions(SimConfig Config, SimCodec Codec)
{
    public SimNetwork Network { get; init; } = SimNetwork.Wired;
    public int AsioFrames { get; init; } = 32;
    /// <summary>The ASIO lane's buffer; 0 = what suits the way of sending (10 ms, or 20 for 10 ms Opus frames, which a
    /// 10 ms buffer cannot hold).</summary>
    public int AsioBufferMs { get; init; }
    public int WasapiBufferMs { get; init; } = 40;
    public bool WasapiOutputStage { get; init; } = true;
    /// <summary>The app's diagnostics switch (logs or an auto-tune on): its probes cost CPU of their own.</summary>
    public bool Diagnostics { get; init; }
    public int Seed { get; init; } = 1;
    public PerfPart Part { get; init; } = PerfPart.Whole;
    /// <summary>The rig's own work only: every card tick, block and delivery as in a real run, and not one call into
    /// RemSound - what the rig itself costs, to take off a real run's figure.</summary>
    public bool RigOnly { get; init; }
    /// <summary>Keep every packet delivered, for a receiver-alone or rig-only run to play back.</summary>
    public bool RecordTrace { get; init; }
    /// <summary>With <see cref="RecordTrace"/>: keep only when, from where and how big - all a rig-only run needs.</summary>
    public bool TraceShapeOnly { get; init; }
    /// <summary>Deliver these packets instead of sending any (receiver alone, or the rig's own cost).</summary>
    public PerfTrace? Replay { get; init; }
    public double SendDriftPpm { get; init; }
    /// <summary>Listen to the first output at its card even when the WASAPI output stage is in front of it (normally it is
    /// heard where the engine hands it to the stage - see <see cref="PerfRig.StartListening"/>).</summary>
    public bool ListenAtCard { get; init; }
    public double ReceiveDriftPpm { get; init; }
}

/// <summary>
/// THE PERFORMANCE RIG (2026-09-28; Ed: "a very light weight program that runs for days sometimes weeks at a time with
/// very little load on the computer ... using our simulator we should be able to perform some perf, soak and stress
/// tests"). The simulator's idea - the REAL AudioSender and AudioReceiver, fake sound cards and a fake network, one
/// thread, virtual time - built for measuring rather than listening:
/// <list type="bullet">
/// <item>any number of senders into one receiver, each a peer at its own address, joining and leaving at will (so four
/// and eight at once, and thousands of comings and goings);</item>
/// <item>nothing scheduled ahead: each card schedules its next tick when it fires, so a run of days holds a handful of
/// events, not billions;</item>
/// <item>the rig allocates nothing once it is running - blocks come from precomputed tables, packets travel in pooled
/// buffers, the event queue is an array of structs - so every byte allocated around a product call is RemSound's, and
/// no collection the rig causes lands in RemSound's CPU figure;</item>
/// <item>every call into RemSound is counted by kind (<see cref="PerfCost"/>): calls, bytes allocated, time.</item>
/// </list>
/// Everything runs on the thread that built the rig (the audio clock is per thread), which is the thread whose CPU the
/// benchmark reads. Nothing opens a device or a socket to the network: each sender's packets go to the rig through a
/// seam, and the addresses are the simulator's own 10.77.x.x.
/// </summary>
internal sealed class PerfRig : IDisposable
{
    public const int Rate = 48000;
    private static readonly IPEndPoint ReceiverAt = new(IPAddress.Parse("10.77.0.2"), 40999);

    /// <summary>A peer's address: 10.77.1.(identity + 1). A stranger (not ticked): 10.99.x.x.</summary>
    public static IPAddress IdentityAddress(int identity) => new([10, 77, 1, (byte)(identity % 250 + 1)]);

    /// <summary>How many identities the receiver's allow-list lets in - as the app always has one set.</summary>
    public const int AllowedIdentities = 64;

    public PerfRigOptions Options { get; }
    public AudioReceiver? Receiver { get; }

    /// <summary>The ASIO lane's buffer this run plays at.</summary>
    public int AsioBufferMs => Options.AsioBufferMs > 0 ? Options.AsioBufferMs : Options.Codec == SimCodec.Opus10 ? 20 : 10;

    // What the rig itself allocated and spent inside a call into RemSound (the sender hands its packets to the rig while
    // it is being fed): taken off that call's figures, so they are RemSound's alone.
    private long rigAllocatedInside;
    private long rigTicksInside;
    public PerfMeter Meter { get; } = new();
    public double Now => now;

    /// <summary>Packets the network lost, delivered, and every datagram the senders handed over.</summary>
    public long Lost { get; private set; }
    public long Delivered { get; private set; }
    public long Tapped { get; private set; }
    /// <summary>Events handled, by kind (the rig's own work is roughly proportional to these).</summary>
    public long[] EventsByKind { get; } = new long[8];

    // ---- clock ----
    private double now;
    private readonly long baseTicks;
    private readonly bool diagnosticsWere;

    // ---- event queue: a binary heap of structs, ordered by time and then by when it was queued ----
    private struct Ev
    {
        public double T;
        public long Seq;
        public int Kind;
        public int A;
        public int B;
    }
    private const int KSendCard = 1, KRecvCard = 2, KProducer = 3, KDeliver = 4, KSecond = 5, KTrace = 6;
    private Ev[] heap = new Ev[512];
    private int heapCount;
    private long seq;

    // ---- packets in flight, in pooled buffers ----
    private byte[][] packets = new byte[256][];
    private int[] packetLength = new int[256];
    private IPEndPoint?[] packetFrom = new IPEndPoint?[256];
    private int[] freePackets = new int[256];
    private int freeCount;
    private int packetSlots;

    // ---- peers ----
    private sealed class PeerSlot
    {
        public int Index;
        public int Identity;
        public int Generation;
        public bool Active;
        public PerfSignal Signal;
        public IPEndPoint From = null!;
        public AudioSender? Sender;
        public int Lanes;
        public readonly SimCard[] Cards = new SimCard[2];
        public readonly double[] Period = new double[2];
        public readonly double[] StartT = new double[2];
        public readonly double[] LastT = new double[2];
        public readonly long[] NextK = new long[2];
        public readonly long[] Captured = new long[2];
        public readonly float[][] Blocks = new float[2][];
        public Random CardRng = null!;
        public Random NetRng = null!;
        public double LastArrival;
        public double StallFrom, StallTo;
        public AudioSender.PacketSpanTap Tap = null!;
    }
    private readonly List<PeerSlot> slots = new();
    private readonly Dictionary<int, AudioSender> sendersByIdentity = new();
    private readonly HashSet<int> sendersUsed = new();
    private int nextPort = 50200;

    // ---- the receiving end's cards ----
    private sealed class Output
    {
        public RenderRoute Route;
        public SimCard Card = null!;
        public double Period;
        public IWaveProvider? Provider;
        public IWaveProvider? EngineSide;
        public MultiOutputPlayout.SimulatedStage? Stage;
        public byte[] ReadBuf = [];
        public byte[] ProducerFrame = [];
        public long NextK, ProducerK;
        public double LastT, ProducerLastT;
        public Random Rng = null!;
        public double StallFrom, StallTo;
        public bool StallCatchUp;
    }
    private readonly List<Output> outputs = new();

    // ---- the network ----
    /// <summary>The network packets cross now: change it part way through a run to flap it.</summary>
    public SimNetwork Network { get; set; }
    /// <summary>Every packet sent while this is set is lost.</summary>
    public bool NetworkDown { get; set; }
    private double holdUntil;

    // ---- recording and playing back ----
    private readonly PerfTrace? trace;
    private readonly PerfTrace? replay;
    private int[] replayOrder = [];

    // ---- listening to the first output ----
    private List<float>? heard;
    private List<(int Frame, double Time)>? heardTimes;

    private readonly Func<RenderRoute, int> renderPeriodFor;
    private readonly int asioCallbackMs;

    public PerfRig(PerfRigOptions options)
    {
        Options = options;
        Network = options.Network;
        asioCallbackMs = (int)Math.Ceiling(options.AsioFrames * 1000.0 / Rate);
        renderPeriodFor = r => r == RenderRoute.AsioLane ? asioCallbackMs : 10;
        baseTicks = Stopwatch.GetTimestamp();
        AudioClock.UseSimulated(() => baseTicks + (long)(now * Stopwatch.Frequency));
        diagnosticsWere = DiagnosticsGate.Enabled;
        DiagnosticsGate.Enabled = options.Diagnostics;
        JamulusStyle.Reset();
        SessionPlayout.ConcealSeedForTest = options.Seed;
        if (options.RecordTrace) trace = new PerfTrace(options.TraceShapeOnly);
        replay = options.Replay;

        var product = !options.RigOnly;
        if (options.Part != PerfPart.SenderAlone)
        {
            if (product)
            {
                var receiver = new AudioReceiver();
                Receiver = receiver;
                var (key, fingerprint) = RemSoundCrypto.ForPlainPassword("simulator");
                receiver.SetOutputDevices([]);
                receiver.AudioKey = key;
                receiver.AudioFingerprint = fingerprint;
                receiver.SetActiveOutputLanes(wasapiActive: options.Config != SimConfig.AsioOnly, asioActive: options.Config != SimConfig.WasapiOnly);
                receiver.SetIndependentLaneLatency(options.Config != SimConfig.WasapiOnly);
                if (options.Config == SimConfig.WasapiOnly) receiver.MaxLatencyMs = options.WasapiBufferMs;
                else
                {
                    receiver.SetMaxLatencyMsFor(RenderRoute.AsioLane, AsioBufferMs);
                    receiver.SetMaxLatencyMsFor(RenderRoute.WasapiLane, options.WasapiBufferMs);
                }
                // The app always hands over who is ticked; strangers are turned away at the door.
                receiver.SetAllowedSenders(Enumerable.Range(0, AllowedIdentities).Select(i => new IPEndPoint(IdentityAddress(i), 0)));
                receiver.SetPlaybackEnabled(true);
            }
            // The cards: the output under test first (the one listened to), then in "both" the WASAPI output beside it.
            var asio = new SimCard(options.AsioFrames, JitterMs: 0.05, DriftPpm: options.ReceiveDriftPpm);
            var wasapi = new SimCard(480, JitterMs: 1, DriftPpm: options.ReceiveDriftPpm);
            switch (options.Config)
            {
                case SimConfig.WasapiOnly: AddOutput(RenderRoute.Mixed, wasapi, stage: options.WasapiOutputStage); break;
                case SimConfig.AsioOnly: AddOutput(RenderRoute.AsioLane, asio, stage: false); break;
                default:
                    AddOutput(RenderRoute.AsioLane, asio, stage: false);
                    AddOutput(RenderRoute.WasapiLane, wasapi, stage: options.WasapiOutputStage);
                    break;
            }
            Push(1, KSecond, 1, 0);
        }
        if (replay is not null)
        {
            replayOrder = replay.Order();
            if (replayOrder.Length > 0) Push(replay.At(replayOrder[0]), KTrace, 0, 0);
        }
    }

    private void AddOutput(RenderRoute route, SimCard card, bool stage)
    {
        var o = new Output
        {
            Route = route,
            Card = card,
            Period = card.Frames / (Rate * (1 + card.DriftPpm * 1e-6)),
            ReadBuf = new byte[card.Frames * 8],
            Rng = new Random(Options.Seed * 31 + outputs.Count + 7),
        };
        if (Receiver is { } receiver)
        {
            o.EngineSide = receiver.SimulationOutput(route);
            if (stage)
            {
                o.Stage = new MultiOutputPlayout.SimulatedStage($"simulated {route} card");
                o.Provider = o.Stage;
                o.ProducerFrame = new byte[MultiOutputPlayout.SimulatedStage.ProducerFrameBytes];
            }
            else o.Provider = o.EngineSide;
        }
        else if (stage) o.ProducerFrame = new byte[MultiOutputPlayout.SimulatedStage.ProducerFrameBytes];
        outputs.Add(o);
        var index = outputs.Count - 1;
        o.NextK = 1;
        Push(NextTime(o.Card, o.Period, 0, 1, ref o.LastT, o.Rng), KRecvCard, index, 0);
        if (o.ProducerFrame.Length > 0)
        {
            o.ProducerK = 1;
            Push(NextTime(ProducerCard, MultiOutputPlayout.SimulatedStage.ProducerPeriodSec, 0, 1, ref o.ProducerLastT, o.Rng), KProducer, index, 0);
        }
    }

    private static readonly SimCard ProducerCard = new(480, JitterMs: 1);

    // =====================================================================================================================
    // Peers
    // =====================================================================================================================

    /// <summary>A peer starts sending: identity <paramref name="identity"/> (its address), from a new source port - a new
    /// stream, as a peer's RemSound starting to send is. The same identity's sender is kept and restarted, as that peer's
    /// program would restart its lanes. Returns the peer's slot, for <see cref="Leave"/>. The way of sending and the
    /// password are the identity's own, given at its first join (the rig's, and the simulator's password, unless said).</summary>
    public int Join(int identity, PerfSignal signal, SimCodec? codec = null, string password = "simulator")
    {
        if (slots.Any(s => s.Active && s.Identity == identity))
            throw new InvalidOperationException($"peer {identity} is already sending - one RemSound, one stream at a time");
        var slot = slots.FirstOrDefault(s => !s.Active && s.Sender is null && s.Generation > 0) ?? NewSlot();
        slot.Identity = identity;
        slot.Generation++;
        slot.Signal = signal;
        slot.From = new IPEndPoint(IdentityAddress(identity), nextPort);
        nextPort = nextPort >= 64000 ? 50200 : nextPort + 1;
        slot.LastArrival = 0;
        slot.StallFrom = slot.StallTo = 0;
        slot.CardRng = new Random(Options.Seed * 7919 + slot.Index * 104729 + slot.Generation);
        slot.NetRng = new Random(Options.Seed * 15485863 + slot.Index * 32452843 + slot.Generation);
        var asioSide = Options.Config != SimConfig.WasapiOnly;
        slot.Lanes = Options.Config == SimConfig.Both ? 2 : 1;
        slot.Cards[0] = asioSide ? new SimCard(Options.AsioFrames, JitterMs: 0.05, DriftPpm: Options.SendDriftPpm) : new SimCard(480, JitterMs: 1, DriftPpm: Options.SendDriftPpm);
        slot.Cards[1] = new SimCard(480, JitterMs: 1, DriftPpm: Options.SendDriftPpm);
        for (var lane = 0; lane < slot.Lanes; lane++)
        {
            slot.Period[lane] = slot.Cards[lane].Frames / (Rate * (1 + slot.Cards[lane].DriftPpm * 1e-6));
            if (slot.Blocks[lane] is null || slot.Blocks[lane].Length != slot.Cards[lane].Frames * 2) slot.Blocks[lane] = new float[slot.Cards[lane].Frames * 2];
            slot.StartT[lane] = now;
            slot.LastT[lane] = now;
            slot.NextK[lane] = 1;
            slot.Captured[lane] = 0;
        }
        if (Options.Part != PerfPart.ReceiverAlone && !Options.RigOnly)
        {
            if (!sendersByIdentity.TryGetValue(identity, out var sender))
            {
                sender = NewSender(codec ?? Options.Codec, password);
                sendersByIdentity[identity] = sender;
            }
            else if (sendersUsed.Contains(identity)) sender.RestartLanesForTest();
            sendersUsed.Add(identity);
            sender.PacketSpanTapForTest = slot.Tap;
            slot.Sender = sender;
        }
        slot.Active = true;
        for (var lane = 0; lane < slot.Lanes; lane++)
            Push(NextTime(slot.Cards[lane], slot.Period[lane], slot.StartT[lane], 1, ref slot.LastT[lane], slot.CardRng), KSendCard, slot.Index, slot.Generation * 2 + lane);
        return slot.Index;
    }

    /// <summary>The peer stops sending - its cards stop, and the receiver hears nothing more from that stream. With
    /// <paramref name="forget"/>, that peer's program closes too: its sender is disposed and a later join builds a new one.</summary>
    public void Leave(int slotIndex, bool forget = false)
    {
        var slot = slots[slotIndex];
        if (!slot.Active) return;
        slot.Active = false;
        if (slot.Sender is { } sender && ReferenceEquals(sender.PacketSpanTapForTest, slot.Tap)) sender.PacketSpanTapForTest = null;
        slot.Sender = null;
        if (forget && sendersByIdentity.Remove(slot.Identity, out var gone))
        {
            sendersUsed.Remove(slot.Identity);
            gone.PacketSpanTapForTest = null;
            gone.Dispose();
        }
    }

    /// <summary>The peers sending now.</summary>
    public int ActivePeers => slots.Count(s => s.Active);

    /// <summary>The senders the rig has built, one per identity (kept across a peer's comings and goings).</summary>
    public IEnumerable<AudioSender> Senders => sendersByIdentity.Values;

    private PeerSlot NewSlot()
    {
        var slot = new PeerSlot { Index = slots.Count };
        slot.Tap = (datagram, _) => OnPacket(slot, datagram);
        slots.Add(slot);
        return slot;
    }

    private AudioSender NewSender(SimCodec codec, string password)
    {
        var sender = new AudioSender();
        var (key, fingerprint) = RemSoundCrypto.ForPlainPassword(password);
        sender.SetReceivers([ReceiverAt]);
        sender.AudioKey = key;
        sender.AudioFingerprint = fingerprint;
        var asioSide = Options.Config != SimConfig.WasapiOnly;
        Simulation.ConfigureCodec(sender, codec);
        if (asioSide) sender.UseAsioLaneForTest();
        sender.UseDefaultLaneForTest(asioSide ? RenderRoute.WasapiLane : RenderRoute.Mixed);
        sender.WasapiCaptureLatencyMsForTest = DeviceLatency.CaptureMs(480 * 1000.0 / Rate, CaptureSource.CaptureBufferMs);
        sender.AsioCaptureLatencyMsForTest = asioSide ? Options.AsioFrames * 1000.0 / Rate : 0;
        return sender;
    }

    /// <summary>Every sending card stops for <paramref name="seconds"/> (the capture device went away and came back):
    /// the blocks it would have delivered are never captured.</summary>
    public void StallSenders(double seconds)
    {
        foreach (var slot in slots.Where(s => s.Active))
        {
            slot.StallFrom = now;
            slot.StallTo = now + seconds;
        }
    }

    // =====================================================================================================================
    // The network and the output cards, disturbed
    // =====================================================================================================================

    /// <summary>The network holds everything sent from now until <paramref name="seconds"/> from now, and delivers the
    /// lot at the end (a stall, as a Wi-Fi scan or a router's hiccup makes one).</summary>
    public void HoldNetwork(double seconds) => holdUntil = now + seconds;

    /// <summary>Every output card stops for <paramref name="seconds"/>: with <paramref name="catchUp"/> its driver thread
    /// is late and fires every missed callback at once when it comes back; without, the device stopped and the missed
    /// callbacks are gone.</summary>
    public void StallOutputs(double seconds, bool catchUp)
    {
        foreach (var o in outputs)
        {
            o.StallFrom = now;
            o.StallTo = now + seconds;
            o.StallCatchUp = catchUp;
        }
    }

    /// <summary>A packet from anywhere, handed to the receiver now, as a flood would arrive.</summary>
    public void Inject(ReadOnlySpan<byte> packet, IPEndPoint from)
    {
        if (Receiver is not { } receiver) return;
        var index = TakePacketSlot();
        var buffer = packets[index];
        if (buffer.Length < packet.Length) packets[index] = buffer = new byte[Math.Max(2048, packet.Length)];
        packet.CopyTo(buffer);
        var a0 = GC.GetAllocatedBytesForCurrentThread();
        var t0 = Stopwatch.GetTimestamp();
        receiver.InjectExternalPacket(buffer, packet.Length, from);
        Meter.Add(PerfCost.Flood, Stopwatch.GetTimestamp() - t0, GC.GetAllocatedBytesForCurrentThread() - a0);
        ReleasePacketSlot(index);
    }

    // =====================================================================================================================
    // Running
    // =====================================================================================================================

    /// <summary>Run the simulation up to <paramref name="t"/> seconds.</summary>
    public void RunUntil(double t)
    {
        while (heapCount > 0 && heap[0].T <= t)
        {
            var e = Pop();
            now = e.T;
            EventsByKind[e.Kind]++;
            switch (e.Kind)
            {
                case KSendCard: SendCard(e.A, e.B); break;
                case KRecvCard: RecvCard(e.A); break;
                case KProducer: Produce(e.A); break;
                case KDeliver: DeliverPacket(e.A); break;
                case KSecond: EverySecond(e.A); break;
                case KTrace: ReplayNext(e.A); break;
            }
        }
        now = Math.Max(now, t);
    }

    public void RunFor(double seconds) => RunUntil(now + seconds);

    private void SendCard(int slotIndex, int tag)
    {
        var slot = slots[slotIndex];
        var lane = tag & 1;
        if (!slot.Active || slot.Generation != tag >> 1) return;   // a peer that has left: its card stopped
        var k = ++slot.NextK[lane];
        Push(NextTime(slot.Cards[lane], slot.Period[lane], slot.StartT[lane], k, ref slot.LastT[lane], slot.CardRng), KSendCard, slotIndex, tag);
        if (now >= slot.StallFrom && now < slot.StallTo) return;
        var block = slot.Blocks[lane];
        Fill(slot, lane, block);
        if (slot.Sender is not { } sender) return;
        rigAllocatedInside = 0;
        rigTicksInside = 0;
        var a0 = GC.GetAllocatedBytesForCurrentThread();
        var t0 = Stopwatch.GetTimestamp();
        var asioLane = Options.Config != SimConfig.WasapiOnly && lane == 0;
        if (asioLane) sender.FeedAsioLaneForTest(block);
        else sender.FeedDefaultLaneForTest(block);
        Meter.Add(PerfCost.Send, Stopwatch.GetTimestamp() - t0 - rigTicksInside, GC.GetAllocatedBytesForCurrentThread() - a0 - rigAllocatedInside);
    }

    private void Fill(PeerSlot slot, int lane, float[] block)
    {
        var frames = block.Length / 2;
        var n = slot.Captured[lane];
        slot.Captured[lane] = n + frames;
        // In "both" the tone and clicks go on the ASIO lane and the WASAPI lane carries silence: every output plays every
        // stream, and the same tone on both lanes would be heard twice and comb-filter.
        var signal = slot.Signal == PerfSignal.ToneAndClicks && lane == 1 ? PerfSignal.Silence : slot.Signal;
        switch (signal)
        {
            case PerfSignal.Silence:
                Array.Clear(block);
                break;
            case PerfSignal.ToneAndClicks:
                for (var i = 0; i < frames; i++)
                {
                    var f = n + i;
                    block[2 * i] = ToneTable[(int)(f % ToneTable.Length)];
                    block[2 * i + 1] = f % ClickEveryFrames == 0 ? Simulation.ClickLevel : 0f;
                }
                break;
            default:
            {
                // Each peer and lane plays the loop from a place of its own, so no two streams are the same sound.
                var loop = BusyLoop;
                var loopFrames = loop.Length / 2;
                var at = (int)((n + slot.Identity * 7919L + lane * 20011L) % loopFrames);
                var done = 0;
                while (done < frames)
                {
                    var run = Math.Min(frames - done, loopFrames - at);
                    Array.Copy(loop, at * 2, block, done * 2, run * 2);
                    done += run;
                    at = 0;
                }
                break;
            }
        }
    }

    private void OnPacket(PeerSlot slot, ReadOnlySpan<byte> datagram)
    {
        var a0 = GC.GetAllocatedBytesForCurrentThread();
        var t0 = Stopwatch.GetTimestamp();
        CarryPacket(slot, datagram);
        rigTicksInside += Stopwatch.GetTimestamp() - t0;
        rigAllocatedInside += GC.GetAllocatedBytesForCurrentThread() - a0;
    }

    private void CarryPacket(PeerSlot slot, ReadOnlySpan<byte> datagram)
    {
        Tapped++;
        if (Options.Part == PerfPart.SenderAlone && trace is null) return;
        var net = Network;
        var rng = slot.NetRng;
        if (NetworkDown || rng.NextDouble() * 100 < net.LossPercent) { Lost++; return; }
        // Networks keep order; now and then a packet is overtaken by the next (as the simulator has it).
        var arrival = Math.Max(Simulation.Arrival(net, now, rng), slot.LastArrival);
        if (rng.NextDouble() * 100 < net.ReorderPercent) arrival += 0.003;
        else slot.LastArrival = arrival;
        if (arrival < holdUntil) arrival = holdUntil;
        Schedule(arrival, datagram, slot.From);
        if (rng.NextDouble() * 100 < net.DuplicatePercent) Schedule(arrival + 0.0005, datagram, slot.From);
    }

    private void Schedule(double arrival, ReadOnlySpan<byte> datagram, IPEndPoint from)
    {
        trace?.Add(arrival, from, datagram);
        if (Options.Part == PerfPart.SenderAlone) return;
        var index = TakePacketSlot();
        var buffer = packets[index];
        if (buffer.Length < datagram.Length) packets[index] = buffer = new byte[Math.Max(2048, datagram.Length)];
        datagram.CopyTo(buffer);
        packetLength[index] = datagram.Length;
        packetFrom[index] = from;
        Push(arrival, KDeliver, index, 0);
    }

    private void DeliverPacket(int index)
    {
        var buffer = packets[index];
        var length = packetLength[index];
        var from = packetFrom[index]!;
        Delivered++;
        if (Receiver is { } receiver)
        {
            var cost = PerfCost.DeliverOther;
            if (RemPacket.TryReadHeader(buffer.AsSpan(0, length), out var type, out _, out _))
                cost = type switch { RemPacketType.Audio => PerfCost.DeliverAudio, RemPacketType.Format => PerfCost.DeliverFormat, _ => PerfCost.DeliverOther };
            var a0 = GC.GetAllocatedBytesForCurrentThread();
            var t0 = Stopwatch.GetTimestamp();
            receiver.InjectExternalPacket(buffer, length, from);
            Meter.Add(cost, Stopwatch.GetTimestamp() - t0, GC.GetAllocatedBytesForCurrentThread() - a0);
        }
        packetFrom[index] = null;
        ReleasePacketSlot(index);
    }

    private void ReplayNext(int position)
    {
        var source = replay!;
        var i = replayOrder[position];
        var bytes = source.Bytes(i);
        var index = TakePacketSlot();
        var buffer = packets[index];
        if (buffer.Length < bytes.Length) packets[index] = buffer = new byte[Math.Max(2048, bytes.Length)];
        bytes.CopyTo(buffer);
        packetLength[index] = bytes.Length;
        packetFrom[index] = source.From(i);
        DeliverPacket(index);
        if (position + 1 < replayOrder.Length) Push(source.At(replayOrder[position + 1]), KTrace, position + 1, 0);
    }

    private void RecvCard(int index)
    {
        var o = outputs[index];
        if (now >= o.StallFrom && now < o.StallTo)
        {
            // Late driver thread: this callback fires when the stall ends (and so does every one after it, at once).
            // Device stopped: this callback never happens.
            if (o.StallCatchUp)
            {
                o.LastT = Math.Max(o.LastT, o.StallTo);
                Push(o.StallTo, KRecvCard, index, 0);
                return;
            }
            Push(NextTime(o.Card, o.Period, 0, ++o.NextK, ref o.LastT, o.Rng), KRecvCard, index, 0);
            return;
        }
        Push(NextTime(o.Card, o.Period, 0, ++o.NextK, ref o.LastT, o.Rng), KRecvCard, index, 0);
        var buffer = o.ReadBuf;
        Array.Clear(buffer);
        if (o.Provider is { } provider)
        {
            var a0 = GC.GetAllocatedBytesForCurrentThread();
            var t0 = Stopwatch.GetTimestamp();
            provider.Read(buffer, 0, buffer.Length);
            Meter.Add(PerfCost.Render, Stopwatch.GetTimestamp() - t0, GC.GetAllocatedBytesForCurrentThread() - a0);
        }
        // The first output is listened to here - unless the WASAPI output stage is in front of its card, which is then
        // listened to where the engine hands it over (in Produce): the stage had a click finding of its own (see
        // SimulatorHoldsOvernight; fixed 2026-09-28, and SIMULATOR WASAPI OUTPUT STAGE listens to it at the card), which is not
        // what a performance run is listening for.
        if (index == 0 && (o.Stage is null || Options.ListenAtCard) && heard is { } list)
        {
            heardTimes!.Add((list.Count / 2, now + o.Period));
            list.AddRange((ReadOnlySpan<float>)System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer.AsSpan()));
        }
    }

    private void Produce(int index)
    {
        var o = outputs[index];
        Push(NextTime(ProducerCard, MultiOutputPlayout.SimulatedStage.ProducerPeriodSec, 0, ++o.ProducerK, ref o.ProducerLastT, o.Rng), KProducer, index, 0);
        if (o.Stage is not { } stage || o.EngineSide is not { } engine) return;
        var a0 = GC.GetAllocatedBytesForCurrentThread();
        var t0 = Stopwatch.GetTimestamp();
        var produced = engine.Read(o.ProducerFrame, 0, o.ProducerFrame.Length);
        if (produced > 0) stage.Feed(o.ProducerFrame, 0, produced);
        Meter.Add(PerfCost.Producer, Stopwatch.GetTimestamp() - t0, GC.GetAllocatedBytesForCurrentThread() - a0);
        if (index == 0 && !Options.ListenAtCard && heard is { } list && produced > 0)
        {
            heardTimes!.Add((list.Count / 2, now));
            list.AddRange((ReadOnlySpan<float>)System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(o.ProducerFrame.AsSpan(0, produced)));
        }
    }

    /// <summary>The main window's once-a-second calls into the receiver: the per-stream sizing, the idle prune, and the
    /// diagnostics snapshot.</summary>
    private void EverySecond(int k)
    {
        Push(k + 1, KSecond, k + 1, 0);
        if (Receiver is not { } receiver) return;
        var a0 = GC.GetAllocatedBytesForCurrentThread();
        var t0 = Stopwatch.GetTimestamp();
        receiver.UpdatePerStreamNeeds(renderPeriodFor, _ => true);   // the most work it can be
        receiver.PruneIdleSessions();
        receiver.TakeDiagnosticsSnapshot();
        Meter.Add(PerfCost.EverySecond, Stopwatch.GetTimestamp() - t0, GC.GetAllocatedBytesForCurrentThread() - a0);
    }

    /// <summary>When a card's k-th callback comes: k periods after it started, up to its jitter late, never before the one
    /// before it.</summary>
    private static double NextTime(SimCard card, double period, double start, long k, ref double last, Random rng)
    {
        var t = start + k * period + (card.JitterMs > 0 ? rng.NextDouble() * card.JitterMs / 1000 : 0);
        t = Math.Max(t, last);
        last = t;
        return t;
    }

    // =====================================================================================================================
    // Listening (the first output: the ASIO one in "both")
    // =====================================================================================================================

    /// <summary>Start keeping what the first output plays (for up to <paramref name="seconds"/>) - where the engine hands it
    /// over: at its card, or with the WASAPI output stage in front of the card, as the engine hands it to the stage (the stage
    /// had a click finding of its own, SimulatorHoldsOvernight's, fixed 2026-09-28 - SIMULATOR WASAPI OUTPUT STAGE listens to
    /// it at the card; a performance run is not listening for it; unless
    /// <see cref="PerfRigOptions.ListenAtCard"/>).</summary>
    public void StartListening(double seconds)
    {
        heard = new List<float>((int)(seconds * Rate * 2) + 4096);
        heardTimes = new List<(int Frame, double Time)>((int)(seconds * Rate / 32) + 64);
    }

    /// <summary>Stop keeping it, and hear what was kept: break-ups, clicks, pitch, noise, and the delay.</summary>
    public SimAnalysis.Findings StopListening()
    {
        var list = heard ?? [];
        var times = heardTimes ?? [];
        heard = null;
        heardTimes = null;
        return SimAnalysis.Analyse(list, times, Rate);
    }

    // =====================================================================================================================
    // The event queue and the packet pool
    // =====================================================================================================================

    private void Push(double t, int kind, int a, int b)
    {
        if (heapCount == heap.Length) Array.Resize(ref heap, heap.Length * 2);
        var e = new Ev { T = t, Seq = seq++, Kind = kind, A = a, B = b };
        var i = heapCount++;
        while (i > 0)
        {
            var parent = (i - 1) >> 1;
            if (!Earlier(e, heap[parent])) break;
            heap[i] = heap[parent];
            i = parent;
        }
        heap[i] = e;
    }

    private Ev Pop()
    {
        var top = heap[0];
        var last = heap[--heapCount];
        var i = 0;
        while (true)
        {
            var child = 2 * i + 1;
            if (child >= heapCount) break;
            if (child + 1 < heapCount && Earlier(heap[child + 1], heap[child])) child++;
            if (!Earlier(heap[child], last)) break;
            heap[i] = heap[child];
            i = child;
        }
        if (heapCount > 0) heap[i] = last;
        return top;
    }

    private static bool Earlier(in Ev x, in Ev y) => x.T < y.T || (x.T == y.T && x.Seq < y.Seq);

    private int TakePacketSlot()
    {
        if (freeCount > 0) return freePackets[--freeCount];
        if (packetSlots == packets.Length)
        {
            Array.Resize(ref packets, packets.Length * 2);
            Array.Resize(ref packetLength, packetLength.Length * 2);
            Array.Resize(ref packetFrom, packetFrom.Length * 2);
            Array.Resize(ref freePackets, freePackets.Length * 2);
        }
        packets[packetSlots] = new byte[2048];
        return packetSlots++;
    }

    private void ReleasePacketSlot(int index) => freePackets[freeCount++] = index;

    /// <summary>The packets delivered so far, when <see cref="PerfRigOptions.RecordTrace"/> is set.</summary>
    public PerfTrace? Trace => trace;

    /// <summary>How many packet buffers and queued events the rig holds right now (both stay small however long it runs).</summary>
    public (int PacketBuffers, int QueuedEvents) RigSize => (packetSlots, heapCount);

    /// <summary>What the rig itself keeps on the heap and never gives back while it lives: its pooled packet buffers and its
    /// event queue (both grow to the most a run has needed at once) - taken off a stress run's heap figure.</summary>
    public long RetainedBytes => packets.Sum(p => (long)(p?.Length ?? 0)) + heap.Length * 32L + packets.Length * 24L;

    /// <summary>The slots of the peers sending now (for a stress run to send them away).</summary>
    public IReadOnlyList<int> ActiveSlots => slots.Where(s => s.Active).Select(s => s.Index).ToList();

    /// <summary>The slot's peer's identity.</summary>
    public int IdentityOf(int slotIndex) => slots[slotIndex].Identity;

    public void Dispose()
    {
        foreach (var sender in sendersByIdentity.Values)
        {
            sender.PacketSpanTapForTest = null;
            try { sender.Dispose(); } catch { /* teardown */ }
        }
        sendersByIdentity.Clear();
        try { Receiver?.Dispose(); } catch { /* teardown */ }
        SessionPlayout.ConcealSeedForTest = null;
        AudioClock.UseSimulated(null);
        DiagnosticsGate.Enabled = diagnosticsWere;
        JamulusStyle.Reset();
    }

    // =====================================================================================================================
    // The signals
    // =====================================================================================================================

    private const int ClickEveryFrames = (int)(Simulation.ClickEverySec * Rate);

    /// <summary>The 1 kHz tone at half scale: exactly 48 samples a cycle at 48 kHz, so one cycle is the whole table.</summary>
    private static readonly float[] ToneTable = Enumerable.Range(0, 48)
        .Select(n => (float)(Simulation.ToneLevel * Math.Sin(2 * Math.PI * Simulation.ToneHz * n / Rate))).ToArray();

    /// <summary>One second of music-like sound, looped: whole-cycle tones on each side and a little noise.</summary>
    private static readonly float[] BusyLoop = MakeBusyLoop();

    private static float[] MakeBusyLoop()
    {
        var rng = new Random(20260928);
        var loop = new float[Rate * 2];
        for (var n = 0; n < Rate; n++)
        {
            var t = n / (double)Rate;
            loop[2 * n] = (float)(0.25 * Math.Sin(2 * Math.PI * 220 * t) + 0.12 * Math.Sin(2 * Math.PI * 1234 * t) + (rng.NextDouble() - 0.5) * 0.06);
            loop[2 * n + 1] = (float)(0.2 * Math.Sin(2 * Math.PI * 330 * t) + 0.1 * Math.Sin(2 * Math.PI * 3100 * t) + (rng.NextDouble() - 0.5) * 0.06);
        }
        return loop;
    }
}

/// <summary>What every call of each kind cost, since the last <see cref="Reset"/>: how many, the bytes allocated, and
/// the time spent (Stopwatch ticks, wall time inside the call).</summary>
internal sealed class PerfMeter
{
    private static readonly int Kinds = Enum.GetValues<PerfCost>().Length;
    private readonly long[] calls = new long[Kinds];
    private readonly long[] allocated = new long[Kinds];
    private readonly long[] ticks = new long[Kinds];

    /// <summary>Told of every call that allocated anything (kind, bytes) - for finding where an allocation happens.</summary>
    public Action<PerfCost, long>? OnAllocated { get; set; }

    private readonly long[] allocating = new long[Kinds];

    public void Add(PerfCost kind, long elapsedTicks, long allocatedBytes)
    {
        var i = (int)kind;
        if (allocatedBytes > 0)
        {
            allocating[i]++;
            OnAllocated?.Invoke(kind, allocatedBytes);
        }
        calls[i]++;
        ticks[i] = ticks[i] + elapsedTicks;
        allocated[i] = allocated[i] + allocatedBytes;
    }

    public void Reset()
    {
        Array.Clear(calls);
        Array.Clear(allocated);
        Array.Clear(ticks);
        Array.Clear(allocating);
    }

    /// <summary>How many calls of this kind allocated anything at all.</summary>
    public long AllocatingCalls(PerfCost kind) => allocating[(int)kind];

    public long Calls(PerfCost kind) => calls[(int)kind];
    public long Allocated(PerfCost kind) => allocated[(int)kind];
    public double Seconds(PerfCost kind) => ticks[(int)kind] / (double)Stopwatch.Frequency;

    public long Allocated(params PerfCost[] kinds) => kinds.Sum(k => allocated[(int)k]);
    public long Calls(params PerfCost[] kinds) => kinds.Sum(k => calls[(int)k]);
}

/// <summary>Packets as a run delivered them - when, from where, and the bytes - to play into a receiver on its own.</summary>
internal sealed class PerfTrace(bool shapeOnly = false)
{
    private static readonly byte[] Zeros = new byte[2048];
    private double[] at = new double[4096];
    private IPEndPoint[] from = new IPEndPoint[4096];
    private int[] offset = new int[4096];
    private int[] length = new int[4096];
    private byte[] arena = shapeOnly ? [] : new byte[1 << 20];
    private int used;

    public int Count { get; private set; }
    public long ArenaBytes => used;

    public void Add(double time, IPEndPoint source, ReadOnlySpan<byte> bytes)
    {
        if (Count == at.Length)
        {
            Array.Resize(ref at, at.Length * 2);
            Array.Resize(ref from, from.Length * 2);
            Array.Resize(ref offset, offset.Length * 2);
            Array.Resize(ref length, length.Length * 2);
        }
        at[Count] = time;
        from[Count] = source;
        length[Count] = bytes.Length;
        if (!shapeOnly)
        {
            if (used + bytes.Length > arena.Length) Array.Resize(ref arena, Math.Max(arena.Length * 2, used + bytes.Length));
            bytes.CopyTo(arena.AsSpan(used));
            offset[Count] = used;
            used += bytes.Length;
        }
        Count++;
    }

    public double At(int i) => at[i];
    public IPEndPoint From(int i) => from[i];
    /// <summary>The packet's bytes (zeros of its length, for a trace that kept only the shape).</summary>
    public ReadOnlySpan<byte> Bytes(int i) => shapeOnly ? Zeros.AsSpan(0, length[i]) : arena.AsSpan(offset[i], length[i]);

    /// <summary>The packets in the order they arrive (time, then the order they were sent).</summary>
    public int[] Order()
    {
        var order = Enumerable.Range(0, Count).ToArray();
        Array.Sort(order, (x, y) => at[x] != at[y] ? at[x].CompareTo(at[y]) : x.CompareTo(y));
        return order;
    }
}
