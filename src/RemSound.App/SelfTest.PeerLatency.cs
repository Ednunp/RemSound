using System.Net;
using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>PEER LATENCY (Ed, 2026-09-29, after the live test: "latency to each peer in their status, like ping"): each
    /// connected peer's line in the status says its total latency one way beside its ping - its capture, the sending wait,
    /// half its ping, the buffer its own stream plays at, and the output's delay - a figure for each kind of output its
    /// streams play on, ASIO first; its ping alone while it sends nothing. In all three configurations.</summary>
    private static string? PeerLatencyInTheStatus()
    {
        // The sum and the words.
        Check(MainForm.PeerDelayMs(1, 2, 10, 20, 5) == 38, "the total latency must be capture + sending + half the ping + buffer + output");
        Check(MainForm.PeerStatusLine("Andre", 20, []) == "  Andre: ping 20 ms", "a peer sending nothing must keep its ping alone");
        Check(MainForm.PeerStatusLine("Andre", 20, [(false, 44.4)]) == "  Andre: ping 20 ms, total latency about 45 ms",
            $"one figure, rounded to 5 ms as the ping is ({MainForm.PeerStatusLine("Andre", 20, [(false, 44.4)])})");
        var two = MainForm.PeerStatusLine("Andre", null, [(false, 52), (true, 16)]);
        Check(two == "  Andre: ping unknown, total latency about 15 ms on ASIO, 50 ms on WASAPI", $"one for each kind of output, ASIO first ({two})");

        var said = new List<string>();
        foreach (var configuration in AudioConfigurations.All)
        {
            MainForm? form = null;
            try
            {
                try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
                catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
                var main = form;
                SetAudioConfigurationForTest(main, configuration);
                var name = configuration.Describe();
                var receiver = main.ReceiverForTest;
                var (rawKey, fingerprint) = RemSoundCrypto.ForPlainPassword("peer-latency");
                receiver.AudioKey = Require(rawKey, "the gate password must derive a key");
                receiver.AudioFingerprint = fingerprint;
                // Receiving, as Receive audio ticked has it (no output opened headless), on the outputs this setup has - except
                // WASAPI only, where a copy for an ASIO output is left in place: which outputs count is the window's to know.
                receiver.SetPlaybackEnabled(true);
                if (configuration != AudioConfiguration.WasapiOnly)
                    receiver.SetActiveOutputLanes(wasapiActive: configuration == AudioConfiguration.Both, asioActive: true);
                var peer = new IPEndPoint(IPAddress.Parse("198.51.100.90"), 47999);
                main.SelectPeerForTest(new PeerAnnouncement(Guid.NewGuid(), "Andre", 47999, true, true, DateTime.UtcNow, peer.Address));   // ticked, so their audio is let in
                var now = DateTime.UtcNow;
                main.PeerHealthForTest = () => [HeartbeatService.SnapshotHealthForTest(peer, now, now - TimeSpan.FromSeconds(5), 20, now)];

                // Connected, sending nothing yet: the ping alone.
                var quiet = main.StatusTextForTest();
                Check(quiet.Contains("  Andre: ping 20 ms\r\n", StringComparison.Ordinal) || quiet.Contains("  Andre: ping 20 ms\n", StringComparison.Ordinal),
                    $"{name}: a peer sending nothing must have its ping alone ({quiet.Replace("\r\n", " | ")})");

                // One stream from them, on the lane this configuration listens to.
                const ushort Stream = 4101;
                var lane = configuration == AudioConfiguration.WasapiOnly ? RenderRoute.WasapiLane : RenderRoute.AsioLane;
                var format = new AudioFormatInfo(48000, 2, 16, 1, 4, 192000, (int)AudioTransportCodec.Opus, 120, lane, CaptureLatencyMs: 3);
                var payload = new byte[64];
                var n = RemPacket.WriteFormatPayload(payload, format);
                var packet = new byte[RemPacket.HeaderSize + n];
                RemPacket.WriteHeader(packet, RemPacketType.Format, Stream, 1);
                payload.AsSpan(0, n).CopyTo(packet.AsSpan(RemPacket.HeaderSize));
                receiver.InjectExternalPacket(packet, packet.Length, peer);

                var delays = main.PeerDelaysForTest(peer, 20);
                Check(delays.Count >= 1, $"{name}: a peer whose stream is arriving must have a total latency");
                if (configuration == AudioConfiguration.WasapiOnly)
                    Check(delays.All(d => !d.Asio), $"{name}: with no ASIO driver there is no ASIO figure");
                else
                    Check(delays.Any(d => d.Asio), $"{name}: the ASIO output's figure must be there");
                var status = main.StatusTextForTest();
                var line = MainForm.PeerStatusLine("Andre", 20, delays);
                Check(status.Contains(line, StringComparison.Ordinal) && line.Contains("total latency about", StringComparison.Ordinal),
                    $"{name}: the status must carry the peer's line with its total latency ({line}; status: {status.Replace("\r\n", " | ")})");

                // Its buffer is its own stream's: a stream played 7 ms below its output's setting lowers the figure by 7.
                var playout = receiver.GetOrCreateSessionForTest(peer, Stream);
                var kind = playout.Route == RenderRoute.AsioLane;
                var before = delays.Single(d => d.Asio == kind).Ms;
                var target = receiver.TargetLatencyMsFor(playout.Route);
                playout.OwnNeedMs = target - 7;
                var after = main.PeerDelaysForTest(peer, 20).Single(d => d.Asio == kind).Ms;
                Check(Math.Abs(before - after - 7) < 0.01, $"{name}: the figure must count the buffer the stream plays at ({before:0.0} ms, then {after:0.0} with it 7 ms lower)");
                // And half its OWN ping.
                var slower = main.PeerDelaysForTest(peer, 60).Single(d => d.Asio == kind).Ms;
                Check(Math.Abs(slower - after - 20) < 0.01, $"{name}: and half its own ping ({after:0.0} ms at 20 ms ping, {slower:0.0} at 60)");
                said.Add($"{name}: {line.Trim()}");
            }
            finally { try { form?.Dispose(); } catch { /* teardown */ } }
        }
        return "the sum and the words as meant; a quiet peer its ping alone; a sending one its total latency, from its own stream's buffer "
             + "and its own ping - " + string.Join("; ", said);
    }
}
