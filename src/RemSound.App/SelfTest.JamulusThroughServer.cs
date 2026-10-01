using System.Net;
using RemSound.Core;
using RemSound.Sender;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>JAMULUS-STYLE THROUGH THE SERVER (2026-09-29, Ed: "build a test copy which uses 64 opus and the metronome and
    /// put it on andre's server"). The real relay and two apps that can both decode it, each sending as the app sends - one
    /// copy to the server's own address, which it passes on - and each learning from the other's own packets that it can
    /// decode it, as the app does: the ASIO lane's 64-sample stream goes through the server to the other and is taken as
    /// that. A third on the server who cannot decode it changes nothing until it ticks us back - the server passes it
    /// nothing before - and then the stream goes back to the ordinary one for everyone there, since the server passes one
    /// stream to all. And with a phone paired there, the same. (Live test 2026-09-29: this used to send to each member's
    /// own address, which the app never does - and the app, asking it of the server's address, never sent it at all.)</summary>
    private static string? JamulusStyleThroughTheServer()
    {
        // REMSOUND_TEST_LIVE_RELAY=host:port: through a real relay instead, the first part only - never in the gate.
        var liveText = Environment.GetEnvironmentVariable("REMSOUND_TEST_LIVE_RELAY");
        var live = string.IsNullOrWhiteSpace(liveText) ? null : IPEndPoint.Parse(liveText.Trim());
        var root = FindSourceRoot();
        if (root is null && live is null) return Skip("the source tree is not reachable (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var python = FindPython();
        if (python is null && live is null) return Skip("there is no Python on this machine to run the relay (py.exe, or a python.exe outside WindowsApps)");
        OpusCustomNative.TryLoad();
        if (!OpusCustomNative.Available) return Skip($"the Jamulus-style Opus library is not here ({OpusCustomNative.LoadMessage})");

        var restorePlays = JamulusStyle.PlaysThroughAsio;
        var port = FreeUdpPort();
        var logDir = Path.Combine(Path.GetTempPath(), $"remsound-relay-j64-{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "relay.log");
        var relayProcess = live is null ? StartRelay(python!, Path.Combine(root!, "server", "remsound-relay.py"), port, logPath) : null;
        var apps = new List<RelayTestApp>();
        using var stopFeeding = new CancellationTokenSource();
        Thread? feeder = null;
        // Who is playing: each sender and whether it is fed on its ASIO lane (the instrument) or its other lane (a mic).
        var talking = Array.Empty<(AudioSender Sender, bool Asio)>();
        try
        {
            if (relayProcess is not null)
                Check(WaitUntil(() => ReadShared(logPath).Contains("event=startup"), 10000) && !relayProcess.HasExited, $"the relay must start under {python}");
            var relay = live ?? new IPEndPoint(IPAddress.Loopback, port);
            // Every copy here plays through ASIO, so each says it can take the frames (one process: one JamulusStyle). The
            // older app and the phone below never send a Format packet or a flagged heartbeat, so they never say so.
            JamulusStyle.PlaysThroughAsio = true;
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
            }) { IsBackground = true, Name = "self-test Jamulus-style through the server" };
            feeder.Start();

            var password = live is null ? "jamming" : $"remsound self-test {Guid.NewGuid():N}";   // on a real relay, a group of its own
            var ed = new RelayTestApp("Ed", password, relay, groups: true);
            var andre = new RelayTestApp("Andre", password, relay, groups: true);
            apps.AddRange([ed, andre]);
            Check(WaitUntil(() => ed.Group!.Members.Count == 1 && andre.Group!.Members.Count == 1, 15000), "both must join the server's group and see each other");
            var andreSeenByEd = ed.Group!.Members.Single();
            var edSeenByAndre = andre.Group!.Members.Single();
            // Ticking sends as the app sends (MainForm.SendTargets): one copy, to the server. Andre plays his mic on the
            // other lane (Andre, live test 2026-09-29: "both asio and wasapi. wasapi only for his mic").
            ed.Tick([andreSeenByEd.Id]);
            andre.Tick([edSeenByAndre.Id]);
            // Opus, as a new profile sends: the Jamulus-style frames take the place of Opus (a PCM choice stays PCM).
            ed.Sender.ConfigureCodec(AudioTransportCodec.Opus, 120);
            andre.Sender.ConfigureCodec(AudioTransportCodec.Opus, 120);
            ed.Sender.UseAsioLaneForTest();
            andre.Sender.UseDefaultLaneForTest(RenderRoute.WasapiLane);
            Volatile.Write(ref talking, [(ed.Sender, true), (andre.Sender, false)]);
            bool Hears(AudioTransportCodec codec) => andre.Receiver.SessionCodecsForTest.Any(c => c.From.Equals(edSeenByAndre.Address) && c.Codec == codec);
            Check(WaitUntil(() => Hears(AudioTransportCodec.OpusCustom), 8000),
                $"THROUGH THE SERVER: both able to decode it, Ed's ASIO lane must reach Andre as the Jamulus-style stream ({Codecs(andre)}; {ed.Sender.CustomOpusTargetCount} target(s))");
            Check(ed.Sender.CustomOpusTargetCount == 1, $"with the server - Andre's way in - its one target ({ed.Sender.CustomOpusTargetCount})");
            if (live is not null)
                return $"THROUGH THE REAL SERVER {live}: sending as the app sends, each learning from the other's packets that it can decode it, "
                     + $"Ed's ASIO lane reached Andre as the Jamulus-style stream ({Codecs(andre)})";

            // Somebody who cannot decode it joins, and Ed ticks everyone: until they tick Ed back the server passes them
            // nothing of his, so nothing changes; then the one stream it passes to all goes back to the ordinary one.
            var old = new RelayTestApp("Older app", password, relay, groups: true);
            apps.Add(old);
            Check(WaitUntil(() => ed.Group.Members.Count == 2 && old.Group!.Members.Count == 2, 10000), "a third must join the group");
            ed.Tick(ed.Group.Members.Select(m => m.Id));
            Check(!WaitUntil(() => ed.Sender.CustomOpusTargetCount == 0, 1500),
                "NOT YET A LISTENER: somebody who has not ticked Ed back is passed nothing of his, so the Jamulus-style stream must carry on");
            old.Tick([old.Group!.Members.Single(m => m.Name == "Ed").Id]);
            Check(WaitUntil(() => ed.Sender.CustomOpusTargetCount == 0, 5000),
                $"EVERYONE OR NOBODY: once somebody who cannot decode it hears Ed through the server, nobody there must be sent it ({ed.Sender.CustomOpusTargetCount} target(s))");
            Check(WaitUntil(() => Hears(AudioTransportCodec.Opus) || Hears(AudioTransportCodec.Pcm), 5000),
                $"and Andre must be sent the ordinary stream instead ({Codecs(andre)})");

            // A phone paired with Ed there: the server passes Ed's sound to it too, in the form a phone reads - so with only
            // RemSounds that can decode it in the group, the Jamulus-style stream must still stop.
            Volatile.Write(ref talking, [(ed.Sender, true)]);
            foreach (var gone in new[] { old, andre }) { gone.Dispose(); apps.Remove(gone); }
            Check(WaitUntil(() => ed.Group.Members.Count == 0, 10000), $"the others must leave the group ({ed.Group.Members.Count} left)");
            var phone = new RelayTestApp("Phone", password, relay, groups: false);
            apps.Add(phone);
            Check(WaitUntil(() => ed.Group.IsV1Paired(relay), 10000), "a phone arriving must pair with Ed, the one member there");
            var carol = new RelayTestApp("Carol", password, relay, groups: true);
            apps.Add(carol);
            Check(WaitUntil(() => ed.Group.Members.Count == 1 && carol.Group!.Members.Count == 1, 10000), "Carol must join Ed's group");
            var carolSeenByEd = ed.Group.Members.Single();
            var edSeenByCarol = carol.Group!.Members.Single();
            ed.Tick([carolSeenByEd.Id]);
            carol.Tick([edSeenByCarol.Id]);
            carol.Sender.UseDefaultLaneForTest(RenderRoute.WasapiLane);
            Volatile.Write(ref talking, [(ed.Sender, true), (carol.Sender, false)]);
            Check(WaitUntil(() => CustomOpusPeers.CanDecode(carolSeenByEd.Address.Address), 8000), "Ed must learn from Carol's own packets that she can decode it");
            bool CarolHears(AudioTransportCodec codec) => carol.Receiver.SessionCodecsForTest.Any(c => c.From.Equals(edSeenByCarol.Address) && c.Codec == codec);
            Check(WaitUntil(() => CarolHears(AudioTransportCodec.Opus) || CarolHears(AudioTransportCodec.Pcm), 8000)
                  && ed.Sender.CustomOpusTargetCount == 0 && !CarolHears(AudioTransportCodec.OpusCustom),
                $"A PHONE PAIRED: with a phone taking Ed's sound through the server, nobody there must be sent the Jamulus-style stream "
                + $"({ed.Sender.CustomOpusTargetCount} target(s); Carol: {Codecs(carol)})");
            return "sending as the app sends, and each learning from the other's packets that it can decode it, the Jamulus-style "
                 + "stream went through the server; a third who could not changed nothing until it ticked back, then nobody on the "
                 + "server was sent it and the ordinary stream went instead; with a phone paired there, the same";
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

        static string Codecs(RelayTestApp app) => string.Join(", ", app.Receiver.SessionCodecsForTest.Select(c => $"{c.From} {c.Codec}"));
    }
}
