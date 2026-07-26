using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// The service's "unmute the machine and set volume to X% when the service starts" option
/// (Additional service options; feature request 2026-07-26). Two timing modes, chosen by a list in
/// the dialog: only the FIRST service start after each boot (the default — a mid-day manual
/// restart then never blasts the volume back up while someone is using the machine), or EVERY
/// service start. Boot identity comes from the system uptime clock, persisted in a marker file, so
/// "first start after boot" survives same-boot service restarts and still re-fires after a reboot.
/// </summary>
internal static class StartupVolume
{
    /// <summary>Two boot instants within this window are the SAME boot. Generous: the instant is
    /// computed from now-minus-uptime, which drifts a little between computations (timer
    /// granularity, clock adjustments); a real reboot separates instants by minutes at least.</summary>
    internal static readonly TimeSpan SameBootTolerance = TimeSpan.FromMinutes(2);

    /// <summary>When THIS boot began (UTC), from the monotonic uptime counter.</summary>
    public static DateTime CurrentBootUtc() => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>Pure decision core, pinned by the self-test: apply when enabled, and — in boot-only
    /// mode — when the recorded marker belongs to a DIFFERENT boot (or there is no marker yet).</summary>
    internal static bool ShouldApply(bool enabled, bool bootOnly, DateTime? markerBootUtc, DateTime currentBootUtc)
    {
        if (!enabled) return false;
        if (!bootOnly) return true;
        if (markerBootUtc is null) return true;
        return (currentBootUtc - markerBootUtc.Value).Duration() > SameBootTolerance;
    }

    /// <summary>Called from the service's OnStart. Reads the option, decides, applies via the same
    /// endpoint-volume helper the remote-control commands use, and records the boot marker on a
    /// successful apply. Never throws — a volume hiccup must not stop the service starting.</summary>
    public static void ApplyIfConfigured(Action<string>? log)
    {
        try
        {
            var (enabled, percent, bootOnly) = ServiceStore.LoadStartupVolume();
            if (!enabled) return;
            var boot = CurrentBootUtc();
            if (!ShouldApply(enabled, bootOnly, ServiceStore.LoadStartupVolumeBootMarker(), boot))
            {
                log?.Invoke("service: startup volume skipped — already applied this boot (boot-only mode)");
                return;
            }
            var ok = SystemVolumeHelper.TrySetVolumeAndUnmute(percent);
            var outcome = ok
                ? $"startup volume applied — default output set to {percent}% and unmuted ({(bootOnly ? "first start after boot" : "every service start")})"
                : "startup volume FAILED — could not reach the default output device (will retry on the next qualifying start)";
            log?.Invoke($"service: {outcome}");
            // Also into the always-on events log (not gated on the logging toggle): one line per
            // qualifying start, so "did it fire?" is answerable without turning full logging on.
            ServiceStore.AppendServiceEvent(outcome);
            // Marker only on success: a boot-time failure (audio stack not up yet) leaves the next
            // same-boot restart eligible to retry rather than silently never applying.
            if (ok) ServiceStore.SaveStartupVolumeBootMarker(boot);
        }
        catch (Exception ex)
        {
            log?.Invoke($"service: startup volume error {ex.GetType().Name}: {ex.Message}");
        }
    }
}
