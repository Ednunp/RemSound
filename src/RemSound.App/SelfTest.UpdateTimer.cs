using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// The background update check. Ed, 2026-09-13, on removing the saved last-check time: "as long as the timer still works".
/// That time was written after every check and read by nothing, so the timer never depended on it; these steps pin the
/// timer itself.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>
    /// THE UPDATE CHECK IS ARMED AT START-UP FROM THE FREQUENCY SETTING.
    ///
    /// <para>The window arms the background check once, while it is built, from the frequency chosen in Preferences.
    /// If that call went, updates would only ever be found at start-up or by hand.</para>
    /// </summary>
    private static string? AuditUpdateCheckArmedAtStartup() => WithUpdateTimerWindow(UpdateCheckFrequency.Every6Hours, form =>
    {
        var (running, interval) = form.UpdateCheckTimerForTest;
        Check(running && interval == 6 * 60 * 60 * 1000,
            "a window opened with the check set to every 6 hours must have its update check armed at 6 hours "
            + $"(running {running}, every {interval / 60_000} min)");
        return "a window opened with the check set to every 6 hours has its update check armed at 6 hours";
    });

    /// <summary>
    /// EACH UPDATE FREQUENCY SETS ITS INTERVAL.
    ///
    /// <para>Hourly, every 6 hours and every 24 hours each run the check at that interval; Never stops it. A new install
    /// checks every 24 hours until someone chooses otherwise.</para>
    /// </summary>
    private static string? AuditUpdateCheckFrequencies()
    {
        Check(new AppConfig().UpdateCheckFrequency == UpdateCheckFrequency.Every24Hours,
            "a new install checks for updates every 24 hours until someone chooses otherwise");
        return WithUpdateTimerWindow(UpdateCheckFrequency.Every24Hours, form =>
        {
            foreach (var (frequency, expectedMs) in new[]
            {
                (UpdateCheckFrequency.EveryHour, 60 * 60 * 1000),
                (UpdateCheckFrequency.Every6Hours, 6 * 60 * 60 * 1000),
                (UpdateCheckFrequency.Every24Hours, 24 * 60 * 60 * 1000),
                (UpdateCheckFrequency.Never, 0),
            })
            {
                var cfg = AppConfig.Load();
                cfg.UpdateCheckFrequency = frequency;
                cfg.Save();
                form.ApplyUpdateCheckTimerForTest();
                var (running, interval) = form.UpdateCheckTimerForTest;
                if (expectedMs == 0)
                    Check(!running, "with the check set to never, the update timer must be stopped");
                else
                    Check(running && interval == expectedMs,
                        $"with the check set to {frequency}, the update timer must run every {expectedMs / 60_000} min "
                        + $"(running {running}, every {interval / 60_000} min)");
            }
            return "hourly, every 6 hours and every 24 hours each run the check at that interval; never stops it; a new install checks daily";
        });
    }

    /// <summary>A headless main window built in a throwaway settings folder with the given update frequency saved, muted,
    /// and disposed afterwards.</summary>
    private static string? WithUpdateTimerWindow(UpdateCheckFrequency frequency, Func<MainForm, string?> body)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-updatetimer-" + Guid.NewGuid().ToString("N"));
        using var userData = AppConfig.UseThrowawayUserDataDirectory(scratch);
        var cfg = AppConfig.Load();
        cfg.UpdateCheckFrequency = frequency;
        cfg.Save();

        var restoreMuted = CuePlayer.GloballyMuted;
        var restoreChecks = CheckSoundService.Suppressed;
        CuePlayer.GloballyMuted = true;
        CheckSoundService.Suppressed = true;
        MainForm? form = null;
        try
        {
            var profile = Profile.NewBlank();
            profile.Password = RemSoundCrypto.Obfuscate("update-timer-test-password");
            try { form = new MainForm(null, profile, null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            return body(form);
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown is best-effort */ }
            CuePlayer.GloballyMuted = restoreMuted;
            CheckSoundService.Suppressed = restoreChecks;
        }
    }
}
