using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RemSound.Core;

namespace RemSound.Sender;

/// <summary>
/// Owns N <see cref="CaptureSource"/> objects + an NAudio <see cref="MixingSampleProvider"/> +
/// a 10 ms mix tick. Each tick pulls one frame's worth of mixed 48 kHz stereo float samples
/// from the mix bus and hands it to <see cref="OnMixedSamples"/>, which the caller wires into
/// the encoder/UDP path.
///
/// Architecture rationale (validated by external research, see notes in CaptureSource.cs):
///   • Separate WASAPI captures, each into a buffered ring, all converted to a common 48 kHz
///     stereo float format, then summed via NAudio's MixingSampleProvider — the canonical
///     pattern (https://www.markheath.net/post/mixing-and-looping-with-naudio).
///   • Loopback captures only fire callbacks when something is rendering on the device. Without
///     a continuous render stream, the mic capture (which always fires) and the loopback (which
///     intermittently fires) desync and the mix gets gaps — see naudio/NAudio#1110. The sender
///     starts a <see cref="SilentRenderKeepAlive"/> on every loopback source's device to keep
///     callbacks firing continuously.
///   • Per-source clock drift between independent audio devices is unavoidable across long
///     sessions. The 250 ms ring + DiscardOnBufferOverflow tolerates it for realistic
///     conversation lengths. A proper drift-correcting micro-resample is a future addition.
///
/// Source-list changes are LIVE: <see cref="UpdateSources"/> diffs the desired set against the
/// active set and only adds/removes the sources that actually changed, using NAudio's
/// AddMixerInput / RemoveMixerInput. The mix loop never pauses; the encoder's streamId stays
/// the same; the receiver doesn't re-init its playout. This is what stops a checkbox toggle
/// from causing a 60 ms gap + receiver underrun + auto-tune freakout.
///
/// The mix-tick loop runs on its own task with Stopwatch-based scheduling for jitter-tolerant
/// 10 ms timing — better than System.Threading.Timer or Sleep-based loops.
/// </summary>
internal sealed class MixingEngine : ICaptureBackend
{
    private const int MixSampleRate = 48000;
    private const int MixChannels = 2;
    private const int MixTickMs = 10;
    private const int MixSamplesPerTick = MixSampleRate * MixChannels * MixTickMs / 1000; // 960 floats

    private readonly Action<ReadOnlyMemory<float>> onMixedSamples;
    private readonly Action<string>? onDiagnostic;
    private readonly object gate = new();

    private readonly List<ActiveSource> active = [];
    private MixingSampleProvider? mixer;
    private float[] mixScratch = new float[MixSamplesPerTick];
    private CancellationTokenSource? cts;
    private Task? mixTask;

    private long clippedSampleCount;
    private long mixTickCount;

    public MixingEngine(Action<ReadOnlyMemory<float>> onMixedSamples, Action<string>? onDiagnostic = null)
    {
        this.onMixedSamples = onMixedSamples;
        this.onDiagnostic = onDiagnostic;
    }

    public bool IsRunning => mixTask is { IsCompleted: false };
    public long ClippedSampleCount => Interlocked.Read(ref clippedSampleCount);
    public long MixTickCount => Interlocked.Read(ref mixTickCount);

    public long TotalCaptureCallbacks
    {
        get
        {
            lock (gate)
            {
                long total = 0;
                foreach (var a in active) total += a.Source.CallbackCount;
                return total;
            }
        }
    }

    public long TotalCaptureBytes
    {
        get
        {
            lock (gate)
            {
                long total = 0;
                foreach (var a in active) total += a.Source.BytesCaptured;
                return total;
            }
        }
    }

    /// <summary>MixingEngine doesn't track per-callback timing — its mix tick is timer-driven
    /// rather than callback-driven, so the metric isn't directly meaningful here. Returning 0
    /// is fine: the sender's diag log treats "0 means n/a or no spike". Tight Latency mode in
    /// WASAPI uses <see cref="PushModeWasapiBackend"/> instead, which is callback-driven.</summary>
    public int TakeMaxCallbackGapMs() => 0;

    public string? FirstCaptureFormatDescription
    {
        get { lock (gate) return active.Count == 0 ? null : active[0].Source.CaptureFormatDescription; }
    }

    public string? FirstCaptureLastError
    {
        get { lock (gate) return active.Count == 0 ? null : active[0].Source.LastError; }
    }

    public IReadOnlyList<string> ActiveSourceNames
    {
        get { lock (gate) return active.Select(a => a.Source.Name).ToList(); }
    }

    /// <summary>Multi-source pull-mode WASAPI doesn't yet feed the raw-capture probe — each
    /// <see cref="CaptureSource"/> chain (BufferedWaveProvider → ToSampleProvider →
    /// resampler → stereo-mixdown) would need a per-source probe to be useful, and during
    /// the 2026-05-15 instrumentation push the user's tests have all been single-source on
    /// <see cref="PushModeWasapiBackend"/> instead. Stays at zero here; if a future
    /// multi-source WASAPI test needs the probe, add it per-source in CaptureSource.</summary>
    public float TakeMaxRawCaptureStep() => 0f;
    public float TakeMaxRawCaptureStepCrossBuffer() => 0f;
    public float TakeMaxRawCaptureStepWithinBuffer() => 0f;
    public long TakeCumulativeCaptureTicks() => Interlocked.Exchange(ref cumulativeMixLoopTicks, 0);

    // Cumulative ticks the mix-loop task spent doing per-tick work (everything between
    // wake-up and the next sleep). Reported as captureMs on the diag log so the user sees
    // the WASAPI mix-engine's CPU footprint when it's the active capture path.
    // 2026-05-22 (item 2 of RemSoundefficiency.md).
    private long cumulativeMixLoopTicks;

    /// <summary>
    /// Starts the mix loop with the given initial source set. If already running, the existing
    /// loop is stopped first. After Start, <see cref="UpdateSources"/> can be called to add/remove
    /// sources without interrupting the loop.
    /// </summary>
    public void Start(IReadOnlyList<CaptureSourceSpec> specs)
    {
        lock (gate)
        {
            if (IsRunning) StopInternal();
            if (specs.Count == 0) return;

            var mixFormat = WaveFormat.CreateIeeeFloatWaveFormat(MixSampleRate, MixChannels);
            mixer = new MixingSampleProvider(mixFormat) { ReadFully = true };

            foreach (var spec in specs)
            {
                var entry = OpenSource(spec);
                if (entry is null) continue;
                mixer.AddMixerInput(entry.Source.Provider);
                active.Add(entry);
            }

            if (active.Count == 0)
            {
                onDiagnostic?.Invoke("mixer: no sources opened — staying stopped");
                mixer = null;
                return;
            }

            foreach (var a in active)
            {
                try { a.Source.Start(); }
                catch (Exception ex)
                {
                    onDiagnostic?.Invoke($"mixer: source \"{a.Source.Name}\" failed to start: {ex.GetType().Name}: {ex.Message}");
                }
            }

            Interlocked.Exchange(ref clippedSampleCount, 0);
            Interlocked.Exchange(ref mixTickCount, 0);
            cts = new CancellationTokenSource();
            mixTask = Task.Run(() => MixLoop(cts.Token));
            onDiagnostic?.Invoke($"mixer started with {active.Count} source(s): [{string.Join(", ", active.Select(a => $"\"{a.Source.Name}\" ({a.Source.Kind})"))}]");
        }
    }

    /// <summary>
    /// Live add/remove of sources without stopping the mix loop. Diffs the desired specs
    /// against the currently active set: removes those no longer wanted (RemoveMixerInput +
    /// dispose), adds those newly wanted (open + AddMixerInput + start). The mix loop continues
    /// reading uninterrupted from whatever is currently in the mixer.
    /// </summary>
    public void UpdateSources(IReadOnlyList<CaptureSourceSpec> specs)
    {
        lock (gate)
        {
            // If the engine was started with no sources (specs.Count==0 returns early in
            // Start, so mixTask is never created), a later UpdateSources adding sources used
            // to silently no-op. That broke the BothIndependent flow where a user starts in
            // AsioOnly→BothIndependent with no WASAPI ticks, then later ticks a WASAPI source
            // — the lane would never come alive. Mirror AsioCaptureBackend's pattern: when
            // not running and the new spec set is non-empty, just delegate to Start. The
            // existing empty-specs case (still not running, still no sources to add) stays a
            // no-op as before. 2026-05-11.
            if (!IsRunning || mixer is null)
            {
                if (specs.Count > 0)
                {
                    Start(specs);
                }
                return;
            }

            var desiredKeys = specs.Select(s => SourceKey(s.DeviceId, s.Kind)).ToHashSet();

            // Remove sources no longer wanted.
            for (var i = active.Count - 1; i >= 0; i--)
            {
                var a = active[i];
                if (desiredKeys.Contains(SourceKey(a.Source.DeviceId, a.Source.Kind))) continue;
                try { mixer.RemoveMixerInput(a.Source.Provider); } catch { /* ignore */ }
                DisposeEntry(a);
                active.RemoveAt(i);
                onDiagnostic?.Invoke($"mixer: removed source \"{a.Source.Name}\" ({a.Source.Kind})");
            }

            // Add new sources.
            var existingKeys = active.Select(a => SourceKey(a.Source.DeviceId, a.Source.Kind)).ToHashSet();
            foreach (var spec in specs)
            {
                if (existingKeys.Contains(SourceKey(spec.DeviceId, spec.Kind))) continue;
                var entry = OpenSource(spec);
                if (entry is null) continue;
                mixer.AddMixerInput(entry.Source.Provider);
                active.Add(entry);
                try
                {
                    entry.Source.Start();
                    onDiagnostic?.Invoke($"mixer: added source \"{entry.Source.Name}\" ({entry.Source.Kind})");
                }
                catch (Exception ex)
                {
                    onDiagnostic?.Invoke($"mixer: source \"{entry.Source.Name}\" failed to start: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    public void Stop()
    {
        lock (gate) StopInternal();
    }

    private void StopInternal()
    {
        try { cts?.Cancel(); } catch { /* ignore */ }
        try { mixTask?.Wait(TimeSpan.FromMilliseconds(500)); } catch { /* ignore */ }
        cts?.Dispose();
        cts = null;
        mixTask = null;

        foreach (var a in active) DisposeEntry(a);
        active.Clear();
        mixer = null;
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Opens a single source from a spec: enumerates the device, creates the capture, attaches a
    /// silence keepalive for loopback sources. Does NOT register with the mixer or start capture
    /// — caller does that. Returns null on any failure (device gone, format negotiation, etc.)
    /// after disposing partial state.
    /// </summary>
    private ActiveSource? OpenSource(CaptureSourceSpec spec)
    {
        // Per-application source: no MMDevice, no render keepalive — the process-loopback client
        // captures the app's render stream directly. If the app has exited, the PID is stale and
        // activation fails; the caller simply drops it and the next reconcile re-resolves the name.
        if (spec.Kind == CaptureKind.ProcessLoopback)
        {
            try
            {
                if (!ProcessLoopbackId.TryParse(spec.DeviceId, out var pid))
                    throw new ArgumentException($"bad process-loopback id \"{spec.DeviceId}\"");
                var proc = new ProcessLoopbackCapture(pid);
                var src = new CaptureSource(proc, spec.Kind, spec.DeviceId, spec.Name, onDiagnostic);
                return new ActiveSource { Source = src, KeepAlive = null, Device = null };
            }
            catch (Exception ex)
            {
                onDiagnostic?.Invoke($"mixer: failed to open app source \"{spec.Name}\": {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        MMDevice? device = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            device = enumerator.GetDevice(spec.DeviceId);
            var src = new CaptureSource(device, spec.Kind, spec.Name, onDiagnostic);
            SilentRenderKeepAlive? ka = null;
            if (spec.Kind == CaptureKind.Loopback)
            {
                try
                {
                    ka = new SilentRenderKeepAlive(device, onDiagnostic);
                    ka.Start();
                }
                catch (Exception ex)
                {
                    onDiagnostic?.Invoke($"mixer: keepalive failed for \"{spec.Name}\": {ex.GetType().Name}: {ex.Message}");
                    ka = null; // capture still works without it; just less robust on USB devices
                }
            }
            return new ActiveSource { Source = src, KeepAlive = ka, Device = device };
        }
        catch (Exception ex)
        {
            onDiagnostic?.Invoke($"mixer: failed to open source \"{spec.Name}\" ({spec.Kind}): {ex.GetType().Name}: {ex.Message}");
            try { device?.Dispose(); } catch { /* ignore */ }
            return null;
        }
    }

    /// <summary>Disposes a source bundle in the right order: keepalive first (it shares the device
    /// with the capture; tearing down the device first leaves the keepalive's WasapiOut talking to
    /// a freed COM handle), then capture, then device.</summary>
    private static void DisposeEntry(ActiveSource a)
    {
        try { a.KeepAlive?.Dispose(); } catch { /* ignore */ }
        try { a.Source.Dispose(); } catch { /* ignore */ }
        try { a.Device?.Dispose(); } catch { /* ignore */ }
    }

    private static string SourceKey(string deviceId, CaptureKind kind) => $"{deviceId}|{kind}";

    private async Task MixLoop(CancellationToken ct)
    {
        // Pro Audio scheduling category if available; falls back gracefully if MMCSS isn't accessible.
        using var threadBoost = new WindowsAudioThreadBoost("Pro Audio");
        // Hold the system timer at 1 ms while mixing. Like the receive producer loop, this paces
        // itself with WaitHandle.WaitOne, whose granularity is the (coarse, ~15.6 ms by default)
        // system timer; without a fine timer the mix tick slips and the capture path turns lumpy.
        // Not gated behind Priority mode — see SystemTimerResolution. 2026-06-15.
        using var timerResolution = new SystemTimerResolution(1);

        var ticksPerFrame = Stopwatch.Frequency * MixTickMs / 1000;
        var nextTickStopwatch = Stopwatch.GetTimestamp() + ticksPerFrame;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = Stopwatch.GetTimestamp();
                if (nextTickStopwatch > now)
                {
                    var sleepMs = (int)Math.Clamp((nextTickStopwatch - now) * 1000 / Stopwatch.Frequency, 1, 50);
                    // Item 6 of RemSoundefficiency.md: use WaitHandle.WaitOne directly
                    // instead of WaitAny(new[] { ct.WaitHandle }, ...). Identical semantics
                    // (returns true on signal / false on timeout — i.e. the same as WaitAny
                    // returning index 0 for our single-element case), but no per-call array
                    // allocation. At this loop's ~100 Hz cadence the old line was producing
                    // ~100 small array allocations per second; the new one produces none.
                    if (ct.WaitHandle.WaitOne(sleepMs)) break;
                    continue;
                }

                // If we fell catastrophically behind (>4 frames), resync rather than spinning.
                if (now - nextTickStopwatch > ticksPerFrame * 4)
                {
                    nextTickStopwatch = now;
                }
                nextTickStopwatch += ticksPerFrame;

                var localMixer = mixer;
                if (localMixer is null) continue;

                // Per-thread CPU instrumentation. Capture-the-work-tick at the start of
                // the active body so we can report this loop's CPU footprint via the
                // captureMs column on the diag log.
                var diag = RemSound.Core.DiagnosticsGate.Enabled;
                var workStart = diag ? Stopwatch.GetTimestamp() : 0L;

                var read = localMixer.Read(mixScratch, 0, MixSamplesPerTick);
                if (read <= 0)
                {
                    if (diag) Interlocked.Add(ref cumulativeMixLoopTicks, Stopwatch.GetTimestamp() - workStart);
                    continue;
                }

                // Hard-clamp mixed sum to [-1, 1] to prevent encoder clipping when multiple loud
                // sources sum past unity. Shared rule (SampleClamp); one batched counter add.
                var clipped = SampleClamp.ClampBuffer(mixScratch.AsSpan(0, read));
                if (clipped > 0) Interlocked.Add(ref clippedSampleCount, clipped);
                Interlocked.Increment(ref mixTickCount);

                onMixedSamples(new ReadOnlyMemory<float>(mixScratch, 0, read));
                if (diag) Interlocked.Add(ref cumulativeMixLoopTicks, Stopwatch.GetTimestamp() - workStart);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                onDiagnostic?.Invoke($"mix loop error: {ex.GetType().Name}: {ex.Message}");
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }
    }

    private sealed class ActiveSource
    {
        public required CaptureSource Source { get; init; }
        // Null for per-application (process-loopback) sources, which have no MMDevice.
        public required MMDevice? Device { get; init; }
        public SilentRenderKeepAlive? KeepAlive { get; init; }
    }
}
