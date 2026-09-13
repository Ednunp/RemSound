using System.Diagnostics;
using System.Drawing;
using System.Text;
using Microsoft.Win32;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// "Install RemSound on this PC" — promotes a portable copy (a folder the user extracted from the
/// zip and runs from Downloads / a USB stick / wherever) into a proper per-user install under
/// <c>%LOCALAPPDATA%\Programs\RemSound</c>, with optional desktop and Start-menu shortcuts, a
/// Windows "Installed apps" entry, and login auto-start. Modelled on Andre's Sensor Readout
/// self-installer.
///
/// Design choices, all deliberate:
///   * <b>Per-user location</b> (<c>%LOCALAPPDATA%\Programs</c>), never Program Files. No admin /
///     UAC prompt, and the silent auto-updater keeps working because it can write its own folder.
///   * <b>No separate installer .exe / script.</b> A screen-reader user never has to hunt down and
///     run a loose file — it's a menu item inside the app they already have open.
///   * <b>The whole folder is the install.</b> RemSound reads its settings/profiles/logs relative to
///     its own exe (see <see cref="AppConfig.UserDataDirectory"/>), so once it's running from the
///     install folder it finds everything there automatically — no AppData lookup, no code changes.
///     The installer just copies the program files (always) plus, per the user's tick-boxes, their
///     "user settings and logs" data and their recordings into the same layout inside the target.
///   * <b>Batch uninstaller, not PowerShell.</b> A running exe can't delete its own folder, so a
///     tiny <c>.cmd</c> waits for RemSound to close then removes the folder. Batch (not a .ps1) so a
///     locked-down PowerShell execution policy can never block an uninstall.
///
/// Entry points: <see cref="RunInstall"/> (Options → Install…), <see cref="RunUninstallInProcess"/>
/// (Options → Uninstall… while running the installed copy) and <see cref="RunUninstallStandalone"/>
/// (the <c>--uninstall</c> switch behind the Start-menu / Installed-apps "Uninstall" entry).
/// </summary>
internal static class AppInstaller
{
    private const string AppName = "RemSound";
    private const string ExeName = "RemSound.exe";
    private const string ManualName = "readme.html";

    /// <summary>Windows "Installed apps" registration lives under the per-user hive (no admin).</summary>
    private const string UninstallRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\RemSound";

    /// <summary>Subfolders/entries beside the exe that are USER DATA, not program files — copied only
    /// when the matching tick-box is set, never as part of the always-copied program files.</summary>
    private static readonly string[] UserDataEntries =
    {
        AppConfig.UserDataFolderName, // "user settings and logs" (config + profiles + logs)
        "recordings",
    };

    /// <summary>Filename of the marker RemSound drops beside its exe when it installs itself. Its
    /// PRESENCE is the authority for "am I an installed copy" — an explicit token the installer writes,
    /// NOT a guess based on which folder the exe happens to sit in. Inferring install-state from the
    /// path (does my folder equal the computed install folder?) is fragile: it breaks if the install
    /// location ever moves, if the path resolves differently, or if the folder is relocated. A fresh
    /// copy unzipped from the release has no marker, so it correctly knows it's portable.</summary>
    private const string InstalledMarkerName = "installed.marker";

    /// <summary>The fixed per-user install location: <c>%LOCALAPPDATA%\Programs\RemSound</c>.</summary>
    public static string InstallFolder
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                localAppData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local");
            }
            return Path.Combine(localAppData, "Programs", AppName);
        }
    }

    private static string MarkerPath(string folder) => Path.Combine(folder, InstalledMarkerName);

    /// <summary>True when THIS running copy is an installed one — decided purely by whether the
    /// installer's marker file sits beside this exe, never by comparing folder paths. The Options menu
    /// uses it to show "Uninstall…" instead of "Install…".</summary>
    public static bool IsInstalledCopy => File.Exists(MarkerPath(AppContext.BaseDirectory));

    /// <summary>True when an install already exists at the target location (a re-run becomes an
    /// update) — read from the destination's marker.</summary>
    private static bool InstallExistsAtTarget => File.Exists(MarkerPath(InstallFolder));

    // ---------------- install ----------------

    /// <summary>Options → "Install RemSound on this PC". Shows the tick-box dialog, copies the files,
    /// wires up shortcuts/registration/auto-start, then relaunches the installed copy and asks the
    /// portable one to exit. <paramref name="log"/> (may be null) records milestones to the diagnostic
    /// log when logging is on.</summary>
    public static void RunInstall(IWin32Window owner, Action<string>? log = null)
    {
        var source = NormalizeFolder(AppContext.BaseDirectory);
        var target = NormalizeFolder(InstallFolder);

        if (IsInstalledCopy)
        {
            MessageBox.Show(owner,
                "This copy of RemSound is already installed, so there's nothing to do. " +
                "To remove it, use Uninstall RemSound from this PC.",
                "Already installed", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Degenerate case: the portable copy was extracted straight into the install location (no marker
        // yet, so IsInstalledCopy is false). Copying the folder onto itself would throw an IOException
        // (self-copy) and abort with a scary message. Detect it and skip the copy — we're already in
        // place, so registration (marker + shortcuts) is all that's needed.
        var sameLocation = string.Equals(source, target, StringComparison.OrdinalIgnoreCase);
        var updating = InstallExistsAtTarget;
        var options = ShowInstallOptionsDialog(owner, target, updating);
        if (options is null) return; // cancelled

        var installedExe = Path.Combine(target, ExeName);
        try
        {
            log?.Invoke($"install: starting {(updating ? "update" : "fresh install")} to \"{target}\" " +
                        $"(desktop={options.CreateDesktopShortcut}, startMenu={options.CreateStartMenuFolder}, " +
                        $"startup={options.RunAtStartup}, profiles={options.CopyProfilesAndConfig}, " +
                        $"recordings={options.CopyRecordings}, logs={options.CopyLogs})");

            // The file copy is the part that must succeed — a failure here (disk full, permissions)
            // aborts with the copy untouched. Skipped entirely when we're already running from the
            // target (self-copy would throw); registration below still runs.
            Directory.CreateDirectory(target);
            if (!sameLocation)
            {
                CopyProgramFiles(source, target);
                CopyUserData(source, target, options);
            }

            // Drop the marker that tells the installed copy it IS installed (so the Options menu shows
            // Uninstall). Part of the must-succeed path: without it the install wouldn't recognise
            // itself. Written last, after the files are safely in place.
            File.WriteAllText(MarkerPath(target),
                $"RemSound {CommandLine.AppVersion} was installed here by the in-app installer." + Environment.NewLine +
                "RemSound checks for this file to know it is installed rather than portable." + Environment.NewLine +
                "Removing this file makes RemSound treat this copy as portable again.", new UTF8Encoding(false));

            // Everything below is best-effort desktop integration: a hiccup in one (e.g. a policy
            // blocking a shortcut) is logged but must NOT undo a good install — the app is already
            // sitting installed, and the user can add a shortcut by hand.
            TryStep(log, "desktop shortcut", () => SetDesktopShortcut(options.CreateDesktopShortcut, installedExe, target));
            TryStep(log, "Start menu folder", () => SetStartMenuFolder(options.CreateStartMenuFolder, installedExe, target));
            // Re-point (or clear) login auto-start so it launches the INSTALLED exe, not the portable
            // one the user may delete. Either way the entry now reflects the tick-box and the new path.
            TryStep(log, "run at startup", () =>
            {
                if (options.RunAtStartup) StartupAutoStart.TryEnable(installedExe);
                else StartupAutoStart.TryDisable();
            });
            TryStep(log, "register in Installed apps", () => RegisterInstalledApp(target, installedExe));

            log?.Invoke("install: files copied and shortcuts written; relaunching installed copy");
        }
        catch (Exception ex)
        {
            log?.Invoke($"install: FAILED — {ex.GetType().Name}: {ex.Message}");
            MessageBox.Show(owner,
                "RemSound could not finish installing:" + Environment.NewLine + Environment.NewLine + ex.Message +
                Environment.NewLine + Environment.NewLine +
                "Nothing was changed to the copy you're running now — you can keep using it.",
                "Install failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        MessageBox.Show(owner,
            $"RemSound is now installed on this PC, in:{Environment.NewLine}{target}{Environment.NewLine}{Environment.NewLine}" +
            "Press OK to finish. RemSound will now close and reopen from the new install location.",
            "RemSound installed", MessageBoxButtons.OK, MessageBoxIcon.Information);

        // Offer to install the send-only Windows service too (Ed, 2026-07-17). It's a separate, optional
        // component that needs its own admin (UAC) step, so we ask rather than assume. Declining is fine —
        // it can be installed any time from the app's Service menu. Never block the app install on it.
        try
        {
            if (!ServiceControl.IsInstalled())
            {
                var wantService = MessageBox.Show(owner,
                    "Do you also want to install the RemSound service?" + Environment.NewLine + Environment.NewLine +
                    "The service streams this PC's audio to your RemSound peers even when nobody is logged in " +
                    "(for example at the lock screen after a reboot). It's send-only and steps aside whenever the " +
                    "RemSound app is open. You can install or remove it later from the app's Service menu.",
                    "Install the RemSound service?", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (wantService == DialogResult.Yes)
                {
                    log?.Invoke("install: user opted to install the service too");
                    // Installed FROM the installed copy's folder, not this portable one: the service watches the folder it
                    // was installed from for updates, and updates now land there.
                    var rc = RunElevatedResponsive(owner, ServiceControl.InstallVerb, "Installing the RemSound service...", serviceSource: target);
                    if (rc == 0)
                    {
                        // Offer to start it now — otherwise it only comes up at the next boot, so a
                        // first-time user sees nothing happen after installing it.
                        var startNow = MessageBox.Show(owner,
                            "The RemSound service was installed." + Environment.NewLine + Environment.NewLine +
                            "Do you want to start it now? It will also start automatically at every boot. " +
                            "You can configure who it sends to from the app's Service menu.",
                            "Start the RemSound service?", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                        if (startNow == DialogResult.Yes)
                        {
                            log?.Invoke("install: user opted to start the service now");
                            var startRc = RunElevatedResponsive(owner, ServiceControl.StartVerb, "Starting the RemSound service...");
                            MessageBox.Show(owner,
                                startRc == 0
                                    ? "The RemSound service is running."
                                    : "The service was installed but couldn't be started just now. You can start it later from the app's Service menu.",
                                "RemSound service", MessageBoxButtons.OK, startRc == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                        }
                    }
                    else
                    {
                        MessageBox.Show(owner,
                            "The RemSound service was not installed (the elevation prompt was declined, or it failed). You can try again later from the app's Service menu.",
                            "RemSound service", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
            else
            {
                // The service is already here — perhaps installed from the portable copy this install ran from. It watches
                // the folder it was installed from for updates, and updates now land in the installed copy, so point it
                // there. The account that installed the service can write its folder, so there's no administrator prompt;
                // any other account can't, and the service keeps following its old folder. 2026-09-13 review.
                var recorded = RemSound.Core.ServiceStore.LoadAppSourcePath();
                if (RepointServiceSource(recorded, source, target, folder =>
                    {
                        RemSound.Core.ServiceStore.SaveAppSourcePath(folder);
                        return SameFolder(RemSound.Core.ServiceStore.LoadAppSourcePath(), folder);
                    }))
                    log?.Invoke($"install: the service now follows the installed copy for updates (it was following \"{recorded}\")");
            }
        }
        catch (Exception ex) { log?.Invoke($"install: optional service step skipped ({ex.GetType().Name}: {ex.Message})"); }

        // Hand over cleanly. We can't just launch the installed exe and exit: the single-instance
        // lock would still be held for the instant it takes us to shut down, and the new copy would
        // see "already running". So a tiny batch waits for THIS process to exit (lock released), then
        // starts the installed copy. Then we exit decisively.
        try { StartRelaunchAfterExit(installedExe, target); } catch { /* fall through — worst case the user starts it from the shortcut */ }
        Environment.Exit(0);
    }

    /// <summary>Run an elevated service verb while keeping the UI thread ALIVE. The install flow is
    /// sequential (offer → install → offer start → start → relaunch), so a fire-and-forget task doesn't
    /// fit — but blocking the UI thread in WaitForExit froze the window and any live audio (the same
    /// hang class as the service install-freeze). This runs the elevated helper on a worker while a tiny
    /// modal "working…" shell pumps messages: the app stays responsive, the user can't double-trigger
    /// anything, NVDA announces what's happening, and the call still returns the exit code in-line.</summary>
    private static int RunElevatedResponsive(IWin32Window? owner, string verb, string statusText, string? serviceSource = null)
    {
        var rc = -1;
        using var wait = new Form
        {
            Text = "RemSound",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ControlBox = false,
            ShowInTaskbar = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(24),
            AccessibleName = statusText,
        };
        wait.Controls.Add(new Label
        {
            Text = statusText + Environment.NewLine + "Windows may ask for administrator permission.",
            AutoSize = true,
            AccessibleName = statusText,
        });
        wait.Shown += (_, _) => Task.Run(() =>
        {
            try { rc = ServiceControl.RunElevated(verb, serviceSource); }
            catch { rc = -1; }
            try { wait.BeginInvoke(new Action(wait.Close)); } catch { /* already closed */ }
        });
        wait.ShowDialog(owner);
        return rc;
    }

    // ---------------- uninstall ----------------

    /// <summary>Options → "Uninstall RemSound from this PC", chosen from inside the running installed
    /// copy. THIS process holds the files open, so we confirm, tear down shortcuts/registration/
    /// auto-start, launch the wait-then-delete batch, and exit so it can remove the folder.</summary>
    public static void RunUninstallInProcess(IWin32Window owner, Action<string>? log = null)
    {
        // The folder we're running from IS the install (the marker confirms it) — delete that, rather
        // than a separately computed path, so uninstall never targets the wrong place.
        var target = NormalizeFolder(AppContext.BaseDirectory);
        if (!IsInstalledCopy)
        {
            MessageBox.Show(owner,
                "This copy of RemSound isn't an installed one, so there's nothing to uninstall from here. " +
                "You can just delete this folder.",
                "Not an installed copy", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var options = ShowUninstallConfirmDialog(owner, target);
        if (options is null) return; // cancelled

        log?.Invoke($"uninstall: starting (removeProfilesConfigLogs={options.RemoveProfilesConfigLogs}, " +
                    $"removeRecordings={options.RemoveRecordings}, removePlugin={options.RemovePlugin}, removeService={options.RemoveService})");
        var leftBehind = RemoveChosenComponents(options, PluginInstaller.Uninstall,
            () => RunElevatedResponsive(owner, ServiceControl.UninstallVerb, "Removing the RemSound service..."), log);
        TearDownIntegration();
        try { StartDeleteAfterExit(target, options.RemoveProfilesConfigLogs, options.RemoveRecordings); }
        catch (Exception ex)
        {
            log?.Invoke($"uninstall: could not start remover — {ex.Message}");
            MessageBox.Show(owner,
                "RemSound couldn't start the uninstaller:" + Environment.NewLine + Environment.NewLine + ex.Message,
                "Uninstall failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Uninstall doesn't relaunch — just tell the user it's done, then close (the remover finishes
        // deleting the folder the moment this process exits).
        MessageBox.Show(owner, FinishedUninstallMessage(leftBehind),
            "RemSound uninstalled", MessageBoxButtons.OK, leftBehind is null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        Environment.Exit(0);
    }

    /// <summary>The <c>--uninstall</c> switch: run from the Start-menu "Uninstall" shortcut or Windows
    /// "Installed apps". A separate installed copy may still be running, so we confirm, force-close any
    /// running RemSound, tear down integration, and start the wait-then-delete batch. Called very early
    /// in <see cref="Program"/>, before the single-instance guard, and always ends the process.</summary>
    public static void RunUninstallStandalone()
    {
        // Reached only from the installed exe (the Start-menu / Installed-apps Uninstall entries run
        // "<installed exe> --uninstall"), so the folder we're in is the real install — gated by the
        // marker so a stray "--uninstall" on a portable copy does nothing.
        var target = NormalizeFolder(AppContext.BaseDirectory);
        if (!IsInstalledCopy)
        {
            MessageBox.Show(
                "This copy of RemSound isn't an installed one, so there's nothing to remove.",
                "Nothing to uninstall", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var options = ShowUninstallConfirmDialog(null, target);
        if (options is null) return; // cancelled

        // Close any running RemSound (the installed copy) so its files unlock; the remover also retries,
        // so exact timing doesn't matter.
        try { SingleInstanceCoordinator.ForceCloseOtherInstances(); } catch { /* remover retries regardless */ }
        var leftBehind = RemoveChosenComponents(options, PluginInstaller.Uninstall,
            () => RunElevatedResponsive(null, ServiceControl.UninstallVerb, "Removing the RemSound service..."), null);
        TearDownIntegration();
        try { StartDeleteAfterExit(target, options.RemoveProfilesConfigLogs, options.RemoveRecordings); }
        catch (Exception ex)
        {
            MessageBox.Show(
                "RemSound couldn't start the uninstaller:" + Environment.NewLine + Environment.NewLine + ex.Message,
                "Uninstall failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        MessageBox.Show(FinishedUninstallMessage(leftBehind),
            "RemSound uninstalled", MessageBoxButtons.OK, leftBehind is null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    /// <summary>
    /// Take away the separately installed parts the user ticked, before the folder goes: the DAW plugin, and the service
    /// (removed elevated). Returns a sentence for the closing message about anything that could not be removed, or null.
    /// </summary>
    internal static string? RemoveChosenComponents(UninstallOptions options, Func<(bool Ok, string Message)> removePlugin,
        Func<int> removeService, Action<string>? log)
    {
        var problems = new List<string>();
        if (options.RemovePlugin)
        {
            var (ok, message) = removePlugin();
            log?.Invoke($"uninstall: DAW plugin — {message}");
            if (!ok) problems.Add("The DAW plugin could not be removed — close your music software and remove it from its plugin folder.");
        }
        if (options.RemoveService)
        {
            var rc = removeService();
            log?.Invoke($"uninstall: removing the service finished with code {rc}");
            if (rc != 0) problems.Add("The RemSound service was not removed (administrator permission was declined, or it failed), so it is still installed.");
        }
        return problems.Count == 0 ? null : string.Join(Environment.NewLine, problems);
    }

    internal static string FinishedUninstallMessage(string? leftBehind) =>
        leftBehind is null
            ? "RemSound has been removed from this PC. Press OK to finish."
            : "RemSound has been removed from this PC." + Environment.NewLine + Environment.NewLine + leftBehind
              + Environment.NewLine + Environment.NewLine + "Press OK to finish.";

    /// <summary>Remove everything the install put OUTSIDE its own folder — desktop + Start-menu
    /// shortcuts, the login auto-start entry, and the Windows "Installed apps" registration. The
    /// folder itself is deleted by the batch remover after RemSound closes.</summary>
    private static void TearDownIntegration()
    {
        try { SetDesktopShortcut(false, "", ""); } catch { }
        try { SetStartMenuFolder(false, "", ""); } catch { }
        // Only clear login-autostart if it points at THIS installed copy — never wipe a different
        // copy's (e.g. a portable copy's) autostart entry that shares the "RemSound" value name.
        try { StartupAutoStart.TryDisableIfPointsInto(AppContext.BaseDirectory); } catch { }
        try { UnregisterInstalledApp(); } catch { }
    }

    // ---------------- file copy ----------------

    /// <summary>Copy the PROGRAM files (exe, DLLs, native runtimes, default sounds, manual, Tolk, …) —
    /// everything beside the exe EXCEPT the user-data entries, which are handled separately so the
    /// tick-boxes can include or exclude them. Overwrites, so a re-run cleanly updates/repairs.</summary>
    private static void CopyProgramFiles(string source, string target)
    {
        foreach (var file in Directory.GetFiles(source))
        {
            // Never carry a stray install marker across — the installer writes a fresh one into the
            // target itself, so a source copy's marker must not leak in and mislabel things.
            if (string.Equals(Path.GetFileName(file), InstalledMarkerName, StringComparison.OrdinalIgnoreCase))
                continue;
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (UserDataEntries.Any(e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase)))
                continue; // user data — copied conditionally below
            CopyDirectory(dir, Path.Combine(target, name));
        }
    }

    /// <summary>Copy the user's own data into the same layout inside the install folder, per the
    /// tick-boxes: profiles + the global config file, recordings, and logs — each independently.</summary>
    private static void CopyUserData(string source, string target, InstallOptions options)
    {
        var srcData = Path.Combine(source, AppConfig.UserDataFolderName);
        var dstData = Path.Combine(target, AppConfig.UserDataFolderName);

        if (options.CopyProfilesAndConfig)
        {
            var srcProfiles = Path.Combine(srcData, "profiles");
            if (Directory.Exists(srcProfiles)) CopyDirectory(srcProfiles, Path.Combine(dstData, "profiles"));
            var srcConfig = Path.Combine(srcData, "global config.json");
            if (File.Exists(srcConfig))
            {
                Directory.CreateDirectory(dstData);
                File.Copy(srcConfig, Path.Combine(dstData, "global config.json"), overwrite: true);
            }
        }

        if (options.CopyLogs)
        {
            var srcLogs = Path.Combine(srcData, "logs");
            if (Directory.Exists(srcLogs)) CopyDirectory(srcLogs, Path.Combine(dstData, "logs"));
        }

        if (options.CopyRecordings)
        {
            var srcRec = Path.Combine(source, "recordings");
            if (Directory.Exists(srcRec)) CopyDirectory(srcRec, Path.Combine(target, "recordings"));
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    // ---------------- shortcuts ----------------

    private static string DesktopShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    /// <summary>The per-user Start-menu Programs folder that holds RemSound's shortcuts group.</summary>
    private static string StartMenuFolderPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName);

    private static void SetDesktopShortcut(bool enabled, string targetExe, string workingDir)
    {
        if (!enabled)
        {
            if (File.Exists(DesktopShortcutPath)) File.Delete(DesktopShortcutPath);
            return;
        }
        CreateShortcut(DesktopShortcutPath, targetExe, "", workingDir, "RemSound low-latency audio", targetExe);
    }

    /// <summary>Create (or remove) the Start-menu "RemSound" folder with three shortcuts: the program,
    /// the manual, and Uninstall — the classic "installed app" grouping the user asked for.</summary>
    private static void SetStartMenuFolder(bool enabled, string targetExe, string workingDir)
    {
        var folder = StartMenuFolderPath;
        if (!enabled)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); } catch { }
            return;
        }

        Directory.CreateDirectory(folder);
        CreateShortcut(Path.Combine(folder, AppName + ".lnk"),
            targetExe, "", workingDir, "RemSound low-latency audio", targetExe);

        var manual = Path.Combine(workingDir, ManualName);
        if (File.Exists(manual))
            CreateShortcut(Path.Combine(folder, "RemSound Manual.lnk"),
                manual, "", workingDir, "Open the RemSound manual", targetExe);

        CreateShortcut(Path.Combine(folder, "Uninstall RemSound.lnk"),
            targetExe, "--uninstall", workingDir, "Uninstall RemSound from this PC", targetExe);
    }

    /// <summary>Write a .lnk using the Windows Script Host shell object via late-bound COM, so there's
    /// no extra library to ship and no IShellLink P/Invoke to maintain. Best-effort per shortcut.</summary>
    private static void CreateShortcut(string linkPath, string targetPath, string arguments,
        string workingDir, string description, string iconSourceExe)
    {
        var dir = Path.GetDirectoryName(linkPath);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(linkPath)) File.Delete(linkPath);

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return;
        dynamic? shell = null;
        object? linkObj = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            dynamic link = shell!.CreateShortcut(linkPath);
            linkObj = link; // keep a handle so we can release this SECOND COM object too, not just shell
            link.TargetPath = targetPath;
            link.Arguments = arguments;
            link.WorkingDirectory = workingDir;
            link.Description = description;
            if (!string.IsNullOrWhiteSpace(iconSourceExe)) link.IconLocation = iconSourceExe + ",0";
            link.Save();
        }
        finally
        {
            // Release both COM objects deterministically (the IWshShortcut from CreateShortcut AND the
            // WScript.Shell), rather than leaving the shortcut object to the GC finalizer.
            if (linkObj is not null)
            {
                try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(linkObj); } catch { }
            }
            if (shell is not null)
            {
                try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); } catch { }
            }
        }
    }

    // ---------------- "Installed apps" registration ----------------

    private static void RegisterInstalledApp(string installFolder, string installedExe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallRegistryPath, writable: true);
        if (key is null) return;
        key.SetValue("DisplayName", "RemSound");
        key.SetValue("DisplayVersion", CommandLine.AppVersion);
        key.SetValue("Publisher", "RemSound");
        key.SetValue("InstallLocation", installFolder);
        key.SetValue("DisplayIcon", installedExe);
        key.SetValue("UninstallString", $"\"{installedExe}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{installedExe}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        try { key.SetValue("EstimatedSize", (int)(FolderSizeBytes(installFolder) / 1024), RegistryValueKind.DWord); }
        catch { /* size is cosmetic in the Apps list — skip if it can't be measured */ }
    }

    private static void UnregisterInstalledApp()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallRegistryPath, throwOnMissingSubKey: false); }
        catch { }
    }

    private static long FolderSizeBytes(string folder)
    {
        long total = 0;
        foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(f).Length; } catch { }
        }
        return total;
    }

    // ---------------- batch handoff / removal ----------------

    /// <summary>Launch the installed copy and hand it our foreground right. We start it DIRECTLY (not
    /// via a detached helper) while THIS process is still alive and frontmost, so we can call
    /// <c>AllowSetForegroundWindow</c> to grant the child permission to pull itself to the front once
    /// it's up — the Windows-sanctioned way. A process launched from a background helper never gets
    /// that right, which is why the window kept opening behind everything. The child is told our PID
    /// (<c>--await-pid</c>) so it waits for us to exit — releasing the single-instance lock — before it
    /// starts for real, avoiding the "already running" race.</summary>
    private static void StartRelaunchAfterExit(string installedExe, string installFolder)
    {
        var psi = new ProcessStartInfo
        {
            FileName = installedExe,
            WorkingDirectory = installFolder,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--foreground");
        psi.ArgumentList.Add("--await-pid");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());

        using var child = Process.Start(psi);
        // Grant the just-launched child the right to take the foreground. Given now, while we still
        // hold it, it survives our imminent exit and lets the child's SetForegroundWindow succeed.
        if (child is not null)
        {
            try { AllowSetForegroundWindow(child.Id); } catch { /* the child also self-nudges as a fallback */ }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    /// <summary>Spawn a hidden batch that waits for RemSound to close, then removes the install folder,
    /// retrying a few times in case a copy is slow to release its files, then deletes itself. The two
    /// flags decide independently whether the "user settings and logs" folder (profiles + config +
    /// logs) and the "recordings" folder go too; whichever aren't removed are kept in place.</summary>
    /// <summary>
    /// Is this folder one we are willing to point <c>rd /s /q</c> at?
    ///
    /// <para>The remover deletes whatever folder the install marker sits next to, and the marker is
    /// just a file — copy an installed RemSound somewhere else and it travels with it. Copy one to a
    /// drive root and "Uninstall RemSound" would generate <c>rd /s /q "D:\"</c>, which walks the whole
    /// drive. Nothing checked; found 2026-08-24 reading the uninstall path.</para>
    ///
    /// <para>Unlikely, and catastrophic, and free to prevent — which is the whole argument for a
    /// guard. It refuses a drive root and the handful of folders nobody could ever legitimately have
    /// installed INTO, and lets every real install through untouched.</para>
    /// </summary>
    internal static bool IsSafeToRemove(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;

        // DRIVE-RELATIVE PATHS, refused before anything resolves them. "C:" and "C:sub" name a drive
        // WITHOUT a separator, and Windows resolves those against that drive's own current directory —
        // so Path.GetFullPath("C:") returns wherever this process happens to be on C:, which is a real
        // nested folder and sails straight through the drive-root check below. The guard would then
        // approve deleting it.
        //
        // Found 2026-09-06 by the gate itself: AUDIT INST1 passes from a working directory on D: and
        // FAILS from one on C:, because that is the only difference. A test that passes because of
        // where it was run from is barely a test, and a safety guard that depends on the current
        // directory is not a guard. An install folder is always fully qualified, so the shape is
        // simply refused.
        var trimmed = folder.Trim();
        if (trimmed.Length >= 2 && trimmed[1] == ':'
            && (trimmed.Length == 2 || (trimmed[2] != Path.DirectorySeparatorChar && trimmed[2] != Path.AltDirectorySeparatorChar)))
        {
            return false;
        }

        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)); }
        catch { return false; }
        if (full.Length == 0) return false;

        // A drive root, or a UNC share root — never.
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root)) return false;
        if (string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase)) return false;

        // Folders that hold OTHER things. An install lives in its own folder inside one of these,
        // never as one of them.
        foreach (var special in new[]
                 {
                     Environment.SpecialFolder.Windows, Environment.SpecialFolder.System,
                     Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.UserProfile,
                     Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
                     Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments,
                     Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.Personal,
                 })
        {
            string path;
            try { path = Environment.GetFolderPath(special); } catch { continue; }
            if (string.IsNullOrEmpty(path)) continue;
            if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), full, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static void StartDeleteAfterExit(string installFolder, bool removeProfilesConfigLogs, bool removeRecordings)
    {
        // Refuse before writing a script that deletes a tree. Both callers already turn a throw here
        // into "RemSound couldn't start the uninstaller: <reason>", which is the right outcome: the
        // user is told plainly and nothing is removed.
        if (!IsSafeToRemove(installFolder))
        {
            throw new InvalidOperationException(
                $"\"{installFolder}\" is not a folder RemSound is willing to delete — it looks like a drive root or a "
                + "system folder rather than an install of its own. Nothing has been removed. If this really is a "
                + "RemSound install, move it into its own folder first and uninstall from there.");
        }

        var pid = Environment.ProcessId;

        // Folders to KEEP: everything else (all program files, and any data folder not being removed)
        // is deleted. When nothing is kept, the whole folder goes in one shot.
        var keep = new List<string>();
        if (!removeProfilesConfigLogs) keep.Add(AppConfig.UserDataFolderName);
        if (!removeRecordings) keep.Add("recordings");

        string deleteBody;
        if (keep.Count == 0)
        {
            deleteBody = $"rd /s /q \"{installFolder}\"\r\n";
        }
        else
        {
            // Delete every subfolder except the kept one(s), plus all files in the root. The kept
            // folders remain, so the install folder itself is left holding just the user's data.
            var guards = string.Concat(keep.Select(k => $"if /I not \"%%~nxD\"==\"{k}\" "));
            deleteBody =
                $"for /d %%D in (\"{installFolder}\\*\") do {guards}rd /s /q \"%%D\"\r\n" +
                $"del /f /q \"{installFolder}\\*\" >nul 2>&1\r\n";
        }

        // Retry sentinel: the exe file is the last thing to unlock, so loop until it (or the whole
        // folder) is gone. Written without parenthesised blocks so %tries% expands fresh each pass.
        var script =
            "@echo off\r\n" +
            ":wait\r\n" +
            $"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul\r\n" +
            "if %errorlevel%==0 ( ping -n 2 127.0.0.1 >nul & goto wait )\r\n" +
            "set tries=0\r\n" +
            ":del\r\n" +
            deleteBody +
            $"if not exist \"{installFolder}\\{ExeName}\" goto done\r\n" +
            "ping -n 2 127.0.0.1 >nul\r\n" +
            "set /a tries+=1\r\n" +
            "if %tries% lss 30 goto del\r\n" +
            ":done\r\n" +
            "del \"%~f0\"\r\n";
        RunHiddenBatch(script, "RemSound-uninstall-");
    }

    private static void RunHiddenBatch(string script, string namePrefix)
    {
        var path = Path.Combine(Path.GetTempPath(), namePrefix + Guid.NewGuid().ToString("N") + ".cmd");
        File.WriteAllText(path, script, new UTF8Encoding(false));
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{path}\"",
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = false,
        });
    }

    // ---------------- dialogs ----------------

    private sealed class InstallOptions
    {
        public bool CreateDesktopShortcut;
        public bool CreateStartMenuFolder;
        public bool RunAtStartup;
        public bool CopyProfilesAndConfig;
        public bool CopyRecordings;
        public bool CopyLogs;
    }

    internal sealed class UninstallOptions
    {
        public bool RemoveProfilesConfigLogs;
        public bool RemoveRecordings;
        public bool RemovePlugin;
        public bool RemoveService;
    }

    /// <summary>The install tick-box dialog. Tab order is: every check-box first, THEN Install, THEN
    /// Cancel — and there is deliberately no default button, so Enter never fires Install before the
    /// user has tabbed past the options. Escape cancels.</summary>
    private static InstallOptions? ShowInstallOptionsDialog(IWin32Window owner, string target, bool updating)
    {
        using var dialog = new Form
        {
            Text = updating ? "Update the RemSound install" : "Install RemSound on this PC",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ShowInTaskbar = false,
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(14),
        };

        var heading = Theme.Heading(dialog.Text);

        var intro = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Margin = new Padding(3, 3, 3, 10),
            Text = (updating
                    ? "RemSound is already installed. This will update the installed copy in:"
                    : "RemSound will be installed for you in:")
                   + Environment.NewLine + target + Environment.NewLine + Environment.NewLine
                   + "No administrator rights are needed. Choose what to set up, then select Install.",
        };

        var desktopBox = MakeCheck("Create a &desktop shortcut", "Create a desktop shortcut", true);
        var startMenuBox = MakeCheck("Add to the &Start menu (program, manual and uninstall)",
            "Add a Start menu folder containing the program, the manual and an uninstall shortcut", true);
        var startupBox = MakeCheck("&Run RemSound when I sign in to Windows",
            "Run RemSound automatically when you sign in to Windows", StartupAutoStart.IsEnabled);
        var profilesBox = MakeCheck("Copy my &profiles and settings across",
            "Copy your profiles and all your settings from this copy into the install folder", true);
        var recordingsBox = MakeCheck("Copy my re&cordings across",
            "Copy your recordings from this copy into the install folder", true);
        var logsBox = MakeCheck("Copy my &logs across",
            "Copy your diagnostic logs from this copy into the install folder", false);

        var installButton = new Button
        {
            Text = updating ? "&Update" : "&Install",
            AccessibleName = updating ? "Update" : "Install",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            Margin = new Padding(3, 12, 3, 3),
        };
        var cancelButton = new Button
        {
            Text = "&Cancel",
            AccessibleName = "Cancel",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            Margin = new Padding(3, 12, 3, 3),
        };

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };
        buttons.Controls.Add(installButton);
        buttons.Controls.Add(cancelButton);

        foreach (var c in new Control[]
                 { heading, intro, desktopBox, startMenuBox, startupBox, profilesBox, recordingsBox, logsBox, buttons })
        {
            layout.Controls.Add(c);
        }
        dialog.Controls.Add(layout);

        // Tab order: options top-to-bottom, then Install, then Cancel. No AcceptButton, so pressing
        // Enter on a check-box does nothing — the user must tab to Install and activate it.
        var order = new Control[]
            { desktopBox, startMenuBox, startupBox, profilesBox, recordingsBox, logsBox, installButton, cancelButton };
        for (var i = 0; i < order.Length; i++) order[i].TabIndex = i;
        dialog.CancelButton = cancelButton; // Escape cancels
        dialog.AcceptButton = null;

        if (dialog.ShowDialog(owner) != DialogResult.OK) return null;
        return new InstallOptions
        {
            CreateDesktopShortcut = desktopBox.Checked,
            CreateStartMenuFolder = startMenuBox.Checked,
            RunAtStartup = startupBox.Checked,
            CopyProfilesAndConfig = profilesBox.Checked,
            CopyRecordings = recordingsBox.Checked,
            CopyLogs = logsBox.Checked,
        };
    }

    // AccessibleCheckBox (not a plain CheckBox) — it re-fires the MSAA focus event on every toggle so
    // NVDA reliably announces "checked" / "not checked", including for a spacebar toggle while the box
    // already has focus, which is the state plain WinForms checkboxes leave silent on .NET 10.
    private static AccessibleCheckBox MakeCheck(string text, string accessibleName, bool @checked) => new()
    {
        Text = text,
        AccessibleName = accessibleName,
        Checked = @checked,
        AutoSize = true,
        Margin = new Padding(3, 2, 3, 2),
    };

    /// <summary>The uninstall confirmation. Two independent opt-ins — remove profiles/config/logs, and
    /// remove recordings — both unticked by default, so the safe outcome (keep my data) is the default.
    /// Below them, only when they are installed, the DAW plugin and the service: ticked, because they are
    /// parts of RemSound rather than the user's data, and neither is any use without it.
    /// OK to go ahead, Cancel to back out.</summary>
    private static UninstallOptions? ShowUninstallConfirmDialog(IWin32Window? owner, string target)
    {
        var (pluginInstalled, serviceInstalled) = InstalledComponents();
        using var dialog = BuildUninstallConfirmDialog(owner, target, pluginInstalled, serviceInstalled, out var readOptions);
        var result = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return result == DialogResult.OK ? readOptions() : null;
    }

    /// <summary>Which separately installed parts of RemSound are on this PC. Never throws.</summary>
    private static (bool Plugin, bool Service) InstalledComponents()
    {
        bool plugin = false, service = false;
        try { plugin = PluginInstaller.IsInstalled(); } catch { /* treat as not installed */ }
        try { service = ServiceIsInstalled(); } catch { /* the service types could not load: nothing to offer */ }
        return (plugin, service);
    }

    // A method of its own, so the service types load only when it runs (ServiceEntry explains why that matters).
    private static bool ServiceIsInstalled() => ServiceControl.IsInstalled();

    /// <summary>Builds the uninstall confirmation without showing it, so the gate can check what it offers.
    /// <paramref name="readOptions"/> reads the ticks once the user has answered.</summary>
    internal static Form BuildUninstallConfirmDialog(IWin32Window? owner, string target, bool pluginInstalled, bool serviceInstalled,
        out Func<UninstallOptions> readOptions)
    {
        var dialog = new Form
        {
            Text = "Uninstall RemSound from this PC",
            StartPosition = owner is null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ShowInTaskbar = false,
            TopMost = owner is null, // launched with no parent (Start menu) — make sure it's seen
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(14),
        };

        var heading = Theme.Heading("Uninstall RemSound from this PC");

        var intro = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Margin = new Padding(3, 3, 3, 10),
            Text = "This will remove the installed RemSound from this PC:" + Environment.NewLine + target +
                   Environment.NewLine + Environment.NewLine +
                   "RemSound will close when the uninstall starts.",
        };
        var removeDataBox = MakeCheck("Remove &profiles, config and logs", "Remove profiles, config and logs", false);
        // Alt+R. It was "re&cordings", which shared Alt+C with Cancel.
        var removeRecBox = MakeCheck("Remove &recordings", "Remove recordings", false);
        // The DAW plugin and the service were left behind without a word: the plugin with nothing to connect to, the
        // service still sending at the lock screen and following a folder that no longer exists, so it never updated
        // again. 2026-09-13 review.
        var removePluginBox = MakeCheck("Remove the &DAW plugin too", "Remove the DAW plugin", true);
        var removeServiceBox = MakeCheck("Remove the RemSound &service too (Windows will ask for administrator permission)",
            "Remove the RemSound service", true);

        var okButton = new Button
        {
            Text = "&OK", AccessibleName = "OK",
            DialogResult = DialogResult.OK, AutoSize = true, Margin = new Padding(3, 12, 3, 3),
        };
        var cancelButton = new Button
        {
            Text = "&Cancel", AccessibleName = "Cancel",
            DialogResult = DialogResult.Cancel, AutoSize = true, Margin = new Padding(3, 12, 3, 3),
        };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty,
        };
        buttons.Controls.Add(okButton);
        buttons.Controls.Add(cancelButton);

        var rows = new List<Control> { heading, intro, removeDataBox, removeRecBox };
        if (pluginInstalled) rows.Add(removePluginBox); else dialog.Disposed += (_, _) => removePluginBox.Dispose();
        if (serviceInstalled) rows.Add(removeServiceBox); else dialog.Disposed += (_, _) => removeServiceBox.Dispose();
        rows.Add(buttons);
        foreach (var c in rows) layout.Controls.Add(c);
        dialog.Controls.Add(layout);

        // Tab order: the check-boxes, then OK, then Cancel — set on the button row itself, since OK and Cancel sit
        // inside it. No default button, so Enter on a box doesn't fire OK — the user tabs to OK deliberately.
        // Escape cancels.
        removeDataBox.TabIndex = 0;
        removeRecBox.TabIndex = 1;
        removePluginBox.TabIndex = 2;
        removeServiceBox.TabIndex = 3;
        buttons.TabIndex = 4;
        okButton.TabIndex = 0;
        cancelButton.TabIndex = 1;
        dialog.CancelButton = cancelButton;
        dialog.AcceptButton = null;

        readOptions = () => new UninstallOptions
        {
            RemoveProfilesConfigLogs = removeDataBox.Checked,
            RemoveRecordings = removeRecBox.Checked,
            RemovePlugin = pluginInstalled && removePluginBox.Checked,
            RemoveService = serviceInstalled && removeServiceBox.Checked,
        };
        return dialog;
    }

    /// <summary>Run a best-effort integration step, logging (never throwing) on failure — so a blocked
    /// shortcut or registry write doesn't undo an otherwise-good install.</summary>
    private static void TryStep(Action<string>? log, string what, Action step)
    {
        try { step(); }
        catch (Exception ex) { log?.Invoke($"install: could not set up {what} — {ex.GetType().Name}: {ex.Message}"); }
    }

    private static string NormalizeFolder(string path) =>
        Path.GetFullPath(path ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Pure, testable: point an already-installed service at the installed copy when it follows the portable folder this
    /// install ran from, or no folder at all. A service following some other folder follows a copy somebody chose, and is
    /// left alone. <paramref name="save"/> writes the folder and reports whether the write took. Returns true only when
    /// this call moved the service to <paramref name="installedFolder"/>.
    /// </summary>
    internal static bool RepointServiceSource(string? recorded, string portableFolder, string installedFolder, Func<string, bool> save)
    {
        if (SameFolder(recorded, installedFolder)) return false;
        if (!string.IsNullOrWhiteSpace(recorded) && !SameFolder(recorded, portableFolder)) return false;
        return save(installedFolder);
    }

    internal static bool SameFolder(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try { return string.Equals(NormalizeFolder(a), NormalizeFolder(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}
