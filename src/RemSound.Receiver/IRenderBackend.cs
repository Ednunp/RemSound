namespace RemSound.Receiver;

/// <summary>
/// Abstraction over the render-side audio backend so <see cref="AudioReceiver"/> can be wired
/// to either a WASAPI implementation (today's <see cref="MultiOutputPlayout"/>) or an ASIO
/// implementation (<see cref="AsioRenderBackend"/>) without caring which is in use.
///
/// Both backends pull mixed audio from <see cref="PlayoutEngine"/>'s <see cref="IWaveProvider"/>
/// surface and route it to one or more output destinations. WASAPI destinations are MMDevice
/// IDs; ASIO destinations are synthetic IDs of the form
/// <c>"asio:&lt;driver-name&gt;|&lt;channel-pair-index&gt;"</c>.
/// </summary>
internal interface IRenderBackend : IDisposable
{
    bool IsRunning { get; }

    /// <summary>Friendly summary for the snapshot log column. "(none)" when nothing is
    /// configured, comma-joined names for ≤3 outputs, "(N outputs)" otherwise.</summary>
    string ActiveDeviceSummary { get; }

    IReadOnlyList<string> ActiveDeviceIds { get; }

    /// <summary>
    /// How long audio waits in the OUTPUT device after this backend hands it over, in milliseconds,
    /// as the device or driver reports it. Zero when it won't say.
    ///
    /// <para>This replaces a clamp. The estimate used to take the measured render-callback gap,
    /// double it, then clamp up to a floor of 10 ms — which pinned a fast card at 10 whatever the
    /// truth was. Ed's 2026-08-23 log shows "render=10.0" on 2,541 of 2,547 samples on a card whose
    /// real callback period was about 1.4 ms, so that figure was the floor and not a reading. ASIO
    /// drivers state their playback latency outright; WASAPI states its engine period, doubled here
    /// because the device plays one buffer while the app fills the next.</para>
    ///
    /// <para>Read ONCE when a device is opened, never in a callback. 2026-08-24.</para>
    /// </summary>
    double ReportedOutputLatencyMs { get; }

    /// <summary>Audio queued in the backend's own device buffers right now, in milliseconds — a
    /// stage between the playout engine and the sound card that the latency estimate never counted.
    /// MEASURED. Zero for ASIO, which pulls straight from the engine and has no such queue, and that
    /// difference is a real part of why ASIO is tighter. 2026-08-24.</summary>
    double OutputQueueMs { get; }

    /// <summary>
    /// The same two figures, PER OUTPUT LANE, because WASAPI and ASIO run at the same time and the
    /// readout reports them apart.
    ///
    /// <para>Ed leaves both lanes active and switches between them, so a single blended figure — or
    /// worse, one lane's figure shown against both — hides exactly the difference he is trying to
    /// read. The lane-free properties above answer for whichever lane the listener is on and drive
    /// the one-number status line; these drive the two-line readout. 2026-08-24.</para>
    /// </summary>
    double ReportedOutputLatencyMsFor(RemSound.Core.RenderRoute route);

    /// <inheritdoc cref="ReportedOutputLatencyMsFor"/>
    double OutputQueueMsFor(RemSound.Core.RenderRoute route);

    void Start();

    void Stop();

    /// <summary>Live-update of the output set. Devices already present stay live; removed ones
    /// are torn down; new ones are opened. Empty list = render to nothing without stopping the
    /// mixer (so receive-side state stays alive).</summary>
    void SetOutputDevices(IReadOnlyList<string> deviceIds);
}
