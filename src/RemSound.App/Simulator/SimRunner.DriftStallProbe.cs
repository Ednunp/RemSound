namespace RemSound.App.Simulator;

/// <summary>
/// --simulate drift-stall: a PROBE (2026-09-30), not a gate step. Two cards at the same rate; the sending card stops for
/// 150 ms (its sound is lost, as a device that stops and comes back loses it) at a moment near the end of the drift
/// loop's second ten-second window. The window the stall falls in is thrown away; the question is what the window
/// AFTER it reads. Prints the drift loop's ratio (ppm) and the delay second by second.
/// </summary>
internal static partial class SimRunner
{
    private static void DriftStallProbe(Action<string> say)
    {
        foreach (var config in new[] { SimConfig.AsioOnly, SimConfig.WasapiOnly })
            foreach (var stallAt in new[] { 0.0, 15.0, 19.7, 19.85, 19.95, 20.05 })
            {
                var asio = config == SimConfig.AsioOnly;
                var card = Simulation.DefaultCard(asio) with { StallAtSec = stallAt, StallOnceMs = stallAt > 0 ? 150 : 0 };
                var r = Simulation.Run(new SimScenario($"sending card stops 150 ms at {stallAt:0.00} s", config, SimCodec.Opus2_5, SimNetwork.Wired,
                    asio ? 20 : 40, 65, SendCard: card, KeepAudio: true));
                var ppm = r.DriftPpmBySecond;
                string At(int second) => second - 1 < ppm.Count ? $"{ppm[second - 1]:+0;-0;0}" : "?";
                // The delay at each click, from the kept audio: the first and last of the run and the extremes after the stall.
                var clicks = SimAnalysis.Clicks(r.Audio!, r.AudioTimes, Simulation.SampleRate);
                var after = clicks.Where(c => c.HeardSec > 22).Select(c => c.DelayMs).ToList();
                say($"{config}, {(stallAt > 0 ? $"stall at {stallAt:0.00} s" : "no stall")}: drift loop ppm at 12 s {At(12)}, 22 s {At(22)}, 32 s {At(32)}, 42 s {At(42)}, 52 s {At(52)}, 62 s {At(62)}; "
                    + $"delay after the stall {(after.Count > 0 ? $"{after.Min():0.0} to {after.Max():0.0} ms (last {after[^1]:0.0})" : "?")}; underruns {r.Underruns}");
            }
    }
}
