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
            // Opus frame size follows the send rate the same way the main app does (the "Small" rate
            // halves the Opus frame) — otherwise the service would encode at a different frame than the
            // main app would for the identical profile.
            sender.ConfigureCodec(profile.Codec, MainForm.EffectiveOpusFrameSamples(profile.Codec, profile.OpusFrameSamplesPerChannel, profile.SendRate));
            sender.SetSendRate(profile.SendRate);
            sender.SetTightLatency(profile.TightLatencyMode);
            // Arm the full set to begin with (nothing is known-dead yet); RefreshSendArming then prunes any
            // peer the heartbeat can't reach and re-arms it when it recovers.
            allEndpoints = endpoints.ToArray();
            armedSignature = null;
            sender.SetReceivers(endpoints);
            sender.Configure(specs);
            sender.Start();
            // Come up on the network too, so the peers can discover and connect to us — not just receive a
            // blind push. Same well-known audio port and the same components the interactive app uses.
            presence.Start(RemPacket.DefaultPort, endpoints);
            running = true;
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
    public void RunLoop(CancellationToken ct, int pollMs = 1000, int resumeSettleMs = 2000)
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
            // While streaming, re-arm to only the reachable peers (drop dead ones, pick up recovered ones).
            // Piggybacks this existing tick — no extra timer, no background pile-up.
            if (IsSending) RefreshSendArming();
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
        try { presence.Dispose(); } catch { }
        try { sender.Stop(); } catch { }
        try { sender.Dispose(); } catch { }
    }
}
