using System.Net;
using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App;

/// <summary>
/// ONE STREAM IS ONE SESSION, however many of a peer's addresses it arrives on.
///
/// <para>A peer on the LAN and on a VPN at the same time can have their packets reach us from either
/// address. Sessions are keyed by (source endpoint, streamId), so one stream became TWO sessions,
/// each fed a fraction of the packets, each starving and being pruned on the four-second rule,
/// alternately. Around a hundred concealments a second for as long as it lasted, and it is what
/// Anthony Reyers heard as the iPhone breaking up on 2026-09-01.</para>
///
/// <para>The negative control matters as much as the fix here: two addresses that are NOT known to be
/// one person must still get a session each, or this would have quietly merged two different people
/// onto one stream.</para>
/// </summary>
internal static partial class SelfTest
{
    private static string OneStreamFromTwoAddressesIsOneSession()
    {
        var lan = new IPEndPoint(IPAddress.Parse("192.168.69.48"), 47830);
        var vpn = new IPEndPoint(IPAddress.Parse("100.118.45.53"), 47830);
        var stranger = new IPEndPoint(IPAddress.Parse("10.9.9.9"), 47830);
        const ushort Stream = 21249;

        uint sequence = 1;
        static byte[] FormatPacket(ushort stream, uint seq)
        {
            // 48 kHz stereo PCM, 480 samples a channel — a format IsUsable accepts, so nothing here is
            // testing format validation.
            var format = new AudioFormatInfo(48000, 2, 16, 1, 4, 192000, (int)AudioTransportCodec.Pcm, 480, RenderRoute.Mixed);
            var packet = new byte[RemPacket.HeaderSize + 64];
            RemPacket.WriteHeader(packet, RemPacketType.Format, stream, seq);
            var written = RemPacket.WriteFormatPayload(packet.AsSpan(RemPacket.HeaderSize), format);
            Array.Resize(ref packet, RemPacket.HeaderSize + written);
            return packet;
        }

        void Announce(AudioReceiver target, IPEndPoint from)
        {
            var packet = FormatPacket(Stream, sequence++);
            target.InjectExternalPacket(packet, packet.Length, from);
        }

        using var receiver = new AudioReceiver();
        receiver.SetOutputDevices([]);       // decode only; the gate never opens a device or makes a sound
        receiver.SetPlaybackEnabled(true);

        // --- THE NEGATIVE CONTROL, first, because it is what stops this fix merging strangers -------
        Announce(receiver, lan);
        Announce(receiver, stranger);
        Check(receiver.LiveSessionCountForTest == 2,
            $"two addresses that are NOT known to be one person must get a session each, even on the same stream id - "
          + $"a stream id is chosen by the sender and is not unique between senders ({receiver.LiveSessionCountForTest} sessions)");

        // --- Now tell the receiver those two addresses ARE one person ------------------------------
        using var merged = new AudioReceiver();
        merged.SetOutputDevices([]);
        merged.SetPlaybackEnabled(true);
        merged.SetPeerAddressGroups([[lan.Address, vpn.Address]]);

        Announce(merged, lan);
        Check(merged.LiveSessionCountForTest == 1, "the first path must open one session");

        Announce(merged, vpn);
        Check(merged.LiveSessionCountForTest == 1,
            $"the SAME stream arriving from the peer's other address must join the session it already has, not open a second "
          + $"one ({merged.LiveSessionCountForTest} sessions). Two sessions each get a fraction of the packets, both starve, "
          + "and both are pruned - which is heard as the peer breaking up");

        // --- And the audio has to actually land on that session ------------------------------------
        // Real encryption, real reassembly, real decode: the point is that a packet arriving on the
        // SECOND path is played rather than dropped on the floor, and only a byte that reaches the
        // playout buffer proves that.
        var (rawKey, fingerprint) = RemSoundCrypto.ForPlainPassword("gate-password");
        var key = Require(rawKey, "the gate password must derive a key, or nothing below is encrypting anything");
        merged.AudioKey = key;
        merged.AudioFingerprint = fingerprint;

        var playout = merged.GetOrCreateSessionForTest(lan, Stream);
        var before = playout.BufferedBytes;
        for (var i = 0; i < 8; i++)
        {
            // 240 frames of stereo int24 - silence is fine, since what is under test is whether the
            // bytes arrive at all.
            var sealedFrame = RemSoundCrypto.Encrypt(key, new byte[240 * 2 * 3]);
            var packet = new byte[RemPacket.HeaderSize + RemPcmFrame.SubHeaderSize + sealedFrame.Length];
            RemPacket.WriteHeader(packet, RemPacketType.Audio, Stream, sequence++);
            var payload = packet.AsSpan(RemPacket.HeaderSize);
            RemPcmFrame.WriteSubHeader(payload, (ushort)(100 + i), 0, 1);
            sealedFrame.CopyTo(payload[RemPcmFrame.SubHeaderSize..]);
            // Alternating paths, which is what the log showed the iPhone actually doing.
            merged.InjectExternalPacket(packet, packet.Length, i % 2 == 0 ? lan : vpn);
        }
        Check(playout.BufferedBytes > before,
            $"audio arriving on EITHER path must reach the one session (buffered {before} -> {playout.BufferedBytes} bytes). "
          + "Before this, packets on the second path were dropped on the floor until a format packet opened a second session "
          + "for them, and then the two sessions split the stream between them");

        // --- A PEER WHO GENUINELY MOVES ------------------------------------------------------------
        // Not two paths at once any more: the LAN has gone and everything comes over the VPN from here
        // on. This is the case the fold must not have broken. Before it, the LAN session starved and
        // was pruned after four seconds while a new one opened - a gap. Now the audio simply keeps
        // arriving at the session it always had.
        var beforeMove = playout.BufferedBytes;
        for (var i = 0; i < 8; i++)
        {
            var sealedFrame = RemSoundCrypto.Encrypt(key, new byte[240 * 2 * 3]);
            var packet = new byte[RemPacket.HeaderSize + RemPcmFrame.SubHeaderSize + sealedFrame.Length];
            RemPacket.WriteHeader(packet, RemPacketType.Audio, Stream, sequence++);
            var payload = packet.AsSpan(RemPacket.HeaderSize);
            RemPcmFrame.WriteSubHeader(payload, (ushort)(200 + i), 0, 1);
            sealedFrame.CopyTo(payload[RemPcmFrame.SubHeaderSize..]);
            merged.InjectExternalPacket(packet, packet.Length, vpn);       // the old path is gone
        }
        Announce(merged, vpn);                                            // and its format resend comes over the new one
        Check(merged.LiveSessionCountForTest == 1,
            $"a peer who has MOVED must keep the one session, not open a second one beside it "
          + $"({merged.LiveSessionCountForTest} sessions)");
        Check(playout.BufferedBytes > beforeMove,
            $"and their audio must keep arriving without a break ({beforeMove} -> {playout.BufferedBytes} bytes). Before the "
          + "fold, the old session starved and was pruned four seconds later while a new one opened - which is the gap");

        // The session now lives at an address the app is no longer pinned to, so everything that asks
        // "is this PERSON sending" has to answer about the person and not the path. Address-exact, all
        // of these say no about a peer who is plainly audible: the connected row would say offline, the
        // codec readout would empty, and their volume slider would do nothing.
        Check(merged.IsAudioFlowingFrom(vpn.Address, TimeSpan.FromSeconds(5)),
            "audio must be reported as flowing from the address the peer has MOVED to, not only from the one their session "
          + "opened on - this is what the connected list reads to decide whether to say (offline)");
        Check(merged.IsReceivingFromAddress(vpn.Address),
            "...and the same for the receiving check behind the peer row");
        Check(merged.ActiveFormatFromAddress(vpn.Address) is not null,
            "...and the codec readout, which would otherwise go blank for a peer who is being heard");

        return "two unrelated addresses on one stream id still get a session each; a peer's two addresses get one session, "
             + "audio on either path reaches it, and a peer who moves for good keeps that session with no gap and is still "
             + "reported as sending";
    }
}
