using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using RemSound.Core;

namespace RemSound.Receiver;

/// <summary>
/// Drives N WASAPI output devices from a single shared <see cref="PlayoutEngine"/>. A master
/// producer task running on a Stopwatch-based 10 ms tick reads mixed audio from the engine and
/// fans it out to each device's <see cref="BufferedWaveProvider"/>; each <see cref="WasapiOut"/>
/// consumes from its own buffer at its own device clock.
///
/// Why a master producer loop instead of letting one WasapiOut drive PlayoutEngine.Read directly:
///   - With multiple WasapiOuts, each render thread would call Read independently and only one
///     output would get each frame; the others would starve.
///   - The producer loop runs at the canonical 48 kHz / 10 ms cadence, decoupled from any one
///     device's clock.
///
/// Per-device drift correction (2026-06-08): the producer feeds every device's buffer at the
/// receiver's Stopwatch clock, but each WASAPI device drains at its OWN crystal. Left alone, the
/// two clocks diverge by tens-to-hundreds of ppm and the buffer slowly fills (device slower) or
/// empties (device faster) — Andre's "desktop and laptop drift apart over time on WASAPI". Each
/// device is wrapped in a <see cref="DriftResamplingProvider"/> that sits on the PULL side
/// (between the buffer and the WasapiOut) and continuously stretches/compresses by the measured
/// clock ratio, holding the buffer level steady. This mirrors the proven per-sender corrector in
/// <see cref="SessionPlayout"/> exactly: resampler on the consumer side, output-driven, slow
/// rate-ratio measurement over a multi-second window. Crucially the producer still writes the RAW
/// mix into each buffer (no resampling on the input), so the buffer keeps its natural cushion —
/// the resampler only adjusts the rate at which the device drains it. An earlier attempt that
/// resampled on the PRODUCER side drained the cushion to zero and crackled; this does not.
///
/// Output-device set is diffed on <see cref="SetOutputDevices"/>: existing devices stay live,
/// removed ones are stopped, new ones are opened. No audio interruption to the unchanged ones.
/// </summary>
internal sealed class MultiOutputPlayout : IRenderBackend
{
    private const int MixSampleRate = 48000;
    private const int MixChannels = 2;
    private const int MixBytesPerFrame = MixChannels * sizeof(float);
    private const int FrameMs = 10;
    private const int FrameBytes = MixSampleRate * MixBytesPerFrame * FrameMs / 1000; // 3840 bytes
    private const int OutputBufferMs = 100; // per-device BufferedWaveProvider capacity

    // Source typed as IWaveProvider (rather than concrete PlayoutEngine) so the composite
    // backend can hand us a tee'd buffer instead of the engine directly. Single-backend usage
    // still passes the engine in unchanged.
    private readonly IWaveProvider source;
    private readonly Action<string>? onDiagnostic;
    private readonly object gate = new();
    private readonly Dictionary<string, OutputEntry> outputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly byte[] frameScratch = new byte[FrameBytes];
    private readonly WaveFormat sharedFormat = WaveFormat.CreateIeeeFloatWaveFormat(MixSampleRate, MixChannels);
    // Snapshot of the current per-device drift providers, rebuilt only when SetOutputDevices
    // changes the device set (rare — typically once per user action, minutes apart). The
    // producer loop reads this with a single volatile load per tick instead of taking the gate
    // and rebuilding the list on every 10 ms tick. Item 7 of RemSoundefficiency.md — eliminates
    // ~100 array allocations per second on the receive side. Empty array is a singleton via
    // Array.Empty<T>(), so the default value costs nothing. We feed each provider (which writes
    // the raw mix into its buffer AND counts the bytes for the drift measurement) rather than
    // touching the BufferedWaveProvider directly.
    private volatile DriftResamplingProvider[] outputSnapshot = Array.Empty<DriftResamplingProvider>();

    private CancellationTokenSource? cts;
    private Task? produceTask;

    public MultiOutputPlayout(IWaveProvider source, Action<string>? onDiagnostic = null)
    {
        this.source = source;
        this.onDiagnostic = onDiagnostic;
    }

    public bool IsRunning => produceTask is { IsCompleted: false };

    /// <summary>
    /// Friendly names of currently-active output devices, comma-joined. "(none)" when no
    /// device is enabled. Used by the snapshot log column.
    /// </summary>
    public string ActiveDeviceSummary
    {
        get
        {
            lock (gate)
            {
                if (outputs.Count == 0) return "(none)";
                if (outputs.Count <= 3) return string.Join(", ", outputs.Values.Select(o => o.Name));
                return $"({outputs.Count} outputs)";
            }
        }
    }

    public IReadOnlyList<string> ActiveDeviceIds
    {
        get { lock (gate) return outputs.Keys.ToList(); }
    }

    public void Start()
    {
        lock (gate)
        {
            if (IsRunning) return;
            cts = new CancellationTokenSource();
            produceTask = Task.Run(() => ProduceLoop(cts.Token));
            onDiagnostic?.Invoke("multi-output producer started");
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            try { cts?.Cancel(); } catch { /* ignore */ }
            try { produceTask?.Wait(TimeSpan.FromMilliseconds(500)); } catch { /* ignore */ }
            cts?.Dispose();
            cts = null;
            produceTask = null;

            foreach (var o in outputs.Values) DisposeOutput(o);
            outputs.Clear();
            // Reset the snapshot the producer loop reads so any subsequent Start sees the
            // empty state cleanly (not a stale snapshot from the previous session). Empty
            // array is a cached singleton, no allocation.
            outputSnapshot = Array.Empty<DriftResamplingProvider>();
        }
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Live-update of the output device set. Devices already present stay live (no audio
    /// interruption); removed devices are stopped + disposed; new devices are opened. Caller
    /// supplies device IDs (from MMDeviceEnumerator). An empty set means "render to nothing"
    /// — the producer loop keeps running so receive-side mixing/auto-tune state stays alive.
    /// </summary>
    public void SetOutputDevices(IReadOnlyList<string> deviceIds)
    {
        lock (gate)
        {
            var desired = new HashSet<string>(deviceIds, StringComparer.OrdinalIgnoreCase);

            // Remove outputs no longer wanted.
            foreach (var id in outputs.Keys.Where(k => !desired.Contains(k)).ToList())
            {
                if (outputs.Remove(id, out var o))
                {
                    onDiagnostic?.Invoke($"output removed: \"{o.Name}\"");
                    DisposeOutput(o);
                }
            }

            // Add new outputs.
            using var enumerator = new MMDeviceEnumerator();
            foreach (var id in deviceIds)
            {
                if (outputs.TryGetValue(id, out var existing))
                {
                    if (!existing.Faulted) continue; // already live — leave it running, no audio break
                    // Device faulted mid-stream (unplugged) yet still in the desired set. Tear the dead
                    // output down so the open below re-creates it. SAFETY NET only: normally an unplug
                    // also clears the card's tick, so the remove loop above drops it and replug re-opens
                    // it fresh from the remembered selection (App side) — this branch just covers a fault
                    // where the same id is still desired (e.g. a transient WASAPI invalidation with no
                    // device-state change). If the device is still gone the open fails and it stays absent.
                    onDiagnostic?.Invoke($"output \"{existing.Name}\" faulted — re-opening");
                    outputs.Remove(id);
                    DisposeOutput(existing);
                }
                MMDevice? device = null;
                WasapiOut? wasapi = null;
                try
                {
                    device = enumerator.GetDevice(id);
                    var name = device.FriendlyName;
                    var buffer = new BufferedWaveProvider(sharedFormat)
                    {
                        ReadFully = true,
                        DiscardOnBufferOverflow = true,
                        BufferDuration = TimeSpan.FromMilliseconds(OutputBufferMs),
                    };
                    // Per-device drift corrector sits between the buffer and the WasapiOut. The
                    // device pulls THROUGH it; it pulls the matching amount from `buffer` and
                    // resamples by the measured clock ratio. The producer writes the raw mix
                    // into `buffer` via drift.Feed (which also counts bytes for the measurement).
                    var drift = new DriftResamplingProvider(buffer, name,
                        msg => onDiagnostic?.Invoke($"drift: {msg}"));
                    // Output device buffer. Request 5 ms; shared-mode WASAPI clamps it up to the
                    // device's minimum period (~10 ms on tested hardware, 2026-06-08) — but ~10 ms
                    // is still ~5 ms tighter than the old 15 ms, a free latency win. The per-device
                    // drift corrector keeps this buffer fed from its held card-sized cushion (≈12 ms
                    // on a typical card), so the smaller endpoint reserve doesn't risk underruns.
                    wasapi = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 5);
                    var entry = new OutputEntry { Device = device, Output = wasapi, Buffer = buffer, Drift = drift, Name = name };
                    // Notice the device dying mid-stream (USB card unplugged → WASAPI invalidates the
                    // endpoint and raises PlaybackStopped WITH an exception). Just flag it — never take
                    // the gate or dispose from here: this can fire on the device thread during our own
                    // Stop()/DisposeOutput, so doing real work here could deadlock. The next
                    // SetOutputDevices (the hot-plug notifier fires one on unplug AND replug) sees the
                    // flag and tears the dead entry down so the device can be re-opened. A clean stop
                    // (no exception — we asked for it) is ignored: the entry is being removed anyway.
                    wasapi.PlaybackStopped += (_, stopArgs) =>
                    {
                        if (stopArgs.Exception is not { } stopEx) return;
                        entry.Faulted = true;
                        onDiagnostic?.Invoke($"output \"{name}\" lost the device: {stopEx.GetType().Name}: {stopEx.Message}");
                    };
                    wasapi.Init(drift);
                    wasapi.Play();
                    outputs[id] = entry;
                    onDiagnostic?.Invoke($"output added: \"{name}\"");
                }
                catch (Exception ex)
                {
                    onDiagnostic?.Invoke($"failed to open output \"{id}\": {ex.GetType().Name}: {ex.Message}");
                    try { wasapi?.Dispose(); } catch { /* ignore */ }
                    try { device?.Dispose(); } catch { /* ignore */ }
                }
            }

            // Refresh the snapshot the producer loop reads. Under the gate, so the producer
            // sees a consistent view; once published via the volatile field, the loop reads
            // it without taking the gate every tick. Empty case uses the cached singleton
            // so it's allocation-free. Item 7 of RemSoundefficiency.md.
            outputSnapshot = outputs.Count == 0
                ? Array.Empty<DriftResamplingProvider>()
                : outputs.Values.Select(o => o.Drift).ToArray();
        }
    }

    private static void DisposeOutput(OutputEntry o)
    {
        try { o.Output.Stop(); } catch { /* ignore */ }
        try { o.Output.Dispose(); } catch { /* ignore */ }
        try { o.Device.Dispose(); } catch { /* ignore */ }
    }

    private async Task ProduceLoop(CancellationToken ct)
    {
        // Pro Audio MMCSS for the producer thread — it's the one feeding all WASAPI outputs.
        using var threadBoost = new WindowsAudioThreadBoost("Pro Audio");

        var ticksPerFrame = Stopwatch.Frequency * FrameMs / 1000;
        var nextTickStopwatch = Stopwatch.GetTimestamp() + ticksPerFrame;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = Stopwatch.GetTimestamp();
                if (nextTickStopwatch > now)
                {
                    var sleepMs = (int)Math.Clamp((nextTickStopwatch - now) * 1000 / Stopwatch.Frequency, 1, 50);
                    // Item 6 of RemSoundefficiency.md — see matching change in
                    // MixingEngine.MixLoop for the rationale. WaitOne is allocation-free
                    // and semantically equivalent to WaitAny on a 1-element array.
                    if (ct.WaitHandle.WaitOne(sleepMs)) break;
                    continue;
                }

                if (now - nextTickStopwatch > ticksPerFrame * 4)
                {
                    nextTickStopwatch = now;
                }
                nextTickStopwatch += ticksPerFrame;

                // Read the pre-built snapshot. Volatile load — no lock, no allocation per
                // tick. SetOutputDevices rebuilds the snapshot under the gate whenever the
                // device set changes (rare event), so reads here see a consistent view.
                // Skip the source.Read entirely when no outputs are ticked: in BothIndependent
                // mode the source is shared between WASAPI and ASIO, and pulling here when
                // WASAPI has nothing ticked would consume PlayoutEngine audio ahead of the
                // ASIO consumer. Pre-2026-05-23 this whole block ran under `lock (gate)` and
                // rebuilt the array on every tick — fixed as item 7 of RemSoundefficiency.md.
                var targets = outputSnapshot;
                if (targets.Length == 0) continue;

                var produced = source.Read(frameScratch, 0, FrameBytes);
                if (produced <= 0) continue;

                // Feed the RAW mix into each device's buffer (no resampling here — the buffer
                // keeps its natural cushion). Feed also counts the bytes for that device's
                // drift measurement. Per-output failure shouldn't kill the loop.
                foreach (var drift in targets)
                {
                    try { drift.Feed(frameScratch, 0, produced); }
                    catch { /* ignore */ }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                onDiagnostic?.Invoke($"producer loop error: {ex.GetType().Name}: {ex.Message}");
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }
    }

    private sealed class OutputEntry
    {
        public required MMDevice Device { get; init; }
        public required WasapiOut Output { get; init; }
        public required BufferedWaveProvider Buffer { get; init; }
        public required DriftResamplingProvider Drift { get; init; }
        public required string Name { get; init; }
        // Set true (off-thread, from WasapiOut.PlaybackStopped) when this device dies mid-stream —
        // typically a USB card unplugged, which invalidates the WASAPI endpoint. SetOutputDevices
        // reads it to know the entry is dead and must be torn down + re-opened rather than skipped.
        // Volatile: written on the WASAPI thread, read under the gate without a shared write lock.
        public volatile bool Faulted;
    }

    /// <summary>
    /// Sits between a device's <see cref="BufferedWaveProvider"/> and its <see cref="WasapiOut"/>.
    /// The WasapiOut render thread pulls from THIS (not the buffer directly); we pull the matching
    /// amount of audio from the buffer and run it through a continuous fixed-ratio resampler whose
    /// rate is the measured (producer-feed ÷ device-drain) clock ratio over a multi-second window.
    /// That holds the buffer level steady against per-device clock drift.
    ///
    /// This deliberately mirrors <see cref="SessionPlayout"/>'s Phase-4 corrector: output-driven
    /// (ResamplePrepare asks how many input frames it needs for N output frames), linear-interp
    /// mode, 10 s window, 70/30 smoothing, ±5 % sanity clamp. Resampling on the PULL side keeps
    /// the producer's raw feed (and therefore the buffer's natural cushion) intact — the earlier
    /// producer-side attempt resampled the input and starved the cushion to zero.
    ///
    /// Threading: <see cref="Feed"/> runs on the producer thread; <see cref="Read"/> (and the
    /// resampler + rate update) run on the WasapiOut render thread. The only shared state is
    /// <c>producerFedBytes</c>, guarded by Interlocked. The resampler itself is touched only on
    /// the render thread.
    /// </summary>
    private sealed class DriftResamplingProvider : IWaveProvider
    {
        // Mirror SessionPlayout's proven constants for the clock-ratio feed-forward.
        private const double DriftMeasurementWindowSec = 10.0;
        private const double DriftRatioSmoothingNew = 0.30;
        private const double DriftRatioMin = 0.95;
        private const double DriftRatioMax = 1.05;
        // Feedback: steer the buffer toward a cushion. Pure rate-matching holds the buffer wherever
        // the start-up transient left it (~50 ms and climbing in the field) — SessionPlayout gets
        // away without this because it ARMS at target and has a click-trim net; the device buffer
        // has neither, so it needs an explicit depth term. The correction is tiny (≤0.3 % rate,
        // spread over seconds): a sub-audible pitch nudge, never a click.
        //
        // The cushion is sized to the CARD, not hardcoded. A device can't hold a buffer below one
        // of its own WASAPI pulls — a 10 ms-period card sawtooths by 10 ms, an 18 ms Realtek by 18 —
        // so the target is the measured pull size × a small margin: a 10 ms card lands at 12 ms (its
        // long-proven value), an 18 ms card at ~22. Crucially this READS the card and HOLDS: it is
        // NOT a load-reactive loop, so it never climbs on a CPU/network spike and drops when calm.
        // The pull is the WINDOW AVERAGE (one coalesced double-pull can't move it), and a hysteresis
        // band means only a genuine change in the card's pull size ever shifts the target.
        private const double TargetGulpMultiple = 1.2;  // cushion ≈ this × the card's pull size
        private const int MinTargetDepthMs = 8;         // floor for a tiny-pull device
        private const int MaxTargetDepthMs = 50;        // cap so a pathological pull can't run away
        private const int TargetHysteresisMs = 2;       // only move the target on a real ≥2 ms shift
        private const double DepthCorrectionSec = 15.0; // correct a depth error over ~this long
        private const double MaxDepthBias = 0.003;      // cap the depth nudge at 0.3 % rate

        private readonly BufferedWaveProvider buffer;
        private readonly string name;
        private readonly Action<string>? onDiagnostic;
        private readonly WdlResampler resampler;

        public WaveFormat WaveFormat => buffer.WaveFormat;

        // Drift measurement. producerFedBytes is incremented by the producer thread in Feed;
        // deviceDrainedBytes is incremented by the render thread (us) in Read. Their ratio over
        // a multi-second window is the receiver-feed-rate ÷ device-drain-rate — exactly the
        // ratio the resampler needs to hold the buffer level.
        private long producerFedBytes;     // Interlocked (producer writes, render reads)
        private long deviceDrainedBytes;    // render thread only
        private long windowStartTicks;
        private long windowStartFed;
        private long windowStartDrained;
        private double smoothedRatio = 1.0;
        private bool tracking;
        private bool firstWindowDone;
        // Adaptive-but-stable target. pullSumBytes/pullCount accumulate the card's pull sizes over a
        // window; targetMs is set from their average (× the margin) and then HELD — the hysteresis
        // band keeps it from flitting. Defaults to 12 ms until the first window measures the card.
        private long pullSumBytes;
        private long pullCount;
        private int targetMs = 12;
        private double lastGulpMs;

        // Scratch — grown lazily, persists across calls so the hot path doesn't allocate.
        private byte[] inputBytes = new byte[16384];
        private float[] outputScratch = new float[4096];

        public DriftResamplingProvider(BufferedWaveProvider buffer, string name, Action<string>? onDiagnostic)
        {
            this.buffer = buffer;
            this.name = name;
            this.onDiagnostic = onDiagnostic;
            // interp=true, filtercnt=0 → linear-interpolation mode, plenty for sub-1000-ppm
            // corrections. SetFeedMode(false) = output-driven. Start at 1:1; the first window's
            // measurement replaces it.
            resampler = new WdlResampler();
            resampler.SetMode(interp: true, filtercnt: 0, sinc: false);
            resampler.SetFeedMode(false);
            resampler.SetRates(MixSampleRate, MixSampleRate);
        }

        /// <summary>Producer thread: write the raw mix into the device buffer and count the
        /// bytes for the drift measurement. No resampling here — the buffer keeps its cushion.</summary>
        public void Feed(byte[] data, int offset, int count)
        {
            buffer.AddSamples(data, offset, count);
            Interlocked.Add(ref producerFedBytes, count);
        }

        /// <summary>Render thread: the WasapiOut pulls <paramref name="count"/> bytes. We pull
        /// the resampler's required input from the buffer and produce exactly that many output
        /// bytes (zero-padding any shortfall, so WASAPI never sees a short read).</summary>
        public int Read(byte[] outBuffer, int offset, int count)
        {
            var outFrames = count / MixBytesPerFrame;
            if (outFrames <= 0) return 0;

            // Sample the card's pull size for the adaptive target (see UpdateRatioIfDue). Averaged,
            // so an occasional coalesced double-pull can't distort it.
            pullSumBytes += count;
            pullCount++;

            UpdateRatioIfDue();

            var inputFramesNeeded = resampler.ResamplePrepare(outFrames, MixChannels, out var inBuf, out var inBufOff);
            if (inputFramesNeeded > 0)
            {
                var inputFloats = inputFramesNeeded * MixChannels;
                var inputByteCount = inputFloats * sizeof(float);
                if (inputBytes.Length < inputByteCount) inputBytes = new byte[inputByteCount];
                // BufferedWaveProvider has ReadFully=true, so this returns inputByteCount,
                // zero-padding if the buffer is momentarily short (a brief underrun produces
                // silence, not a glitch — same as the pre-corrector behaviour).
                var got = buffer.Read(inputBytes, 0, inputByteCount);
                var gotFloats = got / sizeof(float);
                MemoryMarshal.Cast<byte, float>(inputBytes.AsSpan(0, got)).CopyTo(inBuf.AsSpan(inBufOff, gotFloats));
                if (gotFloats < inputFloats) inBuf.AsSpan(inBufOff + gotFloats, inputFloats - gotFloats).Clear();
            }

            var outFloats = outFrames * MixChannels;
            if (outputScratch.Length < outFloats) outputScratch = new float[outFloats];
            var produced = resampler.ResampleOut(outputScratch, 0, inputFramesNeeded, outFrames, MixChannels);
            var producedFloats = produced * MixChannels;

            var outSpan = MemoryMarshal.Cast<byte, float>(outBuffer.AsSpan(offset, count));
            var copy = Math.Min(producedFloats, outSpan.Length);
            for (var i = 0; i < copy; i++)
            {
                var v = outputScratch[i];
                // Safety clamp — a NaN or out-of-range sample would otherwise be a loud pop.
                if (v > 1f) v = 1f;
                else if (v < -1f) v = -1f;
                else if (float.IsNaN(v)) v = 0f;
                outSpan[i] = v;
            }
            // Zero-fill any shortfall (startup priming of the resampler delay line, mainly).
            if (copy < outSpan.Length) outSpan.Slice(copy).Clear();

            // Count the device's consumption (always the full requested amount — WASAPI took
            // `count` bytes regardless of how much real audio backed it). Matches SessionPlayout.
            deviceDrainedBytes += count;
            return count;
        }

        /// <summary>If the measurement window has elapsed, recompute the clock ratio (feed-forward)
        /// and the depth-restoring nudge (feedback), combine them, and push to the resampler.
        /// Render thread only.</summary>
        private void UpdateRatioIfDue()
        {
            var now = Stopwatch.GetTimestamp();
            if (windowStartTicks == 0)
            {
                windowStartTicks = now;
                windowStartFed = Interlocked.Read(ref producerFedBytes);
                windowStartDrained = deviceDrainedBytes;
                return;
            }

            var elapsedSec = (now - windowStartTicks) / (double)Stopwatch.Frequency;
            if (elapsedSec < DriftMeasurementWindowSec) return;

            var fedNow = Interlocked.Read(ref producerFedBytes);
            var fedDelta = fedNow - windowStartFed;
            var drainedDelta = deviceDrainedBytes - windowStartDrained;

            // Re-anchor immediately so every early return below still advances the window cleanly.
            windowStartTicks = now;
            windowStartFed = fedNow;
            windowStartDrained = deviceDrainedBytes;

            // Size the target to THIS card from its average pull, then hold it. Done on every path
            // (including the discarded warm-up window) so the next window's average starts clean.
            var avgPullBytes = pullCount > 0 ? pullSumBytes / pullCount : 0;
            pullSumBytes = 0;
            pullCount = 0;
            if (avgPullBytes > 0)
            {
                lastGulpMs = avgPullBytes / (double)MixBytesPerFrame * 1000.0 / MixSampleRate;
                var candidateMs = (int)Math.Round(
                    Math.Clamp(lastGulpMs * TargetGulpMultiple, MinTargetDepthMs, MaxTargetDepthMs));
                // Hysteresis: only move on a genuine ≥2 ms change in the card's pull, so tiny
                // averaging wobble never nudges the latency — it settles once and stays put.
                if (Math.Abs(candidateMs - targetMs) >= TargetHysteresisMs) targetMs = candidateMs;
            }

            // Discard the FIRST completed window. WASAPI primes its endpoint buffer at start-up,
            // which inflates the device-drain count for that window and reads as a large bogus
            // ppm (−1199 ppm observed) that shoves the buffer off target. Start measuring from
            // the next window, by which point start-up is done.
            if (!firstWindowDone) { firstWindowDone = true; return; }

            if (fedDelta <= 0 || drainedDelta <= 0) return;

            // Feed-forward: the true crystal ratio (system feed ÷ device drain). Independent of
            // the resampler rate we apply, so it's a clean measurement of the clock difference.
            // Cancels steady-state drift so the feedback term doesn't have to fight a constant.
            var measured = (double)fedDelta / drainedDelta;
            if (measured >= DriftRatioMin && measured <= DriftRatioMax)
            {
                smoothedRatio = tracking
                    ? (1.0 - DriftRatioSmoothingNew) * smoothedRatio + DriftRatioSmoothingNew * measured
                    : measured;
                tracking = true;
            }
            if (!tracking) return; // nothing valid measured yet — don't touch the rate.

            // Feedback: nudge the buffer toward the adaptive target (targetMs). depthError > 0 = too deep → bias
            // the rate UP so the resampler pulls more per output and drains the buffer faster;
            // < 0 = too shallow → bias down. Clamped + spread over DepthCorrectionSec so it's a
            // gentle, inaudible pitch trim, not a per-sample discontinuity.
            var depthFrames = buffer.BufferedBytes / MixBytesPerFrame;
            var targetFrames = targetMs * MixSampleRate / 1000;
            var depthError = depthFrames - targetFrames;
            var depthCorrection = Math.Clamp(
                depthError / (DepthCorrectionSec * MixSampleRate),
                -MaxDepthBias, MaxDepthBias);

            var appliedRatio = smoothedRatio + depthCorrection;
            resampler.SetRates(MixSampleRate * appliedRatio, MixSampleRate);

            var depthMs = buffer.BufferedBytes / MixBytesPerFrame * 1000 / MixSampleRate;
            var clockPpm = (smoothedRatio - 1.0) * 1_000_000.0;
            var corrPpm = depthCorrection * 1_000_000.0;
            onDiagnostic?.Invoke(
                $"\"{name}\": clock={smoothedRatio:F6} ({clockPpm:+0;-0}ppm) depthMs={depthMs} " +
                $"target={targetMs} gulpMs={lastGulpMs:F0} corr={corrPpm:+0;-0}ppm applied={appliedRatio:F6}");
        }
    }
}
