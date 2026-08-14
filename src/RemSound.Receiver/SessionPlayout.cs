using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using NAudio.Dsp;
using RemSound.Core;

namespace RemSound.Receiver;

/// <summary>
/// One incoming sender's playout state: its own SPSC ring buffer plus a small set of drift /
/// concealment / smoothness state. Each remote endpoint that's actively sending audio gets
/// exactly one SessionPlayout. <see cref="PlayoutEngine"/> owns the collection and reads from
/// all of them per render callback, summing into the mix bus.
///
/// Drift correction: each sender has its own audio crystal that runs at slightly different
/// rate from the receiver's. This class compensates with a slow integrator that drops or
/// repeats one stereo frame at a time when sustained drift is detected, with a short cosine
/// crossfade across each splice for inaudibility. See <c>DriftGain</c> / <c>DriftCrossfadeFrames</c>.
///
/// Threading: <see cref="Write"/> runs on the network thread (per-sender producer);
/// <see cref="ReadFloats"/> runs on the WASAPI/ASIO render thread (single consumer). The
/// AudioRingBuffer is SPSC-safe; drift / concealment state is only touched from the consumer.
/// </summary>
internal sealed class SessionPlayout : IDisposable
{
    private const int MixSampleRate = 48000;
    private const int MixChannels = 2;
    private const int MixBytesPerFrame = MixChannels * sizeof(float);
    private const int MixBytesPerSecond = MixSampleRate * MixBytesPerFrame;

    private readonly AudioRingBuffer playout;

    private volatile bool playbackArmed;
    private volatile bool drainRequested;

    // Tracks the largest single Write's audio duration in ms — i.e. the active codec's
    // packet-frame size as observed at the buffer level. Used to floor the click-trim
    // margin so we don't false-trim during the natural sawtooth caused by packet
    // arrival (each packet bumps the buffer by frame-ms, then render drains it down).
    // Updated from the network thread; read from the audio thread. Volatile is enough
    // because we only ever monotonically increase it within a session lifetime.
    private volatile int largestWriteMs;

    // === Drop-cause split ===
    // Codex pointed out that the legacy `DropCount` on the ring buffer rolled up every reason
    // we ever dropped audio bytes, making "Drops" in the diag opaque. These per-cause counters
    // let the diag log distinguish:
    //   * trim drops      — smoothness-knob click-trim trimming the buffer toward target
    //   * drain drops     — one-shot drain when the user moves the latency slider
    //   * catastrophic    — TrimFromProducer when the buffer crosses the 1s safety cap
    // (Ring-buffer overflow on Write is still counted in playout.DropCount; we expose that
    // separately.) Each counter is in BYTES so the magnitudes are comparable.
    private long trimDropBytes;
    private long drainDropBytes;
    // Separate count of how many TIMES the click-trim fired (a tiny number tells us frequency,
    // independent of the byte amount).
    private long trimFireCount;

    // Reference latency the click-trim threshold is measured against, distinct from the live
    // target. It snaps UP to the target instantly (raising never trims) but eases DOWN slowly when
    // the target is lowered - e.g. the continuous auto-tune walking the latency down. Without this
    // glide, each downward target step drops the trim threshold under the buffer's natural sawtooth
    // and the trim shaves a chunk off the buffer on the spot: an audible click on every step (the
    // "crackly while it tunes" Ed reported, worse at a 3-second tune interval, and on ASIO too since
    // its tighter smoothness=1 trim is even more eager). Easing the ceiling down slower than the
    // depth corrector drains the buffer lets the buffer slide down UNDER the threshold instead of
    // being trimmed. Genuine bloat above the gliding ceiling still trims. In steady state it equals
    // the target, so normal operation - including ASIO Tight mode's burst snap-back - is unchanged.
    private double trimGlideTargetMs;
    private long prevTrimGlideTicks;

    // === Underrun concealment state ===
    // When the playout ring buffer comes up short on a render-side read, AudioRingBuffer
    // silence-fills the missing portion with hard zero. The transient from the last real
    // sample (amplitude X) to instant zero produces an audible click, especially on PCM
    // (Opus has its own decoder-side PLC for packet loss but doesn't help with audio-thread
    // starvation). We replace that hard zero with a brief envelope from the last real sample
    // down to silence, and a matching envelope back up when audio resumes. The buffer is still
    // silent during a sustained underrun — but the *edges* are smooth, which is where the
    // human ear hears the click. ConcealFadeFramesShort at 32 = ~0.67 ms at 48 kHz.
    //
    // The artifact character is user-pickable (cosine tone short / cosine tone low / noise
    // burst / raw click). Each option uses the same edge-smoothing principle but a different
    // generator for the burst itself; see ApplyFadeOut / ApplyFadeIn.
    private const int ConcealFadeFramesShort = 32;
    private const int ConcealFadeFramesLow = 96;
    // After this many consecutive empty-buffer reads, stop synthesising concealment and just
    // emit silence. Concealment is meant to mask brief transient gaps (a packet late by a few
    // ms); it should NOT fire forever when the sender has actually gone away. Without this
    // guard, killing the sender produced a "shshshsh" tremolo for ~4 s on the receiver — every
    // render callback wrote another noise burst into a buffer that never refilled, until the
    // AudioReceiver's idle-prune (4 s) tore the session down. 8 consecutive empties at typical
    // 5 ms ASIO render = 40 ms of repeated bursts before we give up; covers normal jitter
    // without bleeding into "sender gone" pauses.
    private const int ConcealmentMaxConsecutiveEmpties = 8;
    private bool inUnderrunConcealment;
    private int consecutiveEmptyReads;
    private float lastConcealSampleL;
    private float lastConcealSampleR;
    private volatile int concealmentArtifactRaw = (int)ConcealmentArtifact.NoiseBurst;
    // Per-peer pan + EQ, or null when this peer isn't shaped. Built on the UI thread and swapped in
    // atomically (volatile reference); the audio thread reads it once per block. See PeerDspChain.
    private volatile PeerDspChain? dsp;
    // Split-recording tap: when set, each block this session produces is handed to the recorder as this
    // one peer's audio — RAW (before pan/EQ) when recordRaw, else SHAPED (after). Set/cleared on the UI
    // thread; read once per block on the audio thread. recordScratch is a reusable copy so the tap never
    // allocates and never hands out the live output span.
    private volatile Action<IPEndPoint, ReadOnlyMemory<float>>? recordTap;
    private volatile bool recordRaw;
    private float[] recordScratch = new float[2048];
    // Per-session RNG for noise concealment. Seeded from process-level Shared so each session
    // gets a different sequence — but we don't care about reproducibility, just character.
    private readonly Random concealRng = new(Random.Shared.Next());

    // === Drift correction (Phase 4, 2026-05-14) — fixed-ratio resampler ===
    //
    // Replaces the Phase-2/3 discrete-splice drift corrector. Sender and receiver each have
    // their own free-running audio crystal and the two rates differ by a few-tens to a few-
    // hundreds of ppm. Without compensation the receive ring buffer slowly drifts up
    // (sender faster) or down (sender slower) and eventually clicks via either overflow or
    // underrun.
    //
    // Discrete single-frame drop / repeat splices — even with cosine crossfade and adaptive-
    // gain integrator scheduling — are audible at any drift rate above roughly 1 correction
    // per second on tonal content. Ed reported them as a continuous train of "tiny pops" all
    // through this session. The instrumentation added 2026-05-14 confirmed the corrector
    // was firing at 4-13 repeats/sec for his Audient EVO4 ↔ EVO8 setup (~150-200 ppm drift).
    //
    // The fixed-ratio resampler approach is what every serious networked-audio implementation
    // (Jamulus, SonoBus, Dante) uses for the same problem:
    //   1. Measure the sender's effective sample rate by comparing bytes written to the ring
    //      vs bytes output to the audio device over a long window (multi-second).
    //   2. Configure a WdlResampler with input rate = measured-sender-rate and output rate =
    //      MixSampleRate. The resampler continuously stretches or compresses the incoming
    //      stream by the necessary ppm.
    //   3. Update the resampler's input rate ONLY every DriftMeasurementWindowSec, smoothed
    //      heavily. The earlier doomed attempt (May) modulated rates per-sample based on
    //      instantaneous buffer level; that caused phase discontinuities and sample-level
    //      artefacts. Slow updates avoid that entirely.
    //
    // Pitch shift introduced by a fixed-ratio resampler at, say, 200 ppm is 0.02 % — far
    // below the ~5 % human pitch-discrimination threshold and even below tuning precision.
    // Genuinely inaudible.
    //
    // Safety net: the legacy click-trim block above stays in place and fires only at
    // catastrophic buffer levels (target + ~23 ms or 1 second worst-case cap). The discrete
    // splice corrector (drop / repeat with crossfade) is GONE — the resampler handles
    // steady-state drift smoothly. If the resampler somehow can't keep up (transient
    // catastrophe), the click-trim safety net fires once.
    //
    // The resampler. Output-driven (we want N output frames; ask the resampler how many
    // input frames it needs and feed those in from the ring buffer). interp=true with
    // filtercnt=0 selects WdlResampler's low-cost linear-interpolation mode — plenty good
    // enough for the ppm-scale rate corrections we apply. Higher filter modes would add
    // CPU cost for a sample quality difference well below audibility at these tiny ratios.
    // SetRates is called periodically from the audio thread (the only thread that touches
    // this resampler) so we don't need any cross-thread synchronisation around it.
    private readonly WdlResampler driftResampler;

    // Drift measurement counters. bytesWrittenForDriftEst is incremented by the producer
    // thread on every Write; bytesReadOutputForDriftEst is incremented by the consumer
    // thread (us) on every successful ReadFloats. Their ratio over a multi-second window
    // is the sender's effective rate divided by the receiver's nominal rate — exactly the
    // ratio the resampler needs.
    private long bytesWrittenForDriftEst;
    private long bytesReadOutputForDriftEst;

    // Window state. windowStartTicks = when the current measurement window started, ticks
    // 0 means "not yet armed for measurement". The values at window start are snapshotted
    // so we can compute the delta cleanly even if the counters wrap (long is 63-bit so wrap
    // is ~190 years at 48 kHz stereo float, but the math is still cleaner with snapshots).
    private long resamplerWindowStartTicks;
    private long resamplerWindowStartBytesWritten;
    private long resamplerWindowStartBytesOutput;

    // Smoothed ratio currently applied to the resampler (1.0 = no resampling). Smoothing
    // is "first measurement = the measurement; subsequent = 70 % previous + 30 % new" so
    // a one-window outlier doesn't yank the rate. Settles within a few windows to the
    // true drift.
    private double smoothedRateRatio = 1.0;
    private bool resamplerActivelyTracking;
    // Fast-approach state (see the FastDepthBias block). Set from the UI thread via
    // RequestFastLatencyApproach, cleared on the audio thread once the ring reaches the new target.
    private volatile bool fastApproachUntilOnTarget;
    private long lastFastApplyTicks;
    private long resamplerUpdatesTotal;

    // Scratch buffer for reading from the ring buffer in float form. Sized lazily based on
    // the largest input-frames request the resampler asks for; persists across calls so
    // we don't realloc on the hot path.
    private float[] resamplerInputScratch = new float[2048];

    // driftDropFramesTotal + driftRepeatFramesTotal fields removed 2026-05-23. They were
    // Phase-2/3 splice-corrector counters that the Phase-4 fixed-ratio resampler design
    // never incremented; they sat at zero and fed dead diag-log columns that have also been
    // removed. The current corrector's "where is the buffer" signal is filteredErrorFrames
    // (below) — that one IS still active and IS still surfaced via FilteredDriftErrorFrames.
    // Live state for the diag log — the current buffer-level offset from target, low-pass
    // filtered. Lets the diag line continue to surface "where the buffer is sitting".
    // Updated each Read; no longer drives any correction logic itself.
    private double filteredErrorFrames;
    private long prevDriftSampleTicks;
    // Integrator gain. Lowered 2026-05-06 (10×) after an empirical test where the previous
    // gain (0.05) produced ~10 corrections per second on the user's hardware (two free-running
    // Drift-measurement window for the fixed-ratio resampler. After this many seconds of
    // sustained streaming, we compute (bytes_written / bytes_output) over the window and
    // smooth-update the resampler's input rate. Long enough that brief network jitter or
    // GC pauses don't bias the measurement; short enough to track temperature-induced
    // crystal-rate changes (USB audio clocks can drift several ppm over minutes as the
    // device warms up). 10 sec is the canonical Jamulus / SonoBus value.
    private const double DriftMeasurementWindowSec = 10.0;
    // First-window length. Same as DriftMeasurementWindowSec for simplicity; could be
    // shortened to engage compensation faster after session start at the cost of a noisier
    // initial measurement.
    private const double DriftFirstWindowSec = 10.0;
    // Ratio smoothing weight. New measurement = 30 %; previous smoothed = 70 %. Tunes how
    // quickly the rate tracks vs how stable it is. The first measurement after session
    // start uses 100 % new (no previous value to weight).
    private const double DriftRatioSmoothingNew = 0.30;
    // Sanity-range clamp on the measured ratio. Real clock differences between USB audio
    // crystals are sub-1000 ppm (0.1 %); anything beyond ±5 % indicates a measurement
    // artefact (a buffer-fill burst, a transient, or a counter wrap). Reject those and
    // keep the previous ratio.
    private const double DriftRatioMin = 0.95;
    private const double DriftRatioMax = 1.05;
    // Low-pass filter time constant for the buffer-level-error display in the diag log.
    // Doesn't affect any correction logic in Phase 4 — purely informational.
    private const double DriftFilterTimeConstantSec = 2.0;
    // === Depth feedback (2026-06-12) — drain a standing buffer back to target ===
    // The clock-ratio resampler above is a pure rate-MATCHER: it equalises long-run sender-write
    // and receiver-output rates, which holds the ring at whatever depth it currently sits. On the
    // WASAPI render path (a Stopwatch-paced producer loop drains the ring, not a hardware clock) a
    // stall-then-burst can leave the ring standing ~100 ms deep and the rate-matcher then freezes
    // it there for the whole session — receive latency ratchets up overnight and only ever resets
    // on a reconnect or a glitchy click-trim. Andre's 2026-06-11 overnight WASAPI log showed
    // exactly this sawtooth. The per-device WASAPI corrector (MultiOutputPlayout.DriftResampling-
    // Provider) already carries this depth term; this ports the same term onto the per-sender ring.
    // depthError > 0 (too deep) biases the rate UP so the resampler pulls more input per output and
    // drains faster; clamped to ±MaxDepthBias and spread over DepthCorrectionSec → a sub-audible
    // ≤0.3 % pitch nudge, never a click.
    private const double DepthCorrectionSec = 15.0;
    private const double MaxDepthBias = 0.003;
    // === Fast approach after a DELIBERATE latency change (2026-08-14) ===
    // The steady-state numbers above are deliberately sleepy: 0.3% of realtime is ~3 ms of catch-up
    // per second, and the correction is only recomputed at the 10 s drift-window boundary. That's
    // right for silently absorbing clock drift, and hopeless for a user who just dragged the slider
    // 400 ms and wants to HEAR the delay change — it would take over two minutes to arrive, which
    // reads as "the slider does nothing" even once the slider's value reaches the right place.
    // So a user-initiated change (the hard setter — never the auto-tune's soft one, which keeps the
    // sleepy numbers and its parked descent behaviour) engages a fast approach: a bigger rate bias,
    // recomputed several times a second, until the ring is near the new target. The audio slows or
    // speeds by up to FastDepthBias while it converges — an audible, deliberate glide (that's the
    // point: the user asked for the change and can hear it happen), never a gap or a click.
    private const double FastDepthCorrectionSec = 3.0;
    private const double FastDepthBias = 0.05;          // 5% => ~50 ms of catch-up per second
    private const double FastApplyIntervalSec = 0.2;    // recompute 5x/sec while converging
    private const double FastApproachDoneMs = 15.0;     // close enough — hand back to steady state
    // Number of stereo frames each side of a splice point that get blended when a drop or
    // repeat fires. Cosine crossfade over this window smooths the discontinuity into an audio
    // DriftDropFramesTotal / DriftRepeatFramesTotal accessors removed 2026-05-23 alongside
    // their backing fields — they only ever surfaced two always-zero columns in the diag log,
    // and the columns have been removed too.
    /// <summary>Diagnostic accessor — current smoothed sender-rate-ratio applied to the
    /// resampler. 1.0 = no resampling (matched clocks). Values like 1.0002 = sender running
    /// 200 ppm faster than receiver; 0.9998 = 200 ppm slower.</summary>
    public double DriftResamplerRatio => smoothedRateRatio;
    /// <summary>Number of times the resampler rate has been updated since session start.</summary>
    public long DriftResamplerUpdates => Interlocked.Read(ref resamplerUpdatesTotal);

    // Per-stage discontinuity probes — the receiver-side instrumentation that, combined
    // with the sender's pre-encode probe and the StreamSession's post-decode probe,
    // localises exactly where in the pipeline a click is introduced. PostRingRead is
    // what came out of the ring buffer (after wire+decode+ring). PostResampler is what
    // came out of the resampler (after rate compensation).
    private readonly AudioStepProbe postRingReadStepProbe = new();
    private readonly AudioStepProbe postResamplerStepProbe = new();
    public float TakeMaxPostRingReadStep() => postRingReadStepProbe.TakeMax();
    public float TakeMaxPostResamplerStep() => postResamplerStepProbe.TakeMax();
    public float TakeMaxPostRingReadStepCrossBuffer() => postRingReadStepProbe.TakeMaxCrossBuffer();
    public float TakeMaxPostRingReadStepWithinBuffer() => postRingReadStepProbe.TakeMaxWithinBuffer();
    public float TakeMaxPostResamplerStepCrossBuffer() => postResamplerStepProbe.TakeMaxCrossBuffer();
    public float TakeMaxPostResamplerStepWithinBuffer() => postResamplerStepProbe.TakeMaxWithinBuffer();

    // Concealment vs partial-read counters split from the legacy "Underruns" — that one
    // increments on ANY short read at the AudioRingBuffer level (whether framesRead==0
    // or framesRead<requested). For diagnosis we care about the split: full-empty reads
    // (concealmentFiresTotal) are audible events that trigger noise-burst + fade-in;
    // sub-frame partial reads (partialReadFiresTotal) used to be audible too but are
    // now silent after the 2026-05-14 fix that stops concealment from engaging on partials.
    private long concealmentFiresTotal;
    private long partialReadFiresTotal;
    public long ConcealmentFiresTotal => Interlocked.Read(ref concealmentFiresTotal);
    public long PartialReadFiresTotal => Interlocked.Read(ref partialReadFiresTotal);

    // Cause-split of ring short-reads, for the continuous auto-tune (2026-06-13). The legacy
    // UnderrunCount can't tell apart two very different events that both come up short on a read:
    //   * PRODUCER starvation — the ring is draining below target because decoded audio isn't
    //     arriving fast enough (late/lost packets, or a stalled receive/decode thread). MORE
    //     buffer genuinely helps; this is the "raise the target / don't lower" case.
    //   * DEVICE gulp — the output render callback came late/chunky and asked for an oversized
    //     block in one go (classic onboard-Realtek WASAPI ~21ms period stretching toward ~42ms),
    //     so a perfectly healthy on-target ring still can't fill that one gulp. These are partial
    //     short-reads that are INAUDIBLE (zero-padded sub-frame tail since the 2026-05-14 fix), and
    //     more buffer never cures the gulp pattern — it just adds permanent latency. This is the
    //     case that used to pin the auto-tune high forever (it skipped lowering on ANY underrun).
    // We tell them apart by where the buffer is sitting at the instant of the short-read: a
    // chronically-below-target buffer (filteredErrorFrames well negative) is producer starvation;
    // an on-target buffer that momentarily came up short is a device gulp. The split is computed
    // live on the render thread, independent of file logging, so it drives the live tuner even
    // with logs off. The cumulative totals are also surfaced for the diag log so the classification
    // can be validated against real captures.
    private long tuneBlockingUnderrunsTotal;
    private long deviceGulpUnderrunsTotal;
    /// <summary>Short-reads the auto-tune should treat as "need more buffer" — full-empty reads
    /// (audible) plus any short-read taken while the ring was chronically below target (producer
    /// starvation). These block the auto-tune from lowering, exactly as the legacy total used to.</summary>
    public long TuneBlockingUnderrunsTotal => Interlocked.Read(ref tuneBlockingUnderrunsTotal);
    /// <summary>Inaudible partial short-reads taken while the ring was on target — the device-gulp
    /// case more buffer can't fix. These must NOT block the auto-tune from lowering.</summary>
    public long DeviceGulpUnderrunsTotal => Interlocked.Read(ref deviceGulpUnderrunsTotal);
    /// <summary>Live state — the LP-filtered drift error in stereo frames. Positive = buffer
    /// running above target on average (sender clock faster); negative = buffer below
    /// target. Magnitude shows how off-target the buffer's average position is right now.</summary>
    public double FilteredDriftErrorFrames => filteredErrorFrames;
    // DriftAccumulator accessor removed 2026-05-23. The Phase-4 fixed-ratio resampler design
    // never sets an integrator accumulator value; the property always returned 0. Removed
    // along with the driftAcc= diag column.

    public IPEndPoint Endpoint { get; }
    /// <summary>The stream ID this session was opened for. Sessions are keyed by
    /// (Endpoint, StreamId) so a single peer can produce multiple simultaneous streams
    /// (e.g. WASAPI lane + ASIO lane in the native-independent mode). For single-lane
    /// modes there's still one session per peer with whatever streamId the sender chose
    /// (currently 1).</summary>
    public ushort StreamId { get; }
    /// <summary>Which render route this session's audio belongs to. Set by AudioReceiver
    /// from the format packet's Lane byte at session-creation (and updated on the rare
    /// in-place format change that keeps the same SessionPlayout alive). PlayoutEngine
    /// uses this to decide which of its per-route IWaveProvider surfaces this session
    /// contributes to. Defaults to <see cref="RenderRoute.Mixed"/> — the value an old
    /// sender or a classic-mode (WasapiOnly / AsioOnly / Both) sender writes.</summary>
    public RenderRoute Route { get; set; } = RenderRoute.Mixed;
    /// <summary>Ring capacity this session was sized to (bytes) — so a mirror replica for another
    /// output lane can be created with the same capacity.</summary>
    public int Capacity { get; }
    public int BufferedBytes => playout.BufferedBytes;
    public int BufferedMs => playout.BufferedBytes / MixBytesPerFrame * 1000 / MixSampleRate;
    public long UnderrunCount => playout.UnderrunCount;
    public long DropCount => playout.DropCount;
    public bool IsArmed => playbackArmed;

    /// <summary>Per-cause drop accessors (cumulative bytes / counts since session start).
    /// Splits the previously-opaque DropCount so the diag log can distinguish click-trim
    /// from drain-on-knob-change from ringbuffer overflow. AggregateDrops on the engine
    /// continues to expose the rolled-up total for back-compat.</summary>
    public long TrimDropBytes => Interlocked.Read(ref trimDropBytes);
    public long DrainDropBytes => Interlocked.Read(ref drainDropBytes);
    public long TrimFireCount => Interlocked.Read(ref trimFireCount);

    /// <summary>Sets the concealment artifact this session's playout uses on underrun gaps.
    /// Takes effect on the very next gap; no need to restart playback. Receiver-side only —
    /// the sender doesn't see this and wouldn't behave differently if it did.</summary>
    public void SetConcealmentArtifact(ConcealmentArtifact value) =>
        concealmentArtifactRaw = (int)value;

    /// <summary>Sets (or clears with null) this session's per-peer pan+EQ chain. Takes effect on the
    /// next block. Called from the UI thread; the audio thread reads the reference lock-free.</summary>
    public void SetDsp(PeerDspChain? value) => dsp = value;

    /// <summary>Sets (or clears with null) this session's split-recording tap. <paramref name="raw"/> =
    /// deliver the block before pan/EQ (bypass); otherwise after. Takes effect on the next block.</summary>
    public void SetRecordTap(Action<IPEndPoint, ReadOnlyMemory<float>>? tap, bool raw)
    {
        recordRaw = raw;
        recordTap = tap;
    }

    private void EmitRecordTap(Action<IPEndPoint, ReadOnlyMemory<float>> tap, Span<float> block, int frames)
    {
        int n = frames * 2;
        if (recordScratch.Length < n) recordScratch = new float[n];
        block[..n].CopyTo(recordScratch);
        tap(Endpoint, recordScratch.AsMemory(0, n));
    }

    /// <summary>UTC time of the most recent successful audio write. Used by <see cref="AudioReceiver"/>
    /// to prune long-idle sessions so the dictionary doesn't grow unboundedly.</summary>
    public DateTime LastWriteUtc { get; private set; } = DateTime.UtcNow;

    public SessionPlayout(IPEndPoint endpoint, ushort streamId, int capacityBytes)
    {
        Endpoint = endpoint;
        StreamId = streamId;
        Capacity = capacityBytes;
        playout = new AudioRingBuffer(capacityBytes);

        // Resampler init. interp=true, filtercnt=0 picks WdlResampler's linear-interpolation
        // mode — perfectly adequate for the sub-1000-ppm rate corrections we apply (the
        // higher-cost sinc modes would buy theoretical quality wins below human audibility).
        // sinc=false confirms we're not using the sinc-table mode. Output-driven feed: each
        // ResamplePrepare call asks the resampler "how many input frames do you need for N
        // output frames" and we satisfy from the ring buffer. SetRates(in, out) starts at
        // 1:1; we update with measured drift after the first window completes.
        driftResampler = new WdlResampler();
        driftResampler.SetMode(interp: true, filtercnt: 0, sinc: false);
        driftResampler.SetFeedMode(false);
        driftResampler.SetRates(MixSampleRate, MixSampleRate);
    }

    // Mirror replicas. In BothIndependent mode the SAME decoded audio is fanned out to one extra
    // SessionPlayout per additional output lane, so every output device plays every stream. Each
    // mirror is a FULL, independent SessionPlayout — its own ring, drift resampler, arming and
    // concealment — so each output keeps its own clock and latency with no shared cache and no extra
    // latency (identical to a single-output setup, by construction). Only the decode-side fan-out lives
    // here; ReadFloats (the tuned drift/trim/conceal math) is untouched and each replica runs it alone.
    // Replaced wholesale (reference swap) by the UI thread; the network/audio threads only read it.
    private volatile SessionPlayout[] mirrors = [];
    public void SetMirrors(SessionPlayout[] value) => mirrors = value;

    public void Write(ReadOnlySpan<byte> source)
    {
        WriteLocal(source);
        var mir = mirrors;
        for (var i = 0; i < mir.Length; i++) mir[i].WriteLocal(source);
    }

    /// <summary>The single-instance write body — used directly for a mirror replica (so a mirror never
    /// re-fans-out) and by <see cref="Write"/> for the primary before it forwards to its mirrors.</summary>
    private void WriteLocal(ReadOnlySpan<byte> source)
    {
        var ms = source.Length * 1000 / MixBytesPerSecond;
        if (ms > largestWriteMs) largestWriteMs = ms;
        playout.Write(source);
        // Track bytes written for the drift-resampler measurement window. Producer thread
        // updates this; consumer thread (audio thread in ReadFloats) reads it via
        // Interlocked.Read when sampling the window. Cumulative since session start.
        Interlocked.Add(ref bytesWrittenForDriftEst, source.Length);
        LastWriteUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Network-thread callback after a frame has been queued. Arms playback the moment this
    /// session's buffer first reaches the user's target; subsequent reads then engage the
    /// drift corrector. Each session arms independently, so a newly-arrived sender can start
    /// playing without waiting for already-armed sessions.
    ///
    /// Also enforces a CATASTROPHIC-only cap on buffer level: if audio piles up beyond 1 second
    /// (because the render thread hasn't started yet, or got stuck), we trim down to 250 ms.
    /// The threshold is intentionally far above any reasonable jitter cushion — earlier we used
    /// 3× target which fought with the drift corrector on a noisy WAN (target 10 ms, real
    /// jitter up to 76 ms ⇒ buffer was being trimmed every second, causing the very clicking
    /// it was supposed to avoid). Now the cap is purely a safety net against catastrophic
    /// backlogs (multi-second pile-ups while no consumer exists); ordinary jitter is absorbed
    /// by the buffer + drift corrector + click-trim combo.
    /// </summary>
    public void NoteFramesQueued(int targetLatencyMs)
    {
        NoteFramesQueuedLocal(targetLatencyMs);
        // Each mirror arms independently on its own ring (same bytes arrive at the same rate, so they
        // arm at ~the same moment). Forwarded here rather than called separately so the decode side
        // only ever touches the primary.
        var mir = mirrors;
        for (var i = 0; i < mir.Length; i++) mir[i].NoteFramesQueuedLocal(targetLatencyMs);
    }

    private void NoteFramesQueuedLocal(int targetLatencyMs)
    {
        const int CatastrophicCapMs = 1000;
        const int CatastrophicTrimToMs = 250;
        if (playout.BufferedBytes > MillisecondsToBytes(CatastrophicCapMs))
        {
            playout.TrimFromProducer(MillisecondsToBytes(CatastrophicTrimToMs));
        }

        if (playbackArmed) return;
        if (playout.BufferedBytes >= MillisecondsToBytes(Math.Max(targetLatencyMs, 1)))
        {
            playbackArmed = true;
        }
    }

    /// <summary>Disarm and request a drain on the next read — used when the user raises or
    /// lowers the latency knob. The mix bus continues with whatever's already armed.</summary>
    public void DisarmAndRequestDrain()
    {
        playbackArmed = false;
        drainRequested = true;
    }

    /// <summary>The user just moved the latency slider: converge on the new depth in seconds rather
    /// than minutes (see the FastDepthBias block). Deliberate changes only — the auto-tune's soft
    /// setter must NOT call this, so its gentle, parked descent behaviour is untouched.</summary>
    public void RequestFastLatencyApproach()
    {
        fastApproachUntilOnTarget = true;
        var mir = mirrors;
        for (var i = 0; i < mir.Length; i++) mir[i].fastApproachUntilOnTarget = true;
    }

    public void Dispose()
    {
        // AudioRingBuffer is managed; nothing to free explicitly. Method present for symmetry
        // with Stream/Capture sessions and to allow future per-session unmanaged state.
    }

    /// <summary>
    /// WASAPI/ASIO render thread. Pulls <paramref name="outFrames"/> stereo frames from this
    /// session's playout ring into <paramref name="output"/>, applying drift correction and
    /// underrun concealment along the way. Returns the count of frames actually produced; if
    /// the session is disarmed (or drained completely) the return is 0 and
    /// <paramref name="output"/> is untouched (caller is responsible for zero-fill).
    /// </summary>
    public int ReadFloats(Span<float> output, int outFrames, int targetLatencyMs, int currentMaxLatencyMs, int smoothness = 3)
    {
        // Drain on user knob change.
        if (drainRequested)
        {
            drainRequested = false;
            var targetBytes = MillisecondsToBytes(targetLatencyMs);
            var buffered = playout.BufferedBytes;
            if (buffered > targetBytes)
            {
                var bytes = buffered - targetBytes;
                playout.DropOldest(bytes);
                Interlocked.Add(ref drainDropBytes, bytes);
            }
        }

        if (!playbackArmed)
        {
            return 0;
        }

        // NOTE: there used to be an "auto-disarm if buffer empty" block here. It was added
        // 2026-04-30 to clean up phantom underrun counts after a peer disconnect (the
        // 4-second idle-prune fires later, so without auto-disarm the underrun counter would
        // climb at ~100/sec while we waited). The comment claimed "mix output unchanged
        // either way" — but that was wrong at tight target latency.
        //
        // What auto-disarm did wrong: any time the buffer dipped to zero even momentarily
        // (ordinary sender-side mix-tick jitter — a 16ms gap between packets is normal on
        // Windows), it would disarm the session and return 0. ReadFloats then output silence
        // until packets refilled the buffer ALL THE WAY BACK to the user's target latency
        // and NoteFramesQueued re-armed. Each transient underrun became a 10-15ms silence
        // gap instead of a few-ms pad. That's the audible click that Ed kept hearing on
        // localhost at target=10 vs the older build that ran clean.
        //
        // Now: a momentary empty buffer just produces a small silence-pad on this Read (the
        // underrun counter still increments via playout.ReadFloats's own silence-fill — fine,
        // that's diagnostic noise not audio noise). The 4-second idle-prune in AudioReceiver
        // still handles long-term disconnect by tearing the session down entirely. No auto-
        // disarm needed.

        // === Click-based buffer-smoothness trim ===
        //
        // The Buffer-smoothness knob (1 = aggressive, 10 = smooth) controls how aggressively
        // we DROP oldest samples when the buffer drifts above target. When (bufferedMs >
        // target + trimMargin) we drop the excess down to target. Causes a brief click at the
        // drop point but holds the queue right at the user's chosen latency.
        //
        // Largely a safety net post-Phase 2: the drift corrector below keeps the buffer near
        // target in normal operation, so the trim only fires under catastrophic conditions
        // (large step changes the slow integrator can't keep up with). Replaced an earlier
        // resampler-rate controller that pitch-shifted music while correcting drift — clicks
        // turned out to be the lesser evil, and the trim itself is a direct DropOldest on the
        // ring buffer (no resampler involved), so it's guaranteed to fire when needed.
        //
        // Margin and drop-destination computation. SPLIT BY KNOB:
        //
        // smoothness == 1 ("stupid aggressive" — used in ASIO Tight Latency mode for
        // sub-10 ms target):
        //   floor       = largestWriteMs * 2 + 4 (min 4)
        //   drop-to     = target + largestWriteMs (one packet's cushion only)
        //   This is the original "reconnect-feel" tightness — tight threshold,
        //   tight drop, frequent clicks but glued to target. Don't touch this. It's
        //   what makes ASIO at target=1 ms snap back to target after every burst.
        //
        // smoothness >= 2:
        //   floor       = largestWriteMs * 4 + 4 (min 15)
        //   drop-to     = target + largestWriteMs * 2 + 5 (covers render-period +
        //                 frame + jitter pad)
        //   The looser values prevent default-smoothness-3 from tripping on routine
        //   startup bursts (96 kHz device + Opus 5 ms saw a 40 ms initial buffer that
        //   exceeded the old default threshold of 32 ms — the rate controller would
        //   have handled it in a few seconds, but trim fired and dropped the buffer
        //   too low to absorb normal sender jitter, causing 20 underruns/sec for the
        //   rest of the session).
        //
        // Direct evidence: localhost 96 kHz Opus test 2026-05-02 06:37 with
        // smoothness=3 — drops=9600 from one startup trim, then 20 underruns/sec.
        // Subsequent 48 kHz test with same smoothness ran clean because trim never
        // fired (buffer never quite reached the 32 ms threshold). The looser default
        // floor lets the rate controller handle initial transients on either device
        // rate.
        //
        // Examples at target=10 ms:
        //   smoothness=1:
        //     PCM Tight 2.5: floor=8.   drop to 12.5. trims at >18.
        //     Opus 10:       floor=24.  drop to 20.   trims at >34.
        //   smoothness=3 (default):
        //     PCM Tight 2.5: floor=15.  drop to 19.   trims at >33.
        //     PCM 5:         floor=24.  drop to 25.   trims at >42.
        //     Opus 10:       floor=44.  drop to 35.   trims at >62.
        var clampedKnob = Math.Clamp(smoothness, 1, 10);
        var aggressive = clampedKnob == 1;
        var floorMarginMs = aggressive
            ? Math.Max(largestWriteMs * 2 + 4, 4)
            : Math.Max(largestWriteMs * 4 + 4, 15);
        var dropToCushionMs = aggressive
            ? largestWriteMs
            : largestWriteMs * 2 + 5;
        var knobExtraMs = clampedKnob switch
        {
            1 => 0,
            2 => 3,
            3 => 8,
            4 => 16,
            5 => 28,
            6 => 45,
            7 => 70,
            8 => 110,
            9 => 200,
            _ => -1, // 10 = no trim
        };
        if (knobExtraMs >= 0)
        {
            // Ease the trim's reference latency: snap UP to the target instantly (raising never
            // trims), glide DOWN at ~1.5 ms/sec - slower than the depth corrector can drain the
            // buffer - so a lowered target (the auto-tune walking latency down) lets the buffer
            // slide down under the threshold rather than being shaved into a click on each step.
            const double TrimGlideDownMsPerSec = 1.5;
            var nowTrimTicks = Stopwatch.GetTimestamp();
            if (trimGlideTargetMs <= 0 || targetLatencyMs >= trimGlideTargetMs)
            {
                trimGlideTargetMs = targetLatencyMs;
            }
            else if (prevTrimGlideTicks != 0)
            {
                var dtSec = (nowTrimTicks - prevTrimGlideTicks) / (double)Stopwatch.Frequency;
                trimGlideTargetMs = Math.Max(targetLatencyMs, trimGlideTargetMs - TrimGlideDownMsPerSec * dtSec);
            }
            prevTrimGlideTicks = nowTrimTicks;

            var trimMarginMs = floorMarginMs + knobExtraMs;
            // Threshold uses the gliding ceiling so a freshly-lowered target doesn't trip the trim.
            // The drop destination still uses the REAL target, so if genuine bloat does push the
            // buffer above the gliding ceiling, it's cut back to where the latency actually wants it.
            var trimThresholdBytes = MillisecondsToBytes((int)Math.Round(trimGlideTargetMs) + trimMarginMs);
            if (playout.BufferedBytes > trimThresholdBytes)
            {
                var keepBytes = MillisecondsToBytes(Math.Max(targetLatencyMs + dropToCushionMs, 1));
                var dropBytes = playout.BufferedBytes - keepBytes;
                if (dropBytes > 0)
                {
                    playout.DropOldest(dropBytes);
                    Interlocked.Add(ref trimDropBytes, dropBytes);
                    Interlocked.Increment(ref trimFireCount);
                }
            }
        }

        // === Drift compensation (Phase 4) — fixed-ratio resampler ===
        //
        // 1. Update the LP-filtered buffer-level error for the diag log (informational only).
        // 2. Update the resampler's rate ratio if the measurement window has elapsed.
        // 3. Read through the resampler into the caller's output span.
        //
        // The buffer-level error LP filter no longer drives any correction — that job is
        // now the resampler's. It's kept purely as a diag display so the log shows where
        // the buffer is sitting.
        var nowTicks = Stopwatch.GetTimestamp();
        var driftTargetBytes = MillisecondsToBytes(targetLatencyMs);
        if (prevDriftSampleTicks != 0)
        {
            var dtSec = (nowTicks - prevDriftSampleTicks) / (double)Stopwatch.Frequency;
            var errorFrames = ((double)playout.BufferedBytes - driftTargetBytes) / MixBytesPerFrame;
            var filterAlpha = dtSec / (DriftFilterTimeConstantSec + dtSec);
            filteredErrorFrames = (1.0 - filterAlpha) * filteredErrorFrames + filterAlpha * errorFrames;
        }
        prevDriftSampleTicks = nowTicks;

        UpdateDriftResamplerRateIfDue(nowTicks, targetLatencyMs);
        // While converging on a freshly-moved slider, re-apply the (much larger) depth correction
        // several times a second instead of waiting for the 10 s drift window — otherwise the first
        // seconds after the user's move do nothing at all, which is exactly what "the slider doesn't
        // work" felt like. Self-clearing once the ring is within FastApproachDoneMs of target.
        if (fastApproachUntilOnTarget && resamplerActivelyTracking
            && (nowTicks - lastFastApplyTicks) / (double)Stopwatch.Frequency >= FastApplyIntervalSec)
        {
            lastFastApplyTicks = nowTicks;
            ApplyDepthCorrectedRate(targetLatencyMs);
        }

        // Read through the resampler and apply concealment on full underruns.
        ReadThroughResampler(output, outFrames);
        // Split-recording tap, RAW variant — grab this peer's block BEFORE pan/EQ (bypass recording).
        var tap = recordTap;
        if (tap is not null && recordRaw) EmitRecordTap(tap, output, outFrames);
        // Per-peer pan + EQ: this peer's block is fully decoded and isolated here, immediately before
        // PlayoutEngine sums it into the mix — so shaping is per-peer and pre-mix, and being per-sample
        // it adds no buffering latency. Null (unshaped peer) skips the whole stage.
        var d = dsp;
        d?.Process(output, outFrames);
        // Split-recording tap, SHAPED variant — after pan/EQ (the default, "record what you hear").
        if (tap is not null && !recordRaw) EmitRecordTap(tap, output, outFrames);
        return outFrames;
    }

    /// <summary>Set the resampler rate to the feed-forward clock ratio plus the depth-feedback bias
    /// that walks the ring toward <paramref name="targetLatencyMs"/>. Shared by the steady-state
    /// 10 s window and the fast approach after a deliberate slider move — the ONLY difference is how
    /// hard it's allowed to pull (see the FastDepthBias block), so both paths stay one piece of
    /// arithmetic rather than two that can drift apart.</summary>
    private void ApplyDepthCorrectedRate(int targetLatencyMs)
    {
        var depthFrames = playout.BufferedBytes / MixBytesPerFrame;
        var targetFrames = targetLatencyMs * MixSampleRate / 1000;
        var depthError = depthFrames - targetFrames;
        var fast = fastApproachUntilOnTarget;
        if (fast && Math.Abs(depthError) <= FastApproachDoneMs * MixSampleRate / 1000)
        {
            fastApproachUntilOnTarget = false; // arrived — back to the sleepy steady-state numbers
            fast = false;
        }
        var spreadSec = fast ? FastDepthCorrectionSec : DepthCorrectionSec;
        var bias = fast ? FastDepthBias : MaxDepthBias;
        var depthCorrection = Math.Clamp(depthError / (spreadSec * MixSampleRate), -bias, bias);
        var appliedRatio = smoothedRateRatio + depthCorrection;
        driftResampler.SetRates(MixSampleRate * appliedRatio, MixSampleRate);
        Interlocked.Increment(ref resamplerUpdatesTotal);
    }

    /// <summary>
    /// If the current drift-measurement window has expired, compute the new sender-to-
    /// receiver rate ratio from the bytes-written and bytes-output counters, smooth it
    /// into the live ratio, and push it to the resampler. Called from the audio thread
    /// on every ReadFloats. No-op if the window hasn't elapsed yet.
    /// </summary>
    private void UpdateDriftResamplerRateIfDue(long nowTicks, int targetLatencyMs)
    {
        if (resamplerWindowStartTicks == 0)
        {
            // First call — anchor the measurement window. Defer the first rate update by
            // DriftFirstWindowSec so we get a stable initial measurement rather than one
            // based on the first few writes (which can be bursty during session arming).
            resamplerWindowStartTicks = nowTicks;
            resamplerWindowStartBytesWritten = Interlocked.Read(ref bytesWrittenForDriftEst);
            resamplerWindowStartBytesOutput = bytesReadOutputForDriftEst;
            return;
        }

        var windowDuration = resamplerActivelyTracking ? DriftMeasurementWindowSec : DriftFirstWindowSec;
        var elapsedSec = (nowTicks - resamplerWindowStartTicks) / (double)Stopwatch.Frequency;
        if (elapsedSec < windowDuration) return;

        var bytesWrittenNow = Interlocked.Read(ref bytesWrittenForDriftEst);
        var bytesWrittenInWindow = bytesWrittenNow - resamplerWindowStartBytesWritten;
        var bytesOutputInWindow = bytesReadOutputForDriftEst - resamplerWindowStartBytesOutput;

        if (bytesOutputInWindow > 0 && bytesWrittenInWindow > 0)
        {
            // Feed-forward: ratio = bytes_sender_produced / bytes_receiver_consumed over the
            // window. Above 1.0 = sender clock faster than receiver; below = slower. This is the
            // true crystal difference, independent of the resampler rate we apply, so it cleanly
            // cancels steady-state drift and the depth feedback below doesn't have to fight it.
            // For Ed's hardware (sender slower than receiver) this should settle ~0.9998.
            var measuredRatio = (double)bytesWrittenInWindow / bytesOutputInWindow;
            if (measuredRatio >= DriftRatioMin && measuredRatio <= DriftRatioMax)
            {
                if (!resamplerActivelyTracking)
                {
                    // First measurement — use directly. No previous value to weight.
                    smoothedRateRatio = measuredRatio;
                    resamplerActivelyTracking = true;
                }
                else
                {
                    // Subsequent — smooth so a one-window outlier doesn't yank the rate.
                    smoothedRateRatio = (1.0 - DriftRatioSmoothingNew) * smoothedRateRatio + DriftRatioSmoothingNew * measuredRatio;
                }
            }
            // If the measured ratio is outside the sanity window (>5 % off), reject it.
            // That happens transiently during session arming, slider raises, or sender
            // start-of-stream bursts. Keep the previous ratio rather than yanking.
        }

        // Feedback: nudge the ring back toward the user's target depth. Without this the
        // resampler is a pure rate-matcher and a standing (bloated) buffer never drains — see the
        // DepthCorrectionSec / MaxDepthBias note above. depthError > 0 (too deep) biases the
        // applied rate UP so the resampler pulls more input per output and drains the buffer
        // faster; < 0 biases down to refill. Clamped + spread over DepthCorrectionSec so it's a
        // gentle, inaudible pitch trim, not a per-sample discontinuity. No-op until the
        // feed-forward has a valid measurement (smoothedRateRatio is meaningless before then).
        if (resamplerActivelyTracking) ApplyDepthCorrectedRate(targetLatencyMs);

        // Anchor the next window.
        resamplerWindowStartTicks = nowTicks;
        resamplerWindowStartBytesWritten = bytesWrittenNow;
        resamplerWindowStartBytesOutput = bytesReadOutputForDriftEst;
    }

    /// <summary>
    /// Resampler-backed read. Asks the resampler how many input frames it needs to
    /// produce <paramref name="outFrames"/> output frames at the current rate ratio,
    /// reads that many from the playout ring, runs ResampleOut, and copies the result
    /// into <paramref name="output"/> with a safety clamp to [-1, 1]. Handles full-empty
    /// underruns with the existing concealment fade-out / fade-in machinery.
    /// </summary>
    private void ReadThroughResampler(Span<float> output, int outFrames)
    {
        var outFloats = outFrames * MixChannels;
        var inputFramesNeeded = driftResampler.ResamplePrepare(outFrames, MixChannels, out var inBuf, out var inBufOff);
        lastInputFramesAvailable = inputFramesNeeded;
        if (inputFramesNeeded <= 0)
        {
            // Resampler doesn't need any input this call (its internal filter delay line
            // has enough). Just produce output from buffered state.
            ResampleOutAndCopy(output, outFrames);
            bytesReadOutputForDriftEst += outFloats * sizeof(float);
            return;
        }

        var inputFloatsNeeded = inputFramesNeeded * MixChannels;

        // Grow our scratch buffer if a larger request than ever before. After the first few
        // reads at session start, this stops being a fresh allocation.
        if (resamplerInputScratch.Length < inputFloatsNeeded)
        {
            resamplerInputScratch = new float[inputFloatsNeeded];
        }
        var ringBytes = MemoryMarshal.AsBytes(resamplerInputScratch.AsSpan(0, inputFloatsNeeded));
        var bytesGot = playout.Read(ringBytes);
        var floatsGot = bytesGot / sizeof(float);
        var framesGot = floatsGot / MixChannels;

        // Pipeline-stage probe — scan what we got out of the ring buffer BEFORE the
        // resampler touches it. If this shows large steps, the artefact is being
        // introduced somewhere between the sender and here (wire, decode, ring buffer).
        // If this is clean but the post-resampler probe shows large steps, the resampler
        // is the source.
        if (floatsGot > 0)
        {
            postRingReadStepProbe.ScanStereo(resamplerInputScratch.AsSpan(0, floatsGot));
        }

        // Copy whatever we got into the resampler's input buffer. AudioRingBuffer.Read
        // already zero-fills the tail of a short read, but we copy via the float view so
        // the resampler sees consistent float samples regardless of read shortfall.
        resamplerInputScratch.AsSpan(0, floatsGot).CopyTo(inBuf.AsSpan(inBufOff));
        if (floatsGot < inputFloatsNeeded)
        {
            inBuf.AsSpan(inBufOff + floatsGot, inputFloatsNeeded - floatsGot).Clear();
        }

        // Diagnostic split — distinguish full-empty reads from partial short reads. Only
        // full-empty (framesGot == 0) triggers audible concealment treatment. Partial
        // reads happen when the ring has fewer than inputFramesNeeded frames but more
        // than zero; the zero-padded tail just produces silence at the resampler output
        // for that fraction.
        var artifact = (ConcealmentArtifact)concealmentArtifactRaw;
        // Cause-split for the continuous auto-tune (see tuneBlockingUnderrunsTotal declaration).
        // Any short-read is classified the instant it happens by where the buffer is sitting:
        // a ring chronically below target (filteredErrorFrames well negative) is producer/network
        // starvation that more buffer fixes; an on-target ring that came up short is an inaudible
        // device gulp that more buffer can't fix. ~3ms of deficit is the boundary — past the
        // on-target jitter the depth-corrector leaves behind, far short of a real producer stall.
        if (framesGot < inputFramesNeeded)
        {
            const int TuneStarveMs = 3;
            var producerBehind = filteredErrorFrames <= -(TuneStarveMs * MixSampleRate / 1000.0);
            if (framesGot == 0 || producerBehind)
                Interlocked.Increment(ref tuneBlockingUnderrunsTotal);
            else
                Interlocked.Increment(ref deviceGulpUnderrunsTotal);
        }
        if (framesGot == 0)
        {
            Interlocked.Increment(ref concealmentFiresTotal);
            consecutiveEmptyReads++;
        }
        else if (framesGot < inputFramesNeeded)
        {
            Interlocked.Increment(ref partialReadFiresTotal);
            consecutiveEmptyReads = 0;
        }
        else
        {
            consecutiveEmptyReads = 0;
        }

        // Run the resampler. Output goes into outputScratch (the resampler needs a float[]
        // not a Span<float>); we then copy with clamp to the caller's span.
        ResampleOutAndCopy(output, outFrames);
        bytesReadOutputForDriftEst += outFloats * sizeof(float);

        // Concealment overlay on full-empty reads. The resampler will have produced
        // mostly-silence output for this call (we zero-padded its input); replace the
        // head of that silence with the chosen artifact so the user hears the "something
        // went wrong" cue rather than dead air, with a cosine fade-in on the next read
        // when real audio resumes.
        if (framesGot == 0)
        {
            if (consecutiveEmptyReads <= ConcealmentMaxConsecutiveEmpties)
            {
                ApplyFadeOut(output, startFrame: 0, outFrames, artifact);
            }
            inUnderrunConcealment = true;
        }
        else if (inUnderrunConcealment)
        {
            ApplyFadeIn(output, outFrames, artifact);
            inUnderrunConcealment = false;
        }

        // Track the last real sample for the next fade-out.
        if (framesGot > 0)
        {
            var lastIdx = (framesGot - 1) * MixChannels;
            lastConcealSampleL = resamplerInputScratch[lastIdx];
            lastConcealSampleR = resamplerInputScratch[lastIdx + 1];
        }
    }

    /// <summary>Run the resampler with already-supplied input and copy the result to the
    /// caller's span, clamping samples to [-1, 1] as a safety against any pathological
    /// resampler output. If the resampler returns fewer than requested output frames, the
    /// tail is zero-filled.</summary>
    private void ResampleOutAndCopy(Span<float> output, int outFrames)
    {
        var outFloats = outFrames * MixChannels;
        if (resamplerOutputScratch.Length < outFloats)
        {
            resamplerOutputScratch = new float[outFloats];
        }
        // ResampleOut consumes the input we wrote into the buffer obtained from
        // ResamplePrepare, plus any state it holds internally, and produces up to outFrames
        // output frames. Returns the actual count.
        var produced = driftResampler.ResampleOut(resamplerOutputScratch, 0, GetLastInputFramesAvailable(), outFrames, MixChannels);
        var producedFloats = produced * MixChannels;
        // Pipeline-stage probe — scan the resampler output before we clamp or copy. If this
        // shows steps that don't appear in the post-ring-read probe, the resampler itself
        // is the source.
        if (producedFloats > 0)
        {
            postResamplerStepProbe.ScanStereo(resamplerOutputScratch.AsSpan(0, producedFloats));
        }
        for (var i = 0; i < producedFloats; i++)
        {
            var v = resamplerOutputScratch[i];
            // Safety clamp. Real resampler output should never escape [-1, 1] from in-range
            // input, but a single bad sample / NaN would otherwise produce a loud audible
            // pop. Clamping is cheap insurance.
            if (v > 1f) v = 1f;
            else if (v < -1f) v = -1f;
            else if (float.IsNaN(v)) v = 0f;
            output[i] = v;
        }
        // Zero-fill if the resampler didn't produce as many frames as we asked for. Should
        // only happen in pathological cases (just after session start with empty filter
        // delay line, or after a Reset).
        if (produced < outFrames)
        {
            output.Slice(producedFloats, (outFrames - produced) * MixChannels).Clear();
        }
    }

    // Resampler scratch — output buffer (input scratch is the resamplerInputScratch field).
    private float[] resamplerOutputScratch = new float[2048];
    // How many input frames we just supplied to the resampler this call. Used by
    // ResampleOutAndCopy to call ResampleOut with the right input count. Updated by
    // ReadThroughResampler before the ResampleOutAndCopy call.
    private int lastInputFramesAvailable;
    private int GetLastInputFramesAvailable() => lastInputFramesAvailable;

    /// <summary>Synthesises the fade-out burst for the chosen artifact into the silence
    /// region starting at <paramref name="startFrame"/>. Click variant leaves the buffer's
    /// hard-zero in place.</summary>
    private void ApplyFadeOut(Span<float> inSpan, int startFrame, int silenceFrameCount, ConcealmentArtifact artifact)
    {
        if (artifact == ConcealmentArtifact.Click) return; // Hard zero; produces the original click.

        var fadeLen = artifact == ConcealmentArtifact.CosineToneLow
            ? ConcealFadeFramesLow
            : ConcealFadeFramesShort;
        var fadeFrames = Math.Min(fadeLen, silenceFrameCount);
        for (var f = 0; f < fadeFrames; f++)
        {
            // Common envelope: cosine ramp from 1.0 → 0.0 across the fade region.
            var t = (f + 1) / (double)fadeFrames;
            var g = (float)((Math.Cos(Math.PI * t) + 1.0) * 0.5);
            var idx = (startFrame + f) * MixChannels;
            switch (artifact)
            {
                case ConcealmentArtifact.NoiseBurst:
                    // White noise at last-sample peak amplitude. Random per channel — broader
                    // stereo image than mono noise, and avoids correlated content the brain
                    // can latch onto as a tone.
                    var peak = Math.Max(Math.Abs(lastConcealSampleL), Math.Abs(lastConcealSampleR));
                    inSpan[idx] = ((float)concealRng.NextDouble() * 2f - 1f) * peak * g;
                    inSpan[idx + 1] = ((float)concealRng.NextDouble() * 2f - 1f) * peak * g;
                    break;
                default:
                    // Cosine-tone variants (short/low). Hold last sample, scaled by envelope.
                    inSpan[idx] = lastConcealSampleL * g;
                    inSpan[idx + 1] = lastConcealSampleR * g;
                    break;
            }
        }
    }

    /// <summary>Fades the resumed audio in from zero with the same cosine envelope used on
    /// the way out. Click variant skips the fade — the goal of "Click" is to expose the
    /// original raw zero-fill behaviour, including its discontinuity at audio resumption.</summary>
    private static void ApplyFadeIn(Span<float> inSpan, int requestedFrames, ConcealmentArtifact artifact)
    {
        if (artifact == ConcealmentArtifact.Click) return;

        var fadeLen = artifact == ConcealmentArtifact.CosineToneLow
            ? ConcealFadeFramesLow
            : ConcealFadeFramesShort;
        var fadeFrames = Math.Min(fadeLen, requestedFrames);
        for (var f = 0; f < fadeFrames; f++)
        {
            var t = f / (double)fadeFrames;
            var g = (float)((1.0 - Math.Cos(Math.PI * t)) * 0.5);
            var idx = f * MixChannels;
            inSpan[idx] *= g;
            inSpan[idx + 1] *= g;
        }
    }

    private static int MillisecondsToBytes(int milliseconds) =>
        Math.Max(MixBytesPerFrame, milliseconds * MixBytesPerSecond / 1000);
}
