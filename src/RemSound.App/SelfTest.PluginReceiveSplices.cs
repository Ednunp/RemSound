using System.Net;
using RemSound.Core;
using RemSound.Plugin;

namespace RemSound.App;

/// <summary>
/// THE PLUGIN PLAYS EVERY FRAME IT ASKED FOR, IN ORDER (2026-09-25 sweep, measured; Ed: "yes all 7"). Each call plays the
/// reply to the ask made a few calls earlier, sized for THAT call - and it was cut to the size of the block being played.
/// A host at 44.1 kHz asks a frame or two more or less each call, so the rest was thrown away: about 21 splices a second.
/// A host that changes its block size got gaps: 12.7% of the track silent. And the short blocks were counted as starved,
/// so the plugin's status blamed "audio arriving short".
///
/// <para>Measured, not reasoned: the peer is a ramp (wire frame k is (k mod 100000) x 1e-5), so every frame dropped or
/// repeated is an irregular step on the track. The real link, the real client and the real render bridge, over loopback.</para>
/// </summary>
internal static partial class SelfTest
{
    private static string? ThePluginPlaysEveryFrameItAskedFor()
    {
        long nextFrame = 0;
        int Ramp(IPAddress _, Span<float> dest, int frames)
        {
            for (var i = 0; i < frames; i++)
            {
                var v = (nextFrame % 100000) * 1e-5f;
                dest[i * 2] = v;
                dest[i * 2 + 1] = v;
                nextFrame++;
            }
            return frames;
        }

        int[] varying = [512, 480, 544, 512, 256, 768, 512, 441];
        var cases = new (string Name, double Rate, Func<int, int> Block)[]
        {
            ("48 kHz, 512-frame blocks", 48000, _ => 512),
            ("44.1 kHz, 512-frame blocks", 44100, _ => 512),
            ("44.1 kHz, 256-frame blocks", 44100, _ => 256),
            ("48 kHz, the block size changing every call", 48000, b => varying[b % varying.Length]),
        };
        var report = new List<string>();
        foreach (var (name, rate, blockSize) in cases)
        {
            nextFrame = 0;
            using var host = new PluginBridgeHost(Ramp, port: 0);
            using var client = new PluginBridgeClient(host.Port);
            client.Hello();
            Thread.Sleep(100);
            client.SetReceivedPeers([IPAddress.Parse("10.9.9.9")]);
            var render = new PeerRenderBridge(client);
            render.PrepareForBlockSize(1024, rate);

            var output = new List<double>(1_500_000);
            var left = new double[2048];
            var right = new double[2048];
            // 800 blocks, the bridge given a turn every second one: long enough that the old cut showed ~120 splices at 44.1 kHz,
            // short enough for the gate (a Sleep(1) is ~15 ms on Windows).
            const int Blocks = 800, Warmup = 60;
            for (var b = 0; b < Blocks; b++)
            {
                var n = blockSize(b);
                Array.Clear(left);
                Array.Clear(right);
                render.FillHostBlock(left.AsSpan(0, n), right.AsSpan(0, n), 1f, mixInto: false);
                if (b >= Warmup) for (var i = 0; i < n; i++) output.Add(left[i]);
                if (b % 2 == 1) Thread.Sleep(1);
            }

            var expected = 1e-5 * 48000.0 / rate;
            int jumps = 0, gaps = 0;
            for (var i = 1; i < output.Count; i++)
            {
                var d = output[i] - output[i - 1];
                if (d < -0.5) continue;                                  // the ramp wrapping round
                if (output[i] == 0) { if (output[i - 1] != 0) gaps++; continue; }
                if (output[i - 1] == 0) continue;
                if (d > expected * 1.5) jumps++;
            }
            var served = client.ServedBlocks;
            var starved = client.StarvedBlocks;
            var saysShort = served > 200 && starved > served / 20;
            report.Add($"{name}: {jumps} splices, {gaps} gaps, {starved} of {served + starved} blocks short");
            // A reply late on a busy machine can cost one: before, it was hundreds.
            Check(jumps <= 2, $"THE SPLICES: {name} - every frame asked for must be played, in order ({jumps} splices in {output.Count} samples)");
            Check(gaps <= 2, $"THE GAPS: {name} - no silent gaps while the peer is sending ({gaps} gaps)");
            Check(!saysShort, $"THE STATUS: {name} - with every reply on time, the plugin must not say the audio is arriving short ({starved} short of {served + starved})");
        }
        return string.Join("; ", report);
    }
}
