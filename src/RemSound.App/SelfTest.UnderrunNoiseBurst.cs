using RemSound.App.Simulator;
using RemSound.Core;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>UNDERRUN NOISE BURST (2026-09-30, Ed approved). When the receive buffer runs empty for a moment, the default
    /// artefact sound fills the gap with a short hiss. Each channel's hiss is at that channel's own level: until this date
    /// both took the louder channel's, so a silent channel got a hiss as loud as the other's music - found by the
    /// simulator's music-like signal. Here the tone is on the left and the right is silent from the sender, over a
    /// network that empties a small buffer again and again, in ASIO only, WASAPI only and both (the concealment is the one
    /// both lanes share): the right channel must stay silent through every gap.</summary>
    private static string UnderrunNoiseBurstKeepsEachChannelsLevel()
    {
        var said = new List<string>();
        foreach (var (config, buffer) in new[] { (SimConfig.AsioOnly, 5), (SimConfig.WasapiOnly, 20), (SimConfig.Both, 5) })
        {
            // PCM, so nothing a codec does to a stereo pair can put sound in the silent channel; no delay clicks, so the
            // right channel carries nothing at all.
            var r = Simulation.Run(new SimScenario($"left tone, silent right, {config}", config, SimCodec.Pcm, SimNetwork.Lumpy, buffer, 12,
                DelayClicks: false, KeepAudio: true));
            var audio = Require(r.Audio, "the run's sound");
            float loudestRight = 0, loudestLeft = 0;
            for (var i = 0; i + 1 < audio.Length; i += 2)
            {
                loudestLeft = Math.Max(loudestLeft, Math.Abs(audio[i]));
                loudestRight = Math.Max(loudestRight, Math.Abs(audio[i + 1]));
            }
            Check(r.Underruns > 5, $"premise ({config}): the buffer must run empty again and again ({r.Underruns} times)");
            Check(loudestLeft > 0.4f, $"premise ({config}): the tone must be heard on the left (loudest {loudestLeft:0.00})");
            Check(loudestRight < 0.001f,
                $"{config}: through {r.Underruns} gaps the silent right channel must stay silent - its gap-filling hiss is at its own level, "
                + $"not the left's (loudest on the right {loudestRight:0.0000})");
            said.Add($"{config}: {r.Underruns} gaps, the silent channel at most {loudestRight:0.0000}");
        }
        return "each channel's hiss at its own level - " + string.Join("; ", said);
    }
}
