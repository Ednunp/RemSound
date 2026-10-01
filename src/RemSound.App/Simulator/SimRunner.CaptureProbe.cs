using RemSound.Core;
using RemSound.Sender;

namespace RemSound.App.Simulator;

/// <summary>
/// Push mode's capture path on its own, with no network and no receiver (2026-09-30): a sine from a simulated input at a
/// given sample rate, in callbacks of a given size, and what comes out the other side - how many frames against how many
/// should, and how clean a 1 kHz tone it still is. This is what showed push mode losing frames at 44.1 and 88.2 kHz:
/// about 0.6 of a frame every 441-frame callback. --simulate capture-probe prints it for the common rates.
/// </summary>
internal static partial class SimRunner
{
    /// <summary>What push mode makes of <paramref name="callbacks"/> callbacks of <paramref name="frames"/> frames at
    /// <paramref name="rate"/> Hz: frames out, frames that should come out, and the tone against everything else (dB).</summary>
    internal static (int Got, double Expected, double ToneToNoiseDb) PushModeOnItsOwn(int rate, int frames, int callbacks = 400)
    {
        var outSamples = new List<float>();
        var device = new SimCaptureDevice("probe", new SimCard(frames), SampleRate: rate, Clicks: false);
        var input = new SimCaptureInput(device, 10);
        CaptureDevicesForTest.Open = _ => input;
        try
        {
            using var push = new PushModeWasapiBackend(m => outSamples.AddRange(m.ToArray()));
            push.Start([new CaptureSourceSpec("sim:probe", CaptureKind.Input, "probe")]);
            for (var k = 1; k <= callbacks; k++) input.Deliver(k);
            return (outSamples.Count / 2, callbacks * frames * (double)Simulation.SampleRate / rate, ToneToNoise(outSamples));
        }
        finally { CaptureDevicesForTest.Open = null; }
    }

    /// <summary>Push mode on its own, over 400 callbacks and over 4,000: what it holds back (frames short of what the input
    /// should make, at the end of each), and the tone's quality. Input-driven (2026-09-30 on), the converter holds the
    /// filter's reach and nothing more: the same few frames after 4,000 callbacks as after 400. A shortfall that grows is
    /// sound lost.</summary>
    internal static (double HeldAt400, double HeldAt4000, double ToneToNoiseDb) PushModeHold(int rate, int frames)
    {
        var (got, expected, snr) = PushModeOnItsOwn(rate, frames);
        var (gotLong, expectedLong, _) = PushModeOnItsOwn(rate, frames, 4000);
        return (expected - got, expectedLong - gotLong, snr);
    }

    private static void CaptureProbe(Action<string> say)
    {
        foreach (var (rate, frames) in new[] { (16000, 160), (22050, 220), (32000, 320), (44100, 441), (44100, 448), (48000, 480), (88200, 882), (96000, 960), (176400, 1764), (192000, 1920) })
        {
            var (got, expected, snr) = PushModeOnItsOwn(rate, frames);
            var (gotLong, expectedLong, _) = PushModeOnItsOwn(rate, frames, 4000);
            say($"push mode {rate} Hz in {frames}-frame callbacks: {got} frames out of {expected:0} expected ({got - expected:+0;-0;0}), "
                + $"and after ten times as many {gotLong - expectedLong:+0;-0;0}; tone-to-noise {snr:0.0} dB");
        }
    }

    /// <summary>Push mode end to end at a rate it converts from, a stretch at a time: does the delay settle, and does the
    /// receiver's drift loop read the stream as the steady 48 kHz it now is (2026-09-30, after the input-driven fix)?</summary>
    private static void CaptureProbeLong(Action<string> say)
    {
        foreach (var (name, device) in new[]
        {
            ("push mode, 44.1 kHz 16-bit", new SimCaptureDevice("input 44.1k 16-bit", new SimCard(441, JitterMs: 1), SampleRate: 44100, Bits: 16, Float: false)),
            ("push mode, 48 kHz float", new SimCaptureDevice("input 48k float", new SimCard(480, JitterMs: 1))),
            ("the mixing engine, a program at 44.1 kHz", new SimCaptureDevice("a program 44.1k", new SimCard(441, JitterMs: 1), SampleRate: 44100, PerApp: true)),
        })
        {
            var r = Simulation.Run(new SimScenario(name, SimConfig.WasapiOnly, SimCodec.Opus2_5, SimNetwork.Wired, 30, 120,
                CaptureDevices: [device], ChunkSeconds: 20));
            say($"{name}: {string.Join("; ", r.Stretches.Select(c => $"from {c.FromSec:0} s delay {c.Findings.DelayMedianMs:0.0} ({c.Findings.DelayMinMs:0.0}-{c.Findings.DelayMaxMs:0.0}), pitch {c.Findings.WorstPitchCents:0.0}"))}");
            say($"  the receiver's drift loop, ppm at 5 s steps: {string.Join(" ", r.DriftPpmBySecond.Where((_, i) => i % 5 == 4).Select(p => double.IsNaN(p) ? "-" : p.ToString("+0;-0;0")))}");
        }
    }

    /// <summary>An input that stops for 200 ms at 5 s: the delay at every click, and the receiver's drift loop, second by
    /// second - where does the buffer go after the stop, and does it come back?</summary>
    private static void CaptureProbeStall(Action<string> say)
    {
        foreach (var buffer in new[] { 30, 60 })
        {
            var r = Simulation.Run(new SimScenario($"an input that stops for 200 ms at 5 s, {buffer} ms buffer", SimConfig.WasapiOnly, SimCodec.Opus2_5,
                SimNetwork.Wired, buffer, 60, KeepAudio: true,
                CaptureDevices: [new SimCaptureDevice("stalling input", new SimCard(480, JitterMs: 1, StallAtSec: 5, StallOnceMs: 200))]));
            var clicks = SimAnalysis.Clicks(r.Audio!, r.AudioTimes, Simulation.SampleRate);
            say($"{r.Scenario.Name}: delay at each second's first click: "
                + string.Join(" ", Enumerable.Range(0, 60).Select(sec => clicks.FirstOrDefault(c => c.HeardSec >= sec && c.HeardSec < sec + 1))
                    .Select(c => c.Frame == 0 ? "-" : c.DelayMs.ToString("0"))));
            say($"  drift loop ppm by second: {string.Join(" ", r.DriftPpmBySecond.Select(p => double.IsNaN(p) ? "-" : p.ToString("+0;-0;0")))}");
            say($"  underruns {r.Underruns}");
        }
        // ASIO only: the sending card itself stops. Its tone and clicks are made by count, so after the stop they come 200 ms
        // later than the clock says: the delay shown here has that taken off.
        foreach (var buffer in new[] { 10, 30 })
        {
            var r = Simulation.Run(new SimScenario($"ASIO only, the sending card stops for 200 ms at 5 s, {buffer} ms buffer", SimConfig.AsioOnly,
                SimCodec.Opus2_5, SimNetwork.Wired, buffer, 40, KeepAudio: true,
                SendCard: Simulation.DefaultCard(true) with { StallAtSec = 5, StallOnceMs = 200 }));
            var clicks = SimAnalysis.Clicks(r.Audio!, r.AudioTimes, Simulation.SampleRate);
            say($"{r.Scenario.Name}: delay at each second's first click: "
                + string.Join(" ", Enumerable.Range(0, 40).Select(sec => clicks.FirstOrDefault(c => c.HeardSec >= sec && c.HeardSec < sec + 1))
                    .Select(c => c.Frame == 0 ? "-" : (c.HeardSec > 5.1 ? c.DelayMs - 200 : c.DelayMs).ToString("0"))));
            say($"  underruns {r.Underruns}");
        }
    }

    /// <summary>Why does a converted stream start with its buffer standing deep? Second by second for the first 40 s: the
    /// delay, the receiver's buffer (its drift loop's mean depth), the loop's clock ratio and whether it has a reading.</summary>
    private static void CaptureProbeStart(Action<string> say)
    {
        foreach (var (name, device) in new[]
        {
            ("push mode, 48 kHz", new SimCaptureDevice("input 48k", new SimCard(480, JitterMs: 1))),
            ("push mode, 44.1 kHz", new SimCaptureDevice("input 44.1k", new SimCard(441, JitterMs: 1), SampleRate: 44100)),
            ("the mixing engine, 48 kHz", new SimCaptureDevice("a program 48k", new SimCard(480, JitterMs: 1), PerApp: true)),
            ("the mixing engine, 44.1 kHz", new SimCaptureDevice("a program 44.1k", new SimCard(441, JitterMs: 1), SampleRate: 44100, PerApp: true)),
        })
        {
            var r = Simulation.Run(new SimScenario(name, SimConfig.WasapiOnly, SimCodec.Opus2_5, SimNetwork.Wired, int.TryParse(Environment.GetEnvironmentVariable("PROBE_BUFFER"), out var pb) ? pb : 30, 40,
                CaptureDevices: [device], KeepAudio: true, PerStreamBuffers: Environment.GetEnvironmentVariable("PROBE_PER_STREAM") != "0",
                RecordNetwork: true));
            if (r.Recording is { } rec)
            {
                // The recording keeps the last minute: the run's first packets are its first lines.
                say($"  first packets (ms, sequence, bytes): {string.Join("  ", rec.Packets.Take(24).Select(p => $"{p.Ms:0.0}/{p.Sequence}/{p.Bytes}"))}");
                say($"  sessions opened {r.SessionsOpened}; streams {string.Join(", ", r.Streams)}");
            }
            var clicks = SimAnalysis.Clicks(r.Audio!, r.AudioTimes, Simulation.SampleRate);
            string Delay(int sec) => clicks.FirstOrDefault(c => c.HeardSec >= sec && c.HeardSec < sec + 1) is { Frame: > 0 } c ? c.DelayMs.ToString("0.0") : "-";
            string Depth(int sec) => sec < r.ReplicasBySecond.Count && r.ReplicasBySecond[sec].Count > 0 ? r.ReplicasBySecond[sec][0].DepthMs.ToString("0.0") : "-";
            string Ppm(int sec) => sec < r.ReplicasBySecond.Count && r.ReplicasBySecond[sec].Count > 0 && r.ReplicasBySecond[sec][0].Tracking
                ? ((r.ReplicasBySecond[sec][0].Ratio - 1) * 1e6).ToString("+0;-0;0") : "-";
            say($"{name} [{r.CapturePath}]:");
            say($"  delay: {string.Join(" ", Enumerable.Range(0, 40).Select(Delay))}");
            say($"  depth: {string.Join(" ", Enumerable.Range(0, 40).Select(Depth))}");
            say($"  ratio ppm: {string.Join(" ", Enumerable.Range(0, 40).Select(Ppm))}");
        }
    }

    /// <summary>Two cards 200 ppm apart, WASAPI only, 40 ms: the delay, the depth and the drift loop's ratio second by second.</summary>
    private static void CaptureProbeDrift(Action<string> say)
    {
        foreach (var ppm in new[] { 200.0, -200.0 })
        {
            var card = Simulation.DefaultCard(false);
            var r = Simulation.Run(new SimScenario($"WASAPI only, cards {ppm:+0;-0} ppm apart", SimConfig.WasapiOnly, SimCodec.Opus2_5, SimNetwork.Wired, 40, 45,
                card with { DriftPpm = ppm / 2 }, card with { DriftPpm = -ppm / 2 }, KeepAudio: true));
            var clicks = SimAnalysis.Clicks(r.Audio!, r.AudioTimes, Simulation.SampleRate);
            string Delay(int sec) => clicks.FirstOrDefault(c => c.HeardSec >= sec && c.HeardSec < sec + 1) is { Frame: > 0 } c ? c.DelayMs.ToString("0.0") : "-";
            string Depth(int sec) => sec < r.ReplicasBySecond.Count && r.ReplicasBySecond[sec].Count > 0 ? r.ReplicasBySecond[sec][0].DepthMs.ToString("0.0") : "-";
            string Ppm(int sec) => sec < r.ReplicasBySecond.Count && r.ReplicasBySecond[sec].Count > 0 && r.ReplicasBySecond[sec][0].Tracking
                ? ((r.ReplicasBySecond[sec][0].Ratio - 1) * 1e6).ToString("+0;-0;0") : "-";
            say(r.Scenario.Name + ":");
            say($"  delay: {string.Join(" ", Enumerable.Range(0, 45).Select(Delay))}");
            say($"  depth: {string.Join(" ", Enumerable.Range(0, 45).Select(Depth))}");
            say($"  applied ratio ppm: {string.Join(" ", Enumerable.Range(0, 45).Select(Ppm))}");
            say($"  clock reading ppm: {string.Join(" ", r.DriftPpmBySecond.Select(p => double.IsNaN(p) ? "-" : p.ToString("+0;-0;0")))}");
        }
    }

    /// <summary>The left channel against the best-fitting 1 kHz sine, from a tenth of a second in.</summary>
    private static double ToneToNoise(List<float> interleaved)
    {
        var n = interleaved.Count / 2;
        var from = Math.Min(4800, n / 2);
        double ss = 0, cc = 0, sc = 0, ys = 0, yc = 0;
        for (var i = from; i < n; i++)
        {
            var w = 2 * Math.PI * Simulation.ToneHz * i / Simulation.SampleRate;
            double s = Math.Sin(w), c = Math.Cos(w), y = interleaved[2 * i];
            ss += s * s; cc += c * c; sc += s * c; ys += y * s; yc += y * c;
        }
        var det = ss * cc - sc * sc;
        var a = (ys * cc - yc * sc) / det;
        var b = (yc * ss - ys * sc) / det;
        double signal = 0, noise = 0;
        for (var i = from; i < n; i++)
        {
            var w = 2 * Math.PI * Simulation.ToneHz * i / Simulation.SampleRate;
            var fit = a * Math.Sin(w) + b * Math.Cos(w);
            signal += fit * fit;
            var e = interleaved[2 * i] - fit;
            noise += e * e;
        }
        return 10 * Math.Log10(signal / Math.Max(noise, 1e-20));
    }
}
