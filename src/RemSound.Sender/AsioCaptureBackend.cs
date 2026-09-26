using System.Diagnostics;
using NAudio.Wave;
using NAudio.Wave.Asio;
using RemSound.Core;

namespace RemSound.Sender;

/// <summary>
/// ASIO capture backend. Drives a single <see cref="AsioOut"/> for the chosen ASIO driver and
/// produces 48 kHz stereo float frames in the same shape <see cref="MixingEngine"/> does, so
/// <see cref="AudioSender"/> doesn't care which backend is active.
///
/// Spec identity: each <see cref="CaptureSourceSpec"/> for ASIO uses a synthetic
/// <c>DeviceId</c> of the form <c>"asio:&lt;channel-pair-index&gt;"</c>. Channel pair 0 = ASIO
/// channels 0+1, pair 1 = channels 2+3, etc. The driver is implicit (a single driver per
/// session, configured through the Connectivity &amp; transport dialog).
///
/// Limitations vs the WASAPI backend (deliberate to keep this manageable):
///   • The driver name is fixed at construction. Switching drivers means a new instance;
///     AudioSender releases the old one in the background.
///   • The driver is always opened with its full input channel count, regardless of which
///     pairs the user selected. The unused channels are pulled but discarded. This trades a
///     tiny amount of buffer memory for a big stability win: adding or removing a channel pair
///     never requires reopening the driver, which the playback side may be holding open through
///     the same <see cref="SharedAsioDevice"/> on single-client drivers (Komplete Audio etc.).
///   • Sample rate is fixed at 48 kHz; if the driver doesn't support that, capture fails to
///     start (the diagnostic line says so). All modern pro audio interfaces support 48 kHz.
///   • Hardware loopback channels (e.g. EVO 8's Loop-back 1/2) are just regular ASIO inputs
///     from our perspective; they live in the same channel space and are picked the same way.
/// </summary>
internal sealed class AsioCaptureBackend : ICaptureBackend
{
    private const int MixSampleRate = 48000;
    private const int MixChannels = 2;

    // Volatile-published callback. The ASIO audio thread reads this every callback to
    // decide where to deliver samples; AudioSender swaps it so the same open driver can keep
    // running while delivery moves between the ASIO lane and a no-op (parked, or being released).
    // Volatile is sufficient for reference assignment on .NET (atomic, with memory barrier).
    private volatile Action<ReadOnlyMemory<float>> onMixedSamples;
    // Raw-capture step probe — measures discontinuities in the ASIO buffer exactly as the
    // driver delivered it, BEFORE our code sums the selected channel pairs or clamps to ±1.0.
    // Each capture backend owns its own probe so BothIndependent mode (ASIO and WASAPI both
    // capturing) can be diagnosed without the probes contaminating each other's state.
    private readonly AudioStepProbe rawCaptureStepProbe = new();
    private readonly Action<string>? onDiagnostic;
    private readonly string driverName;
    private readonly object gate = new();

    // The driver, SHARED with the receiver's playback side: one instance per driver name for the whole
    // process, opened full duplex, so capture and playback on one interface never ask a driver for a
    // second instance. The Zoom H4essential refuses one ("Unable to instantiate ASIO"), which is what
    // made "send AND receive on the Zoom" impossible (Anthony Reyers, 2026-09-04). The device owns the
    // apartment thread and the open and close - the same single pumped STA thread that stopped the
    // native crash on close; this backend attaches a callback and lets go. See SharedAsioDevice.
    private SharedAsioDevice? device;

    /// <summary>What this lane was asked to capture, whether or not the driver opened: <see cref="HealSources"/> tries a
    /// failed open again. It was tried once, and a wake before the interface was back, or a music program holding it,
    /// left ASIO sending silent until re-ticked; the WASAPI lane heals every few seconds (2026-09-25 sweep).</summary>
    private IReadOnlyList<CaptureSourceSpec> wantedSpecs = [];
    private DateTime lastFailedOpenUtc = DateTime.MinValue;
    internal static TimeSpan ReopenBackoff = TimeSpan.FromSeconds(10);
    internal int OpenAttemptsForTest;
    // VOLATILE, not lock-guarded, and that is load-bearing. The ASIO audio callback reads this list on
    // the driver's real-time thread. It used to take `gate` to do so — but StopInternal holds `gate` for
    // the WHOLE close, so a callback already in flight when a close began could not return until the
    // close finished. That is the opposite of what the close needs: it unhooks the callback and sleeps
    // 60 ms precisely to let an in-flight callback DRAIN (that sequence now runs inside
    // SharedAsioDevice.Close, still under our `gate`), and the callback could not drain while the
    // closing thread held the lock it was waiting on. The driver's own Stop typically waits for its
    // callback thread, so the two could deadlock until the bounded Invoke gave up.
    // The list is only ever REPLACED wholesale (UpdateSources, Start, StopInternal) and never mutated in
    // place, so a volatile reference read is correct and needs no lock. Writers still hold `gate` to
    // serialise against each other. 2026-08-23 audit, finding S2.
    private volatile List<int> activeChannelPairIndices = [];
    private int recordChannelCount;
    private float[] mixScratch = new float[1024];
    private float[] interleavedScratch = new float[1024];

    private long callbackCount;
    private long bytesCaptured;
    private long clippedSampleCount;
    private string? lastError;
    private string? captureFormat;
    private readonly Stopwatch uptime = new();
    // Per-callback gap tracking. The ASIO callback should fire on a strict period (= buffer
    // size in samples / sample rate). When the .NET runtime, GC, USB driver, or Windows
    // scheduler stalls the audio thread, that period stretches and the audio stream gets a
    // discontinuity — which the receiver can't detect because it just sees a packet arrive
    // late. We measure the elapsed time between consecutive callbacks here, track the worst
    // since the last read, and let the sender's diag logger surface it. Plain int; access
    // is via Interlocked which provides its own memory barriers (no need for volatile).
    private long lastCallbackTimestamp;
    private int maxCallbackGapMs;
    // Cumulative ticks the ASIO capture callback spent doing per-callback work. The diag
    // log samples this once a second; per-thread CPU instrumentation from item 2 of
    // RemSoundefficiency.md. Gated by DiagnosticsGate.Enabled so logs-off costs nothing.
    // 2026-05-22.
    private long cumulativeCaptureTicks;

    public AsioCaptureBackend(string driverName, Action<ReadOnlyMemory<float>> onMixedSamples, Action<string>? onDiagnostic = null)
    {
        this.driverName = driverName;
        this.onMixedSamples = onMixedSamples;
        this.onDiagnostic = onDiagnostic;
    }

    /// <summary>
    /// Swap the callback that captured audio is delivered to. Used by AudioSender to keep
    /// one persistent AsioCaptureBackend instance alive across capture-engine rebuilds — the
    /// driver stays open and the callback is pointed at the ASIO lane, or at a no-op while the
    /// instance is being released (see also <see cref="Park"/>). Volatile write, so the audio
    /// thread picks the new callback up on its very next ASIO buffer.
    /// </summary>
    public void SetCallback(Action<ReadOnlyMemory<float>> callback)
    {
        onMixedSamples = callback;
        callbackDetached = false;
    }

    // True while the callback is the park no-op. Only exists so the gate can assert that STOPPING
    // THE SENDER parks the lane — the bug was never in Park itself, it was that nothing called it.
    private volatile bool callbackDetached;

    /// <summary>True when this lane's callback has been parked to a no-op, so the driver's buffers
    /// go nowhere. Test seam: lets the gate prove <c>AudioSender.Stop</c> actually parks, without a
    /// real ASIO driver to open.</summary>
    internal bool CallbackDetachedForTest => callbackDetached;

    /// <summary>
    /// Stop delivering audio WITHOUT touching the driver. This is Ed's park, from
    /// "ASIO: park the driver on switch-away instead of closing it" — rewire the callback to a
    /// no-op and drop to zero active pairs. No native call, so no close, no reopen, and none of
    /// the five-second Audient hang that made keeping the driver open necessary in the first place.
    ///
    /// <para>Used when the user turns "Send my audio" off. Before this, stopping the sender stopped
    /// only the WASAPI lane: the composite deliberately never stops the borrowed ASIO child, nothing
    /// re-pointed its callback, and the peer list is not cleared on a stop either — so an ASIO lane
    /// went on capturing, encoding, encrypting and TRANSMITTING to every armed peer with the send
    /// toggle off. 2026-08-23 audit, finding S1.</para>
    ///
    /// <para>Both halves matter. The no-op callback stops delivery immediately (a volatile write, so
    /// it takes effect on the very next buffer). The zero pairs make the callback return before the
    /// probe and the mix — it still copies the driver's buffer out and counts it, but sums and sends
    /// nothing. Unparking is automatic: the composite's Start calls
    /// <see cref="UpdateSources"/> with the real specs, and <see cref="AudioSender"/> re-points the
    /// callback for the current mode.</para>
    /// </summary>
    public void Park()
    {
        SetCallback(_ => { });
        callbackDetached = true;   // set AFTER SetCallback, which clears it
        lock (gate)
        {
            if (activeChannelPairIndices.Count == 0) return;
            activeChannelPairIndices = [];
        }
        onDiagnostic?.Invoke("asio capture: parked — callback detached and zero active pairs; driver stays open");
    }

    /// <summary>The callback the driver's thread would invoke, so the gate can drive it directly and
    /// MEASURE whether audio still reaches the sender lane. Testing that Park() was called proves
    /// nothing — the bug was that a live callback kept delivering after a stop, so the test has to
    /// invoke the thing the driver invokes. Test seam only; nothing in the app reads this.</summary>
    internal Action<ReadOnlyMemory<float>> CallbackForTest => onMixedSamples;

    public float TakeMaxRawCaptureStepCrossBuffer() => rawCaptureStepProbe.TakeMaxCrossBuffer();
    public float TakeMaxRawCaptureStepWithinBuffer() => rawCaptureStepProbe.TakeMaxWithinBuffer();
    public long TakeCumulativeCaptureTicks() => Interlocked.Exchange(ref cumulativeCaptureTicks, 0);

    public bool IsRunning => device is not null;

    // Set from the driver's own reported buffer size when the stream opens; cleared on close.
    // Volatile because the UI thread reads it while the apartment thread writes it at open.
    private volatile float reportedInputLatencyMsStore;
    private double reportedInputLatencyMs
    {
        get => reportedInputLatencyMsStore;
        set => reportedInputLatencyMsStore = (float)value;
    }

    /// <summary>The driver's own buffer size, expressed in milliseconds. See
    /// <see cref="ICaptureBackend.ReportedInputLatencyMs"/>.</summary>
    public double ReportedInputLatencyMs => reportedInputLatencyMs;

    /// <summary>ASIO has no equivalent of WASAPI's "the capture stopped without being asked".
    /// A driver-level failure surfaces as an exception out of a control call (handled in Start) or
    /// out of the audio callback (handled there, rate-limited), not as a stopped-stream event. And
    /// the lane is deliberately left open and parked when sending is off, which must never read as
    /// a fault. Always false. See <see cref="ICaptureBackend.HasFaulted"/>.</summary>
    public bool HasFaulted => false;
    public long TotalCaptureCallbacks => Interlocked.Read(ref callbackCount);
    public long TotalCaptureBytes => Interlocked.Read(ref bytesCaptured);
    public string? FirstCaptureFormatDescription => captureFormat;
    public string? FirstCaptureLastError => lastError;
    public long ClippedSampleCount => Interlocked.Read(ref clippedSampleCount);

    public IReadOnlyList<string> ActiveSourceNames
    {
        get
        {
            lock (gate)
            {
                return activeChannelPairIndices
                    .Select(p => $"{driverName} ASIO {p * 2 + 1}/{p * 2 + 2}")
                    .ToList();
            }
        }
    }

    public void Start(IReadOnlyList<CaptureSourceSpec> specs)
    {
        lock (gate)
        {
            if (IsRunning) StopInternal(CloseTimeoutMs);
            wantedSpecs = specs.ToList();
            if (specs.Count == 0) return;

            activeChannelPairIndices = ParseChannelPairIndices(specs);
            if (activeChannelPairIndices.Count == 0)
            {
                onDiagnostic?.Invoke("asio capture: no valid channel pair indices in spec list");
                return;
            }

            try
            {
                // Through the SHARED device - see SharedAsioDevice. The playback side may already hold
                // this driver open; either way there is one instance, opened full duplex on the
                // device's apartment thread with the same breadcrumbs as before. A failure to open
                // becomes a throw, so the catch below runs the same StopInternal cleanup.
                OpenAttemptsForTest++;
                var shared = SharedAsioDevice.Acquire(driverName, onDiagnostic);
                device = shared;
                try
                {
                    shared.EnsureOpen();
                    recordChannelCount = shared.InputChannelCount;
                    if (recordChannelCount <= 0)
                        throw new InvalidOperationException($"driver \"{driverName}\" reports zero input channels");
                    // Sanity-check that the requested pairs are within the driver's channel range. The
                    // device opened the full count anyway, but if a saved spec references a pair above
                    // it, the OnAudioAvailable mixer would silently emit zero - say so.
                    var maxPairIndex = activeChannelPairIndices.Max();
                    var highestNeededChannel = (maxPairIndex + 1) * 2;
                    if (highestNeededChannel > recordChannelCount)
                        onDiagnostic?.Invoke($"asio capture: driver \"{driverName}\" only has {recordChannelCount} input channels, but spec requests channel pair {maxPairIndex} (channels {maxPairIndex * 2 + 1}/{maxPairIndex * 2 + 2})");
                    // The DRIVER's buffer size, from the device. Audio accumulates for exactly one buffer
                    // before the callback fires, so this IS the capture wait - no doubling.
                    reportedInputLatencyMs = shared.FramesPerBuffer > 0
                        ? shared.FramesPerBuffer * 1000.0 / MixSampleRate
                        : 0;
                    onDiagnostic?.Invoke(reportedInputLatencyMs > 0
                        ? $"asio open: driver reports {shared.FramesPerBuffer} frames per buffer = {reportedInputLatencyMs:0.0} ms of capture latency"
                        : "asio open: driver did not report a buffer size — capture latency will fall back to an estimate");
                    captureFormat = $"{MixSampleRate} Hz, {recordChannelCount} input channel(s), 32-bit float (ASIO)";
                    shared.AttachCapture(OnAudioAvailable);
                }
                catch
                {
                    device = null;
                    shared.Release(onDiagnostic, CloseTimeoutMs);
                    throw;
                }
                uptime.Restart();
                onDiagnostic?.Invoke($"asio capture started \"{driverName}\" {captureFormat}; pairs={string.Join(",", activeChannelPairIndices)}");
                lastFailedOpenUtc = DateTime.MinValue;
            }
            catch (Exception ex)
            {
                lastError = SharedAsioDevice.Explain(ex);
                onDiagnostic?.Invoke($"asio capture start failed: {ex.GetType().Name}: {lastError} - tried again in {ReopenBackoff.TotalSeconds:0} s");
                lastFailedOpenUtc = DateTime.UtcNow;
                StopInternal(CloseTimeoutMs);
            }
        }
    }

    /// <summary>A driver that failed to open while channels are wanted is tried again - no sooner than
    /// <see cref="ReopenBackoff"/> after the last failure. Nothing is touched while the driver is open.</summary>
    public void HealSources()
    {
        lock (gate)
        {
            if (IsRunning || wantedSpecs.Count == 0) return;
            if (DateTime.UtcNow - lastFailedOpenUtc < ReopenBackoff) return;
            onDiagnostic?.Invoke("asio capture: channels are wanted but the driver is not open - trying it again");
            Start(wantedSpecs);
        }
    }

    public void UpdateSources(IReadOnlyList<CaptureSourceSpec> specs)
    {
        lock (gate)
        {
            wantedSpecs = specs.ToList();
            if (!IsRunning)
            {
                Start(specs);
                return;
            }
            var newPairs = ParseChannelPairIndices(specs);
            // No reopen needed regardless of which pairs change. We always opened the driver
            // with its full input channel count at Start, so adding or removing a pair is just
            // a matter of which input channels the OnAudioAvailable mixer reads from. Even
            // when the new pair set is empty we DO NOT close the driver here — Audient's
            // ASIO driver (and several others) doesn't tolerate a close+reopen within a few
            // seconds, which is exactly the pattern the user produces by unticking the last
            // ASIO source and then ticking another one. Keeping the driver open with zero
            // active pairs makes the callback fire harmlessly (zeros) and the next pair
            // addition takes effect on the very next callback. Toggling pairs never closes the
            // driver; the actual close happens elsewhere — Stop()/Dispose() (sender disabled or
            // app exit) and AudioSender releasing the driver when ASIO is deselected or the user
            // picks a different driver — all of which are now safe on the apartment thread.
            activeChannelPairIndices = newPairs;
            onDiagnostic?.Invoke($"asio capture: pairs updated to [{string.Join(",", activeChannelPairIndices)}] (no driver restart)");
        }
    }

    /// <summary>How long to wait for a driver close before abandoning it, on the normal
    /// (interactive) paths. NOT tuned to any one driver, and deliberately far above the worst real
    /// close measured so far.
    ///
    /// <para>History, because the number matters. This was 8 s, chosen on the belief that "a healthy
    /// close is ~5ms". Ed's own logs then showed 5 of 46 closes hitting the bound — and the timings
    /// say the driver was never wedged: his Audient takes exactly 5.00 s inside Stop and 2.94 s
    /// inside Dispose, which with the 60 ms drain sleep is 8.00 s against an 8000 ms cap. The close
    /// completed cleanly in the same millisecond the caller gave up on it, and the caller then went
    /// on to open a new driver while the old close was still running.</para>
    ///
    /// <para>Ed's point on reading that: 7.94 s is HIS driver, and another card could be slower
    /// again. So the fix is not to tune a number to one Audient — it is to make the bound generous
    /// enough that no healthy driver reaches it, and to LOG THE MEASURED CLOSE TIME every time so we
    /// accumulate real figures for real drivers instead of guessing twice. The bound now exists only
    /// for a driver that is genuinely never coming back. 2026-08-23 audit, finding S2.</para></summary>
    internal const int CloseTimeoutMs = 30_000;

    /// <summary>Gate seam: told the close bound every time this backend lets go of its driver, so a test
    /// can see which bound a path uses without a real driver to close.</summary>
    internal Action<int>? CloseBoundObservedForTest { get; set; }

    /// <summary>Shutdown gets a much shorter bound. At process exit the OS reclaims the device
    /// regardless, so waiting half a minute to quit is strictly worse than abandoning a slow close —
    /// the opposite trade-off from an interactive stop, where abandoning early is what causes the
    /// next open to find the card still held.</summary>
    private const int ShutdownCloseTimeoutMs = 3_000;

    public void Stop()
    {
        lock (gate)
        {
            wantedSpecs = [];   // stopped on purpose: nothing left for the heal to open
            StopInternal(CloseTimeoutMs);
        }
    }

    private void StopInternal(int closeTimeoutMs)
    {
        CloseBoundObservedForTest?.Invoke(closeTimeoutMs);
        var shared = device;
        if (shared is not null)
        {
            // Let go of the shared driver. A volatile detach stops delivery on the very next callback;
            // the driver itself closes only when the playback side has let go as well, on the device's
            // apartment thread, bounded and timed exactly as this backend always did - see
            // SharedAsioDevice.Close.
            shared.DetachCapture();
            device = null;
            shared.Release(onDiagnostic, closeTimeoutMs);
        }
        uptime.Stop();
        activeChannelPairIndices = [];
        recordChannelCount = 0;
        reportedInputLatencyMs = 0;   // the driver's figure belongs to the stream we just closed
    }

    public void Dispose()
    {
        // Shutdown path: a short bound. See ShutdownCloseTimeoutMs — at exit the OS reclaims the
        // device anyway, so a slow close must not hold the app's quit open for half a minute.
        lock (gate) StopInternal(ShutdownCloseTimeoutMs);
    }

    private static List<int> ParseChannelPairIndices(IReadOnlyList<CaptureSourceSpec> specs)
    {
        var result = new List<int>();
        foreach (var spec in specs)
        {
            if (AsioDeviceId.TryParse(spec.DeviceId, out var pair))
            {
                result.Add(pair);
            }
        }
        result.Sort();
        return result.Distinct().ToList();
    }

    public int TakeMaxCallbackGapMs() => Interlocked.Exchange(ref maxCallbackGapMs, 0);

    // Rate-limit for the callback-failure line below. An ASIO callback fires up to ~750 times a
    // second; a fault that repeats every buffer would otherwise write 750 log lines a second and
    // bury the very evidence we need. Log the first, then at most one a second.
    private long lastCallbackErrorTicks;
    private long callbackErrorsSuppressed;

    /// <summary>
    /// The event handler the driver calls. Its ONLY job is to make sure nothing escapes into the
    /// driver's native real-time thread — an exception crossing that managed/native boundary is
    /// undefined behaviour and has taken this process down before, with no managed stack to show
    /// for it. Every other capture backend already wraps its callback body; this one did not.
    /// Reachable in practice: <see cref="AudioSender.Dispose"/> frees the lane's AES-GCM cipher
    /// while this callback can still be live, which throws ObjectDisposedException from inside
    /// the encrypt. 2026-08-23 audit, finding S3.
    /// </summary>
    private void OnAudioAvailable(object? sender, AsioAudioAvailableEventArgs e)
    {
        try
        {
            HandleAudioAvailable(e);
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
            var now = Stopwatch.GetTimestamp();
            var prev = Volatile.Read(ref lastCallbackErrorTicks);
            if (prev != 0 && now - prev < Stopwatch.Frequency)
            {
                Interlocked.Increment(ref callbackErrorsSuppressed);
                return;
            }
            Volatile.Write(ref lastCallbackErrorTicks, now);
            var suppressed = Interlocked.Exchange(ref callbackErrorsSuppressed, 0);
            onDiagnostic?.Invoke($"asio capture: callback error: {ex.GetType().Name}: {ex.Message}"
                + (suppressed > 0 ? $" ({suppressed} more suppressed in the last second)" : ""));
        }
    }

    private void HandleAudioAvailable(AsioAudioAvailableEventArgs e)
    {
        Interlocked.Increment(ref callbackCount);
        // Capture-callback gap timing. First callback seeds the timestamp without recording a
        // gap (we have nothing to compare to). Subsequent callbacks compute the elapsed ms
        // since the previous one and CAS-update the max. Skipped entirely when diagnostics
        // are off — saves the Stopwatch reads, exchange and CAS loop on every ASIO callback.
        var diag = RemSound.Core.DiagnosticsGate.Enabled;
        long workStart = 0;
        if (diag)
        {
            var now = Stopwatch.GetTimestamp();
            workStart = now;
            var prev = Interlocked.Exchange(ref lastCallbackTimestamp, now);
            if (prev != 0)
            {
                var gapMs = (int)((now - prev) * 1000 / Stopwatch.Frequency);
                int current;
                do
                {
                    current = Volatile.Read(ref maxCallbackGapMs);
                    if (gapMs <= current) break;
                } while (Interlocked.CompareExchange(ref maxCallbackGapMs, gapMs, current) != current);
            }
        }
        // Pull all interleaved float samples for the recorded channels into a reusable buffer.
        var samplesNeeded = e.SamplesPerBuffer * e.InputBuffers.Length;
        if (interleavedScratch.Length < samplesNeeded) interleavedScratch = new float[samplesNeeded];
        var written = e.GetAsInterleavedSamples(interleavedScratch);
        Interlocked.Add(ref bytesCaptured, written * sizeof(float));
        var interleaved = interleavedScratch;

        // Frame count = total samples / channel count.
        var frames = written / Math.Max(1, recordChannelCount);
        var stereoFloats = frames * MixChannels;
        if (mixScratch.Length < stereoFloats) mixScratch = new float[stereoFloats];
        Array.Clear(mixScratch, 0, stereoFloats);

        // Mix selected channel pairs into the stereo output. Each pair contributes its L/R to
        // the mix bus. Lock-free volatile read — see the field comment for why taking `gate` here
        // was a deadlock against the close path.
        var pairs = activeChannelPairIndices;

        if (pairs.Count == 0) return;

        // Diagnostic raw-capture probe — scans the FIRST active channel pair's L channel in
        // the as-delivered-by-the-driver interleaved buffer. Fires BEFORE the mix/sum/clamp
        // below so the probe sees the driver's data verbatim. If this probe goes non-zero
        // on big steps while the post-mix probe also does, the discontinuity is upstream of
        // our code (driver, USB transport, audio hardware). If it stays clean while the
        // post-mix probe goes non-zero, something in the mix/clamp loop is creating the step.
        if (frames > 0 && recordChannelCount > 0)
        {
            var firstPair = pairs[0];
            var lCh = firstPair * 2;
            if (lCh < recordChannelCount)
            {
                rawCaptureStepProbe.ScanInterleavedChannel(
                    new ReadOnlySpan<float>(interleavedScratch, 0, written), recordChannelCount, lCh);
            }
        }

        for (var f = 0; f < frames; f++)
        {
            var srcBase = f * recordChannelCount;
            var dstBase = f * MixChannels;
            float l = 0f, r = 0f;
            foreach (var pair in pairs)
            {
                var lCh = pair * 2;
                var rCh = pair * 2 + 1;
                if (lCh < recordChannelCount) l += interleaved[srcBase + lCh];
                if (rCh < recordChannelCount) r += interleaved[srcBase + rCh];
            }
            mixScratch[dstBase] = l;
            mixScratch[dstBase + 1] = r;
        }

        // Encoder-boundary clamp — the shared rule (SampleClamp), with ONE batched counter add per
        // buffer instead of the old up-to-four interlocked increments per frame on this RT thread.
        var clipped = SampleClamp.ClampBuffer(mixScratch.AsSpan(0, stereoFloats));
        if (clipped > 0) Interlocked.Add(ref clippedSampleCount, clipped);

        onMixedSamples(new ReadOnlyMemory<float>(mixScratch, 0, stereoFloats));
        // Capture-thread CPU instrumentation (item 2 of RemSoundefficiency.md). Records
        // the time the WHOLE callback spent — including the synchronous downstream
        // OnMixedSamples invocation, because that runs on this same thread and counts
        // toward "the capture thread's per-second CPU load". Send-side encode work is
        // ALSO tallied separately via AudioSender.cumulativeEmitTicks for a more detailed
        // breakdown; capture vs send columns let us see "is the bottleneck the buffer
        // copy + mix loop, or is it encode + sendto".
        if (diag) Interlocked.Add(ref cumulativeCaptureTicks, Stopwatch.GetTimestamp() - workStart);
    }
}
