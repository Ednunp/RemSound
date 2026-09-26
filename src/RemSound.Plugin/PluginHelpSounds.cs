using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace RemSound.Plugin;

/// <summary>
/// The context help sounds inside a music program (Ed, 2026-09-25). The plugin can't read RemSound's settings from inside
/// the DAW, so RemSound puts copies of the two sounds chosen in Preferences beside it ("help open.wav", "help close.wav"),
/// and takes one away when it is switched off. No file, no sound. Played exactly as RemSound plays its cues (CuePlayer):
/// read with NAudio, resampled to 48 kHz by a managed resampler, and out through WaveOut to the default device, set up on
/// a worker so the window never waits.
/// </summary>
internal static class PluginHelpSounds
{
    internal const string OpenName = "help open.wav";
    internal const string CloseName = "help close.wav";

    /// <summary>Gate seam: count the play and open no device.</summary>
    internal static bool NoDeviceForTest;
    internal static int PlaysForTest;

    public static void Play(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        if (!File.Exists(path)) return;
        Interlocked.Increment(ref PlaysForTest);
        if (NoDeviceForTest) return;
        Task.Run(() =>
        {
            AudioFileReader? reader = null;
            WaveOutEvent? output = null;
            try
            {
                reader = new AudioFileReader(path);
                ISampleProvider source = reader;
                if (reader.WaveFormat.SampleRate != 48000) source = new WdlResamplingSampleProvider(source, 48000);
                output = new WaveOutEvent();
                var capturedReader = reader;
                var capturedOutput = output;
                output.PlaybackStopped += (_, _) =>
                {
                    try { capturedOutput.Dispose(); } catch { /* best-effort */ }
                    try { capturedReader.Dispose(); } catch { /* best-effort */ }
                };
                output.Init(new SampleToWaveProvider16(source));
                output.Play();
            }
            catch
            {
                // A sound that won't play must never disturb the music program.
                try { output?.Dispose(); } catch { /* ignore */ }
                try { reader?.Dispose(); } catch { /* ignore */ }
            }
        });
    }
}
