using RemSound.App.Simulator;
using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App;

/// <summary>
/// PUSH TO TALK (Ed, 2026-09-28: "build and test thoroughly every single thing"; "every single one of these conditions you
/// should test using the simulator ... that's wasapi asio and both"; and "I don't want a delay when you hit the key ... ms,
/// not seconds"). The simulator runs the real sender and receiver: the key goes through the real key logic
/// (PushToTalkKeyState) into the real gate on the send lanes, over a simulated wired network, into a real receiver, and what
/// comes out is measured - when the sound starts after the key, how it fades, what stays silent, which path is held,
/// the sound-level trigger, the recording, the DAW track. Every simulator step runs WASAPI only, ASIO only and both.
/// </summary>
internal static partial class SelfTest
{
    // ---------------- the simulator's side ----------------

    private static readonly (SimConfig Config, int BufferMs)[] PttSetups = [(SimConfig.WasapiOnly, 40), (SimConfig.AsioOnly, 10), (SimConfig.Both, 10)];

    private static SimResult PttRun(SimConfig config, int buffer, SimPushToTalk? ptt, double seconds, bool clicks = true) =>
        Simulation.Run(new SimScenario($"push to talk, {config}", config, SimCodec.Opus2_5, SimNetwork.Wired, buffer, seconds,
            KeepAudio: true, DelayClicks: clicks, PushToTalk: ptt));

    private static PushToTalkSettings PttOn(PushToTalkPath path = PushToTalkPath.Both, bool hold = true, bool doubleTap = false) =>
        new() { Enabled = true, Path = path, Trigger = PushToTalkTrigger.Key, HoldToSpeak = hold, DoubleTapToHold = doubleTap };

    private static (double At, bool Down)[] KeyMoments(params (double At, bool Down)[] events) => events;

    /// <summary>When the output frame was heard, in simulated seconds.</summary>
    private static double HeardAt(IReadOnlyList<(int Frame, double Time)> times, int frame)
    {
        var lo = 0;
        var hi = times.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (times[mid].Frame <= frame) lo = mid; else hi = mid - 1;
        }
        return times[lo].Time + (frame - times[lo].Frame) / (double)Simulation.SampleRate;
    }

    /// <summary>The first output frame heard at or after a simulated moment.</summary>
    private static int FrameHeardAt(IReadOnlyList<(int Frame, double Time)> times, double t)
    {
        foreach (var (frame, time) in times) if (time >= t) return frame;
        return times.Count > 0 ? times[^1].Frame : 0;
    }

    /// <summary>The loudest left-channel sample (the tone's channel) in a stretch of output frames.</summary>
    private static float PeakL(float[] audio, int from, int to)
    {
        var peak = 0f;
        for (var f = Math.Max(0, from); f < Math.Min(to, audio.Length / 2); f++) peak = Math.Max(peak, Math.Abs(audio[2 * f]));
        return peak;
    }

    /// <summary>The first frame from <paramref name="from"/> whose left sample is louder than the level, or -1.</summary>
    private static int FirstAbove(float[] audio, int from, float level)
    {
        for (var f = Math.Max(0, from); f < audio.Length / 2; f++) if (Math.Abs(audio[2 * f]) > level) return f;
        return -1;
    }

    /// <summary>The last frame before <paramref name="to"/> whose left sample is louder than the level, or -1.</summary>
    private static int LastAbove(float[] audio, int from, int to, float level)
    {
        for (var f = Math.Min(to, audio.Length / 2) - 1; f >= Math.Max(0, from); f--) if (Math.Abs(audio[2 * f]) > level) return f;
        return -1;
    }

    /// <summary>The amplitude of one frequency in the left channel over a stretch (Goertzel).</summary>
    private static double ToneAmplitude(float[] audio, int from, int frames, double hz)
    {
        frames = Math.Min(frames, audio.Length / 2 - from);
        if (frames <= 0) return 0;
        var w = 2 * Math.PI * hz / Simulation.SampleRate;
        var coeff = 2 * Math.Cos(w);
        double s1 = 0, s2 = 0;
        for (var i = 0; i < frames; i++)
        {
            var s0 = audio[2 * (from + i)] + coeff * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        var power = s1 * s1 + s2 * s2 - coeff * s1 * s2;
        return 2 * Math.Sqrt(Math.Max(0, power)) / frames;
    }

    /// <summary>The tone's amplitude over a stretch heard between two moments.</summary>
    private static double HeardAmplitude(SimResult r, double from, double to, double hz, bool wasapiOutput = false)
    {
        var audio = wasapiOutput ? r.WasapiAudio! : r.Audio!;
        var times = wasapiOutput ? r.WasapiAudioTimes : r.AudioTimes;
        var a = FrameHeardAt(times, from);
        var b = FrameHeardAt(times, to);
        return ToneAmplitude(audio, a, b - a, hz);
    }

    private static float HeardPeak(SimResult r, double from, double to) => PeakL(r.Audio!, FrameHeardAt(r.AudioTimes, from), FrameHeardAt(r.AudioTimes, to));

    /// <summary>How long the sound takes to come up at an onset: the envelope (the loudest sample over the last
    /// millisecond, a whole cycle of the tone) from a tenth to nine tenths of its full level.</summary>
    private static double RiseMs(float[] audio, int onset, float full)
    {
        double Envelope(int f) { var p = 0f; for (var k = Math.Max(0, f - 47); k <= f; k++) p = Math.Max(p, Math.Abs(audio[2 * k])); return p; }
        var start = onset - 96;
        int? tenth = null, ninth = null;
        for (var f = Math.Max(0, start); f < Math.Min(audio.Length / 2, onset + 960); f++)
        {
            var e = Envelope(f);
            if (tenth is null && e >= 0.1 * full) tenth = f;
            if (ninth is null && e >= 0.9 * full) { ninth = f; break; }
        }
        return tenth is { } t && ninth is { } n ? (n - t) * 1000.0 / Simulation.SampleRate : double.NaN;
    }

    private static float MaxStepL(float[] audio, int from, int to)
    {
        var step = 0f;
        for (var f = Math.Max(1, from); f < Math.Min(to, audio.Length / 2); f++) step = Math.Max(step, Math.Abs(audio[2 * f] - audio[2 * f - 2]));
        return step;
    }

    /// <summary>The stream's own delay in this setup - the same run with push to talk off, timed by its clicks.</summary>
    private static double PttBaseDelayMs(SimConfig config, int buffer)
    {
        var r = PttRun(config, buffer, null, 4);
        Check(r.Findings.Clean, $"{config}: premise - without push to talk the tone plays clean ({SimRunner.Describe(r)})");
        return r.Findings.DelayMedianMs;
    }

    /// <summary>PUSH TO TALK OFF CHANGES NOTHING: a profile with push to talk off, the key pressed and released all the
    /// while, sends exactly what a run without push to talk sends - sample for sample, on every output.</summary>
    private static string? PushToTalkOffChangesNothing()
    {
        var said = new List<string>();
        foreach (var (config, buffer) in PttSetups)
        {
            var plain = PttRun(config, buffer, null, 4);
            var off = PttRun(config, buffer, new SimPushToTalk(new PushToTalkSettings { Enabled = false },
                KeyMoments((1.0, true), (1.5, false), (2.0, true), (2.1, false), (2.2, true), (3.0, false))), 4);
            Check(plain.Audio!.Length == off.Audio!.Length && plain.Audio.SequenceEqual(off.Audio),
                $"{config}: with push to talk off, what is heard must be exactly what is heard without it, sample for sample");
            if (config == SimConfig.Both)
                Check(plain.WasapiAudio!.SequenceEqual(off.WasapiAudio!), "Both: and on the WASAPI output too");
            Check(off.Findings.Clean, $"{config}: and clean ({SimRunner.Describe(off)})");
            said.Add($"{config} identical");
        }
        return "push to talk off, key pressed all the while: " + string.Join(", ", said);
    }

    /// <summary>
    /// THE AIRLOCK (Ed: "send is the inner airlock open, and push to talk is the outer airlock which shoots open and closes
    /// when you move that switch"; "I don't want a delay when you hit the key ... ms, but not in seconds"). Hold to speak:
    /// nothing goes out before the key; the sound is heard the stream's ordinary delay after the key, give or take a block
    /// and the fade; it stops as quickly when the key is let go; it fades up and down over about 5 ms, never a step; and
    /// it works the same the second time.
    /// </summary>
    private static string? PushToTalkKeyOpensAndClosesAtOnce()
    {
        var said = new List<string>();
        foreach (var (config, buffer) in PttSetups)
        {
            var d0 = PttBaseDelayMs(config, buffer) / 1000;
            var r = PttRun(config, buffer, new SimPushToTalk(PttOn(), KeyMoments((2.0, true), (4.0, false), (6.0, true), (7.0, false))), 8);
            var audio = r.Audio!;
            var times = r.AudioTimes;
            var what = $"{config}";

            Check(HeardPeak(r, 0.5, 2.0 + d0 - 0.02) <= 0.001, $"{what}: before the key, nothing may go out ({HeardPeak(r, 0.5, 2.0 + d0 - 0.02):0.0000})");
            var measured = new List<string>();
            foreach (var (down, up) in new[] { (2.0, 4.0), (6.0, 7.0) })
            {
                var onset = FirstAbove(audio, FrameHeardAt(times, down), 0.25f);
                Check(onset >= 0, $"{what}: after the key at {down} s, the sound must come");
                var keyToSound = (HeardAt(times, onset) - down) * 1000;
                Check(Math.Abs(keyToSound - d0 * 1000) <= 15,
                    $"{what}: THE AIRLOCK - the sound must be heard the stream's ordinary delay after the key, give or take a block and the fade: {keyToSound:0.0} ms after the key, the stream's delay being {d0 * 1000:0.0} ms");
                var rise = RiseMs(audio, onset, Simulation.ToneLevel);
                Check(rise is >= 2 and <= 8, $"{what}: it must fade up over about 5 ms, not step on ({rise:0.0} ms from a tenth to nine tenths)");
                var step = MaxStepL(audio, onset - 240, onset + 480);
                Check(step <= 0.1f, $"{what}: no click where it opens - the largest step between samples must be the tone's own ({step:0.000})");
                Check(HeardAmplitude(r, down + d0 + 0.03, up + d0 - 0.03, Simulation.ToneHz) > 0.4, $"{what}: while the key is held, the tone goes out whole");
                var last = LastAbove(audio, FrameHeardAt(times, up), FrameHeardAt(times, up + d0 + 0.2), 0.01f);
                Check(last >= 0, $"{what}: premise - the tone is still heard just after the key is let go (the delay)");
                var letGo = (HeardAt(times, last) - up) * 1000;
                Check(Math.Abs(letGo - d0 * 1000) <= 15,
                    $"{what}: and stop the ordinary delay after the key is let go: {letGo:0.0} ms, the stream's delay being {d0 * 1000:0.0} ms");
                Check(HeardPeak(r, up + d0 + 0.02, up + d0 + 0.9) <= 0.001, $"{what}: after the key is let go, nothing goes out");
                measured.Add($"key to sound {keyToSound:0.0} ms, let go {letGo:0.0} ms, fade {rise:0.0} ms");
            }
            said.Add($"{config} (delay {d0 * 1000:0.0} ms): {measured[0]}");
        }
        return "hold to speak, the stream's ordinary delay and no more: " + string.Join("; ", said);
    }

    /// <summary>PUSH TO TALK KEY BEHAVIOURS ON THE WIRE: a tap turning talking on and off, a double tap locking it, and
    /// both boxes together - each heard exactly when it should be, in every setup.</summary>
    private static string? PushToTalkKeyBehavioursReachTheWire()
    {
        var said = new List<string>();
        foreach (var (config, buffer) in PttSetups)
        {
            var d0 = PttBaseDelayMs(config, buffer) / 1000;
            void Heard(SimResult r, string what, double from, double to) =>
                Check(HeardAmplitude(r, from + d0 + 0.03, to + d0 - 0.03, Simulation.ToneHz) > 0.4, $"{config}, {what}: the tone must go out from {from} s to {to} s");
            void Silent(SimResult r, string what, double from, double to) =>
                Check(HeardPeak(r, from + d0 + 0.03, to + d0 - 0.03) <= 0.001, $"{config}, {what}: nothing may go out from {from} s to {to} s ({HeardPeak(r, from + d0 + 0.03, to + d0 - 0.03):0.0000})");

            // Neither box: a tap on, the next tap off.
            var tap = PttRun(config, buffer, new SimPushToTalk(PttOn(hold: false), KeyMoments((2.0, true), (2.1, false), (4.0, true), (4.1, false))), 6);
            Silent(tap, "a tap", 0.5, 2.0);
            Heard(tap, "a tap", 2.0, 4.0);
            Silent(tap, "the next tap", 4.0, 5.8);

            // Double tap to hold alone: a single tap does nothing, a double tap locks it, the next double tap lets go.
            var dbl = PttRun(config, buffer, new SimPushToTalk(PttOn(hold: false, doubleTap: true),
                KeyMoments((2.0, true), (2.1, false), (2.3, true), (2.4, false), (5.0, true), (5.1, false), (5.3, true), (5.4, false), (6.5, true), (6.6, false))), 7.5);
            Silent(dbl, "one tap of double tap", 0.5, 2.3);
            Heard(dbl, "a double tap", 2.3, 5.3);
            Silent(dbl, "the releasing double tap, then a single tap", 5.3, 7.3);

            // Both: holding talks while held; a double tap locks it; the next double tap releases it, still talking while held.
            var both = PttRun(config, buffer, new SimPushToTalk(PttOn(hold: true, doubleTap: true),
                KeyMoments((2.0, true), (2.5, false), (3.0, true), (3.1, false), (3.3, true), (3.4, false), (5.0, true), (5.1, false), (5.3, true), (5.8, false))), 7);
            Heard(both, "held", 2.0, 2.5);
            Silent(both, "let go", 2.5, 3.0);
            Heard(both, "a double tap while holding counts", 3.0, 5.8);
            Silent(both, "released by a double tap, then let go", 5.8, 6.8);
            said.Add($"{config}: tap, double tap and both heard as set");
        }
        return string.Join("; ", said);
    }

    /// <summary>PUSH TO TALK PATH: only the path chosen is held; the other goes out as normal (Ed: "asio stuff playing and a
    /// mic on push to talk wasapi"). In "both" each lane carries a tone of its own (1 kHz ASIO, 1.6 kHz WASAPI), heard apart
    /// on both outputs; in WASAPI only and ASIO only, the path that isn't the running one leaves it alone.</summary>
    private static string? PushToTalkHoldsOnlyItsPath()
    {
        var said = new List<string>();
        var talk = KeyMoments((3.0, true), (5.0, false));
        foreach (var (config, buffer) in PttSetups)
        {
            var d0 = PttBaseDelayMs(config, buffer) / 1000;
            if (config == SimConfig.Both)
            {
                foreach (var path in new[] { PushToTalkPath.Wasapi, PushToTalkPath.Asio, PushToTalkPath.Both })
                {
                    var r = PttRun(config, buffer, new SimPushToTalk(PttOn(path), talk, OtherLaneTone: true), 6);
                    foreach (var wasapiOut in new[] { false, true })
                    {
                        var output = wasapiOut ? "the WASAPI output" : "the ASIO output";
                        var asioShut = HeardAmplitude(r, 1.0 + d0, 2.8, Simulation.ToneHz, wasapiOut);
                        var asioOpen = HeardAmplitude(r, 3.2 + d0, 4.8, Simulation.ToneHz, wasapiOut);
                        var wasapiShut = HeardAmplitude(r, 1.0 + d0, 2.8, SimPushToTalk.OtherLaneHz, wasapiOut);
                        var wasapiOpen = HeardAmplitude(r, 3.2 + d0, 4.8, SimPushToTalk.OtherLaneHz, wasapiOut);
                        var holdsAsio = path != PushToTalkPath.Wasapi;
                        var holdsWasapi = path != PushToTalkPath.Asio;
                        var what = $"Both, path {PushToTalkSettings.PathName(path)}, {output}";
                        Check(holdsAsio ? asioShut < 0.01 : asioShut > 0.4, $"{what}: the ASIO lane's tone must be {(holdsAsio ? "held" : "left alone")} while not talking ({asioShut:0.000})");
                        Check(holdsWasapi ? wasapiShut < 0.01 : wasapiShut > 0.2, $"{what}: the WASAPI lane's tone must be {(holdsWasapi ? "held" : "left alone")} while not talking ({wasapiShut:0.000})");
                        Check(asioOpen > 0.4 && wasapiOpen > 0.2, $"{what}: both lanes go out while talking ({asioOpen:0.000}, {wasapiOpen:0.000})");
                    }
                }
                said.Add("Both: WASAPI, ASIO and both paths each hold only their own lanes, on both outputs");
            }
            else
            {
                // The one lane running is WASAPI's in WASAPI only, ASIO's in ASIO only.
                var own = config == SimConfig.WasapiOnly ? PushToTalkPath.Wasapi : PushToTalkPath.Asio;
                var other = own == PushToTalkPath.Wasapi ? PushToTalkPath.Asio : PushToTalkPath.Wasapi;
                var held = PttRun(config, buffer, new SimPushToTalk(PttOn(own), talk), 6);
                var free = PttRun(config, buffer, new SimPushToTalk(PttOn(other), talk), 6);
                var heldShut = HeardAmplitude(held, 1.0 + d0, 2.8, Simulation.ToneHz);
                var freeShut = HeardAmplitude(free, 1.0 + d0, 2.8, Simulation.ToneHz);
                Check(heldShut < 0.01, $"{config}, path {PushToTalkSettings.PathName(own)}: its own lane must be held while not talking ({heldShut:0.000})");
                Check(freeShut > 0.4, $"{config}, path {PushToTalkSettings.PathName(other)}: the lane it doesn't hold must go out as normal ({freeShut:0.000})");
                Check(free.Findings.Clean, $"{config}: and clean ({SimRunner.Describe(free)})");
                said.Add($"{config}: {PushToTalkSettings.PathName(own)} held, {PushToTalkSettings.PathName(other)} left alone");
            }
        }
        return string.Join("; ", said);
    }

    /// <summary>PUSH TO TALK BY SOUND LEVEL: sound under the level never goes out; sound at it opens push to talk at once
    /// (the ordinary delay and no more); it shuts half a second after the sound drops below; and the slider is honoured - a
    /// level above the sound keeps it shut. The clicks that time the delay are off here: they would open it themselves.</summary>
    private static string? PushToTalkOpensOnItsLevel()
    {
        var said = new List<string>();
        const float quiet = 0.005f;   // -46 dB, under the -40 dB level
        const float loud = 0.3f;      // -10.5 dB, over it
        var levels = new[] { (0.0, quiet), (2.0, loud), (4.0, quiet) };
        foreach (var (config, buffer) in PttSetups)
        {
            var d0 = PttBaseDelayMs(config, buffer) / 1000;
            var settings = new PushToTalkSettings { Enabled = true, Trigger = PushToTalkTrigger.Level, LevelDb = -40 };
            var r = PttRun(config, buffer, new SimPushToTalk(settings, [], levels), 6, clicks: false);
            var audio = r.Audio!;
            var times = r.AudioTimes;
            Check(HeardPeak(r, 0.5, 2.0 + d0 - 0.02) <= 0.0005,
                $"{config}: sound under the level must never go out ({HeardPeak(r, 0.5, 2.0 + d0 - 0.02):0.0000}, the sound being {quiet})");
            var onset = FirstAbove(audio, FrameHeardAt(times, 2.0), 0.15f);
            Check(onset >= 0, $"{config}: sound over the level must go out");
            var toSound = (HeardAt(times, onset) - 2.0) * 1000;
            Check(Math.Abs(toSound - d0 * 1000) <= 15,
                $"{config}: AT ONCE - sound reaching the level must be heard the ordinary delay later: {toSound:0.0} ms, the stream's delay being {d0 * 1000:0.0} ms");
            var last = LastAbove(audio, FrameHeardAt(times, 4.0 + d0 + 0.05), FrameHeardAt(times, 5.9), 0.0015f);
            Check(last >= 0, $"{config}: premise - the quiet sound still goes out for the half second after");
            var hang = (HeardAt(times, last) - 4.0 - d0) * 1000;
            Check(hang is >= 470 and <= 530, $"{config}: it must shut half a second after the sound drops below the level ({hang:0} ms)");
            Check(HeardPeak(r, 4.5 + d0 + 0.04, 5.9) <= 0.0005, $"{config}: and then nothing goes out");

            var high = new PushToTalkSettings { Enabled = true, Trigger = PushToTalkTrigger.Level, LevelDb = -8 };
            var shut = PttRun(config, buffer, new SimPushToTalk(high, [], levels), 6, clicks: false);
            Check(HeardPeak(shut, 0.5, 5.9) <= 0.0005, $"{config}: THE SLIDER - with the level at -8 dB, sound at -10.5 dB must never open it ({HeardPeak(shut, 0.5, 5.9):0.0000})");
            said.Add($"{config}: opened {toSound:0.0} ms after the sound (delay {d0 * 1000:0.0}), shut {hang:0} ms after it dropped");
        }
        return string.Join("; ", said);
    }

    /// <summary>PUSH TO TALK AND RECORDING: with the bypass off, the recording holds silence on the push to talk path while
    /// not talking, just as a listener hears it; with it on, everything coming out of the send channels - and in both
    /// cases the wire is held the same.</summary>
    private static string? PushToTalkRecordsAsHeard()
    {
        var said = new List<string>();
        foreach (var (config, buffer) in PttSetups)
        {
            var d0 = PttBaseDelayMs(config, buffer) / 1000;
            var toneLane = config == SimConfig.WasapiOnly ? RenderRoute.Mixed : RenderRoute.AsioLane;
            foreach (var bypass in new[] { false, true })
            {
                var settings = PttOn();
                settings.BypassForRecording = bypass;
                var r = PttRun(config, buffer, new SimPushToTalk(settings, KeyMoments((2.0, true), (4.0, false))), 5);
                var mine = r.Recorded.Where(x => x.Lane == toneLane).ToList();
                Check(mine.Count > 100, $"{config}: premise - the recording tap was handed the tone's lane ({mine.Count} blocks)");
                var shut = mine.Where(x => x.Time is > 0.2 and < 1.98 || x.Time is > 4.02 and < 4.9).ToList();
                var open = mine.Where(x => x.Time is > 2.02 and < 3.98).ToList();
                Check(open.All(x => x.Peak > 0.4), $"{config}, bypass {(bypass ? "on" : "off")}: while talking the recording holds the sound");
                if (bypass)
                    Check(shut.All(x => x.Peak > 0.4), $"{config}: BYPASS ON - the recording must hold everything sent, talking or not ({shut.Count(x => x.Peak <= 0.4)} silent blocks)");
                else
                    Check(shut.All(x => x.Peak == 0f), $"{config}: BYPASS OFF - while not talking the recording must hold silence, as a listener hears it ({shut.Max(x => x.Peak):0.0000})");
                Check(HeardPeak(r, 0.5, 2.0 + d0 - 0.02) <= 0.001, $"{config}, bypass {(bypass ? "on" : "off")}: the recording bypass never opens the wire");
            }
            said.Add($"{config}: silence recorded while not talking, everything with the bypass");
        }
        return string.Join("; ", said);
    }

    /// <summary>PUSH TO TALK AND THE DAW PLUGIN: a DAW track (440 Hz on the plugin lane) waits for talking like your own
    /// sound; with the plugin bypass ticked it goes out regardless.</summary>
    private static string? PushToTalkHoldsTheDawTrack()
    {
        var said = new List<string>();
        foreach (var (config, buffer) in PttSetups)
        {
            var d0 = PttBaseDelayMs(config, buffer) / 1000;
            foreach (var bypass in new[] { false, true })
            {
                var settings = PttOn();
                settings.BypassForPlugin = bypass;
                var r = PttRun(config, buffer, new SimPushToTalk(settings, KeyMoments((3.0, true), (5.0, false)), PluginTrack: true), 6);
                var shut = HeardAmplitude(r, 1.0 + d0, 2.8, SimPushToTalk.PluginHz);
                var open = HeardAmplitude(r, 3.2 + d0, 4.8, SimPushToTalk.PluginHz);
                Check(open > 0.2, $"{config}, plugin bypass {(bypass ? "on" : "off")}: premise - the DAW track goes out while talking ({open:0.000})");
                if (bypass) Check(shut > 0.2, $"{config}: BYPASS ON - the DAW track must go out talking or not ({shut:0.000})");
                else Check(shut < 0.01, $"{config}: BYPASS OFF - the DAW track must wait for talking ({shut:0.000})");
                Check(HeardAmplitude(r, 1.0 + d0, 2.8, Simulation.ToneHz) < 0.01, $"{config}: your own sound is held either way");
            }
            said.Add($"{config}: the track waits, and goes regardless with the bypass");
        }
        return string.Join("; ", said);
    }

    // ---------------- the rest ----------------

    /// <summary>PUSH TO TALK KEYS, press by press: every combination of the two boxes, repeats while held, the edge of a
    /// double tap at the medium speed (500 ms; every speed's edge is in the speed step), a third quick press, and a change of
    /// settings letting go.</summary>
    private static string? PushToTalkKeyLogic()
    {
        var k = new PushToTalkKeyState();
        bool Run(bool hold, bool dbl, params (long Ms, bool Down)[] events)
        {
            k.Configure(hold, dbl);
            foreach (var (ms, down) in events) { if (down) k.Press(ms); else k.Release(ms); }
            return k.Talking;
        }
        // Hold to speak.
        Check(Run(true, false, (0, true)), "hold: pressed, talking");
        Check(!Run(true, false, (0, true), (100, false)), "hold: let go, not talking");
        k.Configure(true, false);
        k.Press(0);
        Check(!k.Press(30) && k.Talking, "a key held repeats; a repeat is not a press and changes nothing");
        // Where a repeat would do harm: tapping toggles, and a double tap locks.
        Check(Run(false, false, (0, true), (30, true), (60, true), (90, true)), "neither box: a key held down repeating must not toggle talking off and on");
        Check(!Run(false, true, (0, true), (30, true), (60, true), (500, false)), "double tap: a key held down repeating is not a double tap");
        // Neither: a tap toggles.
        Check(Run(false, false, (0, true), (50, false)), "neither: one tap turns talking on");
        Check(!Run(false, false, (0, true), (50, false), (2000, true), (2050, false)), "neither: the next tap turns it off");
        // Double tap to hold alone.
        Check(!Run(false, true, (0, true), (50, false)), "double tap: a single tap does nothing");
        Check(Run(false, true, (0, true), (50, false), (500, true), (550, false)), "double tap: two presses 500 ms apart lock talking on");
        Check(!Run(false, true, (0, true), (50, false), (501, true), (550, false)), "double tap: 501 ms apart is two single taps");
        // One tap releases it (Ed, 2026-09-29: "double tap needed to turn push to talk off at the moment, single tap should do it").
        Check(!Run(false, true, (0, true), (50, false), (300, true), (350, false), (2000, true)),
            "double tap: ONE tap releases it - it used to take another double tap");
        Check(!Run(false, true, (0, true), (50, false), (300, true), (350, false), (600, true), (650, false)),
            "double tap: a third press, however quick, releases it");
        Check(!Run(false, true, (0, true), (50, false), (300, true), (350, false), (2000, true), (2050, false), (2300, true), (2350, false)),
            "double tap: the releasing tap starts no new pair - a quick press after it does not lock again");
        // Both.
        Check(Run(true, true, (0, true)), "both: holding talks");
        Check(!Run(true, true, (0, true), (500, false)), "both: let go without a double tap, it stops");
        Check(Run(true, true, (0, true), (50, false), (300, true), (350, false)), "both: a double tap locks it on after the release");
        Check(Run(true, true, (0, true), (50, false), (300, true), (350, false), (3000, true)),
            "both: the releasing tap still talks while held");
        Check(!Run(true, true, (0, true), (50, false), (300, true), (350, false), (3000, true), (3050, false)),
            "both: and stops when let go - one tap, not a double");
        // A change of settings lets go of anything held or locked.
        Run(false, true, (0, true), (50, false), (300, true), (350, false));
        k.Configure(false, true);
        Check(!k.Talking, "a change of settings starts from silence");
        return "hold, a tap, a double tap (within 500 ms, not 501), one tap to release, and both behave press by press; repeats ignored; a change lets go";
    }

    /// <summary>
    /// THE PUSH TO TALK WINDOW, driven as a person does: the button after Send my audio says it is disabled; opened from it
    /// on the hidden desktop through the control channel, the settings are changed control by control and OK keeps them -
    /// in the profile, marked unsaved, in the sender's gate, in the log - and the button says enabled. The trigger list
    /// switches what can be changed, and Cancel changes nothing.
    /// </summary>
    private static string? PushToTalkWindowSavesToTheProfile()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        var restoreWait = RemoteControl.CommandWait;
        RemoteControl.CommandWait = TimeSpan.FromSeconds(1);
        MainForm? form = null;
        RemoteControlServer? server = null;
        var pipe = "RemSound.control.selftest.ptt." + Guid.NewGuid().ToString("N")[..8];
        try
        {
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            var main = form;
            var logged = new List<string>();
            main.LogForTest.EventTapForTest = line => { lock (logged) logged.Add(line); };
            Check(main.PushToTalkButtonForTest.AccessibleName == "Push to talk settings, currently disabled",
                $"a new profile's button must say push to talk is disabled ({main.PushToTalkButtonForTest.AccessibleName})");
            server = new RemoteControlServer(pipe, () => main, new RemoteControlEngine(() => main));
            server.Start();
            string Ask(string command)
            {
                var reply = RemoteControl.Send(command, pipe, out var reached, connectTimeoutMs: 5000);
                Check(reached, $"the control channel did not answer \"{command}\": {reply}");
                return reply;
            }
            bool Ok(string reply) => reply.StartsWith("ok", StringComparison.Ordinal);

            // Cancel first: nothing changes.
            DriveWhilePumping(main, () =>
            {
                Check(Ask("click pushToTalkButton").Contains("\"Push to talk settings\"", StringComparison.Ordinal), "the button must open Push to talk settings");
                Check(Ok(Ask("set enableBox on")), "Enable push to talk must tick");
                Check(Ok(Ask("answer Cancel")), "Cancel closes it");
            }, TimeSpan.FromSeconds(30));
            Check(!main.SettingsForTest.LoadPushToTalkSettings().Enabled && !main.SenderForTest.PushToTalk.Enabled,
                "CANCEL: a change then Cancel must leave push to talk off");

            DriveWhilePumping(main, () =>
            {
                Ask("click pushToTalkButton");
                Check(Ok(Ask("set enableBox on")), "Enable push to talk must tick");
                Check(Ok(Ask("select pathList WASAPI")), "the path list must take WASAPI");
                Check(Ok(Ask("select triggerList \"Sound reaching a level\"")), "the trigger list must take sound reaching a level");
                var disabled = main.Invoke(() => Application.OpenForms.OfType<PushToTalkDialog>().Single() is var d
                    && !d.HoldBoxForTest.Enabled && !d.DoubleTapBoxForTest.Enabled && d.LevelBarForTest.Enabled);
                Check(disabled, "by sound level: the key's two boxes are off and the level can be set");
                Check(Ok(Ask("set levelBar -30")), "the level slider must take -30");
                Check(main.Invoke(() => Application.OpenForms.OfType<PushToTalkDialog>().Single().LevelBarForTest.AccessibleName)
                      == "Send when sound reaches this level: -30 dB", "the slider's name must say its level for the screen reader");
                Check(Ok(Ask("select triggerList \"A key\"")), "back to a key");
                Check(main.Invoke(() => Application.OpenForms.OfType<PushToTalkDialog>().Single() is var d2 && d2.HoldBoxForTest.Enabled && !d2.LevelBarForTest.Enabled),
                    "by key: the two boxes can be changed and the level cannot");
                Check(Ok(Ask("set doubleTapBox on")), "Double tap to hold must tick");
                Check(Ok(Ask("select speedList \"Slow, 0.9 seconds\"")), "the double tap speed list must take slow");
                Check(Ok(Ask("set bypassRecordingBox on")), "the recording bypass must tick");
                Check(Ok(Ask("set bypassPluginBox on")), "the plugin bypass must tick");
                Check(Ok(Ask("answer OK")), "OK closes it");
            }, TimeSpan.FromSeconds(30));

            var saved = main.SettingsForTest.LoadPushToTalkSettings();
            Check(saved is { Enabled: true, Path: PushToTalkPath.Wasapi, Trigger: PushToTalkTrigger.Key, HoldToSpeak: true, DoubleTapToHold: true,
                    DoubleTapSpeed: PushToTalkDoubleTapSpeed.Slow, LevelDb: -30, BypassForRecording: true, BypassForPlugin: true },
                $"OK must keep every setting in the profile ({saved.Describe()}, level {saved.LevelDb})");
            Check(main.UnsavedChangesForTest, "and mark the profile unsaved");
            Check(main.SenderForTest.PushToTalk.Enabled && main.SenderForTest.PushToTalk.Settings?.Path == PushToTalkPath.Wasapi,
                "and put them in the sender's gate at once");
            Check(main.PushToTalkButtonForTest.AccessibleName == "Push to talk settings, currently enabled", "and the button must say enabled");
            // The key now goes by slow: two presses 0.8 s apart lock talking on (at medium they would be two single taps).
            main.PushToTalkKeyForTest(true, 100_000);
            main.PushToTalkKeyForTest(false, 100_050);
            main.PushToTalkKeyForTest(true, 100_800);
            main.PushToTalkKeyForTest(false, 100_850);
            Check(main.PushToTalkStatusForTest == "Push to talk: talking",
                $"with slow chosen, two presses of the key 0.8 s apart must lock talking on ({main.PushToTalkStatusForTest})");
            lock (logged) Check(logged.Any(l => l.Contains("ui: push to talk", StringComparison.Ordinal) && l.Contains("on, WASAPI", StringComparison.Ordinal)),
                $"and the change must be logged ({string.Join(" | ", logged.TakeLast(3))})");
            main.LogForTest.EventTapForTest = null;
        }
        finally
        {
            server?.Dispose();
            RemoteControl.CommandWait = restoreWait;
            try { form?.Dispose(); } catch { /* teardown */ }
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
        }
        return "the button said disabled, then enabled; Cancel changed nothing; OK kept every setting in the profile (unsaved), in the "
             + "sender's gate and in the log; the trigger list switched between the key's boxes and the level";
    }

    /// <summary>
    /// PUSH TO TALK IN THE WINDOW: the key through the window's own path (the hook's, and the control channel's "talk")
    /// opens and shuts the sender's gate, plays the on and off cues in key mode and never in sound-level mode, logs each
    /// change, and the status says whether you are talking. Off, the key does nothing.
    /// </summary>
    private static string? PushToTalkKeyThroughTheWindow()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        var restoreNoDevice = CuePlayer.NoDeviceForTest;
        CuePlayer.NoDeviceForTest = true;
        MainForm? form = null;
        var pipe = "RemSound.control.selftest.talk." + Guid.NewGuid().ToString("N")[..8];
        RemoteControlServer? server = null;
        try
        {
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            var main = form;
            var gate = main.SenderForTest.PushToTalk;
            var logged = new List<string>();
            main.LogForTest.EventTapForTest = line => { lock (logged) logged.Add(line); };
            CuePlayer.GloballyMuted = false;

            // Off: the key does nothing.
            main.PushToTalkKeyForTest(true, 0);
            Check(gate.IsOpen && main.PushToTalkStatusForTest is null, "with push to talk off, sound goes out and the key does nothing");
            main.PushToTalkKeyForTest(false, 50);

            main.ApplyPushToTalkForTest(PttOn());
            Check(!gate.IsOpen, "on by key: nothing goes out until the key");
            Check(main.PushToTalkStatusForTest == "Push to talk: not talking", $"and the status says so ({main.PushToTalkStatusForTest})");
            var plays = CuePlayer.PlaysStartedForTest;
            main.PushToTalkKeyForTest(true, 1000);
            Check(gate.IsOpen, "THE KEY: pressed through the window, the gate opens");
            Check(CuePlayer.PlaysStartedForTest == plays + 1, $"and the push to talk on cue plays ({CuePlayer.PlaysStartedForTest - plays})");
            Check(main.PushToTalkStatusForTest == "Push to talk: talking", "and the status says talking");
            main.PushToTalkKeyForTest(true, 1030);
            Check(CuePlayer.PlaysStartedForTest == plays + 1, "a repeat plays nothing more");
            main.PushToTalkKeyForTest(false, 1200);
            Check(!gate.IsOpen && CuePlayer.PlaysStartedForTest == plays + 2, "let go: it shuts, and the off cue plays");
            lock (logged)
                Check(logged.Any(l => l.Contains("push to talk: talking (key, hold to speak)", StringComparison.Ordinal))
                      && logged.Any(l => l.Contains("push to talk: stopped talking", StringComparison.Ordinal)),
                    $"each change is logged ({string.Join(" | ", logged.TakeLast(3))})");

            // The control channel's talk command takes the same path.
            server = new RemoteControlServer(pipe, () => main, new RemoteControlEngine(() => main));
            server.Start();
            string Ask(string command) => RemoteControl.Send(command, pipe, out _, connectTimeoutMs: 5000);
            string down = "", state = "", up = "";
            DriveWhilePumping(main, () => { down = Ask("talk down"); state = Ask("talk state"); up = Ask("talk up"); }, TimeSpan.FromSeconds(20));
            Check(down == "ok: Push to talk: talking" && state == "ok: Push to talk: talking" && up == "ok: Push to talk: not talking",
                $"the control channel's talk must press and release the key ({down} / {state} / {up})");

            // Sound-level mode: the cues stay silent, and the key does nothing.
            main.ApplyPushToTalkForTest(new PushToTalkSettings { Enabled = true, Trigger = PushToTalkTrigger.Level });
            plays = CuePlayer.PlaysStartedForTest;
            main.PushToTalkKeyForTest(true, 5000);
            main.PushToTalkKeyForTest(false, 5100);
            Check(CuePlayer.PlaysStartedForTest == plays, "by sound level: no cue, and the key does nothing");
            main.LogForTest.EventTapForTest = null;
        }
        finally
        {
            server?.Dispose();
            try { form?.Dispose(); } catch { /* teardown */ }
            CuePlayer.GloballyMuted = restoreMuted;
            CuePlayer.NoDeviceForTest = restoreNoDevice;
        }
        return "off, the key did nothing; on, the key opened and shut the gate, with the on and off cues and a log line each, the status "
             + "said talking and not talking; the control channel's talk did the same; by sound level, no cues";
    }

    /// <summary>
    /// THE PUSH TO TALK KEY'S HOOK, decided without a hook: the key (Scroll Lock alone by default) is taken - press, repeats
    /// and release - and nothing else is; a press with other modifiers is left alone; while a window for choosing a key is
    /// open, the key reaches it; unset, nothing is taken. And no copy the self-test or a --headless start runs ever installs
    /// the real hook: it would take the key from the person at the computer.
    /// </summary>
    private static string? PushToTalkKeyHookTakesOnlyItsKey()
    {
        const int down = 0x0100, up = 0x0101;
        var events = new List<bool>();
        var hook = new PushToTalkKeyHook((isDown, _) => events.Add(isDown));
        hook.Arm(HotkeyInfo.Unset);   // arming unset installs nothing
        Check(!hook.Installed, "an unset key installs no hook");
        var scroll = (int)System.Windows.Forms.Keys.Scroll;
        Check(!hook.Handle(down, scroll, false, false, false, 0), "unset: Scroll Lock is left for Windows");

        // The decision, as the hook would make it, with the key set but no hook installed.
        var decide = new PushToTalkKeyHook((isDown, _) => events.Add(isDown));
        decide.UseKeyForTest(RemSoundSettingsStore.PushToTalkDefaultKey);
        Check(decide.Handle(down, scroll, false, false, false, 0) && events.SequenceEqual([true]), "Scroll Lock pressed: taken, and talking starts");
        Check(decide.Handle(down, scroll, false, false, false, 30) && events.Count == 1, "its repeats: taken, and nothing more");
        Check(!decide.Handle(down, (int)System.Windows.Forms.Keys.A, false, false, false, 40), "another key is left alone");
        Check(decide.Handle(up, scroll, false, false, false, 100) && events.SequenceEqual([true, false]), "let go: taken, and talking stops");
        Check(!decide.Handle(down, scroll, true, false, false, 200) && events.Count == 2, "Ctrl+Scroll Lock is not the key: left alone");
        Check(!decide.Handle(up, scroll, false, false, false, 210), "and a release not ours is left alone");
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);   // off the screen and out of focus while it is open
        try
        {
            using var capture = MainFormHotkeyController.NewHotkeyCaptureForm(allowSingleKey: true);
            capture.Show();
            try { Check(!decide.Handle(down, scroll, false, false, false, 300) && events.Count == 2, "while a window for choosing a key is open, the key reaches it"); }
            finally { capture.Close(); }
        }
        finally { Windowless.SetActiveForTest(false); }
        Check(decide.Handle(down, scroll, false, false, false, 400) && decide.Handle(up, scroll, false, false, false, 450), "and afterwards it is taken again");

        // The window for choosing a key takes Scroll Lock alone for push to talk, and nowhere else.
        using (var single = new HotkeyCaptureForm(allowSingleKey: true))
        {
            _ = single.Handle;
            single.BoxForTest.Select();
            single.KeyDownForTest(System.Windows.Forms.Keys.Scroll);
            single.KeyUpForTest(System.Windows.Forms.Keys.Scroll);
            Check(single.CapturedHotkey == RemSoundSettingsStore.PushToTalkDefaultKey, $"the push to talk key's window must take Scroll Lock on its own ({single.CapturedHotkey})");
        }
        using (var combo = new HotkeyCaptureForm())
        {
            _ = combo.Handle;
            combo.BoxForTest.Select();
            combo.KeyDownForTest(System.Windows.Forms.Keys.Scroll);
            combo.KeyUpForTest(System.Windows.Forms.Keys.Scroll);
            Check(combo.CapturedHotkey is null, "every other shortcut still needs Ctrl, Shift or Alt");
        }
        Check(RemSoundSettingsStore.PushToTalkDefaultKey.ToString() == "Scroll Lock", "and it reads as Scroll Lock");

        // Never installed in a copy the self-test runs, even on by key.
        using var form = new MainForm(null, Profile.NewBlank(), null, null, headless: true);
        form.ApplyPushToTalkForTest(PttOn());
        Check(!form.PushToTalkHookInstalledForTest, "a --headless copy or the self-test must never install the hook - it would take the key from the person at the computer");
        return "Scroll Lock taken - press, repeats and release - and nothing else; left alone with other modifiers, while choosing a key, "
             + "and when unset; chosen alone only for push to talk; never hooked in a headless copy or the self-test";
    }

    /// <summary>PUSH TO TALK IN THE PROFILE: off in a new profile; every setting kept with the profile; a profile saved
    /// before push to talk (v6.0) loads with it off; the key is kept on this computer, Scroll Lock by default, and listed in
    /// Keyboard shortcuts with the others.</summary>
    private static string? PushToTalkInTheProfile()
    {
        Check(!Profile.NewBlank().PushToTalk.Enabled && !new Profile().PushToTalk.Enabled, "a new profile has push to talk off");
        var fresh = Profile.NewBlank().PushToTalk;
        Check(fresh is { Path: PushToTalkPath.Both, Trigger: PushToTalkTrigger.Key, HoldToSpeak: true, DoubleTapToHold: true, LevelDb: -40,
                BypassForRecording: false, BypassForPlugin: false },
            $"a new profile's push to talk: both paths, by key, hold to speak AND double tap to hold on (Ed), -40 dB, both bypasses off ({fresh.Describe()})");
        var store = new RemSoundSettingsStore("RemSound");
        var backup = new Profile();
        store.CopyTo(backup);
        try
        {
            var p = Profile.NewBlank();
            p.PushToTalk = new PushToTalkSettings { Enabled = true, Path = PushToTalkPath.Asio, Trigger = PushToTalkTrigger.Level, HoldToSpeak = false,
                DoubleTapToHold = true, DoubleTapSpeed = PushToTalkDoubleTapSpeed.Fast, LevelDb = -55, BypassForRecording = true, BypassForPlugin = true };
            store.ApplyProfile(p);
            var back = new Profile();
            store.CopyTo(back);
            Check(back.PushToTalk.SameAs(p.PushToTalk), $"every setting must round-trip the profile ({back.PushToTalk.Describe()})");
            var json = System.Text.Json.JsonSerializer.Serialize(p);
            var reread = System.Text.Json.JsonSerializer.Deserialize<Profile>(json)!;
            Check(reread.PushToTalk.SameAs(p.PushToTalk), "and survive the profile file");
            var old = System.Text.Json.JsonSerializer.Deserialize<Profile>(V60Profile)!;
            Check(old.PushToTalk is { Enabled: false, DoubleTapSpeed: PushToTalkDoubleTapSpeed.Medium }, "a profile saved by v6.0, before push to talk, must load with it off, and medium");
        }
        finally { store.ApplyProfile(backup); }

        var scratch = Path.Combine(Path.GetTempPath(), "remsound-ptt-key-" + Guid.NewGuid().ToString("N"));
        using (AppConfig.UseThrowawayUserDataDirectory(scratch))
        {
            var keys = new RemSoundSettingsStore("RemSound");
            Check(keys.LoadPushToTalkHotkey() == RemSoundSettingsStore.PushToTalkDefaultKey, "the key is Scroll Lock by default");
            keys.SavePushToTalkHotkey(new HotkeyInfo(System.Windows.Forms.Keys.F13, false, false, false));
            Check(new RemSoundSettingsStore("RemSound").LoadPushToTalkHotkey().Key == System.Windows.Forms.Keys.F13, "a key chosen is kept on this computer");
            var controller = new MainFormHotkeyController(keys, () => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { },
                () => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { });
            using var shortcuts = controller.BuildKeyboardShortcutsDialogForAudit();
            var rows = shortcuts.Controls.Cast<Control>().SelectMany(c => c.Controls.Cast<Control>().Prepend(c)).OfType<ListBox>()
                .SelectMany(l => l.Items.Cast<object>().Select(i => i.ToString() ?? "")).ToList();
            Check(rows.Any(r => r.StartsWith("Push to talk, set up in Push to talk settings", StringComparison.Ordinal) && r.EndsWith("F13", StringComparison.Ordinal)),
                $"Keyboard shortcuts must list the push to talk key and say where the rest is ({rows.LastOrDefault()})");
        }
        return "off in a new profile, every setting kept with it and in the file, a v6.0 profile loads with it off; the key kept on this computer, "
             + "Scroll Lock by default, and listed in Keyboard shortcuts";
    }

    /// <summary>PUSH TO TALK DOUBLE TAP SPEED (Ed, 2026-09-28: "slow medium fast"): 0.9, 0.5 and 0.3 seconds, medium unless
    /// changed. Press by press, each speed's edge to the millisecond. On the wire, in every setup: two taps 0.7 s apart lock
    /// talking on at slow and are two single taps at medium; 0.4 s apart lock it at medium and not at fast; 0.25 s at fast.</summary>
    private static string? PushToTalkDoubleTapSpeeds()
    {
        Check(new PushToTalkSettings().DoubleTapSpeed == PushToTalkDoubleTapSpeed.Medium && Profile.NewBlank().PushToTalk.DoubleTapSpeed == PushToTalkDoubleTapSpeed.Medium,
            "medium unless changed");
        var k = new PushToTalkKeyState();
        foreach (var (speed, ms) in new[] { (PushToTalkDoubleTapSpeed.Slow, 900), (PushToTalkDoubleTapSpeed.Medium, 500), (PushToTalkDoubleTapSpeed.Fast, 300) })
        {
            Check(PushToTalkSettings.DoubleTapMs(speed) == ms, $"{speed} must be {ms} ms ({PushToTalkSettings.DoubleTapMs(speed)})");
            bool Locks(long gap)
            {
                k.Configure(false, true, speed);
                k.Press(10_000); k.Release(10_050); k.Press(10_000 + gap); k.Release(10_050 + gap);
                return k.Talking;
            }
            Check(Locks(ms), $"{speed}: two presses {ms} ms apart must lock talking on");
            Check(!Locks(ms + 1), $"{speed}: {ms + 1} ms apart must be two single taps");
        }

        var said = new List<string>();
        foreach (var (config, buffer) in PttSetups)
        {
            var d0 = PttBaseDelayMs(config, buffer) / 1000;
            // Double tap to hold alone, so a single tap does nothing: what goes out after both taps says whether they locked.
            (double Tone, float Before, float After) Taps(PushToTalkDoubleTapSpeed speed, double gap)
            {
                var settings = PttOn(hold: false, doubleTap: true);
                settings.DoubleTapSpeed = speed;
                var r = PttRun(config, buffer, new SimPushToTalk(settings, KeyMoments((2.0, true), (2.05, false), (2.0 + gap, true), (2.05 + gap, false))), 4.5);
                return (HeardAmplitude(r, 3.2 + d0, 4.2 + d0, Simulation.ToneHz), HeardPeak(r, 0.5 + d0, 1.95 + d0), HeardPeak(r, 3.2 + d0, 4.2 + d0));
            }
            foreach (var (speed, gap, locks) in new[]
            {
                (PushToTalkDoubleTapSpeed.Slow, 0.7, true), (PushToTalkDoubleTapSpeed.Medium, 0.7, false),
                (PushToTalkDoubleTapSpeed.Medium, 0.4, true), (PushToTalkDoubleTapSpeed.Fast, 0.4, false),
                (PushToTalkDoubleTapSpeed.Fast, 0.25, true),
            })
            {
                var (tone, before, after) = Taps(speed, gap);
                Check(before <= 0.001, $"{config}, {speed}, taps {gap} s apart: nothing may go out before the taps ({before:0.0000})");
                if (locks) Check(tone > 0.4, $"{config}, {speed}: two taps {gap} s apart must lock talking on (tone {tone:0.000} after them)");
                else Check(after <= 0.001, $"{config}, {speed}: two taps {gap} s apart are two single taps - nothing may go out after them ({after:0.0000})");
            }
            said.Add($"{config}: 0.7 s locks at slow only, 0.4 s at medium not fast, 0.25 s at fast");
        }
        return "each speed's edge to the millisecond; " + string.Join("; ", said);
    }

    /// <summary>PUSH TO TALK DOUBLE TAP PREVIEW (Ed, 2026-09-28: "as you go up and down the list, you hear a keyboard key ...
    /// getting tapped 2 times, slow med fast with the exact timing"). Measured offline: each speed's preview is the shipped
    /// key sound, then silence, then the same sound starting exactly that speed's interval after the first, to the sample.
    /// In the window: it opens on medium without a sound; every move along the list plays that speed's preview; the list
    /// can be used only with a key trigger and double tap to hold ticked; with RemSound's sounds muted a move is silent;
    /// the speed chosen is the window's result.</summary>
    private static string? PushToTalkDoubleTapPreview()
    {
        var measured = new List<string>();
        using (var preview = new DoubleTapPreview())
        {
            var clip = preview.LoadClip() ?? [];
            Check(clip.Length > 1000, "premise: the shipped key sound (key 1.wav) must load");
            var threshold = clip.Max(Math.Abs) * 0.05f;
            int FirstAbove(float[] a, int from) { for (var i = from; i < a.Length; i++) if (Math.Abs(a[i]) >= threshold) return i; return -1; }
            float Peak(float[] a, int from, int to) { var p = 0f; for (var i = from; i < to; i++) p = Math.Max(p, Math.Abs(a[i])); return p; }
            foreach (var speed in new[] { PushToTalkDoubleTapSpeed.Slow, PushToTalkDoubleTapSpeed.Medium, PushToTalkDoubleTapSpeed.Fast })
            {
                var ms = PushToTalkSettings.DoubleTapMs(speed);
                var r = preview.Render(ms)!;
                var frames = ms * DoubleTapPreview.SampleRate / 1000;
                Check(frames * 2 >= clip.Length, $"premise: at {speed} the second tap starts after the first has ended");
                var first = FirstAbove(r, 0);
                var second = FirstAbove(r, clip.Length);
                Check(first >= 0 && second >= 0, $"{speed}: two taps must be heard");
                Check((second - first) / 2 == frames && (second - first) % 2 == 0,
                    $"{speed}: the second tap must start exactly {ms} ms ({frames} frames) after the first ({(second - first) / 2.0} frames)");
                Check(Peak(r, clip.Length, second - first) == 0f, $"{speed}: nothing may sound between the end of the first tap and the start of the second");
                Check(Peak(r, 0, clip.Length) == Peak(r, second - first, r.Length), $"{speed}: the two taps must be the same key, as loud");
                Check(r.Length == frames * 2 + clip.Length, $"{speed}: nothing may follow the second tap");
                measured.Add($"{speed} {(second - first) / 2.0 / DoubleTapPreview.SampleRate * 1000:0.000} ms");
            }
        }

        var restoreMuted = CuePlayer.GloballyMuted;
        var restoreNoDevice = CuePlayer.NoDeviceForTest;
        CuePlayer.GloballyMuted = false;
        CuePlayer.NoDeviceForTest = true;
        try
        {
            using var dialog = new PushToTalkDialog(new PushToTalkSettings { Enabled = true }, RemSoundSettingsStore.PushToTalkDefaultKey, _ => null);
            var list = dialog.SpeedListForTest;
            var p = dialog.PreviewForTest;
            var items = list.Items.Cast<object>().Select(o => o.ToString()).ToList();
            Check(items.SequenceEqual(["Slow, 0.9 seconds", "Medium, 0.5 seconds", "Fast, 0.3 seconds"]), $"the list must be slow, medium, fast ({string.Join(" | ", items)})");
            Check(list.SelectedIndex == 1 && p.PlaysForTest == 0, $"it must open on medium without a sound ({list.SelectedIndex}, {p.PlaysForTest} played)");
            Check(list.Enabled, "with a key trigger and double tap to hold ticked, the list can be used");
            var plays = 0;
            foreach (var (index, ms) in new[] { (0, 900), (2, 300), (1, 500), (0, 900) })
            {
                list.SelectedIndex = index;
                plays++;
                Check(p.PlaysForTest == plays && p.LastIntervalMsForTest == ms,
                    $"moving to {items[index]} must tap the key twice {ms} ms apart ({p.PlaysForTest} played, last {p.LastIntervalMsForTest} ms)");
            }
            dialog.DoubleTapBoxForTest.Checked = false;
            Check(!list.Enabled, "unticking double tap to hold must grey the speed out");
            dialog.DoubleTapBoxForTest.Checked = true;
            Check(list.Enabled, "and ticking it again brings it back");
            dialog.TriggerListForTest.SelectedIndex = 1;
            Check(!list.Enabled, "triggered by sound level, the speed is not used and cannot be changed");
            dialog.TriggerListForTest.SelectedIndex = 0;
            Check(p.PlaysForTest == plays, "nothing but a move along the list may sound it");
            CuePlayer.GloballyMuted = true;
            list.SelectedIndex = 2;
            Check(p.PlaysForTest == plays, "with RemSound's sounds muted, a move must be silent");
            CuePlayer.GloballyMuted = false;
            Check(dialog.Result.DoubleTapSpeed == PushToTalkDoubleTapSpeed.Fast, $"the speed chosen must be the window's result ({dialog.Result.DoubleTapSpeed})");
            Check(!dialog.Result.SameAs(new PushToTalkSettings { Enabled = true }), "a change of speed alone must count as a change, or OK would not keep it");
        }
        finally
        {
            CuePlayer.GloballyMuted = restoreMuted;
            CuePlayer.NoDeviceForTest = restoreNoDevice;
        }
        return "the second tap exactly on time (" + string.Join(", ", measured) + "); in the window, silent on opening, a preview on every move, "
             + "greyed out without a key or double tap, silent when muted, and the speed kept";
    }

    /// <summary>PUSH TO TALK IS NOT PART OF THE SERVICE (Ed: "not part of the service full stop"): a service profile with
    /// push to talk on still sends everything.</summary>
    private static string? PushToTalkIsNotInTheService()
    {
        var profile = new Profile { Title = "service ptt", WasapiSendMode = "devices" };
        profile.SelectedWasapiSendOutputs.Add("fake-device-id");
        profile.SelectedConnectedPeers.Add("127.0.0.1:47999");   // someone to send to, or the service goes no further
        profile.Password = RemSoundCrypto.Obfuscate("hunter2horse42stable");
        profile.PushToTalk = PttOn();
        var restorePresencePort = ServiceSendHost.PresencePortForTest;
        ServiceSendHost.PresencePortForTest = FreeUdpPort();
        using var restorePort = new Disposer(() => ServiceSendHost.PresencePortForTest = restorePresencePort);
        using var host = new ServiceSendHost(() => profile);
        host.ApplyProfile(profile);
        Check(host.SenderConfigForTest.Codec == AudioTransportCodec.Opus && host.SenderConfigForTest.TightLatency,
            "premise: the service set its sender up from this profile");
        Check(!host.PushToTalkEnabledForTest && host.PushToTalkOpenForTest,
            "the service must ignore push to talk and send everything, whatever its profile says");
        return "a service profile with push to talk on: the service's sender holds nothing";
    }
}
