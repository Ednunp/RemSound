using RemSound.App.Simulator;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>SIMULATOR CAPTURE SIDE (2026-09-30, the simulator review's second improvement). The real capture code, from
    /// simulated devices the sender opens as it opens real ones: one input goes through push mode, a program's own sound and
    /// two sources through the mixing engine. Each comes out clean and in tune: 48 kHz float; a one-channel 96 kHz 24-bit mic
    /// (resampled, doubled to two); a program's sound, its delay steady once the mixer has settled; two sources 300 ppm apart,
    /// the drifting one's corrector holding it on the mix clock so the clicks on it keep time with the tone on the other; a
    /// device whose callbacks come up to 8 ms late; and one that stops for 200 ms - heard as the one break, and clean after.
    /// The devices hear the real world in real time, whatever their clocks, as a microphone hears the room.</summary>
    private static string SimulatorCaptureSide()
    {
        var all = SimRunner.CaptureScenarios(20);
        SimResult Run(string start, Func<SimScenario, SimScenario>? change = null)
        {
            var s = Require(all.SingleOrDefault(x => x.Name.StartsWith(start, StringComparison.Ordinal)), $"the scenario \"{start}\"");
            return Simulation.Run(change is null ? s : change(s));
        }
        string Say(SimResult r) => SimRunner.Describe(r) + $" [{r.CapturePath}]";

        var push = Run("one input, 48 kHz");
        Check(push.CapturePath == "push mode", $"one input must go through push mode ({push.CapturePath})");
        Check(push.Findings.Clean && push.Findings.WorstPitchCents < 0.5 && push.Findings.DelayMaxMs - push.Findings.DelayMinMs < 2,
            $"48 kHz float must come through clean, in tune and steady - {Say(push)}");

        // Listened to once the receiver has settled: a stream converted from another rate starts with its buffer standing a
        // little deep, and the receiver walks it back over the first half-minute.
        var mono = Run("one mic", s => s with { Seconds = 50, AnalyseFromSecond = 30 });
        // One channel carries the tone and no delay clicks; it is doubled to both, and judged by the tone alone.
        Check(mono.CapturePath == "push mode" && mono.Findings.CleanTone && mono.Findings.WorstPitchCents < 1 && mono.Findings.MedianSnrDb >= 38
              && mono.Findings.AnalysedSeconds > 10,
            $"a one-channel 96 kHz 24-bit mic must come through clean and in tune - {Say(mono)}");
        Check(mono.Findings.ClicksHeard == 0, $"and its tone, on both channels, must not be timed as if it were delay clicks ({mono.Findings.ClicksHeard} \"clicks\")");

        var program = Run("a program's own sound", s => s with { Seconds = 40, AnalyseFromSecond = 10 });
        Check(program.CapturePath == "the mixing engine", $"a program's own sound must go through the mixing engine ({program.CapturePath})");
        Check(program.Findings.Clean && program.Findings.WorstPitchCents < 1 && program.Findings.DelayMaxMs - program.Findings.DelayMinMs < 3,
            $"and come through clean, in tune, its delay steady once the mixer has settled - {Say(program)}");

        // Both hear the real world, so the clicks the drifting one hears must keep time with the tone the other hears - within
        // the few milliseconds the correctors' steering moves them (5 minutes in the simulator: 86 to 88 ms, each half-minute
        // within about 8 ms).
        var two = Run("two sources 300 ppm apart: tone on one", s => s with { Seconds = 90, AnalyseFromSecond = 30 });
        var drifting = two.CaptureDrift.FirstOrDefault(d => d.Name.Contains("+300 ppm", StringComparison.Ordinal));
        Check(two.CapturePath == "the mixing engine" && two.CaptureDrift.Count == 2, $"two sources must go through the mixing engine ({two.CapturePath}, {two.CaptureDrift.Count})");
        Check(drifting.Tracking && Math.Abs((drifting.Ratio - 1) * 1e6) is > 100 and < 600,
            $"the source 300 ppm out must have its drift corrector on it ({(drifting.Ratio - 1) * 1e6:0} ppm, tracking {drifting.Tracking})");
        Check(two.Findings.Clean && two.Findings.WorstPitchCents < 2.5 && two.Findings.DelayMaxMs - two.Findings.DelayMinMs < 10,
            $"and the clicks on it must keep time with the tone on the other, clean - {Say(two)}");

        var lumps = Run("a device that delivers in lumps");
        Check(lumps.Findings.Clean && lumps.Findings.DelayMaxMs - lumps.Findings.DelayMinMs < 3,
            $"a device whose callbacks come up to 8 ms late must still come through clean at 30 ms - {Say(lumps)}");

        var stall = Run("a device that stops for 200 ms at 5 s");
        var at = stall.Findings.FirstGlitchAt is { } g && double.TryParse(g.Replace(" s", ""), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.CurrentCulture, out var v) ? v : double.NaN;
        Check(stall.Findings.Glitches == 1 && Math.Abs(at - 5) < 0.3, $"a device that stops for 200 ms must be heard as one break, at 5 s - {Say(stall)}");
        var after = Simulation.Run(stall.Scenario with { AnalyseFromSecond = 7 });
        Check(after.Findings.Clean, $"and be clean after it - {Say(after)}");

        return $"push mode: 48 kHz float at {push.Findings.DelayMedianMs:0.0} ms, a 96 kHz 24-bit one-channel mic in tune; the mixing engine: "
             + $"a program's sound steady at {program.Findings.DelayMedianMs:0.0} ms once settled, two sources 300 ppm apart held together "
             + $"(the corrector at {(drifting.Ratio - 1) * 1e6:0} ppm); lumps up to 8 ms clean; a 200 ms stall one break, clean after";
    }
}
