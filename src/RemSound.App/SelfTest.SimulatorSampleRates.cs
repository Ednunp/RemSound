using RemSound.App.Simulator;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>SIMULATOR SAMPLE RATES (2026-09-30, the simulator review's fifth improvement). Devices that do not run at
    /// 48 kHz. On the receiving side nothing of RemSound's own runs at another rate - ASIO is opened at 48 kHz and Windows
    /// converts for a WASAPI output - so this is the capture side, where RemSound's own resamplers work: the mixing engine's,
    /// for inputs from 22.05 to 192 kHz, and push mode's at the rates it divides evenly (96, 176.4 and 192 kHz). Each
    /// comes through whole, clean and in tune. Push mode below 48 kHz and at 88.2 kHz is SIMULATOR PUSH MODE RATES.</summary>
    private static string SimulatorSampleRates()
    {
        var said = new List<string>();
        // The mixing engine: a program's own sound at each rate (one source always goes through the mixer when it is a
        // program's), listened to once the mixer has settled.
        foreach (var rate in new[] { 22050, 32000, 44100, 88200, 96000, 192000 })
        {
            var device = new SimCaptureDevice($"a program at {rate} Hz", new SimCard(rate / 100, JitterMs: 1), SampleRate: rate, PerApp: true);
            var r = Simulation.Run(new SimScenario($"the mixing engine at {rate} Hz", SimConfig.WasapiOnly, SimCodec.Opus2_5, SimNetwork.Wired, 30, 25,
                CaptureDevices: [device], AnalyseFromSecond: 10));
            Check(r.CapturePath == "the mixing engine", $"{rate} Hz: a program's sound must go through the mixing engine ({r.CapturePath})");
            Check(r.Findings.Clean && r.Findings.WorstPitchCents < 1 && r.Findings.MedianSnrDb >= 40,
                $"the mixing engine must bring a {rate} Hz source through clean and in tune - {SimRunner.Describe(r)}");
            said.Add($"{rate / 1000.0:0.##} kHz {r.Findings.MedianSnrDb:0} dB");
        }

        // Push mode above 48 kHz: on its own, nothing lost; and end to end, clean and in tune once the receiver has settled.
        foreach (var rate in new[] { 96000, 176400, 192000 }) PushModeWhole(rate, rate / 100);
        var push96 = Simulation.Run(new SimScenario("push mode at 96 kHz", SimConfig.WasapiOnly, SimCodec.Opus2_5, SimNetwork.Wired, 30, 50,
            CaptureDevices: [new SimCaptureDevice("input 96k", new SimCard(960, JitterMs: 1), SampleRate: 96000)], AnalyseFromSecond: 30));
        Check(push96.CapturePath == "push mode" && push96.Findings.Clean && push96.Findings.WorstPitchCents < 0.5 && push96.Findings.MedianSnrDb >= 40,
            $"and a 96 kHz input must come through push mode clean and in tune - {SimRunner.Describe(push96)}");

        return $"the mixing engine clean and in tune at {string.Join(", ", said)}; push mode whole at 96, 176.4 and 192 kHz "
             + $"({push96.Findings.MedianSnrDb:0} dB end to end at 96 kHz)";
    }

    /// <summary>What push mode's converter may hold back at any moment: its filter's reach (measured 2026-09-30: 102 frames
    /// at 16 kHz, 37 at 44.1, 17 at 96), well under this.</summary>
    private const double PushModeHoldLimitFrames = 120;   // 2.5 ms at 48 kHz

    /// <summary>Push mode on its own at <paramref name="rate"/>: nothing lost - it holds only its filter's reach, the same
    /// after 4,000 callbacks as after 400 - and the tone clean.</summary>
    private static string PushModeWhole(int rate, int frames)
    {
        var (held, heldLong, snr) = SimRunner.PushModeHold(rate, frames);
        Check(Math.Abs(heldLong - held) <= 1 && held >= -1 && held <= PushModeHoldLimitFrames && snr >= 60,
            $"push mode at {rate} Hz in {frames}-frame callbacks must lose nothing: it may hold back only its filter's reach, the same "
            + $"however long it runs, and the tone must stay clean ({held:0} frames held after 400 callbacks, {heldLong:0} after 4,000; {snr:0} dB)");
        return $"{rate / 1000.0:0.##} kHz holds {held:0}, {snr:0} dB";
    }

    /// <summary>SIMULATOR PUSH MODE RATES. One input - the everyday setup, and the service's - goes through push mode, which
    /// converts it to 48 kHz itself. Found 2026-09-30 by the capture side in the loop: below 48 kHz (16, 22.05, 32 and
    /// 44.1 kHz) and at 88.2 kHz it lost frames - 239 in 400 callbacks at 44.1 kHz - so the sound came out short, about
    /// 2 cents sharp, with a small break every callback (27 dB of tone over noise, where the mixing engine gives 51). The
    /// converter was driven by its output, and the part-sample left over whenever the rates did not divide evenly was
    /// dropped. Fixed the same day, Ed approving: it is driven by its input, every frame the device gives going in. Now it
    /// holds only its filter's reach, the same however long it runs; end to end a 44.1 kHz input is in tune once the
    /// receiver has settled, and the receiver's drift loop reads it as the steady 48 kHz it is (it read -1,245 ppm before).</summary>
    private static string SimulatorPushModeRates()
    {
        var said = new List<string>();
        foreach (var (rate, frames) in new[] { (16000, 160), (22050, 220), (32000, 320), (44100, 441), (44100, 448), (88200, 882) })
            said.Add(PushModeWhole(rate, frames));
        var r = Simulation.Run(new SimScenario("push mode at 44.1 kHz", SimConfig.WasapiOnly, SimCodec.Opus2_5, SimNetwork.Wired, 30, 60,
            CaptureDevices: [new SimCaptureDevice("input 44.1k", new SimCard(441, JitterMs: 1), SampleRate: 44100)], AnalyseFromSecond: 40));
        Check(r.CapturePath == "push mode" && r.Findings.Clean && r.Findings.WorstPitchCents < 0.5 && r.Findings.MedianSnrDb >= 40,
            $"and a 44.1 kHz input must come through clean and in tune once the receiver has settled - {SimRunner.Describe(r)}");
        var readings = r.DriftPpmBySecond.Skip(20).Where(p => !double.IsNaN(p)).ToList();
        // Its ordinary wander at 48 kHz is some tens of ppm either way; the lost frames made it read -1,245.
        Check(readings.Count > 0 && readings.All(p => Math.Abs(p) < 200),
            $"and the receiver's drift loop must read it as the steady 48 kHz it is, not 1,245 ppm slow (from 20 s: {string.Join(" ", readings.Where((_, i) => i % 10 == 0).Select(p => p.ToString("+0;-0;0")))} ppm)");
        return $"push mode loses nothing at any rate: {string.Join("; ", said)}; a 44.1 kHz input in tune end to end, read as steady by the receiver";
    }
}
