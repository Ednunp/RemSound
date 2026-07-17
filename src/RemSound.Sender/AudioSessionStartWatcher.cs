using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace RemSound.Sender;

/// <summary>
/// Fires the instant an application starts a new audio session on the default render device — i.e. the
/// moment an app is about to make its first sound. That lets "send specific applications" begin capturing
/// a remembered app right at its START, instead of only noticing it on the next poll (by which time the
/// first sound is already gone). Uses the Windows audio-session-created notification via NAudio's
/// <see cref="AudioSessionManager.OnSessionCreated"/>.
///
/// <para>Best-effort and self-contained: any failure just means we fall back to the poll. The callback
/// arrives on a COM thread with the new session's process id; the caller marshals to the UI thread and
/// decides whether that process is one of the apps it wants. Re-point it at the current default device
/// (<see cref="Rehook"/>) when the default render device changes — reuse the app's existing device-change
/// notifier rather than adding another watcher.</para>
/// </summary>
public sealed class AudioSessionStartWatcher : IDisposable
{
    private readonly Action<int> onSessionStartedPid;
    private readonly Action<string>? log;
    private readonly object gate = new();
    private MMDevice? device;
    private AudioSessionManager? manager;
    private bool disposed;

    public AudioSessionStartWatcher(Action<int> onSessionStartedPid, Action<string>? log = null)
    {
        this.onSessionStartedPid = onSessionStartedPid;
        this.log = log;
        Rehook();
    }

    /// <summary>Point the watcher at the CURRENT default render device. Call on a default-device change so
    /// we keep hearing new sessions on whatever device apps are actually playing to. Never throws.</summary>
    public void Rehook()
    {
        lock (gate)
        {
            if (disposed) return;
            UnhookLocked();
            try
            {
                var en = new MMDeviceEnumerator();
                if (!en.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) { en.Dispose(); return; }
                device = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                en.Dispose();
                manager = device.AudioSessionManager;
                manager.RefreshSessions();               // required before OnSessionCreated fires (Windows quirk)
                manager.OnSessionCreated += HandleSessionCreated;
            }
            catch (Exception ex)
            {
                log?.Invoke($"session-start watcher hook failed: {ex.GetType().Name}: {ex.Message}");
                UnhookLocked();
            }
        }
    }

    private void HandleSessionCreated(object? sender, IAudioSessionControl newSession)
    {
        // COM thread. Pull the PID and hand it off; the caller decides if it cares and marshals threads.
        try
        {
            using var ctl = new AudioSessionControl(newSession);
            var pid = (int)ctl.GetProcessID;
            if (pid > 0) onSessionStartedPid(pid);
        }
        catch { /* a session we can't read — ignore */ }
    }

    private void UnhookLocked()
    {
        try { if (manager is not null) manager.OnSessionCreated -= HandleSessionCreated; } catch { }
        // Dispose the session manager too, not just the device — it holds its own WASAPI COM state, and
        // Rehook() runs on every default-device change, so leaking it here is a slow WASAPI handle drip.
        try { (manager as IDisposable)?.Dispose(); } catch { }
        manager = null;
        try { device?.Dispose(); } catch { }
        device = null;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            UnhookLocked();
        }
    }
}
