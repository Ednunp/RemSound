using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RemSound.Core;

namespace RemSound.Sender;

/// <summary>
/// Single-source WASAPI capture backend with PUSH-DRIVEN timing — each buffer the WASAPI capture
/// delivers is the encode/send trigger, so the audio pipeline runs at the device's own period
/// instead of the OS scheduler's Stopwatch+WaitHandle clock.
///
/// Why this exists: <see cref="MixingEngine"/> uses a Stopwatch-driven 10 ms mix tick that
/// pulls audio through a sample-provider chain. That tick is woken by
/// <see cref="WaitHandle.WaitAny"/>, which on Windows has ~6 ms of inherent jitter even with
/// MMCSS Pro Audio thread priority — visible as <c>maxGapMs=16-20 ms</c> in the receiver
/// diagnostics. At 48 kHz device rate that jitter is absorbed by buffer cushion; at 96 kHz the
/// extra in-tick resampling stage compounds it and the receiver's buffer ends up sitting
/// ~13 ms lower (closer to the underrun edge), producing audible clicks at tight target
/// latency.
///
/// Push mode eliminates the mix tick entirely. The capture delivers at the device's own period
/// where the device's event signals — a loopback device whose event never does is polled at half a
/// buffer instead (see <see cref="LowLatencyWasapiLoopbackCapture"/>) — and we run the
/// resample / stereo-mixdown / soft-clamp / hand-off-to-encoder pipeline directly on the
/// capture thread. Same architectural shape as <see cref="AsioCaptureBackend"/> already has.
///
/// Constraints (deliberate scope reduction so we ship something testable):
///   • Single source only. <see cref="Start"/> with multiple specs throws — caller is
///     expected to fall back to <see cref="MixingEngine"/> for multi-source. Mixing N
///     independent WASAPI capture callbacks needs a rendezvous point that doesn't exist
///     in this design.
///   • 32-bit float, or 16/24/32-bit integer PCM (plain or extensible) converted to float in the
///     callback. Modern WASAPI shared mode delivers 32-bit float on every device we've seen; an
///     input reporting integer PCM used to stop with the lane silently dead. Any other format
///     still stops, with a diagnostic.
///   • Resampling is performed inline using <see cref="WdlResampler"/> in sinc mode (64 taps,
///     32 sub-phases). That is NOT the pull path's configuration: the WdlResamplingSampleProvider
///     in <see cref="CaptureSource"/> runs the same resampler with sinc off (filter count 2).
///
/// Threading: NAudio's WASAPI callback runs on its own thread, which becomes the audio
/// thread for our purposes. <see cref="onMixedSamples"/> is invoked synchronously from
/// inside that callback, so the encoder/UDP-send work happens on the capture thread. PCM
/// pack and Opus encode are both fast enough not to overrun the next callback period
/// (typically &lt; 200 µs of work per 10 ms callback on modern hardware).
/// </summary>
internal sealed class PushModeWasapiBackend : ICaptureBackend
{
    private const int MixSampleRate = 48000;
    private const int MixChannels = 2;
    private const int CaptureBufferMs = 10;

    private readonly Action<ReadOnlyMemory<float>> onMixedSamples;
    private readonly Action<string>? onDiagnostic;
    private readonly object gate = new();

    private IWaveIn? capture;
    private MMDevice? captureDevice; // the device backing capture/keepAlive; WE own it and must dispose it (NAudio's WasapiCapture never does)
    private SilentRenderKeepAlive? keepAlive;
    private CaptureSourceSpec? activeSpec;
    private string? captureFormatDescription;
    private string? lastError;

    private long callbackCount;
    private long bytesCaptured;
    private long clippedSampleCount;

    // Raw-capture step probe — scans the WASAPI source buffer as floats right after we
    // reinterpret the byte buffer, BEFORE resampling / stereo-mixdown / clamp. This is the
    // earliest float-form view of what the Windows audio engine handed us. Used together
    // with the per-lane pre-encode probe to localise where discontinuities enter on the
    // WASAPI path. Per-backend so BothIndependent doesn't cross-contaminate ASIO and WASAPI
    // probes' cross-buffer state.
    private readonly AudioStepProbe rawCaptureStepProbe = new();

    // Resampling state — only allocated when source rate != MixSampleRate.
    private WdlResampler? resampler;
    private int sourceSampleRate;
    private int sourceChannels;
    private bool sourceIsFloat = true;
    private int sourceBitsPerSample = 32;

    // Reusable scratch buffers. Sized lazily inside the callback.
    private float[] sourceFloatScratch = new float[8192];
    private float[] resampledScratch = new float[8192];
    private float[] stereoScratch = new float[4096];

    public PushModeWasapiBackend(Action<ReadOnlyMemory<float>> onMixedSamples, Action<string>? onDiagnostic = null)
    {
        this.onMixedSamples = onMixedSamples;
        this.onDiagnostic = onDiagnostic;
    }

    public bool IsRunning => capture is not null;
    public long TotalCaptureCallbacks => Interlocked.Read(ref callbackCount);
    public long TotalCaptureBytes => Interlocked.Read(ref bytesCaptured);
    public string? FirstCaptureFormatDescription => captureFormatDescription;
    public string? FirstCaptureLastError => lastError;
    public long ClippedSampleCount => Interlocked.Read(ref clippedSampleCount);

    public IReadOnlyList<string> ActiveSourceNames =>
        activeSpec is { } s ? new[] { s.Name } : Array.Empty<string>();

    /// <summary>Push-mode WASAPI is callback-driven and could meaningfully track callback gaps,
    /// but for now we don't — adding the timing only matters once we're hunting an audible
    /// jitter issue on the WASAPI tight-latency path. Returns 0 (= no spike). Compare with
    /// <see cref="AsioCaptureBackend.TakeMaxCallbackGapMs"/> which does track it because that's
    /// where Ed has been hunting jitter.</summary>
    public int TakeMaxCallbackGapMs() => 0;

    public float TakeMaxRawCaptureStepCrossBuffer() => rawCaptureStepProbe.TakeMaxCrossBuffer();
    public float TakeMaxRawCaptureStepWithinBuffer() => rawCaptureStepProbe.TakeMaxWithinBuffer();
    public long TakeCumulativeCaptureTicks() => Interlocked.Exchange(ref cumulativeCaptureTicks, 0);

    // Per-thread CPU instrumentation. Cumulative ticks the WASAPI capture callback spent
    // in per-callback work; the diag log samples this once a second to report captureMs.
    // See item 2 of RemSoundefficiency.md. 2026-05-22.
    private long cumulativeCaptureTicks;

    // What this lane was asked to capture, whether or not it opened: HealSources re-opens it.
    private CaptureSourceSpec? desiredSpec;
    private int failedTries;
    private bool healing;

    /// <summary>See <see cref="ICaptureBackend.HealSources"/>: the one source, re-opened if it failed to open or died.</summary>
    public void HealSources()
    {
        if (desiredSpec is not { } spec) return;
        if (capture is not null && !faulted) return;
        healing = true;
        try { Start([spec]); }
        finally { healing = false; }
        if (capture is not null && !faulted)
        {
            onDiagnostic?.Invoke($"push-wasapi: \"{spec.Name}\" is capturing again (re-opened on try {failedTries + 1})");
            failedTries = 0;
        }
        else if (++failedTries == 1 || failedTries % 20 == 0)
        {
            onDiagnostic?.Invoke($"push-wasapi: \"{spec.Name}\" still cannot be opened ({failedTries} tries) — trying again every few seconds");
        }
    }

    public void Start(IReadOnlyList<CaptureSourceSpec> specs)
    {
        if (specs.Count == 0)
        {
            onDiagnostic?.Invoke("push-wasapi: start called with no specs — staying stopped");
            return;
        }
        if (specs.Count == 1) desiredSpec = specs[0];
        if (specs.Count > 1)
        {
            // Surface this loudly. The caller should have routed multi-source to MixingEngine.
            throw new InvalidOperationException(
                $"PushModeWasapiBackend supports only one source, got {specs.Count}. Caller must fall back to MixingEngine for multi-source.");
        }
        if (specs[0].Kind == CaptureKind.ProcessLoopback)
        {
            // Per-app process-loopback has no MMDevice to open — it must go through MixingEngine.
            // CompositeCaptureBackend.IsPushEligible already excludes these, but backstop it here so a
            // routing slip degrades to a clear diagnostic instead of GetDevice's opaque ArgumentException.
            throw new InvalidOperationException(
                $"PushModeWasapiBackend cannot capture a process-loopback source (\"{specs[0].Name}\"). Caller must route per-app sources to MixingEngine.");
        }

        lock (gate)
        {
            if (IsRunning) StopInternal();
            var spec = specs[0];
            try
            {
                if (MixingEngine.RefuseOpenForTest?.Invoke(spec) == true) throw new InvalidOperationException("refused by the self-test");
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(spec.DeviceId);
                captureDevice = device; // hold it for disposal in StopInternal — see field comment

                capture = spec.Kind == CaptureKind.Loopback
                    ? new LowLatencyWasapiLoopbackCapture(device, CaptureBufferMs, msg => onDiagnostic?.Invoke($"push-wasapi: {msg}"))
                    : (IWaveIn)new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: CaptureBufferMs);

                var fmt = capture.WaveFormat;
                sourceChannels = fmt.Channels;
                sourceSampleRate = fmt.SampleRate;
                captureFormatDescription = $"{fmt.SampleRate} Hz, {fmt.Channels} ch, {fmt.BitsPerSample}-bit "
                    + (fmt.Encoding == WaveFormatEncoding.IeeeFloat ? "float" : fmt.Encoding.ToString());

                // Float or integer PCM, plain or extensible. Integer PCM used to stop here with the lane silently dead —
                // nothing re-opened it — while this class's summary promised a fallback that did not exist. It is now
                // converted in the callback; any other format still stops, with this diagnostic. 2026-09-13 review.
                if (SampleKindOf(fmt) is not { } sample)
                {
                    onDiagnostic?.Invoke(
                        $"push-wasapi: source \"{spec.Name}\" reports a capture format push mode cannot read ({fmt.Encoding}, {fmt.BitsPerSample}-bit)");
                    lastError = $"unsupported source encoding: {fmt.Encoding} {fmt.BitsPerSample}-bit";
                    StopInternal();
                    return;
                }
                (sourceIsFloat, sourceBitsPerSample) = sample;

                if (fmt.SampleRate != MixSampleRate)
                {
                    resampler = new WdlResampler();
                    // Sinc filter, 64-tap, 32 sub-phase. This is NOT what the CaptureSource pull path
                    // runs (NAudio's WdlResamplingSampleProvider sets sinc off, filter count 2), so an
                    // audible difference against MixingEngine on a non-48 kHz device may come from the
                    // filter as well as the timing.
                    resampler.SetMode(true, 2, true, 64, 32);
                    resampler.SetFilterParms();
                    resampler.SetFeedMode(false); // pull mode internally; we drive the pull from our callback
                    resampler.SetRates(sourceSampleRate, MixSampleRate);
                }
                else
                {
                    resampler = null;
                }

                if (spec.Kind == CaptureKind.Loopback)
                {
                    // WASAPI loopback only fires callbacks while something else is rendering on
                    // the device. Same trick MixingEngine uses (see naudio/NAudio#1110).
                    try
                    {
                        keepAlive = new SilentRenderKeepAlive(device, onDiagnostic);
                        keepAlive.Start();
                    }
                    catch (Exception ex)
                    {
                        onDiagnostic?.Invoke($"push-wasapi: keepalive failed for \"{spec.Name}\": {ex.GetType().Name}: {ex.Message}");
                        keepAlive = null;
                    }
                }

                // Ask the DEVICE how long audio waits in it, rather than assuming the 10 ms we asked
                // for. Once, here, off the audio thread — see DeviceLatencyProbe. 2026-08-24.
                reportedInputLatencyMs = DeviceLatencyProbe.CaptureLatencyMs(device, CaptureBufferMs);
                onDiagnostic?.Invoke(reportedInputLatencyMs > 0
                    ? $"push-wasapi: \"{spec.Name}\" reports {reportedInputLatencyMs:0.0} ms of capture latency (engine period {DeviceLatencyProbe.EnginePeriodMs(device):0.0} ms, we asked for {CaptureBufferMs} ms)"
                    : $"push-wasapi: \"{spec.Name}\" would not report its period — capture latency falls back to the {CaptureBufferMs} ms estimate");

                activeSpec = spec;
                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += OnRecordingStopped;
                capture.StartRecording();
                onDiagnostic?.Invoke($"push-wasapi started \"{spec.Name}\" ({spec.Kind}) at {captureFormatDescription}");
            }
            catch (Exception ex)
            {
                // A device-open failure (device disabled/unplugged between enumeration and GetDevice/
                // Initialize) must NOT propagate: it used to be rethrown, and because CompositeCaptureBackend
                // and AudioSender don't wrap the engine's Start, it could crash the whole app during device
                // churn. Match MixingEngine/AsioCaptureBackend — log, stay stopped, let the caller carry on
                // (the device-change watcher / capture self-heal re-open when a good device appears).
                lastError = ex.Message;
                if (!healing) onDiagnostic?.Invoke($"push-wasapi start failed for \"{spec.Name}\": {ex.GetType().Name}: {ex.Message} — tried again every few seconds");
                StopInternal();
            }
        }
    }

    public void UpdateSources(IReadOnlyList<CaptureSourceSpec> specs)
    {
        // Single-source backend; live add/remove like MixingEngine's isn't applicable.
        // If the spec list shape is unchanged, no-op. Otherwise restart.
        // A faulted lane must NOT count as "no change" — that early return is what kept a dead
        // capture on the same device alive-looking forever, because the spec set never changes when
        // a device faults in place. 2026-08-23 audit, finding S6.
        var noChange = !faulted
            && activeSpec is { } s
            && specs.Count == 1
            && specs[0].DeviceId == s.DeviceId
            && specs[0].Kind == s.Kind;
        if (noChange) return;
        lock (gate) StopInternal();
        if (specs.Count > 0) Start(specs);
    }

    public void Stop()
    {
        desiredSpec = null;   // stopped on purpose: nothing for the heal to re-open
        failedTries = 0;
        lock (gate) StopInternal();
    }

    /// <summary>Gate seam: die as a device does when it faults in place.</summary>
    internal void FaultForTest() => faulted = true;

    private void StopInternal()
    {
        // Clear the fault first: whatever happens below, this instance is being torn down or
        // re-opened, so the old fault must not survive into the next attempt and re-trigger the
        // re-apply tick forever.
        faulted = false;
        reportedInputLatencyMs = 0;   // belongs to the device we are closing
        if (capture is not null)
        {
            try { capture.DataAvailable -= OnDataAvailable; } catch { /* ignore */ }
            try { capture.RecordingStopped -= OnRecordingStopped; } catch { /* ignore */ }
            try { capture.StopRecording(); } catch { /* ignore */ }
            try { capture.Dispose(); } catch { /* ignore */ }
            capture = null;
        }
        if (keepAlive is not null)
        {
            try { keepAlive.Dispose(); } catch { /* ignore */ }
            keepAlive = null;
        }
        // Dispose the device AFTER capture + keepAlive (both hold its COM state). NAudio's
        // WasapiCapture keeps no reference to the MMDevice and never disposes it, so without this the
        // device's COM/handle state leaks on every start/stop/switch — the WASAPI handle-leak fingerprint.
        if (captureDevice is not null)
        {
            try { captureDevice.Dispose(); } catch { /* ignore */ }
            captureDevice = null;
        }
        resampler = null;
        activeSpec = null;
    }

    public void Dispose() => Stop();

    // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT and KSDATAFORMAT_SUBTYPE_PCM, the sub-formats WAVE_FORMAT_EXTENSIBLE carries.
    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");

    /// <summary>Pure, testable: what the capture delivers — 32-bit float, or 16/24/32-bit integer PCM, plain or
    /// WAVE_FORMAT_EXTENSIBLE. Null for anything push mode cannot read.</summary>
    internal static (bool IsFloat, int Bits)? SampleKindOf(WaveFormat fmt)
    {
        var encoding = fmt.Encoding;
        if (fmt is WaveFormatExtensible extensible)
        {
            if (extensible.SubFormat == SubtypeIeeeFloat) encoding = WaveFormatEncoding.IeeeFloat;
            else if (extensible.SubFormat == SubtypePcm) encoding = WaveFormatEncoding.Pcm;
        }
        return encoding switch
        {
            WaveFormatEncoding.IeeeFloat when fmt.BitsPerSample == 32 => (true, 32),
            WaveFormatEncoding.Pcm when fmt.BitsPerSample is 16 or 24 or 32 => (false, fmt.BitsPerSample),
            _ => null,
        };
    }

    /// <summary>Pure, testable: interleaved capture bytes to floats in -1..1. Float is copied as it is; 16, 24 and 32-bit
    /// integer PCM are scaled by their full range. Allocation-free — it runs on the capture thread. Returns the sample
    /// count written.</summary>
    internal static int ConvertToFloat(byte[] bytes, int byteCount, bool isFloat, int bitsPerSample, float[] destination)
    {
        var count = byteCount / (bitsPerSample / 8);
        if (isFloat)
        {
            Buffer.BlockCopy(bytes, 0, destination, 0, count * sizeof(float));
            return count;
        }
        switch (bitsPerSample)
        {
            case 16:
                for (var i = 0; i < count; i++) destination[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
                break;
            case 24:
                for (var i = 0; i < count; i++)
                {
                    var o = i * 3;
                    destination[i] = (bytes[o] | (bytes[o + 1] << 8) | ((sbyte)bytes[o + 2] << 16)) / 8388608f;
                }
                break;
            default:
                for (var i = 0; i < count; i++) destination[i] = BitConverter.ToInt32(bytes, i * 4) / 2147483648f;
                break;
        }
        return count;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        Interlocked.Increment(ref callbackCount);
        Interlocked.Add(ref bytesCaptured, e.BytesRecorded);
        if (e.BytesRecorded <= 0) return;

        var diag = RemSound.Core.DiagnosticsGate.Enabled;
        var workStart = diag ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
        try
        {
            // 1. The captured bytes as floats: float copied as it is, integer PCM scaled (see Start). e.Buffer is byte[]
            //    and we need the floats indexable, so they go into our scratch; the copy is cheap (7680 bytes for 10 ms at
            //    96 kHz stereo float).
            var sourceFloatCount = e.BytesRecorded / (sourceBitsPerSample / 8);
            if (sourceFloatScratch.Length < sourceFloatCount)
                sourceFloatScratch = new float[sourceFloatCount];
            ConvertToFloat(e.Buffer, e.BytesRecorded, sourceIsFloat, sourceBitsPerSample, sourceFloatScratch);
            var sourceFrames = sourceFloatCount / sourceChannels;

            // Raw-capture probe — scans the L channel of the source buffer in the form
            // Windows handed it to us, before our resample / mixdown / clamp. Channel layout
            // for WASAPI loopback is interleaved [L,R,...] for stereo or a single channel for
            // mono; the probe walks every Nth sample where N=sourceChannels. If this probe
            // shows steps that the per-lane pre-encode probe doesn't, our downstream
            // processing is masking real source-side issues. If both show the same steps,
            // the discontinuity arrived from Windows / the device driver.
            if (sourceFrames > 0 && sourceChannels > 0)
            {
                rawCaptureStepProbe.ScanInterleavedChannel(
                    new ReadOnlySpan<float>(sourceFloatScratch, 0, sourceFloatCount),
                    sourceChannels,
                    0);
            }

            // 2. Resample to MixSampleRate if needed. The resampler is pull-mode; we drive the
            //    pull from our callback. Approximate output frames = input * outRate / inRate.
            float[] working;
            int workingFrames;
            int workingChannels;
            if (resampler is null)
            {
                working = sourceFloatScratch;
                workingFrames = sourceFrames;
                workingChannels = sourceChannels;
            }
            else
            {
                // Compute a generous upper bound on output frames (add a small pad for the
                // resampler's lookahead). We ask for MORE output than this input can produce, so
                // ResamplePrepare always asks for at least sourceFrames and the Math.Min below never
                // actually truncates — which matters, because anything we did not feed would be
                // DROPPED, not held: it stays in sourceFloatScratch, which the next callback
                // overwrites. (This comment used to claim the resampler held it. It does not. The
                // arithmetic is what makes the path safe, so do not shrink outBound without
                // rechecking it. 2026-08-23 audit, finding S9.)
                var outBound = (int)Math.Ceiling(sourceFrames * (double)MixSampleRate / sourceSampleRate) + 16;
                if (resampledScratch.Length < outBound * sourceChannels)
                    resampledScratch = new float[outBound * sourceChannels];

                var inFramesNeeded = resampler.ResamplePrepare(outBound, sourceChannels, out var inBuf, out var inOff);
                var copyFrames = Math.Min(sourceFrames, inFramesNeeded);
                if (copyFrames > 0)
                {
                    Array.Copy(sourceFloatScratch, 0, inBuf, inOff, copyFrames * sourceChannels);
                }
                var produced = resampler.ResampleOut(resampledScratch, 0, copyFrames, outBound, sourceChannels);
                working = resampledScratch;
                workingFrames = produced;
                workingChannels = sourceChannels;
            }

            if (workingFrames <= 0) return;

            // 3. Stereo mixdown. Mono → duplicate; stereo → passthrough; multi-channel → take
            //    front L/R (matches StereoMixDownSampleProvider in CaptureSource).
            float[] stereo;
            if (workingChannels == 2)
            {
                stereo = working;
            }
            else
            {
                if (stereoScratch.Length < workingFrames * MixChannels)
                    stereoScratch = new float[workingFrames * MixChannels];
                if (workingChannels == 1)
                {
                    for (var i = 0; i < workingFrames; i++)
                    {
                        stereoScratch[i * 2] = working[i];
                        stereoScratch[i * 2 + 1] = working[i];
                    }
                }
                else
                {
                    for (var i = 0; i < workingFrames; i++)
                    {
                        stereoScratch[i * 2] = working[i * workingChannels];
                        stereoScratch[i * 2 + 1] = working[i * workingChannels + 1];
                    }
                }
                stereo = stereoScratch;
            }

            // 4. Encoder-boundary clamp — the shared rule (SampleClamp), one batched counter add.
            var stereoFloatCount = workingFrames * MixChannels;
            var clipped = SampleClamp.ClampBuffer(stereo.AsSpan(0, stereoFloatCount));
            if (clipped > 0) Interlocked.Add(ref clippedSampleCount, clipped);

            // 5. Hand off to the encoder/UDP-send pipeline. Synchronous on the capture thread.
            onMixedSamples(new ReadOnlyMemory<float>(stereo, 0, stereoFloatCount));
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
            onDiagnostic?.Invoke($"push-wasapi: callback error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // Capture-thread CPU instrumentation. See AsioCaptureBackend for matching
            // pattern. Wrapped in `finally` so the count is honest even when the body
            // throws (the catch above is the normal path).
            if (diag)
            {
                Interlocked.Add(ref cumulativeCaptureTicks, System.Diagnostics.Stopwatch.GetTimestamp() - workStart);
            }
        }
    }

    /// <summary>
    /// The capture stopped. If it stopped WITH an exception nobody asked for it, so the lane is dead
    /// — flag it. This used to log and return, leaving <see cref="IsRunning"/> true and the app
    /// reporting that it was still sending while the device was gone. See
    /// <see cref="ICaptureBackend.HasFaulted"/> for the recovery this hooks into.
    /// 2026-08-23 audit, finding S6.
    /// </summary>
    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            lastError = e.Exception.Message;
            faulted = true;
            onDiagnostic?.Invoke($"push-wasapi: capture DIED (not a requested stop): {e.Exception.GetType().Name}: {e.Exception.Message} — lane marked faulted, the app's re-apply tick will re-open it");
        }
    }

    // Set when the capture stops without us asking. Cleared by StopInternal, so a deliberate stop or
    // a successful re-open resets it. Volatile: written on the WASAPI thread, read from the UI thread.
    private volatile bool faulted;
    public bool HasFaulted => faulted;

    // The device's own capture latency, read once at open. Volatile via a float store because the UI
    // thread reads it while the opening thread writes it.
    private volatile float reportedInputLatencyStore;
    private double reportedInputLatencyMs
    {
        get => reportedInputLatencyStore;
        set => reportedInputLatencyStore = (float)value;
    }

    /// <summary>The device's own figure. See <see cref="ICaptureBackend.ReportedInputLatencyMs"/>.</summary>
    public double ReportedInputLatencyMs => reportedInputLatencyMs;
}
