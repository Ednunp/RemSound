using RemSound.Core;
using RemSound.Sender;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>SERVICE BOOT EAR (Ed, 2026-09-28: the Windows startup sound "always came in late"). Until somebody signs in,
    /// in the first minutes after Windows starts, the service listens through Windows' per-program capture of every program
    /// but RemSound instead of the output's own loopback, which the 14 Sep cold boot showed deaf to the tune. The real host,
    /// driven tick by tick with the meter, the capture, Windows' start and the console stood in for: it listens that way,
    /// says what it first heard and through which, goes back to the chosen output when somebody signs in or the minutes are
    /// over, hands over at once if the boot ear is deaf too, and never listens that way in applications mode, after the boot
    /// minutes, with somebody signed in, or where Windows has no per-program capture.</summary>
    private static string? ServiceBootEar()
    {
        // The rule on its own.
        Check(ServiceSendHost.WantBootEar(applicationsMode: false, processLoopbackSupported: true, uptimeMs: 20_000, someoneSignedIn: false, deafThisBoot: false),
            "twenty seconds after Windows started, nobody signed in: the boot ear");
        Check(!ServiceSendHost.WantBootEar(false, true, 20_000, someoneSignedIn: true, false), "somebody signed in: the output's own capture");
        Check(!ServiceSendHost.WantBootEar(false, true, ServiceSendHost.BootPhaseMs, false, false), "five minutes after Windows started: the output's own capture");
        Check(!ServiceSendHost.WantBootEar(applicationsMode: true, true, 20_000, false, false), "applications mode captures per program already: never the boot ear");
        Check(!ServiceSendHost.WantBootEar(false, processLoopbackSupported: false, 20_000, false, false), "no per-program capture on this Windows (7, or 10 before 19041): never the boot ear");
        Check(!ServiceSendHost.WantBootEar(false, true, 20_000, false, deafThisBoot: true), "deaf once this boot: never again this boot");

        // The id: every program but one, told apart from one program.
        var excluding = ProcessLoopbackId.FormatExcluding(4242);
        Check(ProcessLoopbackId.TryParse(excluding, out var pid, out var isExcluding) && pid == 4242 && isExcluding, $"\"{excluding}\" must read back as every program but 4242");
        Check(!ProcessLoopbackId.TryParse(excluding, out _), "an excluding id is not one program - the one-program reading must refuse it");
        Check(ProcessLoopbackId.TryParse(ProcessLoopbackId.Format(4242), out pid, out isExcluding) && pid == 4242 && !isExcluding, "a one-program id must still read as one program");
        // And what the capture engine opens for each: never started here.
        using (var every = MixingEngine.ProcessCaptureFor(excluding))
            Check(!every.IncludesProcessTree, "the boot ear's id must open a capture of every program BUT that one, not of it");
        using (var one = MixingEngine.ProcessCaptureFor(ProcessLoopbackId.Format(4242)))
            Check(one.IncludesProcessTree, "a one-program id must still open that program and its children");

        // The real host.
        var profile = new Profile { Title = "boot ear", WasapiSendMode = "devices" };
        profile.SelectedWasapiSendOutputs.Add("fake-device-id");
        profile.SelectedConnectedPeers.Add("127.0.0.1:47999");
        profile.Password = RemSoundCrypto.Obfuscate("hunter2horse42stable");
        var logged = new List<string>();
        var restorePresencePort = ServiceSendHost.PresencePortForTest;
        var restoreSupported = ProcessLoopbackCapture.ForceSupportedForTest;
        var restoreRefuse = MixingEngine.RefuseOpenForTest;
        ServiceSendHost.PresencePortForTest = FreeUdpPort();
        ProcessLoopbackCapture.ForceSupportedForTest = true;
        // Nothing is really opened: the specs the host hands the sender are what is judged.
        MixingEngine.RefuseOpenForTest = _ => true;
        try
        {
            var opened = 1; // the boot ear opens, unless a case says Windows refused it
            ServiceSendHost Host(Func<long> uptime, Func<bool> signedIn, Func<float> meter, Func<float> heard) =>
                new(() => profile, line => { lock (logged) logged.Add(line); })
                {
                    UptimeForTest = uptime,
                    SignedInForTest = signedIn,
                    MeterForTest = meter,
                    CapturePeakForTest = heard,
                    OpenSourcesForTest = () => opened,
                };
            bool Logged(string text) { lock (logged) return logged.Any(l => l.Contains(text, StringComparison.Ordinal)); }
            string Ear(ServiceSendHost h) => string.Join(", ", h.AppliedSpecsForTest.Select(s => s.DeviceId));
            var bootEarId = ProcessLoopbackId.FormatExcluding(Environment.ProcessId);

            // 1. A boot: twenty seconds in, nobody signed in. The boot ear, the tune heard through it, then somebody signs in.
            var clock = 10_000_000L;
            var uptime = 20_000L;
            var signedIn = false;
            var meter = 0f;
            var heard = 0f;
            var callbacks = 0L;
            using (var host = Host(() => uptime, () => signedIn, () => meter, () => heard))
            {
                host.NowForTest = () => clock;
                host.CaptureCallbacksForTest = () => ++callbacks;
                Check(host.ApplyProfile(profile), "the service must come up");
                Check(host.BootEarForTest && Ear(host) == bootEarId, $"nobody signed in, 20 s after Windows started: it must listen through the boot ear ({bootEarId}) - it opened \"{Ear(host)}\"");
                Check(Logged("somebody signed in: no") && Logged("per-program capture (the boot ear): available"), "the log must say how long ago Windows started, whether anyone is signed in, and whether the boot ear exists");
                Check(Logged("listening through the boot ear"), "the log must say it is listening through the boot ear");
                void Ticks(int n) { for (var i = 0; i < n; i++) { host.WatchCaptureHealthForTest(); clock += 500; uptime += 500; } }

                Ticks(4); // quiet: nothing re-opens, nothing is heard
                Check(host.BootEarForTest, "a quiet machine keeps the boot ear");
                meter = 0.45f; heard = 0.40f; // the tune, heard through it
                Ticks(2);
                Check(Logged("first sound heard") && Logged("through the boot ear"), "the first sound must be logged, and through which ear");
                lock (logged) Check(!logged.Any(l => l.Contains("re-opening capture", StringComparison.Ordinal)), "a boot ear that hears the tune must not be re-opened");
                meter = heard = 0f;
                Ticks(122); // past the pulse, once a minute since 2026-10-19 (it was every 15 s)
                Check(host.BootEarForTest, "still nobody signed in: still the boot ear");
                Check(Logged("maxSendGapMs=") && Logged(" ear=boot"), "the minute's pulse must carry the longest wait between packets leaving, and say it is the boot ear");

                signedIn = true; // somebody signs in
                Ticks(1);
                Check(!host.BootEarForTest && Ear(host) == "fake-device-id", $"somebody signed in: back to the chosen output - it has \"{Ear(host)}\"");
                Check(Logged("somebody signed in — back to capturing the chosen output"), "and the log must say so");
            }

            // 2. A boot where the boot ear is deaf too: the output's meter reads the tune, the boot ear nothing. It hands over at
            // once and never comes back this boot.
            lock (logged) logged.Clear();
            uptime = 20_000; signedIn = false; meter = 0f; heard = 0f;
            using (var host = Host(() => uptime, () => signedIn, () => meter, () => heard))
            {
                host.NowForTest = () => clock;
                host.CaptureCallbacksForTest = () => ++callbacks;
                Check(host.ApplyProfile(profile) && host.BootEarForTest, "a second boot starts on the boot ear");
                meter = 0.45f; // the tune plays; the boot ear hears nothing
                for (var i = 0; i < 2; i++) { host.WatchCaptureHealthForTest(); clock += 500; uptime += 500; }
                Check(!host.BootEarForTest && Ear(host) == "fake-device-id", $"a deaf boot ear must hand over to the output's own capture within a second - it has \"{Ear(host)}\"");
                Check(Logged("the boot ear is DEAF too"), "and the log must say the boot ear was deaf");
                for (var i = 0; i < 6; i++) { host.WatchCaptureHealthForTest(); clock += 500; uptime += 500; }
                Check(!host.BootEarForTest, "and the re-open ladder that follows must stay on the output's own capture this boot");
            }

            // 3. Five minutes after Windows started, still nobody signed in: back to the chosen output.
            lock (logged) logged.Clear();
            uptime = ServiceSendHost.BootPhaseMs - 1_000; signedIn = false; meter = 0f; heard = 0f;
            using (var host = Host(() => uptime, () => signedIn, () => meter, () => heard))
            {
                host.NowForTest = () => clock;
                host.CaptureCallbacksForTest = () => ++callbacks;
                Check(host.ApplyProfile(profile) && host.BootEarForTest, "a minute short of the five: the boot ear");
                for (var i = 0; i < 3; i++) { host.WatchCaptureHealthForTest(); clock += 500; uptime += 500; }
                Check(!host.BootEarForTest && Ear(host) == "fake-device-id", "five minutes after Windows started, it must go back to the chosen output");
                Check(Logged("5 minutes since Windows started"), "and the log must say why");
            }

            // 3b. Windows refuses the per-program capture: the output's own capture at once, before any sound, for the boot.
            lock (logged) logged.Clear();
            uptime = 20_000; signedIn = false; meter = 0f; heard = 0f; opened = 0;
            using (var host = Host(() => uptime, () => signedIn, () => meter, () => heard))
            {
                host.NowForTest = () => clock;
                host.CaptureCallbacksForTest = () => ++callbacks;
                Check(host.ApplyProfile(profile), "a boot whose boot ear Windows refuses still comes up");
                host.WatchCaptureHealthForTest(); clock += 500; uptime += 500;
                Check(!host.BootEarForTest && Ear(host) == "fake-device-id", $"a boot ear that could not be opened must hand over at once, on a quiet machine - it has \"{Ear(host)}\"");
                Check(Logged("the boot ear could not be opened"), "and the log must say so");
                for (var i = 0; i < 4; i++) { host.WatchCaptureHealthForTest(); clock += 500; uptime += 500; }
                Check(!host.BootEarForTest, "and not try it again this boot");
            }
            opened = 1;

            // 4. Never: somebody already signed in, long after boot, no per-program capture. (Applications mode: the rule above -
            // it already captures program by program.)
            (string Why, Action Arrange, Action Undo)[] never =
            [
                ("somebody signed in", () => signedIn = true, () => signedIn = false),
                ("an hour after Windows started", () => uptime = 3_600_000, () => uptime = 20_000),
                ("no per-program capture on this Windows", () => ProcessLoopbackCapture.ForceSupportedForTest = false, () => ProcessLoopbackCapture.ForceSupportedForTest = true),
            ];
            foreach (var (why, arrange, undo) in never)
            {
                uptime = 20_000; signedIn = false;
                arrange();
                try
                {
                    using var host = Host(() => uptime, () => signedIn, () => 0f, () => 0f);
                    host.NowForTest = () => clock;
                    host.ApplyProfile(profile);
                    Check(!host.BootEarForTest && !host.AppliedSpecsForTest.Any(s => s.DeviceId.StartsWith("procx:", StringComparison.Ordinal)),
                        $"{why}: never the boot ear - it opened \"{Ear(host)}\"");
                }
                finally { undo(); }
            }

            // 5. Signing in counts (Ed's 30 Sep restart: let in at 10.1 s, his name at the console only at 10.7 s). The rule on
            // Windows' logon sessions: a person's own account at the console, never the lock screen's own sessions.
            const string person = "S-1-5-21-1111111111-2222222222-3333333333-1001";
            Check(ConsoleSession.IsPersonLetIn(logonType: 2, session: 1, consoleSession: 1, person), "a person let in at the console: signing in");
            Check(ConsoleSession.IsPersonLetIn(11, 1, 1, person), "let in with remembered sign-in details: signing in");
            Check(ConsoleSession.IsPersonLetIn(2, 1, 1, "S-1-12-1-111-222-333-444"), "a work account: signing in");
            Check(!ConsoleSession.IsPersonLetIn(2, 1, 1, "S-1-5-90-0-1"), "the lock screen's own desktop session is nobody");
            Check(!ConsoleSession.IsPersonLetIn(2, 1, 1, "S-1-5-96-0-1"), "the lock screen's own font session is nobody");
            Check(!ConsoleSession.IsPersonLetIn(2, session: 2, consoleSession: 1, person), "somebody on another session (remote desktop) is not at the console");
            Check(!ConsoleSession.IsPersonLetIn(logonType: 5, 1, 1, person), "a service logon is nobody");
            Check(!ConsoleSession.IsPersonLetIn(2, 1, 1, sid: null), "no account at all is nobody");
            // And Windows itself: whoever runs this at the console is there, in the logon sessions too.
            if (System.Diagnostics.Process.GetCurrentProcess().SessionId == ConsoleSession.ConsoleSessionIdForTest)
            {
                Check(ConsoleSession.SomeoneLetInAt(ConsoleSession.ConsoleSessionIdForTest), "the person running this at the console must be found among Windows' logon sessions");
                Check(ConsoleSession.SomeoneSignedIn() && ConsoleSession.Describe() == "yes", $"and must count as signed in (it said \"{ConsoleSession.Describe()}\")");
                // Their name not at the console yet, as while their profile loads: still somebody, signing in.
                ConsoleSession.HideNameForTest = true;
                try
                {
                    Check(ConsoleSession.SomeoneSignedIn() && ConsoleSession.Describe().StartsWith("signing in", StringComparison.Ordinal),
                        $"let in but not yet named at the console must count, as signing in (it said \"{ConsoleSession.Describe()}\")");
                }
                finally { ConsoleSession.HideNameForTest = false; }
            }

            // 6. Listening comes first: the loop's first start opens the capture, and only then does it watch the network.
            lock (logged) logged.Clear();
            bool? watchingAtStart = null;
            ServiceSendHost? self = null;
            using (var host = new ServiceSendHost(() => profile, line =>
                   {
                       if (line.Contains("service: streaming \"", StringComparison.Ordinal)) watchingAtStart ??= self?.AddressChangedHandlerForTest is not null;
                       lock (logged) logged.Add(line);
                   })
                   {
                       UptimeForTest = () => 3_600_000, SignedInForTest = () => true, MeterForTest = () => 0f, CapturePeakForTest = () => 0f,
                       // Every send refused, so the running loop's own look at its sending has something to say.
                       SendCountsForTest = () => (0, 5, System.Net.Sockets.SocketError.NetworkUnreachable), NetworkForTest = () => "test network",
                   })
            {
                self = host;
                using var cts = new CancellationTokenSource();
                using var turned = new ManualResetEventSlim();
                host.LoopTurnedForTest = () => turned.Set();
                var loop = new Thread(() => host.RunLoopWithToken(cts.Token, $"RemSound-BootEarTest-{Guid.NewGuid():N}", pollMs: 50, resumeSettleMs: 0)) { IsBackground = true };
                loop.Start();
                try
                {
                    Check(turned.Wait(TimeSpan.FromSeconds(10)), "the service loop must turn");
                    Check(host.AddressChangedHandlerForTest is not null, "after its first turn the loop must be watching the network");
                }
                finally { cts.Cancel(); loop.Join(3000); }
            }
            Check(watchingAtStart == false, $"the capture must open before the network watch starts (watching when it started: {watchingAtStart?.ToString() ?? "it never started"})");
            // And the service itself: the host, then the loop, and only then the rest of its setup.
            var root = FindSourceRoot();
            if (root is not null)
            {
                var start = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "RemSoundService.cs"));
                int At(string text) => start.IndexOf(text, StringComparison.Ordinal);
                Check(At("host = ServiceSendHost.FromConfig(") > 0 && At("host = ServiceSendHost.FromConfig(") < At("worker.Start();")
                      && At("worker.Start();") < At("OpusInUse.Describe()") && At("worker.Start();") < At("TidyServiceLogs(log.Path)")
                      && At("worker.Start();") < At("ServiceStore.SaveStatus("),
                    "the service must start its loop before the rest of its setup");
            }
            Check(Logged("service: sending FAILS"), "the running service must look at its sending on every turn");

            // 7. Sending at boot (Ed's second 30 Sep restart: nothing left the computer for four seconds, and the log could not
            // say why). The sender counts what Windows refuses and keeps its reason; a send to 0.0.0.0 is refused without anything
            // leaving the computer.
            using (var probe = new AudioSender())
            {
                var nowhere = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 9);
                Check(!probe.SendRaw(new byte[12], 12, nowhere), "premise: Windows refuses a send to 0.0.0.0");
                probe.SetReceivers([nowhere]);
                probe.SendToAll(new byte[12]);
                var (refused, reason) = probe.TakeSendFailures();
                Check(refused == 2 && reason != System.Net.Sockets.SocketError.Success,
                    $"both refused sends (a heartbeat's and a sound packet's) must be counted, with Windows' reason - {refused}, {reason}");
                Check(probe.TakeSendFailures().Count == 0, "and the count starts again once read");
            }
            // The service's lines, driven look by look: nothing while all is well, a failure and its reason once, a new reason,
            // nothing claimed while nothing goes out, and packets out again.
            lock (logged) logged.Clear();
            uptime = 10_000;
            var counts = (SentSoFar: 0L, Failed: 0L, Why: System.Net.Sockets.SocketError.Success);
            var network = "\"Ethernet\" up, no IPv4 address yet";
            using (var host = Host(() => uptime, () => true, () => 0f, () => 0f))
            {
                host.SendCountsForTest = () => counts;
                host.NetworkForTest = () => network;
                void Look(long sentSoFar, long failed, System.Net.Sockets.SocketError why)
                {
                    counts = (sentSoFar, failed, why);
                    host.WatchSendingForTest();
                    uptime += 500;
                }
                int Lines(string text) { lock (logged) return logged.Count(l => l.Contains(text, StringComparison.Ordinal)); }
                Look(0, 0, default);
                lock (logged) Check(logged.Count == 0, $"nothing refused: nothing logged ({logged.FirstOrDefault()})");
                Look(0, 40, System.Net.Sockets.SocketError.NetworkUnreachable);
                Check(Logged($"service: sending FAILS {10.5:0.0} s after Windows started — Windows says NetworkUnreachable (10051); this computer's network: \"Ethernet\" up"),
                    "the first refused sends must be logged with Windows' reason and the network");
                Look(0, 40, System.Net.Sockets.SocketError.NetworkUnreachable);
                Check(Lines("sending FAILS") == 1, "the same reason again is not logged again");
                Look(0, 40, System.Net.Sockets.SocketError.AddressNotAvailable);
                Check(Lines("sending FAILS") == 2 && Logged("Windows says AddressNotAvailable (10049)"), "a new reason must be logged");
                network = "\"Ethernet\" up, 192.168.1.95 (preferred)";
                Look(0, 0, default);
                Check(!Logged("sending works again"), "nothing refused but nothing sent either is not sending working");
                Look(200, 0, default);
                Check(Logged($"service: sending works again {12.5:0.0} s after Windows started, after 120 refused over {2.0:0.0} s; this computer's network: \"Ethernet\" up, 192.168.1.95 (preferred)"),
                    "packets going out again must be logged, with how many were refused and for how long");
                Look(400, 0, default);
                lock (logged) Check(logged.Count == 3, $"and nothing more while it works ({logged.Count} lines)");
            }
            var described = ServiceSendHost.DescribeNetwork();
            Check(described.Length > 0 && !described.StartsWith("unreadable", StringComparison.Ordinal), $"this computer's real network must be readable: \"{described}\"");
        }
        finally
        {
            ServiceSendHost.PresencePortForTest = restorePresencePort;
            ProcessLoopbackCapture.ForceSupportedForTest = restoreSupported;
            MixingEngine.RefuseOpenForTest = restoreRefuse;
        }
        return "before anyone signs in, in the first five minutes, the service listens to every program but itself, logs the first sound and "
             + "through which ear, goes back to the chosen output when somebody signs in or the minutes are over, and hands over at once "
             + "if the boot ear is deaf too; never in applications mode, after the boot minutes, signed in (or signing in), or without the "
             + "Windows API; it opens the capture before the rest of its setup, and logs refused sends with Windows' reason and when sending "
             + "works again";
    }
}
