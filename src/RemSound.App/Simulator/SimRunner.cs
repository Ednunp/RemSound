using System.Text;
using NAudio.Wave;

namespace RemSound.App.Simulator;

/// <summary>
/// RemSound.exe --simulate &lt;what&gt; [--out folder] [--seconds N]. Runs the simulator from the command line and writes a
/// plain-English report (and, for chosen runs, WAV files: the tone on the left, the clicks on the right) to the folder.
/// "smoke" is a quick look at every configuration; "lab" is the latency lab comparison with buffer sweeps.
/// </summary>
internal static partial class SimRunner
{
    public static int Run(string[] args)
    {
        var what = ValueAfter(args, "--simulate") ?? "smoke";
        var outDir = ValueAfter(args, "--out") ?? Path.Combine(Path.GetTempPath(), "RemSound-simulation");
        var seconds = double.TryParse(ValueAfter(args, "--seconds"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 10;
        Directory.CreateDirectory(outDir);
        var report = new StringBuilder();
        void Say(string line) { Console.WriteLine(line); report.AppendLine(line); }
        Say($"RemSound simulator: {what}, {seconds:0.#} simulated seconds a run, {DateTime.Now:yyyy-MM-dd HH:mm}; Opus: {OpusKind()}");
        switch (what)
        {
            case "smoke": Smoke(seconds, Say, outDir); break;
            case "lab": SimLab.Run(seconds, Say, outDir); break;
            case "probe": Probe(seconds, Say); break;
            case "reorder": SimLab.RunReorder(seconds, Say); break;
            case "tuner": SimLab.RunTuner(seconds, Say); break;
            case "service": ServiceToListener(seconds, Say); break;
            case "replay": Replay(args, seconds, Say); break;
            case "calibrate": CalibrateAll(args, Say); break;
            case "record": RecordSeeds(outDir, Say); break;
            case "capture": Capture(seconds, Say); break;
            case "server": Server(seconds, Say); break;
            case "wall": WallClock(seconds, Say); break;
            case "music": Music(seconds, Say, outDir); break;
            case "drift-stall": DriftStallProbe(Say); break;
            case "capture-probe": CaptureProbe(Say); break;
            case "capture-probe-long": CaptureProbeLong(Say); break;
            case "capture-probe-stall": CaptureProbeStall(Say); break;
            case "capture-probe-start": CaptureProbeStart(Say); break;
            case "capture-probe-drift": CaptureProbeDrift(Say); break;
            case "logs": Logs(seconds, args, Say); break;
            default:
                // The performance, soak and stress modes (SimRunner.Performance.cs).
                if (RunPerformanceMode(what, Say, args, outDir)) break;
                Say($"unknown simulation \"{what}\" - try smoke, lab, probe, replay, calibrate, record, capture, server, wall, music, logs, perf, soak, stress or perf-baseline"); return 2;
        }
        // "record" writes recordings into its folder, and the report would sit among them looking like one.
        if (what != "record") File.WriteAllText(Path.Combine(outDir, $"simulation-{what}.txt"), report.ToString());
        return 0;
    }

    /// <summary>The log over every modelled network (SimLogs, 2026-10-19): how big it gets, how many bursts, and whether it
    /// stays in time order. --seconds is each run's length; "--drops N" drops a peer every N seconds as well.</summary>
    private static void Logs(double seconds, string[] args, Action<string> say)
    {
        var dropEvery = int.TryParse(ValueAfter(args, "--drops"), out var d) ? d : 0;
        foreach (var net in new[] { SimNetwork.Perfect, SimNetwork.Wired, SimNetwork.Wifi, SimNetwork.Internet, SimNetwork.BurstyInternet,
                     SimNetwork.Congested, SimNetwork.Rough, SimNetwork.Lumpy, SimNetwork.LaptopLumps })
            foreach (var config in new[] { SimConfig.WasapiOnly, SimConfig.AsioOnly, SimConfig.Both })
            {
                var r = SimLogs.Run(net, config, seconds, dropEvery);
                say(SimLogs.Describe(r) + (r.SecondRows > SimLogs.MostSecondRows(r) ? "; TOO MANY SECOND ROWS" : ""));
            }
    }

    private static void Smoke(double seconds, Action<string> say, string outDir)
    {
        foreach (var (config, codec, buffer) in new[]
        {
            (SimConfig.AsioOnly, SimCodec.Opus2_5, 10),
            (SimConfig.AsioOnly, SimCodec.Pcm, 5),
            (SimConfig.AsioOnly, SimCodec.Custom64, 5),
            (SimConfig.WasapiOnly, SimCodec.Opus2_5, 30),
            (SimConfig.Both, SimCodec.Opus2_5, 10),
        })
        {
            var r = Simulation.Run(new SimScenario($"{config} {codec}", config, codec, SimNetwork.Wired, buffer, seconds, KeepAudio: true));
            say(Describe(r));
            WriteWav(Path.Combine(outDir, $"smoke {config} {codec} {buffer}ms.wav"), r.Audio!);
        }
    }

    /// <summary>The send-only service's stream against the app's, into a listener like Ed's laptop (2026-09-28: "the service
    /// crackles quite a lot"). The laptop's logs show the service's stream arriving with many more repaired and unrepaired
    /// gaps than the app's ASIO-lane stream on the same network minutes apart. The service sends as a WASAPI-only sender does
    /// (Opus 2.5 ms from 10 ms loopback blocks, one untagged stream); the laptop listens on ASIO (32-sample buffer), in ASIO
    /// only and in both, with the everyday auto-tune from 20 ms. Each with the per-stream buffers off and on.</summary>
    private static void ServiceToListener(double seconds, Action<string> say)
    {
        foreach (var net in new[] { SimNetwork.LaptopLumps, SimNetwork.Wired, SimNetwork.Wifi })
            foreach (var config in new[] { SimConfig.AsioOnly, SimConfig.Both })
                foreach (var serviceStyle in new[] { true, false })
                    foreach (var perStream in new[] { false, true })
                    {
                        var who = serviceStyle ? "service (WASAPI only, untagged)" : "app (ASIO lane)";
                        var r = Simulation.Run(new SimScenario($"{who} -> listener {config}", config, SimCodec.Opus2_5, net, 20, seconds,
                            PerStreamBuffers: perStream, ContinuousAutoTune: true, SendWasapiOnly: serviceStyle, ChunkSeconds: seconds > 60 ? 30 : 0));
                        say(Describe(r));
                    }
    }

    /// <summary>Which of a network's faults hurts which codec: each fault of the rough internet on its own, at a buffer
    /// big enough that lateness cannot be the cause.</summary>
    private static void Probe(double seconds, Action<string> say)
    {
        var rough = SimNetwork.Rough;
        var parts = new[]
        {
            rough with { Name = "rough: all of it" },
            new SimNetwork("rough: delay and jitter only", rough.BaseMs, rough.JitterMs),
            new SimNetwork("rough: 2 % loss only", rough.BaseMs, LossPercent: rough.LossPercent),
            new SimNetwork("rough: 0.5 % overtaken only", rough.BaseMs, ReorderPercent: rough.ReorderPercent),
            new SimNetwork("rough: 0.5 % doubled only", rough.BaseMs, DuplicatePercent: rough.DuplicatePercent),
            new SimNetwork("rough: PC hiccups only", rough.BaseMs, HiccupPercent: rough.HiccupPercent, HiccupMs: rough.HiccupMs),
        };
        foreach (var codec in new[] { SimCodec.Opus2_5, SimCodec.Custom64, SimCodec.Opus20 })
            foreach (var net in parts)
                say(Describe(Simulation.Run(new SimScenario(net.Name, SimConfig.AsioOnly, codec, net, 200, seconds))));
    }

    /// <summary>Which Opus the normal path is using here: the native libopus the shipped app uses, or Concentus's managed
    /// fallback. They sound slightly different (the fallback's concealment is rougher), so every report says which.</summary>
    public static string OpusKind()
    {
        // A diagnostic switch, kept on purpose (2026-09-28): set the environment variable OPUS_TRACE=1 before
        // "RemSound.exe --simulate ..." and Concentus prints its own loader log to the console - where it looked for opus.dll
        // and why it did or did not use it. For when a report's first line says "managed fallback" and it should not (the
        // Debug build copies opus.dll beside the exe; a copy that never happened falls back). Off, nothing is printed.
        var decoder = Concentus.OpusCodecFactory.CreateDecoder(48000, 2, Environment.GetEnvironmentVariable("OPUS_TRACE") == "1" ? Console.Out : TextWriter.Null);
        var name = decoder.GetType().Name;
        (decoder as IDisposable)?.Dispose();
        return name.Contains("Native", StringComparison.OrdinalIgnoreCase) ? $"native libopus ({name})" : $"managed fallback ({name})";
    }

    public static string Describe(SimResult r)
    {
        var f = r.Findings;
        var s = r.Scenario;
        return $"{s.Name}: {s.Config}, {s.Codec}, {s.Network.Name}, buffer {s.BufferMs} ms{(s.PerStreamBuffers ? "" : ", without per-stream buffers")}{(s.ReorderWait ? "" : ", without the reorder wait")} -> "
             + (f.ClicksHeard == 0 && f.AnalysedSeconds <= 0 ? "NOTHING HEARD" :
                f.ClicksHeard == 0 ? $"no delay clicks to time it by; {(f.CleanTone ? "CLEAN" : $"{f.Glitches} glitches ({f.QuietMs} quiet ms, {f.StepClicks} clicks, first at {f.FirstGlitchAt})")}; "
                                   + $"pitch within {f.WorstPitchCents:0.0} cents; tone-to-noise {f.MedianSnrDb:0} dB (worst {f.WorstSnrDb:0})" :
                r.Music is { } music ? $"delay {f.DelayMedianMs:0.0} ms (min {f.DelayMinMs:0.0}, max {f.DelayMaxMs:0.0}); {music.Describe()}" :
                $"delay {f.DelayMedianMs:0.0} ms (min {f.DelayMinMs:0.0}, max {f.DelayMaxMs:0.0}, first {f.DelayFirstMs:0.0}, last {f.DelayLastMs:0.0}); "
              + $"{(f.Clean ? "CLEAN" : $"{f.Glitches} glitches ({f.QuietMs} quiet ms, {f.StepClicks} clicks, first at {f.FirstGlitchAt})")}; "
              + $"pitch within {f.WorstPitchCents:0.0} cents; tone-to-noise {f.MedianSnrDb:0} dB (worst {f.WorstSnrDb:0})")
             + $"; underruns {r.Underruns}, packets {r.PacketsSent} ({r.PacketsLost} lost), FEC {r.FecRecoveries}, "
             + $"unrecovered {r.UnrecoveredGaps}, concealed {r.CustomConcealed}, late dropped {r.LateDropped}, reorder waits {r.ReorderWaits} "
             + $"(filled {r.ReorderFilled}); {r.RealSeconds:0.00} s to run";
    }

    public static void WriteWav(string path, float[] interleaved)
    {
        using var w = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(Simulation.SampleRate, 2));
        w.WriteSamples(interleaved, 0, interleaved.Length);
    }

    private static string? ValueAfter(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : null;
    }
}
