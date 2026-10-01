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
    /// EACH UPDATE FREQUENCY, CHOSEN IN PREFERENCES, SETS ITS INTERVAL - AND A TICK REALLY CHECKS.
    ///
    /// <para>Hourly, every 6 hours and every 24 hours each run the check at that interval; Never stops it. A new install
    /// checks every 24 hours until someone chooses otherwise. Since 2026-09-28: the window is built armed at 6 hours (the old
    /// "armed at start-up" step, merged here); each frequency is chosen in the real Preferences list, wired to the window's
    /// own re-arm as the window wires it; and the timer's own tick is raised, against a stand-in release list on this
    /// machine, and must ask it exactly once.</para>
    /// </summary>
    private static string? AuditUpdateCheckFrequencies()
    {
        Check(new AppConfig().UpdateCheckFrequency == UpdateCheckFrequency.Every24Hours,
            "a new install checks for updates every 24 hours until someone chooses otherwise");
        return WithUpdateTimerWindow(UpdateCheckFrequency.Every6Hours, form =>
        {
            var (armed, armedEvery) = form.UpdateCheckTimerForTest;
            Check(armed && armedEvery == 6 * 60 * 60 * 1000,
                $"a window opened with the check set to every 6 hours must have its update check armed at 6 hours (running {armed}, every {armedEvery / 60_000} min)");

            using var prefs = new PreferencesDialog(new RemSoundSettingsStore("RemSound"), StoreWithOneProfileForTest(),
                () => false, _ => { }, () => { }, () => 0, () => { }, form.ApplyUpdateCheckTimerForTest, () => { }, () => { }, () => { }, () => { },
                _ => { }, () => (default(RouterMappingStatus), (System.Net.IPEndPoint?)null, ""), _ => { }, _ => { });
            var list = Require(FieldOf<ComboBox>(prefs, "updateFrequencyBox"), "Preferences has no updateFrequencyBox any more");
            foreach (var (frequency, expectedMs) in new[]
            {
                (UpdateCheckFrequency.EveryHour, 60 * 60 * 1000),
                (UpdateCheckFrequency.Every24Hours, 24 * 60 * 60 * 1000),
                (UpdateCheckFrequency.Never, 0),
                (UpdateCheckFrequency.Every6Hours, 6 * 60 * 60 * 1000),
            })
            {
                list.SelectedIndex = (int)frequency;
                Check(AppConfig.Load().UpdateCheckFrequency == frequency, $"choosing {frequency} in Preferences must save it");
                var (running, interval) = form.UpdateCheckTimerForTest;
                if (expectedMs == 0)
                    Check(!running, "with the check set to never, the update timer must be stopped");
                else
                    Check(running && interval == expectedMs,
                        $"with the check set to {frequency} in Preferences, the update timer must run every {expectedMs / 60_000} min "
                        + $"(running {running}, every {interval / 60_000} min)");
            }

            // THE TICK: raised on the timer itself, so its wiring is what is proven; answered by a stand-in release list that
            // says this version is the newest, so nothing is offered.
            using var releases = new ReleaseListServer($"[{{\"tag_name\":\"v{CommandLine.AppVersion}\",\"draft\":false,\"prerelease\":false,\"assets\":[]}}]");
            var restoreUrl = RemSoundUpdater.ReleasesUrlForTest;
            RemSoundUpdater.ReleasesUrlForTest = releases.Url;
            try
            {
                var timer = Require(FieldOf<System.Windows.Forms.Timer>(form, "updateCheckTimer"), "MainForm.updateCheckTimer not found");
                var onTick = Require(typeof(System.Windows.Forms.Timer).GetMethod("OnTick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
                    "System.Windows.Forms.Timer.OnTick not found");
                _ = form.Handle;
                onTick.Invoke(timer, [EventArgs.Empty]);
                Check(WaitFor(() => { Application.DoEvents(); return Volatile.Read(ref releases.Requests) >= 1; }, TimeSpan.FromSeconds(5)),
                    "a tick of the update timer must actually ask for the release list");
                Thread.Sleep(100);
                Check(Volatile.Read(ref releases.Requests) == 1, $"once a tick ({releases.Requests} asks)");
            }
            finally { RemSoundUpdater.ReleasesUrlForTest = restoreUrl; }
            return "a window opens armed at the saved frequency; hourly, every 6 hours and every 24 hours chosen in Preferences each re-arm "
                 + "the window's timer at that interval, and never stops it; a tick of the timer asks for the release list once; a new install checks daily";
        });
    }

    /// <summary>A stand-in for GitHub's release list on this machine: every request gets the same JSON, and is counted.</summary>
    private sealed class ReleaseListServer : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener listener = new(System.Net.IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        public int Requests;
        public string Url => $"http://127.0.0.1:{((System.Net.IPEndPoint)listener.LocalEndpoint).Port}/releases";

        public ReleaseListServer(string json)
        {
            listener.Start();
            var body = System.Text.Encoding.UTF8.GetBytes(json);
            _ = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    System.Net.Sockets.TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(stop.Token); }
                    catch { return; }
                    Interlocked.Increment(ref Requests);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using (client)
                            {
                                var s = client.GetStream();
                                var buf = new byte[4096];
                                var request = new System.Text.StringBuilder();
                                while (!request.ToString().Contains("\r\n\r\n"))
                                {
                                    var n = await s.ReadAsync(buf, stop.Token);
                                    if (n == 0) return;
                                    request.Append(System.Text.Encoding.ASCII.GetString(buf, 0, n));
                                }
                                await s.WriteAsync(System.Text.Encoding.ASCII.GetBytes(
                                    $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), stop.Token);
                                await s.WriteAsync(body, stop.Token);
                            }
                        }
                        catch { /* the test is over */ }
                    });
                }
            });
        }

        public void Dispose() { stop.Cancel(); try { listener.Stop(); } catch { /* stopped */ } }
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
