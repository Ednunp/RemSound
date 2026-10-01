using System.Net;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Each connected peer's total latency, one way, beside its ping in the status line (Ed, 2026-09-29, after the live test:
/// "latency to each peer in their status, like ping"). Added up the way the Total latency box adds up an output's, but for
/// that peer's own streams: the capture their sender states, the sending wait, half THEIR ping, the buffer THEIR stream
/// plays at on each output, and that output's own queue and delay. A peer heard on both kinds of output has a figure for
/// each; one not sending anything has its ping alone.
/// </summary>
public sealed partial class MainForm
{
    /// <summary>Each output's render period as the per-second tick last read it (reading it again would reset it).</summary>
    private Dictionary<RenderRoute, int> lastLanePeriodMs = [];

    /// <summary>Gate seam: the peers' health, in place of the heartbeat's.</summary>
    internal Func<IEnumerable<PeerHealth>>? PeerHealthForTest;

    /// <summary>One peer's total latency on each kind of output its streams play on - ASIO, or WASAPI (the one output with no
    /// ASIO driver) - the deepest of its streams on each.</summary>
    private List<(bool Asio, double Ms)> PeerDelays(IPEndPoint endpoint, int? rttMs)
    {
        // Only the outputs this setup has: with no ASIO driver there is no ASIO figure, and ASIO only has no WASAPI one.
        var configuration = ActiveAudioConfiguration();
        var streams = receiver.PlayingFrom(endpoint)
            .Where(st => configuration switch
            {
                AudioConfiguration.WasapiOnly => st.Route != RenderRoute.AsioLane,
                AudioConfiguration.AsioOnly => st.Route == RenderRoute.AsioLane,
                _ => true,
            })
            .ToList();
        if (streams.Count == 0) return [];
        var sendMs = SenderAccumulatorEstimateMs();
        var wireMs = (rttMs ?? 0) / 2.0;
        var captureEstimateMs = CaptureBufferEstimateMs(SecondHighest(recentCaptureCbGaps));
        return streams
            .GroupBy(st => st.Route == RenderRoute.AsioLane)
            .Select(g => (g.Key, g.Max(st => PeerDelayMs(
                PickCaptureLatencyMs(st.CaptureMs, sender.CaptureLatencyMsForOutput(st.Route), captureEstimateMs),
                sendMs, wireMs, st.BufferMs, OutputMsFor(st.Route)))))
            .ToList();
    }

    /// <summary>What an output adds after the buffer: its queue, and the delay the device states - or two of its periods
    /// when it states none (the Total latency box's rule).</summary>
    private double OutputMsFor(RenderRoute route)
    {
        var reported = receiver.ReportedOutputLatencyMsFor(route);
        return receiver.OutputQueueMsFor(route)
             + (reported > 0 ? reported : RenderBufferEstimateMs(lastLanePeriodMs.GetValueOrDefault(route)));
    }

    /// <summary>The sum, one way, from their microphone to your ears.</summary>
    internal static double PeerDelayMs(double captureMs, double sendMs, double wireMs, int bufferMs, double outputMs) =>
        captureMs + sendMs + wireMs + bufferMs + outputMs;

    /// <summary>A peer's line in the status: its ping, and its total latency - one figure, or one for each kind of output,
    /// ASIO first - rounded to 5 ms as the ping is.</summary>
    internal static string PeerStatusLine(string label, int? rttMs, IReadOnlyList<(bool Asio, double Ms)> delays)
    {
        var line = $"  {label}: ping {(rttMs is { } r ? $"{r} ms" : "unknown")}";
        return delays.Count switch
        {
            0 => line,
            1 => $"{line}, total latency about {RoundToFive((int)Math.Round(delays[0].Ms))} ms",
            _ => $"{line}, total latency about " + string.Join(", ", delays.OrderBy(d => d.Asio ? 0 : 1)
                     .Select(d => $"{RoundToFive((int)Math.Round(d.Ms))} ms on {(d.Asio ? "ASIO" : "WASAPI")}")),
        };
    }

    internal List<(bool Asio, double Ms)> PeerDelaysForTest(IPEndPoint endpoint, int? rttMs) => PeerDelays(endpoint, rttMs);
}
