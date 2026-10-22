using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App;

/// <summary>
/// The columns of the app's readings file (2026-10-22, issue 35): what the status row, the diagnostic line, the "streams"
/// line and the delay estimate said every second, as named columns that open in Excel. Each belongs to what it measures,
/// and is written only while that is in use (Ed: readings "only get logged when they get turned on") - an output that is
/// not ticked, a send lane not sending, the plugin not connected leave their cells empty. The minute rows sum each column
/// up as its <see cref="ReadingAgg"/> says.
/// </summary>
internal static class AppReadings
{
    // What a reading belongs to. "" is always in use (the program itself).
    public const string Receiving = "receiving";
    public const string Sending = "sending";
    public const string WasapiOutput = "WASAPI output";
    public const string AsioOutput = "ASIO output";
    public const string WasapiSend = "WASAPI send";
    public const string AsioSend = "ASIO send";
    public const string PluginSend = "plugin send";
    public const string CaptureSources = "capture sources";
    public const string Server = "server";
    public const string Latency = "latency";
    public const string Stream = "stream";

    private static ReadingColumn C(string name, ReadingAgg agg, string group = "") => new(name, agg, group);
    private static ReadingColumn T(string name, string group = "") => new(name, ReadingAgg.Last, group, Text: true);

    public static readonly ReadingsSchema Schema = new(
    [
        // The status row.
        C("connected", ReadingAgg.Last), C("sending", ReadingAgg.Last), C("receiving", ReadingAgg.Last), T("codec"), T("peers"),
        C("bufferMs", ReadingAgg.Mean, Receiving), C("receivedPackets", ReadingAgg.Last, Receiving), C("receivedKB", ReadingAgg.Last, Receiving),
        C("underrunsTotal", ReadingAgg.Last, Receiving), C("dropsTotal", ReadingAgg.Last, Receiving),
        C("opusFecRecoveries", ReadingAgg.Last, Receiving), C("opusUnrecoveredGaps", ReadingAgg.Last, Receiving), T("receiveDevice", Receiving),
        C("sentPackets", ReadingAgg.Last, Sending), C("sentKB", ReadingAgg.Last, Sending), T("sendDevice", Sending),
        C("captureCallbacks", ReadingAgg.Last, Sending),
        C("wasapiMaxMs", ReadingAgg.Last, WasapiOutput), C("wasapiTargetMs", ReadingAgg.Last, WasapiOutput),
        C("asioMaxMs", ReadingAgg.Last, AsioOutput), C("asioTargetMs", ReadingAgg.Last, AsioOutput),
        // Receiving: the diagnostic line's half for this machine's playback.
        C("bufAvgMs", ReadingAgg.Mean, Receiving), C("bufMinMs", ReadingAgg.Min, Receiving), C("bufMaxMs", ReadingAgg.Max, Receiving),
        C("maxGapMs", ReadingAgg.Max, Receiving), C("renderCbGapMs", ReadingAgg.Max, Receiving), C("maxReadMs", ReadingAgg.Max, Receiving),
        C("reads", ReadingAgg.Sum, Receiving), C("rxDispMs", ReadingAgg.Max, Receiving), C("rxNetGapMs", ReadingAgg.Max, Receiving),
        C("recvMs", ReadingAgg.Mean, Receiving), C("renderMs", ReadingAgg.Mean, Receiving),
        C("wasapiRenderMs", ReadingAgg.Mean, WasapiOutput), C("asioRenderMs", ReadingAgg.Mean, AsioOutput),
        C("wasapiUnderruns", ReadingAgg.Sum, WasapiOutput), C("asioUnderruns", ReadingAgg.Sum, AsioOutput),
        C("wasapiGapsTotal", ReadingAgg.Last, WasapiOutput), C("asioGapsTotal", ReadingAgg.Last, AsioOutput),
        C("gaps", ReadingAgg.Sum, Receiving), C("longestGapMs", ReadingAgg.Max, Receiving),
        C("trimBytes", ReadingAgg.Last, Receiving), C("trimCount", ReadingAgg.Last, Receiving), C("trims", ReadingAgg.Sum, Receiving),
        C("drainBytes", ReadingAgg.Last, Receiving), C("overflowBytes", ReadingAgg.Last, Receiving), C("rejectedPackets", ReadingAgg.Last, Receiving),
        C("concealments", ReadingAgg.Sum, Receiving), C("shortReads", ReadingAgg.Sum, Receiving), C("deviceGulps", ReadingAgg.Sum, Receiving),
        C("driftErrorFrames", ReadingAgg.Mean, Receiving),
        C("stepPostDecode", ReadingAgg.Max, Receiving), C("stepPostRing", ReadingAgg.Max, Receiving), C("stepPostResampler", ReadingAgg.Max, Receiving),
        C("spikes", ReadingAgg.Sum, Receiving),
        C("wireInOrder", ReadingAgg.Sum, Receiving), C("wireMissed", ReadingAgg.Sum, Receiving), C("wireReordered", ReadingAgg.Sum, Receiving),
        C("wireDuplicated", ReadingAgg.Sum, Receiving), C("pcmRejected", ReadingAgg.Last, Receiving), C("pcmDiscarded", ReadingAgg.Last, Receiving),
        // Sending.
        C("sendCbGapMs", ReadingAgg.Max, Sending), C("emitMs", ReadingAgg.Max, Sending), C("sendCallMs", ReadingAgg.Max, Sending),
        C("captureMs", ReadingAgg.Mean, Sending), C("sendMs", ReadingAgg.Mean, Sending), C("capturePeak", ReadingAgg.Max, Sending),
        C("framesSent", ReadingAgg.Sum, Sending), C("stepRawCapture", ReadingAgg.Max, Sending), C("stepPreEncode", ReadingAgg.Max, Sending),
        C("clipped", ReadingAgg.Sum, Sending),
        C("stepPreEncodeWasapi", ReadingAgg.Max, WasapiSend), C("emitGapWasapiMs", ReadingAgg.Max, WasapiSend),
        C("stepPreEncodeAsio", ReadingAgg.Max, AsioSend), C("emitGapAsioMs", ReadingAgg.Max, AsioSend), T("asioLaneSends", AsioSend),
        C("capturePeakPlugin", ReadingAgg.Max, PluginSend), C("emitGapPluginMs", ReadingAgg.Max, PluginSend),
        T("sourceDrift", CaptureSources), C("peersAskingJamulusStyle", ReadingAgg.Last),
        C("serverPathGapMs", ReadingAgg.Max, Server), C("serverDispatchMs", ReadingAgg.Max, Server),
        // The program itself.
        C("gc0", ReadingAgg.Sum), C("gc1", ReadingAgg.Sum), C("gc2", ReadingAgg.Sum), C("cpuPercent", ReadingAgg.Mean),
        C("managedMB", ReadingAgg.Last), C("workingSetMB", ReadingAgg.Last), C("allocKBps", ReadingAgg.Mean), C("privateMB", ReadingAgg.Last),
        C("gcHeapMB", ReadingAgg.Last), C("gcFragmentedMB", ReadingAgg.Last), C("gcCommittedMB", ReadingAgg.Last),
        C("handles", ReadingAgg.Last), C("threads", ReadingAgg.Last),
        // The delay estimate, one way.
        C("latencyMs", ReadingAgg.Mean, Latency), C("latencyMaxMs", ReadingAgg.Max, Latency),
        C("latencyCaptureMs", ReadingAgg.Mean, Latency), T("latencyCaptureFrom", Latency), C("latencySendMs", ReadingAgg.Mean, Latency),
        C("latencyWireMs", ReadingAgg.Mean, Latency), C("latencyQueueMs", ReadingAgg.Mean, Latency),
        C("latencyOutputQueueMs", ReadingAgg.Mean, Latency), C("latencyRenderMs", ReadingAgg.Mean, Latency), T("latencyRenderFrom", Latency),
        // A stream on one output (the "streams" line): only in a stream's own row.
        C("packets", ReadingAgg.Sum, Stream), C("lostPackets", ReadingAgg.Sum, Stream), C("arrivalGapMs", ReadingAgg.Max, Stream),
        C("arrivalGapPeakMs", ReadingAgg.Max, Stream), C("arrivalGapUsedMs", ReadingAgg.Max, Stream), C("needMs", ReadingAgg.Mean, Stream),
        C("ownMs", ReadingAgg.Last, Stream), C("playsAtMs", ReadingAgg.Mean, Stream), C("outputTargetMs", ReadingAgg.Last, Stream),
        C("depthMs", ReadingAgg.Mean, Stream), C("depthMinMs", ReadingAgg.Min, Stream), C("streamUnderruns", ReadingAgg.Sum, Stream),
        C("streamGaps", ReadingAgg.Sum, Stream), C("streamLongestGapMs", ReadingAgg.Max, Stream),
    ]);

    /// <summary>What a stream's output is called in the readings file and the log: WASAPI or ASIO.</summary>
    public static string OutputName(RenderRoute route) => route == RenderRoute.AsioLane ? "ASIO" : "WASAPI";

    /// <summary>What is in use this second, so each reading is written only while the thing it measures is: the outputs
    /// by the audio configuration (the outputs ticked, not the mode flag - an ASIO driver chosen with no ASIO output ticked
    /// is WASAPI only), the send lanes by whether they sent in the last two seconds.</summary>
    public static List<string> GroupsInUse(AudioConfiguration configuration, bool receiving, bool sending,
        (bool Wasapi, bool Asio, bool Plugin) lanesSending, bool captureSources, bool onServer)
    {
        var groups = new List<string>();
        if (receiving)
        {
            groups.Add(Receiving);
            if (configuration.UsesWasapi()) groups.Add(WasapiOutput);
            if (configuration.UsesAsio()) groups.Add(AsioOutput);
        }
        if (sending)
        {
            groups.Add(Sending);
            if (lanesSending.Wasapi) groups.Add(WasapiSend);
            if (lanesSending.Asio) groups.Add(AsioSend);
            if (lanesSending.Plugin) groups.Add(PluginSend);
            if (captureSources) groups.Add(CaptureSources);
        }
        if (receiving || sending) groups.Add(Latency);
        if (onServer) groups.Add(Server);
        return groups;
    }
}
