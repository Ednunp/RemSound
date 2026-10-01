using RemSound.App.Simulator;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>How far a wall-clock run's delay may sit from virtual time's for the same setup, fed straight to the lane
    /// (measured 2026-09-30 on Ed's desktop: 0.0 to 0.1 ms, every run).</summary>
    private const double WallClockAgreesMs = 2;

    /// <summary>The longest wait between packets at the receiver's socket that a sender of packets 2.5 ms apart or closer
    /// may show on loopback (measured: 1 to 2 ms).</summary>
    private const int WallClockSocketWaitMs = 8;

    /// <summary>SIMULATOR WALL CLOCK (2026-09-30, the simulator review's seventh improvement). The same real sender and
    /// receiver on real threads, the machine's own clock and a real loopback socket. Each setup must be heard whole, every
    /// packet sent must arrive, the run must really take its seconds, and the delay must be what virtual time says the same
    /// setup does - the two modes checking each other. Packets sent 2.5 ms apart or closer must reach the receiver's socket
    /// without a long wait. The capture side runs too: one device through push mode, two through the mixing engine on its
    /// own thread - and there the wall clock must SEE Windows' timer: without the fast timer the mix tick slips to the
    /// 15.6 ms tick, with it the packets keep to 10 ms. A run the machine itself stalled is run once more, and said to be
    /// the machine's if it stalls again.</summary>
    private static string? SimulatorWallClock()
    {
        const double seconds = 3;
        var all = SimRunner.WallClockScenarios(seconds);
        SimScenario Named(string start) => Require(all.SingleOrDefault(x => x.Name.StartsWith(start, StringComparison.Ordinal)), $"the scenario \"{start}\"");
        // Once more when the first go is not good: this is the machine's own clock, and another program can take a core
        // for a moment. A fault in RemSound or in the mode is there both times.
        static WallClockResult Twice(SimScenario s, Func<WallClockResult, bool> good, bool fastTimer = true)
        {
            var first = WallClockSimulation.Run(s, fastTimer);
            return good(first) ? first : WallClockSimulation.Run(s, fastTimer);
        }
        static bool Whole(WallClockResult r) => r.Findings.ClicksHeard > 0 && r.Findings.Clean && r.Underruns == 0 && r.PacketsSent > 0 && r.PacketsReceived == r.PacketsSent;

        // 1. Fed straight to the lane, and one device through push mode: heard whole, and where virtual time says.
        var said = new List<string>();
        foreach (var start in new[] { "everyday", "jamming: ASIO only, 2.5 ms Opus", "jamming: ASIO only, Jamulus", "ASIO only, PCM", "one device" })
        {
            var s = Named(start);
            var inVirtualTime = Simulation.Run(s);
            var virtualDelay = inVirtualTime.Findings.DelayMedianMs;
            var close = s.Config == SimConfig.AsioOnly;   // its packets are 2.5 ms apart or closer
            bool Good(WallClockResult x) => Whole(x) && !x.MachineStalled && Math.Abs(x.Findings.DelayMedianMs - virtualDelay) <= WallClockAgreesMs
                                            && (!close || x.MaxArrivalGapMs <= WallClockSocketWaitMs);
            var r = Twice(s, Good);
            if (r.MachineStalled)
                return Skip($"the machine stalled a simulated card's own thread twice running ({WallClockSimulation.Describe(r)}): what was heard says nothing about RemSound");
            Check(r.PacketsSent > 0 && r.PacketsReceived == r.PacketsSent, $"every packet sent over loopback must arrive - {WallClockSimulation.Describe(r)}");
            Check(Whole(r), $"on the wall clock it must be heard whole - {WallClockSimulation.Describe(r)}");
            Check(r.RealSeconds >= seconds, $"a wall-clock run must really take its {seconds:0} seconds (it took {r.RealSeconds:0.00})");
            Check(Math.Abs(r.PacketsSent - inVirtualTime.PacketsSent) <= inVirtualTime.PacketsSent * 0.02,
                $"and send what {seconds:0} seconds of sound is: {inVirtualTime.PacketsSent} packets in virtual time, {r.PacketsSent} here");
            Check(Math.Abs(r.Findings.DelayMedianMs - virtualDelay) <= WallClockAgreesMs,
                $"the wall clock and virtual time must agree on the delay within {WallClockAgreesMs} ms ({virtualDelay:0.0} ms in virtual time) - {WallClockSimulation.Describe(r)}");
            if (close)
                Check(r.MaxArrivalGapMs <= WallClockSocketWaitMs,
                    $"packets sent 2.5 ms apart or closer must not wait {r.MaxArrivalGapMs} ms at the receiver's socket - {WallClockSimulation.Describe(r)}");
            if (start == "one device") Check(r.CapturePath == "push mode", $"one device must go through push mode ({r.CapturePath})");
            said.Add($"{start.Split(':')[0].Split(',')[0]} {r.Findings.DelayMedianMs:0.0} ms ({virtualDelay:0.0})");
        }

        // 2. Two devices through the mixing engine, on its own thread. Its tick is not in step with the devices, so the delay
        // is within one tick of virtual time's, not on it.
        var mixed = Named("two devices");
        var mixedVirtual = Simulation.Run(mixed).Findings.DelayMedianMs;
        bool MixedGood(WallClockResult x) => Whole(x) && !x.MachineStalled && x.MaxArrivalGapMs <= 13
                                             && x.Findings.DelayMedianMs - mixedVirtual is > -2 and < 12;
        var on = Twice(mixed, MixedGood);
        if (on.MachineStalled)
            return Skip($"the machine stalled a simulated device's own thread twice running ({WallClockSimulation.Describe(on)}): what was heard says nothing about RemSound");
        Check(on.CapturePath == "the mixing engine", $"two devices must go through the mixing engine ({on.CapturePath})");
        Check(Whole(on), $"two devices through the mixing engine's own thread must be heard whole - {WallClockSimulation.Describe(on)}");
        Check(on.Findings.DelayMedianMs - mixedVirtual is > -2 and < 12,
            $"and within one mix tick of virtual time's delay ({mixedVirtual:0.0} ms) - {WallClockSimulation.Describe(on)}");

        // 3. The wall clock sees Windows' timer. This process has no window anyone can see, so without the fast timer its
        // mix tick is on the 15.6 ms tick (28 Sep) - unless this Windows keeps the fine timer for it anyway.
        var off = WallClockSimulation.Run(mixed, fastTimer: false);
        var timer = "this Windows keeps the fine timer for a hidden process anyway, so the mix tick shows nothing without it";
        if (off.MaxArrivalGapMs >= 14)
        {
            Check(on.MaxArrivalGapMs <= 13,
                $"with the fast timer the mixing engine's packets must keep to their 10 ms (the longest wait {on.MaxArrivalGapMs} ms; without it {off.MaxArrivalGapMs} ms)");
            timer = $"the mix tick's packets {on.MaxArrivalGapMs} ms apart at most with the fast timer, {off.MaxArrivalGapMs} ms without";
        }
        else Check(on.MaxArrivalGapMs <= 13, $"the mixing engine's packets must keep to their 10 ms (the longest wait {on.MaxArrivalGapMs} ms)");

        // 4. What the mode cannot do, it must say.
        static bool Refuses(SimScenario s)
        {
            try { WallClockSimulation.Run(s); return false; }
            catch (ArgumentException) { return true; }
        }
        Check(Refuses(new SimScenario("both", SimConfig.Both, SimCodec.Opus2_5, SimNetwork.Perfect, 20, 1)), "both kinds of output at once must be refused, not run as one");
        Check(Refuses(new SimScenario("stalling card", SimConfig.AsioOnly, SimCodec.Opus2_5, SimNetwork.Perfect, 20, 1, SendCard: new SimCard(32, StallEverySec: 1, StallMs: 30))),
            "a card that stalls must be refused: on the wall clock only the machine stalls a card");

        return $"wall clock against virtual time: {string.Join("; ", said)}; the mixing engine {on.Findings.DelayMedianMs:0.0} ms ({mixedVirtual:0.0}); {timer}";
    }
}
