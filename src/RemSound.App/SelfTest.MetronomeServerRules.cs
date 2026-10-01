using System.Net;
using RemSound.Core;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>METRONOME SERVER RULES (2026-09-29, PLAN-metronome-server-2026-09-29.md; Ed: "you can still be the master
    /// metronome, but you then control the server. anyone can ask to be master just like before and they take over server").
    /// The engines alone, on a stand-in clock, with the server's side played by the test: leading claims the server's
    /// metronome and sends it the setting; nothing clicks until the server's grid comes, and then each machine plays its OWN
    /// grid, in its own clock; a grid not yet measured plays nothing; the setting is repeated each second; a follower that
    /// hears nothing from the server stops; a master the server keeps refusing gives way and says who leads it.</summary>
    private static string? MetronomeServerRules()
    {
        double real = 80_000_000;                                   // the true time, in microseconds
        long ClockA() => (long)real;
        long ClockB() => (long)(real * (1 - 30e-6)) + 987_654;      // miles from A's, and 30 ppm slow
        var epA = new IPEndPoint(IPAddress.Parse("10.9.1.1"), 47999);
        var epB = new IPEndPoint(IPAddress.Parse("10.9.1.2"), 47999);
        var idA = Guid.NewGuid();
        var idAndre = Guid.NewGuid();
        var sound = new MetronomeSound("test", Impulse(0.8f), Impulse(0.4f));
        var flying = new List<(double At, MetronomeMessage M, IPEndPoint From, IPEndPoint To)>();
        var engines = new List<MetronomeEngine>();
        var grids = new Dictionary<MetronomeEngine, ServerGrid?>();
        var toServer = new Dictionary<MetronomeEngine, List<(double At, byte[] Body)>>();

        MetronomeEngine Engine(Func<long> clock, IPEndPoint? at, List<string> log, MetronomeSettings settings)
        {
            var e = new MetronomeEngine(clock, log.Add);
            var sent = new List<(double At, byte[] Body)>();
            toServer[e] = sent;
            e.OnServerNow = () => true;
            e.ToServer = body => sent.Add((real, body));
            e.ServerMemberName = id => id == idA ? "A" : id == idAndre ? "Andre" : null;
            e.Peers = () => [];
            if (at is not null) e.Send = (m, to) => flying.Add((real + 2000, m, at, to));
            e.Configure(settings, sound);
            engines.Add(e);
            return e;
        }
        void Run(double seconds)
        {
            var end = real + seconds * 1e6;
            var nextTick = real;
            var nextGrid = real;
            while (real < end)
            {
                foreach (var f in flying.Where(f => f.At <= real).ToList())
                {
                    flying.Remove(f);
                    var (engine, clock) = f.To.Equals(epA) ? (engines[0], (Func<long>)ClockA) : (engines[1], ClockB);
                    engine.OnMessage(f.M, f.From, clock());
                }
                if (real >= nextGrid)
                {
                    // The server's grids, once a second to each member it has a grid for.
                    foreach (var e in engines) if (grids.TryGetValue(e, out var g) && g is not null) e.OnServerGrid(g);
                    nextGrid += 1_000_000;
                }
                if (real >= nextTick) { foreach (var e in engines) e.Tick(); nextTick += 50_000; }
                real += 500;
            }
        }
        int Count(MetronomeEngine e, byte kind, double from) => toServer[e].Count(x => x.At >= from && x.Body.Length > 0 && x.Body[0] == kind);
        bool SentSet(MetronomeEngine e, double from, byte[] set) => toServer[e].Any(x => x.At >= from && x.Body.AsSpan().SequenceEqual(set));

        var logA = new List<string>();
        var logB = new List<string>();
        var a = Engine(ClockA, epA, logA, new MetronomeSettings { Bpm = 120 });
        var b = Engine(ClockB, epB, logB, new MetronomeSettings { Bpm = 60, WhenAsked = MetronomeMasterAnswer.AcceptAutomatically });
        a.Peers = () => [(epB, "B")];
        b.Peers = () => [(epA, "A")];
        var bStopped = new List<string>();
        b.StoppedFollowing = bStopped.Add;

        // Leading through a server: the claim goes at once, and the follow is asked as ever.
        var t0 = real;
        a.BecomeMaster("A");
        Check(Count(a, ServerMetronomeWire.Claim, t0) == 1, "CLAIM: becoming the master on a server must claim the server's metronome at once");
        Run(0.1);
        Check(b.Role == MetronomeRole.Follower, "asked through the server, set to accept, B must follow");
        Check(toServer[b].Count == 0, "a follower sends the server's metronome nothing of its own");
        Check(a.Describe().Contains("waiting for the server", StringComparison.Ordinal), $"until the server says A leads it, A's status must say it waits ({a.Describe()})");

        var started = real;
        a.Start();
        Check(SentSet(a, started, ServerMetronomeWire.BuildSet(true, 120_000, 4, 4)), "START: the master's start must go to the server as its setting - playing, 120 bpm, 4/4");
        Check(a.CurrentSchedule is null, "ONE BEAT FOR ALL: before the server's grid comes, nothing may click - never a beat of the master's own that nobody else shares");

        // The server's grids: each its own, in its own clock - B's sound takes 25 ms longer to reach the server, so its beat comes that much earlier.
        var timelineA = new MetronomeTimeline(2, true, 120_000, 4, 4, 0, ClockA() + 400_000);
        var timelineB = new MetronomeTimeline(2, true, 120_000, 4, 4, 0, ClockB() + 400_000 - 25_000);
        var gridB = new ServerGrid(true, true, false, idA, timelineB);
        grids[a] = new ServerGrid(true, true, true, idA, timelineA);
        grids[b] = gridB;
        Run(1.1);
        Check(a.CurrentSchedule is { Audible: true } sa && sa.Current == timelineA && sa.Map == MetronomeClockMap.Identity,
            $"THE GRID: the master must play the server's grid for it, in its own clock - not its own timeline ({a.CurrentSchedule?.Current.Describe() ?? "nothing"})");
        Check(b.CurrentSchedule is { Audible: true } sb && sb.Current == timelineB && sb.Map == MetronomeClockMap.Identity,
            $"and the follower its OWN grid, in its own clock, with no clock readings to take ({b.CurrentSchedule?.Current.Describe() ?? "nothing"})");
        Check(!a.Describe().Contains("waiting", StringComparison.Ordinal) && a.Describe().Contains("through the server", StringComparison.Ordinal),
            $"granted, the master's status says it leads through the server and no longer waits ({a.Describe()})");

        b.OnServerGrid(gridB with { Measured = false });
        Check(b.CurrentSchedule is null && b.Describe().Contains("timing this computer", StringComparison.Ordinal),
            $"NOT MEASURED: until the server has timed this computer's sound, nothing may click, and the status says so ({b.Describe()})");
        b.OnServerGrid(gridB);
        Check(b.CurrentSchedule is { Audible: true }, "measured again, it clicks again");

        var repeatFrom = real;
        Run(3);
        var sets = Count(a, ServerMetronomeWire.Set, repeatFrom);
        var claims = Count(a, ServerMetronomeWire.Claim, repeatFrom);
        Check(sets is >= 2 and <= 4 && claims == 0,
            $"REPEAT: granted, the master repeats its setting about once a second in case one is lost, and claims no more ({sets} settings, {claims} claims in 3 s)");
        var changed = real;
        a.Configure(new MetronomeSettings { Bpm = 90 }, sound);
        Check(SentSet(a, changed, ServerMetronomeWire.BuildSet(true, 90_000, 4, 4)), "TEMPO: a change must go to the server at once");

        // The server goes quiet for B.
        grids[b] = null;
        Run(3.5);
        Check(b.Role == MetronomeRole.Follower, "under five seconds without the server's grid, the follower still follows");
        Run(2);
        Check(b.Role == MetronomeRole.Solo && bStopped.Any(s => s.Contains("nothing heard from the server's metronome for 5 seconds", StringComparison.Ordinal)),
            $"TIMEOUT: five seconds without the server's grid, the follower must stop following and say so ({string.Join(" | ", bStopped)})");

        var released = real;
        a.ReleaseMaster();
        Check(a.Role == MetronomeRole.Solo && Count(a, ServerMetronomeWire.Release, released) == 1, "RELEASE: unticking must let the server's metronome go");
        grids[a] = null;

        // Somebody else leads it and does not hand over: C keeps asking, then gives way and says who leads it.
        var noticesC = new List<string>();
        var c = Engine(ClockA, null, [], new MetronomeSettings { Bpm = 100 });
        c.MasterNotice = noticesC.Add;
        grids[c] = new ServerGrid(false, true, false, idAndre, MetronomeTimeline.Stopped());
        var claimed = real;
        c.BecomeMaster("C");
        Run(5.5);
        var claimsC = Count(c, ServerMetronomeWire.Claim, claimed);
        Check(c.Role == MetronomeRole.Master && c.Describe().Contains("waiting for the server", StringComparison.Ordinal) && claimsC is >= 5 and <= 7,
            $"REFUSED: while Andre leads it, C must keep claiming about once a second, saying it waits ({claimsC} claims in 5.5 s; {c.Describe()})");
        Run(1.5);
        Check(c.Role == MetronomeRole.Solo && noticesC.Contains("Andre leads the server's metronome"),
            $"GIVE WAY: refused for six seconds, it must let go and say who leads it ({string.Join(" | ", noticesC)})");
        Check(toServer[c].Count > 0 && toServer[c][^1].Body.AsSpan().SequenceEqual(ServerMetronomeWire.BuildRelease()), "and tell the server so");

        // A server that never answers (older than the metronome): the master stays, saying it waits.
        var d = Engine(ClockA, null, [], new MetronomeSettings());
        d.BecomeMaster("D");
        Run(8);
        Check(d.Role == MetronomeRole.Master && d.Describe().Contains("waiting for the server", StringComparison.Ordinal),
            $"AN OLDER SERVER: nothing answers, so the master stays and says it waits - nobody leads it to give way to ({d.Describe()})");
        return "leading claims the server's metronome and sends it the setting at once and every second; nothing clicks before the "
             + "server's grid, then each machine plays its own grid in its own clock, and nothing while its sound is not yet timed; "
             + "a follower the server stops hearing stops after 5 s and says so; unticking lets it go; a master refused for 6 s "
             + "gives way and names who leads; with a server that never answers, the master waits";
    }
}
