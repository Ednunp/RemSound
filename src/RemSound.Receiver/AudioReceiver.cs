using System.Diagnostics;
using System.Net;
using RemSound.Core;

namespace RemSound.Receiver;

/// <summary>
/// Public façade for the receiver pipeline. Routes raw packets from <see cref="NetworkListener"/>
/// to one <see cref="StreamSession"/> per incoming stream (sender endpoint and stream id), all of which write to their own
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

    /// <summary>
    /// The largest capture latency any peer sending to us has ANNOUNCED, or 0 if nobody has.
    ///
    /// <para>The total-latency readout measures their microphone to your ears, and capture happens on
    /// THEIR machine. A receive-only rig cannot measure that stage, so it used to substitute its own
    /// local WASAPI estimate of 10 ms — and Ed's 2026-08-24 logs show the cost: his laptop reported
    /// <c>capture=10.0[est]</c> for audio whose sending desktop had measured 0.7 ms. Senders now
    /// state the figure in the format packet (see <c>RemPacket.FormatPayloadWithCaptureSize</c>), so
    /// the receiver can report what actually happened rather than what it would have cost here.</para>
    ///
    /// <para>The WORST across peers, matching the sender's own rule: with several people sending, the
    /// readout is a single number and the honest one is the longest journey, not the shortest. Zero
    /// from a peer means "not stated" — an older build, or a device that will not say — and is
    /// skipped rather than counted as a very fast capture, which would quietly flatter the total.</para>
    /// </summary>
    public double AnnouncedCaptureLatencyMs
    {
        get
        {
            var worst = 0.0;
            lock (sessionsLock)
            {
                foreach (var session in sessions.Values)
                {
                    var announced = session.Format.CaptureLatencyMs;
                    if (announced > worst) worst = announced;
                }
            }
            return worst;
        }
    }

    /// <summary>True when audio from ANY peer has hit a live session within the window. The app's
    /// Priority-mode scoping reads this as its "receiving right now" signal (2026-07-26 resource
    /// audit — the power levers now engage only while audio actually moves).</summary>
    public bool AnyRecentAudio(TimeSpan within)
    {
        var cutoff = DateTime.UtcNow - within;
        lock (sessionsLock)
        {
            foreach (var session in sessions.Values)
            {
                if (session.LastWriteUtc >= cutoff) return true;
            }
        }
        return false;
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
                // The PERSON, not the path: a session folded from this peer's other address is still
                // this peer sending. See SessionEndpointFor.
                if (playoutEngine.SamePerson(session.Endpoint.Address, address) && session.LastWriteUtc >= cutoff)
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

    /// <summary>Tell the receiver whether the UI is showing two latency sliders. Public and separate
    /// from <see cref="SetAudioMode"/> because it is NOT a backend operation: a headless/test host
    /// skips the backend switch (it can open a real ASIO driver) and would otherwise leave the lane
    /// policy stuck at its default, which is exactly what made the control suite report a working
    /// ASIO latency box as dead (2026-08-15). Idempotent and cheap.</summary>
    public void SetIndependentLaneLatency(bool independent) => playoutEngine.SetIndependentLaneLatency(independent);

    /// <summary>Read one claimed peer's audio for the plugin that holds it, driven by the DAW's block
    /// size rather than a clock of ours. Returns the frames produced, which can be short while the
    /// peer's buffer is still filling.</summary>
    public int ReadClaimedPeer(IPAddress peer, Span<float> destination, int frames)
        => playoutEngine.ReadClaimedPeer(peer, destination, frames);

    /// <summary>Tell the receiver which addresses belong to the same peer, so a plugin holding one path
    /// of a dual-homed sender still gets them when their audio arrives on the other, and the speakers
    /// still know to stay out of the way. One array per peer; addresses not listed are unaffected.</summary>
    public void SetPeerAddressGroups(IReadOnlyList<IPAddress[]>? groups) => playoutEngine.SetPeerAddressGroups(groups);

    /// <summary>Create the playout state an arriving stream would get, so a test can assert WHICH
    /// LANE it lands on — the relationship the 2026-08-14 dead-slider bug broke (a stream tagged with
    /// one lane while the slider wrote another). Internal: a diagnostic seam, not app API.</summary>
    internal SessionPlayout GetOrCreateSessionForTest(IPEndPoint remote, ushort streamId) =>
        playoutEngine.GetOrCreateSession(remote, streamId, MaxBufferCapacityBytes(MaxLatencyForSizingMs));

    /// <summary>How many stream sessions are live right now. Test seam: the gate proves that THREE
    /// concurrent streams from one sender coexist (three streamIds, three distinct Lane bytes) and
    /// that the supersede rule still fires when two of them share a lane. Two concurrent streams is
    /// what BothIndependent ships; three is what a plugin lane alongside both capture lanes would
    /// need, and "the code looks like it should cope" is not the same thing as knowing it does.
    /// Nothing in the app reads this — the live count is otherwise visible only in a log line.</summary>
    internal int LiveSessionCountForTest { get { lock (sessionsLock) return sessions.Count; } }

    /// <summary>Which output lanes have a ticked device. Normally derived by CompositeRenderBackend
    /// from <see cref="SetOutputDevices"/> — exposed because this, NOT the audio mode, is what decides
    /// which lane an incoming stream is tagged with, and therefore which latency control governs it.
    /// A user with an ASIO driver chosen but only ASIO outputs ticked is a genuinely different
    /// configuration from one with both kinds ticked, and the control suite has to be able to build
    /// all three without opening real hardware (Ed, 2026-08-15 — the suite covered two of three).</summary>
    public void SetActiveOutputLanes(bool wasapiActive, bool asioActive)
    {
        playoutEngine.SetLaneActive(RenderRoute.WasapiLane, wasapiActive);
        playoutEngine.SetLaneActive(RenderRoute.AsioLane, asioActive);
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
        // Two latency sliders exist in BothIndependent and nowhere else; every other mode has ONE
        // slider, so one latency value governs every session (see PlayoutEngine.independentLanes —
        // resolving it per output lane is what made the single slider inert, 2026-08-14).
        playoutEngine.SetIndependentLaneLatency(mode == AudioMode.BothIndependent);
        if (wasRunning) multiOutput.Start();
    }

    /// <summary>Sets the Buffer-smoothness knob (1 = aggressive — clicks the buffer back
    /// to target on any drift, holds the user's latency tightly; 10 = smooth — no clicks
    /// but the queue can creep up under jitter or sustained clock drift). Knob drives a
    /// click-based DropOldest trim in <see cref="SessionPlayout.ReadFloats"/>. This is mostly a
    /// safety knob — SessionPlayout's drift resampler keeps the buffer near target so the trim
    /// should rarely fire regardless of this value.</summary>
    public void SetSmoothness(int value) => playoutEngine.SetSmoothness(value);

    /// <summary>Whether incoming audio is being played. Read-back for the control suite (the Receive
    /// audio control must reach this), alongside SetPlaybackEnabled which drives it.</summary>
    public bool PlaybackEnabled => playbackEnabled;
    /// <summary>The measured render-callback period for ONE lane. The latency estimate reads the
    /// lane the user is actually listening on: ASIO's whole point is that it is NOT penalised by
    /// WASAPI's shared-mode buffering, so quoting a single global period would hand an ASIO listener
    /// a WASAPI-sized number for audio that never goes near WASAPI (Ed, 2026-08-15).</summary>
    public int MaxRenderCallbackGapMsFor(RenderRoute route) => diagnostics.MaxRenderCallbackGapMsFor(route);

    /// <summary>Both output figures PER LANE. WASAPI and ASIO run at the same time and the readout
    /// reports them apart, so neither may be answered with the other's number.
    /// See <see cref="IRenderBackend.ReportedOutputLatencyMsFor"/>. 2026-08-24.</summary>
    public double ReportedOutputLatencyMsFor(RenderRoute route) => multiOutput.ReportedOutputLatencyMsFor(route);

    /// <inheritdoc cref="ReportedOutputLatencyMsFor"/>
    public double OutputQueueMsFor(RenderRoute route) => multiOutput.OutputQueueMsFor(route);

    /// <summary>One reading per live WASAPI output stage — the stage the ASIO lane does not have.
    /// Feeds the long-run report; see <see cref="WasapiOutputStage"/> for why it earns its own
    /// line.</summary>
    public IReadOnlyList<WasapiOutputStage> OutputStages() => multiOutput.OutputStages();

    /// <summary>Is this lane actually playing audio right now — ticked AND being read? The auto-tune
    /// uses it to notice a lane coming back after an absence, at which point what it learned about the
    /// departed device is no longer evidence. See <see cref="LaneActivity"/>.</summary>
    public bool LaneIsConsuming(RenderRoute route) => playoutEngine.LaneIsConsuming(route);

    /// <summary>Live read-back of smoothness / gap-artifact, for the control suite to prove those
    /// controls reach the audio path rather than merely persisting (see PlayoutEngine.SmoothnessValue).</summary>
    public int SmoothnessValue => playoutEngine.SmoothnessValue;
    public ConcealmentArtifact ConcealmentArtifactValue => playoutEngine.ConcealmentArtifactValue;

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

    /// <summary>The per-peer tap currently installed, so the gate can drive it with known audio for a
    /// known peer. Multi-peer recording cannot otherwise be tested without real peers on a real
    /// network — which is how it went untested until Ed asked (2026-08-15).</summary>
    internal Action<IPEndPoint, ReadOnlyMemory<float>>? PeerRecordTapForTest => playoutEngine.RecordTapForTest;

    /// <summary>Fired once per rendered block, right after <see cref="OnReceivedSamples"/> — the block
    /// boundary a single-file bypass recording uses to flush its per-peer sum.</summary>
    public Action<int>? OnRecordBlockComplete
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
    /// fills. Discovery (its own UDP port) and heartbeats (this socket, dispatched before the
    /// gate) are unaffected, so non-allowed peers still appear as "discovered" in the UI ready
    /// to be ticked.
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
            // Ceiling (2026-07-26 resource audit): keyed by source IP with no eviction, this was
            // the one table that could creep over a very long run if the receiver sits unfiltered
            // (null allow-list) on a WAN-exposed port collecting one entry per distinct sender.
            // It's a status cache, so the cheap fix is honest: at the cap, drop entries for
            // strangers rather than grow. Normal use (allow-listed peers) never gets near 256.
            if (peerSecurity.Count >= 256 && !peerSecurity.ContainsKey(address))
            {
                peerSecurity.Clear();
                diagnosticSink?.Invoke("peer-security cache hit its ceiling (256 distinct senders) — cleared; statuses repopulate from live format packets");
            }
            peerSecurity[address] = status;
        }
    }

    /// <summary>The relay behind a relay-group member's address (RelayGroupClient.RelayOf), or null for anyone else. A
    /// member of a selected relay's group is let in just as the relay itself is: choosing the relay and the password is
    /// choosing everyone who shares them there.</summary>
    public Func<IPEndPoint, IPEndPoint?>? RelayOfMember { get; set; }

    private bool IsSenderAllowed(IPEndPoint remote)
    {
        var snapshot = allowedSenders;
        if (snapshot is null) return true; // null = no filter
        if (snapshot.Contains(remote.Address)) return true;
        return RelayOfMember?.Invoke(remote) is { } relay && snapshot.Contains(relay.Address);
    }

    /// <summary>Test seam: would audio from this address be let in right now? The same question the receive path asks.</summary>
    internal bool IsSenderAllowedForTest(IPEndPoint remote) => IsSenderAllowed(remote);

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

    /// <summary>The same figure split BY LANE, taken in one call so no lane can be starved by a
    /// second reader. Answers "which lane spent the render time" — the question a machine-wide total
    /// cannot, and the one that matters the moment one lane starts under-running while the other is
    /// fine. See <see cref="PlayoutEngine.TakeRenderTicksByRoute"/>. 2026-08-24.</summary>
    public (double WasapiMs, double AsioMs, double MixedMs) TakeRenderWorkMsByLane()
    {
        var (w, a, m) = playoutEngine.TakeRenderTicksByRoute();
        var scale = 1000.0 / Stopwatch.Frequency;
        return (w * scale, a * scale, m * scale);
    }

    public string OutputDeviceName => multiOutput.ActiveDeviceSummary;
    /// <summary>Hand the receiver the plugin claim registry, so peers taken over by a VST instance
    /// stop coming out of this machine's speakers (see PluginPeerClaims).</summary>
    public void SetPluginPeerClaims(PluginPeerClaims? claims) => playoutEngine.SetPluginPeerClaims(claims);

    public int CurrentBufferMs => playoutEngine.CurrentBufferMs;

    /// <summary>Queue depth for one lane — WASAPI and ASIO are separate journeys and must be reported
    /// apart (see PlayoutEngine.CurrentBufferMsFor).</summary>
    public int CurrentBufferMsFor(RenderRoute route) => playoutEngine.CurrentBufferMsFor(route);
    public int TargetLatencyMs => playoutEngine.TargetLatencyMs;

    /// <summary>
    /// Frame duration of the active streams (PCM frames are 233 samples, just under 5 ms, from a current
    /// sender, 240 from an older one, or 120 in Tight; Opus frames
    /// are whatever the sender chose). null when no stream is active. With multiple streams this
    /// picks the largest frame duration as the
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

    /// <summary>Cross-buffer (packet-boundary) max post-decode single-sample step across all active
    /// stream sessions since the last call, draining each session's cross-buffer counter. Used by
    /// the diag log to pinpoint where in the pipeline audio discontinuities are being introduced.
    /// 2026-05-21 addition for the click hunt.</summary>
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
    /// the buffer / disarm the session. SessionPlayout's depth correction walks the buffer
    /// down gradually instead. Used by auto-tune so its slider
    /// adjustments are inaudible — the user didn't ask for an immediate change and shouldn't
    /// hear one. On a RAISE behaves identically to the regular setter (no drain ever fires
    /// on raise).</summary>
    public void SetMaxLatencyMsSoft(int value) =>
        playoutEngine.SetMaxLatencyMs(value, drainOnLower: false);

    /// <summary>Per-route latency accessors — used in BothIndependent mode where the WASAPI
    /// lane and the ASIO lane each have their own slider. With one slider every route resolves
    /// to the one shared value (see PlayoutEngine.LatencyFor).</summary>
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
    /// <summary>Whether a peer claimed by a DAW plugin arrives carrying the pan and EQ set for them
    /// here, or raw for the DAW to shape itself. The menu item behind this did nothing at all until
    /// 2026-09-06 — see PlayoutEngine.SetPluginShaping.</summary>
    public void SetPluginShaping(bool applyShaping) => playoutEngine.SetPluginShaping(applyShaping);

    /// <summary>True when at least one stream session is currently tagged for this route —
    /// used by MainForm's continuous auto-tune to skip routes with no audio in flight, so a
    /// lane's auto-tune can't pre-inflate its target by reacting to shared network-gap data
    /// from a different lane's packets.</summary>
    public bool HasSessionsForRoute(RenderRoute route) => playoutEngine.HasSessionsForRoute(route);

    /// <summary>Test seam: report a faulted output without a real device having to die. An
    /// <c>OutputEntry</c> needs a live MMDevice and WasapiOut, so the fault itself cannot be staged —
    /// but the CHAIN from "an output faulted" to "the app re-applies its devices" can be, and that
    /// chain is the part that failed in the field on 2026-08-27 while the pure decision it feeds was
    /// green. 2026-08-27.</summary>
    internal bool ForceFaultedOutputForTest;

    /// <summary>Any output device sitting dead after WASAPI invalidated it mid-stream. The app's
    /// per-second tick watches this and re-applies the device set, which re-opens it — recovery that
    /// used to depend on a hot-plug notification that never arrives when the device stays present.
    /// See <see cref="MultiOutputPlayout.HasFaultedOutput"/>. 2026-08-26.</summary>
    public bool HasFaultedOutput =>
        ForceFaultedOutputForTest || (multiOutput is CompositeRenderBackend composite && composite.HasFaultedOutput);

    /// <summary>The output device ids actually OPEN right now, across both lanes. Compared against
    /// what the user has ticked, this is how the app spots a device that is wanted but never opened —
    /// the state a wireless device leaves behind when it comes back later than the resume re-init.
    /// 2026-08-26.</summary>
    public IReadOnlyList<string> ActiveOutputDeviceIds => multiOutput.ActiveDeviceIds;

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
    /// <summary>The worst single-sample step out of the ring buffer (after decode +
    /// SessionPlayout.Write, before resampler) since the last call, split cross/within buffer.</summary>
    public float TakeMaxPostRingReadStepCrossBuffer() => playoutEngine.TakeMaxPostRingReadStepCrossBuffer();
    public float TakeMaxPostRingReadStepWithinBuffer() => playoutEngine.TakeMaxPostRingReadStepWithinBuffer();
    /// <summary>The worst single-sample step out of the resampler since the last call, split
    /// cross/within buffer.</summary>
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
    /// Sets the list of output devices to render received audio to. Every device in this list
    /// plays the received streams — pass an empty list to mute all output without stopping the
    /// receive path. Which devices are ticked, and whether that is remembered, is the app's
    /// business.
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

        // Tear down any sessions left over from a previous Start (in case Stop wasn't called). Under
        // sessionsLock to match the method's "Locked" contract and its other two callers — the counter
        // getters / prune run on the App's snapshot-tick thread and touch `sessions` under this lock, so
        // clearing it bare would be an unsynchronised mutation of the non-thread-safe dictionary.
        lock (sessionsLock)
        {
            DisposeAllSessionsLocked();
        }
        playoutEngine.ResetAll();

        listener.Start(udpPort);
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
            if (!playoutEngine.SamePerson(sp.Endpoint.Address, address)) continue;
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
            if (!playoutEngine.SamePerson(sp.Endpoint.Address, address)) continue;
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
            if (!playoutEngine.SamePerson(sp.Endpoint.Address, address)) continue;
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

    /// <summary>Hook for relay address-proof cookies (AddrCheck, 2026-07-27). The App echoes the
    /// packet back to its source via the sender's socket — same single-port model as heartbeats.</summary>
    public Action<byte[], int, IPEndPoint>? OnAddrCheckReceived { get; set; }

    /// <summary>Hook for Control packets that arrive on the audio receiver's socket. Since 5.6 the
    /// payload is SEALED with the profile's audio key (ControlSealing), so this hands the RAW payload
    /// up — the App authenticates it (key + replay guard), validates the source against the
    /// allow-list, checks the user's "accept remote volume commands" preference, and applies the
    /// change. Set this BEFORE starting the receiver; null = packet is silently dropped.
    /// Travels on the same UDP socket as audio + heartbeat (single-port model 2026-05-07).</summary>
    public Action<byte[], IPEndPoint>? OnRemoteControlReceived { get; set; }

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
            case RemPacketType.AddrCheck:
                // Relay address-proof cookie (2026-07-27): hand the raw packet up so the app can
                // echo it back to the relay verbatim — proving this address really receives, which
                // is what unlocks relay forwarding once the relay enforces. No parsing needed here.
                OnAddrCheckReceived?.Invoke(packet, length, remote);
                break;
            case RemPacketType.Control:
                // Remote-control message (volume up/down, mute toggle). Since 5.6 the payload is
                // SEALED with the profile's audio key (ControlSealing) — the receiver stays
                // crypto-dumb and hands the raw payload up; the App authenticates it against its
                // key + replay guard, then gates on allow-list AND the user's opt-in preference.
                // A legacy 2-byte plaintext payload (pre-5.6 peer) fails the size check here and
                // is dropped — an unauthenticated command must never reach the handler.
                if (payload.Length == ControlSealing.SealedPayloadBytes)
                {
                    OnRemoteControlReceived?.Invoke(payload.ToArray(), remote);
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
    /// which goes to <see cref="HeartbeatService"/>) — a Heartbeat packet passed here is routed to
    /// <see cref="OnHeartbeatReceived"/> exactly as one from the listener would be.
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

        // FAIL CLOSED on a format we cannot decode. Every field here arrives from the network in a
        // packet that is neither encrypted nor authenticated, and until now none of them were
        // range-checked before use. See AudioFormatInfo.IsUsable for what this rejects and why it is
        // cross-port safe. Rate-limited because a spoofed or corrupted stream would otherwise write
        // one line per packet. 2026-08-23 audit, finding R4.
        if (!format.IsUsable(out var formatProblem))
        {
            Interlocked.Increment(ref packetsDropped);
            NoteUnusableFormat(remote, formatProblem);
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

        // ONE STREAM IS ONE SESSION, however many of the peer's addresses it arrives on. See
        // SessionEndpointFor: without this, a peer who is on the LAN and on a VPN at the same time
        // opens two sessions for one stream and each gets a fraction of the packets.
        remote = SessionEndpointFor(remote, streamId, out _);

        SessionPlayout sp;
        StreamSession? newSession = null;
        bool isNewSession = false;
        bool isFormatChange = false;
        // Older sessions from the same peer, on the same lane, that are being replaced because
        // the sender rotated its streamId (codec change / engine restart). Disposed AFTER releasing
        // the sessionsLock so their tear-down doesn't extend the critical section.
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
            // Output routing is now owned by PlayoutEngine: every received stream is fanned out to EVERY
            // active output lane (a primary plus a mirror replica per extra lane), so the sender's
            // captured-lane tag (format.Lane) no longer decides where the audio plays — it plays on all
            // the receiver's outputs. GetOrCreateSession assigns each replica its output lane. (The
            // sender still announces format.Lane on the wire for back-compat; the receiver ignores it
            // for routing.)

            isNewSession = existing is null;
            isFormatChange = existing is not null;

            try
            {
                // BUILD THE REPLACEMENT FIRST, dispose the old one only once it succeeded.
                //
                // This used to call existing.Dispose() before constructing, and on a constructor
                // failure the catch removed the playout and rethrew — leaving `sessions[key]`
                // holding a DISPOSED session. Its Opus decoder was gone, so every audio packet
                // dropped; and the next GOOD format packet matched that disposed session's original
                // format on the MatchesFormat early-return above, so it was never replaced. The peer
                // stayed silent until PruneIdleSessions reaped it four seconds later. A single
                // malformed format packet therefore cost four seconds of that peer's audio.
                // 2026-08-23 audit, finding R5. The format validation in RemPacket.TryReadFormat
                // (finding R4) makes reaching this catch much harder; this makes reaching it cheap.
                //
                // Arm against the target THIS session actually plays to (its own route's), not the
                // engine-wide one — in BothIndependent those differ, and arming to the wrong one
                // starts playback at the wrong depth. In single-slider mode every route resolves to
                // the same shared value, so this is identical to the old call there.
                newSession = new StreamSession(remote, streamId, format, sp, diagnostics,
                    _ => sp.NoteFramesQueued(
                        playoutEngine.TargetLatencyMsFor(sp.Route),
                        // Each mirror replica arms against ITS OWN output lane's slider, not the
                        // primary's — see SessionPlayout.NoteFramesQueued (finding R6).
                        mirrorRoute => playoutEngine.TargetLatencyMsFor(mirrorRoute)),
                    decryptor);
            }
            catch (Exception ex)
            {
                if (existing is not null)
                {
                    // Keep the working session exactly as it was. The sender re-announces its format
                    // every 250 ms, so a one-off bad packet costs nothing at all now.
                    diagnosticSink?.Invoke($"stream format rejected, keeping the existing session: {remote} stream={streamId} — {ex.GetType().Name}: {ex.Message}");
                    return;
                }
                // No previous session to fall back on. GetOrCreateSession already registered a
                // SessionPlayout in PlayoutEngine and nothing was added to `sessions`, so
                // PruneIdleSessions would never reap it and it would linger forever, summed on every
                // render callback and poisoning the auto-tune's underrun stats. Reap it here.
                playoutEngine.RemoveSession(remote, streamId);
                diagnosticSink?.Invoke($"stream session rejected (bad format): {remote} stream={streamId} — {ex.GetType().Name}: {ex.Message}");
                Interlocked.Increment(ref packetsDropped);
                return;
            }
            // The replacement is live; now retire the one it replaces. Its SessionPlayout is kept
            // (buffered audio drains naturally and avoids a gap), which is what the single-source
            // code did for codec switches.
            existing?.Dispose();
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
            // BothIndependent appeared to "produce no audio" on the receiver.
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

    // Rate limit for the unusable-format line. A peer resends its format four times a second, and a
    // spoofed or corrupted stream could arrive far faster than that; one line per packet would bury
    // the log. First occurrence, then at most one every five seconds, with a count of the rest.
    private long lastBadFormatLogTicks;
    private long badFormatsSuppressed;
    private long badFormatsTotal;

    /// <summary>Cumulative Format packets rejected for announcing something undecodable. Surfaced so
    /// a peer that has genuinely gone wrong is visible as a number rather than only as silence.</summary>
    public long FormatPacketsRejected => Interlocked.Read(ref badFormatsTotal);

    private void NoteUnusableFormat(IPEndPoint remote, string problem)
    {
        Interlocked.Increment(ref badFormatsTotal);
        var now = Stopwatch.GetTimestamp();
        var prev = Volatile.Read(ref lastBadFormatLogTicks);
        if (prev != 0 && now - prev < Stopwatch.Frequency * 5)
        {
            Interlocked.Increment(ref badFormatsSuppressed);
            return;
        }
        Volatile.Write(ref lastBadFormatLogTicks, now);
        var suppressed = Interlocked.Exchange(ref badFormatsSuppressed, 0);
        diagnosticSink?.Invoke($"format packet rejected from {remote}: {problem}"
            + (suppressed > 0 ? $" ({suppressed} more suppressed in the last 5s)" : ""));
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
        // The same fold as the format path: audio from the peer's other address belongs to the session
        // this stream already has, not to nothing. Without it the packets arriving on the second path
        // were dropped silently here until a format packet opened a second session for them.
        var sessionEndpoint = SessionEndpointFor(remote, streamId, out var merged);
        StreamSession? session;
        lock (sessionsLock)
        {
            sessions.TryGetValue((sessionEndpoint, streamId), out session);
        }
        // Key lookup guarantees streamId match — kept the defensive check anyway in case of
        // future restructuring (cheap and clarifies intent).
        if (session is null) return;
        if (session.StreamId != streamId) return;
        if (merged && !session.IsMultiPath)
        {
            // Once per session, not once per packet. Whoever reads this log next is trying to work out
            // why somebody sounded odd, and "this peer is reaching us two ways" is the sentence that
            // answers it.
            session.NoteAlternatePath();
            diagnosticSink?.Invoke($"receiver: {remote.Address} is the same peer as {sessionEndpoint.Address} on stream {streamId} "
                                 + "- folding both paths onto one session rather than splitting the stream between two");
        }
        if (!session.HandleAudioPayload(sequence, payload))
        {
            Interlocked.Increment(ref packetsDropped);
        }
    }

    /// <summary>
    /// Which session does a packet from this address belong to?
    ///
    /// <para>Normally itself. But a peer can be reachable at two addresses at once — on the LAN and on
    /// a VPN, which is ordinary rather than exotic — and their packets can then reach us from either.
    /// Sessions are keyed by (source endpoint, streamId), so ONE stream became TWO sessions, each fed
    /// a fraction of the packets, each starving and being pruned on the four-second rule, alternately.
    /// That is around a hundred concealments a second for as long as it lasts, and it is what Anthony
    /// Reyers heard as the iPhone breaking up on 2026-09-01.</para>
    ///
    /// <para>None of the existing multi-homing guards covered it, which is worth stating because
    /// believing they did is the mistake that was made: the pinned send endpoint governs where we
    /// SEND, the widened allow-list governs whether inbound audio is ACCEPTED (it was — that is why
    /// two sessions opened), and SetPeerAddressGroups governs whether the mix treats two addresses as
    /// one person. The gap was session KEYING, and this is it.</para>
    ///
    /// <para>Costs nothing when nobody is multi-homed: the exact key is tried first, and the scan
    /// below does not run at all unless the app has told us somebody has two addresses.</para>
    /// </summary>
    /// <param name="merged">True when this packet came in on a path other than the one its session
    /// lives at — the caller tells the session, so it can drop what the other path already delivered.</param>
    private IPEndPoint SessionEndpointFor(IPEndPoint remote, ushort streamId, out bool merged)
    {
        merged = false;
        if (!playoutEngine.HasPeerAddressGroups) return remote;
        lock (sessionsLock)
        {
            if (sessions.ContainsKey((remote, streamId))) return remote;
            foreach (var key in sessions.Keys)
            {
                if (key.Item2 != streamId) continue;
                if (key.Item1.Equals(remote)) continue;
                if (!playoutEngine.SamePerson(key.Item1.Address, remote.Address)) continue;
                merged = true;
                return key.Item1;
            }
        }
        return remote;
    }

    private static int MaxBufferCapacityBytes(int maxLatencyMs) =>
        Math.Max(maxLatencyMs * CapacityHeadroomMultiplier * MixBytesPerSecond / 1000, 64 * 1024);
}
