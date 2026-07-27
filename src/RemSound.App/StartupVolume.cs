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

    /// <summary>Minimum gap between two volume applications, whatever the mode (2026-07-27). The
    /// service can restart in quick succession for reasons the user never asked for — a self-update
    /// (we saw two applies 14 s apart: the update restart, then a follow-on start), a profile save,
    /// a test deploy, the app handing back. "Every service restart" must not machine-gun the volume
    /// down on each of those, so a fresh apply inside this window is skipped. A genuine "I restarted
    /// the service to reset the volume" a few minutes later still applies; a reboot (boot-only) is
    /// unaffected. NOTE: this is a burst guard — the set-and-forget choice is "first start after
    /// boot", which applies once per boot and never re-punches.</summary>
    internal static readonly TimeSpan ReapplyCooldown = TimeSpan.FromMinutes(5);

    /// <summary>When THIS boot began (UTC), from the monotonic uptime counter.</summary>
    public static DateTime CurrentBootUtc() => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>Pure decision core, pinned by the self-test. Never apply within the re-apply cooldown
    /// of the last successful apply (the burst guard — stops rapid/automatic restarts from re-punching
    /// the volume). Otherwise: boot-only mode applies only when the boot marker belongs to a DIFFERENT
    /// boot (or none yet); every-restart mode applies on any start past the cooldown.</summary>
    internal static bool ShouldApply(bool enabled, bool bootOnly, DateTime? markerBootUtc, DateTime currentBootUtc,
        DateTime? lastAppliedUtc, DateTime nowUtc)
    {
        if (!enabled) return false;
        // Burst guard first, both modes: a fresh apply within the cooldown of the last one is skipped.
        // Guard the negative case too (clock moved backwards) — treat only a positive, sub-cooldown
        // gap as "too soon"; anything else falls through to the normal decision.
        if (lastAppliedUtc is { } last)
        {
            var since = nowUtc - last;
            if (since >= TimeSpan.Zero && since < ReapplyCooldown) return false;
        }
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
            var nowUtc = DateTime.UtcNow;
            if (!ShouldApply(enabled, bootOnly, ServiceStore.LoadStartupVolumeBootMarker(), boot,
                    ServiceStore.LoadStartupVolumeLastAppliedUtc(), nowUtc))
            {
                log?.Invoke(bootOnly
                    ? "service: startup volume skipped — already applied this boot (boot-only mode)"
                    : "service: startup volume skipped — applied within the last few minutes (burst guard; a restart just happened)");
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
            // Markers only on success: a boot-time failure (audio stack not up yet) leaves the next
            // restart eligible to retry. The last-applied stamp drives the burst guard for both modes.
            if (ok)
            {
                ServiceStore.SaveStartupVolumeBootMarker(boot);
                ServiceStore.SaveStartupVolumeLastAppliedUtc(nowUtc);
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"service: startup volume error {ex.GetType().Name}: {ex.Message}");
        }
    }
}
