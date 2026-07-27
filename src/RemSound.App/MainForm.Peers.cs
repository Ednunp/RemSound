using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NAudio.CoreAudioApi;
using RemSound.Core;
using RemSound.Receiver;
using RemSound.Sender;

namespace RemSound.App;

/// <summary>
/// The PEER half of the main window — state, discovery/selection reconciliation, arming, naming and
/// the remembered-peers plumbing — split out of MainForm.cs verbatim in the 2026-07-26 review's
/// god-object shrink (same partial-class pattern Andre's SensorReadout form uses). Pure code motion:
/// same class, same members, no behaviour change — the compiler proves it. The shared arming/address
/// LOGIC these methods lean on (PeerArming, PeerAddress) lives in Core; the send-spec builder
/// (CaptureSpecBuilder) stays in App by necessity — it depends on App's AudioDefaultFollower and the
/// Sender's app enumerator — but is likewise shared with the service.
/// </summary>
public sealed partial class MainForm
{
    // --- Peer state ---
    private readonly Dictionary<Guid, PeerAnnouncement> knownPeers = [];
    private readonly Dictionary<Guid, PeerAnnouncement> manualPeers = [];
    private readonly Dictionary<string, Guid> rememberedPeerInstanceIds = new(StringComparer.OrdinalIgnoreCase);

    // Endpoint targets the user has ticked. STICKY — once a peer is selected, its IP/port stays
    // here regardless of whether discovery currently sees it. Discovery turnover (peer briefly
    // offline, NIC blips, sleep, etc.) does NOT untick or stop the sender. UDP just keeps flowing
    // toward the cached IP; if no one's home, packets disappear, and they resume the moment the
    // peer comes back. Neither machine has to be online "first" or "in order".
    //
    // Key: peer instance Guid (or generated one for IP-only manual entries).
    // Value: last-known endpoint. If discovery sees the same instance with a new address (DHCP
    // renewal etc.) we update the value but keep the key.
    private readonly Dictionary<Guid, IPEndPoint> selectedPeerEndpoints = [];
    // Display labels for selected peers so we can render them in the dialog list even when
    // discovery has temporarily lost sight of them ("Foo (192.168.1.5) — offline").
    private readonly Dictionary<Guid, string> selectedPeerLabels = [];
    // The "named peers" book, keyed by peer identity (machine name, else address). Loaded from AppConfig
    // (machine-wide) at startup and mirrored back on change. Resolved to display names everywhere a peer
    // shows — connected/discovered lists, the volume/pan/EQ list, the status line, split recordings.
    // Only deliberately-renamed peers live here. Last address / last-seen updated as they connect.
    private Dictionary<string, NamedPeer> namedPeers = new(StringComparer.OrdinalIgnoreCase);
    private bool namedPeersDirty;   // an address changed this session; flush on the next tick
    // When each connected peer (by per-run InstanceId) first went healthy — for the "connected for" line.
    private readonly Dictionary<Guid, DateTime> peerConnectedSinceUtc = [];

    // Anti-thrash state for the discovery-driven endpoint follow (see the peer-rebuild loop). A peer
    // reachable at two addresses at once (a VPN address AND a LAN address, say) announces from both,
    // and discovery reports whichever it heard last; following that blindly made the tracked endpoint
    // ping-pong between the two, and a fast ping-pong tore the receiver's audio session down and back
    // up quickly enough to crash the app (#16). A follow now needs the current endpoint to have been
    // unreachable for a sustained spell and can't fire more than once per cooldown. Keyed by peer id.
    private readonly Dictionary<Guid, DateTime> endpointUnreachableSinceUtc = [];
    private readonly Dictionary<Guid, DateTime> lastEndpointMoveUtc = [];
    private static readonly TimeSpan EndpointMoveUnreachableGrace = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan EndpointMoveCooldown = TimeSpan.FromSeconds(15);

    // ===================== Peers =====================

    private void RefreshKnownPeers()
    {
        knownPeers.Clear();
        // Discovered peers go in first so manual peers added by IP don't shadow them.
        foreach (var peer in discovery.Peers) knownPeers[peer.InstanceId] = peer;
        foreach (var peer in manualPeers.Values) knownPeers[peer.InstanceId] = peer;

        // Locked-to-fixed-addresses profiles (#17): the user wants RemSound to use exactly the peer
        // addresses they set and never substitute one found on the network. Skip the discovered-peer
        // merge (which would attach a computer name to their selection) and the address-follow below;
        // the selection's allow-list is still pushed so audio flows to those exact addresses, unchanged.
        if (settings.LoadLockPeerAddresses())
        {
            PushAllowedReceiveSenders();
            return;
        }

        // Dedupe by endpoint (address:port). When a manual peer (typed by IP) and a discovered
        // peer (broadcasting hostname) point to the same machine, drop the manual entry and
        // forward any active selection to the discovered peer so the user doesn't lose it.
        // Prefer entries whose Name is NOT just the IP — those are real hostnames.
        var byEndpoint = new Dictionary<string, PeerAnnouncement>(StringComparer.OrdinalIgnoreCase);
        var redirectedSelections = new List<(Guid From, Guid To)>();
        foreach (var peer in knownPeers.Values.ToList())
        {
            var key = $"{peer.Address}:{peer.AudioPort}";
            if (!byEndpoint.TryGetValue(key, out var existing))
            {
                byEndpoint[key] = peer;
                continue;
            }
            // Prefer the one with a real hostname (Name != IP-as-string).
            var existingIsIp = existing.Name == existing.Address.ToString();
            var peerIsIp = peer.Name == peer.Address.ToString();
            var winner = existingIsIp && !peerIsIp ? peer : existing;
            var loser = winner == existing ? peer : existing;
            byEndpoint[key] = winner;
            // Move loser's selection (if any) to winner so the checkbox state survives.
            if (selectedPeerEndpoints.ContainsKey(loser.InstanceId))
            {
                redirectedSelections.Add((loser.InstanceId, winner.InstanceId));
            }
        }

        foreach (var (from, to) in redirectedSelections)
        {
            if (selectedPeerEndpoints.Remove(from, out var endpoint))
            {
                selectedPeerEndpoints[to] = endpoint;
                if (selectedPeerLabels.Remove(from, out var label))
                {
                    selectedPeerLabels[to] = label;
                }
                // The "manual peer" that lost out should be removed from manualPeers too,
                // otherwise the next discovery refresh re-creates the duplicate.
                manualPeers.Remove(from);
            }
        }

        knownPeers.Clear();
        foreach (var peer in byEndpoint.Values) knownPeers[peer.InstanceId] = peer;

        // If a selected peer's announced address changed (DHCP renewal, network switch), follow it to
        // the new address — but conservatively. A peer reachable at two addresses at once (e.g. a VPN
        // address AND a LAN address) announces from both, and discovery reports whichever it heard
        // last. Following that blindly made the tracked endpoint ping-pong between the two; because
        // this one endpoint feeds the audio sender, the heartbeat AND the receiver's allow-list, the
        // churn was heard as crackle (Tech Singer's Win7-over-VPN report, 2026-05-31) and, when it
        // thrashed fast enough, tore the receiver's audio session down and back up quickly enough to
        // crash the app (#16, same singer). So: never move off an address that's still answering
        // heartbeats; only follow once the current one has been unreachable for a sustained spell,
        // only TO an address that is itself answering, and never more than once per cooldown. Net
        // effect — a peer you reach on a working address stays put; a genuine move (the old address
        // really went away) is still followed a few seconds later.
        var nowUtc = DateTime.UtcNow;
        foreach (var (id, oldEndpoint) in selectedPeerEndpoints.ToList())
        {
            if (!knownPeers.TryGetValue(id, out var peer)) continue;
            selectedPeerLabels[id] = ResolvePeerDisplayName(peer);

            var newEndpoint = new IPEndPoint(peer.Address, peer.AudioPort);
            // Same address, or the one we're on is still healthy: nothing to do — and reset the
            // unreachable-since clock so a brief future blip starts counting from zero.
            if (newEndpoint.Equals(oldEndpoint) || IsEndpointHeartbeatHealthy(oldEndpoint))
            {
                endpointUnreachableSinceUtc.Remove(id);
                continue;
            }
            // The current endpoint isn't answering. Start (or read) its unreachable-since clock, and
            // don't act on the very first unhealthy tick — wait out the grace period.
            if (!endpointUnreachableSinceUtc.TryGetValue(id, out var downSince))
            {
                endpointUnreachableSinceUtc[id] = nowUtc;
                continue;
            }
            if (nowUtc - downSince < EndpointMoveUnreachableGrace) continue;   // not down long enough yet
            if (!IsEndpointHeartbeatHealthy(newEndpoint)) continue;           // don't chase a dead address
            if (lastEndpointMoveUtc.TryGetValue(id, out var lastMove)
                && nowUtc - lastMove < EndpointMoveCooldown) continue;        // anti-thrash cooldown

            selectedPeerEndpoints[id] = newEndpoint;
            lastEndpointMoveUtc[id] = nowUtc;
            endpointUnreachableSinceUtc.Remove(id);
            logFile.Event($"peer {peer.Name} endpoint moved {oldEndpoint} -> {newEndpoint} (old endpoint unreachable {(int)(nowUtc - downSince).TotalSeconds}s)");
        }

        // Endpoints may have moved (DHCP/announcement-update path above) or selections may have
        // been redirected (manual-peer-merged-into-discovered above). Push the latest set down
        // to the receiver's allow-list so we don't keep accepting from a stale endpoint we no
        // longer recognise as a selected peer.
        PushAllowedReceiveSenders();
    }

    private void SelectPeer(PeerAnnouncement peer) => SelectPeer(peer, fromProfileRestore: false);

    private void SelectPeer(PeerAnnouncement peer, bool fromProfileRestore)
    {
        selectedPeerEndpoints[peer.InstanceId] = new IPEndPoint(peer.Address, peer.AudioPort);
        selectedPeerLabels[peer.InstanceId] = ResolvePeerDisplayName(peer);
        logFile.Event($"peer selected: {peer.Name} {peer.Address}:{peer.AudioPort}");
        InvalidateAutoTuneHistory();
        PushAllowedReceiveSenders();
        // fromProfileRestore=true means the call originated from auto-reconnect at startup;
        // we don't want that to flag the profile as dirty. User-initiated selects do.
        if (!fromProfileRestore) MarkProfileDirty();
    }

    private void DeselectPeer(Guid instanceId)
    {
        if (selectedPeerEndpoints.Remove(instanceId))
        {
            selectedPeerLabels.TryGetValue(instanceId, out var label);
            selectedPeerLabels.Remove(instanceId);
            logFile.Event($"peer deselected: {label ?? instanceId.ToString()}");
            InvalidateAutoTuneHistory();
            PushAllowedReceiveSenders();
            MarkProfileDirty();
        }
    }

    /// <summary>
    /// Tells the receiver which sender endpoints are allowed to play audio. Same set as the
    /// peers we're sending to (the checkbox controls both directions). Called whenever the
    /// user selects/deselects a peer, and once at startup so the receiver is in a known state.
    /// Without this, anyone who can reach our UDP port (e.g. a peer who chose us first) would
    /// auto-play to our speakers — we want explicit consent via the checkbox.
    /// </summary>
    private void PushAllowedReceiveSenders()
    {
        // Accept audio from ANY source IP a selected peer is known to use, not only the single address
        // we currently target. A multi-homed sender (on a LAN and a VPN at once) can egress audio from a
        // different interface than the one we discovered or dialled; allow-listing just the one made the
        // receiver silently drop that audio while heartbeats (which skip this check) kept the peer
        // looking connected — connected but silent (#18). The SEND targets stay single-address; only the
        // accept-list widens, and only to other addresses the SAME peer (by InstanceId) announced from.
        var allowed = new List<IPEndPoint>();
        var seen = new HashSet<IPAddress>();
        foreach (var (id, ep) in selectedPeerEndpoints)
        {
            if (seen.Add(ep.Address)) allowed.Add(new IPEndPoint(ep.Address, 0));
            foreach (var addr in discovery.GetKnownAddresses(id))
                if (seen.Add(addr)) allowed.Add(new IPEndPoint(addr, 0));
        }
        receiver.SetAllowedSenders(allowed);
    }

    /// <summary>True if the heartbeat currently considers <paramref name="endpoint"/> healthy —
    /// i.e. we're getting pongs back from exactly that address+port right now. Used to keep the
    /// audio target pinned to a proven-good endpoint instead of chasing a multi-homed peer's
    /// other (possibly unreachable) advertised address every discovery refresh. 2026-05-31.</summary>
    private bool IsEndpointHeartbeatHealthy(IPEndPoint endpoint)
    {
        if (heartbeatService is null) return false;
        foreach (var h in heartbeatService.GetAllPeerHealth())
        {
            if (h.State == PeerHealthState.Healthy && h.AudioEndpoint.Equals(endpoint))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Wipes the rolling max-gap window and pushes <see cref="lastSourceChangeUtc"/> forward,
    /// so the next continuous auto-tune tick has nothing to react to. Called whenever a user
    /// action (peer (de)selection, source list toggle, manually moving the latency slider) is
    /// likely to produce a measured "gap" that doesn't reflect the network — e.g. the user
    /// reselecting localhost after a 5 s pause records a 5 s inter-arrival gap, which would
    /// otherwise pin the auto-tune to its 200 ms cap for half a minute.
    /// </summary>
    private void InvalidateAutoTuneHistory()
    {
        recentMaxGaps.Clear();
        recentRenderCbGaps.Clear();
        lastSourceChangeUtc = DateTime.UtcNow;
    }

    private IPEndPoint[] SelectedSendEndpoints()
    {
        // Collapse duplicates by ip:port so the same address isn't targeted twice.
        return selectedPeerEndpoints.Values
            .GroupBy(ep => $"{ep.Address}:{ep.Port}")
            .Select(g => g.First())
            .ToArray();
    }

    /// <summary>The ticked ASIO channel-pair indices in a list (parsed from the synthetic "asio:N"
    /// ids). Internal + static so the self-test can pin the per-driver tick memory round-trip.</summary>
    internal static int[] SnapshotAsioTicks(CheckedListBox list)
    {
        var pairs = new List<int>();
        for (var i = 0; i < list.Items.Count; i++)
        {
            if (list.GetItemChecked(i) && list.Items[i] is AudioDeviceChoice { DeviceId: { } id }
                && AsioDeviceId.TryParse(id, out var pair))
            {
                pairs.Add(pair);
            }
        }
        return pairs.ToArray();
    }

    /// <summary>Re-tick the given pair indices in a freshly rebuilt ASIO list. Pairs the new driver
    /// doesn't have (fewer channels) are silently skipped. Caller must hold suppressDeviceCheckChange.</summary>
    internal static void RestoreAsioTicks(CheckedListBox list, int[] pairs)
    {
        if (pairs.Length == 0) return;
        var wanted = new HashSet<int>(pairs);
        for (var i = 0; i < list.Items.Count; i++)
        {
            if (list.Items[i] is AudioDeviceChoice { DeviceId: { } id }
                && AsioDeviceId.TryParse(id, out var pair) && wanted.Contains(pair))
            {
                list.SetItemChecked(i, true);
            }
        }
    }

    // Address split + resolve moved to Core (PeerAddress) so the app and the service share one
    // implementation — these thin wrappers keep the existing call sites readable.
    private static Task<IPAddress?> ResolvePeerAddressAsync(string text) => PeerAddress.ResolveHostAsync(text);

    internal static (string host, int? port) TrySplitHostPort(string text) => PeerAddress.Split(text);

    private PeerAnnouncement CreateManualPeer(string entry, IPAddress address)
    {
        var (_, parsedPort) = TrySplitHostPort(entry);
        var label = string.IsNullOrWhiteSpace(entry) ? address.ToString() : entry.Trim();
        return new PeerAnnouncement(
            Guid.NewGuid(),
            label,
            parsedPort ?? RemPacket.DefaultPeerDialPort,
            CanSend: true,
            CanReceive: true,
            DateTime.UtcNow,
            address);
    }

    private async Task AddManualPeerAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show(this, "Enter an IP address or hostname for the other computer.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var address = await ResolvePeerAddressAsync(text);
        if (address is null)
        {
            MessageBox.Show(this, "Could not resolve that IP address or hostname.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var rememberedEntries = settings.LoadRememberedPeers()
            .Select(static value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        rememberedEntries.Add(text.Trim());
        settings.SaveRememberedPeers(rememberedEntries);

        var peer = CreateManualPeer(text, address);
        manualPeers[peer.InstanceId] = peer;
        rememberedPeerInstanceIds[text.Trim()] = peer.InstanceId;
        SelectPeer(peer);
        // New peer in remembered/manual list → tell discovery to start unicasting announcements
        // at this address so they discover us back across VPN/WAN.
        PushDiscoveryUnicastHints();
        logFile.Event($"manual peer added {address}:{peer.AudioPort} ({text.Trim()})");

        RefreshKnownPeers();
        ApplyAudioRuntime();
    }

    private void LoadRememberedPeersFromSettings()
    {
        // No checkboxes on the form for these — they live in the dialog. We just remember them.
        rememberedPeerInstanceIds.Clear();
    }

    /// <summary>
    /// Adds a peer's identity to the persisted Remembered list (if not already present), and
    /// records the entry → instance-id mapping so the Remembered dialog can display it. Used
    /// when connecting via the Discovered list — per Ed's spec, "Remembered" is the long
    /// history of every peer ever connected to, not just manually-added ones.
    /// </summary>
    private void EnsurePeerRemembered(PeerAnnouncement peer)
    {
        var entry = string.IsNullOrWhiteSpace(peer.Name) || peer.Name == peer.Address.ToString()
            ? peer.Address.ToString()
            : peer.Name;
        var existing = settings.LoadRememberedPeers().ToList();
        if (existing.Any(e => string.Equals(e, entry, StringComparison.OrdinalIgnoreCase)))
        {
            // Already remembered — make sure the id mapping is current so
            // SyncDialogRememberedPeerList correctly hides this entry while the peer is connected.
            rememberedPeerInstanceIds[entry] = peer.InstanceId;
            PushDiscoveryUnicastHints();
            return;
        }
        existing.Add(entry);
        settings.SaveRememberedPeers(existing);
        rememberedPeerInstanceIds[entry] = peer.InstanceId;
        PushDiscoveryUnicastHints();
    }

    /// <summary>
    /// Tells the discovery service which IPs to send unicast announcements to. LAN broadcast
    /// alone doesn't reach peers across a VPN (Tailscale, WireGuard, etc.) — so we explicitly
    /// announce to every remembered + manual peer IP on top of broadcast. Anyone in our
    /// remembered list who's running RemSound and reachable will then appear in Discovered,
    /// regardless of physical network. Sending to an offline peer is a no-op.
    /// </summary>
    private void PushDiscoveryUnicastHints()
    {
        // Snapshot the UI-thread-owned inputs HERE, then resolve hostnames OFF the UI thread.
        //
        // The comment that used to live here claimed Dns.GetHostAddresses "returns near-instantly".
        // It does for a parsed IP or an already-cached name — but for a remembered HOSTNAME that
        // can't currently resolve (an offline peer, or a Tailscale/WireGuard name while the VPN is
        // down) it BLOCKS for the system DNS timeout, seconds per entry. This method runs on the UI
        // thread on every connect / disconnect / add-peer (it's how discovery learns its VPN unicast
        // targets), so that block froze the whole window for a few seconds — which a screen-reader
        // user experiences as the entire machine locking up (issue #10). Same class of bug as the
        // v3.0.1 UPnP-on-the-UI-thread hang, in a newer feature.
        //
        // SetUnicastPeerAddresses just swaps a snapshot reference and fires an announcement, and is
        // already called from the discovery receive loop's own thread, so it's safe to call from a
        // background thread here. The hints are advisory and re-pushed frequently, so a slightly
        // stale result from an overlapping resolution is harmless.
        var seedAddresses = manualPeers.Values.Select(p => p.Address).ToList();
        var rememberedEntries = settings.LoadRememberedPeers().ToList();
        Task.Run(() =>
        {
            var hints = new HashSet<IPAddress>(seedAddresses);
            foreach (var entry in rememberedEntries)
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;
                if (IPAddress.TryParse(entry, out var direct))
                {
                    hints.Add(direct);
                    continue;
                }
                try
                {
                    foreach (var addr in Dns.GetHostAddresses(entry))
                    {
                        if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            hints.Add(addr);
                        }
                    }
                }
                catch
                {
                    // Not resolvable right now — skip; re-pushed next time this method runs.
                }
            }
            discovery.SetUnicastPeerAddresses(hints);
        });
    }


    /// <summary>After a Delete in a remembered list: focus the next item AND speak the outcome through
    /// the screen reader. The speech is load-bearing, not decoration: the next item usually lands on the
    /// SAME index the deleted one had, and the list already has focus — so no focus or selection event
    /// fires and NVDA would otherwise say nothing at all (Ed, 2026-07-26: deleting foobar gave silence).
    /// Speaks "«deleted» removed." plus the row now under focus, or that the list is empty.</summary>
    private static void FocusAndAnnounceAfterDelete(CheckedListBox list, string deletedLabel, int prevIndex)
    {
        FocusListItemAfterDelete(list, prevIndex);
        var now = list.SelectedItem?.ToString();
        ScreenReader.Speak(string.IsNullOrWhiteSpace(now)
            ? $"{deletedLabel} removed. The list is empty."
            : $"{deletedLabel} removed. {now}.");
    }

    /// <summary>
    /// After deleting an item from a CheckedListBox, focus the next sensible item so NVDA
    /// announces the new selection. If something exists at the same index that the deleted
    /// item occupied, focus that (it's the next-down). Otherwise drop back to the last item.
    /// Empty list = no focus change.
    /// </summary>
    private static void FocusListItemAfterDelete(CheckedListBox list, int prevIndex)
    {
        if (list.IsDisposed) return;
        var count = list.Items.Count;
        if (count == 0) return;
        var target = Math.Clamp(prevIndex, 0, count - 1);
        list.SelectedIndex = target;
        if (!list.Focused) list.Focus();
    }

    private void RemoveSelectedRememberedPeer(CheckedListBox list)
    {
        if (list.SelectedItem is not RememberedPeerItem selected) return;
        if (rememberedPeerInstanceIds.TryGetValue(selected.Entry, out var pid))
        {
            manualPeers.Remove(pid);
            DeselectPeer(pid);
            rememberedPeerInstanceIds.Remove(selected.Entry);
        }
        var remaining = settings.LoadRememberedPeers().Where(e => !string.Equals(e, selected.Entry, StringComparison.OrdinalIgnoreCase));
        settings.SaveRememberedPeers(remaining);
        RefreshKnownPeers();
        ApplyAudioRuntime();
        PushDiscoveryUnicastHints();
    }

}
