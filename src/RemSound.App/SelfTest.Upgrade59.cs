using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// People updating from 5.9 (Ed, 2026-09-24: "will the update work for them properly? particularly considering the
/// lockscreen service. I don't want it to break things."). A read-only investigation the same day found the update itself
/// sound, and these two ways the lock-screen service could be left behind.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>
    /// UPGRADE FROM 5.9: an installed service watching a folder no update reaches is pointed at the installed copy.
    /// </summary>
    private static string? UpgradeServiceFollowsTheInstalledCopy()
    {
        const string installed = @"C:\Users\x\AppData\Local\Programs\RemSound";
        const string portable = @"D:\Downloads\RemSound";
        string? Version(string folder) => folder switch
        {
            portable => "5.9.0.0",
            @"E:\Newer" => "6.1.0.0",
            @"E:\Same" => "6.0.0.0",
            _ => null,   // a folder that is gone
        };
        string? Adopt(string? recorded, bool installedCopy = true, bool serviceThere = true) =>
            ServiceSourceRepair.FolderToAdopt(recorded, installed, "6.0.0.0", installedCopy, serviceThere, Version);

        // The case that stranded people on 5.9: installed from the portable copy, which still holds 5.9.
        Check(Adopt(portable) == installed, "a service watching the portable copy that still holds 5.9 must be pointed at the installed copy");
        Check(Adopt(@"D:\Deleted\RemSound") == installed, "and one watching a folder that has since gone");
        Check(Adopt(null) == installed, "and one watching nothing at all, where a service is installed");
        // And the cases that must be left alone.
        Check(Adopt(installed) is null, "a service already following this copy is left alone");
        Check(Adopt(installed + @"\") is null, "however the same folder is written");
        Check(Adopt(@"E:\Same") is null, "a service following another copy of the same version follows a copy somebody chose");
        Check(Adopt(@"E:\Newer") is null, "and one following a NEWER copy is never pulled back to an older one");
        Check(Adopt(portable, installedCopy: false) is null, "only the installed copy takes a service over - a portable copy never does");
        Check(Adopt(portable, serviceThere: false) is null, "and only when a service is installed at all");

        // A run that touches nothing real never repoints the real service.
        var recordedBefore = ServiceStore.LoadAppSourcePath();
        var said = new List<string>();
        ServiceSourceRepair.RunAtStartup(said.Add);
        Check(said.Count == 0 && ServiceStore.LoadAppSourcePath() == recordedBefore,
            $"in a throwaway run the start-up repair must do nothing at all (it said: {string.Join(" | ", said)})");

        // The main window's start-up is where it runs. Named in source because only an INSTALLED copy with a service can
        // exercise it for real, and a gate run is neither.
        var root = FindSourceRoot();
        if (root is not null)
        {
            var main = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "MainForm.cs"));
            Check(main.Contains("ServiceSourceRepair.RunAtStartup(", StringComparison.Ordinal),
                "the main window's start-up must run the repair, or no one updating from 5.9 ever gets it");
        }
        return "a service watching the portable 5.9 copy, a folder that has gone, or nothing is pointed at the installed copy; one "
             + "following this copy, a same-version or a newer copy is left alone; a portable copy never takes over; a throwaway run "
             + "does nothing" + (root is null ? " (the start-up call was not checked: no source tree)" : "; the main window runs it at start-up");
    }

    /// <summary>
    /// SECURITY: nobody but SYSTEM and Administrators can move the service's folder - the folder above it is locked down too.
    /// The exact shipped icacls command, run on a scratch folder and read back.
    /// </summary>
    private static string? ServiceParentFolderIsLockedDown()
    {
        Check(ServiceControl.RealParentOfServiceDirectory() is null,
            "in a gate run the service store is throwaway, so the real ProgramData\\RemSound must never be the folder hardened");
        var dir = Path.Combine(AppConfig.UserDataDirectory, "parent-acl-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var rc = RunIcaclsForTest(ServiceControl.BuildParentDirAclArgs(dir));
            Check(rc == 0, $"the shipped icacls command must run cleanly (exit {rc})");
            var security = new DirectoryInfo(dir).GetAccessControl();
            Check(security.AreAccessRulesProtected, "the folder must not inherit ProgramData's permissions - they give the creator full control");
            var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
                .Cast<System.Security.AccessControl.FileSystemAccessRule>().ToList();
            System.Security.AccessControl.FileSystemRights RightsOf(string sid) => rules
                .Where(r => r.IdentityReference.Value == sid && r.AccessControlType == System.Security.AccessControl.AccessControlType.Allow)
                .Aggregate((System.Security.AccessControl.FileSystemRights)0, (all, r) => all | r.FileSystemRights);
            const System.Security.AccessControl.FileSystemRights Dangerous =
                System.Security.AccessControl.FileSystemRights.DeleteSubdirectoriesAndFiles | System.Security.AccessControl.FileSystemRights.Delete
                | System.Security.AccessControl.FileSystemRights.ChangePermissions | System.Security.AccessControl.FileSystemRights.TakeOwnership
                | System.Security.AccessControl.FileSystemRights.CreateDirectories;
            var users = RightsOf("S-1-5-32-545");
            Check((users & Dangerous) == 0,
                $"ordinary accounts must not be able to delete, rename, add a folder or change permissions here - that is how the service folder gets swapped (they have {users})");
            Check(users.HasFlag(System.Security.AccessControl.FileSystemRights.ReadAndExecute), "every account must still read it (the server id is here)");
            Check(users.HasFlag(System.Security.AccessControl.FileSystemRights.CreateFiles), "and add a file - the server id is written by whichever RemSound starts first");
            Check(RightsOf("S-1-5-18").HasFlag(System.Security.AccessControl.FileSystemRights.FullControl)
                  && RightsOf("S-1-5-32-544").HasFlag(System.Security.AccessControl.FileSystemRights.FullControl), "SYSTEM and Administrators keep full control");
            Check(rules.Any(r => r.IdentityReference.Value == "S-1-3-0" && r.PropagationFlags.HasFlag(System.Security.AccessControl.PropagationFlags.InheritOnly)
                                 && r.FileSystemRights.HasFlag(System.Security.AccessControl.FileSystemRights.Modify)),
                "whoever adds a file must be able to change that file (CREATOR OWNER, on files only)");
            // And the server id still works under it: a file added now can be written and read back.
            var id = Path.Combine(dir, AppConfig.MachineIdentityFileName);
            File.WriteAllText(id, "gate-id-1");
            File.WriteAllText(id, "gate-id-2");
            Check(File.ReadAllText(id) == "gate-id-2", "a server id file created under the locked-down folder must still be writable by whoever created it");

            var root = FindSourceRoot();
            if (root is not null)
            {
                var body = SourceMethodBody(File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "ServiceControl.cs")), "internal static void HardenServiceDirectory()");
                Check(body.Contains("BuildParentDirAclArgs(parent)", StringComparison.Ordinal) && body.Contains("takeown.exe", StringComparison.Ordinal)
                      && body.IndexOf("BuildParentDirAclArgs(parent)", StringComparison.Ordinal) > body.IndexOf("ApplyServiceDirAcl(", StringComparison.Ordinal),
                    "the hardening that runs at install and on every service update must lock the parent down too, after the service folder");
            }
            return "the RemSound folder above the service's is cut off from ProgramData's permissions: ordinary accounts can read it and add "
                 + "a file but never delete, rename, add a folder or change permissions; a server id added there still works; a gate run "
                 + "never touches the real one" + (root is null ? "" : "; install and every service update apply it");
        }
        finally
        {
            RunIcaclsForTest($"\"{dir}\" /reset /T /C");
            try { Directory.Delete(dir, recursive: true); } catch { /* throwaway */ }
        }
    }

    /// <summary>
    /// UPGRADE FROM 5.9: the service's update waits for the app's own update to finish landing, and proves its copy.
    /// </summary>
    private static string? UpgradeServiceUpdateWaitsAndChecksItsCopy()
    {
        var root = Path.Combine(AppConfig.UserDataDirectory, "upgrade-" + Guid.NewGuid().ToString("N")[..8]);
        var app = Path.Combine(root, "app");
        var bin = Path.Combine(root, "bin");
        try
        {
            Directory.CreateDirectory(app);
            File.WriteAllText(Path.Combine(app, "RemSound.exe"), "exe v6");
            File.WriteAllText(Path.Combine(app, "RemSound.dll"), "dll v6");
            File.WriteAllText(Path.Combine(app, "RemSound.Ui.dll"), "new in 6.0");
            Directory.CreateDirectory(Path.Combine(app, "plugin"));
            File.WriteAllText(Path.Combine(app, "plugin", "RemSound.Plugin.dll"), "plugin v6");
            Directory.CreateDirectory(Path.Combine(app, "user settings and logs"));
            File.WriteAllText(Path.Combine(app, "user settings and logs", "global config.json"), "{}");

            // 1. While the app's own update is still landing, it waits - and gives up at its limit rather than hang.
            var backup = Path.Combine(app, UpdateApplier.BackupFolderName);
            Directory.CreateDirectory(backup);
            File.WriteAllText(Path.Combine(backup, "RemSound.exe"), "exe v5.9");
            var waited = System.Diagnostics.Stopwatch.StartNew();
            Check(!ServiceControl.WaitForSettledSource(app, TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(50), _ => "6.0.0.0"),
                "while the app's update is still landing (its _update-backup is there) the service must NOT take the files");
            Check(waited.ElapsedMilliseconds < 5000, $"and it must give up at its limit, not hang ({waited.ElapsedMilliseconds} ms)");

            // 2. A version that is still changing is not settled either.
            var tick = 0;
            Check(!ServiceControl.WaitForSettledSource(app + "-x", TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(50), _ => $"6.0.0.{tick++}"),
                "a RemSound.exe whose version is still changing is not a finished update");

            // 3. Once the swap is done and the version holds, it goes ahead.
            Directory.Delete(backup, recursive: true);
            Check(ServiceControl.WaitForSettledSource(app, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(50), _ => "6.0.0.0"),
                "once the swap has finished and the version holds, the service must take the update");

            // 4. The copy is proved, file for file - and never takes the user's state or the updater's half-way copy.
            Directory.CreateDirectory(backup);
            File.WriteAllText(Path.Combine(backup, "stale.dll"), "old");
            ServiceControl.CopyProgramTo(app, bin);
            Check(ServiceControl.ProgramCopyMismatches(app, bin).Count == 0, "a fresh copy must match the app's program files exactly");
            Check(!Directory.Exists(Path.Combine(bin, UpdateApplier.BackupFolderName)), "the updater's half-way copy must never be taken into the service");
            Check(!Directory.Exists(Path.Combine(bin, "user settings and logs")), "nor the user's settings");
            File.WriteAllText(Path.Combine(bin, "RemSound.dll"), "dll v5.9");            // the mix the investigation feared
            File.Delete(Path.Combine(bin, "RemSound.Ui.dll"));                           // a new 6.0 file that never arrived
            var differ = ServiceControl.ProgramCopyMismatches(app, bin);
            Check(differ.Contains("RemSound.dll") && differ.Contains("RemSound.Ui.dll") && differ.Count == 2,
                $"an old file and a missing new one must both be caught, and nothing else (got: {string.Join(", ", differ)})");

            // 5. The update step itself does both: the wait before the stop, and the proof after the copy.
            var sourceRoot = FindSourceRoot();
            if (sourceRoot is not null)
            {
                var control = File.ReadAllText(Path.Combine(sourceRoot, "src", "RemSound.App", "ServiceControl.cs"));
                var body = SourceMethodBody(control, "public static int DoSelfUpdate()");
                var wait = body.IndexOf("WaitForSettledSource(", StringComparison.Ordinal);
                var stop = body.IndexOf("sc.Stop();", StringComparison.Ordinal);
                Check(wait >= 0 && stop > wait, "the service's update must wait for the app's update BEFORE it stops the service");
                var copy = body.IndexOf("CopyAndCheck(", StringComparison.Ordinal);
                var proof = SourceMethodBody(control, "internal static bool CopyAndCheck(");
                Check(copy > stop && proof.Contains("ProgramCopyMismatches(", StringComparison.Ordinal), "and must prove its copy after it");
            }
            return "the service's update waits while the app's own update is landing or its version is still changing, goes ahead once "
                 + "it holds, never takes the half-way copy or the user's settings, and catches an old or missing file after the copy"
                 + (sourceRoot is null ? "" : "; its update step waits before the stop and proves the copy after");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* throwaway */ } }
    }
}
