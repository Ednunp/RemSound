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
    /// <summary>The buffer size we ASK each output device for. Shared-mode WASAPI will not go below
    /// the device's own engine period, so the real figure is the larger of the two — see
    /// DeviceLatencyProbe. 2026-08-24.</summary>
    private const int OutputRequestedLatencyMs = 5;

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
    /// Is any output sitting dead, waiting to be re-opened?
    ///
    /// <para>An output that WASAPI invalidates mid-stream is flagged <c>Faulted</c> and re-opened by
    /// the next <c>SetOutputDevices</c>. That works when the device was UNPLUGGED, because the
    /// hot-plug notifier fires one. It does NOT work when the endpoint is invalidated while the
    /// device stays present — changing anything in its Windows sound-control-panel properties, a
    /// format change, a driver reset, a resume from hibernate. Nothing is added or removed, so no
    /// notification arrives, so nothing calls SetOutputDevices, and the dead output stays dead.</para>
    ///
    /// <para>Ed hit exactly that on 2026-08-26: he changed a level on his Roger On in the Windows
    /// sound panel, closed it, and had no sound until he unticked the device and ticked it again —
    /// which is just a manual SetOutputDevices, the very call the recovery was waiting for. The
    /// per-second tick now watches this and re-applies on its own.</para>
    /// </summary>
    public bool HasFaultedOutput
    {
        get
        {
            lock (gate)
            {
                foreach (var entry in outputs.Values)
                {
                    if (entry.Faulted) return true;
                }
                return false;
            }
        }
    }

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

    /// <summary>The WORST amount of audio queued in a device buffer right now, across the live
    /// outputs. See DriftResamplingProvider.BufferedMs — this is a real stage the estimate used to
    /// skip. Zero when nothing is open.</summary>
    private double WorstQueueMs
    {
        get
        {
            lock (gate)
            {
                var worst = 0.0;
                foreach (var o in outputs.Values) if (o.Drift.BufferedMs > worst) worst = o.Drift.BufferedMs;
                return worst;
            }
        }
    }

    /// <summary>This backend IS the WASAPI lane (and the only lane in a WASAPI-only setup, where
    /// sessions are tagged Mixed). It has nothing to say about the ASIO lane. See
    /// <see cref="IRenderBackend.ReportedOutputLatencyMsFor"/>.</summary>
    public double ReportedOutputLatencyMsFor(RenderRoute route) =>
        route == RenderRoute.AsioLane ? 0 : WorstReportedLatencyMs;

    /// <inheritdoc cref="ReportedOutputLatencyMsFor"/>
    public double OutputQueueMsFor(RenderRoute route) =>
        route == RenderRoute.AsioLane ? 0 : WorstQueueMs;

    /// <summary>One reading per live WASAPI output, for the long-run report. Reads the same lock-free
    /// snapshot the render thread uses, so it never blocks audio. See <see cref="WasapiOutputStage"/>
    /// for why this stage in particular is worth watching over hours.</summary>
    public IReadOnlyList<WasapiOutputStage> OutputStages()
    {
        var snap = outputSnapshot;
        if (snap.Length == 0) return Array.Empty<WasapiOutputStage>();
        var readings = new WasapiOutputStage[snap.Length];
        for (var i = 0; i < snap.Length; i++) readings[i] = snap[i].Snapshot();
        return readings;
    }

    /// <summary>The WORST reported latency among this backend's live outputs, because a listener
    /// hears the slowest one. Private: the only way OUT of this class is per lane, so nobody can ask
    /// a lane-free question. See <see cref="IRenderBackend.ReportedOutputLatencyMsFor"/>.</summary>
    private double WorstReportedLatencyMs
    {
        get
        {
            lock (gate)
            {
                var worst = 0.0;
                foreach (var o in outputs.Values) if (o.ReportedLatencyMs > worst) worst = o.ReportedLatencyMs;
                return worst;
            }
        }
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
                    // Ask the DEVICE how long playback takes, rather than doubling a callback gap and
                    // clamping it up to 10 ms. Once, here, off the audio thread. 2026-08-24.
                    var reportedMs = DeviceLatencyProbe.RenderLatencyMs(device, OutputRequestedLatencyMs);
                    onDiagnostic?.Invoke(reportedMs > 0
                        ? $"output \"{name}\" reports {reportedMs:0.0} ms of playback latency (engine period {DeviceLatencyProbe.EnginePeriodMs(device):0.0} ms, doubled for the buffer it plays while we fill the next)"
                        : $"output \"{name}\" would not report its period — the latency estimate falls back to its own figure");
                    var entry = new OutputEntry { Device = device, Output = wasapi, Buffer = buffer, Drift = drift, Name = name, ReportedLatencyMs = reportedMs };
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

    /// <summary>The producer tick that feeds every WASAPI output. NOT async, and it must stay that
    /// way — see the matching note on MixingEngine.MixLoop. The MMCSS "Pro Audio" boost belongs to
    /// this thread; an await would move the loop off it and leave it unboosted. Wait with
    /// <c>ct.WaitHandle.WaitOne</c>, never <c>await Task.Delay</c>.</summary>
    private void ProduceLoop(CancellationToken ct)
    {
        // Pro Audio MMCSS for the producer thread — it's the one feeding all WASAPI outputs.
        using var threadBoost = new WindowsAudioThreadBoost("Pro Audio");
        // Hold the system timer at 1 ms for as long as we're producing. This loop paces itself with
        // WaitHandle.WaitOne below, whose granularity is the system timer — coarse (~15.6 ms) by
        // default, which slips the 10 ms feed to ~16–31 ms and delivers audio to the outputs in
        // chunky bursts (the receive-render chunkiness behind Andre's dropouts/lag). The MMCSS boost
        // above raises priority but NOT timer granularity. Deliberately NOT gated behind the opt-in
        // Priority mode — the audio path must run smoothly without a user toggle. 2026-06-15.
        using var timerResolution = new SystemTimerResolution(1);

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
                // SYNCHRONOUS wait — see the matching change in MixingEngine.MixLoop. This was
                // `await Task.Delay(50, ct)`, and because MMCSS characteristics are per-thread, the
                // first await moved the loop off the "Pro Audio" thread the boost was applied to and
                // left it unboosted for the rest of the session. This is the loop that feeds every
                // WASAPI output device, so a silent demotion here is heard as the output turning
                // lumpy hours into a session. 2026-08-23 audit, finding S5.
                if (ct.WaitHandle.WaitOne(50)) break;
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
        /// <summary>What this device says its playback latency is, read once at open. Zero when it
        /// would not say. See IRenderBackend.ReportedOutputLatencyMs.</summary>
        public double ReportedLatencyMs { get; init; }
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
        // The clock-ratio loop itself now lives in Core as DriftRatioTracker: the ten-second window,
        // the discarded first window, the ±5 % sanity band, the 70/30 smoothing and the bounded depth
        // term. It was lifted out of THIS class on 2026-09-07 because two other places had grown their
        // own copy of the same arithmetic — the plugin sender's second-DAW lane and the new
        // capture-source corrector — and the newest copy had been written without the parts this one
        // had learned the hard way. Everything below is what is genuinely local to a WASAPI card.
        //
        // Feedback: steer the buffer toward a cushion. Pure rate-matching holds the buffer wherever
        // the start-up transient left it (~50 ms and climbing in the field). SessionPlayout was
        // originally thought to get away without this (it ARMS at target and has a click-trim net),
        // but the same standing-bloat showed up on its ring over a long WASAPI session, so as of
        // 2026-06-12 it carries the identical depth term. The correction is tiny (≤0.3 % rate,
        // spread over seconds): a sub-audible pitch nudge, never a click.
        //
        // The cushion is sized to the CARD, not hardcoded. A device can't hold a buffer below one
        // of its own WASAPI pulls — a 10 ms-period card sawtooths by 10 ms, an 18 ms Realtek by 18 —
        // so the target is the measured pull size × a small margin: a 10 ms card lands at 12 ms (its
        // long-proven value), an 18 ms card at ~22. Crucially this READS the card and HOLDS: it is
        // NOT a load-reactive loop, so it never climbs on a CPU/network spike and drops when calm.
        // The pull is the WINDOW AVERAGE (one coalesced double-pull can't move it), and a hysteresis
        // band means only a genuine change in the card's pull size ever shifts the target.
        // The cushion-sizing numbers (margin, floor, cap, hysteresis) are AdaptiveCushionTarget's.
        // The correction over time (DepthCorrectionSec) and its 0.3 % cap (MaxDepthBias) are the
        // tracker's, shared with every other caller — see DriftRatioTracker.

        private readonly BufferedWaveProvider buffer;
        private readonly string name;
        private readonly Action<string>? onDiagnostic;
        private readonly WdlResampler resampler;

        public WaveFormat WaveFormat => buffer.WaveFormat;

        /// <summary>How much audio is sitting in THIS device's buffer right now, in milliseconds.
        ///
        /// <para>A whole stage of the journey that the latency estimate never counted. Audio leaves
        /// PlayoutEngine, waits HERE while the drift corrector holds the cushion at its target
        /// (about 12 ms on a 10 ms card), and only then reaches the sound card. Ed's 2026-08-23 log
        /// shows this sitting at 10 to 23 ms all session while the estimate ignored it entirely —
        /// the same class of omission as the capture stage that was found missing before.
        /// MEASURED, not derived. ASIO has no equivalent: it pulls straight from the engine, which
        /// is a real reason ASIO is tighter and not just a claim. 2026-08-24.</summary>
        public double BufferedMs => buffer.BufferedBytes / (double)MixBytesPerFrame * 1000.0 / MixSampleRate;

        // Drift measurement. producerFedBytes is incremented by the producer thread in Feed;
        // deviceDrainedBytes is incremented by the render thread (us) in Read. Their ratio over
        // a multi-second window is the receiver-feed-rate ÷ device-drain-rate — exactly the
        // ratio the resampler needs to hold the buffer level.
        private long producerFedBytes;     // Interlocked (producer writes, render reads)
        private long deviceDrainedBytes;    // render thread only
        // The acquisition gate is on HERE and nowhere else: this stage's endpoint is the thing that
        // re-opens, and a freshly opened WASAPI endpoint swings for about thirty seconds before it reads
        // its real clock. See DriftRatioTracker.acquireAgreementPpm for the readings that proved it.
        private readonly DriftRatioTracker tracker = new(MixSampleRate, acquireAgreementPpm: DriftRatioTracker.DeviceAcquisitionAgreementPpm);
        // Adaptive-but-stable target. pullSumBytes/pullCount accumulate the card's pull sizes over a
        // window; targetMs is set from their average (× the margin) and then HELD — the hysteresis
        // band keeps it from flitting. Defaults to 12 ms until the first window measures the card.
        private long pullSumBytes;
        private long pullCount;
        private int targetMs = AdaptiveCushionTarget.DefaultMs;
        private double lastGulpMs;
        private double lastClockPpm;
        private double lastCorrPpm;

        /// <summary>A reading of this stage for the long-run report. These numbers used to exist only
        /// inside a ten-second log line, and only while a receiver was running — so on a send-only
        /// machine, or between lines, they were unavailable. This is the WASAPI-only stage, and the
        /// one Ed's overnight slowdown must live in, so it has to be readable on demand.</summary>
        public WasapiOutputStage Snapshot() =>
            new(name, BufferedMs, targetMs, lastGulpMs, lastClockPpm, lastCorrPpm);

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
            // The tracker owns the window and re-anchors it here, so every early return below still
            // advances the window cleanly.
            if (!tracker.WindowClosed(Interlocked.Read(ref producerFedBytes), deviceDrainedBytes)) return;

            // Size the target to THIS card from its average pull, then hold it. Done on every path
            // (including the discarded warm-up window) so the next window's average starts clean.
            // This is exactly why the tracker's update is split in two: the target does not exist until
            // the window has closed, and the depth term must use it in the SAME window, not the next.
            var avgPullBytes = pullCount > 0 ? pullSumBytes / pullCount : 0;
            pullSumBytes = 0;
            pullCount = 0;
            if (avgPullBytes > 0)
            {
                lastGulpMs = avgPullBytes / (double)MixBytesPerFrame * 1000.0 / MixSampleRate;
                // The rule itself lives in AdaptiveCushionTarget, as a seam rather than three lines
                // here — it is the only thing in the whole receive path that decides how much latency
                // this stage deliberately holds, and inline it could only be tested by running a real
                // card for hours. Extracted, a soak can prove it settles and then stops moving.
                targetMs = AdaptiveCushionTarget.Next(targetMs, lastGulpMs);
            }

            // Feed-forward against the true crystal ratio (system feed ÷ device drain), plus the
            // feedback nudge toward the adaptive target: depth above target biases the rate UP so the
            // resampler pulls more per output and drains the buffer faster, below target biases it
            // down. The tracker discards the FIRST completed window — WASAPI primes its endpoint
            // buffer at start-up, which inflates the device-drain count and reads as a large bogus ppm
            // (−1199 ppm observed) that would shove the buffer off target — and it discards, rather
            // than clamps, any reading outside the sanity band. Nothing is applied on a guess.
            if (!tracker.ApplyMeasurement(buffer.BufferedBytes / MixBytesPerFrame, targetMs * MixSampleRate / 1000)) return;

            var appliedRatio = tracker.AppliedRatio;
            resampler.SetRates(MixSampleRate * appliedRatio, MixSampleRate);

            var depthMs = buffer.BufferedBytes / MixBytesPerFrame * 1000 / MixSampleRate;
            var smoothedRatio = tracker.ClockRatio;
            var clockPpm = (smoothedRatio - 1.0) * 1_000_000.0;
            var corrPpm = tracker.DepthCorrection * 1_000_000.0;
            lastClockPpm = clockPpm;
            lastCorrPpm = corrPpm;
            onDiagnostic?.Invoke(
                $"\"{name}\": clock={smoothedRatio:F6} ({clockPpm:+0;-0}ppm) depthMs={depthMs} " +
                $"target={targetMs} gulpMs={lastGulpMs:F0} corr={corrPpm:+0;-0}ppm applied={appliedRatio:F6}");
        }
    }
}
