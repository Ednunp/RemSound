using System.Net;
using RemSound.Core;
using RemSound.Receiver;
using RemSound.Sender;

namespace RemSound.App;

/// <summary>
/// Uncompressed audio goes out one packet per frame. Ed, 2026-09-15. A 5 ms uncompressed frame (240 samples of 24-bit
/// stereo) came to 1,468 bytes once encrypted, 14 over the 1,454-byte chunk, so every frame went as two packets and losing
/// either lost the frame. Frames are now 236 samples, the largest that fits one packet on a 1,492-byte path.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>An encrypted standard frame on the wire: 236 samples × 2 channels × 3 bytes, plus the cipher's overhead.</summary>
    private const int OnePacketPcmCiphertextBytes = 236 * 6 + RemSoundCrypto.EncryptionOverheadBytes;   // 1,444

    /// <summary>
    /// A STANDARD UNCOMPRESSED FRAME GOES OUT AS ONE PACKET.
    ///
    /// <para>Real sender, real encryption, real socket: every frame must arrive in one packet, and a full frame must be
    /// the largest that fits one — smaller would send more packets than it needs to.</para>
    /// </summary>
    private static string? PcmStandardFrameIsOnePacket() => WithPcmSender(lockToClock: false, (sender, sink) =>
    {
        for (var i = 0; i < 10; i++) sender.SubmitPluginBlock(Block(1000, 0.3f));
        Check(WaitUntil(() => AudioPacketCount(sink) >= 40, 3000), $"uncompressed audio must reach the wire ({AudioPacketCount(sink)} packets)");
        var (packets, split, largest) = PcmParts(sink);
        Check(split == 0,
            $"every standard uncompressed frame must go out as ONE packet — {split} of {packets} were halves of a split frame, and losing either half loses the frame");
        Check(largest == OnePacketPcmCiphertextBytes,
            $"a standard frame must be the largest that fits one encrypted packet ({OnePacketPcmCiphertextBytes} bytes on the wire; the largest was {largest})");
        return $"{packets} standard uncompressed frames, each one packet of {largest} bytes";
    });

    /// <summary>
    /// A LOCK-TO-CLOCK PIECE GOES OUT AS ONE PACKET.
    ///
    /// <para>Timed off the capture clock, the sender sends each delivered buffer as it arrives, cut into pieces. A 480- or
    /// 512-frame buffer was cut at 240, so each piece still went as two packets.</para>
    /// </summary>
    private static string? PcmLockToClockPieceIsOnePacket() => WithPcmSender(lockToClock: true, (sender, sink) =>
    {
        sender.SubmitPluginBlock(Block(480, 0.3f));
        sender.SubmitPluginBlock(Block(512, 0.3f));
        Check(WaitUntil(() => AudioPacketCount(sink) >= 6, 3000), $"uncompressed audio must reach the wire ({AudioPacketCount(sink)} packets)");
        var (packets, split, largest) = PcmParts(sink);
        Check(split == 0,
            $"every piece of a large buffer must go out as ONE packet — {split} of {packets} were halves of a split piece");
        Check(largest == OnePacketPcmCiphertextBytes,
            $"a large buffer must be cut into the largest pieces that fit one encrypted packet ({OnePacketPcmCiphertextBytes} bytes; the largest was {largest})");
        return $"480- and 512-frame buffers went as {packets} pieces, each one packet, the largest {largest} bytes";
    });

    /// <summary>
    /// A RECEIVER GIVES A ONE-PACKET FRAME THE TRIM ROOM OF A 5 MS FRAME.
    ///
    /// <para>A receiver leaves room over target before it trims a buffer, sized from the largest frame it has been sent. A
    /// 236-sample frame is 4.92 ms; rounded down to 4 it would leave 2 ms less room on an ASIO output and 4 ms less on a
    /// WASAPI one, and trim latency down sooner than before. Each lane must still ride out a buffer 1 ms inside a 5 ms
    /// frame's room, and still trim one 1 ms past it.</para>
    /// </summary>
    private static string? ReceiverGivesAOnePacketFrameTheTrimRoomOfA5MsFrame()
    {
        var lanesChecked = new List<string>();
        foreach (var configuration in AudioConfigurations.All)
        {
            // Room over target for a 5 ms frame: smoothness 3 (a WASAPI output's default) leaves 5×4+4 plus 8; smoothness 1
            // (an ASIO output at its tightest) leaves 5×2+4.
            var lanes = new List<(string Name, int TargetMs, int Smoothness, int RoomMs)>();
            if (configuration.UsesWasapi()) lanes.Add(("WASAPI output", 40, 3, 32));
            if (configuration.UsesAsio()) lanes.Add(("ASIO output", 12, 1, 14));
            foreach (var (name, targetMs, smoothness, roomMs) in lanes)
            {
                foreach (var (overMs, shouldTrim) in new[] { (roomMs - 1, false), (roomMs + 1, true) })
                {
                    var session = new SessionPlayout(new IPEndPoint(IPAddress.Loopback, 47830), 1, 1 << 20);
                    // One standard frame from the sender, then 1 ms top-ups (which never count as the largest frame).
                    var frames = (targetMs + overMs) * 48;
                    session.Write(new byte[AudioSender.PcmStandardSamplesPerChannel * 2 * sizeof(float)]);
                    frames -= AudioSender.PcmStandardSamplesPerChannel;
                    for (; frames >= 48; frames -= 48) session.Write(new byte[48 * 2 * sizeof(float)]);
                    if (frames > 0) session.Write(new byte[frames * 2 * sizeof(float)]);
                    session.NoteFramesQueued(targetMs);
                    session.ReadFloats(new float[2], 1, targetMs, 1000, smoothness, applyShaping: false, emitRecordTap: false);
                    Check((session.TrimFireCount > 0) == shouldTrim,
                        $"in {configuration.Describe()}, the {name} must give a {AudioSender.PcmStandardSamplesPerChannel}-sample frame the same trim room "
                        + $"as a 5 ms frame, {roomMs} ms over its {targetMs} ms target: a buffer {overMs} ms over "
                        + (shouldTrim ? "must still be trimmed" : "must not be trimmed, or latency is brought down sooner than before"));
                }
                lanesChecked.Add($"{configuration.Describe()} {name}");
            }
        }
        return $"{string.Join(", ", lanesChecked)}: a 236-sample frame gets a 5 ms frame's trim room, to the millisecond";
    }

    /// <summary>A sender sending uncompressed audio at the standard rate, encrypted, from the plugin lane to a sink.</summary>
    private static string? WithPcmSender(bool lockToClock, Func<AudioSender, WireSink, string?> body)
    {
        using var sink = new WireSink();
        using var sender = new AudioSender();
        sender.SetReceivers([sink.Endpoint]);
        sender.ConfigureCodec(AudioTransportCodec.Pcm);
        sender.SetSendRate(SendRate.Standard);
        sender.SetTightLatency(lockToClock);
        var (key, fingerprint) = RemSoundCrypto.ForPlainPassword("one-packet-pcm");
        sender.AudioKey = key;
        sender.AudioFingerprint = fingerprint;
        sender.SetPluginSendActive(true);
        return body(sender, sink);
    }

    /// <summary>The uncompressed audio packets a sink caught: how many, how many were one part of a split frame, and the
    /// largest encrypted part.</summary>
    private static (int Packets, int Split, int Largest) PcmParts(WireSink sink)
    {
        int packets = 0, split = 0, largest = 0;
        foreach (var packet in sink.Snapshot())
        {
            if (!RemPacket.TryReadHeader(packet, out var type, out _, out _) || type != RemPacketType.Audio) continue;
            var body = packet.AsSpan(RemPacket.HeaderSize);
            if (!RemPcmFrame.TryReadSubHeader(body, out _, out _, out var totalParts)) continue;
            packets++;
            if (totalParts != 1) split++;
            largest = Math.Max(largest, body.Length - RemPcmFrame.SubHeaderSize);
        }
        return (packets, split, largest);
    }
}
