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
        ServiceSendHost.NameRetryInterval = TimeSpan.FromMilliseconds(300);
        ServiceSendHost.ResolveForTest = entry =>
        {
            var (host, _) = PeerAddress.Split(entry);
            lock (answering) return answering.Contains(host) ? IPAddress.Loopback : null;
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
            Thread.Sleep(900);
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
            Check(host.UnresolvedNamesForTest.Count == 1 && host.UnresolvedNamesForTest[0].StartsWith(SecondName, StringComparison.Ordinal),
                $"the one still not answering must still be tried ({string.Join(", ", host.UnresolvedNamesForTest)})");

            // 3. A name that keeps failing is looked up again and again, but never interrupts the stream that is running.
            var suspendsBefore = lines.Count(l => l.Contains("service: suspended", StringComparison.Ordinal));
            Thread.Sleep(1500);   // five retry intervals
            Check(host.IsSending && lines.Count(l => l.Contains("service: suspended", StringComparison.Ordinal)) == suspendsBefore,
                "a name that never answers must not stop and restart the people already being sent to");

            // 4. Windows says the network changed: look up at once, without waiting for the interval.
            ServiceSendHost.NameRetryInterval = TimeSpan.FromHours(1);
            lock (answering) answering.Add(SecondName);
            Thread.Sleep(900);
            Check(second.PacketsReceived == 0, "premise: with the interval an hour away, nothing is looked up by itself");
            host.NetworkChangedForTest();
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
    /// must too (Ed's standing rule: the service tracks the app). Real service loop and capture; only the look-up is a
    /// stand-in, and the "server" is a plain receiver, so it never answers as a server does.
    /// </summary>
    private static string? ServiceFollowsAServerThatMoves()
    {
        string? deviceId;
        try { deviceId = AudioDeviceCatalog.LoadOutputs().FirstOrDefault(o => o.DeviceId is not null)?.DeviceId; }
        catch (Exception ex) { return Skip("could not enumerate outputs: " + ex.Message); }
        if (deviceId is null) return Skip("no usable output device to capture from");

        const string servicePassword = "selftest-service-moves";
        const string ServerName = "server.remsound-selftest.invalid";
        using var standIn = new AudioReceiver();
        var port = FreeUdpPort();
        standIn.Start(port);
        standIn.SetOutputDevices(Array.Empty<string>());
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

        var leadsTo = IPAddress.Parse("127.0.0.1");
        var restoreMoved = ServiceSendHost.RelayMovedAfter;
        var restorePresencePort = ServiceSendHost.PresencePortForTest;
        ServiceSendHost.PresencePortForTest = FreeUdpPort();
        ServiceSendHost.RelayMovedAfter = TimeSpan.FromHours(1);
        ServiceSendHost.ResolveForTest = entry => PeerAddress.Split(entry).Host == ServerName ? Volatile.Read(ref leadsTo) : null;
        var lines = new ConcurrentQueue<string>();
        using var cts = new CancellationTokenSource();
        Thread? loop = null;
        var host = new ServiceSendHost(() => profile, lines.Enqueue);
        int Moves() => lines.Count(l => l.Contains("it has moved", StringComparison.Ordinal));
        try
        {
            var token = @"Global\RemSound.Interactive.selftest." + Guid.NewGuid().ToString("N");
            loop = new Thread(() => host.RunLoopWithToken(cts.Token, token, pollMs: 100, resumeSettleMs: 0)) { IsBackground = true };
            loop.Start();
            Check(WaitFor(() => host.IsSending && host.RelayAtForTest?.Address.Equals(IPAddress.Parse("127.0.0.1")) == true, TimeSpan.FromSeconds(5)),
                $"premise: the service sends to its server by name (it said: {string.Join(" | ", lines.TakeLast(4))})");

            // The server moves. Quiet for less than the wait: left alone.
            Volatile.Write(ref leadsTo, IPAddress.Parse("127.0.0.2"));
            Thread.Sleep(900);
            Check(Moves() == 0, "a server quiet for less than the wait must not be looked up again yet");

            // Windows says the network changed: looked up at once, and followed.
            host.NetworkChangedForTest();
            Check(WaitFor(() => host.IsSending && host.RelayAtForTest?.Address.Equals(IPAddress.Parse("127.0.0.2")) == true, TimeSpan.FromSeconds(5)),
                $"THE NETWORK CHANGE: the service must look its server up again when the network changes, and follow it (on {host.RelayAtForTest})");

            // It moves again, and this time only the silence says so.
            ServiceSendHost.RelayMovedAfter = TimeSpan.FromMilliseconds(300);
            Volatile.Write(ref leadsTo, IPAddress.Parse("127.0.0.3"));
            Check(WaitFor(() => host.IsSending && host.RelayAtForTest?.Address.Equals(IPAddress.Parse("127.0.0.3")) == true, TimeSpan.FromSeconds(5)),
                $"THE MOVE: a server by name that has stopped answering must be looked up again and followed to its new address (on {host.RelayAtForTest})");
            Thread.Sleep(1200);   // several more waits, still silent, the same address
            Check(Moves() == 2, $"THE CHURN: a server still quiet at the same address must not be started again and again ({Moves()} moves)");
            Check(lines.Any(l => l.Contains($"now looks up to 127.0.0.3:{port}", StringComparison.Ordinal)), "THE LOG: each move must be logged");
            return "the service followed its server by name to a new address - at once when the network changed, and after the "
                 + "server had been quiet long enough - left it alone before that, and did not start again while it stayed put";
        }
        finally
        {
            cts.Cancel();
            loop?.Join(3000);
            host.Dispose();
            ServiceSendHost.ResolveForTest = null;
            ServiceSendHost.RelayMovedAfter = restoreMoved;
            ServiceSendHost.PresencePortForTest = restorePresencePort;
        }
    }
}
