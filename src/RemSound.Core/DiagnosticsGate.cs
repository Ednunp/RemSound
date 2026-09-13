namespace RemSound.Core;

/// <summary>
/// Single shared on/off switch for the engine's diagnostic instrumentation. The App turns it on
/// whenever ANY of three things needs the probes: the <em>Enable logs</em> checkbox in Preferences,
/// continuous auto-tune, or the ASIO lane's own continuous auto-tune while an ASIO driver is chosen
/// (<c>MainForm.UpdateDiagnosticsGate</c>). Auto-tune sizes its latency target from the per-second
/// arrival-gap and render-callback-gap figures these probes collect, so turning logging off must not
/// turn the gate off while a tuner is running — that would blind the tuner without a word. Every probe
/// site in <c>RemSound.Sender</c> and <c>RemSound.Receiver</c> reads this flag as its very first action
/// and bails before doing any measurement, CAS update or per-sample arithmetic when it is false.
///
/// What's behind this gate:
/// <list type="bullet">
///   <item>Sender-side max-time probes — <c>SenderLane.OnMixedSamples</c> emit timing,
///         <c>AudioSender.SendToAll</c> kernel-send timing, capture-callback gap timers in
///         <c>AsioCaptureBackend</c> and <c>MixingEngine</c>.</item>
///   <item>Receiver-side max-time probes — <c>NetworkListener</c> dispatch timing,
///         <c>ReceiverDiagnostics</c> arrival-gap and render-callback-gap recording.</item>
///   <item>The per-sample envelope-spike detector
///         (<c>ReceiverDiagnostics.RecordOutputSampleSteps</c>), which iterates every output
///         sample doing second-derivative arithmetic and is the most expensive probe.</item>
/// </list>
///
/// What's <em>not</em> behind this gate: the running counters that feed the always-visible
/// status footer (packets sent, packets received, bytes, underruns, drops). Those are cheap
/// <c>Interlocked.Add</c> calls and the UI needs them whether logs are on or off.
///
/// Flag is plain <c>volatile</c>: the audio path reads it on every callback; lock-free reads
/// are essential, and the only writer is the UI thread, recomputing it when one of the three
/// reasons above may have changed. The gate flips on or off cleanly without any inflight write
/// needing to see the new value mid-probe.
/// </summary>
public static class DiagnosticsGate
{
    private static volatile bool enabled;

    /// <summary>True when the engine should run its diagnostic instrumentation. Set by the App from
    /// logging OR either continuous auto-tune — see the class summary for why it is never logging
    /// alone.</summary>
    public static bool Enabled
    {
        get => enabled;
        set => enabled = value;
    }
}
