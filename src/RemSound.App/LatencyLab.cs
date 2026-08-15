using System.Diagnostics;
using System.Net;
using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App;

/// <summary>
/// Measured latency-slider lab (<c>--latency-lab</c>), built for the 2026-08-14 field report:
/// raising the receive-latency slider audibly lengthens the delay on one machine and does nothing
/// on another — both plain WASAPI, both ordinary consumer outputs, auto-tune off. The mechanism
/// under test is SessionPlayout's depth-feedback resampler bias (raise = play ≤0.3% slow until the
/// ring grows to target ≈ +3ms of depth per second). This drives the REAL playout objects with a
/// realtime-paced producer (sender-shaped writes) and consumer (device-shaped reads), samples the
/// actual buffered depth once a second, and prints growth rates — so the diagnosis rests on
/// numbers from the shipped code, not on reasoning about it (the standing audio rule).
///
/// Scenarios vary the two things that differ between working and stalled rigs: the write shape
/// (10ms PCM-ish vs 20ms Opus-frame-ish vs jittery arrival) and the read shape (clean 10ms
/// callbacks vs chunky 21ms callbacks with periodic double-gulps). Tier 2 re-runs the key shapes
/// through PlayoutEngine's Mixed-route Read — the exact single-slider plumbing a fresh install
/// uses — in case the stall lives above SessionPlayout.
/// </summary>
internal static class LatencyLab
{
    private const int SampleRate = 48000;
    private const int BytesPerFrame = 2 * sizeof(float); // stereo float mix bus
    private const int StartTargetMs = 30;
    private const int RaisedTargetMs = 330;
    private const int SettleSeconds = 15;   // let the 10s drift window engage before judging
    private const int MeasureSeconds = 60;  // growth window: expect ~+3ms/s => ~+180ms

    public static int Run() => Run([]);

    public static int Run(string[] args)
    {
        Console.WriteLine("RemSound latency lab - measuring depth-target convergence with the shipped playout code.");
        Console.WriteLine($"Raise under test: {StartTargetMs}ms -> {RaisedTargetMs}ms. Expected growth at full depth-bias: ~3ms/s.");
        Console.WriteLine();

        var results = new List<string>();

        // T3 — the FAITHFUL classic-mode app path (the field bug). Wires the engine exactly as the
        // running app does: CompositeRenderBackend marks the WASAPI lane active (a ticked WASAPI
        // output, no ASIO), which tags every session RenderRoute.WasapiLane; the single latency
        // slider in every non-BothIndependent mode drives RenderRoute.Mixed (MainForm.MaxLatencyBox-
        // Route). Run this alone with: --latency-lab classic
        // T5 — score the auto-tune DESCENT policy: old fixed crawl vs the evidence-based one, across
        // several network characters. Two numbers decide it: how long to settle, and how many
        // tune-blocking short-reads were bought on the way. Run with: --latency-lab tune
        if (args.Any(a => string.Equals(a, "tune", StringComparison.OrdinalIgnoreCase)))
        {
            ScoreDescentPolicies();
            return 0;
        }

        // T4 — the BothIndependent ASIO lane: does a move of the ASIO slider reach an ASIO-lane
        // stream, and arrive in seconds like the single slider does? Run with: --latency-lab asio
        if (args.Any(a => string.Equals(a, "asio", StringComparison.OrdinalIgnoreCase)))
        {
            RunClassicAppPath(results, "T4 ASIO lane (two-slider mode): ASIO slider drives an ASIO-lane stream", asioLane: true);
            Console.WriteLine();
            Console.WriteLine("=== SUMMARY ===");
            foreach (var line in results) Console.WriteLine("  " + line);
            return 0;
        }

        if (args.Any(a => string.Equals(a, "classic", StringComparison.OrdinalIgnoreCase)))
        {
            RunClassicAppPath(results, "T3 classic app path: WASAPI lane active, slider drives Mixed");
            Console.WriteLine();
            Console.WriteLine("=== SUMMARY ===");
            foreach (var line in results) Console.WriteLine("  " + line);
            return 0;
        }
        RunScenario(results, "T1 clean: write 10ms/10ms, read 10ms/10ms", writeMs: 10, writeJitterMs: 0, readMs: 10, gulpEvery: 0, engineMixed: false);
        RunScenario(results, "T1 opus-frame: write 20ms/20ms, read 10ms/10ms", writeMs: 20, writeJitterMs: 0, readMs: 10, gulpEvery: 0, engineMixed: false);
        RunScenario(results, "T1 gulpy device: write 20ms/20ms, read 21ms + 42ms gulp each ~1s", writeMs: 20, writeJitterMs: 0, readMs: 21, gulpEvery: 48, engineMixed: false);
        RunScenario(results, "T1 jittery net: write 20ms +/-15ms, read 10ms/10ms", writeMs: 20, writeJitterMs: 15, readMs: 10, gulpEvery: 0, engineMixed: false);
        RunScenario(results, "T2 engine-Mixed clean: write 20ms/20ms, read 10ms/10ms", writeMs: 20, writeJitterMs: 0, readMs: 10, gulpEvery: 0, engineMixed: true);
        RunScenario(results, "T2 engine-Mixed gulpy: write 20ms/20ms, read 21ms + 42ms gulp", writeMs: 20, writeJitterMs: 0, readMs: 21, gulpEvery: 48, engineMixed: true);

        Console.WriteLine();
        Console.WriteLine("=== SUMMARY ===");
        foreach (var line in results) Console.WriteLine("  " + line);
        return 0;
    }

    /// <summary>Score the auto-tune's DESCENT: old fixed 5 ms crawl against the evidence-based policy,
    /// over several network characters. This is a fast simulation of the TICK LOOP (not realtime
    /// audio): each network character says what jitter the tuner would measure and how deep the buffer
    /// actually sits, and the two policies are driven tick-for-tick from the same numbers. Reported:
    /// ticks to settle, and how many ticks the target spent BELOW what the network needed — the proxy
    /// for "dropouts bought on the way down", which is the number that can veto the whole change.</summary>
    private static void ScoreDescentPolicies()
    {
        Console.WriteLine("Auto-tune descent scoring — old fixed 5ms/tick vs evidence-based.");
        Console.WriteLine("A tick is one auto-tune interval (5s by default). 'unsafe' = ticks spent below the network's real need.");
        Console.WriteLine();

        // name, the latency this network genuinely needs, the starting slider value
        (string Name, int NeedMs, int StartMs)[] characters =
        [
            ("clean LAN, silly start",      12,  500),
            ("clean LAN, mild overshoot",   12,   80),
            ("stable internet",             35,  500),
            ("jittery wifi",                70,  500),
            ("bad network",                140,  500),
        ];

        foreach (var c in characters)
        {
            var old = SimulateDescent(c.NeedMs, c.StartMs, evidenceBased: false);
            var neu = SimulateDescent(c.NeedMs, c.StartMs, evidenceBased: true);
            Console.WriteLine($"  {c.Name,-28} need={c.NeedMs,3}ms start={c.StartMs,3}ms");
            Console.WriteLine($"      old: settled in {old.Ticks,3} ticks ({old.Ticks * 5,4}s), unsafe ticks={old.Unsafe}");
            Console.WriteLine($"      new: settled in {neu.Ticks,3} ticks ({neu.Ticks * 5,4}s), unsafe ticks={neu.Unsafe}");
        }

        // The case that actually threatens a fast descent: the network gets WORSE part-way down, so
        // the low-water evidence gathered a moment ago is already out of date. A policy that sheds
        // cushion quickly is more exposed here by construction — this is where it has to earn trust,
        // not on the easy characters above. (Recovery is the raise path: immediate, and since 5.9 it
        // arrives in seconds rather than minutes, which is what makes the boldness affordable.)
        Console.WriteLine();
        Console.WriteLine("  Worsening mid-descent — starts easy at 12ms, turns bad (90ms) at tick 6:");
        var oldW = SimulateDescent(12, 500, evidenceBased: false, worsenAtTick: 6, worsenedNeedMs: 90);
        var newW = SimulateDescent(12, 500, evidenceBased: true, worsenAtTick: 6, worsenedNeedMs: 90);
        Console.WriteLine($"      old: settled in {oldW.Ticks,3} ticks ({oldW.Ticks * 5,4}s), unsafe ticks={oldW.Unsafe}");
        Console.WriteLine($"      new: settled in {newW.Ticks,3} ticks ({newW.Ticks * 5,4}s), unsafe ticks={newW.Unsafe}");

        // Tick-by-tick trace of the case Ed asked about: slider left at 500 with a machine whose
        // real need is ~30ms, auto-tune engaged. Answers "what will it actually do" with numbers.
        Console.WriteLine();
        Console.WriteLine("  TRACE - slider 500ms, real need 30ms, auto-tune engaged (tick = 5s):");
        TraceDescent(needMs: 30, startMs: 500);

        Console.WriteLine();
        Console.WriteLine("Verdict rule: the new policy must settle materially faster AND buy no extra unsafe ticks.");
    }

    /// <summary>Print the actual tick-by-tick path a descent takes, old policy against new.</summary>
    private static void TraceDescent(int needMs, int startMs)
    {
        foreach (var evidenceBased in new[] { false, true })
        {
            var current = startMs;
            var cleanTicks = 0;
            var creep = new AutoTuneDescent.CreepState();
            var path = new List<string> { $"{current}" };
            var ticksUsed = 0;
            // Long enough to show the creep finish, not just the fast phase.
            for (var tick = 1; tick <= 400; tick++)
            {
                var lowWater = Math.Max(0, current - needMs);
                cleanTicks++;
                var next = evidenceBased
                    ? AutoTuneDescent.NextTarget(current, needMs, lowWater, sampleCount: 15,
                        consecutiveCleanTicks: cleanTicks, policy: null, creep: creep)
                    : Math.Max(needMs, current - 5);
                ticksUsed = tick;
                if (next == current && !evidenceBased) break;
                if (next == current) continue;      // creep is waiting out its validating quiet spell
                current = next;
                if (path.Count < 16) path.Add($"{current}");
                else if (path.Count == 16) path.Add("...");
                if (current <= needMs) break;
            }
            var label = evidenceBased ? "new" : "old";
            Console.WriteLine($"      {label}: {string.Join(" -> ", path)}  (lands at {current}ms after ~{ticksUsed * 5}s)");
        }
    }

    /// <summary>Drive one policy to convergence. The tuner's recommendation is the network's real need
    /// (that's what its gap measurements converge on), and the buffer's low-water mark is modelled as
    /// "target minus what the network eats" — i.e. a deep buffer visibly never gets touched, which is
    /// exactly the signal the evidence-based policy reads. Settled = within one hysteresis step.</summary>
    private static (int Ticks, int Unsafe) SimulateDescent(
        int needMs, int startMs, bool evidenceBased, int worsenAtTick = 0, int worsenedNeedMs = 0)
    {
        var current = startMs;
        var unsafeTicks = 0;
        var cleanTicks = 0;
        for (var tick = 1; tick <= 400; tick++)
        {
            var need = worsenAtTick > 0 && tick >= worsenAtTick ? worsenedNeedMs : needMs;
            // The buffer sits at the target and the network eats `need` of it, so the shallowest it
            // reaches is target-need (never below zero — that's a dropout, which zeroes the evidence).
            var lowWater = Math.Max(0, current - need);
            var running = current < need;
            if (running) { unsafeTicks++; cleanTicks = 0; } else cleanTicks++;

            // A tick that ran short is SKIPPED by the real tuner (it returns early), and the raise
            // path takes over on the next healthy tick — model both so the exposure is honest.
            int next;
            if (running)
            {
                next = need; // the tuner raises straight to the measured need, immediately
            }
            else
            {
                next = evidenceBased
                    ? AutoTuneDescent.NextTarget(current, need, lowWater, sampleCount: 15, consecutiveCleanTicks: cleanTicks)
                    : Math.Max(need, current - 5); // the old fixed crawl
            }
            if (Math.Abs(next - current) < 5 && !running) return (tick, unsafeTicks); // hysteresis: settled
            current = next;
        }
        return (400, unsafeTicks);
    }

    /// <summary>The classic-mode reproduction: engine wired exactly as the shipped app wires it for a
    /// plain WASAPI setup, then the slider raised through the very call MainForm makes. Measures the
    /// buffered depth the same way as the other scenarios.</summary>
    private static void RunClassicAppPath(List<string> results, string name, bool asioLane = false)
    {
        Console.WriteLine($"--- {name} ---");
        var endpoint = new IPEndPoint(IPAddress.Loopback, 47831);
        var engine = new PlayoutEngine(new ReceiverDiagnostics());

        // Which control the user is actually moving: the single slider (classic modes → Mixed), or
        // the ASIO slider in BothIndependent (→ AsioLane, its own value, its own lane's streams).
        var sliderRoute = asioLane ? RenderRoute.AsioLane : RenderRoute.Mixed;
        if (asioLane) engine.SetIndependentLaneLatency(true); // AudioReceiver.SetAudioMode(BothIndependent)

        // 1. CompositeRenderBackend.SetOutputDevices: which lanes have a ticked output device.
        engine.SetLaneActive(RenderRoute.WasapiLane, !asioLane);
        engine.SetLaneActive(RenderRoute.AsioLane, asioLane);
        // 2. The slider's startup value, applied the way MainForm applies it.
        engine.SetMaxLatencyMs(sliderRoute, StartTargetMs);
        // 3. A peer's stream arrives — ReconcileReplicasLocked tags it with the active lane.
        var session = engine.GetOrCreateSession(endpoint, 1, capacityBytes: 4 * 1024 * 1024);
        Console.WriteLine($"  session route after arrival: {session.Route}  (slider writes to: {sliderRoute})");

        const int WriteMs = 20, ReadMs = 10;
        var stop = false;
        var writeBlock = new byte[WriteMs * SampleRate / 1000 * BytesPerFrame];
        FillSine(writeBlock);

        var producer = new Thread(() =>
        {
            var sw = Stopwatch.StartNew();
            var nextDueMs = 0.0;
            while (!Volatile.Read(ref stop))
            {
                session.Write(writeBlock);
                session.NoteFramesQueued(engine.TargetLatencyMsFor(session.Route)); // AudioReceiver's queued-callback lambda
                nextDueMs += WriteMs;
                var sleep = nextDueMs - sw.Elapsed.TotalMilliseconds;
                if (sleep > 0) Thread.Sleep((int)sleep);
            }
        }) { IsBackground = true, Name = "lab-producer" };

        var readFrames = ReadMs * SampleRate / 1000;
        var readBytes = new byte[readFrames * BytesPerFrame];
        var consumer = new Thread(() =>
        {
            var sw = Stopwatch.StartNew();
            var nextDueMs = 0.0;
            while (!Volatile.Read(ref stop))
            {
                engine.Read(readBytes, 0, readBytes.Length);
                nextDueMs += ReadMs;
                var sleep = nextDueMs - sw.Elapsed.TotalMilliseconds;
                if (sleep > 0) Thread.Sleep((int)sleep);
            }
        }) { IsBackground = true, Name = "lab-consumer" };

        producer.Start();
        Thread.Sleep(120);
        consumer.Start();
        Thread.Sleep(SettleSeconds * 1000);
        var settled = session.BufferedMs;
        Console.WriteLine($"  settled at slider {StartTargetMs}ms: buffered={settled}ms");

        // 4. The user drags the slider up mid-stream — MainForm's exact call.
        engine.SetMaxLatencyMs(sliderRoute, RaisedTargetMs);
        for (var s = 0; s < MeasureSeconds; s++)
        {
            Thread.Sleep(1000);
            if (s % 10 == 9) Console.WriteLine($"  t+{s + 1,2}s buffered={session.BufferedMs,4}ms");
        }
        var after = session.BufferedMs;
        var growth = (after - settled) / (double)MeasureSeconds;
        var verdict = growth >= 2.0 ? "GROWS (slider works)" : growth >= 0.5 ? "SLOW" : "STALLED (the field bug)";
        Console.WriteLine($"  RAISE via slider: {settled}ms -> {after}ms in {MeasureSeconds}s = {growth:F2}ms/s  {verdict}");

        engine.SetMaxLatencyMs(sliderRoute, StartTargetMs);
        Thread.Sleep(3000);
        Console.WriteLine($"  LOWER via slider: back to {StartTargetMs}ms -> buffered={session.BufferedMs}ms after 3s");
        Console.WriteLine();
        results.Add($"{name}: raise {growth:F2}ms/s [{verdict}], lower -> {session.BufferedMs}ms");

        stop = true;
        producer.Join(2000);
        consumer.Join(2000);
    }

    private static void RunScenario(List<string> results, string name, int writeMs, int writeJitterMs, int readMs, int gulpEvery, bool engineMixed)
    {
        Console.WriteLine($"--- {name} ---");
        var endpoint = new IPEndPoint(IPAddress.Loopback, 47831);
        var diagnostics = new ReceiverDiagnostics();
        PlayoutEngine? engine = null;
        SessionPlayout session;
        var target = StartTargetMs;
        if (engineMixed)
        {
            engine = new PlayoutEngine(diagnostics);
            engine.SetMaxLatencyMs(StartTargetMs);
            session = engine.GetOrCreateSession(endpoint, 1, capacityBytes: 4 * 1024 * 1024);
        }
        else
        {
            session = new SessionPlayout(endpoint, 1, capacityBytes: 4 * 1024 * 1024);
        }

        var stop = false;
        var writeBlock = new byte[writeMs * SampleRate / 1000 * BytesPerFrame];
        FillSine(writeBlock); // real signal, not silence - keeps every probe honest
        var rng = new Random(12345);

        // Producer: sender-shaped realtime writes, exactly the AudioReceiver wiring
        // (Write then NoteFramesQueued with the CURRENT target, like the queued-callback lambda).
        var producer = new Thread(() =>
        {
            var sw = Stopwatch.StartNew();
            var nextDueMs = 0.0;
            while (!Volatile.Read(ref stop))
            {
                session.Write(writeBlock);
                session.NoteFramesQueued(engineMixed ? engine!.TargetLatencyMs : Volatile.Read(ref target));
                nextDueMs += writeMs;
                var jitter = writeJitterMs > 0 ? rng.Next(-writeJitterMs, writeJitterMs + 1) : 0;
                var sleep = nextDueMs + jitter - sw.Elapsed.TotalMilliseconds;
                if (sleep > 0) Thread.Sleep((int)sleep);
            }
        }) { IsBackground = true, Name = "lab-producer" };

        // Consumer: device-shaped reads. Clean cadence, or chunky with a periodic double-gulp.
        var readBlockFrames = readMs * SampleRate / 1000;
        var readFloats = new float[readBlockFrames * 2 * 2]; // x2 room for the gulp read
        var readBytes = new byte[readFloats.Length * sizeof(float)];
        var consumer = new Thread(() =>
        {
            var sw = Stopwatch.StartNew();
            var nextDueMs = 0.0;
            var n = 0;
            while (!Volatile.Read(ref stop))
            {
                var gulp = gulpEvery > 0 && ++n % gulpEvery == 0;
                var frames = gulp ? readBlockFrames * 2 : readBlockFrames;
                if (engineMixed)
                {
                    engine!.Read(readBytes, 0, frames * BytesPerFrame);
                }
                else
                {
                    session.ReadFloats(readFloats.AsSpan(0, frames * 2), frames, Volatile.Read(ref target), Volatile.Read(ref target), smoothness: 3);
                }
                nextDueMs += gulp ? readMs * 2 : readMs;
                var sleep = nextDueMs - sw.Elapsed.TotalMilliseconds;
                if (sleep > 0) Thread.Sleep((int)sleep);
            }
        }) { IsBackground = true, Name = "lab-consumer" };

        producer.Start();
        Thread.Sleep(120); // pre-fill past the arming threshold so playback starts
        consumer.Start();

        // Phase A: settle at the low target so the 10s drift-measurement window engages.
        Thread.Sleep(SettleSeconds * 1000);
        var settled = session.BufferedMs;
        Console.WriteLine($"  settled at target {StartTargetMs}ms: buffered={settled}ms ratio={session.DriftResamplerRatio:F6} updates={session.DriftResamplerUpdates}");

        // Phase B: RAISE - the shipped raise path (engine hard setter for tier 2; the value the
        // reads/queued-callbacks see for tier 1). Then measure depth once a second.
        if (engineMixed) engine!.SetMaxLatencyMs(RaisedTargetMs); else Volatile.Write(ref target, RaisedTargetMs);
        var samples = new List<int>();
        for (var s = 0; s < MeasureSeconds; s++)
        {
            Thread.Sleep(1000);
            samples.Add(session.BufferedMs);
            if (s % 5 == 4)
                Console.WriteLine($"  t+{s + 1,2}s buffered={session.BufferedMs,4}ms ratio={session.DriftResamplerRatio:F6} updates={session.DriftResamplerUpdates}");
        }

        // Growth rate over the measure window (simple end-to-end slope; the per-5s prints show shape).
        var growthMsPerSec = (samples[^1] - settled) / (double)MeasureSeconds;
        var verdict = growthMsPerSec >= 2.0 ? "GROWS (mechanism working)"
            : growthMsPerSec >= 0.5 ? "SLOW (partially working)"
            : "STALLED (the field bug)";
        Console.WriteLine($"  RAISE result: {settled}ms -> {samples[^1]}ms in {MeasureSeconds}s = {growthMsPerSec:F2}ms/s  {verdict}");

        // Phase C: LOWER sanity - the drain path should snap back within a couple of seconds.
        if (engineMixed) engine!.SetMaxLatencyMs(StartTargetMs);
        else { Volatile.Write(ref target, StartTargetMs); session.DisarmAndRequestDrain(); }
        Thread.Sleep(3000);
        var afterLower = session.BufferedMs;
        Console.WriteLine($"  LOWER result: back to target {StartTargetMs}ms -> buffered={afterLower}ms after 3s");
        Console.WriteLine();

        results.Add($"{name}: raise {growthMsPerSec:F2}ms/s [{verdict}], lower -> {afterLower}ms");

        stop = true;
        producer.Join(2000);
        consumer.Join(2000);
        session.Dispose();
    }

    /// <summary>A -12 dB 440 Hz sine so the ring carries real audio (probes and concealment
    /// behave as in the field; silence would short-circuit none of them but costs nothing to avoid).</summary>
    private static void FillSine(byte[] block)
    {
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(block.AsSpan());
        for (var i = 0; i < floats.Length; i += 2)
        {
            var sample = (float)(0.25 * Math.Sin(2 * Math.PI * 440 * (i / 2) / SampleRate));
            floats[i] = sample;
            floats[i + 1] = sample;
        }
    }
}
