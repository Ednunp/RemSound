using System.Net;
using System.Net.Sockets;
using NAudio.CoreAudioApi;
using RemSound.Core;
using RemSound.Sender;

namespace RemSound.App;

/// <summary>
/// The headless engine behind the RemSound Windows service: it loads the designated send-only profile
/// and streams it to the profile's peers, with no window, tray, hotkeys or screen reader. It YIELDS to
/// the interactive app — while a normal RemSound is open it suspends (stops capturing, drops the send),
/// resuming when the app closes or crashes (see <see cref="InteractivePresence"/>).
///
/// <para>Send-only and WASAPI-only by design: ASIO can't run in a service, and receive is impossible on
/// Windows 11 with no user logged in, so neither is attempted. v1 sends directly to the profile's
/// configured peer addresses (LAN / port-forwarded / reachable hosts); NAT hole-punching and relay
/// discovery are the interactive app's job, not the service's.</para>
///
/// <para>Structured so the mechanism is unit-testable without a real service: <see cref="ApplyProfile"/>,
/// <see cref="Suspend"/> and <see cref="Resume"/> are driven directly by the self-tests, and
/// <see cref="RunLoop"/> wires them to the interactive-presence token.</para>
/// </summary>
public sealed class ServiceSendHost : IDisposable
{
    private readonly Func<Profile?> loadProfile;
    private readonly Action<string>? log;
    private readonly AudioSender sender = new();
    // Network reachability: discovery + listener + heartbeat, so a peer can actually FIND and CONNECT to
    // the service (the bare sender only ever pushed blindly to fixed addresses). Brought up alongside the
    // sender while we're streaming, and fully torn down to a shell whenever we yield to the interactive app.
    private readonly ServiceNetworkPresence presence;
    private readonly object gate = new();
    private bool running;      // the engine is actively sending
    private bool disposed;

    // Event-driven device-set watcher (the same mechanism the main window uses): it fires ONLY when a
    // device is added/removed/changes state or the default changes — no background polling, nothing that
    // builds up. While the service should be sending, that's our cue to (re)open capture: it covers the
    // audio stack finishing coming up at boot, a device being plugged/unplugged, and the audio service
    // restarting. Debounced because one hot-plug fires several notifications in quick succession.
    private AudioDeviceChangeNotifier? deviceNotifier;
    private volatile bool wantSending;     // true while the app is absent and we intend to stream
    private long lastDeviceChangeTick;

    // Reachability-gated sending (issues #8 / #15): stream ONLY to peers the heartbeat can reach, and drop
    // any that stay unreachable — never blast audio into a dead address forever. The heartbeat keeps
    // probing the FULL set, so a peer that comes back is re-armed on the next refresh. Mirrors the app's
    // RefreshAudioReceivers. Send-only, so there's no "actively receiving" carve-out.
    private static readonly TimeSpan PruneUnreachableAfter = TimeSpan.FromSeconds(30);
    private IPEndPoint[] allEndpoints = [];
    private string? armedSignature;

    /// <param name="loadProfile">Supplies the current service profile (re-read on each resume so edits
    /// are picked up). Returns null if none is configured.</param>
    /// <param name="log">Optional diagnostic sink.</param>
    public ServiceSendHost(Func<Profile?> loadProfile, Action<string>? log = null)
    {
        this.loadProfile = loadProfile;
        this.log = log;
        presence = new ServiceNetworkPresence(sender, log);
        // Wire the audio engine's own diagnostics into the service log — capture opens/failures, backend
        // switches, the silence keepalive, composite mode. Without this the service log is blind below
        // "streaming N sources" (issue #23 taught us that the hard way).
        sender.Diagnostic = msg => log?.Invoke($"sender: {msg}");
    }

    // Capture-health watch (issue #23): the lock-screen bug is "capture open but ZERO buffers ever arrive".
    // Log the first callback when audio genuinely flows, and an explicit zero-callbacks line after 10s so
    // the log names the fault instead of just going quiet.
    // ---- Capture-health watch + boot self-heal (issue #23) --------------------------------------
    // Boot fingerprint (Jonathan's logs + Ed): at the boot lock screen the machine's OWN speakers play
    // the Windows tune and NVDA, the loopback capture opens fine in session 0 and callbacks flow — yet
    // the mix it taps carries none of that audio and peers hear nothing. Signing in fixes it INSTANTLY
    // with no change on our side; a capture opened after sign-out works fine at the lock screen. So: a
    // capture attached in the first seconds of boot can land on an engine mix the logon-session audio
    // path was never wired into, and Windows raises no device event about it.
    //
    // THE DETECTOR: the endpoint's own output METER (IAudioMeterInformation.MasterPeakValue) is read
    // straight from the device, independently of our capture stream. Every watch tick (~0.5s) we
    // compare it with what the capture is hearing. Device audibly playing + capture silent since it
    // opened = the capture is provably DEAF → re-open it immediately (a fresh attach lands on the live
    // graph) — fast enough that the boot tune itself comes through. A quiet machine reads quiet on
    // BOTH sides, so nothing ever fires on healthy captures; the first real audio the capture hears
    // ends the ladder for the stint. Frozen callbacks (2s+) also re-open, deafness aside.
    private long captureWatchStartTick;
    private bool loggedFirstCallback;
    private bool loggedZeroCallbacks;
    private long lastCapturePulseTick;
    private long lastCallbacks = -1;
    private long lastCallbacksChangeTick;
    private volatile bool everHeardAudio; // read from the session-kick COM path as well as the loop
    private long deafSinceTick;          // when continuous meter-audible + capture-silent began; 0 = not deaf
    private int reopenAttempts;
    private long lastReopenTick;
    private float pulsePeakMax;          // loudest capture sample since the last pulse log line
    private float pulseMeterMax;         // loudest endpoint-meter reading since the last pulse log line
    private long pulseFramesSent;
    // Meter readers for the captured loopback endpoints — swapped on every (re)apply, read lock-free
    // on the watch tick (a torn read against a just-disposed device is caught per-device).
    private volatile MMDevice[] meterDevices = [];
    // INSTANT trigger (no polling): Windows raises a session-created notification the moment an app sets
    // up an audio session on the device — BEFORE its first sound plays. At the boot lock screen that's
    // LogonUI / NVDA arriving. If a new session appears while this capture has never heard audio, we
    // re-open right then, so the re-attached capture is listening from the first note. The meter
    // watchdog below stays as the backstop in case this notification doesn't cross sessions at the
    // lock screen. Our own silence keepalive also creates a session — filtered by process id, or it
    // would kick an endless reopen loop.
    private AudioSessionStartWatcher? sessionKick;

    private const int MaxReopens = 3;              // per sending stint; refilled on Resume/power-resume
    private const float AudiblePeak = 0.003f;      // endpoint meter level considered "audibly playing"
    private const float SilentPeak = 0.001f;       // capture level below this counts as silence
    // Time-based (not tick-based) so the reaction doesn't depend on the loop cadence and a fast test
    // cadence can't trip it: a healthy capture hears real sound within ~200ms of it starting, so 450ms
    // of CONTINUOUS device-audible-but-capture-silent can only mean a genuinely deaf capture. At the
    // 500ms service tick this re-opens on the second deaf reading — about 1s after the sound starts.
    private const int DeafMsToReopen = 450;
    private const int StallMsToReopen = 2_000;     // healthy callbacks tick every few ms
    private const int ReopenSpacingMs = 2_000;     // don't thrash between attempts
    private const int CapturePulseIntervalMs = 15_000; // diagnostic log cadence (NOT the trigger)

    /// <summary>Pure decision core of the issue-#23 boot self-heal, split out so the self-test can pin
    /// it: re-open when the capture is provably deaf (the endpoint meter shows the device audibly
    /// playing while the capture has heard nothing since it opened) or its callbacks froze — but never
    /// more than <see cref="MaxReopens"/> times a stint, and never for deafness once real audio has
    /// been heard (after that, silence is just silence).</summary>
    internal static bool ShouldReopenCapture(bool captureDeaf, bool everHeardAudio, bool stalled, int attemptsSoFar) =>
        (stalled || (captureDeaf && !everHeardAudio)) && attemptsSoFar < MaxReopens;

    private void WatchCaptureHealth()
    {
        var now = Environment.TickCount64;

        // What is the CAPTURE hearing? (Resets on read — this watch is the only reader; the pulse log
        // uses the accumulated maxima.) Callbacks alone can't distinguish real sound from our own
        // silence keepalive feeding back, which is why the peak matters.
        var peak = sender.TakeMaxSenderPreEncodePeak();
        if (peak > pulsePeakMax) pulsePeakMax = peak;
        if (peak >= SilentPeak) { everHeardAudio = true; deafSinceTick = 0; }
        pulseFramesSent += sender.TakeSenderAudioFramesSent();

        // What is the DEVICE playing? The endpoint's own meter, independent of our capture stream.
        var meter = 0f;
        foreach (var dev in meterDevices)
        {
            try { var v = dev.AudioMeterInformation.MasterPeakValue; if (v > meter) meter = v; }
            catch { /* endpoint invalidated mid-read — the device watcher / next reopen re-resolves */ }
        }
        if (meter > pulseMeterMax) pulseMeterMax = meter;

        // Deafness evidence: device audibly playing while the capture stays silent-since-open. Track
        // WHEN the continuous deaf state began; any tick that breaks it resets the clock.
        if (meter >= AudiblePeak && peak < SilentPeak && !everHeardAudio)
        {
            if (deafSinceTick == 0) deafSinceTick = now;
        }
        else if (meter < AudiblePeak)
        {
            deafSinceTick = 0;
        }

        // Callback stall: frozen for 2s while sending = the stream is broken regardless of loudness.
        var callbacks = sender.CaptureCallbacks;
        if (callbacks != lastCallbacks) { lastCallbacks = callbacks; lastCallbacksChangeTick = now; }
        var stalled = now - lastCallbacksChangeTick > StallMsToReopen;

        var deaf = deafSinceTick != 0 && now - deafSinceTick >= DeafMsToReopen;
        if (ShouldReopenCapture(deaf, everHeardAudio, stalled, reopenAttempts))
        {
            ReopenCapture(stalled
                ? "capture STALLED — callbacks frozen"
                : $"capture is DEAF — endpoint meter reads {meter:F3} (audible) but the capture has heard nothing since it opened");
            return;
        }

        // Periodic diagnostic pulse (accumulated maxima since the previous line).
        if (now - lastCapturePulseTick >= CapturePulseIntervalMs)
        {
            lastCapturePulseTick = now;
            log?.Invoke($"service: capture pulse — callbacks={callbacks} bytes={sender.CaptureBytes} capPeak={pulsePeakMax:F3} meterPeak={pulseMeterMax:F3} framesSent={pulseFramesSent}"
                + (pulsePeakMax < SilentPeak && pulseMeterMax >= AudiblePeak ? " (DEVICE AUDIBLE BUT CAPTURE SILENT)" : "")
                + (pulsePeakMax < SilentPeak && pulseMeterMax < AudiblePeak ? " (machine quiet — both sides silent)" : ""));
            pulsePeakMax = 0f;
            pulseMeterMax = 0f;
            pulseFramesSent = 0;
        }

        if (loggedFirstCallback) return;
        if (callbacks > 0)
        {
            loggedFirstCallback = true;
            log?.Invoke($"service: first capture callback received — audio is flowing ({sender.CaptureBytes} bytes so far)");
            return;
        }
        if (!loggedZeroCallbacks && now - captureWatchStartTick > 10_000)
        {
            loggedZeroCallbacks = true;
            log?.Invoke("service: capture is OPEN but has delivered ZERO audio callbacks after 10s — the audio engine is not feeding the loopback; peers hear nothing (issue #23 signature)");
        }
    }

    /// <summary>Replace the endpoint-meter readers with ones for the given specs' loopback devices
    /// (empty to clear). Old readers are disposed after the swap; a watch tick racing the swap reads
    /// the old snapshot and its per-device catch absorbs a disposed COM object.</summary>
    private void SwapMeterDevices(IReadOnlyList<CaptureSourceSpec> specs)
    {
        var old = meterDevices;
        var fresh = new List<MMDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var id in specs.Where(s => s.Kind == CaptureKind.Loopback && !string.IsNullOrEmpty(s.DeviceId))
                                    .Select(s => s.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try { fresh.Add(enumerator.GetDevice(id)); }
                catch { /* device gone — opening the capture itself will have failed and logged too */ }
            }
        }
        catch { /* enumerator unavailable — the watch runs meter-blind; stall detection still works */ }
        meterDevices = fresh.ToArray();
        foreach (var d in old) { try { d.Dispose(); } catch { /* ignore */ } }
    }

    /// <summary>Session-created notification (COM thread) — the INSTANT path of the issue-#23 self-heal.
    /// An app just set up an audio session on the default render device, i.e. sound is about to start.
    /// If this capture has never heard audio, re-open it right now so the re-attached capture is
    /// listening from the first note (at boot: the Windows tune / NVDA at the logon screen). Hops to the
    /// thread pool — never tear down audio objects from inside an audio notification callback.</summary>
    private void OnAudioSessionCreated(int pid)
    {
        if (pid == Environment.ProcessId) return; // our own silence keepalive — reacting would loop forever
        if (everHeardAudio || !wantSending || disposed) return;
        Task.Run(() => ReopenCapture($"new audio session (pid {pid}) appeared while the capture has heard nothing — sound is starting"));
    }

    /// <summary>Tear down and re-open JUST the audio capture (sender stop → rebuild specs → start),
    /// leaving the network presence up so peers see no discovery blip. The shared exit of both
    /// self-heal paths (instant session-kick + meter watchdog); rate-limited and capped here so the
    /// two paths can't stack re-opens.</summary>
    private void ReopenCapture(string reason)
    {
        lock (gate)
        {
            if (!running || disposed) return;
            var now = Environment.TickCount64;
            if (reopenAttempts >= MaxReopens || now - lastReopenTick < ReopenSpacingMs) return;
            reopenAttempts++;
            lastReopenTick = now;
            deafSinceTick = 0;
            log?.Invoke($"service: {reason} — re-opening capture to re-attach to the live audio graph "
                + $"(attempt {reopenAttempts}/{MaxReopens}, issue #23 boot self-heal)");
            var profile = loadProfile();
            if (profile is null) return;
            try
            {
                sender.Stop();
                var specs = BuildSendSpecs(profile); // re-resolve (the default device may have moved)
                if (specs.Count == 0) { log?.Invoke("service: re-open found no send sources — capture left stopped"); running = false; return; }
                sender.Configure(specs);
                sender.Start();
                SwapMeterDevices(specs);
                captureWatchStartTick = Environment.TickCount64;
                loggedFirstCallback = false;
                loggedZeroCallbacks = false;
                lastCallbacks = -1;
                lastCallbacksChangeTick = Environment.TickCount64;
                pulsePeakMax = 0f;
                pulseMeterMax = 0f;
                pulseFramesSent = 0;
            }
            catch (Exception ex)
            {
                log?.Invoke($"service: capture re-open failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>Convenience factory for the real service: loads the profile from the machine-wide
    /// <see cref="ServiceStore"/> (ProgramData) each time it's asked — the same file the config dialog
    /// writes, readable by the SYSTEM service account. Re-read on each resume so edits are picked up.</summary>
    public static ServiceSendHost FromConfig(Action<string>? log = null) => new(ServiceStore.LoadProfile, log);

    public bool IsSending { get { lock (gate) return running; } }

    /// <summary>Test seam: is the network presence (discovery + listener + heartbeat) currently up? Tracks
    /// sending — up while streaming, torn down to a shell while yielded to the interactive app.</summary>
    internal bool IsNetworkPresenceUpForTest => presence.IsUp;

    /// <summary>Test seam: the crypto material the host pushed to the sender + the codec/frame it set, so
    /// a self-test can prove the service configures the sender exactly like the main app.</summary>
    internal (byte[]? Key, byte[]? Fingerprint, AudioTransportCodec Codec, int Frame) SenderConfigForTest =>
        (sender.AudioKey, sender.AudioFingerprint, sender.Codec, sender.OpusFrameSamplesPerChannel);

    /// <summary>Builds the send sources, peer endpoints and encryption key from a profile and starts the
    /// sender. Idempotent-ish: call <see cref="Suspend"/> before re-applying a different profile. Returns
    /// false (and stays stopped) if the profile has nothing to send or no reachable peers.</summary>
    public bool ApplyProfile(Profile profile)
    {
        lock (gate)
        {
            if (disposed) return false;
            var specs = BuildSendSpecs(profile);
            var endpoints = BuildEndpoints(profile);
            if (specs.Count == 0) { log?.Invoke("service: profile has no WASAPI send sources — nothing to stream"); return false; }
            if (endpoints.Count == 0) { log?.Invoke("service: profile has no reachable peers — nothing to stream to"); return false; }

            // Encryption: derive BOTH the key AND the fingerprint from the plain password, exactly like
            // MainForm.RecomputeAudioCrypto. The peer verifies the fingerprint before accepting a stream —
            // sending the key without it would get the service's audio rejected at the far end.
            var plainPassword = string.IsNullOrEmpty(profile.Password) ? "" : RemSoundCrypto.Deobfuscate(profile.Password);
            sender.AudioKey = string.IsNullOrEmpty(plainPassword) ? null : RemSoundCrypto.DeriveKey(plainPassword);
            sender.AudioFingerprint = string.IsNullOrEmpty(plainPassword) ? null : RemSoundCrypto.Fingerprint(plainPassword);
            // The service's audio transport is FIXED to the known-good live-jamming config, regardless of
            // what the profile carries (the config dialog no longer exposes these — Ed, 2026-07-17). Raw
            // PCM sounded hideous over the service; Opus at the 2.5 ms live frame with Small packets and
            // lock-to-clock is what sounds right. Forcing it here means a stale or hand-edited profile can
            // never put the service back on a bad codec.
            const AudioTransportCodec serviceCodec = AudioTransportCodec.Opus;
            const int serviceOpusFrameSamples = 120; // 2.5 ms at 48 kHz
            const SendRate serviceSendRate = SendRate.Tight; // "Small" packets
            sender.ConfigureCodec(serviceCodec, MainForm.EffectiveOpusFrameSamples(serviceCodec, serviceOpusFrameSamples, serviceSendRate));
            sender.SetSendRate(serviceSendRate);
            sender.SetTightLatency(true); // lock to audio clock — always on
            // Arm the full set to begin with (nothing is known-dead yet); RefreshSendArming then prunes any
            // peer the heartbeat can't reach and re-arms it when it recovers.
            allEndpoints = endpoints.ToArray();
            armedSignature = null;
            sender.SetReceivers(endpoints);
            sender.Configure(specs);
            // A device-open failure at Start must NOT throw out of the service loop. With lock-to-clock
            // always on, the WASAPI lane uses push-mode, which opens the endpoint synchronously and throws
            // (e.g. ArgumentException) if the device id is invalid or the device has vanished. Swallow it:
            // presence still comes up, and the device-change watcher / self-heal re-open the capture when a
            // good device appears. Without this guard a disappeared device would crash the whole service.
            try { sender.Start(); }
            catch (Exception ex) { log?.Invoke($"service: capture start failed ({ex.GetType().Name}: {ex.Message}) — presence up; will re-open when a device is available"); }
            SwapMeterDevices(specs); // endpoint-meter readers for the deaf-capture detector
            // Instant self-heal trigger: session-created notification on the default render device.
            // Recreated per apply so it re-points at the current default after a device change.
            try { sessionKick?.Dispose(); } catch { /* ignore */ }
            sessionKick = new AudioSessionStartWatcher(OnAudioSessionCreated, msg => log?.Invoke($"service: {msg}"));
            captureWatchStartTick = Environment.TickCount64;
            loggedFirstCallback = false;
            loggedZeroCallbacks = false;
            lastCapturePulseTick = Environment.TickCount64; // first pulse lands one interval after start
            // Fresh capture instance — restart the per-capture watch state so a new stream isn't
            // misread as stalled or deaf on its very first ticks.
            lastCallbacks = -1;
            lastCallbacksChangeTick = Environment.TickCount64;
            deafSinceTick = 0;
            pulsePeakMax = 0f;
            pulseMeterMax = 0f;
            pulseFramesSent = 0;
            // everHeardAudio / reopenAttempts deliberately NOT reset here: the self-heal calls straight
            // back into ApplyProfile, so resetting them here would make the re-open ladder infinite.
            // They reset per sending STINT in Resume() (and on power resume).
            // Come up on the network too, so the peers can discover and connect to us — not just receive a
            // blind push. Same well-known audio port and the same components the interactive app uses.
            presence.Start(RemPacket.DefaultPort, endpoints);
            running = true;
            // Un-throttle the process while streaming. A headless Windows SERVICE is treated by the OS as a
            // background process and gets aggressively downclocked (EcoQoS), migrated onto efficiency cores
            // and given a coarse scheduler quantum — which starves the send loop and makes the stream
            // CRACKLE even though capture and the network look clean. The interactive app only does this on
            // the user's opt-in "high priority" toggle, but the service has no foreground/battery use case
            // (it exists solely to stream), so it always engages while sending. Idempotent; released in
            // Suspend. This is the fix for "the service sounds hideous vs the main app".
            try { PerformanceMode.Apply(true, msg => log?.Invoke($"service: {msg}")); } catch { /* best-effort */ }
            log?.Invoke($"service: streaming \"{profile.Title}\" — {specs.Count} source(s) to {endpoints.Count} peer(s)");
            return true;
        }
    }

    /// <summary>Stops sending and releases capture. Safe to call when already stopped.</summary>
    public void Suspend()
    {
        lock (gate)
        {
            if (!running) return;
            // Vacate the network FIRST (stop announcing, unbind the port, stop the heartbeat) so the
            // interactive app can take it over cleanly, then stop the audio send.
            try { presence.Stop(); } catch (Exception ex) { log?.Invoke($"service: presence stop error {ex.GetType().Name}: {ex.Message}"); }
            try { sender.Stop(); } catch (Exception ex) { log?.Invoke($"service: stop error {ex.GetType().Name}: {ex.Message}"); }
            SwapMeterDevices(Array.Empty<CaptureSourceSpec>()); // release the endpoint-meter readers
            try { sessionKick?.Dispose(); } catch { /* ignore */ }
            sessionKick = null;
            try { PerformanceMode.Apply(false, msg => log?.Invoke($"service: {msg}")); } catch { /* best-effort */ }
            running = false;
            log?.Invoke("service: suspended (interactive app present)");
        }
    }

    /// <summary>Re-reads the current service profile and starts sending. Used when the interactive app
    /// goes away, so any edits it made are picked up.</summary>
    public bool Resume()
    {
        var profile = loadProfile();
        if (profile is null) { log?.Invoke("service: no service profile configured — staying idle"); return false; }
        // Fresh sending stint — refill the deaf-capture self-heal ladder (issue #23).
        everHeardAudio = false;
        reopenAttempts = 0;
        return ApplyProfile(profile);
    }

    /// <summary>Pure, testable: which endpoints to actively stream to — the full set minus any peer the
    /// heartbeat reports as continuously unreachable for longer than <paramref name="pruneAfter"/>. A peer
    /// that's reachable, or still within the grace window, stays armed. Mirrors the app's RefreshAudioReceivers.</summary>
    internal static IPEndPoint[] ComputeArmedEndpoints(IReadOnlyList<IPEndPoint> all, IReadOnlyList<PeerHealth> health, TimeSpan pruneAfter)
    {
        HashSet<string>? dead = null;
        foreach (var ph in health)
        {
            if (ph.State == PeerHealthState.Unreachable && ph.AgeOfLastPong is { } age && age > pruneAfter)
                (dead ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add($"{ph.AudioEndpoint.Address}:{ph.AudioEndpoint.Port}");
        }
        return dead is null ? all.ToArray() : all.Where(ep => !dead.Contains($"{ep.Address}:{ep.Port}")).ToArray();
    }

    /// <summary>Re-arm the sender to only the reachable peers, using the heartbeat health. Cheap and
    /// idempotent — only touches the sender when the armed set actually changes. Called on the service's
    /// existing poll tick while streaming, so there's no extra timer.</summary>
    private void RefreshSendArming()
    {
        lock (gate)
        {
            if (!running || allEndpoints.Length == 0) return;
            var armed = ComputeArmedEndpoints(allEndpoints, presence.PeerHealthSnapshot(), PruneUnreachableAfter);
            var sig = string.Join("|", armed.Select(ep => $"{ep.Address}:{ep.Port}").OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
            if (sig == armedSignature) return;
            armedSignature = sig;
            sender.SetReceivers(armed);
            log?.Invoke(armed.Length == 0
                ? $"service: 0 reachable peers — holding audio (heartbeat still probing {allEndpoints.Length})"
                : $"service: streaming to {armed.Length}/{allEndpoints.Length} reachable peer(s)");
        }
    }

    /// <summary>The service's main loop: watch the interactive-presence token and hand the send back and
    /// forth. Starts sending immediately if no app is present. A short settle delay before resuming
    /// stops rapid app open/close from thrashing the engine. Returns when <paramref name="ct"/> is
    /// cancelled (service stop).</summary>
    // 500ms tick (was 1000): the capture-health watch rides this loop, and the deaf-capture detector
    // must react while the boot tune is still PLAYING — two deaf ticks at 500ms = re-open within ~1s of
    // the first audible sound. The per-tick work is trivial (a mutex probe, a few counter reads and one
    // meter read per captured device), so the faster cadence costs nothing measurable.
    public void RunLoop(CancellationToken ct, int pollMs = 500, int resumeSettleMs = 2000)
        => RunLoopCore(ct, InteractivePresence.IsInteractiveAppRunning, pollMs, resumeSettleMs);

    /// <summary>Test seam: run the loop against a caller-supplied presence-token name so a test can't
    /// collide with a real running app on the production token.</summary>
    internal void RunLoopWithToken(CancellationToken ct, string tokenName, int pollMs, int resumeSettleMs)
        => RunLoopCore(ct, () => InteractivePresence.IsInteractiveAppRunning(tokenName), pollMs, resumeSettleMs);

    private void RunLoopCore(CancellationToken ct, Func<bool> isAppPresent, int pollMs, int resumeSettleMs)
    {
        // Register the event-driven device watcher for the life of the loop. Its callback re-opens
        // capture when the device set changes, so we never poll for device readiness.
        try { deviceNotifier ??= new AudioDeviceChangeNotifier(OnDeviceSetChanged); }
        catch (Exception ex) { log?.Invoke($"service: device-change watcher unavailable ({ex.GetType().Name}) — relying on app-transition re-opens"); }

        var appWasPresent = true; // force an initial evaluation
        var absentSince = Environment.TickCount64;
        var triedThisAbsence = false;
        while (!ct.IsCancellationRequested)
        {
            var appPresent = isAppPresent();
            if (appPresent)
            {
                wantSending = false;
                if (IsSending) Suspend();
                absentSince = long.MaxValue;
                triedThisAbsence = false;
            }
            else
            {
                if (appWasPresent) { absentSince = Environment.TickCount64; triedThisAbsence = false; } // app just left
                if (Environment.TickCount64 - absentSince >= resumeSettleMs)
                {
                    wantSending = true;
                    // One start attempt per absence. If capture isn't ready yet (audio stack still coming
                    // up at boot, device absent), the device-change watcher re-opens it the moment a device
                    // appears — no per-tick retry loop that would keep churning in the background.
                    if (!triedThisAbsence && !IsSending) { Resume(); triedThisAbsence = true; }
                }
            }
            // While streaming, re-arm to only the reachable peers (drop dead ones, pick up recovered ones)
            // and watch capture health (issue #23: open-but-starved loopback). Piggybacks this existing
            // tick — no extra timer, no background pile-up.
            if (IsSending)
            {
                RefreshSendArming();
                WatchCaptureHealth();
            }
            appWasPresent = appPresent;
            ct.WaitHandle.WaitOne(pollMs);
        }
        wantSending = false;
        Suspend();
    }

    /// <summary>Force a re-open of capture if we intend to send — called after a power resume, when the
    /// audio devices have re-initialised and the current capture may be dead. The device-change watcher
    /// usually catches this too, but a resume doesn't always fire an endpoint change, so we re-open
    /// explicitly to be safe.</summary>
    public void ReopenAfterResume()
    {
        if (!wantSending || disposed) return;
        var profile = loadProfile();
        if (profile is null) return;
        log?.Invoke("service: re-opening capture after power resume");
        // Wake-from-sleep re-plumbs the audio graph much like boot does — refill the self-heal ladder.
        everHeardAudio = false;
        reopenAttempts = 0;
        Suspend();
        ApplyProfile(profile);
    }

    /// <summary>Device-set change callback (COM thread). While we intend to send, (re)open capture — this
    /// is the event that fires when the audio stack finishes coming up at boot, a device is plugged or
    /// unplugged, or the audio service restarts. Debounced: a single hot-plug fires several notifications.</summary>
    private void OnDeviceSetChanged()
    {
        if (!wantSending || disposed) return;
        var now = Environment.TickCount64;
        lock (gate)
        {
            if (now - lastDeviceChangeTick < 750) return; // coalesce the burst
            lastDeviceChangeTick = now;
        }
        var profile = loadProfile();
        if (profile is null) return;
        Suspend();
        ApplyProfile(profile);
    }

    // WASAPI-only send specs from a profile. Mirrors the app's applications-vs-devices logic but never
    // touches ASIO (the service can't). Applications mode needs Windows 10 19041+.
    internal static List<CaptureSourceSpec> BuildSendSpecs(Profile p)
    {
        var specs = new List<CaptureSourceSpec>();
        var appsMode = ProcessLoopbackCapture.IsSupported
            && string.Equals(p.WasapiSendMode, "applications", StringComparison.OrdinalIgnoreCase);
        if (appsMode)
        {
            if (p.SendAllApplications)
            {
                var def = ResolveDefaultRenderId();
                if (def is not null) specs.Add(new CaptureSourceSpec(def, CaptureKind.Loopback, "All applications (system audio)"));
            }
            else
            {
                foreach (var name in p.SelectedSendApplications.Distinct(StringComparer.OrdinalIgnoreCase))
                    foreach (var pid in AudioAppEnumerator.PidsForProcessName(name))
                        specs.Add(new CaptureSourceSpec(ProcessLoopbackId.Format(pid), CaptureKind.ProcessLoopback, name));
            }
        }
        else
        {
            foreach (var id in p.SelectedWasapiSendOutputs.Distinct())
                specs.Add(new CaptureSourceSpec(id, CaptureKind.Loopback, id));
        }
        foreach (var id in p.SelectedWasapiSendInputs.Distinct())
            specs.Add(new CaptureSourceSpec(id, CaptureKind.Input, id));
        return specs;
    }

    // Resolve the profile's configured peers to audio endpoints. v1: direct addresses only.
    internal static List<IPEndPoint> BuildEndpoints(Profile p)
    {
        var entries = p.SelectedConnectedPeers.Count > 0 ? p.SelectedConnectedPeers : p.RememberedPeers;
        var result = new List<IPEndPoint>();
        var seen = new HashSet<string>();
        foreach (var entry in entries.Where(e => !string.IsNullOrWhiteSpace(e)).Distinct())
        {
            var (host, port) = SplitHostPort(entry);
            IPAddress? addr;
            if (!IPAddress.TryParse(host, out addr))
            {
                try
                {
                    var found = Dns.GetHostAddresses(host);
                    addr = found.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? found.FirstOrDefault();
                }
                catch { addr = null; }
            }
            if (addr is null) continue;
            // Send to the peer's audio port: an explicit "host:port" wins, else the standard peer port —
            // the same default the main app's manual-peer path uses (NOT the local listen port).
            var ep = new IPEndPoint(addr, port ?? RemPacket.DefaultPeerDialPort);
            if (seen.Add($"{ep.Address}:{ep.Port}")) result.Add(ep);
        }
        return result;
    }

    // Minimal "host[:port]" parser (self-contained so the host doesn't depend on the WinForms UI).
    internal static (string host, int? port) SplitHostPort(string text)
    {
        text = text.Trim();
        var colon = text.LastIndexOf(':');
        if (colon <= 0 || colon == text.Length - 1) return (text, null);
        var host = text[..colon];
        if (host.Contains(':')) return (text, null); // looks like an IPv6 literal — treat whole as host
        return int.TryParse(text[(colon + 1)..], out var port) && port is >= 1 and <= 65535 ? (host, port) : (text, null);
    }

    private static string? ResolveDefaultRenderId()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            if (!en.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return null;
            using var d = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return d.ID;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        wantSending = false;
        try { deviceNotifier?.Dispose(); } catch { } deviceNotifier = null;
        try { sessionKick?.Dispose(); } catch { } sessionKick = null;
        SwapMeterDevices(Array.Empty<CaptureSourceSpec>());
        try { PerformanceMode.Apply(false); } catch { }
        try { presence.Dispose(); } catch { }
        try { sender.Stop(); } catch { }
        try { sender.Dispose(); } catch { }
    }
}
