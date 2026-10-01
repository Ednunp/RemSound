using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// THE DOUBLE TAP SPEED, HEARD (Ed, 2026-09-28): moving along the push to talk window's speed list taps a keyboard key
/// twice, as far apart as that speed allows - so the speed is heard, not just read. The key is the first of the typing
/// sounds ("key 1.wav"). Both taps are rendered into one buffer, the second exactly the interval after the first to the
/// sample, so the gap is exact whatever the output device's own delay. One output, opened at the first preview and kept
/// while the window is open, so arrowing along the list never waits on a device; a new preview replaces one still
/// playing. Silent when RemSound's sounds are muted, and kept off every device by the gate.
/// </summary>
internal sealed class DoubleTapPreview : IDisposable
{
    internal const int SampleRate = 48000;

    private WaveOutEvent? output;
    private MixingSampleProvider? mixer;
    private float[]? clip;
    private readonly string soundsDirectory;

    public DoubleTapPreview(string? soundsDirectory = null) => this.soundsDirectory = soundsDirectory ?? AppConfig.SoundsDirectory;

    /// <summary>Gate seams: previews that got past the mute, and the interval of the last one.</summary>
    internal int PlaysForTest { get; private set; }
    internal int LastIntervalMsForTest { get; private set; }

    /// <summary>Tap the key twice, <paramref name="intervalMs"/> apart.</summary>
    public void Play(int intervalMs)
    {
        if (CuePlayer.GloballyMuted) return;
        PlaysForTest++;
        LastIntervalMsForTest = intervalMs;
        if (CuePlayer.NoDeviceForTest) return;
        try
        {
            var samples = Render(intervalMs);
            if (samples is null) return;
            if (output is null || mixer is null)
            {
                mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2)) { ReadFully = true };
                output = new WaveOutEvent { DesiredLatency = 80, NumberOfBuffers = 3 };
                output.Init(mixer);
                output.Play();
            }
            mixer.RemoveAllMixerInputs();
            mixer.AddMixerInput(new BufferProvider(samples));
        }
        catch { /* a preview must never disturb anything */ }
    }

    /// <summary>The two taps as one stereo buffer at 48 kHz: the key sound at the start and again exactly
    /// <paramref name="intervalMs"/> later. Null when there is no key sound.</summary>
    internal float[]? Render(int intervalMs)
    {
        clip ??= LoadClip();
        if (clip is null) return null;
        var offset = (int)Math.Round(intervalMs * SampleRate / 1000.0) * 2;
        var result = new float[Math.Max(clip.Length, offset + clip.Length)];
        for (var i = 0; i < clip.Length; i++)
        {
            result[i] += clip[i];
            result[offset + i] += clip[i];
        }
        return result;
    }

    /// <summary>The key sound, stereo at 48 kHz.</summary>
    internal float[]? LoadClip()
    {
        var path = Enumerable.Range(1, 8).Select(i => Path.Combine(soundsDirectory, $"key {i}.wav")).FirstOrDefault(File.Exists);
        if (path is null) return null;
        using var reader = new AudioFileReader(path);
        ISampleProvider source = reader;
        if (source.WaveFormat.SampleRate != SampleRate) source = new WdlResamplingSampleProvider(source, SampleRate);
        if (source.WaveFormat.Channels == 1) source = new MonoToStereoSampleProvider(source);
        var all = new List<float>();
        var buffer = new float[SampleRate * 2];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0) all.AddRange(buffer.AsSpan(0, read).ToArray());
        return [.. all];
    }

    public void Dispose()
    {
        try { output?.Stop(); } catch { /* ignore */ }
        try { output?.Dispose(); } catch { /* ignore */ }
        output = null;
        mixer = null;
    }

    /// <summary>One pass over a rendered buffer; the mixer drops it at the end.</summary>
    private sealed class BufferProvider(float[] samples) : ISampleProvider
    {
        private int position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);
        public int Read(float[] buffer, int offset, int count)
        {
            var n = Math.Min(count, samples.Length - position);
            if (n <= 0) return 0;
            Array.Copy(samples, position, buffer, offset, n);
            position += n;
            return n;
        }
    }
}
