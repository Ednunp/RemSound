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
/// rate from the receiver's. This class compensates with a fixed-ratio resampler on the read
/// side, re-measured over a multi-second window, plus a small depth term that walks the ring back
/// to target. See the "Drift correction (Phase 4)" field block.
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
    // A standard uncompressed frame from a current sender is 233 samples, 4.85 ms, so that it fits one packet even through a relay group; it was 240,
    // exactly 5 ms. Rounding 4.85 down to 4 would leave 2 to 4 ms less room before a trim, so a buffer would be trimmed
    // down sooner than before. A write this few frames short of a whole millisecond counts as that millisecond. No other
    // size in use moves: 2.5, 10 and 20 ms frames, and 32- to 2,048-frame buffers, all come out as they always did.
    private const int WriteMsShortfallFrames = 7;

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
    // While it is above the target it never glides below where the buffer actually is: it follows
    // the buffer down, not a clock (2026-09-28; see the trim block in ReadFloats).
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

    // === A REAL STOP REFILLS BEFORE PLAYING ON (2026-09-30, Ed approved) ===
    // When the sender's sound stops for a moment - its card hiccups, the send-only service reopens its capture, the sending
    // PC stalls - the buffer runs dry, and when the sound came back playback carried on at once with almost nothing in
    // reserve. Measured in the simulator: a 200 ms stop at 5 s took the delay from 53 ms to 33 at a 30 ms buffer (83 to 33
    // at 60 ms), and it stayed there for 15 s, then crept back over another 30 to 50 s at the depth correction's 0.3 % -
    // which waits for a clean ten-second clock reading the stop had spoiled. For that minute there was no jitter buffer.
    // So a stop LONGER than LongGapRearmMs - the buffer empty on every read for that long - disarms playback, and the
    // sound coming back is treated as a stream starting: nothing plays until the buffer is back at its target (the
    // resumed audio then fades in, as after any gap). A momentary empty is left exactly as it was: re-arming after EVERY
    // empty read is what was removed in April 2026 (each harmless dip became a 10-15 ms silence - see the NOTE in
    // ReadFloats), and a dip that short never reaches this.
    private const int LongGapRearmMs = 100;
    private int emptyFramesInARow;
    private long longGapRearms;

    /// <summary>Times playback was re-armed after a real stop (see LongGapRearmMs), this copy and its mirrors, for the
    /// simulator and the gate.</summary>
    internal long LongGapRearmsForTest => Interlocked.Read(ref longGapRearms) + mirrors.Sum(m => Interlocked.Read(ref m.longGapRearms));
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
    // ...except in the simulator, which must hear the same run the same way every time: it seeds it (2026-09-28 - the
    // noise burst on an underrun was the one thing left making a run with a stall come out different on a second run).
    private readonly Random concealRng = new(ConcealSeedForTest ?? Random.Shared.Next());

    /// <summary>Simulator seam: the seed for the concealment noise of sessions made on this thread (null, always in the app,
    /// = random).</summary>
    [ThreadStatic] internal static int? ConcealSeedForTest;

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
    // Safety net: the click-trim in ReadFloats stays in place and fires only at
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

    // A WINDOW THE STREAM STALLED IN IS NOT A CLOCK READING (2026-09-28). Home wifi, a 1.5 s network
    // stall (SIMULATOR STALL RECOVERY): the WASAPI output's window ended 118 to 135 ms into it and read
    // -10,250 to -13,000 ppm (9,870 ms written against 10,000 played). That is inside the +-5 % band
    // below, so it was taken for a clock, and the output played 11 cents flat for ten seconds. Real
    // crystals are under 1,000 ppm apart. So the producer notes the longest gap between writes, on the
    // drift loop's own clock (the simulator's virtual time counts), and a window whose longest gap - or
    // the one still open at its end - is over DriftStallGapSec is thrown away; the depth correction at
    // its boundary still runs, and the next window starts fresh. 100 ms: the worst gap in any window on
    // the simulator's networks, measured the same day, is 80 ms (Andre's lumpy receiver, its 60 ms
    // holds included); the rough internet 55, Ed's laptop 50. Not "any underrun in the window": lumpy
    // WASAPI devices short-read routinely, and that would starve the loop of readings.
    // lastWriteTicksForDrift is the producer's; longestWriteGapTicks is raised by the producer and
    // taken (reset to 0) by the consumer at each window boundary.
    private long lastWriteTicksForDrift;
    private long longestWriteGapTicks;
    private const double DriftStallGapSec = 0.100;

    // NOR IS A WINDOW THE OUTPUT STOPPED IN (2026-09-28, Ed approved). An output that hiccups - a driver or a device
    // stopping for a moment, its missed callbacks gone - left the window reading the stall's share of it as a clock: 100 ms
    // in 10 s is +10,000 ppm, inside the band, so it was believed, and the output then played that much fast for most of a
    // minute and ran its buffer dry (the performance suite's finding: 127 underruns in the minute after a 20 ms hiccup on an
    // ASIO output, 6,598 after 450 ms; clean again only 49 s after a 0.1 s one). So the reads are timed too, on the same
    // clock: a window whose longest gap between reads is longer than DriftReadStallMinSec and DriftReadStallFactor of its
    // own average read is thrown away the same way; the depth correction still runs. A late driver thread that fires its
    // missed callbacks in a burst is a different case (the reads catch up) and is left as it was.
    private const double DriftReadStallMinSec = 0.020;
    private const double DriftReadStallFactor = 4;
    private long lastReadTicksForDrift;     // render thread
    private long longestReadGapTicks;       // render thread, taken at each window boundary
    private long readsInWindow;             // render thread

    // THE WRITTEN COUNT AT A WINDOW'S EDGE IS READ OFF THE STREAM'S ARRIVAL LINE (2026-09-28). A stream
    // arrives in lumps - a WASAPI capture's 10 ms blocks (four 2.5 ms Opus packets at once), 10 ms Opus
    // frames, a network that delivers in bunches or late - and the count of bytes written at the
    // edge's moment is whole lumps: whether the lump due there has landed yet is a coin toss, one
    // 10 ms block in a ten-second window, a reading 1,000 ppm out. Measured with the cards at the same
    // rate: the WASAPI output's first reading -1,000 ppm on a wired network, holding its buffer ~10 ms
    // over the target for the first 45 s (SIMULATOR DESCENT on WASAPI); and in "both", the ASIO
    // output's copy of the WASAPI-lane stream read the SAME -1,000 as that stream's WASAPI copy (the
    // two are fed the same lumps and open their windows together), so it moved with the WASAPI side
    // instead of its own output: 29.6 ms at 45 s against a 15 ms target while the ASIO-lane stream's
    // copy beside it was at 20.3 (SIMULATOR AUTO-TUNE FROM 500, wired). First readings over eight
    // seeds: up to 1,000 ppm out on a wired network, 1,067 on home wifi, 1,250 on the internet.
    //
    // So each arrival is noted as where it sits against a steady clock - the bytes written so far less
    // what a steady stream would have written by then - one note per lump (writes within
    // ArrivalSameLumpSec of one another are one lump, and so is the burst after a stall), and the
    // count at an edge is the mean of the last ArrivalLineSec of notes carried forward to the edge's
    // moment: audio counted as it is made, not as it happens to have been delivered. The mean evens
    // out lumps, jitter and a lump a packet long or short; no note is older than ArrivalLineSec, so a
    // stall or a real loss is forgotten a fifth of a second after it (and the window it fell in is
    // thrown away anyway, see DriftStallGapSec). And an edge is not taken in the middle of a lump,
    // while its last packets are still landing: it waits until the last write is DriftEdgeSettleSec
    // old - the next read on an ASIO output, a read or two on a WASAPI one - and is taken regardless
    // after DriftEdgeMaxDeferSec.
    private const double DriftEdgeSettleSec = 0.001;
    private const double DriftEdgeMaxDeferSec = 0.030;
    private const double ArrivalSameLumpSec = 0.0005;
    private const double ArrivalLineSec = 0.2;
    private const int ArrivalNotes = 256;
    private long firstEdgeDueTicks;
    private readonly object arrivalLock = new();
    private readonly double[] arrivalOffsetBytes = new double[ArrivalNotes];   // under arrivalLock
    private readonly long[] arrivalNoteTicks = new long[ArrivalNotes];        // under arrivalLock
    private int arrivalNoteCount;                                              // under arrivalLock
    private int arrivalNoteNext;                                               // under arrivalLock
    private long arrivalOriginTicks;                                           // under arrivalLock

    /// <summary>Network thread, at every write: where the stream now sits against a steady clock (see ArrivalLineSec).</summary>
    private void NoteArrival(long writtenBytes, long nowTicks)
    {
        lock (arrivalLock)
        {
            if (arrivalOriginTicks == 0) arrivalOriginTicks = nowTicks;
            var offset = writtenBytes - (nowTicks - arrivalOriginTicks) / (double)Stopwatch.Frequency * MixBytesPerSecond;
            var last = (arrivalNoteNext + ArrivalNotes - 1) % ArrivalNotes;
            if (arrivalNoteCount > 0 && nowTicks - arrivalNoteTicks[last] <= ArrivalSameLumpSec * Stopwatch.Frequency)
            {
                // The same lump: its note is where the stream sits once the lump is in.
                arrivalOffsetBytes[last] = offset;
                arrivalNoteTicks[last] = nowTicks;
                return;
            }
            arrivalOffsetBytes[arrivalNoteNext] = offset;
            arrivalNoteTicks[arrivalNoteNext] = nowTicks;
            arrivalNoteNext = (arrivalNoteNext + 1) % ArrivalNotes;
            if (arrivalNoteCount < ArrivalNotes) arrivalNoteCount++;
        }
    }

    /// <summary>Is there an arrival line to read at <paramref name="nowTicks"/>: notes reaching back half of
    /// ArrivalLineSec with no stall among them? After a window thrown away for a stall, the next is not started until
    /// there is (see WrittenBytesAt: a stall breaks the line).</summary>
    private bool ArrivalLineSettled(long nowTicks)
    {
        lock (arrivalLock)
        {
            var breakTicks = (long)(DriftStallGapSec * Stopwatch.Frequency);
            var reach = nowTicks - (long)(ArrivalLineSec / 2 * Stopwatch.Frequency);
            var newer = nowTicks;
            for (var i = 0; i < arrivalNoteCount; i++)
            {
                var k = (arrivalNoteNext + ArrivalNotes - 1 - i) % ArrivalNotes;
                if (newer - arrivalNoteTicks[k] > breakTicks) return false;
                if (arrivalNoteTicks[k] <= reach) return true;
                newer = arrivalNoteTicks[k];
            }
            return false;
        }
    }

    /// <summary>May a window's edge, due at <paramref name="dueTicks"/>, be taken now? Once the last write has settled
    /// (see DriftEdgeSettleSec), or once it has waited long enough.</summary>
    private bool EdgeSettled(long nowTicks, long dueTicks)
    {
        var lastWriteTicks = Interlocked.Read(ref lastWriteTicksForDrift);
        return lastWriteTicks == 0
            || nowTicks - lastWriteTicks >= DriftEdgeSettleSec * Stopwatch.Frequency
            || nowTicks - dueTicks >= DriftEdgeMaxDeferSec * Stopwatch.Frequency;
    }

    /// <summary>Render thread, at a window's edge: the bytes written as of now, read off the stream's arrival line - the
    /// mean of the last ArrivalLineSec of arrival notes, carried to now at the steady rate. With no note that recent (the
    /// stream stalled, and the window will be thrown away), the count as it stands.
    ///
    /// <para>A STALL BREAKS THE LINE (2026-09-30, Ed approved). The mean reaches back no further than the last wait of
    /// more than DriftStallGapSec between two notes: the notes before a stall are no guide to the count after it. A sending
    /// card that stopped for 150 ms lost that sound for good, so its notes from before the stall sit 150 ms of audio above
    /// the line the stream is on now; averaged in, they put the next window's starting count too high, and that window -
    /// nothing wrong in it - read two cards at the same rate as 2,500 ppm apart. It was believed (it is inside the band),
    /// and the output played 4 cents flat and grew its buffer for over a minute (SIMULATOR DRIFT AFTER A STALL).</para></summary>
    private long WrittenBytesAt(long nowTicks)
    {
        lock (arrivalLock)
        {
            if (arrivalNoteCount == 0) return Interlocked.Read(ref bytesWrittenForDriftEst);
            var oldest = nowTicks - (long)(ArrivalLineSec * Stopwatch.Frequency);
            var breakTicks = (long)(DriftStallGapSec * Stopwatch.Frequency);
            double sum = 0;
            var notes = 0;
            var newer = nowTicks;
            for (var i = 0; i < arrivalNoteCount; i++)
            {
                var k = (arrivalNoteNext + ArrivalNotes - 1 - i) % ArrivalNotes;
                if (arrivalNoteTicks[k] < oldest || newer - arrivalNoteTicks[k] > breakTicks) break;
                sum += arrivalOffsetBytes[k];
                notes++;
                newer = arrivalNoteTicks[k];
            }
            if (notes == 0) return Interlocked.Read(ref bytesWrittenForDriftEst);
            return (long)(sum / notes + (nowTicks - arrivalOriginTicks) / (double)Stopwatch.Frequency * MixBytesPerSecond);
        }
    }

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
    // Descent state (see the DescentDepthBias block): set on the audio thread when the target it is handed falls.
    private bool descentUntilOnTarget;
    private int lastTargetMs = -1;
    private int lastOutputTargetMs = -1;
    private long snapArmedTicks;
    private long snapCount;
    private long snapSkippedBytes;
    private long snapsInQuiet;
    private long resamplerUpdatesTotal;

    // Scratch buffer for reading from the ring buffer in float form. Sized lazily based on
    // the largest input-frames request the resampler asks for; persists across calls so
    // we don't realloc on the hot path.
    private float[] resamplerInputScratch = new float[2048];

    // Live state — the current buffer-level offset from target, low-pass filtered. Surfaced on the
    // diag line ("where the buffer is sitting") and used to classify short reads for the auto-tune
    // (see ReadThroughResampler). Updated each Read; it drives no drift correction itself.
    private double filteredErrorFrames;
    private long prevDriftSampleTicks;
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
    // The depth that steady-state term steers is the buffer's MEAN over the last DepthMeanSec, not
    // the one sample taken at the window's edge (2026-09-28). The buffer is a sawtooth - packets
    // arrive in lumps, the card takes blocks - and the 10 s window is a whole number of 10 ms
    // capture blocks, so the edge lands at much the same point of the sawtooth every time. Measured:
    // in SIMULATOR AUTO-TUNE FROM 500 (both outputs, home wifi) the ASIO output's copy of the
    // WASAPI-lane stream was sampled 7.1 ms under its mean at the first edge (489.3 against a 500
    // target); with that window's clock reading (-1,067 ppm, one 10 ms block short) it grew the
    // buffer to 523.6 ms and the click-trim cut 188 ms. In SIMULATOR BOTH OUTPUTS every edge sat
    // 3.1 to 4.0 ms over the mean, holding that buffer 3 to 4 ms under its target. A tenth of a
    // second spans ten 10 ms blocks and lags a 3 ms/s descent by 0.3 ms. The fast approach below
    // keeps the instantaneous depth: at 50 ms a second a lagging mean would overshoot.
    private const double DepthMeanSec = 0.1;
    private double depthMeanFrames = -1;
    private long prevDepthMeanTicks;
    /// <summary>Simulator seam: that mean depth, in ms (the depth now before the first read).</summary>
    internal double DepthMeanMsForTest => (depthMeanFrames >= 0 ? depthMeanFrames : playout.BufferedBytes / (double)MixBytesPerFrame) * 1000.0 / MixSampleRate;
    /// <summary>Simulator seam: has the drift loop taken its first clock reading? (A reading can be exactly 1.0.)</summary>
    internal bool DriftTrackingForTest => resamplerActivelyTracking;
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
    // === Coming down from a lowered target (Ed, 2026-09-28) ===
    // Ed: from a silly delay "you want it to snap back down... at most a minute... honestly more like 30 seconds"; from a
    // sensible one, 100 ms to 20 in about 20 seconds. Measured before: the auto-tune took 3.5 minutes from 500 ms, draining
    // at the steady-state 0.3 % recomputed once every 10 s window, and 45 ms and under crept in with a 15 s time constant.
    // So once the target is LOWERED (by anything - the auto-tune, the per-person correction, the lab's tuner):
    // - Descent mode: the depth correction pulls up to DescentDepthBias (0.4 %, about 4 ms a second, ~7 cents - still
    //   click-free, the sound just plays that little bit fast), spread over DescentDepthCorrectionSec and recomputed several
    //   times a second like the fast approach, until the buffer is within DescentDoneMs of the target.
    // - The snap: while the buffer is more than SnapAboveMs over the target, everything above that is skipped at once with a
    //   short crossfade (AudioRingBuffer.SkipWithCrossfade) - a skipped bit of sound, never a click - waiting up to
    //   SnapWaitForQuietSec for a quiet moment so the skip lands in a pause when there is one. Then the rest drains as above.
    // A burst after a network stall (the target unchanged) is still the click-trim's business: only a lowered target snaps.
    private const double DescentDepthCorrectionSec = 3.0;
    private const double DescentDepthBias = 0.004;
    private const double DescentDoneMs = 2.0;
    private const int SnapAboveMs = 100;
    private const int SnapMinSkipMs = 20;
    private const double SnapWaitForQuietSec = 2.0;
    private const float SnapQuietPeak = 0.0056f;        // -45 dBFS
    private const int SnapCrossfadeFrames = 720;        // 15 ms
    private const int SnapAlignFrames = 240;            // the landing point may move 5 ms either way to line up
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

    // Audible GAPS (2026-09-26): one run of tune-blocking short reads is one gap, however many callbacks it spans - at ASIO's
    // 0.67 ms callbacks a 5 ms gap is about eight short reads, which is what made the underrun count look alarming while
    // Ed heard clean music. The latency lab's "keep dropouts rare" auto-tune counts these.
    private long dropoutGapsTotal;
    private bool lastReadWasShort;
    public long DropoutGapsTotal => Interlocked.Read(ref dropoutGapsTotal);
    // How long each gap lasted (2026-10-22, for the log's run-of-gaps lines, "the longest 40 ms"): the sound missing over a
    // run of short reads, in input frames, written down as milliseconds when the run ends. Render thread only, but for the
    // peak the log takes once a second.
    private long gapRunMissingFrames;
    private int longestGapMs;
    /// <summary>The longest audible gap that ended since the last call, in ms. Resets on read.</summary>
    public int TakeLongestGapMs() => Interlocked.Exchange(ref longestGapMs, 0);
    /// <summary>Inaudible partial short-reads taken while the ring was on target — the device-gulp
    /// case more buffer can't fix. These must NOT block the auto-tune from lowering.</summary>
    public long DeviceGulpUnderrunsTotal => Interlocked.Read(ref deviceGulpUnderrunsTotal);
    /// <summary>Live state — the LP-filtered drift error in stereo frames. Positive = buffer
    /// running above target on average (sender clock faster); negative = buffer below
    /// target. Magnitude shows how off-target the buffer's average position is right now.</summary>
    public double FilteredDriftErrorFrames => filteredErrorFrames;

    public IPEndPoint Endpoint { get; }
    /// <summary>The stream ID this session was opened for. Sessions are keyed by
    /// (Endpoint, StreamId) so a single peer can produce multiple simultaneous streams
    /// (e.g. WASAPI lane + ASIO lane in the native-independent mode). For single-lane
    /// modes there's still one session per peer with whatever streamId the sender chose
    /// (a random one, re-rolled whenever the stream restarts or changes format).</summary>
    public ushort StreamId { get; }
    /// <summary>Which output lane this session renders on. Assigned by PlayoutEngine against the
    /// output lanes that have a device ticked — the sender's announced Lane byte does not decide it.
    /// PlayoutEngine uses this to decide which of its per-route IWaveProvider surfaces this session
    /// contributes to. <see cref="RenderRoute.Mixed"/> until a lane is assigned.</summary>
    public RenderRoute Route { get; set; } = RenderRoute.Mixed;
    /// <summary>Ring capacity this session was sized to (bytes) — so a mirror replica for another
    /// output lane can be created with the same capacity.</summary>
    public int Capacity { get; }

    /// <summary>True when this is a MIRROR replica rather than the primary — an extra copy of the
    /// same stream, fed the same decoded bytes, that exists so a second output lane can play it on
    /// its own clock. Both live in PlayoutEngine's snapshot.
    ///
    /// <para>Any read path that does NOT filter by <see cref="Route"/> must filter on this instead,
    /// or it sums the primary and its mirror and gets the same audio twice. That is what
    /// <c>ReadClaimedPeer</c> did: the fan-out landed 136 commits before the plugin's read path was
    /// written, so the plugin summed both replicas and a claimed peer arrived in the DAW about 6 dB
    /// hot and comb-filtered as the two rings drifted apart. 2026-08-23 audit, finding R2.</para></summary>
    public bool IsMirror { get; init; }
    public int BufferedBytes => playout.BufferedBytes;
    public int BufferedMs => playout.BufferedBytes / MixBytesPerFrame * 1000 / MixSampleRate;
    public long UnderrunCount => playout.UnderrunCount;
    public long DropCount => playout.DropCount;

    /// <summary>Per-cause drop accessors (cumulative bytes / counts since session start).
    /// Splits the previously-opaque DropCount so the diag log can distinguish click-trim
    /// from drain-on-knob-change from ringbuffer overflow. AggregateDrops on the engine
    /// continues to expose the rolled-up total for back-compat.</summary>
    public long TrimDropBytes => Interlocked.Read(ref trimDropBytes);
    public long DrainDropBytes => Interlocked.Read(ref drainDropBytes);
    public long TrimFireCount => Interlocked.Read(ref trimFireCount);
    /// <summary>Snaps down from a lowered target (see the DescentDepthBias block): how many, how much they skipped, and how
    /// many landed in a quiet moment.</summary>
    public long SnapCount => Interlocked.Read(ref snapCount);
    public long SnapSkippedBytes => Interlocked.Read(ref snapSkippedBytes);
    public long SnapsInQuiet => Interlocked.Read(ref snapsInQuiet);
    /// <summary>Coming down from a lowered target right now (descent mode).</summary>
    public bool DescentActive => descentUntilOnTarget;

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
    public DateTime LastWriteUtc { get; private set; } = AudioClock.UtcNow;

    /// <summary>Gate seam: audio bytes really written into this session's buffer since it opened. LastWriteUtc starts at
    /// "now" so a new session is not swept at once, which means a session opened by a format packet alone reads as
    /// recently written for a moment; this does not.</summary>
    internal long AudioBytesWrittenForTest => Interlocked.Read(ref bytesWrittenForDriftEst);

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
    internal SessionPlayout[] Mirrors => mirrors;

    /// <summary>The latency lab's per-stream buffer (2026-09-26): this stream's own measured need in ms, or 0 for none.
    /// When set and below the output's target, the stream is played at this depth instead - so a smooth stream stops
    /// paying for a bursty one on the same output. Never above the output's target: the output's setting stays the
    /// ceiling. It only moves through the same arming, trim-glide and drift paths as any other target, so lowering it
    /// is exactly as gentle as the auto-tune lowering the slider (and the caller lowers it at most 1 ms a second).</summary>
    public volatile int OwnNeedMs;

    /// <summary>The per-stream sizing's memory of this replica's underrun counter (UI thread only).</summary>
    internal long LabLastUnderrunTotal = -1;

    /// <summary>The target this replica actually plays at: the output's, or this stream's own need if lower.</summary>
    public int EffectiveTargetMs(int routeTargetMs)
    {
        var own = OwnNeedMs;
        return own > 0 && own < routeTargetMs ? own : routeTargetMs;
    }

    /// <summary>Set on every replica the engine fans out — the primary and each mirror — so it can ask
    /// whether a lane is still taking audio. Null in tests that build a session directly.</summary>
    internal Func<RenderRoute, bool>? LaneIsConsuming { get; set; }

    /// <summary>The clock the trim glide and the drift loop read: the Stopwatch, always, in the app. A self-test hands in
    /// a simulated clock so it can drive minutes of this loop in a moment.</summary>
    internal Func<long> NowTicks { get; init; } = AudioClock.GetTimestamp;

    public void Write(ReadOnlySpan<byte> source)
    {
        // Audio arrived for this stream, whichever copy below plays it. Stamped only inside WriteLocal, a stream playing
        // from its ASIO copy while the WASAPI lane was not reading looked silent, was pruned four seconds later, and came
        // back on the next format packet: the ASIO output dropped out every few seconds (found 2026-09-25).
        LastWriteUtc = AudioClock.UtcNow;
        var mir = mirrors;
        // EACH LANE PLAYS ITS OWN COPY. When the lane this primary belongs to stops reading while a mirror's lane
        // is still reading, that mirror is what plays the peer. So the primary is not fed — exactly as a mirror on
        // a quiet lane is not fed below — and the read path does not let the primary fall through onto the
        // mirror's lane (PlayoutEngine.ReadForRouteInner). It used to fall through AND the mirror played, so the
        // peer was heard twice on that lane, about 6 dB hot and comb-filtered. Letting the mirror play, not the
        // primary, keeps that lane's buffer, underrun and tuning figures its own. 2026-09-13 review, finding 3.
        if (!(mir.Length > 0 && LaneIsConsuming is { } primaryLane && !primaryLane(Route) && AnyMirrorConsuming(mir, primaryLane)))
            WriteLocal(source);
        for (var i = 0; i < mir.Length; i++)
        {
            var m = mir[i];
            // DO NOT FEED A LANE THAT IS NOT DRINKING. A mirror whose lane has stopped being read has
            // nobody to play it, so every byte written here is a byte that will overflow. Ed,
            // 2026-09-07: his ASIO lane stopped rendering while its output was still ticked, and this
            // line ran 288,960 bytes a second into it for hours. The cost was never the bytes — it was
            // the CPU, which starved the lane he was actually listening to. See LaneActivity.
            if (m.LaneIsConsuming is { } consuming && !consuming(m.Route)) continue;
            m.WriteLocal(source);
        }
    }

    /// <summary>The copy of this stream being fed right now: this primary, or - when its own lane has stopped reading and a
    /// mirror's lane still is - that mirror, exactly as <see cref="Write"/> decides. A plugin's track reads whichever copy is
    /// fed: reading the primary alone, a DAW track receiving the peer went silent whenever the WASAPI output stopped being
    /// read with an ASIO output still going (2026-09-25 sweep, measured: 95,040 frames with the lane read, 0 without).</summary>
    internal SessionPlayout FedCopy
    {
        get
        {
            var mir = mirrors;
            if (mir.Length == 0 || LaneIsConsuming is not { } primaryLane || primaryLane(Route)) return this;
            for (var i = 0; i < mir.Length; i++)
                if (primaryLane(mir[i].Route)) return mir[i];
            return this;
        }
    }

    private static bool AnyMirrorConsuming(SessionPlayout[] mir, Func<RenderRoute, bool> consuming)
    {
        for (var i = 0; i < mir.Length; i++)
        {
            if (consuming(mir[i].Route)) return true;
        }
        return false;
    }

    /// <summary>Does this stream have a mirror for <paramref name="route"/>? Then that lane has a copy of its own,
    /// and the primary must never fall through onto it. Render thread; allocation-free.</summary>
    internal bool HasMirrorOn(RenderRoute route)
    {
        var mir = mirrors;
        for (var i = 0; i < mir.Length; i++)
        {
            if (mir[i].Route == route) return true;
        }
        return false;
    }

    /// <summary>The single-instance write body — used directly for a mirror replica (so a mirror never
    /// re-fans-out) and by <see cref="Write"/> for the primary before it forwards to its mirrors.</summary>
    private void WriteLocal(ReadOnlySpan<byte> source)
    {
        var ms = (source.Length / MixBytesPerFrame + WriteMsShortfallFrames) * 1000 / MixSampleRate;
        if (ms > largestWriteMs) largestWriteMs = ms;
        playout.Write(source);
        // Track bytes written for the drift-resampler measurement window. Producer thread
        // updates this; consumer thread (audio thread in ReadFloats) reads it via
        // Interlocked.Read when sampling the window. Cumulative since session start.
        var writtenNow = Interlocked.Add(ref bytesWrittenForDriftEst, source.Length);
        var nowWriteTicks = NowTicks();
        // Where the stream now sits against a steady clock, for the window edges (see ArrivalLineSec).
        NoteArrival(writtenNow, nowWriteTicks);
        // The gap since the last write, for the drift loop's stall check (see DriftStallGapSec). A compare-and-swap loop, so
        // a gap raised here just as the consumer takes and resets the window's longest is never lost to the reset.
        var lastWriteTicks = Interlocked.Exchange(ref lastWriteTicksForDrift, nowWriteTicks);
        if (lastWriteTicks != 0)
        {
            var gap = nowWriteTicks - lastWriteTicks;
            long seen;
            while (gap > (seen = Interlocked.Read(ref longestWriteGapTicks))
                   && Interlocked.CompareExchange(ref longestWriteGapTicks, gap, seen) != seen) { }
        }
        LastWriteUtc = AudioClock.UtcNow;
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
    public void NoteFramesQueued(int targetLatencyMs) => NoteFramesQueued(targetLatencyMs, _ => targetLatencyMs);

    /// <summary>
    /// As above, but each mirror arms against ITS OWN output lane's target rather than the primary's.
    ///
    /// <para>The mirrors used to be handed the primary's number. With two independent sliders — WASAPI
    /// at 80 ms and ASIO at 20 ms, say — the ASIO mirror then waited for its ring to hold 80 ms before
    /// it would start playing, and only afterwards did the depth corrector glide it down to the 20 ms
    /// its own slider asked for. So the ASIO output started late and drifted to the right value
    /// instead of simply arming at it. 2026-08-23 audit, finding R6.</para>
    /// </summary>
    public void NoteFramesQueued(int targetLatencyMs, Func<RenderRoute, int> targetForRoute)
    {
        NoteFramesQueuedLocal(targetLatencyMs);
        // Each mirror arms independently on its own ring (same bytes arrive at the same rate, so they
        // arm at ~the same moment). Forwarded here rather than called separately so the decode side
        // only ever touches the primary.
        var mir = mirrors;
        for (var i = 0; i < mir.Length; i++) mir[i].NoteFramesQueuedLocal(targetForRoute(mir[i].Route));
    }

    private void NoteFramesQueuedLocal(int targetLatencyMs)
    {
        targetLatencyMs = EffectiveTargetMs(targetLatencyMs);
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

    /// <summary>The user lowered the latency knob: on the next read the buffer comes straight down to the new target, the
    /// excess skipped with a short crossfade (2026-09-28). It used to be dropped outright and the session disarmed until
    /// the buffer refilled - a click and a moment's silence on every move down.</summary>
    public void RequestDrainToTarget()
    {
        drainRequested = true;
    }

    /// <summary>The user just moved the latency slider: converge on the new depth in seconds rather
    /// than minutes (see the FastDepthBias block). Deliberate changes only — the auto-tune's soft
    /// setter must NOT call this, so its gentle, parked descent behaviour is untouched.
    ///
    /// <para>THIS COPY ONLY (2026-09-28). It used to set every mirror too, and the slider's setter
    /// (PlayoutEngine.SetMaxLatencyMs) already calls it on every copy its slider governs - so the only
    /// thing the forwarding ever added was copies on the OTHER lane. With both kinds of output every
    /// stream's primary plays on the WASAPI output and its mirror on the ASIO one, so moving the WASAPI
    /// slider by hand threw the ASIO output's copies into the 5 % fast approach toward their own,
    /// unchanged target: measured in the simulator, a WASAPI raise from 40 to 80 ms while the ASIO
    /// output was coming down from 100 to 20 took its descent from 6.8 cents sharp to 39 to 41 cents
    /// for three seconds. The ASIO slider's own moves never reached the WASAPI copies (mirrors have no
    /// mirrors), so it was one-way. Each lane's copies now answer to their own slider alone.</para></summary>
    public void RequestFastLatencyApproach()
    {
        fastApproachUntilOnTarget = true;
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
    /// <param name="emitRecordTap">False when this read is not on the lane that records. Every replica carries the
    /// split-recording tap, so the tap follows whichever copy the recording lane plays — the primary normally, its
    /// mirror while the primary's own lane is not reading — and a peer is never recorded once per lane.</param>
    public int ReadFloats(Span<float> output, int outFrames, int targetLatencyMs, int currentMaxLatencyMs, int smoothness = 3, bool applyShaping = true, bool emitRecordTap = true)
    {
        var outputTargetMs = targetLatencyMs;
        targetLatencyMs = EffectiveTargetMs(targetLatencyMs);
        // Drain on user knob change.
        if (drainRequested)
        {
            drainRequested = false;
            var targetBytes = MillisecondsToBytes(targetLatencyMs);
            var buffered = playout.BufferedBytes;
            if (buffered > targetBytes)
            {
                var bytes = playout.SkipWithCrossfade(buffered - targetBytes, SnapCrossfadeFrames, SnapAlignFrames);
                Interlocked.Add(ref drainDropBytes, bytes);
                depthMeanFrames = -1;
            }
        }
        // A lowered target starts the descent (see the DescentDepthBias block). The hand-moved slider has its own drain.
        // Only the OUTPUT's target lowered - the slider, the auto-tune, the per-person correction. A stream's own need
        // (OwnNeedMs) comes down 1 ms a second by itself and reaches it through the steady drift path, as it was built to;
        // counted as a lowering, every second of it started the descent's 7-cent pull and let a network stall's backlog
        // be snapped away - measured in the simulator once per-stream buffers became normal (2026-09-29).
        if (lastTargetMs >= 0 && targetLatencyMs < lastTargetMs && outputTargetMs < lastOutputTargetMs) descentUntilOnTarget = true;
        lastTargetMs = targetLatencyMs;
        lastOutputTargetMs = outputTargetMs;

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
        // Largely a safety net: the drift resampler and its depth term below keep the buffer near
        // target in normal operation, so the trim only fires under catastrophic conditions
        // (large step changes the slow depth correction can't keep up with). Replaced an earlier
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
            //
            // And while the ceiling is above the target it never glides below where the buffer actually
            // is: it follows the buffer down, not a clock (2026-09-28). The glide assumed the buffer was
            // already draining. It is not until the drift loop's next 10 s window, and on WASAPI the
            // 10 ms lumps keep it close to the line. Measured in SIMULATOR AUTO-TUNE FROM 500 (WASAPI only,
            // wired): the target went 500 -> 405 with the buffer at 507.9 ms; five seconds later the buffer
            // had wandered UP to 516.2, the ceiling had glided to 492.6, and the trim cut 102 ms at once -
            // the same in every WASAPI run of that step, 102 to 106 ms each. Held at the buffer, the ceiling
            // waits for the drain. A genuine surge - the buffer growing more than the margin above the held
            // ceiling - still trims, the glide never goes faster than 1.5 ms/s, and in steady state
            // (ceiling == target) nothing changes.
            const double TrimGlideDownMsPerSec = 1.5;
            var nowTrimTicks = NowTicks();
            if (trimGlideTargetMs <= 0 || targetLatencyMs >= trimGlideTargetMs)
            {
                trimGlideTargetMs = targetLatencyMs;
            }
            else if (prevTrimGlideTicks != 0)
            {
                var dtSec = (nowTrimTicks - prevTrimGlideTicks) / (double)Stopwatch.Frequency;
                var glidedMs = trimGlideTargetMs - TrimGlideDownMsPerSec * dtSec;
                var bufferedMs = playout.BufferedBytes * 1000.0 / MixBytesPerSecond;
                trimGlideTargetMs = Math.Max(targetLatencyMs, Math.Max(glidedMs, Math.Min(trimGlideTargetMs, bufferedMs)));
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
        var nowTicks = NowTicks();
        var driftTargetBytes = MillisecondsToBytes(targetLatencyMs);
        if (prevDriftSampleTicks != 0)
        {
            var dtSec = (nowTicks - prevDriftSampleTicks) / (double)Stopwatch.Frequency;
            var errorFrames = ((double)playout.BufferedBytes - driftTargetBytes) / MixBytesPerFrame;
            var filterAlpha = dtSec / (DriftFilterTimeConstantSec + dtSec);
            filteredErrorFrames = (1.0 - filterAlpha) * filteredErrorFrames + filterAlpha * errorFrames;
        }
        prevDriftSampleTicks = nowTicks;

        if (descentUntilOnTarget) SnapDownIfFarAbove(targetLatencyMs, nowTicks);
        UpdateDriftResamplerRateIfDue(nowTicks, targetLatencyMs);
        // While converging on a freshly-moved slider, re-apply the (much larger) depth correction
        // several times a second instead of waiting for the 10 s drift window — otherwise the first
        // seconds after the user's move do nothing at all, which is exactly what "the slider doesn't
        // work" felt like. Self-clearing once the ring is within FastApproachDoneMs of target.
        // Coming down from a lowered target is recomputed as often (see the DescentDepthBias block).
        if ((fastApproachUntilOnTarget || descentUntilOnTarget) && resamplerActivelyTracking
            && (nowTicks - lastFastApplyTicks) / (double)Stopwatch.Frequency >= FastApplyIntervalSec)
        {
            lastFastApplyTicks = nowTicks;
            ApplyDepthCorrectedRate(targetLatencyMs, fastApproachUntilOnTarget ? -1 : depthMeanFrames);
        }

        // Read through the resampler and apply concealment on full underruns.
        ReadThroughResampler(output, outFrames);
        // Split-recording tap, RAW variant — grab this peer's block BEFORE pan/EQ (bypass recording).
        var tap = emitRecordTap ? recordTap : null;
        if (tap is not null && recordRaw) EmitRecordTapGuarded(tap, output, outFrames);
        // Per-peer pan + EQ: this peer's block is fully decoded and isolated here, immediately before
        // PlayoutEngine sums it into the mix — so shaping is per-peer and pre-mix, and being per-sample
        // it adds no buffering latency. Null (unshaped peer) skips the whole stage.
        //
        // GUARDED. Both this and the taps around it used to run bare on the render thread, so an
        // exception from a recorder callback or a filter unwound through PlayoutEngine and
        // LaneOutput.Read into NAudio's WasapiOut render loop — which catches it, raises
        // PlaybackStopped, and makes MultiOutputPlayout log `output "X" lost the device`. A recorder
        // bug therefore presented as a hardware fault AND killed the output. The mixed-mix taps
        // either side of this are already wrapped with "recorder failure isolated from audio path";
        // these were missed. 2026-08-23 audit, finding R3.
        // applyShaping = false is the DAW's "give me the raw peer" path: a plugin track that would
        // rather do its own pan and EQ than inherit RemSound's. Safe to skip here because a peer a
        // plugin has CLAIMED is excluded from the speaker mix entirely, so this session has exactly
        // one consumer. See PlayoutEngine.ReadClaimedPeer. 2026-09-06.
        var d = applyShaping ? dsp : null;
        if (d is not null)
        {
            try { d.Process(output, outFrames); }
            catch { /* a bad filter must not take the audio device down; the block passes unshaped */ }
        }
        // Split-recording tap, SHAPED variant — after pan/EQ (the default, "record what you hear").
        if (tap is not null && !recordRaw) EmitRecordTapGuarded(tap, output, outFrames);
        return outFrames;
    }

    /// <summary>Hand this peer's block to the recorder, isolating the audio path from anything the
    /// recorder does with it. See the note in <see cref="ReadFloats"/> for why bare was wrong.</summary>
    private void EmitRecordTapGuarded(Action<IPEndPoint, ReadOnlyMemory<float>> tap, Span<float> block, int frames)
    {
        try { EmitRecordTap(tap, block, frames); }
        catch { /* recorder failure isolated from audio path */ }
    }

    /// <summary>Set the resampler rate to the feed-forward clock ratio plus the depth-feedback bias
    /// that walks the ring toward <paramref name="targetLatencyMs"/>. Shared by the steady-state
    /// 10 s window and the fast approach after a deliberate slider move — the ONLY difference is how
    /// hard it's allowed to pull (see the FastDepthBias block), so both paths stay one piece of
    /// arithmetic rather than two that can drift apart. <paramref name="meanDepthFrames"/>: the
    /// steady-state window's depth, the buffer's mean (see DepthMeanSec); the fast approach and a
    /// window during it use the depth now.</summary>
    private void ApplyDepthCorrectedRate(int targetLatencyMs, double meanDepthFrames = -1)
    {
        var depthFrames = meanDepthFrames >= 0 && !fastApproachUntilOnTarget ? meanDepthFrames : playout.BufferedBytes / MixBytesPerFrame;
        var targetFrames = targetLatencyMs * MixSampleRate / 1000;
        var depthError = depthFrames - targetFrames;
        var fast = fastApproachUntilOnTarget;
        if (fast && Math.Abs(depthError) <= FastApproachDoneMs * MixSampleRate / 1000)
        {
            fastApproachUntilOnTarget = false; // arrived — back to the sleepy steady-state numbers
            fast = false;
        }
        var descent = !fast && descentUntilOnTarget;
        if (descent && depthError <= DescentDoneMs * MixSampleRate / 1000)
        {
            descentUntilOnTarget = false; // down - back to the steady state
            descent = false;
        }
        var spreadSec = fast ? FastDepthCorrectionSec : descent ? DescentDepthCorrectionSec : DepthCorrectionSec;
        var bias = fast ? FastDepthBias : descent ? DescentDepthBias : MaxDepthBias;
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
        // The buffer's mean depth for the depth correction (see DepthMeanSec), sampled where the one
        // sample it replaces was taken: every read, before the read.
        var depthNowFrames = playout.BufferedBytes / (double)MixBytesPerFrame;
        if (depthMeanFrames < 0 || prevDepthMeanTicks == 0) depthMeanFrames = depthNowFrames;
        else
        {
            var dtSec = (nowTicks - prevDepthMeanTicks) / (double)Stopwatch.Frequency;
            depthMeanFrames += dtSec / (DepthMeanSec + dtSec) * (depthNowFrames - depthMeanFrames);
        }
        prevDepthMeanTicks = nowTicks;

        // The gap since the last read, for the output-stall check (see DriftReadStallMinSec).
        if (lastReadTicksForDrift != 0 && nowTicks - lastReadTicksForDrift > longestReadGapTicks) longestReadGapTicks = nowTicks - lastReadTicksForDrift;
        lastReadTicksForDrift = nowTicks;
        readsInWindow++;

        if (resamplerWindowStartTicks == 0)
        {
            // Not in the middle of a lump (see DriftEdgeSettleSec).
            if (firstEdgeDueTicks == 0) firstEdgeDueTicks = nowTicks;
            if (!EdgeSettled(nowTicks, firstEdgeDueTicks)) return;
            // After a window thrown away for a stall: not until the arrivals have a line again (see ArrivalLineSettled).
            if (restartAfterStall && !ArrivalLineSettled(nowTicks)) return;
            restartAfterStall = false;
            // First call — anchor the measurement window. Defer the first rate update by
            // DriftFirstWindowSec so we get a stable initial measurement rather than one
            // based on the first few writes (which can be bursty during session arming).
            resamplerWindowStartTicks = nowTicks;
            resamplerWindowStartBytesWritten = WrittenBytesAt(nowTicks);
            resamplerWindowStartBytesOutput = bytesReadOutputForDriftEst;
            Interlocked.Exchange(ref longestWriteGapTicks, 0);
            longestReadGapTicks = 0;
            readsInWindow = 0;
            return;
        }

        var windowDuration = resamplerActivelyTracking ? DriftMeasurementWindowSec : DriftFirstWindowSec;
        var elapsedSec = (nowTicks - resamplerWindowStartTicks) / (double)Stopwatch.Frequency;
        if (elapsedSec < windowDuration) return;
        if (!EdgeSettled(nowTicks, resamplerWindowStartTicks + (long)(windowDuration * Stopwatch.Frequency))) return;

        var bytesWrittenNow = WrittenBytesAt(nowTicks);
        var bytesWrittenInWindow = bytesWrittenNow - resamplerWindowStartBytesWritten;
        var bytesOutputInWindow = bytesReadOutputForDriftEst - resamplerWindowStartBytesOutput;
        // The window's longest hole in its arrivals, counting one still open now (a stall the window ends
        // inside); taken and reset, so the next window starts fresh. See DriftStallGapSec.
        var lastWriteTicks = Interlocked.Read(ref lastWriteTicksForDrift);
        var openGapTicks = lastWriteTicks != 0 ? nowTicks - lastWriteTicks : 0;
        var longestGapTicks = Math.Max(Interlocked.Exchange(ref longestWriteGapTicks, 0), openGapTicks);
        var stalled = longestGapTicks > DriftStallGapSec * Stopwatch.Frequency;
        // ...or the output stopped reading (see DriftReadStallMinSec).
        var meanReadTicks = readsInWindow > 0 ? (nowTicks - resamplerWindowStartTicks) / (double)readsInWindow : 0;
        if (longestReadGapTicks > Math.Max(DriftReadStallMinSec * Stopwatch.Frequency, DriftReadStallFactor * meanReadTicks)) stalled = true;
        longestReadGapTicks = 0;
        readsInWindow = 0;

        if (bytesOutputInWindow > 0 && bytesWrittenInWindow > 0)
        {
            // Feed-forward: ratio = bytes_sender_produced / bytes_receiver_consumed over the
            // window. Above 1.0 = sender clock faster than receiver; below = slower. This is the
            // true crystal difference, independent of the resampler rate we apply, so it cleanly
            // cancels steady-state drift and the depth feedback below doesn't have to fight it.
            // For Ed's hardware (sender slower than receiver) this should settle ~0.9998.
            var measuredRatio = (double)bytesWrittenInWindow / bytesOutputInWindow;
            if (!stalled && measuredRatio >= DriftRatioMin && measuredRatio <= DriftRatioMax)
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
            // start-of-stream bursts. Keep the previous ratio rather than yanking. A window the
            // stream stalled in is rejected the same way, whatever it read (see DriftStallGapSec).
        }

        // Feedback: nudge the ring back toward the user's target depth. Without this the
        // resampler is a pure rate-matcher and a standing (bloated) buffer never drains — see the
        // DepthCorrectionSec / MaxDepthBias note above. depthError > 0 (too deep) biases the
        // applied rate UP so the resampler pulls more input per output and drains the buffer
        // faster; < 0 biases down to refill. Clamped + spread over DepthCorrectionSec so it's a
        // gentle, inaudible pitch trim, not a per-sample discontinuity. No-op until the
        // feed-forward has a valid measurement (smoothedRateRatio is meaningless before then).
        // It steers the buffer's mean depth, not the one sample at this edge (see DepthMeanSec).
        if (resamplerActivelyTracking) ApplyDepthCorrectedRate(targetLatencyMs, depthMeanFrames);

        // Anchor the next window - unless this one was thrown away for a stall. Then the next is started afresh, once the
        // arrivals have a line again: a count taken at the stall's end would still be read off the stream from before it
        // (see WrittenBytesAt).
        if (stalled)
        {
            resamplerWindowStartTicks = 0;
            firstEdgeDueTicks = 0;
            restartAfterStall = true;
            return;
        }
        resamplerWindowStartTicks = nowTicks;
        resamplerWindowStartBytesWritten = bytesWrittenNow;
        resamplerWindowStartBytesOutput = bytesReadOutputForDriftEst;
    }

    /// <summary>The last window was thrown away for a stall: the next waits for a line to read (see ArrivalLineSettled).</summary>
    private bool restartAfterStall;

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
            // This IS real audio, so it counts as a non-empty read. Without this the early return
            // skipped the concealment bookkeeping entirely, leaving a pending fade-in unresolved
            // across it and the consecutive-empty counter stale. 2026-08-23 audit, finding R6.
            consecutiveEmptyReads = 0;
            if (inUnderrunConcealment)
            {
                ApplyFadeIn(output, outFrames, (ConcealmentArtifact)concealmentArtifactRaw);
                inUnderrunConcealment = false;
            }
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
            {
                Interlocked.Increment(ref tuneBlockingUnderrunsTotal);
                if (!lastReadWasShort) Interlocked.Increment(ref dropoutGapsTotal);
                lastReadWasShort = true;
                gapRunMissingFrames += inputFramesNeeded - framesGot;
            }
            else
            {
                Interlocked.Increment(ref deviceGulpUnderrunsTotal);
            }
        }
        else
        {
            lastReadWasShort = false;
            if (gapRunMissingFrames > 0)
            {
                var gapMs = (int)Math.Round(gapRunMissingFrames * 1000.0 / MixSampleRate);
                if (gapMs > Volatile.Read(ref longestGapMs)) Volatile.Write(ref longestGapMs, gapMs);
                gapRunMissingFrames = 0;
            }
        }
        if (framesGot == 0)
        {
            Interlocked.Increment(ref concealmentFiresTotal);
            consecutiveEmptyReads++;
            emptyFramesInARow += outFrames;
        }
        else if (framesGot < inputFramesNeeded)
        {
            Interlocked.Increment(ref partialReadFiresTotal);
            consecutiveEmptyReads = 0;
            emptyFramesInARow = 0;
        }
        else
        {
            consecutiveEmptyReads = 0;
            emptyFramesInARow = 0;
        }
        // A real stop: empty for longer than LongGapRearmMs. Wait for the buffer to be back at its target before playing
        // on, as at a stream's start (NoteFramesQueuedLocal arms it again).
        if (emptyFramesInARow >= LongGapRearmMs * MixSampleRate / 1000 && playbackArmed)
        {
            playbackArmed = false;
            emptyFramesInARow = 0;
            Interlocked.Increment(ref longGapRearms);
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
        // only happen in pathological cases (just after session start, while the filter
        // delay line is still empty).
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
                    // White noise, each channel at its OWN last sample's amplitude. Random per channel —
                    // broader stereo image than mono noise, and avoids correlated content the brain
                    // can latch onto as a tone. Until 2026-09-30 both channels took the louder one's level,
                    // so a silent or quiet channel got a hiss as loud as the other channel's music - a
                    // sound that was never there, with nothing in that channel to cover it (found by the
                    // simulator's music-like signal; Ed approved the change).
                    inSpan[idx] = ((float)concealRng.NextDouble() * 2f - 1f) * Math.Abs(lastConcealSampleL) * g;
                    inSpan[idx + 1] = ((float)concealRng.NextDouble() * 2f - 1f) * Math.Abs(lastConcealSampleR) * g;
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

    /// <summary>The snap (see the DescentDepthBias block): with the buffer more than SnapAboveMs over a lowered target, skip
    /// everything above that with a crossfade - at a quiet moment if one comes within SnapWaitForQuietSec, else then.</summary>
    private void SnapDownIfFarAbove(int targetLatencyMs, long nowTicks)
    {
        var keepBytes = MillisecondsToBytes(targetLatencyMs + SnapAboveMs);
        var excessBytes = playout.BufferedBytes - keepBytes;
        if (excessBytes < MillisecondsToBytes(SnapMinSkipMs))
        {
            snapArmedTicks = 0;
            return;
        }
        if (snapArmedTicks == 0) snapArmedTicks = nowTicks;
        var quiet = playout.PeakAhead(SnapCrossfadeFrames * MixBytesPerFrame) <= SnapQuietPeak;
        if (!quiet && (nowTicks - snapArmedTicks) / (double)Stopwatch.Frequency < SnapWaitForQuietSec) return;
        var skipped = playout.SkipWithCrossfade(excessBytes, SnapCrossfadeFrames, SnapAlignFrames);
        snapArmedTicks = 0;
        if (skipped <= 0) return;
        Interlocked.Increment(ref snapCount);
        Interlocked.Add(ref snapSkippedBytes, skipped);
        if (quiet) Interlocked.Increment(ref snapsInQuiet);
        depthMeanFrames = -1;   // the mean starts again from the new depth
    }

    private static int MillisecondsToBytes(int milliseconds) =>
        Math.Max(MixBytesPerFrame, milliseconds * MixBytesPerSecond / 1000);
}
