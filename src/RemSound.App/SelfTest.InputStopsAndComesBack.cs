using RemSound.App.Simulator;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>INPUT STOPS AND COMES BACK (2026-09-30, Ed approved). The sender's sound stops for 200 ms and comes back - its
    /// card hiccups, the send-only service reopens its capture, the sending PC stalls. The receiver's buffer runs dry, and
    /// until this date playback carried on the moment sound returned, with almost nothing in reserve: 53 ms of delay became
    /// 33 at a 30 ms buffer (83 to 33 at 60 ms), for 15 s, then crept back over another 30 to 50. For that minute there
    /// was no jitter buffer. Now a stop of more than 100 ms is treated as a stream starting: nothing plays until the
    /// buffer is back at its target. Checked in WASAPI only (an input device that stops), ASIO only and both (the sending
    /// card itself stops); and on networks whose momentary empties must be left exactly as they were - no refill for those,
    /// which is the April 2026 behaviour Ed got rid of.</summary>
    private static string InputStopsAndComesBack()
    {
        var said = new List<string>();
        List<(int Frame, double HeardSec, double DelayMs)> Clicks(SimResult r) => SimAnalysis.Clicks(Require(r.Audio, "the run's sound"), r.AudioTimes, Simulation.SampleRate);
        static double Median(IEnumerable<double> v) { var o = v.OrderBy(x => x).ToList(); return o.Count == 0 ? double.NaN : o[o.Count / 2]; }

        // WASAPI only: the input device stops for 200 ms at 5 s.
        foreach (var buffer in new[] { 30, 60 })
        {
            var r = Simulation.Run(new SimScenario($"an input stops for 200 ms, {buffer} ms buffer", SimConfig.WasapiOnly, SimCodec.Opus2_5, SimNetwork.Wired,
                buffer, 25, KeepAudio: true,
                CaptureDevices: [new SimCaptureDevice("stalling input", new SimCard(480, JitterMs: 1, StallAtSec: 5, StallOnceMs: 200))]));
            var clicks = Clicks(r);
            var before = Median(clicks.Where(c => c.HeardSec is > 1 and < 5).Select(c => c.DelayMs));
            var after = clicks.Where(c => c.HeardSec is > 5.6 and < 20).Select(c => c.DelayMs).ToList();
            Check(after.Count > 20 && after.All(d => Math.Abs(d - before) < 3),
                $"WASAPI only, {buffer} ms buffer: after the input's 200 ms stop the delay must be back where it was at once, not "
                + $"{buffer} ms short of it ({before:0.0} ms before; after, {after.DefaultIfEmpty(double.NaN).Min():0.0} to {after.DefaultIfEmpty(double.NaN).Max():0.0})");
            Check(r.LongGapRearms == 1, $"WASAPI only, {buffer} ms buffer: the stop must be refilled once ({r.LongGapRearms} times)");
            said.Add($"WASAPI {buffer} ms: {before:0} ms before, {Median(after):0} after");
        }

        // ASIO only, and both kinds of output (the ASIO one listened to, its WASAPI lane beside it): the sending card itself
        // stops. Its tone and clicks are made by count, so after the stop they come 200 ms later than the clock says; that is
        // taken off.
        foreach (var config in new[] { SimConfig.AsioOnly, SimConfig.Both })
        {
            var r = Simulation.Run(new SimScenario($"the sending card stops for 200 ms, {config}", config, SimCodec.Opus2_5, SimNetwork.Wired,
                30, 25, KeepAudio: true, SendCard: Simulation.DefaultCard(true) with { StallAtSec = 5, StallOnceMs = 200 }));
            var clicks = Clicks(r);
            var before = Median(clicks.Where(c => c.HeardSec is > 1 and < 5).Select(c => c.DelayMs));
            var after = clicks.Where(c => c.HeardSec is > 5.6 and < 20).Select(c => c.DelayMs - 200).ToList();
            Check(after.Count > 20 && after.All(d => Math.Abs(d - before) < 3),
                $"{config}: after the sending card's 200 ms stop the delay must be back where it was at once ({before:0.0} ms before; after, "
                + $"{after.DefaultIfEmpty(double.NaN).Min():0.0} to {after.DefaultIfEmpty(double.NaN).Max():0.0})");
            Check(r.LongGapRearms >= 1, $"{config}: the stop must be refilled ({r.LongGapRearms} times)");
            said.Add($"{config} 30 ms: {before:0} ms before, {Median(after):0} after, {r.LongGapRearms} refill(s)");
        }

        // Momentary empties - a lumpy receiver (its 60 ms holds included), home wifi at a tight buffer - are left as they were: never a refill.
        foreach (var (config, net, buffer) in new[] { (SimConfig.AsioOnly, SimNetwork.Lumpy, 10), (SimConfig.WasapiOnly, SimNetwork.Lumpy, 20), (SimConfig.AsioOnly, SimNetwork.Wifi, 5) })
        {
            var r = Simulation.Run(new SimScenario($"{net.Name}, {config}, {buffer} ms", config, SimCodec.Opus2_5, net, buffer, 20));
            Check(r.Underruns > 0, $"premise: {net.Name} at {buffer} ms must empty the buffer now and then ({config}, {r.Underruns} underruns)");
            Check(r.LongGapRearms == 0, $"{net.Name} at {buffer} ms ({config}): its momentary empties must play on as before, never refilled ({r.LongGapRearms} refills, {r.Underruns} underruns)");
        }
        return "a real stop is refilled at once - " + string.Join("; ", said) + "; momentary empties on a lumpy receiver and wifi left as they were";
    }
}
