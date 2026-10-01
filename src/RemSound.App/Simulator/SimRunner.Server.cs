namespace RemSound.App.Simulator;

/// <summary>
/// --simulate server: people playing to one listener through a server (2026-09-30). One on a wired network; three at once,
/// each on their own tone; one the listener has not ticked, and one who has not ticked the listener; the internet on both
/// sides of the server, at a buffer too small for it and at one big enough; and a server that is late passing sound on.
/// </summary>
internal static partial class SimRunner
{
    internal static IReadOnlyList<ServerScenario> ServerScenarios(double seconds)
    {
        ServerSender Person(string name, double hz, SimNetwork? net = null, bool ticks = true) => new(name, hz, net ?? SimNetwork.Wired, TicksListener: ticks);
        var band = new[] { Person("Ed", 1000), Person("Andre", 1500), Person("Jonathan", 700) };
        var internet = new SimNetwork("internet leg", 12, 3, HiccupPercent: 0.2, HiccupMs: 5);
        return
        [
            new("one through a wired server", [Person("Ed", 1000)], SimNetwork.Wired, 10, seconds),
            new("three through a wired server", band, SimNetwork.Wired, 10, seconds),
            new("the listener has not ticked Andre", band, SimNetwork.Wired, 10, seconds, ListenerTicks: [0, 2]),
            new("Jonathan has not ticked the listener", [band[0], band[1], Person("Jonathan", 700, ticks: false)], SimNetwork.Wired, 10, seconds),
            new("the internet each side of the server, 10 ms", band.Select(p => p with { ToServer = internet }).ToList(), internet, 10, seconds),
            new("the internet each side of the server, 60 ms", band.Select(p => p with { ToServer = internet }).ToList(), internet, 60, seconds),
            new("a server late with one packet in fifty, by up to 25 ms, 10 ms", band, SimNetwork.Wired, 10, seconds, ServerLatePercent: 2, ServerLateMs: 25),
        ];
    }

    private static void Server(double seconds, Action<string> say)
    {
        foreach (var s in ServerScenarios(seconds)) say(ServerSimulation.Describe(ServerSimulation.Run(s)));
    }
}
