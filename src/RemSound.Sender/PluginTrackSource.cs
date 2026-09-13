using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Dsp;
using RemSound.Core;

namespace RemSound.Sender;

/// <summary>
/// The DAW side of the send path: turns blocks arriving on the plugin bridge into one continuous
/// stream on <see cref="AudioSender"/>'s plugin lane.
///
/// <para><b>Why this exists at all.</b> The app's own capture sources go through
/// <see cref="MixingEngine"/>, which runs a free-running 10 ms Stopwatch tick and resamples every
/// source onto it. A DAW track does not need any of that and must not have it: the block arrives
/// already at 48 kHz stereo (the plugin resamples at its own boundary), and the DAW's audio clock is
/// the only clock on the path. Feeding it through the mixer would introduce a second clock for it to
/// drift against, and cost a tick of latency to do so. So it goes straight to its own lane.</para>
///
/// <para><b>One DAW is the whole point, and it is the free case.</b> With a single host connected —
/// what all but a handful of sessions will ever be — no ring is touched, no resampler runs, and the
/// block is handed to the lane exactly as it arrived. Zero drift, lowest latency, nothing to
/// configure.</para>
///
/// <para><b>Two or more DAWs.</b> Blocks from two hosts cannot simply be concatenated: they are two
/// independent clocks arriving on their own cadences. So the first host to arrive DRIVES — its block
/// sets the cadence — and every other host's audio goes into a ring which the driver's block pulls
/// from, drift-corrected onto the driver's clock. If the driver disappears, the next host by arrival
/// order takes over.</para>
///
/// <para><b>Threading.</b> Every block arrives on the plugin bridge's single receive thread (see
/// <c>PluginBridgeLink</c>), which is what satisfies <see cref="SenderLane"/>'s one-producer-thread
/// contract — the lane is fed from here and from nowhere else. <see cref="Sweep"/> runs on the app's
/// UI timer thread and is the only other toucher of the host table, hence the lock. There is no
/// audio thread in this process at all on this path: the DAW's audio thread lives in the DAW's
/// process, on the far side of a socket.</para>
///
/// <para><b>Instances within one DAW never reach here separately.</b> They are summed inside the
/// plugin, on the DAW's own audio thread, where the blocks are already sample-aligned — see
/// <c>PluginSendBus</c>. One connected DAW is therefore one host here, however many tracks it has.</para>
/// </summary>
public sealed class PluginTrackSource : IDisposable
{
    private const int Channels = PluginBridgeProtocol.WireChannels;
    private const int SampleRate = PluginBridgeProtocol.WireSampleRate;
    private const int MaxFramesPerBlock = PluginBridgeProtocol.MaxAudioBytes / (Channels * sizeof(float));

    /// <summary>How long a host may go without delivering a block before we forget it.
    ///
    /// <para>Not <see cref="PluginPeerClaims.ClaimTimeout"/>'s five seconds: a sending plugin
    /// delivers a block per audio callback, hundreds a second, and keeps doing so with the transport
    /// stopped because the host keeps calling it. Two seconds of nothing means the DAW closed, the
    /// plugin was bypassed, or the track was removed. Long enough that a momentary scheduler stall
    /// cannot disarm the lane, short enough that a killed DAW does not hold it for a noticeable
    /// time.</para></summary>
    public static readonly TimeSpan HostIdleTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How long the DRIVING host may go quiet before another host that is still delivering
    /// takes the clock, without waiting for the sweep.
    ///
    /// <para>Much shorter than <see cref="HostIdleTimeout"/> on purpose. A non-driving host's audio
    /// only leaves when the driver's block pulls it, so a driver that stalls takes everybody with it:
    /// waiting two seconds to notice would be two seconds of silence from a DAW that is playing
    /// perfectly well. Comfortably longer than any real block period, including the 4096-frame
    /// maximum the bridge will carry (85 ms).</para></summary>
    private static readonly TimeSpan DriverStaleAfter = TimeSpan.FromMilliseconds(250);

    private sealed class HostState
    {
        public required Guid Id { get; init; }
        /// <summary>Arrival order, so driver hand-over is deterministic rather than dictionary order.</summary>
        public required long Order { get; init; }
        public long LastBlockTicks;
        /// <summary>Null while this host is the driver — the driver's audio never goes through a ring.</summary>
        public HostLane? Lane;
        public long BlocksReceived;
    }

    private readonly AudioSender sender;
    private readonly Action<string>? diagnostic;
    private readonly object gate = new();
    private readonly Dictionary<Guid, HostState> hosts = new();
    private Guid driverId;
    private long orderCounter;
    private bool armed;

    // Mix scratch for the summing path. Sized for the largest block the bridge can carry, allocated
    // once. Only touched on the bridge thread.
    private readonly float[] mixScratch = new float[MaxFramesPerBlock * Channels];
    private readonly float[] pullScratch = new float[MaxFramesPerBlock * Channels];

    private long blocksSubmitted;
    private long blocksSummed;

    /// <summary>Blocks handed to the plugin lane. The number that separates "the plugin is sending
    /// and the app is taking it" from "the app subscribed but nothing arrived".</summary>
    public long BlocksSubmitted => Interlocked.Read(ref blocksSubmitted);

    /// <summary>Blocks where a second DAW's audio was summed in. Zero in the ordinary single-host
    /// session, which is also the assertion that the fast path is the one being taken.</summary>
    public long BlocksSummed => Interlocked.Read(ref blocksSummed);

    /// <summary>How many DAWs are delivering track audio right now.</summary>
    public int HostCount { get { lock (gate) return hosts.Count; } }

    /// <summary>Is any DAW sending? The app folds this into its "should the sender be running"
    /// decision, so putting a plugin into send mode is enough on its own — there is no second
    /// checkbox to forget.</summary>
    public bool AnyHostSending { get { lock (gate) return hosts.Count > 0; } }

    public PluginTrackSource(AudioSender sender, Action<string>? diagnostic = null)
    {
        this.sender = sender;
        this.diagnostic = diagnostic;
    }

    /// <summary>
    /// One block of DAW track audio, interleaved stereo float at 48 kHz. Called on the plugin
    /// bridge's receive thread, once per audio callback of the sending DAW.
    /// </summary>
    public void OnTrackBlock(Guid hostId, ReadOnlyMemory<float> block)
    {
        var span = block.Span;
        if (span.Length < Channels) return;
        // Whole frames only: a truncated tail would swap the channels for everything after it.
        var frames = span.Length / Channels;
        // Larger than the bridge's own maximum, which no plugin sends: drop it.
        if (frames > MaxFramesPerBlock) return;

        bool isDriver;
        bool needArm;
        HostState[]? others = null;
        var now = Stopwatch.GetTimestamp();
        lock (gate)
        {
            if (!hosts.TryGetValue(hostId, out var host))
            {
                host = new HostState { Id = hostId, Order = ++orderCounter };
                hosts[hostId] = host;
                if (hosts.Count == 1 || !hosts.ContainsKey(driverId))
                {
                    driverId = hostId;
                    diagnostic?.Invoke($"plugin send: DAW {Short(hostId)} is driving the plugin lane");
                }
                else
                {
                    host.Lane = new HostLane(Short(hostId), diagnostic);
                    diagnostic?.Invoke($"plugin send: a second DAW ({Short(hostId)}) joined - its audio is summed onto {Short(driverId)}'s clock");
                }
            }
            host.LastBlockTicks = now;
            host.BlocksReceived++;

            // A driver that has gone quiet takes every other host down with it, because their audio
            // only leaves on ITS block. So a host that IS delivering takes the clock without waiting
            // for the sweep. Nothing to do in the ordinary single-host case: the driver is us.
            if (hostId != driverId
                && (!hosts.TryGetValue(driverId, out var current)
                    || now - current.LastBlockTicks > (long)(DriverStaleAfter.TotalSeconds * Stopwatch.Frequency)))
            {
                PromoteDriverLocked(host);
            }

            // Re-assert rather than trust our own flag. AudioSender.Stop() disarms the lane directly
            // ("stop sending my audio" means the DAW track too), and if that happened while a DAW was
            // still playing, believing our own bookkeeping would leave the lane dead with nothing in
            // the app to say why. Cheap: a volatile read per block, and SetPluginSendActive is a no-op
            // when the state already matches.
            needArm = !armed || !sender.IsPluginSending;
            armed = true;

            isDriver = hostId == driverId;
            if (!isDriver)
            {
                // Not driving: park the audio. It leaves on the driver's next block.
                host.Lane?.Feed(span);
            }
            else if (hosts.Count > 1)
            {
                others = new HostState[hosts.Count - 1];
                var i = 0;
                foreach (var other in hosts.Values) if (other.Id != hostId) others[i++] = other;
            }
        }

        // Outside the lock: SetPluginSendActive takes the sender's own configGate, and holding two
        // locks in one order here and the other order anywhere else is how deadlocks are built.
        if (needArm) sender.SetPluginSendActive(true);
        if (!isDriver) return;

        if (others is null || others.Length == 0)
        {
            // THE ORDINARY CASE. One DAW, so the block goes out exactly as it arrived: no copy, no
            // ring, no resampler, no clamp. Whatever the track's fader is doing is what the peers get.
            Interlocked.Increment(ref blocksSubmitted);
            sender.SubmitPluginBlock(block);
            return;
        }

        var floats = frames * Channels;
        var mix = mixScratch.AsSpan(0, floats);
        span[..floats].CopyTo(mix);
        var pull = pullScratch.AsSpan(0, floats);
        foreach (var other in others)
        {
            var lane = other.Lane;
            if (lane is null) continue;
            lane.Read(pull);
            for (var i = 0; i < floats; i++) mix[i] += pull[i];
        }
        // Clamp ONLY on the summing path. Two full-scale tracks from two DAWs genuinely can sum past
        // 1.0, and the PCM packer would wrap rather than clip if we let it through. The single-host
        // path above is deliberately left alone: nothing there can raise the level, and a clamp that
        // never fires is a clamp that only costs a pass over the buffer.
        SampleClamp.ClampBuffer(mix);
        Interlocked.Increment(ref blocksSubmitted);
        Interlocked.Increment(ref blocksSummed);
        sender.SubmitPluginBlock(new ReadOnlyMemory<float>(mixScratch, 0, floats));
    }

    /// <summary>
    /// Forget hosts that have gone quiet, and disarm the lane when the last one goes. Called once a
    /// second by the app.
    ///
    /// <para>This is the path a crashed DAW takes. A plugin unloading cleanly says goodbye and the
    /// bridge forgets its instance, but a DAW that is killed says nothing at all, and a lane left
    /// armed forever would keep "Send my audio" switched on from the app's point of view with no
    /// way for the user to see why.</para>
    /// </summary>
    public void Sweep()
    {
        var disarm = false;
        lock (gate)
        {
            if (hosts.Count == 0) return;
            var cutoff = Stopwatch.GetTimestamp() - (long)(HostIdleTimeout.TotalSeconds * Stopwatch.Frequency);
            List<Guid>? stale = null;
            foreach (var host in hosts.Values)
            {
                if (host.LastBlockTicks > cutoff) continue;
                (stale ??= []).Add(host.Id);
            }
            if (stale is not null)
            {
                foreach (var id in stale)
                {
                    if (hosts.Remove(id, out var host)) host.Lane?.Dispose();
                    diagnostic?.Invoke($"plugin send: DAW {Short(id)} stopped sending");
                }
                ElectDriverLocked();
            }
            if (hosts.Count == 0 && armed)
            {
                armed = false;
                disarm = true;
            }
        }
        // Outside the lock: SetPluginSendActive takes the sender's own configGate, and holding two
        // locks in one order here and the other order anywhere else is how deadlocks are built.
        if (disarm) sender.SetPluginSendActive(false);
    }

    /// <summary>A plugin instance said goodbye, or the bridge dropped it. Same handling as a timeout,
    /// just immediate.</summary>
    public void Forget(Guid hostId)
    {
        var disarm = false;
        lock (gate)
        {
            if (!hosts.Remove(hostId, out var host)) return;
            host.Lane?.Dispose();
            diagnostic?.Invoke($"plugin send: DAW {Short(hostId)} closed");
            ElectDriverLocked();
            if (hosts.Count == 0 && armed)
            {
                armed = false;
                disarm = true;
            }
        }
        if (disarm) sender.SetPluginSendActive(false);
    }

    /// <summary>Hand the clock to a host that is still delivering. The outgoing driver keeps its place
    /// and gets a ring, so if it comes back it is summed in rather than lost.</summary>
    private void PromoteDriverLocked(HostState next)
    {
        if (next.Id == driverId) return;
        if (hosts.TryGetValue(driverId, out var previous) && previous.Lane is null)
        {
            previous.Lane = new HostLane(Short(previous.Id), diagnostic);
        }
        driverId = next.Id;
        next.Lane?.Dispose();
        next.Lane = null;
        diagnostic?.Invoke($"plugin send: DAW {Short(driverId)} took over driving the plugin lane (the previous one went quiet)");
    }

    /// <summary>Make sure a live host is driving. The earliest-arrived survivor takes over, and drops
    /// its ring — the driver's audio goes straight out, so a ring on it would only add latency.</summary>
    private void ElectDriverLocked()
    {
        if (hosts.Count == 0) { driverId = Guid.Empty; return; }
        if (hosts.ContainsKey(driverId)) return;

        HostState? next = null;
        foreach (var host in hosts.Values)
        {
            if (next is null || host.Order < next.Order) next = host;
        }
        if (next is null) return;
        driverId = next.Id;
        next.Lane?.Dispose();
        next.Lane = null;
        diagnostic?.Invoke($"plugin send: DAW {Short(driverId)} took over driving the plugin lane");
    }

    private static string Short(Guid id) => id.ToString("N")[..8];

    public void Dispose()
    {
        var disarm = false;
        lock (gate)
        {
            foreach (var host in hosts.Values) host.Lane?.Dispose();
            hosts.Clear();
            driverId = Guid.Empty;
            if (armed) { armed = false; disarm = true; }
        }
        if (disarm) sender.SetPluginSendActive(false);
    }

    /// <summary>Which host is currently driving, for the gate.</summary>
    public Guid DriverForTest { get { lock (gate) return driverId; } }

    /// <summary>
    /// A non-driving DAW's audio, held in a ring and pulled onto the driver's clock.
    ///
    /// <para>Straight port of the receive side's <c>DriftResamplingProvider</c>, and deliberately so:
    /// the same measured feed÷drain ratio over a ten-second window, the same 70/30 smoothing, the
    /// same ±5 % sanity clamp, and the same depth term steering the ring toward a cushion sized off
    /// the consumer's actual pull. That code is what holds a WASAPI card's buffer flat over hours,
    /// and this problem is the same problem with different names on the two clocks.</para>
    ///
    /// <para><b>Pull side only.</b> The resampler runs where the driver reads, never where the
    /// plugin writes. The receive-side file records that a producer-side attempt starved the cushion
    /// to zero and crackled, and there is no reason this would behave differently.</para>
    /// </summary>
    private sealed class HostLane : IDisposable
    {
        // The window, the sanity band, the smoothing and the depth term are the shared ones in
        // DriftRatioTracker, which this loop was folded into on 2026-09-07. What stays here is what is
        // local to a DAW: how deep a cushion this lane wants, and when it is ready to join the mix.
        private const double TargetGulpMultiple = 1.2;
        private const int MinTargetDepthMs = 8;
        private const int MaxTargetDepthMs = 50;
        private const int TargetHysteresisMs = 2;
        private const int BytesPerFrame = Channels * sizeof(float);

        private readonly AudioRingBuffer ring;
        private readonly WdlResampler resampler = new();
        private readonly DriftRatioTracker tracker = new(SampleRate);
        private readonly string name;
        private readonly Action<string>? diagnostic;

        private long fedBytes;
        private long drainedBytes;
        private long pullSumBytes;
        private long pullCount;
        private int targetMs = 12;
        // Until the ring holds a cushion, this lane contributes silence and takes nothing out.
        //
        // Without it a second DAW starts with whatever happened to be in the ring when the driver
        // first pulled - usually one block or less, because the two are running at the same rate and
        // a cushion never builds on its own. Every scheduling wobble then underruns and zero-fills,
        // which is a click, and the drift corrector cannot help because its first measurement is ten
        // seconds away. SessionPlayout arms at its target for the same reason. The cost is about a
        // cushion's worth of silence from a DAW at the moment it joins, once.
        private bool primed;
        private int TargetFrames => targetMs * SampleRate / 1000;

        private byte[] inputBytes = new byte[16384];
        private float[] outputScratch = new float[MaxFramesPerBlock * Channels];

        /// <summary>The rate ratio currently applied to this host, so the gate can assert that a
        /// matched pair of clocks leaves it alone and a mismatched pair moves it the right way.</summary>
        public double AppliedRatio { get; private set; } = 1.0;
        public int BufferedFrames => ring.BufferedBytes / BytesPerFrame;

        public HostLane(string name, Action<string>? diagnostic)
        {
            this.name = name;
            this.diagnostic = diagnostic;
            // Half a second of stereo float. Deep enough to ride out a DAW whose block delivery is
            // lumpy, shallow enough that a host which runs away is corrected rather than hoarded.
            ring = new AudioRingBuffer(SampleRate / 2 * BytesPerFrame);
            resampler.SetMode(interp: true, filtercnt: 0, sinc: false);
            resampler.SetFeedMode(false);   // output-driven: we ask for N frames out
            resampler.SetRates(SampleRate, SampleRate);
        }

        /// <summary>This host's block arrived. Straight into the ring, uncorrected — the cushion is
        /// what the corrector steers, so nothing may touch it on the way in.</summary>
        public void Feed(ReadOnlySpan<float> block)
        {
            var bytes = MemoryMarshal.AsBytes(block);
            ring.Write(bytes);
            fedBytes += bytes.Length;
        }

        /// <summary>The driver wants this many frames. Produces exactly that many, zero-padding any
        /// shortfall so a host that has fallen behind contributes silence rather than a short block.</summary>
        public void Read(Span<float> destination)
        {
            destination.Clear();
            var outFrames = destination.Length / Channels;
            if (outFrames <= 0) return;

            var requestedBytes = outFrames * BytesPerFrame;
            if (!primed)
            {
                if (ring.BufferedBytes / BytesPerFrame < TargetFrames) return;   // silence, and the ring keeps filling
                primed = true;
                // Start measuring from here: the fill above is not drift, and counting it would read
                // as a huge bogus clock ratio on the first window.
                tracker.Reset();
                diagnostic?.Invoke($"plugin send: DAW {name} buffered {TargetFrames} frames of cushion and joined the mix");
            }

            pullSumBytes += requestedBytes;
            pullCount++;
            UpdateRatioIfDue();

            var inputFramesNeeded = resampler.ResamplePrepare(outFrames, Channels, out var inBuf, out var inBufOff);
            if (inputFramesNeeded > 0)
            {
                var inputFloats = inputFramesNeeded * Channels;
                var inputByteCount = inputFloats * sizeof(float);
                if (inputBytes.Length < inputByteCount) inputBytes = new byte[inputByteCount];
                var got = ring.Read(inputBytes.AsSpan(0, inputByteCount));
                var gotFloats = got / sizeof(float);
                MemoryMarshal.Cast<byte, float>(inputBytes.AsSpan(0, got)).CopyTo(inBuf.AsSpan(inBufOff, gotFloats));
                if (gotFloats < inputFloats) inBuf.AsSpan(inBufOff + gotFloats, inputFloats - gotFloats).Clear();
            }

            if (outputScratch.Length < destination.Length) outputScratch = new float[destination.Length];
            var produced = resampler.ResampleOut(outputScratch, 0, inputFramesNeeded, outFrames, Channels);
            var producedFloats = Math.Min(produced * Channels, destination.Length);
            outputScratch.AsSpan(0, producedFloats).CopyTo(destination);
            drainedBytes += requestedBytes;
        }

        private void UpdateRatioIfDue()
        {
            // The tracker owns the window and re-anchors it; the target below is sized from the window
            // that just ended, which is why the update is taken in two steps rather than one.
            if (!tracker.WindowClosed(fedBytes, drainedBytes)) return;

            var avgPullBytes = pullCount > 0 ? pullSumBytes / pullCount : 0;
            pullSumBytes = 0;
            pullCount = 0;
            if (avgPullBytes > 0)
            {
                var gulpMs = avgPullBytes / (double)BytesPerFrame * 1000.0 / SampleRate;
                var candidateMs = (int)Math.Round(Math.Clamp(gulpMs * TargetGulpMultiple, MinTargetDepthMs, MaxTargetDepthMs));
                if (Math.Abs(candidateMs - targetMs) >= TargetHysteresisMs) targetMs = candidateMs;
            }

            // The tracker discards the first completed window — it contains the ring filling from
            // empty, which reads as a large bogus ratio — and discards, rather than clamps, anything
            // outside the sanity band.
            if (!tracker.ApplyMeasurement(ring.BufferedBytes / BytesPerFrame, targetMs * SampleRate / 1000)) return;

            AppliedRatio = tracker.AppliedRatio;
            resampler.SetRates(SampleRate * AppliedRatio, SampleRate);
            diagnostic?.Invoke(
                $"plugin send: DAW {name} clock={tracker.ClockRatio:F6} ({(tracker.ClockRatio - 1.0) * 1_000_000.0:+0;-0}ppm) "
              + $"depthMs={ring.BufferedBytes / BytesPerFrame * 1000 / SampleRate} targetMs={targetMs}");
        }

        /// <summary>Drive one measurement window from the gate. Real drift takes ten seconds to
        /// measure by design, and a test that waited that long per direction would not be run.</summary>
        internal void ForceWindowForTest(double elapsedSeconds)
        {
            tracker.RewindWindowStartForTest(elapsedSeconds);
            UpdateRatioIfDue();
        }

        public void Dispose() { ring.Reset(); primed = false; }
    }

    /// <summary>Build a lane on its own, for the gate: the corrector is the one part of this file with
    /// behaviour that cannot be observed from the outside in under ten seconds.</summary>
    internal static object CreateHostLaneForTest(Action<string>? diagnostic = null) => new HostLane("test", diagnostic);

    internal static void FeedHostLaneForTest(object lane, ReadOnlySpan<float> block) => ((HostLane)lane).Feed(block);
    internal static void ReadHostLaneForTest(object lane, Span<float> destination) => ((HostLane)lane).Read(destination);
    internal static void ForceWindowForTest(object lane, double elapsedSeconds) => ((HostLane)lane).ForceWindowForTest(elapsedSeconds);
    internal static double AppliedRatioForTest(object lane) => ((HostLane)lane).AppliedRatio;
    internal static int BufferedFramesForTest(object lane) => ((HostLane)lane).BufferedFrames;
}
