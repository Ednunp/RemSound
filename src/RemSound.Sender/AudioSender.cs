using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RemSound.Core;

namespace RemSound.Sender;

// PcmPack is in RemSound.Core (used by both Sender and Receiver).

/// <summary>
/// Captures from one or more Windows audio devices via WASAPI (loopback for output devices,
/// direct capture for input devices), mixes them into a single 48 kHz stereo float stream
/// through <see cref="MixingEngine"/>, encodes (PCM 24-bit or Opus), and sends to a configurable
/// set of UDP receivers.
///
/// The mixing engine owns the capture lifecycle and the per-source silence keepalive (needed on
/// USB audio interfaces whose loopback callbacks otherwise stall when no app is rendering — see
/// naudio/NAudio#1110). AudioSender just wires the mixer's mixed-sample callback into the
/// existing PCM/Opus encode + UDP path.
///
/// Threading model: the mixer's tick task delivers 10 ms frames here on its own thread; this
/// class accumulates into PCM 5 ms or Opus 10/20 ms frames and dispatches over UDP. No
/// cross-thread synchronization other than reading a few volatile flags (codec, mute,
/// receiver list).
/// </summary>
public sealed class AudioSender : IDisposable
{
    // PCM frame size is configurable via SendRate. Standard = 5 ms (240 samples = 1440 bytes,
    // single UDP packet under MaxAudioPayloadBytes=1454). Tight = 2.5 ms (120 samples = 720
    // bytes, also single packet). Tight mode adds nothing structurally — same packet shape,
    // just half-size — so the receive-side multipart assembler stays a no-op.
    private const int MixChannels = 2;
    private const int OpusBitrateLan = 192_000;
    private const int PcmStandardSamplesPerChannel = 240;  // 5 ms
    private const int PcmTightSamplesPerChannel = 120;     // 2.5 ms

    // Mutable PCM frame parameters — updated by SetSendRate. Keep them volatile because the
    // hot-path read happens on the audio thread while writes come from the UI thread.
    private volatile int pcmFrameSamplesPerChannel = PcmStandardSamplesPerChannel;
    internal int PcmFrameStereoSamples => pcmFrameSamplesPerChannel * MixChannels;
    internal int PcmFrameSamplesPerChannel => pcmFrameSamplesPerChannel;

    private readonly object configGate = new();
    private ICaptureBackend engine;
    private IReadOnlyList<CaptureSourceSpec> pendingSources = [];
    private readonly UdpClient udp;
    // qWAVE attachment for the outbound UDP socket. Marks our packets at DSCP Voice (EF/46)
    // and gives them NIC-scheduler priority over best-effort traffic. Wins on LAN and Wi-Fi
    // (WMM Voice access category); neutral across the public internet (most ISPs strip DSCP).
    // Always on — no toggle. Failure (qwave.dll missing, QoS service disabled) is logged and
    // ignored; the socket continues unprioritised.
    private readonly NetworkPriority networkPriority = new();
    // Two lanes. defaultLane carries every output in the three classic modes (Mixed route).
    // In BothIndependent mode defaultLane carries WASAPI-only audio (route WasapiLane) and
    // asioLane carries ASIO-only audio (route AsioLane), each producing its own UDP stream
    // tagged with the matching Lane byte so the receiver routes them to per-lane
    // IWaveProvider surfaces. We always construct both lanes — the asio lane sits idle
    // (no capture child wired to it) in classic modes and the memory cost is trivial.
    private readonly SenderLane defaultLane;
    private readonly SenderLane asioLane;
    // Third lane: a VST plugin instance's DAW track, fed straight from the bridge by
    // <see cref="SubmitPluginBlock"/>. Deliberately NOT wired into CompositeCaptureBackend — it has
    // no capture child, and the DAW's audio thread is already this lane's clock, exactly as the ASIO
    // driver's callback thread is asioLane's. Putting it through MixingEngine would hand it a SECOND
    // clock (the mixer's free-running 10 ms tick) and give it something to drift against for no gain.
    //
    // It carries whichever RenderRoute value the current audio mode leaves unused: AsioLane in
    // WasapiOnly, Mixed in BothIndependent. See SetAudioMode for why one is always free. Three
    // concurrent lane bytes from one sender is a shape every port already copes with — the supersede
    // rule keys on (endpoint, streamId) and only drops a session whose lane MATCHES, on Windows, iOS
    // and Android alike. Proven here by the "Three concurrent streams from one sender" gate step.
    private readonly SenderLane pluginLane;
    // Persistent AsioCaptureBackend that survives audio-mode changes. The composite borrows
    // a reference to it; mode rebuilds rewire its callback (via SetCallback) rather than
    // tearing it down and reopening the driver. This avoids Audient (and similar single-
    // client drivers) hanging the audio thread for ~5 s on rapid close+reopen — which had
    // been crashing the laptop on every "switch between Both and AsioOnly" attempt.
    // Lazily created when first needed, disposed when transitioning to WasapiOnly OR when
    // the user picks a different ASIO driver entirely. The Action<...> stub is a deliberate
    // placeholder that gets immediately swapped via SetCallback in EnsurePersistentAsio.
    private AsioCaptureBackend? persistentAsio;
    private string? persistentAsioDriverName;

    /// <summary>Optional diagnostic sink. Set by callers (typically the App) before Start to receive
    /// human-readable status strings ("capture started…", "capture stopped with error…", etc.).</summary>
    public Action<string>? Diagnostic
    {
        get => diagnostic;
        set => diagnostic = value;
    }
    private Action<string>? diagnostic;

    // Per-stream state (streamId, audioSequence, frame accumulator, Opus encoder, PCM frame id,
    // format-resend timer) now lives on each SenderLane. This file kept its monolithic shape
    // through Phase 1/2 — the BothIndependent refactor required splitting "stuff that belongs
    // to one outbound stream" from "shared infrastructure". The accumulator/outbound scratch/
    // streamId/sequence counters are all per-lane; the UDP socket, codec config, mute flag,
    // engine and stats stay here. See <see cref="SenderLane"/> for the per-stream hot path.

    private readonly Stopwatch uptime = new();
    private volatile AudioTransportCodec codec = AudioTransportCodec.Pcm;
    // Opus frame size in samples-per-channel at 48 kHz. Default 480 = 10 ms. Renamed from
    // opusFrameMs 2026-05-23 (v3.0 wire-format refactor) so the 2.5 ms RESTRICTED_LOWDELAY
    // mode (= 120 samples) can be expressed cleanly. Only meaningful when codec == Opus.
    private volatile int opusFrameSamples = 480;
    private volatile bool muted;

    // Audio encryption (2026-05-31). The key is derived from the active profile's password by
    // the app and pushed down here; the lanes read it on their capture threads (hence volatile)
    // and rebuild their ciphers when the reference changes. The fingerprint is a short, non-
    // reversible id of the same password, sent in the format packet so a peer can detect a
    // password mismatch. Null until a password is set — with no key the lanes send nothing.
    private volatile byte[]? audioKey;
    private volatile byte[]? audioFingerprint;
    /// <summary>The AES key derived from the active profile's password (or null = no password).
    /// Set by the app; read by the sender lanes. Pushing a new array (not mutating in place)
    /// is what signals the lanes to rebuild their ciphers.</summary>
    public byte[]? AudioKey { get => audioKey; set => audioKey = value; }
    /// <summary>Short non-reversible fingerprint of the active password, advertised in the format
    /// packet for peer password-match detection. Null = none.</summary>
    public byte[]? AudioFingerprint { get => audioFingerprint; set => audioFingerprint = value; }
    private IPEndPoint[] receivers = [];
    private long packetsSent;
    private long bytesSent;

    // Internal accessor so SenderLane can read tight-latency without exposing the field
    // publicly. Codec, OpusFrameSamplesPerChannel and IsMuted are already exposed publicly
    // below and re-used directly by the lane.
    internal bool IsTightLatencyEnabled => tightLatencyEnabled;

    // Hot-path timing instrumentation. Both lanes update these on every emit; the SNAP
    // timer reads + resets them once per second. Used to split observed inter-packet jitter
    // between "our code is slow" vs "the kernel is slow" vs "the network is slow".
    //   maxEmitTicks    = Stopwatch ticks for the WIDEST observation of SenderLane's
    //                     OnMixedSamples (encode + scratch + SendToAll). If this is in
    //                     the multi-ms range, our encode pipeline is the bottleneck.
    //   maxSendCallTicks = Stopwatch ticks for the WIDEST single udp.Client.SendTo call.
    //                     If this is in the multi-ms range, the kernel TX buffer / NIC
    //                     driver / send-socket contention is the bottleneck.
    // Both are reset on each Take() so the SNAP gets per-second peaks.
    private long maxEmitTicks;
    private long maxSendCallTicks;
    // Cumulative counters mirroring the max ones above. The diag log samples these once
    // a second to report "milliseconds-of-CPU-per-second" for the send-side audio thread —
    // i.e. per-thread CPU usage from item 2 of RemSoundefficiency.md. Drain-on-read so the
    // value reads naturally as "this last second's load". 2026-05-22.
    private long cumulativeEmitTicks;
    internal void RecordEmitTicks(long ticks)
    {
        long current;
        do { current = Volatile.Read(ref maxEmitTicks); }
        while (ticks > current && Interlocked.CompareExchange(ref maxEmitTicks, ticks, current) != current);
        Interlocked.Add(ref cumulativeEmitTicks, ticks);
    }
    internal void RecordSendCallTicks(long ticks)
    {
        long current;
        do { current = Volatile.Read(ref maxSendCallTicks); }
        while (ticks > current && Interlocked.CompareExchange(ref maxSendCallTicks, ticks, current) != current);
    }
    public int TakeMaxEmitMs() => (int)(Interlocked.Exchange(ref maxEmitTicks, 0) * 1000 / Stopwatch.Frequency);
    public int TakeMaxSendCallMs() => (int)(Interlocked.Exchange(ref maxSendCallTicks, 0) * 1000 / Stopwatch.Frequency);
    /// <summary>Cumulative milliseconds the send-side audio thread spent inside
    /// <see cref="SenderLane.OnMixedSamples"/> (encode + sendto + per-packet bookkeeping)
    /// since the last call. Resets on read. Diag log emits this as sendMs per second
    /// — direct measurement of "how busy is the send thread". 2026-05-22.</summary>
    public double TakeSendWorkMs() =>
        Interlocked.Exchange(ref cumulativeEmitTicks, 0) * 1000.0 / Stopwatch.Frequency;

    /// <summary>Cumulative milliseconds the capture-side threads spent doing per-callback
    /// work (ASIO buffer copy + mix loop; WASAPI capture body; MixingEngine.MixLoop per
    /// tick) since the last call. Resets on read. Diag log emits this as captureMs per
    /// second. Sister metric to <see cref="TakeSendWorkMs"/> — the two together split
    /// "what is the sender side spending its CPU on". 2026-05-22.</summary>
    public double TakeCaptureWorkMs() =>
        engine.TakeCumulativeCaptureTicks() * 1000.0 / Stopwatch.Frequency;

    /// <summary>Loudest absolute pre-encode sample across both lanes since the last call (resets on
    /// read). ~0 means we're sending silence; surfaced on the diag line as capPeak.</summary>
    public float TakeMaxSenderPreEncodePeak()
    {
        var a = defaultLane.TakeMaxPreEncodePeak();
        var b = asioLane.TakeMaxPreEncodePeak();
        var c = pluginLane.TakeMaxPreEncodePeak();
        return Math.Max(a, Math.Max(b, c));
    }

    /// <summary>Loudest absolute pre-encode sample on the PLUGIN lane since the last call. Split out
    /// from the aggregate because "the DAW track is silent" and "the microphone is silent" are
    /// different faults with different fixes, and the aggregate cannot tell them apart.</summary>
    public float TakeMaxPreEncodePeakPluginLane() => pluginLane.TakeMaxPreEncodePeak();

    /// <summary>Total audio frames both lanes actually handed to the wire since the last call
    /// (resets on read). Pairs with <see cref="TakeMaxSenderPreEncodePeak"/> on the diag line:
    /// capPeak proves real signal reached the encoder; this proves frames left the socket. A
    /// high capPeak with zero frames sent localises a silence to the encode/encrypt stage — the
    /// missing measurement behind the "mic only works in ASIO" report. In WasapiOnly mode only
    /// defaultLane fires, so this number IS the WASAPI mic lane's output.</summary>
    public long TakeSenderAudioFramesSent() =>
        defaultLane.TakeAudioFramesSent() + asioLane.TakeAudioFramesSent() + pluginLane.TakeAudioFramesSent();

    // Pre-encode discontinuity probe, read per lane — each <see cref="SenderLane"/> owns its own, so
    // the log can tell which lane is producing an artefact. Splitting the probe per lane (2026-05-15)
    // eliminated the cross-stream synthetic-step artefact that appeared when the lanes shared one
    // probe and their interleaved callbacks fooled the cross-buffer step computation into recording
    // a "step" between two unrelated audio streams. Cross-buffer (boundary) and within-buffer
    // (content) are read separately — see AudioStepProbe for the diagnostic distinction — so an
    // offline log inspection can tell a real audio transient apart from a buffer-boundary glitch.
    public float TakeMaxPreEncodeStepWasapiLaneCrossBuffer() => defaultLane.TakeMaxPreEncodeStepCrossBuffer();
    public float TakeMaxPreEncodeStepWasapiLaneWithinBuffer() => defaultLane.TakeMaxPreEncodeStepWithinBuffer();
    public float TakeMaxPreEncodeStepAsioLaneCrossBuffer() => asioLane.TakeMaxPreEncodeStepCrossBuffer();
    public float TakeMaxPreEncodeStepAsioLaneWithinBuffer() => asioLane.TakeMaxPreEncodeStepWithinBuffer();
    public float TakeMaxPreEncodeStepPluginLaneCrossBuffer() => pluginLane.TakeMaxPreEncodeStepCrossBuffer();
    public float TakeMaxPreEncodeStepPluginLaneWithinBuffer() => pluginLane.TakeMaxPreEncodeStepWithinBuffer();

    /// <summary>Largest gap between two audio frames leaving each lane since the last call, in
    /// milliseconds. Resets on read.
    ///
    /// <para>The number the far end's jitter buffer actually feels. A lane can hold a perfect
    /// packets-per-second rate while emitting in clumps, and a clump plus a gap is what an underrun on
    /// somebody else's machine is made of — so "the rate looked fine" is not evidence that the timing
    /// was. The capture lanes are clocked by an audio callback; the plugin lane is fed from the
    /// bridge's network receive thread, which is not one. Logging all three side by side is what makes
    /// the comparison possible at all: the capture lane is the control.</para></summary>
    public int TakeMaxEmitGapMsWasapiLane() => defaultLane.TakeMaxEmitGapMs();
    public int TakeMaxEmitGapMsAsioLane() => asioLane.TakeMaxEmitGapMs();
    public int TakeMaxEmitGapMsPluginLane() => pluginLane.TakeMaxEmitGapMs();

    // Raw capture-side step probe — lives inside each <see cref="ICaptureBackend"/>
    // implementation so the ASIO path and the WASAPI path each measure their own buffers
    // independently. These just ask the backend for the max since last read; in
    // BothIndependent mode the composite backend forwards to both inners and returns the
    // larger value.
    public float TakeMaxSenderRawCaptureStepCrossBuffer() => engine.TakeMaxRawCaptureStepCrossBuffer();
    public float TakeMaxSenderRawCaptureStepWithinBuffer() => engine.TakeMaxRawCaptureStepWithinBuffer();

    // Snapshot the cumulative "hit the hard clamp" sample counter. The sender's mix path
    // clamps any sample whose magnitude exceeds 1.0 (avoids producing samples the int24 path
    // can't represent or that the resampler would treat as garbage). Per-second delta tells
    // us whether the input signal is getting close enough to the rails that clipping is
    // active — clipping itself produces no step, but a flat-topped sample plateau plus a
    // following sharp drop can produce audible distortion that masquerades as a click.
    /// <summary>Samples this sender has hard-clamped at the encoder boundary, capture engine and
    /// plugin lane together. The plugin lane's share was missing, so a DAW track running over full
    /// scale — which is what two tracks at 0 dB summed gives you — read as zero clipping all session
    /// while the audio distorted (Anthony Reyers, 2026-09-01).</summary>
    public long ClippedSampleCount => engine.ClippedSampleCount + Interlocked.Read(ref pluginClippedSamples);

    // === inbound dispatch (relay-mode) ===
    // The send socket is normally write-only, but in relay-mode the same socket is what
    // catches return packets — the relay forwards traffic into our NAT pinhole, which lives on
    // this socket's ephemeral port. An optional inbound-packet callback lets the App route
    // those packets into the receiver pipeline (audio) or the heartbeat service.
    // Existing LAN peer-to-peer behaviour is unchanged: nothing inbound arrives at this socket
    // from a LAN peer because LAN peers send to the receiver's well-known port directly.
    private CancellationTokenSource? inboundCts;
    private Thread? inboundThread;
    private long inboundPackets;

    /// <summary>
    /// Optional callback invoked for each UDP datagram that arrives at this sender's socket.
    /// Buffer is owned by the receive thread — copy what you keep. Length is the byte count
    /// (the buffer may be larger). Remote is the sender of the packet (typically a relay).
    /// Set this before <see cref="StartReceiving"/> is called.
    /// </summary>
    public Action<byte[], int, IPEndPoint>? OnInboundPacket { get; set; }

    /// <summary>
    /// Optional callback invoked every time a SenderLane is about to encode a buffer of
    /// captured float audio. The span is 48 kHz interleaved stereo float, lives on the
    /// audio thread, and must be processed quickly or copied — the buffer is reused on
    /// the very next callback. The <see cref="RenderRoute"/> tag identifies which
    /// SenderLane invoked the callback (Mixed in classic modes; WasapiLane or AsioLane in
    /// BothIndependent) so the recorder can keep per-lane streams separate and mix them
    /// at drain time rather than appending sequentially. Null = no tap.
    /// </summary>
    public Action<ReadOnlyMemory<float>, RenderRoute>? OnSentSamples { get; set; }

    /// <summary>
    /// Internal helper for <see cref="SenderLane"/> to invoke <see cref="OnSentSamples"/>
    /// without paying a delegate-invocation cost when no tap is wired. Catches and drops
    /// any exception from the user callback — a misbehaving recorder must not crash the
    /// audio thread.
    /// </summary>
    internal void DispatchSentSamples(ReadOnlyMemory<float> samples, RenderRoute lane)
    {
        var cb = OnSentSamples;
        if (cb is null) return;
        try { cb(samples, lane); } catch { /* recorder failure isolated from audio path */ }
    }

    public AudioSender()
    {
        udp = new UdpClient(AddressFamily.InterNetwork);
        // 1 MB kernel buffers each way — big enough to absorb GC pauses or scheduler hiccups
        // up to ~30 ms at typical PCM-stereo bitrates without dropping packets on the kernel
        // side. The old 256 KB ceiling was the actual cap on resilience to short stalls.
        udp.Client.SendBufferSize = 1024 * 1024;
        udp.Client.ReceiveBufferSize = 1024 * 1024;
        // Explicit bind to port 0 (OS picks an ephemeral). Two reasons:
        //   1. ReceiveFrom on an unbound UDP socket throws SocketException (WSAEINVAL) on
        //      Windows — the receive thread we start below would then CPU-spin in its
        //      catch/continue loop. Binding up front makes ReceiveFrom block normally for
        //      data instead.
        //   2. Same NAT pinhole is shared between send and receive — relay mode requires
        //      this; LAN peer-to-peer is unaffected (we still send from this port, peer just
        //      sends to its own well-known port as before).
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        // Attach the bound socket to qWAVE Voice flow. Must happen after Bind — qWAVE
        // inspects the local endpoint when it registers the flow. Diagnostics route through
        // the same sink as everything else; if Diagnostic hasn't been wired yet (typical at
        // construction time), the lines are silently dropped, which is acceptable for a
        // success/no-op outcome. On failure the socket keeps working without prioritisation.
        networkPriority.TryAttach(udp.Client, msg => diagnostic?.Invoke(msg));
        defaultLane = new SenderLane(this, opusFrameSamples, OpusBitrateLan);
        asioLane = new SenderLane(this, opusFrameSamples, OpusBitrateLan);
        pluginLane = new SenderLane(this, opusFrameSamples, OpusBitrateLan);
        // WasapiOnly is the startup mode and it puts Mixed on defaultLane, so the plugin lane must
        // not be left on its Mixed default: two live streams from one endpoint sharing a lane byte
        // supersede each other on every receiver, four times a second, and both go silent. That is
        // the exact failure the lane-match qualifier was added for on 2026-05-11. SetAudioMode keeps
        // the three in step from here on.
        pluginLane.SetRoute(RenderRoute.AsioLane);
        // WasapiOnly at startup — no ASIO needed yet, so persistentAsio stays null.
        currentAudioMode = AudioMode.WasapiOnly;
        currentAsioDriverName = null;
        engine = new CompositeCaptureBackend(currentAudioMode, currentAsioDriverName, defaultLane.OnMixedSamples, asioLane.OnMixedSamples, persistentAsio, msg => diagnostic?.Invoke(msg), useTightLatencyWasapi: false);
    }

    /// <summary>The persistent ASIO backend, or null when no ASIO driver is selected. Test seam only:
    /// it lets the gate prove that stopping the sender PARKS this lane, which is the fix for "send
    /// off but still transmitting" (2026-08-23 audit, S1). Constructing one does not open a driver,
    /// so the gate can drive this on any machine.</summary>
    internal AsioCaptureBackend? PersistentAsioForTest => persistentAsio;

    // Held so SetTightLatency can rebuild the composite with the same mode/driver.
    private AudioMode currentAudioMode;
    private string? currentAsioDriverName;

    /// <summary>
    /// Sets the audio backend mode and (when ASIO is involved) the driver name. The composite is
    /// rebuilt to match. Two reachable pipeline shapes today:
    ///   * WasapiOnly: MixingEngine direct, no ASIO code in the path. Lowest latency for users
    ///     without ASIO.
    ///   * BothIndependent: WASAPI MixingEngine + persistent AsioCaptureBackend running side by
    ///     side, each on its own SenderLane (own streamId, own UDP stream). No mix loop, no tee.
    ///     Each lane keeps its native latency.
    /// If running, previously-pending sources are re-applied automatically.
    /// </summary>
    public void SetAudioMode(AudioMode mode, string? asioDriverName)
    {
        lock (configGate)
        {
            currentAudioMode = mode;
            currentAsioDriverName = asioDriverName;
            // Lane route assignment. WasapiOnly: only defaultLane is active, carrying Mixed.
            // BothIndependent: defaultLane carries the WASAPI lane, asioLane carries the ASIO
            // lane. SetRoute rotates each lane's streamId so the receiver opens a fresh
            // session under the new Lane tag — old session drains naturally on its 4-second
            // prune.
            if (mode != AudioMode.WasapiOnly)
            {
                defaultLane.SetRoute(RenderRoute.WasapiLane);
                asioLane.SetRoute(RenderRoute.AsioLane);
            }
            else
            {
                defaultLane.SetRoute(RenderRoute.Mixed);
                asioLane.SetRoute(RenderRoute.Mixed); // idle; no callbacks will fire on it
            }
            // The plugin lane takes the route the capture lanes are NOT using in this mode, so the
            // three streams never share a lane byte. In WasapiOnly that is AsioLane: asioLane is set
            // to Mixed just above but receives no callbacks at all (CompositeCaptureBackend does not
            // build an ASIO child in that mode), and since EnsureFormatPacketSent runs only from
            // inside SenderLane.OnMixedSamples, a lane with no callbacks emits nothing — not even a
            // format announce. In BothIndependent the capture lanes hold WasapiLane and AsioLane, so
            // Mixed is the free one. No new RenderRoute value is invented: both mobile ports clamp an
            // unknown lane byte to Mixed, so a fourth value would collide there rather than route.
            pluginLane.SetRoute(mode == AudioMode.WasapiOnly ? RenderRoute.AsioLane : RenderRoute.Mixed);
            EnsurePersistentAsioLocked();
            RebuildEngineLocked();
        }
    }

    /// <summary>
    /// Make sure <see cref="persistentAsio"/> matches the current mode + driver. Created
    /// fresh when first transitioning into an ASIO-using mode; reused across subsequent
    /// mode changes that keep the same driver; disposed when transitioning to WasapiOnly
    /// (no ASIO) or when the user picks a different driver. The persistent instance is
    /// loaned to the composite via the constructor; the composite borrows but doesn't
    /// dispose, so the underlying ASIO driver handle stays open across engine rebuilds.
    /// Caller must hold <see cref="configGate"/>. The callback is also pointed at the ASIO
    /// lane here, which is what unparks it after a <see cref="Stop"/>.
    /// </summary>
    private void EnsurePersistentAsioLocked()
    {
        var willUseAsio = currentAudioMode != AudioMode.WasapiOnly
            && !string.IsNullOrEmpty(currentAsioDriverName);

        if (!willUseAsio)
        {
            // ASIO is no longer selected (driver set to "(none)", or a WASAPI-only mode). RELEASE the
            // driver now — close it on its dedicated apartment thread, which is safe as of the AsioApartment
            // change — so the sound card is FREE for other applications (a DAW, etc.) instead of being held
            // exclusively for the whole time RemSound is open. This replaces the old "park it open forever"
            // workaround, which existed only because closing the Audient driver crashed. The composite is
            // rebuilt without ASIO immediately after (RebuildEngineLocked); the composite borrows but never
            // disposes this instance, so disposing it here can't pull the rug from a live engine.
            if (persistentAsio is not null)
            {
                ReleaseAsioBackendInBackground(persistentAsio, "release-on-deselect");
                persistentAsio = null;
                persistentAsioDriverName = null;
            }
            return;
        }

        // Need ASIO. Reuse if the driver matches; rebuild otherwise (rare — only when the
        // user picks a different driver in the dropdown).
        if (persistentAsio is null || persistentAsioDriverName != currentAsioDriverName)
        {
            if (persistentAsio is not null)
            {
                ReleaseAsioBackendInBackground(persistentAsio, "driver-switch");
            }
            persistentAsio = new AsioCaptureBackend(
                currentAsioDriverName!,
                _ => { /* placeholder, replaced by SetCallback below */ },
                msg => diagnostic?.Invoke($"asio: {msg}"));
            persistentAsioDriverName = currentAsioDriverName;
        }

        // ASIO audio always goes to the dedicated ASIO lane: WasapiOnly never reaches here
        // (willUseAsio is false above), so this is BothIndependent.
        persistentAsio.SetCallback(asioLane.OnMixedSamples);
    }

    /// <summary>Close an outgoing ASIO backend WITHOUT making the caller wait. The caller here is the
    /// UI thread (driver switch / deselect), and a slow driver close — Ed's EVO once took 5+ seconds
    /// inside its own Stop — froze the window for the duration even with the close on the apartment
    /// thread, because the caller still blocked on it. Now: the callback is unhooked IMMEDIATELY (a
    /// volatile write, no driver call — so the old driver stops feeding the lanes before the new one
    /// starts), and the actual Stop/Dispose runs on a worker, where the apartment's 8 s bound still
    /// backstops a wedged driver. Known edge, accepted: re-selecting the SAME driver within the close
    /// window can find the card still held and fail to open — the capture-start failure is logged, and
    /// picking the driver again once the close finishes recovers it.</summary>
    private void ReleaseAsioBackendInBackground(AsioCaptureBackend backend, string why)
    {
        backend.SetCallback(_ => { });
        Task.Run(() =>
        {
            try { backend.Dispose(); }
            catch (Exception ex) { diagnostic?.Invoke($"asio: background {why} release threw {ex.GetType().Name}: {ex.Message}"); }
        });
    }

    /// <summary>
    /// (Re)create the composite backend with the current audio-mode + asio-driver-name +
    /// tight-latency-WASAPI flag. Caller must hold <c>configGate</c>. Preserves the running
    /// state — if the engine was running before, restart it with the same source list.
    /// The persistent ASIO instance is passed in by reference so the composite borrows
    /// rather than creates+disposes it; that's what keeps the driver open across rebuilds.
    /// </summary>
    private void RebuildEngineLocked()
    {
        var wasRunning = engine.IsRunning;
        try { engine.Stop(); } catch { /* ignore */ }
        try { engine.Dispose(); } catch { /* ignore */ }
        engine = new CompositeCaptureBackend(
            currentAudioMode,
            currentAsioDriverName,
            defaultLane.OnMixedSamples,
            asioLane.OnMixedSamples,
            persistentAsio,
            msg => diagnostic?.Invoke(msg),
            useTightLatencyWasapi: tightLatencyEnabled);
        if (wasRunning && pendingSources.Count > 0)
        {
            engine.Start(pendingSources);
        }
    }

    /// <summary>Updates the PCM frame size based on the user's "Send rate" choice. For Opus,
    /// frame size is set via <see cref="ConfigureCodec"/>'s opusFrameSamplesPerChannel
    /// parameter (the App halves it when SendRate is Tight). On a frame-size change, resets
    /// the accumulator and stream id so the receiver opens a fresh session at the new format.
    /// </summary>
    public void SetSendRate(SendRate rate)
    {
        lock (configGate)
        {
            var newSamples = rate == SendRate.Tight ? PcmTightSamplesPerChannel : PcmStandardSamplesPerChannel;
            if (newSamples == pcmFrameSamplesPerChannel) return;
            pcmFrameSamplesPerChannel = newSamples;
            // Both lanes need to rotate streamId + reset accumulator on a frame-size change.
            // The asio lane is idle in classic modes (no producer feeding it) so the reset is
            // harmless there; in BothIndependent both lanes are active and both must roll.
            defaultLane.OnPcmFrameSizeChanged();
            asioLane.OnPcmFrameSizeChanged();
            pluginLane.OnPcmFrameSizeChanged();
        }
    }

    /// <summary>Tight-latency mode toggle. Affects two things:
    ///   * ASIO-only PCM: every incoming ASIO buffer is emitted directly as a single packet
    ///     instead of being accumulated to the PCM frame size — saves ~frame_size_ms/2 of
    ///     average send-side latency. ProcessPcm reads <c>tightLatencyEnabled</c> directly.
    ///   * WasapiOnly with single source: rebuilds the capture backend as
    ///     <see cref="PushModeWasapiBackend"/> instead of <see cref="MixingEngine"/>. The WASAPI
    ///     capture event drives the encode/UDP-send pipeline directly, eliminating the ~6 ms
    ///     of Stopwatch+WaitHandle scheduler jitter that <see cref="MixingEngine"/>'s mix tick
    ///     adds. Especially important at high device sample rates (96 kHz EVO8 etc.) where the
    ///     in-tick resampler stage compounds the jitter. <see cref="CompositeCaptureBackend"/>
    ///     decides whether push-mode actually applies based on source count and mode.
    /// No effect on Opus accumulation (Opus needs fixed frame sizes) or AsioOnly's WASAPI
    /// (there's no WASAPI source). Sender-side only as of Phase 3 (2026-05-06): the
    /// receiver no longer has a resampler to bypass.</summary>
    public void SetTightLatency(bool enabled)
    {
        lock (configGate)
        {
            if (tightLatencyEnabled == enabled) return;
            tightLatencyEnabled = enabled;
            RebuildEngineLocked();
        }
    }
    private volatile bool tightLatencyEnabled;

    public bool IsRunning => engine.IsRunning;
    public long CaptureCallbacks => engine.TotalCaptureCallbacks;
    public long CaptureBytes => engine.TotalCaptureBytes;
    /// <summary>Largest gap between capture callbacks since the last call. Resets on read.
    /// Use this in periodic diagnostics — if it spikes well above the audio-buffer period
    /// (e.g. 19 ms when the period should be ≤ 5 ms), the audio capture thread is being
    /// stalled by GC, USB, or scheduler issues, which produces audible discontinuities the
    /// receiver can't detect (because no packets are lost — they just contain audio with
    /// holes in it).</summary>
    public int TakeMaxCaptureCallbackGapMs() => engine.TakeMaxCallbackGapMs();

    /// <summary>How long audio waits in the CAPTURE device, as the device itself reports it. Zero
    /// when nothing is open or the device won't say, in which case the caller keeps its own estimate.
    /// See <see cref="ICaptureBackend.ReportedInputLatencyMs"/> for why this replaced a constant.</summary>
    public double ReportedInputLatencyMs => engine.ReportedInputLatencyMs;

    /// <summary>Per-source capture ring depth and applied drift ratio, so the diag line can SHOW two
    /// sources pulling apart instead of leaving it to be heard. Empty on the ASIO or push paths, which
    /// have one clock and nothing to correct. 2026-09-07.</summary>
    public IReadOnlyList<(string Name, int BufferedMs, double Ratio)> CaptureSourceDrift =>
        (engine as CompositeCaptureBackend)?.SourceDrift ?? [];
    public string? CaptureFormatDescription => engine.FirstCaptureFormatDescription;
    public string? LastCaptureError => engine.FirstCaptureLastError;
    public AudioTransportCodec Codec => codec;
    /// <summary>Opus frame size in samples-per-channel at 48 kHz. 120 = 2.5 ms, 240 = 5 ms,
    /// 480 = 10 ms, 960 = 20 ms. Renamed from OpusFrameMilliseconds in the v3.0 wire-format
    /// refactor (see <see cref="AudioFormatInfo"/>).</summary>
    public int OpusFrameSamplesPerChannel => opusFrameSamples;

    /// <summary>
    /// Atomically set the codec and (for Opus) the frame size. Resets stream identity and the
    /// frame accumulator so the receiver sees the new format from the next packet onward. Both
    /// parameters are taken together because changing only one would briefly send malformed
    /// frames at the encoder boundary.
    /// </summary>
    public void ConfigureCodec(AudioTransportCodec newCodec, int newOpusFrameSamplesPerChannel = 480)
    {
        // Clamp to the legal Opus range at 48 kHz: 120 (2.5 ms) to 2880 (60 ms).
        var clampedSamples = Math.Clamp(newOpusFrameSamplesPerChannel, 120, 2880);
        if (codec == newCodec && (newCodec != AudioTransportCodec.Opus || opusFrameSamples == clampedSamples))
        {
            return;
        }
        lock (configGate)
        {
            codec = newCodec;
            opusFrameSamples = clampedSamples;
            // Rebuild both lanes' encoders + rotate their streamIds. Same idle-lane rationale
            // as SetSendRate — harmless when the asio lane has no producer; necessary when it
            // does (BothIndependent).
            defaultLane.OnCodecChanged(newCodec, clampedSamples);
            asioLane.OnCodecChanged(newCodec, clampedSamples);
            pluginLane.OnCodecChanged(newCodec, clampedSamples);
        }
    }

    public bool IsMuted { get => muted; set => muted = value; }
    public long PacketsSent => Interlocked.Read(ref packetsSent);
    public long BytesSent => Interlocked.Read(ref bytesSent);
    public TimeSpan Uptime => uptime.Elapsed;

    /// <summary>
    /// Friendly summary of currently-active sources for diagnostic columns. Returns
    /// "(none)" when nothing is configured, "(N sources)" when there are 4+ — the snapshot log
    /// column is fixed-width-ish and a long join becomes unreadable past 3 sources.
    /// </summary>
    public string CaptureDeviceName
    {
        get
        {
            var names = engine.ActiveSourceNames;
            if (names.Count == 0) return "(none)";
            if (names.Count <= 3) return string.Join(", ", names);
            return $"({names.Count} sources)";
        }
    }

    /// <summary>Set the destinations to which packets are sent. Live-updateable.</summary>
    public void SetReceivers(IEnumerable<IPEndPoint> endpoints)
    {
        var list = endpoints.ToArray();
        Volatile.Write(ref receivers, list);
    }

    /// <summary>
    /// Sets the list of capture sources to mix. Each spec identifies a WASAPI device + whether
    /// it's a loopback (output device, system audio) or direct input (mic, line-in). Order does
    /// not matter — sources are summed equally.
    /// </summary>
    public void Configure(IReadOnlyList<CaptureSourceSpec> sources)
    {
        pendingSources = sources;
        if (engine.IsRunning)
        {
            // Live add/remove via NAudio's MixingSampleProvider — mix loop never pauses,
            // streamId stays the same, receiver doesn't see a new stream session, no underrun.
            engine.UpdateSources(sources);
        }
    }

    public void Start()
    {
        if (engine.IsRunning) return;
        StartEngineWithCurrentSources();
    }

    // Transition-only guard for the no-sources line: the UI's periodic re-apply retries every second
    // while nothing is ticked, and logging it every time buried the useful lines (Ed's 2026-07-26 log
    // had 18 identical lines in a row). Log the first occurrence, then stay quiet until sources return.
    private bool loggedNoSources;

    private void StartEngineWithCurrentSources()
    {
        if (pendingSources.Count == 0)
        {
            if (!loggedNoSources)
            {
                loggedNoSources = true;
                diagnostic?.Invoke("sender: start requested but no sources configured (repeats suppressed until sources appear)");
            }
            return;
        }
        loggedNoSources = false;

        defaultLane.ResetForStart();
        asioLane.ResetForStart();
        Interlocked.Exchange(ref packetsSent, 0);
        Interlocked.Exchange(ref bytesSent, 0);
        uptime.Restart();
        // Unpark the ASIO lane if a previous Stop parked it (see Stop below). This re-points the
        // persistent instance's callback at the lane the CURRENT mode wants; the composite's Start,
        // a line below, restores its channel pairs via UpdateSources. Idempotent and cheap when the
        // lane was never parked, and it touches no driver call in either case.
        lock (configGate) EnsurePersistentAsioLocked();
        engine.Start(pendingSources);
    }

    public void Stop()
    {
        engine.Stop();
        // The composite stops the WASAPI lane only — it deliberately never stops the borrowed ASIO
        // child, because closing and reopening that driver is what hangs Audient for ~5 seconds.
        // That part is right and must stay. But nothing else stood the lane down either: the
        // callback still pointed at asioLane, and MainForm does not clear the peer list on a stop,
        // so an ASIO lane kept capturing, encoding and TRANSMITTING with "Send my audio" off.
        // Park it instead — Ed's own mechanism, no driver call at all. 2026-08-23 audit, S1.
        lock (configGate) persistentAsio?.Park();
        // Stand the plugin lane down too. Stop() means "stop sending my audio", and a DAW track is
        // audio being sent; leaving the flag up would keep encoding blocks that are still arriving on
        // the bridge thread after the user switched sending off. The app re-arms it on its next tick
        // if a plugin is genuinely still delivering blocks (see PluginTrackSource).
        pluginSendActive = false;
        uptime.Stop();
    }

    // === plugin lane (the DAW track) ===

    // Whether the plugin lane is armed. Volatile because SubmitPluginBlock reads it on the bridge
    // thread while the UI thread writes it. NOT derived from engine.IsRunning: the whole point of the
    // plugin lane is that it works with no capture device ticked, where the capture engine never
    // starts at all (StartEngineWithCurrentSources returns early on empty specs, and MixingEngine.Start
    // does the same). The two lifecycles are deliberately separate.
    private volatile bool pluginSendActive;

    /// <summary>Is the plugin lane armed right now? The app folds this into its "should the sender be
    /// running" decision, so a DAW track goes out whether or not "Send my audio" is also ticked.</summary>
    public bool IsPluginSending => pluginSendActive;

    /// <summary>This lane's current route and stream id, for the gate. The route is the whole
    /// backward-compatibility argument in one byte, so it is asserted rather than assumed.</summary>
    internal RenderRoute PluginLaneRouteForTest => pluginLane.Route;
    internal ushort PluginLaneStreamIdForTest => pluginLane.StreamId;
    internal RenderRoute DefaultLaneRouteForTest => defaultLane.Route;
    internal RenderRoute AsioLaneRouteForTest => asioLane.Route;
    internal ushort DefaultLaneStreamIdForTest => defaultLane.StreamId;
    internal ushort AsioLaneStreamIdForTest => asioLane.StreamId;

    /// <summary>
    /// Arm or disarm the plugin lane. Called by <see cref="PluginTrackSource"/> when the first DAW
    /// track starts arriving and when the last one stops (or times out, for a DAW that was killed).
    ///
    /// <para>Arming rotates the lane's stream id, so the receiver opens a fresh session rather than
    /// continuing one it had already pruned. Disarming rotates nothing: the lane simply stops being
    /// fed, and a lane that is not fed emits nothing at all — not even a format announce, because
    /// <c>EnsureFormatPacketSent</c> is only reached from inside <c>OnMixedSamples</c>.</para>
    /// </summary>
    public void SetPluginSendActive(bool active)
    {
        lock (configGate)
        {
            if (pluginSendActive == active) return;
            pluginSendActive = active;
            if (active)
            {
                pluginLane.ResetForStart();
                // Uptime is otherwise only started by the capture engine, and a plugin-only send has
                // no capture engine. Without this the diagnostic line divides by a stopped clock.
                if (!uptime.IsRunning) uptime.Restart();
                diagnostic?.Invoke($"sender: plugin lane armed on route {pluginLane.Route}, stream {pluginLane.StreamId}");
            }
            else
            {
                diagnostic?.Invoke("sender: plugin lane disarmed");
            }
        }
    }

    /// <summary>
    /// One block of a DAW track, 48 kHz interleaved stereo float, straight onto the wire.
    ///
    /// <para>Called from the plugin bridge's single receive thread — which is what satisfies
    /// <see cref="SenderLane"/>'s one-producer-thread contract. <see cref="PluginTrackSource"/> is
    /// the only caller and it submits from exactly that thread; audio from any OTHER connected DAW
    /// is summed into the block before it gets here, never submitted separately.</para>
    ///
    /// <para>Silently ignored when the lane is not armed, which covers blocks still in flight on the
    /// bridge at the moment sending is switched off.</para>
    /// </summary>
    public void SubmitPluginBlock(ReadOnlyMemory<float> stereoFloats)
    {
        if (!pluginSendActive) return;
        var source = stereoFloats.Span;
        if (source.Length == 0 || source.Length > pluginClampScratch.Length) return;

        // THE SHARED ENCODER-BOUNDARY CLAMP, which this lane used to skip. Every other source goes
        // through SampleClamp before it reaches a lane — the mix engine, the ASIO backend and the
        // push-mode WASAPI backend all do — and the plugin lane was the one that did not, on the
        // reasoning that nothing on its path could raise the level. True of our processing and wrong
        // about the SOURCE: a DAW track is over 0 dBFS whenever the user's mix is, and two plugin
        // instances summed together are 6 dB hotter than one.
        //
        // The clamp itself changes almost nothing audible, because the Opus encoder clips out-of-range
        // input internally and PcmPack clamps as it packs. What it buys is the COUNT: without it
        // ClippedSampleCount watches only the capture devices, so a track running at nearly +6 dB
        // reads as zero clipping and the log cannot tell anyone why their audio is distorting.
        source.CopyTo(pluginClampScratch);
        var block = pluginClampScratch.AsSpan(0, source.Length);
        var clipped = SampleClamp.ClampBuffer(block);
        if (clipped > 0) Interlocked.Add(ref pluginClippedSamples, clipped);
        pluginLane.OnMixedSamples(new ReadOnlyMemory<float>(pluginClampScratch, 0, source.Length));
    }

    // Clamp scratch for the plugin lane, sized to the largest block the bridge can carry. Touched
    // only on the bridge's single receive thread, which is the same one-producer rule SenderLane
    // itself relies on.
    private readonly float[] pluginClampScratch = new float[PluginBridgeProtocol.MaxAudioBytes / sizeof(float)];
    private long pluginClippedSamples;

    /// <summary>
    /// Start a background thread reading inbound packets from this sender's socket and
    /// dispatching them to <see cref="OnInboundPacket"/>. Idempotent — safe to call repeatedly.
    /// Used in relay mode so heartbeat replies and audio coming back through the relay
    /// (which arrive at the sender's NAT pinhole, not the receiver's well-known port) get
    /// routed into the right pipelines. No-op for pure LAN peer-to-peer setups.
    /// </summary>
    public void StartReceiving()
    {
        lock (configGate)
        {
            if (inboundThread is { IsAlive: true }) return;
            inboundCts = new CancellationTokenSource();
            var token = inboundCts.Token;
            inboundThread = new Thread(() => InboundReceiveLoop(token))
            {
                IsBackground = true,
                Name = "RemSound.SenderReceive",
            };
            inboundThread.Start();
        }
    }

    private void InboundReceiveLoop(CancellationToken token)
    {
        var buffer = new byte[2048];
        EndPoint anyEndpoint = new IPEndPoint(IPAddress.Any, 0);
        // Consecutive socket errors. A single one is routine on Windows UDP — an ICMP port-unreachable
        // from a peer that has gone away surfaces as WSAECONNRESET on the NEXT receive — so the common
        // case must stay a bare `continue`. A PERSISTENT error is different: the old code spun this
        // loop as fast as the CPU allowed, with no log line anywhere, so "the socket is broken and we
        // are receiving nothing" looked exactly like "nobody is sending". Back off and say so.
        // 2026-08-23 audit, finding S9.
        var consecutiveErrors = 0;
        while (!token.IsCancellationRequested)
        {
            int received;
            try
            {
                received = udp.Client.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref anyEndpoint);
                consecutiveErrors = 0;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.Interrupted) { break; }
            catch (ObjectDisposedException) { break; }
            catch (OperationCanceledException) { break; }
            catch (SocketException ex)
            {
                consecutiveErrors++;
                if (consecutiveErrors == 1) continue;   // the routine single reset — no cost, no noise
                // Report on a power-of-two ladder so a stuck socket is visible without flooding.
                if ((consecutiveErrors & (consecutiveErrors - 1)) == 0)
                {
                    diagnostic?.Invoke($"sender inbound socket error x{consecutiveErrors}: {ex.SocketErrorCode} — backing off; relay traffic will not arrive while this persists");
                }
                if (token.WaitHandle.WaitOne(Math.Min(1000, consecutiveErrors * 10))) break;
                continue;
            }

            if (received <= 0) continue;
            if (anyEndpoint is not IPEndPoint remote) continue;
            Interlocked.Increment(ref inboundPackets);

            try
            {
                OnInboundPacket?.Invoke(buffer, received, remote);
            }
            catch (Exception ex)
            {
                diagnostic?.Invoke($"sender inbound dispatch threw: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Send an arbitrary datagram on this sender's UDP socket. Used by the heartbeat service
    /// in relay mode so its packets share the same NAT pinhole as audio. Returns false if the
    /// send failed.
    /// </summary>
    public bool SendVia(byte[] data, int length, IPEndPoint destination)
    {
        try
        {
            udp.Send(data, length, destination);
            return true;
        }
        catch (SocketException) { return false; }
        catch (ObjectDisposedException) { return false; }
    }

    /// <summary>Cumulative inbound packets received on this sender's socket. Mostly zero
    /// outside relay mode.</summary>
    public long InboundPackets => Interlocked.Read(ref inboundPackets);

    /// <summary>
    /// Shutdown. The ORDER here is the whole content of this method, so it is spelled out.
    ///
    /// <para>Stop first, and never let it throw past this point — it used to be an unguarded call,
    /// so a backend that failed to stop skipped the socket close, the thread join and the qWAVE
    /// detach that follow it (2026-08-23 audit, S9).</para>
    ///
    /// <para>Then the socket, and close it BEFORE joining the inbound thread. That thread blocks in
    /// ReceiveFrom, which cancelling a token does not wake; closing the socket is what wakes it.
    /// Cancelling and then joining, as this did, meant the join always burned its full 500 ms and
    /// the thread only died later (2026-08-23 audit, S9). qWAVE is detached before the close because
    /// its flow handle points into the kernel-side socket state.</para>
    ///
    /// <para>Then the engine, then the persistent ASIO driver, and ONLY THEN the lanes' ciphers.
    /// That order is load-bearing: the ASIO driver's callback thread is what encrypts, so freeing
    /// the AES-GCM state while the driver is still open is a use-after-free on a real-time thread.
    /// The Stop above parks the lane, which closes the window; disposing in this order closes it
    /// again for anyone who calls Dispose without Stop (2026-08-23 audit, S3).</para>
    /// </summary>
    public void Dispose()
    {
        try { Stop(); }
        catch (Exception ex) { diagnostic?.Invoke($"sender: stop during dispose threw {ex.GetType().Name}: {ex.Message}"); }

        try { inboundCts?.Cancel(); } catch { /* ignore */ }
        try { networkPriority.Dispose(); } catch { /* ignore */ }
        try { udp.Dispose(); } catch { /* ignore */ }
        try { inboundThread?.Join(500); } catch { /* ignore */ }
        try { inboundCts?.Dispose(); } catch { /* ignore */ }
        inboundCts = null;
        inboundThread = null;

        try { engine.Dispose(); } catch (Exception ex) { diagnostic?.Invoke($"sender: engine dispose threw {ex.GetType().Name}: {ex.Message}"); }
        try { persistentAsio?.Dispose(); } catch (Exception ex) { diagnostic?.Invoke($"sender: asio dispose threw {ex.GetType().Name}: {ex.Message}"); }
        persistentAsio = null;

        try { defaultLane.DisposeCrypto(); } catch { /* ignore */ }
        try { asioLane.DisposeCrypto(); } catch { /* ignore */ }
        try { pluginLane.DisposeCrypto(); } catch { /* ignore */ }
    }

    // === wire path (shared across all lanes) ===

    /// <summary>
    /// Emit a fully-constructed packet to every configured receiver. Per-lane code in
    /// <see cref="SenderLane"/> builds the header + payload (in its stack/pre-allocated
    /// outboundScratch) and calls this; we forward the span straight into the socket's
    /// span-aware Send overload so the audio thread never allocates anything in the hot
    /// path. The pre-2026-05-11 implementation did packet.ToArray() per send, which on
    /// ASIO tight-latency throughput (~750 packets/sec per lane × two lanes in
    /// BothIndependent) was a steady ~2 MB/sec of small-byte-array Gen 0 allocations and
    /// drove visible packet-emission jitter via GC pauses. The span overload eliminates
    /// that entire allocation stream.
    ///
    /// Single point of outbound socket use means both lanes share the same NAT pinhole
    /// and stats. The send-buffer-full or kernel-mutex contention between two threads
    /// sending on the same UDP socket is microseconds in practice and not the source of
    /// the ms-scale jitter we observe; the per-packet allocation was.
    ///
    /// UDP failures per-receiver are swallowed by design — UDP is unreliable and one
    /// peer dropping shouldn't disturb the others.
    /// </summary>
    internal void SendToAll(ReadOnlySpan<byte> packet)
    {
        var targets = Volatile.Read(ref receivers);
        if (targets.Length == 0) return;

        // Use Socket.SendTo with the span overload — UdpClient's span-Send signature is
        // .NET 6+. Going via Client (the underlying Socket) avoids one wrapper layer too.
        var packetLen = packet.Length;
        // Measure the kernel-side time of just the SendTo call when diagnostics are enabled.
        // If this number spikes, the bottleneck is the TX path (kernel buffer pressure, NIC,
        // single-socket cross-thread contention) rather than our encode pipeline. Hoisted
        // out of the per-target loop so a multi-peer broadcast pays one branch instead of N.
        var diag = RemSound.Core.DiagnosticsGate.Enabled;
        foreach (var target in targets)
        {
            try
            {
                if (diag)
                {
                    var sendStart = Stopwatch.GetTimestamp();
                    udp.Client.SendTo(packet, target);
                    RecordSendCallTicks(Stopwatch.GetTimestamp() - sendStart);
                }
                else
                {
                    udp.Client.SendTo(packet, target);
                }
                Interlocked.Increment(ref packetsSent);
                Interlocked.Add(ref bytesSent, packetLen);
            }
            catch (SocketException)
            {
                // Single-packet failures are a non-event; UDP is unreliable by design.
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }
}
