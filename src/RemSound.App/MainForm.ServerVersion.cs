using System.Net;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// OUT-OF-DATE SERVERS (Ed, 2026-10-01: "people on out of date servers worries me, so we need a check when RemSound connects
/// to a server, it should check the server version and compare it to the latest available on github. if there is a version
/// mismatch or if a server doesn't say its version, you get a warning that always shows, i.e. you can't switch it off with a
/// checkbox"). A server says its version in its status (server-v2.16 on); one that says nothing in time is older than that.
/// The newest is the highest plain server-vX.Y release on GitHub, asked for as you connect and kept for an hour - or, with
/// GitHub out of reach, the server this build was made with, so an older server is still caught offline. A server newer than
/// both, a test build ahead of its release, is not out of date. The parsing and the rule are here; the asking and the
/// warning are MainForm's, below.
/// </summary>
internal static class ServerVersionCheck
{
    /// <summary>The server this build was made with (server/VERSION; the self-test holds the two together).</summary>
    internal const string BuiltWith = "server-v2.16";

    /// <summary>Ed's words.</summary>
    internal const string WarningText = "Warning: you are connecting to a RemSound server that is out of date. Please contact the "
        + "server administrator to request an update to the latest version for features and security enhancements.";

    /// <summary>Where the warning's GitHub button goes: the newest server release's own page, with its code and notes, or the
    /// release list when GitHub could not be asked which that is.</summary>
    internal static string LatestCodeUrl(string? latestReleased) =>
        IsPlainReleaseTag(latestReleased)
            ? $"https://github.com/{RemSoundUpdater.RepoOwner}/{RemSoundUpdater.RepoName}/releases/tag/{latestReleased}"
            : $"https://github.com/{RemSoundUpdater.RepoOwner}/{RemSoundUpdater.RepoName}/releases";

    /// <summary>A gate run: GitHub is never asked unless a step points the release list at a stand-in of its own, and the
    /// warning is never shown unless a step answers it.</summary>
    internal static bool ForGateRun { get; set; }

    private static readonly object gate = new();
    private static Task<string?>? asking;
    private static (string Tag, DateTime AtUtc)? known;

    /// <summary>"server-v2.16" is 2.16.0, "server-v2.13.3-metronome-test" 2.13.3; anything else null.</summary>
    internal static Version? Parse(string? tag)
    {
        const string prefix = "server-v";
        if (tag is null || !tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = tag[prefix.Length..];
        var end = 0;
        while (end < rest.Length && (char.IsDigit(rest[end]) || rest[end] == '.')) end++;
        var parts = rest[..end].Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 3) return null;
        var n = new int[3];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out n[i])) return null;
        return new Version(n[0], n[1], n[2]);
    }

    /// <summary>A released server's tag: server-v and numbers only, no test-build words after them.</summary>
    internal static bool IsPlainReleaseTag(string? tag) =>
        Parse(tag) is not null && tag!.Skip("server-v".Length).All(c => char.IsDigit(c) || c == '.');

    /// <summary>The newest server known: the newest release, or this build's own when that is newer or GitHub can't say.</summary>
    internal static string Newest(string? latestReleased) =>
        Parse(latestReleased) is { } released && released > Parse(BuiltWith)! ? latestReleased! : BuiltWith;

    /// <summary>Out of date: says no version, or one older than <see cref="Newest"/>.</summary>
    internal static bool IsOutOfDate(string? serverVersion, string? latestReleased) =>
        Parse(serverVersion) is not { } server || server < Parse(Newest(latestReleased))!;

    /// <summary>The newest server release, asked of GitHub once an hour at most; null when it can't be had. A failure is not
    /// kept, so the next server connected to asks again.</summary>
    internal static Task<string?> LatestReleasedAsync(RemSoundUpdater updater)
    {
        lock (gate)
        {
            if (known is { } k && DateTime.UtcNow - k.AtUtc < TimeSpan.FromHours(1)) return Task.FromResult<string?>(k.Tag);
            if (ForGateRun && RemSoundUpdater.ReleasesUrlForTest is null) return Task.FromResult<string?>(null);
            if (asking is { IsCompleted: false } running) return running;
            return asking = Ask();
        }

        async Task<string?> Ask()
        {
            var tag = await updater.LatestServerReleaseAsync().ConfigureAwait(false);
            if (tag is not null) lock (gate) known = (tag, DateTime.UtcNow);
            return tag;
        }
    }

    internal static void ForgetForTest() { lock (gate) { known = null; asking = null; } }
}

public sealed partial class MainForm
{
    /// <summary>How long after connecting the newest release is waited for before this build's own is used instead.</summary>
    internal static readonly TimeSpan ServerVersionGitHubWaitsUpTo = TimeSpan.FromSeconds(10);

    /// <summary>A server answering our pings is server-v2.16 or newer and says its version every 5 seconds: a lost status
    /// packet is waited out this long before it is taken for one that says nothing.</summary>
    internal static readonly TimeSpan ServerVersionPingingWaitsUpTo = TimeSpan.FromSeconds(20);

    private IPEndPoint? serverVersionPendingFor;
    private DateTime serverVersionStartedUtc;
    private Task<string?>? serverVersionLatest;

    /// <summary>As we connect to a server, however it was connected to: the newest release is asked for now, and the server
    /// judged once it says its version or doesn't in time.</summary>
    private void StartServerVersionCheck(IPEndPoint relay)
    {
        serverVersionPendingFor = relay;
        serverVersionStartedUtc = DateTime.UtcNow;
        serverVersionLatest = ServerVersionCheck.LatestReleasedAsync(updater);
    }

    /// <summary>Once a second while the server we joined is still to be judged.</summary>
    private void TickServerVersionCheck()
    {
        if (serverVersionPendingFor is not { } relay) return;
        if (!relay.Equals(relayGroup.ConnectedRelay)) { serverVersionPendingFor = null; return; }
        var info = relayGroup.LatestServerInfo;
        if (info is null)
        {
            // Not said yet: judged only once the server has us in its group and has said nothing for as long as the status
            // takes an older server to be (and longer while it answers our pings).
            if (relayGroup.InGroupSinceUtc is not { } since) return;
            var waited = DateTime.UtcNow - since;
            if (waited < ServerStatusOlderAfter) return;
            if (relayGroup.ServerRoundTripMs is not null && waited < ServerVersionPingingWaitsUpTo) return;
        }
        if (serverVersionLatest is { IsCompleted: false } && DateTime.UtcNow - serverVersionStartedUtc < ServerVersionGitHubWaitsUpTo) return;
        serverVersionPendingFor = null;
        var latest = serverVersionLatest is { IsCompletedSuccessfully: true } done ? done.Result : null;
        var entry = relayEntryText.Length > 0 ? relayEntryText : relay.Address.ToString();
        if (!JudgeServerVersion(entry, info?.Version, latest, beforeConnecting: false) && relay.Equals(relayGroup.ConnectedRelay))
            DisconnectFromRelay(userAsked: false);
    }

    /// <summary>How long after the server check the newest release is still waited for, when GitHub has not answered yet;
    /// then this build's own server is used.</summary>
    internal static readonly TimeSpan ServerVersionGitHubWaitsAfterCheck = TimeSpan.FromMilliseconds(1500);

    /// <summary>Counts connections begun, so a check overtaken by another server chosen, or by leaving, does nothing.</summary>
    private int serverCheckRun;

    /// <summary>The server the check before connecting found older than server-v2.16 (it answered, but never said its
    /// version): its status says so at once, instead of "measuring" until it has said nothing for long enough.</summary>
    private IPEndPoint? serverKnownOlder;

    /// <summary>BEFORE connecting (Ed, 2026-10-02: "it should give you the warning instantly and not connect"): the server is
    /// asked what it is (ServerVersionProbe: a throwaway identity nobody on the server is shown, about a second at most),
    /// and judged. Up to date, or Connect anyway: connected as ever, sound and all. Cancel: never connected - no sound, nobody
    /// told. A server that does not answer the check is connected to as before and judged once it says its version.</summary>
    private async void CheckServerThenConnect(string entry, IPEndPoint resolved, bool userAsked)
    {
        var run = ++serverCheckRun;
        relayConnectButton.Enabled = false;
        SetRelayStatus($"Checking {entry}...");
        logFile.Event($"server version: checking {entry} ({resolved}) before connecting [{ActiveAudioConfiguration().Describe()}]");
        var latest = ServerVersionCheck.LatestReleasedAsync(updater);
        ServerVersionProbe.Result probe;
        try { probe = await (ServerProbeForTest?.Invoke(resolved) ?? ServerVersionProbe.ProbeAsync(resolved)); }
        catch (Exception ex)
        {
            logFile.Event($"server version: the check of {entry} failed: {ex.GetType().Name}: {ex.Message}");
            probe = new ServerVersionProbe.Result(false, null, null, false);
        }
        if (!latest.IsCompleted) await Task.WhenAny(latest, Task.Delay(ServerVersionGitHubWaitsAfterCheck));
        if (run != serverCheckRun || IsDisposed) return;   // another server chosen meanwhile, or left
        relayConnectButton.Enabled = true;
        if (!probe.Answered || probe.Full)
        {
            logFile.Event($"server version: {entry} {(probe.Full ? "is full" : "did not answer the check")} - connecting as usual; its version is judged once it says it");
            ConnectToRelayNow(entry, resolved, userAsked, checkedFirst: false);
            return;
        }
        var latestTag = latest.IsCompletedSuccessfully ? latest.Result : null;
        serverKnownOlder = probe.Version is null ? resolved : null;
        if (JudgeServerVersion(entry, probe.Version, latestTag, beforeConnecting: true))
        {
            ConnectToRelayNow(entry, resolved, userAsked, checkedFirst: true);
            return;
        }
        SetRelayStatus($"Not connected: {entry} is out of date.");
        UpdateRelayControls();
    }

    /// <summary>Logged either way; out of date, Ed's warning, every time - there is no tick box to hide it. True to connect
    /// (or stay): up to date, Connect anyway, or nobody there to answer. False for Cancel: before connecting, not connected;
    /// after, left. The profile keeps the server either way, so it is asked again next time.</summary>
    private bool JudgeServerVersion(string entry, string? version, string? latest, bool beforeConnecting)
    {
        var outOfDate = ServerVersionCheck.IsOutOfDate(version, latest);
        var says = string.IsNullOrWhiteSpace(version) ? "no version (it's older than server-v2.16)" : version;
        var newest = latest is null ? $"{ServerVersionCheck.Newest(null)} (this build's own: GitHub was not reached)" : $"{ServerVersionCheck.Newest(latest)} (GitHub's newest is {latest})";
        logFile.Event($"server version: {entry} says {says}; the newest is {newest} - {(outOfDate ? "OUT OF DATE" : "up to date")} [{ActiveAudioConfiguration().Describe()}]");
        if (!outOfDate) return true;
        bool connectAnyway;
        if (OutOfDateServerAnswerForTest is { } answer) connectAnyway = answer(BuildOutOfDateServerWarning(out _, latest));
        else if (NobodyToAnswer || ServerVersionCheck.ForGateRun)
        {
            logFile.Event($"server version: the out-of-date warning was not shown - nobody to answer it; {(beforeConnecting ? "connecting" : "staying on the server")}");
            return true;
        }
        else
        {
            var page = BuildOutOfDateServerWarning(out var connectAnywayButton, latest);
            connectAnyway = ForegroundDialog.Show(owner => AppTaskDialog.ShowDialog(owner, page)) == connectAnywayButton;
        }
        logFile.Event(connectAnyway
            ? $"server version: Connect anyway - {(beforeConnecting ? "connecting to" : "staying on")} the out-of-date server"
            : $"server version: Cancel - {(beforeConnecting ? "not connecting to" : "leaving")} the out-of-date server");
        return connectAnyway;
    }

    /// <summary>Ed's warning, not shown: his words; Connect anyway, Visit GitHub for the latest RemSound server code (Ed,
    /// 2026-10-02: "can we put another button"), or Cancel; Cancel for Enter; no tick box. The GitHub button opens the newest
    /// server release in the browser and leaves the warning up, so Connect anyway or Cancel is still to choose.</summary>
    internal static TaskDialogPage BuildOutOfDateServerWarning(out TaskDialogButton connectAnyway, string? latestReleased = null)
    {
        connectAnyway = new TaskDialogButton("&Connect anyway");
        var visitGitHub = new TaskDialogButton("&Visit GitHub for the latest RemSound server code") { AllowCloseDialog = false };
        var url = ServerVersionCheck.LatestCodeUrl(latestReleased);
        visitGitHub.Click += (_, _) => HelpLauncher.OpenWebPage(url, "the latest RemSound server code on GitHub, from the out-of-date server warning");
        return new TaskDialogPage
        {
            Caption = AppName,
            Text = ServerVersionCheck.WarningText,
            Icon = TaskDialogIcon.Warning,
            Buttons = { connectAnyway, visitGitHub, TaskDialogButton.Cancel },
            DefaultButton = TaskDialogButton.Cancel,
            AllowCancel = true,
        };
    }

    /// <summary>Test seams: the warning answered (true for Connect anyway) instead of shown, and the check run now. And the
    /// check before connecting: made in a gate run too, and answered by the test instead of a server.</summary>
    internal Func<TaskDialogPage, bool>? OutOfDateServerAnswerForTest;
    internal bool CheckServerFirstForTest;
    internal bool ServerKnownOlderForTest(IPEndPoint server) => server.Equals(serverKnownOlder);
    internal Func<IPEndPoint, Task<ServerVersionProbe.Result>>? ServerProbeForTest;
    internal void TickServerVersionCheckForTest() => TickServerVersionCheck();
    internal bool ServerVersionPendingForTest => serverVersionPendingFor is not null;
    internal void BackdateServerVersionCheckForTest(TimeSpan by) => serverVersionStartedUtc -= by;
}
