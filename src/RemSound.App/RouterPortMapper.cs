using System;
using System.Net;
using System.Threading;
using Mono.Nat;

namespace RemSound.App;

/// <summary>
/// Status of the router port mapping attempt — used to drive the inline status label in
/// the Preferences dialog.
/// </summary>
internal enum RouterMappingStatus
{
    /// <summary>The feature is off (the user hasn't enabled UPnP).</summary>
    Disabled,
    /// <summary>Looking for a UPnP / NAT-PMP / PCP router on the LAN.</summary>
    Searching,
    /// <summary>Mapping opened successfully and the router is reachable.</summary>
    Mapped,
    /// <summary>No router with UPnP / NAT-PMP / PCP support was found. Either the router
    /// doesn't support it, has it disabled, or the network blocks discovery.</summary>
    NoRouterFound,
    /// <summary>A router was found and the mapping was added, but the reported external
    /// address is in the carrier-grade NAT (CGNAT) range — peers on the public internet
    /// will not be able to reach this machine even though the local router cooperated.</summary>
    CgnatDetected,
    /// <summary>A router was found but the mapping attempt failed (port already mapped to
    /// another device, router rejected the request, etc.). <see cref="LastError"/> has the
    /// detail.</summary>
    MappingFailed,
}

/// <summary>
/// Asks the user's router to forward inbound UDP <see cref="AudioPort"/> traffic to this
/// machine, using UPnP / NAT-PMP / PCP via the Mono.Nat library. The point is to spare
/// home users from manual port-forwarding when they want peers on the public internet to
/// reach them. Mono.Nat picks whichever protocol the router speaks.
///
/// Off by default and gated by <c>AppConfig.UpnpEnabled</c> — RemSound never pokes the
/// router unless the user has explicitly ticked the Preferences checkbox. Failures are
/// surfaced via <see cref="StatusChanged"/> and the Preferences status label; they never
/// throw or pop a dialog (the network is too lumpy for a popup to be useful).
///
/// Lifecycle:
///   * <see cref="Start"/> kicks off discovery on a background task. When (or if) a router
///     replies, the mapping is added and <see cref="StatusChanged"/> fires with
///     <see cref="RouterMappingStatus.Mapped"/>.
///   * Renewal happens automatically — Mono.Nat extends the lease before it expires.
///   * <see cref="Refresh"/> can be called after a sleep / resume cycle to make sure the
///     router didn't drop the mapping while the machine was off; this re-runs discovery.
///   * <see cref="Stop"/> politely removes the mapping and stops discovery.
///
/// Detects CGNAT by checking whether the router's reported external address falls in
/// <c>100.64.0.0/10</c> (RFC 6598) — when it does, UPnP technically succeeded but the user
/// is still unreachable from the public internet because of an upstream ISP NAT layer.
/// We surface that as a distinct status so the user understands why peers still can't
/// connect and is pointed at Tailscale / the relay instead.
/// </summary>
internal sealed class RouterPortMapper : IDisposable
{
    /// <summary>The UDP port RemSound uses for audio + heartbeat.</summary>
    public const int AudioPort = 47830;

    /// <summary>Lease duration on the port mapping, in seconds. The router (and Mono.Nat's
    /// internal renewal) will refresh this before it expires; we set a deliberately
    /// short-ish lease so a long sleep on the machine doesn't leave a stale forwarded port
    /// pointing at us forever.</summary>
    private const int MappingLeaseSeconds = 3600;

    private readonly Action<string>? log;
    private readonly object gate = new();
    private INatDevice? device;
    private Mapping? mapping;
    private IPAddress? externalAddress;
    private string lastError = "";
    private RouterMappingStatus status = RouterMappingStatus.Disabled;
    private bool searching;
    private bool disposed;

    /// <summary>Raised whenever <see cref="Status"/> changes. Always fires on a thread-pool
    /// thread — the caller is responsible for marshaling onto the UI thread if it touches
    /// UI state.</summary>
    public event EventHandler? StatusChanged;

    public RouterPortMapper(Action<string>? log = null)
    {
        this.log = log;
    }

    /// <summary>Current state of the mapping attempt. Read by the Preferences dialog to
    /// keep its inline status label up to date.</summary>
    public RouterMappingStatus Status
    {
        get { lock (gate) { return status; } }
    }

    /// <summary>The external (WAN-side) address and port the router reports for this
    /// machine when the mapping is open. Null until <see cref="Status"/> is
    /// <see cref="RouterMappingStatus.Mapped"/> or <see cref="RouterMappingStatus.CgnatDetected"/>.</summary>
    public IPEndPoint? ExternalEndpoint
    {
        get
        {
            lock (gate)
            {
                return externalAddress is null ? null : new IPEndPoint(externalAddress, AudioPort);
            }
        }
    }

    /// <summary>Last error message captured during a failed mapping attempt — surfaced in
    /// the status label so the user has a hint at what's going on.</summary>
    public string LastError
    {
        get { lock (gate) { return lastError; } }
    }

    /// <summary>Start (or restart) the UPnP discovery + mapping cycle. Safe to call multiple
    /// times; redundant calls are coalesced.</summary>
    public void Start()
    {
        lock (gate)
        {
            if (disposed) return;
            if (searching) return;
            searching = true;
            status = RouterMappingStatus.Searching;
            lastError = "";
        }
        RaiseChanged();
        try
        {
            // Subscribe-once: Refresh() (fired on every sleep/wake) calls Start() again without a
            // prior unsubscribe, and DeviceFound is a STATIC Mono.Nat event — a bare += would stack
            // a new handler every resume (duplicate port-maps + this object kept alive forever).
            // Remove first so there's only ever one subscription.
            NatUtility.DeviceFound -= OnDeviceFound;
            NatUtility.DeviceFound += OnDeviceFound;
            NatUtility.StartDiscovery();
            log?.Invoke("UPnP discovery started");
        }
        catch (Exception ex)
        {
            lock (gate)
            {
                searching = false;
                status = RouterMappingStatus.MappingFailed;
                lastError = ex.Message;
            }
            log?.Invoke($"UPnP discovery could not start: {ex.GetType().Name}: {ex.Message}");
            RaiseChanged();
        }

        // Mono.Nat doesn't fire DeviceFound at all when the network has no UPnP / NAT-PMP /
        // PCP router. Without a timeout the status would sit at Searching forever, which the
        // user-facing label reads as "still trying" indefinitely. Give it a reasonable window
        // and then declare no-router-found if nothing has replied.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(8));
            bool stillSearching;
            lock (gate)
            {
                stillSearching = searching && status == RouterMappingStatus.Searching;
            }
            if (!stillSearching) return;
            lock (gate)
            {
                searching = false;
                status = RouterMappingStatus.NoRouterFound;
                lastError = "";
            }
            try { NatUtility.StopDiscovery(); } catch { /* ignore */ }
            log?.Invoke("UPnP discovery timed out — no router responded");
            RaiseChanged();
        });
    }

    /// <summary>Re-run discovery and re-create the mapping. Used by the resume handler to
    /// recover from routers that drop NAT entries during the user's sleep window.</summary>
    public void Refresh()
    {
        lock (gate)
        {
            if (disposed) return;
        }
        log?.Invoke("UPnP refresh requested");
        // Drop any existing mapping; Start() will rediscover and remap.
        RemoveMappingBestEffort();
        try { NatUtility.StopDiscovery(); } catch { /* ignore */ }
        lock (gate)
        {
            searching = false;
            status = RouterMappingStatus.Disabled;
            device = null;
            mapping = null;
            externalAddress = null;
        }
        RaiseChanged();
        Start();
    }

    /// <summary>Politely remove the mapping and stop discovery. Safe to call from
    /// <c>FormClosing</c> or app shutdown.</summary>
    public void Stop()
    {
        lock (gate)
        {
            if (disposed) return;
        }
        RemoveMappingBestEffort();
        try { NatUtility.StopDiscovery(); } catch { /* ignore */ }
        try { NatUtility.DeviceFound -= OnDeviceFound; } catch { /* ignore */ }
        lock (gate)
        {
            searching = false;
            status = RouterMappingStatus.Disabled;
            device = null;
            mapping = null;
            externalAddress = null;
            lastError = "";
        }
        log?.Invoke("UPnP stopped");
        RaiseChanged();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { Stop(); } catch { /* shutting down */ }
    }

    private void OnDeviceFound(object? sender, DeviceEventArgs args)
    {
        try
        {
            var found = args.Device;
            log?.Invoke($"UPnP device found: {found.GetType().Name}");

            // Add the mapping. Mono.Nat's CreatePortMap is synchronous-but-quick; doing it
            // on the discovery thread is acceptable. If the same port is already mapped to
            // a different internal IP, the router will reject — surface that as MappingFailed
            // so the Preferences label can tell the user.
            try
            {
                var m = new Mapping(Protocol.Udp, AudioPort, AudioPort, MappingLeaseSeconds, "RemSound audio");
                found.CreatePortMap(m);
                IPAddress? ext = null;
                try { ext = found.GetExternalIP(); }
                catch (Exception ipEx) { log?.Invoke($"UPnP GetExternalIP failed: {ipEx.GetType().Name}: {ipEx.Message}"); }

                lock (gate)
                {
                    device = found;
                    mapping = m;
                    externalAddress = ext;
                    searching = false;

                    // Detect CGNAT — RFC 6598 reserves 100.64.0.0/10 for carrier-grade NAT.
                    // If the router's "external" address is in that range, UPnP succeeded
                    // but we're still behind another tier of NAT we can't open.
                    if (ext is not null && IsCgnatAddress(ext))
                    {
                        status = RouterMappingStatus.CgnatDetected;
                        lastError = "";
                        log?.Invoke($"UPnP mapping added but external address {ext} is in the CGNAT range — peers will not reach this machine via UPnP alone");
                    }
                    else
                    {
                        status = RouterMappingStatus.Mapped;
                        lastError = "";
                        log?.Invoke($"UPnP mapping added: external {ext}:{AudioPort} -> internal :{AudioPort}");
                    }
                }
            }
            catch (Exception ex)
            {
                lock (gate)
                {
                    searching = false;
                    status = RouterMappingStatus.MappingFailed;
                    lastError = ex.Message;
                }
                log?.Invoke($"UPnP mapping creation failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"UPnP DeviceFound handler threw: {ex.GetType().Name}: {ex.Message}");
        }
        RaiseChanged();
    }

    private void RemoveMappingBestEffort()
    {
        INatDevice? d;
        Mapping? m;
        lock (gate)
        {
            d = device;
            m = mapping;
        }
        if (d is null || m is null) return;
        try
        {
            d.DeletePortMap(m);
            log?.Invoke($"UPnP mapping removed (port {AudioPort})");
        }
        catch (Exception ex)
        {
            log?.Invoke($"UPnP mapping removal failed (harmless — the router will expire it): {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool IsCgnatAddress(IPAddress addr)
    {
        if (addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = addr.GetAddressBytes();
        // 100.64.0.0/10 — RFC 6598 shared address space for CGNAT.
        return b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }

    private void RaiseChanged()
    {
        try { StatusChanged?.Invoke(this, EventArgs.Empty); }
        catch { /* event handlers shouldn't escape on their own thread */ }
    }
}
