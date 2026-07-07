using System.Diagnostics;
using System.Net;
using RemSound.Core;

namespace RemSound.Receiver;

/// <summary>
/// Public façade for the receiver pipeline. Routes raw packets from <see cref="NetworkListener"/>
/// to one <see cref="StreamSession"/> per remote sender, all of which write to their own
/// <see cref="SessionPlayout"/>; the <see cref="PlayoutEngine"/> then mixes those at render time.
///
/// Multi-source rationale: the previous design held a single <c>activeSession</c> and reset the
/// playout buffer whenever a Format packet arrived from a different endpoint. With two senders
/// transmitting to the same receiver simultaneously (peer-to-peer plus a localhost-monitor, or
/// a future conferencing setup), Format packets alternated and the buffer flushed several times
/// per second — the crackle the WAN test surfaced. Now each endpoint gets its own session and
/// playout state, all summed at the render output.
///
/// Idle sessions are pruned: any session that hasn't received audio data in
/// <see cref="SessionIdleTimeout"/> is removed by <see cref="PruneIdleSessions"/>, called by the
/// App's snapshot tick.
///
/// Responsibilities deliberately scoped:
///   * Lifecycle (Start / Stop / Dispose).
///   * Public configuration (max latency, volume, mute, output device).
///   * Routing packets to the right session, creating sessions for new endpoints.
/// </summary>
public sealed class AudioReceiver : IDisposable
{
    public const int MixSampleRate = 48000;
    public const int MixChannels = 2;
    private const int MixBytesPerSecond = MixSampleRate * MixChannels * sizeof(float);

    /// <summary>How big each session's AudioRingBuffer is sized — enough to absorb burst arrival
    /// over the maximum supported latency without dropping. Values much above the user-set max
    /// latency just waste memory; below it can drop on a deep WAN burst.</summary>
    private const int CapacityHeadroomMultiplier = 8;
    private const int MaxLatencyForSizingMs = 500;

    /// <summary>Sessions that have received nothing for this long are pruned. Long enough that a
    /// brief silent gap (mute / no input) doesn't kill the session, short enough that a peer that
    /// truly stops sending doesn't keep occupying state forever (and inflating the underrun
    /// counter — every render read of an empty-but-armed session bumps the underrun count even
    /// though the mix output is unaffected).</summary>
    public static readonly TimeSpan SessionIdleTimeout = TimeSpan.FromSeconds(4);

    /// <summary>Hard ceiling on concurrently-tracked stream sessions. A backstop for the idle
    /// prune: even if reconnect churn somehow outpaces the idle sweep, the session table — and
    /// the multi-MB playout ring each entry owns — can never grow without bound. Far above any
    /// legitimate scenario (the lobby relay caps peers at 10, and a peer emits at most one
    /// stream per render lane). When exceeded, <see cref="PruneIdleSessions"/> evicts the
    /// idlest sessions down to this cap. 2026-05-15.</summary>
    public const int MaxLiveSessions = 32;

    private readonly Stopwatch uptime = new();
    private readonly ReceiverDiagnostics diagnostics = new();
    private readonly PlayoutEngine playoutEngine;
    private IRenderBackend multiOutput;
    private readonly NetworkListener listener;
    private Action<string>? diagnosticSink;

    private readonly object sessionsLock = new();
    // Sessions are keyed by (Endpoint, StreamId) — 2026-05-11. A peer can produce
    // multiple simultaneous streams (e.g. WASAPI lane + ASIO lane in the native-
    // independent audio mode). For single-lane modes the sender emits one streamId so
    // the dict still has one entry per peer, identical to the pre-refactor behaviour.
    private readonly Dictionary<(IPEndPoint Endpoint, ushort StreamId), StreamSession> sessions = new();
    // Last live-session count emitted to the diagnostic sink. Lets PruneIdleSessions log
    // only when the count actually changes, so unbounded growth is visible in the log
    // without spamming it or needing a Task Manager screenshot to notice.
    private int lastLoggedSessionCount = -1;

    /// <summary>When false (the default), a Format packet arriving with a NEW streamId from
    /// a peer that already has a session under a DIFFERENT streamId triggers immediate
    /// disposal of the old session — preserves the pre-refactor "one peer = one active
    /// session" behaviour. The sender legitimately rotates streamId on codec changes /
    /// engine restarts; without this, the old SessionPlayout sits empty for 4 seconds
    /// until <see cref="PruneIdleSessions"/> fires, racking up phantom underrun counts
    /// from the render thread polling its empty buffer (~100 per second).
    ///
    /// Set true in the native-independent audio mode (Stage 4) where two streamIds from
    /// the same peer are expected to coexist (WASAPI lane + ASIO lane). In that mode the
    /// auto-dispose-old-on-new-streamId is wrong — both lanes are continuously active.</summary>
    public bool AllowMultipleStreamsPerPeer { get; set; }

    // True when audio playback is enabled — i.e. multiOutput is started and Format/Audio
    // packets should be processed into sessions. False means the listener stays bound
    // (so the single-port heartbeat path keeps working) but audio packets are discarded
    // before any decode/buffer work, and no SessionPlayout is created. Volatile because
    // packet handlers run on the network thread and may observe a SetPlaybackEnabled
    // toggle at any moment. See the single-port unification (2026-05-06): the listener
    // is bound for the duration of a connection so heartbeat packets always reach
    // OnHeartbeatReceived, regardless of the user's "Receive audio" tick state.
    private volatile bool playbackEnabled;

    // Audio decryption (2026-05-31). One shared decryptor — all receive decode runs on the
    // single network thread, so no per-session cipher is needed. AudioKey is the AES key derived
    // from the local profile's password; AudioFingerprint is the short id of that password we
    // compare against the fingerprints peers advertise in their format packets. Both are pushed
    // down by the app and read on the network thread (hence volatile). peerSecurity records the
    // latest match result per peer address for the app to surface ("passwords don't match" etc.).
    private readonly AudioDecryptor decryptor = new();
    private volatile byte[]? audioKey;
    private volatile byte[]? audioFingerprint;
    public byte[]? AudioKey { get => audioKey; set => audioKey = value; }
    public byte[]? AudioFingerprint { get => audioFingerprint; set => audioFingerprint = value; }
    private readonly object securityLock = new();
    private readonly Dictionary<IPAddress, PeerSecurityStatus> peerSecurity = new();

    /// <summary>Latest per-peer encryption status (whether their profile password matches ours),
    /// derived from the fingerprint each peer advertises in its format packets. Keyed by source
    /// address. The app polls this to tell the user about a password mismatch or an out-of-date
    /// peer instead of leaving a silent stream a mystery.</summary>
    public IReadOnlyList<KeyValuePair<IPAddress, PeerSecurityStatus>> GetPeerSecurityStatuses()
    {
        lock (securityLock)
        {
            return peerSecurity.ToList();
        }
    }

    /// <summary>True if decoded audio from <paramref name="address"/> has been written to a
    /// playout buffer within <paramref name="within"/>. The app uses this to drive the
    /// connect/disconnect cues off the ACTUAL audio stream rather than the heartbeat alone —
    /// so a heartbeat blip while audio keeps flowing never fires a false "disconnect" cue, and
    /// the connect cue can fire the moment audio starts. Returns false when not receiving (no
    /// sessions), so the caller falls back to the heartbeat for send-only setups. 2026-05-31.</summary>
    public bool IsAudioFlowingFrom(IPAddress address, TimeSpan within)
    {
        var cutoff = DateTime.UtcNow - within;
        lock (sessionsLock)
        {
            foreach (var session in sessions.Values)
            {
                if (session.Endpoint.Address.Equals(address) && session.LastWriteUtc >= cutoff)
                {
                    return true;
                }
            }
        }
        return false;
    }

    // Allowed-senders gate. The App ticks peer checkboxes; only those endpoints' audio reaches
    // the playout. A null set means "no filter" (legacy behaviour). An empty set means "block
    // everyone". Stored as IP addresses (not full IPEndPoint) because incoming packets carry
    // the sender's *outbound* (ephemeral) source port, not the port we'd see in their
    // announcement — comparing port-included would always fail. The peer is identified by
    // machine IP; we accept audio from any source port on that IP. Read on the network thread,
    // updated from the UI thread via SetAllowedSenders.
    private volatile HashSet<IPAddress>? allowedSenders;

    private long packetsReceived;
    private long bytesReceived;
    private long packetsDropped;
    private long packetsRejectedNotAllowed;

    public AudioReceiver()
    {
        playoutEngine = new PlayoutEngine(diagnostics);
        multiOutput = new CompositeRenderBackend(AudioMode.WasapiOnly, null, playoutEngine, msg => diagnosticSink?.Invoke($"output: {msg}"));
        listener = new NetworkListener(HandleRawPacket, msg => diagnosticSink?.Invoke($"network: {msg}"));
    }

    /// <summary>
    /// Sets the audio backend mode (and ASIO driver, when ASIO is involved) for the render side.
    /// Mirrors AudioSender.SetAudioMode. The App should re-issue SetOutputDevices afterwards with
    /// the current device-id selection.
    /// </summary>
    public void SetAudioMode(AudioMode mode, string? asioDriverName)
    {
        var wasRunning = multiOutput.IsRunning;
        try { multiOutput.Stop(); } catch { /* ignore */ }
        try { multiOutput.Dispose(); } catch { /* ignore */ }
        multiOutput = new CompositeRenderBackend(mode, asioDriverName, playoutEngine, msg => diagnosticSink?.Invoke($"output: {msg}"));
        if (wasRunning) multiOutput.Start();
    }

    public bool IsAsioBackend => multiOutput is CompositeRenderBackend;

    /// <summary>Sets the Buffer-smoothness knob (1 = aggressive — clicks the buffer back
    /// to target on any drift, holds the user's latency tightly; 10 = smooth — no clicks
    /// but the queue can creep up under jitter or sustained clock drift). Knob drives a
    /// click-based DropOldest trim in <see cref="SessionPlayout.ReadFloats"/>. As of the
    /// 2026-05-06 cleanup (Phase 3) this is mostly a safety knob — the Phase-2 drift
    /// corrector keeps the buffer near target so the trim should rarely fire regardless of
    /// this value.</summary>
    public void SetSmoothness(int value) => playoutEngine.SetSmoothness(value);

    /// <summary>Sets the concealment artifact used when the playout buffer comes up empty
    /// on a render-side read. Pure receiver-side cosmetic — sender doesn't see this.
    /// Live: takes effect on the next underrun, no need to restart playback.</summary>
    public void SetConcealmentArtifact(ConcealmentArtifact artifact) =>
        playoutEngine.SetConcealmentArtifact(artifact);

    /// <summary>Sets (or clears with null) the per-peer pan+EQ chain for the given peer address.
    /// Applies to that peer's current and future sessions. Called from the UI thread.</summary>
    public void SetPeerDsp(IPAddress address, PeerDspChain? chain) =>
        playoutEngine.SetPeerDsp(address, chain);

    /// <summary>Sets (or clears with null) the split-recording tap on every current and future session,
    /// so the recorder receives each peer's block separately. <paramref name="raw"/> = before pan/EQ
    /// (bypass), else after. Called from the UI thread.</summary>
    public void SetPeerRecordTap(Action<IPEndPoint, ReadOnlyMemory<float>>? tap, bool raw) =>
        playoutEngine.SetRecordTap(tap, raw);

    /// <summary>Fired once per rendered block, right after <see cref="OnReceivedSamples"/> — the block
    /// boundary a single-file bypass recording uses to flush its per-peer sum.</summary>
    public Action? OnRecordBlockComplete
    {
        get => playoutEngine.OnRecordBlockComplete;
        set => playoutEngine.OnRecordBlockComplete = value;
    }

    /// <summary>
    /// Optional callback invoked when the engine produces fully-processed mixed received
    /// audio (volume / mute / limiter all applied). Span is 48 kHz interleaved stereo
    /// float, lives on the render thread — copy or consume quickly. The
    /// <see cref="RenderRoute"/> tag identifies which lane fired the callback (Mixed in
    /// classic modes; WasapiLane or AsioLane in BothIndependent where each lane Reads
    /// independently). The recorder uses the tag to keep per-lane streams separate and
    /// mix them at drain time rather than appending sequentially. Setter mirrors directly
    /// onto <see cref="PlayoutEngine"/>; null clears the tap.
    /// </summary>
    public Action<ReadOnlyMemory<float>, RenderRoute>? OnReceivedSamples
    {
        get => playoutEngine.OnReceivedSamples;
        set => playoutEngine.OnReceivedSamples = value;
    }

    /// <summary>
    /// Sets the allow-list of sender endpoints whose audio will be rendered. Pass an empty set
    /// to block all (the user has selected no peers); pass null to disable filtering and accept
    /// everyone (test/diagnostic only — production UI always passes a real set).
    ///
    /// Why this exists: without an allow-list, anyone who can reach our UDP port (e.g. a peer
    /// who has us in *their* selected list, or a stale broadcast announcement that another
    /// instance starts honouring) gets their audio rendered to our speakers automatically. The
    /// user expects audio to play only after they explicitly tick a peer's checkbox; this gate
    /// implements that contract.
    ///
    /// The filter is applied at packet receipt — Format and Audio packets from non-allowed
    /// endpoints are counted but discarded, no SessionPlayout is created, no playout buffer
    /// fills. Discovery and heartbeat (separate UDP ports) are unaffected, so non-allowed
    /// peers still appear as "discovered" in the UI ready to be ticked.
    /// </summary>
    public void SetAllowedSenders(IEnumerable<IPEndPoint>? allowed)
    {
        // Reduce IPEndPoint inputs to bare IPAddress for the gate; see field-comment for why.
        var snapshot = allowed is null ? null : new HashSet<IPAddress>(allowed.Select(ep => ep.Address));
        allowedSenders = snapshot;
        // Tear down sessions for endpoints that just got removed from the allow-list — without
        // this, audio would keep playing from a session that was opened before the user
        // unticked its checkbox. Match by IP since that's how the gate works.
        if (snapshot is not null)
        {
            List<StreamSession> toClose = [];
            lock (sessionsLock)
            {
                foreach (var (key, session) in sessions)
                {
                    if (!snapshot.Contains(key.Endpoint.Address))
                    {
                        toClose.Add(session);
                    }
                }
                foreach (var session in toClose)
                {
                    sessions.Remove((session.Endpoint, session.StreamId));
                }
            }
            foreach (var session in toClose)
            {
                playoutEngine.RemoveSession(session.Endpoint, session.StreamId);
                session.Dispose();
                diagnosticSink?.Invoke($"stream session closed (sender no longer in selected peers): {session.Endpoint} stream={session.StreamId}");
            }
        }
    }

    /// <summary>Cumulative count of audio/format packets dropped because the sender wasn't in
    /// the allow-list. Surfaced via diagnostics so we can confirm the filter is working.</summary>
    public long PacketsRejectedNotAllowed => Interlocked.Read(ref packetsRejectedNotAllowed);

    private void UpdatePeerSecurity(IPAddress address, byte[]? peerFingerprint)
    {
        var myFp = audioFingerprint;
        PeerSecurityStatus status;
        if (peerFingerprint is null)
        {
            status = PeerSecurityStatus.PeerNeedsUpdate; // peer is on a pre-encryption build
        }
        else if (myFp is null)
        {
            status = PeerSecurityStatus.Unknown; // we have no password set ourselves yet
        }
        else
        {
            status = RemSoundCrypto.FingerprintsEqual(peerFingerprint, myFp)
                ? PeerSecurityStatus.Secure
                : PeerSecurityStatus.PasswordMismatch;
        }
        lock (securityLock)
        {
            peerSecurity[address] = status;
        }
    }

    private bool IsSenderAllowed(IPEndPoint remote)
    {
        var snapshot = allowedSenders;
        if (snapshot is null) return true; // null = no filter
        return snapshot.Contains(remote.Address);
    }

    /// <summary>Optional diagnostic sink (App writes to log file).</summary>
    public Action<string>? Diagnostic { get => diagnosticSink; set => diagnosticSink = value; }

    /// <summary>True when audio playback is active — i.e. <see cref="SetPlaybackEnabled"/>
    /// has been called with <c>true</c> and the underlying render backend is running. This
    /// matches the previous semantic of "the user has Receive audio on and we're rendering".
    /// The UDP listener socket is NOT covered by this flag — see <see cref="IsListenerRunning"/>.
    /// In single-port mode (post-2026-05-06) the listener stays bound for the whole connection
    /// so heartbeat packets always reach us; this flag tracks only the playback half.</summary>
    public bool IsRunning => multiOutput.IsRunning;
    /// <summary>True when the UDP listener socket is bound. Independent of playback state.
    /// Surfaced for diagnostic/symmetry only — most callers want <see cref="IsRunning"/>.</summary>
    public bool IsListenerRunning => listener.IsRunning;
    /// <summary>Max time-in-user-handler (the work between Socket.ReceiveFrom returning and
    /// onPacket finishing) observed since the last call. The SNAP loop reads this each
    /// second to split observed inter-packet jitter into network vs receiver-processing
    /// contributions. Resets on read.</summary>
    public int TakeMaxOnPacketMs() => listener.TakeMaxOnPacketMs();

    /// <summary>Worst inter-packet arrival gap (ms) at the user-space UDP socket since the
    /// last call. Resets on read. Compared with the sender's per-callback gap on the other
    /// machine, this localises a stall: if the sender's send-callback gap is small but this
    /// is large, the OS/network between sender and receiver delayed delivery (NIC IRQ
    /// servicing, scheduler not waking our receive thread, kernel batching). 2026-05-21.</summary>
    public int TakeMaxInterPacketGapMs() => listener.TakeMaxInterPacketGapMs();

    /// <summary>Cumulative milliseconds the network receive thread spent inside packet-
    /// handler work since the last call (drain-on-read pattern). Diag log emits this as
    /// recvMs per second — a direct read of how busy the network thread is. Item 2 of
    /// RemSoundefficiency.md. Resets on read.</summary>
    public double TakeReceiveWorkMs() =>
        listener.TakeCumulativeOnPacketTicks() * 1000.0 / Stopwatch.Frequency;

    /// <summary>Cumulative milliseconds the audio render threads spent inside
    /// <see cref="PlayoutEngine.Read"/> / <see cref="PlayoutEngine.ReadForRoute"/>
    /// (per-session mix + volume + limiter + pack-to-bytes) since the last call. Diag log
    /// emits this as renderMs per second. Resets on read. 2026-05-22.</summary>
    public double TakeRenderWorkMs() =>
        playoutEngine.TakeCumulativeRenderTicks() * 1000.0 / Stopwatch.Frequency;

    // TakeMaxFanOutCacheMs removed 2026-05-23. Originally measured the FanOutSource cache age
    // between WASAPI and ASIO consumers in BothIndependent mode. The FanOut architecture was
    // removed in May when each lane got its own filtered PlayoutEngine source — there is no
    // shared cache to measure any more, so the method always returned 0. Removed alongside
    // CompositeRenderBackend.TakeMaxFanOutCacheBytes and the fanCacheMs= diag column.
    public string OutputDeviceName => multiOutput.ActiveDeviceSummary;
    public int CurrentBufferMs => playoutEngine.CurrentBufferMs;
    public int TargetLatencyMs => playoutEngine.TargetLatencyMs;

    /// <summary>
    /// Frame duration of the most-recently-active stream (10 ms PCM, 20 ms Opus). null when no
    /// stream is active. With multiple senders this picks the largest frame duration as the
    /// codec floor — most conservative for the auto-tune. Returns ms (rounded up to the next
    /// integer if the underlying sample-count yields a fractional duration, e.g. 2.5 ms → 3),
    /// so the auto-tune always overestimates rather than underestimates the codec floor.
    /// </summary>
    public int? ActiveStreamFrameMs
    {
        get
        {
            lock (sessionsLock)
            {
                if (sessions.Count == 0) return null;
                var maxSamples = 0;
                var sampleRate = 48000;
                foreach (var s in sessions.Values)
                {
                    if (s.Format.FrameSamplesPerChannel > maxSamples)
                    {
                        maxSamples = s.Format.FrameSamplesPerChannel;
                        sampleRate = s.Format.SampleRate > 0 ? s.Format.SampleRate : 48000;
                    }
                }
                // Round up so a 2.5 ms frame reports as 3 ms — the auto-tune treats this as
                // a floor, and overestimating by half a millisecond is safer than rounding
                // down to 2 ms and pushing the buffer below the real codec frame size.
                return (maxSamples * 1000 + sampleRate - 1) / sampleRate;
            }
        }
    }

    /// <summary>Aggregate count across all active PCM sessions of frames the assembler rejected.
    /// Resets per-session when a session ends; the receiver-level number is the live sum.</summary>
    public long PcmFrameRejections
    {
        get
        {
            lock (sessionsLock)
            {
                long total = 0;
                foreach (var s in sessions.Values) total += s.PcmFrameRejections;
                return total;
            }
        }
    }

    /// <summary>Take the worst post-decode single-sample step magnitude across all active
    /// stream sessions since the last call, resetting each session's probe. Used by the
    /// diag log to pinpoint where in the pipeline audio discontinuities are being
    /// introduced. Returns max-of-(cross, within); for the split values use the XB/WB
    /// methods below and do NOT also call this in the same drain window.</summary>
    public float TakeMaxPostDecodeStep()
    {
        lock (sessionsLock)
        {
            var max = 0f;
            foreach (var s in sessions.Values)
            {
                var v = s.TakeMaxPostDecodeStep();
                if (v > max) max = v;
            }
            return max;
        }
    }

    /// <summary>Cross-buffer (packet-boundary) max post-decode step across all sessions.
    /// Drains each session's cross-buffer counter. 2026-05-21 addition for the click hunt.</summary>
    public float TakeMaxPostDecodeStepCrossBuffer()
    {
        lock (sessionsLock)
        {
            var max = 0f;
            foreach (var s in sessions.Values)
            {
                var v = s.TakeMaxPostDecodeStepCrossBuffer();
                if (v > max) max = v;
            }
            return max;
        }
    }

    /// <summary>Within-buffer (in-packet content) max post-decode step across all sessions.
    /// Drains each session's within-buffer counter. 2026-05-21 addition for the click hunt.</summary>
    public float TakeMaxPostDecodeStepWithinBuffer()
    {
        lock (sessionsLock)
        {
            var max = 0f;
            foreach (var s in sessions.Values)
            {
                var v = s.TakeMaxPostDecodeStepWithinBuffer();
                if (v > max) max = v;
            }
            return max;
        }
    }

    public long PcmFrameDiscardedPartials
    {
        get
        {
            lock (sessionsLock)
            {
                long total = 0;
                foreach (var s in sessions.Values) total += s.PcmFrameDiscardedPartials;
                return total;
            }
        }
    }

    public int MaxLatencyMs
    {
        get => playoutEngine.MaxLatencyMs;
        set => playoutEngine.SetMaxLatencyMs(value);
    }

    /// <summary>Soft variant: same as setting MaxLatencyMs, but on a LOWER does not drain
    /// the buffer / disarm the session. The drift corrector's adaptive gain shrinks the
    /// buffer gradually over a few seconds instead. Used by auto-tune so its slider
    /// adjustments are inaudible — the user didn't ask for an immediate change and shouldn't
    /// hear one. On a RAISE behaves identically to the regular setter (no drain ever fires
    /// on raise).</summary>
    public void SetMaxLatencyMsSoft(int value) =>
        playoutEngine.SetMaxLatencyMs(value, drainOnLower: false);

    /// <summary>Per-route latency accessors — used in BothIndependent mode where the WASAPI
    /// lane and the ASIO lane each have their own slider. In classic modes only the Mixed
    /// route has sessions, so the route-specific values are configured but never observed.</summary>
    public int MaxLatencyMsFor(RenderRoute route) => playoutEngine.MaxLatencyMsFor(route);
    public int TargetLatencyMsFor(RenderRoute route) => playoutEngine.TargetLatencyMsFor(route);
    public void SetMaxLatencyMsFor(RenderRoute route, int value) =>
        playoutEngine.SetMaxLatencyMs(route, value);
    public void SetMaxLatencyMsSoftFor(RenderRoute route, int value) =>
        playoutEngine.SetMaxLatencyMs(route, value, drainOnLower: false);
    /// <summary>Per-route underrun count for the auto-tune skip-while-underrunning gate. In
    /// BothIndependent the WASAPI lane's underruns should not make the ASIO auto-tune defer
    /// (and vice versa); reading per-route fixes that.</summary>
    public long UnderrunsFor(RenderRoute route) => playoutEngine.AggregateUnderrunsFor(route);
    /// <summary>True when at least one stream session is currently tagged for this route —
    /// used by MainForm's continuous auto-tune to skip routes with no audio in flight, so a
    /// lane's auto-tune can't pre-inflate its target by reacting to shared network-gap data
    /// from a different lane's packets.</summary>
    public bool HasSessionsForRoute(RenderRoute route) => playoutEngine.HasSessionsForRoute(route);

    public long Underruns => playoutEngine.AggregateUnderruns;
    public long Drops => playoutEngine.AggregateDrops + Interlocked.Read(ref packetsDropped);

    /// <summary>Cause-split of <see cref="Underruns"/> for the continuous auto-tune (2026-06-13).
    /// <c>TuneBlockingUnderruns</c> are the short-reads that mean "the buffer is genuinely too thin"
    /// (full-empty reads + reads taken while the ring was draining below target) — the auto-tune
    /// gates its lowering on THESE rather than the legacy total, so a steady trickle of inaudible
    /// device-gulp partials (a chunky output render callback asking for an oversized block on an
    /// otherwise on-target ring) no longer pins the target high forever. <c>DeviceGulpUnderruns</c>
    /// is that excluded device-structural remainder, surfaced for the diag log so the split can be
    /// validated. Both are per-route for BothIndependent, mirroring <see cref="UnderrunsFor"/>.</summary>
    public long TuneBlockingUnderruns => playoutEngine.AggregateTuneBlockingUnderruns;
    public long TuneBlockingUnderrunsFor(RenderRoute route) => playoutEngine.AggregateTuneBlockingUnderrunsFor(route);
    public long DeviceGulpUnderruns => playoutEngine.AggregateDeviceGulpUnderruns;
    public long DeviceGulpUnderrunsFor(RenderRoute route) => playoutEngine.AggregateDeviceGulpUnderrunsFor(route);

    /// <summary>Per-cause split of the legacy `Drops` rollup. Useful in the diag log to tell
    /// "we deliberately trimmed the buffer to track the latency target" (TrimDropBytes) from
    /// "we got malformed packets" (PacketsRejectedMalformed) from "ringbuffer overflowed and
    /// the producer dropped oldest" (RingbufferOverflowDropBytes). Without this split a single
    /// "Drops" value couldn't tell us which mechanism was firing.</summary>
    public long TrimDropBytes => playoutEngine.AggregateTrimDropBytes;
    public long DrainDropBytes => playoutEngine.AggregateDrainDropBytes;
    public long TrimFireCount => playoutEngine.AggregateTrimFireCount;
    // DriftDropFrames / DriftRepeatFrames accessors removed 2026-05-23. They aggregated
    // Phase-2 splice-corrector counters that the Phase-4 fixed-ratio resampler design never
    // increments. Always-zero. Surfaced two unhelpful diag-log columns that are now gone.
    /// <summary>Cumulative count of FULL-empty playout reads (framesRead == 0) — the audible
    /// underrun events that trigger noise-burst concealment + fade-in. Separated from
    /// <see cref="Underruns"/> (which conflates full and partial short reads) so the diag
    /// log can show "real underruns this second" distinct from "partial near-misses".</summary>
    public long ConcealmentFires => playoutEngine.AggregateConcealmentFires;
    /// <summary>Cumulative count of sub-frame partial reads (0 &lt; framesRead &lt; requested).
    /// Inaudible since the 2026-05-14 concealment fix but tracked so we can see clock
    /// in-phase patterns.</summary>
    public long ShortReadFires => playoutEngine.AggregateShortReadFires;
    /// <summary>Live LP-filtered drift error of the primary active session (stereo frames,
    /// signed). Negative = buffer running below target on average; positive = above.</summary>
    public double FilteredDriftErrorFrames => playoutEngine.PrimaryFilteredDriftErrorFrames;
    // DriftAccumulator removed 2026-05-23. Phase-4 fixed-ratio resampler never sets an
    // integrator value; always returned 0. Removed alongside the driftAcc= diag column.
    /// <summary>Take the worst single-sample step out of the ring buffer (after decode +
    /// SessionPlayout.Write, before resampler) since the last call.</summary>
    public float TakeMaxPostRingReadStep() => playoutEngine.TakeMaxPostRingReadStep();
    public float TakeMaxPostRingReadStepCrossBuffer() => playoutEngine.TakeMaxPostRingReadStepCrossBuffer();
    public float TakeMaxPostRingReadStepWithinBuffer() => playoutEngine.TakeMaxPostRingReadStepWithinBuffer();
    /// <summary>Take the worst single-sample step out of the resampler since the last call.</summary>
    public float TakeMaxPostResamplerStep() => playoutEngine.TakeMaxPostResamplerStep();
    public float TakeMaxPostResamplerStepCrossBuffer() => playoutEngine.TakeMaxPostResamplerStepCrossBuffer();
    public float TakeMaxPostResamplerStepWithinBuffer() => playoutEngine.TakeMaxPostResamplerStepWithinBuffer();
    /// <summary>RingbufferOverflowDropBytes = AggregateDrops minus the deliberate trim+drain
    /// causes. Whatever's left was the producer-side overflow (Write into a full buffer) or
    /// the catastrophic-cap trim from NoteFramesQueued. Both indicate "we genuinely couldn't
    /// keep up", as opposed to "we deliberately reshaped the buffer".</summary>
    public long RingbufferOverflowDropBytes
        => Math.Max(0, playoutEngine.AggregateDrops - TrimDropBytes - DrainDropBytes);
    public long PacketsRejectedMalformed => Interlocked.Read(ref packetsDropped);

    public long PacketsReceived => Interlocked.Read(ref packetsReceived);
    public long BytesReceived => Interlocked.Read(ref bytesReceived);
    public TimeSpan Uptime => uptime.Elapsed;

    /// <summary>Total times we used Opus inband FEC to recover a single-packet gap, across all active sessions.</summary>
    public long OpusFecRecoveries
    {
        get
        {
            long total = 0;
            lock (sessionsLock)
            {
                foreach (var s in sessions.Values) total += s.OpusFecRecoveries;
            }
            return total;
        }
    }

    /// <summary>Total times we saw a multi-packet gap that FEC could not fill, across all active sessions.</summary>
    public long OpusUnrecoveredGaps
    {
        get
        {
            long total = 0;
            lock (sessionsLock)
            {
                foreach (var s in sessions.Values) total += s.OpusUnrecoveredGaps;
            }
            return total;
        }
    }

    // === Wire-level packet sequence diagnostics ===
    // Each audio packet carries a per-session sequence number from the sender. Tracking it
    // at receipt tells us whether the network or NIC stack between sender and receiver is
    // reordering, dropping, or duplicating packets — any of which would manifest as audible
    // pops on the PCM path. On a healthy LAN all four counters should grow as
    // WireInOrder == packets, all others == 0. A non-zero Missed / Reordered / Duplicated
    // points straight at transport pathology and rules out codec / playout / hardware as
    // pop sources.

    /// <summary>Cumulative count of audio packets that arrived with the expected wire sequence.</summary>
    public long WireInOrderCount
    {
        get
        {
            long total = 0;
            lock (sessionsLock)
            {
                foreach (var s in sessions.Values) total += s.WireInOrderCount;
            }
            return total;
        }
    }

    /// <summary>Cumulative count of packets that the wire claims went missing (forward gaps).</summary>
    public long WireMissedCount
    {
        get
        {
            long total = 0;
            lock (sessionsLock)
            {
                foreach (var s in sessions.Values) total += s.WireMissedCount;
            }
            return total;
        }
    }

    /// <summary>Cumulative count of packets that arrived out-of-order (later sequence first, then earlier).</summary>
    public long WireReorderedCount
    {
        get
        {
            long total = 0;
            lock (sessionsLock)
            {
                foreach (var s in sessions.Values) total += s.WireReorderedCount;
            }
            return total;
        }
    }

    /// <summary>Cumulative count of duplicate-sequence packets (the same wire seq delivered twice).</summary>
    public long WireDuplicatedCount
    {
        get
        {
            long total = 0;
            lock (sessionsLock)
            {
                foreach (var s in sessions.Values) total += s.WireDuplicatedCount;
            }
            return total;
        }
    }

    public float Volume { get => playoutEngine.Volume; set => playoutEngine.Volume = value; }
    public bool IsMuted { get => playoutEngine.IsMuted; set => playoutEngine.IsMuted = value; }

    /// <summary>
    /// Sets the list of output devices to render received audio to. The receiver mixes once and
    /// fans out to every device in this list — pass an empty list to mute all output without
    /// stopping the receive path. Per session policy, the App does NOT persist this selection;
    /// every session starts with no outputs ticked.
    /// </summary>
    public void SetOutputDevices(IReadOnlyList<string> deviceIds) => multiOutput.SetOutputDevices(deviceIds);

    /// <summary>Take a snapshot of the rolling diagnostic counters. Caller drives at 1 Hz.</summary>
    public ReceiverDiagnostics.DiagSnapshot TakeDiagnosticsSnapshot() => diagnostics.Take(MixBytesPerSecond);

    /// <summary>
    /// Bind the UDP listener socket on <paramref name="udpPort"/>. Does NOT start audio
    /// playback — call <see cref="SetPlaybackEnabled"/>(true) for that. Splitting these
    /// lets the single-port heartbeat path keep working while the user has "Receive audio"
    /// off: the socket stays bound so heartbeat packets reach <see cref="OnHeartbeatReceived"/>,
    /// but Format/Audio packets are discarded at receipt (no decode, no buffer growth).
    /// </summary>
    public void Start(int udpPort = RemPacket.DefaultPort)
    {
        if (listener.IsRunning) return;

        Interlocked.Exchange(ref packetsReceived, 0);
        Interlocked.Exchange(ref bytesReceived, 0);
        Interlocked.Exchange(ref packetsDropped, 0);

        // Tear down any sessions left over from a previous Start (in case Stop wasn't called).
        DisposeAllSessionsLocked();
        playoutEngine.ResetAll();

        listener.Start(udpPort);
        uptime.Restart();
    }

    /// <summary>
    /// Toggles audio playback on or off. When <paramref name="enabled"/> goes false, the
    /// render backend is stopped and any open sessions are disposed (so a re-enable doesn't
    /// drain stale audio). Heartbeat packet routing is unaffected — the listener stays
    /// bound either way as long as <see cref="Start"/> has been called. Idempotent.
    /// </summary>
    public void SetPlaybackEnabled(bool enabled)
    {
        if (enabled == multiOutput.IsRunning)
        {
            playbackEnabled = enabled;
            return;
        }
        if (enabled)
        {
            // Reset packet handlers' gate before starting the backend, so packets that arrive
            // between multiOutput.Start and the next handler invocation aren't misrouted.
            playbackEnabled = true;
            multiOutput.Start();
        }
        else
        {
            // Flip the gate first so HandleFormat/HandleAudio stop opening new sessions, then
            // tear down the backend and any in-flight sessions. Order matters — if we stopped
            // the backend first, in-flight packets could open a fresh session that nothing
            // would ever drain.
            playbackEnabled = false;
            multiOutput.Stop();
            lock (sessionsLock)
            {
                DisposeAllSessionsLocked();
            }
            playoutEngine.ResetAll();
        }
    }

    public void Stop()
    {
        listener.Stop();
        playbackEnabled = false;
        multiOutput.Stop();
        uptime.Stop();
        lock (sessionsLock)
        {
            DisposeAllSessionsLocked();
        }
        playoutEngine.ResetAll();
    }

    public void Dispose()
    {
        Stop();
        listener.Dispose();
        multiOutput.Dispose();
        decryptor.Dispose();
    }

    /// <summary>
    /// Drop sessions that haven't received audio data in <see cref="SessionIdleTimeout"/>. Caller
    /// (the App's snapshot tick) drives this so it stays serialised with the network thread on
    /// the same lock the packet handlers use.
    /// </summary>
    public void PruneIdleSessions()
    {
        var now = DateTime.UtcNow;
        lock (sessionsLock)
        {
            var toRemove = new HashSet<(IPEndPoint Endpoint, ushort StreamId)>();

            // 1) Idle sweep — reap sessions with no decoded write within SessionIdleTimeout.
            //    Reaped on the session's OWN last-write time. The previous implementation
            //    cross-referenced PlayoutEngine.ActiveSessions by (endpoint, streamId) and
            //    silently skipped — leaking the session forever — whenever that lookup missed.
            //    A reconnecting peer never reuses an old key: a sender reboot rerolls the
            //    streamId AND rebinds to a fresh ephemeral source port, so the old session is
            //    always an orphan the lookup-based prune could strand. Reading the session's
            //    own LastWriteUtc removes the lookup, the race, and the leak. 2026-05-15.
            foreach (var (key, session) in sessions)
            {
                if (now - session.LastWriteUtc > SessionIdleTimeout) toRemove.Add(key);
            }

            // 2) Hard-cap backstop. If more than MaxLiveSessions would still remain after the
            //    idle sweep, evict the idlest extras. Guarantees the session table can never
            //    grow without bound whatever churn the idle sweep can't keep up with.
            var survivors = sessions.Count - toRemove.Count;
            if (survivors > MaxLiveSessions)
            {
                foreach (var key in sessions
                             .Where(kv => !toRemove.Contains(kv.Key))
                             .OrderBy(kv => kv.Value.LastWriteUtc)
                             .Take(survivors - MaxLiveSessions)
                             .Select(kv => kv.Key))
                {
                    toRemove.Add(key);
                }
            }

            // 3) Apply removals — drop the StreamSession and its paired SessionPlayout together.
            foreach (var key in toRemove)
            {
                if (sessions.Remove(key, out var session)) session.Dispose();
                playoutEngine.RemoveSession(key.Endpoint, key.StreamId);
                diagnosticSink?.Invoke($"stream session pruned: {key.Endpoint} stream={key.StreamId}");
            }

            // Surface the live-session count whenever it changes, so any accumulation is
            // visible in the log without summing open/prune events.
            if (sessions.Count != lastLoggedSessionCount)
            {
                lastLoggedSessionCount = sessions.Count;
                diagnosticSink?.Invoke($"stream sessions live: {sessions.Count}");
            }
        }
    }

    private void DisposeAllSessionsLocked()
    {
        foreach (var s in sessions.Values) s.Dispose();
        sessions.Clear();
    }

    /// <summary>
    /// Whether we have a recent audio stream session from the given peer IP. "Recent" matches the
    /// playout-engine's idle-prune timeout — i.e. a session whose last write is within
    /// <see cref="SessionIdleTimeout"/>. Compares on IP only, not port (incoming packets carry the
    /// sender's outbound source port, which won't equal their announced audio port). Lockless and
    /// safe to call from any thread.
    /// </summary>
    public bool IsReceivingFromAddress(IPAddress address)
    {
        var now = DateTime.UtcNow;
        foreach (var sp in playoutEngine.ActiveSessions)
        {
            if (!sp.Endpoint.Address.Equals(address)) continue;
            if (now - sp.LastWriteUtc <= SessionIdleTimeout) return true;
        }
        return false;
    }

    /// <summary>
    /// The codec format being received from the given peer IP, or null if no recent session.
    /// Useful for surfacing "we're receiving Opus 10ms from this peer" in the UI.
    /// </summary>
    public AudioFormatInfo? ActiveFormatFromAddress(IPAddress address)
    {
        var now = DateTime.UtcNow;
        SessionPlayout? freshest = null;
        foreach (var sp in playoutEngine.ActiveSessions)
        {
            if (!sp.Endpoint.Address.Equals(address)) continue;
            if (now - sp.LastWriteUtc > SessionIdleTimeout) continue;
            if (freshest is null || sp.LastWriteUtc > freshest.LastWriteUtc) freshest = sp;
        }
        if (freshest is null) return null;
        lock (sessionsLock)
        {
            if (sessions.TryGetValue((freshest.Endpoint, freshest.StreamId), out var session))
            {
                return session.Format;
            }
        }
        return null;
    }

    /// <summary>
    /// The format of every currently-active (non-idle) receive session from the given peer IP — one
    /// entry per stream, i.e. per capture device the peer is sending. Each carries its sample rate,
    /// codec and lane (WASAPI vs ASIO), so the UI can say "sending N devices on ASIO at 48 kHz, Opus"
    /// with no protocol change. Empty when nothing is arriving from that peer.
    /// </summary>
    public IReadOnlyList<AudioFormatInfo> ActiveFormatsFromAddress(IPAddress address)
    {
        var now = DateTime.UtcNow;
        var fresh = new List<SessionPlayout>();
        foreach (var sp in playoutEngine.ActiveSessions)
        {
            if (!sp.Endpoint.Address.Equals(address)) continue;
            if (now - sp.LastWriteUtc > SessionIdleTimeout) continue;
            fresh.Add(sp);
        }
        var formats = new List<AudioFormatInfo>();
        lock (sessionsLock)
        {
            foreach (var sp in fresh)
                if (sessions.TryGetValue((sp.Endpoint, sp.StreamId), out var session) && session.Format is not null)
                    formats.Add(session.Format);
        }
        return formats;
    }

    /// <summary>
    /// The wire codec of the freshest currently-active receive session across all peers, or
    /// null when nothing is being received. Surfaced in the SNAP log's Codec column so a
    /// receive-only node reports what it is actually decoding rather than its dormant send-codec
    /// setting (the old behaviour logged "Pcm" for a node receiving "PCM over Opus"). Lockless
    /// scan for the freshest session, then a short lock to read its codec.
    /// </summary>
    public AudioTransportCodec? ActiveReceiveCodec
    {
        get
        {
            var now = DateTime.UtcNow;
            SessionPlayout? freshest = null;
            foreach (var sp in playoutEngine.ActiveSessions)
            {
                if (now - sp.LastWriteUtc > SessionIdleTimeout) continue;
                if (freshest is null || sp.LastWriteUtc > freshest.LastWriteUtc) freshest = sp;
            }
            if (freshest is null) return null;
            lock (sessionsLock)
            {
                if (sessions.TryGetValue((freshest.Endpoint, freshest.StreamId), out var session))
                {
                    return session.Codec;
                }
            }
            return null;
        }
    }

    // === Packet routing (called on network thread) ===

    /// <summary>Hook for Heartbeat packets that arrive on the audio receiver's socket. The
    /// App wires this to <see cref="HeartbeatService.HandleInjectedPacket"/>. Set this *before*
    /// starting the receiver, otherwise heartbeats arriving on this socket will be silently
    /// dropped as unknown packet type. In single-port mode (the only mode since 2026-05-06)
    /// every heartbeat reaches us via this hook — the audio sender writes to the peer's
    /// audio port, which is this receiver's bound socket; there is no separate heartbeat
    /// socket on either end any more.</summary>
    public Action<byte[], int, IPEndPoint>? OnHeartbeatReceived { get; set; }

    /// <summary>Hook for Control packets that arrive on the audio receiver's socket. The
    /// App wires this to a handler that validates the source against the allow-list (the
    /// peer must be in the user's selected-peers set), checks the user's "accept remote
    /// volume commands" preference, and applies the requested change to the local volume
    /// slider. Set this BEFORE starting the receiver; null = packet is silently dropped.
    /// Travels on the same UDP socket as audio + heartbeat (single-port model 2026-05-07).</summary>
    public Action<RemoteControlKind, sbyte, IPEndPoint>? OnRemoteControlReceived { get; set; }

    private void HandleRawPacket(byte[] packet, int length, IPEndPoint remote)
    {
        Interlocked.Increment(ref packetsReceived);
        Interlocked.Add(ref bytesReceived, length);

        var packetSpan = packet.AsSpan(0, length);
        if (!RemPacket.TryReadHeader(packetSpan, out var type, out var streamId, out var sequence))
        {
            Interlocked.Increment(ref packetsDropped);
            return;
        }

        var payload = packetSpan[RemPacket.HeaderSize..];
        switch (type)
        {
            case RemPacketType.Format:
                HandleFormat(remote, streamId, payload);
                break;
            case RemPacketType.Audio:
                HandleAudio(remote, streamId, sequence, payload);
                break;
            case RemPacketType.KeepAlive:
                // Informational only at this layer.
                break;
            case RemPacketType.Heartbeat:
                // Route to the heartbeat service via the App-supplied delegate. In single-port
                // mode this is the primary inbound path for heartbeats (the heartbeat service
                // no longer binds its own socket). The hook MUST be wired before Start();
                // otherwise heartbeats are dropped and peer health stays "unreachable".
                OnHeartbeatReceived?.Invoke(packet, length, remote);
                break;
            case RemPacketType.Control:
                // Remote-control message (volume up/down, mute toggle). Parse the payload
                // here so the handler doesn't need to know about RemPacket layout. Caller
                // is expected to gate on allow-list AND the user's opt-in preference.
                if (RemPacket.TryReadControl(payload, out var ctrlKind, out var ctrlDelta))
                {
                    OnRemoteControlReceived?.Invoke(ctrlKind, ctrlDelta, remote);
                }
                else
                {
                    Interlocked.Increment(ref packetsDropped);
                }
                break;
            default:
                Interlocked.Increment(ref packetsDropped);
                break;
        }
    }

    /// <summary>
    /// Inject a packet that arrived on a non-listener socket (e.g. the AudioSender's socket
    /// in relay mode). Runs the same dispatch logic as the listener thread. Caller is
    /// responsible for filtering out packet types it has handled itself (typically Heartbeat,
    /// which goes to <see cref="HeartbeatService"/>) — passing a Heartbeat packet here is
    /// safe (it'll be counted and dropped) but wasteful.
    /// </summary>
    public void InjectExternalPacket(byte[] packet, int length, IPEndPoint remote)
    {
        HandleRawPacket(packet, length, remote);
    }

    private void HandleFormat(IPEndPoint remote, ushort streamId, ReadOnlySpan<byte> payload)
    {
        // Single-port mode: the listener stays bound when playback is off (so heartbeats
        // keep flowing on the same socket), but Format/Audio are dropped without opening a
        // session. Doing this BEFORE the format-parse keeps the malformed-packet counter
        // honest — disabled-playback drops aren't a malformedness signal.
        if (!playbackEnabled) return;

        if (!RemPacket.TryReadFormat(payload, out var format, out var peerFingerprint))
        {
            Interlocked.Increment(ref packetsDropped);
            return;
        }

        if (!IsSenderAllowed(remote))
        {
            // Sender isn't in the user's selected-peers set. Don't open a session, don't play
            // their audio. They'll appear in discovery / heartbeat as a peer the user can tick
            // if they want; until then, silence on our side. Counted separately so it shows in
            // diagnostics without inflating the generic "drops" stat.
            Interlocked.Increment(ref packetsRejectedNotAllowed);
            return;
        }

        // Record whether this (selected) peer's profile password matches ours, from the
        // fingerprint they advertised in this format packet. The app reads this to tell the user
        // about a password mismatch (or an out-of-date peer) instead of leaving silence a mystery.
        UpdatePeerSecurity(remote.Address, peerFingerprint);

        SessionPlayout sp;
        StreamSession? newSession = null;
        bool isNewSession = false;
        bool isFormatChange = false;
        // Older sessions from the same peer that are being replaced because we're in
        // single-stream mode (AllowMultipleStreamsPerPeer=false) and the sender rotated
        // its streamId (codec change / engine restart). Disposed AFTER releasing the
        // sessionsLock so their tear-down doesn't extend the critical section.
        List<StreamSession>? supersededByStreamIdChange = null;

        var key = (remote, streamId);
        lock (sessionsLock)
        {
            sessions.TryGetValue(key, out var existing);
            if (existing is not null && existing.MatchesFormat(remote, streamId, format))
            {
                return; // same session; nothing to do
            }

            sp = playoutEngine.GetOrCreateSession(remote, streamId, MaxBufferCapacityBytes(MaxLatencyForSizingMs));
            // Tag the session with the wire-announced render route. For classic-mode senders
            // (or pre-2026-05-11 builds) this is always Mixed and PlayoutEngine treats the
            // session exactly as it always did. BothIndependent senders will tag their two
            // lanes with WasapiLane / AsioLane so the per-route surfaces direct each lane to
            // the matching render backend without mixing. Updated unconditionally so an
            // in-place format change can re-route a session (e.g. a sender that mistakenly
            // started in classic mode and re-announces with the right lane mid-stream).
            sp.Route = format.Lane;

            if (existing is null)
            {
                isNewSession = true;
            }
            else
            {
                // Same (endpoint, streamId), different format (codec change within the same lane).
                // Replace the StreamSession but keep its SessionPlayout — buffered audio drains
                // naturally and avoids a gap. Matches the behaviour the single-source code
                // preserved for codec switches.
                existing.Dispose();
                isFormatChange = true;
            }

            newSession = new StreamSession(remote, streamId, format, sp, diagnostics, _ => sp.NoteFramesQueued(playoutEngine.TargetLatencyMs), decryptor);
            sessions[key] = newSession;

            // Same-lane streamId rotation: drop other sessions from this peer that share the
            // SAME render route as the new format. The sender rotates streamId on codec
            // changes and engine restarts; the old session sits empty otherwise, racking up
            // phantom underruns from render-thread polling. The lane-match qualifier is
            // critical for BothIndependent mode (added 2026-05-11) where the same peer
            // legitimately produces TWO concurrent streamIds — one per lane — and each lane's
            // Format-resend packets must NOT supersede the other lane's session. Without the
            // lane match, the two lanes' 250 ms format announces took turns killing each
            // other 8× per second, neither lane could stay alive long enough to arm, and
            // BothIndependent appeared to "produce no audio" on the receiver. AllowMultiple-
            // StreamsPerPeer is preserved as an override knob (default false) for unusual
            // setups; even with it true, lane-mismatched sessions would still coexist, so the
            // flag now only governs same-lane-different-streamId behaviour.
            if (!AllowMultipleStreamsPerPeer)
            {
                foreach (var (otherKey, otherSession) in sessions)
                {
                    if (otherKey.Endpoint.Equals(remote)
                        && otherKey.StreamId != streamId
                        && otherSession.Format.Lane == format.Lane)
                    {
                        supersededByStreamIdChange ??= [];
                        supersededByStreamIdChange.Add(otherSession);
                    }
                }
                if (supersededByStreamIdChange is not null)
                {
                    foreach (var s in supersededByStreamIdChange)
                    {
                        sessions.Remove((s.Endpoint, s.StreamId));
                    }
                }
            }
        }

        if (supersededByStreamIdChange is not null)
        {
            foreach (var s in supersededByStreamIdChange)
            {
                playoutEngine.RemoveSession(s.Endpoint, s.StreamId);
                s.Dispose();
                diagnosticSink?.Invoke($"stream session superseded (sender rotated streamId): {s.Endpoint} oldStream={s.StreamId} newStream={streamId}");
            }
        }

        if (isNewSession)
        {
            // Reset the global inter-packet / inter-render-callback gap timers. If we don't,
            // the first audio packet of this new session records a gap measured from the LAST
            // packet of the previous session — which on a mode switch or codec change can be
            // tens of seconds of user-idle time. That bogus gap then feeds the auto-tune's
            // recent-gap window and makes it recommend an absurd latency target (e.g. 27 s
            // observed → recommendation clamped to 200 ms hard cap → fresh session never
            // arms because its buffer can't reach 200 ms before underrun). 2026-05-11 fix.
            diagnostics.ResetGapMeasurements();
            Interlocked.Increment(ref sessionsOpenedCount);
            diagnosticSink?.Invoke($"stream session opened: {remote} stream={streamId} {format}");
        }
        else if (isFormatChange)
        {
            diagnosticSink?.Invoke($"stream format changed: {remote} stream={streamId} {format}");
        }
    }

    private long sessionsOpenedCount;
    /// <summary>
    /// Monotonic count of new <c>StreamSession</c> instances opened since this receiver
    /// started. Exposed so the App can detect a fresh session and reset its rolling
    /// observation windows (recentMaxGaps etc.) — see the matching reset in MainForm's
    /// SNAP loop. Increments only on truly-new sessions, not on format-change-keep-buffer.
    /// </summary>
    public long SessionsOpenedCount => Interlocked.Read(ref sessionsOpenedCount);

    private void HandleAudio(IPEndPoint remote, ushort streamId, uint sequence, ReadOnlySpan<byte> payload)
    {
        // See HandleFormat — same single-port gate. We drop Audio packets silently when
        // playback is off; the underlying NAT pinhole / heartbeat path isn't affected since
        // Heartbeat packets are dispatched in HandleRawPacket before reaching here.
        if (!playbackEnabled) return;
        if (!IsSenderAllowed(remote))
        {
            Interlocked.Increment(ref packetsRejectedNotAllowed);
            return;
        }
        // Make sure the decryptor reflects the current profile password before the session
        // tries to decrypt (cheap reference check; rebuild only happens on a password change).
        decryptor.EnsureKey(audioKey);
        StreamSession? session;
        lock (sessionsLock)
        {
            sessions.TryGetValue((remote, streamId), out session);
        }
        // Key lookup guarantees streamId match — kept the defensive check anyway in case of
        // future restructuring (cheap and clarifies intent).
        if (session is null) return;
        if (session.StreamId != streamId) return;
        if (!session.HandleAudioPayload(sequence, payload))
        {
            Interlocked.Increment(ref packetsDropped);
        }
    }

    private static int MaxBufferCapacityBytes(int maxLatencyMs) =>
        Math.Max(maxLatencyMs * CapacityHeadroomMultiplier * MixBytesPerSecond / 1000, 64 * 1024);
}
