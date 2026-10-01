using System.Net;
using RemSound.App.Simulator;
using RemSound.Core;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>SIMULATOR SERVER HOP (2026-09-30, the simulator review's sixth improvement). People playing to one listener
    /// through a server: real senders and a real receiver, each with its own real group client, and a server between them
    /// that passes sound on as server/remsound-relay.py does. One through a wired server is as clean as direct, and later
    /// by the second network only. Three at once arrive as three streams, each credited to its own address, each tone whole
    /// and at its own level. Sound passes only between two who have ticked each other, whichever of them has not. The
    /// internet on both sides of the server needs a buffer for both; a server that is late passing sound on breaks a small
    /// one. And the same run twice is the same.</summary>
    private static string SimulatorServerHop()
    {
        var all = SimRunner.ServerScenarios(15);
        ServerResult Run(string start) => ServerSimulation.Run(Require(all.SingleOrDefault(x => x.Name.StartsWith(start, StringComparison.Ordinal)), $"the scenario \"{start}\""));
        static bool Whole(ServerSenderResult x) => x.Heard && x.BreakUps == 0 && Math.Abs(x.Level - ServerSimulation.ToneLevel) < 0.02;

        // 1. One through a wired server, against the same two machines with no server between.
        var one = Run("one through");
        var direct = Simulation.Run(new SimScenario("no server", SimConfig.AsioOnly, SimCodec.Opus2_5, SimNetwork.Wired, 10, 15));
        Check(Whole(one.Senders[0]) && one.SessionsOpened == 1 && one.Clicks.DelayMaxMs - one.Clicks.DelayMinMs < 1.5,
            $"one person through a wired server must be heard whole, as one stream, the delay steady - {ServerSimulation.Describe(one)}");
        var extra = one.Clicks.DelayMedianMs - direct.Findings.DelayMedianMs;
        Check(extra is > -1 and < 3, $"and later than direct only by the server's own network ({one.Clicks.DelayMedianMs:0.0} against {direct.Findings.DelayMedianMs:0.0} ms direct)");
        Check(IPEndPoint.TryParse(one.Senders[0].Address, out var credited) && RelayGroupClient.IsMemberAddress(credited.Address),
            $"and credited to a group member's address, not the server's ({one.Senders[0].Address})");

        // 2. Three at once.
        var three = Run("three through");
        Check(three.Senders.All(Whole) && three.SessionsOpened == 3 && three.Senders.Select(x => x.Address).Distinct().Count() == 3,
            $"three people must arrive as three streams, each at its own address, each tone whole and at its own level - {ServerSimulation.Describe(three)}");
        Check(three.Underruns == 0 && three.Clicks.DelayMaxMs - three.Clicks.DelayMinMs < 1.5, $"with nothing lost to the mix - {ServerSimulation.Describe(three)}");
        Check(three.OrdinarySound == 0, $"and nobody's sound must reach the server in the ordinary one-to-one form, which it would take for a pair's ({three.OrdinarySound} packets)");

        // 3. Ticks, either way round.
        var unticked = Run("the listener has not ticked Andre");
        Check(!unticked.Senders[1].Heard && Whole(unticked.Senders[0]) && Whole(unticked.Senders[2]) && unticked.SessionsOpened == 2,
            $"somebody the listener has not ticked must not be heard, and the others untouched - {ServerSimulation.Describe(unticked)}");
        Check(unticked.Forwarded < three.Forwarded * 0.7, $"because the server does not pass their sound on at all ({unticked.Forwarded} passed on, against {three.Forwarded})");
        var notTicking = Run("Jonathan has not ticked the listener");
        Check(!notTicking.Senders[2].Heard && Whole(notTicking.Senders[0]) && Whole(notTicking.Senders[1]) && notTicking.SessionsOpened == 2,
            $"and somebody who has not ticked the listener is not heard either - {ServerSimulation.Describe(notTicking)}");

        // 4. The internet on both sides of the server.
        var thin = Run("the internet each side of the server, 10 ms");
        var deep = Run("the internet each side of the server, 60 ms");
        Check(thin.Underruns > 20 && thin.Senders.Sum(x => x.BreakUps) > 0, $"two internet legs must break up a 10 ms buffer - {ServerSimulation.Describe(thin)}");
        Check(deep.Senders.All(Whole) && deep.Underruns == 0 && deep.Clicks.DelayMedianMs is > 84 and < 110,
            $"and play whole at 60 ms, later by both legs - {ServerSimulation.Describe(deep)}");

        // 5. A server that is late passing sound on.
        var late = Run("a server late");
        Check(late.Underruns > 10 && three.Underruns == 0, $"a server late with one packet in fifty must break a 10 ms buffer that a prompt one leaves whole - {ServerSimulation.Describe(late)}");

        // 6. The same run twice is the same.
        var again = Run("three through");
        Check(again.Forwarded == three.Forwarded && again.Clicks.DelayMedianMs == three.Clicks.DelayMedianMs
              && again.Senders.Select(x => x.Level).SequenceEqual(three.Senders.Select(x => x.Level)),
            "the same run twice must give the same figures");

        return $"one through a wired server {one.Clicks.DelayMedianMs:0.0} ms ({direct.Findings.DelayMedianMs:0.0} direct); three as three streams, whole; "
             + $"the unticked not passed on, either way; two internet legs {thin.Senders.Sum(x => x.BreakUps)} break-ups at 10 ms and none at 60 "
             + $"({deep.Clicks.DelayMedianMs:0.0} ms); a late server {late.Underruns} underruns at 10 ms";
    }
}
