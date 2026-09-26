using System.Net;
using System.Net.Sockets;
using RemSound.Core;
using RemSound.Plugin;
using RemSound.Receiver;
using RemSound.Sender;

namespace RemSound.App;

/// <summary>
/// THE SEND DIRECTION — a DAW track going out to the peers.
///
/// <para>The receive direction had a whole suite before it worked; this one had none, because until
/// now the app dropped every track block on the floor. The thing that makes this direction risky is
/// not the audio, it is the third concurrent stream: a machine that used to emit at most two now
/// emits three, and the peers that have to cope with that are an iPhone, an Android phone and a
/// Python relay, none of which can be rebuilt to match. So the lane assignment is asserted here
/// rather than reasoned about, and the wire is inspected at the socket rather than from state.</para>
///
/// <para>Every step is bounded and offline: real sockets, but only on loopback, and no audio device
/// is ever opened.</para>
/// </summary>
internal static partial class SelfTest
{
    // ---------------------------------------------------------------------------------------
    // A UDP sink standing in for a peer. Records whole datagrams so a step can assert what
    // actually left the machine, rather than what the sender's own counters say it did.
    // ---------------------------------------------------------------------------------------
    private sealed class WireSink : IDisposable
    {
        private readonly UdpClient socket;
        private readonly Thread thread;
        private readonly object gate = new();
        private readonly List<byte[]> packets = [];
        private volatile bool running = true;

        public IPEndPoint Endpoint { get; }

        public WireSink()
        {
            socket = new UdpClient(0, AddressFamily.InterNetwork);
            Endpoint = new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)socket.Client.LocalEndPoint!).Port);
            socket.Client.ReceiveTimeout = 200;
            thread = new Thread(Loop) { IsBackground = true, Name = "SelfTest.WireSink" };
            thread.Start();
        }

        private void Loop()
        {
            var any = new IPEndPoint(IPAddress.Any, 0);
            while (running)
            {
                try
                {
                    var data = socket.Receive(ref any);
                    lock (gate) packets.Add(data);
                }
                catch (SocketException) { /* timeout, or the socket closing under us */ }
                catch (ObjectDisposedException) { return; }
            }
        }

        public int Count { get { lock (gate) return packets.Count; } }

        public byte[][] Snapshot() { lock (gate) return packets.ToArray(); }

        public void Clear() { lock (gate) packets.Clear(); }

        public void Dispose()
        {
            running = false;
            try { socket.Dispose(); } catch { /* closing is what wakes the thread */ }
            try { thread.Join(500); } catch { /* best-effort */ }
        }
    }

    /// <summary>Every Lane byte announced in the format packets this sink collected, with the stream
    /// id that announced it. Reading the WIRE, not the sender's fields: the lane byte is the whole of
    /// the backward-compatibility argument, and a field agreeing with itself proves nothing.</summary>
    private static List<(RenderRoute Lane, ushort StreamId)> FormatAnnouncements(WireSink sink)
    {
        var found = new List<(RenderRoute, ushort)>();
        foreach (var packet in sink.Snapshot())
        {
            if (!RemPacket.TryReadHeader(packet, out var type, out var streamId, out _)) continue;
            if (type != RemPacketType.Format) continue;
            if (!RemPacket.TryReadFormat(packet.AsSpan(RemPacket.HeaderSize), out var format, out _)) continue;
            found.Add((format.Lane, streamId));
        }
        return found;
    }

    private static int AudioPacketCount(WireSink sink)
    {
        var count = 0;
        foreach (var packet in sink.Snapshot())
        {
            if (RemPacket.TryReadHeader(packet, out var type, out _, out _) && type == RemPacketType.Audio) count++;
        }
        return count;
    }

    /// <summary>A block of interleaved stereo at a fixed level, for the steps that only care whether
    /// audio arrived and at what amplitude.</summary>
    private static float[] Block(int frames, float amplitude)
    {
        var block = new float[frames * 2];
        for (var i = 0; i < block.Length; i++) block[i] = amplitude;
        return block;
    }

    /// <summary>Arm a sender against a sink: a password (mandatory encryption means no key, no audio)
    /// and one destination.</summary>
    private static void ArmSender(AudioSender sender, WireSink sink, string password = "gate-password")
    {
        var (key, fingerprint) = RemSoundCrypto.ForPlainPassword(password);
        sender.AudioKey = key;
        sender.AudioFingerprint = fingerprint;
        sender.SetReceivers([sink.Endpoint]);
    }

    // ---------------------------------------------------------------------------------------
    // 1. The lane the plugin announces, in every mode the app can reach
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The plugin lane must never share a Lane byte with a capture lane that is actually emitting.
    ///
    /// <para>This is the one decision in the whole feature that a peer can see, and getting it wrong
    /// is not a subtle fault: two live streams from one endpoint sharing a lane byte supersede each
    /// other on every receiver, four times a second, and BOTH go silent. That is the exact failure
    /// the lane-match qualifier was added for on 2026-05-11, and it would come back as "the plugin
    /// killed my microphone".</para>
    ///
    /// <para>Asserted at the socket. The sender's own <c>Route</c> properties are checked too, but
    /// they are the thing under test, so what a peer would actually parse out of the format payload
    /// is checked as well.</para>
    /// </summary>
    private static string PluginLaneNeverCollides()
    {
        using var sink = new WireSink();
        using var sender = new AudioSender();
        ArmSender(sender, sink);

        // --- WasapiOnly, the startup mode -------------------------------------------------------
        Check(sender.DefaultLaneRouteForTest == RenderRoute.Mixed,
            $"WasapiOnly must keep announcing Mixed on the capture lane - changing that is a peer-visible change (got {sender.DefaultLaneRouteForTest})");
        Check(sender.PluginLaneRouteForTest == RenderRoute.AsioLane,
            $"in WasapiOnly the plugin lane must take AsioLane, the value no capture lane is emitting (got {sender.PluginLaneRouteForTest})");
        Check(sender.PluginLaneRouteForTest != sender.DefaultLaneRouteForTest,
            "the plugin lane must never share a lane byte with the lane that IS emitting");

        sender.SetPluginSendActive(true);
        sender.SubmitPluginBlock(Block(480, 0.5f));
        Check(WaitUntil(() => sink.Count > 0),
            "a block submitted to an armed plugin lane must actually reach the socket");
        var announcements = FormatAnnouncements(sink);
        Check(announcements.Count > 0, "the plugin lane must announce its format before its audio");
        Check(announcements.All(a => a.Lane == RenderRoute.AsioLane),
            $"the format packet ON THE WIRE must carry AsioLane in WasapiOnly (got {string.Join(",", announcements.Select(a => a.Lane))})");
        Check(announcements.All(a => a.StreamId == sender.PluginLaneStreamIdForTest),
            "and carry the plugin lane's own stream id, so the receiver keys it as its own session");

        // --- BothIndependent, where both capture lanes are live ----------------------------------
        sink.Clear();
        sender.SetAudioMode(AudioMode.BothIndependent, "RemSound self-test - no such ASIO driver");
        Check(sender.DefaultLaneRouteForTest == RenderRoute.WasapiLane, "BothIndependent puts WasapiLane on the WASAPI capture lane");
        Check(sender.AsioLaneRouteForTest == RenderRoute.AsioLane, "and AsioLane on the ASIO capture lane");
        Check(sender.PluginLaneRouteForTest == RenderRoute.Mixed,
            $"which leaves Mixed as the only free value for the plugin (got {sender.PluginLaneRouteForTest})");
        Check(sender.PluginLaneRouteForTest != sender.DefaultLaneRouteForTest
              && sender.PluginLaneRouteForTest != sender.AsioLaneRouteForTest,
            "all three lanes must hold three DIFFERENT values in BothIndependent - that is what lets the three sessions coexist");

        sender.SubmitPluginBlock(Block(480, 0.5f));
        Check(WaitUntil(() => FormatAnnouncements(sink).Count > 0), "the plugin lane must re-announce after a mode change");
        Check(FormatAnnouncements(sink).All(a => a.Lane == RenderRoute.Mixed),
            "the format packet on the wire must carry Mixed in BothIndependent");

        // --- No fourth value, ever ---------------------------------------------------------------
        // Both mobile ports clamp an unknown lane byte to Mixed for forward compatibility, so a
        // fourth value would not route anywhere new - it would collide with Mixed on every phone.
        foreach (var (lane, _) in FormatAnnouncements(sink))
        {
            Check(lane is RenderRoute.Mixed or RenderRoute.WasapiLane or RenderRoute.AsioLane,
                $"only the three existing RenderRoute values may ever appear on the wire (saw {(int)lane})");
        }

        return "plugin lane takes AsioLane in WasapiOnly and Mixed in BothIndependent, never colliding with a live capture lane, "
             + "and the wire carries exactly that";
    }

    // ---------------------------------------------------------------------------------------
    // 2. An idle plugin lane is completely silent
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// With nothing sending, the plugin lane must put NOTHING on the wire — not one audio packet and
    /// not one format announce.
    ///
    /// <para>It matters because the format announce is what opens a session at the far end. A third
    /// lane that announced itself while idle would make every peer carry a permanently-empty extra
    /// session, and on a phone that is a real cost for audio nobody is sending. The guarantee comes
    /// from <c>EnsureFormatPacketSent</c> living inside <c>OnMixedSamples</c>, which is easy to break
    /// by moving one line, so it is asserted at the socket rather than assumed.</para>
    /// </summary>
    private static string IdlePluginLaneIsSilent()
    {
        using var sink = new WireSink();
        using var sender = new AudioSender();
        ArmSender(sender, sink);

        // Armed but never fed. Give it well over the 250 ms format-resend interval, which is the
        // period a lane that announced on a timer instead of on audio would fire in.
        sender.SetPluginSendActive(true);
        Thread.Sleep(600);
        Check(sink.Count == 0,
            $"an armed but unfed plugin lane must emit nothing at all, not even a format announce (got {sink.Count} packets)");

        // And the same when it was never armed - a stray block must not open a stream either.
        sender.SetPluginSendActive(false);
        sender.SubmitPluginBlock(Block(480, 0.5f));
        Thread.Sleep(100);
        Check(sink.Count == 0, $"a block submitted while the lane is disarmed must not reach the wire (got {sink.Count})");

        // The counter-proof: the same sink, the same sender, one armed block. Without this the step
        // above would pass just as well against a sender that cannot send anything at all.
        sender.SetPluginSendActive(true);
        for (var i = 0; i < 5; i++) sender.SubmitPluginBlock(Block(480, 0.5f));
        Check(WaitUntil(() => sink.Count > 0), "and feeding it must produce packets, or this step proved nothing");

        return $"idle lane emitted 0 packets over 600 ms and 0 while disarmed; {sink.Count} once fed";
    }

    // ---------------------------------------------------------------------------------------
    // 3. Mode, codec and send-rate changes reach the plugin lane
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Every setting that rotates the capture lanes' stream ids must rotate the plugin lane's too,
    /// exactly once, and a change that changes nothing must rotate nothing.
    ///
    /// <para>A lane whose stream id did NOT rotate on a format change would keep the receiver's
    /// existing session open and start feeding it packets in a format it was never told about —
    /// which decodes as noise, not as silence.</para>
    /// </summary>
    private static string PluginLaneFollowsFormatChanges()
    {
        using var sender = new AudioSender();

        var before = sender.PluginLaneStreamIdForTest;
        var captureBefore = sender.DefaultLaneStreamIdForTest;
        sender.SetAudioMode(AudioMode.BothIndependent, "RemSound self-test - no such ASIO driver");
        var afterMode = sender.PluginLaneStreamIdForTest;
        Check(afterMode != before, "an audio-mode change must rotate the plugin lane's stream id - its lane byte just changed");
        Check(sender.DefaultLaneStreamIdForTest != captureBefore, "and the capture lane's, as it always did");

        // Idempotence. SetRoute is a no-op when the route is unchanged, so re-applying the same mode
        // must not churn the stream id: every rotation costs the receiver a fresh session, and the
        // app re-applies its audio runtime once a second.
        sender.SetAudioMode(AudioMode.BothIndependent, "RemSound self-test - no such ASIO driver");
        Check(sender.PluginLaneStreamIdForTest == afterMode,
            "re-applying the SAME mode must not rotate the plugin lane again - the app does this every second");

        var beforeCodec = sender.PluginLaneStreamIdForTest;
        sender.ConfigureCodec(AudioTransportCodec.Opus, 480);
        Check(sender.PluginLaneStreamIdForTest != beforeCodec, "a codec change must rotate the plugin lane's stream id");

        var beforeRate = sender.PluginLaneStreamIdForTest;
        sender.SetSendRate(SendRate.Tight);
        Check(sender.PluginLaneStreamIdForTest != beforeRate, "a send-rate change must rotate the plugin lane's stream id");

        // Arming rotates too, so a lane coming back after a gap never lands on a session the
        // receiver had already pruned.
        var beforeArm = sender.PluginLaneStreamIdForTest;
        sender.SetPluginSendActive(true);
        Check(sender.PluginLaneStreamIdForTest != beforeArm, "arming the lane must rotate its stream id");
        var armed = sender.PluginLaneStreamIdForTest;
        sender.SetPluginSendActive(true);
        Check(sender.PluginLaneStreamIdForTest == armed, "arming an already-armed lane must change nothing");

        return "mode, codec, send-rate and arming each rotate the plugin lane's stream id once; a repeat of any of them rotates nothing";
    }

    // ---------------------------------------------------------------------------------------
    // 4. End to end: a DAW block reaches a real receiver as audible audio
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A block handed to <see cref="PluginTrackSource"/> must come out of a real
    /// <see cref="AudioReceiver"/> as decodable audio at the level it went in.
    ///
    /// <para>The whole path, with nothing stubbed: the source elects a driver and arms the lane, the
    /// lane encodes and encrypts, the socket carries it to a receiver on loopback, the receiver
    /// decrypts, decodes and buffers it, and the audio is read back out through the very call the
    /// plugin's receive side uses. Peak in, peak out.</para>
    ///
    /// <para>Also the proof that plugin audio reaches EVERY armed peer, not just the first: two
    /// receivers, both fed from the one lane.</para>
    /// </summary>
    private static string PluginSendEndToEnd()
    {
        int PickPort()
        {
            using var probe = new UdpClient(0, AddressFamily.InterNetwork);
            return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        }

        var portA = PickPort();
        var portB = PickPort();
        using var receiverA = new AudioReceiver();
        using var receiverB = new AudioReceiver();
        try { receiverA.Start(portA); receiverB.Start(portB); }
        catch (Exception ex) { return Skip($"could not bind loopback test ports: {ex.Message}"); }

        var (key, fingerprint) = RemSoundCrypto.ForPlainPassword("gate-password");
        foreach (var receiver in new[] { receiverA, receiverB })
        {
            receiver.AudioKey = key;
            receiver.AudioFingerprint = fingerprint;
            receiver.SetOutputDevices(Array.Empty<string>());
            receiver.SetPlaybackEnabled(true);
        }

        using var sender = new AudioSender();
        sender.AudioKey = key;
        sender.AudioFingerprint = fingerprint;
        // Tight PCM keeps every encrypted frame inside one datagram, so this step tests the send path
        // rather than the multipart reassembler, which has its own suite.
        sender.SetSendRate(SendRate.Tight);
        sender.SetReceivers([new IPEndPoint(IPAddress.Loopback, portA), new IPEndPoint(IPAddress.Loopback, portB)]);

        // Through the REAL link, wired exactly as MainForm wires it. Calling OnTrackBlock directly
        // would skip the one line that was missing all along - the subscription - and that line is
        // the whole feature.
        using var bridgeHost = new PluginBridgeHost(readPeer: null, port: 0);
        using var source = new PluginTrackSource(sender);
        bridgeHost.TrackAudioReceived += source.OnTrackBlock;
        using var pluginClient = new PluginBridgeClient(bridgeHost.Port);
        pluginClient.Hello();
        Check(WaitUntil(() => pluginClient.Connected), "the plugin must reach the app before any of this means anything");

        // A tone rather than a constant: a constant would survive almost any mistake in the packing,
        // and the point is that what comes out is the audio that went in.
        const int Frames = 240;
        var block = new float[Frames * 2];
        var phase = 0.0;
        void FillTone()
        {
            for (var i = 0; i < block.Length; i += 2)
            {
                var s = (float)(0.5 * Math.Sin(phase));
                phase += 2 * Math.PI * 440 / 48000;
                block[i] = s;
                block[i + 1] = s;
            }
        }

        // Half a second of audio, submitted at roughly real time so the receiver's jitter buffer
        // fills the way it would in a session rather than in one burst.
        for (var i = 0; i < 100; i++)
        {
            FillTone();
            pluginClient.SendTrackBlock(block);
            Thread.Sleep(2);
        }

        Check(sender.IsPluginSending, "the first block from a DAW must arm the plugin lane on its own - there is no second switch");
        Check(WaitUntil(() => source.BlocksSubmitted >= 90),
            $"the DAW's blocks must cross the bridge and reach the lane (got {source.BlocksSubmitted} of 100 - loopback UDP may lose one or two, but not most)");
        Check(source.BlocksSummed == 0, "with one DAW connected nothing may go through the summing path - that is the zero-drift case");

        float PeakFrom(AudioReceiver receiver)
        {
            var peak = 0f;
            var scratch = new float[Frames * 2];
            for (var round = 0; round < 120; round++)
            {
                var got = receiver.ReadClaimedPeer(IPAddress.Loopback, scratch, Frames);
                for (var i = 0; i < got * 2; i++)
                {
                    var a = Math.Abs(scratch[i]);
                    if (a > peak) peak = a;
                }
                if (peak > 0.3f) break;
                Thread.Sleep(4);
            }
            return peak;
        }

        var peakA = PeakFrom(receiverA);
        Check(peakA > 0.3f, $"the DAW track must arrive at the receiver as audio, not silence (peak {peakA:0.000})");
        Check(peakA < 0.75f, $"and at its own level, not doubled or clipped (peak {peakA:0.000}, sent 0.5)");

        var peakB = PeakFrom(receiverB);
        Check(peakB > 0.3f, $"plugin audio must reach EVERY armed peer, not only the first (second peer peak {peakB:0.000})");

        Check(receiverA.SessionsOpenedCount == 1,
            $"the plugin lane must open exactly one session, not churn through several ({receiverA.SessionsOpenedCount})");
        Check(receiverA.LiveSessionCountForTest == 1,
            $"and leave exactly one live ({receiverA.LiveSessionCountForTest})");

        return $"a 440 Hz DAW track at 0.5 arrived at two peers at {peakA:0.000} and {peakB:0.000}, on one session each";
    }

    // ---------------------------------------------------------------------------------------
    // 5. Sending with no capture device ticked
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The plugin lane must work with nothing ticked in the app — no microphone, no loopback, no ASIO.
    ///
    /// <para>This is the requirement the whole design is shaped around: "it still needs to work when
    /// no other sound cards have been checked". Two early returns stood in the way — the sender's own
    /// (no pending sources, so <c>ResetForStart</c> never ran) and the mixer's (no specs, so the mix
    /// task never started). The fix was to separate the lifecycles, and this asserts the separation:
    /// the lane emits, and the capture engine stays down.</para>
    /// </summary>
    private static string PluginSendsWithNoCaptureDevice()
    {
        using var sink = new WireSink();
        using var sender = new AudioSender();
        ArmSender(sender, sink);

        // Exactly the state the app is in with "Send my audio" on and nothing ticked.
        sender.Configure([]);
        sender.Start();
        Check(!sender.IsRunning, "with no capture source configured the capture engine must stay down - that is existing behaviour and must not change");

        using var source = new PluginTrackSource(sender);
        var daw = Guid.NewGuid();
        for (var i = 0; i < 20; i++) source.OnTrackBlock(daw, Block(240, 0.4f));

        Check(WaitUntil(() => AudioPacketCount(sink) > 0),
            "the plugin lane must put audio on the wire with NO capture device ticked - anything less makes the plugin depend on a setting nobody told the user about");
        Check(!sender.IsRunning,
            "and it must do so without starting the capture engine: a mix loop with nothing feeding it is a tick of latency and a source of jitter for no audio");

        var announcements = FormatAnnouncements(sink);
        Check(announcements.Count > 0 && announcements.All(a => a.Lane == RenderRoute.AsioLane),
            "the lane byte must still be the plugin's own, capture engine or no capture engine");

        return $"{AudioPacketCount(sink)} audio packets left the socket with no capture device ticked and the mixer never started";
    }

    // ---------------------------------------------------------------------------------------
    // 6. Mute, recording and mandatory encryption apply to the plugin lane too
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The plugin lane must obey the same three house rules as every other lane: no password means
    /// nothing goes out, mute means silence goes out, and what goes out is recorded.
    ///
    /// <para>All three come for free from <c>SenderLane</c>, which is exactly why they are worth
    /// asserting: a future refactor that gave the plugin lane its own emit path would lose all three
    /// at once, and the loss of the first is an audio leak.</para>
    /// </summary>
    private static string PluginLaneObeysTheHouseRules()
    {
        using var sink = new WireSink();
        using var sender = new AudioSender();
        sender.SetReceivers([sink.Endpoint]);
        sender.SetSendRate(SendRate.Tight);
        sender.SetPluginSendActive(true);

        // --- No password, nothing sent ------------------------------------------------------------
        sender.AudioKey = null;
        sender.AudioFingerprint = null;
        for (var i = 0; i < 10; i++) sender.SubmitPluginBlock(Block(240, 0.5f));
        Thread.Sleep(100);
        Check(AudioPacketCount(sink) == 0,
            $"with no password the plugin lane must send NO audio - mandatory encryption is not per-lane ({AudioPacketCount(sink)} packets)");

        // --- Recording tap ------------------------------------------------------------------------
        var (key, fingerprint) = RemSoundCrypto.ForPlainPassword("gate-password");
        sender.AudioKey = key;
        sender.AudioFingerprint = fingerprint;

        var recordedRoutes = new List<RenderRoute>();
        var recordedPeak = 0f;
        sender.OnSentSamples = (samples, route) =>
        {
            lock (recordedRoutes)
            {
                recordedRoutes.Add(route);
                foreach (var s in samples.Span) { var a = Math.Abs(s); if (a > recordedPeak) recordedPeak = a; }
            }
        };
        for (var i = 0; i < 10; i++) sender.SubmitPluginBlock(Block(240, 0.5f));
        lock (recordedRoutes)
        {
            Check(recordedRoutes.Count >= 10, $"the recorder tap must see the plugin lane's audio ({recordedRoutes.Count} blocks)");
            Check(recordedRoutes.All(r => r == RenderRoute.AsioLane),
                "and see it tagged with the plugin lane's own route, so a split recording keeps it on its own track");
            Check(recordedPeak > 0.4f, $"the recording must carry the real signal, not silence ({recordedPeak:0.000})");
        }
        sender.OnSentSamples = null;
        Check(WaitUntil(() => AudioPacketCount(sink) > 0), "with a password set the same blocks must now reach the wire");

        // --- Mute -----------------------------------------------------------------------------------
        // Mute must reach the WIRE, not merely stop the encoder. Packets keep flowing (that is what
        // keeps the far end's session open) and they must contain silence.
        var loudPacket = LargestAudioPayload(sink);
        sink.Clear();
        sender.IsMuted = true;
        for (var i = 0; i < 10; i++) sender.SubmitPluginBlock(Block(240, 0.5f));
        Check(WaitUntil(() => AudioPacketCount(sink) > 0), "a muted lane keeps its stream alive - the packets must still flow");
        var mutedPayload = DecryptedPeak(sink, key!);
        sender.IsMuted = false;
        Check(mutedPayload is >= 0f and < 0.001f,
            $"mute must silence the plugin lane's audio on the wire (decoded peak {mutedPayload:0.0000})");
        Check(loudPacket > 0.4f,
            $"and the same measurement on the UNMUTED audio must show the signal, or the check above proves nothing ({loudPacket:0.000})");

        return $"no password sent nothing; the recorder saw the lane tagged AsioLane at {recordedPeak:0.000}; mute put {mutedPayload:0.0000} on the wire";
    }

    /// <summary>Decrypt and decode the loudest sample in the audio packets a sink collected. The
    /// sender's own counters cannot answer "was it silence", and this is the same work the receiver
    /// does — decrypt, then unpack int24 — so it measures what a peer would actually hear.</summary>
    private static float DecryptedPeak(WireSink sink, byte[] key)
    {
        using var gcm = RemSoundCrypto.CreateGcm(key);
        var plain = new byte[8192];
        var floats = new float[4096];
        var peak = -1f;
        foreach (var packet in sink.Snapshot())
        {
            if (!RemPacket.TryReadHeader(packet, out var type, out _, out _) || type != RemPacketType.Audio) continue;
            var body = packet.AsSpan(RemPacket.HeaderSize);
            if (body.Length <= RemPcmFrame.SubHeaderSize) continue;
            if (!RemPcmFrame.TryReadSubHeader(body[..RemPcmFrame.SubHeaderSize], out _, out _, out var totalParts)) continue;
            // Single-part frames only: the step that calls this runs at the tight send rate for
            // exactly that reason, and a half a frame would decrypt to nothing anyway.
            if (totalParts != 1) continue;
            if (!RemSoundCrypto.TryDecryptInto(gcm, body[RemPcmFrame.SubHeaderSize..], plain, out var written)) continue;
            var samples = written / 3;
            if (samples == 0 || samples > floats.Length) continue;
            PcmPack.Int24LEToFloat(plain.AsSpan(0, samples * 3), floats.AsSpan(0, samples));
            if (peak < 0f) peak = 0f;
            for (var i = 0; i < samples; i++) { var a = Math.Abs(floats[i]); if (a > peak) peak = a; }
        }
        return peak;
    }

    private static float LargestAudioPayload(WireSink sink)
    {
        var (key, _) = RemSoundCrypto.ForPlainPassword("gate-password");
        return DecryptedPeak(sink, key!);
    }

    // ---------------------------------------------------------------------------------------
    // 7. Instances inside one DAW sum exactly, in the DAW's own process
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Two plugin instances on two tracks of one DAW must reach the app as ONE block that is their
    /// exact sum, with no offset between them.
    ///
    /// <para>Exactness is the whole reason the summing lives in the plugin. Instances there share an
    /// audio thread and block boundaries, so adding them is addition on aligned data. Summing them in
    /// the app instead would mean two block streams over a socket with a buffer each, putting one
    /// track a variable few milliseconds behind the other — which is not a latency figure, it is a
    /// wrong mix.</para>
    ///
    /// <para>Also: a stalled instance must cost only its own contribution. An instance that stops
    /// contributing would otherwise hold the round open and take every other track down with it.</para>
    /// </summary>
    private static string PluginInstancesSumExactly()
    {
        using var host = new PluginBridgeHost(readPeer: null, port: 0);
        var blocks = new List<float[]>();
        var senders = new List<Guid>();
        host.TrackAudioReceived += (id, audio) => { lock (blocks) { blocks.Add(audio.ToArray()); senders.Add(id); } };

        PluginSendBus.ResetForTest();
        using var first = new PluginBridgeClient(host.Port);
        using var second = new PluginBridgeClient(host.Port);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        try
        {
            // --- One instance: straight through, no accumulator ------------------------------------
            // Both instances say hello first, so the app can group them. They are in ONE process -
            // this one - so the app must hand both their audio to the send path under the SAME id.
            // Without that, a change of which instance carries the summed block reads as one DAW
            // leaving and another arriving, and the newcomer is handed a buffer and a resampler for
            // audio that came off the very same audio thread.
            first.Hello();
            second.Hello();
            Check(WaitUntil(() => first.Connected && second.Connected), "both instances must reach the app");

            PluginSendBus.Register(a, first);
            PluginSendBus.Submit(a, Block(64, 0.25f));
            Check(WaitUntil(() => { lock (blocks) return blocks.Count == 1; }),
                "one sending instance must reach the app with no summing involved");
            Guid firstSender;
            lock (blocks)
            {
                Check(Math.Abs(blocks[0][0] - 0.25f) < 1e-6f, $"and arrive unchanged (got {blocks[0][0]:0.0000})");
                firstSender = senders[0];
                blocks.Clear();
                senders.Clear();
            }

            // --- Two instances: one block, exact sum ------------------------------------------------
            PluginSendBus.Register(b, second);
            PluginSendBus.Submit(a, Block(64, 0.25f));
            Check(!WaitUntil(() => { lock (blocks) return blocks.Count > 0; }, timeoutMs: 120),
                "with two instances registered, the first must NOT flush on its own - the round is still open");
            PluginSendBus.Submit(b, Block(64, 0.5f));
            Check(WaitUntil(() => { lock (blocks) return blocks.Count == 1; }),
                "the second instance completing the round must flush exactly one block");
            lock (blocks)
            {
                var summed = blocks[0];
                Check(summed.Length == 64 * 2, $"the summed block must be one block long, not two appended ({summed.Length} floats)");
                Check(summed.All(v => Math.Abs(v - 0.75f) < 1e-6f),
                    $"and be the EXACT sum of the two tracks, sample for sample (first sample {summed[0]:0.0000}, expected 0.7500)");
                blocks.Clear();
            }

            // --- A stalled instance ------------------------------------------------------------------
            // b stops contributing. a keeps going: its second submission is proof the host moved on,
            // so the open round is flushed carrying only what it has.
            PluginSendBus.Submit(a, Block(64, 0.25f));
            PluginSendBus.Submit(a, Block(64, 0.25f));
            Check(WaitUntil(() => { lock (blocks) return blocks.Count >= 1; }),
                "a stalled instance must not stall the others - the next block from a live instance closes the round");
            lock (blocks)
            {
                Check(blocks[0].All(v => Math.Abs(v - 0.25f) < 1e-6f),
                    $"the flushed block carries the live instance's audio alone, at its own level (got {blocks[0][0]:0.0000})");
                blocks.Clear();
            }
            Check(PluginSendBus.PartialFlushes >= 1, "and the partial flush must be counted, so a stalling track is diagnosable");

            // --- Leaving --------------------------------------------------------------------------------
            // b switches to receiving. The bus must stop waiting for it, or every remaining track pays
            // a block of delay on every block, forever.
            // Leaving also completes the round the bus was still holding open for it. Stranding that
            // audio instead would drop a block of every OTHER track every time somebody switched a
            // plugin to receive.
            PluginSendBus.Unregister(b);
            Check(WaitUntil(() => { lock (blocks) return blocks.Count == 1; }),
                "an instance leaving must release the round the bus was holding for it, not strand it");
            lock (blocks) blocks.Clear();
            PluginSendBus.Submit(a, Block(64, 0.25f));
            Check(WaitUntil(() => { lock (blocks) return blocks.Count == 1; }),
                "and the remaining instance must flush on its own again");

            // Whichever instance actually carried a block, the app must have been told the same DAW
            // every time. This is the grouping the process id in Hello exists for.
            lock (blocks)
            {
                Check(senders.All(s => s == firstSender),
                    "every block from this process must reach the send path under one id, whichever instance carried it");
                Check(firstSender != Guid.Empty, "and that id must be a real one");
            }

            PluginSendBus.Unregister(a);
            lock (blocks) { blocks.Clear(); senders.Clear(); }
            PluginSendBus.Submit(a, Block(64, 0.25f));
            Check(!WaitUntil(() => { lock (blocks) return blocks.Count > 0; }, timeoutMs: 120),
                "an instance that has left must not be able to send - that is what stops a receiving track leaking into the mix");

            return "one instance passes straight through; two sum exactly and arrive as one block; a stalled instance costs only its own audio; leaving stops the wait";
        }
        finally { PluginSendBus.ResetForTest(); }
    }

    // ---------------------------------------------------------------------------------------
    // 8. Lane ownership: the last DAW to leave releases it, and a crashed DAW releases by timeout
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The plugin lane is armed by the first DAW to send and released by the last to stop — including
    /// a DAW that was killed and never said goodbye.
    ///
    /// <para>A lane left armed forever is not a silent failure: it keeps the app's "should the sender
    /// be running" answer at yes, so the sender stays up with nothing feeding it and the user has
    /// nothing on screen explaining why. The claim register on the RECEIVE side has exactly this
    /// timeout for exactly this reason, and it was reported as "intermittent, the worst possible
    /// bug".</para>
    /// </summary>
    private static string PluginLaneOwnershipAndTimeout()
    {
        using var sender = new AudioSender();
        using var source = new PluginTrackSource(sender);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        Check(!sender.IsPluginSending, "nothing is armed before a DAW sends");
        Check(!source.AnyHostSending, "and the app must not think anybody is sending");

        source.OnTrackBlock(first, Block(240, 0.3f));
        Check(sender.IsPluginSending, "the first block arms the lane");
        Check(source.AnyHostSending, "and the app's send decision must see it - this is what replaces a second checkbox");
        Check(source.DriverForTest == first, "the first DAW to arrive drives the lane");

        source.OnTrackBlock(second, Block(240, 0.3f));
        Check(source.HostCount == 2, "a second DAW must join rather than replace the first");
        Check(source.DriverForTest == first, "and must not take the clock away from the DAW that was already driving");

        // Nobody sends for a while. Both must time out, and the lane must be released - this is the
        // crashed-DAW path: no goodbye, no release message, nothing but silence.
        Check(PluginTrackSource.HostIdleTimeout < TimeSpan.FromSeconds(4),
            "the idle timeout must be short enough that a killed DAW does not hold the lane noticeably");
        Thread.Sleep((int)PluginTrackSource.HostIdleTimeout.TotalMilliseconds + 250);
        source.Sweep();
        Check(source.HostCount == 0, "a DAW that stops delivering blocks must be forgotten");
        Check(!sender.IsPluginSending, "and the last one leaving must release the lane, with no goodbye needed");
        Check(!source.AnyHostSending, "so the app stops answering yes to 'is anybody sending'");

        // Coming back must work. A lane that could only be armed once would fail the second time a
        // user opened their DAW, which is not a failure anybody would connect to this code.
        source.OnTrackBlock(first, Block(240, 0.3f));
        Check(sender.IsPluginSending, "a DAW that comes back must arm the lane again");
        Check(source.DriverForTest == first, "and drive it");

        // Stopping the sender stands the lane down - "stop sending my audio" covers a DAW track too.
        // But a DAW that is still playing must get it back on its very next block: believing our own
        // bookkeeping here would leave the lane dead with nothing on screen to explain it.
        sender.Stop();
        Check(!sender.IsPluginSending, "stopping the sender must stand the plugin lane down");
        source.OnTrackBlock(first, Block(240, 0.3f));
        Check(sender.IsPluginSending,
            "and the next block from a DAW that is still playing must bring it straight back, without waiting for anything");

        // A clean unload takes the same path: the plugin stops sending, and the sweep lets it go (there is no separate
        // goodbye for a track - PluginTrackSource.Forget, which claimed to be one, was never called; removed 2026-09-25).
        Thread.Sleep((int)PluginTrackSource.HostIdleTimeout.TotalMilliseconds + 250);
        source.Sweep();
        Check(!sender.IsPluginSending, "a DAW that stops sending must release the lane once the sweep sees it quiet");

        return "first block arms, second DAW joins without taking the clock, both time out with no goodbye, a stop is recovered from, and a return re-arms";
    }

    // ---------------------------------------------------------------------------------------
    // 9. Two DAWs: summing, driver hand-over
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Two DAWs connected at once must both be heard, and the loss of the driving one must not stop
    /// the audio.
    ///
    /// <para>The second DAW's blocks arrive on their own clock, so they go into a ring and are pulled
    /// onto the driver's cadence. That is a real buffer with real latency, which is why it is only
    /// used for the second and later hosts — the single-DAW case, asserted elsewhere, touches none of
    /// it.</para>
    /// </summary>
    private static string TwoDawsAtOnce()
    {
        using var sink = new WireSink();
        using var sender = new AudioSender();
        ArmSender(sender, sink);
        sender.SetSendRate(SendRate.Tight);
        using var source = new PluginTrackSource(sender);

        var driver = Guid.NewGuid();
        var joiner = Guid.NewGuid();
        var (key, _) = RemSoundCrypto.ForPlainPassword("gate-password");

        // Prime the joiner's ring first, then run both. The ring starts empty by definition, so a
        // first block that measured a silent contribution would be measuring the cushion filling.
        for (var i = 0; i < 12; i++) source.OnTrackBlock(joiner, Block(120, 0.25f));
        for (var i = 0; i < 40; i++)
        {
            source.OnTrackBlock(driver, Block(120, 0.25f));
            source.OnTrackBlock(joiner, Block(120, 0.25f));
        }

        Check(source.HostCount == 2, "both DAWs must be tracked");
        Check(source.BlocksSummed > 0, "the second DAW's audio must go through the summing path");
        Check(WaitUntil(() => AudioPacketCount(sink) > 0), "and the summed result must reach the wire");

        var peak = DecryptedPeak(sink, key!);
        Check(peak > 0.35f,
            $"two DAWs at 0.25 each must arrive summed, near 0.5, not as one of them alone (decoded peak {peak:0.000})");
        Check(peak < 0.75f, $"and not doubled again on top of that ({peak:0.000})");

        // --- Hand-over: the driving DAW stops sending (closed or crashed - the same to us), the other carries on ---------
        var quietUntil = DateTime.UtcNow + PluginTrackSource.HostIdleTimeout + TimeSpan.FromMilliseconds(250);
        while (DateTime.UtcNow < quietUntil) { source.OnTrackBlock(joiner, Block(120, 0.25f)); Thread.Sleep(20); }
        source.Sweep();
        Check(source.DriverForTest == joiner, "when the driving DAW goes, the other must take the clock");
        Check(sender.IsPluginSending, "and the lane must stay armed - somebody is still playing");
        sink.Clear();
        for (var i = 0; i < 20; i++) source.OnTrackBlock(joiner, Block(120, 0.25f));
        Check(WaitUntil(() => AudioPacketCount(sink) > 0), "audio must continue after the hand-over, with no gap the user has to recover from");
        var afterPeak = DecryptedPeak(sink, key!);
        Check(afterPeak > 0.15f, $"and carry the surviving DAW's own level ({afterPeak:0.000})");

        // --- A driver that goes QUIET without leaving -------------------------------------------
        // The worse case, and the one a sweep alone would not catch for two seconds. A non-driving
        // host's audio only leaves on the driver's block, so a stalled driver silences a DAW that is
        // playing perfectly well. Somebody who is still delivering must take the clock.
        var third = Guid.NewGuid();
        source.OnTrackBlock(third, Block(120, 0.25f));
        Check(source.DriverForTest == joiner, "a new host joins behind the driver, it does not displace one that is working");
        Thread.Sleep((int)(TimeSpan.FromMilliseconds(300)).TotalMilliseconds);
        sink.Clear();
        for (var i = 0; i < 20; i++) source.OnTrackBlock(third, Block(120, 0.25f));
        Check(source.DriverForTest == third,
            "a driver that has gone quiet must lose the clock to a host that is still delivering, without waiting for the idle sweep");
        Check(WaitUntil(() => AudioPacketCount(sink) > 0),
            "and the audio of the DAW that never stopped must reach the wire again");

        return $"two DAWs summed to {peak:0.000} from 0.25 each; the survivor took the clock and kept playing at {afterPeak:0.000}; "
             + "a driver that went quiet lost the clock without a sweep";
    }

    // ---------------------------------------------------------------------------------------
    // 10. The drift corrector
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The corrector on a non-driving DAW's ring must hold the level in both directions, must leave a
    /// matched pair of clocks alone, and must not damage the audio doing it.
    ///
    /// <para>Real drift takes ten seconds to measure by design — the same window the receive side
    /// uses — so the window is driven directly rather than waited out. What is NOT faked is the
    /// resampler, the ring, or the arithmetic: the feed and drain counts come from genuinely feeding
    /// and draining, at deliberately mismatched rates.</para>
    ///
    /// <para>The "no-op when matched" half is the one that matters most. A corrector that always
    /// nudges would be pitching every second DAW slightly sharp or flat for no reason, and nothing
    /// about the sound would say why.</para>
    /// </summary>
    private static string PluginDriftCorrector()
    {
        const int Frames = 240;

        double RunLane(double feedRatio, out int endDepth, out float worstStep)
        {
            var lane = PluginTrackSource.CreateHostLaneForTest();
            var carry = 0.0;
            var phase = 0.0;
            var lastSample = 0f;
            worstStep = 0f;
            var outBlock = new float[Frames * 2];
            var inBlock = new float[(int)(Frames * 1.2) * 2];

            for (var round = 0; round < 400; round++)
            {
                // Feed feedRatio × as many frames as we drain, spread evenly - a producer whose clock
                // runs fast or slow relative to the consumer's.
                carry += Frames * feedRatio;
                var feedFrames = (int)carry;
                carry -= feedFrames;
                for (var i = 0; i < feedFrames * 2; i += 2)
                {
                    var s = (float)(0.4 * Math.Sin(phase));
                    phase += 2 * Math.PI * 440 / 48000;
                    inBlock[i] = s;
                    inBlock[i + 1] = s;
                }
                PluginTrackSource.FeedHostLaneForTest(lane, inBlock.AsSpan(0, feedFrames * 2));
                PluginTrackSource.ReadHostLaneForTest(lane, outBlock);
                // Once the ring is primed, the output must stay a continuous sine. A resampler being
                // driven wrongly shows up here as a step, not as a level change. lastSample is carried
                // across EVERY round including the ignored ones - comparing the first measured sample
                // against a stale zero would report the whole amplitude as a step.
                for (var i = 0; i < outBlock.Length; i += 2)
                {
                    var step = Math.Abs(outBlock[i] - lastSample);
                    // The early rounds are the lane filling its cushion and then joining: silence, then
                    // the signal. That step is real and is what a track being unmuted sounds like.
                    if (round > 20 && step > worstStep) worstStep = step;
                    lastSample = outBlock[i];
                }
                // Close a measurement window every 40 rounds, which is what a ten-second wall clock
                // would do at this block rate in a real session.
                if (round % 40 == 39) PluginTrackSource.ForceWindowForTest(lane, 11.0);
            }
            endDepth = PluginTrackSource.BufferedFramesForTest(lane);
            return PluginTrackSource.AppliedRatioForTest(lane);
        }

        // --- Matched clocks: the corrector must do nothing ------------------------------------------
        var matched = RunLane(1.0, out var matchedDepth, out var matchedStep);
        Check(Math.Abs(matched - 1.0) < 0.004,
            $"with the two clocks matched the corrector must stay at 1:1 (applied {matched:F6})");
        Check(matchedDepth is > 0 and < 9600,
            $"and the ring must hold a cushion without running away - roughly the target, not empty and not seconds deep ({matchedDepth} frames)");

        // --- A producer running FAST: the ring fills, so the rate must be biased up to drain it ------
        var fast = RunLane(1.002, out var fastDepth, out var fastStep);
        Check(fast > matched,
            $"a DAW whose clock runs fast must be pulled from faster, or its ring fills until it drops audio (applied {fast:F6} vs {matched:F6})");
        Check(fastDepth < 24000, $"and the ring must stay bounded - half a second is already too deep ({fastDepth} frames)");

        // --- A producer running SLOW: the ring drains, so the rate must be biased down ---------------
        var slow = RunLane(0.998, out _, out var slowStep);
        Check(slow < matched,
            $"a DAW whose clock runs slow must be pulled from slower, or its ring empties and the track drops out (applied {slow:F6} vs {matched:F6})");

        // --- And the correction must be a nudge, not a pitch shift -----------------------------------
        Check(Math.Abs(fast - 1.0) < 0.06 && Math.Abs(slow - 1.0) < 0.06,
            $"corrections must stay inside the sanity clamp - anything larger is audible as pitch ({fast:F6}, {slow:F6})");
        // Asked of the fast and slow runs too - they are the ones where the corrector is actually working. Only the
        // matched run was checked until 2026-09-24, where the corrector does nothing and so cannot click.
        var worstOfAll = Math.Max(matchedStep, Math.Max(fastStep, slowStep));
        Check(worstOfAll < 0.05f,
            $"a corrected sine must stay continuous - a step is a click, which is worse than the drift it fixes (matched {matchedStep:0.0000}, fast {fastStep:0.0000}, slow {slowStep:0.0000})");

        return $"matched clocks left the rate at {matched:F6}; a fast producer moved it to {fast:F6} and a slow one to {slow:F6}; "
             + $"worst sample step on the corrected signal {worstOfAll:0.0000} (fast {fastStep:0.0000}, slow {slowStep:0.0000})";
    }
}
