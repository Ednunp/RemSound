using RemSound.Core;
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
