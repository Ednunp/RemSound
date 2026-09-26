using Mono.Nat;

namespace RemSound.App;

/// <summary>
/// UNTICKING UPNP HOLDS, EVEN MID-RENEWAL (2026-09-25 sweep; Ed: "yes all 10"). A renewal calls the router outside the
/// lock, and a router can take tens of seconds to answer; unticking UPnP meanwhile removed the mapping, then the renewal put
/// it back (or, failing, called Start, which set "wanted" on again) - the router open for the rest of the session while
/// Preferences said off.
/// </summary>
internal static partial class SelfTest
{
    private sealed class SlowRouter : INatDevice
    {
        public readonly ManualResetEventSlim Release = new(false);
        public volatile bool Refuse;
        public int Creates;
        public int Deletes;
        public System.Net.IPEndPoint DeviceEndpoint { get; } = new(System.Net.IPAddress.Parse("192.0.2.2"), 5000);
        public DateTime LastSeen { get; } = DateTime.Now;
        public NatProtocol NatProtocol => NatProtocol.Upnp;
        public Mapping CreatePortMap(Mapping mapping)
        {
            if (Interlocked.Increment(ref Creates) > 1) Release.Wait(TimeSpan.FromSeconds(10));   // the first, at discovery, answers at once
            if (Refuse) throw new InvalidOperationException("slow router refuses");
            return mapping;
        }
        public Mapping DeletePortMap(Mapping mapping) { Interlocked.Increment(ref Deletes); return mapping; }
        public Mapping[] GetAllMappings() => [];
        public System.Net.IPAddress GetExternalIP() => System.Net.IPAddress.Parse("203.0.113.8");
        public Mapping GetSpecificMapping(Protocol protocol, int publicPort) => throw new NotSupportedException();
        public Task<Mapping> CreatePortMapAsync(Mapping mapping) => Task.FromResult(CreatePortMap(mapping));
        public Task<Mapping> DeletePortMapAsync(Mapping mapping) => Task.FromResult(DeletePortMap(mapping));
        public Task<Mapping[]> GetAllMappingsAsync() => Task.FromResult(GetAllMappings());
        public Task<System.Net.IPAddress> GetExternalIPAsync() => Task.FromResult(GetExternalIP());
        public Task<Mapping> GetSpecificMappingAsync(Protocol protocol, int publicPort) => throw new NotSupportedException();
    }

    private static string? UnTickingUpnpHoldsEvenMidRenewal()
    {
        var restoreInterval = RouterPortMapper.RenewInterval;
        var restoreNoNetwork = RouterPortMapper.NoNetworkForTest;
        RouterPortMapper.NoNetworkForTest = true;
        RouterPortMapper.RenewInterval = TimeSpan.FromHours(1);   // renewals only when this step asks
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        try
        {
            foreach (var refuse in new[] { false, true })
            {
                var mapper = new RouterPortMapper(lines.Enqueue);
                var router = new SlowRouter();
                try
                {
                    mapper.Start();
                    mapper.DeviceFoundForTest(router);
                    Check(mapper.Status == RouterMappingStatus.Mapped, "premise: mapped");
                    router.Refuse = refuse;
                    var renewal = Task.Run(mapper.RenewNowForTest);               // the router is slow to answer
                    Check(WaitFor(() => router.Creates == 2, TimeSpan.FromSeconds(5)), "premise: a renewal is waiting on the router");
                    mapper.Stop();                                                // UPnP unticked meanwhile
                    var deletesAtStop = router.Deletes;
                    router.Release.Set();
                    Check(renewal.Wait(TimeSpan.FromSeconds(10)), "the renewal must finish");
                    Thread.Sleep(200);
                    var what = refuse ? "a renewal that then failed" : "a renewal that then succeeded";
                    Check(mapper.Status == RouterMappingStatus.Disabled,
                        $"THE UNDONE UNTICK: after {what}, UPnP must still be off ({mapper.Status})");
                    if (!refuse)
                        Check(router.Deletes > deletesAtStop, "THE OPEN ROUTER: the mapping the late renewal put back must be removed again");
                }
                finally
                {
                    router.Release.Set();
                    try { mapper.Dispose(); } catch { /* teardown */ }
                }
            }
            Check(lines.Any(l => l.Contains("switched off while a renewal was under way", StringComparison.Ordinal)), "THE LOG: the undone renewal must be logged");
        }
        finally
        {
            RouterPortMapper.RenewInterval = restoreInterval;
            RouterPortMapper.NoNetworkForTest = restoreNoNetwork;
        }
        return "UPnP unticked while a slow renewal waited on the router stayed off - a renewal that then succeeded had its mapping removed "
             + "again, and one that failed did not set UPnP going again";
    }
}
