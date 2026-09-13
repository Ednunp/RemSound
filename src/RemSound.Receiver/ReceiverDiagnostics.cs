using System.Diagnostics;

namespace RemSound.Receiver;

/// <summary>
/// Sub-second telemetry that the App pulls once per second for the log file.
/// Tracks rolling stats so a 1 Hz snapshot reveals what's actually happening
/// at audio-rate resolution. All counters are interlocked or volatile so the
/// network thread, render thread, and App thread can read/write without locks.
///
/// Record* methods are called on the network and render threads; <see cref="Take"/> snapshots
/// and resets the rolling counters once a second. The per-lane render period is read, and reset,
/// separately through <see cref="MaxRenderCallbackGapMsFor"/>.
/// </summary>
public sealed class ReceiverDiagnostics
{
    // Network arrival timing.
    private long lastPacketTicks;
    private long maxArrivalGapTicks;
    private long packetCountSinceLastReport;

    // Buffer sampling. Each Read call records the buffer level it observed.
    // We keep a tiny rolling window so the App can show min/avg/max for the last second.
    private long bufferSampleSumBytes;
    private int bufferSampleCount;
    private int bufferSampleMinBytes = int.MaxValue;
    private int bufferSampleMaxBytes;

    // WASAPI render Read sizes.
    private int maxRenderReadBytes;
    private int renderReadCount;

    // Render-read timing — the parallel of sender's capture-callback gap. On the ASIO lane the
    // engine is read from the driver's own callback, so the period is the ASIO buffer / sample
    // rate; on the WASAPI lane it is read by MultiOutputPlayout's 10 ms producer tick, which feeds
    // each device's buffer. Spikes here mean the thread doing that read is being scheduled with
    // jitter — which manifests as audible discontinuities even when RemSound's playout buffer is
    // healthy, because the audio HARDWARE expects samples on a rigid clock and gets them late.
    // RemSound's "Underruns" counter measures whether RemSound's buffer ran dry; this measures
    // whether the audio device's own buffer was being fed punctually.
    private long lastRenderCallbackTicks;
    private long maxRenderCallbackGapTicks;

    // Sample-step diagnostic. Two quantities:
    //
    // 1. maxSampleStep — the largest |sample[n] - sample[n-1]| in the diag window. Peak
    //    indicator, prone to false positives on bright music. Kept for visibility.
    //
    // 2. spikeCount — adaptive second-derivative outlier detector. A click manifests as a
    //    second derivative |s[i+1] - 2*s[i] + s[i-1]| that's anomalously large *relative to
    //    its recent typical value*. Smooth music has consistent (low) second-derivative
    //    energy. Bright music has consistent (medium) second-derivative energy. A click has
    //    *suddenly* much larger second-derivative energy than recent norm, regardless of
    //    overall content level.
    //
    // The detector tracks an EMA of |second derivative| over ~64 samples (~1.3 ms at 48 kHz)
    // and flags samples whose own second-derivative exceeds that EMA by a multiplier.
    // Multiplier of 5× = "this sample's discontinuity is 5× louder than the recent local
    // discontinuity baseline." Plus an absolute floor so quiet-content noise doesn't trip it.
    //
    // Behaviour by signal type:
    //   - Silence: zero second derivative → spikeCount stays 0.
    //   - Smooth tone: low, consistent 2nd derivative → ratio ~1 → spikeCount stays 0.
    //   - Bright tone: high but consistent 2nd derivative → ratio ~1 → spikeCount stays 0.
    //   - Click on top of any of the above: 2nd derivative spikes for one sample, ratio >> 5
    //     → spikeCount increments by 1 per click sample.
    private const float SpikeEnvAlpha = 1f / 64f;          // ~1.3 ms half-life at 48 kHz
    private const float SpikeRatioThreshold = 5.0f;        // sample's 2nd-deriv must be 5× recent norm
    private const float SpikeAbsoluteFloor = 0.02f;        // ignore near-silence noise
    private float maxSampleStep;
    private int spikeCount;
    private float secondDerivEMA;                          // running average of |2nd derivative|
    private bool spikeStateSeeded;
    private float prevPrevSample;                          // s[i-2] when computing s[i]
    // Last sample of the previous Read call — used as the seed for the first sample of the
    // next Read so step measurement spans Read boundaries (otherwise we'd miss clicks that
    // sit on the boundary between two Reads, which is exactly where buffer-edge clicks live).
    private float lastWrittenSample;
    private bool lastSampleSeeded;

    public void RecordPacketArrived()
    {
        // Diagnostics-gate first. packetCountSinceLastReport feeds the SNAP diag line too so
        // it isn't worth the audio-thread cost to keep incrementing it when nobody is reading.
        if (!RemSound.Core.DiagnosticsGate.Enabled) return;
        var now = Stopwatch.GetTimestamp();
        var prev = Interlocked.Exchange(ref lastPacketTicks, now);
        if (prev != 0)
        {
            var gap = now - prev;
            // Track max gap (lock-free max via CAS).
            long currentMax;
            do { currentMax = Volatile.Read(ref maxArrivalGapTicks); }
            while (gap > currentMax && Interlocked.CompareExchange(ref maxArrivalGapTicks, gap, currentMax) != currentMax);
        }
        Interlocked.Increment(ref packetCountSinceLastReport);
    }

    /// <summary>
    /// Zeros the inter-packet and render-callback timestamps so the next sample taken doesn't
    /// measure a gap across a stream-session boundary. Called from <c>AudioReceiver</c>
    /// whenever a new <c>StreamSession</c> opens — without this, the first packet of the new
    /// session would record a gap equal to the entire idle duration between the previous
    /// session ending and this one starting (potentially tens of seconds), poisoning the
    /// auto-tune's recent-gap window and causing it to recommend an absurdly large latency
    /// target. The same applies to the render-callback timing: a new audio output device or
    /// re-opened ASIO driver should start its own gap measurement, not inherit one from the
    /// previous backend's last render.
    /// </summary>
    public void ResetGapMeasurements()
    {
        Interlocked.Exchange(ref lastPacketTicks, 0);
        Interlocked.Exchange(ref maxArrivalGapTicks, 0);
        Interlocked.Exchange(ref lastRenderCallbackTicks, 0);
        Interlocked.Exchange(ref maxRenderCallbackGapTicks, 0);
    }

    public void RecordBufferLevel(int bufferedBytes)
    {
        if (!RemSound.Core.DiagnosticsGate.Enabled) return;
        Interlocked.Add(ref bufferSampleSumBytes, bufferedBytes);
        Interlocked.Increment(ref bufferSampleCount);
        // Track min/max via CAS.
        int curMin;
        do { curMin = Volatile.Read(ref bufferSampleMinBytes); }
        while (bufferedBytes < curMin && Interlocked.CompareExchange(ref bufferSampleMinBytes, bufferedBytes, curMin) != curMin);
        int curMax;
        do { curMax = Volatile.Read(ref bufferSampleMaxBytes); }
        while (bufferedBytes > curMax && Interlocked.CompareExchange(ref bufferSampleMaxBytes, bufferedBytes, curMax) != curMax);
    }

    // Per-LANE render-callback timing. The single global below is whichever lane fired last, which
    // is fine for a mixed setup and WRONG the moment two lanes run at once: the WASAPI lane's
    // shared-mode period (~10-11 ms, whatever Windows grants) would be attributed to an ASIO listener
    // whose audio never goes near it. ASIO's whole point is that it is NOT penalised by WASAPI's
    // buffering (Ed, 2026-08-15) — so the latency estimate must read the period of the lane the user
    // is actually listening on. Indexed by RenderRoute.
    private readonly long[] lastRenderCbTicksByRoute = new long[8];
    private readonly long[] maxRenderCbGapTicksByRoute = new long[8];

    /// <summary>The measured render-callback period for ONE lane, in ms — 0 when that lane hasn't
    /// rendered. Read (and reset) alongside the snapshot.</summary>
    public int MaxRenderCallbackGapMsFor(RemSound.Core.RenderRoute route)
    {
        var idx = (int)route & 7;
        var ticks = Interlocked.Exchange(ref maxRenderCbGapTicksByRoute[idx], 0);
        return (int)(ticks * 1000.0 / Stopwatch.Frequency);
    }

    /// <summary>Record a render callback for a specific lane, so per-lane periods stay separate.</summary>
    public void RecordRenderRead(int bytesRequested, RemSound.Core.RenderRoute route)
    {
        RecordRenderRead(bytesRequested);
        if (!RemSound.Core.DiagnosticsGate.Enabled) return;
        var idx = (int)route & 7;
        var now = Stopwatch.GetTimestamp();
        var prev = Interlocked.Exchange(ref lastRenderCbTicksByRoute[idx], now);
        if (prev == 0) return;
        var gap = now - prev;
        long cur;
        do { cur = Volatile.Read(ref maxRenderCbGapTicksByRoute[idx]); }
        while (gap > cur && Interlocked.CompareExchange(ref maxRenderCbGapTicksByRoute[idx], gap, cur) != cur);
    }

    public void RecordRenderRead(int bytesRequested)
    {
        if (!RemSound.Core.DiagnosticsGate.Enabled) return;
        Interlocked.Increment(ref renderReadCount);
        int curMax;
        do { curMax = Volatile.Read(ref maxRenderReadBytes); }
        while (bytesRequested > curMax && Interlocked.CompareExchange(ref maxRenderReadBytes, bytesRequested, curMax) != curMax);

        // Track the gap since the previous render callback. First call seeds the timestamp
        // without recording a gap (no prior reference). Lock-free max-update via CAS.
        var now = Stopwatch.GetTimestamp();
        var prev = Interlocked.Exchange(ref lastRenderCallbackTicks, now);
        if (prev != 0)
        {
            var gap = now - prev;
            long currentMax;
            do { currentMax = Volatile.Read(ref maxRenderCallbackGapTicks); }
            while (gap > currentMax && Interlocked.CompareExchange(ref maxRenderCallbackGapTicks, gap, currentMax) != currentMax);
        }
    }

    /// <summary>Scan a span of float samples that RemSound is about to hand to NAudio and
    /// record (a) the peak sample-to-sample step and (b) an adaptive count of second-
    /// derivative outliers — samples whose discontinuity is anomalously large relative to
    /// recent local norm. The latter is the click-specific signal: it's content-INVARIANT,
    /// triggering only when a sample really does break the local audio's predictability.</summary>
    public void RecordOutputSampleSteps(ReadOnlySpan<float> samples)
    {
        // Most expensive probe in the engine — per-sample second-derivative arithmetic on
        // every render block. Gate it at the top so the render thread doesn't pay any of
        // this when nobody is going to read the column.
        if (!RemSound.Core.DiagnosticsGate.Enabled) return;
        if (samples.IsEmpty) return;
        var localMax = maxSampleStep;
        var localSpikes = spikeCount;
        var prev = lastSampleSeeded ? lastWrittenSample : samples[0];
        var prevPrev = spikeStateSeeded ? prevPrevSample : prev;
        // Initial seed for the EMA on first-ever call: small positive value so the first
        // few samples can't all register as anomalies before the EMA has a chance to learn.
        var derivEMA = spikeStateSeeded ? secondDerivEMA : 0.01f;
        var oneMinusAlpha = 1f - SpikeEnvAlpha;
        for (var i = 0; i < samples.Length; i++)
        {
            var cur = samples[i];
            var step = cur - prev;
            if (step < 0) step = -step;
            if (step > localMax) localMax = step;

            // Second derivative: |s[i] - 2*s[i-1] + s[i-2]|. Smooth audio has low and
            // consistent values; a click introduces a sudden large value at one sample.
            var d2 = cur - 2f * prev + prevPrev;
            if (d2 < 0f) d2 = -d2;
            // Spike detector: anomalously high second derivative relative to recent norm.
            // The absolute floor (0.02) prevents counting in near-silence where the EMA
            // is tiny and any small sample noise would technically exceed N× the EMA.
            // Math.Max ensures we don't divide-by-zero or trip on EMA close to 0.
            var dynamicThreshold = Math.Max(derivEMA * SpikeRatioThreshold, SpikeAbsoluteFloor);
            if (d2 > dynamicThreshold)
            {
                localSpikes++;
                // Update the EMA WITHOUT folding this anomaly in (so a click doesn't poison
                // the baseline and mask subsequent clicks). Re-feed the EMA with its current
                // value, effectively a no-op update on click samples.
            }
            else
            {
                // Update EMA only on non-anomalous samples — keeps the baseline tracking
                // smooth audio character, not click events.
                derivEMA = derivEMA * oneMinusAlpha + d2 * SpikeEnvAlpha;
            }

            prevPrev = prev;
            prev = cur;
        }
        maxSampleStep = localMax;
        spikeCount = localSpikes;
        secondDerivEMA = derivEMA;
        prevPrevSample = prevPrev;
        spikeStateSeeded = true;
        lastWrittenSample = prev;
        lastSampleSeeded = true;
    }

    /// <summary>
    /// Snapshot the rolling counters and reset them. Called by the App once per second.
    /// </summary>
    public DiagSnapshot Take(int mixBytesPerSecond)
    {
        var maxGapTicks = Interlocked.Exchange(ref maxArrivalGapTicks, 0);
        var pktCount = Interlocked.Exchange(ref packetCountSinceLastReport, 0);
        var sumBytes = Interlocked.Exchange(ref bufferSampleSumBytes, 0);
        var sampleCount = Interlocked.Exchange(ref bufferSampleCount, 0);
        var minBytes = Interlocked.Exchange(ref bufferSampleMinBytes, int.MaxValue);
        var maxBytes = Interlocked.Exchange(ref bufferSampleMaxBytes, 0);
        var maxReadBytes = Interlocked.Exchange(ref maxRenderReadBytes, 0);
        var readCount = Interlocked.Exchange(ref renderReadCount, 0);
        var maxRenderCbGap = Interlocked.Exchange(ref maxRenderCallbackGapTicks, 0);
        // Sample-step is read from the render thread (which is the only writer); diag thread
        // reads + zeroes. The reader sees a slightly stale value if a Read is in flight, which
        // is fine — values will fold into the next snapshot.
        var maxStep = maxSampleStep;
        maxSampleStep = 0f;
        var bigSteps = spikeCount;
        spikeCount = 0;

        var ticksToMsScale = 1000.0 / Stopwatch.Frequency;
        return new DiagSnapshot(
            PacketCount: pktCount,
            MaxArrivalGapMs: (int)(maxGapTicks * ticksToMsScale),
            BufferAvgMs: sampleCount > 0 ? (int)(sumBytes / sampleCount * 1000.0 / mixBytesPerSecond) : 0,
            BufferMinMs: minBytes == int.MaxValue ? 0 : (int)(minBytes * 1000.0 / mixBytesPerSecond),
            BufferMaxMs: (int)(maxBytes * 1000.0 / mixBytesPerSecond),
            BufferSampleCount: sampleCount,
            MaxRenderReadMs: (int)(maxReadBytes * 1000.0 / mixBytesPerSecond),
            MaxRenderCallbackGapMs: (int)(maxRenderCbGap * ticksToMsScale),
            RenderReadCount: readCount,
            MaxOutputSampleStep: maxStep,
            EnvelopeSpikeCount: bigSteps);
    }

    public readonly record struct DiagSnapshot(
        long PacketCount,
        int MaxArrivalGapMs,
        int BufferAvgMs,
        int BufferMinMs,
        int BufferMaxMs,
        int BufferSampleCount,
        int MaxRenderReadMs,
        int MaxRenderCallbackGapMs,
        int RenderReadCount,
        float MaxOutputSampleStep,
        int EnvelopeSpikeCount);
}
