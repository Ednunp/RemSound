using NAudio.Wave;
using RemSound.Core;

namespace RemSound.Receiver;

/// <summary>
/// Render backend that runs a WASAPI <see cref="MultiOutputPlayout"/> and an
/// <see cref="AsioRenderBackend"/> in parallel. Two pipeline shapes are reachable today:
/// <list type="bullet">
///   <item>WasapiOnly: WASAPI child reads <see cref="PlayoutEngine"/> directly; no ASIO in
///         the path. Used when no ASIO driver is selected.</item>
///   <item>BothIndependent: WASAPI and ASIO children each get their own consumer view from a
///         shared <see cref="FanOutSource"/>. The FanOut pulls from PlayoutEngine on demand
///         and caches so both views see the same samples without one consumer slowing the
///         other. Neither backend pays the classic-Both master-producer tee's ~5–10 ms
///         buffer headroom — each lane runs at its native callback rate.</item>
/// </list>
/// The legacy <c>AudioMode.Both</c> tee mode and <c>AudioMode.AsioOnly</c> values are no
/// longer reachable from the UI and are not produced here.
/// </summary>
internal sealed class CompositeRenderBackend : IRenderBackend
{
    private readonly PlayoutEngine source;
    private readonly Action<string>? onDiagnostic;
    private readonly object gate = new();

    // BothIndependent no longer uses a shared FanOut between the two render backends — each
    // backend reads directly from its own lane-filtered source (PlayoutEngine.WasapiLaneOutput /
    // AsioLaneOutput). Those surfaces filter PlayoutEngine's session snapshot by RenderRoute,
    // so the WASAPI consumer's Read only advances WasapiLane sessions and the ASIO consumer's
    // Read only advances AsioLane sessions. The two lanes are fully independent — no shared
    // cache, no cross-lane interference, neither lane pays a cache-age penalty when the other
    // is also playing.
    private readonly MultiOutputPlayout? wasapi;
    private readonly AsioRenderBackend? asio;
    private readonly string? asioDriverName;
    private readonly RemSound.Core.AudioMode mode;

    private bool started;

    public CompositeRenderBackend(RemSound.Core.AudioMode mode, string? asioDriverName, PlayoutEngine source, Action<string>? onDiagnostic = null)
    {
        this.source = source;
        this.onDiagnostic = onDiagnostic;
        this.asioDriverName = asioDriverName;
        this.mode = mode;

        // Coerce legacy enum values (AsioOnly, Both) into a reachable mode. Anything non-
        // WASAPI without a driver demotes to WasapiOnly; anything non-WASAPI with a driver
        // is treated as BothIndependent (the only ASIO-using render mode now).
        if (mode != RemSound.Core.AudioMode.WasapiOnly)
        {
            if (string.IsNullOrEmpty(asioDriverName))
            {
                this.mode = mode = RemSound.Core.AudioMode.WasapiOnly;
            }
            else if (mode != RemSound.Core.AudioMode.BothIndependent)
            {
                this.mode = mode = RemSound.Core.AudioMode.BothIndependent;
            }
        }

        if (mode == RemSound.Core.AudioMode.WasapiOnly)
        {
            // MultiOutputPlayout reads PlayoutEngine directly — no master producer, no tee.
            // Sessions in WasapiOnly mode are all on RenderRoute.Mixed (the legacy single-knob
            // world), and the all-sessions Read does the right thing.
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
            // reads its own audio at its native callback latency, exactly as it would in a
            // hypothetical AsioOnly setup — even when WASAPI is also actively playing.
            // The previous implementation wrapped a single FanOut around the whole engine,
            // which (a) made both lanes play the combined mix instead of per-lane audio and
            // (b) added up to one WASAPI tick (~10 ms) of cache-age latency to whichever
            // consumer was the slower of the two.
            wasapi = new MultiOutputPlayout(source.WasapiLaneOutput, msg => onDiagnostic?.Invoke($"wasapi out: {msg}"));
            asio = new AsioRenderBackend(asioDriverName!, source.AsioLaneOutput, msg => onDiagnostic?.Invoke($"asio out: {msg}"));
        }
    }

    public bool IsRunning => started;

    // TakeMaxFanOutCacheBytes removed 2026-05-23. The FanOutSource architecture was retired
    // in mid-May when each lane got its own filtered PlayoutEngine source — there's no shared
    // cache to measure any more, so the method always returned 0. The receiver-side
    // pass-through (AudioReceiver.TakeMaxFanOutCacheMs) and the fanCacheMs= diag column were
    // removed alongside it.

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
            onDiagnostic?.Invoke($"composite render started (mode={ModeLabel()})");
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
        // No FanOut bookkeeping any more — each lane's source is independent, so consumer
        // activity / inactivity doesn't affect the other lane's read path. The "skip the
        // pull when no outputs are ticked" behaviour now lives inside MultiOutputPlayout's
        // producer loop, which short-circuits source.Read when outputs.Count == 0.

        // Tell the PlayoutEngine which lanes have an active output device. ReadForRoute
        // uses this to route "orphan" sessions (those whose announced lane has no active
        // output) through whichever lane IS being read — so a peer that announced WASAPI
        // is still audible on a receiver who has only ASIO outputs ticked. Without this
        // signal those sessions stay stuck in their session ring and the user just hears
        // silence from that peer. 2026-05-15.
        source.SetLaneActive(RenderRoute.WasapiLane, wasapiIds.Count > 0);
        source.SetLaneActive(RenderRoute.AsioLane, asioIds.Count > 0);
    }

    public void Dispose()
    {
        Stop();
        try { wasapi?.Dispose(); } catch { /* ignore */ }
        try { asio?.Dispose(); } catch { /* ignore */ }
    }

    private string ModeLabel() => mode switch
    {
        RemSound.Core.AudioMode.WasapiOnly => "fast (WASAPI direct)",
        RemSound.Core.AudioMode.BothIndependent => "independent lanes (WASAPI + ASIO, no mix)",
        _ => mode.ToString(),
    };

    // FanOutSource and SwitchableSource have been removed (2026-05-13). The BothIndependent
    // rewiring put each lane on its own filtered PlayoutEngine.{Wasapi,Asio}LaneOutput
    // surface, so there is no shared source for two consumers to fight over and no cache
    // to manage. Either class can be reintroduced if a future routing shape needs them.
}
