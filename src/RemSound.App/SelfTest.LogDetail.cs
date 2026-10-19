using System.Text.RegularExpressions;
using System.Windows.Forms;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Logs a person can read (2026-10-19, Jonathan Schuster's issue 35, "Make logging less verbose": a 24-million-character
/// file after seven hours, "No idea how a human, nor any AI agent is supposed to debug this"). The lines made every second
/// go out once a minute and in full around a problem; the files are named by when they started; and Preferences opens the
/// current log and the logs folder.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>The seconds whose lines reached the file, in the order they are in it, read from the "sec=NNNNN" each test
    /// line carries.</summary>
    private static List<int> SecondsInFile(string text) =>
        Regex.Matches(text, @"sec=(\d{5})").Select(m => int.Parse(m.Groups[1].Value)).ToList();

    private static string ReadLogShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    /// <summary>
    /// THE LINES MADE EVERY SECOND: ONE SECOND'S WORTH A MINUTE, AND EVERY SECOND AROUND A PROBLEM.
    ///
    /// <para>Ed's desktop wrote 35.7 MB in under five hours on 2026-10-19, 99.9% of it four lines made every second. Driven
    /// through the real log on a stepped clock and read back as BYTES ON DISK: which seconds reached the file, against the
    /// rule worked out by hand. A quiet hour, a problem (the 30 seconds before it and the 30 after), problems inside the
    /// ten-minute gap (left out, then counted by the next), Write logs now (inside the gap, because the person asked), and
    /// logging switched off with lines waiting (none of them may reach the file afterwards).</para>
    ///
    /// <para>Not per-configuration: the rule is about when a line is written, and nothing in it differs between WASAPI
    /// only, ASIO only and both. Which configuration a problem came from is in the problem's own words (the audible-gap
    /// step below).</para>
    /// </summary>
    private static string? LogLinesMadeEverySecondFollowTheRule()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-logdetail-" + Guid.NewGuid().ToString("N"));
        using var scope = AppConfig.UseThrowawayUserDataDirectory(scratch);
        var start = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        var now = start;

        // --- A quiet hour: one second's worth a minute, nothing else ---------------------------------------------------
        using (var quiet = new RemSoundLog { Enabled = true, UtcNowForTest = () => now })
        {
            for (var t = 0; t <= 3600; t++)
            {
                now = start.AddSeconds(t);
                quiet.Detail($"diag sec={t:D5}");
                quiet.Detail($"streams sec={t:D5}");
            }
            var text = ReadLogShared(Require(quiet.Path, "an enabled log must have made its file"));
            var seconds = SecondsInFile(text).Distinct().ToList();
            var expected = Enumerable.Range(0, 61).Select(m => m * 60).ToList();
            Check(seconds.SequenceEqual(expected),
                $"a quiet hour must write one second's lines a minute - 61 seconds of 3,601 - and no others (wrote {seconds.Count}: "
                + $"{string.Join(", ", seconds.Take(12))}...)");
            Check(SecondsInFile(text).Count == 122, $"and both of that second's lines, once each ({SecondsInFile(text).Count} lines)");
        }

        // --- Problems ------------------------------------------------------------------------------------------------
        now = start;
        var path = "";
        using (var log = new RemSoundLog { Enabled = true, UtcNowForTest = () => now })
        {
            void Second(int t)
            {
                now = start.AddSeconds(t);
                log.Detail($"diag sec={t:D5}");
                log.Snapshot(true, true, true, "Opus", 10, 10, 10, t, t, $"sec={t:D5}", t, t, 0, 0, "out", "ok", 0, 0);
            }
            void Trouble(int t, string why, bool asked = false) { now = start.AddSeconds(t); log.Trouble(why, asked); }

            for (var t = 0; t < 300; t++) Second(t);
            Trouble(300, "test gap one");                  // the 30 s before, then 300..329 as they come
            for (var t = 300; t < 1000; t++)
            {
                if (t == 500) Trouble(500, "inside the gap"); // left out
                if (t == 650) Trouble(650, "inside the gap"); // left out
                if (t == 950) Trouble(950, "test gap two");   // ten minutes on: written, saying two were left out
                Second(t);
            }
            Trouble(1000, "you pressed Write logs now", asked: true);  // inside the gap, but asked for
            for (var t = 1000; t < 1100; t++) Second(t);
            path = Require(log.Path, "the log must have made its file");

            // --- Switched off with lines waiting: none of them may reach the file ----------------------------------
            var before = new FileInfo(path).Length;
            for (var t = 1100; t < 1150; t++) Second(t);     // 1120 is a minute line; 1121..1149 wait in memory
            log.Enabled = false;
            for (var t = 1150; t < 1160; t++) Second(t);
            Trouble(1160, "while off");
            log.Enabled = true;
            Trouble(1900, "after switching back on", asked: true);
            Second(1900);
        }

        var all = ReadLogShared(path);
        var order = SecondsInFile(all);
        var written = order.Distinct().ToList();
        var want = new List<int>();
        want.AddRange(new[] { 0, 60, 120, 180, 240 });                            // the minute lines
        want.AddRange(Enumerable.Range(270, 30));                                 // the 30 s before the first problem
        want.AddRange(Enumerable.Range(300, 30));                                 // and the 30 after
        want.AddRange(Enumerable.Range(6, 10).Select(m => m * 60));               // 360..900, a minute apart
        want.AddRange(Enumerable.Range(920, 30));                                 // the 30 s before the second
        want.AddRange(Enumerable.Range(950, 30));                                 // and after
        want.AddRange(Enumerable.Range(980, 20));                                 // Write logs now: all since the last line...
        want.AddRange(Enumerable.Range(1000, 30));                                // ...and the 30 after
        want.AddRange(new[] { 1060, 1120 });                                      // minute lines again
        want.Add(1900);                                                           // back on: only what came after
        Check(written.SequenceEqual(want),
            "THE RULE: the file must hold exactly the minute lines and the seconds around each written problem. "
            + $"Missing: {string.Join(", ", want.Except(written).Take(15))}; extra: {string.Join(", ", written.Except(want).Take(15))}");
        Check(order.Zip(order.Skip(1)).All(p => p.First <= p.Second),
            "THE FILE IN TIME ORDER: a burst writes only what came after the last line in the file, never an older second");
        Check(order.Count == written.Count * 2, $"each second's two lines once each ({order.Count} lines for {written.Count} seconds)");

        var headings = all.Split('\n').Where(l => l.Contains("\tdetail: ", StringComparison.Ordinal)).ToList();
        Check(headings.Count == 4, $"one heading for each problem written in full, and none for those left out ({headings.Count}: {string.Join(" | ", headings.Select(h => h.Trim()))})");
        Check(headings.Count == 4 && headings[0].Contains("test gap one", StringComparison.Ordinal) && headings[0].Contains("from the 30 seconds before", StringComparison.Ordinal),
            "the first heading must say what went wrong and how far back the lines go");
        Check(headings.Count == 4 && headings[1].Contains("test gap two", StringComparison.Ordinal) && headings[1].Contains("2 more problems since the last time", StringComparison.Ordinal),
            "THE COUNT: the next burst after the gap must say how many problems in between were not written in full");
        Check(headings.Count == 4 && headings[2].Contains("you pressed Write logs now", StringComparison.Ordinal),
            "WRITE LOGS NOW: asked for inside the gap, it must still write");
        Check(!all.Contains("while off", StringComparison.Ordinal), "a problem while logging is off must write nothing, not even its heading");
        Check(order.All(s => s < 1121 || s > 1159),
            "SWITCHED OFF MEANS OFF: lines waiting in memory when logging went off must never reach the file afterwards");

        return "a quiet hour wrote 61 of 3,601 seconds, a minute apart; each problem wrote the 30 seconds before and the 30 after, "
             + "in time order; two problems inside the ten-minute gap were left out and counted by the next; Write logs now wrote "
             + "inside the gap; lines waiting when logging went off never reached the file";
    }

    /// <summary>
    /// LOG FILES NAMED BY WHEN THEY STARTED. Jonathan: "a bunch of files with random IDs, the date is at the very end".
    /// The app's and the plugin's files lead with the date and time, then the computer; a second copy in the same minute,
    /// and a session's later parts, get names of their own and never write into another file.
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
                // The first copy in a minute has no process number; another in the same minute carries it (a minute
                // turning over between them is allowed for).
                Check(!matches[0].Groups[3].Success, $"the first copy must not carry a process number ({names[0]})");
                for (var i = 1; i < 3; i++)
                    if (matches[i].Groups[1].Value == matches[0].Groups[1].Value)
                        Check(matches[i].Groups[3].Value == pid.ToString(), $"another copy in the same minute must carry its process number ({names[i]})");
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

        return "app logs are named RemSound-date-time-computer, three copies in a minute made three files, a session's next file "
             + "is its part 2 with the same start; plugin logs are RemSoundPlugin-date-time-computer-instance; a name already taken "
             + "is passed over";
    }

    /// <summary>
    /// THE PLUGIN'S LINE MADE EVERY SECOND FOLLOWS THE SAME RULE: one a minute, and the 30 seconds either side when sound
    /// arrives late on the track or RemSound stops answering.
    /// </summary>
    private static string? PluginLogLinesFollowTheRule()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-pluginlogdetail-" + Guid.NewGuid().ToString("N"));
        using var scope = AppConfig.UseThrowawayUserDataDirectory(scratch);
        AppConfig.WritePluginPointer(loggingEnabled: true);
        var start = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        var now = start;
        long late = 0;
        var connected = true;
        string? path;
        using (var log = new PluginLog(Guid.NewGuid()) { UtcNowForTest = () => now })
        {
            log.Pause();   // the gate steps the seconds itself
            var t = 0;
            log.SnapshotSource = () => new PluginLogSnapshot(
                Job: $"sec={t:D5}", Peer: "192.168.1.50", Connected: connected, HostSampleRate: 48000, BlockFrames: 256, Resampling: false,
                BlocksOut: t, BlocksIn: t, ShortBlocks: late, RingFrames: 480, BytesOut: 0, BytesIn: 0, KnownPeers: 1);
            for (t = 0; t < 400; t++)
            {
                now = start.AddSeconds(t);
                if (t == 150) late = 2;            // sound arrived late: written in full
                if (t == 300) connected = false;   // inside the ten minutes after: left out
                log.WriteSnapshotForTest();
            }
            path = log.Path;
        }
        var text = ReadLogShared(Require(path, "the plugin log must have made its file"));
        var written = SecondsInFile(text);
        var want = new[] { 0, 60, 120 }.Concat(Enumerable.Range(121, 29)).Concat(Enumerable.Range(150, 30))
            .Concat(new[] { 210, 270, 330, 390 }).ToList();
        Check(written.SequenceEqual(want),
            $"the plugin's line must be written a minute apart and in full around late sound. Missing: {string.Join(", ", want.Except(written).Take(15))}; "
            + $"extra: {string.Join(", ", written.Except(want).Take(15))}");
        Check(text.Contains("detail: 2 blocks of sound arrived late", StringComparison.Ordinal), "the heading must say what went wrong on the track");
        Check(!text.Contains("detail: RemSound stopped answering", StringComparison.Ordinal),
            "a second problem inside the ten minutes must not write again");
        return "the plugin's line went out a minute apart, and in full for the 30 seconds either side of late sound; a second problem "
             + "inside ten minutes did not write again";
    }

    /// <summary>
    /// A PROBLEM IN THE WINDOW REACHES THE LOG: a new audible gap on any output is a problem the log writes in full, with
    /// the configuration in its words. The count is every stream's together (the receiver's DropoutGaps), whichever output
    /// plays it, so the configuration changes only the words.
    /// </summary>
    private static string? AudibleGapWritesTheSecondsAroundIt()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        MainForm? form = null;
        try
        {
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            var troubles = new List<string>();
            form.LogForTest.TroubleTapForTest = why => { lock (troubles) troubles.Add(why); };
            long gaps = 0;
            form.AudibleGapsForTest = () => gaps;

            form.NoteAudibleGapsForTheLogForTest();
            Check(troubles.Count == 0, "the first look only learns the count so far; it is not a problem");
            gaps = 3;
            form.NoteAudibleGapsForTheLogForTest();
            Check(troubles.Count == 1 && troubles[0].StartsWith("3 audible gaps [", StringComparison.Ordinal),
                $"three new gaps must be a problem the log writes in full, with the configuration ({string.Join(" | ", troubles)})");
            form.NoteAudibleGapsForTheLogForTest();
            Check(troubles.Count == 1, "no new gap, no problem");
            gaps = 1;   // a stream ended and took its count with it
            form.NoteAudibleGapsForTheLogForTest();
            Check(troubles.Count == 1, "a count that falls is not a gap");
            gaps = 2;
            form.NoteAudibleGapsForTheLogForTest();
            Check(troubles.Count == 2 && troubles[1].StartsWith("1 audible gap [", StringComparison.Ordinal),
                $"one new gap after the fall is one gap ({string.Join(" | ", troubles)})");

            // Which configuration it was is in the words: the one the window is in.
            Check(troubles[^1].EndsWith($"[{form.ActiveAudioConfigurationForTest().Describe()}]", StringComparison.Ordinal),
                $"the problem must name the configuration it happened in ({troubles[^1]})");
            return "a new audible gap is a problem the log writes in full, naming the configuration; a count standing still or "
                 + "falling is not";
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            CuePlayer.GloballyMuted = restoreMuted;
        }
    }

    /// <summary>
    /// PREFERENCES OPENS THE CURRENT LOG AND THE LOGS FOLDER, and Write logs now writes the seconds before it - driven
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
            var troubles = new List<string>();
            main.LogForTest.TroubleTapForTest = why => { lock (troubles) troubles.Add(why); };
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

                // Write logs now: the marker, and the seconds before it in full.
                Ask("click \"Write logs now\"");
                Check(Ask("log 5 user requested write logs now").Contains("user requested write logs now", StringComparison.Ordinal),
                    "Write logs now must still write its marker");
                lock (troubles) Check(troubles.Contains("you pressed Write logs now"), $"and write the seconds before it in full ({string.Join(" | ", troubles)})");
                Check(Ask("answer Close").StartsWith("ok", StringComparison.Ordinal), "Preferences closes with its own button");
            }, TimeSpan.FromSeconds(60));

            summary = "through the control channel: Tab meets Write logs now, Open current log and Open logs folder in order; with "
                    + "logging off Open current log says there is none; with it on it names the session's own file, Open logs folder "
                    + "names the folder, and a headless copy opened neither; Write logs now wrote its marker and the seconds before it";
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
    /// quality of connection ... could that variability make logs go all over the place". Simulated sessions (SimLogs):
    /// the real receiver over modelled networks, its real audible gaps, the auto-tune running, the real log on the
    /// simulated clock. A clean network writes its minute lines and nothing more, in each configuration; a rough one, and
    /// Ed's lumpy laptop, with a peer dropping every seven minutes as well, have problems all the time - and the log stays
    /// inside the rule's bounds (a burst at most every ten minutes), in time order, every second written whole.
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
            Check(clean.Problems == 0, $"premise: a perfect network has no problems ({Simulator.SimLogs.Describe(clean)})");
            Check(clean.Bursts == 0 && clean.SecondsWritten == 5,
                $"a clean session must write one second's lines a minute and nothing else ({Simulator.SimLogs.Describe(clean)})");
            Check(clean.InTimeOrder && clean.EverySecondWhole, $"in time order, every second whole ({Simulator.SimLogs.Describe(clean)})");
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
            Check(r.Problems >= 3, $"premise: a bad connection with a peer dropping must have problems ({what})");
            Check(r.Bursts >= 2 && r.Bursts <= Simulator.SimLogs.MostBurstsIn(r.Hours),
                $"problems all the time must still mean a burst at most every ten minutes ({what})");
            Check(r.SecondsWritten <= Simulator.SimLogs.MostSecondsIn(r.Hours, r.Bursts),
                $"and no more seconds than the minute lines and the bursts' own ({what})");
            Check(r.InTimeOrder && r.EverySecondWhole, $"in time order, every second written whole ({what})");
            said.Add($"{network.Name}: {r.Problems} problems, {r.Bursts} bursts, {r.BytesPerHour / 1024:0} KB an hour");
        }
        return "a perfect network wrote its minute lines and nothing else in all three configurations; "
             + string.Join("; ", said) + " - each within the rule's bounds, in time order, every second whole";
    }
}
