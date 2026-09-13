namespace RemSound.Receiver;

/// <summary>
/// Abstraction over the render-side audio backend. <see cref="AudioReceiver"/> holds a
/// <see cref="CompositeRenderBackend"/>, which runs the WASAPI implementation
/// (<see cref="MultiOutputPlayout"/>) and, when an ASIO driver is chosen, the ASIO one
/// (<see cref="AsioRenderBackend"/>) behind this same interface.
///
/// The WASAPI and ASIO backends each pull mixed audio from a <see cref="PlayoutEngine"/>
/// <c>IWaveProvider</c> surface and route it to one or more output destinations. WASAPI
/// destinations are MMDevice IDs; ASIO destinations are synthetic IDs of the form
/// <c>"asio:&lt;channel-pair-index&gt;"</c> (see <c>AsioDeviceId</c>).
/// </summary>
internal interface IRenderBackend : IDisposable
{
    bool IsRunning { get; }

    /// <summary>Friendly summary for the snapshot log column. "(none)" when nothing is
    /// configured, comma-joined names for ≤3 outputs, "(N outputs)" otherwise.</summary>
    string ActiveDeviceSummary { get; }

    IReadOnlyList<string> ActiveDeviceIds { get; }

    /// <summary>
    /// These figures are ONLY available per output lane. There is deliberately no lane-free version.
    ///
    /// <para>There was one, and it is what produced the bug Ed caught: a single property answering
    /// "ASIO if it is running, otherwise WASAPI" meant that with both lanes live — which is how he
    /// actually runs — the WASAPI line of the readout showed ASIO's numbers. Removing the convenient
    /// one-value shortcut is what makes that mistake unavailable rather than merely discouraged:
    /// you cannot ask "what is the output latency" without saying which lane you mean.</para>
    ///
    /// <para>Audio queued in the device buffer is MEASURED and is zero on ASIO, which pulls straight
    /// from the playout engine — a real structural reason ASIO is tighter, not a claim about it.
    /// 2026-08-24.</para>
    /// </summary>
    double ReportedOutputLatencyMsFor(RemSound.Core.RenderRoute route);

    /// <inheritdoc cref="ReportedOutputLatencyMsFor"/>
    double OutputQueueMsFor(RemSound.Core.RenderRoute route);

    /// <summary>One reading per live WASAPI output stage; EMPTY for a backend that has no such stage.
    /// ASIO returning empty is the point rather than a gap: the stage exists on one lane and not the
    /// other, which is exactly why it is where to look when a fault appears on one lane only. See
    /// <see cref="WasapiOutputStage"/>.</summary>
    IReadOnlyList<WasapiOutputStage> OutputStages();

    void Start();

    void Stop();

    /// <summary>Live-update of the output set. Devices already present stay live; removed ones
    /// are torn down; new ones are opened. Empty list = render to nothing without stopping the
    /// mixer (so receive-side state stays alive).</summary>
    void SetOutputDevices(IReadOnlyList<string> deviceIds);
}
