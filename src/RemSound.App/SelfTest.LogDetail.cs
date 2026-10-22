using System.Net;
using System.Text.RegularExpressions;
using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App;

/// <summary>
/// Logs a person can read (issue 35, Jonathan Schuster). 6.2.2 (2026-10-19) wrote the lines made every second once a
/// minute and in full around a problem; 6.2.3 (2026-10-22, the seven ideas Jonathan agreed) moves those readings to a CSV
/// file of their own, sums every minute up, writes each reading only while what it measures is in use, makes a run of
/// gaps one capture and one summary line, gives every disconnect its reason and its own capture, ends each connection with
/// a summary, and starts every line of the log with a severity word and a category.
/// </summary>
internal static partial class SelfTest
{
    private static List<string> ReadLinesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        var lines = new List<string>();
        while (sr.ReadLine() is { } line) lines.Add(line);
        return lines;
    }

    /// <summary>
    /// THE READINGS FILE: A ROW FOR EVERY MINUTE, SUMMED UP, AND EVERY SECOND ONLY AROUND A PROBLEM - in time order, five
    /// minutes behind until it is flushed, and off means off.
    ///
    /// <para>Driven through the real log on a stepped clock and read back as BYTES ON DISK. Each kind of summing (a count
    /// added, a peak, a low point, an average, the last value, a name with a comma and quotes in it) checked on a full minute;
    /// the 5-minute lag; a 30-second capture and a 5-minute one, overlapping without a row twice; a reading not in use left
    /// empty while a zero in use stays; and rows waiting when logging went off never reaching the file.</para>
    ///
    /// <para>Not per-configuration: this is the file's rule. Which readings each configuration keeps is its own step.</para>
    /// </summary>
    private static string? LogReadingsFileFollowsTheRule()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-readings-" + Guid.NewGuid().ToString("N"));
        using var scope = AppConfig.UseThrowawayUserDataDirectory(scratch);
        var start = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Local);
        var now = start;
        var schema = new ReadingsSchema(
        [
            new("sum", ReadingAgg.Sum), new("max", ReadingAgg.Max), new("min", ReadingAgg.Min), new("mean", ReadingAgg.Mean),
            new("last", ReadingAgg.Last), new("lane", ReadingAgg.Last, "lane"), new("name", ReadingAgg.Last, Text: true), new("sec", ReadingAgg.Last),
        ]);
        string readingsPath;
        int writtenAt600;
        using (var log = new RemSoundLog { Enabled = true, NowForTest = () => now })
        {
            void Second(int t, bool laneInUse = true)
            {
                now = start.AddSeconds(t);
                log.Readings(new ReadingsRow(schema, laneInUse ? ["lane"] : []).Set("sum", 1).Set("max", t % 60 == 30 ? 99 : 1)
                    .Set("min", t % 60 == 45 ? -5 : 2).Set("mean", t % 2 == 0 ? 10 : 20).Set("last", t).Set("lane", 0)
                    .Text("name", "a, \"quoted\" name").Set("sec", t));
            }
            for (var t = 0; t < 600; t++) Second(t);
            readingsPath = Require(log.ReadingsPath, "the readings file must have been made once the first rows were old enough");
            writtenAt600 = ReadLinesShared(readingsPath).Count - 1;
            now = start.AddSeconds(600);
            log.Capture("a gap", TimeSpan.FromSeconds(30));             // at 600, before its row: 570..630
            for (var t = 600; t < 2000; t++) Second(t, laneInUse: t is < 605 or > 610);
            now = start.AddSeconds(2000);
            log.Capture("a disconnect", TimeSpan.FromMinutes(5));       // at 2000: 1700..2030
            for (var t = 2000; t < 2010; t++) Second(t);
            now = start.AddSeconds(2010);
            log.Capture("another disconnect", TimeSpan.FromMinutes(5)); // at 2010: overlaps, on to 2040
            for (var t = 2010; t < 2500; t++) Second(t);
            now = start.AddSeconds(2500);
            log.Capture("waiting when logging went off", TimeSpan.FromSeconds(30));   // 2470..2530: must never be written
            log.Enabled = false;
            for (var t = 2500; t < 2510; t++) Second(t);
            log.Enabled = true;
            for (var t = 2510; t <= 2600; t++) Second(t);
        }

        var lines = ReadLinesShared(readingsPath);
        var header = CommandLine.CsvFields(lines[0]);
        Check(lines[0].StartsWith("Time,Kind,Stream,Output,sum,max,min,mean,last,lane,name,sec", StringComparison.Ordinal),
            $"the file must start with its header row naming every column ({lines[0]})");
        var rows = lines.Skip(1).Select(CommandLine.CsvFields).ToList();
        string Cell(List<string> row, string name) => row[header.IndexOf(name)];
        Check(rows.All(r => r.Count == header.Count), "EVERY ROW WHOLE: a name with a comma and quotes in it must not break a row");
        var times = rows.Select(r => DateTime.Parse(r[0])).ToList();
        Check(times.Zip(times.Skip(1)).All(p => p.First <= p.Second), "IN TIME ORDER: a capture reaching back five minutes must never put an older row after a newer one");
        Check(writtenAt600 == 4, $"FIVE MINUTES BEHIND: at 12:10 the file must hold only the minutes older than five minutes - 4 rows, not {writtenAt600}");

        var minutes = rows.Where(r => r[1] == "minute").ToList();
        var seconds = rows.Where(r => r[1] == "second").Select(r => int.Parse(Cell(r, "sec"))).ToList();
        var first = minutes.FirstOrDefault();
        Check(first is not null && first[0] == "2026-10-22 12:01:00" && Cell(first, "sum") == "60" && Cell(first, "max") == "99"
              && Cell(first, "min") == "-5" && Cell(first, "mean") == "15" && Cell(first, "last") == "59" && Cell(first, "lane") == "0"
              && Cell(first, "name") == "a, \"quoted\" name",
            $"THE WHOLE MINUTE: its row must add up the counts (60), keep the peak (99), the low point (-5), the average (15) and the last value (59), at the time the minute ended ({(first is null ? "none" : string.Join(",", first))})");
        var want = Enumerable.Range(570, 61).Concat(Enumerable.Range(1700, 341)).ToList();
        Check(seconds.SequenceEqual(want),
            $"THE CAPTURES: every second from 30 before the gap to 30 after, and from five minutes before each disconnect to 30 after the last, once each. Missing: {string.Join(", ", want.Except(seconds).Take(12))}; extra: {string.Join(", ", seconds.Except(want).Take(12))}");
        var notInUse = rows.Where(r => r[1] == "second" && int.Parse(Cell(r, "sec")) is >= 605 and <= 610).ToList();
        var inUse = rows.Where(r => r[1] == "second" && int.Parse(Cell(r, "sec")) == 600).ToList();
        Check(notInUse.Count == 6 && notInUse.All(r => Cell(r, "lane") == "") && inUse.Count == 1 && Cell(inUse[0], "lane") == "0",
            "ED'S RULE: a reading is empty while what it measures is not in use, and a zero while it is stays a zero");
        Check(!seconds.Any(s => s is >= 2470 and <= 2530), "OFF MEANS OFF: rows waiting when logging went off must never reach the file afterwards");
        // 36 minutes written before logging went off (those still waiting went with it), then two and the part-minute at the end.
        Check(minutes.Count == 39, $"a row for every minute logged ({minutes.Count})");

        return $"a header and {rows.Count} whole rows in time order; each minute summed up (count 60, peak 99, low -5, average 15, "
             + "last 59); five minutes behind until flushed; the 30-second and five-minute captures written once each; a reading "
             + "not in use empty and a zero in use kept; rows waiting when logging went off never written";
    }

    /// <summary>
    /// EVERY LINE OF THE LOG STARTS WITH A SEVERITY WORD, THE TIME AND A CATEGORY: "WARN 14:02:31.250 Peer: ...". Lines
    /// written before categories existed are sorted by their own words; the date is said at the start and when it changes.
    /// </summary>
    private static string? LogLinesSaySeverityAndCategory()
    {
        // Lines as the app writes them, and what they must become.
        (string Message, LogLevel Level, string Category, string Text)[] cases =
        [
            ("relay: connected to \"remote\" (192.0.2.1:47830)", LogLevel.Info, "Server", "connected to \"remote\" (192.0.2.1:47830)"),
            ("peer disconnected: 192.0.2.7:47830 (audio stopped, heartbeat=Unreachable)", LogLevel.Warn, "Peer disconnected", "192.0.2.7:47830 (audio stopped, heartbeat=Unreachable)"),
            ("service menu: status query failed IOException: the pipe broke", LogLevel.Error, "Service menu", "status query failed IOException: the pipe broke"),
            ("sender running but no capture callbacks yet (device=\"Mic\", error=\"none\")", LogLevel.Warn, "Sender", "sender running but no capture callbacks yet (device=\"Mic\", error=\"none\")"),
            ("ui: listening volume → 37% [WASAPI only]", LogLevel.Info, "Settings", "listening volume → 37% [WASAPI only]"),
            ("gate copy: listening on 47901", LogLevel.Info, "Gate copy", "listening on 47901"),
            ("first capture callback received (3840 bytes)", LogLevel.Info, "Audio", "first capture callback received (3840 bytes)"),
            ("CRASH REPORT written to crash-1.txt", LogLevel.Error, "App", "CRASH REPORT written to crash-1.txt"),
        ];
        foreach (var (message, level, category, text) in cases)
        {
            var got = LogLines.Classify(message);
            Check(got == (level, category, text), $"\"{message}\" must be {LogLines.Word(level)} {category}: {text} (got {LogLines.Word(got.Level)} {got.Category}: {got.Text})");
        }

        var scratch = Path.Combine(Path.GetTempPath(), "remsound-loglines-" + Guid.NewGuid().ToString("N"));
        using var scope = AppConfig.UseThrowawayUserDataDirectory(scratch);
        var now = new DateTime(2026, 10, 22, 23, 59, 58, DateTimeKind.Local);
        string path;
        using (var log = new RemSoundLog { Enabled = true, NowForTest = () => now })
        {
            log.Event("relay: connected to \"remote\"");
            log.Event(LogLevel.Warn, "Peer", "LAPTOP disconnected: it stopped answering");
            now = now.AddSeconds(4);   // past midnight
            log.Event("power: resumed");
            path = Require(log.Path, "the log must have made its file");
        }
        var lines = ReadLinesShared(path);
        var shape = new Regex(@"^(INFO|WARN|ERROR)\t\d\d:\d\d:\d\d\.\d{3}\t[^\t:]+: ");
        Check(lines.All(l => shape.IsMatch(l)), $"EVERY LINE: a severity word, the time and a category ({string.Join(" | ", lines.Where(l => !shape.IsMatch(l)).Take(3))})");
        Check(lines[0].Contains("Log: RemSound ", StringComparison.Ordinal) && lines[0].Contains("Thursday 22 October 2026", StringComparison.Ordinal)
              && lines[0].Contains("-readings.csv", StringComparison.Ordinal),
            $"THE FIRST LINE says what the log is, its date, and where the readings are ({lines[0]})");
        Check(lines.Any(l => l.StartsWith("INFO\t23:59:58.000\tServer: connected to", StringComparison.Ordinal)), "a line in its own words, sorted: INFO Server: ...");
        Check(lines.Any(l => l.StartsWith("WARN\t23:59:58.000\tPeer: LAPTOP disconnected", StringComparison.Ordinal)), "a line that says its own level and category");
        Check(lines.Any(l => l.Contains("Log: the date is now Friday 23 October 2026", StringComparison.Ordinal)), "THE DATE when it changes, so a time is never read against the wrong day");
        return $"{cases.Length} kinds of line sorted into their severity and category; every line of the file starts with the word, the "
             + "time and the category; the first line says what it is and where the readings are; the date is said when it changes";
    }

    /// <summary>
    /// LOG FILES NAMED BY WHEN THEY STARTED. Jonathan: "a bunch of files with random IDs, the date is at the very end".
    /// The app's and the plugin's files lead with the date and time, then the computer; a second copy in the same minute,
    /// and a session's later parts, get names of their own; the readings file is the log's name with "-readings.csv";
    /// housekeeping counts and deletes readings files with the logs, and never the one being written.
    /// </summary>
    private static string? LogFilesNamedByWhenTheyStarted()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-lognames-" + Guid.NewGuid().ToString("N"));
        using var scope = AppConfig.UseThrowawayUserDataDirectory(scratch);
        var machine = Regex.Escape(string.Concat(Environment.MachineName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)));
        var pid = Environment.ProcessId;
        var dated = new Regex(@"^RemSound-(\d{4}-\d{2}-\d{2}-\d{4})-" + machine + @"(-(\d+)(-\d+)?)?(-part\d+)?\.log$");

        var logs = new List<RemSoundLog>();
        try
        {
            var before = DateTime.Now.AddMinutes(-1);
            for (var i = 0; i < 3; i++)
            {
                var log = new RemSoundLog { Enabled = true };
                logs.Add(log);
                log.Event($"copy {i}");
            }
            var names = logs.Select(l => Path.GetFileName(Require(l.Path, "each log must have made a file"))).ToList();
            var matches = names.Select(n => dated.Match(n)).ToList();
            for (var i = 0; i < names.Count; i++)
            {
                Check(matches[i].Success, $"an app log must be named RemSound-date-time-computer.log (got {names[i]})");
                if (!matches[i].Success) continue;
                var when = DateTime.ParseExact(matches[i].Groups[1].Value, "yyyy-MM-dd-HHmm", null);
                Check(when >= before && when <= DateTime.Now, $"the date in the name must be when it started ({names[i]})");
            }
            Check(names.Distinct().Count() == 3, $"three copies must make three files, never share one ({string.Join(", ", names)})");
            if (matches.All(m => m.Success))
            {
                Check(!matches[0].Groups[3].Success, $"the first copy must not carry a process number ({names[0]})");
                for (var i = 1; i < 3; i++)
                    if (matches[i].Groups[1].Value == matches[0].Groups[1].Value)
                        Check(matches[i].Groups[3].Value == pid.ToString(), $"another copy in the same minute must carry its process number ({names[i]})");
            }

            // The readings file: the log's name with -readings.csv, and the window's summary reads it back.
            var first = logs[0];
            first.Readings(new ReadingsRow(AppReadings.Schema, [AppReadings.Receiving, AppReadings.WasapiOutput]).Set("connected", true)
                .Text("codec", "Opus 10 ms").Set("bufferMs", 21).Set("underrunsTotal", 4));
            first.FlushReadings(closing: true);
            var readings = Require(first.ReadingsPath, "the first rows must make the readings file");
            Check(readings == first.Path![..^".log".Length] + "-readings.csv", $"the readings file must be the log's name with -readings.csv ({Path.GetFileName(readings)})");
            var summary = CommandLine.LastSessionSnapshot();
            Check(summary.Contains("Connected=1", StringComparison.Ordinal) && summary.Contains("Buffer=21ms", StringComparison.Ordinal),
                $"the diagnostics report must read the session's last row from the readings file ({summary})");

            // Housekeeping: readings files are logs too, and the one being written is never deleted.
            var old = Path.Combine(AppConfig.LogsDirectory, "RemSound-2020-01-01-0000-OLD-readings.csv");
            File.WriteAllText(old, "Time,Kind\n");
            Check(LogMaintenance.LogsFolderSizeBytes() >= new FileInfo(readings).Length + new FileInfo(old).Length, "the logs folder's size must count the readings files");
            foreach (var l in logs.Skip(1)) l.Dispose();
            LogMaintenance.DeleteAllLogs(first.Path);
            Check(!File.Exists(old), "Delete all logs must delete readings files too");
            Check(File.Exists(readings) && File.Exists(first.Path!), "...but never the log being written, nor its readings file");
            // The same with files nothing holds open, so Windows' own lock on a file being written cannot hide a mistake: the
            // log named as the one being written keeps its readings file, and so does its later part.
            foreach (var active in new[] { "RemSound-2020-02-02-0000-X.log", "RemSound-2020-02-02-0000-X-part2.log" })
            {
                var log = Path.Combine(AppConfig.LogsDirectory, active);
                var itsReadings = Path.Combine(AppConfig.LogsDirectory, "RemSound-2020-02-02-0000-X-readings.csv");
                File.WriteAllText(log, "INFO");
                File.WriteAllText(itsReadings, "Time,Kind");
                LogMaintenance.DeleteAllLogs(log);
                Check(File.Exists(itsReadings), $"the readings file of the log being written ({active}) must never be deleted under it");
                LogMaintenance.DeleteAllLogs(first.Path);
                Check(!File.Exists(itsReadings), "and is deleted with the rest once its log is not the one being written");
            }

            // A session's later file: the same name, "-part2".
            var rolled = new RemSoundLog { Enabled = true, RollAfterBytes = 300 };
            logs.Add(rolled);
            var paths = new List<string>();
            for (var i = 0; i < 12; i++)
            {
                rolled.Event($"padding line {i} padding padding padding padding");
                if (rolled.Path is { } p && !paths.Contains(p)) paths.Add(p);
            }
            Check(paths.Count >= 2 && paths[1] == paths[0][..^".log".Length] + "-part2.log",
                $"the session's second file must be its part 2, named as its first with -part2 ({string.Join(", ", paths.Select(Path.GetFileName))})");
        }
        finally
        {
            foreach (var log in logs) log.Dispose();
        }

        // The plugin's: date first, then the computer, then the instance.
        AppConfig.WritePluginPointer(loggingEnabled: true);
        var id = Guid.NewGuid();
        string? plugin;
        using (var p = new PluginLog(id))
        {
            p.Pause();
            p.Event("named?");
            plugin = p.Path is { } pp ? Path.GetFileName(pp) : null;
        }
        Check(plugin is not null && Regex.IsMatch(plugin, @"^RemSoundPlugin-\d{4}-\d{2}-\d{2}-\d{4}-" + machine + "-" + id.ToString("N")[..8] + @"\.log$"),
            $"a plugin log must be named RemSoundPlugin-date-time-computer-instance.log (got {plugin ?? "no file"})");

        // Already taken - by anybody - is never opened: the next name is used instead.
        var dir = AppConfig.LogsDirectory;
        var stem = LogFileNames.AppStem(DateTime.Now);
        File.WriteAllText(Path.Combine(dir, stem + "-part7.log"), "somebody else's");
        var (taken, stream, _) = LogFileNames.CreateNew(dir, stem, 7);
        stream.Dispose();
        Check(Path.GetFileName(taken) == $"{stem}-{pid}-part7.log", $"a name already there must be passed over, not written into (got {Path.GetFileName(taken)})");
        Check(File.ReadAllText(Path.Combine(dir, stem + "-part7.log")) == "somebody else's", "and the file already there left as it was");

        return "app logs are named RemSound-date-time-computer, three copies in a minute made three files, the readings file is the "
             + "log's name with -readings.csv and the diagnostics report reads it; housekeeping counts and deletes readings files but "
             + "never the live ones; a session's next file is its part 2; plugin logs are RemSoundPlugin-date-time-computer-instance; "
             + "a name already taken is passed over";
    }

    /// <summary>
    /// A RUN OF AUDIBLE GAPS IS ONE CAPTURE AND ONE SUMMARY LINE. The rule (GapEpisodes) on a stepped clock: a trickle for
    /// twenty minutes is one start; a gap clearly longer than any before it a fresh capture; an hour in, a still-going line;
    /// a minute without a gap ends it, saying how long, how many, the longest and the stream most were on. Then the window's
    /// own wiring: each stream's report becomes a row per output, and the start of a run keeps its seconds.
    /// </summary>
    private static string? GapRunsAreOneCaptureAndOneLine()
    {
        var episodes = new GapEpisodes();
        var t0 = new DateTime(2026, 10, 22, 12, 0, 0);
        var changes = new List<(int At, GapEpisodes.Change Change)>();
        var byStream = new Dictionary<string, long> { ["the stream from LAPTOP on ASIO"] = 1 };
        for (var t = 0; t < 4000; t++)
        {
            long gaps = t < 3700 && t % 3 == 0 ? 1 : 0;
            var longest = gaps == 0 ? 0 : t == 600 ? 45 : 12;
            foreach (var c in episodes.Second(t0.AddSeconds(t), gaps, longest, gaps > 0 ? byStream : null)) changes.Add((t, c));
        }
        var kinds = changes.Select(c => c.Change.Kind).ToList();
        Check(kinds.Count(k => k == GapEpisodes.Kind.Started) == 1 && changes[0].At == 0, $"ONE START for a trickle of gaps ({string.Join(", ", kinds)})");
        Check(changes.Any(c => c.Change.Kind == GapEpisodes.Kind.Worse && c.At == 600 && c.Change.Words.Contains("a 45 ms gap", StringComparison.Ordinal)),
            "WORSE: a gap clearly longer than any before it is a fresh capture");
        Check(changes.Count(c => c.Change.Kind == GapEpisodes.Kind.StillGoing) == 1 && changes.Single(c => c.Change.Kind == GapEpisodes.Kind.StillGoing).At == 3600,
            "STILL GOING once an hour into a run");
        var ended = changes.SingleOrDefault(c => c.Change.Kind == GapEpisodes.Kind.Ended);
        Check(ended.At == 3699 + 60 && ended.Change is { } e && e.Words.StartsWith("gaps ended after 62 minutes: 1,234 gaps, the longest 45 ms, on the stream from LAPTOP on ASIO", StringComparison.Ordinal),
            $"THE SUMMARY a minute after the last gap: how long, how many, the longest and the stream ({ended.At}: {ended.Change?.Words})");

        // The window's own wiring.
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        MainForm? form = null;
        try
        {
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            var captures = new List<string>();
            var lines = new List<string>();
            form.LogForTest.CaptureTapForTest = why => { lock (captures) captures.Add(why); };
            form.LogForTest.EventTapForTest = line => { lock (lines) lines.Add(line); };
            form.ReceivingForTest = () => true;
            var laptop = new IPEndPoint(IPAddress.Parse("192.0.2.7"), 47830);
            AudioReceiver.StreamNeedReport Report(long asioGaps, long wasapiGaps, int longest) => new("192.0.2.7:47830 stream=1 Mixed Opus 10ms", 100, 11, 14, 12, "")
            {
                Endpoint = laptop, WireInOrderTotal = 100, WireMissedTotal = asioGaps,
                Outputs = [new(RenderRoute.AsioLane, 9, 0, 9, 10, 8, asioGaps, asioGaps, longest), new(RenderRoute.WasapiLane, 21, 0, 21, 30, 19, 0, wasapiGaps, 0)],
            };
            form.BeginReadingsForTest();
            form.NoteStreamsSecondForTest([Report(5, 2, 0)]);     // the first sighting: counts start here, nothing new
            form.SubmitReadingsForTest();
            Check(captures.Count == 0, "a stream's first second has nothing new: no run of gaps");
            form.BeginReadingsForTest();
            form.NoteStreamsSecondForTest([Report(8, 2, 14)]);    // three new gaps on ASIO
            var streams = form.LastStreamReadingsForTest;
            form.SubmitReadingsForTest();
            streams = form.LastStreamReadingsForTest;
            Check(captures.Count == 1 && captures[0].StartsWith("gaps started: 3 gaps this second, the longest 14 ms", StringComparison.Ordinal),
                $"THE START of a run keeps its seconds ({string.Join(" | ", captures)})");
            Check(lines.Any(l => l.StartsWith("Gaps: gaps started: 3 gaps", StringComparison.Ordinal) && l.Contains("on the stream from 192.0.2.7:47830 on ASIO", StringComparison.Ordinal)),
                $"and says so in the log, naming the stream and the output ({string.Join(" | ", lines.Where(l => l.StartsWith("Gaps", StringComparison.Ordinal)))})");
            var asio = streams.SingleOrDefault(r => r.Output == "ASIO");
            var wasapi = streams.SingleOrDefault(r => r.Output == "WASAPI");
            Check(asio?.Cell("streamGaps") == "3" && asio.Cell("streamLongestGapMs") == "14" && asio.Cell("lostPackets") == "3" && asio.Cell("depthMs") == "8"
                  && wasapi?.Cell("streamGaps") == "0" && wasapi.Cell("depthMs") == "19",
                "EACH OUTPUT ITS OWN ROW: the stream's gaps, lost packets and depth on ASIO and on WASAPI apart");
            form.BeginReadingsForTest();
            form.NoteStreamsSecondForTest([Report(9, 2, 0)]);
            form.SubmitReadingsForTest();
            Check(captures.Count == 1, "more gaps in the same run keep no more seconds");
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            CuePlayer.GloballyMuted = restoreMuted;
        }
        return "a trickle of gaps for an hour was one start, one fresh capture when a gap came clearly longer, one still-going line "
             + "and one summary a minute after the last gap; through the window, a stream's report became a row per output and the "
             + "start of a run kept its seconds, naming the stream and the output";
    }

    /// <summary>
    /// EACH READING ONLY WHILE WHAT IT MEASURES IS IN USE (Ed, 2026-10-20: "maybe they only get logged when they get
    /// turned on"). The outputs follow the configuration - asked of what is ticked, not the mode flag - in all three; a send
    /// lane only while it sends. Then through the window's real tick: not receiving, the receiving and output readings are
    /// empty; receiving, they are there.
    /// </summary>
    private static string? ReadingsOnlyWhileInUse()
    {
        foreach (var configuration in AudioConfigurations.All)
        {
            var groups = AppReadings.GroupsInUse(configuration, receiving: true, sending: true, (true, false, true), captureSources: true, onServer: false);
            Check(groups.Contains(AppReadings.WasapiOutput) == configuration.UsesWasapi() && groups.Contains(AppReadings.AsioOutput) == configuration.UsesAsio(),
                $"{configuration.Describe()}: the outputs in use are the ones the configuration plays on ({string.Join(", ", groups)})");
            Check(groups.Contains(AppReadings.WasapiSend) && !groups.Contains(AppReadings.AsioSend) && groups.Contains(AppReadings.PluginSend),
                $"{configuration.Describe()}: a send lane only while it sends");
            var quiet = AppReadings.GroupsInUse(configuration, receiving: false, sending: false, (true, true, true), captureSources: false, onServer: false);
            Check(quiet.Count == 0, $"{configuration.Describe()}: neither sending nor receiving, nothing but the program itself ({string.Join(", ", quiet)})");
        }

        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        var diagnosticsWere = DiagnosticsGate.Enabled;
        MainForm? form = null;
        try
        {
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            DiagnosticsGate.Enabled = true;
            var receiving = false;
            form.ReceivingForTest = () => receiving;
            form.SnapshotTickForTest();
            var off = Require(form.LastReadingsForTest, "the tick must have made its readings");
            Check(off.Unknown.Count == 0, $"every reading the tick sets must be a column of the readings file ({string.Join(", ", off.Unknown)})");
            Check(off.Cell("connected") is not null, "the program's own readings are always there");
            Check(off.Cell("bufferMs") is null && off.Cell("wasapiTargetMs") is null && off.Cell("asioTargetMs") is null,
                "NOT RECEIVING: the receiving and output readings stay empty");
            receiving = true;
            form.SnapshotTickForTest();
            var on = Require(form.LastReadingsForTest, "the tick must have made its readings");
            Check(on.Cell("bufferMs") is not null && on.Cell("wasapiTargetMs") is not null,
                $"RECEIVING on WASAPI: its readings are there ({form.ActiveAudioConfigurationForTest().Describe()})");
            Check(on.Cell("asioTargetMs") is null, "and the ASIO output's, not in use, are not");
            // Sending from the ASIO input only: its lane's readings, and not the WASAPI lane's.
            receiving = false;
            form.SenderRunningForTest = () => true;
            form.LanesSendingForTest = () => (false, true, false);
            form.SnapshotTickForTest();
            var sending = Require(form.LastReadingsForTest, "the tick must have made its readings");
            Check(sending.Cell("stepPreEncodeAsio") is not null && sending.Cell("stepPreEncodeWasapi") is null && sending.Cell("sendCbGapMs") is not null,
                "SENDING from the ASIO input only: its lane's readings are there, the WASAPI lane's are not");
        }
        finally
        {
            DiagnosticsGate.Enabled = diagnosticsWere;
            try { form?.Dispose(); } catch { /* teardown */ }
            CuePlayer.GloballyMuted = restoreMuted;
        }
        return "in all three configurations the outputs in use are the ones it plays on and a send lane only while it sends; "
             + "through the window's tick, not receiving left the receiving and output readings empty, receiving filled the WASAPI "
             + "output's and not the ASIO one's; sending from the ASIO input only filled its lane and not the WASAPI one; every "
             + "reading the tick set is a column of the file";
    }

    /// <summary>
    /// THE PLUGIN'S LOG AND READINGS FOLLOW THE SAME RULES: lines that start with a severity word, its readings in a file
    /// of its own with a row a minute, and every second kept around a run of late sound and around RemSound no longer
    /// answering.
    /// </summary>
    private static string? PluginLogFollowsTheRules()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-pluginlogdetail-" + Guid.NewGuid().ToString("N"));
        using var scope = AppConfig.UseThrowawayUserDataDirectory(scratch);
        AppConfig.WritePluginPointer(loggingEnabled: true);
        var start = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Local);
        var now = start;
        long late = 0;
        var connected = true;
        string? path, readings;
        using (var log = new PluginLog(Guid.NewGuid()) { NowForTest = () => now })
        {
            log.Pause();   // the gate steps the seconds itself
            var t = 0;
            log.SnapshotSource = () => new PluginLogSnapshot(
                Job: "receive", Peer: "192.0.2.50", Connected: connected, HostSampleRate: 48000, BlockFrames: 256, Resampling: false,
                BlocksOut: t, BlocksIn: t, ShortBlocks: late, RingFrames: 480 - t % 7, BytesOut: 0, BytesIn: 0, KnownPeers: 1);
            log.Event("job: receiving 192.0.2.50 onto this track");
            for (t = 0; t < 400; t++)
            {
                now = start.AddSeconds(t);
                if (t == 150) late = 2;            // sound arrived late: a run starts, the seconds around it kept
                if (t == 300) connected = false;   // RemSound stopped answering: the five minutes before it kept
                log.WriteSnapshotForTest();
            }
            path = log.Path;
            readings = log.ReadingsPath;
        }
        var lines = ReadLinesShared(Require(path, "the plugin log must have made its file"));
        var shape = new Regex(@"^(INFO|WARN|ERROR)\t\d\d:\d\d:\d\d\.\d{3}\t[^\t:]+: ");
        Check(lines.All(l => shape.IsMatch(l)), $"every line of the plugin's log starts with a severity word, the time and a category ({string.Join(" | ", lines.Take(3))})");
        Check(lines.Any(l => l.StartsWith("WARN", StringComparison.Ordinal) && l.Contains("late blocks started", StringComparison.Ordinal)), "a run of late sound says so, as a warning");
        Check(lines.Any(l => l.StartsWith("WARN", StringComparison.Ordinal) && l.Contains("RemSound stopped answering", StringComparison.Ordinal)), "and RemSound no longer answering");
        var rows = ReadLinesShared(Require(readings, "the plugin's readings file must have been made")).Skip(1).Select(CommandLine.CsvFields).ToList();
        var header = CommandLine.CsvFields(ReadLinesShared(readings!)[0]);
        var secs = rows.Where(r => r[1] == "second").Select(r => (int)(DateTime.Parse(r[0]) - start).TotalSeconds).ToList();
        Check(rows.Count(r => r[1] == "minute") >= 6, $"a row for every minute ({rows.Count(r => r[1] == "minute")})");
        Check(secs.Contains(120) && secs.Contains(150) && secs.Contains(300) && secs.Contains(330) && !secs.Contains(340) && !secs.Contains(399),
            $"every second around late sound and before RemSound stopped answering, and none after ({secs.FirstOrDefault()}..{secs.LastOrDefault()}, {secs.Count} rows)");
        var lateCol = header.IndexOf("lateBlocks");
        Check(lateCol > 0 && rows.Any(r => r[1] == "second" && r[lateCol] == "2"), "the late blocks counted in the second they arrived");
        return "the plugin's lines start with a severity word; its readings file has a row a minute and every second around late "
             + "sound and before RemSound stopped answering, none after";
    }

    /// <summary>
    /// A DISCONNECT SAYS WHY, AND THIS COMPUTER'S NETWORK CHANGING IS A LINE OF ITS OWN. Jonathan's laptop "kept
    /// disconnecting": the line says whether the peer stopped answering or left the server, and whether this computer's
    /// network changed or it woke from sleep just before. (The disconnect itself, its own capture and the connection's
    /// summary are driven in "Everybody on a server is there".)
    /// </summary>
    private static string? DisconnectsSayWhy()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        MainForm? form = null;
        try
        {
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            var lines = new List<string>();
            form.LogForTest.EventTapForTest = line => { lock (lines) lines.Add(line); };
            var peer = new PeerHealth(new IPEndPoint(IPAddress.Parse("192.0.2.7"), 47830), PeerHealthState.Unreachable, null, TimeSpan.FromSeconds(6));
            var plain = form.WhyLostForTest(peer);
            Check(plain == "it stopped answering - no reply to its pings for 6 seconds, and no sound for over 3 seconds",
                $"WHY: a peer that stopped answering, in words ({plain})");
            form.NoteNetworkChangedForTest(null);
            form.NoteNetworkChangedForTest(null);   // Windows says it more than once
            lock (lines)
            {
                Check(lines.Count(l => l.StartsWith("Network: this computer's network changed (connected now: ", StringComparison.Ordinal)) == 1,
                    $"THE NETWORK: a change is one line, saying what is connected now ({string.Join(" | ", lines)})");
            }
            var afterChange = form.WhyLostForTest(peer);
            Check(afterChange.Contains("; this computer's network changed 0 seconds before", StringComparison.Ordinal),
                $"a peer lost just after this computer's network changed says so ({afterChange})");
            form.NoteResumeForTest();
            Check(form.WhyLostForTest(peer).Contains("; this computer woke from sleep 0 seconds before", StringComparison.Ordinal),
                "and just after a wake from sleep");
            form.NoteNetworkChangedForTest(false);
            lock (lines) Check(lines.Count(l => l.StartsWith("Network: ", StringComparison.Ordinal)) == 1, "a second change within ten seconds adds no line");
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            CuePlayer.GloballyMuted = restoreMuted;
        }
        return "a lost peer's line says it stopped answering and for how long, and whether this computer's network had just changed "
             + "or it had just woken; a network change is one line saying what is connected, however many times Windows reports it";
    }

    /// <summary>
    /// PREFERENCES OPENS THE CURRENT LOG AND THE LOGS FOLDER, and Write logs now keeps the seconds before it - driven
    /// through the control channel on a headless copy, as a person meets them: the Logging tab, the buttons by name, the
    /// result read back from the log. A headless copy opens nothing, so nothing appears on the screen of the computer
    /// running the gate; what it would have opened is in its log.
    /// </summary>
    private static string? PreferencesOpensTheCurrentLogAndFolder()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-openlog-" + Guid.NewGuid().ToString("N"));
        using var scope = AppConfig.UseThrowawayUserDataDirectory(scratch);
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        MainForm? form = null;
        RemoteControlServer? server = null;
        var pipe = "RemSound.control.selftest.openlog." + Guid.NewGuid().ToString("N")[..8];
        var summary = "";
        try
        {
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            var main = form;
            var captures = new List<string>();
            main.LogForTest.CaptureTapForTest = why => { lock (captures) captures.Add(why); };
            server = new RemoteControlServer(pipe, () => main, new RemoteControlEngine(() => main));
            server.Start();
            string Ask(string command)
            {
                var reply = RemoteControl.Send(command, pipe, out var reached, connectTimeoutMs: 5000);
                Check(reached, $"the control channel did not answer \"{command}\": {reply}");
                return reply;
            }

            DriveWhilePumping(main, () =>
            {
                Check(Ask("menu Options > Preferences").Contains("\"Preferences\"", StringComparison.Ordinal), "Options, Preferences must open Preferences");
                Check(Ask("tab Logging").StartsWith("ok", StringComparison.Ordinal), "the Logging tab must be reachable by name");
                var order = Ask("taborder");
                var at = new[] { "Write logs now", "Open current log", "Open logs folder" }.Select(n => order.IndexOf("\"" + n + "\"", StringComparison.Ordinal)).ToArray();
                Check(at.All(i => i >= 0) && at[0] < at[1] && at[1] < at[2],
                    $"Tab must meet Write logs now, Open current log and Open logs folder in that order ({Head(order)})");

                // Logging off: there is no current log, and the person is told so.
                Ask("set \"Enable logs for this profile\" off");
                Ask("click \"Open current log\"");
                var told = Ask("windows");
                Check(told.Contains("Logging is off for this profile", StringComparison.Ordinal),
                    $"with logging off, Open current log must say there is no current log ({Head(told)})");
                Ask("answer OK");

                // Logging on: the file this session is writing.
                Ask("set \"Enable logs for this profile\" on");
                Ask("click \"Open current log\"");
                var current = Require(main.LogPathForTest, "with logging on, Open current log must make the session's log if it has none yet");
                var said = Ask("log 5 headless copy opens nothing");
                Check(said.Contains("the current log is " + current, StringComparison.Ordinal),
                    $"Open current log must name the file this session is writing ({Head(said)})");
                Check(File.Exists(current), "and that file must be there");

                Ask("click \"Open logs folder\"");
                said = Ask("log 5 the logs folder is");
                Check(said.Contains("the logs folder is " + AppConfig.LogsDirectory, StringComparison.Ordinal),
                    $"Open logs folder must name the logs folder ({Head(said)})");

                // Write logs now: the marker, and the minutes before it kept in full.
                Ask("click \"Write logs now\"");
                Check(Ask("log 5 user requested write logs now").Contains("user requested write logs now", StringComparison.Ordinal),
                    "Write logs now must still write its marker");
                lock (captures) Check(captures.Contains("you pressed Write logs now"), $"and keep the seconds before it ({string.Join(" | ", captures)})");
                Check(Ask("answer Close").StartsWith("ok", StringComparison.Ordinal), "Preferences closes with its own button");
            }, TimeSpan.FromSeconds(60));

            summary = "through the control channel: Tab meets Write logs now, Open current log and Open logs folder in order; with "
                    + "logging off Open current log says there is none; with it on it names the session's own file, Open logs folder "
                    + "names the folder, and a headless copy opened neither; Write logs now wrote its marker and kept the seconds before it";
        }
        finally
        {
            server?.Dispose();
            try { form?.Dispose(); } catch { /* teardown */ }
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
        }
        return summary;
    }

    /// <summary>
    /// SIMULATOR LOGS: THE LOG ON A REAL CONNECTION, GOOD OR BAD. Ed, before releasing it: "if logs are variable given
    /// quality of connection ... could that variability make logs go all over the place". Simulated sessions (SimLogs): the
    /// real receiver over modelled networks, its real audible gaps, the auto-tune running, the real log on the simulated
    /// clock. A clean network writes its minute rows and no second rows, in each configuration; a rough one, and Ed's lumpy
    /// laptop, with a peer dropping every seven minutes as well, have problems all the time - and the readings stay inside
    /// what the captures allow, in time order, every row whole, every minute summed up.
    /// </summary>
    private static string? SimulatorLogStaysWithinTheRule()
    {
        foreach (var configuration in AudioConfigurations.All)
        {
            var config = configuration switch
            {
                AudioConfiguration.AsioOnly => Simulator.SimConfig.AsioOnly,
                AudioConfiguration.Both => Simulator.SimConfig.Both,
                _ => Simulator.SimConfig.WasapiOnly,
            };
            var clean = Simulator.SimLogs.Run(Simulator.SimNetwork.Perfect, config, 300);
            var what = Simulator.SimLogs.Describe(clean);
            Check(clean.Gaps == 0 && clean.Runs == 0, $"premise: a perfect network has no gaps ({what})");
            Check(clean.SecondRows == 0 && clean.MinuteRows >= 5, $"a clean session must write its minute rows and no second rows ({what})");
            Check(clean.InTimeOrder && clean.RowsWhole && clean.MinutesComplete, $"in time order, every row whole, every minute summed up ({what})");
        }

        var said = new List<string>();
        foreach (var (network, config) in new[]
                 {
                     (Simulator.SimNetwork.Rough, Simulator.SimConfig.Both),
                     (Simulator.SimNetwork.LaptopLumps, Simulator.SimConfig.AsioOnly),
                 })
        {
            var r = Simulator.SimLogs.Run(network, config, 1200, peerDropEverySec: 420);
            var what = Simulator.SimLogs.Describe(r);
            Check(r.PeerDrops >= 2 && r.Captures >= r.PeerDrops, $"premise: every peer dropping must have its capture ({what})");
            Check(r.SecondRows > 0 && r.SecondRows <= Simulator.SimLogs.MostSecondRows(r),
                $"the second rows must be the captures' and no more ({what})");
            Check(r.InTimeOrder && r.RowsWhole && r.MinutesComplete, $"in time order, every row whole, every minute summed up ({what})");
            said.Add($"{network.Name}: {r.Gaps} gaps in {r.Runs} runs, {r.PeerDrops} drops, the log {r.LogLines} lines, the readings {r.ReadingsBytesPerHour / 1024:0} KB an hour");
        }
        return "a perfect network wrote its minute rows and no second rows in all three configurations; "
             + string.Join("; ", said) + " - each within its captures, in time order, every row whole";
    }
}
