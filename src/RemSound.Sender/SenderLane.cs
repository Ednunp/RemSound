using System.Security.Cryptography;
using RemSound.Core;

namespace RemSound.Sender;

/// <summary>
/// One outbound audio stream's worth of state. Each lane owns its own streamId, audio
/// sequence counter, frame accumulator, Opus encoder, format-resend timer and PCM frame id.
/// AudioSender holds three: the default lane, fed by the WASAPI capture child; the ASIO lane,
/// fed by the ASIO capture child in BothIndependent; and the plugin lane, fed DAW track blocks
/// from the plugin bridge. Each lane that is fed produces its own UDP stream on its own
/// streamId, multiplexed by the receiver's (endpoint, streamId) keying.
///
/// Threading: the hot-path methods (<see cref="OnMixedSamples"/> and below) are called from
/// the thread that feeds the lane — a capture callback or mix tick, or the plugin bridge's
/// receive thread. Each lane has exactly one such thread feeding it.
/// Cross-thread state read from AudioSender (codec, mute, opusFrameSamples, etc.) goes through
/// volatile fields on the owner.
///
/// Configuration mutations (<see cref="OnCodecChanged"/>, <see cref="OnPcmFrameSizeChanged"/>,
/// <see cref="SetRoute"/>) come from the UI thread and take AudioSender's configGate. Note what that
/// does and does NOT buy: the gate serialises those mutations against each other, not against the
/// capture thread, because the capture thread never takes it. So a codec or send-rate change can zero
/// frameAccumulatorWritten while the capture thread is mid-write into the accumulator. The worst case
/// is one malformed frame at the changeover — a single tick, on a deliberate user action that already
/// rotates the stream id and makes the receiver open a fresh session. That is an accepted cost, not a
/// guarantee: this comment used to say the gate serialised "in-flight accumulator writes" and it never
/// did. The Opus ENCODER swap is a different matter and IS properly guarded — see encoderGate, because
/// freeing native libopus state under a live Encode is a hard crash rather than a tick.
/// 2026-08-23 audit, finding S9.
/// </summary>
internal sealed class SenderLane
{
    private const int MixSampleRate = 48000;
    private const int MixChannels = 2;
    private const int MaxFrameStereoSamples = MixSampleRate * 20 / 1000 * MixChannels; // 1920, Opus 20 ms
    private const int FormatResendIntervalMs = 250;

    private readonly AudioSender owner;
    private readonly int opusBitrate;
    // Push to talk's hold on this lane: which of your paths it is, and where its fade has got to.
    private readonly PushToTalkGate.Lane pushToTalk;

    // Hot-path scratch. Sized to the largest possible single frame (Opus 20 ms = 1920 stereo
    // samples). A Standard PCM frame uses only the first 466, Opus 10 ms only the first 960. Reusing one
    // buffer means no realloc on codec change. outboundScratch is per-lane so two lanes don't
    // step on each other's packet construction.
    private readonly float[] frameAccumulator = new float[MaxFrameStereoSamples];
    private int frameAccumulatorWritten;
    private readonly byte[] outboundScratch = new byte[2048];

    // Audio encryption (always on as of the 2026-05-31 encryption feature). Each lane keeps its
    // OWN AES-GCM cipher because AES-GCM isn't thread-safe and the lanes run on separate
    // producer threads. Rebuilt only when the key reference changes (rare — a password change);
    // null when no password is set, in which case the lane sends nothing (mandatory encryption).
    // cipherScratch holds the per-frame ciphertext (plaintext + 28 bytes overhead); 4096 covers
    // the largest single frame (Opus 20 ms or PCM 5 ms) with room to spare.
    private AesGcm? cryptoGcm;
    private RemSoundCrypto.NonceSequence? cryptoNonces;
    private byte[]? cryptoKeyCached;
    private readonly byte[] cipherScratch = new byte[4096];

    // Per-stream sequence counters. audioSequence is what the receiver's gap-detector and Opus
    // FEC look at — it must stay monotonic per stream. formatSequence is used for the periodic
    // format-announce packet; receiver doesn't sequence-check format packets but having a
    // separate counter keeps the audio FEC clean.
    private uint audioSequence;
    private uint pcmFrameId;
    private uint formatSequence;
    private ushort streamId;
    // When the format packet last went out, on the audio clock (0 = never). The audio clock, not the wall clock, so the
    // simulator's virtual time paces the resends too - on the wall clock they went out at random points of a simulated
    // run, each one taking a turn of the fake network's dice, and no two runs sounded alike (2026-09-26).
    private long lastFormatPacketTicks;

    private OpusEncoderState opusEncoder;
    private int opusFrameStereoSamples;
    // Serialises the Opus encoder SWAP (OnCodecChanged, UI thread) against its USE (EmitOpusFrame,
    // capture thread). Without it, changing codec or send-rate while streaming could Dispose the native
    // libopus encoder mid-Encode on the capture thread — a native use-after-free / hard crash with no
    // managed stack. Held only for the encode + copy-out (microseconds) and the rare swap, so hot-path
    // contention is negligible.
    private readonly object encoderGate = new();
    // Copy of the just-encoded Opus bytes, taken under encoderGate so encryption + send can run OUTSIDE
    // the lock — LastEncoded returns a span into the encoder's own buffer, which the swap frees.
    private readonly byte[] opusPlainScratch = new byte[4096];

    // Per-lane pre-encode discontinuity probe. Moved here from AudioSender (2026-05-15) so
    // each lane has its OWN probe state and the cross-buffer step measurement (which carries
    // lastL/lastR across calls) only sees samples from one continuous audio stream. With the
    // earlier shared-probe design, BothIndependent mode mixed two unrelated streams' samples
    // into the same probe's cross-buffer carry, producing synthetic "steps" of arbitrary
    // magnitude every time the two lanes' callbacks interleaved — making the diag log unable
    // to tell a real capture glitch from instrumentation aliasing. Per-lane separation fixes
    // that without changing what the probe measures.
    private readonly AudioStepProbe preEncodeStepProbe = new();
    public float TakeMaxPreEncodeStepCrossBuffer() => preEncodeStepProbe.TakeMaxCrossBuffer();
    public float TakeMaxPreEncodeStepWithinBuffer() => preEncodeStepProbe.TakeMaxWithinBuffer();

    // Loudest absolute sample seen on this lane's pre-encode buffer since the last drain (resets on
    // read), surfaced on the diag line. ~0 = we are sending silence (mic blocked / muted / wrong
    // endpoint); a clear non-zero = real audio is reaching the encoder. This is the signal level the
    // log never had — which is exactly why a "mic sends silence" report couldn't be confirmed from it.
    private float preEncodePeak;
    public float TakeMaxPreEncodePeak() { var p = preEncodePeak; preEncodePeak = 0f; return p; }

    // Count of audio frames this lane actually handed to the wire (encode AND encrypt both
    // succeeded → SendAudio / SendPcmPart called) since the last drain. Pairs with preEncodePeak:
    // capPeak proves real signal reached the encoder INPUT, but every post-encode early-return —
    // the Opus encoder returning len<=0, no password so cryptoGcm is null, or the accumulator
    // never completing a frame — is INVISIBLE to it. This counts what actually left the machine,
    // so a log can finally tell "mic captured but nothing sent" (a drop at encode/encrypt) from
    // "mic captured and sent" (the silence is downstream). Added 2026-06-12 for Andre's
    // WASAPI-mic-only-works-in-ASIO investigation. Reset on read, like the peak.
    private long audioFramesSent;
    public long TakeAudioFramesSent() => Interlocked.Exchange(ref audioFramesSent, 0);

    // Largest gap between two audio frames LEAVING this lane, in Stopwatch ticks. Reset on read.
    //
    // This is the number the far end's jitter buffer actually feels, and nothing else here measures
    // it. A lane's per-second packet rate can look perfect while its packets leave in clumps, and a
    // clump plus a gap is exactly what an underrun on somebody else's machine is made of. The two
    // capture lanes are clocked by an audio callback and should sit at the frame period; the PLUGIN
    // lane is fed from the bridge's network receive thread, which is not an audio thread and is also
    // answering a receiving instance's requests, so it is the one this exists for. Comparing the two
    // in the same log line is the whole point — one is the control.
    //
    // Producer thread only, like the peak and the probe beside it.
    private long lastEmitTicks;
    private long maxEmitGapTicks;

    private void NoteEmitted()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        // Skip the first emit after a (re)start: the gap since "never" is not a gap.
        if (lastEmitTicks != 0)
        {
            var gap = now - lastEmitTicks;
            if (gap > maxEmitGapTicks) maxEmitGapTicks = gap;
        }
        lastEmitTicks = now;
    }

    /// <summary>Largest gap between two audio frames leaving this lane since the last call, in
    /// milliseconds. Resets on read.</summary>
    public int TakeMaxEmitGapMs()
    {
        var ticks = Interlocked.Exchange(ref maxEmitGapTicks, 0);
        return (int)(ticks * 1000 / System.Diagnostics.Stopwatch.Frequency);
    }

    // Which render route this lane announces in its format packets. RemSound's own receiver no
    // longer decides where a stream plays by it — every stream plays on every ticked output — but
    // the supersede rule on every port only retires a session whose lane MATCHES, so two live lanes
    // from one sender must never announce the same value. AudioSender assigns routes via SetRoute:
    // Mixed (the pre-2026-05-11 value) on the default lane in WasapiOnly, WasapiLane / AsioLane on
    // the capture lanes in BothIndependent, and whichever is left free on the plugin lane.
    private volatile RenderRoute route = RenderRoute.Mixed;
    public RenderRoute Route => route;

    public ushort StreamId => streamId;

    public SenderLane(AudioSender owner, int initialOpusFrameSamplesPerChannel, int opusBitrate, SendLaneKind kind)
    {
        this.owner = owner;
        this.kind = kind;
        pushToTalk = new PushToTalkGate.Lane(kind);
        this.opusBitrate = opusBitrate;
        opusEncoder = new OpusEncoderState(initialOpusFrameSamplesPerChannel, opusBitrate);
        opusFrameStereoSamples = opusEncoder.FrameSizePerChannel * MixChannels;
        streamId = NewStreamId();
    }

    private static ushort NewStreamId() => (ushort)Random.Shared.Next(1, ushort.MaxValue);

    /// <summary>
    /// Set this lane's render route. Called by AudioSender when audio-mode changes — e.g.
    /// switching into BothIndependent flips the default lane from Mixed to WasapiLane and
    /// activates the asio lane as AsioLane. Rotates streamId and forces an immediate format
    /// re-announce so the receiver opens a fresh session with the new Lane tag rather than
    /// continuing to route the existing session under the old tag.
    /// </summary>
    public void SetRoute(RenderRoute newRoute)
    {
        if (route == newRoute) return;
        route = newRoute;
        streamId = NewStreamId();
        lastFormatPacketTicks = 0;
        frameAccumulatorWritten = 0;
        lastEmitTicks = 0;
    }

    /// <summary>Reset per-lane counters and pick a new streamId. Called from
    /// <see cref="AudioSender.Start"/> so the receiver sees a fresh session on each start.</summary>
    public void ResetForStart()
    {
        streamId = NewStreamId();
        audioSequence = 0;
        pcmFrameId = 0;
        formatSequence = 0;
        frameAccumulatorWritten = 0;
        lastFormatPacketTicks = 0;
        lastEmitTicks = 0;
    }

    /// <summary>Gate seam: see <see cref="AudioSender.StartWireCountersAtForTest"/>.</summary>
    internal void StartWireCountersAtForTest(uint value)
    {
        audioSequence = value;
        pcmFrameId = value;
        formatSequence = value;
    }

    /// <summary>
    /// Codec just changed. Rotates streamId (the receiver opens a fresh session at the new
    /// format), rebuilds the Opus encoder if Opus is in play, and zeroes the accumulator so
    /// any half-filled frame from the previous format doesn't leak into the new one.
    /// </summary>
    public void OnCodecChanged(AudioTransportCodec newCodec, int opusFrameSamplesPerChannel)
    {
        if (newCodec == AudioTransportCodec.Opus)
        {
            // Dispose the outgoing encoder before replacing it — its underlying NativeOpusEncoder owns
            // native libopus state that doesn't get released until explicit Dispose under our
            // SustainedLowLatency GC mode. Under encoderGate so the capture thread can't be mid-Encode on
            // the old native encoder when we free it (that was a native use-after-free on a codec/rate
            // change while streaming).
            lock (encoderGate)
            {
                opusEncoder.Dispose();
                opusEncoder = new OpusEncoderState(opusFrameSamplesPerChannel, opusBitrate);
                opusFrameStereoSamples = opusEncoder.FrameSizePerChannel * MixChannels;
            }
        }
        streamId = NewStreamId();
        lastFormatPacketTicks = 0;
        frameAccumulatorWritten = 0;
    }

    /// <summary>PCM frame size just changed. Rotates streamId so the receiver sees a fresh
    /// session at the new packet cadence and resets the accumulator. No encoder rebuild —
    /// Opus is unaffected by the PCM send-rate setting.</summary>
    public void OnPcmFrameSizeChanged()
    {
        streamId = NewStreamId();
        lastFormatPacketTicks = 0;
        frameAccumulatorWritten = 0;
    }

    // === hot path ===

    // Which capture this lane carries: the WASAPI capture, the ASIO inputs, or a DAW track through the plugin link.
    private readonly SendLaneKind kind;
    // The frames in the last block this lane was handed. On the plugin lane that block IS the DAW's buffer, so it is the
    // wait the plugin path knows about (see CaptureLatencyMs).
    private volatile int lastBlockFrames;

    /// <summary>
    /// THIS LANE'S OWN capture delay, for its format packet (N7, Ed 2026-09-28: "FIX IT... that's going to be important
    /// later"). Every lane used to announce one figure for the whole machine - the ASIO input's whenever ASIO ran, so the
    /// WASAPI stream in "both" claimed ASIO's 1-3 ms for a capture that takes 10 or more, and the plugin lane claimed a
    /// device it never touched. Now the WASAPI lane carries the WASAPI capture's figure, the ASIO lane the ASIO input's,
    /// and the plugin lane the DAW block it was handed (the host owns the device and does not say more). The value only:
    /// the packet's layout is unchanged, and every port reads the same field.
    /// </summary>
    internal double CaptureLatencyMs => kind == SendLaneKind.Plugin
        ? lastBlockFrames * 1000.0 / 48000
        : owner.CaptureLatencyMsFor(kind);

    public void OnMixedSamples(ReadOnlyMemory<float> stereoFloats)
    {
        var span = stereoFloats.Span;
        if (span.IsEmpty) return;
        // Push to talk, before anything else sees the block: what is encoded, probed and (unless the recording bypass says
        // otherwise) recorded is what goes out. Off, or not this lane's path, it is the block itself.
        var ungated = stereoFloats;
        stereoFloats = owner.PushToTalk.Apply(pushToTalk, stereoFloats);
        span = stereoFloats.Span;
        lastBlockFrames = span.Length / MixChannels;
        // For the server's metronome: which frame this block starts at, and when its first sample reached the input.
        blockFirstFrame = framesIn;
        framesIn += lastBlockFrames;
        if (owner.TimingWanted) blockCapturedUs = CaptureTimeOfBlock((owner.CaptureClockForTest?.Invoke() ?? MetronomeClock.NowUs()) - (long)(owner.InputDelayMsFor(kind) * 1000));
        // The Jamulus-style stream (JamulusStyle): on the ASIO lane, in place of Opus - a PCM choice stays PCM - with the
        // library loaded, and only while some target plays through ASIO and has said so. Those targets then get ONLY that
        // stream from this lane; everyone else keeps this lane's normal one.
        customActive = route == RenderRoute.AsioLane && owner.Codec == AudioTransportCodec.Opus && OpusCustomNative.Available
                       && owner.AnyCustomOpusTarget();
        if (customActive) ProcessCustomOpus(span);
        else if (customEncoder is not null) { customEncoder.Dispose(); customEncoder = null; }

        // Whole-callback timing — captures encode plus kernel send for the SNAP's emitMs
        // column. Skipped entirely when diagnostics are off so the audio thread doesn't pay
        // two Stopwatch reads + a CAS loop per callback for a number nobody is going to log.
        var diag = RemSound.Core.DiagnosticsGate.Enabled;
        var emitStart = diag ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
        EnsureFormatPacketSent();

        // Recording tap — the recorder gets the float audio about to be encoded. The lane
        // doesn't know whether the recorder is running; the dispatcher early-outs when no
        // callback is wired. Captured here (before encoding) so the recording is bit-clean
        // float, independent of which codec the wire is using. The lane tag is forwarded so
        // BothIndependent mode (where both WASAPI and ASIO SenderLanes fire on every capture
        // callback) can be correctly handled by the recorder — each lane writes into its own
        // ring, and the recorder mixes them rather than appending them sequentially (which
        // would double the file's effective sample rate). 2026-05-15 fix.
        owner.DispatchSentSamples(owner.PushToTalk.RecordsUngated ? ungated : stereoFloats, route);
        // The web listener hears what peers hear (2026-10-10).
        owner.DispatchWebSamples(stereoFloats, route);

        // Discontinuity probe — what does the audio look like just before we encode it?
        // Compared to the receiver's per-stage probes, this tells us whether artefacts are
        // present at the sender side already (capture hardware glitch, mix-bus issue) or
        // introduced somewhere in the wire / decode / playout chain. Per-lane probe — see
        // <see cref="preEncodeStepProbe"/> field comment for why this isn't shared with the
        // other lane in BothIndependent.
        preEncodeStepProbe.ScanStereo(span);
        // Capture-level peak alongside the discontinuity probe — the loudest sample about to be sent.
        var peak = preEncodePeak;
        for (var s = 0; s < span.Length; s++)
        {
            var a = span[s] < 0f ? -span[s] : span[s];
            if (a > peak) peak = a;
        }
        preEncodePeak = peak;

        var carriedBefore = frameAccumulatorWritten;
        switch (owner.Codec)
        {
            case AudioTransportCodec.Pcm:
                ProcessPcm(span);
                break;
            case AudioTransportCodec.Opus:
                ProcessOpus(span);
                break;
        }
        NoteSendWait(carriedBefore, span.Length, frameAccumulatorWritten);
        if (diag) owner.RecordEmitTicks(System.Diagnostics.Stopwatch.GetTimestamp() - emitStart);
    }

    // WHEN EACH PACKET'S SOUND WAS CAPTURED, for the server's metronome (2026-09-29): the server pairs these with when the
    // packets reached it, and so learns how long this machine's sound takes to get there - clocks and all. Kept only while
    // AudioSender.TimingWanted; the newest few are read by the window for its report.
    private long framesIn;
    private long blockFirstFrame;
    private long blockCapturedUs;
    private readonly object timingGate = new();

    // THE CAPTURE TIMELINE (2026-09-29, Ed after the first jam through the Pi: "see if that code to lock can be made
    // better"). A block reaches this lane late by however long the capture thread took to run - a WASAPI capture by
    // milliseconds, now and then more - and never early. Timing each block by the moment it arrived carried that lateness
    // into every capture time, and the server keeps the LEAST delay it sees, so it kept the block that ran latest: this
    // machine's beat came late by the worst of it. Instead each block says when frame 0 of the lane must have been captured
    // (its arrival, less the input's delay, less the frames before it); the earliest of the last 2 to 4 seconds is the one
    // that ran on time, and every packet is timed from it by its frame count. Two buckets, so the reading follows the card's
    // clock as it drifts against this one (at 100 ppm, 0.4 ms at most). A jump later by more than CaptureOriginJumpUs - a
    // capture that restarted or lost sound - starts it again, as does a pause in the timing (not on a server for a while).
    private const long CaptureOriginBucketUs = 2_000_000;
    private const long CaptureOriginJumpUs = 20_000;
    private long originCurrentUs = long.MaxValue, originPreviousUs = long.MaxValue, originBucketStartUs;

    /// <summary>When this block's first sample reached the input, from the lane's capture timeline (see above), given the
    /// block's own reading: <paramref name="arrivedLessInputUs"/>, the moment it arrived less the input's delay.</summary>
    private long CaptureTimeOfBlock(long arrivedLessInputUs)
    {
        var framesUs = (long)Math.Round(blockFirstFrame * 1_000_000.0 / 48_000);
        var origin = arrivedLessInputUs - framesUs;
        var envelope = Math.Min(originCurrentUs, originPreviousUs);
        if (envelope == long.MaxValue || origin - envelope > CaptureOriginJumpUs
            || arrivedLessInputUs - originBucketStartUs >= 2 * CaptureOriginBucketUs)   // not timed for a while: start again
        {
            originCurrentUs = originPreviousUs = origin;
            originBucketStartUs = arrivedLessInputUs;
        }
        else
        {
            if (arrivedLessInputUs - originBucketStartUs >= CaptureOriginBucketUs)
            {
                originPreviousUs = originCurrentUs;
                originCurrentUs = long.MaxValue;
                originBucketStartUs = arrivedLessInputUs;
            }
            originCurrentUs = Math.Min(originCurrentUs, origin);
        }
        return Math.Min(originCurrentUs, originPreviousUs) + framesUs;
    }
    private readonly (ushort Stream, uint Sequence, long CapturedUs)[] timingRing = new (ushort, uint, long)[ServerMetronomeWire.MaxReportEntries];
    private int timingNext, timingCount;

    /// <summary>A packet went out (its sequence moved on): note when its first sample was captured - <paramref name="frameInBlock"/>
    /// frames from this block's first, which may be before it (a sample carried over from the last block).</summary>
    private void NoteSent(ushort stream, uint sequenceBefore, uint sequenceAfter, int frameInBlock)
    {
        if (sequenceAfter == sequenceBefore || !owner.TimingWanted) return;
        var captured = blockCapturedUs + (long)Math.Round(frameInBlock * 1_000_000.0 / 48_000);
        lock (timingGate)
        {
            timingRing[timingNext] = (stream, sequenceAfter, captured);
            timingNext = (timingNext + 1) % timingRing.Length;
            timingCount = Math.Min(timingCount + 1, timingRing.Length);
        }
    }

    /// <summary>The packets noted since the last call, oldest first; forgotten.</summary>
    internal List<(ushort Stream, uint Sequence, long CapturedUs)> TakeTiming()
    {
        lock (timingGate)
        {
            var list = new List<(ushort, uint, long)>(timingCount);
            for (var i = timingCount; i > 0; i--) list.Add(timingRing[(timingNext - i + timingRing.Length) % timingRing.Length]);
            timingCount = 0;
            return list;
        }
    }

    // THE SENDING WAIT, MEASURED (2026-09-28, PLAN-fixes E3): how long a sample waits in this lane's accumulator between
    // arriving and going out in a packet, on average, weighted by samples. The latency readout counted half a packet for
    // it. But a WASAPI capture block of 10 ms cut into 4.85 ms PCM frames sends nearly every sample in the callback it
    // arrived in, so the true wait is close to nothing - one of the things that made the WASAPI readout read high. Only the
    // samples carried over from an earlier callback wait at all, and they all go out with the next packet.
    private long carriedArrivalTicks;
    private long sendWaitTicks;     // sample-weighted
    private long sendWaitSamples;

    private void NoteSendWait(int carriedBefore, int arrived, int carriedAfter)
    {
        var now = AudioClock.GetTimestamp();
        var sent = carriedBefore + arrived - carriedAfter;
        if (sent > 0)
        {
            // The carried samples sit at the front of the accumulator, so a packet sent now took them all; the rest of
            // what was sent arrived now and waited nothing.
            if (carriedBefore > 0) Interlocked.Add(ref sendWaitTicks, (now - carriedArrivalTicks) * carriedBefore);
            Interlocked.Add(ref sendWaitSamples, sent);
            carriedArrivalTicks = now;
        }
        else if (carriedBefore + arrived > 0)
        {
            // Nothing sent: the new samples join the carried ones, their arrival averaged in.
            carriedArrivalTicks = carriedBefore == 0 ? now
                : carriedArrivalTicks + (long)((now - carriedArrivalTicks) * (arrived / (double)(carriedBefore + arrived)));
        }
    }

    /// <summary>The sending wait since the last call: sample-weighted ticks and the samples they cover; both reset.</summary>
    internal (long Ticks, long Samples) TakeSendWait() => (Interlocked.Exchange(ref sendWaitTicks, 0), Interlocked.Exchange(ref sendWaitSamples, 0));

    private void ProcessPcm(ReadOnlySpan<float> samples)
    {
        // Tight-latency mode: emit each delivered sample buffer as its own packet instead of
        // accumulating to the PCM frame size. Saves up to (frame_size_ms / 2) of average
        // accumulator delay. Variable packet size per call. Capped at the Standard frame size, the
        // largest that still goes out as one encrypted packet (AudioSender.PcmStandardSamplesPerChannel),
        // so a 480-frame WASAPI push buffer or a 512-frame ASIO buffer is cut into pieces that each fit.
        // Normal ASIO buffer sizes (64/128) never reach the cap.
        //
        // The pieces are EQUAL (Ed, 2026-10-01): a 480-frame block goes out as 160 + 160 + 160, a 512-frame one as
        // 171 + 171 + 170. They were cut 233 + 233 + 14, and the receiver makes up a lost piece at the size of the one
        // after it (StreamSession.ConcealLostPcmFrames) - so a lost 233 was made up as 14 and a lost 14 as 233. The
        // receiver's clock matching then misread the sender by hundreds of ppm, the buffer wandered between 25 and 70 ms
        // and broke up: PCM over the internet on WASAPI played about 3 cents flat (the simulator, 2026-10-01). The same
        // number of packets as before; every port already takes PCM packets of any size.
        if (owner.IsTightLatencyEnabled)
        {
            const int MaxFramesPerPacket = AudioSender.PcmStandardSamplesPerChannel;
            var frames = samples.Length / MixChannels;
            var pieces = Math.Max(1, (frames + MaxFramesPerPacket - 1) / MaxFramesPerPacket);
            var pos = 0;
            for (var piece = 0; piece < pieces; piece++)
            {
                var chunk = (frames / pieces + (piece < frames % pieces ? 1 : 0)) * MixChannels;
                if (chunk == 0) continue;
                var sequenceBefore = audioSequence;
                EmitPcmFrame(samples.Slice(pos, chunk));
                NoteSent(streamId, sequenceBefore, audioSequence, pos / MixChannels);
                pos += chunk;
            }
            return;
        }

        var pcmFrameStereoSamples = owner.PcmFrameStereoSamples;
        var idx = 0;
        while (idx < samples.Length)
        {
            var spaceLeftForPcmFrame = pcmFrameStereoSamples - frameAccumulatorWritten;
            var copy = Math.Min(spaceLeftForPcmFrame, samples.Length - idx);
            samples.Slice(idx, copy).CopyTo(frameAccumulator.AsSpan(frameAccumulatorWritten));
            frameAccumulatorWritten += copy;
            idx += copy;

            if (frameAccumulatorWritten == pcmFrameStereoSamples)
            {
                var before = audioSequence;
                EmitPcmFrame(frameAccumulator.AsSpan(0, pcmFrameStereoSamples));
                frameAccumulatorWritten = 0;
                NoteSent(streamId, before, audioSequence, (idx - pcmFrameStereoSamples) / MixChannels);
            }
        }
    }

    private void ProcessOpus(ReadOnlySpan<float> samples)
    {
        var frameSamples = opusFrameStereoSamples;
        var idx = 0;
        while (idx < samples.Length)
        {
            var spaceLeft = frameSamples - frameAccumulatorWritten;
            var copy = Math.Min(spaceLeft, samples.Length - idx);
            samples.Slice(idx, copy).CopyTo(frameAccumulator.AsSpan(frameAccumulatorWritten));
            frameAccumulatorWritten += copy;
            idx += copy;

            if (frameAccumulatorWritten == frameSamples)
            {
                var before = audioSequence;
                EmitOpusFrame(frameAccumulator.AsSpan(0, frameSamples));
                frameAccumulatorWritten = 0;
                NoteSent(streamId, before, audioSequence, (idx - frameSamples) / MixChannels);
            }
        }
    }

    private void EmitPcmFrame(ReadOnlySpan<float> stereoFloats)
    {
        var bytesOnWire = stereoFloats.Length * 3;
        Span<byte> int24 = stackalloc byte[bytesOnWire];
        if (owner.IsMuted)
        {
            int24.Clear();
        }
        else
        {
            PcmPack.FloatToInt24LE(stereoFloats, int24);
        }
        EnsureCrypto();
        if (cryptoGcm is null) return; // no password yet → never send audio in the clear
        // Encrypt the whole PCM frame, then split the ciphertext across as many parts as the
        // Ethernet payload budget needs (the +28-byte crypto overhead can push a 5 ms frame over
        // a single datagram). The receiver reassembles the parts and then decrypts.
        var ctLen = RemSoundCrypto.EncryptInto(cryptoGcm, cryptoNonces!, int24, cipherScratch);
        var maxPart = RemPacket.MaxAudioPayloadBytes;
        var totalParts = (byte)((ctLen + maxPart - 1) / maxPart);
        pcmFrameId++;
        Interlocked.Increment(ref audioFramesSent);
        NoteEmitted();
        for (byte part = 0; part < totalParts; part++)
        {
            var offset = part * maxPart;
            var len = Math.Min(maxPart, ctLen - offset);
            SendPcmPart(pcmFrameId, part, totalParts, cipherScratch.AsSpan(offset, len));
        }
    }

    private void EmitOpusFrame(ReadOnlySpan<float> stereoFloats)
    {
        int encLen;
        // Encode and copy the bytes out UNDER encoderGate, so a concurrent OnCodecChanged can't Dispose
        // the encoder mid-Encode (native use-after-free) or free the LastEncoded buffer before we copy
        // it. Crypto + send run outside the lock, off the copied bytes.
        lock (encoderGate)
        {
            if (owner.IsMuted)
            {
                Span<float> silence = stackalloc float[opusFrameStereoSamples];
                silence.Clear();
                encLen = opusEncoder.Encode(silence);
            }
            else
            {
                encLen = opusEncoder.Encode(stereoFloats);
            }
            if (encLen <= 0) return;
            opusEncoder.LastEncoded(encLen).CopyTo(opusPlainScratch);
        }
        EnsureCrypto();
        if (cryptoGcm is null) return; // no password yet → never send audio in the clear
        var ctLen = RemSoundCrypto.EncryptInto(cryptoGcm, cryptoNonces!, opusPlainScratch.AsSpan(0, encLen), cipherScratch);
        Interlocked.Increment(ref audioFramesSent);
        NoteEmitted();
        SendAudio(cipherScratch.AsSpan(0, ctLen));
    }

    // === wire path ===

    /// <summary>Is a format packet due - never sent, or the resend interval gone by on the audio clock?</summary>
    private static bool FormatDue(long lastTicks)
        => lastTicks == 0 || AudioClock.GetTimestamp() - lastTicks >= FormatResendIntervalMs * System.Diagnostics.Stopwatch.Frequency / 1000;

    private void EnsureFormatPacketSent()
    {
        if (!FormatDue(lastFormatPacketTicks)) return;
        lastFormatPacketTicks = AudioClock.GetTimestamp();

        // Wire field FrameSamplesPerChannel: receiver uses this for buffer sizing and the
        // decoder hot path. PCM passes through the sender's own sample-count directly; Opus
        // uses whatever the encoder is configured for. v3.0 wire format — see
        // AudioFormatInfo doc comment for the semantic-shift rationale.
        var codec = owner.Codec;
        var opusFrameSamples = owner.OpusFrameSamplesPerChannel;
        var pcmFrameSamples = owner.PcmFrameSamplesPerChannel;
        // Pass this lane's current Route as the Lane field — see the route field for what
        // receivers do with it.
        // OUR OWN capture latency travels with the format, so the receiving end can report the real
        // journey instead of substituting its own 10 ms guess for a stage that happens HERE (2026-08-24).
        // This lane's own figure: each lane is its own capture stage (see CaptureLatencyMs).
        var captureLatencyMs = CaptureLatencyMs;
        var format = codec == AudioTransportCodec.Opus
            ? new AudioFormatInfo(48000, 2, 16, 1, 4, 192_000, (int)AudioTransportCodec.Opus, opusFrameSamples, route, captureLatencyMs)
            : new AudioFormatInfo(48000, 2, 24, 1, 6, 288_000, (int)AudioTransportCodec.Pcm, pcmFrameSamples, route, captureLatencyMs);
        // While this machine plays through ASIO (and the library loaded), every Format packet it sends says it can take
        // the Jamulus-style stream - with the heartbeat's flag, how a far RemSound learns it may send one here.
        if (JamulusStyle.CanTakeIt)
            format = format with { LabFlags = AudioFormatInfo.LabFlagCanDecodeCustomOpus };

        // Allocate room for the longest format payload (RemPacket.FormatPayloadWithCaptureSize, 46
        // bytes) — see RemPacket for the backward-compat contract. Old receivers parse the first 32
        // bytes and ignore the rest; newer ones read the Lane byte from the extension. The Lane value
        // carried here is this lane's current route, passed into the AudioFormatInfo above.
        Span<byte> packet = stackalloc byte[RemPacket.HeaderSize + RemPacket.FormatPayloadWithCaptureSize];
        RemPacket.WriteHeader(packet, RemPacketType.Format, streamId, ++formatSequence);
        // Append our password fingerprint so the peer can tell whether its profile password
        // matches ours without anyone sending the password, and our capture latency after it.
        // WriteFormatPayload returns 36 (no fingerprint), 44 (fingerprint) or 46 (fingerprint +
        // capture); we send exactly that many payload bytes, and every reader takes a MINIMUM
        // length, so a peer that has never heard of the last field is unaffected.
        var payloadLen = RemPacket.WriteFormatPayload(packet[RemPacket.HeaderSize..], format, owner.AudioFingerprint);
        SendMain(packet[..(RemPacket.HeaderSize + payloadLen)]);
    }

    /// <summary>Rebuild this lane's AES-GCM cipher if the owner's audio key reference changed.
    /// Cheap reference check on the hot path; the actual rebuild only happens on a password
    /// change. Null key (no password) leaves the cipher null, which stops the lane sending.</summary>
    private void EnsureCrypto()
    {
        var key = owner.AudioKey;
        if (ReferenceEquals(key, cryptoKeyCached)) return;
        cryptoGcm?.Dispose();
        cryptoGcm = key is null ? null : RemSoundCrypto.CreateGcm(key);
        // Fresh nonce sequence with the fresh cipher: new random prefix, counter from zero —
        // a rebuilt key never continues an old counter, and an old key never sees a reused one.
        cryptoNonces = key is null ? null : new RemSoundCrypto.NonceSequence();
        cryptoKeyCached = key;
    }

    /// <summary>Release the AES-GCM cipher's native handle. Called from AudioSender.Dispose so
    /// the handle doesn't leak on teardown (same native-handle discipline as the Opus encoder).</summary>
    public void DisposeCrypto()
    {
        cryptoGcm?.Dispose();
        cryptoGcm = null;
        cryptoKeyCached = null;
    }

    private void SendPcmPart(uint frameId, byte partIndex, byte totalParts, ReadOnlySpan<byte> partBytes)
    {
        var headerSize = RemPacket.HeaderSize;
        var subHeaderSize = RemPcmFrame.SubHeaderSize;
        var totalLen = headerSize + subHeaderSize + partBytes.Length;
        var dst = outboundScratch.AsSpan(0, totalLen);
        RemPacket.WriteHeader(dst, RemPacketType.Audio, streamId, ++audioSequence);
        RemPcmFrame.WriteSubHeader(dst.Slice(headerSize, subHeaderSize), frameId, partIndex, totalParts);
        partBytes.CopyTo(dst[(headerSize + subHeaderSize)..]);
        SendMain(dst);
    }

    private void SendAudio(ReadOnlySpan<byte> opusBytes)
    {
        var totalLen = RemPacket.HeaderSize + opusBytes.Length;
        var dst = outboundScratch.AsSpan(0, totalLen);
        RemPacket.WriteHeader(dst, RemPacketType.Audio, streamId, ++audioSequence);
        opusBytes.CopyTo(dst[RemPacket.HeaderSize..]);
        SendMain(dst);
    }

    /// <summary>This lane's normal stream: to everyone, or - while the Jamulus-style stream is running - to everyone
    /// except the targets that take that instead.</summary>
    private void SendMain(ReadOnlySpan<byte> packet)
    {
        if (customActive) owner.SendToSome(packet, customOpusTargets: false);
        else owner.SendToAll(packet);
    }

    // === The Jamulus-style stream (2026-09-26; automatic since 2026-10-01, see JamulusStyle) ===
    // Its own stream id, sequence, accumulator and custom-mode encoder; encrypted with this lane's key and nonces (same
    // thread, so every nonce stays unique); sent only to targets that asked for it (AudioSender.IsCustomOpusTarget).
    private volatile bool customActive;
    private OpusCustomEncoder? customEncoder;
    private ushort customStreamId;
    private uint customSequence;
    private uint customFormatSequence;
    private long lastCustomFormatTicks;
    private readonly float[] customAccumulator = new float[256 * MixChannels];
    private int customWritten;
    private readonly byte[] customPlain = new byte[1500];
    private long customFramesSent;

    /// <summary>True while this lane is sending the Jamulus-style stream to someone.</summary>
    public bool CustomOpusActive => customActive;

    /// <summary>Jamulus-style frames sent by this lane so far.</summary>
    public long CustomOpusFramesSent => Interlocked.Read(ref customFramesSent);

    private void ProcessCustomOpus(ReadOnlySpan<float> samples)
    {
        var frames = JamulusStyle.FrameSamples;
        if (customEncoder is null || customEncoder.FrameSamplesPerChannel != frames)
        {
            customEncoder?.Dispose();
            customEncoder = null;
            try { customEncoder = new OpusCustomEncoder(frames, MixChannels); }
            catch { customActive = false; return; }
            customStreamId = NewStreamId();
            customSequence = 0;
            customWritten = 0;
            lastCustomFormatTicks = 0;
        }
        if (FormatDue(lastCustomFormatTicks)) SendCustomFormat(frames);
        var frameFloats = frames * MixChannels;
        var idx = 0;
        while (idx < samples.Length)
        {
            var copy = Math.Min(frameFloats - customWritten, samples.Length - idx);
            samples.Slice(idx, copy).CopyTo(customAccumulator.AsSpan(customWritten));
            customWritten += copy;
            idx += copy;
            if (customWritten == frameFloats)
            {
                var before = customSequence;
                EmitCustomFrame(customAccumulator.AsSpan(0, frameFloats));
                customWritten = 0;
                NoteSent(customStreamId, before, customSequence, (idx - frameFloats) / MixChannels);
            }
        }
    }

    private void EmitCustomFrame(Span<float> frame)
    {
        if (owner.IsMuted) frame.Clear();
        var len = customEncoder!.Encode(frame, customPlain);
        if (len <= 0) return;
        EnsureCrypto();
        if (cryptoGcm is null) return; // no password yet: never send audio in the clear
        var ctLen = RemSoundCrypto.EncryptInto(cryptoGcm, cryptoNonces!, customPlain.AsSpan(0, len), cipherScratch);
        var totalLen = RemPacket.HeaderSize + ctLen;
        var dst = outboundScratch.AsSpan(0, totalLen);
        RemPacket.WriteHeader(dst, RemPacketType.Audio, customStreamId, ++customSequence);
        cipherScratch.AsSpan(0, ctLen).CopyTo(dst[RemPacket.HeaderSize..]);
        Interlocked.Increment(ref customFramesSent);
        owner.SendToSome(dst, customOpusTargets: true);
    }

    private void SendCustomFormat(int frames)
    {
        lastCustomFormatTicks = AudioClock.GetTimestamp();
        // Its flag says what every Format packet from here says: whether THIS copy can take the stream back.
        var format = new AudioFormatInfo(48000, 2, 16, 1, 4, 192_000, (int)AudioTransportCodec.OpusCustom, frames, route, CaptureLatencyMs)
            { LabFlags = JamulusStyle.CanTakeIt ? AudioFormatInfo.LabFlagCanDecodeCustomOpus : (byte)0 };
        Span<byte> packet = stackalloc byte[RemPacket.HeaderSize + RemPacket.FormatPayloadWithCaptureSize];
        RemPacket.WriteHeader(packet, RemPacketType.Format, customStreamId, ++customFormatSequence);
        var payloadLen = RemPacket.WriteFormatPayload(packet[RemPacket.HeaderSize..], format, owner.AudioFingerprint);
        owner.SendToSome(packet[..(RemPacket.HeaderSize + payloadLen)], customOpusTargets: true);
    }
}
