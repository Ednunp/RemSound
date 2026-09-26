using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Ed's four decisions from the 2026-09-25 sweep ("yes to each suggestion"), apart from the per-person server cues, which
/// are in "Everybody on a server is there...": one server identity per Windows account; the service folder repaired only
/// by the account it belongs to; and a device away at save time keeps its tick in the profile.
/// </summary>
internal static partial class SelfTest
{
    private static string? OneServerIdentityPerWindowsAccount()
    {
        if (!AppConfig.MachineIdentityIsolated) return Skip("this run keeps the computer's identity in the real ProgramData; it is only changed in an isolated run");
        var restoreSid = AppConfig.CurrentUserSidForTest;
        var ownerFile = Path.Combine(AppConfig.MachineIdentityDirectory, AppConfig.MachineIdentityOwnerFileName);
        var ownerBefore = File.Exists(ownerFile) ? File.ReadAllText(ownerFile) : null;
        var serviceIdBefore = ServiceStore.LoadRelayClientId();
        try
        {
            if (File.Exists(ownerFile)) File.Delete(ownerFile);
            const string First = "S-1-5-21-900-900-900-1001", Second = "S-1-5-21-900-900-900-1002";
            AppConfig.CurrentUserSidForTest = () => First;
            var first = AppConfig.LoadOrCreateRelayClientId();
            AppConfig.CurrentUserSidForTest = () => "S-1-5-18";
            var computers = AppConfig.LoadOrCreateRelayClientId();
            Check(first == computers, "the first account to run this version keeps the computer's own id - nobody has to tick it again");

            AppConfig.CurrentUserSidForTest = () => Second;
            var second = AppConfig.LoadOrCreateRelayClientId();
            Check(second != first,
                "THE SHARED ID: a second Windows account must be a different person on a server - one id for two signed-in accounts had the server bouncing it between them");
            Check(AppConfig.LoadOrCreateRelayClientId() == second, "and stay the same person, start after start");
            AppConfig.CurrentUserSidForTest = () => First;
            Check(AppConfig.LoadOrCreateRelayClientId() == first, "while the first account is still who it was");

            // The service is whoever set its profile up.
            AppConfig.CurrentUserSidForTest = () => "S-1-5-18";
            ServiceStore.SaveRelayClientId(second);
            Check(ServiceNetworkPresence.ServiceRelayClientId() == second,
                "THE SERVICE'S ID: the service must be the person whose account set it up, not the computer's first account");
        }
        finally
        {
            AppConfig.CurrentUserSidForTest = restoreSid;
            try
            {
                if (ownerBefore is null) File.Delete(ownerFile); else File.WriteAllText(ownerFile, ownerBefore);
                if (serviceIdBefore is { } id) ServiceStore.SaveRelayClientId(id);
            }
            catch { /* the run's own folder */ }
        }
        var root = FindSourceRoot();
        if (root is null) return Skip("the ids are right, but the source tree is not reachable to check the service profile's save (set REMSOUND_SOURCE_ROOT)");
        var form = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "MainForm.cs"));
        Check(form.Contains("ServiceStore.SaveRelayClientId(relayGroup.ClientId);", StringComparison.Ordinal),
            "saving the service profile must record this account's id for the service");
        return "the first account kept the computer's id, a second account was a person of its own and stayed so, and the service "
             + "took the id of the account that set it up";
    }

    private static string? TheServiceFolderIsRepairedOnlyByItsAccount()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        var restoreSid = AppConfig.CurrentUserSidForTest;
        var restoreWritable = MainForm.ServiceFolderWritableForTest;
        var ownerBefore = ServiceStore.LoadServiceOwnerSid();
        MainForm? form = null;
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        try
        {
            const string Owner = "S-1-5-21-901-901-901-1001", Other = "S-1-5-21-901-901-901-1002";
            Directory.CreateDirectory(ServiceStore.Directory);
            if (File.Exists(ServiceStore.ServiceOwnerFileForTest)) File.Delete(ServiceStore.ServiceOwnerFileForTest);   // nothing left from before
            ServiceStore.SaveInstallingUserSid(Owner);
            Check(ServiceStore.LoadServiceOwnerSid() == Owner, "THE UNREADABLE OWNER: the service's account must be kept where every account can read it");
            MainForm.ServiceFolderWritableForTest = () => false;
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            var main = form;
            main.SomebodyToAnswerForTest = true;
            main.NobodyToAskForTest = () => false;
            string? Asked()
            {
                string? question = null;
                DriveWhilePumping(main, () =>
                {
                    main.BeginInvoke(main.OfferServiceFolderRepairForTest);
                    if (WaitFor(() => main.Invoke(() => Application.OpenForms.OfType<HeadlessQuestionForm>().Any()), TimeSpan.FromSeconds(3)))
                    {
                        main.Invoke(() =>
                        {
                            var box = Application.OpenForms.OfType<HeadlessQuestionForm>().First();
                            question = box.Question + " [" + string.Join(", ", box.Answers) + "]";
                            box.Close();
                        });
                        WaitFor(() => main.Invoke(() => !Application.OpenForms.OfType<HeadlessQuestionForm>().Any()), TimeSpan.FromSeconds(3));
                    }
                }, TimeSpan.FromSeconds(15));
                return question;
            }

            // Another account: told, once, and never offered the repair that would take the folder from its owner.
            AppConfig.CurrentUserSidForTest = () => Other;
            var told = Asked();
            Check(told is not null && told.Contains("set up from another Windows account", StringComparison.Ordinal) && !told.Contains("Repair", StringComparison.Ordinal),
                $"THE TUG OF WAR: another account must be told the service is not theirs, and not offered the repair that takes it from its owner ({told ?? "nothing said"})");
            Check(Asked() is null, "and told once, not at every start");

            // The owner is still offered it.
            AppConfig.CurrentUserSidForTest = () => Owner;
            var offered = Asked();
            Check(offered is not null && offered.Contains("Repair now", StringComparison.Ordinal),
                $"the account the service belongs to must still be offered the repair ({offered ?? "nothing said"})");
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            Windowless.SetActiveForTest(false);
            AppConfig.CurrentUserSidForTest = restoreSid;
            MainForm.ServiceFolderWritableForTest = restoreWritable;
            if (ownerBefore is { } sid) ServiceStore.SaveInstallingUserSid(sid);
            else try { File.Delete(ServiceStore.ServiceOwnerFileForTest); } catch { /* the run's own folder */ }
            CuePlayer.GloballyMuted = restoreMuted;
        }
        return "another account was told once, plainly, that the service is not theirs and was never offered the repair; the owning "
             + "account still was";
    }

    /// <summary>
    /// AN ASIO INTERFACE SWITCHED ON LATE (2026-09-26; Ed: yes). The driver's probe failed at start - the interface was
    /// off - and the failure was kept until a driver change or a wake: the ASIO lists stayed empty all session and the
    /// profile's ASIO ticks never came. One more try on a device change, not more than once every 30 seconds, and the
    /// profile's ticks put back. ASIO only and both: WASAPI only has no driver to probe.
    /// </summary>
    private static string? AnAsioInterfaceSwitchedOnLateComesBack()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        var probes = 0;
        var readable = false;
        MainForm.AsioProbeForTest = _ =>
        {
            Interlocked.Increment(ref probes);
            return readable
                ? new RemSound.Sender.AsioDriverProbeResult(4, 2, ["In 1", "In 2", "In 3", "In 4"], ["Out 1", "Out 2"])
                : new RemSound.Sender.AsioDriverProbeResult(-1, -1, [], []);
        };
        MainForm? form = null;
        try
        {
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            var now = DateTime.UtcNow;
            form.AsioRetryClockForTest = () => now;
            SetAsioDriverForTest(form, "Self-test interface that starts switched off");
            var profile = Profile.NewBlank();
            profile.Title = "Late interface";
            profile.AsioDriverName = "Self-test interface that starts switched off";
            profile.SelectedAsioSendInputs.Add(RemSound.Core.AsioDeviceId.Format(0));
            form.ApplyThenCaptureForTest(profile);
            form.RefreshAudioDeviceListsForTest();
            var atStart = probes;
            Check(atStart >= 1 && form.AsioSendDevicesListForTest.Items.Count == 0, $"premise: the interface is off, so its list is empty ({atStart} probes, {form.AsioSendDevicesListForTest.Items.Count} items)");
            form.RefreshAudioDeviceListsForTest();
            Check(probes == atStart, "a refresh with nothing changed must not try the driver again - it is never tried on a timer");

            // Switched on: Windows says a device came.
            readable = true;
            form.DeviceChangedForTest();
            form.RefreshAudioDeviceListsForTest();
            Check(probes == atStart + 1 && form.AsioSendDevicesListForTest.Items.Count > 0,
                "THE EMPTY LIST: a device arriving must try a driver that could not be read once more - its lists stayed empty all session");
            Check(form.AsioSendDevicesListForTest.CheckedItems.Count == 1,
                "THE LOST TICK: once the interface can be read, the channels the profile ticked must be ticked again");
            now += MainForm.FailedProbeRetryInterval + TimeSpan.FromSeconds(1);   // well past the wait: only "it works" can stop a probe
            var working = probes;
            form.DeviceChangedForTest();
            form.RefreshAudioDeviceListsForTest();
            Check(probes == working, "THE LEAK: a driver that can be read must not be opened again because a device came or went");

            // Still failing: at most once every 30 seconds, however many devices come and go.
            readable = false;
            form.ClearAsioProbeCacheForTest();
            form.RefreshAudioDeviceListsForTest();                  // fails, and is kept as failed
            now += MainForm.FailedProbeRetryInterval + TimeSpan.FromSeconds(1);
            form.DeviceChangedForTest();                            // one retry, allowed
            form.RefreshAudioDeviceListsForTest();
            var failedAgain = probes;
            form.DeviceChangedForTest();                            // another device, moments later
            form.RefreshAudioDeviceListsForTest();
            Check(probes == failedAgain, "THE LEAK: a burst of device changes must not probe the driver again and again - some drivers leak on every open");
            now += MainForm.FailedProbeRetryInterval + TimeSpan.FromSeconds(1);
            form.DeviceChangedForTest();
            form.RefreshAudioDeviceListsForTest();
            Check(probes == failedAgain + 1, "and after the wait a device change may try it once more");
        }
        finally
        {
            MainForm.AsioProbeForTest = null;
            try { form?.Dispose(); } catch { /* teardown */ }
            CuePlayer.GloballyMuted = restoreMuted;
        }
        return "an interface off at start was tried once more when a device arrived, its list filled and the profile's channel ticked "
             + "again; a refresh with nothing changed never probed; a burst of device changes probed at most once every 30 seconds";
    }

    private static string? ADeviceAwayKeepsItsTickInTheProfile()
    {
        const string AwayInput = "{0.0.1.00000000}.{a1b2c3d4-0000-0000-0000-remsoundaway1}";
        const string AwayOutput = "{0.0.0.00000000}.{a1b2c3d4-0000-0000-0000-remsoundaway2}";
        const string AwayAsio = "asio:Self-test interface that is switched off:in:0";
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        try
        {
            foreach (var configuration in AudioConfigurations.All)
            {
                var profile = Profile.NewBlank();
                profile.Title = "Away devices";
                profile.SelectedWasapiSendInputs.Add(AwayInput);
                profile.SelectedWasapiReceiveOutputs.Add(AwayOutput);
                profile.SelectedAsioSendInputs.Add(AwayAsio);
                MainForm? form = null;
                try
                {
                    try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
                    catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
                    SetAudioConfigurationForTest(form, configuration);
                    // Loaded as a start loads it (once the lists exist), then saved.
                    var saved = form.ApplyThenCaptureForTest(profile);
                    Check(saved.SelectedWasapiSendInputs.Contains(AwayInput) && saved.SelectedWasapiReceiveOutputs.Contains(AwayOutput),
                        $"{configuration.Describe()}: THE LOST TICK: a sound device that is not plugged in when the profile is saved must keep its tick in the profile");
                    Check(saved.SelectedAsioSendInputs.Contains(AwayAsio),
                        $"{configuration.Describe()}: and so must an ASIO channel whose interface is switched off - the next start came up silent");
                }
                finally { try { form?.Dispose(); } catch { /* teardown */ } }
            }
        }
        finally { CuePlayer.GloballyMuted = restoreMuted; }

        // Unplugged while RemSound runs: kept too. A list of its own, so no real device is opened.
        MainForm? main = null;
        try
        {
            try { main = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            using var list = new CheckedListBox { AccessibleName = "Self-test devices" };
            var staying = new AudioDeviceChoice("Staying", "{self-test}.staying");
            var leaving = new AudioDeviceChoice("Unplugged", "{self-test}.unplugged");
            list.Items.Add(staying, false);
            list.Items.Add(leaving, true);
            main.SyncDeviceListForTest(list, [staying]);   // it is unplugged
            Check(main.SavedDeviceIdsForTest(list).Contains("{self-test}.unplugged"),
                "THE MID-SESSION LOSS: a ticked device unplugged while RemSound runs must keep its tick when the profile is then saved");
            main.SyncDeviceListForTest(list, [staying, leaving]);   // back, and shown as it comes back: unticked
            Check(!main.SavedDeviceIdsForTest(list).Contains("{self-test}.unplugged"),
                "and once it is back, it is saved as it is shown");
        }
        finally { try { main?.Dispose(); } catch { /* teardown */ } }
        return "in all three configurations, a WASAPI input and output and an ASIO channel that were not here kept their ticks when "
             + "the profile was saved; so did a device unplugged while RemSound ran, and once back it was saved as shown";
    }
}
