using System.Text;
using System.Text.RegularExpressions;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>What the main window's once-a-second work calls on the receiver or the sender that the simulator's main
    /// window (SimMainWindow) does not, and why each changes nothing the simulator hears or decides. A call that is in
    /// neither this list nor the simulator fails the guard: it goes in the simulator, or it is named here with its reason.</summary>
    private static readonly (string Why, string[] Calls)[] MainWindowOnlyCalls =
    [
        ("left out of the simulator on purpose: idle streams are pruned on the wall clock, and no simulated stream goes idle",
            ["receiver.PruneIdleSessions()"]),
        ("read for the readings file only (until 2026-10-22 the once-a-second \"streams\" line)",
            ["sender.TakeInboundMaxGapMs()", "sender.TakeInboundMaxDispatchMs()"]),
        ("the snapshot log line's own columns: what each lane is set to",
            ["receiver.MaxLatencyMsFor()", "receiver.TargetLatencyMsFor()"]),
        ("the real output says these; the simulator's devices say them themselves (SimMainWindow's reportedOutputMs and outputQueueMs)",
            ["receiver.ReportedOutputLatencyMsFor()", "receiver.OutputQueueMsFor()"]),
        ("the log line's own label for where the capture figure came from; the figure itself is the app's LaneCaptureMs in both",
            ["receiver.AnnouncedCaptureLatencyMsFor()", "sender.CaptureLatencyMsForOutput()"]),
        ("peaks and totals the diagnostics log line prints; reading one resets only itself, and nothing else reads it",
            [
                "sender.TakeMaxEmitMs()", "sender.TakeMaxSendCallMs()", "receiver.TakeMaxOnPacketMs()", "receiver.TakeMaxInterPacketGapMs()",
                "sender.TakeCaptureWorkMs()", "sender.TakeSendWorkMs()", "receiver.TakeReceiveWorkMs()", "receiver.TakeRenderWorkMs()",
                "receiver.TakeRenderWorkMsByLane()",
                "sender.TakeMaxPreEncodeStepWasapiLaneCrossBuffer()", "sender.TakeMaxPreEncodeStepWasapiLaneWithinBuffer()",
                "sender.TakeMaxPreEncodeStepAsioLaneCrossBuffer()", "sender.TakeMaxPreEncodeStepAsioLaneWithinBuffer()",
                "sender.TakeMaxSenderRawCaptureStepCrossBuffer()", "sender.TakeMaxSenderRawCaptureStepWithinBuffer()",
                "sender.TakeMaxSenderPreEncodePeak()", "sender.TakeMaxPreEncodePeakPluginLane()",
                "sender.TakeMaxEmitGapMsWasapiLane()", "sender.TakeMaxEmitGapMsAsioLane()", "sender.TakeMaxEmitGapMsPluginLane()",
                "sender.TakeSenderAudioFramesSent()",
                "receiver.TakeMaxPostDecodeStepCrossBuffer()", "receiver.TakeMaxPostDecodeStepWithinBuffer()",
                "receiver.TakeMaxPostRingReadStepCrossBuffer()", "receiver.TakeMaxPostRingReadStepWithinBuffer()",
                "receiver.TakeMaxPostResamplerStepCrossBuffer()", "receiver.TakeMaxPostResamplerStepWithinBuffer()",
            ]),
    ];

    /// <summary>What the simulator's main window calls that the app's tick does not, and where the app does it instead.</summary>
    private static readonly (string Why, string[] Calls)[] SimulatorOnlyCalls =
    [
        ("in the app the tuner moves the slider, and the slider's own handler makes this call",
            ["receiver.SetMaxLatencyMsSoftFor()"]),
    ];

    /// <summary>The main window's auto-tune tick reads this for its log line only ("devGulp=… ignored").</summary>
    private static readonly string[] TuneTickLogOnly = ["receiver.DeviceGulpUnderrunsFor()"];

    /// <summary>SIMULATOR GUARD (2026-09-30, the simulator review's ninth improvement). The simulator cannot run the main
    /// window, so SimMainWindow re-does its once-a-second work: the auto-tune's evidence, the tune tick, the latency
    /// readout. If the app's tick changes and the simulator's copy does not, the simulator goes on passing while it tests
    /// something the app no longer does. This reads both sources and compares them: everything each does to the receiver,
    /// the sender and the tuner's evidence, in the order it does it; the auto-tune decision's arguments; the readout's
    /// parts; and the order the simulator runs the three jobs in. A call in the app's tick that the simulator neither makes
    /// nor has been named as harmless fails the step.</summary>
    private static string? SimulatorFollowsTheMainWindow()
    {
        var root = FindSourceRoot();
        if (root is null) return Skip("the source tree is not reachable from here (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var app = Path.Combine(root, "src", "RemSound.App");
        var mainForm = CodeOnly(File.ReadAllText(Path.Combine(app, "MainForm.cs")));
        var streamBuffers = CodeOnly(File.ReadAllText(Path.Combine(app, "MainForm.StreamBuffers.cs")));
        var sim = CodeOnly(File.ReadAllText(Path.Combine(app, "Simulator", "SimMainWindow.cs")));
        var simulation = CodeOnly(File.ReadAllText(Path.Combine(app, "Simulator", "Simulation.cs")));

        // The app's own methods that are part of the tick, each read in place where the tick calls it.
        var parts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TickStreamBuffers"] = MethodBody(streamBuffers, "private void TickStreamBuffers()"),
            ["OnNewStreamSessions"] = MethodBody(mainForm, "private void OnNewStreamSessions()"),
            ["ForgetTuneReadings"] = MethodBody(mainForm, "private void ForgetTuneReadings()"),
            ["NoteLaneAudibility"] = MethodBody(mainForm, "private void NoteLaneAudibility(RenderRoute route, bool consuming)"),
        };
        var ignoredInTheApp = MainWindowOnlyCalls.SelectMany(g => g.Calls).ToHashSet(StringComparer.Ordinal);
        var ignoredInTheSim = SimulatorOnlyCalls.SelectMany(g => g.Calls).ToHashSet(StringComparer.Ordinal);

        // 1. Once a second. The simulator sizes each stream's buffer first (Simulation.cs), then runs EverySecond.
        var appSecondAll = TickShape(MethodBody(mainForm, "private void SnapshotLogIfDue()"), parts);
        var appSecond = appSecondAll.Where(t => !ignoredInTheApp.Contains(t)).ToList();
        // The list of harmless calls must not outlive the calls: one the tick no longer makes comes off it.
        foreach (var named in ignoredInTheApp)
            Check(appSecondAll.Contains(named), $"{named} is named as a harmless call of the app's tick, and the tick no longer makes it: take it off MainWindowOnlyCalls");
        var simSecond = new[] { "receiver.UpdatePerStreamNeeds()" }
            .Concat(TickShape(MethodBody(sim, "public void EverySecond(double now)"),
                new Dictionary<string, string> { ["ComposeReadout"] = MethodBody(sim, "private void ComposeReadout(") }))
            .Where(t => !ignoredInTheSim.Contains(t)).ToList();
        CheckSameShape("once a second", Collapse(appSecond), Collapse(simSecond));
        Check(simSecond.Count >= 15, $"premise: the once-a-second work must be found at all ({simSecond.Count} things: {string.Join(", ", simSecond)})");

        // 2. The order the simulator runs the three jobs in, within one second: each stream's buffer, the evidence, the tune.
        var sized = ScheduledAt(simulation, @"receiver\.UpdatePerStreamNeeds\(");
        var evidence = ScheduledAt(simulation, @"window\.EverySecond\(");
        var tuned = ScheduledAt(simulation, @"window\.TuneTick\(");
        Check(sized < evidence && evidence < tuned,
            $"the simulator must size each stream's buffer, then gather the evidence, then tick the tune, as the app's tick does (at +{sized}, +{evidence} and +{tuned} s)");

        // 3. The auto-tune tick: which lanes, and then one lane's decision.
        var appTune = TickShape(MethodBody(mainForm, "private void ContinuousTuneTick()"), new Dictionary<string, string>());
        var simTune = TickShape(MethodBody(sim, "public void TuneTick(double now)"), new Dictionary<string, string>());
        CheckSameShape("the auto-tune tick", Collapse(appTune), Collapse(simTune));
        var appRouteBody = MethodBody(mainForm, "private void TickRoute(");
        var simRouteBody = MethodBody(sim, "private void TickRoute(RenderRoute route, double now, int frameMs)");
        var appRoute = TickShape(appRouteBody, new Dictionary<string, string>()).Where(t => !TuneTickLogOnly.Contains(t)).ToList();
        var simRoute = TickShape(simRouteBody, new Dictionary<string, string>()).Where(t => !ignoredInTheSim.Contains(t)).ToList();
        CheckSameShape("one lane's auto-tune tick", Collapse(appRoute), Collapse(simRoute));
        Check(simRoute.Contains("ContinuousAutoTune.Decide") && simRoute.Contains("the baseline taken"),
            $"premise: the lane's tick must be found, its baseline and its decision ({string.Join(", ", simRoute)})");
        Check(Regex.IsMatch(mainForm, @"receiver\.SetMaxLatencyMsSoftFor\("), "the app must still apply a tuner's move by the soft setter, as the simulator does");

        // The decision's arguments: the same evidence, in the same places.
        var appArgs = CallArguments(appRouteBody, "ContinuousAutoTune.Decide(");
        var simArgs = CallArguments(simRouteBody, "ContinuousAutoTune.Decide(");
        Check(appArgs.Count == simArgs.Count && appArgs.Count == 12,
            $"the auto-tune's decision must be given the same number of things by both ({appArgs.Count} in the app, {simArgs.Count} in the simulator)");
        foreach (var i in new[] { 4, 5, 6, 7, 10, 11 })
            Check(appArgs[i] == simArgs[i], $"the auto-tune's decision, argument {i + 1}: the app gives it \"{appArgs[i]}\", the simulator \"{simArgs[i]}\"");
        Check(Regex.IsMatch(appRouteBody, @"const int (\w+) = TuneRenderFloorMs;") && Regex.IsMatch(simRouteBody, @"const int (\w+) = MainForm\.TuneRenderFloorMs;")
              && Regex.IsMatch(appRouteBody, @"const int (\w+) = TuneSafetyMs;") && Regex.IsMatch(simRouteBody, @"const int (\w+) = MainForm\.TuneSafetyMs;"),
            "both must take the render floor and the safety margin from the app's own two constants");
        foreach (var body in new[] { appRouteBody, simRouteBody })
        {
            Check(Regex.IsMatch(body, @"var laneRenderCbGaps = memory\.RenderCbGaps\.Count > 0 \? memory\.RenderCbGaps : recentRenderCbGaps;")
                  && Regex.IsMatch(body, @"var laneMinBuffers = memory\.MinBuffers\.Count > 0 \? memory\.MinBuffers : recentMinBuffers;"),
                "both must give the decision the lane's own readings, and the shared ones only when the lane has none");
        }

        // 4. The readings' window, and the latency readout's parts: the simulator uses the app's own, and the app still does.
        var windows = Regex.Matches(sim, @"\.Count > (?<length>[\w.]+)\) \w+\.Dequeue\(\)").Select(m => m.Groups["length"].Value).ToList();
        Check(windows.Count >= 2 && windows.All(w => w == "MainForm.RecentMaxGapWindowSeconds"),
            $"every window of readings the simulator keeps must be the app's own length (they are: {string.Join(", ", windows)})");
        var readoutParts = Regex.Matches(MethodBody(sim, "private void ComposeReadout("), @"\bMainForm\.(\w+)\(").Select(m => m.Groups[1].Value)
            .Select(n => n.EndsWith("ForTest", StringComparison.Ordinal) ? n[..^"ForTest".Length] : n).Distinct().ToList();
        Check(readoutParts.Count >= 6, $"premise: the simulator's readout must be built from the app's own parts ({string.Join(", ", readoutParts)})");
        // The app's readout is put together in the tick, in the method that writes the box, and in the one line between.
        var laneLatency = Regex.Match(mainForm, @"private double LaneLatencyMs\([^)]*\) =>[^;]*;");
        Check(laneLatency.Success, "premise: the app's LaneLatencyMs must be found");
        var appReadout = MethodBody(mainForm, "private void SnapshotLogIfDue()") + MethodBody(mainForm, "private void UpdateMeasuredLatencyReadout()") + laneLatency.Value;
        foreach (var part in readoutParts)
            Check(Regex.IsMatch(appReadout, $@"\b{part}\("), $"the app's own readout no longer uses {part}, which the simulator's still does");

        return $"once a second {simSecond.Count} things in the same order in both; the three jobs in the app's order; the auto-tune tick and its decision's arguments the same; "
             + $"the readout from the app's own {readoutParts.Count} parts; {ignoredInTheApp.Count} calls of the app's named as changing nothing here";
    }

    /// <summary>Everything a stretch of code does to the receiver, the sender or the tuner's evidence, in order. A call to
    /// one of <paramref name="parts"/> is read in place.</summary>
    private static List<string> TickShape(string body, Dictionary<string, string> parts)
    {
        var shape = new List<string>();
        foreach (Match m in TickLandmark.Matches(body))
        {
            if (m.Groups["own"].Success)
            {
                if (parts.TryGetValue(m.Groups["own"].Value, out var inner)) shape.AddRange(TickShape(inner, parts));
            }
            else if (m.Groups["call"].Success) shape.Add($"{m.Groups["obj"].Value}.{m.Groups["call"].Value}()");
            else if (m.Groups["read"].Success) shape.Add($"receiver.{m.Groups["read"].Value}");
            else if (m.Groups["window"].Success) shape.Add($"{m.Groups["window"].Value}.{m.Groups["op"].Value}");
            else if (m.Groups["pushed"].Success) shape.Add($"{m.Groups["pushed"].Value}.Enqueue");
            else if (m.Groups["lane"].Success) shape.Add($"the lane's {m.Groups["lane"].Value}.Enqueue");
            else if (m.Groups["pushedLane"].Success) shape.Add($"the lane's {m.Groups["pushedLane"].Value}.Enqueue");
            else if (m.Groups["audible"].Success) shape.Add("the lane's NoteLaneAudible");
            else if (m.Groups["reset"].Success) shape.Add("the lanes forget");
            else if (m.Groups["baseline"].Success) shape.Add("the baseline taken");
            else if (m.Groups["decide"].Success) shape.Add("ContinuousAutoTune.Decide");
            else if (m.Groups["two"].Success) shape.Add("waits for two readings");
            else if (m.Groups["changed"].Success) shape.Add("a new stream noted");
            else if (m.Groups["since"].Success) shape.Add("waits an interval after a new stream");
        }
        return shape;
    }

    private static readonly Regex TickLandmark = new(
        @"\b(?<obj>receiver|sender)\.(?<call>\w+)\("
        + @"|\breceiver\.(?<read>ActiveStreamFrameMs|SessionsOpenedCount|TuneBlockingUnderruns)\b(?![\w(])"
        + @"|\b(?<window>recent\w+)\.(?<op>Enqueue|Clear)\("
        + @"|\bPush\((?<pushed>recent\w+),"
        + @"|\bPush\(memory\.(?<pushedLane>RenderCbGaps|MinBuffers),"
        + @"|\b\w*Memory\.(?<lane>RenderCbGaps|MinBuffers)\.Enqueue\("
        + @"|\.(?<audible>NoteLaneAudible)\("
        + @"|\b\w*[mM]emory\.(?<reset>Reset)\(\)"
        + @"|\.(?<baseline>NeedsUnderrunBaseline) = false"
        + @"|(?<decide>ContinuousAutoTune\.Decide)\("
        + @"|(?<two>recentMaxGaps\.Count < 2)"
        + @"|\b(?<changed>lastSourceChange\w*) = "
        + @"|- (?<since>lastSourceChange\w*) < "
        + @"|(?<![\w.])(?<own>[A-Z]\w+)\(",
        RegexOptions.Compiled);

    /// <summary>The same thing done several times running is one thing (three lanes read, three memories reset); readings
    /// cleared together are one clearing, whatever order they are cleared in.</summary>
    private static List<string> Collapse(List<string> shape)
    {
        var result = new List<string>();
        for (var i = 0; i < shape.Count; i++)
        {
            if (shape[i].EndsWith(".Clear", StringComparison.Ordinal))
            {
                var cleared = new SortedSet<string>(StringComparer.Ordinal);
                for (; i < shape.Count && shape[i].EndsWith(".Clear", StringComparison.Ordinal); i++) cleared.Add(shape[i][..^".Clear".Length]);
                i--;
                result.Add("readings cleared: " + string.Join(", ", cleared));
            }
            else if (result.Count == 0 || result[^1] != shape[i]) result.Add(shape[i]);
        }
        return result;
    }

    private static void CheckSameShape(string what, List<string> app, List<string> sim)
    {
        var at = 0;
        while (at < app.Count && at < sim.Count && app[at] == sim[at]) at++;
        if (at == app.Count && at == sim.Count) return;
        string Around(List<string> shape) => at >= shape.Count ? "nothing more" : string.Join(" -> ", shape.Skip(Math.Max(0, at - 1)).Take(12));
        Check(false, $"THE SIMULATOR NO LONGER FOLLOWS THE MAIN WINDOW ({what}): after {at} things the same, the app does \"{Around(app)}\" "
                   + $"and the simulator does \"{Around(sim)}\". Change SimMainWindow to match the app - or, for a call that changes nothing the "
                   + "simulator hears or decides, name it in MainWindowOnlyCalls with its reason.");
    }

    /// <summary>The offset into each second at which Simulation.cs schedules a call: "At(sec + 0.25, () => window.EverySecond"
    /// is 0.25, "At(sec, () =>" followed by the call is 0.</summary>
    private static double ScheduledAt(string simulation, string call)
    {
        var m = Regex.Match(simulation, @"At\((?:sec|t)(?: \+ (?<offset>[0-9.]+))?, \(\) =>\s*\{?\s*(?:var \w+ = )?" + call);
        Check(m.Success, $"premise: where the simulator schedules {call} each second must be found in Simulation.cs");
        return m.Groups["offset"].Success ? double.Parse(m.Groups["offset"].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>A method's body, braces and all, which must be there.</summary>
    private static string MethodBody(string code, string signature)
    {
        var body = MethodBodyIn(code, signature);
        Check(body.Length > 0, $"premise: \"{signature}\" must be found in the source, with its body");
        return body;
    }

    /// <summary>A call's arguments, split at its own commas, spaces squeezed and "MainForm." dropped.</summary>
    private static List<string> CallArguments(string body, string call)
    {
        var at = body.IndexOf(call, StringComparison.Ordinal);
        Check(at >= 0, $"premise: a call to {call} must be found");
        var args = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        for (var i = at + call.Length; i < body.Length; i++)
        {
            var c = body[i];
            if (c is '(' or '[' or '{') depth++;
            if (c is ')' or ']' or '}')
            {
                if (depth == 0) break;
                depth--;
            }
            if (c == ',' && depth == 0)
            {
                args.Add(current.ToString());
                current.Clear();
            }
            else current.Append(c);
        }
        args.Add(current.ToString());
        return args.Select(a => Regex.Replace(a, @"\s+", " ").Trim().Replace("MainForm.", "", StringComparison.Ordinal)).ToList();
    }
}
