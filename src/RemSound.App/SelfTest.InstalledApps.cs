namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>INSTALLED APPS ENTRY (Ed, 2026-09-28): an update swaps the files but never re-registers, so Windows' installed
    /// apps list kept the first version installed, and "RemSound" as publisher, for good. Now every start of an installed
    /// copy puts the publisher and version right, writes the whole entry again if it has gone, and changes nothing - and
    /// logs nothing - when it is already right. Decided without touching this computer's registry; the start-up call is
    /// checked in the source.</summary>
    private static string? InstalledAppsEntryKeptUpToDate()
    {
        const string folder = @"C:\Users\someone\AppData\Local\Programs\RemSound";
        const string exe = folder + @"\RemSound.exe";
        var full = AppInstaller.InstalledAppValues(folder, exe, "6.1.0");
        Check(full["Publisher"] as string == AuthorName && full["DisplayVersion"] as string == "6.1.0"
              && full["UninstallString"] as string == $"\"{exe}\" --uninstall" && full["InstallLocation"] as string == folder,
            "an install writes the publisher, the version and how to uninstall");

        var stale = AppInstaller.InstalledAppEntryChanges(new Dictionary<string, object?> { ["Publisher"] = "RemSound", ["DisplayVersion"] = "6.0.0" }, folder, exe, "6.1.0");
        Check(stale is { Count: 2 } && stale["Publisher"] as string == AuthorName && stale["DisplayVersion"] as string == "6.1.0",
            $"an entry left by an older install must get the new publisher and version, and nothing else ({Describe(stale)})");
        var oldVersion = AppInstaller.InstalledAppEntryChanges(new Dictionary<string, object?> { ["Publisher"] = AuthorName, ["DisplayVersion"] = "6.0.0" }, folder, exe, "6.1.0");
        Check(oldVersion is { Count: 1 } && oldVersion.ContainsKey("DisplayVersion"), $"only the version, when only the version is old ({Describe(oldVersion)})");
        var right = AppInstaller.InstalledAppEntryChanges(new Dictionary<string, object?> { ["Publisher"] = AuthorName, ["DisplayVersion"] = "6.1.0" }, folder, exe, "6.1.0");
        Check(right is null, $"an entry already right must be left alone ({Describe(right)})");
        var gone = AppInstaller.InstalledAppEntryChanges(null, folder, exe, "6.1.0");
        Check(gone is not null && gone.Count == full.Count && gone.Keys.All(full.ContainsKey), "an entry that has gone must be written whole again");
        Check(AppInstaller.RefreshedEntryValues.SequenceEqual(["Publisher", "DisplayVersion"]), "a start looks only at the publisher and the version");

        var root = FindSourceRoot();
        if (root is null) return Skip("the entry is decided right, but the source tree is not reachable to check the start-up call (set REMSOUND_SOURCE_ROOT)");
        var main = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "MainForm.cs"));
        var shown = main.IndexOf("ServiceSourceRepair.RunAtStartup", StringComparison.Ordinal);
        var call = main.IndexOf("AppInstaller.RefreshInstalledAppEntryAtStart(", StringComparison.Ordinal);
        Check(shown > 0 && call > shown && call - shown < 400, "every start of the window must bring the entry up to date, beside the service's own start-up repair");
        var installer = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "AppInstaller.cs"));
        Check(installer.Contains("if (!IsInstalledCopy) return;", StringComparison.Ordinal), "and a portable copy must leave Windows alone");
        return "an older entry gets the new publisher and version, a missing one is written whole, a right one is left alone; every start does it";

        static string Describe(Dictionary<string, object>? d) => d is null ? "nothing" : string.Join(", ", d.Select(kv => $"{kv.Key}={kv.Value}"));
    }
}
