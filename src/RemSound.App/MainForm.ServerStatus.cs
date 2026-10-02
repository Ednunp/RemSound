using System.Net;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// THE SERVER'S STATUS (Ed, 2026-10-01: "we need a status box about the server. once connected, it should show things like
/// how many people connected and in how many groups ... how long you've been connected maybe? also, can we show your round
/// trip latency to the server"). Shown in the Server window ("put it in the server window. we don't need yet more stuff
/// cluttering up the connectivity window"), in Ed's order: which server and for how long, then the round trip and how
/// steadily your sound reaches it ("very near the top"), then your group, the whole server, its version, and a phone or
/// older app paired with you. The round trip, the server's size and version and your sound's steadiness come from
/// server-v2.16 (RelayGroupClient.ServerInfo and the server ping); an older server is said to be one. The wobbling figures
/// are held still until they move by enough to matter, so a screen reader is not told a new number every second.
/// PLAN-server-status-2026-10-01.md has the decisions.
/// </summary>
public sealed partial class MainForm
{
    /// <summary>How long in a group with no status and no ping back before the server is taken for one older than
    /// server-v2.16.</summary>
    internal static readonly TimeSpan ServerStatusOlderAfter = TimeSpan.FromSeconds(8);

    /// <summary>Everything the status says, gathered: the text is made from this alone, so it can be checked without a
    /// server.</summary>
    /// <param name="Sending">Our sound goes to this server: someone here ticked, or hearing ourselves.</param>
    /// <param name="SendOn">Send my audio is on (or a DAW track sends). On, but nobody here to send to, is not "not sending"
    /// (Ed, 2026-10-02: "it says you're not sending to the server but i'm fairly sure I was sending").</param>
    internal sealed record ServerStatusFacts(string Entry, IPEndPoint Address, TimeSpan ConnectedFor, double? RoundTripMs,
        RelayGroupClient.ServerInfo? Info, bool OlderServer, bool Sending, IReadOnlyList<(string Name, bool TicksUs)> Group,
        bool PhonePaired, bool SendOn = true);

    private double? shownServerRoundTripMs;
    private double? shownServerGapMs;
    private int shownServerLongGaps;
    private DateTime lastServerStatusLogUtc = DateTime.MinValue;

    /// <summary>The status, one line each, in Ed's order.</summary>
    internal static string ServerStatusText(ServerStatusFacts f)
    {
        var lines = new List<string>();
        var where = string.Equals(f.Entry, f.Address.Address.ToString(), StringComparison.OrdinalIgnoreCase) ? f.Entry : $"{f.Entry} ({f.Address.Address})";
        lines.Add($"On {where}, connected for {Duration(f.ConnectedFor)}.");
        if (!f.OlderServer)
        {
            lines.Add(f.RoundTripMs is { } rtt ? $"Your round trip to the server: {rtt:0} ms." : "Your round trip to the server: measuring.");
            if (!f.SendOn) lines.Add("Send my audio is off, so none of your sound goes to the server.");
            else if (!f.Sending) lines.Add("Send my audio is on: your sound goes to the server once you tick somebody there, or unmute yourself to test round trip.");
            else if (f.Info is { } info)
            {
                if (info.YourPackets == 0) lines.Add("None of your sound has reached the server in the last minute.");
                else if (info.YourLongGaps == 0) lines.Add($"Your sound reaches the server steadily: longest gap {info.YourLongestGapMs:0} ms in the last minute.");
                else lines.Add($"Your sound reaches the server unevenly: longest gap {info.YourLongestGapMs:0} ms, and {info.YourLongGaps} "
                             + $"{(info.YourLongGaps == 1 ? "gap" : "gaps")} over 20 ms in the last minute.");
            }
            else lines.Add("How steadily your sound reaches the server: measuring.");
        }
        lines.Add(GroupLine(f.Group));
        if (f.Info is { } server)
        {
            lines.Add($"On the server: {server.People} {(server.People == 1 ? "person" : "people")} in {server.Groups} "
                    + $"{(server.Groups == 1 ? "group" : "groups")}, {server.PlacesFree} {(server.PlacesFree == 1 ? "place" : "places")} free.");
            lines.Add($"Server version {server.Version}.");
        }
        if (f.PhonePaired) lines.Add("A phone or older app is paired with you here.");
        if (f.OlderServer) lines.Add("This server doesn't report its size, version or your round trip (it's older than server-v2.16).");
        return string.Join(Environment.NewLine, lines);
    }

    private static string GroupLine(IReadOnlyList<(string Name, bool TicksUs)> group)
    {
        if (group.Count == 0) return "Nobody else from your group is on the server yet.";
        var names = JoinAnd(group.Select(g => g.Name).ToList());
        if (group.Count == 1)
            return group[0].TicksUs ? $"In your group: {names}, who has ticked you." : $"In your group: {names}, who hasn't ticked you yet.";
        var ticked = group.Where(g => g.TicksUs).Select(g => g.Name).ToList();
        var waiting = group.Where(g => !g.TicksUs).Select(g => g.Name).ToList();
        if (waiting.Count == 0) return $"In your group: {names}. All of them have ticked you.";
        if (ticked.Count == 0) return $"In your group: {names}. None of them has ticked you yet.";
        return $"In your group: {names}. {JoinAnd(ticked)} {(ticked.Count == 1 ? "has" : "have")} ticked you; "
             + $"{JoinAnd(waiting)} {(waiting.Count == 1 ? "hasn't" : "haven't")} yet.";
    }

    private static string JoinAnd(IReadOnlyList<string> names) =>
        names.Count <= 1 ? string.Join("", names) : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];

    /// <summary>"less than a minute", "12 minutes", "1 hour 5 minutes", "2 days 3 hours".</summary>
    internal static string Duration(TimeSpan t)
    {
        static string Unit(int n, string one) => n == 1 ? $"1 {one}" : $"{n} {one}s";
        if (t < TimeSpan.FromMinutes(1)) return "less than a minute";
        if (t.TotalHours < 1) return Unit((int)t.TotalMinutes, "minute");
        if (t.TotalDays < 1) return t.Minutes == 0 ? Unit((int)t.TotalHours, "hour") : $"{Unit((int)t.TotalHours, "hour")} {Unit(t.Minutes, "minute")}";
        return t.Hours == 0 ? Unit((int)t.TotalDays, "day") : $"{Unit((int)t.TotalDays, "day")} {Unit(t.Hours, "hour")}";
    }

    /// <summary>The status for the Server window now, or null when we are on no server. The round trip is shown afresh only
    /// when it moves by 2 ms or a tenth, the longest gap by 3 ms or when it starts or stops being uneven.</summary>
    private string? ServerStatusForWindow()
    {
        if (relayGroup.ConnectedRelay is not { } relay) return null;
        var info = relayGroup.LatestServerInfo;
        var rtt = relayGroup.ServerRoundTripMs;
        if (rtt is null) shownServerRoundTripMs = null;
        else if (shownServerRoundTripMs is not { } shown || Math.Abs(rtt.Value - shown) >= Math.Max(2, shown * 0.1)) shownServerRoundTripMs = rtt;
        if (info is not null)
        {
            if (shownServerGapMs is not { } gap || Math.Abs(info.YourLongestGapMs - gap) >= 3 || (info.YourLongGaps > 0) != (shownServerLongGaps > 0))
            {
                shownServerGapMs = info.YourLongestGapMs;
                shownServerLongGaps = info.YourLongGaps;
            }
            info = info with { YourLongestGapMs = shownServerGapMs ?? info.YourLongestGapMs, YourLongGaps = shownServerLongGaps };
        }
        // Older than server-v2.16: known from the check before connecting (it answered without saying its version), or by
        // saying nothing for ServerStatusOlderAfter once in its group. Until 2026-10-02 only the second, so "measuring" stood
        // for 8 seconds on a server already known not to report.
        var older = info is null && rtt is null && (relay.Equals(serverKnownOlder)
            || relayGroup.InGroupSinceUtc is { } since && DateTime.UtcNow - since >= ServerStatusOlderAfter);
        var sendOn = sender.IsRunning || pluginTrackSource.AnyHostSending;
        var sending = sendOn && SelectedSendEndpoints().Any(ep => ep.Equals(relay));
        var group = relayGroup.Members.Where(m => m.Relay.Equals(relay))
            .Select(m => (string.IsNullOrWhiteSpace(m.Name) ? "someone not yet named" : m.Name, m.TicksUs)).ToList();
        var entry = relayEntryText.Length > 0 ? relayEntryText : relay.Address.ToString();
        var connectedFor = relayConnectedUtc is { } at ? DateTime.UtcNow - at : TimeSpan.Zero;
        return ServerStatusText(new ServerStatusFacts(entry, relay, connectedFor, shownServerRoundTripMs, info, older, sending, group,
            relayGroup.IsV1Paired(relay), sendOn));
    }

    /// <summary>Once a minute while on a server, the status in the log on one line, stamped with the configuration.</summary>
    private void LogServerStatusIfDue()
    {
        if (relayGroup.ConnectedRelay is null) { lastServerStatusLogUtc = DateTime.MinValue; return; }
        if (DateTime.UtcNow - lastServerStatusLogUtc < TimeSpan.FromMinutes(1)) return;
        if (ServerStatusForWindow() is not { } text) return;
        lastServerStatusLogUtc = DateTime.UtcNow;
        logFile.Event($"server status [{ActiveAudioConfiguration().Describe()}]: {text.Replace(Environment.NewLine, " | ")}");
    }

    internal string? ServerStatusForTest() => ServerStatusForWindow();
    internal void LogServerStatusForTest() { lastServerStatusLogUtc = DateTime.MinValue; LogServerStatusIfDue(); }
}
