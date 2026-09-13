using System.Text.RegularExpressions;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// The 2026-09-13 review, stage 3: the gate itself — what it was allowed to touch, and what it could not see.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>
    /// A GATE RUN TOUCHES NO REAL SETTINGS AND OPENS NO REAL OUTPUT.
    ///
    /// <para>2026-09-13 review (Gate #1, #2). run-tests.ps1 starts the self-test with no --config-dir, so four steps wrote the
    /// real settings beside the exe and relied on putting them back — one wiped the remembered peers first. A wake step
    /// could go looking for the real router. And the lifecycle churn opened the first real output and played silence into
    /// it on every loop.</para>
    /// </summary>
    private static string? AuditGateRunTouchesNoRealSettingsOrOutputs()
    {
        var beside = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, AppConfig.UserDataFolderName)).TrimEnd('\\');
        var inUse = Path.GetFullPath(AppConfig.UserDataDirectory).TrimEnd('\\');
        Check(!string.Equals(inUse, beside, StringComparison.OrdinalIgnoreCase),
            $"a gate run must keep settings in a throwaway folder, never the real one beside the exe (it is using {inUse})");

        var root = FindSourceRoot();
        if (root is null) return Skip("this run's settings are throwaway, but the source tree is not reachable (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var selfTest = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "SelfTest.cs"));
        Check(SourceMethodBody(selfTest, "public static int Run(string[] args)").Contains("AppConfig.UseThrowawayUserDataDirectory(", StringComparison.Ordinal),
            "the self-test must make its own throwaway settings folder when it is started without --config-dir");
        var churn = SourceMethodBody(selfTest, "private static string? LifecycleChurn()");
        Check(churn.Contains("var recvSets = new List<string[]> { Array.Empty<string>() };", StringComparison.Ordinal)
              && !churn.Contains("recvSets.Add(", StringComparison.Ordinal),
            "the lifecycle churn must never add a real output to its receive sets — it opened one and played silence into it on every loop");
        var main = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "MainForm.cs"));
        Check(Regex.IsMatch(main, @"if \(!headless && AppConfig\.Load\(\)\.UpnpEnabled\)"),
            "a headless window must never refresh the real router's port mapping on wake");
        return "settings are throwaway for the whole run; the churn opens no output; a headless wake leaves the router alone";
    }

    /// <summary>
    /// THE ABOUT BOX CARRIES ONLY RECENT NOTES; THE HISTORY IS KEPT IN THE REPOSITORY.
    ///
    /// <para>2026-09-13 review (Dialogs #20). Every release note back to v1.0 — about 1,300 lines — was compiled into the exe,
    /// and only the newest five were ever shown. The older notes moved to RELEASE_HISTORY.md.</para>
    /// </summary>
    private static string? AuditAboutCarriesOnlyRecentNotes()
    {
        var blocks = AboutDialog.ReleaseNotesForTest.Split('\n').Count(l => l.TrimEnd('\r').StartsWith("RemSound v", StringComparison.Ordinal));
        Check(blocks <= 6,
            $"the About box's notes must carry only the newest releases (it carries {blocks}); older ones belong in RELEASE_HISTORY.md, "
            + "not compiled into every copy of the app");

        var root = FindSourceRoot();
        if (root is null) return Skip("the notes are short, but the source tree is not reachable (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var history = Path.Combine(root, "RELEASE_HISTORY.md");
        Check(File.Exists(history) && File.ReadAllText(history).Contains("RemSound v1.0", StringComparison.Ordinal),
            "the older release notes must be kept in the repository once they leave the exe");
        return $"the About box carries {blocks} releases; the history back to v1.0 is in RELEASE_HISTORY.md";
    }
}
