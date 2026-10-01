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
        // The installed service's settings too (2026-09-24: one step wrote ProgramData\RemSound\service three times).
        var programData = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)).TrimEnd('\\');
        var serviceStore = Path.GetFullPath(ServiceStore.Directory).TrimEnd('\\');
        Check(!serviceStore.StartsWith(programData + "\\", StringComparison.OrdinalIgnoreCase),
            $"a gate run must keep the service's settings in a throwaway folder, never the installed service's own ({serviceStore})");

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

}
