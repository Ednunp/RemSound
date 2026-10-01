namespace RemSound.App.Simulator;

/// <summary>
/// --simulate capture: the capture side in the loop (2026-09-30). Simulated devices opened by the sender as it opens real
/// ones: one input through push mode (48 kHz float, 44.1 kHz 16-bit, a one-channel 96 kHz 24-bit mic), a program's own
/// sound and two sources on different clocks through the mixing engine, a device that delivers late in lumps, and one that
/// stops for a moment.
/// </summary>
internal static partial class SimRunner
{
    /// <summary>The capture scenarios, by name, as the gate runs them too.</summary>
    internal static IReadOnlyList<SimScenario> CaptureScenarios(double seconds)
    {
        SimScenario Over(string name, int buffer, params SimCaptureDevice[] devices) =>
            new(name, SimConfig.WasapiOnly, SimCodec.Opus2_5, SimNetwork.Wired, buffer, seconds, CaptureDevices: devices);
        var input48 = new SimCaptureDevice("input 48k float", new SimCard(480, JitterMs: 1));
        return
        [
            Over("one input, 48 kHz float (push mode)", 30, input48),
            Over("one input, 44.1 kHz 16-bit (push mode, resampled)", 30,
                new SimCaptureDevice("input 44.1k 16-bit", new SimCard(441, JitterMs: 1), SampleRate: 44100, Bits: 16, Float: false)),
            Over("one mic, 96 kHz 24-bit, one channel (push mode, resampled, doubled to two)", 30,
                new SimCaptureDevice("mic 96k mono", new SimCard(960, JitterMs: 1), SampleRate: 96000, Channels: 1, Bits: 24, Float: false, Clicks: false)),
            Over("a program's own sound (the mixing engine, one source)", 30,
                new SimCaptureDevice("a program", new SimCard(960, JitterMs: 2), PerApp: true)),
            Over("two sources 300 ppm apart: tone on one, clicks on the other (the mixing engine, drift corrected)", 40,
                new SimCaptureDevice("mic, tone", new SimCard(480, JitterMs: 1), Clicks: false),
                new SimCaptureDevice("second mic, clicks, 44.1k +300 ppm", new SimCard(441, JitterMs: 1, DriftPpm: 300), SampleRate: 44100, Tone: false)),
            Over("PROBE one input, 44.1 kHz float", 30,
                new SimCaptureDevice("input 44.1k float", new SimCard(441, JitterMs: 1), SampleRate: 44100)),
            Over("PROBE one input, 48 kHz 16-bit", 30,
                new SimCaptureDevice("input 48k 16-bit", new SimCard(480, JitterMs: 1), Bits: 16, Float: false)),
            Over("PROBE a program's own sound at 44.1 kHz float (the mixing engine's resampler)", 30,
                new SimCaptureDevice("a program 44.1k", new SimCard(441, JitterMs: 1), SampleRate: 44100, PerApp: true)),
            Over("PROBE a program's own sound, no jitter anywhere, 60 s", 30,
                new SimCaptureDevice("a program", new SimCard(960), PerApp: true)) with { Seconds = 60, MixTickJitterMs = 0 },
            Over("PROBE push mode, 20 ms callbacks, 60 s", 30,
                new SimCaptureDevice("input 20 ms", new SimCard(960))) with { Seconds = 60 },
            Over("PROBE push mode, 20 ms callbacks, 60 s, 60 ms buffer", 60,
                new SimCaptureDevice("input 20 ms", new SimCard(960))) with { Seconds = 60 },
            Over("PROBE a program's own sound, 60 s", 30,
                new SimCaptureDevice("a program", new SimCard(960, JitterMs: 2), PerApp: true)) with { Seconds = 60 },
            Over("a device that delivers in lumps (callbacks up to 8 ms late)", 30,
                new SimCaptureDevice("lumpy input", new SimCard(480, JitterMs: 8))),
            Over("a device that stops for 200 ms at 5 s", 30,
                new SimCaptureDevice("stalling input", new SimCard(480, JitterMs: 1, StallAtSec: 5, StallOnceMs: 200))),
        ];
    }

    private static void Capture(double seconds, Action<string> say)
    {
        if (seconds >= 120)
        {
            // A long look at the mixing engine's delay, a stretch at a time.
            foreach (var devices in new[]
            {
                new[] { new SimCaptureDevice("a program", new SimCard(960, JitterMs: 2), PerApp: true) },
                new[] { new SimCaptureDevice("mic, tone", new SimCard(480, JitterMs: 1), Clicks: false),
                        new SimCaptureDevice("second mic, clicks, 44.1k +300 ppm", new SimCard(441, JitterMs: 1, DriftPpm: 300), SampleRate: 44100, Tone: false) },
            })
            {
                var r = Simulation.Run(new SimScenario($"{devices.Length} source(s), long", SimConfig.WasapiOnly, SimCodec.Opus2_5, SimNetwork.Wired, 40, seconds,
                    CaptureDevices: devices, ChunkSeconds: 30));
                say(Describe(r) + $" [{r.CapturePath}; correctors {string.Join(", ", r.CaptureDrift.Select(d => $"{d.Name} {(d.Ratio - 1) * 1e6:0} ppm"))}]");
                foreach (var (from, f) in r.Stretches) say($"  from {from:0} s: delay {f.DelayMedianMs:0.0} (min {f.DelayMinMs:0.0}, max {f.DelayMaxMs:0.0}), pitch {f.WorstPitchCents:0.0} cents, {f.Glitches} glitches");
            }
            return;
        }
        foreach (var s in CaptureScenarios(Math.Max(seconds, 20)))
        {
            var r = Simulation.Run(s);
            say(Describe(r) + $"; delay by stretch: first {r.Findings.DelayFirstMs:0.0}, last {r.Findings.DelayLastMs:0.0}");
        }
    }
}
