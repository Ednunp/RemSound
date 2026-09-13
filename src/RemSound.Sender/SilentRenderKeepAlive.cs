using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RemSound.Sender;

/// <summary>
/// Pins a continuous silent render stream on a WASAPI device so the device stays "warm".
/// Some USB audio interfaces (Audient EVO8, RME, Focusrite, etc.) only deliver WASAPI loopback
/// audio when something is actively rendering to the device — when the render endpoint
/// goes idle, the loopback path stops delivering frames until an application starts rendering
/// again. By pinning a zero-volume silent render stream on the same device we capture from,
/// loopback capture keeps delivering regardless of whether other apps are playing audio.
///
/// Same idea the legacy "silence.exe" used; folded into the sender so it's automatic, sized
/// to the device's actual mix format (no resampler stage), and ties to capture lifetime.
/// We do not own the MMDevice — the capture backend that opened it does — so we never dispose it.
/// </summary>
internal sealed class SilentRenderKeepAlive : IDisposable
{
    private readonly WasapiOut output;
    private readonly Action<string>? onDiagnostic;

    public SilentRenderKeepAlive(MMDevice device, Action<string>? onDiagnostic = null)
    {
        this.onDiagnostic = onDiagnostic;
        // Shared mode with a 50 ms buffer. Latency doesn't matter for silence; longer buffers
        // mean fewer wakeups per second. Event sync still gives us efficient blocking turnover.
        output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 50);
        output.Init(new SilenceProvider(output.OutputWaveFormat));
    }

    public void Start()
    {
        try
        {
            output.Play();
            onDiagnostic?.Invoke($"silence keepalive started ({output.OutputWaveFormat.SampleRate} Hz, {output.OutputWaveFormat.Channels} ch)");
        }
        catch (Exception ex)
        {
            onDiagnostic?.Invoke($"silence keepalive start failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try { output.Stop(); } catch { /* ignore */ }
        try { output.Dispose(); } catch { /* ignore */ }
    }

    private sealed class SilenceProvider(WaveFormat format) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = format;

        public int Read(byte[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }
    }
}
