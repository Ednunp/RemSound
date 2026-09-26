using System.Net;
using Mono.Nat;

namespace RemSound.App;

/// <summary>
/// THE ROUTER MAPPING IS ASKED FOR AGAIN BEFORE IT RUNS OUT.
///
/// <para>Review 2026-09-25; Ed chose every 15 minutes. "Automatically open my router" asked the router for an hour's
/// mapping and never asked again: a comment said Mono.Nat renewed it, and Mono.Nat has no renewal at all. Routers that
/// honour the lease closed the port after an hour while Preferences still said it was open. Driven here with a
/// stand-in router (no real one is touched): found and mapped, asked again on the beat, a refusal shown as a failure
/// rather than "mapped", the router looked for again, and nothing asked once the tick is off. Real routers' own
/// handling of the request is not something a test here can prove.</para>
/// </summary>
internal static partial class SelfTest
{
    private sealed class StandInRouter : INatDevice
    {
        public int Creates;
        public int Deletes;
        public volatile bool Refuse;
        public IPEndPoint DeviceEndpoint { get; } = new(IPAddress.Parse("192.0.2.1"), 5000);
        public DateTime LastSeen { get; } = DateTime.Now;
        public NatProtocol NatProtocol => NatProtocol.Upnp;

        public Mapping CreatePortMap(Mapping mapping)
        {
            if (Refuse) throw new InvalidOperationException("stand-in router refuses");
            Interlocked.Increment(ref Creates);
            return mapping;
        }
        public Mapping DeletePortMap(Mapping mapping) { Interlocked.Increment(ref Deletes); return mapping; }
        public Mapping[] GetAllMappings() => [];
        public IPAddress GetExternalIP() => IPAddress.Parse("203.0.113.7");
        public Mapping GetSpecificMapping(Protocol protocol, int publicPort) => throw new NotSupportedException();
        public Task<Mapping> CreatePortMapAsync(Mapping mapping) => Task.FromResult(CreatePortMap(mapping));
        public Task<Mapping> DeletePortMapAsync(Mapping mapping) => Task.FromResult(DeletePortMap(mapping));
        public Task<Mapping[]> GetAllMappingsAsync() => Task.FromResult(GetAllMappings());
        public Task<IPAddress> GetExternalIPAsync() => Task.FromResult(GetExternalIP());
        public Task<Mapping> GetSpecificMappingAsync(Protocol protocol, int publicPort) => throw new NotSupportedException();
    }

    private static string? RouterMappingIsAskedForAgainBeforeItRunsOut()
    {
        var restoreInterval = RouterPortMapper.RenewInterval;
        var restoreNoNetwork = RouterPortMapper.NoNetworkForTest;
        RouterPortMapper.NoNetworkForTest = true;   // never the real router
        RouterPortMapper.RenewInterval = TimeSpan.FromMilliseconds(250);
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var mapper = new RouterPortMapper(lines.Enqueue);
        try
        {
            var router = new StandInRouter();
            mapper.Start();
            mapper.DeviceFoundForTest(router);
            Check(mapper.Status == RouterMappingStatus.Mapped && router.Creates == 1,
                $"premise: the stand-in router must be found and mapped ({mapper.Status}, {router.Creates} requests)");

            // Asked for again on the beat, without being told to.
            Check(WaitFor(() => router.Creates >= 3, TimeSpan.FromSeconds(5)),
                $"THE LAPSE: the mapping must be asked for again before its hour runs out, by itself ({router.Creates} requests)");
            Check(lines.Any(l => l.Contains("UPnP mapping renewed", StringComparison.Ordinal)), "and the log must say it was renewed");
            Check(mapper.Status == RouterMappingStatus.Mapped, "and it stays mapped");

            // The router refuses. A mapper of its own with a real-length beat, renewed once by hand, so no tick of the beat
            // can do the work between: the refusal itself must stop Preferences saying the router is open, and look for the
            // router again - at once, not a whole beat (15 minutes in real use) later.
            RouterPortMapper.RenewInterval = TimeSpan.FromHours(1);
            var refused = new RouterPortMapper(lines.Enqueue);
            try
            {
                var refusing = new StandInRouter();
                refused.Start();
                refused.DeviceFoundForTest(refusing);
                Check(refused.Status == RouterMappingStatus.Mapped, "premise: mapped before it refuses");
                refusing.Refuse = true;
                refused.RenewNowForTest();
                Check(refused.Status != RouterMappingStatus.Mapped,
                    "THE FALSE COMFORT: once the router refuses, Preferences must no longer say the router is open");
                Check(lines.Any(l => l.Contains("UPnP renewal failed", StringComparison.Ordinal)), "the log must say the renewal failed");
                Check(refused.Status == RouterMappingStatus.Searching, $"and the router must be looked for again, at once ({refused.Status})");

                // It answers again: mapped again.
                refusing.Refuse = false;
                refused.DeviceFoundForTest(refusing);
                Check(refused.Status == RouterMappingStatus.Mapped, "found again, it is mapped again");
            }
            finally { try { refused.Dispose(); } catch { /* teardown */ } }

            // Ticked off: nothing is asked any more.
            mapper.Stop();
            Check(router.Deletes >= 1, "turning it off must remove the mapping");
            var afterStop = router.Creates;
            var linesAtStop = lines.Count;
            Thread.Sleep(900);
            Check(router.Creates == afterStop, $"and nothing may be asked of the router once it is off ({router.Creates - afterStop} requests)");
            Check(mapper.Status == RouterMappingStatus.Disabled && !lines.Skip(linesAtStop).Any(l => l.Contains("looking for the router again", StringComparison.Ordinal)),
                $"and it must stay off, not go looking for the router again ({mapper.Status})");
            return "the mapping is asked for again on the beat and the log says so; a refusal stops it saying the router is "
                 + "open and looks for the router again; turned off, nothing more is asked";
        }
        finally
        {
            try { mapper.Dispose(); } catch { /* teardown */ }
            RouterPortMapper.RenewInterval = restoreInterval;
            RouterPortMapper.NoNetworkForTest = restoreNoNetwork;
        }
    }
}
