namespace RemSound.App.Simulator;

/// <summary>
/// --simulate wall [--seconds N]: the wall-clock mode (2026-09-30). The everyday and the jamming setups on real threads,
/// real timers and a real loopback socket, each beside what virtual time says the same setup does on a perfect network.
/// </summary>
internal static partial class SimRunner
{
    /// <summary>The wall-clock scenarios, as the gate runs them too.</summary>
    internal static IReadOnlyList<SimScenario> WallClockScenarios(double seconds) =>
    [
        new("everyday: WASAPI only, 2.5 ms Opus", SimConfig.WasapiOnly, SimCodec.Opus2_5, SimNetwork.Perfect, 60, seconds, AnalyseFromSecond: 1, ReceiveCard: new SimCard(480, JitterMs: 1, PhaseMs: 5)),
        new("jamming: ASIO only, 2.5 ms Opus", SimConfig.AsioOnly, SimCodec.Opus2_5, SimNetwork.Perfect, 20, seconds, AnalyseFromSecond: 1),
        new("jamming: ASIO only, Jamulus-style 64", SimConfig.AsioOnly, SimCodec.Custom64, SimNetwork.Perfect, 20, seconds, AnalyseFromSecond: 1),
        new("ASIO only, PCM", SimConfig.AsioOnly, SimCodec.Pcm, SimNetwork.Perfect, 20, seconds, AnalyseFromSecond: 1),
        // The capture side, real threads and all: one device through push mode; two through the mixing engine, whose own
        // thread sleeps on Windows' timer - the part of the sending side the 15.6 ms timer reaches.
        new("one device, push mode", SimConfig.WasapiOnly, SimCodec.Opus2_5, SimNetwork.Perfect, 60, seconds, AnalyseFromSecond: 1,
            ReceiveCard: new SimCard(480, JitterMs: 1, PhaseMs: 5),
            CaptureDevices: [new SimCaptureDevice("the microphone", new SimCard(480, JitterMs: 1))]),
        new("two devices, the mixing engine", SimConfig.WasapiOnly, SimCodec.Opus2_5, SimNetwork.Perfect, 60, seconds, AnalyseFromSecond: 1,
            ReceiveCard: new SimCard(480, JitterMs: 1, PhaseMs: 5),
            CaptureDevices: [new SimCaptureDevice("the microphone", new SimCard(480, JitterMs: 1)),
                             new SimCaptureDevice("the speakers' own sound", new SimCard(480, JitterMs: 1, PhaseMs: 3), Tone: false, Clicks: false, Loopback: true)]),
    ];

    private static void WallClock(double seconds, Action<string> say)
    {
        foreach (var s in WallClockScenarios(seconds))
        {
            var real = WallClockSimulation.Run(s);
            say(WallClockSimulation.Describe(real));
            say("  without the fast timer: " + WallClockSimulation.Describe(WallClockSimulation.Run(s, fastTimer: false)));
            var virtualRun = Simulation.Run(s);
            say($"  in virtual time: delay {virtualRun.Findings.DelayMedianMs:0.0} ms, {(virtualRun.Findings.Clean ? "CLEAN" : $"{virtualRun.Findings.Glitches} glitches")} "
                + $"- the wall clock is {real.Findings.DelayMedianMs - virtualRun.Findings.DelayMedianMs:+0.0;-0.0} ms from it");
        }
    }
}
