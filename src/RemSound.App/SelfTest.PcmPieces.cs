using System.Net;
using RemSound.App.Simulator;
using RemSound.Core;
using RemSound.Sender;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>PCM IN EQUAL PIECES (Ed, 2026-10-01). A block of PCM too big for one packet - every 480-frame WASAPI block,
    /// and ASIO buffers over 233 frames - is cut into equal pieces. It was cut 233 + 233 + 14, and the receiver makes up a
    /// lost piece at the size of the one after it, so a lost 233 was made up as 14 and a lost 14 as 233: the clock matching
    /// misread the sender by hundreds of ppm, the buffer wandered between 25 and 70 ms and broke up, and PCM over the
    /// internet on WASAPI played about 3 cents flat. On the wire, each lane's pieces; then listened to over a lossy
    /// network - WASAPI only, ASIO only with a 512-frame card, and both - each in tune with no dropout.</summary>
    private static string PcmBlocksGoOutInEqualPieces()
    {
        var said = new List<string>();

        // ---- On the wire: the pieces each lane sends ----
        using (var sink = new WireSink())
        using (var sender = new AudioSender())
        {
            sender.SetReceivers([sink.Endpoint]);
            var (key, fingerprint) = RemSoundCrypto.ForPlainPassword("pcm pieces");
            var audioKey = Require(key, "a key");
            sender.AudioKey = audioKey;
            sender.AudioFingerprint = fingerprint;
            sender.SetTightLatency(true);
            sender.ConfigureCodec(AudioTransportCodec.Pcm);
            sender.UseAsioLaneForTest();
            sender.UseDefaultLaneForTest(RenderRoute.WasapiLane);
            int[] Pieces(int frames, bool asio)
            {
                var stream = asio ? sender.AsioLaneStreamIdForTest : sender.DefaultLaneStreamIdForTest;
                var from = sink.Snapshot().Count();
                if (asio) sender.FeedAsioLaneForTest(new float[frames * 2]); else sender.FeedDefaultLaneForTest(new float[frames * 2]);
                Thread.Sleep(150);
                var pieces = new List<int>();
                foreach (var p in sink.Snapshot().Skip(from))
                {
                    if (!RemPacket.TryReadHeader(p, out var type, out var id, out _) || type != RemPacketType.Audio || id != stream) continue;
                    var payload = p.AsSpan(RemPacket.HeaderSize);
                    Check(RemPcmFrame.TryReadSubHeader(payload, out _, out _, out var parts) && parts == 1, "premise: each piece is one packet");
                    Check(RemSoundCrypto.TryDecrypt(audioKey, payload[RemPcmFrame.SubHeaderSize..], out var plain), "premise: each piece opens with the key");
                    pieces.Add(plain.Length / 6);   // 24-bit stereo
                }
                return pieces.ToArray();
            }
            foreach (var (frames, asio, want) in new (int, bool, int[])[]
            {
                (480, false, [160, 160, 160]), (480, true, [160, 160, 160]), (512, true, [171, 171, 170]), (700, true, [175, 175, 175, 175]),
                (234, true, [117, 117]), (233, true, [233]), (128, true, [128]), (32, true, [32]),
            })
            {
                var got = Pieces(frames, asio);
                Check(got.SequenceEqual(want), $"{(asio ? "ASIO" : "WASAPI")} lane, a {frames}-frame block: must go out as {string.Join(" + ", want)} "
                    + $"(went out as {string.Join(" + ", got)})");
            }
            said.Add("480 frames as 160 x 3 on both lanes, 512 as 171 + 171 + 170, 700 as 175 x 4, 234 as 117 x 2; 233, 128 and 32 whole");
        }

        // ---- Listened to: a network that loses half a percent and nothing else, PCM in each configuration ----
        var lossy = new SimNetwork("0.5 % lost, nothing else", 12, 0, LossPercent: 0.5);
        foreach (var (config, card) in new[] { (SimConfig.WasapiOnly, (SimCard?)null), (SimConfig.AsioOnly, new SimCard(512, JitterMs: 0.05)), (SimConfig.Both, new SimCard(512, JitterMs: 0.05)) })
        {
            var ask = Simulate(config, SimCodec.Pcm, lossy, 80, 20, send: card).TunerWouldAskMs(false);
            var r = Simulate(config, SimCodec.Pcm, lossy, ask, 60, send: card);
            var f = r.Findings;
            var what = $"{config}, PCM{(card is null ? "" : $" from a {card.Frames}-frame card")}, 0.5 % lost, at the {ask} ms the auto-tune asks";
            Check(r.PacketsLost > 20 && r.PcmConcealed > 0, $"premise ({what}): packets must be lost and made up ({r.PacketsLost} lost, {r.PcmConcealed} frames made up)");
            Check(r.Underruns == 0, $"{what}: each lost piece made up at its own length, the buffer must never run dry ({r.Underruns} times)");
            Check(f.PitchMedianCents < 0.5, $"{what}: in tune, not flat by the misread lengths ({f.PitchMedianCents:0.0} cents typical, {f.WorstPitchCents:0.0} worst)");
            said.Add($"{config}: {r.PacketsLost} lost, no dropout, {f.PitchMedianCents:0.0} cents typical");
        }
        return string.Join("; ", said);
    }
}
