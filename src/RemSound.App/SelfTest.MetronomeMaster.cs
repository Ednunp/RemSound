using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// THE MASTER METRONOME (Ed, 2026-09-28: "when that lead selects this, all the peers connected get prompted or auto accept
/// ... when the lead starts the metronome, all your metronomes start at the same time ... they all have to stay in synch").
/// The protocol's rules on simulated time, measured; then two real windows over loopback, one on a clock 1234.567 ms off and
/// 50 ppm fast, whose clicks land on the same master moment; then a window asked by stand-in masters, declining, accepting,
/// timing out, and leading a peer that never answers.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>
    /// METRONOME PROTOCOL (simulated time, measured): a master offers to everyone connected at once and every 2 s for 10 s
    /// - six offers - to a peer that never answers, then logs why (a server that does not pass the metronome on, when it is
    /// one); a follower that accepts gets the timeline three times 50 ms apart and then every second; it reads the master's
    /// clock every 250 ms for 3 s and every second after; its clicks land within half the path's asymmetry plus 0.2 ms of the
    /// master's; an older timeline and a reply nobody asked for are ignored; mute is the follower's alone; a master declines
    /// another's offer; Leave, Release and 5 s of silence each end following; a peer that never answers gets nothing but offers.
    /// </summary>
    private static string? MetronomeProtocolRules()
    {
        double real = 50_000_000;                       // the true time, in microseconds
        long ClockA() => (long)real;                    // the master's clock
        long ClockB() => (long)(real * (1 + 50e-6)) + 1_234_567;   // the follower's: 1234.567 ms apart and 50 ppm fast
        var epA = new IPEndPoint(IPAddress.Parse("10.9.0.1"), 47830);
        var epB = new IPEndPoint(IPAddress.Parse("10.9.0.2"), 47830);
        var epOld = new IPEndPoint(IPAddress.Parse("10.9.0.3"), 47830);
        const double aToB = 2500, bToA = 1500;          // one-way delays: 1 ms more one way than the other
        var logA = new List<string>();
        var logB = new List<string>();
        var a = new MetronomeEngine(ClockA, logA.Add);
        var b = new MetronomeEngine(ClockB, logB.Add);
        var flying = new List<(double At, MetronomeMessage M, IPEndPoint From, IPEndPoint To)>();
        var sent = new List<(double At, MetronomeKind Kind, IPEndPoint To)>();
        var dropAToB = false;
        a.Send = (m, to) => { sent.Add((real, m.Kind, to)); if (!(dropAToB && to.Equals(epB)) && !to.Equals(epOld)) flying.Add((real + aToB, m, epA, to)); };
        b.Send = (m, to) => { sent.Add((real, m.Kind, to)); flying.Add((real + bToA, m, epB, to)); };
        a.Peers = () => [(epB, "B"), (epOld, "Old")];
        b.Peers = () => [(epA, "A")];
        a.ThroughServer = ep => ep.Equals(epOld);
        var bStopped = new List<string>();
        b.StoppedFollowing = bStopped.Add;
        var sound = new MetronomeSound("test", Impulse(0.8f), Impulse(0.4f));
        a.Configure(new MetronomeSettings { Bpm = 120 }, sound);
        b.Configure(new MetronomeSettings { Bpm = 60, WhenAsked = MetronomeMasterAnswer.AcceptAutomatically }, sound);
        void Run(double seconds)
        {
            var end = real + seconds * 1e6;
            var nextTick = real;
            while (real < end)
            {
                foreach (var f in flying.Where(f => f.At <= real).ToList())
                {
                    flying.Remove(f);
                    var (engine, clock) = f.To.Equals(epA) ? (a, (Func<long>)ClockA) : (b, ClockB);
                    if (f.M.Kind == MetronomeKind.SyncRequest)
                    {
                        if (engine.TryAnswerSync(f.M, f.From, clock()) is { } reply) engine.Send!(reply with { T2 = clock() + 30 }, f.From);
                    }
                    else engine.OnMessage(f.M, f.From, clock());
                }
                if (real >= nextTick) { a.Tick(); b.Tick(); nextTick += 50_000; }
                real += 500;
            }
        }
        int Count(MetronomeKind kind, IPEndPoint to, double from, double until) => sent.Count(s => s.Kind == kind && s.To.Equals(to) && s.At >= from && s.At < until);

        // Offers.
        var t0 = real;
        a.BecomeMaster("A");
        Check(Count(MetronomeKind.Offer, epB, t0, t0 + 1) == 1 && Count(MetronomeKind.Offer, epOld, t0, t0 + 1) == 1, "becoming master must offer to everyone connected at once");
        Run(0.05);
        Check(b.Role == MetronomeRole.Follower && a.Followers.Count == 1, "set to accept automatically, the follower must accept and the master count it");
        var accepted = sent.First(s => s.Kind == MetronomeKind.Accept).At;
        Run(12.5);
        Check(Count(MetronomeKind.Offer, epOld, t0, real) == 6, $"a peer that never answers must be offered six times, every 2 s for 10 s ({Count(MetronomeKind.Offer, epOld, t0, real)})");
        Check(Count(MetronomeKind.Offer, epB, t0, real) == 1, "a peer that answered is not offered again");
        Check(logA.Any(l => l.Contains("Old never answered", StringComparison.Ordinal) && l.Contains("server", StringComparison.Ordinal)),
            "and the master must log that it never answered - reached through a server, which does not pass the metronome on yet");
        Check(sent.Where(s => s.To.Equals(epOld)).All(s => s.Kind == MetronomeKind.Offer), "AN OLD PEER gets nothing but offers");
        var statesAfterAccept = sent.Where(s => s.Kind == MetronomeKind.State && s.To.Equals(epB) && s.At >= accepted && s.At < accepted + 400_000).Select(s => s.At).ToList();
        // 50 ms apart as the window's 50 ms timer can place them: each repeat 40 to 100 ms after the one before.
        var gaps = statesAfterAccept.Zip(statesAfterAccept.Skip(1), (x, y) => y - x).ToList();
        Check(statesAfterAccept.Count == 3 && gaps.All(g => g is >= 40_000 and <= 100_000),
            $"the new follower gets the timeline three times, about 50 ms apart ({statesAfterAccept.Count}: {string.Join(", ", gaps.Select(g => $"{g / 1000:0} ms"))})");
        var syncsFast = Count(MetronomeKind.SyncRequest, epA, accepted, accepted + 3_000_000);
        var syncsSlow = Count(MetronomeKind.SyncRequest, epA, accepted + 3_000_000, accepted + 11_000_000);
        Check(syncsFast is >= 12 and <= 13 && syncsSlow is >= 7 and <= 9, $"the follower reads the clock every 250 ms for 3 s ({syncsFast}), then every second ({syncsSlow} in 8 s)");
        Check(b.FollowerReady && b.CurrentSchedule is not { Audible: true } && !b.Playing, "settled, and nothing to play until the master starts");

        // Start: the follower plays the master's tempo, not its own, and lines up.
        var started = real;
        a.Start();
        Run(0.5);
        var statesAfterStart = sent.Where(s => s.Kind == MetronomeKind.State && s.To.Equals(epB) && s.At >= started && s.At < started + 300_000).Select(s => s.At).ToList();
        Check(statesAfterStart.Count == 3 && statesAfterStart[0] == started, $"a start goes out at once, and twice more ({statesAfterStart.Count})");
        Check(b.Playing && b.Timeline.MilliBpm == 120_000 && b.CurrentSchedule is { Audible: true }, "the follower must play the master's timeline (120 bpm, not its own 60)");
        Run(4);
        var steady = Count(MetronomeKind.State, epB, started + 1_000_000, started + 4_000_000);
        Check(steady is >= 2 and <= 4, $"and the timeline every second after ({steady} in 3 s)");
        var map = b.CurrentSchedule!.Map;
        var worst = 0.0;
        for (var n = 8; n < 20; n++)
        {
            var masterBeat = a.Timeline.BeatTimeUs(n);               // A's clock is the true time
            var followerLocal = map.MasterToLocal(masterBeat);        // where B plays it, on B's clock
            var followerReal = (followerLocal - 1_234_567) / (1 + 50e-6);
            worst = Math.Max(worst, Math.Abs(followerReal - masterBeat));
        }
        Check(worst <= 500 + 200, $"THE CLICKS LINE UP: each beat on the follower within half the 1 ms asymmetry plus 0.2 ms of the master's ({worst / 1000:0.000} ms at worst)");

        // Mute is the follower's alone.
        b.StartStop();
        Check(b.Muted && b.CurrentSchedule is { Muted: true, Audible: false } && a.Playing, "MUTE: the follower's own clicks silenced, the master playing on");
        a.StartStop();
        Check(!a.Playing && a.Role == MetronomeRole.Master, "the master's Start/Stop is its own");
        Run(0.2);
        Check(!b.Playing, "and stops the follower");
        b.StartStop();
        Check(!b.Muted, "UNMUTE");
        a.Start();
        Run(0.3);

        // An older timeline and an answer nobody asked for change nothing.
        var version = b.Timeline.Version;
        b.OnMessage(new MetronomeMessage(MetronomeKind.State, a.SessionId) { State = MetronomeTimeline.Stopped(90_000, version: version - 1) }, epA, ClockB());
        Check(b.Playing && b.Timeline.Version == version, "a replayed, older timeline must be ignored");
        var samples = b.Estimator.Samples;
        b.OnMessage(new MetronomeMessage(MetronomeKind.SyncReply, a.SessionId) { Seq = 99_999, T0 = 1, T1 = 2, T2 = 3 }, epA, ClockB());
        Check(b.Estimator.Samples == samples, "a clock reply no request is waiting for must be ignored");
        Check(a.TryAnswerSync(new MetronomeMessage(MetronomeKind.SyncRequest, a.SessionId) { Seq = 1, T0 = 1 }, epOld, ClockA()) is null,
            "a clock reading from someone not following is not answered");

        // A master declines someone else's offer.
        var declines = Count(MetronomeKind.Decline, epB, 0, double.MaxValue);
        a.OnMessage(new MetronomeMessage(MetronomeKind.Offer, 12345) { MasterName = "B" }, epB, ClockA());
        Check(a.Role == MetronomeRole.Master && Count(MetronomeKind.Decline, epB, 0, double.MaxValue) == declines + 1, "a master must decline another's offer");

        // Leave: the follower goes, the master drops it.
        b.StopFollowing("left", sendLeave: true);
        Run(0.05);
        Check(a.Followers.Count == 0 && b.Role == MetronomeRole.Solo, "LEAVE: the master must drop a follower that leaves");
        // Release: offered again (a new session), then let go.
        a.ReleaseMaster();
        a.BecomeMaster("A");
        Run(0.1);
        Check(b.Role == MetronomeRole.Follower, "a new session: offered and accepted again");
        a.ReleaseMaster();
        Run(0.05);
        Check(b.Role == MetronomeRole.Solo && bStopped.LastOrDefault()?.Contains("no longer the master", StringComparison.Ordinal) == true, "RELEASE: the follower stops, and says why");
        // Five seconds of silence.
        a.BecomeMaster("A");
        a.Start();
        Run(2);
        Check(b.Role == MetronomeRole.Follower && b.Playing, "following again, and playing");
        // A change goes out three times, 50 ms apart; the silence starts straight after the last.
        a.Configure(new MetronomeSettings { Bpm = 100 }, sound);
        Run(0.11);
        dropAToB = true;
        Run(4.5);
        Check(b.Role == MetronomeRole.Follower, "4.5 s of silence is not yet enough");
        Run(1);
        Check(b.Role == MetronomeRole.Solo && !b.Playing && bStopped.Last().Contains("nothing heard from A for 5 seconds", StringComparison.Ordinal),
            $"TIMEOUT: after 5 s with no timeline the follower stops and says so ({bStopped.Last()})");
        return $"six offers in 10 s to a peer that never answered (it got nothing else), three timelines 50 ms apart and one a second, clock readings "
             + $"at 250 ms then 1 s; beats within {worst / 1000:0.000} ms of the master's over a path 1 ms lopsided; mute, stale timelines, "
             + "stray replies, a rival offer, Leave, Release and a 5 s silence each handled";
    }

    /// <summary>A window's master tick and the clock of another window, over real loopback.</summary>
    private static (MainForm Form, int Port) MetronomeLoopbackWindow(string password, MetronomeSettings metronome, Func<long>? clock, List<string> logged)
    {
        int port;
        using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        var profile = Profile.NewBlank();
        profile.Password = RemSoundCrypto.Obfuscate(password);
        profile.Metronome = metronome;
        MainForm.MetronomeClockForTest = clock;
        MainForm.LocalAudioPortForTest = port;
        try
        {
            var form = MetronomeWindow(logged, profile);
            Require(typeof(MainForm).GetMethod("Connect", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, Type.EmptyTypes),
                "MainForm.Connect not found").Invoke(form, null);
            return (form, port);
        }
        finally
        {
            MainForm.MetronomeClockForTest = null;
            MainForm.LocalAudioPortForTest = 0;
        }
    }

    private static bool PumpUntil(Func<bool> condition, double seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            if (condition()) return true;
            Thread.Sleep(5);
        }
        return condition();
    }

    /// <summary>
    /// METRONOME MASTER (two real windows over loopback, the plan's test): the follower's clock 1234.567 ms off and 50 ppm
    /// fast. Ticking "Be the master metronome" offers; the follower, set to accept automatically, follows; the master's Start
    /// starts both, their clicks placed from their own clocks landing within 1 ms of the same moment; the follower's item and
    /// hotkey mute and unmute; the master's Stop stops both; unticking lets the follower go. Each logged.
    /// </summary>
    private static string? MetronomeMasterOverLoopback()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        var loggedA = new List<string>();
        var loggedB = new List<string>();
        MainForm? a = null, b = null;
        static long Real() => MetronomeClock.NowUs();
        static long Skewed(long real) => (long)(real * (1 + 50e-6)) + 1_234_567;
        MainForm.MasterWithoutServerForTest = true;   // the asking and answering, directly between windows
        try
        {
            int portA, portB;
            try
            {
                (a, portA) = MetronomeLoopbackWindow("metronome-pair", new MetronomeSettings(), null, loggedA);
                (b, portB) = MetronomeLoopbackWindow("metronome-pair", new MetronomeSettings { Sound = "Beep", WhenAsked = MetronomeMasterAnswer.AcceptAutomatically },
                    () => Skewed(Real()), loggedB);
            }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            a.SelectPeerForTest(new PeerAnnouncement(Guid.NewGuid(), "Follower", portB, true, true, DateTime.UtcNow, IPAddress.Loopback));
            b.SelectPeerForTest(new PeerAnnouncement(Guid.NewGuid(), "Master", portA, true, true, DateTime.UtcNow, IPAddress.Loopback));
            var ea = a.MetronomeForTest;
            var eb = b.MetronomeForTest;

            a.SetMetronomeMasterForTest(true);
            Check(a.MetronomeMasterItemForTest.Checked, "the menu's master item must be ticked");
            Check(PumpUntil(() => eb.Role == MetronomeRole.Follower && ea.Followers.Count == 1, 5), "OFFERED AND ACCEPTED: the follower, set to accept automatically, must follow within 5 s");
            Check(PumpUntil(() => eb.FollowerReady, 5), $"and its clock must settle ({eb.Estimator.Describe()})");
            Check(b.MetronomeStartStopItemForTest.AccessibleName == "Mute metronome", $"following, the item must say Mute metronome ({b.MetronomeStartStopItemForTest.AccessibleName})");

            a.MetronomeStartStopForTest("the menu");
            Check(PumpUntil(() => eb.Playing && eb.CurrentSchedule is { Audible: true }, 3), "the master's Start must start the follower");
            Check(a.MetronomeStartStopItemForTest.AccessibleName == "Stop metronome", "the master's item says Stop metronome");
            Check(eb.Timeline.MilliBpm == ea.Timeline.MilliBpm && eb.Sound?.Name == "Beep" && ea.Sound?.Name == "Click",
                "the follower plays the master's tempo on its own sound");
            PumpUntil(() => false, 1.5);   // a few more clock readings

            // Place the next beats from each window's own schedule and clock, from one real moment, and compare.
            var sa = Require(ea.CurrentSchedule, "the master's schedule");
            var sb = Require(eb.CurrentSchedule, "the follower's schedule");
            var laneA = new MetronomeClickLane("master");
            var laneB = new MetronomeClickLane("follower");
            var onsetsA = new Dictionary<long, double>();
            var onsetsB = new Dictionary<long, double>();
            var realStart = (double)Real();
            for (var i = 0; i < 400; i++)
            {
                var r = realStart + i * 10_000;
                var beforeA = laneA.OnsetsPlaced;
                var beforeB = laneB.OnsetsPlaced;
                laneA.Mix(new float[480 * 2], 2, 48000, r, 0, sa);
                laneB.Mix(new float[480 * 2], 2, 48000, Skewed((long)r), 0, sb);
                // Where each heard its beat, in real time: the master's clock is the real one; the follower's is undone.
                if (laneA.OnsetsPlaced != beforeA) onsetsA[laneA.LastOnsetBeat] = laneA.LastEarUs + laneA.LastOnsetFrameInBlock * 1e6 / 48000;
                if (laneB.OnsetsPlaced != beforeB)
                    onsetsB[laneB.LastOnsetBeat] = (laneB.LastEarUs + laneB.LastOnsetFrameInBlock * 1e6 / 48000 - 1_234_567) / (1 + 50e-6);
            }
            var both = onsetsA.Keys.Intersect(onsetsB.Keys).ToList();
            var placed = both.Count;
            var worstMs = both.Count == 0 ? double.NaN : both.Max(n => Math.Abs(onsetsA[n] - onsetsB[n])) / 1000;
            Check(placed >= 6, $"premise: both windows placed the same beats ({placed})");
            Check(worstMs <= 1.0, $"THE CLICKS LINE UP: placed from each window's own clock, every beat within 1 ms of the master's ({worstMs:0.000} ms at worst, {eb.Estimator.Describe()})");

            // Mute and unmute: the follower's item, and its hotkey.
            b.MetronomeStartStopForTest("the menu");
            Check(eb.Muted && b.MetronomeStartStopItemForTest.AccessibleName == "Unmute metronome" && ea.Playing, "MUTE: the follower's clicks silenced, the master playing on");
            Require(FieldOf<MainFormHotkeyController>(b, "hotkeyController")?.ToggleMetronome, "the hotkey's action")();
            Check(!eb.Muted && b.MetronomeStartStopItemForTest.AccessibleName == "Mute metronome", "the hotkey must unmute");

            a.MetronomeStartStopForTest("the menu");
            Check(PumpUntil(() => !eb.Playing, 3), "the master's Stop must stop the follower");
            a.SetMetronomeMasterForTest(false);
            Check(PumpUntil(() => eb.Role == MetronomeRole.Solo, 3) && b.MetronomeStartStopItemForTest.AccessibleName == "Start metronome",
                "unticking must let the follower go, its item back to Start metronome");
            Check(Logged(loggedA, "metronome: you are the master metronome") && Logged(loggedA, "accepted - following you"), "the master's log must say it led and who followed");
            Check(Logged(loggedB, "asks to be the master metronome (accepting automatically)") && Logged(loggedB, "clock settled against")
                  && Logged(loggedB, "stopped following"), "the follower's log must say it accepted, how its clock settled, and that it stopped following");
            return $"offered, accepted automatically, started and stopped together; the clicks, each placed from its own clock (one 1234.567 ms off and "
                 + $"50 ppm fast), within {worstMs:0.000} ms of each other over {placed} beats; mute and unmute by item and hotkey; unticking let go";
        }
        finally
        {
            MainForm.MasterWithoutServerForTest = false;
            try { a?.Dispose(); } catch { /* teardown */ }
            try { b?.Dispose(); } catch { /* teardown */ }
            CuePlayer.GloballyMuted = restoreMuted;
        }
    }

    /// <summary>A stand-in peer on a socket of its own: a metronome engine speaking the real sealed messages (a stand-in
    /// master), or none at all (a peer that drops type 12, as an older RemSound or a phone does). Its engine lives on its
    /// own thread.</summary>
    private sealed class StandInMetronomePeer : IDisposable
    {
        private readonly UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly byte[] key;
        private readonly Thread thread;
        private readonly ConcurrentQueue<Action<MetronomeEngine>> work = new();
        private volatile bool stop;
        private uint sequence;
        public MetronomeEngine? Engine { get; }
        public volatile bool Silent;
        public readonly ConcurrentQueue<MetronomeKind> Received = new();
        public readonly ConcurrentQueue<string> Seen = new();
        public IPEndPoint Endpoint => (IPEndPoint)socket.Client.LocalEndPoint!;

        public StandInMetronomePeer(byte[] key, IPEndPoint window, string? masterName)
        {
            this.key = key;
            if (masterName is not null)
            {
                Engine = new MetronomeEngine();
                Engine.Peers = () => [(window, "window")];
                Engine.Send = (m, to) => Send(m, to);
                Engine.Configure(new MetronomeSettings(), null);
            }
            thread = new Thread(Loop) { IsBackground = true, Name = "stand-in metronome peer" };
            thread.Start();
        }

        public void Do(Action<MetronomeEngine> action) => work.Enqueue(action);

        /// <summary>A message of the test's own, sealed and sent as it is.</summary>
        public void SendRaw(MetronomeMessage m, IPEndPoint to) => Send(m, to);

        private void Send(MetronomeMessage m, IPEndPoint to)
        {
            if (Silent) return;
            var packet = MetronomeWire.BuildPacket(key, m, ++sequence);
            try { socket.Send(packet, packet.Length, to); } catch { /* UDP */ }
        }

        private void Loop()
        {
            var nextTick = 0L;
            while (!stop)
            {
                while (work.TryDequeue(out var action)) if (Engine is not null) action(Engine);
                try
                {
                    // Wait for a datagram with Poll, never a timed-out Receive: a Receive that times out as a datagram
                    // arrives can lose it (seen here: three offers in six gone).
                    if (!socket.Client.Poll(10_000, SelectMode.SelectRead)) goto tick;
                    IPEndPoint? from = null;
                    var got = socket.Receive(ref from);
                    var stamp = MetronomeClock.NowUs();
                    Seen.Enqueue((RemPacket.TryReadHeader(got, out var seenType, out _, out _) ? $"{seenType}/{got.Length}" : $"?/{got.Length}") + $"@{DateTime.Now:ss.fff}");
                    if (RemPacket.TryReadHeader(got, out var type, out _, out _) && type == RemPacketType.Metronome
                        && MetronomeWire.TryOpen(key, got.AsSpan(RemPacket.HeaderSize), out var m, out _) && m is not null)
                    {
                        Received.Enqueue(m.Kind);
                        if (Engine is null) { }
                        else if (m.Kind == MetronomeKind.SyncRequest) { if (Engine.TryAnswerSync(m, from!, stamp) is { } reply) Send(reply with { T2 = MetronomeClock.NowUs() }, from!); }
                        else Engine.OnMessage(m, from!, stamp);
                    }
                }
                catch (SocketException ex) { if (ex.SocketErrorCode != SocketError.TimedOut) Seen.Enqueue("socket " + ex.SocketErrorCode); }
                catch (Exception ex) when (ex is not ObjectDisposedException) { Seen.Enqueue("threw " + ex.GetType().Name + ": " + ex.Message); }
                catch (ObjectDisposedException) { break; }
                tick:
                if (Engine is not null && Environment.TickCount64 >= nextTick) { Engine.Tick(); nextTick = Environment.TickCount64 + 50; }
            }
        }

        public void Dispose()
        {
            stop = true;
            thread.Join(2000);
            socket.Dispose();
        }
    }

    /// <summary>
    /// METRONOME ASKED (a window, and stand-in masters speaking the real sealed messages over loopback): set to ask, the
    /// window puts the question with the master's name; No is sent, not asked again as the offers repeat, and logged; Yes
    /// follows, plays when the master starts, and the item says Mute; a master that falls silent is left after 5 s and that
    /// is said. Leading, the window offers a peer that drops type 12 six times in 10 s and sends it nothing else, and logs
    /// that it never answered.
    /// </summary>
    private static string? MetronomeAskedDeclinedAndOldPeers()
    {
        // Its own questions only: the ASIO warning has its own step (METRONOME ASIO WARNING).
        using var noAsioWarning = WithoutMetronomeAsioWarning();
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        var logged = new List<string>();
        MainForm? form = null;
        StandInMetronomePeer? first = null, second = null, old = null;
        MainForm.MasterWithoutServerForTest = true;   // the asking and answering, directly between windows
        try
        {
            int port;
            try { (form, port) = MetronomeLoopbackWindow("metronome-asked", new MetronomeSettings(), null, logged); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            var main = form;
            main.SomebodyToAnswerForTest = true;
            main.NobodyToAskForTest = () => false;
            var engine = main.MetronomeForTest;
            var control = new RemoteControlEngine(() => main);
            var key = Require(RemSoundCrypto.ForPlainPassword("metronome-asked").Key, "a key");
            var window = new IPEndPoint(IPAddress.Loopback, port);
            bool Asking() => main.Invoke(() => Application.OpenForms.OfType<HeadlessQuestionForm>().Any());
            string Question() => main.Invoke(() => Application.OpenForms.OfType<HeadlessQuestionForm>().First().Question);
            var speechMark = HeadlessRecords.Speech.Mark;
            var questions = 0;
            string firstQuestion = "";

            DriveWhilePumping(main, () =>
            {
                first = new StandInMetronomePeer(key, window, "First master");
                first.Do(e => e.BecomeMaster("First master"));
                Check(WaitFor(Asking, TimeSpan.FromSeconds(5)), "ASK: an offer must be asked about");
                firstQuestion = Question();
                questions++;
                Check(firstQuestion.Contains("First master would like to be the master metronome", StringComparison.Ordinal), $"the question must name the master ({firstQuestion})");
                Check(main.Invoke(() => control.Execute("answer No")).StartsWith("ok", StringComparison.Ordinal), "the question must take No");
                Check(WaitFor(() => first.Received.Contains(MetronomeKind.Decline), TimeSpan.FromSeconds(3)), "NO: the master must be told");
                // The same offer again (the answer lost on the way, say): answered No without asking.
                var session = first.Engine!.SessionId;
                first.SendRaw(new MetronomeMessage(MetronomeKind.Offer, session) { MasterName = "First master" }, window);
                first.SendRaw(new MetronomeMessage(MetronomeKind.Offer, session) { MasterName = "First master" }, window);
                Check(WaitFor(() => first.Received.Count(k => k == MetronomeKind.Decline) >= 3, TimeSpan.FromSeconds(3)) && !Asking(),
                    $"and not asked again as the offer repeats - each answered No ({first.Received.Count(k => k == MetronomeKind.Decline)} declines)");

                second = new StandInMetronomePeer(key, window, "Second master");
                second.Do(e => e.BecomeMaster("Second master"));
                Check(WaitFor(Asking, TimeSpan.FromSeconds(5)), "another master's offer must be asked about");
                questions++;
                Check(main.Invoke(() => control.Execute("answer Yes")).StartsWith("ok", StringComparison.Ordinal), "the question must take Yes");
                Check(WaitFor(() => main.Invoke(() => engine.Role == MetronomeRole.Follower && engine.MasterName == "Second master"), TimeSpan.FromSeconds(3)), "YES: the window must follow");
                Check(WaitFor(() => main.Invoke(() => engine.FollowerReady), TimeSpan.FromSeconds(5)), "and its clock settle");
                second.Do(e => e.Start());
                Check(WaitFor(() => main.Invoke(() => engine.Playing && engine.CurrentSchedule is { Audible: true }), TimeSpan.FromSeconds(3)), "and play when the master starts");
                Check(main.Invoke(() => main.MetronomeStartStopItemForTest.AccessibleName) == "Mute metronome", "the item must say Mute metronome");

                // The master falls silent.
                second.Silent = true;
                Check(!WaitFor(() => main.Invoke(() => engine.Role != MetronomeRole.Follower), TimeSpan.FromSeconds(3.5)), "three and a half seconds of silence is not yet enough");
                Check(WaitFor(() => main.Invoke(() => engine.Role == MetronomeRole.Solo), TimeSpan.FromSeconds(3)), "TIMEOUT: after 5 s with nothing heard, the window must stop following");
            }, TimeSpan.FromSeconds(60));
            Check(!engine.Playing && main.MetronomeStartStopItemForTest.AccessibleName == "Start metronome", "the clicks stop, and the item is Start again");
            Check(HeadlessRecords.Speech.Since(speechMark).Any(l => l.Contains("Stopped following Second master's master metronome: nothing heard from Second master for 5 seconds", StringComparison.Ordinal)),
                "and it must be said");
            Check(Logged(logged, "metronome: declined First master's offer") && Logged(logged, "following Second master's master metronome")
                  && Logged(logged, "nothing heard from Second master for 5 seconds"), "each logged");

            // Leading a peer that drops type 12.
            old = new StandInMetronomePeer(key, window, null);
            var windowSent = new ConcurrentQueue<string>();
            main.MetronomeSentForTest = (kind, to, went) => { if (to.Port == old.Endpoint.Port) windowSent.Enqueue($"{kind}{(went ? "" : " (not sent)")}@{DateTime.Now:ss.fff}"); };
            main.SelectPeerForTest(new PeerAnnouncement(Guid.NewGuid(), "Old RemSound", old.Endpoint.Port, true, true, DateTime.UtcNow, IPAddress.Loopback));
            main.SetMetronomeMasterForTest(true);
            main.MetronomeStartStopForTest("the menu");
            Check(PumpUntil(() => Logged(logged, "Old RemSound never answered"), 15), "leading, a peer that never answers must be logged as such after the offers");
            Check(PumpUntil(() => HeadlessRecords.Speech.Since(speechMark).Any(l => l.Contains("Old RemSound did not answer the metronome offer", StringComparison.Ordinal)), 2),
                "and the window must say so, as it says who follows and who said no");
            PumpUntil(() => false, 1.5);
            var kinds = old.Received.ToList();
            string MetronomeLines() { lock (logged) return string.Join(" | ", logged.Where(l => l.Contains("metronome", StringComparison.Ordinal)).TakeLast(12)); }
            Check(kinds.Count == 6 && kinds.All(k => k == MetronomeKind.Offer),
                $"AN OLD PEER gets six offers in 10 s and nothing else, the metronome playing all the while ({string.Join(", ", kinds)}; the window sent {string.Join(", ", windowSent)}; it saw {string.Join(", ", old.Seen.GroupBy(x => x).Select(g => $"{g.Key} x{g.Count()}"))}; {MetronomeLines()})");
            main.MetronomeStartStopForTest("the menu");
            main.SetMetronomeMasterForTest(false);
            return $"asked with the master's name ({questions} questions); No sent and not asked again; Yes followed, played and said Mute; a silent master left "
                 + "after 5 s and said; an old peer got six offers and nothing else";
        }
        finally
        {
            MainForm.MasterWithoutServerForTest = false;
            first?.Dispose();
            second?.Dispose();
            old?.Dispose();
            try { form?.Dispose(); } catch { /* teardown */ }
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
        }
    }

    /// <summary>METRONOME ON THE WIRE, AT THE DOOR: a type-12 packet reaches the window whole, on the listener and on the
    /// sender's socket (a relay's path); one outside the sizes any kind can have is dropped and counted; the window opens
    /// only its own password's, and refuses a replay.</summary>
    private static string? MetronomeReachesTheWindow()
    {
        var receiver = new RemSound.Receiver.AudioReceiver();
        var handed = 0;
        receiver.OnMetronomeReceived = (_, _) => handed++;
        var key = Require(RemSoundCrypto.ForPlainPassword("door").Key, "a key");
        // A loopback address with nobody on it: the window's answers go nowhere off this machine.
        var from = new IPEndPoint(IPAddress.Loopback, FreeUdpPort());
        var good = MetronomeWire.BuildPacket(key, new MetronomeMessage(MetronomeKind.Accept, 9), 1);
        receiver.InjectExternalPacket(good, good.Length, from);
        Check(handed == 1, "a metronome message must be handed up whole");
        var before = receiver.PacketsRejectedMalformed;
        var runt = new byte[RemPacket.HeaderSize + MetronomeWire.MinSealedBytes - 1];
        RemPacket.WriteHeader(runt, RemPacketType.Metronome, 0xFFFF, 2);
        receiver.InjectExternalPacket(runt, runt.Length, from);
        var huge = new byte[RemPacket.HeaderSize + MetronomeWire.MaxSealedBytes + 1];
        RemPacket.WriteHeader(huge, RemPacketType.Metronome, 0xFFFF, 3);
        receiver.InjectExternalPacket(huge, huge.Length, from);
        Check(handed == 1 && receiver.PacketsRejectedMalformed == before + 2, "one too short and one too long must be dropped and counted, never handed up");
        receiver.Dispose();

        MainForm? form = null;
        var logged = new List<string>();
        try
        {
            var profile = Profile.NewBlank();
            profile.Password = RemSoundCrypto.Obfuscate("door");
            profile.Metronome = new MetronomeSettings { WhenAsked = MetronomeMasterAnswer.AcceptAutomatically };
            try { form = MetronomeWindow(logged, profile); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            var main = form;
            // The window's password key is worked out as it connects; hand it the same one.
            Require(typeof(MainForm).GetMethod("RecomputeAudioCrypto", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
                "MainForm.RecomputeAudioCrypto not found").Invoke(main, null);
            var offer = MetronomeWire.BuildPacket(key, new MetronomeMessage(MetronomeKind.Offer, 77) { MasterName = "Door master" }, 4);
            var payload = offer.AsSpan(RemPacket.HeaderSize).ToArray();
            main.MetronomeArrivedForTest(payload, from);
            Check(PumpUntil(() => main.MetronomeForTest.Role == MetronomeRole.Follower, 2), "an offer sealed with the window's own password must reach the metronome (set to accept)");
            main.MetronomeForTest.StopFollowing("test", sendLeave: false);
            main.MetronomeArrivedForTest(payload, from);
            PumpUntil(() => false, 0.3);
            Check(main.MetronomeForTest.Role == MetronomeRole.Solo, "A REPLAY of the same seal must be refused");
            var otherKey = Require(RemSoundCrypto.ForPlainPassword("not-the-door").Key, "another key");
            var stranger = MetronomeWire.BuildPacket(otherKey, new MetronomeMessage(MetronomeKind.Offer, 78) { MasterName = "Stranger" }, 5);
            main.MetronomeArrivedForTest(stranger.AsSpan(RemPacket.HeaderSize).ToArray(), from);
            PumpUntil(() => false, 0.3);
            Check(main.MetronomeForTest.Role == MetronomeRole.Solo, "another password's offer must be refused");
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
        }
        return "handed up whole; runts and oversize dropped and counted; the window opened its own password's offer, refused a replay and a stranger";
    }
}
