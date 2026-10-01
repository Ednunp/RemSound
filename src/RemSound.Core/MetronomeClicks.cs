using System.Collections.Concurrent;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace RemSound.Core;

/// <summary>One click sound: the bar's first beat (the accent) and every other beat, stereo, interleaved, at
/// <see cref="SampleRate"/>. Copies at other rates (a DAW at 44.1 kHz) are made once and kept.</summary>
public sealed class MetronomeSound
{
    public const int SampleRate = 48000;

    private readonly ConcurrentDictionary<int, (float[] Accent, float[] Beat)> atRate = new();

    public MetronomeSound(string name, float[] accent, float[] beat)
    {
        Name = name;
        Accent = accent;
        Beat = beat;
        atRate[SampleRate] = (accent, beat);
    }

    public string Name { get; }
    public float[] Accent { get; }
    public float[] Beat { get; }

    /// <summary>The two sounds at another rate (linear interpolation: a click is short and bright, and this is only for a
    /// DAW running at something other than 48 kHz).</summary>
    public (float[] Accent, float[] Beat) At(int sampleRate) =>
        atRate.TryGetValue(sampleRate, out var made) ? made   // the render path: no allocation once made
            : atRate.GetOrAdd(sampleRate, r => (Resample(Accent, SampleRate, r), Resample(Beat, SampleRate, r)));

    /// <summary>Linear interpolation: no filter, so no delay - the click's first sample stays its first sample, which is the
    /// moment the metronome places. A band-limited resampler would start every click a few samples late and softened.</summary>
    private static float[] Resample(float[] stereo, int fromRate, int rate)
    {
        if (fromRate == rate) return stereo;
        var frames = stereo.Length / 2;
        var outFrames = (int)((long)frames * rate / fromRate);
        var result = new float[outFrames * 2];
        for (var f = 0; f < outFrames; f++)
        {
            var pos = f * (double)fromRate / rate;
            var i = (int)pos;
            var frac = (float)(pos - i);
            for (var c = 0; c < 2; c++)
            {
                var a = stereo[Math.Min(i, frames - 1) * 2 + c];
                var b = stereo[Math.Min(i + 1, frames - 1) * 2 + c];
                result[f * 2 + c] = a + (b - a) * frac;
            }
        }
        return result;
    }

    /// <summary>A WAV as stereo at 48 kHz, its leading silence taken off so the click starts on its first sample - which is
    /// the moment the metronome places. Null when it will not open.</summary>
    public static float[]? LoadStereo(string path)
    {
        try
        {
            using var reader = new AudioFileReader(path);
            ISampleProvider source = reader;
            if (source.WaveFormat.Channels == 1) source = new MonoToStereoSampleProvider(source);
            else if (source.WaveFormat.Channels != 2) return null;
            var rate = source.WaveFormat.SampleRate;
            var all = new List<float>();
            var buffer = new float[rate * 2];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0 && all.Count < rate * 2 * 4) all.AddRange(buffer.AsSpan(0, read).ToArray());
            var first = 0;
            while (first + 1 < all.Count && Math.Abs(all[first]) < 1e-3f && Math.Abs(all[first + 1]) < 1e-3f) first += 2;
            return first >= all.Count ? null : Resample([.. all.Skip(first)], rate, SampleRate);
        }
        catch { return null; }
    }
}

/// <summary>
/// The click sounds on offer: the shipped ones in <c>default sounds\metronome\</c>, and a person's own in a
/// <see cref="UserFolderName"/> folder beside their profiles - "&lt;name&gt; accent.wav" with "&lt;name&gt; beat.wav", or one
/// "&lt;name&gt;.wav" for both. Listed by name, shipped first in the order Ed's list gives them.
/// </summary>
public static class MetronomeSoundLibrary
{
    public const string UserFolderName = "Metronome sounds";

    /// <summary>The shipped sounds, in order.</summary>
    public static readonly string[] ShippedNames = ["Click", "Woodblock", "Beep", "Rim", "Cowbell"];

    public static string DefaultName => ShippedNames[0];

    public static string ShippedDirectory => Path.Combine(AppConfig.SoundsDirectory, "metronome");

    public static string UserDirectory => Path.Combine(AppConfig.UserDataDirectory, UserFolderName);

    /// <summary>Every sound name on offer: the shipped ones, then a person's own (a name already shipped is not offered twice).</summary>
    public static IReadOnlyList<string> Names()
    {
        var names = new List<string>();
        foreach (var name in ShippedNames)
            if (PathsFor(ShippedDirectory, name) is not null) names.Add(name);
        foreach (var name in PairNames(UserDirectory))
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        return names;
    }

    /// <summary>The names a folder holds a sound for, sorted.</summary>
    internal static IEnumerable<string> PairNames(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        try
        {
            return Directory.EnumerateFiles(directory, "*.wav")
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .Select(stem => stem.EndsWith(" accent", StringComparison.OrdinalIgnoreCase) ? stem[..^7]
                    : stem.EndsWith(" beat", StringComparison.OrdinalIgnoreCase) ? stem[..^5] : stem)
                .Where(n => n.Length > 0 && PathsFor(directory, n) is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return []; }
    }

    /// <summary>The two files for a name in a folder: the accent and the beat, or the one file for both.</summary>
    internal static (string Accent, string Beat)? PathsFor(string directory, string name)
    {
        var accent = Path.Combine(directory, name + " accent.wav");
        var beat = Path.Combine(directory, name + " beat.wav");
        if (File.Exists(accent) && File.Exists(beat)) return (accent, beat);
        var single = Path.Combine(directory, name + ".wav");
        return File.Exists(single) ? (single, single) : null;
    }

    /// <summary>Load a sound by name - a person's own first, so they can replace a shipped one. Null when there is none.</summary>
    public static MetronomeSound? Load(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var paths = PathsFor(UserDirectory, name) ?? PathsFor(ShippedDirectory, name);
        if (paths is not { } p) return null;
        var accent = MetronomeSound.LoadStereo(p.Accent);
        var beat = p.Beat == p.Accent ? accent : MetronomeSound.LoadStereo(p.Beat);
        return accent is null || beat is null ? null : new MetronomeSound(name, accent, beat);
    }
}

/// <summary>
/// Everything the audio threads need to place clicks, published whole by the window (a new object each change, swapped in
/// one write, so a render call always sees one consistent picture): the timeline on the master's clock, the one before it
/// (a tempo change is announced ahead, and the beats up to its bar line still belong to the old tempo), how this machine's
/// clock maps to the master's, the sound and its level, and this machine's clock itself.
/// </summary>
public sealed class MetronomeSchedule
{
    public MetronomeSchedule(MetronomeTimeline current, MetronomeTimeline? previous, MetronomeClockMap map, MetronomeSound? sound, float gain, bool muted,
        long runId, Func<long>? clock = null)
    {
        Current = current;
        RunId = runId;
        Previous = previous is { Running: true } p && current.Running && current.AnchorBeat > p.AnchorBeat ? previous : null;
        Map = map;
        Sound = sound;
        Gain = gain;
        Muted = muted;
        Clock = clock ?? MetronomeClock.NowUs;
    }

    public MetronomeTimeline Current { get; }
    public MetronomeTimeline? Previous { get; }
    public MetronomeClockMap Map { get; }
    public MetronomeSound? Sound { get; }
    public float Gain { get; }
    public bool Muted { get; }
    /// <summary>Which run of the metronome this is: a new one each time it starts from stopped, or follows a new master.
    /// Within a run the beat numbers carry on and are never played twice.</summary>
    public long RunId { get; }
    /// <summary>This machine's clock (the Stopwatch in microseconds; a test gives a machine a clock of its own).</summary>
    public Func<long> Clock { get; }

    public bool Audible => Current.Running && !Muted && Sound is not null && Gain > 0;

    /// <summary>Beat n's time on the master's clock, and whether it is an accent. False when no timeline plays it: before the
    /// current anchor with no earlier timeline known (a follower that joined during a change) - silence, not a guess.</summary>
    public bool TryBeat(long beat, out double masterUs, out bool accent)
    {
        var t = beat >= Current.AnchorBeat ? Current : beat >= (Previous?.AnchorBeat ?? long.MaxValue) ? Previous! : null;
        if (t is null) { masterUs = 0; accent = false; return false; }
        masterUs = t.BeatTimeUs(beat);
        accent = t.IsAccent(beat);
        return true;
    }

    /// <summary>The first beat at or after a moment on the master's clock, whichever timeline it falls in.</summary>
    public long FirstBeatAtOrAfter(double masterUs)
    {
        if (Previous is { } p && masterUs < Current.AnchorTimeUs)
            return Math.Min(Math.Max(p.BeatAtOrAfter(masterUs), p.AnchorBeat), Current.AnchorBeat);
        return Math.Max(Current.BeatAtOrAfter(masterUs), Current.AnchorBeat);
    }
}

/// <summary>
/// ONE OUTPUT'S CLICKS (PLAN-metronome-2026-09-28, "Timing" 3): a WASAPI output, the ASIO output, a DAW track. Called from
/// that output's own render call with the block it is producing, this machine's time now, and how long that output takes
/// to play what it is given. The block's first frame will be heard at now + that delay; a click whose time falls inside the
/// block starts at round((t - earTime0) * sampleRate). Each beat is placed on its own from the timeline, so nothing adds up.
///
/// <para>The ear time is kept as a clock of the output's own: each block is predicted to start where the last one ended
/// (by its frame count), and only drawn towards the measured now + delay a little each time - so a render thread woken a
/// fraction of a millisecond late, or a delay reading that wobbles, does not move clicks about. A measurement more than
/// <see cref="SnapUs"/> from the prediction (the output stopped, was re-opened, fell behind, its delay was measured anew) is
/// taken as it is - once the next block's measurement agrees, so one thread woken late cannot throw the clock off.</para>
///
/// <para>A click that starts in one block carries on into the next from where it got to, so every click plays whole. One
/// instance per output; render thread only; nothing allocated after the first call.</para>
/// </summary>
public sealed class MetronomeClickLane
{
    public const double SnapUs = 2000;
    /// <summary>Two measurements this close together, both past <see cref="SnapUs"/>, are a real step.</summary>
    public const double SnapAgreeUs = 1000;
    public const double Gain = 0.02;
    /// <summary>A beat this late when its block comes round (the ear clock moved it just past a block edge) still plays,
    /// at the block's start; later than this it is missed rather than played out of time.</summary>
    public const double LateToleranceUs = 1500;
    private const int MaxVoices = 4;

    private readonly float[]?[] voiceSamples = new float[MaxVoices][];
    private readonly int[] voicePosition = new int[MaxVoices];
    private readonly float[] voiceGain = new float[MaxVoices];
    private bool haveEar;
    private bool stepPending;
    private double stepOff;
    /// <summary>How many times the ear clock took a step (the output re-opened or its delay changed) - for the gate.</summary>
    public int Snaps { get; private set; }
    private double earUs;
    private int earFrames;
    private int earRate;
    private long lastRunId = long.MinValue;
    private long lastBeat = long.MinValue;

    public MetronomeClickLane(string name) => Name = name;

    public string Name { get; }

    /// <summary>Clicks this output has started, and where the last one landed - for the log and the gate.</summary>
    public long OnsetsPlaced { get; private set; }
    public int LastOnsetFrameInBlock { get; private set; } = -1;
    public double LastOnsetLocalUs { get; private set; }
    public double LastOnsetMasterUs { get; private set; }
    public long LastOnsetBeat => lastBeat;
    public bool LastOnsetAccent { get; private set; }
    /// <summary>The ear time this output's last block started at (this machine's clock, microseconds).</summary>
    public double LastEarUs => earUs;

    /// <summary>A click is still sounding (it carries into the next block).</summary>
    public bool Sounding
    {
        get
        {
            foreach (var v in voiceSamples) if (v is not null) return true;
            return false;
        }
    }

    /// <summary>Forget the output's clock and anything playing (the output was re-opened).</summary>
    public void Reset()
    {
        haveEar = false;
        Array.Clear(voiceSamples);
    }

    /// <summary>Mix this block's clicks into <paramref name="interleaved"/> (added to what is there).</summary>
    public void Mix(Span<float> interleaved, int channels, int sampleRate, double nowUs, double outputDelayUs, MetronomeSchedule? schedule)
    {
        if (channels <= 0 || sampleRate <= 0) return;
        var frames = interleaved.Length / channels;
        if (frames <= 0) return;

        // The ear clock.
        var measured = nowUs + outputDelayUs;
        if (haveEar && earRate == sampleRate)
        {
            var predicted = earUs + earFrames * 1e6 / earRate;
            var off = measured - predicted;
            if (Math.Abs(off) <= SnapUs) { earUs = predicted + Gain * off; stepPending = false; }
            else if (stepPending && Math.Abs(off - stepOff) <= SnapAgreeUs) { earUs = measured; stepPending = false; Snaps++; }
            else { earUs = predicted; stepPending = true; stepOff = off; }
        }
        else earUs = measured;
        haveEar = true;
        earFrames = frames;
        earRate = sampleRate;

        // Clicks already sounding carry on.
        for (var v = 0; v < MaxVoices; v++)
        {
            if (voiceSamples[v] is not { } samples) continue;
            voicePosition[v] = Play(interleaved, channels, 0, samples, voicePosition[v], voiceGain[v], out var ended);
            if (ended) voiceSamples[v] = null;
        }

        if (schedule is null || !schedule.Audible) return;
        var (accentSound, beatSound) = schedule.Sound!.At(sampleRate);
        var map = schedule.Map;
        var blockStart = earUs - LateToleranceUs;
        var blockEnd = earUs + frames * 1e6 / sampleRate;
        var beat = schedule.FirstBeatAtOrAfter(map.LocalToMaster(blockStart));
        for (var guard = 0; guard < 64; guard++, beat++)
        {
            if (!schedule.TryBeat(beat, out var masterUs, out var accent)) break;
            var localUs = map.MasterToLocal(masterUs);
            if (localUs >= blockEnd) break;
            if (localUs < blockStart || (schedule.RunId == lastRunId && beat <= lastBeat)) continue;
            var offset = (int)Math.Round((localUs - earUs) * sampleRate / 1e6);
            offset = Math.Clamp(offset, 0, frames - 1);
            var v = FreeVoice();
            voiceSamples[v] = accent ? accentSound : beatSound;
            voiceGain[v] = schedule.Gain;
            voicePosition[v] = Play(interleaved, channels, offset, voiceSamples[v]!, 0, schedule.Gain, out var done);
            if (done) voiceSamples[v] = null;
            lastRunId = schedule.RunId;
            lastBeat = beat;
            LastOnsetMasterUs = masterUs;
            LastOnsetLocalUs = localUs;
            LastOnsetFrameInBlock = offset;
            LastOnsetAccent = accent;
            OnsetsPlaced++;
        }
    }

    private int FreeVoice()
    {
        var oldest = 0;
        for (var v = 0; v < MaxVoices; v++)
        {
            if (voiceSamples[v] is null) return v;
            if (voicePosition[v] > voicePosition[oldest]) oldest = v;
        }
        return oldest;
    }

    /// <summary>Add a stereo sound from <paramref name="from"/> (in floats) into the block from frame <paramref name="offset"/>;
    /// returns where it got to, and whether it has ended.</summary>
    private static int Play(Span<float> block, int channels, int offset, float[] sound, int from, float gain, out bool done)
    {
        var frames = block.Length / channels;
        var f = offset;
        var s = from;
        for (; f < frames && s + 1 < sound.Length; f++, s += 2)
        {
            var i = f * channels;
            if (channels == 1) block[i] += (sound[s] + sound[s + 1]) * 0.5f * gain;
            else
            {
                block[i] += sound[s] * gain;
                block[i + 1] += sound[s + 1] * gain;
            }
        }
        done = s + 1 >= sound.Length;
        return s;
    }
}
