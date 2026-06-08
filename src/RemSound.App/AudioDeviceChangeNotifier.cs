using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace RemSound.App;

/// <summary>
/// Fires a callback whenever the set of Windows audio endpoints changes — a device is added,
/// removed, changes state (plugged / unplugged / enabled / disabled), or the default device
/// changes. This replaces the pre-v3.4 3-second polling of the device lists: instead of
/// re-enumerating every tick whether or not anything changed, RemSound re-reads the device lists
/// only when Windows actually tells us the device set changed.
///
/// The callbacks arrive on a COM thread, so the consumer (<see cref="MainForm"/>) marshals to the
/// UI thread and debounces — a single hot-plug typically fires several of these in quick
/// succession (state-changed + added + default-changed), which collapse into one refresh.
///
/// Property-value changes (volume nudges, format tweaks) are deliberately ignored: they fire
/// constantly and never change the device <em>set</em>, so reacting to them would defeat the
/// whole point of going event-driven.
/// </summary>
internal sealed class AudioDeviceChangeNotifier : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator enumerator = new();
    private readonly Action onChange;
    private bool registered;

    public AudioDeviceChangeNotifier(Action onChange)
    {
        this.onChange = onChange;
        enumerator.RegisterEndpointNotificationCallback(this);
        registered = true;
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => onChange();
    public void OnDeviceAdded(string pwstrDeviceId) => onChange();
    public void OnDeviceRemoved(string deviceId) => onChange();
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => onChange();
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { /* ignore — too chatty, no set change */ }

    public void Dispose()
    {
        try { if (registered) enumerator.UnregisterEndpointNotificationCallback(this); }
        catch { /* COM teardown race on shutdown — harmless */ }
        registered = false;
        try { enumerator.Dispose(); } catch { /* ignore */ }
    }
}
