using NAudio.Wave;
using RemSound.Core;

namespace RemSound.Receiver;

/// <summary>
/// Render backend that runs a WASAPI <see cref="MultiOutputPlayout"/> and an
/// <see cref="AsioRenderBackend"/> in parallel. Two pipeline shapes are reachable today:
/// <list type="bullet">
///   <item>WasapiOnly: WASAPI child reads <see cref="PlayoutEngine"/> directly; no ASIO in
///         the path. Used when no ASIO driver is selected.</item>
///   <item>BothIndependent: WASAPI and ASIO children each read their own lane-filtered surface
///         from PlayoutEngine (<see cref="PlayoutEngine.WasapiLaneOutput"/> /
///         <see cref="PlayoutEngine.AsioLaneOutput"/>) — no shared source, no cache, and each
///         lane runs at its native callback rate.</item>
/// </list>
/// BothIndependent without a driver name runs as WasapiOnly.
/// </summary>
internal sealed class CompositeRenderBackend : IRenderBackend
{
    private readonly PlayoutEngine source;
    private readonly Action<string>? onDiagnostic;
    private readonly object gate = new();

    // In BothIndependent each backend reads directly from its own lane-filtered source
    // (PlayoutEngine.WasapiLaneOutput / AsioLaneOutput), which filter PlayoutEngine's session
    // snapshot by RenderRoute. No shared cache, and neither lane pays a cache-age penalty when the
    // other is also playing.
    private readonly MultiOutputPlayout? wasapi;
    private readonly AsioRenderBackend? asio;
    // What is actually TICKED, so the log can name the configuration rather than only the mode.
    private bool wasapiTicked;
    private bool asioTicked;
    private readonly string? asioDriverName;
    private readonly RemSound.Core.AudioMode mode;

    private bool started;

    public CompositeRenderBackend(RemSound.Core.AudioMode mode, string? asioDriverName, PlayoutEngine source, Action<string>? onDiagnostic = null)
    {
        this.source = source;
        this.onDiagnostic = onDiagnostic;
        this.asioDriverName = asioDriverName;

        // The ASIO lane needs a driver name; without one there is no ASIO lane to build.
        var usesAsio = mode != RemSound.Core.AudioMode.WasapiOnly && !string.IsNullOrEmpty(asioDriverName);
        this.mode = usesAsio ? RemSound.Core.AudioMode.BothIndependent : RemSound.Core.AudioMode.WasapiOnly;

        if (!usesAsio)
        {
            // MultiOutputPlayout reads PlayoutEngine's all-sessions Read directly, which sums every
            // stream whatever lane it is tagged with — the single-lane world.
            wasapi = new MultiOutputPlayout(source, msg => onDiagnostic?.Invoke($"wasapi out: {msg}"));
        }
        else
        {
            // BothIndependent. Each backend reads its OWN lane-filtered source from
            // PlayoutEngine — no FanOut, no shared cache, no inter-lane interference. The
            // WasapiLaneOutput surface filters PlayoutEngine's session snapshot down to
            // route=WasapiLane sessions; AsioLaneOutput does the same for route=AsioLane.
            // Each consumer's Read only advances its own lane's sessions, so the two
            // consumers can run on independent threads at independent rates without one
            // starving the other. Crucially: neither lane pays a cache-age overhead. ASIO
            // reads its own audio at its native callback latency, exactly as it would in an
            // ASIO-only configuration — even when WASAPI is also actively playing.
            // The previous implementation wrapped a single FanOut around the whole engine,
            // which (a) made both lanes play the combined mix instead of per-lane audio and
            // (b) added up to one WASAPI tick (~10 ms) of cache-age latency to whichever
            // consumer was the slower of the two.
            wasapi = new MultiOutputPlayout(source.WasapiLaneOutput, msg => onDiagnostic?.Invoke($"wasapi out: {msg}"));
            asio = new AsioRenderBackend(asioDriverName!, source.AsioLaneOutput, msg => onDiagnostic?.Invoke($"asio out: {msg}"));
        }
    }

    public bool IsRunning => started;

    /// <summary>Any WASAPI output sitting dead awaiting a re-open. ASIO is not included: an ASIO output that failed to
    /// open is missing from the backend's ActiveDeviceIds, which the heal reads, and the backend itself tries it again
    /// after its back-off (AsioRenderBackend.ReopenBackoff). See
    /// <see cref="MultiOutputPlayout.HasFaultedOutput"/> for what this is for. 2026-08-26.</summary>
    public bool HasFaultedOutput => wasapi?.HasFaultedOutput ?? false;

    public string ActiveDeviceSummary
    {
        get
        {
            var parts = new List<string>();
            if (wasapi is not null)
            {
                var wSummary = wasapi.ActiveDeviceSummary;
                if (wSummary != "(none)") parts.Add(wSummary);
            }
            if (asio is not null)
            {
                var aSummary = asio.ActiveDeviceSummary;
                if (aSummary != "(none)") parts.Add(aSummary);
            }
            return parts.Count == 0 ? "(none)" : string.Join(" + ", parts);
        }
    }

    public IReadOnlyList<string> ActiveDeviceIds
    {
        get
        {
            var combined = new List<string>();
            if (wasapi is not null) combined.AddRange(wasapi.ActiveDeviceIds);
            if (asio is not null) combined.AddRange(asio.ActiveDeviceIds);
            return combined;
        }
    }

    /// <summary>Per-lane, because both lanes run AT THE SAME TIME and the readout reports them apart.
    /// Ed leaves both active and switches between them, so answering with one lane's figure for both
    /// would hide the very difference the box exists to show. See
    /// <see cref="IRenderBackend.ReportedOutputLatencyMsFor"/>. 2026-08-24.</summary>
    public double ReportedOutputLatencyMsFor(RenderRoute route) =>
        ForLane(route, wasapi?.ReportedOutputLatencyMsFor(RenderRoute.WasapiLane) ?? 0, asio?.ReportedOutputLatencyMsFor(RenderRoute.AsioLane) ?? 0);

    /// <inheritdoc cref="ReportedOutputLatencyMsFor"/>
    public double OutputQueueMsFor(RenderRoute route) =>
        // ASIO pulls straight from the engine, so its queue is zero by construction.
        ForLane(route, wasapi?.OutputQueueMsFor(RenderRoute.WasapiLane) ?? 0, asio?.OutputQueueMsFor(RenderRoute.AsioLane) ?? 0);

    /// <summary>The WASAPI lane's stages. The ASIO child has none by construction, so this is simply
    /// the WASAPI child's list — see <see cref="WasapiOutputStage"/>.</summary>
    public IReadOnlyList<WasapiOutputStage> OutputStages() =>
        wasapi?.OutputStages() ?? Array.Empty<WasapiOutputStage>();

    /// <summary>The lane-picking rule, on its own so the gate can prove it actually PICKS.
    ///
    /// <para>This is a seam rather than an inline conditional because the bug it guards against is
    /// invisible with no hardware attached: a version that returned the same figure for both lanes
    /// passed a test that only checked "everything reads zero when nothing is open". Given two
    /// different values it is obvious; given two zeroes it is not. So the test feeds it two different
    /// values. 2026-08-24.</para></summary>
    internal static double ForLane(RenderRoute route, double wasapiValue, double asioValue) =>
        route == RenderRoute.AsioLane ? asioValue : wasapiValue;

    public void Start()
    {
        lock (gate)
        {
            if (started) return;
            // `started` is set BEFORE either child starts, and each start is guarded. Previously
            // both ran bare and `started = true` came after: if the ASIO start threw, the WASAPI
            // producer was already running but `started` stayed false, so Stop() early-returned and
            // that producer could never be stopped — a stuck output and a leaked task for the rest
            // of the session. One lane failing must not strand the other. 2026-08-23 audit, R6.
            started = true;
            try { wasapi?.Start(); }
            catch (Exception ex) { onDiagnostic?.Invoke($"wasapi render failed to start: {ex.GetType().Name}: {ex.Message}"); }
            try { asio?.Start(); }
            catch (Exception ex) { onDiagnostic?.Invoke($"asio render failed to start: {ex.GetType().Name}: {ex.Message}"); }
            onDiagnostic?.Invoke($"composite render started ({RenderStartLabel(ModeLabel(), wasapiTicked, asioTicked)})");
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            if (!started) return;
            try { wasapi?.Stop(); } catch { /* ignore */ }
            try { asio?.Stop(); } catch { /* ignore */ }
            started = false;
        }
    }

    public void SetOutputDevices(IReadOnlyList<string> deviceIds)
    {
        // Split by id format: ASIO ids start with "asio:". WASAPI ids are MMDevice strings.
        var wasapiIds = new List<string>();
        var asioIds = new List<string>();
        foreach (var id in deviceIds)
        {
            if (RemSound.Core.AsioDeviceId.TryParse(id, out _))
            {
                asioIds.Add(id);
            }
            else
            {
                wasapiIds.Add(id);
            }
        }
        if (wasapi is not null) wasapi.SetOutputDevices(wasapiIds);
        if (asio is not null) asio.SetOutputDevices(asioIds);
        // The "skip the pull when no outputs are ticked" behaviour lives inside MultiOutputPlayout's
        // producer loop, which skips source.Read while no output is open.

        // Tell the PlayoutEngine which lanes have an active output device. ReadForRoute
        // uses this to route "orphan" sessions (those whose announced lane has no active
        // output) through whichever lane IS being read — so a peer that announced WASAPI
        // is still audible on a receiver who has only ASIO outputs ticked. Without this
        // signal those sessions stay stuck in their session ring and the user just hears
        // silence from that peer. 2026-05-15.
        source.SetLaneActive(RenderRoute.WasapiLane, wasapiIds.Count > 0);
        // An ASIO lane exists only when there is an ASIO backend to read it. With no driver chosen, a
        // stale "asio:" id still in the ticked list used to flag the lane active anyway, and the engine
        // then made a mirror copy of every stream for a lane nothing would ever read: fed for good, and
        // summed on top of its primary by the all-sessions read. 2026-09-13 review, finding 4.
        source.SetLaneActive(RenderRoute.AsioLane, asio is not null && asioIds.Count > 0);

        // Record what is ticked, and say so when it changes. Without this the log only ever named
        // the MODE, which claims "WASAPI + ASIO" whenever an ASIO driver is chosen — so an ASIO-only
        // session read as though both lanes were live. See RenderStartLabel. 2026-08-24.
        var previous = RenderStartLabel(ModeLabel(), wasapiTicked, asioTicked);
        wasapiTicked = wasapiIds.Count > 0;
        asioTicked = asioIds.Count > 0;
        var now = RenderStartLabel(ModeLabel(), wasapiTicked, asioTicked);
        if (started && now != previous) onDiagnostic?.Invoke($"output {now}");
    }

    public void Dispose()
    {
        Stop();
        try { wasapi?.Dispose(); } catch { /* ignore */ }
        try { asio?.Dispose(); } catch { /* ignore */ }
    }

    private string ModeLabel() => mode == RemSound.Core.AudioMode.BothIndependent
        ? "independent lanes (WASAPI + ASIO, no mix)"
        : "fast (WASAPI direct)";

    /// <summary>
    /// What the render log says it started. The MODE alone is not enough — saying only the mode
    /// actively misleads.
    ///
    /// <para>"independent lanes (WASAPI + ASIO, no mix)" is the label for BothIndependent, and
    /// BothIndependent only means an ASIO driver has been CHOSEN. So an ASIO-only rig, with every
    /// WASAPI output unticked, logged a line naming WASAPI. On 2026-08-24 that line convinced me Ed
    /// had both lanes live while he was telling me he had turned WASAPI off. He was right and the
    /// log was wrong: the mode-not-configuration trap, this time written into the diagnostics rather
    /// than into the code — where it costs a misdiagnosis instead of a bug.</para>
    ///
    /// <para>The line now leads with the CONFIGURATION — what is actually ticked, and therefore what
    /// is actually rendering — keeping the mode alongside for the driver question it really
    /// answers.</para>
    /// </summary>
    internal static string RenderStartLabel(string modeLabel, bool wasapiTicked, bool asioTicked) =>
        $"configuration={RemSound.Core.AudioConfigurations.From(wasapiTicked, asioTicked).Describe()}, mode={modeLabel}";
}
