using NAudio.Wave;
using RemSound.Core;
using RemSound.Sender;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>
    /// TWO CAPTURE SOURCES IN ONE STREAM MUST STAY ALIGNED.
    ///
    /// <para>Anthony Reyers, 2026-09: individual WASAPI sources summed into one stream get out of
    /// alignment. He is right, and <c>CaptureSource</c>'s own class comment had said so all along —
    /// "Per-source clock drift across independent audio devices IS unavoidable ... A proper
    /// drift-correcting micro-resample is a future addition."</para>
    ///
    /// <para>Every device has its own crystal and the mix loop pulls a fixed 10 ms per tick on a
    /// Stopwatch belonging to none of them. Nothing corrected that: a source running fast filled its
    /// 250 ms ring until the oldest audio was discarded (a jump); a source running slow emptied it and
    /// was padded with silence (a hole). Neither happens until the error is already a quarter of a
    /// second, and the relative offset between two sources grows from the first second.</para>
    ///
    /// <para>So this measures what the name promises: the offset between two sources in one mix. Two
    /// devices, one 500 ppm fast and one 500 ppm slow, go through one real mixing engine with the shipped
    /// ten-second window, and each writes the moment it captured every sample into its own side of the
    /// stereo pair - so each mixed frame says, to a small fraction of a millisecond, how far apart in time
    /// the two things summed into it really were. It runs ten minutes on this thread's audio clock
    /// (AudioClock, as the simulator does), which takes about a second. Until 2026-09-28 it ran four
    /// seconds of wall clock through one corrector fed a made-up ring, and never measured an offset at
    /// all.</para>
    ///
    /// <para>And it requires a lone source to be left exactly alone, because that is the setup almost
    /// everybody has and correcting it would be all risk and no benefit.</para>
    ///
    /// <para>Not per-configuration: this is the WASAPI capture mixer. ASIO capture is a separate send
    /// lane fed straight from one driver callback — one clock, no mixing engine, nothing to drift
    /// against — and the plugin lane likewise rides the DAW's clock. Those are different streams, so
    /// no configuration puts them in the same mix.</para>
    /// </summary>
    private static string? AuditCaptureSourcesStayAligned()
    {
        // The SHIPPED window is what runs below: on a simulated clock ten seconds cost nothing, so the
        // test no longer needs a short window of its own - and a changed default is caught here too.
        Check(CaptureDriftCorrector.DefaultMeasurementWindowSec == 10.0,
            $"the shipped measurement window must stay 10 s, matching the receive side and the plugin lane (got "
            + $"{CaptureDriftCorrector.DefaultMeasurementWindowSec}) — shorter and one lumpy callback reads as a clock error");

        CheckDriftWindowsRunOnTheAudioClock();
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        using var clock = new StoppedAudioClock();
        using var engine = new MixingEngine(_ => { });
        var fast = new TimestampingDevice(format, ppm: +500, channel: 0);
        var slow = new TimestampingDevice(format, ppm: -500, channel: 1);
        var fastSource = new CaptureSource(fast, CaptureKind.Input, "gate:fast", "a device 500 ppm fast");
        var slowSource = new CaptureSource(slow, CaptureKind.Input, "gate:slow", "a device 500 ppm slow");
        engine.AddSourceForTest(fastSource);
        engine.AddSourceForTest(slowSource);

        // Each mixed block, 10 ms at a time as the mix loop pulls it. The left of its middle frame is when the fast device
        // captured it, the right when the slow one did: the difference is how far apart the two sources are in the mix.
        var mix = new float[480 * 2];
        var before20s = 0.0;
        var worstLate = 0.0;
        var lastOutOfLine = 0.0;
        var shortBlocks = 0;
        const int ticks = 10 * 60 * 100;       // ten simulated minutes of 10 ms mix ticks
        const double lateFrom = 3 * 60;        // in line from the third minute on
        for (var tick = 1; tick <= ticks; tick++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(10));
            fast.CaptureUntil(clock.Seconds);
            slow.CaptureUntil(clock.Seconds);
            if (engine.ReadMixForTest(mix) != mix.Length) shortBlocks++;
            var offsetMs = (mix[480] - mix[481]) * 1000.0;
            if (tick == 1950) before20s = offsetMs;
            if (Math.Abs(offsetMs) >= 1.0) lastOutOfLine = clock.Seconds;
            if (clock.Seconds >= lateFrom) worstLate = Math.Max(worstLate, Math.Abs(offsetMs));
        }

        Check(shortBlocks == 0, $"the mix must hand back a full block every tick ({shortBlocks} short)");
        // The premise: the two really do run apart until the second window closes (the first is start-up fill, thrown away),
        // a millisecond a second between them. Without it the offset below would prove nothing.
        Check(Math.Abs(before20s) is > 15 and < 25,
            $"premise: before the corrector has a measurement, sources 1000 ppm apart must drift about a millisecond a second "
            + $"apart (at 19.5 s they were {before20s:0.0} ms apart)");
        Check(worstLate < 1.0,
            $"two sources in one mix must stay within a millisecond of each other once corrected: from the third minute to the "
            + $"tenth they were as much as {worstLate:0.000} ms apart (last out of line at {lastOutOfLine:0} s)");
        Check(lastOutOfLine < lateFrom,
            $"and they must have come into line within three minutes (last a millisecond or more apart at {lastOutOfLine:0} s)");
        var fastPpm = (fastSource.AppliedDriftRatio - 1.0) * 1e6;
        var slowPpm = (slowSource.AppliedDriftRatio - 1.0) * 1e6;
        Check(Math.Abs(fastPpm - 500) < 100 && Math.Abs(slowPpm + 500) < 100,
            $"each corrector must hold its own device on the mix clock - the fast one pulled about 500 ppm faster, the slow one "
            + $"about 500 ppm slower (got {fastPpm:+0;-0} and {slowPpm:+0;-0} ppm)");
        engine.Stop();

        // A LONE SOURCE IS LEFT ALONE. Nothing to align with, so correcting is all risk - even when it runs fast.
        using var aloneEngine = new MixingEngine(_ => { });
        var only = new TimestampingDevice(format, ppm: +500, channel: 0);
        var alone = new CaptureSource(only, CaptureKind.Input, "gate:only", "the only device");
        aloneEngine.AddSourceForTest(alone);
        for (var tick = 1; tick <= 60 * 100; tick++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(10));
            only.CaptureUntil(clock.Seconds);
            aloneEngine.ReadMixForTest(mix);
        }
        var aloneCorrector = Require(alone.DriftCorrector, "a capture source must have a drift corrector");
        Check(alone.AppliedDriftRatio == 1.0 && !aloneCorrector.IsTracking,
            $"a LONE capture source must be left exactly alone (ratio {alone.AppliedDriftRatio:0.000000} after a simulated minute "
            + "500 ppm fast) — it has nothing to stay aligned with, and resampling the commonest setup of all would be pure risk "
            + "for no benefit");
        aloneEngine.Stop();

        return $"two sources 1000 ppm apart drifted {Math.Abs(before20s):0.0} ms apart in the first 20 s, came into line by "
             + $"{lastOutOfLine:0} s and stayed within {worstLate:0.000} ms of each other to the tenth simulated minute "
             + $"(corrected {fastPpm:+0;-0} and {slowPpm:+0;-0} ppm); a lone source is left untouched at ratio 1.0";
    }

    /// <summary>
    /// THE ONE DRIFT LOOP, AND ITS FOUR HARD-WON RULES.
    ///
    /// <para>Three places had their own copy of this arithmetic: the receive side's device output, the
    /// plugin sender's second-DAW lane, and the capture-source corrector. They were folded onto
    /// <see cref="DriftRatioTracker"/> on 2026-09-07, and the reason was not tidiness. The newest copy
    /// had been written without three of the rules below, and nothing would have caught that — each
    /// caller's own test proves ITS behaviour, and a missing safety rule only shows up in the field, as
    /// a glitch that drags the rate for a minute afterwards.</para>
    ///
    /// <para>So the rules are pinned HERE, once, on the shared type. Every caller inherits them: break
    /// any one of these and all three lanes go red together, which is exactly the point of there being
    /// one copy.</para>
    ///
    /// <para>Not per-configuration. This is arithmetic over two byte counters — there is no device, no
    /// driver and no output lane in it. What differs between ASIO and WASAPI is which callers use it:
    /// the receive-side device output is WASAPI-only (ASIO pulls straight from the engine and has no
    /// buffer of its own to hold flat), while the two sender-side callers are on the capture path,
    /// which is upstream of every output choice. Those differences are covered by each caller's own
    /// test; this one covers what they share.</para>
    /// </summary>
    private static string? AuditSharedDriftLoopRules()
    {
        const int rate = 48000, channels = 2;
        var bytesPerSec = (long)rate * channels * sizeof(float);
        var target = rate * 12 / 1000;   // a 12 ms cushion, the long-proven receive-side value

        // The shipped numbers. Every caller now inherits these, so a change here is a change to the
        // receive side that has held real buffers flat for months — it must be deliberate.
        Check(DriftRatioTracker.MeasurementWindowSec == 10.0,
            $"the measurement window must stay 10 s (got {DriftRatioTracker.MeasurementWindowSec}) — shorter and one "
            + "lumpy callback reads as a clock error");
        Check(DriftRatioTracker.RatioSmoothingNew == 0.30,
            $"smoothing must stay 70/30 (got {DriftRatioTracker.RatioSmoothingNew}) so no single window can swing the rate");
        Check(DriftRatioTracker.RatioMin == 0.95 && DriftRatioTracker.RatioMax == 1.05,
            "the sanity band must stay ±5 % — it is what separates a clock from a glitch");
        Check(DriftRatioTracker.MaxDepthBias == 0.003,
            $"the depth nudge must stay capped at 0.3 % (got {DriftRatioTracker.MaxDepthBias}) — above that it stops "
            + "being a sub-audible pitch trim and becomes something you can hear");

        // RULE 1: THE FIRST WINDOW IS DISCARDED. It is start-up fill, not drift. On the receive side it
        // measured −1199 ppm, and that shoved the buffer off target for the window after it.
        //
        // The bogus figure below is deliberately INSIDE the sanity band, because the real one was: a
        // −1199 ppm reading is 0.9988, nowhere near ±5 %. Use a wild out-of-band number here and the
        // sanity gate rejects it, the discard is never exercised, and the check passes whether the
        // discard exists or not. (It was written that way first, and deleting the discard left the
        // gate green — which is exactly what "prove it can fail" is for.)
        var t = new DriftRatioTracker(rate, windowSec: 0.05);
        long fed = 0, drained = 0;
        t.Update(fed, drained, target, target);                 // anchors the window; measures nothing yet
        fed += (long)(bytesPerSec * 1.02); drained += bytesPerSec;   // start-up fill: plausible, and in band
        t.RewindWindowStartForTest(1.0);
        var firstAccepted = t.Update(fed, drained, target, target);
        Check(!firstAccepted && t.AppliedRatio == 1.0 && !t.IsTracking,
            $"the FIRST completed window must be discarded and change nothing (ratio {t.AppliedRatio:0.000000}) — it "
            + "contains the buffer filling from empty, which is not a clock error however sane the number looks");

        // RULE 2: A READING OUTSIDE THE BAND IS DISCARDED, NOT CLAMPED. This is the one that is easy to
        // get wrong, because clamping looks safe. It is not: a stall or a device hiccup would be folded
        // into the smoother at the band edge and drag the rate for several windows afterwards.
        fed += bytesPerSec; drained += bytesPerSec / 2;         // ratio 2.0 again — a glitch, not a clock
        t.RewindWindowStartForTest(1.0);
        t.Update(fed, drained, target, target);
        Check(t.AppliedRatio == 1.0 && !t.IsTracking,
            $"a measurement outside ±5 % must be DISCARDED, not clamped to the edge (ratio {t.AppliedRatio:0.000000}) — "
            + "clamping feeds a glitch into the smoother and drags the rate for windows afterwards");

        // RULE 3: A VALID READING IS ACCEPTED, WHOLE, ON THE FIRST MEASUREMENT. No smoothing toward it
        // from 1.0 — the first real number is the best estimate there is, and easing into it would
        // leave the buffer drifting for a minute for no reason.
        fed += (long)(bytesPerSec * 1.001); drained += bytesPerSec;   // 1000 ppm fast, well inside the band
        t.RewindWindowStartForTest(1.0);
        var accepted = t.Update(fed, drained, target, target);
        Check(accepted && t.IsTracking,
            "a measurement inside the sanity band must be accepted");
        Check(Math.Abs(t.ClockRatio - 1.001) < 1e-4,
            $"the first valid window must be taken whole, not smoothed up from 1.0 (clock {t.ClockRatio:0.000000}, "
            + "expected about 1.001000)");

        // RULE 4: THE DEPTH NUDGE IS BOUNDED. A buffer that is absurdly deep must still only be walked
        // back gently — the whole point is that the correction is never audible.
        var deep = rate;   // a full second too deep, far beyond anything the cap should allow through
        t.RewindWindowStartForTest(1.0);
        fed += (long)(bytesPerSec * 1.001); drained += bytesPerSec;
        t.Update(fed, drained, deep, target);
        Check(Math.Abs(t.DepthCorrection - DriftRatioTracker.MaxDepthBias) < 1e-9,
            $"a hugely over-deep buffer must clamp to the 0.3 % cap, not chase the error (got {t.DepthCorrection:0.000000})");
        Check(t.DepthCorrection > 0,
            "a buffer deeper than target must bias the rate UP, so the consumer pulls more and the depth comes down");

        // AND THE ORDERING THE TWO DEVICE CALLERS DEPEND ON. Both size their wanted cushion from the
        // window that just ended, so they take the update in two steps and do that sizing in between.
        // ApplyMeasurement must therefore refuse to run unless a window really did just close, or a
        // stray call would count one window twice.
        Check(!t.ApplyMeasurement(target, target),
            "ApplyMeasurement must do nothing unless a window has just closed — otherwise a second call would fold the "
            + "same window into the ratio twice");

        var settled = t.ClockRatio;
        t.Reset();
        Check(t.AppliedRatio == 1.0 && t.ClockRatio == 1.0 && !t.IsTracking,
            $"Reset must forget everything (clock {t.ClockRatio:0.000000}) — a reopened device or a source that went "
            + "away and came back is a new clock, and the old lesson no longer holds");

        return $"the shared loop discards its start-up window and any out-of-band glitch, takes the first valid reading "
             + $"whole ({settled:0.000000}), caps the depth nudge at 0.3 %, and refuses to count a window twice";
    }

    /// <summary>
    /// SENDING MUST NOT FREEZE WHEN A CAPTURE SOURCE IS ADDED OR REPLACED.
    ///
    /// <para>2026-09-13 review. Every source's drift corrector asks the mixing engine how many sources
    /// are live, on every audio read, from inside NAudio's mixer — which holds the mixer's own lock while
    /// it reads. The engine answered under ITS lock. Adding a source (ticking another one, an
    /// application's capture being replaced when the application restarts, a dead device being reopened)
    /// takes the engine's lock first and the mixer's second. Two threads, each holding the lock the other
    /// is waiting for: sending stops for good, and nothing is logged.</para>
    ///
    /// <para>So this holds the engine's lock exactly as adding a source does, and runs a real mix read on
    /// another thread. The read must finish while the lock is still held.</para>
    ///
    /// <para>Not per-configuration: this is the capture mixer, which sits upstream of every output choice,
    /// and its lock order is the same whatever is ticked.</para>
    /// </summary>
    private static string? AuditMixerNeverWaitsOnItsLockFromTheAudioThread()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        using var engine = new MixingEngine(_ => { });
        var first = new CaptureSource(new FakeCaptureDevice(format), CaptureKind.Input, "gate:first", "first source");
        var second = new CaptureSource(new FakeCaptureDevice(format), CaptureKind.Input, "gate:second", "second source");

        engine.AddSourceForTest(first);
        Check(engine.ActiveSourceCount == 1 && first.CorrectionWanted?.Invoke() == false,
            $"with ONE source the engine must count one and leave correction off (count {engine.ActiveSourceCount}) — a lone "
            + "source has nothing to stay aligned with");
        engine.AddSourceForTest(second);
        Check(engine.ActiveSourceCount == 2 && first.CorrectionWanted?.Invoke() == true,
            $"with TWO sources the engine must count two and switch correction on (count {engine.ActiveSourceCount})");

        var finishedWhileHeld = false;
        var buffer = new float[960];
        engine.HoldLockForTest(() =>
        {
            var audioThread = new Thread(() => engine.ReadMixForTest(buffer)) { IsBackground = true, Name = "gate: mix read" };
            audioThread.Start();
            // Judged INSIDE the lock. A read that needs this lock cannot finish until the lambda returns,
            // so checking afterwards would let it finish in the gap and pass.
            finishedWhileHeld = audioThread.Join(TimeSpan.FromSeconds(2));
        });
        Check(finishedWhileHeld,
            "a mix read must finish while another thread holds the engine's lock. It did not, so the audio thread is "
            + "waiting on the lock that adding a source holds while it waits on the mixer — the two can freeze each other "
            + "and sending stops for good");

        engine.Stop();
        Check(engine.ActiveSourceCount == 0,
            $"stopping the engine must bring the count back to zero (got {engine.ActiveSourceCount}) — otherwise the next "
            + "start believes a source is still there and switches correction on for a lone one");

        return "a mix read finishes while the engine's lock is held, so adding a source can no longer freeze sending; the "
             + "lock-free count follows one, two and zero sources";
    }

    /// <summary>
    /// A 44.1 KHZ MONO 16-BIT SOURCE MUST BE DRIFT-CORRECTED, NOT JUST A 48 KHZ STEREO FLOAT ONE.
    ///
    /// <para>2026-09-13 review. The corrector counts what it pulls as 48 kHz stereo float. It was handed
    /// the device's own byte count to compare against, so any other format measured as a huge clock
    /// error: 44.1 kHz read as 8 % slow, mono or 16-bit as half speed, 96 kHz as double. All of those are
    /// outside the ±5 % sanity band, so every window was thrown away and nothing was corrected — silently,
    /// for most real devices. The test above never saw it because it feeds the corrector 48 kHz stereo
    /// float units directly, the one format where the two counts agree.</para>
    ///
    /// <para>So this goes through a real <c>CaptureSource</c>, with the resampler and mix-down in front of
    /// the corrector, fed by a device in a format that is wrong in all three ways at once.</para>
    ///
    /// <para>Not per-configuration: capture-side, upstream of every output choice.</para>
    /// </summary>
    private static string? AuditCaptureDriftCountsInTheMixersUnits()
    {
        Check(CaptureSource.ToMixEquivalentBytes(88_200, new WaveFormat(44100, 16, 1)) == 384_000,
            "one second of 44.1 kHz mono 16-bit is one second of mix audio: 384,000 bytes of 48 kHz stereo float");
        Check(CaptureSource.ToMixEquivalentBytes(768_000, WaveFormat.CreateIeeeFloatWaveFormat(96000, 2)) == 384_000,
            "one second of 96 kHz stereo float is one second of mix audio too");
        Check(CaptureSource.ToMixEquivalentBytes(384_000, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)) == 384_000,
            "a source already in the mix format is counted as it is");

        // On this thread's audio clock, and with the SHIPPED ten-second window: 25 simulated seconds, two windows closed (the
        // first is start-up fill and thrown away), in a moment. Until 2026-09-28 it ran 2.5 s of wall clock on a half-second
        // window of its own.
        CheckDriftWindowsRunOnTheAudioClock();
        using var clock = new StoppedAudioClock();
        var device = new FakeCaptureDevice(new WaveFormat(44100, 16, 1));
        using var source = new CaptureSource(device, CaptureKind.Input, "gate:44k1", "44.1 kHz mono 16-bit");
        source.CorrectionWanted = () => true;

        var chunk = new byte[4096];
        for (var i = 0; i + 1 < chunk.Length; i += 2) BitConverter.TryWriteBytes(chunk.AsSpan(i), (short)3000);
        device.Feed(chunk, 1323 * 2);   // a 30 ms cushion to start, where the corrector aims to hold the ring

        // Half a percent fast: well inside the sanity band, and big enough that the ring-depth nudge
        // (capped at 0.3 %) cannot pull the ratio back under 1.0 by itself.
        const double fast = 1.005;
        var pull = new float[480 * 2];
        var owedFrames = 0.0;
        for (var tick = 1; tick <= 25 * 100; tick++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(10));
            owedFrames += 441 * fast;             // 10 ms of 44.1 kHz audio, a little over
            var frames = (int)owedFrames;
            owedFrames -= frames;
            device.Feed(chunk, frames * 2);
            source.Provider.Read(pull, 0, pull.Length);   // 10 ms of 48 kHz stereo out, as the mixer pulls
        }

        var corrector = Require(source.DriftCorrector, "the capture source must have a drift corrector");
        Check(corrector.IsTracking,
            "no measurement was ever accepted for a 44.1 kHz mono 16-bit source: every window was thrown away as a wild "
            + "clock error, so this source is never held on the mix clock. The feed side is being counted in the device's "
            + "own units instead of the mixer's");
        // Measured as it is - half a percent fast, plus at most the 0.3 % depth nudge for a ring that grew while the first
        // two windows closed - not as 8 % slow or half speed, and inside the sanity band.
        Check(source.AppliedDriftRatio is >= 1.004 and <= 1.0085,
            $"a source feeding half a percent fast must be pulled half a percent faster, give or take the depth nudge (ratio "
            + $"{source.AppliedDriftRatio:0.000000})");

        return $"a 44.1 kHz mono 16-bit source is measured in the mixer's units and corrected (ratio {source.AppliedDriftRatio:0.0000} "
             + "for a feed 0.5 % fast, over 25 simulated seconds)";
    }

    /// <summary>The premise of every run on a <see cref="StoppedAudioClock"/>: a drift window closes when the AUDIO clock says
    /// its ten seconds are up. Were it on the wall clock again, a run of simulated minutes would close no window at all, and
    /// the checks after it would blame the audio for what is the clock.</summary>
    private static void CheckDriftWindowsRunOnTheAudioClock()
    {
        using var clock = new StoppedAudioClock();
        var window = new DriftRatioTracker();
        window.WindowClosed(0, 0);
        clock.Advance(TimeSpan.FromSeconds(DriftRatioTracker.MeasurementWindowSec));
        Check(window.WindowClosed(1, 1),
            "premise: a drift window must close when this thread's audio clock says ten seconds have passed - the clock the "
            + "simulator runs on - not the wall clock; on the wall clock this run of simulated time would measure nothing");
    }

    /// <summary>This thread's audio clock (<see cref="AudioClock"/>, as the simulator uses it), standing still until the test
    /// moves it on. Every drift window in the capture path reads that clock, so minutes of it pass in a moment and come out
    /// the same every run. Put back to the real clock when disposed.</summary>
    private sealed class StoppedAudioClock : IDisposable
    {
        // Never zero: a drift window takes a zero start to mean "not started".
        private readonly long start = System.Diagnostics.Stopwatch.GetTimestamp();
        private long elapsed;

        public StoppedAudioClock() => AudioClock.UseSimulated(() => start + elapsed);

        /// <summary>Seconds since the clock was made.</summary>
        public double Seconds => elapsed / (double)System.Diagnostics.Stopwatch.Frequency;

        public void Advance(TimeSpan by) => elapsed += (long)Math.Round(by.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);

        public void Dispose() => AudioClock.UseSimulated(null);
    }

    /// <summary>A 48 kHz stereo float capture device whose crystal runs <c>ppm</c> fast or slow, and which writes into one side
    /// of each frame the moment, in seconds, it captured that frame (the other side is silent). Two of them summed into one
    /// mix put their two capture times side by side in every mixed frame, so the mix itself says how far apart the sources
    /// are. It starts 30 ms ahead of the mixer, as a real device has a callback or three in hand when the mix first pulls.</summary>
    private sealed class TimestampingDevice(WaveFormat format, double ppm, int channel) : IWaveIn
    {
        private const double Lead = 0.030;
        private readonly double rate = format.SampleRate * (1 + ppm / 1e6);
        private long captured;
        private float[] samples = [];
        private byte[] bytes = [];

        public WaveFormat WaveFormat { get; set; } = format;
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped { add { } remove { } }
        public void StartRecording() { }
        public void StopRecording() { }
        public void Dispose() { }

        /// <summary>Hand over everything this device has captured by <paramref name="seconds"/> into the mix.</summary>
        public void CaptureUntil(double seconds)
        {
            var due = (long)Math.Floor((seconds + Lead) * rate);
            var frames = (int)(due - captured);
            if (frames <= 0) return;
            if (samples.Length < frames * 2) { samples = new float[frames * 2]; bytes = new byte[frames * 2 * sizeof(float)]; }
            for (var i = 0; i < frames; i++)
            {
                samples[i * 2 + channel] = (float)((captured + i) / rate - Lead);
                samples[i * 2 + 1 - channel] = 0f;
            }
            Buffer.BlockCopy(samples, 0, bytes, 0, frames * 2 * sizeof(float));
            DataAvailable?.Invoke(this, new WaveInEventArgs(bytes, frames * 2 * sizeof(float)));
            captured = due;
        }
    }

    /// <summary>A capture device that produces exactly what the test hands it, when the test hands it.</summary>
    private sealed class FakeCaptureDevice(WaveFormat format) : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = format;
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped { add { } remove { } }
        public void StartRecording() { }
        public void StopRecording() { }
        public void Dispose() { }
        public void Feed(byte[] data, int count) => DataAvailable?.Invoke(this, new WaveInEventArgs(data, count));
    }
}
