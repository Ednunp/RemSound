using System.Net;
using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App;

/// <summary>
/// Three faults the 2026-09-25 review found in the receive path, each proved on the real classes.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>
    /// WITH BOTH OUTPUT TYPES, A PEER PLAYING ON ASIO KEEPS ITS SESSION.
    ///
    /// <para>While the WASAPI lane is not being read - a ticked Bluetooth headset not back after a wake, say - the peer
    /// plays from its ASIO copy and the WASAPI one is not fed. Only the copy that was fed was stamped as having had
    /// audio, so the session looked silent, was thrown away four seconds later, and came back on the next format packet:
    /// the ASIO output dropped out every few seconds, and the connect cues churned (found 2026-09-25). Only "both
    /// together" has a second copy to play from.</para>
    /// </summary>
    private static string? AnAsioCopyPlayingKeepsItsSessionAlive()
    {
        var peer = new IPEndPoint(IPAddress.Parse("192.168.1.66"), 47830);
        var engine = new PlayoutEngine(new ReceiverDiagnostics());
        engine.SetIndependentLaneLatency(true);
        engine.SetLaneActive(RenderRoute.WasapiLane, true);
        engine.SetLaneActive(RenderRoute.AsioLane, true);
        var session = engine.GetOrCreateSession(peer, 21, 1024 * 1024);
        Check(session.Route == RenderRoute.WasapiLane && session.HasMirrorOn(RenderRoute.AsioLane),
            "premise: with both output types a stream plays on WASAPI with a copy of its own on ASIO");

        // Both lanes read once; then the WASAPI one stops being read while ASIO carries on.
        var buffer = new byte[480 * 8];
        var mix = new float[960];
        var scratch = new float[960];
        engine.ReadForRoute(buffer, 0, buffer.Length, RenderRoute.WasapiLane, mix, scratch, false);
        engine.ReadForRoute(buffer, 0, buffer.Length, RenderRoute.AsioLane, mix, scratch, false);
        engine.RewindLaneReadClockForTest(RenderRoute.WasapiLane, LaneActivity.DefaultStallSeconds + 1);
        Check(!engine.LaneIsConsuming(RenderRoute.WasapiLane) && engine.LaneIsConsuming(RenderRoute.AsioLane),
            "premise: the WASAPI lane has stopped being read and the ASIO lane is reading");

        var before = session.LastWriteUtc;
        Thread.Sleep(30);
        session.Write(new byte[480 * 8]);
        Check(session.LastWriteUtc > before,
            "THE DROPOUT: audio arriving for a peer who plays on ASIO must count as audio for the session, or it is thrown "
            + "away four seconds later as silent and the ASIO output drops out");
        return "with both output types and the WASAPI lane not reading, audio fed only to the ASIO copy still keeps the session alive";
    }

    /// <summary>
    /// TWO RECEIVE THREADS EACH DECRYPT THEIR OWN AUDIO.
    ///
    /// <para>Audio from a server arrives on the sender's socket thread, and audio sent to us directly on the listener's.
    /// Both decrypted into one shared buffer, and read it afterwards, so one peer's sound could be overwritten by another's
    /// - heard as clicks or noise with a server peer and a direct peer at once (found 2026-09-25).</para>
    /// </summary>
    private static string? TwoReceiveThreadsEachDecryptTheirOwnAudio()
    {
        var (key, _) = RemSoundCrypto.ForPlainPassword("two receive threads");
        var plainA = Enumerable.Repeat((byte)0xAA, 600).ToArray();
        var plainB = Enumerable.Repeat((byte)0xBB, 600).ToArray();
        var packetA = RemSoundCrypto.Encrypt(key!, plainA);
        var packetB = RemSoundCrypto.Encrypt(key!, plainB);
        using var decryptor = new AudioDecryptor();
        decryptor.EnsureKey(key);

        var wrong = 0;
        var errors = 0;
        void Receive(byte[] packet, byte expected)
        {
            for (var i = 0; i < 20000; i++)
            {
                try
                {
                    var plain = decryptor.TryDecrypt(packet);
                    Thread.SpinWait(30);   // as long as a decode takes to start reading it
                    if (plain.Length != 600) { Interlocked.Increment(ref wrong); continue; }
                    foreach (var b in plain)
                        if (b != expected) { Interlocked.Increment(ref wrong); break; }
                }
                catch { Interlocked.Increment(ref errors); }
            }
        }
        var listener = new Thread(() => Receive(packetA, 0xAA));
        var fromServer = new Thread(() => Receive(packetB, 0xBB));
        listener.Start();
        fromServer.Start();
        listener.Join();
        fromServer.Join();
        Check(wrong == 0 && errors == 0,
            $"THE NOISE: two receive threads decrypting at once must each get their own audio ({wrong} of 40000 came back as "
            + $"the other's or empty, {errors} threw)");
        return "two threads decrypting 20,000 packets each at once every one got its own audio back";
    }

    /// <summary>
    /// A SESSION IS NEVER FREED WHILE IT IS DECODING.
    ///
    /// <para>The network thread decodes a packet outside the receiver's lock, while the window's thread can close the
    /// session at the same moment - someone unticked, receiving switched off, a stream replaced. Closing freed the Opus
    /// decoder, which holds native memory, possibly in the middle of a decode (found 2026-09-25). A packet being handled
    /// and a close now take turns.</para>
    /// </summary>
    private static string? ASessionIsNeverFreedWhileItDecodes()
    {
        var peer = new IPEndPoint(IPAddress.Parse("192.168.1.67"), 47830);
        var engine = new PlayoutEngine(new ReceiverDiagnostics());
        var playout = engine.GetOrCreateSession(peer, 31, 1024 * 1024);
        using var decryptor = new AudioDecryptor();
        var format = new AudioFormatInfo(48000, 2, 24, 1, 6, 288_000, (int)AudioTransportCodec.Pcm, 240);
        var session = new StreamSession(peer, 31, format, playout, new ReceiverDiagnostics(), _ => { }, decryptor);

        Task? closing = null;
        var closedDuringHandling = false;
        session.WhileHandlingForTest = () =>
        {
            closing = Task.Run(session.Dispose);
            closedDuringHandling = closing.Wait(300);
        };
        session.HandleAudioPayload(1, new byte[64]);
        session.WhileHandlingForTest = null;
        Check(!closedDuringHandling,
            "THE CRASH: closing a session must wait for the packet it is handling - it freed the decoder in the middle of a decode");
        Check(closing is not null && closing.Wait(2000), "and must then go ahead once the packet is done");
        Check(!session.HandleAudioPayload(2, new byte[64]), "and a closed session must refuse the next packet rather than decode with nothing");
        return "a close waits for the packet being handled, then goes ahead, and a closed session refuses the next packet";
    }
}
