using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// A CLICK SOUND, HEARD (Ed, 2026-09-28): moving along the metronome window's sound list plays that sound's bar start and
/// then one beat, a beat apart at the tempo in the window - the same mechanics as the push to talk double tap preview
/// (<see cref="DoubleTapPreview"/>): both clicks rendered into one buffer, the second exactly a beat after the first, one
/// output opened at the first preview and kept while the window is open, a new preview replacing one still playing. At the
/// window's volume. Silent when RemSound's sounds are muted, and kept off every device by the gate.
/// </summary>
internal sealed class MetronomePreview : IDisposable
{
    internal const int SampleRate = MetronomeSound.SampleRate;

    private WaveOutEvent? output;
    private MixingSampleProvider? mixer;

    /// <summary>Gate seams: previews that got past the mute, and what the last one was.</summary>
    internal int PlaysForTest { get; private set; }
    internal string? LastSoundForTest { get; private set; }
    internal int LastBeatMsForTest { get; private set; }

    /// <summary>Play a sound's accent then a beat, <paramref name="beatMs"/> apart, at <paramref name="gain"/>.</summary>
    public void Play(MetronomeSound? sound, int beatMs, float gain)
    {
        if (sound is null || CuePlayer.GloballyMuted) return;
        PlaysForTest++;
        LastSoundForTest = sound.Name;
        LastBeatMsForTest = beatMs;
        if (CuePlayer.NoDeviceForTest) return;
        try
        {
            var samples = Render(sound, beatMs, gain);
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

    /// <summary>The accent at the start and the beat exactly <paramref name="beatMs"/> later, stereo at 48 kHz.</summary>
    internal static float[] Render(MetronomeSound sound, int beatMs, float gain)
    {
        var offset = (int)Math.Round(beatMs * SampleRate / 1000.0) * 2;
        var result = new float[Math.Max(sound.Accent.Length, offset + sound.Beat.Length)];
        for (var i = 0; i < sound.Accent.Length; i++) result[i] += sound.Accent[i] * gain;
        for (var i = 0; i < sound.Beat.Length; i++) result[offset + i] += sound.Beat[i] * gain;
        return result;
    }

    public void Dispose()
    {
        try { output?.Stop(); } catch { /* ignore */ }
        try { output?.Dispose(); } catch { /* ignore */ }
        output = null;
        mixer = null;
    }

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
