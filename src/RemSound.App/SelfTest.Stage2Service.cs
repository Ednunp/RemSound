using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// The 2026-09-13 review, stage 2: the service, installing and updating, and closing RemSound from the command line.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>
    /// THE SERVICE TAKES AN APP UPDATE ONLY ONCE THE UPDATE HAS FINISHED LANDING.
    ///
    /// <para>2026-09-13 review (Service #3). The app's updater swaps its folder one file at a time, retrying each for up to a
    /// minute, and RemSound.exe can be new while the files beside it are still old. The service looked only at RemSound.exe's
    /// version, so a poll part way through copied a mixed build into itself and ran it. And when starting again after the
    /// copy failed once, the service stayed stopped until the next reboot.</para>
    /// </summary>
    private static string? AuditServiceUpdateWaitsForTheWholeSwap()
    {
        // Four parts, as the running assembly reports it: a two-part 6.0 sorts BELOW 6.0.0.0.
        var running = new Version(6, 0, 0, 0);
        Check(!ServiceUpdate.ReadyToApply(running, "6.1.0.0", null, swapInProgress: false),
            "a newer build seen for the first time must wait one more poll — the swap may still be under way");
        Check(ServiceUpdate.ReadyToApply(running, "6.1.0.0", "6.1.0.0", swapInProgress: false),
            "the same newer build at two polls in a row, with no swap under way, must be taken");
        Check(!ServiceUpdate.ReadyToApply(running, "6.1.0.0", "6.1.0.0", swapInProgress: true),
            "never while the updater's backup folder exists — the swap has not finished, or is being rolled back");
        Check(!ServiceUpdate.ReadyToApply(running, "6.2.0.0", "6.1.0.0", swapInProgress: false),
            "a version that changed between polls must wait again");
        Check(!ServiceUpdate.ReadyToApply(running, "6.0.0.0", "6.0.0.0", swapInProgress: false),
            "the version already running is not an update");
        Check(!ServiceUpdate.ReadyToApply(running, null, null, swapInProgress: false), "an unreadable version is never an update");
        Check(!ServiceUpdate.ReadyToApply(null, "6.1.0.0", "6.1.0.0", swapInProgress: false), "an unknown running version is never an update");

        var appDir = Path.Combine(Path.GetTempPath(), "remsound-swap-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(appDir);
            Check(!ServiceUpdate.SwapInProgressIn(appDir), "an app folder without the updater's backup folder is not part way through a swap");
            Directory.CreateDirectory(Path.Combine(appDir, UpdateApplier.BackupFolderName));
            Check(ServiceUpdate.SwapInProgressIn(appDir), "the updater's backup folder means a swap is under way");
        }
        finally { try { Directory.Delete(appDir, recursive: true); } catch { /* temp */ } }
        Check(!ServiceUpdate.SwapInProgressIn(null), "no recorded app folder is not a swap");

        var root = FindSourceRoot();
        if (root is null) return Skip("the rules hold, but the source tree is not reachable to check they are used (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var service = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "RemSoundService.cs"));
        Check(SourceMethodBody(service, "private void CheckForUpdate()").Contains("ServiceUpdate.ReadyToApply(", StringComparison.Ordinal),
            "the service's update poll must use the whole-swap rule, not the bare version check");
        var applier = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "UpdateApplier.cs"));
        Check(applier.Contains("Path.Combine(target, BackupFolderName)", StringComparison.Ordinal),
            "the updater must make its backup folder under the name the service waits on");
        var control = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "ServiceControl.cs"));
        Check(SourceMethodBody(control, "public static int DoSelfUpdate()").Contains("attempt < SelfUpdateStartAttempts", StringComparison.Ordinal)
              && ServiceControl.SelfUpdateStartAttempts > 1,
            "starting the service again after the copy must try more than once — one failed start left it stopped until the next reboot");
        return "an update is taken only when the same newer build is seen twice with no swap under way; the restart retries";
    }

    /// <summary>
    /// THE SERVICE HANDLES A WAKE ONCE, AND TIDIES ITS OWN LOGS.
    ///
    /// <para>2026-09-13 review (Service #4, #9). One wake sends the service two or three resume notices, and each ran a
    /// full stop and start of the send, back to back. And the service's log files and crash reports were never tidied —
    /// the app's start-up housekeeping never runs for the service.</para>
    /// </summary>
    private static string? AuditServiceHandlesAWakeOnceAndTidiesItsLogs()
    {
        Check(RemSoundService.ShouldHandleResume(50_000, 0), "the first resume notice must be acted on");
        Check(!RemSoundService.ShouldHandleResume(55_000, 50_000),
            "a second notice five seconds into the same wake must not stop and start the send again");
        Check(RemSoundService.ShouldHandleResume(50_000 + RemSoundService.ResumeDebounceMs, 50_000),
            "a notice a whole window later is a new wake");

        var outcome = "a wake is handled once";
        // The tidy-up works on this run's logs folder. Only touch it when this run has its own throwaway settings
        // folder, as the gate does — never somebody's real logs.
        if (CommandLine.TryGetConfigDir(Environment.GetCommandLineArgs(), out _))
        {
            var dir = AppConfig.LogsDirectory;
            Directory.CreateDirectory(dir);
            var stamp = Guid.NewGuid().ToString("N")[..8];
            var old = Path.Combine(dir, $"zz-selftest-old-{stamp}.log");
            var recent = Path.Combine(dir, $"zz-selftest-recent-{stamp}.log");
            var active = Path.Combine(dir, $"zz-selftest-active-{stamp}.log");
            var crashes = Enumerable.Range(0, 14).Select(i => Path.Combine(dir, $"crash-zzselftest-{stamp}-{i:00}.txt")).ToList();
            var made = new[] { old, recent, active }.Concat(crashes).ToList();
            try
            {
                foreach (var file in made) File.WriteAllText(file, "self-test");
                var longAgo = DateTime.Now.AddDays(-(RemSoundService.ServiceLogKeepDays + 5));
                File.SetLastWriteTime(old, longAgo);
                File.SetLastWriteTime(active, longAgo);
                for (var i = 0; i < crashes.Count; i++) File.SetLastWriteTimeUtc(crashes[i], DateTime.UtcNow.AddMinutes(-i));

                var line = RemSoundService.TidyServiceLogs(active);
                Check(!File.Exists(old), $"a service log older than {RemSoundService.ServiceLogKeepDays} days must be removed");
                Check(File.Exists(recent), "a recent service log must stay");
                Check(File.Exists(active), "the log being written must stay, however old its date looks");
                Check(Directory.EnumerateFiles(dir, "crash-*.txt").Count() <= 10, "only the newest crash reports may stay");
                Check(line is not null && line.StartsWith("service: log housekeeping", StringComparison.Ordinal),
                    $"the service log must say what the tidy-up removed (got: {line ?? "nothing"})");
            }
            finally { foreach (var file in made) { try { File.Delete(file); } catch { /* already gone */ } } }
            outcome = "a wake is handled once; old service logs and surplus crash reports go, the active and recent logs stay";
        }

        var root = FindSourceRoot();
        if (root is null) return Skip("the rules hold, but the source tree is not reachable to check the service uses them (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var service = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "RemSoundService.cs"));
        Check(SourceMethodBody(service, "protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)").Contains("ShouldHandleResume(", StringComparison.Ordinal),
            "the service's power handler must use the once-a-wake rule");
        Check(SourceMethodBody(service, "protected override void OnStart(string[] args)").Contains("TidyServiceLogs(", StringComparison.Ordinal),
            "the service must tidy its logs when it starts");
        return outcome;
    }

    /// <summary>
    /// THE SERVICE SENDS A CHOSEN APPLICATION THAT STARTS AFTER IT DOES.
    ///
    /// <para>2026-09-13 review (Service #2). Sending specific applications, the service stopped looking when none of them was
    /// running: it gave up before making its session watcher, and the watcher it did make ignored every new session once
    /// any audio had been heard. So an application opened after the service started was never sent. It now keeps the
    /// watcher and captures again when what it would capture changes.</para>
    /// </summary>
    private static string? AuditServiceSendsAnApplicationStartedLater()
    {
        const ServiceSendHost.SessionStartResponse reapply = ServiceSendHost.SessionStartResponse.ReapplyForChosenApps;
        const ServiceSendHost.SessionStartResponse reopen = ServiceSendHost.SessionStartResponse.ReopenDeafCapture;
        const ServiceSendHost.SessionStartResponse ignore = ServiceSendHost.SessionStartResponse.Ignore;
        Check(ServiceSendHost.DecideSessionStart(true, false, "", "pid:4242", everHeardAudio: false) == reapply,
            "with none of the chosen applications running, the first one starting must start the send");
        Check(ServiceSendHost.DecideSessionStart(true, false, "pid:1", "pid:1|pid:4242", everHeardAudio: true) == reapply,
            "another chosen application starting after audio has been heard must still be picked up — the watcher used to ignore everything once audio had flowed");
        Check(ServiceSendHost.DecideSessionStart(true, false, "pid:1", "pid:1", everHeardAudio: true) == ignore,
            "a session from an application that isn't chosen changes nothing");
        Check(ServiceSendHost.DecideSessionStart(true, false, "pid:1", "pid:1", everHeardAudio: false) == reopen,
            "the boot self-heal is unchanged: a new session while nothing has been heard re-opens the capture");
        Check(ServiceSendHost.DecideSessionStart(true, false, null, null, everHeardAudio: false) == reopen,
            "output-device mode keeps the boot self-heal");
        Check(ServiceSendHost.DecideSessionStart(true, false, null, null, everHeardAudio: true) == ignore,
            "output-device mode after audio has been heard: nothing, as before");
        Check(ServiceSendHost.DecideSessionStart(false, false, "", "pid:4242", everHeardAudio: false) == ignore,
            "nothing while the app has the send");
        Check(ServiceSendHost.DecideSessionStart(true, true, "", "pid:4242", everHeardAudio: false) == ignore,
            "nothing once the host is gone");

        var outcome = "the decision holds";
        if (RemSound.Sender.ProcessLoopbackCapture.IsSupported)
        {
            var lines = new ConcurrentQueue<string>();
            var chosenApps = new Profile
            {
                Title = "self-test service",
                SelectedSendApplications = ["zzremsound_no_such_process"],
                SelectedConnectedPeers = ["127.0.0.1:9"],
            };
            chosenApps.WasapiSendMode = "applications";
            using var host = new ServiceSendHost(() => chosenApps, lines.Enqueue);
            Check(!host.ApplyProfile(chosenApps), "with the chosen application not running there is nothing to send yet");
            if (lines.Any(l => l.Contains("session watcher unavailable", StringComparison.Ordinal)))
                return Skip("the decision holds; this machine has no audio endpoint to watch sessions on");
            Check(host.IsWaitingForChosenApplicationForTest,
                "the service must keep its session watcher while waiting for a chosen application — without it, one opened later was never sent");
            Check(lines.Any(l => l.Contains("waiting for one to start", StringComparison.Ordinal)),
                "the service log must say it is waiting for a chosen application");
            host.Suspend("self-test: the app arrives");
            Check(!host.HasSessionWatcherForTest, "handing the send to the app must let go of the waiting watcher");

            var outputs = new Profile { Title = "self-test service", SelectedConnectedPeers = ["127.0.0.1:9"] };
            Check(!host.ApplyProfile(outputs) && !host.IsWaitingForChosenApplicationForTest,
                "output-device mode with nothing to send must not wait on sessions");
            outcome = "the decision holds; with no chosen application running the service waits with its watcher up, and lets it go on hand-over";
        }

        var root = FindSourceRoot();
        if (root is null) return Skip("the rules hold, but the source tree is not reachable (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var hostText = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "ServiceSendHost.cs"));
        foreach (var signature in new[] { "public void ReopenAfterResume()", "private void OnDeviceSetChanged()" })
        {
            var body = SourceMethodBody(hostText, signature);
            Check(body.Contains("Suspend(\"", StringComparison.Ordinal) && !body.Contains("Suspend();", StringComparison.Ordinal),
                $"{signature} must say why it stops the send — the default reason reads as the app arriving, which sent a bug hunt after hand-overs that never happened");
        }
        return outcome;
    }

    /// <summary>
    /// INSTALLING REMSOUND POINTS THE SERVICE AT THE INSTALLED COPY.
    ///
    /// <para>2026-09-13 review (Service #1). "Install RemSound on this PC" runs from the portable copy, and installed the
    /// service from there too. The service watches the folder it was installed from for updates — so it watched the
    /// portable folder, which no update ever reaches again. And with the service already installed, nothing changed at
    /// all.</para>
    /// </summary>
    private static string? AuditServiceFollowsTheInstalledCopy()
    {
        var built = ServiceControl.BuildElevatedArguments(ServiceControl.InstallVerb, @"C:\Users\someone\AppData\Local\Programs\RemSound\");
        Check(built.StartsWith(ServiceControl.InstallVerb, StringComparison.Ordinal)
              && built.EndsWith($"{ServiceControl.ServiceSourceArg} \"C:\\Users\\someone\\AppData\\Local\\Programs\\RemSound\"", StringComparison.Ordinal),
            $"the installer must name the installed folder, quoted, with no trailing backslash to swallow the quote (got: {built})");
        Check(!ServiceControl.BuildElevatedArguments(ServiceControl.InstallVerb).Contains(ServiceControl.ServiceSourceArg, StringComparison.Ordinal),
            "with no folder named, there is no source argument");
        Check(ServiceControl.ParseServiceSource(["--install-service", "--as-user", "S-1-5-21-1-2-3-1001", "--service-source", @"C:\Programs\RemSound"]) == @"C:\Programs\RemSound",
            "the elevated helper must read the named folder back");
        Check(ServiceControl.ParseServiceSource(["--install-service", "--service-source"]) is null, "a source argument with no folder names nothing");
        Check(ServiceControl.ParseServiceSource(["--install-service", "--service-source", "--as-user"]) is null, "the next argument is not a folder");
        Check(ServiceControl.ParseServiceSource(["--install-service"]) is null, "no argument, no folder");

        static bool HasExe(string path) => string.Equals(path, @"C:\Programs\RemSound\RemSound.exe", StringComparison.OrdinalIgnoreCase);
        Check(ServiceControl.ChooseServiceSource(@"C:\Programs\RemSound", @"D:\Portable", HasExe) == @"C:\Programs\RemSound",
            "a named folder holding RemSound.exe is where the service is installed from");
        Check(ServiceControl.ChooseServiceSource(@"C:\Elsewhere", @"D:\Portable", HasExe) == @"D:\Portable",
            "a named folder without RemSound.exe must not be taken — the service would copy nothing and watch nothing");
        Check(ServiceControl.ChooseServiceSource(@"Programs\RemSound", @"D:\Portable", _ => true) == @"D:\Portable",
            "a relative folder must never be taken by a helper running as administrator");
        Check(ServiceControl.ChooseServiceSource(null, @"D:\Portable", HasExe) == @"D:\Portable",
            "no folder named: the helper's own folder, as before");

        string? saved = null;
        bool Save(string folder) { saved = folder; return true; }
        Check(AppInstaller.RepointServiceSource(@"D:\Portable\", @"D:\Portable", @"C:\Programs\RemSound", Save) && saved == @"C:\Programs\RemSound",
            "a service already following the portable copy this install ran from must follow the installed copy — updates land there now");
        saved = null;
        Check(AppInstaller.RepointServiceSource(null, @"D:\Portable", @"C:\Programs\RemSound", Save) && saved == @"C:\Programs\RemSound",
            "a service following no folder must follow the installed copy");
        saved = null;
        Check(!AppInstaller.RepointServiceSource(@"E:\Another copy", @"D:\Portable", @"C:\Programs\RemSound", Save) && saved is null,
            "a service following some other copy was set up that way on purpose and must be left alone");
        Check(!AppInstaller.RepointServiceSource(@"c:\programs\remsound", @"D:\Portable", @"C:\Programs\RemSound", Save) && saved is null,
            "a service already following the installed copy needs nothing written");
        Check(!AppInstaller.RepointServiceSource(@"D:\Portable", @"D:\Portable", @"C:\Programs\RemSound", _ => false),
            "a write that did not take — another account's service folder — must not be reported as done");

        var root = FindSourceRoot();
        if (root is null) return Skip("the rules hold, but the source tree is not reachable (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var installer = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "AppInstaller.cs"));
        var install = SourceMethodBody(installer, "public static void RunInstall(IWin32Window owner, Action<string>? log = null)");
        Check(install.Contains("ServiceControl.InstallVerb, \"Installing the RemSound service...\", serviceSource: target", StringComparison.Ordinal),
            "installing the service during the app install must name the installed folder");
        Check(install.Contains("RepointServiceSource(", StringComparison.Ordinal),
            "with the service already installed, the app install must point it at the installed copy");
        var entry = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "ServiceEntry.cs"));
        Check(entry.Contains("ServiceControl.ElevatedServiceSource = ServiceControl.ParseServiceSource(args);", StringComparison.Ordinal),
            "the elevated helper must pick the named folder up");
        var control = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "ServiceControl.cs"));
        var doInstall = SourceMethodBody(control, "public static int DoInstall()");
        Check(doInstall.Contains("ChooseServiceSource(ElevatedServiceSource", StringComparison.Ordinal)
              && doInstall.Contains("IsUsableServiceSource(ElevatedServiceSource", StringComparison.Ordinal),
            "the service install must use the named folder, both fresh and when already installed");
        return "the installer names the installed folder; the helper takes it only when it holds RemSound.exe; an installed service follows the installed copy";
    }

    /// <summary>
    /// UNINSTALLING OFFERS TO REMOVE THE DAW PLUGIN AND THE SERVICE.
    ///
    /// <para>2026-09-13 review (Service #11). Uninstalling left the plugin in the DAW's folder with nothing to connect to,
    /// and left the service running — still sending at the lock screen, following a folder that no longer existed, so it
    /// never updated again. Nothing said so. Also: "Remove re&amp;cordings" and "&amp;Cancel" both used Alt+C.</para>
    /// </summary>
    private static string? AuditUninstallOffersThePluginAndTheService()
    {
        const string target = @"C:\Users\someone\AppData\Local\Programs\RemSound";
        using (var dialog = AppInstaller.BuildUninstallConfirmDialog(null, target, pluginInstalled: true, serviceInstalled: true, out var read))
        {
            var boxes = Stage2Descendants(dialog).OfType<CheckBox>().ToList();
            var plugin = boxes.FirstOrDefault(b => b.AccessibleName == "Remove the DAW plugin");
            var service = boxes.FirstOrDefault(b => b.AccessibleName == "Remove the RemSound service");
            Check(plugin is not null && service is not null,
                "with the plugin and the service installed, uninstalling must offer to remove both — they were left behind without a word");
            Check(plugin!.Checked && service!.Checked, "both must start ticked: they are parts of RemSound, not the user's data");
            var chosen = read();
            Check(chosen.RemovePlugin && chosen.RemoveService && !chosen.RemoveProfilesConfigLogs && !chosen.RemoveRecordings,
                "the ticks must come back as the choices, with the user's own data still kept by default");
            service!.Checked = false;
            Check(!read().RemoveService, "unticking the service must keep it");

            var keys = Stage2Descendants(dialog).OfType<ButtonBase>().Select(c => Stage2Mnemonic(c.Text)).Where(k => k is not null).ToList();
            Check(keys.Count == keys.Distinct().Count(),
                $"every Alt key in the uninstall dialog must be different — Remove recordings and Cancel both used Alt+C (keys: {string.Join(" ", keys)})");

            var order = new List<string>();
            Control? at = null;
            for (var guard = 0; guard < 200; guard++)
            {
                at = dialog.GetNextControl(at, forward: true);
                if (at is null) break;
                if (at is ButtonBase) order.Add(at.AccessibleName ?? at.Text);
            }
            string[] expected = ["Remove profiles, config and logs", "Remove recordings", "Remove the DAW plugin", "Remove the RemSound service", "OK", "Cancel"];
            Check(order.SequenceEqual(expected), $"Tab must go through the ticks, then OK, then Cancel (got: {string.Join(", ", order)})");
        }
        using (var dialog = AppInstaller.BuildUninstallConfirmDialog(null, target, pluginInstalled: false, serviceInstalled: false, out var read))
        {
            Check(!Stage2Descendants(dialog).OfType<CheckBox>().Any(b => b.AccessibleName is "Remove the DAW plugin" or "Remove the RemSound service"),
                "parts that aren't installed must not be offered");
            var chosen = read();
            Check(!chosen.RemovePlugin && !chosen.RemoveService, "parts that aren't installed can't be chosen");
        }

        var calls = new List<string>();
        var left = AppInstaller.RemoveChosenComponents(new AppInstaller.UninstallOptions { RemovePlugin = true, RemoveService = true },
            () => { calls.Add("plugin"); return (true, "removed"); }, () => { calls.Add("service"); return 0; }, log: null);
        Check(calls.SequenceEqual(["plugin", "service"]) && left is null,
            "ticked parts must be removed, and a clean removal adds nothing to the closing message");
        calls.Clear();
        var logged = new List<string>();
        left = AppInstaller.RemoveChosenComponents(new AppInstaller.UninstallOptions { RemovePlugin = false, RemoveService = true },
            () => { calls.Add("plugin"); return (true, "removed"); }, () => { calls.Add("service"); return -1; }, logged.Add);
        Check(calls.SequenceEqual(["service"]), "an unticked part must not be touched");
        Check(left is not null && left.Contains("service", StringComparison.Ordinal)
              && AppInstaller.FinishedUninstallMessage(left).Contains(left, StringComparison.Ordinal),
            "a service that was not removed must be named in the closing message — it keeps running");
        Check(logged.Any(l => l.StartsWith("uninstall: removing the service", StringComparison.Ordinal)),
            "the log must record how removing the service went");

        var root = FindSourceRoot();
        if (root is null) return Skip("the dialog and removals hold, but the source tree is not reachable (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var installer = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "AppInstaller.cs"));
        foreach (var signature in new[] { "public static void RunUninstallInProcess(IWin32Window owner, Action<string>? log = null)", "public static void RunUninstallStandalone()" })
        {
            var body = SourceMethodBody(installer, signature);
            var remove = body.IndexOf("RemoveChosenComponents(", StringComparison.Ordinal);
            var teardown = body.IndexOf("TearDownIntegration();", StringComparison.Ordinal);
            Check(remove >= 0 && teardown > remove, $"{signature} must remove the chosen parts before the install folder goes");
        }
        return "the plugin and the service are offered, ticked, only when installed; failures are named in the closing message; Alt keys are unique";
    }

    /// <summary>
    /// THE INSTALLED DAW PLUGIN FOLLOWS THE APP, AND REMOVING IT STAYS INSIDE ITS OWN FOLDER.
    ///
    /// <para>2026-09-13 review (Service #12, #10). The installed plugin was only ever refreshed from the menu, so after an
    /// update the DAW kept loading the old one — and the first time the plugin link changes, an old plugin can't connect.
    /// Separately, two "is this file ours?" guards were bare prefix checks: VST3\RemSoundX starts with VST3\RemSound.</para>
    /// </summary>
    private static string? AuditInstalledPluginFollowsTheApp()
    {
        var root = Path.Combine(Path.GetTempPath(), "remsound-plugin-refresh-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var target = Path.Combine(root, "VST3", "RemSound");
        var dll = Path.Combine(target, "RemSound.Plugin.dll");
        var stamp = Path.Combine(target, PluginInstaller.VersionStampName);
        try
        {
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "RemSound.Plugin.dll"), "this build");

            var (refreshed, message) = PluginInstaller.RefreshIfStaleForTest(source, target, "6.1");
            Check(!refreshed && message is null && !Directory.Exists(target), "starting the app must never install a plugin nobody installed");

            Check(PluginInstaller.InstallForTest(source, target, "6.0").Ok, "installing the plugin must succeed");
            Check(File.Exists(stamp) && File.ReadAllText(stamp).Trim() == "6.0", "an install must record which build placed the plugin");
            Check(File.ReadAllLines(Path.Combine(target, PluginInstaller.ManifestName)).Contains(PluginInstaller.VersionStampName),
                "the record must be listed with the placed files, so uninstall removes it too");

            // An install from before the record existed, holding an older build's files.
            File.Delete(stamp);
            File.WriteAllText(dll, "older build");
            (refreshed, message) = PluginInstaller.RefreshIfStaleForTest(source, target, "6.1");
            Check(refreshed, "a plugin placed by an older build must be brought up to date when the app starts — the DAW kept loading the old one");
            Check(File.ReadAllText(dll) == "this build", "the refresh must copy this build's files");
            Check(File.Exists(stamp) && File.ReadAllText(stamp).Trim() == "6.1", "the refresh must record this build");
            Check(message is not null && message.StartsWith("DAW plugin:", StringComparison.Ordinal) && message.Contains("6.1", StringComparison.Ordinal),
                $"the refresh must leave a line for the log naming the build (got: {message ?? "nothing"})");

            File.WriteAllText(dll, "left alone");
            (refreshed, message) = PluginInstaller.RefreshIfStaleForTest(source, target, "6.1");
            Check(!refreshed && message is null && File.ReadAllText(dll) == "left alone",
                "a plugin already from this build must not be copied again at every start");

            var neighbour = Path.Combine(root, "VST3", "RemSoundX");
            Directory.CreateDirectory(neighbour);
            var theirs = Path.Combine(neighbour, "their.vst3");
            File.WriteAllText(theirs, "not ours");
            File.AppendAllLines(Path.Combine(target, PluginInstaller.ManifestName), [@"..\RemSoundX\their.vst3"]);
            Check(PluginInstaller.UninstallForTest(target).Ok, "uninstall must complete");
            Check(File.Exists(theirs),
                "a manifest line into VST3\\RemSoundX must not delete that plugin's file — a folder whose name merely starts with ours is not ours");
            Check(!File.Exists(stamp), "uninstall must remove the build record it placed");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* temp */ } }

        Check(PathContainment.IsInside(@"C:\Plugins\RemSound\x.dll", @"C:\Plugins\RemSound"), "a file in the folder is inside it");
        Check(PathContainment.IsInside(@"C:\Plugins\RemSound\sub\x.dll", @"C:\Plugins\RemSound\"), "a trailing separator on the folder changes nothing");
        Check(PathContainment.IsInside(@"c:\plugins\remsound\x.dll", @"C:\Plugins\RemSound"), "case does not matter in Windows paths");
        Check(!PathContainment.IsInside(@"C:\Plugins\RemSoundX\x.dll", @"C:\Plugins\RemSound"), "RemSoundX is not inside RemSound");
        Check(!PathContainment.IsInside(@"C:\Plugins\RemSound\..\Other\x.dll", @"C:\Plugins\RemSound"), "climbing out with .. is not inside");
        Check(!PathContainment.IsInside(@"C:\Plugins\RemSound", @"C:\Plugins\RemSound"), "the folder itself is not inside itself");
        Check(!PathContainment.IsInside(null, @"C:\Plugins\RemSound") && !PathContainment.IsInside(@"C:\x", null), "nothing is inside nothing");

        var src = FindSourceRoot();
        if (src is null) return Skip("the rules hold, but the source tree is not reachable (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var main = File.ReadAllText(Path.Combine(src, "src", "RemSound.App", "MainForm.cs"));
        Check(SourceMethodBody(main, "private void RunStartupNotices()").Contains("RefreshInstalledPluginIfStale();", StringComparison.Ordinal)
              && SourceMethodBody(main, "private void RefreshInstalledPluginIfStale()").Contains("PluginInstaller.RefreshIfStale()", StringComparison.Ordinal),
            "starting the app must bring an installed plugin up to date");
        var autostart = File.ReadAllText(Path.Combine(src, "src", "RemSound.App", "StartupAutoStart.cs"));
        Check(SourceMethodBody(autostart, "public static bool TryDisableIfPointsInto(string folder)").Contains("PathContainment.IsInside(", StringComparison.Ordinal),
            "uninstall must clear only an autostart entry inside the removed folder — Programs\\RemSound2 is not inside Programs\\RemSound");
        return "an older plugin is refreshed at start, the same build is left alone, a missing one is never installed; removal stays inside its folder";
    }

    /// <summary>
    /// --CLOSE ASKS THE RUNNING COPY TO CLOSE BEFORE ENDING IT.
    ///
    /// <para>2026-09-13 review (Service #26). RemSound --close ended the running copy outright: a recording lost its ending,
    /// the router's port mapping stayed open and the log lost its last line. It now asks first, and the running copy closes
    /// the way File, Exit does. Private names throughout — the real ones would reach the RemSound actually open.</para>
    /// </summary>
    private static string? AuditCloseFromTheCommandLineAsksFirst()
    {
        var id = Guid.NewGuid().ToString("N");
        var closeName = $"RemSound.SelfTest.Close.{id}";
        using (var coordinator = new SingleInstanceCoordinator($"RemSound.SelfTest.Mutex.{id}", $"RemSound.SelfTest.Activate.{id}", closeName))
        {
            Check(coordinator.TryAcquire(TimeSpan.Zero), "a private lock nobody else holds must be taken");
            var closes = 0;
            var activates = 0;
            coordinator.CloseRequested += () => Interlocked.Increment(ref closes);
            coordinator.ActivateRequested += () => Interlocked.Increment(ref activates);
            Check(!SingleInstanceCoordinator.RequestGracefulClose(closeName),
                "with nothing listening, the request must say nobody heard it, so --close goes on to end the process");
            coordinator.StartActivationListener();
            Check(SingleInstanceCoordinator.RequestGracefulClose(closeName), "a running copy must be listening for the close request");
            Check(WaitUntil(() => Volatile.Read(ref closes) == 1), "the request must reach the running copy as a close");
            Check(Volatile.Read(ref activates) == 0, "a close must not be taken for 'come to the front'");
        }
        Check(!SingleInstanceCoordinator.RequestGracefulClose(closeName), "a copy that has closed stops listening");

        var root = FindSourceRoot();
        if (root is null) return Skip("the signal works, but the source tree is not reachable (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var close = SourceMethodBody(File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "CommandLine.cs")), "private static int CloseRunning()");
        var ask = close.IndexOf("RequestGracefulClose(", StringComparison.Ordinal);
        var force = close.IndexOf("ForceCloseOtherInstances(", StringComparison.Ordinal);
        Check(ask >= 0 && force > ask, "--close must ask the running copy to close before ending the process");
        Check(File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "Program.cs"))
                .Contains("instance.CloseRequested += () => activeMainForm?.CloseFromCommandLine();", StringComparison.Ordinal),
            "the running copy must pass a close request to its window");
        var main = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "MainForm.cs"));
        Check(Regex.IsMatch(SourceMethodBody(main, "protected override void OnFormClosing(FormClosingEventArgs e)"), @"skipPrompt\s*=[^;]*closingFromCommandLine"),
            "a close from the command line must not stop at the unsaved-changes question with nobody there to answer it");
        Check(SourceMethodBody(main, "internal void CloseFromCommandLine()").Contains("Application.Exit()", StringComparison.Ordinal),
            "a close from the command line must close every window, open dialogs included");
        return "a close request reaches the running copy as a close, and --close asks before it forces";
    }

    /// <summary>
    /// --LIST-PROFILES FINDS THE PROFILES THE APP SAVED.
    ///
    /// <para>2026-09-13 review (Other #7). It read the profiles base folder, while profiles live in a folder per machine
    /// below it, so it printed "No saved profiles" on every ordinary install.</para>
    /// </summary>
    private static string? AuditListProfilesFindsSavedProfiles()
    {
        var cfg = AppConfig.Load();
        var appStore = cfg.CreateStore();
        var title = "zz self-test listing " + Guid.NewGuid().ToString("N")[..8];
        appStore.Save(new Profile { Title = title });
        try
        {
            Check(CommandLine.ProfileStoreForListing(cfg).ListProfileTitles().Contains(title),
                "--list-profiles must list a profile the app saved");
        }
        finally { appStore.Delete(title); }
        return "a profile the app saves is listed";
    }

    /// <summary>
    /// A PROFILE HOLDING A RETIRED CONCEALMENT TONE LOADS AS NOISE BURST.
    ///
    /// <para>2026-09-13 review (Core #5). The cosine tones were taken out of the choice and three places said old profiles
    /// were moved to Noise burst. Nothing did: the dropdown showed "Noise burst" while the tone played.</para>
    /// </summary>
    private static string? AuditRetiredConcealmentTonesLoadAsNoiseBurst()
    {
        var store = new RemSoundSettingsStore("RemSound");
        var before = store.LoadConcealmentArtifact();
        try
        {
            foreach (var (saved, expected) in new[]
                     {
                         (ConcealmentArtifact.CosineToneShort, ConcealmentArtifact.NoiseBurst),
                         (ConcealmentArtifact.CosineToneLow, ConcealmentArtifact.NoiseBurst),
                         (ConcealmentArtifact.NoiseBurst, ConcealmentArtifact.NoiseBurst),
                         (ConcealmentArtifact.Click, ConcealmentArtifact.Click),
                     })
            {
                store.SaveConcealmentArtifact(saved);
                var loaded = store.LoadConcealmentArtifact();
                Check(loaded == expected, $"a profile holding {saved} must load as {expected} (got {loaded})");
            }
        }
        finally { store.SaveConcealmentArtifact(before); }
        return "both retired tones load as noise burst; noise burst and click load as saved";
    }

    /// <summary>
    /// KEEPING THE MACHINE AWAKE BELONGS TO THE PROCESS, NOT ONE THREAD.
    ///
    /// <para>2026-09-13 review (Service #14). "System required" was set with SetThreadExecutionState, which belongs to the
    /// thread that calls it. The service switches Full CPU speed on and off from different threads, so switching it off
    /// could leave the machine held awake from another thread for good. It now rides on the process's power request.</para>
    /// </summary>
    private static string? AuditKeepAwakeBelongsToTheProcess()
    {
        var root = FindSourceRoot();
        if (root is null) return Skip("the source tree is not reachable from here (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var text = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "PerformanceMode.cs"));
        Check(!text.Contains("SetThreadExecutionState(", StringComparison.Ordinal),
            "keeping the machine awake must not be set per thread — switching it off from another thread leaves it on");
        Check(SourceMethodBody(text, "private static void TryStartPowerRequest(Action<string>? log)")
                .Contains("PowerSetRequest(handle, PowerRequestSystemRequired)", StringComparison.Ordinal),
            "switching on must ask to keep the machine awake on the process's power request");
        Check(SourceMethodBody(text, "private static void TryStopPowerRequest(Action<string>? log)")
                .Contains("PowerClearRequest(powerRequestHandle, PowerRequestSystemRequired)", StringComparison.Ordinal),
            "switching off must clear that request");
        return "system-required is set and cleared on the process's power request";
    }

    /// <summary>
    /// THE PLUGIN FILES ARE GATHERED AFTER THE PLUGIN BUILDS, AND --HELP IS CURRENT.
    ///
    /// <para>2026-09-13 review (Service #13). The build gathered the plugin's files when the project was first read — before
    /// the plugin was built — so a fresh clone's first build or release had an empty plugin folder. And --help still showed
    /// a --selftest --opus example and said --config-dir held sounds.</para>
    /// </summary>
    private static string? AuditPluginFilesAreGatheredAfterThePluginBuilds()
    {
        var root = FindSourceRoot();
        if (root is null) return Skip("the source tree is not reachable from here (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var csproj = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "RemSound.App.csproj"));
        var outsideTargets = Regex.Replace(csproj, @"<Target\b.*?</Target>", "", RegexOptions.Singleline);
        Check(!outsideTargets.Contains(@"RemSound.Plugin\bin", StringComparison.OrdinalIgnoreCase),
            "the plugin's build output must be gathered inside the copy targets, when they run — not when the project is first read");
        Check(Regex.IsMatch(csproj, @"<Target Name=""CopyPluginPayloadToPublish"".*?<Error Condition=""'@\(_PluginPayloadForPublish\)' == ''""", RegexOptions.Singleline),
            "a publish must stop rather than ship without the plugin");
        Check(Directory.Exists(PluginInstaller.SourceDirectory) && Directory.EnumerateFiles(PluginInstaller.SourceDirectory, "*", SearchOption.AllDirectories).Any(),
            "this build's plugin folder must not be empty");

        var commandLine = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "CommandLine.cs"));
        Check(!commandLine.Contains("--selftest --opus", StringComparison.Ordinal), "--help must not show an option that no longer exists");
        Check(!commandLine.Contains("logs and sounds", StringComparison.Ordinal), "--help must not say --config-dir holds sounds");
        return "plugin files are gathered at copy time and a publish refuses an empty set; --help is current";
    }

    private static IEnumerable<Control> Stage2Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var below in Stage2Descendants(child)) yield return below;
        }
    }

    /// <summary>The Alt key a control's text gives it, upper-cased, or null. "&amp;&amp;" is a literal ampersand.</summary>
    private static char? Stage2Mnemonic(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (text[i] != '&') continue;
            if (text[i + 1] == '&') { i++; continue; }
            return char.ToUpperInvariant(text[i + 1]);
        }
        return null;
    }
}
