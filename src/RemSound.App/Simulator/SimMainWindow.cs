using System.Text.RegularExpressions;
using RemSound.Core;
using RemSound.Receiver;
using RemSound.Sender;

namespace RemSound.App.Simulator;

/// <summary>
/// The main window's once-a-second work that changes what is heard, or what the user is told, run inside the simulator
/// (2026-09-28; the review of every test, items 81, 87 and 90). The window itself cannot run here - it opens devices,
/// sockets and a message loop - so this does its jobs from the same readings, in the same order, and through the app's
/// own code wherever that code can be called:
/// <list type="bullet">
/// <item>the auto-tune's evidence, gathered each second as <c>MainForm.SnapshotLogIfDue</c> gathers it (the same 60 s
/// windows, the same per-lane readings, the same forgetting when a new stream opens or an output comes back), into the
/// app's own lane memories (<see cref="MainForm.LaneTuneMemory"/>);</item>
/// <item>the continuous auto-tune, every tick interval, as <c>ContinuousTuneTick</c> and <c>TickRoute</c> run it: the
/// baseline tick, the per-lane underrun counts, and the decision itself - <see cref="ContinuousAutoTune.Decide"/>, which
/// IS the app's - applied the way the slider applies it (the soft setter, on the slider's route);</item>
/// <item>the Total latency readout, composed by the app's own <see cref="MainForm.PickCaptureLatencyMs"/>,
/// <see cref="MainForm.LaneHardwareMs"/> and <see cref="MainForm.FormatMeasuredLatency"/> from the receiver's counters
/// and what the simulated devices report about themselves.</item>
/// </list>
/// Left out, because none happens in a simulated run: the user moving a slider, a wake from sleep, an output being
/// re-opened, and the idle-session prune (which runs on the wall clock).
/// </summary>
internal sealed class SimMainWindow
{
    private readonly AudioReceiver receiver;
    private readonly AudioSender sender;
    private readonly SimConfig config;
    private readonly int intervalSec;
    private readonly Func<RenderRoute, double> reportedOutputMs;
    private readonly Func<RenderRoute, double> outputQueueMs;
    private readonly Func<double> pingRttMs;

    // MainForm's windows, one reading a second each, RecentMaxGapWindowSeconds long.
    private readonly Queue<int> recentMaxGaps = new();
    private readonly Queue<int> recentRenderCbGaps = new();
    private readonly Queue<int> recentMinBuffers = new();
    private readonly Queue<int> recentCaptureCbGaps = new();
    private readonly Dictionary<RenderRoute, MainForm.LaneTuneMemory> memories = new()
    {
        [RenderRoute.Mixed] = new(), [RenderRoute.WasapiLane] = new(), [RenderRoute.AsioLane] = new(),
    };
    private readonly Dictionary<RenderRoute, int> sliders = new();
    private readonly Dictionary<RenderRoute, long> lastUnderruns = new();
    private long lastSessionsOpened;
    private double lastSourceChange = double.NegativeInfinity;
    private long lastPacketsSent;
    private double lastPacketsAt = -1;
    private double measuredEmitIntervalMs;
    private double measuredSendWaitMs = -1;
    private int? rttEwmaMs;

    /// <param name="reportedOutputMs">What each lane's simulated output device says its playback latency is.</param>
    /// <param name="outputQueueMs">What is waiting in each lane's device buffer (the WASAPI output stage; 0 on ASIO).</param>
    /// <param name="pingRttMs">One heartbeat round trip over the simulated network, in ms.</param>
    public SimMainWindow(AudioReceiver receiver, AudioSender sender, SimConfig config, bool autoTune, int intervalSec,
        int bufferMs, int wasapiLaneMs, Func<RenderRoute, double> reportedOutputMs, Func<RenderRoute, double> outputQueueMs,
        Func<double> pingRttMs)
    {
        this.receiver = receiver;
        this.sender = sender;
        this.config = config;
        AutoTune = autoTune;
        this.intervalSec = intervalSec;
        this.reportedOutputMs = reportedOutputMs;
        this.outputQueueMs = outputQueueMs;
        this.pingRttMs = pingRttMs;
        // The sliders, where the run starts them: one (Mixed) with no ASIO driver; the WASAPI and ASIO ones otherwise.
        if (config == SimConfig.WasapiOnly) sliders[RenderRoute.Mixed] = bufferMs;
        else
        {
            sliders[RenderRoute.WasapiLane] = wasapiLaneMs;
            sliders[RenderRoute.AsioLane] = bufferMs;
        }
    }

    public bool AutoTune { get; }

    /// <summary>When each lane's first tick ran - the baseline reading, which decides nothing (see TickRoute).</summary>
    public Dictionary<RenderRoute, double> BaselineAt { get; } = new();

    /// <summary>The tick interval, in seconds.</summary>
    public int IntervalSec => intervalSec;

    /// <summary>Every tick decision the auto-tune made: when, on which lane, from and to, and what it decided.</summary>
    public List<(double At, RenderRoute Route, int From, int To, ContinuousAutoTune.Verdict Verdict)> Ticks { get; } = new();

    /// <summary>The Total latency readout each second, per lane, as the box would show it (whole ms) and the figure it
    /// is made of: the jitter buffer as set, and every measured or reported term.</summary>
    public List<Readout> Readouts { get; } = new();

    public readonly record struct Readout(double At, RenderRoute Route, int ShownTotalMs, int SetMs, double HardwareMs,
        double CaptureMs, double SenderWaitMs, double WireMs, double QueueMs, double OutputMs);

    /// <summary>Where a lane's slider is now.</summary>
    public int SliderMs(RenderRoute route) => sliders.GetValueOrDefault(route);

    private AudioConfiguration AppConfiguration => config switch
    {
        SimConfig.WasapiOnly => AudioConfiguration.WasapiOnly,
        SimConfig.AsioOnly => AudioConfiguration.AsioOnly,
        _ => AudioConfiguration.Both,
    };

    /// <summary>MainForm.SnapshotLogIfDue's part in the audio, once a simulated second.</summary>
    public void EverySecond(double now)
    {
        var diag = receiver.TakeDiagnosticsSnapshot();
        var sendCbGapMs = sender.TakeMaxCaptureCallbackGapMs();
        // Each route's render period, read ONCE (the accessor resets as it reads) and shared.
        var periodByRoute = new Dictionary<RenderRoute, int>
        {
            [RenderRoute.WasapiLane] = receiver.MaxRenderCallbackGapMsFor(RenderRoute.WasapiLane),
            [RenderRoute.AsioLane] = receiver.MaxRenderCallbackGapMsFor(RenderRoute.AsioLane),
            [RenderRoute.Mixed] = receiver.MaxRenderCallbackGapMsFor(RenderRoute.Mixed),
        };
        // A heartbeat each second, smoothed as HeartbeatService smooths it (whole ms, 70/30).
        var rtt = (int)pingRttMs();
        rttEwmaMs = rttEwmaMs is null ? rtt : (int)(rttEwmaMs.Value * 0.7 + rtt * 0.3);

        if (diag.BufferSampleCount > 0 || diag.RenderReadCount > 0) ComposeReadout(now, diag, periodByRoute);

        // A new stream: new conditions, so every reading goes and every lane forgets what it learned (OnNewStreamSessions,
        // with no wake to put a tune back from). The tick waits one interval from here.
        var opened = receiver.SessionsOpenedCount;
        if (opened > lastSessionsOpened)
        {
            lastSessionsOpened = opened;
            lastSourceChange = now;
            recentMaxGaps.Clear();
            recentRenderCbGaps.Clear();
            recentCaptureCbGaps.Clear();
            recentMinBuffers.Clear();
            foreach (var memory in memories.Values) memory.Reset();
        }
        // An output that went quiet and came back re-earns its floor (NoteLaneAudibility, all three routes).
        foreach (var route in new[] { RenderRoute.Mixed, RenderRoute.WasapiLane, RenderRoute.AsioLane })
            memories[route].NoteLaneAudible(receiver.LaneIsConsuming(route));

        if (sendCbGapMs > 0)
        {
            recentCaptureCbGaps.Enqueue(sendCbGapMs);
            while (recentCaptureCbGaps.Count > MainForm.RecentMaxGapWindowSeconds) recentCaptureCbGaps.Dequeue();
        }
        // The sender's real emit cadence. (In the app this is the receiving machine's own sender - the simulator's one
        // sender stands for both ends sending alike, as Ed's two machines do.)
        var packets = sender.PacketsSent;
        if (lastPacketsAt >= 0 && packets > lastPacketsSent)
        {
            var elapsedMs = (now - lastPacketsAt) * 1000;
            var sent = packets - lastPacketsSent;
            if (elapsedMs > 0 && sent > 0) measuredEmitIntervalMs = elapsedMs / sent;
        }
        lastPacketsSent = packets;
        lastPacketsAt = now;
        if (sender.TakeMeasuredSendWaitMs() is var wait and >= 0) measuredSendWaitMs = wait;

        if (diag.PacketCount <= 0) return;
        Push(recentMaxGaps, diag.MaxArrivalGapMs, always: true);
        Push(recentRenderCbGaps, diag.MaxRenderCallbackGapMs, always: true);
        foreach (var lane in new[] { RenderRoute.WasapiLane, RenderRoute.AsioLane, RenderRoute.Mixed })
        {
            var memory = memories[lane];
            Push(memory.RenderCbGaps, periodByRoute[lane]);
            var laneBuffer = receiver.CurrentBufferMsFor(lane);
            Push(memory.MinBuffers, laneBuffer);
        }
        Push(recentMinBuffers, diag.BufferMinMs, always: true);
    }

    private static void Push(Queue<int> window, int value, bool always = false)
    {
        if (!always && value <= 0) return;
        window.Enqueue(value);
        while (window.Count > MainForm.RecentMaxGapWindowSeconds) window.Dequeue();
    }

    /// <summary>ContinuousTuneTick, every tick interval: the Mixed route with no ASIO driver, otherwise each lane that has
    /// audio - the WASAPI one on the main slider, the ASIO one on its own.</summary>
    public void TuneTick(double now)
    {
        if (!AutoTune) return;
        var frameMs = receiver.ActiveStreamFrameMs;
        if (frameMs is null) return;
        if (recentMaxGaps.Count < 2) return;
        if (now - lastSourceChange < intervalSec) return;
        if (config == SimConfig.WasapiOnly)
        {
            TickRoute(RenderRoute.Mixed, now, frameMs.Value);
            return;
        }
        if (receiver.HasSessionsForRoute(RenderRoute.WasapiLane)) TickRoute(RenderRoute.WasapiLane, now, frameMs.Value);
        if (receiver.HasSessionsForRoute(RenderRoute.AsioLane)) TickRoute(RenderRoute.AsioLane, now, frameMs.Value);
    }

    private int shownWasapiBufferMs = -1;
    private int shownAsioBufferMs = -1;

    /// <summary>Outputs the lowest-delay tune has, where the usual tune stands aside (as the app's TickContinuousAutoTune).</summary>
    public HashSet<RenderRoute> StandAside { get; } = [];

    private void TickRoute(RenderRoute route, double now, int frameMs)
    {
        if (StandAside.Contains(route)) return;
        var memory = memories[route];
        const int renderFloorMs = MainForm.TuneRenderFloorMs;
        const int safetyMs = MainForm.TuneSafetyMs;
        // Counters first: lifetime totals, the baseline moving only here.
        var underruns = route == RenderRoute.Mixed ? receiver.TuneBlockingUnderruns : receiver.TuneBlockingUnderrunsFor(route);
        var delta = underruns - lastUnderruns.GetValueOrDefault(route);
        lastUnderruns[route] = underruns;
        if (memory.NeedsUnderrunBaseline)
        {
            memory.NeedsUnderrunBaseline = false;
            BaselineAt[route] = now;
            return;
        }
        var laneRenderCbGaps = memory.RenderCbGaps.Count > 0 ? memory.RenderCbGaps : recentRenderCbGaps;
        var laneMinBuffers = memory.MinBuffers.Count > 0 ? memory.MinBuffers : recentMinBuffers;
        var current = sliders[route];
        var decision = ContinuousAutoTune.Decide(current, 1, 500, delta,
            recentMaxGaps, laneRenderCbGaps, laneMinBuffers, MainForm.CodecFloorMs(frameMs), renderFloorMs, safetyMs,
            ref memory.CleanTicks, memory.Creep);
        Ticks.Add((now, route, current, decision.Target, decision.Verdict));
        if (decision.Verdict is not (ContinuousAutoTune.Verdict.Raise or ContinuousAutoTune.Verdict.Move)) return;
        // The slider moved by the tuner: its handler takes the soft path on the slider's route.
        sliders[route] = decision.Target;
        receiver.SetMaxLatencyMsSoftFor(route, decision.Target);
    }

    private static readonly Regex ReadoutLine = new(
        @"(?:(?<lane>WASAPI|ASIO): )?jitter buffer (?<set>\d+) ms\. Sound card and hardware add (?<hw>\d+) ms\. Total latency approximately (?<total>\d+) ms one way\.",
        RegexOptions.Compiled);

    /// <summary>The Total latency readout, as the per-second tick composes it.</summary>
    private void ComposeReadout(double now, ReceiverDiagnostics.DiagSnapshot diag, Dictionary<RenderRoute, int> periodByRoute)
    {
        if (measuredEmitIntervalMs <= 0 || rttEwmaMs is null) return; // the first second: nothing measured yet
        // Per output, as the window does since N7: each output counts the capture of the stream sent for it.
        var captureEstimateMs = MainForm.CaptureBufferEstimateMsForTest(MainForm.SecondHighest(recentCaptureCbGaps));
        double CaptureMs(RenderRoute lane) => MainForm.LaneCaptureMs(receiver, sender, lane, captureEstimateMs);
        // As the window does: the wait measured sample by sample first (MainForm.SenderAccumulatorEstimateMs).
        var senderWaitMs = measuredSendWaitMs >= 0 ? Math.Clamp(measuredSendWaitMs, 0, 200) : Math.Clamp(measuredEmitIntervalMs / 2.0, 0.1, 200);
        var wireMs = rttEwmaMs.Value / 2.0;
        var listening = MainForm.PickListeningRoute(receiver.HasSessionsForRoute(RenderRoute.AsioLane),
            receiver.HasSessionsForRoute(RenderRoute.WasapiLane), AppConfiguration);
        var wasapiPeriodMs = periodByRoute[RenderRoute.WasapiLane];
        var asioPeriodMs = periodByRoute[RenderRoute.AsioLane];
        var lanePeriodMs = listening == RenderRoute.AsioLane ? asioPeriodMs : wasapiPeriodMs;
        if (lanePeriodMs <= 0) lanePeriodMs = diag.MaxRenderCallbackGapMs;
        double SharedMs(RenderRoute lane) => CaptureMs(lane) + senderWaitMs + wireMs;
        double Hardware(RenderRoute lane, int periodMs) => MainForm.LaneHardwareMs(receiver.HasSessionsForRoute(lane),
            reportedOutputMs(lane), periodMs, lanePeriodMs, SharedMs(lane), outputQueueMs(lane));
        var wasapiHw = Hardware(RenderRoute.WasapiLane, wasapiPeriodMs);
        var asioHw = Hardware(RenderRoute.AsioLane, asioPeriodMs);
        shownWasapiBufferMs = MainForm.ShownBufferMs(receiver, RenderRoute.WasapiLane, shownWasapiBufferMs);
        shownAsioBufferMs = MainForm.ShownBufferMs(receiver, RenderRoute.AsioLane, shownAsioBufferMs);
        var text = MainForm.FormatMeasuredLatency(AppConfiguration, shownWasapiBufferMs, wasapiHw, shownAsioBufferMs, asioHw);
        foreach (Match m in ReadoutLine.Matches(text))
        {
            var lane = m.Groups["lane"].Value switch
            {
                "WASAPI" => RenderRoute.WasapiLane,
                "ASIO" => RenderRoute.AsioLane,
                _ => config == SimConfig.WasapiOnly ? RenderRoute.WasapiLane : RenderRoute.AsioLane,
            };
            var hw = lane == RenderRoute.AsioLane ? asioHw : wasapiHw;
            Readouts.Add(new Readout(now, lane, int.Parse(m.Groups["total"].Value), int.Parse(m.Groups["set"].Value), hw,
                CaptureMs(lane), senderWaitMs, wireMs, outputQueueMs(lane), hw - SharedMs(lane) - outputQueueMs(lane)));
        }
    }
}
