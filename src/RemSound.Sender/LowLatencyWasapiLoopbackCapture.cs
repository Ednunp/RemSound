using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RemSound.Core;

namespace RemSound.Sender;

/// <summary>
/// WASAPI loopback capture with a short buffer and a capture loop we own, so a device whose
/// loopback event never signals still delivers every frame.
///
/// This used to be a three-line subclass of NAudio's <see cref="WasapiCapture"/> with
/// <c>useEventSync: true</c>. That combination — AUDCLNT_STREAMFLAGS_EVENTCALLBACK together with
/// AUDCLNT_STREAMFLAGS_LOOPBACK — is not a supported pairing: a loopback stream is fed by the
/// render endpoint, and plenty of drivers (a generic "High Definition Audio Device" among them)
/// never signal the client's event at all. Initialize still succeeds, so nothing fails loudly; the
/// capture just goes quiet on the event.
///
/// NAudio's loop then falls back to its wait timeout, and that fallback is the trap:
///
///   waitMilliseconds = 3 * bufferDuration;      // NAudio WasapiCapture.DoRecording
///   frameEventWaitHandle.WaitOne(waitMilliseconds);
///   ReadNextPacket(capture);                    // can only retrieve ONE buffer's worth
///
/// Wait three buffers, drain one — WASAPI has long since overwritten the rest. The result is
/// exactly one third of the audio, deterministically, whatever the buffer size: a 20 ms buffer
/// polled every 60 ms. It reaches the far end as a 16 Hz stutter with the receiver's jitter buffer
/// pinned at empty (diagnosed 2026-09-09 from HUGHES-PC: 16.5 capture callbacks/s, 4.00 PCM frames
/// of 5 ms each per callback, 33.0% duty cycle, and a matching 16.64 Hz peak in the modulation
/// spectrum of a recording made at the far end).
///
/// So we run the loop ourselves. It is NAudio's loop with one substantive change — the wait timeout
/// is half a buffer instead of three, and the drain happens whether or not the event fired:
///
///   * where the loopback event DOES signal (most modern drivers) the wait returns at the device
///     period and we stay hardware-clocked, which is what <see cref="PushModeWasapiBackend"/> is
///     built around;
///   * where it does not, we poll at half the buffer and still never lose a frame — the same
///     safety margin NAudio's own non-event path uses.
///
/// Two things come along for the ride, both of which the push path was missing because NAudio
/// starts its capture thread as a plain background thread: MMCSS "Pro Audio" scheduling and a 1 ms
/// system timer, so the poll timeout means what it says rather than rounding up to the default
/// ~15.6 ms tick. In push mode the encode and UDP send run on this thread, so its priority matters
/// as much as the mix thread's did.
///
/// One second in, the loop logs whether the event is actually driving it. That line is the one the
/// old log could not print, and the only cheap way to tell a healthy device from this failure mode.
///
/// We do not own the <see cref="MMDevice"/> — the caller does — so we never dispose it.
/// </summary>
internal sealed class LowLatencyWasapiLoopbackCapture : IWaveIn
{
    private const long ReftimesPerMillisec = 10000;

    private readonly MMDevice device;
    private readonly int requestedBufferMs;
    private readonly Action<string>? onDiagnostic;
    private readonly SynchronizationContext? syncContext;

    private AudioClient? audioClient;
    /// <summary>How long Dispose waits for the capture thread before abandoning it. Generous next to
    /// one poll timeout plus a drain, so it only bites when a WASAPI call is genuinely not returning.</summary>
    private const int JoinTimeoutMs = 2000;

    private EventWaitHandle? frameEventWaitHandle;
    private WaveFormat? mixFormat;
    private byte[] recordBuffer = [];
    private int bytesPerFrame;
    private int pollTimeoutMs;
    private bool initialized;

    private Thread? captureThread;
    private volatile CaptureState captureState;

    // Wake-up bookkeeping for the one-shot sync-mode report. Touched only by the capture thread.
    private long eventWakeups;
    private long timeoutWakeups;
    private long discontinuities;

    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public LowLatencyWasapiLoopbackCapture(MMDevice device, int audioBufferMilliseconds = 10, Action<string>? onDiagnostic = null)
    {
        this.device = device;
        this.onDiagnostic = onDiagnostic;
        requestedBufferMs = Math.Clamp(audioBufferMilliseconds, 5, 200);
        syncContext = SynchronizationContext.Current;
        captureState = CaptureState.Stopped;
    }

    /// <summary>The device's mix format, reduced to a standard PCM / IEEE-float format the way
    /// NAudio's <see cref="WasapiCapture.WaveFormat"/> does — callers switch on
    /// <see cref="WaveFormat.Encoding"/>, which a raw WAVEFORMATEXTENSIBLE reports as Extensible.
    /// The setter exists only to satisfy <see cref="IWaveIn"/>; loopback capture always runs at the
    /// device's own mix format, so accepting anything else would be a lie.</summary>
    public WaveFormat WaveFormat
    {
        get
        {
            EnsureFormat();
            return mixFormat!.AsStandardWaveFormat();
        }
        set => throw new InvalidOperationException(
            "Loopback capture format is fixed to the device mix format and cannot be assigned.");
    }

    public CaptureState CaptureState => captureState;

    public void StartRecording()
    {
        if (captureState != CaptureState.Stopped)
        {
            throw new InvalidOperationException("Previous recording still in progress");
        }
        captureState = CaptureState.Starting;
        try
        {
            Initialize();
        }
        catch
        {
            captureState = CaptureState.Stopped;
            throw;
        }
        captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "RemSound WASAPI loopback capture",
        };
        captureThread.Start();
    }

    public void StopRecording()
    {
        if (captureState != CaptureState.Stopped) captureState = CaptureState.Stopping;
    }

    public void Dispose()
    {
        StopRecording();
        // Join before releasing the client — the capture thread is still calling into it.
        //
        // BOUNDED, unlike NAudio's. In the normal case this returns within one poll timeout plus a
        // drain, so the bound never comes into play. It exists for the case where it does: a device
        // pulled mid-capture can leave a WASAPI call not returning, and an unbounded Join here would
        // hang whatever is disposing us — which on the shutdown path is the app closing. A hang is
        // strictly worse than a leaked thread, because a hang names nothing and a leak is logged.
        var thread = captureThread;
        if (thread is not null && thread != Thread.CurrentThread)
        {
            try
            {
                if (!thread.Join(JoinTimeoutMs))
                    onDiagnostic?.Invoke($"loopback capture thread did not stop within {JoinTimeoutMs} ms — "
                        + "abandoning it rather than blocking shutdown (the device is most likely gone)");
            }
            catch { /* ignore */ }
        }
        captureThread = null;
        if (audioClient is not null)
        {
            try { audioClient.Dispose(); } catch { /* ignore */ }
            audioClient = null;
        }
        if (frameEventWaitHandle is not null)
        {
            try { frameEventWaitHandle.Dispose(); } catch { /* ignore */ }
            frameEventWaitHandle = null;
        }
        initialized = false;
    }

    /// <summary>MMDevice.AudioClient activates a NEW client on every read, so take one only once
    /// and hold it — reading the property in a loop would leak a COM object per call.</summary>
    private void EnsureFormat()
    {
        audioClient ??= device.AudioClient;
        mixFormat ??= audioClient.MixFormat;
    }

    private void Initialize()
    {
        if (initialized) return;
        EnsureFormat();

        // Ask for the event even though loopback isn't guaranteed to deliver it: where it works we
        // want to be woken by the device clock rather than by a timer. The loop below doesn't
        // depend on it. Shared mode with EventCallback requires periodicity 0.
        var requestedDuration = ReftimesPerMillisec * requestedBufferMs;
        try
        {
            audioClient!.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.Loopback,
                requestedDuration, 0, mixFormat!, Guid.Empty);
            frameEventWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset);
            audioClient.SetEventHandle(frameEventWaitHandle.SafeWaitHandle.DangerousGetHandle());
        }
        catch (Exception ex)
        {
            // Some drivers reject EventCallback on a loopback stream outright rather than accepting
            // it and never signalling. An IAudioClient can't be re-Initialized, so start over with a
            // fresh one and no event — the loop polls, which is all we needed from it anyway.
            onDiagnostic?.Invoke(
                $"loopback capture: event-sync init rejected ({ex.GetType().Name}: {ex.Message}) — falling back to polled capture");
            try { audioClient!.Dispose(); } catch { /* ignore */ }
            frameEventWaitHandle = null;
            audioClient = device.AudioClient;
            audioClient.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback,
                requestedDuration, 0, mixFormat!, Guid.Empty);
        }

        var bufferFrameCount = audioClient!.BufferSize;
        bytesPerFrame = mixFormat!.Channels * mixFormat.BitsPerSample / 8;
        recordBuffer = new byte[bufferFrameCount * bytesPerFrame];

        // Windows hands back whatever buffer it likes — asking for 10 ms commonly yields 20 ms — so
        // pace off the buffer we actually got, not the one we asked for. Half a buffer leaves a full
        // half-buffer of slack for a late wake-up before anything is overwritten.
        var bufferMs = (int)Math.Round(bufferFrameCount * 1000.0 / mixFormat.SampleRate);
        // Through the seam rather than inline. The gate drives WasapiCaptureLoop against a simulated
        // device and measures 33 % against the old policy and 100 % against this one — but that only
        // means anything if the shipped code asks the same function the test asks. An inline copy of
        // the same arithmetic would leave the test proving something the product does not use, which
        // is the mistake that let this bug live for years. See WasapiCaptureLoop.
        pollTimeoutMs = WasapiCaptureLoop.PollTimeoutMs(bufferMs);
        initialized = true;
    }

    private void CaptureLoop()
    {
        // Pro Audio scheduling, and a fine system timer so a 5-10 ms poll timeout isn't rounded up
        // to the default ~15.6 ms tick. Both are what MixingEngine's mix thread already held; the
        // push path lost them when the mix tick went away, because NAudio starts its capture thread
        // at plain background priority. Encode and UDP send run on this thread in push mode.
        using var threadBoost = new WindowsAudioThreadBoost("Pro Audio");
        using var timerResolution = new SystemTimerResolution(1);

        Exception? error = null;
        var client = audioClient!;
        try
        {
            using var capture = client.AudioCaptureClient;
            client.Start();
            if (captureState == CaptureState.Starting) captureState = CaptureState.Capturing;

            var started = Stopwatch.GetTimestamp();
            var reported = false;
            while (captureState == CaptureState.Capturing)
            {
                if (frameEventWaitHandle is not null)
                {
                    if (frameEventWaitHandle.WaitOne(pollTimeoutMs)) eventWakeups++;
                    else timeoutWakeups++;
                }
                else
                {
                    Thread.Sleep(pollTimeoutMs);
                    timeoutWakeups++;
                }
                if (captureState != CaptureState.Capturing) break;

                // Unconditional. Whether we got here on the event or on the timeout, the device may
                // be holding frames, and anything we don't take now is overwritten.
                Drain(capture);

                if (!reported && Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(1))
                {
                    reported = true;
                    ReportSyncMode();
                }
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            // The device may already be gone (unplugged mid-capture), in which case Stop itself
            // throws — swallow it so RecordingStopped still fires and this thread never dies with an
            // unhandled exception that would take the process with it.
            try { client.Stop(); } catch { /* device already gone */ }
        }
        captureState = CaptureState.Stopped;
        RaiseRecordingStopped(error);
    }

    private void Drain(AudioCaptureClient capture)
    {
        var packetFrames = capture.GetNextPacketSize();
        var offset = 0;
        while (packetFrames != 0)
        {
            var buffer = capture.GetBuffer(out var framesAvailable, out var flags);
            var bytesAvailable = framesAvailable * bytesPerFrame;
            if (bytesAvailable > 0)
            {
                if ((flags & AudioClientBufferFlags.DataDiscontinuity) == AudioClientBufferFlags.DataDiscontinuity)
                {
                    discontinuities++;
                }
                if (recordBuffer.Length - offset < bytesAvailable)
                {
                    // Flush what we have rather than straddle two callbacks' worth of audio, then
                    // grow if a single packet is somehow larger than the whole buffer.
                    if (offset > 0)
                    {
                        DataAvailable?.Invoke(this, new WaveInEventArgs(recordBuffer, offset));
                        offset = 0;
                    }
                    if (recordBuffer.Length < bytesAvailable) recordBuffer = new byte[bytesAvailable];
                }
                if ((flags & AudioClientBufferFlags.Silent) == AudioClientBufferFlags.Silent)
                {
                    Array.Clear(recordBuffer, offset, bytesAvailable);
                }
                else
                {
                    Marshal.Copy(buffer, recordBuffer, offset, bytesAvailable);
                }
                offset += bytesAvailable;
            }
            capture.ReleaseBuffer(framesAvailable);
            packetFrames = capture.GetNextPacketSize();
        }
        // Unlike NAudio we don't raise an empty event on a dry poll: polling at half a buffer means
        // plenty of dry wake-ups, and a zero-byte DataAvailable is pure noise to every consumer — it
        // would also make the diag log's captureCallbacks count wake-ups instead of buffers of
        // audio, which is the number that made this bug legible in the first place.
        if (offset > 0) DataAvailable?.Invoke(this, new WaveInEventArgs(recordBuffer, offset));
    }

    private void ReportSyncMode()
    {
        var total = eventWakeups + timeoutWakeups;
        if (total == 0) return;
        var mode = frameEventWaitHandle is null
            ? "polled (no event handle)"
            : eventWakeups * 4 >= total * 3
                ? "event-driven"
                : eventWakeups == 0
                    ? "polled (loopback event never signalled)"
                    : "mixed (loopback event signalling intermittently)";
        var bufferFrames = bytesPerFrame > 0 ? recordBuffer.Length / bytesPerFrame : 0;
        onDiagnostic?.Invoke(
            $"loopback capture sync mode = {mode}; first second: {eventWakeups} event wake-ups, "
            + $"{timeoutWakeups} timeout wake-ups at {pollTimeoutMs} ms, {bufferFrames} frame buffer, "
            + $"{discontinuities} discontinuities");
    }

    private void RaiseRecordingStopped(Exception? e)
    {
        var handler = RecordingStopped;
        if (handler is null) return;
        if (syncContext is null) handler(this, new StoppedEventArgs(e));
        else syncContext.Post(_ => handler(this, new StoppedEventArgs(e)), null);
    }
}
