using System.Text.RegularExpressions;

namespace RemSound.App;

/// <summary>
/// The relay runs as its own user, with a private log. GitHub issue #30 (2026-09-15), and our own review of 2026-09-13: the
/// relay ran as root, and its log of client IP addresses was readable by everyone on the machine. It only ever needed root
/// to write that log. The auto-updater installs each release's service file but cannot create a user, so systemd makes one
/// for the relay (DynamicUser) and a private folder for its log: a relay that updates itself needs nothing else.
/// </summary>
internal static partial class SelfTest
{
    private const string RelayLogPath = "/var/log/remsound-relay/remsound-relay.log";

    /// <summary>
    /// THE RELAY RUNS AS ITS OWN USER, WITH A PRIVATE LOG.
    ///
    /// <para>Read from the bundle a relay installs and updates itself from. Every problem is listed, not only the first, so
    /// one look shows the whole state.</para>
    /// </summary>
    private static string? AuditRelayRunsAsItsOwnUserWithAPrivateLog()
    {
        var root = FindSourceRoot();
        if (root is null) return Skip("the source tree is not reachable (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        string Read(string name) => File.ReadAllText(Path.Combine(root, "server", name)).Replace("\r", "");
        var unit = Read("remsound-relay.service").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToArray();
        bool Has(string line) => unit.Contains(line);
        var problems = new List<string>();
        void Need(bool ok, string problem) { if (!ok) problems.Add(problem); }

        Need(Has("DynamicUser=yes") && !unit.Any(l => l.StartsWith("User=", StringComparison.Ordinal)),
            "the relay must run as a user systemd makes for it (DynamicUser=yes, no User= line): as root it has the whole machine, "
            + "and the auto-updater cannot create a named user");
        Need(Has("NoNewPrivileges=yes") && Has("CapabilityBoundingSet="),
            "the relay must have no special powers (NoNewPrivileges=yes, an empty CapabilityBoundingSet=)");
        Need(Has("LogsDirectory=remsound-relay") && Has("LogsDirectoryMode=0750") && Has("UMask=0027"),
            "the relay's log must be in its own folder and readable by no one else (LogsDirectory=remsound-relay, "
            + "LogsDirectoryMode=0750, UMask=0027): it holds client IP addresses");
        var start = unit.Where(l => l.StartsWith("ExecStart=", StringComparison.Ordinal)).ToArray();
        Need(start.Length == 1 && start[0].EndsWith(" --log-path " + RelayLogPath, StringComparison.Ordinal),
            $"the relay must be told to log to {RelayLogPath}, inside its own folder, or it cannot write its log at all");
        Need(Read("remsound-relay.py").Contains($"DEFAULT_LOG_PATH = \"{RelayLogPath}\"", StringComparison.Ordinal),
            "the relay's own default log path must be the same one");
        Need(Has("ExecStartPre=-+/bin/chmod 0600 /var/log/remsound-relay.log"),
            "a log left from before server-v2.7 holds client IP addresses and was readable by everyone: it must be made root-only when the relay starts");
        var install = Read("install.sh");
        Need(!Regex.IsMatch(install, @"touch[^\n]*/var/log/remsound-relay\.log") && !Regex.IsMatch(install, @"chmod 0644[^\n]*/var/log/remsound-relay\.log"),
            "the installer must not create the relay's log as root or make it readable by everyone");
        foreach (var name in new[] { "install.sh", "smoke-test.sh", "uninstall.sh", "README.md" })
            Need(Read(name).Contains(RelayLogPath, StringComparison.Ordinal), $"{name} must point at the relay's log where it now is, {RelayLogPath}");

        Check(problems.Count == 0, string.Join(" | ", problems));
        return $"the relay runs as its own user with no special powers, logs to {RelayLogPath} for root and itself only, "
            + "and a log from before is made root-only";
    }
}
