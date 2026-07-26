using NAudio.CoreAudioApi;

namespace RemSound.App;

/// <summary>
/// Thin wrapper around NAudio's <see cref="MMDeviceEnumerator"/> + <see cref="AudioEndpointVolume"/>
/// to drive the Windows master volume on the system's <em>default</em> render device — the same
/// device the system-tray volume slider and the keyboard's volume keys control.
///
/// Why default-render-device specifically (and not e.g. the WASAPI outputs RemSound is currently
/// playing through): in Ed's primary use case the listener machine is using ASIO for RemSound
/// playback, but the Windows default output device is what NVDA + browsers + everything else
/// runs through, and that's the volume Ed wants to nudge. Targeting the default device matches
/// what the user already mentally maps "the system volume" to. ASIO devices don't expose a
/// MasterVolumeLevelScalar via this CoreAudio surface anyway — they have hardware gain — so the
/// "what about ASIO?" question doesn't apply here.
///
/// 2026-05-11 — switched to a cached enumerator/device/endpoint-volume trio (previously each call
/// created and disposed a fresh set). Reason: the system-volume hotkeys now allow Windows-side
/// auto-repeat on hold (see <see cref="RemSound.Core.GlobalHotkey.Register"/>), which can fire
/// the receiver-side handler at ~30 Hz. Each fresh enumeration is multiple COM calls into the
/// Windows audio service; doing that 30 times a second was correlated with receive-side audio
/// glitches in testing logs. Caching collapses every steady-state call to one VolumeStepUp/Down
/// on the cached endpoint-volume. The cache is invalidated on any COM exception so a device
/// hot-swap or audio-service restart self-heals on the next call.
/// </summary>
internal static class SystemVolumeHelper
{
    private static readonly object cacheLock = new();
    private static MMDeviceEnumerator? cachedEnumerator;
    private static MMDevice? cachedDevice;

    /// <summary>Bumps the default render device's master volume by Windows' native step
    /// (typically ~2% — same as one keyboard-volume-up press). Returns true on success,
    /// false if the device couldn't be enumerated (catches all exceptions to keep a remote
    /// hotkey press from ever throwing).</summary>
    public static bool TryStepUp() => TryDo(v => v.VolumeStepUp());

    /// <summary>Mirror of <see cref="TryStepUp"/> in the down direction.</summary>
    public static bool TryStepDown() => TryDo(v => v.VolumeStepDown());

    /// <summary>Toggles the default render device's master mute. Reads the current state,
    /// flips it, writes it back. Returns true on success.</summary>
    public static bool TryToggleMute() => TryDo(v => v.Mute = !v.Mute);

    /// <summary>Reads the current default-render-device master volume scalar (0.0..1.0) and
    /// mute state, for diagnostic logging. Returns null on any failure. Uses the same cached
    /// endpoint as the step/mute calls.</summary>
    public static (float scalar, bool mute)? TryReadState()
    {
        lock (cacheLock)
        {
            try
            {
                var device = GetOrCreateDeviceLocked();
                if (device is null) return null;
                return (device.AudioEndpointVolume.MasterVolumeLevelScalar, device.AudioEndpointVolume.Mute);
            }
            catch
            {
                InvalidateCacheLocked();
                return null;
            }
        }
    }

    /// <summary>Sets the default render device's master volume to <paramref name="percent"/> and
    /// unmutes it, in one call. The service's startup-volume option uses this so a machine that
    /// booted muted (or was left turned down) is audible again without anyone at the keyboard.
    /// Master endpoint volume is device-global, so this works from session 0 too.</summary>
    public static bool TrySetVolumeAndUnmute(int percent) => TryDo(v =>
    {
        v.MasterVolumeLevelScalar = Math.Clamp(percent, 0, 100) / 100f;
        v.Mute = false;
    });

    private static bool TryDo(Action<AudioEndpointVolume> action)
    {
        lock (cacheLock)
        {
            try
            {
                var device = GetOrCreateDeviceLocked();
                if (device is null) return false;
                // Multimedia role matches the system tray slider's idea of "default device" on a
                // typical setup. (Console role is for system sounds; the user's default playback
                // is normally configured the same for both. Multimedia is the right default for
                // "audio I'm listening to".)
                action(device.AudioEndpointVolume);
                return true;
            }
            catch
            {
                // Possible failure modes: default device changed, audio service restarted,
                // device disconnected, COM marshalling glitch. Drop the cache so the next call
                // re-enumerates fresh; the user just sees a missed tick rather than a thrown
                // exception or a stuck-stale endpoint.
                InvalidateCacheLocked();
                return false;
            }
        }
    }

    private static MMDevice? GetOrCreateDeviceLocked()
    {
        if (cachedDevice is not null) return cachedDevice;
        cachedEnumerator ??= new MMDeviceEnumerator();
        cachedDevice = cachedEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return cachedDevice;
    }

    private static void InvalidateCacheLocked()
    {
        try { cachedDevice?.Dispose(); } catch { /* ignore */ }
        cachedDevice = null;
        // Keep the enumerator across invalidations — the enumerator itself doesn't go stale
        // when the default device changes, only the device handle does. Cheaper to keep one
        // enumerator alive for the app's lifetime than to re-create it on every device hop.
    }
}
