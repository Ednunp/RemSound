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
                case "--selftest": case "--self-test":
                    return WithConsole(() => RunSelfTest(args));
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

    private static string AppVersion =>
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
        Console.WriteLine("  --selftest [--opus]   Run a localhost audio round-trip (capture -> encode ->");
        Console.WriteLine("             [--seconds N]  network -> decode) and report PASS or FAIL.");
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

    /// <summary>Localhost audio round-trip: capture the default output (as loopback) → encode →
    /// send to 127.0.0.1 → receive → decode. The receiver renders to nothing (no sound), so this is
    /// safe to run any time. PASS when packets flow end-to-end; exit code 0 = PASS, 1 = FAIL.</summary>
    private static int RunSelfTest(string[] args)
    {
        var opus = args.Any(a => a.Equals("--opus", StringComparison.OrdinalIgnoreCase));
        var seconds = int.TryParse(ValueAfter(args, "--seconds"), out var s) && s is > 0 and <= 60 ? s : 5;

        Console.WriteLine($"RemSound self-test: localhost {(opus ? "Opus" : "PCM")} round-trip for {seconds}s...");

        IReadOnlyList<AudioDeviceChoice> outputs;
        try { outputs = AudioDeviceCatalog.LoadOutputs(); }
        catch (Exception ex) { Console.WriteLine($"  could not enumerate outputs: {ex.Message}"); return 1; }
        var dev = outputs.FirstOrDefault(o => o.DeviceId is not null);
        var deviceId = dev?.DeviceId;
        if (dev is null || deviceId is null)
        {
            Console.WriteLine("  RESULT: SKIP - no usable output device to capture from.");
            return 1;
        }

        using var receiver = new AudioReceiver();
        using var sender = new AudioSender();
        try
        {
            receiver.Start();
            receiver.SetOutputDevices(Array.Empty<string>()); // decode only - never make sound during a test
            sender.ConfigureCodec(opus ? AudioTransportCodec.Opus : AudioTransportCodec.Pcm);
            sender.Configure(new[] { new CaptureSourceSpec(deviceId, CaptureKind.Loopback, dev.Name) });
            sender.SetReceivers(new[] { new IPEndPoint(IPAddress.Loopback, RemPacket.DefaultPort) });
            sender.Start();
            Console.WriteLine($"  capturing \"{dev.Name}\" -> 127.0.0.1:{RemPacket.DefaultPort}");
            Thread.Sleep(seconds * 1000);
        }
        finally
        {
            try { sender.Stop(); } catch { /* ignore */ }
            try { receiver.Stop(); } catch { /* ignore */ }
        }

        var sent = sender.PacketsSent;
        var got = receiver.PacketsReceived;
        Console.WriteLine($"  packets sent={sent}  received={got}  bytes received={receiver.BytesReceived}");
        var pass = sent > 0 && got > 0;
        Console.WriteLine(pass
            ? "  RESULT: PASS - capture, encode, network and decode are all working."
            : "  RESULT: FAIL - audio did not flow end-to-end (sent or received was zero).");
        return pass ? 0 : 1;
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

    /// <summary>Write a support-friendly diagnostics report and exit. Always writes a file so it
    /// works even when launched without a terminal; prints the path if a console is attached.</summary>
    private static int RunDiagnostics(string? pathArg)
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

        AppConfig cfg;
        try { cfg = AppConfig.Load(); } catch { cfg = new AppConfig(); }
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

        sb.AppendLine("Most recent log (tail):");
        sb.AppendLine(TailNewestLog(40));
        sb.AppendLine();

        var path = !string.IsNullOrWhiteSpace(pathArg)
            ? pathArg!
            : Path.Combine(AppConfig.UserDataDirectory,
                $"RemSound-diagnostics-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, sb.ToString());
            Console.WriteLine($"Diagnostics written to:");
            Console.WriteLine($"  {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not write the diagnostics file: {ex.Message}");
            Console.WriteLine();
            Console.Write(sb.ToString()); // last resort - dump to the terminal
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
