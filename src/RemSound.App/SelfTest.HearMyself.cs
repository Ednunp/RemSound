using System.Collections.Concurrent;
using System.Net;
using RemSound.Core;
using RemSound.Sender;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>HEAR MYSELF, THE CLIENT'S PART (Ed, 2026-10-01: "unmute myself to test round trip"). With no server: while on,
    /// the hello names us first; our own sound coming back is delivered as from our own echo address, anything else of ours
    /// is still consumed, and nothing of ours is delivered while off; leaving the relay, or going to another, mutes us; alone
    /// there, our echo is the only receiver the Jamulus-style rule asks about, and with somebody else there it changes
    /// nothing. And the meter: a packet timed whichever comes first, its report or its echo.</summary>
    private static string? HearMyselfOnTheWire()
    {
        var relay = new IPEndPoint(IPAddress.Parse("203.0.113.70"), RemPacket.DefaultPort);
        var me = Guid.NewGuid();
        var alice = Guid.NewGuid();
        var group = new RelayGroupClient(me);
        group.SetIdentity("Ed", new byte[RelayGroupClient.GroupTagBytes]);
        Check(!group.SetHearMyself(true) && !group.HearsItself, "with no relay there is nobody to send our sound back: it must stay off");
        group.Connect(relay);
        Guid[] HelloIds()
        {
            var hello = group.HelloForTest();
            var at = RelayGroupClient.GroupHeaderSize + RelayGroupClient.NameBytes + RelayGroupClient.GroupTagBytes;
            return Enumerable.Range(0, hello[at]).Select(i => new Guid(hello.AsSpan(at + 1 + i * RelayGroupClient.ClientIdSize, RelayGroupClient.ClientIdSize), bigEndian: true)).ToArray();
        }
        group.SetTicked([alice], pairPartner: false);
        Check(!HelloIds().Contains(me), "premise: muted, the hello must not name us");

        Check(group.SetHearMyself(true) && group.HearsItself && group.OwnEchoAddress is { } echo && RelayGroupClient.IsMemberAddress(echo.Address),
            $"unmuted on a relay, it must be on, with our own sound's address among the member addresses ({group.OwnEchoAddress})");
        var ids = HelloIds();
        Check(ids.Length == 2 && ids[0] == me && ids[1] == alice, $"UNMUTED: the hello must name us FIRST, then who we ticked ({string.Join(", ", ids)})");
        group.SetTicked(Enumerable.Range(0, 80).Select(_ => Guid.NewGuid()), pairPartner: false);
        ids = HelloIds();
        Check(ids.Length == RelayGroupClient.MaxTickedIds && ids[0] == me, "with more ticked than a hello holds, we must still be in it, first");
        group.SetTicked([alice], pairPartner: false);
        Check(group.HearsItself, "ticking people must leave it alone");

        var ownEcho = group.OwnEchoAddress!;
        byte[] Own(RemPacketType type) => GroupPacket((byte)type, me, [1, 2, 3, 4]);
        var audio = Own(RemPacketType.Audio);
        var length = audio.Length;
        Check(group.HandleInbound(audio, ref length, relay, out var from) == RelayInbound.Delivered && ownEcho.Equals(from)
              && length == RemPacket.HeaderSize + 4 && audio[4] == RemPacket.Version,
            $"OUR SOUND BACK: our own audio must be delivered, in ordinary form, as from our own echo address ({from})");
        Feed(group, relay, Own(RemPacketType.Format), RelayInbound.Delivered, "our own format packet, back from the server");
        Feed(group, relay, Own(RemPacketType.Heartbeat), RelayInbound.Consumed, "our own heartbeat, which the server never echoes");
        Feed(group, relay, Own(RemPacketType.Control), RelayInbound.Consumed, "our own control packet");

        // Alone there, our echo is the only receiver; with somebody else, it changes nothing for them.
        Feed(group, relay, RosterPacket([(me, "Ed", true)], paired: false), RelayInbound.Consumed, "a member list with only us on it");
        Check(group.EveryReceiverOn(relay, a => a.Equals(ownEcho)), "ALONE: our own echo must be the receiver asked about");
        Check(!group.EveryReceiverOn(relay, _ => false), "and its answer must be the answer");
        Feed(group, relay, RosterPacket([(alice, "Alice", true)], paired: false), RelayInbound.Consumed, "a member list with Alice");
        var aliceAt = group.Members.Single().Address;
        Check(!group.EveryReceiverOn(relay, a => a.Equals(ownEcho)), "WITH ALICE: our echo must not stand in for her");
        Check(group.EveryReceiverOn(relay, a => a.Equals(aliceAt)), "and only she must be asked about");

        group.SetHearMyself(false);
        Check(!group.HearsItself && group.OwnEchoAddress is null && !HelloIds().Contains(me), "MUTED: off, no echo address, and the hello no longer names us");
        Feed(group, relay, Own(RemPacketType.Audio), RelayInbound.Consumed, "our own audio while muted");

        group.SetHearMyself(true);
        group.Disconnect();
        Check(!group.HearsItself, "LEAVING THE SERVER must mute us");
        group.Connect(relay);
        group.SetHearMyself(true);
        group.Connect(new IPEndPoint(IPAddress.Parse("203.0.113.71"), RemPacket.DefaultPort));
        Check(!group.HearsItself, "GOING TO ANOTHER SERVER must mute us: our sound was on the one we left");

        // The meter: either side first.
        var meter = new HearMyselfMeter();
        meter.NoteSent([(1, 10, 1_000_000), (1, 11, 1_000_500)]);
        meter.NoteArrival(1, 10, 1_030_000);
        meter.NoteArrival(2, 77, 1_031_000);            // nothing sent like it: counted, not timed
        meter.NoteArrival(1, 12, 1_045_000);            // back before its report
        meter.NoteSent([(1, 12, 1_005_000)]);
        var (arrivals, timed, typical, longest) = meter.TakeSecond();
        Check(arrivals == 3 && timed == 2 && typical is 35.0 && longest is 40.0,
            $"THE METER: the echo before its report must be timed as well as the one after (3 back, 2 timed, 30 and 40 ms; got {arrivals}, {timed}, {typical}, {longest})");
        Check(meter.TakeSecond() is (0, 0, null, null), "and each second must be counted afresh");
        meter.NoteSent(Enumerable.Range(0, HearMyselfMeter.MaxWaiting * 3).Select(i => ((ushort)3, (uint)i, (long)i)).ToList());
        meter.NoteArrival(3, 0, 5);
        Check(meter.TakeSecond().Timed == 0, "packets long gone must be forgotten, oldest first, so it never grows");
        return "unmuted, the hello names us first even when full; our own sound and format come back as from our own echo address, "
             + "nothing else of ours, and nothing while muted; leaving or changing server mutes us; alone our echo is the one "
             + "receiver asked about, with Alice it changes nothing; the meter times an echo before or after its report";
    }

    /// <summary>HEAR MYSELF THROUGH THE SERVER (Ed, 2026-10-01). The real relay, and an app sending as the app sends: alone
    /// there and unmuted, its own sound comes back, each lane's stream as its own, and is timed from the input to back;
    /// muted, the server stops sending it; with Andre there too he still hears Ed once, as ever. Alone and playing through ASIO
    /// with the library here, the ASIO lane comes back as the Jamulus-style stream somebody like Ed would be sent.
    /// REMSOUND_TEST_LIVE_RELAY=host:port runs the first part through a real server instead (never in the gate).</summary>
    private static string? HearMyselfThroughTheServer()
    {
        var liveText = Environment.GetEnvironmentVariable("REMSOUND_TEST_LIVE_RELAY");
        var live = string.IsNullOrWhiteSpace(liveText) ? null : IPEndPoint.Parse(liveText.Trim());
        var root = FindSourceRoot();
        if (root is null && live is null) return Skip("the source tree is not reachable (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var python = FindPython();
        if (python is null && live is null) return Skip("there is no Python on this machine to run the relay (py.exe, or a python.exe outside WindowsApps)");

        var restorePlays = JamulusStyle.PlaysThroughAsio;
        var port = FreeUdpPort();
        var logDir = Path.Combine(Path.GetTempPath(), $"remsound-relay-echo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "relay.log");
        var relayProcess = live is null ? StartRelay(python!, Path.Combine(root!, "server", "remsound-relay.py"), port, logPath) : null;
        var apps = new List<RelayTestApp>();
        using var stopFeeding = new CancellationTokenSource();
        Thread? feeder = null;
        var talking = Array.Empty<(AudioSender Sender, bool Asio)>();
        try
        {
            if (relayProcess is not null)
                Check(WaitUntil(() => ReadShared(logPath).Contains("event=startup"), 10000) && !relayProcess.HasExited, $"the relay must start under {python}");
            var relay = live ?? new IPEndPoint(IPAddress.Loopback, port);
            JamulusStyle.PlaysThroughAsio = false;
            CustomOpusPeers.Clear();
            feeder = new Thread(() =>
            {
                var block = new float[480 * 2];
                var next = Environment.TickCount64;
                while (!stopFeeding.IsCancellationRequested)
                {
                    foreach (var (sender, asio) in Volatile.Read(ref talking))
                        if (asio) sender.FeedAsioLaneForTest(block); else sender.FeedDefaultLaneForTest(block);
                    next += 10;
                    var wait = next - Environment.TickCount64;
                    if (wait > 0) Thread.Sleep((int)wait);
                }
            }) { IsBackground = true, Name = "self-test hear myself" };
            feeder.Start();

            var password = live is null ? "hear myself" : $"remsound self-test {Guid.NewGuid():N}";   // on a real server, a group of its own
            var ed = new RelayTestApp("Ed", password, relay, groups: true);
            apps.Add(ed);
            Check(WaitUntil(() => ed.Group!.IsInGroup(relay), 15000), "Ed must join the server's group");
            ed.Sender.ConfigureCodec(AudioTransportCodec.Opus, 120);
            ed.Sender.UseAsioLaneForTest();
            ed.Sender.UseDefaultLaneForTest(RenderRoute.WasapiLane);
            ed.Sender.TimingWanted = true;
            ed.Tick([]);
            Volatile.Write(ref talking, [(ed.Sender, true), (ed.Sender, false)]);
            Thread.Sleep(800);
            Check(ed.OwnPacketsBack == 0 && ed.Receiver.LiveSessionCountForTest == 0,
                $"premise: alone, muted and with nobody ticked, nothing of ours comes back ({ed.OwnPacketsBack} back)");

            Check(ed.HearMyself(true), "unmuting on a server must ask it");
            var ownEcho = ed.Group!.OwnEchoAddress!;
            Check(MainForm.SendTargets([], ed.Group, relayPairTicked: false) is [var target] && target.Equals(relay),
                "UNMUTED ALONE: our sound must go to the server, though nobody else is ticked");
            string Back() => string.Join(", ", ed.Receiver.StreamDepthsFrom(ownEcho).Select(s => $"{s.Lane} on {s.Route} {s.BufferedMs} ms"));
            bool BackOn(RenderRoute lane) => ed.Receiver.StreamDepthsFrom(ownEcho).Any(s => s.Lane == lane);
            Check(WaitUntil(() => BackOn(RenderRoute.AsioLane) && BackOn(RenderRoute.WasapiLane), 8000),
                $"OUR OWN SOUND BACK: each lane's stream must come back from the server, as the lane it was sent on ({Back()}; {ed.OwnPacketsBack} packets back)");

            ed.Meter.TakeSecond();
            var until = Environment.TickCount64 + 2500;
            while (Environment.TickCount64 < until)
            {
                Thread.Sleep(200);
                ed.Meter.NoteSent(ed.Sender.TakeTimingReports());
            }
            var (arrivals, timed, typical, longest) = ed.Meter.TakeSecond();
            Check(arrivals > 200 && timed >= 10 && typical is > 0 and < 1000,
                $"MEASURED: our packets must be timed from the input to back here ({arrivals} back, {timed} timed, typical {typical:0.0} ms)");
            if (live is not null)
                return $"THROUGH THE REAL SERVER {live}: our own sound came back on both lanes, {typical:0.0} ms from the input to back "
                     + $"(typical of {timed} timed, longest {longest:0.0} ms)";

            ed.HearMyself(false);
            Thread.Sleep(600);   // what was already on its way
            var stopped = ed.OwnPacketsBack;
            Thread.Sleep(1500);
            Check(ed.OwnPacketsBack == stopped, $"MUTED: the server must stop sending our sound back ({ed.OwnPacketsBack - stopped} more came)");
            Check(MainForm.SendTargets([], ed.Group, relayPairTicked: false).Length == 0, "and with nobody ticked, nothing must be sent at all");

            // Andre arrives: he hears Ed once, as ever, while Ed hears himself.
            var andre = new RelayTestApp("Andre", password, relay, groups: true);
            apps.Add(andre);
            Check(WaitUntil(() => ed.Group.Members.Count == 1 && andre.Group!.Members.Count == 1, 15000), "Andre must join Ed's group");
            var andreSeenByEd = ed.Group.Members.Single();
            var edSeenByAndre = andre.Group!.Members.Single();
            ed.Tick([andreSeenByEd.Id]);
            andre.Tick([edSeenByAndre.Id]);
            ed.HearMyself(true);
            Check(MainForm.SendTargets([andreSeenByEd.Address], ed.Group, relayPairTicked: false) is [var one] && one.Equals(relay),
                "WITH ANDRE: still one copy, to the server");
            Check(WaitUntil(() => andre.Receiver.StreamDepthsFrom(edSeenByAndre.Address).Count == 2 && BackOn(RenderRoute.AsioLane) && BackOn(RenderRoute.WasapiLane), 8000),
                $"Andre must hear Ed's two lanes while Ed hears himself ({andre.Receiver.StreamDepthsFrom(edSeenByAndre.Address).Count} at Andre; Ed: {Back()})");
            Thread.Sleep(1000);
            Check(andre.Receiver.LiveSessionCountForTest == 2,
                $"and only once each: Ed's own echo must never reach Andre as more of Ed ({andre.Receiver.LiveSessionCountForTest} streams at Andre)");

            // Alone again and playing through ASIO, with the library: the ASIO lane comes back as the Jamulus-style stream.
            OpusCustomNative.TryLoad();
            var jamulus = "the Jamulus-style part was skipped: the library is not here";
            if (OpusCustomNative.Available)
            {
                Volatile.Write(ref talking, [(ed.Sender, true), (ed.Sender, false)]);
                andre.Dispose();
                apps.Remove(andre);
                Check(WaitUntil(() => ed.Group.Members.Count == 0, 10000), "Andre must leave");
                ed.Tick([]);
                JamulusStyle.PlaysThroughAsio = true;
                bool BackAs(AudioTransportCodec codec) => ed.Receiver.SessionCodecsForTest.Any(c => c.From.Equals(ownEcho) && c.Codec == codec);
                Check(WaitUntil(() => BackAs(AudioTransportCodec.OpusCustom), 10000),
                    $"ALONE THROUGH ASIO: our ASIO lane must come back as the Jamulus-style stream ({string.Join(", ", ed.Receiver.SessionCodecsForTest.Where(c => c.From.Equals(ownEcho)).Select(c => c.Codec))})");
                jamulus = "alone and playing through ASIO, the ASIO lane came back as the Jamulus-style stream";
            }
            Check(ReadShared(logPath).Contains("event=self_echo") && ReadShared(logPath).Contains("state=on") && ReadShared(logPath).Contains("state=off"),
                "the server's log must say when it started and stopped sending a member its own sound");
            return $"alone and unmuted, our own sound came back on both lanes, timed at {typical:0.0} ms from the input (loopback "
                 + $"server); muted, the server stopped; with Andre there, one copy to the server and he heard Ed once; {jamulus}; "
                 + "the server logged it on and off";
        }
        finally
        {
            Volatile.Write(ref talking, []);
            stopFeeding.Cancel();
            feeder?.Join(2000);
            foreach (var app in apps) app.Dispose();
            JamulusStyle.PlaysThroughAsio = restorePlays;
            CustomOpusPeers.Clear();
            try { relayProcess?.Kill(entireProcessTree: true); } catch { /* already gone */ }
            relayProcess?.Dispose();
            try { Directory.Delete(logDir, recursive: true); } catch { /* the relay may still hold its log a moment */ }
        }
    }

    /// <summary>HEAR MYSELF, THE BUTTON (Ed, 2026-10-01). On the Audio profile tab, straight after Total latency in Tab order:
    /// "Unmute myself to test round trip", then "Mute myself" (Alt+S both). Pressed where it cannot work - no server, nothing
    /// sent, Receive audio off - it says why and stays muted. On a server: unmuted, our sound goes to the server and our own
    /// echo is let in; muted, neither. Leaving the server mutes it; so does nothing coming back. The loopback warning (Ed:
    /// "1st time you use it ... a do not show this message again dialogue about not using loop back") and its tick.</summary>
    private static string? HearMyselfButton()
    {
        var page = MainForm.BuildHearMyselfWarning(out var tick);
        var words = (page.Heading ?? "") + " " + (page.Text ?? "");
        Check(words.Contains("loopback", StringComparison.Ordinal) && words.Contains("headphones", StringComparison.OrdinalIgnoreCase)
              && words.Contains("feedback", StringComparison.Ordinal),
            $"the warning must be about loopback and feedback, and say headphones ({words})");
        Check(tick.Text == "Do not show me this message again" && !tick.Checked, "it must carry Ed's tick, unticked");
        Check(page.Buttons.Count == 2 && page.Buttons.Contains(TaskDialogButton.OK) && page.Buttons.Contains(TaskDialogButton.Cancel),
            "and OK to go on or Cancel to stay muted");
        Check(MainForm.WhyCannotHearMyself(onServer: false, sending: true, playing: true)?.Contains("not on a server", StringComparison.Ordinal) == true
              && MainForm.WhyCannotHearMyself(true, sending: false, playing: true)?.Contains("Send my audio", StringComparison.Ordinal) == true
              && MainForm.WhyCannotHearMyself(true, true, playing: false)?.Contains("Receive audio", StringComparison.Ordinal) == true
              && MainForm.WhyCannotHearMyself(true, true, true) is null,
            "each thing it needs must be said by name when it is missing, and nothing when all are there");

        var scratch = Path.Combine(Path.GetTempPath(), "remsound-hear-myself-" + Guid.NewGuid().ToString("N"));
        using var userData = AppConfig.UseThrowawayUserDataDirectory(scratch);
        Check(AppConfig.Load().ShowHearMyselfWarning, "the warning must be on until hidden");
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        MainForm? form = null;
        RemoteControlServer? server = null;
        var pipe = "RemSound.control.selftest.hearmyself." + Guid.NewGuid().ToString("N")[..8];
        try
        {
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            var main = form;
            var said = new ConcurrentQueue<string>();
            form.LogForTest.EventTapForTest = said.Enqueue;
            var button = form.HearMyselfButtonForTest;
            string Plain() => button.Text.Replace("&", "");
            Check(Plain() == "Unmute myself to test round trip on server (Alt+S)" && button.AccessibleName == "Unmute myself to test round trip on server",
                $"it must start muted, saying what pressing it does ({button.Text} / {button.AccessibleName})");
            Check(!IsSetVisibleForAltAudit(button), "NOT ON A SERVER: the button must not be there (Ed: it should vanish if you're not connected to a server)");
            Check(button.Text.Contains("&s", StringComparison.Ordinal), "and Alt+S must be its key, as it says");
            var tab = button.Parent;
            while (tab is not null and not TabPage) tab = tab.Parent;
            Check(tab is TabPage { Text: "Audio profile" }, $"it must be on the Audio profile tab ({tab?.Text})");
            var readout = form.MeasuredLatencyReadoutForTest;
            var root = (Control)tab!;

            form.ToggleHearMyselfForTest();
            Check(!form.RelayGroupForTest.HearsItself && Plain().StartsWith("Unmute", StringComparison.Ordinal), "NO SERVER: it must stay muted");
            Check(said.Any(l => l.Contains("hear myself: not unmuted - You're not on a server", StringComparison.Ordinal)), "and the log must say why");

            var relay = new IPEndPoint(IPAddress.Parse("203.0.113.72"), RemPacket.DefaultPort);
            form.ConnectToRelayForTest("relay.example.test", relay);
            Check(IsSetVisibleForAltAudit(button), "ON A SERVER: the button must be there");
            Check(root.GetNextControl(readout, true) == button,
                $"straight after Total latency in Tab order ({root.GetNextControl(readout, true)?.AccessibleName})");
            form.HearMyselfPrereqsForTest = (Sending: false, Receiving: true);
            form.ToggleHearMyselfForTest();
            Check(!form.RelayGroupForTest.HearsItself && said.Any(l => l.Contains("not unmuted - Nothing is being sent", StringComparison.Ordinal)),
                "NOTHING SENT: it must stay muted and say why");
            form.HearMyselfPrereqsForTest = (Sending: true, Receiving: false);
            form.ToggleHearMyselfForTest();
            Check(!form.RelayGroupForTest.HearsItself && said.Any(l => l.Contains("not unmuted - Receive audio is off", StringComparison.Ordinal)),
                "RECEIVE AUDIO OFF: it must stay muted and say why");

            form.HearMyselfPrereqsForTest = (Sending: true, Receiving: true);
            Check(form.SendTargetsNowForTest().Length == 0, "premise: on a server with nobody ticked, nothing is sent");
            form.ToggleHearMyselfForTest();
            var ownEcho = form.RelayGroupForTest.OwnEchoAddress;
            Check(form.RelayGroupForTest.HearsItself && ownEcho is not null, "UNMUTED: the server must be asked");
            Check(Plain() == "Mute myself (Alt+S)" && button.AccessibleName == "Mute myself", $"and the button must now offer to mute ({button.Text})");
            Check(form.SendTargetsNowForTest() is [var target] && target.Equals(relay), "our sound must go to the server, though nobody is ticked");
            Check(form.ReceiverForTest.IsSenderAllowedForTest(ownEcho!), "and our own sound coming back must be let in");
            // Ed, 2026-10-01 15:56: alone on a server nobody answers its heartbeat, and after 30 s it was taken for
            // unreachable and nothing more was sent - his own sound stopped 12 seconds after he unmuted.
            form.PeerHealthForTest = () => [new PeerHealth(relay, PeerHealthState.Unreachable, null, TimeSpan.FromMinutes(1))];
            Check(form.EndpointsToArmForTest([relay]) is [var armed] && armed.Equals(relay),
                "UNANSWERED: a server that has not answered the heartbeat for a minute must still be sent our sound while we hear ourselves");
            Check(said.Any(l => l.Contains("ui: unmute myself to test round trip → on (the button) [", StringComparison.Ordinal)), "the log must say it, with the configuration");

            form.ToggleHearMyselfForTest();
            Check(!form.RelayGroupForTest.HearsItself && Plain().StartsWith("Unmute", StringComparison.Ordinal), "MUTED again: off, and the button back");
            Check(form.SendTargetsNowForTest().Length == 0 && !form.ReceiverForTest.IsSenderAllowedForTest(ownEcho!),
                "nothing must be sent now, and our own address must be turned away again");
            Check(form.EndpointsToArmForTest([relay]).Length == 0, "and muted, an unanswering server is dropped as before");
            form.PeerHealthForTest = null;

            // Its global hotkey (Ed, 2026-10-01: "the unmute myself button should have a global hot key"): the button's own toggle.
            var hotkeys = Require(FieldOf<MainFormHotkeyController>(main, "hotkeyController"), "the window's hotkey controller");
            var hotkey = Require(hotkeys.ToggleHearMyself, "the unmute-myself key's action must be wired");
            hotkey();
            Check(form.RelayGroupForTest.HearsItself && Plain() == "Mute myself (Alt+S)"
                  && said.Any(l => l.Contains("ui: unmute myself to test round trip → on (the hotkey) [", StringComparison.Ordinal)),
                "THE HOTKEY: it must unmute as the button does, and the log must say it was the hotkey");
            hotkey();
            Check(!form.RelayGroupForTest.HearsItself && said.Any(l => l.Contains("hear myself: muted - you muted yourself (the hotkey)", StringComparison.Ordinal)),
                "and mute again");

            // Headless: the control channel presses it as a person does, by the name a screen reader reads.
            server = new RemoteControlServer(pipe, () => main, new RemoteControlEngine(() => main));
            server.Start();
            string Ask(string command)
            {
                var reply = RemoteControl.Send(command, pipe, out var reached, connectTimeoutMs: 5000);
                Check(reached, $"the control channel did not answer \"{command}\": {reply}");
                return reply;
            }
            DriveWhilePumping(main, () =>
            {
                Ask("click \"Unmute myself to test round trip on server\"");
                Check(WaitUntil(() => main.RelayGroupForTest.HearsItself, 3000), "HEADLESS: clicking it through the control channel must unmute");
                Ask("click \"Mute myself\"");
                Check(WaitUntil(() => !main.RelayGroupForTest.HearsItself, 3000), "and clicking it again must mute");
            }, TimeSpan.FromSeconds(30));

            form.ToggleHearMyselfForTest();
            form.DisconnectFromRelayForTest();
            Check(!form.RelayGroupForTest.HearsItself && Plain().StartsWith("Unmute", StringComparison.Ordinal), "LEAVING THE SERVER must mute it");
            Check(!IsSetVisibleForAltAudit(button), "and the button must go with the server");
            Check(said.Any(l => l.Contains("hear myself: muted - you left the server", StringComparison.Ordinal)), "and the log must say so");

            form.ConnectToRelayForTest("relay.example.test", relay);
            form.ToggleHearMyselfForTest();
            form.TickHearMyselfForTest();
            Check(form.RelayGroupForTest.HearsItself, "a second with nothing back yet must not give up");
            form.BackdateHearMyselfForTest(MainForm.HearMyselfNothingBackAfter);
            form.TickHearMyselfForTest();
            Check(!form.RelayGroupForTest.HearsItself && Plain().StartsWith("Unmute", StringComparison.Ordinal),
                "NOTHING BACK: a server that sends nothing back in time must be taken as one that doesn't, and we must be muted");
            Check(said.Any(l => l.Contains("nothing came back from the server", StringComparison.Ordinal)), "and the log must say so");

            // It came back, then stopped: muted again after the same wait, saying it stopped.
            form.ToggleHearMyselfForTest();
            form.HearMyselfMeterForTest.NoteArrival(1, 1, 1);
            form.TickHearMyselfForTest();
            form.BackdateHearMyselfForTest(MainForm.HearMyselfNothingBackAfter - TimeSpan.FromSeconds(1));
            form.TickHearMyselfForTest();
            Check(form.RelayGroupForTest.HearsItself, "sound that came back 4 s ago must not mute us yet");
            form.BackdateHearMyselfForTest(TimeSpan.FromSeconds(1));
            form.TickHearMyselfForTest();
            Check(!form.RelayGroupForTest.HearsItself && said.Any(l => l.Contains("your sound stopped coming back from the server", StringComparison.Ordinal)),
                "STOPPED: our sound no longer coming back must mute us, and the log must say it stopped");

            form.HearMyselfWarningAnswered(wentOn: true, dontShowAgain: false);
            Check(AppConfig.Load().ShowHearMyselfWarning, "OK without the tick must leave the warning on");
            form.HearMyselfWarningAnswered(wentOn: true, dontShowAgain: true);
            Check(!AppConfig.Load().ShowHearMyselfWarning, "THE TICK: Do not show me this message again must hide it");
            Check(said.Any(l => l.Contains("the loopback warning is hidden from now on", StringComparison.Ordinal)), "and the log must say so");
            form.LogForTest.EventTapForTest = null;
        }
        finally
        {
            try { server?.Dispose(); } catch { /* teardown */ }
            try { form?.Dispose(); } catch { /* teardown */ }
            CuePlayer.GloballyMuted = restoreMuted;
        }
        return "the warning is about loopback, headphones and feedback, with Ed's tick, OK and Cancel; the button starts muted, "
             + "Alt+S, on Audio profile straight after Total latency; no server, nothing sent or Receive audio off each keep it "
             + "muted and say why; unmuted, our sound goes to the server and our echo is let in; muted, neither; the control "
             + "channel presses it both ways; leaving the "
             + "server or nothing back mutes it; the tick hides the warning";
    }
}
