using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Per-stream buffers and the lowest-delay auto-tune - both from the latency lab (2026-09-26), both settled by the live
/// test with Andre (2026-09-29): per-stream buffers became normal behaviour, and the lab's lean margins and "keep dropouts
/// rare" tuning became one tick box beside the auto-tune interval, on WASAPI as well as ASIO (Ed: "make that wasapi as well
/// and put it as one switch next to auto tune check interval"). Once a second, with the window's other per-second work.
/// </summary>
public sealed partial class MainForm
{
    private long streamsLastUnderrunsAsio = -1;
    private long streamsLastUnderrunsWasapi = -1;
    private readonly DropoutTuner wasapiDropoutTuner = new();
    private readonly DropoutTuner asioDropoutTuner = new();
    private long lastGapsMixed = -1;
    private long lastGapsWasapi = -1;
    private long lastGapsAsio = -1;
    /// <summary>The profile's <see cref="Profile.AutoTuneLowestDelay"/>, as its tick box holds it.</summary>
    private bool lowestDelayTune;

    /// <summary>The tick box moved, or a profile set it: the receiver's margins follow at once, and every lane's tuners
    /// start afresh - the usual auto-tune takes a new baseline rather than acting on what happened while it stood aside.</summary>
    private void ApplyLowestDelayTune(bool on)
    {
        lowestDelayTune = on;
        receiver.LeanTuning = on;
        wasapiDropoutTuner.Reset();
        asioDropoutTuner.Reset();
        foreach (var route in new[] { RenderRoute.Mixed, RenderRoute.WasapiLane, RenderRoute.AsioLane })
            TuneMemoryFor(route).NeedsUnderrunBaseline = true;
    }

    /// <summary>Whether the usual auto-tune has <paramref name="route"/> now: its auto-tune on, audio on it, not held after a
    /// wake - and the lowest-delay tune off. Exactly one of this and <see cref="LowestDelayTuneHas"/> has a lane.</summary>
    private bool UsualTuneHas(RenderRoute route, bool autoTuneOn, bool hasAudio) =>
        autoTuneOn && hasAudio && TuneLaneAllowed(route, DateTime.UtcNow) && !lowestDelayTune;

    /// <summary>Whether the lowest-delay tune has <paramref name="route"/> now: the same, with the tick box on.</summary>
    private bool LowestDelayTuneHas(RenderRoute route, bool autoTuneOn, bool hasAudio) =>
        autoTuneOn && hasAudio && TuneLaneAllowed(route, DateTime.UtcNow) && lowestDelayTune;

    /// <summary>Whether the auto-tune is on for the output a stream plays on: the one tick box with no ASIO driver chosen (every
    /// stream plays on the one output); with one, the ASIO output's own box for the ASIO lane, and the WASAPI one's for the
    /// rest. Per-stream buffers follow it (Ed, 2026-09-29: with auto-tune off, the number you set is what you get).</summary>
    private bool AutoTuneOnFor(RenderRoute route) =>
        settings.LoadAudioMode() == AudioMode.BothIndependent && route == RenderRoute.AsioLane
            ? settings.LoadContinuousAutoTuneAsioEnabled()
            : continuousTuneEnabled;

    /// <summary>The usual auto-tune's margins: a 2 ms render floor and 5 ms of safety. The lean ones (1 and 2) go with the
    /// lowest-delay tune, into each stream's own need (<see cref="AudioReceiver.LeanTuning"/>).</summary>
    internal const int TuneRenderFloorMs = 2;
    internal const int TuneSafetyMs = 5;

    /// <summary>The usual auto-tune's codec floor: one and a half frames.</summary>
    internal static int CodecFloorMs(int frameMs) => (int)Math.Ceiling(1.5 * frameMs);

    /// <summary>The second-worst render-callback gap this output has had over the tuner's lookback, or 0 when it has
    /// no readings yet - the per-stream need floors it anyway.</summary>
    private int RenderCbMsFor(RenderRoute route)
    {
        var memory = TuneMemoryFor(route);
        var window = memory.RenderCbGaps.Count > 0 ? memory.RenderCbGaps : recentRenderCbGaps;
        int peak = 0, second = 0, n = 0;
        var skip = Math.Max(0, window.Count - StreamArrivalLookbackSeconds);
        var i = 0;
        foreach (var g in window)
        {
            if (i++ < skip) continue;
            n++;
            if (g > peak) { second = peak; peak = g; }
            else if (g > second) second = g;
        }
        return n >= 2 ? second : peak;
    }

    private const int StreamArrivalLookbackSeconds = 15;

    /// <summary>
    /// The lowest-delay auto-tune, once a second, on every lane whose auto-tune is on (see <see cref="DropoutTuner"/>):
    /// the lane's audible gaps that second in, its buffer out. The usual tune (TickRoute) stands aside on those lanes
    /// meanwhile. The slider is moved the way the usual tune moves it, under the suppress flag so it is not taken for the
    /// user, and its own handler pushes the value to the receiver. A move by the user starts it afresh.
    /// </summary>
    private void TickLowestDelayTune()
    {
        if (settings.LoadAudioMode() == AudioMode.BothIndependent)
        {
            TickLowestDelayRoute(RenderRoute.WasapiLane, maxLatencyBox, "WASAPI", wasapiDropoutTuner, ref lastGapsWasapi,
                ref suppressUserSliderMoveTracking, lastUserSliderMoveUtc, continuousTuneEnabled);
            TickLowestDelayRoute(RenderRoute.AsioLane, maxLatencyAsioBox, "ASIO", asioDropoutTuner, ref lastGapsAsio,
                ref suppressUserAsioSliderMoveTracking, lastUserAsioSliderMoveUtc, settings.LoadContinuousAutoTuneAsioEnabled());
        }
        else
        {
            TickLowestDelayRoute(RenderRoute.Mixed, maxLatencyBox, "", wasapiDropoutTuner, ref lastGapsMixed,
                ref suppressUserSliderMoveTracking, lastUserSliderMoveUtc, continuousTuneEnabled);
        }
    }

    /// <summary>Whether a lane has audio arriving: any stream at all on the one output with no ASIO driver.</summary>
    private bool HasAudioOn(RenderRoute route) =>
        HasAudioForTest?.Invoke(route) ?? (route == RenderRoute.Mixed ? receiver.ActiveStreamFrameMs is not null : receiver.HasSessionsForRoute(route));

    private void TickLowestDelayRoute(RenderRoute route, NumericUpDown slider, string label, DropoutTuner tuner, ref long lastGaps,
        ref bool suppressFlag, DateTime lastUserMoveUtc, bool autoTuneOn)
    {
        // The counter first, before any return: a lifetime total whose baseline moves only here.
        var gaps = DropoutGapsForTest?.Invoke(route) ?? (route == RenderRoute.Mixed ? receiver.DropoutGaps : receiver.DropoutGapsFor(route));
        var newGaps = lastGaps < 0 ? 0 : (int)Math.Max(0, gaps - lastGaps);
        lastGaps = gaps;
        if (!LowestDelayTuneHas(route, autoTuneOn, HasAudioOn(route)))
        {
            tuner.Reset();
            return;
        }
        if (DateTime.UtcNow - lastUserMoveUtc < TimeSpan.FromSeconds(5)) { tuner.Reset(); return; }
        var current = (int)slider.Value;
        // Never below what the output takes at a time (its render period: 10 ms on WASAPI, the ASIO buffer on ASIO): under
        // that every pull comes up short, and on an on-target ring those short pulls are not counted as gaps, so the tuner
        // sank to 1 ms breaking up all the way (the simulator, WASAPI only, 2026-09-29).
        var floor = Math.Clamp(RenderCbMsFor(route), (int)slider.Minimum, (int)slider.Maximum);
        var next = tuner.Tick(newGaps, current, floor, (int)slider.Maximum);
        if (next == current) return;
        suppressFlag = true;
        try { slider.Value = next; }
        finally { suppressFlag = false; }
        var prefix = string.IsNullOrEmpty(label) ? "lowest-delay auto-tune" : $"lowest-delay auto-tune {label}";
        logFile.Event($"{prefix}: {current} ms -> {next} ms ({tuner.LastReason}) [{ActiveAudioConfiguration().Describe()}]");
    }

    /// <summary>Once a second: the lowest-delay auto-tune, the per-stream buffers, and one "streams" line - every stream's
    /// arrivals and what it plays at on each output, plus each output's new underruns and audible gaps.</summary>
    private void TickStreamBuffers()
    {
        NoteNetworkSecond();
        if (!receiver.IsRunning) return;
        TickLowestDelayTune();
        var reports = receiver.UpdatePerStreamNeeds(RenderCbMsFor, AutoTuneOnFor);
        if (reports.Count == 0) return;
        var asioUnderruns = receiver.TuneBlockingUnderrunsFor(RenderRoute.AsioLane);
        var wasapiUnderruns = receiver.TuneBlockingUnderrunsFor(RenderRoute.WasapiLane);
        var asioNew = streamsLastUnderrunsAsio < 0 ? 0 : asioUnderruns - streamsLastUnderrunsAsio;
        var wasapiNew = streamsLastUnderrunsWasapi < 0 ? 0 : wasapiUnderruns - streamsLastUnderrunsWasapi;
        streamsLastUnderrunsAsio = asioUnderruns;
        streamsLastUnderrunsWasapi = wasapiUnderruns;
        var streams = string.Join(" | ", reports.Select(r =>
            $"{r.Stream} pkts={r.Packets} gap={r.GapMs}ms gap-peak={r.GapPeakMs}ms gap-used={r.GapUsedMs}ms {r.Replicas}"));
        var serverGap = sender.TakeInboundMaxGapMs();
        var serverDispatch = sender.TakeInboundMaxDispatchMs();
        var (asioCustom, customFrames) = sender.AsioLaneLabState;
        var sending = !sender.IsRunning ? "not sending"
            : asioCustom ? $"ASIO lane sends Jamulus-style to {sender.CustomOpusTargetCount} (frames {customFrames}) and the codec setting to the rest"
            : "ASIO lane sends the codec setting";
        logFile.Event($"streams [{ActiveAudioConfiguration().Describe()}] [lowest-delay auto-tune {(lowestDelayTune ? "on" : "off")}]: "
                    + $"underruns this second ASIO={asioNew} WASAPI={wasapiNew}; audible gaps so far ASIO={receiver.DropoutGapsFor(RenderRoute.AsioLane)} "
                    + $"WASAPI={receiver.DropoutGapsFor(RenderRoute.WasapiLane)}; server path gap={serverGap}ms dispatch={serverDispatch}ms; "
                    + $"{sending}; peers asking for Jamulus-style={CustomOpusPeers.Count} | {streams}");
    }

    internal bool LowestDelayTuneForTest => lowestDelayTune;
    internal bool AutoTuneOnForTest(RenderRoute route) => AutoTuneOnFor(route);
    internal void TickStreamBuffersForTest() => TickStreamBuffers();
    internal void TickLowestDelayTuneForTest() => TickLowestDelayTune();
    /// <summary>Gate seams: a lane's audible gaps so far, and whether it has audio, in place of the receiver's.</summary>
    internal Func<RenderRoute, long>? DropoutGapsForTest;
    internal Func<RenderRoute, bool>? HasAudioForTest;
    /// <summary>Which tune has a lane, with its auto-tune on and audio arriving.</summary>
    internal (bool Usual, bool LowestDelay) TuneOwnerForTest(RenderRoute route) =>
        (UsualTuneHas(route, autoTuneOn: true, hasAudio: true), LowestDelayTuneHas(route, autoTuneOn: true, hasAudio: true));
}
