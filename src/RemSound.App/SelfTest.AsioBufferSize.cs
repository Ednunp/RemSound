using NAudio.Wave.Asio;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// THE ASIO BUFFER SIZE, A PROFILE SETTING (Ed, 2026-09-28): out of the latency lab, where it was one switch for the whole
/// machine, and onto the Audio inputs and outputs tab beside the ASIO driver, saved with the profile. "Driver's own" - the
/// default, what RemSound always did - asks the driver for nothing; 32 to 512 samples ask for that size, fitted to what
/// the driver allows. ASIO only: WASAPI has no application-chosen buffer in shared mode.
/// </summary>
internal static partial class SelfTest
{
    private static string? TheAsioBufferSizeIsAProfileSetting()
    {
        // 1. The choices, as they read.
        Check(AsioBufferSizing.Choices.SequenceEqual(new[] { 0, 32, 64, 128, 256, 512 }),
            $"the sizes on offer must be the driver's own, then 32 to 512 samples ({string.Join(", ", AsioBufferSizing.Choices)})");
        Check(AsioBufferSizing.Describe(0) == "Driver's own" && AsioBufferSizing.Describe(64) == "64 samples (1.3 ms)"
              && AsioBufferSizing.Describe(32) == "32 samples (0.7 ms)" && AsioBufferSizing.Describe(512) == "512 samples (10.7 ms)",
            $"each size must say how long it is at 48 kHz, as \"64 samples (1.3 ms)\" ({AsioBufferSizing.Describe(64)})");

        // 2. Per profile: on disk, each its own; an older profile, and a new one, the driver's own.
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-asiobuffer-" + Guid.NewGuid().ToString("N"));
        using (AppConfig.UseThrowawayUserDataDirectory(scratch))
        {
            var store = new ProfileStore();
            store.Save(new Profile { Title = "asio buffer 64", AsioBufferFrames = 64 });
            store.Save(new Profile { Title = "asio buffer new" });
            File.WriteAllText(store.PathFor("asio buffer v6.0"), "{ \"Title\": \"asio buffer v6.0\", \"AsioDriverName\": \"Some ASIO\" }");
            Check(store.Load("asio buffer 64")?.AsioBufferFrames == 64, "a profile saved at 64 samples must open at 64");
            Check(store.Load("asio buffer new")?.AsioBufferFrames == 0 && store.Load("asio buffer v6.0")?.AsioBufferFrames == 0,
                "a new profile, and one saved before the setting existed, must open at the driver's own size");
        }
        // Through the window's settings cache, which is what the profile is read into and written back from.
        var cache = new RemSoundSettingsStore("RemSound");
        var backup = new Profile();
        cache.CopyTo(backup);
        try
        {
            cache.ApplyProfile(new Profile { AsioBufferFrames = 256 });
            Check(cache.LoadAsioBufferFrames() == 256, "a profile's size must reach the settings the window reads");
            cache.SaveAsioBufferFrames(32);
            var back = new Profile();
            cache.CopyTo(back);
            Check(back.AsioBufferFrames == 32, "and a change must go back into the profile that is saved");
            cache.ApplyProfile(new Profile { AsioBufferFrames = 100 });
            Check(cache.LoadAsioBufferFrames() == 0, "a size not on offer (a hand-edited profile) must never be asked of a driver: the driver's own");
        }
        finally { cache.ApplyProfile(backup); }

        // 3. What an open does: "Driver's own" leaves the driver's record alone; a size is fitted onto it.
        var restoreRequest = SharedAsioDevice.RequestedBufferFrames;
        static AsioDriverCapability Driver() => new() { BufferMinSize = 32, BufferMaxSize = 1024, BufferPreferredSize = 256, BufferGranularity = -1 };
        try
        {
            SharedAsioDevice.RequestedBufferFrames = 0;
            var own = Driver();
            var ownLine = SharedAsioDevice.SizeForOpen(own);
            Check(own.BufferPreferredSize == 256 && ownLine.Contains("using its preferred size", StringComparison.Ordinal),
                $"\"Driver's own\" must ask for nothing: the driver keeps its preferred 256 ({own.BufferPreferredSize}; {ownLine})");
            SharedAsioDevice.RequestedBufferFrames = 64;
            var asked = Driver();
            var askedLine = SharedAsioDevice.SizeForOpen(asked);
            Check(asked.BufferPreferredSize == 64 && askedLine.Contains("asking for 64 frames (the profile's ASIO buffer size)", StringComparison.Ordinal),
                $"64 samples must be asked of the driver at its open ({asked.BufferPreferredSize}; {askedLine})");
        }
        finally { SharedAsioDevice.RequestedBufferFrames = restoreRequest; }

        // 4. The window: the profile's size is what the first open asks for; the list beside the ASIO driver changes it,
        // saved in the profile, logged with the configuration, headless too; and the latency lab's old machine-wide size
        // is said once and forgotten.
        if (RemSound.Sender.AsioDeviceProbe.EnumerateDriverNames().Count == 0)
            return Skip("no ASIO driver is installed here, so the Audio inputs and outputs tab has no ASIO driver or buffer size to show");
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        MainForm? form = null;
        RemoteControlServer? server = null;
        var pipe = "RemSound.control.selftest.asiobuffer." + Guid.NewGuid().ToString("N")[..8];
        var logged = new List<string>();
        try
        {
            var cfg = AppConfig.Load();
            cfg.LabAsioBufferFrames = 64;   // as a lab user's config holds it
            cfg.Save();
            SharedAsioDevice.RequestedBufferFrames = 0;
            var profile = Profile.NewBlank();
            profile.AsioBufferFrames = 128;
            profile.AsioDriverName = "RemSound Test ASIO Driver";
            var mark = HeadlessRecords.Log.Mark;
            try { form = new MainForm(null, profile, null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            var main = form;
            var atStart = HeadlessRecords.Log.Since(mark);
            Check(SharedAsioDevice.RequestedBufferFrames == 128, $"the profile's 128 samples must be what the first open asks for ({SharedAsioDevice.RequestedBufferFrames})");
            Check(atStart.Any(l => l.Contains("ASIO buffer size at start: 128 samples (2.7 ms) (this profile)", StringComparison.Ordinal)),
                $"the start must say what this profile asks for (logged: {string.Join(" | ", atStart.Where(l => l.Contains("ASIO buffer", StringComparison.Ordinal)))})");
            Check(atStart.Count(l => l.Contains("the latency lab's machine-wide 64 frames no longer applies", StringComparison.Ordinal)) == 1,
                "a lab user's old machine-wide size must be said once at start, as no longer applying");
            Check(AppConfig.Load().LabAsioBufferFrames == 0, "and then forgotten, so it is said only once");

            // Tab meets it straight after the ASIO driver and before Receive audio: walked as Tab walks, what is showing,
            // enabled and a tab stop (a headless window is never shown, so the control channel's own walk finds nothing).
            Control Field(string name) => Require(
                typeof(MainForm).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(main) as Control,
                $"MainForm.{name} was not found - the tab order cannot be checked");
            var walk = new List<Control>();
            var seen = new HashSet<Control>();
            for (var c = main.GetNextControl(main, true); c is not null && seen.Add(c); c = main.GetNextControl(c, true))
                if (IsSetVisibleForAltAudit(c) && c.Enabled && c.TabStop) walk.Add(c);
            var driverAt = walk.IndexOf(Field("asioDriverBox"));
            var bufferAt = walk.IndexOf(Field("asioBufferSizeBox"));
            var receiveAt = walk.IndexOf(Field("receiveAudioCheckbox"));
            Check(driverAt >= 0 && bufferAt == driverAt + 1 && receiveAt == bufferAt + 1,
                $"Tab must meet the ASIO buffer size straight after the ASIO driver, then Receive audio (driver {driverAt}, buffer size {bufferAt}, receive {receiveAt})");

            main.LogForTest.EventTapForTest = line => { lock (logged) logged.Add(line); };
            server = new RemoteControlServer(pipe, () => main, new RemoteControlEngine(() => main));
            server.Start();
            string Ask(string command)
            {
                var reply = RemoteControl.Send(command, pipe, out var reached, connectTimeoutMs: 5000);
                Check(reached, $"the control channel did not answer \"{command}\": {reply}");
                return reply;
            }
            DriveWhilePumping(main, () =>
            {
                Check(Ask("tab Audio inputs and outputs").StartsWith("ok", StringComparison.Ordinal), "the Audio inputs and outputs tab must be reachable by name");
                Check(Ask("get \"ASIO buffer size\"").Contains("\"128 samples (2.7 ms)\" chosen", StringComparison.Ordinal), "the list must show the profile's 128 samples");
                int Reopens(string size) { lock (logged) return logged.Count(l => l.Contains($"ASIO buffer size {size}: a headless window touches no real driver", StringComparison.Ordinal)); }

                var chose = Ask("select \"ASIO buffer size\" \"64 samples\"");
                Check(chose.StartsWith("ok", StringComparison.Ordinal), $"the ASIO buffer size must take a choice from the control channel ({chose})");
                Check(main.AsioBufferFramesForTest == 64, $"64 samples must be saved in the profile's settings ({main.AsioBufferFramesForTest})");
                Check(SharedAsioDevice.RequestedBufferFrames == 64, "and be what the next open asks the driver for");
                Check(Ask("settings").Contains("\"AsioBufferFrames\": 64", StringComparison.Ordinal), "and be in the profile as the control channel reads it back");
                var cap = Driver();
                SharedAsioDevice.SizeForOpen(cap);
                Check(cap.BufferPreferredSize == 64, $"a change must reach the ASIO open request ({cap.BufferPreferredSize})");
                Check(WaitUntil(() => Reopens("64 samples (1.3 ms)") == 1, 3000),
                    "once the choice settles the driver is reopened for it - which a headless window only says");

                Check(Ask("select \"ASIO buffer size\" \"Driver's own\"").StartsWith("ok", StringComparison.Ordinal), "and take \"Driver's own\"");
                Check(main.AsioBufferFramesForTest == 0 && SharedAsioDevice.RequestedBufferFrames == 0, "which is saved and asked for as 0");
                var own = Driver();
                SharedAsioDevice.SizeForOpen(own);
                Check(own.BufferPreferredSize == 256, $"and asks the driver for nothing ({own.BufferPreferredSize})");
                Check(WaitUntil(() => Reopens("Driver's own") == 1, 3000), "and the driver is reopened for that too");

                // Arrowing through the list: two choices in quick succession reopen the driver once, for the last.
                Check(Ask("select \"ASIO buffer size\" \"256 samples\"").StartsWith("ok", StringComparison.Ordinal)
                      && Ask("select \"ASIO buffer size\" \"512 samples\"").StartsWith("ok", StringComparison.Ordinal), "two quick choices");
                Check(WaitUntil(() => Reopens("512 samples (10.7 ms)") == 1, 3000), "the last choice must reopen the driver");
                Thread.Sleep(500);
                Check(Reopens("256 samples (5.3 ms)") == 0 && Reopens("512 samples (10.7 ms)") == 1,
                    "a choice passed straight over must not reopen a single-client driver, and the last only once");
            }, TimeSpan.FromSeconds(60));

            string[] lines;
            lock (logged) lines = logged.ToArray();
            var configuration = main.ActiveAudioConfigurationForTest().Describe();
            Check(lines.Any(l => l.Contains("ui: ASIO buffer size → 64 samples (1.3 ms) [" + configuration + "]", StringComparison.Ordinal)),
                $"a change must be logged with its value and the audio configuration (logged: {string.Join(" | ", lines.Where(l => l.Contains("ASIO buffer", StringComparison.Ordinal)))})");
            form.LogForTest.EventTapForTest = null;
            try { form.Dispose(); } catch { /* teardown */ }
            form = null;

            // A second start: the old lab size was forgotten, so nothing is said about it again.
            mark = HeadlessRecords.Log.Mark;
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            Check(!HeadlessRecords.Log.Since(mark).Any(l => l.Contains("no longer applies", StringComparison.Ordinal)), "the old lab size must be said once only");
            Check(SharedAsioDevice.RequestedBufferFrames == 0, "a new profile asks for the driver's own size");
            return "driver's own, then 32 to 512 samples with their length; each profile keeps its own on disk and through the window's "
                 + "settings, older ones at the driver's own; the open asks for the profile's size and nothing for the driver's own; the list "
                 + "beside the ASIO driver changes it from the control channel, saved, logged with the configuration, one reopen per choice; "
                 + "the lab's old size said once and forgotten";
        }
        finally
        {
            if (form is not null) form.LogForTest.EventTapForTest = null;
            server?.Dispose();
            try { form?.Dispose(); } catch { /* teardown */ }
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
            SharedAsioDevice.RequestedBufferFrames = 0;
            var c = AppConfig.Load();
            c.LabAsioBufferFrames = 0;
            c.Save();
        }
    }
}
