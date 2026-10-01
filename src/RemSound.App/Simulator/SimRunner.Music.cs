namespace RemSound.App.Simulator;

/// <summary>
/// --simulate music [--seconds N] [--out folder]: the music-like signal (2026-09-30). Each setup is run twice on the same
/// network with the same dice - once on the steady tone, once on the music - so the tone's count of glitches sits beside
/// what the music says of the same losses: which fell in a loud passage, which in a quiet one or a rest, how deep, and
/// how audible. The runs over a faulty network are written as WAV files too (the music on the left, the delay clicks on
/// the right), to be listened to.
/// </summary>
internal static partial class SimRunner
{
    /// <summary>The music scenarios, as the gate runs them too: whole over a perfect network on every codec and both kinds
    /// of output, then over networks that lose, delay and bunch packets.</summary>
    internal static IReadOnlyList<SimScenario> MusicScenarios(double seconds) =>
    [
        new("music, a perfect network, 2.5 ms Opus", SimConfig.AsioOnly, SimCodec.Opus2_5, SimNetwork.Perfect, 10, seconds, Music: true),
        new("music, a perfect network, Jamulus-style 64", SimConfig.AsioOnly, SimCodec.Custom64, SimNetwork.Perfect, 10, seconds, Music: true),
        new("music, a perfect network, PCM", SimConfig.AsioOnly, SimCodec.Pcm, SimNetwork.Perfect, 10, seconds, Music: true),
        new("music, a perfect network, WASAPI only", SimConfig.WasapiOnly, SimCodec.Opus2_5, SimNetwork.Perfect, 40, seconds, Music: true),
        new("music over home wifi, 10 ms", SimConfig.AsioOnly, SimCodec.Opus2_5, SimNetwork.Wifi, 10, seconds, Music: true),
        new("music over the internet, 20 ms", SimConfig.AsioOnly, SimCodec.Opus2_5, SimNetwork.Internet, 20, seconds, Music: true),
        new("music over a rough internet, 40 ms, 2.5 ms Opus", SimConfig.AsioOnly, SimCodec.Opus2_5, SimNetwork.Rough, 40, seconds, Music: true),
        new("music over a rough internet, 40 ms, Jamulus-style 64", SimConfig.AsioOnly, SimCodec.Custom64, SimNetwork.Rough, 40, seconds, Music: true),
        new("music over a rough internet, 40 ms, PCM", SimConfig.AsioOnly, SimCodec.Pcm, SimNetwork.Rough, 40, seconds, Music: true),
        new("music over an internet that loses in bursts, 30 ms", SimConfig.AsioOnly, SimCodec.Opus2_5, SimNetwork.BurstyInternet, 30, seconds, Music: true),
        new("music over a congested line, 30 ms", SimConfig.AsioOnly, SimCodec.Opus2_5, SimNetwork.Congested, 30, seconds, Music: true),
    ];

    private static void Music(double seconds, Action<string> say, string outDir)
    {
        foreach (var s in MusicScenarios(seconds))
        {
            var faulty = s.Network != SimNetwork.Perfect;
            var music = Simulation.Run(s with { KeepAudio = faulty });
            var tone = Simulation.Run(s with { Music = false });
            say(Describe(music));
            say($"  the steady tone, the same network and dice: {(tone.Findings.Clean ? "CLEAN" : $"{tone.Findings.Glitches} glitches ({tone.Findings.QuietMs} quiet ms, {tone.Findings.StepClicks} clicks)")}, "
                + $"delay {tone.Findings.DelayMedianMs:0.0} ms (min {tone.Findings.DelayMinMs:0.0}, max {tone.Findings.DelayMaxMs:0.0}); lost {tone.PacketsLost} packets of {tone.PacketsSent}");
            foreach (var fault in music.Music!.Faults.Take(12)) say($"    {fault}");
            if (music.Music.Faults.Count > 12) say($"    ... and {music.Music.Faults.Count - 12} more");
            if (music.Audio is not null) WriteWav(Path.Combine(outDir, $"{s.Name.Replace(',', ' ').Replace("  ", " ")}.wav"), music.Audio);
        }
    }
}
