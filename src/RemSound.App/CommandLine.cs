using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RemSound.Core;
using RemSound.Receiver;
using RemSound.Sender;

namespace RemSound.App;

/// <summary>
/// Command-line interface for RemSound. RemSound is primarily a GUI app, but - inspired by Andre's
/// Sensor Readout - it also accepts command-line options so a screen-reader user (or a script) can
/// list devices, run an audio self-test, dump a diagnostics bundle, toggle logging, close a running
/// copy, or launch straight into a profile / connection without touching the UI.
///
/// Two kinds of option:
///   * "do-and-exit" commands (--help, --version, --devices, --selftest, --diagnostics, --log,
///     --close) print to the calling terminal (and/or a file) and terminate the process.
///   * "launch options" (--profile, --connect, --minimized) modify a normal GUI start.
///
/// Wired into <see cref="Program"/> right after the legacy-layout migration and before the
/// single-instance guard. The do-and-exit commands need no window and no instance lock.
/// </summary>
internal static class CommandLine
{
    /// <summary>Overrides applied to a normal GUI launch when no do-and-exit command ran.</summary>
    internal sealed class LaunchOverrides
    {
        public bool StartMinimized;
        public string? ProfileName;
        public readonly List<IPEndPoint> ConnectPeers = new();
        /// <summary>--connect given with no --profile → start on a blank profile and connect.</summary>
        public bool ForceBlankProfile;
    }

    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int dwProcessId);
    private const int ATTACH_PARENT_PROCESS = -1;

    /// <summary>The folder given after <c>--config-dir</c>, or null. Read at the very start of
    /// <see cref="Program"/> - before the layout migration and any config/profile/log/sound access -
    /// so it can redirect ALL user state via <see cref="AppConfig.SetUserDataDirectoryOverride"/>.
    /// Applies to every command (e.g. <c>--selftest --config-dir</c>, <c>--diagnostics --config-dir</c>)
    /// and to a normal GUI launch, so a test can exercise a real build without touching live settings.</summary>
    public static bool TryGetConfigDir(string[] args, out string dir)
    {
        dir = "";
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--config-dir", StringComparison.OrdinalIgnoreCase) && !args[i + 1].StartsWith('-'))
            {
                dir = args[i + 1];
                return !string.IsNullOrWhiteSpace(dir);
            }
        }
        return false;
    }

    /// <summary>
    /// Process the command line. Returns a non-null exit code when a do-and-exit command ran (the
    /// caller should <c>Environment.Exit</c> it); returns null to continue into the GUI launch with
    /// <paramref name="overrides"/> populated.
    /// </summary>
    public static int? Process(string[] args, out LaunchOverrides overrides)
    {
        overrides = new LaunchOverrides();
        if (args.Length == 0) return null;

        // --- do-and-exit commands (first match wins) ---
        foreach (var raw in args)
        {
            switch (raw.ToLowerInvariant())
            {
                case "--help": case "-h": case "/?": case "-?": case "--?":
                    return WithConsole(PrintHelp);
                case "--version": case "-v": case "--ver":
                    return WithConsole(PrintVersion);
                case "--devices": case "--list-devices":
                    return WithConsole(() => { WriteDevices(Console.Out); return 0; });
                case "--selftest": case "--self-test": case "--smoke-test": case "--smoketest":
                    return WithConsole(() => SelfTest.Run(args));
                case "--perftest": case "--perf-test":
                    return WithConsole(() => RunPerfTest(args));
                case "--diagnostics": case "--diag":
                    return WithConsole(() => RunDiagnostics(ValueAfter(args, raw)));
                case "--log":
                    return WithConsole(() => SetLogging(ValueAfter(args, raw)));
                case "--close": case "--quit":
                    return WithConsole(CloseRunning);
            }
        }

        // --- launch options (applied to the normal GUI start) ---
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--minimized": case "--minimised": case "--tray":
                    overrides.StartMinimized = true;
                    break;
                case "--profile":
                    overrides.ProfileName = ValueAt(args, i + 1);
                    break;
                case "--connect":
                    if (TryParsePeer(ValueAt(args, i + 1), out var ep)) overrides.ConnectPeers.Add(ep);
                    break;
            }
        }
        overrides.ForceBlankProfile = overrides.ConnectPeers.Count > 0 && overrides.ProfileName is null;
        return null;
    }

    // ---------------- console plumbing ----------------

    /// <summary>Attach to the calling terminal (when launched from one), point Console.Out at the
    /// real stdout handle (works for an interactive console AND a redirected pipe), run the command,
    /// and return its exit code. A WinExe has no console of its own, hence the attach dance.</summary>
    private static int WithConsole(Func<int> body)
    {
        try { AttachConsole(ATTACH_PARENT_PROCESS); } catch { /* no parent console - fine */ }
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* redirected / no console */ }
        try { Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true }); }
        catch { /* no usable stdout - file-writing commands still work */ }
        try { return body(); }
        catch (Exception ex) { Console.WriteLine($"RemSound: error - {ex.GetType().Name}: {ex.Message}"); return 1; }
    }

    // ---------------- commands ----------------

    internal static string AppVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private static int PrintVersion()
    {
        Console.WriteLine($"RemSound {AppVersion}");
        return 0;
    }

    private static int PrintHelp()
    {
        Console.WriteLine($"RemSound {AppVersion} - command-line options");
        Console.WriteLine();
        Console.WriteLine("Run RemSound.exe with no options to start normally.");
        Console.WriteLine();
        Console.WriteLine("Information and tests (these print, then exit):");
        Console.WriteLine("  --help, -h            Show this help.");
        Console.WriteLine("  --version             Show the installed version.");
        Console.WriteLine("  --devices             List all microphones, outputs and ASIO drivers,");
        Console.WriteLine("                        with their formats and device ids.");
        Console.WriteLine("  --selftest [--seconds N]  Run the built-in self-test - a localhost audio");
        Console.WriteLine("  (or --smoke-test)     round-trip plus checks of encryption, the wire format,");
        Console.WriteLine("                        settings, profiles, dialog accessibility and bundled files.");
        Console.WriteLine("  --perftest [--seconds N]  Run repeated audio cycles and report whether handle,");
        Console.WriteLine("                        memory and thread counts stay bounded (leak sanity check).");
        Console.WriteLine("  --diagnostics [path]  Write a diagnostics report (version, config, profiles,");
        Console.WriteLine("                        devices, mic-privacy check, recent log) and exit. With");
        Console.WriteLine("                        no path, it is saved in the user settings and logs folder.");
        Console.WriteLine();
        Console.WriteLine("Settings and control (these act, then exit):");
        Console.WriteLine("  --log on|off          Turn the diagnostic log on or off.");
        Console.WriteLine("  --close               Close a running copy of RemSound.");
        Console.WriteLine();
        Console.WriteLine("Start-up options (these change how RemSound launches):");
        Console.WriteLine("  --profile \"<name>\"    Start straight into the named profile (skip the picker).");
        Console.WriteLine("  --connect <ip[:port]> Start and connect to a peer at this address. With no");
        Console.WriteLine("                        --profile, starts on a fresh profile connected to it.");
        Console.WriteLine("  --minimized, --tray   Start minimized to the notification area.");
        Console.WriteLine("  --config-dir <folder> Use an explicit folder for this run's settings, profiles,");
        Console.WriteLine("                        logs and sounds, instead of the usual location. Lets a test");
        Console.WriteLine("                        exercise RemSound without touching your real settings.");
        Console.WriteLine("                        Works with any command (e.g. --selftest --config-dir ...).");
        Console.WriteLine("  --silent              Play no cue sounds and show no missing-sound pop-ups for");
        Console.WriteLine("                        this run - for automated / unattended launches.");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  RemSound.exe --devices");
        Console.WriteLine("  RemSound.exe --selftest --opus");
        Console.WriteLine("  RemSound.exe --diagnostics");
        Console.WriteLine("  RemSound.exe --profile \"Studio\" --minimized");
        Console.WriteLine("  RemSound.exe --connect 192.168.1.42");
        return 0;
    }

    /// <summary>Write the full device inventory (capture inputs, render outputs, ASIO drivers) to a
    /// writer - shared by <c>--devices</c> and the diagnostics report. Formats come straight from
    /// each endpoint's mix format, the same value the audio engine negotiates.</summary>
    private static void WriteDevices(TextWriter w)
    {
        using var en = new MMDeviceEnumerator();

        void ListEndpoints(DataFlow flow, string header)
        {
            w.WriteLine(header);
            string? defaultId = null;
            try { using var def = en.GetDefaultAudioEndpoint(flow, Role.Multimedia); defaultId = def.ID; }
            catch { /* no default of this kind */ }
            var any = false;
            foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                any = true;
                var isDefault = defaultId is not null && d.ID == defaultId ? "  [default]" : "";
                string fmt;
                try
                {
                    var f = d.AudioClient.MixFormat;
                    var enc = f.Encoding is WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible
                        ? "float" : f.Encoding.ToString();
                    fmt = $"{f.SampleRate} Hz, {f.Channels} ch, {f.BitsPerSample}-bit {enc}";
                }
                catch { fmt = "format unavailable"; }
                w.WriteLine($"  {d.FriendlyName}{isDefault}");
                w.WriteLine($"      {fmt}");
                w.WriteLine($"      id: {d.ID}");
                d.Dispose();
            }
            if (!any) w.WriteLine("  (none)");
            w.WriteLine();
        }

        ListEndpoints(DataFlow.Capture, "Microphones / line-in (WASAPI capture inputs):");
        ListEndpoints(DataFlow.Render, "Speakers / headphones (WASAPI outputs; also capturable as system-audio loopback):");

        w.WriteLine("ASIO drivers:");
        IReadOnlyList<string> asio;
        try { asio = AsioDeviceProbe.EnumerateDriverNames(); } catch { asio = Array.Empty<string>(); }
        if (asio.Count == 0) w.WriteLine("  (none installed)");
        else foreach (var n in asio) w.WriteLine($"  {n}");
        w.WriteLine();
    }

    /// <summary>Resource-sanity check: run several short audio-loopback cycles and watch this
    /// process's handle / memory / thread counts. Each cycle builds and tears down the audio
    /// engine, so a handle or thread count that ratchets up cycle on cycle is the fingerprint of a
    /// leak (RemSound has a handle-leak history). Lenient thresholds - it flags obvious runaway, not
    /// normal fluctuation - and logs the numbers so builds can be compared. Note: the loopback
    /// renders to nothing (no real output device, to stay silent), so it exercises the
    /// capture/encode/network/decode path, not the WASAPI render path.</summary>
    private static int RunPerfTest(string[] args)
    {
        var seconds = int.TryParse(ValueAfter(args, "--seconds"), out var s) && s is >= 6 and <= 120 ? s : 15;
        const int cycles = 3;
        var perCycle = Math.Max(2, seconds / cycles);
        var proc = System.Diagnostics.Process.GetCurrentProcess();

        Console.WriteLine($"RemSound perf sanity: {cycles} x {perCycle}s audio loopback, watching handles/memory/threads...");
        Console.WriteLine();

        static (int Handles, long WorkingSetMb, long PrivateMb, int Threads) Measure(System.Diagnostics.Process p)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            p.Refresh();
            return (p.HandleCount, p.WorkingSet64 / (1024 * 1024), p.PrivateMemorySize64 / (1024 * 1024), p.Threads.Count);
        }

        var baseline = Measure(proc);
        Console.WriteLine($"  baseline:  handles={baseline.Handles}  workingSet={baseline.WorkingSetMb}MB  private={baseline.PrivateMb}MB  threads={baseline.Threads}");

        for (var i = 1; i <= cycles; i++)
        {
            var r = AudioLoopback.Run(opus: true, perCycle);
            if (!r.Ran)
            {
                Console.WriteLine($"  RESULT: SKIP - {r.SkipReason} (no audio device to exercise).");
                return 0;
            }
            var m = Measure(proc);
            Console.WriteLine($"  cycle {i}/{cycles}: handles={m.Handles}  workingSet={m.WorkingSetMb}MB  private={m.PrivateMb}MB  threads={m.Threads}  (sent={r.PacketsSent}, received={r.PacketsReceived})");
        }

        var final = Measure(proc);
        var handleGrowth = final.Handles - baseline.Handles;
        var threadGrowth = final.Threads - baseline.Threads;
        var memGrowth = final.WorkingSetMb - baseline.WorkingSetMb;
        Console.WriteLine();
        Console.WriteLine($"  net change over {cycles} cycles: handles {handleGrowth:+#;-#;0}, threads {threadGrowth:+#;-#;0}, workingSet {memGrowth:+#;-#;0}MB");

        // Lenient: flag obvious runaway (e.g. a handle leak ratcheting up each cycle), not noise.
        var runaway = handleGrowth > 1500 || threadGrowth > 100 || final.WorkingSetMb > 1500;
        Console.WriteLine(runaway
            ? "  RESULT: FAIL - resource use looks like it is running away (possible leak); compare the per-cycle trend above."
            : "  RESULT: PASS - handles, threads and memory stayed bounded across cycles.");
        return runaway ? 1 : 0;
    }

    private static int SetLogging(string? value)
    {
        var on = value is not null && value.ToLowerInvariant() is "on" or "true" or "1" or "enable" or "enabled" or "yes";
        var off = value is not null && value.ToLowerInvariant() is "off" or "false" or "0" or "disable" or "disabled" or "no";
        if (!on && !off)
        {
            Console.WriteLine("Usage: --log on   (or)   --log off");
            return 1;
        }
        var cfg = AppConfig.Load();
        cfg.LoggingEnabled = on;
        try { cfg.Save(); }
        catch (Exception ex) { Console.WriteLine($"Could not save the setting: {ex.Message}"); return 1; }
        Console.WriteLine($"RemSound logging is now {(on ? "ON" : "OFF")}. The change takes effect next time RemSound starts.");
        return 0;
    }

    private static int CloseRunning()
    {
        bool closed;
        try { closed = SingleInstanceCoordinator.ForceCloseOtherInstances(); }
        catch (Exception ex) { Console.WriteLine($"Could not close RemSound: {ex.Message}"); return 1; }
        Console.WriteLine(closed
            ? "Closed the running copy of RemSound."
            : "No running copy of RemSound was found (or it could not be closed - it may be running as administrator).");
        return 0;
    }

    /// <summary>Build the support diagnostics report text for a given config (version, settings,
    /// profiles, devices, mic-privacy, recent log). Shared by <c>--diagnostics</c> and the
    /// self-test's privacy check. Lists profile titles only - never their contents.</summary>
    internal static string BuildDiagnosticsReport(AppConfig cfg, bool runLiveAudioProbe = true)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"RemSound diagnostics");
        sb.AppendLine($"Generated:  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Version:    {AppVersion}");
        sb.AppendLine($"Machine:    {Environment.MachineName}");
        sb.AppendLine($"OS:         {Environment.OSVersion}");
        sb.AppendLine($".NET:       {Environment.Version}");
        sb.AppendLine($"Exe:        {Environment.ProcessPath}");
        sb.AppendLine();

        sb.AppendLine("Settings:");
        sb.AppendLine($"  Logging enabled:        {cfg.LoggingEnabled}");
        sb.AppendLine($"  Start minimised:        {cfg.StartMinimised}");
        sb.AppendLine($"  Start with profile:     {cfg.StartWithProfileTitle ?? "(picker)"}");
        sb.AppendLine($"  Profiles folder:        {cfg.ProfilesDirectory ?? AppConfig.ProfilesBaseDirectory}");
        sb.AppendLine($"  Update check frequency: {cfg.UpdateCheckFrequency}");
        sb.AppendLine($"  Startup cue enabled:    {cfg.EnableStartupCue}");
        sb.AppendLine();

        sb.AppendLine("Profiles:");
        try
        {
            var store = cfg.CreateStore();
            var titles = store.ListProfileTitles();
            if (titles.Count == 0) sb.AppendLine("  (none)");
            else foreach (var t in titles) sb.AppendLine($"  {t}{(store.IsProfileReadOnly(t) ? "  (read-only)" : "")}");
        }
        catch (Exception ex) { sb.AppendLine($"  (could not list profiles: {ex.Message})"); }
        sb.AppendLine();

        try { using var sw = new StringWriter(sb); WriteDevices(sw); }
        catch (Exception ex) { sb.AppendLine($"Devices: (could not enumerate: {ex.Message})"); sb.AppendLine(); }

        sb.AppendLine("Microphone privacy (Windows):");
        sb.AppendLine($"  {DescribeMicPrivacy()}");
        sb.AppendLine();

        if (runLiveAudioProbe)
        {
            sb.AppendLine("Live audio self-check (localhost loopback, no sound output):");
            AppendLiveAudioCheck(sb);
            sb.AppendLine();
        }

        sb.AppendLine("Most recent session snapshot (from the log):");
        sb.AppendLine($"  {LastSessionSnapshot()}");
        sb.AppendLine();

        sb.AppendLine("Recent warnings and errors (from the log):");
        sb.AppendLine(RecentLogProblems(15));
        sb.AppendLine();

        sb.AppendLine("Most recent log (tail):");
        sb.AppendLine(TailNewestLog(40));
        sb.AppendLine();

        return sb.ToString();
    }

    /// <summary>Run a short localhost loopback for each codec and report the live counters - proves
    /// this machine's capture/encode/network/decode path works, with real packet/underrun/drop
    /// numbers. Skips (no failure) when there's no audio device.</summary>
    private static void AppendLiveAudioCheck(StringBuilder sb)
    {
        foreach (var opus in new[] { false, true })
        {
            var r = AudioLoopback.Run(opus, 2);
            if (!r.Ran) { sb.AppendLine($"  {r.Codec}: skipped ({r.SkipReason})"); continue; }
            sb.AppendLine($"  {r.Codec}: sent={r.PacketsSent} pkts, received={r.PacketsReceived} pkts ({r.BytesReceived} bytes), "
                        + $"underruns={r.Underruns}, drops={r.Drops}, buffer={r.BufferMs}ms, target latency={r.TargetLatencyMs}ms "
                        + $"-> {(r.Flowed ? "OK" : "NO AUDIO")}");
        }
    }

    /// <summary>The last one-second SNAP row from the newest log, as a readable line: the real
    /// running session's codec, send/receive state, buffer, underruns, drops and heartbeat. These
    /// are the live values a static report can't otherwise show. Empty when logging is off.</summary>
    private static string LastSessionSnapshot()
    {
        try
        {
            var dir = AppConfig.LogsDirectory;
            if (!Directory.Exists(dir)) return "(no logs - logging may be off)";
            var newest = new DirectoryInfo(dir).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (newest is null) return "(no log files - logging may be off)";
            var lines = File.ReadAllLines(newest.FullName);
            var header = lines.FirstOrDefault(l => l.StartsWith("Kind\t") || l.Contains("\tCodec\t"));
            var lastSnap = lines.LastOrDefault(l => l.StartsWith("SNAP\t"));
            if (header is null || lastSnap is null) return "(no session snapshots in the newest log yet)";
            var cols = header.Split('\t');
            var vals = lastSnap.Split('\t');
            string Col(string name) { var i = Array.IndexOf(cols, name); return i >= 0 && i < vals.Length ? vals[i] : "?"; }
            return $"Connected={Col("Connected")}, Send={Col("SendRunning")}, Receive={Col("ReceiveRunning")}, "
                 + $"Codec={Col("Codec")}, Buffer={Col("BufferMs")}ms, Underruns={Col("Underruns")}, "
                 + $"Drops={Col("Drops")}, Heartbeat={Col("Heartbeat")}, TargetLatency={Col("TargetLatencyMs")}ms";
        }
        catch (Exception ex) { return $"(could not read snapshot: {ex.Message})"; }
    }

    /// <summary>The recent WARN/ERROR/exception-style lines from the newest log, so a support
    /// reader sees the problems without scrolling the whole file. Last <paramref name="max"/>.</summary>
    private static string RecentLogProblems(int max)
    {
        try
        {
            var dir = AppConfig.LogsDirectory;
            if (!Directory.Exists(dir)) return "  (no logs - logging may be off)";
            var newest = new DirectoryInfo(dir).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (newest is null) return "  (no log files - logging may be off)";
            var problems = File.ReadLines(newest.FullName)
                .Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)
                         || l.Contains("warn", StringComparison.OrdinalIgnoreCase)
                         || l.Contains("exception", StringComparison.OrdinalIgnoreCase)
                         || l.Contains("unreachable", StringComparison.OrdinalIgnoreCase)
                         || l.Contains("failed", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (problems.Count == 0) return "  (none in the newest log)";
            var tail = problems.Count <= max ? problems : problems.GetRange(problems.Count - max, max);
            return string.Join(Environment.NewLine, tail.Select(l => "  " + l));
        }
        catch (Exception ex) { return $"  (could not scan log: {ex.Message})"; }
    }

    /// <summary>Write a support-friendly diagnostics report and exit. Always writes a file so it
    /// works even when launched without a terminal; prints the path if a console is attached.</summary>
    private static int RunDiagnostics(string? pathArg)
    {
        AppConfig cfg;
        try { cfg = AppConfig.Load(); } catch { cfg = new AppConfig(); }
        var report = BuildDiagnosticsReport(cfg);

        var path = !string.IsNullOrWhiteSpace(pathArg)
            ? pathArg!
            : Path.Combine(AppConfig.UserDataDirectory,
                $"RemSound-diagnostics-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, report);
            Console.WriteLine("Diagnostics written to:");
            Console.WriteLine($"  {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not write the diagnostics file: {ex.Message}");
            Console.WriteLine();
            Console.Write(report); // last resort - dump to the terminal
            return 1;
        }
        return 0;
    }

    private static string DescribeMicPrivacy()
    {
        try
        {
            const string consent = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
            var perUser = Registry.GetValue(consent, "Value", null) as string;
            var policy = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsAccessMicrophone", null);
            var bits = new List<string> { $"user mic access = {perUser ?? "Allow (default)"}" };
            if (policy is int p) bits.Add($"group-policy LetAppsAccessMicrophone = {p}{(p == 2 ? " (FORCE DENY)" : "")}");
            return string.Join("; ", bits);
        }
        catch (Exception ex) { return $"(could not read: {ex.Message})"; }
    }

    private static string TailNewestLog(int lines)
    {
        try
        {
            var dir = AppConfig.LogsDirectory;
            if (!Directory.Exists(dir)) return "  (no logs folder - logging may be off)";
            var newest = new DirectoryInfo(dir).GetFiles("*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (newest is null) return "  (no log files - logging may be off)";
            var all = File.ReadAllLines(newest.FullName);
            var tail = all.Length <= lines ? all : all[^lines..];
            return $"  file: {newest.Name}" + Environment.NewLine
                 + string.Join(Environment.NewLine, tail.Select(l => "  " + l));
        }
        catch (Exception ex) { return $"  (could not read log: {ex.Message})"; }
    }

    // ---------------- arg helpers ----------------

    /// <summary>The token after the first occurrence of <paramref name="flag"/>, unless that token
    /// is itself a flag (starts with '-'); null when absent. Used for optional values like a path.</summary>
    private static string? ValueAfter(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
                return ValueAt(args, i + 1);
        }
        return null;
    }

    private static string? ValueAt(string[] args, int index) =>
        index >= 0 && index < args.Length && !args[index].StartsWith('-') ? args[index] : null;

    private static bool TryParsePeer(string? text, out IPEndPoint endpoint)
    {
        endpoint = new IPEndPoint(IPAddress.Loopback, RemPacket.DefaultPort);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split(':');
        if (parts.Length == 1 && IPAddress.TryParse(parts[0], out var ip1))
        {
            endpoint = new IPEndPoint(ip1, RemPacket.DefaultPort);
            return true;
        }
        if (parts.Length == 2 && IPAddress.TryParse(parts[0], out var ip2)
            && int.TryParse(parts[1], out var port) && port is > 0 and <= 65535)
        {
            endpoint = new IPEndPoint(ip2, port);
            return true;
        }
        return false;
    }
}
