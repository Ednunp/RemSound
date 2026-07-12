using RemSound.Core;

namespace RemSound.Sender;

/// <summary>
/// Capture backend that runs a WASAPI <see cref="MixingEngine"/> and the persistent
/// <see cref="AsioCaptureBackend"/> owned by <see cref="AudioSender"/> in parallel, as two
/// independent lanes — each producing its own PCM stream for its own <see cref="SenderLane"/>.
///
/// Two pipeline shapes are reachable today:
/// <list type="bullet">
///   <item>WasapiOnly: WASAPI child only, no ASIO in the path. Used when no ASIO driver is
///         selected (or none is installed). Lowest latency for WASAPI-only setups.</item>
///   <item>BothIndependent: WASAPI child + persistent ASIO child running side by side. Each
///         delivers samples to its own callback; there is no mix loop, no shared buffer, no
///         tee. ASIO keeps its native sub-5 ms pipeline; WASAPI keeps its WASAPI-event rate.
///         The legacy <c>AudioMode.Both</c> tee-style mode and <c>AudioMode.AsioOnly</c> are
///         no longer reachable from the UI; their enum values remain in
///         <see cref="AudioMode"/> for back-compat but produce nothing here.</item>
/// </list>
/// </summary>
internal sealed class CompositeCaptureBackend : ICaptureBackend
{
    // WASAPI lane callback. In WasapiOnly this is the only callback in use; in BothIndependent
    // it is specifically the WASAPI lane (the ASIO lane has its own callback below).
    private readonly Action<ReadOnlyMemory<float>> onMixedSamples;
    // ASIO lane callback. Only meaningful in BothIndependent (passed but unused in WasapiOnly,
    // where the persistent ASIO instance is disposed by AudioSender).
    private readonly Action<ReadOnlyMemory<float>>? onAsioLaneSamples;
    private readonly Action<string>? onDiagnostic;
    private readonly object gate = new();

    // WASAPI child. Normally a MixingEngine (timer-driven, supports N sources); swapped to
    // PushModeWasapiBackend in Start() when useTightLatencyWasapi is true AND there is exactly
    // one WASAPI source. Push-mode lets the WASAPI capture event drive the encoder/UDP-send
    // pipeline directly, eliminating ~6 ms of Stopwatch+WaitHandle scheduler jitter that's
    // otherwise visible in the receiver as maxGapMs spikes. Multi-source push mode isn't
    // supported (rendezvous-of-N-callback-streams problem) — multi-source falls back to
    // MixingEngine.
    private ICaptureBackend? wasapi;
    // ASIO child. BORROWED — AudioSender owns the persistent instance and keeps the driver
    // open across audio-mode rebuilds (so Audient and similar drivers don't get a rapid
    // close+reopen, which they hate). The composite uses this reference but does NOT
    // dispose it; AudioSender disposes on app shutdown or driver change.
    private readonly AsioCaptureBackend? asio;
    private readonly string? asioDriverName;
    private readonly AudioMode mode;
    private readonly bool useTightLatencyWasapi;

    private List<CaptureSourceSpec> wasapiSpecs = [];
    private List<CaptureSourceSpec> asioSpecs = [];
    private bool started;

    // Coalesces capture-lane rebuilds. A push<->mix backend swap tears down and restarts the whole
    // WASAPI lane (a brief audible gap); when the capture set flaps, doing that per-change produces a
    // burst of restarts. We defer the swap by RebuildDebounceMs and re-arm on each change, so a run of
    // changes collapses into one rebuild. Only the swap path is debounced — in-place source updates
    // still apply immediately. The timer callback and all of these fields are mutated under `gate`.
    private System.Threading.Timer? rebuildTimer;
    private IReadOnlyList<CaptureSourceSpec>? pendingRebuildSpecs;
    private const int RebuildDebounceMs = 250;

    public CompositeCaptureBackend(AudioMode mode, string? asioDriverName, Action<ReadOnlyMemory<float>> onMixedSamples, Action<ReadOnlyMemory<float>>? onAsioLaneSamples, AsioCaptureBackend? injectedAsio, Action<string>? onDiagnostic = null, bool useTightLatencyWasapi = false)
    {
        this.onMixedSamples = onMixedSamples;
        this.onAsioLaneSamples = onAsioLaneSamples;
        this.onDiagnostic = onDiagnostic;
        this.asioDriverName = asioDriverName;
        this.mode = mode;
        this.useTightLatencyWasapi = useTightLatencyWasapi;

        // Legacy enum values (AsioOnly, Both) are no longer produced by the UI but might
        // arrive here from in-flight callers. Coerce them into reachable modes: a non-WASAPI
        // request without a driver demotes to WasapiOnly; a non-WASAPI request with a driver
        // is treated as BothIndependent (the only ASIO-using mode now).
        if (mode != AudioMode.WasapiOnly)
        {
            if (string.IsNullOrEmpty(asioDriverName) || injectedAsio is null)
            {
                this.mode = mode = AudioMode.WasapiOnly;
            }
            else if (mode != AudioMode.BothIndependent)
            {
                this.mode = mode = AudioMode.BothIndependent;
            }
        }

        // Always build the WASAPI lane (it is the WasapiOnly callback path, and the WASAPI
        // lane in BothIndependent). Push-mode swap, if applicable, happens in Start().
        wasapi = new MixingEngine(onMixedSamples, msg => onDiagnostic?.Invoke($"wasapi: {msg}"));

        // Borrow the persistent ASIO instance only in BothIndependent. AudioSender already
        // pointed its callback at the right lane via SetCallback before constructing us.
        if (mode == AudioMode.BothIndependent)
        {
            asio = injectedAsio;
        }
    }

    public bool IsRunning => started;
    public long TotalCaptureCallbacks => (wasapi?.TotalCaptureCallbacks ?? 0) + (asio?.TotalCaptureCallbacks ?? 0);
    public long TotalCaptureBytes => (wasapi?.TotalCaptureBytes ?? 0) + (asio?.TotalCaptureBytes ?? 0);
    public string? FirstCaptureFormatDescription => asio?.FirstCaptureFormatDescription ?? wasapi?.FirstCaptureFormatDescription;
    public string? FirstCaptureLastError => asio?.FirstCaptureLastError ?? wasapi?.FirstCaptureLastError;
    // Sum both inner backends' clip counters. Each per-lane backend (MixingEngine / PushModeWasapi /
    // Asio) still clamps and counts clipped samples; the earlier "no shared mix bus, so always 0" was
    // stale and silently zeroed the clip diagnostic that the SNAP log reports (clipΔ), leaving real
    // clipping invisible — which matters given RemSound's click-vs-clip debugging history.
    public long ClippedSampleCount => (wasapi?.ClippedSampleCount ?? 0) + (asio?.ClippedSampleCount ?? 0);

    /// <summary>Worst callback-gap across both inner backends. We have to take from BOTH (so
    /// each inner's counter resets), then return the larger — otherwise the unread inner
    /// would just keep accumulating its max forever.</summary>
    public int TakeMaxCallbackGapMs()
    {
        var w = wasapi?.TakeMaxCallbackGapMs() ?? 0;
        var a = asio?.TakeMaxCallbackGapMs() ?? 0;
        return Math.Max(w, a);
    }

    public IReadOnlyList<string> ActiveSourceNames
    {
        get
        {
            var combined = new List<string>();
            if (wasapi is not null) combined.AddRange(wasapi.ActiveSourceNames);
            if (asio is not null) combined.AddRange(asio.ActiveSourceNames);
            return combined;
        }
    }

    /// <summary>Max raw-capture step across both inner backends since the last call. Has to
    /// drain BOTH probes (so neither sits accumulating forever after we read one) and return
    /// the larger value.</summary>
    public float TakeMaxRawCaptureStep()
    {
        var w = wasapi?.TakeMaxRawCaptureStep() ?? 0f;
        var a = asio?.TakeMaxRawCaptureStep() ?? 0f;
        return w > a ? w : a;
    }

    /// <summary>Cross-buffer (boundary) max across both inner backends. Drains BOTH.</summary>
    public float TakeMaxRawCaptureStepCrossBuffer()
    {
        var w = wasapi?.TakeMaxRawCaptureStepCrossBuffer() ?? 0f;
        var a = asio?.TakeMaxRawCaptureStepCrossBuffer() ?? 0f;
        return w > a ? w : a;
    }

    /// <summary>Within-buffer max across both inner backends. Drains BOTH.</summary>
    public float TakeMaxRawCaptureStepWithinBuffer()
    {
        var w = wasapi?.TakeMaxRawCaptureStepWithinBuffer() ?? 0f;
        var a = asio?.TakeMaxRawCaptureStepWithinBuffer() ?? 0f;
        return w > a ? w : a;
    }

    /// <summary>Sum of cumulative capture-callback ticks across both inner backends since
    /// the last call. The diag log uses this for captureMs — the per-thread CPU footprint
    /// of all capture-side work (item 2 of RemSoundefficiency.md). Drains BOTH so neither
    /// accumulates forever; in BothIndependent the user wants both lanes' load combined.</summary>
    public long TakeCumulativeCaptureTicks()
    {
        var w = wasapi?.TakeCumulativeCaptureTicks() ?? 0L;
        var a = asio?.TakeCumulativeCaptureTicks() ?? 0L;
        return w + a;
    }

    public void Start(IReadOnlyList<CaptureSourceSpec> specs)
    {
        lock (gate)
        {
            if (started) StopInternal();
            (wasapiSpecs, asioSpecs) = SplitSpecs(specs);

            // Push-mode WASAPI selection. Lets the WASAPI capture event drive the encoder/UDP
            // send pipeline directly, eliminating ~6 ms of Stopwatch+WaitHandle scheduler
            // jitter. Conditions: tight-latency requested, and exactly one WASAPI source
            // (multi-source needs the rendezvous logic in MixingEngine). Applies equally in
            // WasapiOnly and BothIndependent — in either, the WASAPI lane is single-source
            // when the user has ticked one input.
            // Process-loopback (per-application) sources are never eligible for push mode — that fast
            // path opens an MMDevice by id, which a synthetic "proc:<pid>" id has no counterpart for.
            // They always go through MixingEngine, which knows how to open a process-loopback capture.
            var wantPushMode = useTightLatencyWasapi && wasapiSpecs.Count == 1
                && wasapiSpecs[0].Kind != CaptureKind.ProcessLoopback;
            var currentIsPush = wasapi is PushModeWasapiBackend;
            if (wantPushMode != currentIsPush)
            {
                try { wasapi?.Dispose(); } catch { /* ignore */ }
                if (wantPushMode)
                {
                    wasapi = new PushModeWasapiBackend(onMixedSamples, msg => onDiagnostic?.Invoke($"wasapi: {msg}"));
                    onDiagnostic?.Invoke("wasapi backend: switched to push-mode (audio-clock-locked, single-source)");
                }
                else
                {
                    wasapi = new MixingEngine(onMixedSamples, msg => onDiagnostic?.Invoke($"wasapi: {msg}"));
                    onDiagnostic?.Invoke("wasapi backend: switched to mix-engine (timer-driven, multi-source capable)");
                }
            }

            wasapi!.Start(wasapiSpecs);
            // ASIO child is BORROWED from AudioSender. If the driver is already open from a
            // previous engine instance we want UpdateSources (which won't close it) rather
            // than Start (which would Stop+Open and trigger the close+reopen hang on Audient).
            // The callback was already wired to the correct lane by EnsurePersistentAsioLocked.
            if (asio is not null)
            {
                if (asio.IsRunning) asio.UpdateSources(asioSpecs);
                else asio.Start(asioSpecs);
            }
            started = true;
            onDiagnostic?.Invoke($"composite capture started: wasapi={wasapiSpecs.Count} sources, asio={asioSpecs.Count} sources, mode={ModeLabel()}{(wantPushMode ? " [wasapi push]" : "")}");
        }
    }

    public void UpdateSources(IReadOnlyList<CaptureSourceSpec> specs)
    {
        lock (gate)
        {
            if (!started)
            {
                Start(specs);
                return;
            }
            var (newWasapi, newAsio) = SplitSpecs(specs);

            // A push-mode swap (single WASAPI source toggled on/off) means tearing the whole WASAPI
            // lane down and rebuilding it — a brief audible gap. When the capture set flaps (a device
            // coming and going, or a quick reconfiguration), doing that on every change produces a
            // burst of restarts. Coalesce instead: stash the target and (re)arm a short timer so a run
            // of changes collapses into ONE rebuild to the final state. PushModeWasapiBackend still
            // supports only one source; this just defers the swap, it doesn't change the end result.
            var wouldBePush = useTightLatencyWasapi && newWasapi.Count == 1;
            var isPush = wasapi is PushModeWasapiBackend;
            if (wouldBePush != isPush)
            {
                pendingRebuildSpecs = specs;
                (rebuildTimer ??= new System.Threading.Timer(OnRebuildDue)).Change(RebuildDebounceMs, System.Threading.Timeout.Infinite);
                onDiagnostic?.Invoke($"wasapi backend: source-count change ({wasapiSpecs.Count}→{newWasapi.Count}) — coalescing rebuild in {RebuildDebounceMs}ms");
                return;
            }

            // No swap needed: the live backend takes these specs in place (cheap, no glitch). This
            // also resolves a pending rebuild whose target flapped back to the current backend shape.
            CancelPendingRebuild();
            ApplyInPlace(newWasapi, newAsio);
        }
    }

    private void ApplyInPlace(List<CaptureSourceSpec> newWasapi, List<CaptureSourceSpec> newAsio)
    {
        if (wasapi is not null && !SpecsEqual(wasapiSpecs, newWasapi))
        {
            wasapi.UpdateSources(newWasapi);
            wasapiSpecs = newWasapi;
        }
        if (asio is not null && !SpecsEqual(asioSpecs, newAsio))
        {
            asio.UpdateSources(newAsio);
            asioSpecs = newAsio;
        }
    }

    /// <summary>Fires ~<see cref="RebuildDebounceMs"/> after the last swap-triggering change. Applies
    /// the final capture set with a single rebuild — or, if the set flapped back to the current
    /// backend shape during the window, just an in-place update with no rebuild at all. Holds gate.</summary>
    private void OnRebuildDue(object? state)
    {
        lock (gate)
        {
            var specs = pendingRebuildSpecs;
            pendingRebuildSpecs = null;
            if (specs is null || !started) return;
            var (newWasapi, newAsio) = SplitSpecs(specs);
            var wouldBePush = useTightLatencyWasapi && newWasapi.Count == 1;
            var isPush = wasapi is PushModeWasapiBackend;
            if (wouldBePush != isPush)
            {
                onDiagnostic?.Invoke($"wasapi backend: applying coalesced rebuild → {newWasapi.Count} wasapi source(s)");
                StopInternal();
                Start(specs);
            }
            else
            {
                ApplyInPlace(newWasapi, newAsio);
            }
        }
    }

    private void CancelPendingRebuild()
    {
        pendingRebuildSpecs = null;
        rebuildTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
    }

    private string ModeLabel() => mode switch
    {
        AudioMode.WasapiOnly => "fast (WASAPI direct)",
        AudioMode.BothIndependent => "independent lanes (WASAPI + ASIO, no mix)",
        _ => mode.ToString(),
    };

    public void Stop()
    {
        lock (gate) StopInternal();
    }

    private void StopInternal()
    {
        CancelPendingRebuild();
        if (!started) return;
        try { wasapi?.Stop(); } catch { /* ignore */ }
        // ASIO child is NEVER stopped here — it's the persistent instance owned by AudioSender
        // and kept alive across engine rebuilds. Stopping it would force a close+reopen that
        // Audient (and similar drivers) hang on for ~5 s. AudioSender disposes it on app
        // shutdown or driver change.
        started = false;
    }

    public void Dispose()
    {
        Stop();
        try { rebuildTimer?.Dispose(); } catch { /* ignore */ }
        try { wasapi?.Dispose(); } catch { /* ignore */ }
        // ASIO child not disposed — see StopInternal above.
    }

    private static (List<CaptureSourceSpec> wasapi, List<CaptureSourceSpec> asio) SplitSpecs(IReadOnlyList<CaptureSourceSpec> specs)
    {
        var wasapi = new List<CaptureSourceSpec>();
        var asio = new List<CaptureSourceSpec>();
        foreach (var spec in specs)
        {
            if (AsioDeviceId.TryParse(spec.DeviceId, out _)) asio.Add(spec);
            else wasapi.Add(spec);
        }
        return (wasapi, asio);
    }

    private static bool SpecsEqual(IReadOnlyList<CaptureSourceSpec> a, IReadOnlyList<CaptureSourceSpec> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].DeviceId != b[i].DeviceId || a[i].Kind != b[i].Kind) return false;
        }
        return true;
    }
}
