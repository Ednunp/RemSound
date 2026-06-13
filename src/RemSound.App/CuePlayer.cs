using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace RemSound.App;

/// <summary>
/// Plays a short cue WAV reliably, regardless of its format. Replaces System.Media.SoundPlayer,
/// which only handles bog-standard 16-bit / 44.1–48 kHz PCM and silently fails (plays nothing,
/// intermittently) on anything else — which is exactly why RemSound's own 96 kHz / 24-bit cue
/// files, and any custom WAV a user browses for, played unpredictably (2026-06-02 investigation
/// of Ed's "didn't hear the disconnect cue" report).
///
/// Each Play() reads the file with NAudio (which copes with 16/24/32-bit and any sample rate),
/// resamples to a universally-safe 48 kHz / 16-bit with a pure-managed resampler (no Media
/// Foundation, so it works on Windows 7 too), and renders it through WaveOut to the default
/// output device. Setup happens on a background thread so a cue never hitches the UI, and the
/// output + reader are disposed when playback finishes. Cues are occasional and short, so a
/// fresh device-open per play is fine and keeps the class stateless and overlap-safe (two cues
/// close together simply mix). 2026-06-02.
/// </summary>
internal sealed class CuePlayer : IDisposable
{
    /// <summary>The "this is a silent / automated launch" flag, set once at startup by the
    /// <c>--silent</c> flag. Primarily an app-wide mute for every cue sound (startup, connect,
    /// checkbox, tab-switch, send/receive, previews — everything that plays through a CuePlayer).
    /// It ALSO doubles as the signal to suppress the unattended startup pop-ups that would otherwise
    /// ding at and bother whoever's at the screen during a throwaway test launch — the missing-sound
    /// warning and the Realtek-ASIO-detected warning both check it. So a <c>--silent</c> launch is
    /// completely quiet: no cue audio, no warning dings, no dialogs. Off in normal use.</summary>
    public static bool GloballyMuted { get; set; }

    private readonly string filePath;

    public CuePlayer(string filePath) => this.filePath = filePath;

    public void Play()
    {
        if (GloballyMuted) return;
        var path = filePath;
        Task.Run(() =>
        {
            AudioFileReader? reader = null;
            WaveOutEvent? output = null;
            try
            {
                reader = new AudioFileReader(path);
                ISampleProvider source = reader;
                if (reader.WaveFormat.SampleRate != 48000)
                {
                    source = new WdlResamplingSampleProvider(source, 48000);
                }
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
                // A cue that won't load or play must never disturb anything. Clean up and move on.
                try { output?.Dispose(); } catch { /* ignore */ }
                try { reader?.Dispose(); } catch { /* ignore */ }
            }
        });
    }

    public void Dispose()
    {
        // Nothing persistent to release — each Play owns and disposes its own reader + output.
    }
}
