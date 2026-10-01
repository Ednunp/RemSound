using RemSound.App.Simulator;
using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>THE SKIP (2026-09-28): AudioRingBuffer.SkipWithCrossfade, the heart of coming down from a silly delay. On a
    /// held 440 Hz note it lands in step with the sound - no bigger jump between samples than the note makes itself, and
    /// no dip or swell - and moves on by what was asked, give or take the 5 ms it may shift to line up. On two unrelated
    /// sounds it blends equal-power, without a jump. Too little buffered to blend, it drops plainly. And PeakAhead says how
    /// loud the sound about to play is.</summary>
    private static string? RingBufferSkipsWithoutAClick()
    {
        const int Rate = 48000;
        static float[] Tone(double hz, int frames, double phase = 0)
        {
            var s = new float[frames * 2];
            for (var i = 0; i < frames; i++) s[i * 2] = s[i * 2 + 1] = (float)(0.8 * Math.Sin(2 * Math.PI * hz * i / Rate + phase));
            return s;
        }
        static AudioRingBuffer Filled(float[] floats)
        {
            var ring = new AudioRingBuffer(floats.Length * sizeof(float) * 2);
            ring.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(floats.AsSpan()));
            return ring;
        }
        static float[] ReadAll(AudioRingBuffer ring)
        {
            var rest = new float[ring.BufferedBytes / sizeof(float)];
            ring.ReadFloats(rest);
            return rest;
        }
        static float MaxStep(float[] s) { var m = 0f; for (var i = 2; i < s.Length; i += 2) m = Math.Max(m, Math.Abs(s[i] - s[i - 2])); return m; }
        static float Peak(float[] s, int from, int to) { var m = 0f; for (var i = from * 2; i < to * 2; i += 2) m = Math.Max(m, Math.Abs(s[i])); return m; }

        // What was played, then the skip, then the rest: the join with what was already heard is in view.
        static float[] PlaySkipPlay(AudioRingBuffer ring, int skipBytes, int alignFrames, out int skipped)
        {
            var played = new float[480 * 2];
            ring.ReadFloats(played);
            skipped = ring.SkipWithCrossfade(skipBytes, 720, alignFrames);
            return played.Concat(ReadAll(ring)).ToArray();
        }

        // A held note, 300 ms of it not a whole number of cycles (131.35), so a plain cut or an unaligned blend would show.
        const double NoteHz = 437.83;
        var note = Tone(NoteHz, Rate);
        var askFrames = Rate * 300 / 1000;
        var heard = PlaySkipPlay(Filled(note), askFrames * 8, 240, out var skipped);
        Check(skipped % 8 == 0 && Math.Abs(skipped / 8 - askFrames) <= 240,
            $"a skip of 300 ms must move on by 300 ms, give or take the 5 ms it may shift to line up ({skipped / 8.0 / Rate * 1000:0.0} ms)");
        var natural = MaxStep(note);
        Check(MaxStep(heard) <= natural * 1.05,
            $"on a held note there must be no bigger jump between samples than the note makes itself ({MaxStep(heard):0.0000} against {natural:0.0000}) - a click is a jump");
        var blend = Peak(heard, 480, 480 + 720);
        var dip = Enumerable.Range(0, 720 / 60).Select(k => Peak(heard, 480 + k * 60, 480 + (k + 1) * 60 + 60)).Min();
        Check(blend < 0.8 * 1.05 && dip > 0.8 * 0.95, $"and no dip or swell across the blend (from {dip:0.000} to {blend:0.000} where the note is 0.800)");
        Check(MaxStep(PlaySkipPlayCut(note, askFrames)) > natural * 1.5,
            "premise: the same skip as a plain cut, not lined up, would jump");

        // Two unrelated sounds: equal-power, no jump.
        // (Landing well inside the second: the test signal itself jumps where the two meet.)
        var mixed = Tone(440, Rate / 2).Concat(Tone(1234, Rate / 2, 1.0)).ToArray();
        var into = PlaySkipPlay(Filled(mixed), (Rate / 2 + 2000 - 480) * 8, 0, out _);
        var worstNatural = Math.Max(MaxStep(Tone(440, 4800)), MaxStep(Tone(1234, 4800)));
        // Equal-power, the two gains add to at most 1.41, and each sound's own steps with them.
        Check(MaxStep(into) <= worstNatural * 1.5, $"from one sound into another the blend must not jump ({MaxStep(into):0.0000} against {worstNatural:0.0000})");
        var hardCut = MaxStep(Tone(440, 1).Concat(Tone(1234, 10, 1.0)).ToArray());
        Check(hardCut > worstNatural * 1.5, $"premise: the same skip with no blend would jump ({hardCut:0.0000})");

        // Too little to blend: dropped plainly, exactly as asked.
        var ring = Filled(Tone(440, 100));
        Check(ring.SkipWithCrossfade(90 * 8, 720, 240) == 90 * 8 && ring.BufferedBytes == 10 * 8, "with too little buffered to blend, the skip is a plain drop of what was asked");

        // How loud the sound about to play is.
        ring = Filled(Tone(440, 480).Select(v => v * 0.001f).Concat(Tone(440, 480)).ToArray());
        Check(ring.PeakAhead(480 * 8) < 0.001f && ring.PeakAhead(960 * 8) > 0.7f, "PeakAhead must hear the quiet before the loud, and the loud after it");
        return $"a held note skipped 300 ms without a jump or a dip; unrelated sounds blended without a jump; a plain drop when too little; "
             + $"PeakAhead hears quiet and loud";
    }

    /// <summary>The note as a plain cut would leave it: 480 frames played, then straight on from 480 + the skip.</summary>
    private static float[] PlaySkipPlayCut(float[] note, int skipFrames) =>
        note.Take(480 * 2).Concat(note.Skip((480 + skipFrames) * 2).Take(4800)).ToArray();

    /// <summary>
    /// SIMULATOR SNAP AND DRAIN (Ed, 2026-09-28: "you put a silly number in there you want it to snap back down... at most
    /// a minute... honestly more like 30 seconds"; the design he agreed: PLAN-fixes item 13). The everyday auto-tune from
    /// 500 and 200 ms in all three configurations, and from 500 on home wifi: its first deciding tick goes straight to the
    /// need (AutoTuneDescent's SillyAboveNeedMs), each stream skips once, with a crossfade, to within 100 ms, and the rest
    /// drains at the descent's 0.4 % - down within 50 seconds of the sound starting (the auto-tune first listens for about
    /// ten), no break-up, no click-trim. From 100 ms: no skip at all, drained, down within 45 seconds. And the slider moved
    /// down by hand: down at once, without a break-up, where it used to be dropped outright with a moment's silence.
    /// </summary>
    private static string? SimulatorSnapsDownAndDrains()
    {
        // The auto-tune's own step: silly goes straight to the need; sensible takes the careful step as before.
        Check(AutoTuneDescent.NextTarget(500, 20, -1, 0, 0) == 20 + AutoTuneDescent.Default.HeadroomMs,
            "500 ms against a need of 20 must go straight to the need and its headroom");
        var careful = AutoTuneDescent.NextTarget(115, 20, -1, 0, 0);
        Check(careful > 35 && careful < 115, $"95 ms above the need is sensible - a careful step, not a jump ({careful} ms)");

        var descent = 1200 * Math.Log2(1.004);
        var said = new List<string>();
        var runs = new List<(string What, SimConfig Config, SimNetwork Net, int From, bool Skips, int DownBySec)>();
        foreach (var config in new[] { SimConfig.AsioOnly, SimConfig.WasapiOnly, SimConfig.Both })
        {
            runs.Add(($"{config}, from 500", config, SimNetwork.Wired, 500, true, 50));
            runs.Add(($"{config}, from 200", config, SimNetwork.Wired, 200, true, 50));
            runs.Add(($"{config}, from 100", config, SimNetwork.Wired, 100, false, 45));
        }
        runs.Add(("AsioOnly, from 500, home wifi", SimConfig.AsioOnly, SimNetwork.Wifi, 500, true, 50));
        foreach (var (what, config, net, from, skips, downBy) in runs)
        {
            var r = Simulation.Run(new SimScenario($"snap and drain {what}", config, SimCodec.Opus2_5, net, from, 120,
                WasapiLaneBufferMs: from, ContinuousAutoTune: true, ChunkSeconds: 5));
            var sessions = config == SimConfig.Both ? 4 : 1;
            if (skips)
                Check(r.SnapCount >= 1 && r.SnapCount <= sessions,
                    $"{what}: each stream must skip once from far above - {r.SnapCount} skips for {sessions} stream(s)");
            else
                Check(r.SnapCount == 0, $"{what}: a sensible buffer must be drained, never skipped ({r.SnapCount} skips)");
            Check(r.Findings.Glitches == 0 && r.TrimFires == 0,
                $"{what}: no break-up and no click-trim on the way down ({r.Findings.Glitches} break-ups, {r.TrimFires} trims) - {SimRunner.Describe(r)}");
            foreach (var (output, stretches) in OutputsHeard(r))
            {
                var end = stretches[^1].Findings.DelayMedianMs;
                var arrived = stretches.First(c => stretches.Where(d => d.FromSec >= c.FromSec).All(d => Math.Abs(d.Findings.DelayMedianMs - end) < 8)).FromSec;
                Check(arrived <= downBy, $"{what}, {output}: down within {downBy} s of the sound starting (settled from {arrived:0} s, at {end:0.0} ms)");
                var extra = output.Contains("WASAPI", StringComparison.OrdinalIgnoreCase) ? 2.2 : 0;
                var fastest = stretches.Where(c => !double.IsNaN(c.Findings.PitchP95Cents)).Max(c => c.Findings.PitchP95Cents);
                Check(fastest < descent + extra + 0.5,
                    $"{what}, {output}: the drain may bend the pitch by the descent's {descent:0.0} cents{(extra > 0 ? $" and {extra:0.0} more on a WASAPI output" : "")} "
                    + $"and no more ({fastest:0.0})");
                said.Add($"{what}, {output}: down from {arrived:0} s to {end:0.0} ms, {r.SnapCount} skip(s), {fastest:0.0} cents");
            }
        }

        // Only a lowered target skips: the backlog a network stall delivers all at once, the target unchanged, is the
        // click-trim's business as before - never a skip.
        foreach (var config in new[] { SimConfig.AsioOnly, SimConfig.WasapiOnly, SimConfig.Both })
        {
            // The click-trim off (smoothness 10), so nothing but a skip could take the backlog off at once.
            var r = Simulation.Run(new SimScenario($"stall {config}", config, SimCodec.Opus2_5, SimNetwork.Wired with { StallAtSec = 20, StallOnceMs = 1500 },
                30, 40, WasapiLaneBufferMs: 40, Smoothness: 10));
            Check(r.SnapCount == 0, $"{config}: a 1.5 s network stall's backlog, the target unchanged, must never be skipped ({r.SnapCount} skips)");
        }

        // By hand: 200 ms lowered to 30 at 20 s comes straight down, with no break-up.
        foreach (var config in new[] { SimConfig.AsioOnly, SimConfig.WasapiOnly })
        {
            var r = Simulation.Run(new SimScenario($"by hand {config}", config, SimCodec.Opus2_5, SimNetwork.Wired, 200, 45,
                WasapiLaneBufferMs: 200, HandTargetMs: 30, HandTargetAtSecond: 20, ChunkSeconds: 5));
            var stretches = OutputsHeard(r).First().Stretches;
            var before = stretches.First(c => c.FromSec >= 10).Findings.DelayMedianMs;
            var right = stretches.First(c => c.FromSec >= 25).Findings.DelayMedianMs;
            var end = stretches[^1].Findings.DelayMedianMs;
            Check(before > 180 && Math.Abs(right - end) < 6 && end < 70,
                $"{config}, by hand, 200 ms lowered to 30: down at once ({before:0.0} ms before, {right:0.0} five seconds after, {end:0.0} at the end)");
            Check(r.Findings.Glitches == 0, $"{config}, by hand: no break-up where the buffer came down - it used to be dropped outright, with a moment's silence ({SimRunner.Describe(r)})");
            said.Add($"{config} by hand: {before:0.0} -> {right:0.0} ms at once, no break-up");

            // And raised by hand: 30 ms to 200 at 20 s. A raise cannot be met by dropping anything, only by banking sound, so
            // the fast approach plays up to 5 % slow until the buffer is there - a few seconds, heard as a deliberate glide,
            // never a gap. No simulator step drove it until 2026-09-28.
            var up = Simulation.Run(new SimScenario($"raised by hand {config}", config, SimCodec.Opus2_5, SimNetwork.Wired, 30, 45,
                WasapiLaneBufferMs: 30, HandTargetMs: 200, HandTargetAtSecond: 20, ChunkSeconds: 5));
            var upStretches = OutputsHeard(up).First().Stretches;
            var low = upStretches.First(c => c.FromSec >= 10).Findings.DelayMedianMs;
            var soon = upStretches.First(c => c.FromSec >= 30).Findings.DelayMedianMs;
            var high = upStretches[^1].Findings.DelayMedianMs;
            // Nine tenths of the way within ten seconds: on WASAPI the output stage's own cushion then takes a while longer to
            // settle (PLAN-fixes, the WASAPI output's slow settle; measured 204.8 then 213.6 ms).
            Check(low < 80 && soon - low >= 0.9 * (high - low) && high > 180,
                $"{config}, raised by hand from 30 ms to 200: there within ten seconds ({low:0.0} ms before, {soon:0.0} ten seconds after, {high:0.0} at the end)");
            Check(up.Findings.Glitches == 0, $"{config}, raised by hand: no break-up while it banks the sound ({SimRunner.Describe(up)})");
            said.Add($"{config} raised by hand: {low:0.0} -> {soon:0.0} ms within ten seconds, no break-up");
        }
        return string.Join("; ", said);
    }
}
