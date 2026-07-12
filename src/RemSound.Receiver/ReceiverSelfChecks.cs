using System.Net;
using RemSound.Core;

namespace RemSound.Receiver;

/// <summary>
/// Public entry points for the app's <c>--selftest</c> that need to drive the receiver's INTERNAL
/// playout engine directly (PlayoutEngine / SessionPlayout are internal to this assembly). Keeps the
/// test logic next to the code it exercises without widening those types' visibility.
/// </summary>
public static class ReceiverSelfChecks
{
    /// <summary>Proves the "every received stream plays to EVERY active output" fan-out. With both output
    /// lanes active (BothIndependent), one incoming stream must produce audio on BOTH the WASAPI and the
    /// ASIO lane surface — WASAPI from the primary session, ASIO from its mirror replica. Returns null on
    /// success, or a message describing which lane was silent. (Before the fan-out, only the lane matching
    /// the sender's capture tag played and the other output was silent.)</summary>
    public static string? FanOutToBothOutputs()
    {
        const int rate = 48000, ch = 2, bpf = ch * 4;
        var engine = new PlayoutEngine(new ReceiverDiagnostics());
        // Both output lanes active = BothIndependent.
        engine.SetLaneActive(RenderRoute.WasapiLane, true);
        engine.SetLaneActive(RenderRoute.AsioLane, true);

        var endpoint = new IPEndPoint(IPAddress.Loopback, 40000);
        var sp = engine.GetOrCreateSession(endpoint, 1234, rate * bpf); // 1 s ring

        // ~200 ms of a constant, clearly non-zero stereo signal, then arm playback.
        var floats = new float[rate / 5 * ch];
        Array.Fill(floats, 0.25f);
        var block = new byte[floats.Length * 4];
        Buffer.BlockCopy(floats, 0, block, 0, block.Length);
        sp.Write(block);
        sp.NoteFramesQueued(5);

        // Read a 10 ms block from each lane surface: WASAPI = primary, ASIO = mirror.
        var rd = rate / 100 * bpf;
        var w = new byte[rd];
        var a = new byte[rd];
        engine.WasapiLaneOutput.Read(w, 0, rd);
        engine.AsioLaneOutput.Read(a, 0, rd);

        if (!HasAudio(w)) return "WASAPI output lane produced silence — the fan-out primary isn't playing";
        if (!HasAudio(a)) return "ASIO output lane produced silence — the mirror isn't playing (the second output would be silent)";
        return null;
    }

    private static bool HasAudio(byte[] pcmFloat)
    {
        var f = new float[pcmFloat.Length / 4];
        Buffer.BlockCopy(pcmFloat, 0, f, 0, pcmFloat.Length);
        foreach (var v in f) if (Math.Abs(v) > 0.001f) return true;
        return false;
    }
}
