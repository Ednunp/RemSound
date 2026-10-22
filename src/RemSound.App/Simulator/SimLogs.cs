using System.Globalization;
using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App.Simulator;

/// <summary>What one simulated session did to the log and its readings file.</summary>
internal sealed record SimLogResult(
    string Network, SimConfig Config, double Hours,
    long Gaps, int SecondsWithGaps, int PeerDrops, int Runs, int Worse, int Captures,
    int MinuteRows, int SecondRows, int StreamRows, long LogBytes, long ReadingsBytes, int LogLines,
    bool InTimeOrder, bool RowsWhole, bool MinutesComplete, string LogPath, string ReadingsPath)
{
    public double LogBytesPerHour => Hours > 0 ? LogBytes / Hours : 0;
    public double ReadingsBytesPerHour => Hours > 0 ? ReadingsBytes / Hours : 0;
}

/// <summary>
/// THE LOG ON A REAL CONNECTION (2026-10-19, kept up to date 2026-10-22). Ed, before releasing the once-a-minute log: "if
/// logs are variable given quality of connection ... could that variability make logs go all over the place".
///
/// <para>A simulated session over each modelled network: the real receiver, its real audible gaps and each stream's own
/// report, the main window's auto-tune running, and the real <see cref="RemSoundLog"/> on the simulated clock taking a
/// readings row a second, as the window gives it one (MainForm.Readings.cs) - the machine's row and a row per stream on
/// each output, runs of gaps through the app's own <see cref="GapEpisodes"/>, and a peer made to drop at set times
/// (Jonathan's "my laptop kept disconnecting"), each keeping the five minutes before it. Then both files are read back.</para>
///
/// <para>The machine row's numbers are this run's where the receiver has them and fixed elsewhere; their length decides the
/// size. Which seconds are written, the minute rows and the problem lines are the app's own code.</para>
/// </summary>
internal static class SimLogs
{
    public static SimLogResult Run(SimNetwork network, SimConfig config, double seconds, int peerDropEverySec = 0, int seed = 1)
    {
        var dir = Path.Combine(Path.GetTempPath(), "remsound-simlogs-" + Guid.NewGuid().ToString("N"));
        using var scope = AppConfig.UseThrowawayUserDataDirectory(dir);
        var start = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Local);
        var simNow = 0.0;
        var configuration = config switch
        {
            SimConfig.AsioOnly => AudioConfiguration.AsioOnly,
            SimConfig.Both => AudioConfiguration.Both,
            _ => AudioConfiguration.WasapiOnly,
        };
        var groups = AppReadings.GroupsInUse(configuration, receiving: true, sending: false, (false, false, false), captureSources: false, onServer: false);
        var episodes = new GapEpisodes();
        var gapsSeen = new Dictionary<string, long>(StringComparer.Ordinal);
        long gaps = 0;
        int secondsWithGaps = 0, drops = 0, runs = 0, worse = 0, captures = 0;
        string? logPath, readingsPath;
        using (var log = new RemSoundLog { Enabled = true, NowForTest = () => start.AddSeconds(simNow) })
        {
            void EverySecond(double now, AudioReceiver receiver, IReadOnlyList<AudioReceiver.StreamNeedReport> reports)
            {
                simNow = now;
                var sec = (int)Math.Floor(now);
                var time = start.AddSeconds(now);
                // The app's order: a peer that went, then the second's streams and its run of gaps, then the rows.
                if (peerDropEverySec > 0 && sec % peerDropEverySec == 0)
                {
                    drops++;
                    captures++;
                    var kept = log.Capture("LAPTOP disconnected", MainForm.DisconnectLookBack);
                    log.Event(LogLevel.Warn, "Peer", $"LAPTOP disconnected: it stopped answering - {kept}");
                }
                var row = new ReadingsRow(AppReadings.Schema, groups)
                    .Set("connected", true).Set("sending", false).Set("receiving", true).Text("codec", "Opus 10 ms").Text("peers", "192.0.2.10: 1ms")
                    .Set("bufferMs", receiver.CurrentBufferMs).Set("receivedPackets", receiver.PacketsReceived).Set("receivedKB", receiver.BytesReceived / 1024)
                    .Set("underrunsTotal", receiver.Underruns).Set("dropsTotal", receiver.Drops).Text("receiveDevice", "Simulated output")
                    .Set("bufAvgMs", receiver.CurrentBufferMs).Set("bufMinMs", receiver.CurrentBufferMs).Set("bufMaxMs", receiver.CurrentBufferMs)
                    .Set("maxGapMs", 20).Set("renderCbGapMs", 1).Set("reads", 1520).Set("recvMs", 0.4).Set("renderMs", 8.9)
                    .Set("wasapiRenderMs", 4.2).Set("asioRenderMs", 8.9).Set("driftErrorFrames", 830.8).Set("wireInOrder", 100)
                    .Set("cpuPercent", 1.2).Set("managedMB", 3.6).Set("workingSetMB", 126.5).Set("privateMB", 56.6).Set("handles", 820).Set("threads", 29)
                    .Set("latencyMs", 27.9).Set("latencyMaxMs", 27.9).Set("latencyQueueMs", receiver.CurrentBufferMs).Set("latencyRenderMs", 3.6);
                var streams = new List<ReadingsRow>();
                long newGaps = 0;
                var longest = 0;
                var byStream = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var r in reports)
                    foreach (var o in r.Outputs)
                    {
                        var key = $"{r.Stream}|{o.Route}";
                        var g = MainForm.UnderrunsThisSecond(o.GapsTotal, gapsSeen.GetValueOrDefault(key, -1));
                        gapsSeen[key] = o.GapsTotal;
                        newGaps += g;
                        longest = Math.Max(longest, o.LongestGapMs);
                        if (g > 0) byStream[$"the stream on {AppReadings.OutputName(o.Route)}"] = g;
                        streams.Add(new ReadingsRow(AppReadings.Schema, [AppReadings.Stream]) { Stream = r.Stream, Output = AppReadings.OutputName(o.Route) }
                            .Set("packets", r.Packets).Set("arrivalGapMs", r.GapMs).Set("arrivalGapPeakMs", r.GapPeakMs).Set("needMs", o.NeedMs)
                            .Set("playsAtMs", o.PlaysAtMs).Set("depthMs", o.DepthMs).Set("depthMinMs", o.DepthMs).Set("streamUnderruns", o.NewUnderruns)
                            .Set("streamGaps", g).Set("streamLongestGapMs", o.LongestGapMs));
                    }
                row.Set("gaps", newGaps).Set("longestGapMs", longest);
                if (newGaps > 0) { gaps += newGaps; secondsWithGaps++; }
                foreach (var change in episodes.Second(time, newGaps, longest, byStream))
                {
                    if (change.Kind is GapEpisodes.Kind.Started or GapEpisodes.Kind.Worse)
                    {
                        if (change.Kind == GapEpisodes.Kind.Started) runs++; else worse++;
                        captures++;
                        var kept = log.Capture(change.Words, MainForm.GapsLookBack);
                        log.Event(LogLevel.Warn, "Gaps", $"{change.Words} [{configuration.Describe()}] - {kept}");
                    }
                    else log.Event(change.Kind == GapEpisodes.Kind.StillGoing ? LogLevel.Info : LogLevel.Warn, "Gaps", $"{change.Words} [{configuration.Describe()}]");
                }
                log.Readings(row, streams);
            }
            Simulation.Run(new SimScenario($"logs: {network.Name}", config, SimCodec.Opus10, network, 20, seconds, Seed: seed,
                ContinuousAutoTune: true, ChunkSeconds: 60, EverySecondDo: EverySecond));
            simNow = seconds + 1;
            logPath = log.Path;
            log.Dispose();
            readingsPath = log.ReadingsPath;
        }

        var logLines = logPath is null ? [] : File.ReadAllLines(logPath);
        var readings = readingsPath is null ? [] : File.ReadAllLines(readingsPath);
        var header = readings.FirstOrDefault() ?? "";
        var width = header.Split(',').Length;
        var times = new List<DateTime>();
        int minuteRows = 0, secondRows = 0, streamRows = 0;
        var whole = true;
        foreach (var line in readings.Skip(1))
        {
            var fields = line.Split(',');   // the simulator's names hold no commas
            whole &= fields.Length == width;
            times.Add(DateTime.ParseExact(fields[0], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            if (fields[2].Length > 0) streamRows++;
            else if (fields[1] == "minute") minuteRows++;
            else secondRows++;
        }
        var hours = seconds / 3600.0;
        return new SimLogResult(network.Name, config, hours, gaps, secondsWithGaps, drops, runs, worse, captures,
            minuteRows, secondRows, streamRows,
            logPath is null ? 0 : new FileInfo(logPath).Length, readingsPath is null ? 0 : new FileInfo(readingsPath).Length, logLines.Length,
            InTimeOrder: times.Zip(times.Skip(1)).All(p => p.First <= p.Second),
            RowsWhole: whole && header.StartsWith("Time,Kind,Stream,Output,", StringComparison.Ordinal),
            MinutesComplete: minuteRows >= (int)Math.Floor(seconds / 60),
            LogPath: logPath ?? "", ReadingsPath: readingsPath ?? "");
    }

    /// <summary>The most machine-wide second rows a run can hold: every capture's look-back and the 30 seconds after, with
    /// one more for the second it began in.</summary>
    public static int MostSecondRows(SimLogResult r) =>
        r.PeerDrops * (int)(MainForm.DisconnectLookBack.TotalSeconds + ReadingsRecorder.After.TotalSeconds + 1)
        + (r.Runs + r.Worse) * (int)(MainForm.GapsLookBack.TotalSeconds + ReadingsRecorder.After.TotalSeconds + 1);

    public static string Describe(SimLogResult r) =>
        $"{r.Network}, {r.Config}: {r.Hours * 60:0} min; {r.Gaps} audible gaps in {r.SecondsWithGaps} seconds, {r.PeerDrops} peer drops; "
        + $"{r.Runs} runs of gaps ({r.Worse} got worse), {r.Captures} captures; the log {r.LogLines} lines, {r.LogBytesPerHour / 1024.0:0.#} KB an hour; "
        + $"the readings {r.MinuteRows} minute rows, {r.SecondRows} second rows, {r.StreamRows} stream rows, {r.ReadingsBytesPerHour / 1024.0:0} KB an hour; "
        + $"{(r.InTimeOrder ? "in time order" : "OUT OF ORDER")}, {(r.RowsWhole ? "every row whole" : "A ROW BROKEN")}, "
        + $"{(r.MinutesComplete ? "every minute summed up" : "MINUTES MISSING")}";
}
