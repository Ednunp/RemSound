using System;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace RemSound.App;

/// <summary>
/// Subscribes to Windows' system power-state changes and fires a single callback on
/// <see cref="PowerModes.Resume"/> — i.e. when the system has just come back from sleep
/// (S3) or hibernate (S4). Both states raise the same Resume event, so the one hook covers
/// both. Used by <see cref="MainForm"/> to restart the audio after a wake: it stops every
/// stream, puts back the auto-tune from before the sleep, closes and reopens the audio
/// backend on both sides and starts again whatever was running. The reopen also clears a
/// USB audio device (ASIO / WASAPI) that came back from sleep running but silent.
///
/// Threading: PowerModeChanged is raised on a system-message thread, NOT the UI thread.
/// The handler returns from that thread quickly (no work done inline) and schedules the
/// restart on a background task — which then marshals onto the UI thread via the
/// caller's callback. The caller's callback is responsible for any UI-thread marshaling.
///
/// USB settle delay: Windows raises Resume early, sometimes before USB devices have
/// finished re-enumerating. The handler waits <see cref="SettleDelay"/> before firing the
/// callback so the audio restart has a fully-ready USB stack to talk to.
///
/// Debounce: Windows can in rare cases fire Resume twice in quick succession after a
/// short sleep. The handler ignores Resume events within <see cref="DebounceWindow"/> of
/// the previous one so the audio isn't restarted twice for one wake.
/// </summary>
internal sealed class PowerResumeHandler : IDisposable
{
    /// <summary>How long to wait after Resume before firing the callback — gives the USB
    /// bus and audio drivers time to finish re-enumerating.</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(1500);

    /// <summary>A second Resume event within this window of the first is treated as a
    /// duplicate and ignored.</summary>
    public static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(5);

    private readonly Action onResume;
    private readonly Action<string>? log;
    private readonly object gate = new();
    private DateTime lastResumeUtc = DateTime.MinValue;
    private bool disposed;

    /// <param name="onResume">Invoked once per resume event, on a background thread, after
    /// the USB-settle delay. The callback is responsible for marshaling onto the UI thread
    /// if it touches UI or audio state.</param>
    /// <param name="log">Optional sink for diagnostic lines — wire to the app's log if you
    /// want resume events visible there.</param>
    public PowerResumeHandler(Action onResume, Action<string>? log = null)
    {
        this.onResume = onResume ?? throw new ArgumentNullException(nameof(onResume));
        this.log = log;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        log?.Invoke("subscribed to PowerModeChanged");
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        if (disposed) return;

        lock (gate)
        {
            var now = DateTime.UtcNow;
            if (now - lastResumeUtc < DebounceWindow)
            {
                log?.Invoke($"Resume event ignored (within {DebounceWindow.TotalSeconds:0} s debounce of previous)");
                return;
            }
            lastResumeUtc = now;
        }

        log?.Invoke($"system Resume detected — scheduling audio backend reset in {SettleDelay.TotalMilliseconds:0} ms");
        // Return from the system message thread immediately; do the work on a background task.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SettleDelay).ConfigureAwait(false);
                if (disposed) return;
                onResume();
            }
            catch (Exception ex)
            {
                log?.Invoke($"Resume callback failed: {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { /* shutting down */ }
    }
}
