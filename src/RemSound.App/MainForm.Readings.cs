using System.Net;
using System.Net.NetworkInformation;
using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App;

/// <summary>
/// The once-a-second readings and the problem lines of the log (2026-10-22, issue 35, the seven ideas Jonathan Schuster
/// agreed): the readings go to their own CSV file, each only while what it measures is in use; a run of audible gaps is
/// one episode with one capture and one summary line; every disconnect says who and why, keeps the five minutes before it,
/// and ends its connection with a summary line; a change in this computer's network is a line of its own.
/// </summary>
public sealed partial class MainForm
{
    private ReadingsRow? tickRow;
    private List<ReadingsRow> tickStreams = [];
    private bool gapsNotedThisTick;
    private readonly GapEpisodes gapEpisodes = new();
    /// <summary>Each stream's audible gaps (per output) and lost packets as last seen, to count this second's.</summary>
    private readonly Dictionary<string, long> streamGapsSeen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> streamMissedSeen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> streamInOrderSeen = new(StringComparer.Ordinal);
    /// <summary>Each connection while it lasts, by the peer's key: its summary line when it ends.</summary>
    private readonly Dictionary<string, ConnectionTally> connections = new(StringComparer.OrdinalIgnoreCase);
    private DateTime lastNetworkChangeUtc = DateTime.MinValue, lastNetworkLineUtc = DateTime.MinValue, lastResumeUtc = DateTime.MinValue;

    /// <summary>How far a disconnect looks back in the readings file (the gaps that led up to it), and a run of gaps.</summary>
    internal static readonly TimeSpan DisconnectLookBack = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan GapsLookBack = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan WriteLogsNowLookBack = TimeSpan.FromMinutes(5);

    /// <summary>The last second's readings, for the gate.</summary>
    internal ReadingsRow? LastReadingsForTest { get; private set; }
    internal IReadOnlyList<ReadingsRow> LastStreamReadingsForTest { get; private set; } = [];
    /// <summary>Gate seam: what the sender's lanes are doing, in place of the real sender's.</summary>
    internal Func<(bool Wasapi, bool Asio, bool Plugin)>? LanesSendingForTest;
    /// <summary>Gate seam: the receiving half's state, in place of the real receiver's.</summary>
    internal Func<bool>? ReceivingForTest;

    /// <summary>The start of a second: what is in use decides which readings this second's row will keep.</summary>
    private void BeginReadings()
    {
        var groups = AppReadings.GroupsInUse(ActiveAudioConfiguration(), ReceivingForTest?.Invoke() ?? receiver.IsRunning, SenderRunningNow,
            LanesSendingForTest?.Invoke() ?? sender.LanesSending, captureSources: SenderRunningNow, onServer: relayGroup.ConnectedRelay is not null);
        tickRow = new ReadingsRow(AppReadings.Schema, groups);
        tickStreams = [];
        gapsNotedThisTick = false;
    }

    /// <summary>This second's row, begun if a gate step reached here without the tick.</summary>
    private ReadingsRow Row
    {
        get
        {
            if (tickRow is null) BeginReadings();
            return tickRow!;
        }
    }

    /// <summary>The program's own readings: its garbage collections this second, CPU, memory, handles and threads.</summary>
    private void SetProcessReadings(ProcessSelfMeter.Snapshot m, int gc0, int gc1, int gc2) =>
        Row.Set("gc0", gc0).Set("gc1", gc1).Set("gc2", gc2).Set("cpuPercent", m.CpuPercentOneCore).Set("managedMB", m.ManagedHeapMb)
           .Set("workingSetMB", m.WorkingSetMb).Set("allocKBps", m.AllocatedKbPerSecond).Set("privateMB", m.PrivateBytesMb)
           .Set("gcHeapMB", m.GcHeapMb).Set("gcFragmentedMB", m.GcFragmentedMb).Set("gcCommittedMB", m.GcCommittedMb)
           .Set("handles", m.HandleCount).Set("threads", m.ThreadCount);

    /// <summary>The end of a second: its rows to the readings file, and a quiet second for a run of gaps if none were heard.</summary>
    private void SubmitReadings()
    {
        if (tickRow is null) return;
        if (!gapsNotedThisTick) NoteGapEpisode(0, 0, null);
        LastReadingsForTest = tickRow;
        LastStreamReadingsForTest = tickStreams;
        logFile.Readings(tickRow, tickStreams);
        tickRow = null;
    }
    internal void SubmitReadingsForTest() => SubmitReadings();
    internal void BeginReadingsForTest() => BeginReadings();

    /// <summary>
    /// Each stream's second, from the per-stream report: a row for each stream on each output it plays on (only outputs
    /// in use have one), this machine's dropouts and gaps per output added up from them - asked of the configuration, so
    /// with no ASIO driver the one output is WASAPI's - and the run of gaps and each connection's tally fed.
    /// </summary>
    private void NoteStreamsSecond(int serverGapMs, int serverDispatchMs, string asioLaneSends, IReadOnlyList<AudioReceiver.StreamNeedReport> reports)
    {
        var row = Row;
        row.Set("serverPathGapMs", serverGapMs).Set("serverDispatchMs", serverDispatchMs).Text("asioLaneSends", asioLaneSends)
           .Set("peersAskingJamulusStyle", CustomOpusPeers.Count);
        long wasapiUnderruns = 0, asioUnderruns = 0, wasapiGapsTotal = 0, asioGapsTotal = 0, newGaps = 0;
        var longest = 0;
        var byStream = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var r in reports)
        {
            var missedNow = r.WireMissedTotal;
            // Counted as the dropouts always were (UnderrunsThisSecond): none at a stream's first second, none when a count falls.
            var lost = UnderrunsThisSecond(missedNow, streamMissedSeen.GetValueOrDefault(r.Stream, -1));
            streamMissedSeen[r.Stream] = missedNow;
            var inOrder = UnderrunsThisSecond(r.WireInOrderTotal, streamInOrderSeen.GetValueOrDefault(r.Stream, -1));
            streamInOrderSeen[r.Stream] = r.WireInOrderTotal;
            foreach (var o in r.Outputs)
            {
                var key = $"{r.Stream}|{o.Route}";
                var gaps = UnderrunsThisSecond(o.GapsTotal, streamGapsSeen.GetValueOrDefault(key, -1));
                streamGapsSeen[key] = o.GapsTotal;
                if (o.Route == RenderRoute.AsioLane) { asioUnderruns += o.NewUnderruns; asioGapsTotal += o.GapsTotal; }
                else { wasapiUnderruns += o.NewUnderruns; wasapiGapsTotal += o.GapsTotal; }
                newGaps += gaps;
                if (o.LongestGapMs > longest) longest = o.LongestGapMs;
                if (gaps > 0) byStream[$"the stream from {r.Endpoint?.ToString() ?? r.Stream} on {AppReadings.OutputName(o.Route)}"] = gaps;
                tickStreams.Add(new ReadingsRow(AppReadings.Schema, [AppReadings.Stream]) { Stream = r.Stream, Output = AppReadings.OutputName(o.Route) }
                    .Set("packets", r.Packets).Set("lostPackets", lost).Set("arrivalGapMs", r.GapMs).Set("arrivalGapPeakMs", r.GapPeakMs)
                    .Set("arrivalGapUsedMs", r.GapUsedMs).Set("needMs", o.NeedMs).Set("ownMs", o.OwnMs).Set("playsAtMs", o.PlaysAtMs)
                    .Set("outputTargetMs", o.OutputMs).Set("depthMs", o.DepthMs).Set("depthMinMs", o.DepthMs)
                    .Set("streamUnderruns", o.NewUnderruns).Set("streamGaps", gaps).Set("streamLongestGapMs", o.LongestGapMs));
                if (r.Endpoint is { } from) TallyStream(from, gaps, o.DepthMs);
            }
            if (r.Endpoint is { } source) TallyPackets(source, r.Packets, lost, inOrder);
        }
        row.Set("wasapiUnderruns", wasapiUnderruns).Set("asioUnderruns", asioUnderruns)
           .Set("wasapiGapsTotal", wasapiGapsTotal).Set("asioGapsTotal", asioGapsTotal)
           .Set("gaps", newGaps).Set("longestGapMs", longest);
        NoteGapEpisode(newGaps, longest, byStream);
    }

    /// <summary>A run of audible gaps: its start and a gap clearly worse than any before it keep the seconds around them;
    /// once an hour it says it is still going; a minute without a gap ends it with one summary line (GapEpisodes).</summary>
    private void NoteGapEpisode(long newGaps, int longestMs, IReadOnlyDictionary<string, long>? byStream)
    {
        gapsNotedThisTick = true;
        foreach (var change in gapEpisodes.Second(DateTime.Now, newGaps, longestMs, byStream))
        {
            var config = $" [{ActiveAudioConfiguration().Describe()}]";
            switch (change.Kind)
            {
                case GapEpisodes.Kind.Started:
                case GapEpisodes.Kind.Worse:
                    var kept = logFile.Capture(change.Words, GapsLookBack);
                    logFile.Event(LogLevel.Warn, "Gaps", change.Words + config + (kept is null ? "" : $" - {kept}"));
                    break;
                case GapEpisodes.Kind.StillGoing:
                    logFile.Event(LogLevel.Info, "Gaps", change.Words + config);
                    break;
                case GapEpisodes.Kind.Ended:
                    logFile.Event(LogLevel.Warn, "Gaps", change.Words + config);
                    break;
            }
        }
    }

    // ---- Connections: who, why, and how it went -------------------------------------------------------------------------

    /// <summary>One connection while it lasts: when it began, its pings, and - for a peer reached directly - its sound.</summary>
    private sealed class ConnectionTally(DateTime startedUtc, IPAddress address, bool viaServer)
    {
        public DateTime StartedUtc { get; } = startedUtc;
        public IPAddress Address { get; } = address;
        /// <summary>Somebody on a server: their sound arrives from the server's address with everybody else's, so it is not
        /// theirs to count - only their pings are.</summary>
        public bool ViaServer { get; } = viaServer;
        public long Pings, PingSumMs;
        public int PingWorstMs;
        public long Packets, Lost, Gaps;
        public int? LowestDepthMs;
        public bool HeardSound;

        public void NoteStream(long gaps, int depthMs)
        {
            Gaps += gaps;
            if (depthMs > 0 && (LowestDepthMs is null || depthMs < LowestDepthMs)) LowestDepthMs = depthMs;
        }

        public void NotePackets(long packets, long lost, long inOrder)
        {
            Packets += Math.Max(packets, inOrder + lost);
            Lost += lost;
            HeardSound |= packets > 0;
        }

        public void NotePing(int rtt)
        {
            Pings++;
            PingSumMs += rtt;
            if (rtt > PingWorstMs) PingWorstMs = rtt;
        }
    }

    private void TallyStream(IPEndPoint from, long gaps, int depthMs)
    {
        foreach (var c in connections.Values)
        {
            if (c.ViaServer || !c.Address.Equals(from.Address)) continue;
            c.NoteStream(gaps, depthMs);
        }
    }

    private void TallyPackets(IPEndPoint from, long packets, long lost, long inOrder)
    {
        foreach (var c in connections.Values)
        {
            if (c.ViaServer || !c.Address.Equals(from.Address)) continue;
            c.NotePackets(packets, lost, inOrder);
        }
    }

    /// <summary>A connection begins: its tally from now.</summary>
    private void StartConnection(string key, PeerHealth ph) =>
        connections[key] = new ConnectionTally(DateTime.UtcNow, ph.AudioEndpoint.Address, viaServer: relayGroup.RelayOf(ph.AudioEndpoint) is not null
            || relayGroup.Members.Any(m => m.Address.Equals(ph.AudioEndpoint)));

    private void TallyPing(string key, int? rttMs)
    {
        if (rttMs is not { } rtt || !connections.TryGetValue(key, out var c)) return;
        c.NotePing(rtt);
    }

    /// <summary>A connection's summary when it ends: how long it lasted, its sound and its pings, in one line.</summary>
    private void EndConnection(string key, string name, string how)
    {
        if (!connections.Remove(key, out var c)) return;
        var parts = new List<string>();
        if (c.HeardSound)
        {
            parts.Add($"{c.Gaps:#,0} audible gap{(c.Gaps == 1 ? "" : "s")}");
            parts.Add(c.Packets > 0
                ? $"{c.Lost:#,0} of {c.Packets:#,0} packets lost ({100.0 * c.Lost / c.Packets:0.##}%)"
                : $"{c.Lost:#,0} packets lost");
            if (c.LowestDepthMs is { } low) parts.Add($"its buffer at its lowest {low} ms");
        }
        else parts.Add(c.ViaServer ? "its sound came through the server, which does not say whose was whose" : "no sound came from it");
        if (c.Pings > 0) parts.Add($"ping {c.PingSumMs / c.Pings} ms on average, {c.PingWorstMs} ms at worst");
        logFile.Event(LogLevel.Info, "Peer", $"{name}: connection ended ({how}) after {GapEpisodes.Duration(DateTime.UtcNow - c.StartedUtc)}: {string.Join(", ", parts)}");
    }

    /// <summary>RemSound closing: each connection still open gets its summary.</summary>
    private void EndAllConnections(string how)
    {
        foreach (var key in connections.Keys.ToList()) EndConnection(key, key, how);
    }

    /// <summary>Why a peer was lost, in words a reader acts on: it left the server, or it stopped answering - and whether
    /// this computer's network changed, or it woke from sleep, just before (2026-10-22, issue 35: Jonathan's laptop "kept
    /// disconnecting").</summary>
    private string WhyLost(PeerHealth ph)
    {
        var why = relayGroup.Members.FirstOrDefault(m => m.Address.Equals(ph.AudioEndpoint)) is { } member && relayRecentlyGone.ContainsKey(member.Id)
            ? "it left the server"
            : ph.AgeOfLastPong is { } age
                ? $"it stopped answering - no reply to its pings for {GapEpisodes.Duration(age)}, and no sound for over {PeerAudioWindow.TotalSeconds:0} seconds"
                : "it never answered its pings, and no sound came";
        var now = DateTime.UtcNow;
        if (now - lastNetworkChangeUtc < TimeSpan.FromMinutes(1))
            why += $"; this computer's network changed {GapEpisodes.Duration(now - lastNetworkChangeUtc)} before";
        if (now - lastResumeUtc < TimeSpan.FromMinutes(1))
            why += $"; this computer woke from sleep {GapEpisodes.Duration(now - lastResumeUtc)} before";
        return why;
    }

    /// <summary>This computer's network changed: a line of its own (what is connected now), at most one every ten seconds -
    /// Windows reports one change several times - and remembered, so a disconnect just after can say so.</summary>
    private void NoteNetworkChangedForTheLog(bool? available)
    {
        var now = DateTime.UtcNow;
        lastNetworkChangeUtc = now;
        if (now - lastNetworkLineUtc < TimeSpan.FromSeconds(10)) return;
        lastNetworkLineUtc = now;
        string up;
        try
        {
            up = string.Join(", ", NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => n.Name));
        }
        catch (Exception ex) { up = $"(could not list them: {ex.GetType().Name})"; }
        logFile.Event(LogLevel.Warn, "Network", available == false
            ? $"this computer has no network now (connected: {(up.Length == 0 ? "nothing" : up)})"
            : $"this computer's network changed (connected now: {(up.Length == 0 ? "nothing" : up)})");
    }
    internal void NoteNetworkChangedForTest(bool? available) => NoteNetworkChangedForTheLog(available);
    internal string WhyLostForTest(PeerHealth ph) => WhyLost(ph);
    internal void NoteStreamsSecondForTest(IReadOnlyList<AudioReceiver.StreamNeedReport> reports) => NoteStreamsSecond(0, 0, "the codec setting", reports);
    internal void NoteResumeForTest() => lastResumeUtc = DateTime.UtcNow;
}
