using System.Globalization;
using RemSound.Core;

namespace RemSound.App.Simulator;

/// <summary>
/// --simulate replay --recording &lt;file&gt; [--stream N] [--one-way ms]: a network the app recorded (Record &gt; Save the last
/// minute of the network), replayed (2026-09-30). First as it was - the recording's own configuration, codec and buffer -
/// beside what the receiver itself heard go wrong in that minute; then the same network under other codecs and buffers.
/// </summary>
internal static partial class SimRunner
{
    private static void Replay(string[] args, double seconds, Action<string> say)
    {
        var file = ValueAfter(args, "--recording");
        if (file is null || !File.Exists(file)) { say("replay needs --recording <file>, a file saved by Record > Save the last minute of the network"); return; }
        var rec = NetworkRecording.Load(file);
        var stream = int.TryParse(ValueAfter(args, "--stream"), out var n) ? n : -1;
        var replay = NetworkReplay.From(rec, stream, $"replayed: {Path.GetFileName(file)}");
        var oneWay = double.TryParse(ValueAfter(args, "--one-way"), NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w
            : rec.Number("one-way ms") ?? 0;
        foreach (var (k, v) in rec.Facts) say($"  {k}: {v}");
        foreach (var s in rec.Streams) say($"  stream {s.Index}: {s.From}, {s.Format}, {rec.Packets.Count(p => p.Stream == s.Index)} packets");
        say($"replaying {replay.Name}: a packet every {replay.Period * 1000:0.###} ms, {replay.Places} places, {replay.Lost} never came, "
            + $"{replay.Doubled} came twice; one-way delay taken as {oneWay:0.#} ms");
        var net = SimNetwork.Replaying(replay, oneWay);
        var asRecorded = ScenarioFor(rec, stream, net, seconds);
        say("As recorded:");
        if (Calibrate(rec, Path.GetFileName(file), stream) is { } calibration)
        {
            say("  " + Describe(calibration.Replayed));
            say("  " + calibration.Describe());
        }
        else
        {
            say("  " + Describe(Simulation.Run(asRecorded)));
            say("  (the recording holds no live figures to check the simulator against)");
        }
        say("Other codecs and buffers on the same network:");
        foreach (var codec in new[] { SimCodec.Custom64, SimCodec.Opus2_5, SimCodec.Opus10 })
            foreach (var buffer in new[] { 5, 8, 10, 15, 20, 30 })
                say("  " + Describe(Simulation.Run(asRecorded with { Name = $"{codec} at {buffer} ms", Codec = codec, BufferMs = buffer })));
    }

    /// <summary>--simulate calibrate [--recordings folder]: every recording in the folder (the repository's
    /// simulation-recordings unless one is named), replayed as it was recorded, against what its own receiver heard.</summary>
    private static void CalibrateAll(string[] args, Action<string> say)
    {
        var folder = ValueAfter(args, "--recordings") ?? RecordingsFolder();
        if (folder is null || !Directory.Exists(folder)) { say("no recordings folder: name one with --recordings, or run from the source tree (simulation-recordings)"); return; }
        foreach (var file in Directory.GetFiles(folder, "*.txt").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var c = Calibrate(NetworkRecording.Load(file), Path.GetFileName(file));
                say(c is null ? $"{Path.GetFileName(file)}: no live figures in it" : c.Describe());
            }
            catch (Exception ex) { say($"{Path.GetFileName(file)}: could not be replayed ({ex.GetType().Name}: {ex.Message})"); }
        }
    }

    /// <summary>--simulate record [--out folder]: recordings made by known simulated runs, for the calibration check to
    /// have something to check itself against until real ones are dropped beside them.</summary>
    private static void RecordSeeds(string outDir, Action<string> say)
    {
        foreach (var (file, scenario) in SeedScenarios())
        {
            var r = Simulation.Run(scenario);
            var path = Path.Combine(outDir, file);
            r.Recording!.Save(path);
            say($"{path}: {Describe(r)}");
        }
    }

    /// <summary>The simulated runs whose recordings seed the recordings folder: a lumpy laptop at a buffer too small for it.</summary>
    internal static IEnumerable<(string File, SimScenario Scenario)> SeedScenarios()
    {
        yield return ("simulated-laptop-lumps-asio-10ms.txt",
            new SimScenario("a lumpy laptop at 10 ms", SimConfig.AsioOnly, SimCodec.Opus2_5, SimNetwork.LaptopLumps, 10, 60, RecordNetwork: true));
    }

    /// <summary>The repository's recordings folder, when the source tree can be found from here.</summary>
    internal static string? RecordingsFolder()
    {
        var root = Environment.GetEnvironmentVariable("REMSOUND_SOURCE_ROOT");
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); root is null && dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "simulation-recordings")) && Directory.Exists(Path.Combine(dir.FullName, "src"))) root = dir.FullName;
        return root is null ? null : Path.Combine(root, "simulation-recordings");
    }

    /// <summary>
    /// A recording's own receiver against the simulator (2026-09-30, the simulator review's third improvement). The recording
    /// is replayed as it was made - its configuration, codec, buffer and output period - and the simulated receiver's
    /// counters are set beside the live ones the recording carries, per minute: audible gaps, late or doubled packets
    /// dropped, gaps the codec could not repair. They agree when each is within <see cref="Floor"/> a minute or
    /// <see cref="Percent"/> of the larger.
    /// </summary>
    internal sealed record Calibration(string File, (string What, double Live, double Simulated)[] PerMinute, SimResult Replayed)
    {
        public const double Floor = 3;
        public const double Percent = 0.5;
        public bool Agrees => PerMinute.All(f => Math.Abs(f.Live - f.Simulated) <= Math.Max(Floor, Percent * Math.Max(f.Live, f.Simulated)));
        public string Describe() =>
            $"{File}: {(Agrees ? "the simulator agrees with the live receiver" : "the simulator DISAGREES with the live receiver")} - a minute: "
            + string.Join("; ", PerMinute.Select(f => $"{f.What} {f.Live:0.#} live, {f.Simulated:0.#} simulated"));
    }

    /// <summary>Replay a recording as it was made and compare; null when it carries no live figures.</summary>
    internal static Calibration? Calibrate(NetworkRecording rec, string name, int stream = -1)
    {
        if (rec.Number("live seconds") is not { } liveSeconds || liveSeconds <= 0 || rec.Packets.Count == 0) return null;
        var replay = NetworkReplay.From(rec, stream, $"replayed: {name}");
        var scenario = ScenarioFor(rec, stream, SimNetwork.Replaying(replay, rec.Number("one-way ms") ?? 0), 0) with { RecordNetwork = true };
        var result = Simulation.Run(scenario);
        var sim = result.Recording!;
        var simSeconds = sim.Number("live seconds") ?? scenario.Seconds;
        (string, double, double) Figure(string what, string fact) =>
            (what, (rec.Number(fact) ?? 0) * 60 / liveSeconds, (sim.Number(fact) ?? 0) * 60 / Math.Max(1, simSeconds));
        return new Calibration(name,
        [
            Figure("audible gaps", "live audible gaps"),
            Figure("late or doubled dropped", "live late or doubled dropped"),
            Figure("gaps not repaired", "live gaps not repaired"),
        ], result);
    }

    /// <summary>The scenario a recording was made in: its configuration, the stream's codec, the buffer that lane had and
    /// its output's period, over the replayed network, for as long as the recording (or <paramref name="seconds"/> if longer).</summary>
    internal static SimScenario ScenarioFor(NetworkRecording rec, int stream, SimNetwork net, double seconds)
    {
        var config = ConfigOf(rec.Fact("configuration"));
        var s = stream >= 0 && stream < rec.Streams.Count ? rec.Streams[stream]
            : rec.Streams.OrderByDescending(x => rec.Packets.Count(p => p.Stream == x.Index)).FirstOrDefault();
        var codec = s is null ? SimCodec.Opus2_5 : CodecOf(s.Codec, s.FrameSamples);
        var buffer = (int)(config switch
        {
            SimConfig.WasapiOnly => rec.Number("buffer ms"),
            _ => rec.Number("buffer ASIO ms"),
        } ?? 20);
        var length = rec.Packets.Count > 0 ? rec.Packets[^1].Ms / 1000 : 0;
        // The listened output's period: a simulated run says its card's frames; the app says each output's period in ms.
        var frames = (int)(rec.Number("receive card frames")
            ?? (rec.Number(config == SimConfig.WasapiOnly ? "WASAPI output period ms" : "ASIO output period ms") ?? 0) * Simulation.SampleRate / 1000);
        return new SimScenario($"as recorded ({config}, {codec}, {buffer} ms)", config, codec, net, Math.Max(1, buffer), Math.Max(seconds, Math.Floor(length)),
            ReceiveCard: frames > 0 ? Simulation.DefaultCard(config != SimConfig.WasapiOnly) with { Frames = frames } : null);
    }

    internal static SimConfig ConfigOf(string? text) => text switch
    {
        null => SimConfig.AsioOnly,
        _ when text.Contains("WASAPI only", StringComparison.OrdinalIgnoreCase) || text == nameof(SimConfig.WasapiOnly) => SimConfig.WasapiOnly,
        _ when text.Contains("ASIO only", StringComparison.OrdinalIgnoreCase) || text == nameof(SimConfig.AsioOnly) => SimConfig.AsioOnly,
        _ when text.Contains("both", StringComparison.OrdinalIgnoreCase) => SimConfig.Both,
        _ => SimConfig.AsioOnly,
    };

    internal static SimCodec CodecOf(int codec, int frameSamples) => (AudioTransportCodec)codec switch
    {
        AudioTransportCodec.OpusCustom => frameSamples <= 64 ? SimCodec.Custom64 : SimCodec.Custom128,
        AudioTransportCodec.Opus => frameSamples switch { <= 120 => SimCodec.Opus2_5, <= 480 => SimCodec.Opus10, _ => SimCodec.Opus20 },
        _ => SimCodec.Pcm,
    };
}
