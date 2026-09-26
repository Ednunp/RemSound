using System.Net;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// What happens when somebody else ticks you. Ed's request, 2026-09-20.
///
/// <para>RemSound has always needed both sides to tick each other, which is safe but means somebody who has ticked you
/// waits in silence until you happen to look. There are now three ways to handle it, in Preferences, Connectivity, kept
/// machine-wide so it holds whichever profile is loaded:</para>
///
/// <list type="bullet">
/// <item><b>Prompt</b> (the default) — a question with their name and where they are, answered once.</item>
/// <item><b>Automatically</b> — tick them back the moment they tick you.</item>
/// <item><b>Manual</b> — nothing happens until you tick them yourself. How RemSound has always worked.</item>
/// </list>
///
/// <para>There are two ways to know somebody has ticked you. On a network, their audio arrives at our port and is
/// turned away, which only happens when they have chosen us. On a relay, the member list says so outright. Both land
/// here, and somebody on a different password is never offered or accepted either way: their audio could not be
/// played, so there is nothing to agree to.</para>
/// </summary>
public sealed partial class MainForm
{
    /// <summary>Addresses and relay people already asked about, so one answer lasts the run. A "no" is remembered the
    /// same as a "yes": the point of asking once is not asking again.</summary>
    private readonly HashSet<string> acceptAsked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>People the user has unticked themselves this run. Without this, unticking somebody who still has you
    /// ticked puts them straight back: their audio keeps arriving, this sees audio from somebody unticked, and ticks
    /// them. Ed, 2026-09-21. It holds until the user ticks them again, or until the next run — a fresh start with
    /// nobody ticked is what "connect when someone ticks me" is for.</summary>
    private readonly HashSet<string> acceptRefusedByUser = new(StringComparer.OrdinalIgnoreCase);
    private bool acceptPromptOpen;
    private int acceptAskCount;

    private static PeerAcceptMode AcceptMode()
    {
        try { return AppConfig.Load().AcceptPeerConnections; }
        catch { return PeerAcceptMode.Prompt; }
    }

    /// <summary>
    /// Somebody not in our list is sending us audio, so they have ticked us. Act on the setting.
    /// </summary>
    private void OnSomeoneWantsToConnect(IPEndPoint remote, bool samePassword)
    {
        if (IsDisposed) return;
        var mode = AcceptMode();
        if (mode == PeerAcceptMode.Manual) return;
        if (!samePassword)
        {
            logFile.Event($"accept connections: {remote.Address} is sending to us on a different password — ignored");
            return;
        }
        var peer = KnownPeerAt(remote.Address) ?? CreateManualPeer(remote.Address.ToString(), remote.Address);
        ConsiderAccepting(remote, peer, mode, "is sending to us");
    }

    // ---- Tick proofs: "I have ticked you, and I hold the password" (Ed, 2026-09-25) ----------------------------------
    //
    // "Anyone who has the password should be prompted or auto accepted", whatever they send or receive. Audio proves the
    // password (its format packet carries the fingerprint), but a peer that only listens - a phone ticking a PC to hear it
    // - sends none, and a ping proves nothing. So every peer tells everybody it has ticked, every few seconds, with a
    // proof sealed by the password (TickProof). Nobody without the password is ever offered or accepted.

    private readonly TickProofGuard tickProofGuard = new();
    private readonly object tickProofGate = new();
    private readonly Dictionary<string, long> tickProofLastSeen = new(StringComparer.OrdinalIgnoreCase);
    private DateTime nextTickProofUtc = DateTime.MinValue;
    private uint tickProofSequence;
    private int tickProofsReachedWindow;
    private readonly HashSet<string> tickProofAnnounced = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A tick proof arrived, on the network thread. At most one per address every two seconds goes on to the
    /// window's thread - a peer sends one every five - so a flood costs a dictionary lookup, not a decryption each.</summary>
    private void OnTickProofArrived(byte[] payload, IPEndPoint remote)
    {
        var now = Environment.TickCount64;
        lock (tickProofGate)
        {
            var key = remote.Address.ToString();
            if (tickProofLastSeen.TryGetValue(key, out var last) && now - last < 2000) return;
            if (tickProofLastSeen.Count >= 1024) tickProofLastSeen.Clear();
            tickProofLastSeen[key] = now;
        }
        try { BeginInvoke(() => OnTickProof(payload, remote)); }
        catch (ObjectDisposedException) { /* closing */ }
        catch (InvalidOperationException) { /* no handle yet */ }
    }

    /// <summary>Somebody has ticked us and says they hold the password. If the seal opens with OUR key, they do: ask or
    /// accept, as the setting says - found as the very device the proof names, never merely by name or address.</summary>
    private void OnTickProof(byte[] payload, IPEndPoint remote)
    {
        Interlocked.Increment(ref tickProofsReachedWindow);
        if (IsDisposed) return;
        var mode = AcceptMode();
        if (mode == PeerAcceptMode.Manual) return;
        // People on a server arrive through the server's member list.
        if (RelayGroupClient.IsMemberAddress(remote.Address)) return;
        if (currentAudioKey is not { } key)
        {
            if (acceptAsked.Add($"proof-nokey:{remote.Address}"))
                logFile.Event($"accept connections: {remote.Address} has ticked us, but this profile has no password, so nobody can prove they share it — not offered or accepted");
            return;
        }
        if (!tickProofGuard.TryAccept(key, payload, DateTime.UtcNow, out var senderId, out var why))
        {
            if (acceptAsked.Add($"proof-rejected:{remote.Address}"))
                logFile.Event($"accept connections: {remote.Address} has ticked us but did not prove our password ({why}) — not offered or accepted");
            return;
        }
        // The device the proof names, where the proof came from. Two devices can share a name ("iPhone"); their identities
        // differ, and the proof carries its sender's.
        var named = discovery.Peers.FirstOrDefault(p => p.InstanceId == senderId);
        var peer = named is not null
            ? (named.Address.Equals(remote.Address) ? named : named with { Address = remote.Address })
            : KnownPeerAt(remote.Address) ?? CreateManualPeer(remote.Address.ToString(), remote.Address);
        ConsiderAccepting(remote, peer, mode, "has ticked us and proved our password");
    }

    /// <summary>Every few seconds, a sealed proof to each peer we have ticked - never to a server or to somebody behind
    /// one, whom the server's member list already covers. Nothing without a password: there would be nothing to prove.</summary>
    private void SendTickProofsIfDue()
    {
        if (!connected || currentAudioKey is not { } key) return;
        var nowUtc = DateTime.UtcNow;
        if (nowUtc < nextTickProofUtc) return;
        nextTickProofUtc = nowUtc + TickProof.SendInterval;
        var targets = TickProofTargets();
        if (targets.Count == 0) return;
        var packet = TickProof.BuildPacket(key, discovery.InstanceId, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ++tickProofSequence);
        foreach (var target in targets)
        {
            try { sender.SendVia(packet, packet.Length, target); }
            catch { /* UDP; the next round tries again */ }
            if (tickProofAnnounced.Add(target.ToString()))
                logFile.Event($"accept connections: telling {target} we have ticked them, with proof of the password (every {TickProof.SendInterval.TotalSeconds:0} s)");
        }
    }

    /// <summary>Who gets a tick proof: the peers we have ticked, less any server and anybody reached through one.</summary>
    private List<IPEndPoint> TickProofTargets()
    {
        var targets = new List<IPEndPoint>();
        foreach (var ep in selectedPeerEndpoints.Values)
        {
            if (RelayGroupClient.IsMemberAddress(ep.Address)) continue;
            if (relayGroup.RelayOf(ep) is not null) continue;
            if (relayGroup.ConnectedRelay is { } relay && relay.Address.Equals(ep.Address)) continue;
            if (!targets.Any(t => t.Equals(ep))) targets.Add(ep);
        }
        return targets;
    }

    /// <summary>The one decision both signs of a connection come to: not already ticked, not unticked by the user, not
    /// a server, asked about once - then tick them back, or ask.</summary>
    private void ConsiderAccepting(IPEndPoint remote, PeerAnnouncement peer, PeerAcceptMode mode, string how)
    {
        if (selectedPeerEndpoints.Values.Any(ep => ep.Address.Equals(remote.Address))) return;
        if (acceptRefusedByUser.Contains($"peer:{remote.Address}")) return;
        // A relay's own address is not a person: the people behind it arrive in the relay list with names of their own.
        if (relayGroup.ConnectedRelay is { } relay && relay.Address.Equals(remote.Address)) return;
        if (!acceptAsked.Add($"peer:{remote.Address}")) return;

        var name = ResolvePeerDisplayName(peer);
        logFile.Event($"accept connections: {name} at {remote.Address} {how} and is not ticked (mode={mode})");
        if (mode == PeerAcceptMode.Automatic)
        {
            AcceptPeer(peer, $"{name} at {remote.Address}");
            return;
        }
        AskThenAccept($"peer:{remote.Address}", $"{name} at {remote.Address} would like to connect to you.",
            () => AcceptPeer(peer, $"{name} at {remote.Address}"));
    }

    /// <summary>The peer we already know at this address, if any, so an accepted connection carries their real name
    /// and their identity rather than becoming a second entry for the same machine.</summary>
    private PeerAnnouncement? KnownPeerAt(IPAddress address)
    {
        foreach (var peer in knownPeers.Values)
        {
            if (peer.Address.Equals(address)) return peer;
        }
        return null;
    }

    private void AcceptPeer(PeerAnnouncement peer, string description)
    {
        // A peer discovery hears is held by its own identity, exactly as ticking it in the Discovered list holds it - never
        // filed as a hand-typed entry. Filed as one, the rule that joins a typed NAME to the one device of that name took it
        // for a name, found the OTHER device called "iPhone" on the network, and moved the connection there: Ed's phone was
        // accepted and his wife's got the stream (2026-09-25). Only somebody discovery does not hear is kept by hand.
        if (!DiscoveryHears(peer.InstanceId)) manualPeers[peer.InstanceId] = peer;
        SelectPeer(peer);
        EnsurePeerRemembered(peer);
        RefreshKnownPeers();
        SyncAllPeerLists();
        ApplyAudioRuntime();
        logFile.Event($"accept connections: connected to {description}");
        ScreenReader.Speak($"Connected to {description}.");
    }

    /// <summary>
    /// Somebody on the relay has ticked us and we have not ticked them. The relay's member list says so outright, so
    /// there is no guessing here. Called from the relay list's own refresh.
    /// </summary>
    private void OnRelayMemberWantsToConnect(RelayGroupClient.Member member)
    {
        var mode = AcceptMode();
        if (mode == PeerAcceptMode.Manual) return;
        // Everybody on a relay is on our password by definition — the relay groups by it — so there is nothing more
        // to check than that we have not already answered for this person.
        if (acceptRefusedByUser.Contains($"relay:{member.Id:D}")) return;
        if (!acceptAsked.Add($"relay:{member.Id:D}")) return;
        var name = RelayMemberName(member);
        logFile.Event($"accept connections: {name} has ticked us on the relay (mode={mode})");
        if (mode == PeerAcceptMode.Automatic)
        {
            AcceptRelayMember(member.Id, name);
            return;
        }
        AskThenAccept($"relay:{member.Id:D}", $"{name} on the server would like to connect to you.",
            () => AcceptRelayMember(member.Id, name));
    }

    private void AcceptRelayMember(Guid id, string name)
    {
        relayTicked.Add(id);
        relayListSignature = null;        // the row's tick has changed, so the next refresh rebuilds the list
        ApplyRelayTicks();
        MarkProfileDirty();
        logFile.Event($"accept connections: ticked {name} on the relay");
        ScreenReader.Speak($"Connected to {name}.");
    }

    /// <summary>
    /// The relay has put a phone or an older app in one of its ordinary pair slots beside us. That device chose this
    /// relay and the relay chose us, which is as close to "they have ticked you" as an app that knows nothing about
    /// ticking can get — so it goes through the same setting as everybody else.
    /// </summary>
    private void OnRelayPairPartnerWantsToConnect(IPEndPoint relay)
    {
        var mode = AcceptMode();
        if (mode == PeerAcceptMode.Manual) return;
        if (acceptRefusedByUser.Contains($"relay-phone:{relay}")) return;
        if (!acceptAsked.Add($"relay-phone:{relay}")) return;
        logFile.Event($"accept connections: a phone or older app is paired with us on {relay} (mode={mode})");
        if (mode == PeerAcceptMode.Automatic)
        {
            AcceptRelayPairPartner();
            return;
        }
        AskThenAccept($"relay-phone:{relay}", "Someone on a phone or an older app would like to connect to you.",
            AcceptRelayPairPartner);
    }

    private void AcceptRelayPairPartner()
    {
        relayPairTicked = true;
        relayListSignature = null;
        ApplyRelayTicks();
        MarkProfileDirty();
        logFile.Event("accept connections: ticked the phone or older app paired with us on the relay");
        ScreenReader.Speak("Connected to someone on a phone or an older app.");
    }

    /// <summary>
    /// Ask, in front of whatever is on screen, and act on a yes. One question at a time: while one is open the rest
    /// wait for the next time round, so a room of people arriving at once cannot stack dialogs on each other.
    /// </summary>
    private void AskThenAccept(string askedKey, string question, Action onYes)
    {
        // Put it off rather than stacking: forgetting that we asked is what brings the question back next time.
        if (acceptPromptOpen) { acceptAsked.Remove(askedKey); return; }
        acceptAskCount++;
        // Nobody is there to answer in a headless or silent run, and an unanswerable question is a hang.
        if (NobodyToAnswer) { logFile.Event($"accept connections: nobody to ask here — {question}"); return; }
        acceptPromptOpen = true;
        try
        {
            var answer = ForegroundDialog.Show(owner => AppMessageBox.Show(owner,
                $"{question}\n\nDo you want to allow this?", AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question));
            if (answer == DialogResult.Yes) onYes();
            else logFile.Event($"accept connections: declined — {question}");
        }
        finally { acceptPromptOpen = false; }
    }

    // ---- A device ticked before, back to listen (Ed, 2026-09-26: "build option 2") ------------------------------------
    //
    // A phone that only listens sends nothing that proves the password until its app sends the proof (TickProof) - only
    // pings, which anyone can send. So a device you have ticked yourself is remembered by its own identity, and when it
    // pings again it is treated as somebody wanting to connect: asked about, accepted, or left, as Accept connections says.
    // A device never ticked still waits for you. By identity and never by name: two phones called "iPhone" are two devices.

    private readonly Dictionary<string, long> untrackedPingSeen = new(StringComparer.Ordinal);

    /// <summary>A ping from somebody this copy is not pinging (receive thread). At most one a second per address reaches
    /// the window.</summary>
    private void OnPingFromUntracked(IPEndPoint remote)
    {
        if (RelayGroupClient.IsMemberAddress(remote.Address)) return;   // people on a server come through its list
        var key = remote.Address.ToString();
        var now = Environment.TickCount64;
        lock (untrackedPingSeen)
        {
            if (untrackedPingSeen.TryGetValue(key, out var last) && now - last < 1000) return;
            if (untrackedPingSeen.Count >= 256) untrackedPingSeen.Clear();
            untrackedPingSeen[key] = now;
        }
        try { BeginInvoke(() => ConsiderTickedBeforeDevice(remote)); }
        catch (InvalidOperationException) { /* no window yet, or closing */ }
    }

    private void ConsiderTickedBeforeDevice(IPEndPoint remote)
    {
        if (IsDisposed) return;
        var mode = AcceptMode();
        if (mode == PeerAcceptMode.Manual) return;
        if (KnownPeerAt(remote.Address) is not { } peer || !DiscoveryHears(peer.InstanceId)) return;
        if (!AppConfig.Load().TickedDeviceIds.Contains(peer.InstanceId.ToString("D"), StringComparer.OrdinalIgnoreCase)) return;
        ConsiderAccepting(remote, peer, mode, "has connected to listen, and is a device you have ticked before");
    }

    /// <summary>Remember a device you tick, by its own identity - only one discovery hears, which is a device on the
    /// network with an identity of its own (not a typed address, not somebody on a server).</summary>
    private void RememberTickedDevice(PeerAnnouncement peer)
    {
        if (RelayGroupClient.IsMemberAddress(peer.Address) || !DiscoveryHears(peer.InstanceId)) return;
        var id = peer.InstanceId.ToString("D");
        var config = AppConfig.Load();
        if (config.TickedDeviceIds.Contains(id, StringComparer.OrdinalIgnoreCase)) return;
        config.TickedDeviceIds.Add(id);
        while (config.TickedDeviceIds.Count > 64) config.TickedDeviceIds.RemoveAt(0);
        try
        {
            config.Save();
            logFile.Event($"accept connections: remembered {peer.Name} at {peer.Address} as a device you have ticked - it is accepted again when it connects to listen");
        }
        catch (Exception ex) { logFile.Event($"accept connections: could not remember {peer.Name}: {ex.GetType().Name}: {ex.Message}"); }
    }

    private void ForgetTickedDevices()
    {
        var config = AppConfig.Load();
        config.TickedDeviceIds.Clear();
        try { config.Save(); } catch { /* cleared again next time */ }
    }

    internal void ClearRememberedPeersForTest() { settings.SaveRememberedPeers(Array.Empty<string>()); rememberedPeerInstanceIds.Clear(); ForgetTickedDevices(); }

    // ------- test seams ----------------------------------------------------------------------------------------------

    internal void SomeoneWantsToConnectForTest(IPEndPoint remote, bool samePassword) => OnSomeoneWantsToConnect(remote, samePassword);
    internal void TickProofForTest(byte[] payload, IPEndPoint remote) => OnTickProof(payload, remote);
    internal void TickProofArrivedForTest(byte[] payload, IPEndPoint remote) => OnTickProofArrived(payload, remote);
    internal int TickProofsReachedWindowForTest => tickProofsReachedWindow;
    internal List<IPEndPoint> TickProofTargetsForTest() => TickProofTargets();
    internal Guid DiscoveryInstanceIdForTest => discovery.InstanceId;
    internal void RelayMemberWantsToConnectForTest(RelayGroupClient.Member member) => OnRelayMemberWantsToConnect(member);
    internal IReadOnlyCollection<string> AcceptAskedForTest => acceptAsked;
    internal int AcceptAskCountForTest => acceptAskCount;
}
