using RemSound.App.Simulator;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>SIMULATOR SERVICE STREAM (2026-09-28, Ed: "the service crackles quite a lot"). The send-only service sends as
    /// a WASAPI-only sender does - one untagged stream of 2.5 ms Opus from 10 ms loopback blocks - and Ed listens to it on his
    /// laptop's ASIO output, in "both". The laptop's own logs from 6 and 24 September show that stream arriving with many more
    /// repaired and unrepaired gaps than the app's ASIO-lane stream on the same network (the receive code of then). With
    /// today's receiving end, measured: the service's stream plays clean into an ASIO-only and a "both" listener on a wired
    /// network and on home wifi, with the everyday auto-tune from 20 ms. This keeps it so.</summary>
    private static string? SimulatorServiceStream()
    {
        var parts = new List<string>();
        // The listener on ASIO only (Ed's laptop), on both (how it runs), and on WASAPI only (a listener without ASIO).
        foreach (var (config, net) in new[] { (SimConfig.AsioOnly, SimNetwork.Wired), (SimConfig.AsioOnly, SimNetwork.Wifi), (SimConfig.Both, SimNetwork.Wifi), (SimConfig.WasapiOnly, SimNetwork.Wifi) })
        {
            var r = Simulation.Run(new SimScenario($"service -> {config} listener", config, SimCodec.Opus2_5, net, 20, 60,
                ContinuousAutoTune: true, SendWasapiOnly: true));
            var f = r.Findings;
            Check(r.Streams.Count > 0 && r.Streams.All(s => s.Contains("Mixed", StringComparison.Ordinal)),
                $"{config}, {net.Name}: the listener must hear one untagged stream, as the service sends - it heard [{string.Join(", ", r.Streams)}]");
            Check(f.ClicksHeard > 0, $"{config}, {net.Name}: the service's stream must be heard");
            Check(f.Clean, $"{config}, {net.Name}: the service's stream must play clean - {SimRunner.Describe(r)}");
            parts.Add($"{config} on {net.Name}: {f.DelayMedianMs:0} ms, clean");
        }
        return "the service's untagged WASAPI stream into each kind of listener: " + string.Join("; ", parts);
    }
}
