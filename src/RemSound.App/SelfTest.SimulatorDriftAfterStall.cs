using RemSound.App.Simulator;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>SIMULATOR DRIFT AFTER A STALL (2026-09-30; found while porting the drift loop to the Mac, where the ported
    /// tests showed it, then measured here). Two sound cards at the SAME rate. The sending card stops for 150 ms - its
    /// sound lost, as a device that stops and comes back loses it - a little before the receiver's drift loop ends a
    /// ten-second window. The loop throws that window away, rightly. But it starts the next window at the same moment, on
    /// a count of what has arrived read off the last fifth of a second of arrivals - which still holds the stream from
    /// BEFORE the stall, 150 ms of sound ahead of where it now is. So the window after the stall, with nothing wrong in
    /// it, reads the sender about 2,500 parts per million slow; it is inside the band a clock could be, so it is believed,
    /// and the receiver played 4 cents flat and let its buffer grow, the error falling away by only 30 % every ten seconds.
    /// Fixed the same day, Ed approving: a stall breaks the arrival line, and the window after a stalled one waits for a
    /// line to read (SessionPlayout.WrittenBytesAt, ArrivalLineSettled). Before the fix: 2,252 ppm; after: 1 to 2.</summary>
    private static string SimulatorDriftAfterAStall()
    {
        // The drift loop is the receiver's, one per stream on each output: ASIO only (the sending card stops), both kinds of
        // output (the same, the ASIO output listened to), and WASAPI only (the sending input device stops).
        SimResult Run(SimConfig config, double stallAt) => Simulation.Run(new SimScenario($"the sending card stops for 150 ms at {stallAt:0.00} s, {config}",
            config, SimCodec.Opus2_5, SimNetwork.Wired, 20, 45,
            SendCard: Simulation.DefaultCard(config != SimConfig.WasapiOnly) with { StallAtSec = stallAt, StallOnceMs = stallAt > 0 ? 150 : 0 }));
        static double Worst(SimResult r, int fromSecond) => r.DriftPpmBySecond.Skip(fromSecond).Where(p => !double.IsNaN(p)).Select(Math.Abs).DefaultIfEmpty(0).Max();

        var said = new List<string>();
        var wrong = new List<string>();
        foreach (var config in new[] { SimConfig.AsioOnly, SimConfig.Both, SimConfig.WasapiOnly })
        {
            var steady = Run(config, 0);
            Check(Worst(steady, 11) < 50, $"premise ({config}): with no stall, two cards at the same rate must be read as the same ({Worst(steady, 11):0} ppm at worst)");
            Check(steady.DriftPpmBySecond.Count >= 40 && steady.DriftPpmBySecond.Skip(11).Any(p => !double.IsNaN(p)),
                $"premise ({config}): the drift loop must have taken readings to look at");
            var worstHere = new List<string>();
            foreach (var stallAt in new[] { 19.70, 19.80, 19.85, 19.90, 19.95 })
            {
                var r = Run(config, stallAt);
                var worst = Worst(r, 21);
                worstHere.Add($"{worst:0}");
                if (worst >= 100) wrong.Add($"{config}: a stall at {stallAt:0.00} s left the drift loop reading {worst:0} ppm");
            }
            said.Add($"{config} {string.Join("/", worstHere)}");
        }
        Check(wrong.Count == 0,
            "after a 150 ms stall in the sending card, two cards at the same rate must still be read as the same (under 100 ppm) - "
            + string.Join("; ", wrong) + ". The window after a stalled one starts on a count read off the arrivals from before the stall.");
        return $"after a 150 ms stall at 19.70 to 19.95 s the drift loop still reads the cards as the same - worst ppm: {string.Join("; ", said)}";
    }
}
