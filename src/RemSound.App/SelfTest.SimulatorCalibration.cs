using RemSound.App.Simulator;
using RemSound.Core;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>SIMULATOR CALIBRATION (2026-09-30, the simulator review's third improvement). A recording carries what its
    /// own receiver heard go wrong in that minute; replayed as it was made, the simulated receiver must hear the same, per
    /// minute, within 3 or half. A run with real break-ups, recorded and replayed, agrees with itself; the same recording
    /// with its live figures changed does not; one without live figures is not judged; and every recording kept in the
    /// repository's simulation-recordings folder agrees - the simulated seed today, Ed's and Andre's real ones as they
    /// are added.</summary>
    private static string SimulatorAgreesWithLiveRecordings()
    {
        // 1. A run with break-ups (the laptop's lumps at a buffer too small), recorded, then replayed as recorded.
        var (_, seed) = SimRunner.SeedScenarios().First();
        var run = Simulation.Run(seed with { Seconds = 30 });
        var rec = Require(run.Recording, "the run's recording");
        var gaps = rec.Number("live audible gaps") ?? 0;
        Check(gaps >= 2 && (rec.Number("live late or doubled dropped") ?? 0) > 0, $"premise: the run must have break-ups and late packets to agree about ({gaps} gaps)");
        var own = Require(SimRunner.Calibrate(rec, "its own run"), "a calibration of a recording that has live figures");
        Check(own.Agrees, $"a run replayed as it was recorded must hear what it heard: {own.Describe()}");
        Check(own.PerMinute.Single(f => f.What == "audible gaps").Simulated > 0, $"and hear the break-ups, not nothing: {own.Describe()}");
        var scenario = SimRunner.ScenarioFor(rec, -1, SimNetwork.Perfect, 0);
        Check(scenario.Config == SimConfig.AsioOnly && scenario.Codec == SimCodec.Opus2_5 && scenario.BufferMs == 10 && scenario.ReceiveCard?.Frames == 32,
            $"the recording must be replayed as it was made: ASIO only, 2.5 ms Opus, 10 ms, a 32-frame card ({scenario.Config}, {scenario.Codec}, {scenario.BufferMs}, {scenario.ReceiveCard?.Frames})");

        // 2. The same recording saying its receiver heard far more, and far less, than the simulator does: not agreed.
        NetworkRecording Doctored(Func<double, double> change)
        {
            var text = new StringWriter();
            rec.Save(text);
            var copy = NetworkRecording.Load(new StringReader(text.ToString()));
            var at = copy.Facts.FindIndex(f => f.Key == "live audible gaps");
            copy.Facts[at] = new("live audible gaps", change(gaps).ToString(System.Globalization.CultureInfo.InvariantCulture));
            return copy;
        }
        Check(!Require(SimRunner.Calibrate(Doctored(g => g * 4 + 20), "heard far more"), "a calibration").Agrees, "a receiver that heard far more break-ups than the simulator must be a disagreement");
        Check(gaps < 7 || !Require(SimRunner.Calibrate(Doctored(_ => 0), "heard none"), "a calibration").Agrees, "and one that heard none where the simulator hears many");
        Check(SimRunner.Calibration.Floor == 3 && SimRunner.Calibration.Percent == 0.5, "the margin is 3 a minute or half of the larger");

        // 3. A recording with no live figures is replayable but not judged.
        var bare = new NetworkRecording();
        bare.Streams.Add(rec.Streams[0]);
        bare.Packets.AddRange(rec.Packets.Take(400));
        Check(SimRunner.Calibrate(bare, "no live figures") is null, "a recording with no live figures cannot be judged");

        // 4. Every recording kept in the repository.
        var folder = SimRunner.RecordingsFolder();
        if (folder is null || !Directory.Exists(folder))
            return Skip("the calibration works on its own run, but the repository's simulation-recordings folder is not reachable from here (set REMSOUND_SOURCE_ROOT)");
        var files = Directory.GetFiles(folder, "*.txt").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        Check(files.Count > 0, "the simulation-recordings folder must hold at least the simulated seed recording");
        var judged = new List<string>();
        foreach (var file in files)
        {
            var c = SimRunner.Calibrate(NetworkRecording.Load(file), Path.GetFileName(file));
            if (c is null) continue;
            Check(c.Agrees, c.Describe());
            judged.Add(Path.GetFileName(file));
        }
        Check(judged.Count > 0, "at least one kept recording must carry live figures");
        return $"a run with {gaps:0} break-ups in 30 s replayed hears the same; changed live figures disagree; {judged.Count} kept recording(s) agree: "
             + string.Join(", ", judged);
    }
}
