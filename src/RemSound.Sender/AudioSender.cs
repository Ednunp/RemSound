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

    // Pre-encode discontinuity probe — per-lane (each <see cref="SenderLane"/> owns its own).
    // The aggregate accessor returns the max across both lanes since the last read; per-lane
    // accessors expose them individually so BothIndependent mode can tell which lane is
    // producing the artefact. Splitting the probe per-lane (2026-05-15) eliminates the
    // cross-stream synthetic-step artefact that appeared when both lanes shared one probe and
    // their interleaved callbacks fooled the cross-buffer step computation into recording a
    // "step" between two unrelated audio streams.
    public float TakeMaxSenderPreEncodeStep()
    {
        var a = defaultLane.TakeMaxPreEncodeStep();
        var b = asioLane.TakeMaxPreEncodeStep();
        return a > b ? a : b;
    }
    public float TakeMaxPreEncodeStepWasapiLane() => defaultLane.TakeMaxPreEncodeStep();
    public float TakeMaxPreEncodeStepAsioLane() => asioLane.TakeMaxPreEncodeStep();

    /// <summary>Loudest absolute pre-encode sample across both lanes since the last call (resets on
    /// read). ~0 means we're sending silence; surfaced on the diag line as capPeak.</summary>
    public float TakeMaxSenderPreEncodePeak()
    {
        var a = defaultLane.TakeMaxPreEncodePeak();
        var b = asioLane.TakeMaxPreEncodePeak();
        return a > b ? a : b;
    }

    /// <summary>Total audio frames both lanes actually handed to the wire since the last call
    /// (resets on read). Pairs with <see cref="TakeMaxSenderPreEncodePeak"/> on the diag line:
    /// capPeak proves real signal reached the encoder; this proves frames left the socket. A
    /// high capPeak with zero frames sent localises a silence to the encode/encrypt stage — the
    /// missing measurement behind the "mic only works in ASIO" report. In WasapiOnly mode only
    /// defaultLane fires, so this number IS the WASAPI mic lane's output.</summary>
    public long TakeSenderAudioFramesSent() =>
        defaultLane.TakeAudioFramesSent() + asioLane.TakeAudioFramesSent();

    // Cross-buffer (boundary) and within-buffer (content) split — see AudioStepProbe for the
    // diagnostic distinction. Used by the per-second diag logger to emit two extra columns so
    // an offline log inspection can tell a real audio transient apart from a buffer-boundary
    // glitch. 2026-05-21 addition.
    public float TakeMaxPreEncodeStepWasapiLaneCrossBuffer() => defaultLane.TakeMaxPreEncodeStepCrossBuffer();
    public float TakeMaxPreEncodeStepWasapiLaneWithinBuffer() => defaultLane.TakeMaxPreEncodeStepWithinBuffer();
    public float TakeMaxPreEncodeStepAsioLaneCrossBuffer() => asioLane.TakeMaxPreEncodeStepCrossBuffer();
    public float TakeMaxPreEncodeStepAsioLaneWithinBuffer() => asioLane.TakeMaxPreEncodeStepWithinBuffer();

    // Raw capture-side step probe — now lives inside each <see cref="ICaptureBackend"/>
    // implementation so the ASIO path and the WASAPI path each measure their own buffers
    // independently. The aggregate just asks the backend for the max since last read; in
    // BothIndependent mode the composite backend forwards to both inners and returns the
    // larger value.
    public float TakeMaxSenderRawCaptureStep() => engine.TakeMaxRawCaptureStep();
    public float TakeMaxSenderRawCaptureStepCrossBuffer() => engine.TakeMaxRawCaptureStepCrossBuffer();
    public float TakeMaxSenderRawCaptureStepWithinBuffer() => engine.TakeMaxRawCaptureStepWithinBuffer();

    // Snapshot the cumulative "hit the hard clamp" sample counter. The sender's mix path
    // clamps any sample whose magnitude exceeds 1.0 (avoids producing samples the int24 path
    // can't represent or that the resampler would treat as garbage). Per-second delta tells
    // us whether the input signal is getting close enough to the rails that clipping is
    // active — clipping itself produces no step, but a flat-topped sample plateau plus a
    // following sharp drop can produce audible distortion that masquerades as a click.
    public long ClippedSampleCount => engine.ClippedSampleCount;

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
        // WasapiOnly at startup — no ASIO needed yet, so persistentAsio stays null.
        currentAudioMode = AudioMode.WasapiOnly;
        currentAsioDriverName = null;
        engine = new CompositeCaptureBackend(currentAudioMode, currentAsioDriverName, defaultLane.OnMixedSamples, asioLane.OnMixedSamples, persistentAsio, msg => diagnostic?.Invoke(msg), useTightLatencyWasapi: false);
    }

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
    /// Legacy <c>AudioMode.AsioOnly</c> and <c>AudioMode.Both</c> are tolerated (the composite
    /// coerces them) but no UI path produces them any more. If running, previously-pending
    /// sources are re-applied automatically.
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
            // prune. Legacy AsioOnly / Both can't be produced by the UI any more; if they
            // arrive (in-flight callers, future call sites) we treat them as BothIndependent
            // for routing purposes so the streams still carry distinct Lane tags.
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
    /// Caller must hold <see cref="configGate"/>. The callback is also rewired here based
    /// on which lane should receive ASIO audio in the new mode.
    /// </summary>
    private void EnsurePersistentAsioLocked()
    {
        var willUseAsio = currentAudioMode != AudioMode.WasapiOnly
            && !string.IsNullOrEmpty(currentAsioDriverName);

        if (!willUseAsio)
        {
            // Mode no longer uses ASIO. Dispose the persistent instance so the driver
            // releases (other apps may want it).
            if (persistentAsio is not null)
            {
                try { persistentAsio.Dispose(); } catch { /* ignore */ }
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
                try { persistentAsio.Dispose(); } catch { /* ignore */ }
            }
            persistentAsio = new AsioCaptureBackend(
                currentAsioDriverName!,
                _ => { /* placeholder, replaced by SetCallback below */ },
                msg => diagnostic?.Invoke($"asio: {msg}"));
            persistentAsioDriverName = currentAsioDriverName;
        }

        // Wire the callback to the right lane for the current mode. WasapiOnly never reaches
        // here (willUseAsio is false above). BothIndependent is the only ASIO-using mode the
        // UI can produce, and it routes ASIO into the dedicated AsioLane. Legacy AsioOnly is
        // tolerated by sending into defaultLane (which carries RenderRoute.Mixed in non-
        // BothIndependent setups).
        persistentAsio.SetCallback(
            currentAudioMode == AudioMode.BothIndependent
                ? asioLane.OnMixedSamples
                : defaultLane.OnMixedSamples);
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

    private void StartEngineWithCurrentSources()
    {
        if (pendingSources.Count == 0)
        {
            diagnostic?.Invoke("sender: start requested but no sources configured");
            return;
        }

        defaultLane.ResetForStart();
        asioLane.ResetForStart();
        Interlocked.Exchange(ref packetsSent, 0);
        Interlocked.Exchange(ref bytesSent, 0);
        uptime.Restart();
        engine.Start(pendingSources);
    }

    public void Stop()
    {
        engine.Stop();
        uptime.Stop();
    }

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
        while (!token.IsCancellationRequested)
        {
            int received;
            try
            {
                received = udp.Client.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref anyEndpoint);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.Interrupted) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            catch (OperationCanceledException) { break; }

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

    public void Dispose()
    {
        Stop();
        try { inboundCts?.Cancel(); } catch { /* ignore */ }
        try { inboundThread?.Join(500); } catch { /* ignore */ }
        try { defaultLane.DisposeCrypto(); } catch { /* ignore */ }
        try { asioLane.DisposeCrypto(); } catch { /* ignore */ }
        engine.Dispose();
        // Dispose the persistent ASIO LAST, after the engine that was borrowing it. The
        // composite's Dispose doesn't touch the persistent instance (it borrowed it); we
        // own it here and close the driver as part of app shutdown.
        try { persistentAsio?.Dispose(); } catch { /* ignore */ }
        persistentAsio = null;
        // Detach the qWAVE flow before closing the socket — the qwave handle holds a
        // reference into the kernel-side socket state, and closing the socket first leaves
        // the QOS flow handle pointing at freed state. Order matters even though both calls
        // are wrapped in try/catch.
        try { networkPriority.Dispose(); } catch { /* ignore */ }
        udp.Dispose();
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
