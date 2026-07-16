using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RemSound.Core;

namespace RemSound.Sender;

/// <summary>
/// Single-source WASAPI capture backend with PUSH-DRIVEN timing — the WASAPI capture event
/// callback is the encode/send trigger, so the audio pipeline runs on the audio device's
/// hardware clock instead of the OS scheduler's Stopwatch+WaitHandle clock.
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
/// Push mode eliminates the mix tick entirely. The WASAPI callback already fires at the
/// device's hardware-clocked period (sub-millisecond precision), and we run the
/// resample / stereo-mixdown / soft-clamp / hand-off-to-encoder pipeline directly on the
/// callback thread. Same architectural shape as <see cref="AsioCaptureBackend"/> already has.
///
/// Constraints (deliberate scope reduction so we ship something testable):
///   • Single source only. <see cref="Start"/> with multiple specs throws — caller is
///     expected to fall back to <see cref="MixingEngine"/> for multi-source. Mixing N
///     independent WASAPI capture callbacks needs a rendezvous point that doesn't exist
///     in this design.
///   • Float-format capture only. Modern WASAPI loopback / shared-mode delivers
///     <see cref="WaveFormatEncoding.IeeeFloat"/> 32-bit stereo on every device we've seen.
///     Direct-input devices that report int16 will fall through to a diagnostic and the
///     callback returns silence; caller can fall back to <see cref="MixingEngine"/> in that
///     case (which uses NAudio's <c>ToSampleProvider</c> conversion path that handles all
///     formats).
///   • Resampling is performed inline using <see cref="WdlResampler"/> (sinc filter). Same
///     resampler the existing pull path uses — kept identical to keep audio quality
///     comparable.
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

    private WasapiCapture? capture;
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

    public float TakeMaxRawCaptureStep() => rawCaptureStepProbe.TakeMax();
    public float TakeMaxRawCaptureStepCrossBuffer() => rawCaptureStepProbe.TakeMaxCrossBuffer();
    public float TakeMaxRawCaptureStepWithinBuffer() => rawCaptureStepProbe.TakeMaxWithinBuffer();
    public long TakeCumulativeCaptureTicks() => Interlocked.Exchange(ref cumulativeCaptureTicks, 0);

    // Per-thread CPU instrumentation. Cumulative ticks the WASAPI capture callback spent
    // in per-callback work; the diag log samples this once a second to report captureMs.
    // See item 2 of RemSoundefficiency.md. 2026-05-22.
    private long cumulativeCaptureTicks;

    public void Start(IReadOnlyList<CaptureSourceSpec> specs)
    {
        if (specs.Count == 0)
        {
            onDiagnostic?.Invoke("push-wasapi: start called with no specs — staying stopped");
            return;
        }
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
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(spec.DeviceId);
                captureDevice = device; // hold it for disposal in StopInternal — see field comment

                capture = spec.Kind == CaptureKind.Loopback
                    ? new LowLatencyWasapiLoopbackCapture(device, audioBufferMilliseconds: CaptureBufferMs)
                    : new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: CaptureBufferMs);

                var fmt = capture.WaveFormat;
                sourceChannels = fmt.Channels;
                sourceSampleRate = fmt.SampleRate;
                captureFormatDescription = $"{fmt.SampleRate} Hz, {fmt.Channels} ch, {fmt.BitsPerSample}-bit "
                    + (fmt.Encoding == WaveFormatEncoding.IeeeFloat ? "float" : fmt.Encoding.ToString());

                if (fmt.Encoding != WaveFormatEncoding.IeeeFloat)
                {
                    onDiagnostic?.Invoke(
                        $"push-wasapi: source \"{spec.Name}\" reports non-float capture format ({fmt.Encoding}); push mode requires IeeeFloat");
                    lastError = $"unsupported source encoding: {fmt.Encoding}";
                    StopInternal();
                    return;
                }

                if (fmt.SampleRate != MixSampleRate)
                {
                    resampler = new WdlResampler();
                    // Same configuration the existing CaptureSource pull path uses — sinc filter,
                    // 64-tap, 32 sub-phase. Quality matches the pull path so any audible
                    // difference vs MixingEngine is timing-driven, not filter-quality-driven.
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

                activeSpec = spec;
                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += OnRecordingStopped;
                capture.StartRecording();
                onDiagnostic?.Invoke($"push-wasapi started \"{spec.Name}\" ({spec.Kind}) at {captureFormatDescription}");
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                onDiagnostic?.Invoke($"push-wasapi start failed for \"{spec.Name}\": {ex.GetType().Name}: {ex.Message}");
                StopInternal();
                throw;
            }
        }
    }

    public void UpdateSources(IReadOnlyList<CaptureSourceSpec> specs)
    {
        // Single-source backend; live add/remove like MixingEngine V2 isn't applicable.
        // If the spec list shape is unchanged, no-op. Otherwise restart.
        var noChange = activeSpec is { } s
            && specs.Count == 1
            && specs[0].DeviceId == s.DeviceId
            && specs[0].Kind == s.Kind;
        if (noChange) return;
        lock (gate) StopInternal();
        if (specs.Count > 0) Start(specs);
    }

    public void Stop()
    {
        lock (gate) StopInternal();
    }

    private void StopInternal()
    {
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

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        Interlocked.Increment(ref callbackCount);
        Interlocked.Add(ref bytesCaptured, e.BytesRecorded);
        if (e.BytesRecorded <= 0) return;

        var diag = RemSound.Core.DiagnosticsGate.Enabled;
        var workStart = diag ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
        try
        {
            // 1. Reinterpret captured bytes as floats. Only IeeeFloat is supported (see Start).
            var sourceFloatCount = e.BytesRecorded / sizeof(float);
            if (sourceFloatScratch.Length < sourceFloatCount)
                sourceFloatScratch = new float[sourceFloatCount];
            // MemoryMarshal.Cast avoids a copy where layout permits, but e.Buffer is byte[] and we
            // need the floats indexable so we copy into our scratch. Copy is cheap: 7680 bytes for
            // 10 ms at 96 kHz stereo float.
            Buffer.BlockCopy(e.Buffer, 0, sourceFloatScratch, 0, e.BytesRecorded);
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
                // resampler's lookahead). The resampler is fed exactly what it needs and tells us
                // how many output frames it actually produced; any input we couldn't feed in this
                // iteration is held in its internal state for next callback.
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

            // 4. Soft clamp at the encoder boundary (matches MixingEngine / AsioCaptureBackend).
            var stereoFloatCount = workingFrames * MixChannels;
            for (var i = 0; i < stereoFloatCount; i++)
            {
                var v = stereo[i];
                if (v > 1f) { stereo[i] = 1f; Interlocked.Increment(ref clippedSampleCount); }
                else if (v < -1f) { stereo[i] = -1f; Interlocked.Increment(ref clippedSampleCount); }
            }

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

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            lastError = e.Exception.Message;
            onDiagnostic?.Invoke($"push-wasapi: capture stopped with error: {e.Exception.GetType().Name}: {e.Exception.Message}");
        }
    }
}
