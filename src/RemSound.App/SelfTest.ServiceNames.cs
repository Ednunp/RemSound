using System.Collections.Concurrent;
using System.Net;
using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App;

/// <summary>
/// The lock-screen service looks up again a name that did not answer, instead of giving up until something else happens.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>
    /// THE SERVICE LOOKS A NAME UP AGAIN UNTIL IT ANSWERS.
    ///
    /// <para>Review 2026-09-25, Ed agreed the fix. The service looked up its peers' and server's names once, when it
    /// started sending. At boot, or straight after waking, that can be before the network is up: the look-ups failed, it
    /// sent to nobody, and it stayed that way until a device changed or the app came and went. Now a name that failed is
    /// looked up again every so often, and at once when Windows says the network changed; the service starts again with
    /// it once it answers, and a name that never answers does not interrupt the people it already sends to.</para>
    ///
    /// <para>Run with the real service loop, capture and two real receivers on ports of their own. Only the name look-up
    /// is a stand-in (ServiceSendHost.ResolveForTest), because a test cannot make a real name start answering.</para>
    /// </summary>
    private static string? ServiceLooksNamesUpAgainUntilTheyAnswer()
    {
        string? deviceId;
        try { deviceId = AudioDeviceCatalog.LoadOutputs().FirstOrDefault(o => o.DeviceId is not null)?.DeviceId; }
        catch (Exception ex) { return Skip("could not enumerate outputs: " + ex.Message); }
        if (deviceId is null) return Skip("no usable output device to capture from");

        const string servicePassword = "selftest-service-names";
        var (key, fingerprint) = RemSoundCrypto.ForPlainPassword(servicePassword);
        AudioReceiver Listener(int port)
        {
            var r = new AudioReceiver();
            r.Start(port);
            r.SetOutputDevices(Array.Empty<string>());   // decode only, never a sound
            r.SetPlaybackEnabled(true);
            r.AudioKey = key;
            r.AudioFingerprint = fingerprint;
            return r;
        }
        var portA = FreeUdpPort();
        var portB = FreeUdpPort();
        using var first = Listener(portA);
        using var second = Listener(portB);

        const string FirstName = "first.remsound-selftest.invalid", SecondName = "second.remsound-selftest.invalid";
        var answering = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var profile = new Profile
        {
            Title = "selftest-service-names",
            WasapiSendMode = "devices",
            Codec = AudioTransportCodec.Pcm,
            Password = RemSoundCrypto.Obfuscate(servicePassword),
        };
        profile.SelectedWasapiSendOutputs.Add(deviceId);
        profile.SelectedConnectedPeers.Add($"{FirstName}:{portA}");
        profile.SelectedConnectedPeers.Add($"{SecondName}:{portB}");

        var restoreInterval = ServiceSendHost.NameRetryInterval;
        var restorePresencePort = ServiceSendHost.PresencePortForTest;
        ServiceSendHost.PresencePortForTest = FreeUdpPort();
        ServiceSendHost.NameRetryInterval = TimeSpan.FromMilliseconds(100);   // the loop looks every 100 ms; counted, not timed
        // Every look-up counted, so the steps below wait for the service's own attempts instead of a fixed time (2026-09-28).
        var lookups = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int Lookups(string name) { lock (answering) return lookups.GetValueOrDefault(name); }
        ServiceSendHost.ResolveForTest = entry =>
        {
            var (host, _) = PeerAddress.Split(entry);
            lock (answering)
            {
                lookups[host] = lookups.GetValueOrDefault(host) + 1;
                return answering.Contains(host) ? IPAddress.Loopback : null;
            }
        };
        var lines = new ConcurrentQueue<string>();
        using var cts = new CancellationTokenSource();
        Thread? loop = null;
        var host = new ServiceSendHost(() => profile, lines.Enqueue);
        try
        {
            var token = @"Global\RemSound.Interactive.selftest." + Guid.NewGuid().ToString("N");   // nobody holds it: the app is away
            loop = new Thread(() => host.RunLoopWithToken(cts.Token, token, pollMs: 100, resumeSettleMs: 0)) { IsBackground = true };
            loop.Start();

            // 1. Boot, before the network: neither name answers. Nothing to send to, and the log says why.
            Check(WaitFor(() => Lookups(FirstName) >= 2, TimeSpan.FromSeconds(5)), "a name that does not answer must be looked up again");
            Check(!host.IsSending, "with no name answering there is nobody to send to");
            Check(lines.Any(l => l.Contains("could not look up", StringComparison.Ordinal) && l.Contains(FirstName, StringComparison.Ordinal)),
                $"the service log must name what it could not look up (it said: {Tail(lines)})");

            // 2. The network comes up and the first name answers: the service starts, on its own, and the first peer hears it.
            lock (answering) answering.Add(FirstName);
            Check(WaitFor(() => host.IsSending, TimeSpan.FromSeconds(5)),
                $"THE SILENCE: once a name answers, the service must start sending by itself, not wait for a device change or the app (it said: {Tail(lines)})");
            Check(WaitFor(() => first.AudioBytesDecodedForTest > 0, TimeSpan.FromSeconds(5)), "and the peer whose name answered must hear audio");
            Check(second.PacketsReceived == 0, "and nothing may go to a name that still does not answer");
            Check(lines.Any(l => l.Contains("answers now", StringComparison.Ordinal)), "the log must say a name answers now");
            // Sending, the service holds Priority mode, and with it the fast timer's flag, which keeps its 10 ms tick fine with no
            // window to be seen (Ed's review of the latency lab, 2026-09-28: the app does that for itself while streaming).
            Check(lines.Any(l => l.Contains("service: priority mode: ON", StringComparison.Ordinal)),
                $"the service must switch Priority mode on while it sends (it said: {Tail(lines)})");
            Check(host.UnresolvedNamesForTest.Count == 1 && host.UnresolvedNamesForTest[0].StartsWith(SecondName, StringComparison.Ordinal),
                $"the one still not answering must still be tried ({string.Join(", ", host.UnresolvedNamesForTest)})");

            // 3. A name that keeps failing is looked up again and again, but never interrupts the stream that is running.
            var suspendsBefore = lines.Count(l => l.Contains("service: suspended", StringComparison.Ordinal));
            var triedBefore = Lookups(SecondName);
            Check(WaitFor(() => Lookups(SecondName) >= triedBefore + 5, TimeSpan.FromSeconds(10)),
                $"the name that does not answer must go on being looked up ({Lookups(SecondName) - triedBefore} more tries)");
            Check(host.IsSending && lines.Count(l => l.Contains("service: suspended", StringComparison.Ordinal)) == suspendsBefore,
                "a name that never answers must not stop and restart the people already being sent to");

            // 4. Windows says the network changed: look up at once, without waiting for the interval.
            ServiceSendHost.NameRetryInterval = TimeSpan.FromHours(1);
            // Past the retry already due when the interval moved, then a window of the loop's own polls with no look-up.
            var settled = Lookups(SecondName);
            WaitFor(() => Lookups(SecondName) > settled, TimeSpan.FromMilliseconds(400));
            lock (answering) answering.Add(SecondName);
            var quiet = Lookups(SecondName);
            Thread.Sleep(300);
            Check(Lookups(SecondName) == quiet && second.PacketsReceived == 0, "premise: with the interval an hour away, nothing is looked up by itself");
            // Raised the way Windows raises it: through the very handler the running loop gave NetworkChange.
            var onAddressChanged = Require(host.AddressChangedHandlerForTest, "the running loop must have given Windows a network-address handler");
            onAddressChanged(null, EventArgs.Empty);
            Check(WaitFor(() => second.AudioBytesDecodedForTest > 0, TimeSpan.FromSeconds(5)),
                $"when Windows says the network changed, the service must look up at once and reach the second peer (it said: {Tail(lines)})");
            Check(WaitFor(() => first.AudioBytesDecodedForTest > 0 && host.IsSending, TimeSpan.FromSeconds(3)), "and still reach the first");
            Check(host.UnresolvedNamesForTest.Count == 0, "nothing is left to look up");
            Check(lines.Any(l => l.Contains("every name looks up now", StringComparison.Ordinal)), "and the log says so");

            // What makes it "at once" in real life: the loop listens to Windows' own network-change events. A test cannot
            // change the network, so the wiring is read from the source.
            var root = FindSourceRoot();
            if (root is null) return Skip("the retries work, but the source tree is not reachable to check the network-change wiring (set REMSOUND_SOURCE_ROOT)");
            var text = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "ServiceSendHost.cs"));
            Check(text.Contains("onAddressChanged = (_, _) => OnNetworkChanged();", StringComparison.Ordinal)
                  && text.Contains("NetworkChange.NetworkAddressChanged += onAddressChanged;", StringComparison.Ordinal),
                "the service loop must listen for Windows' network-address changes");
            Check(text.Contains("if (e.IsAvailable) OnNetworkChanged();", StringComparison.Ordinal)
                  && text.Contains("NetworkChange.NetworkAvailabilityChanged += onAvailabilityChanged;", StringComparison.Ordinal),
                "and for the network becoming available");
            return "with no name answering it waits and says why; it starts by itself once one answers, keeps trying the other "
                 + "without interrupting the stream, and looks up at once when the network changes";
        }
        finally
        {
            cts.Cancel();
            loop?.Join(3000);
            host.Dispose();
            ServiceSendHost.ResolveForTest = null;
            ServiceSendHost.NameRetryInterval = restoreInterval;
            ServiceSendHost.PresencePortForTest = restorePresencePort;
        }

        static string Tail(IEnumerable<string> log) => string.Join(" | ", log.TakeLast(4));
    }

    /// <summary>
    /// THE SERVICE FOLLOWS A SERVER THAT MOVES (2026-09-25 sweep; Ed: "yes all 10"). A home server's address can change
    /// under it. The app now looks a server's name up again once it has stopped answering, and follows it; the service
    /// must too (Ed's standing rule: the service tracks the app).
    ///
    /// <para>Real service loop and capture. The server is a stand-in relay on three loopback addresses, one port, speaking
    /// the relay's own format: it answers a hello with an address check, and lists the service once the check comes back.
    /// So following is proved by what reaches the server: a hello at the new address, the check answered from there and the
    /// service's audio arriving there group-framed - and a goodbye at the address it left. Only the name look-up is a
    /// stand-in. The wait for silence runs on the service's own clock seam, moved by hand, and each look is waited for by
    /// the loop's turns, not by time. Until 2026-09-28 the "server" was a plain receiver that never answered, so rejoining
    /// was never checked, and about two seconds of the step were fixed sleeps.</para>
    /// </summary>
    private static string? ServiceFollowsAServerThatMoves()
    {
        string? deviceId;
        try { deviceId = AudioDeviceCatalog.LoadOutputs().FirstOrDefault(o => o.DeviceId is not null)?.DeviceId; }
        catch (Exception ex) { return Skip("could not enumerate outputs: " + ex.Message); }
        if (deviceId is null) return Skip("no usable output device to capture from");

        const string servicePassword = "selftest-service-moves";
        const string ServerName = "server.remsound-selftest.invalid";
        IPAddress first = IPAddress.Parse("127.0.0.1"), second = IPAddress.Parse("127.0.0.2"), third = IPAddress.Parse("127.0.0.3");
        var servers = StandInRelay.OnOnePort(first, second, third);
        using var atFirst = servers[0];
        using var atSecond = servers[1];
        using var atThird = servers[2];
        var port = atFirst.Endpoint.Port;
        var profile = new Profile
        {
            Title = "selftest-service-moves",
            WasapiSendMode = "devices",
            Codec = AudioTransportCodec.Pcm,
            Password = RemSoundCrypto.Obfuscate(servicePassword),
            RelayServer = $"{ServerName}:{port}",
            RelayConnectOnStart = true,
        };
        profile.SelectedWasapiSendOutputs.Add(deviceId);

        var leadsTo = first;
        var lookups = 0;
        var restorePresencePort = ServiceSendHost.PresencePortForTest;
        ServiceSendHost.PresencePortForTest = FreeUdpPort();
        ServiceSendHost.ResolveForTest = entry =>
        {
            if (PeerAddress.Split(entry).Host != ServerName) return null;
            Interlocked.Increment(ref lookups);
            return Volatile.Read(ref leadsTo);
        };
        var lines = new ConcurrentQueue<string>();
        using var cts = new CancellationTokenSource();
        Thread? loop = null;
        var host = new ServiceSendHost(() => profile, lines.Enqueue);
        // The service's own clock, standing still unless moved here: the shipped wait (RelayMovedAfter) passes in a moment.
        var clock = Environment.TickCount64;
        host.NowForTest = () => Interlocked.Read(ref clock);
        void Advance(long ms) => Interlocked.Add(ref clock, ms);
        var turns = 0L;
        host.LoopTurnedForTest = () => Interlocked.Increment(ref turns);
        // Two whole turns of the loop after this point: it has looked again, with whatever the clock says now.
        void TwoTurns()
        {
            var from = Interlocked.Read(ref turns);
            Check(WaitUntil(() => Interlocked.Read(ref turns) >= from + 2, 3000), "premise: the service loop must keep turning");
        }
        int Moves() => lines.Count(l => l.Contains("it has moved", StringComparison.Ordinal));
        int Lookups() => Volatile.Read(ref lookups);
        var wait = (long)ServiceSendHost.RelayMovedAfter.TotalMilliseconds;
        var me = ServiceNetworkPresence.ServiceRelayClientId();

        // In the server's group: a hello there, its address check answered from where the hello came, and the service's
        // audio arriving there group-framed. A relay group says its first hello on the next turn of its one-second timer; a
        // turn by hand says the same hello at once.
        void JoinedAt(StandInRelay server, string what)
        {
            StandInRelay.Hello? hello = null;
            Check(WaitUntil(() =>
                {
                    host.PresenceForTest.RelayGroupForTest?.TickForTest();
                    return (hello = server.HellosSince(0).FirstOrDefault(h => h.ClientId == me)) is not null;
                }, 5000),
                $"{what}: the service must say hello to the server at {server.Endpoint} (it is on {host.RelayAtForTest}; {server.Describe()}; "
                + $"it said: {string.Join(" | ", lines.TakeLast(4))})");
            Check(WaitUntil(() => server.Proved(hello!.From), 5000), $"{what}: and answer the address check of the server at {server.Endpoint}");
            Check(WaitUntil(() => server.Count(a => a.IsGroupFramed(me, RemPacketType.Audio)) > 0, 5000),
                $"{what}: and be in its group there, its audio arriving group-framed ({server.Describe()})");
        }

        // The server goes quiet: it hears everything and answers nothing. The group gives it up after GroupAliveFor without a
        // member list; that silence is put back five seconds here rather than sat through.
        void FallsSilent(StandInRelay server)
        {
            server.Answering = false;
            Check(WaitUntil(() =>
                {
                    if (host.PresenceForTest.RelayGroupForTest is not { } group) return false;
                    group.BackdateRosterForTest(RelayGroupClient.GroupAliveFor + TimeSpan.FromSeconds(1));
                    group.TickForTest();
                    return !host.PresenceForTest.RelayAnswering;
                }, 3000),
                $"premise: a server that has stopped sending its member list must count as not answering (at {server.Endpoint})");
            TwoTurns();   // and the loop has seen it, and started timing the silence
        }

        try
        {
            // Turning every 20 ms: nothing it does waits on the turn but this test, which counts them.
            var token = @"Global\RemSound.Interactive.selftest." + Guid.NewGuid().ToString("N");
            loop = new Thread(() => host.RunLoopWithToken(cts.Token, token, pollMs: 20, resumeSettleMs: 0)) { IsBackground = true };
            loop.Start();
            JoinedAt(atFirst, "premise");
            Check(host.RelayAtForTest?.Address.Equals(first) == true, $"premise: the service must be on its server by name at {first} (on {host.RelayAtForTest})");

            // A server that answers is never looked up again, however long.
            var looked = Lookups();
            Advance(10 * wait);
            TwoTurns();
            Check(Lookups() == looked && Moves() == 0,
                $"a server that answers must not be looked up again, however long it has been ({Lookups() - looked} look-ups, {Moves()} moves)");

            // Windows says the network changed: looked up at once, and followed.
            Volatile.Write(ref leadsTo, second);
            host.NetworkChangedForTest();
            JoinedAt(atSecond, "THE NETWORK CHANGE");
            Check(host.RelayAtForTest?.Address.Equals(second) == true,
                $"THE NETWORK CHANGE: the service must look its server up again when the network changes, and follow it (on {host.RelayAtForTest})");
            Check(WaitUntil(() => atFirst.Count(a => a.IsBye(me)) > 0, 3000),
                "and the server at the old address must be told it has gone, so its group sees the service leave at once");

            // It moves again, and this time only the silence says so. It answers for a while first, so the wait below is timed
            // from the silence and not from the last look-up. Quiet for less than the wait: left alone. Quiet as long as the
            // wait: looked up, and followed.
            Advance(wait);
            TwoTurns();
            Volatile.Write(ref leadsTo, third);
            FallsSilent(atSecond);
            looked = Lookups();
            Advance(wait - 1);
            TwoTurns();
            Check(Lookups() == looked && host.RelayAtForTest?.Address.Equals(second) == true,
                $"THE WAIT: a server quiet for less than {wait} ms must not be looked up again yet ({Lookups() - looked} look-ups; on {host.RelayAtForTest})");
            Advance(1);
            JoinedAt(atThird, "THE MOVE");
            Check(host.RelayAtForTest?.Address.Equals(third) == true,
                $"THE MOVE: a server by name that has stopped answering must be looked up again and followed to its new address (on {host.RelayAtForTest})");

            // Still quiet, and still at the same address: looked up again after each wait, and never started again for it.
            FallsSilent(atThird);
            var suspends = lines.Count(l => l.Contains("service: suspended", StringComparison.Ordinal));
            for (var i = 1; i <= 3; i++)
            {
                var before = Lookups();
                Advance(wait);
                Check(WaitUntil(() => Lookups() > before, 3000), $"a server still quiet must be looked up again after each wait (wait {i})");
                TwoTurns();
                Check(Moves() == 2 && lines.Count(l => l.Contains("service: suspended", StringComparison.Ordinal)) == suspends && atThird.Count(a => a.IsBye(me)) == 0,
                    $"THE CHURN: a server still quiet at the same address must not be started again and again ({Moves()} moves after wait {i})");
            }
            Check(lines.Any(l => l.Contains($"now looks up to 127.0.0.3:{port}", StringComparison.Ordinal)), "THE LOG: each move must be logged");
            return "the service followed its server by name - at once when the network changed, and once it had been quiet for the "
                 + $"whole {wait / 1000} s wait on its own clock - and joined its group at each new address (hello, address check, "
                 + "group-framed audio) and left the old one; it left a server that answers alone however long, one quiet for less than "
                 + "the wait alone, and one still quiet at the same address was looked up again but never restarted";
        }
        finally
        {
            cts.Cancel();
            loop?.Join(3000);
            host.Dispose();
            ServiceSendHost.ResolveForTest = null;
            ServiceSendHost.PresencePortForTest = restorePresencePort;
        }
    }
}
