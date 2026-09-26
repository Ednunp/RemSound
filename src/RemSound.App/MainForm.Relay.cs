using System.Net;
using System.Net.Sockets;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// The relay half of the Connectivity tab. Ed's design, 2026-09-20: a relay is a place you connect to, not a person in
/// your peer list. You type its address, press Connect, and the people on it who share your password appear in a list
/// of their own. Tick them exactly as you tick anyone else: ticking means "I send to you and I accept yours", and the
/// relay passes sound between two people only when each has ticked the other.
///
/// <para>Nobody is ever typed in as a relay person; they only ever arrive from the relay's own list. The ordinary
/// add-by-address box is untouched, for people on your network or over Tailscale. If an address typed there turns out
/// to be a relay, RemSound offers to connect to it as one instead.</para>
/// </summary>
public sealed partial class MainForm
{
    private readonly Button relayConnectButton = new() { Text = "Co&nnect to server (Alt+N)", AutoSize = true, AccessibleName = "Connect to server" };
    private readonly CheckedListBox relayPeersList = new() { CheckOnClick = true, Width = 430, Height = 90, AccessibleName = "Discovered peers on server (Alt+0)" };
    private readonly Label relayPeersStatus = new() { AutoSize = true, Text = "Nobody on the server yet." };
    private MnemonicLabel? relayPeersLabel;
    private Label? relayStatusLine;

    /// <summary>Who we have ticked on the relay, by client id. Kept here rather than read off the list, because the
    /// list is rebuilt as people come and go.</summary>
    private readonly HashSet<Guid> relayTicked = [];
    private bool relayPairTicked;
    private bool suppressRelayTickEvents;
    /// <summary>The list as it was last built, so a screen reader is not interrupted by a rebuild that changes nothing.
    /// Null means "build it whatever happens". It cannot be the empty string: that is the REAL signature of an empty
    /// list, and using it as the force meant a list which legitimately emptied kept its stale rows for good.</summary>
    private string? relayListSignature;
    /// <summary>The relay's address as the user typed it. It used to live in a box on the tab; the box is in the relay
    /// window now, so the text is kept here — it is what a profile remembers and what the status line says.</summary>
    private string relayEntryText = "";
    /// <summary>The password the relay half last saw, so a change of it can start the list again. Null until the first
    /// time it is set, which is startup and not a change.</summary>
    private string? relayPasswordSignature;
    /// <summary>Names of the people last seen on the relay, and when someone dropped off, so a list being read is not
    /// reshuffled the instant somebody's connection hiccups.</summary>
    private readonly Dictionary<Guid, string> relayKnownNames = [];
    private readonly Dictionary<Guid, (string Name, DateTime GoneUtc)> relayRecentlyGone = [];
    private static readonly TimeSpan RelayGoneGrace = TimeSpan.FromSeconds(15);
    /// <summary>Who we have ticked, and whether the relay last said they had ticked us back. Sound only flows when both
    /// have ticked, so without this the app would simply go quiet and leave you guessing which it was.</summary>
    private readonly Dictionary<Guid, bool> relayTickedBack = [];
    /// <summary>When we last pressed Connect (or a profile put a server back), so a server that never answers can be told
    /// apart from one that is still being reached. Null while not connected.</summary>
    private DateTime? relayConnectedUtc;
    /// <summary>How long a server gets to send its first member list before the status line says it isn't answering.
    /// It sends one every second, so this is several missed in a row.</summary>
    internal static readonly TimeSpan RelayAnswerGrace = TimeSpan.FromSeconds(10);
    /// <summary>The kind of thing the status line last said, so the log records a change of state once rather than every
    /// second.</summary>
    private string relayStatusKind = "";
    /// <summary>Addresses we have already asked about ("that looks like a server"), so nobody is asked twice a run.</summary>
    private readonly HashSet<string> relayOfferAsked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One row in the relay list: a person, or the phone / older app we are paired with through the relay.</summary>
    private sealed record RelayPeerItem(Guid Id, string Label, bool IsPairPartner)
    {
        public override string ToString() => Label;
    }

    // ------- building the rows -------------------------------------------------------------------------------------

    /// <summary>The list of people on the relay, which sits with the other discovered-peers list. It is not there at
    /// all until you are on a relay: there is nothing it could show.</summary>
    private void BuildRelayPeersRow(TableLayoutPanel panel, int row)
    {
        relayPeersLabel = FormLayoutRows.AddCheckedListRow(panel, row, "Discovered peers on server (Alt+&0)", relayPeersList, relayPeersStatus, FocusListControl);
        WireCheckedListAccessibility(relayPeersList, relayPeersStatus, "relay peer");
        relayPeersList.ItemCheck += (_, args) =>
        {
            if (suppressRelayTickEvents) return;
            var index = args.Index;
            var ticked = args.NewValue == CheckState.Checked;
            BeginInvoke(() => OnRelayPeerTicked(index, ticked));
        };
    }

    /// <summary>The one button on the tab for all of this, and the line beside it saying what the relay is doing.
    /// Everything else — the address, the relays you have been on before, connecting and leaving — is in the window it
    /// opens. Ed, 2026-09-20: "just have a close button not a cancel, that's easier".</summary>
    private void BuildRelayButtonRow(TableLayoutPanel panel, int row)
    {
        var buttonRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
        buttonRow.Controls.Add(relayConnectButton);
        relayStatusLine = new Label { AutoSize = true, Text = "Not connected to a server.", AccessibleName = "Server status", Anchor = AnchorStyles.Left };
        buttonRow.Controls.Add(relayStatusLine);
        panel.Controls.Add(new Label { Text = "Server", AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        panel.Controls.Add(buttonRow, 1, row);
        relayConnectButton.Click += (_, _) => OnRelayConnectButton();
        UpdateRelayControls();
    }

    /// <summary>The button opens the relay window and leaves it open: connecting and leaving both happen in there, and
    /// the button inside says which of the two pressing it would do right now.</summary>
    private void ShowRelayDialog()
    {
        using var dialog = new RelayConnectDialog(
            connectedTo: () => relayGroup.ConnectedRelay is null ? null : (relayEntryText.Length > 0 ? relayEntryText : relayGroup.ConnectedRelay.ToString()),
            connect: entry => ConnectToRelay(entry, userAsked: true),
            disconnect: () => DisconnectFromRelay(userAsked: true),
            loadRemembered: () => settings.LoadRememberedRelays(),
            forget: ForgetRelay,
            status: () => relayStatusLine?.Text ?? "");
        ForegroundDialog.Show(owner => dialog.ShowDialog(owner));
        UpdateRelayControls();
    }

    // ------- connecting --------------------------------------------------------------------------------------------

    private void OnRelayConnectButton() => ShowRelayDialog();

    /// <summary>Connect to a relay. The address is resolved OFF the UI thread: a hostname that cannot resolve right now
    /// blocks for the DNS timeout, which on the UI thread reads as the whole app locking up (issue #10).</summary>
    private void ConnectToRelay(string typed, bool userAsked)
    {
        var entry = (typed ?? "").Trim();
        if (entry.Length == 0)
        {
            SetRelayStatus("Type a server address first.");
            return;
        }
        if (userAsked) ShowRelayNotice();
        // The person choosing a server themselves ends any wait for the profile's own.
        if (userAsked) pendingRelayName = null;
        // Going somewhere else is leaving here: the people you had ticked are on the relay you are leaving.
        if (relayGroup.ConnectedRelay is not null) DisconnectFromRelay(userAsked);
        relayEntryText = entry;
        // An address needs no looking up, so there is nothing to wait for: connect here and now. A profile putting
        // its own server back at start-up takes this path, and it is the one that has to work before the window has
        // a message loop to come back to.
        if (ResolveWithoutLookup(entry) is { } immediate)
        {
            FinishConnectingToRelay(entry, immediate, userAsked);
            return;
        }
        relayConnectButton.Enabled = false;
        SetRelayStatus($"Connecting to {entry}...");
        Task.Run(() => LookUpRelay(entry)).ContinueWith(task =>
        {
            var resolved = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
            try
            {
                BeginInvoke(() => FinishConnectingToRelay(entry, resolved, userAsked));
            }
            catch (ObjectDisposedException) { /* window closing */ }
            catch (InvalidOperationException) { /* window closing */ }
        }, TaskScheduler.Default);
    }

    /// <summary>The part that runs back on the UI thread once the address has resolved (or failed to).</summary>
    private void FinishConnectingToRelay(string entry, IPEndPoint? resolved, bool userAsked = true)
    {
        relayConnectButton.Enabled = true;
        if (resolved is null)
        {
            SetRelayStatus($"Couldn't find {entry}.");
            // The profile's own server, at start or on a switch: kept, and looked up again until it answers.
            if (!userAsked)
            {
                if (!string.Equals(pendingRelayName, entry, StringComparison.OrdinalIgnoreCase))
                    logFile.Event($"relay: could not resolve \"{entry}\" - trying again every {NameRetryInterval.TotalSeconds:0} s and when the network changes");
                pendingRelayName = entry;
            }
            else logFile.Event($"relay: could not resolve \"{entry}\"");
            return;
        }
        if (pendingRelayName is not null) logFile.Event($"relay: \"{entry}\" found at last");
        pendingRelayName = null;
        relayNotAnsweringSinceUtc = null;
        // Note the password we are joining under, so a later change of it is recognised as a move to different people. Not
        // known yet (the key is still being worked out, as it is at start-up) stays not known: recorded as an empty password,
        // the key arriving a moment later read as a CHANGE of password, and a profile that connects on start lost every tick
        // it had there and said "Password changed" (found 2026-09-25).
        relayPasswordSignature = currentAudioFingerprint is null ? null : Convert.ToHexString(currentAudioFingerprint);
        relayGroup.Connect(resolved);
        relayConnectedUtc = DateTime.UtcNow;
        ApplyServerPing();
        RememberRelay(entry);
        logFile.Event($"relay: connected to \"{entry}\" ({resolved})");
        ApplyRelayTicks();
        UpdateRelayControls();
        // A profile putting its own relay back is not a change to that profile.
        if (userAsked) MarkProfileDirty();
    }

    private void DisconnectFromRelay(bool userAsked)
    {
        var was = relayGroup.ConnectedRelay;
        foreach (var id in relayTicked.ToList()) DeselectPeer(id);
        relayGroup.Disconnect();
        relayConnectedUtc = null;
        ApplyServerPing();
        relayPairTicked = false;
        relayTickedBack.Clear();
        relayKnownNames.Clear();
        relayRecentlyGone.Clear();
        relayListSignature = null;
        PushAllowedReceiveSenders();
        ApplyAudioRuntime();
        SyncRelayPeersList();
        UpdateRelayControls();
        if (was is not null)
        {
            logFile.Event("relay: disconnected");
            if (userAsked) MarkProfileDirty();
        }
    }

    /// <summary>The server we are on is pinged every second for as long as we are on it, whoever is ticked: that ping, in
    /// ordinary form, is what claims the pair slot beside a phone or older app waiting there. It is pinged as a place,
    /// not a person (HeartbeatService.SetPingOnlyTargets), so it is never a row, a connect sound or a status line.
    ///
    /// <para>2026-09-24, found on Ed's desk: since the server became a place of its own on 2026-09-20 it was pinged only
    /// while somebody on it was ticked, so a PC alone on a server never answered a waiting phone - "unreachable" on the
    /// phone for ever. The lock-screen service kept the ping and so never had the fault.</para></summary>
    private void ApplyServerPing()
    {
        var targets = ServerPingTargets();
        var said = targets.Length == 0 ? "" : targets[0].ToString();
        if (said != serverPingLogged)
        {
            logFile.Event(said.Length == 0
                ? "heartbeat: no longer pinging a server"
                : $"heartbeat: pinging the server at {said} every second, so a phone or older app waiting there can be paired with us");
            serverPingLogged = said;
        }
        heartbeatService?.SetPingOnlyTargets(targets);
    }

    private string serverPingLogged = "";

    private IPEndPoint[] ServerPingTargets() => relayGroup.ConnectedRelay is { } relay ? [relay] : [];

    /// <summary>Gate seam: the heartbeat on its own, sending through <paramref name="transport"/> instead of the audio
    /// socket, wired exactly as <see cref="Connect"/> wires it. The window's network is otherwise never started headless.</summary>
    internal HeartbeatService StartHeartbeatForTest(Func<byte[], int, IPEndPoint, bool> transport)
    {
        heartbeatService?.Dispose();
        heartbeatService = new HeartbeatService { SendTransport = transport, RelayOfMember = relayGroup.RelayOf, OnPingFromUntracked = OnPingFromUntracked };
        ApplyServerPing();
        heartbeatService.Start();
        return heartbeatService;
    }

    internal IPEndPoint[] ServerPingTargetsForTest() => ServerPingTargets();

    /// <summary>The host and port the user typed, split apart. No looking anything up.</summary>
    private static (string Host, int Port) SplitRelayAddress(string entry)
    {
        var host = entry;
        var port = RemPacket.DefaultPort;
        var colon = entry.LastIndexOf(':');
        if (colon > 0 && !IPAddress.TryParse(entry, out _)
            && int.TryParse(entry[(colon + 1)..], out var typedPort) && typedPort is > 0 and <= 65535)
        {
            host = entry[..colon];
            port = typedPort;
        }
        return (host, port);
    }

    /// <summary>The endpoint when the entry is already an address, or null when it is a name that has to be looked
    /// up. Costs nothing and never blocks, so the caller can take it before going anywhere near another thread.</summary>
    private static IPEndPoint? ResolveWithoutLookup(string entry)
    {
        var (host, port) = SplitRelayAddress(entry.Trim());
        return IPAddress.TryParse(host, out var direct) ? new IPEndPoint(direct, port) : null;
    }

    /// <summary>A hostname or address, optionally with a port, to an endpoint. Runs off the UI thread.</summary>
    private static IPEndPoint? ResolveRelayAddress(string entry)
    {
        var (host, port) = SplitRelayAddress(entry);
        if (IPAddress.TryParse(host, out var direct)) return new IPEndPoint(direct, port);
        try
        {
            foreach (var address in Dns.GetHostAddresses(host))
            {
                if (address.AddressFamily == AddressFamily.InterNetwork) return new IPEndPoint(address, port);
            }
        }
        catch (SocketException) { /* not resolvable right now */ }
        catch (ArgumentException) { /* nonsense in the box */ }
        return null;
    }

    /// <summary>The notice Ed wrote, shown the first time someone connects, with a "don't show this again" tick.</summary>
    private void ShowRelayNotice()
    {
        if (AppConfig.Load().RelayNoticeSuppressed) return;
        var again = RelayNoticeDialog.ShowNotice(this);
        if (!again) return;
        // Loaded again after the message: saving the copy loaded before it wrote over anything written while it was open -
        // a named peer's new address, somebody remembered by Automatic accept (2026-09-25 sweep).
        var config = AppConfig.Load();
        config.RelayNoticeSuppressed = true;
        try { config.Save(); } catch { /* best-effort, like the app's other AppConfig writes */ }
        logFile.Event("relay: notice suppressed by the user");
    }

    /// <summary>
    /// An address check arrived from an address the user has in their peer list. Only a relay ever sends one, so ask
    /// whether to connect to it as a relay instead — a relay sitting in the peer list pretending to be a person is
    /// exactly the confusion this design removes.
    /// </summary>
    private void OfferRelayForPeerAddress(IPEndPoint remote)
    {
        if (!ShouldOfferRelay(remote)) return;
        // Nobody is there to answer in a headless or silent run, and an unanswerable question is a hang.
        if (NobodyToAnswer) { logFile.Event($"relay: {remote} answers as a relay — nobody to ask here"); return; }
        logFile.Event($"relay: {remote.Address} answers as a relay — offering to connect");
        var answer = ForegroundDialog.Show(owner => AppMessageBox.Show(owner,
            "You have entered the address of a server. Would you like to connect to this server?",
            AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question));
        if (answer != DialogResult.Yes) return;
        foreach (var (id, ep) in selectedPeerEndpoints.ToList())
        {
            if (ep.Address.Equals(remote.Address)) DeselectPeer(id);
        }
        ConnectToRelay(RelayEntryFor(remote), userAsked: true);
    }

    /// <summary>What to connect to for a server found at this address. Its PORT goes with it unless it is the default:
    /// it used to be dropped, so a server on another port (a second one on the same machine, say) was joined on 47830,
    /// where it was not.</summary>
    internal static string RelayEntryFor(IPEndPoint remote) =>
        remote.Port == RemPacket.DefaultPort ? remote.Address.ToString() : $"{remote.Address}:{remote.Port}";

    /// <summary>Whether to ask about that address at all: only for somebody actually in the peer list, only while not
    /// already on a relay, and only once a run for any one address. Asking is a modal question, so the deciding is kept
    /// separate from the asking.</summary>
    private bool ShouldOfferRelay(IPEndPoint remote)
    {
        if (relayGroup.ConnectedRelay is not null) return false;
        if (!selectedPeerEndpoints.Values.Any(ep => ep.Address.Equals(remote.Address))) return false;
        return relayOfferAsked.Add(remote.Address.ToString());
    }

    // ------- the people on the relay -------------------------------------------------------------------------------

    private void OnRelayPeerTicked(int index, bool ticked)
    {
        if (index < 0 || index >= relayPeersList.Items.Count || relayPeersList.Items[index] is not RelayPeerItem item) return;
        var refusalKey = item.IsPairPartner ? RelayPairRefusalKey() : $"relay:{item.Id:D}";
        if (ticked) { if (refusalKey is not null) acceptRefusedByUser.Remove(refusalKey); }
        else if (refusalKey is not null) acceptRefusedByUser.Add(refusalKey);
        if (item.IsPairPartner) relayPairTicked = ticked;
        else if (ticked) relayTicked.Add(item.Id);
        else { relayTicked.Remove(item.Id); relayTickedBack.Remove(item.Id); }
        ApplyRelayTicks();
        LogUiChange("relay peer", $"{(ticked ? "ticked" : "unticked")} {item.Label}");
        // Ticking somebody who has not ticked you back is silence with a reason. Say the reason, once, here.
        if (ticked && !item.IsPairPartner)
        {
            var member = relayGroup.Members.FirstOrDefault(m => m.Id == item.Id);
            if (member is not null && !member.TicksUs)
            {
                relayTickedBack[item.Id] = false;
                ScreenReader.Speak($"Waiting for {member.Name} to tick you.");
            }
        }
        MarkProfileDirty();
    }

    /// <summary>
    /// Push the ticks out. Someone ticked becomes a peer like any other, so they are allowed in and carry their own
    /// volume, pan, recording track and place in the DAW plugin. The relay is told as well, because it passes sound
    /// between two people only when each has ticked the other.
    /// </summary>
    private void ApplyRelayTicks()
    {
        foreach (var member in relayGroup.Members)
        {
            // Only somebody not already selected where they are. Every change on the server list - anybody joining,
            // leaving or renamed - re-selected everyone ticked, and each selection wrote a log line and threw away the
            // auto-tuner's readings; on a busy server, continuous auto-tune never kept any (2026-09-25 sweep).
            if (relayTicked.Contains(member.Id))
            {
                if (!selectedPeerEndpoints.TryGetValue(member.Id, out var at) || !at.Equals(member.Address)) SelectRelayMember(member);
            }
            else DeselectPeer(member.Id);
        }
        relayGroup.SetTicked(relayTicked, relayPairTicked);
        PushAllowedReceiveSenders();
        ApplyAudioRuntime();
    }

    private void SelectRelayMember(RelayGroupClient.Member member)
    {
        var announcement = new PeerAnnouncement(member.Id, RelayMemberName(member), member.Address.Port,
            CanSend: true, CanReceive: true, DateTime.UtcNow, member.Address.Address);
        SelectPeer(announcement, fromProfileRestore: true);
    }

    /// <summary>The key an untick of the phone-or-older-app row is remembered under, or null when we are not on a
    /// server and there is nothing to remember it against.</summary>
    private string? RelayPairRefusalKey() =>
        relayGroup.ConnectedRelay is { } relay ? $"relay-phone:{relay}" : null;

    private static string RelayMemberName(RelayGroupClient.Member member) =>
        string.IsNullOrWhiteSpace(member.Name) ? $"Someone not yet named ({member.Id.ToString("N")[..6]})" : member.Name;

    /// <summary>Rebuilds the relay list when it has actually changed, so a screen reader is not interrupted every
    /// second. Somebody who drops off stays in the list briefly, marked, rather than vanishing mid-read.</summary>
    private void SyncRelayPeersList()
    {
        var relay = relayGroup.ConnectedRelay;
        var items = new List<RelayPeerItem>();
        if (relay is not null)
        {
            var now = DateTime.UtcNow;
            var live = relayGroup.Members.Where(m => m.Relay.Equals(relay)).ToList();
            var liveIds = new HashSet<Guid>();
            foreach (var member in live)
            {
                liveIds.Add(member.Id);
                relayRecentlyGone.Remove(member.Id);
                relayKnownNames[member.Id] = RelayMemberName(member);
                // Somebody you are connected to lives in Connected peers, exactly as somebody on your network does.
                // Two lists sitting one above the other have to mean the same thing, or you get the same person in
                // both at once — Ed, 2026-09-21: "I see ed_dT even though I'm connected already to ed DT on server.
                // that's weird and should not happen". Untick them there and they come back here.
                if (selectedPeerEndpoints.ContainsKey(member.Id)) continue;
                items.Add(new RelayPeerItem(member.Id, RelayMemberName(member), IsPairPartner: false));
            }
            foreach (var (id, name) in relayKnownNames.ToList())
            {
                if (liveIds.Contains(id)) continue;
                if (!relayRecentlyGone.ContainsKey(id)) relayRecentlyGone[id] = (name, now);
                relayKnownNames.Remove(id);
            }
            foreach (var (id, gone) in relayRecentlyGone.ToList())
            {
                if (now - gone.GoneUtc > RelayGoneGrace) { relayRecentlyGone.Remove(id); continue; }
                // Somebody you are connected to is in Connected peers, marked offline - not here as well: the same person
                // in both lists for 15 seconds is what Ed objected to on 2026-09-21 (2026-09-25 sweep).
                if (selectedPeerEndpoints.ContainsKey(id)) continue;
                items.Add(new RelayPeerItem(id, $"{gone.Name} (gone)", IsPairPartner: false));
            }
            if (relayGroup.IsV1Paired(relay))
            {
                items.Add(new RelayPeerItem(Guid.Empty, "Someone on a phone or an older app", IsPairPartner: true));
            }
        }

        var signature = string.Join("|", items.Select(i => $"{i.Id}:{i.Label}"));
        var listChanged = signature != relayListSignature;
        if (listChanged)
        {
            relayListSignature = signature;
            var wasOn = CaptureListCursor(relayPeersList);
            suppressRelayTickEvents = true;
            try
            {
                relayPeersList.BeginUpdate();
                relayPeersList.Items.Clear();
                foreach (var item in items)
                {
                    var ticked = item.IsPairPartner ? relayPairTicked : relayTicked.Contains(item.Id);
                    relayPeersList.Items.Add(item, ticked);
                }
                relayPeersList.EndUpdate();
            }
            finally { suppressRelayTickEvents = false; }
            RestoreListCursorAfterRebuild(relayPeersList, wasOn);
            // Somebody ticked before they were here — a profile putting its ticks back, or somebody who left and came
            // back — has to be picked up now they are. Ticking is about a person, not about the moment you did it.
            ApplyRelayTicks();
        }

        // Somebody on the relay who has ticked us and whom we have not ticked: that is a connection waiting to happen,
        // and what happens next is the user's setting (ask, tick back, or nothing).
        foreach (var member in relayGroup.Members)
        {
            if (relay is null || !member.Relay.Equals(relay)) continue;
            if (member.TicksUs && !relayTicked.Contains(member.Id)) OnRelayMemberWantsToConnect(member);
        }
        // A phone or an older app cannot say it has ticked us — it knows nothing about ticking. Being GIVEN one of the
        // relay's ordinary pair slots beside us is the same statement, so it goes through the same decision.
        if (relay is not null && relayGroup.IsV1Paired(relay) && !relayPairTicked) OnRelayPairPartnerWantsToConnect(relay);

        // Somebody you have ticked who has not ticked you back: say so when it changes, and keep it in the status line.
        var waitingFor = new List<string>();
        foreach (var member in relayGroup.Members.Where(m => relay is not null && m.Relay.Equals(relay) && relayTicked.Contains(m.Id)))
        {
            if (!member.TicksUs) waitingFor.Add(member.Name);
            if (relayTickedBack.TryGetValue(member.Id, out var was) && was == member.TicksUs) continue;
            if (relayTickedBack.ContainsKey(member.Id))
            {
                ScreenReader.Speak(member.TicksUs
                    ? $"{member.Name} ticked you. You can hear each other now."
                    : $"{member.Name} unticked you.");
            }
            relayTickedBack[member.Id] = member.TicksUs;
        }
        foreach (var id in relayTickedBack.Keys.ToList())
        {
            if (!relayTicked.Contains(id)) relayTickedBack.Remove(id);
        }

        var waiting = waitingFor.Count == 0 ? "" : $", waiting for {string.Join(", ", waitingFor)} to tick you";
        var (kind, text) = DescribeRelayState(relay, waiting);
        NoteRelayAnswering(kind != "not answering");
        if (kind != relayStatusKind)
        {
            // A change of state, once: the log's own record of what the user was being told.
            if (kind is "no password" or "not answering") logFile.Event($"relay: status is now \"{text}\"");
            relayStatusKind = kind;
        }
        SetRelayStatus(text);
    }

    /// <summary>
    /// What the line beside the server button says. Two things it used to get wrong (review, 2026-09-23):
    ///
    /// <para>The count was the number of ROWS in the server list, and since people you are connected to left that list
    /// on 2026-09-21 it said 0 while you talked to the only other person there. It counts the people on the server on
    /// your password, whichever list they are in.</para>
    ///
    /// <para>With no password nothing is ever sent to the server (it groups people by their password), and a server
    /// that has gone away never answers; both used to read "Connecting..." for ever, the same words, so nobody could
    /// tell which. Each now says what it is.</para>
    /// </summary>
    private (string Kind, string Text) DescribeRelayState(IPEndPoint? relay, string waiting)
    {
        if (relay is null) return ("not connected", "Not connected to a server.");
        var server = relayEntryText.Trim().Length > 0 ? relayEntryText.Trim() : relay.ToString();
        if (relayGroup.IsInGroup(relay))
        {
            var here = relayGroup.Members.Count(m => m.Relay.Equals(relay));
            return ("in a group", $"Connected to the server at {server} — {here} here on your password{waiting}");
        }
        // In a group means the server has us on a password, so the next question only arises when we are not.
        if (string.IsNullOrEmpty(currentProfilePassword))
            return ("no password", $"On the server at {server}, but this profile has no password, so nobody there can be shown. Set one in the File menu.");
        var since = relayConnectedUtc is { } at ? DateTime.UtcNow - at : TimeSpan.Zero;
        return since < RelayAnswerGrace
            ? ("connecting", $"Connecting to the server at {server}...")
            : ("not answering", $"The server at {server} isn't answering.");
    }

    // ------- odds and ends -----------------------------------------------------------------------------------------

    private void SetRelayStatus(string text)
    {
        // Only the line beside Connect. The list's own label is the list's: it carries what the screen reader is told
        // on every arrow press, and anything written here as well would stamp over it once a second.
        if (relayStatusLine is not null && relayStatusLine.Text != text) relayStatusLine.Text = text;
    }

    private void UpdateRelayControls()
    {
        var connected = relayGroup.ConnectedRelay is not null;
        relayConnectButton.Text = connected ? "Disco&nnect from or change server (Alt+N)" : "Co&nnect to server (Alt+N)";
        relayConnectButton.AccessibleName = connected ? "Disconnect from or change server" : "Connect to server";
        SetConnectivityRowVisible(relayPeersLabel, relayPeersList, connected);
        SyncRelayPeersList();
    }

    private void RememberRelay(string entry)
    {
        var kept = new List<string> { entry.Trim() };
        kept.AddRange(settings.LoadRememberedRelays().Where(e => !string.Equals(e, entry.Trim(), StringComparison.OrdinalIgnoreCase)));
        settings.SaveRememberedRelays(kept);
    }

    private void ForgetRelay(string entry)
    {
        settings.SaveRememberedRelays(settings.LoadRememberedRelays()
            .Where(e => !string.Equals(e, entry, StringComparison.OrdinalIgnoreCase)));
        logFile.Event($"relay: forgot remembered relay \"{entry}\"");
    }

    /// <summary>
    /// The profile password changed while we were running. A relay only shows you the people on your own password, so a
    /// new password is a new set of people: the ones we had ticked belong to the group we have just left. Drop them,
    /// tell the relay, and let the new group's list arrive. Ed asked for this on 2026-09-20.
    /// </summary>
    private void RelayPasswordChanged(byte[]? fingerprint)
    {
        // No fingerprint yet means the key is still being worked out on another thread, not that the password went away.
        if (fingerprint is null) return;
        var now = Convert.ToHexString(fingerprint);
        if (now == relayPasswordSignature) return;
        var knewOne = relayPasswordSignature is not null;
        relayPasswordSignature = now;
        if (!knewOne || relayGroup.ConnectedRelay is null) return;
        foreach (var id in relayTicked.ToList()) DeselectPeer(id);
        relayTicked.Clear();
        relayTickedBack.Clear();
        relayPairTicked = false;
        relayKnownNames.Clear();
        relayRecentlyGone.Clear();
        relayListSignature = null;
        relayGroup.SetTicked(relayTicked, relayPairTicked);
        PushAllowedReceiveSenders();
        ApplyAudioRuntime();
        SyncRelayPeersList();
        logFile.Event("relay: the password changed, so the list on the relay starts again");
        ScreenReader.Speak("Password changed. The people on the server will be listed again.");
    }

    /// <summary>Called when a profile is loaded: its relay, its ticks, and a connect if it asked for one.</summary>
    private void ApplyRelayProfile(Profile profile)
    {
        DisconnectFromRelay(userAsked: false);
        relayTicked.Clear();
        foreach (var text in profile.RelayTickedIds)
        {
            if (Guid.TryParse(text, out var id)) relayTicked.Add(id);
        }
        // After the disconnect above, which clears it: the phone or older app paired beside us keeps its tick too.
        relayPairTicked = profile.RelayPairPartnerTicked;
        relayEntryText = profile.RelayServer ?? "";
        if (profile.RelayConnectOnStart && !string.IsNullOrWhiteSpace(profile.RelayServer))
        {
            ConnectToRelay(profile.RelayServer!, userAsked: false);
        }
        UpdateRelayControls();
    }

    // ------- test seams ----------------------------------------------------------------------------------------------

    internal RelayGroupClient RelayGroupForTest => relayGroup;
    internal CheckedListBox RelayPeersListForTest => relayPeersList;
    internal CheckedListBox DiscoveredPeersListForTest => discoveredPeersList;
    internal Button RelayConnectButtonForTest => relayConnectButton;
    internal string RelayStatusForTest => relayStatusLine?.Text ?? "";
    internal string RelayEntryTextForTest => relayEntryText;
    internal IReadOnlyList<string> RememberedRelaysForTest => settings.LoadRememberedRelays();
    internal string RelayListStatusForTest => relayPeersStatus.Text;
    /// <summary>The row wrapper the relay's peer list sits in, so a test can read its own visible flag: on a window
    /// that has never been shown, Control.Visible reports false for everything.</summary>
    internal Control? RelayPeersRowForTest => relayPeersList.Parent;
    /// <summary>Connect to an already-resolved relay, through the same routine the resolver calls back into.</summary>
    internal void ConnectToRelayForTest(string entry, IPEndPoint resolved) => FinishConnectingToRelay(entry, resolved);
    internal void DisconnectFromRelayForTest() => DisconnectFromRelay(userAsked: false);
    internal void SyncRelayPeersListForTest() => SyncRelayPeersList();
    internal void OfferRelayForTest(IPEndPoint remote) => OfferRelayForPeerAddress(remote);
    internal void ForgetRelayForTest(string entry) => ForgetRelay(entry);
    internal IPEndPoint[] SelectedSendEndpointsForTest() => SelectedSendEndpoints();
    internal void RelayPasswordChangedForTest(byte[]? fingerprint) => RelayPasswordChanged(fingerprint);
    internal IReadOnlyCollection<Guid> RelayTickedForTest => relayTicked;
    internal bool RelayPairTickedForTest => relayPairTicked;
    internal void DeselectPeerForTest(Guid instanceId) => DeselectPeer(instanceId);
    internal void DeselectPeerByUserForTest(Guid instanceId) => DeselectPeer(instanceId, byUser: true);
    internal void SelectPeerByUserForTest(PeerAnnouncement peer) => SelectPeer(peer, fromProfileRestore: false);
    internal CheckedListBox ConnectedPeersListForTest => connectedPeersList;
    internal IReadOnlyList<(IPAddress Address, string Name)> PeerListForPluginsForTest() => PeerListForPlugins();
    internal void BackdateRelayConnectForTest(TimeSpan by) { if (relayConnectedUtc is { } at) relayConnectedUtc = at - by; }
    internal void SetProfilePasswordForTest(string password) => currentProfilePassword = password;
    internal static IPEndPoint? ResolveWithoutLookupForTest(string entry) => ResolveWithoutLookup(entry);
    internal IReadOnlyCollection<string> AcceptRefusedForTest => acceptRefusedByUser;
    internal IReadOnlyCollection<Guid> SelectedPeerIdsForTest => selectedPeerEndpoints.Keys.ToList();
    internal void SyncAllPeerListsForTest() => SyncAllPeerLists();
    internal void UpdateConnectedListLiveStatusForTest() => UpdateConnectedListLiveStatus();
    internal void RelayPeerTickedForTest(int index, bool ticked) => OnRelayPeerTicked(index, ticked);
    internal bool ShouldOfferRelayForTest(IPEndPoint remote) => ShouldOfferRelay(remote);
    internal void SelectPeerForTest(PeerAnnouncement peer) => SelectPeer(peer, fromProfileRestore: true);
    internal void ApplyRelayProfileForTest(Profile profile) => ApplyRelayProfile(profile);

    /// <summary>Called when a profile is saved.</summary>
    private void GatherRelayProfile(Profile profile)
    {
        profile.RelayServer = relayEntryText.Trim();
        // Connected now - or still waiting for the profile's own server's name to look up, which is the profile's choice,
        // not a change of mind: saved as "not connected", the next start did not even try (2026-09-25 sweep).
        profile.RelayConnectOnStart = relayGroup.ConnectedRelay is not null
            || (pendingRelayName is not null && string.Equals(pendingRelayName, profile.RelayServer, StringComparison.OrdinalIgnoreCase));
        profile.RelayTickedIds = relayTicked.Select(id => id.ToString("D")).ToList();
        profile.RelayPairPartnerTicked = relayPairTicked;
    }
}
