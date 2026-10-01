using RemSound.App.Simulator;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>SIMULATOR MUSIC (2026-09-30, the simulator review's eighth improvement). A music-like signal beside the
    /// tone - notes that start sharply and die away, a held note, a quiet note, a rest - and a listening that says of
    /// every fault where in the music it fell, how deep it went and how audible it is. First the signal itself. Then the
    /// listening against known answers: the bar played back untouched is whole; a hole cut in the held note is a dip in a
    /// loud passage, plain to hear; the same hole in the quiet note is slight; in the rest it is nothing; a hole on a
    /// note's start is said to be on its attack; a level halved is a 6 dB dip; noise added in the rest is found and noise
    /// the music covers is not; a jump between samples is a click. Then through the simulator: whole over a perfect network
    /// on every codec and both kinds of output, with the tone's own delay; faults over a rough one, each placed and
    /// weighed; the same run twice the same; and what it cannot do, refused.</summary>
    private static string SimulatorMusic()
    {
        const int rate = Simulation.SampleRate;

        // 1. The signal.
        double peak = 0, biggestJump = 0, furthestFromRepeating = 0;
        for (var n = 0; n < (int)(SimMusic.BarSeconds * rate); n++)
        {
            var x = SimMusic.Sample(n / (double)rate);
            peak = Math.Max(peak, Math.Abs(x));
            biggestJump = Math.Max(biggestJump, Math.Abs(x - SimMusic.Sample((n - 1) / (double)rate)));
            furthestFromRepeating = Math.Max(furthestFromRepeating, Math.Abs(x - SimMusic.Sample(n / (double)rate + SimMusic.BarSeconds * 3)));
        }
        Check(furthestFromRepeating < 1e-5, $"the bar must repeat: three bars on, the same sound (it is up to {furthestFromRepeating:0.000000} out)");
        Check(peak is > 0.35 and <= 0.5, $"the loudest note must be near half scale and never over it (the peak is {peak:0.000})");
        Check(biggestJump < 0.08, $"the music itself must have no click in it - not at a note's start, not at its end (its biggest jump between samples is {biggestJump:0.000})");
        Check(Enumerable.Range(0, 100).All(i => SimMusic.Sample(3.31 + i * 0.0078) == 0f), "the bar must end in a rest: silence");
        var quiet = Enumerable.Range(0, 2000).Max(i => (double)Math.Abs(SimMusic.Sample(2.7 + i / (double)rate)));
        Check(quiet is > 0.02 and < 0.04, $"the quiet note must be about 24 dB under the loud ones (its peak is {quiet:0.000})");
        var early = Enumerable.Range(0, 480).Max(i => (double)Math.Abs(SimMusic.Sample(0.105 + i / (double)rate)));
        var late = Enumerable.Range(0, 480).Max(i => (double)Math.Abs(SimMusic.Sample(0.50 + i / (double)rate)));
        Check(late < early * 0.15, $"a plucked note must die away ({early:0.000} at its start, {late:0.000} near its end)");

        // 2. The listening, against known answers: the bar as it would come out 23 ms late, three bars of it.
        const double delayMs = 23;
        const double seconds = 12;
        var delayFrames = (int)(delayMs * rate / 1000);
        float[] Played()
        {
            var o = new float[(int)(seconds * rate) * 2];
            for (var f = delayFrames; f < o.Length / 2; f++)
            {
                var n = f - delayFrames;
                o[2 * f] = SimMusic.Sample(n / (double)rate);
                o[2 * f + 1] = n % (int)(Simulation.ClickEverySec * rate) == 0 ? Simulation.ClickLevel : 0f;
            }
            return o;
        }
        (int Frame, double Time)[] times = [(0, 0.0)];
        // Where a moment of the second bar comes out, in frames.
        int At(double barSeconds) => (int)((SimMusic.BarSeconds + barSeconds) * rate) + delayFrames;
        SimMusic.Findings Hear(Action<float[]> spoil)
        {
            var o = Played();
            spoil(o);
            return SimMusic.Analyse(o, times, rate);
        }
        static void Hole(float[] o, int from, double ms)
        {
            for (var f = from; f < from + (int)(ms * 48); f++) o[2 * f] = 0;
        }
        static void Noise(float[] o, int from, double ms, float level)
        {
            var dice = new Random(7);
            for (var f = from; f < from + (int)(ms * 48); f++) o[2 * f] += (float)((dice.NextDouble() * 2 - 1) * level);
        }

        var whole = Hear(_ => { });
        Check(whole.Clean && whole.ClicksHeard >= 20 && Math.Abs(whole.DelayMedianMs - delayMs) < 0.1 && whole.JudgedSeconds > seconds * 0.9,
            $"the bar played back untouched must be whole, its delay the {delayMs} ms it was given, and nearly all of it judged - {whole.Describe()}, delay {whole.DelayMedianMs:0.00} ms");

        var held = Hear(o => Hole(o, At(1.9), 10));
        Check(held.Faults.Count == 1 && held.Faults[0] is { Kind: SimMusic.Kind.Dip, Passage: SimMusic.LoudPassage, OnAttack: false, Audible: "plain" } h
              && h.Ms is > 7 and < 13 && h.DepthDb < -30 && Math.Abs(h.AtSec - At(1.9) / (double)rate) < 0.003 && h.FaultDb > -6,
            $"a 10 ms hole in the held note must be one dip of about 10 ms in a loud passage, deep, plain to hear, where it was cut - {held.Describe()}");

        var quietHole = Hear(o => Hole(o, At(2.9), 10));
        Check(quietHole.Faults.Count == 1 && quietHole.Faults[0] is { Kind: SimMusic.Kind.Dip, Passage: SimMusic.QuietPassage, Audible: "slight" } q && q.FaultDb < -20,
            $"the same hole in the quiet note must be a dip in a quiet passage, slight - {quietHole.Describe()}");

        var restHole = Hear(o => Hole(o, At(3.6), 10));
        Check(restHole.Clean, $"and in the rest it must be nothing at all: there is nothing there to lose - {restHole.Describe()}");

        var attack = Hear(o => Hole(o, At(0.6), 8));
        Check(attack.Faults.Count == 1 && attack.Faults[0] is { Kind: SimMusic.Kind.Dip, OnAttack: true },
            $"a hole at a note's start must be said to be on its attack - {attack.Describe()}");
        Check(!held.Faults[0].OnAttack, "and one in the middle of a note must not");

        var halved = Hear(o => { for (var f = At(1.9); f < At(1.9) + 20 * 48; f++) o[2 * f] *= 0.5f; });
        Check(halved.Faults.Count == 1 && halved.Faults[0] is { Kind: SimMusic.Kind.Dip } half && half.DepthDb is > -8 and < -4.5 && half.Ms is > 15 and < 23,
            $"the held note halved for 20 ms must be a dip of 6 dB and about 20 ms - {halved.Describe()}");
        var nearly = Hear(o => { for (var f = At(1.9); f < At(1.9) + 20 * 48; f++) o[2 * f] *= 0.8f; });
        Check(nearly.Clean, $"and one only 2 dB down must not be called a fault - {nearly.Describe()}");

        var added = Hear(o => Noise(o, At(3.6), 5, 0.02f));
        Check(added.Faults.Count == 1 && added.Faults[0] is { Kind: SimMusic.Kind.Added, Passage: SimMusic.Rest, Audible: "plain" } a && a.FaultDb is > -32 and < -24,
            $"5 ms of noise in the rest must be found as added sound, plain to hear: nothing covers it - {added.Describe()}");
        var faint = Hear(o => Noise(o, At(3.6), 5, 0.012f));
        Check(faint.Faults.Count == 1 && faint.Faults[0] is { Kind: SimMusic.Kind.Added, Audible: "slight" },
            $"fainter noise there must be found, and slight - {faint.Describe()}");
        var covered = Hear(o => Noise(o, At(1.9), 5, 0.02f));
        Check(covered.Clean, $"and the first noise under the held note must not be found: the music covers it - {covered.Describe()}");

        var jump = Hear(o => o[2 * At(1.9)] += 0.3f);
        Check(jump.Faults.Count == 1 && jump.Faults[0] is { Kind: SimMusic.Kind.Click, Passage: SimMusic.LoudPassage, Audible: "plain" },
            $"one sample thrown 0.3 out in the held note must be a click, plain to hear - {jump.Describe()}");

        var two = Hear(o => { Hole(o, At(1.9), 10); Hole(o, At(2.1), 10); });
        Check(two.Faults.Count == 2 && two.Plain == 2 && two.Dips == 2 && two.InLoud == 2, $"two holes 200 ms apart must be two faults - {two.Describe()}");

        // The noise an underrun is covered with goes out on both channels. In the clicks' channel it must not be taken for a
        // delay click: that gave a "delay" of anything up to half a second, and skipped the true click after it.
        var noisy = Played();
        var burst = new Random(11);
        for (var f = At(1.9); f < At(1.9) + 48; f++)
        {
            noisy[2 * f] = (float)((burst.NextDouble() * 2 - 1) * 0.4);
            noisy[2 * f + 1] = (float)((burst.NextDouble() * 2 - 1) * 0.4);
        }
        var clicksFound = SimAnalysis.Clicks(noisy, times, rate);
        Check(clicksFound.Count == whole.ClicksHeard && clicksFound.All(c => Math.Abs(c.DelayMs - delayMs) < 0.1),
            $"a millisecond of noise on both channels must not be taken for a delay click ({clicksFound.Count} clicks found, {whole.ClicksHeard} sent; "
            + $"delays {clicksFound.Min(c => c.DelayMs):0.0} to {clicksFound.Max(c => c.DelayMs):0.0} ms)");
        var burstHeard = SimMusic.Analyse(noisy, times, rate);
        Check(burstHeard.Faults.Count == 1 && Math.Abs(burstHeard.Faults[0].AtSec - At(1.9) / (double)rate) < 0.003,
            $"and must be heard as the one fault it is, where it is - {burstHeard.Describe()}");

        var nothing = SimMusic.Analyse(new float[(int)(seconds * rate) * 2], times, rate);
        Check(nothing.ClicksHeard == 0 && !nothing.Clean, "silence must be NOTHING HEARD, never whole");

        // 3. Through the simulator. Whole over a perfect network on every codec and both kinds of output.
        var all = SimRunner.MusicScenarios(12);
        foreach (var s in all.Where(x => x.Network == SimNetwork.Perfect))
        {
            var music = Simulation.Run(s);
            var tone = Simulation.Run(s with { Music = false });
            Check(music.Music is { Clean: true } && music.Underruns == 0, $"over a perfect network the music must come through whole - {SimRunner.Describe(music)}");
            Check(tone.Music is null && tone.Findings.Clean, $"premise: and the tone beside it clean, with no music findings - {SimRunner.Describe(tone)}");
            Check(Math.Abs(music.Music!.DelayMedianMs - tone.Findings.DelayMedianMs) < 0.5 && music.PacketsSent == tone.PacketsSent,
                $"the music must take the same road as the tone: the same delay ({music.Music.DelayMedianMs:0.0} against {tone.Findings.DelayMedianMs:0.0} ms) and as many packets");
        }

        // Over a rough internet on PCM, where a lost packet is a hole and nothing repairs it: faults, each placed and weighed.
        var rough = Require(all.SingleOrDefault(x => x.Name.StartsWith("music over a rough internet, 40 ms, PCM", StringComparison.Ordinal)), "the rough PCM scenario");
        var r = Simulation.Run(rough with { Seconds = 24 });
        var found = Require(r.Music, "the music's findings");
        Check(found.Faults.Count >= 5 && found.Dips >= 5, $"a rough internet on PCM must leave dips in the music - {SimRunner.Describe(r)}");
        Check(found.Plain + found.Slight == found.Faults.Count && found.InLoud + found.InQuiet + found.InRests == found.Faults.Count,
            "every fault must be weighed (plain or slight) and placed (loud, quiet or a rest)");
        Check(found.Faults.Where(f => f.Kind == SimMusic.Kind.Dip).All(f => f.Passage != SimMusic.Rest && f.DepthDb < 0),
            "no dip can be in a rest, and every dip has a depth");
        Check(found.InQuiet > 0 && found.InLoud > 0, $"over 24 seconds they must fall in loud passages and quiet ones ({found.InLoud} and {found.InQuiet})");
        Check(found.Faults.Zip(found.Faults.Skip(1)).All(p => p.First.AtSec < p.Second.AtSec) && found.Faults.All(f => f.AtSec is > 0 and < 24),
            "in the order they were heard, inside the run");
        Check(SimRunner.Describe(r).Contains("faults in the music", StringComparison.Ordinal), $"and the run's own line must say so - {SimRunner.Describe(r)}");

        // The same run twice is the same.
        var again = Simulation.Run(rough with { Seconds = 24 }).Music!;
        Check(again.Faults.SequenceEqual(found.Faults), "the same run twice must find the same faults");

        // What it cannot do, it must say.
        static bool Refuses(SimScenario s)
        {
            try { Simulation.Run(s); return false; }
            catch (ArgumentException) { return true; }
        }
        Check(Refuses(rough with { ChunkSeconds = 5 }), "the music listened to a stretch at a time must be refused: it is listened to whole");
        Check(Refuses(rough with { DelayClicks = false }), "and the music without its delay clicks: they are what places it");

        return $"the bar whole when untouched; a hole in the held note plain, in the quiet note slight, in the rest nothing; noise in the rest found, under a note not; "
             + $"whole over a perfect network on every codec; a rough internet on PCM {found.Faults.Count} faults in 24 s ({found.Plain} plain, {found.Slight} slight; "
             + $"{found.InLoud} in loud passages, {found.InQuiet} in quiet)";
    }
}
