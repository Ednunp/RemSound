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
    private readonly TextBox relayAddressBox = new() { Width = 200, AccessibleName = "Relay server address (Alt+Z)" };
    private readonly Button relayConnectButton = new() { Text = "Co&nnect (Alt+N)", AutoSize = true, AccessibleName = "Connect to relay server" };
    private readonly ListBox rememberedRelaysList = new() { Width = 200, Height = 74, AccessibleName = "Remembered relay servers (Alt+7)" };
    private readonly CheckedListBox relayPeersList = new() { CheckOnClick = true, Width = 430, Height = 90, AccessibleName = "Discovered peers on relay server (Alt+0)" };
    private readonly Label relayPeersStatus = new() { AutoSize = true, Text = "Nobody on the relay yet." };
    private MnemonicLabel? relayPeersLabel;
    private Label? relayStatusLine;

    /// <summary>Who we have ticked on the relay, by client id. Kept here rather than read off the list, because the
    /// list is rebuilt as people come and go.</summary>
    private readonly HashSet<Guid> relayTicked = [];
    private bool relayPairTicked;
    private bool suppressRelayTickEvents;
    private string relayListSignature = "";
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
    /// <summary>Addresses we have already asked about ("that looks like a relay"), so nobody is asked twice a run.</summary>
    private readonly HashSet<string> relayOfferAsked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One row in the relay list: a person, or the phone / older app we are paired with through the relay.</summary>
    private sealed record RelayPeerItem(Guid Id, string Label, bool IsPairPartner)
    {
        public override string ToString() => Label;
    }

    // ------- building the rows -------------------------------------------------------------------------------------

    /// <summary>Adds the relay rows to the Connectivity tab from <paramref name="firstRow"/>, and returns the next free
    /// row. Three rows: the address and Connect, the remembered relays, and the people on the relay.</summary>
    private int BuildRelayRows(TableLayoutPanel panel, int firstRow)
    {
        var addressLabel = new MnemonicLabel { Text = "Relay server (Alt+&Z)", AutoSize = true, Anchor = AnchorStyles.Left, MnemonicTarget = relayAddressBox };
        addressLabel.Click += (_, _) => relayAddressBox.Focus();
        var addressRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
        addressRow.Controls.Add(relayAddressBox);
        addressRow.Controls.Add(relayConnectButton);
        relayStatusLine = new Label { AutoSize = true, Text = "Not connected to a relay server.", AccessibleName = "Relay server status", Anchor = AnchorStyles.Left };
        addressRow.Controls.Add(relayStatusLine);
        panel.Controls.Add(addressLabel, 0, firstRow);
        panel.Controls.Add(addressRow, 1, firstRow);
        relayConnectButton.Click += (_, _) => OnRelayConnectButton();
        relayAddressBox.KeyDown += (_, args) =>
        {
            if (args.KeyCode != Keys.Enter) return;
            args.SuppressKeyPress = true;
            OnRelayConnectButton();
        };

        var rememberedLabel = new MnemonicLabel { Text = "Remembered relays (Alt+&7)", AutoSize = true, Anchor = AnchorStyles.Left, MnemonicTarget = rememberedRelaysList };
        rememberedLabel.Click += (_, _) => rememberedRelaysList.Focus();
        panel.Controls.Add(rememberedLabel, 0, firstRow + 1);
        panel.Controls.Add(rememberedRelaysList, 1, firstRow + 1);
        rememberedRelaysList.KeyDown += (_, args) =>
        {
            if (args.KeyCode == Keys.Enter && rememberedRelaysList.SelectedItem is string chosen)
            {
                args.SuppressKeyPress = true;
                relayAddressBox.Text = chosen;
                ConnectToRelay(chosen, userAsked: true);
            }
            else if (args.KeyCode == Keys.Delete && rememberedRelaysList.SelectedItem is string doomed)
            {
                args.SuppressKeyPress = true;
                ForgetRelay(doomed);
            }
        };
        rememberedRelaysList.DoubleClick += (_, _) =>
        {
            if (rememberedRelaysList.SelectedItem is string chosen)
            {
                relayAddressBox.Text = chosen;
                ConnectToRelay(chosen, userAsked: true);
            }
        };

        relayPeersLabel = FormLayoutRows.AddCheckedListRow(panel, firstRow + 2, "Discovered peers on relay server (Alt+&0)", relayPeersList, relayPeersStatus, FocusListControl);
        WireCheckedListAccessibility(relayPeersList, relayPeersStatus, "relay peer");
        relayPeersList.ItemCheck += (_, args) =>
        {
            if (suppressRelayTickEvents) return;
            var index = args.Index;
            var ticked = args.NewValue == CheckState.Checked;
            BeginInvoke(() => OnRelayPeerTicked(index, ticked));
        };

        RefreshRememberedRelaysList();
        UpdateRelayControls();
        return firstRow + 3;
    }

    // ------- connecting --------------------------------------------------------------------------------------------

    private void OnRelayConnectButton()
    {
        if (relayGroup.ConnectedRelay is not null)
        {
            DisconnectFromRelay(userAsked: true);
            return;
        }
        ConnectToRelay(relayAddressBox.Text, userAsked: true);
    }

    /// <summary>Connect to a relay. The address is resolved OFF the UI thread: a hostname that cannot resolve right now
    /// blocks for the DNS timeout, which on the UI thread reads as the whole app locking up (issue #10).</summary>
    private void ConnectToRelay(string typed, bool userAsked)
    {
        var entry = (typed ?? "").Trim();
        if (entry.Length == 0)
        {
            SetRelayStatus("Type a relay server address first.");
            return;
        }
        if (userAsked) ShowRelayNotice();
        // Going somewhere else is leaving here: the people you had ticked are on the relay you are leaving.
        if (relayGroup.ConnectedRelay is not null) DisconnectFromRelay(userAsked);
        relayAddressBox.Text = entry;
        relayConnectButton.Enabled = false;
        SetRelayStatus($"Connecting to {entry}...");
        Task.Run(() => ResolveRelayAddress(entry)).ContinueWith(task =>
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
            logFile.Event($"relay: could not resolve \"{entry}\"");
            return;
        }
        // Note the password we are joining under, so a later change of it is recognised as a move to different people.
        relayPasswordSignature = currentAudioFingerprint is null ? "" : Convert.ToHexString(currentAudioFingerprint);
        relayGroup.Connect(resolved);
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
        relayPairTicked = false;
        relayTickedBack.Clear();
        relayKnownNames.Clear();
        relayRecentlyGone.Clear();
        relayListSignature = "";
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

    /// <summary>A hostname or address, optionally with a port, to an endpoint. Runs off the UI thread.</summary>
    private static IPEndPoint? ResolveRelayAddress(string entry)
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
        var config = AppConfig.Load();
        if (config.RelayNoticeSuppressed) return;
        var again = RelayNoticeDialog.ShowNotice(this);
        if (!again) return;
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
        logFile.Event($"relay: {remote.Address} answers as a relay — offering to connect");
        var answer = ForegroundDialog.Show(owner => MessageBox.Show(owner,
            "You have entered the address of a relay. Would you like to connect to this relay?",
            AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question));
        if (answer != DialogResult.Yes) return;
        foreach (var (id, ep) in selectedPeerEndpoints.ToList())
        {
            if (ep.Address.Equals(remote.Address)) DeselectPeer(id);
        }
        relayAddressBox.Text = remote.Address.ToString();
        ConnectToRelay(remote.Address.ToString(), userAsked: true);
    }

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
            if (relayTicked.Contains(member.Id)) SelectRelayMember(member);
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
            // Somebody ticked before they were here — a profile putting its ticks back, or somebody who left and came
            // back — has to be picked up now they are. Ticking is about a person, not about the moment you did it.
            ApplyRelayTicks();
        }

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
        SetRelayStatus(relay is null
            ? "Not connected to a relay server."
            : relayGroup.IsInGroup(relay)
                ? $"Connected to {relayAddressBox.Text.Trim()} — {items.Count(i => !i.IsPairPartner)} here on your password{waiting}"
                : $"Connecting to {relayAddressBox.Text.Trim()}...");
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
        relayConnectButton.Text = connected ? "Disco&nnect (Alt+N)" : "Co&nnect (Alt+N)";
        relayConnectButton.AccessibleName = connected ? "Disconnect from relay server" : "Connect to relay server";
        SetConnectivityRowVisible(relayPeersLabel, relayPeersList, connected);
        SyncRelayPeersList();
    }

    private void RefreshRememberedRelaysList()
    {
        var remembered = settings.LoadRememberedRelays();
        var chosen = rememberedRelaysList.SelectedItem as string;
        rememberedRelaysList.BeginUpdate();
        rememberedRelaysList.Items.Clear();
        foreach (var entry in remembered) rememberedRelaysList.Items.Add(entry);
        rememberedRelaysList.EndUpdate();
        if (chosen is not null)
        {
            var index = rememberedRelaysList.Items.IndexOf(chosen);
            if (index >= 0) rememberedRelaysList.SelectedIndex = index;
        }
    }

    private void RememberRelay(string entry)
    {
        var kept = new List<string> { entry.Trim() };
        kept.AddRange(settings.LoadRememberedRelays().Where(e => !string.Equals(e, entry.Trim(), StringComparison.OrdinalIgnoreCase)));
        settings.SaveRememberedRelays(kept);
        RefreshRememberedRelaysList();
    }

    private void ForgetRelay(string entry)
    {
        settings.SaveRememberedRelays(settings.LoadRememberedRelays()
            .Where(e => !string.Equals(e, entry, StringComparison.OrdinalIgnoreCase)));
        RefreshRememberedRelaysList();
        logFile.Event($"relay: forgot remembered relay \"{entry}\"");
        ScreenReader.Speak($"{entry} removed");
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
        relayListSignature = "";
        relayGroup.SetTicked(relayTicked, relayPairTicked);
        PushAllowedReceiveSenders();
        ApplyAudioRuntime();
        SyncRelayPeersList();
        logFile.Event("relay: the password changed, so the list on the relay starts again");
        ScreenReader.Speak("Password changed. The people on the relay will be listed again.");
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
        relayAddressBox.Text = profile.RelayServer ?? "";
        if (profile.RelayConnectOnStart && !string.IsNullOrWhiteSpace(profile.RelayServer))
        {
            ConnectToRelay(profile.RelayServer!, userAsked: false);
        }
        UpdateRelayControls();
    }

    // ------- test seams ----------------------------------------------------------------------------------------------

    internal RelayGroupClient RelayGroupForTest => relayGroup;
    internal CheckedListBox RelayPeersListForTest => relayPeersList;
    internal ListBox RememberedRelaysListForTest => rememberedRelaysList;
    internal TextBox RelayAddressBoxForTest => relayAddressBox;
    internal Button RelayConnectButtonForTest => relayConnectButton;
    internal string RelayStatusForTest => relayStatusLine?.Text ?? "";
    internal string RelayListStatusForTest => relayPeersStatus.Text;
    /// <summary>The row wrapper the relay's peer list sits in, so a test can read its own visible flag: on a window
    /// that has never been shown, Control.Visible reports false for everything.</summary>
    internal Control? RelayPeersRowForTest => relayPeersList.Parent;
    /// <summary>Connect to an already-resolved relay, through the same routine the resolver calls back into.</summary>
    internal void ConnectToRelayForTest(string entry, IPEndPoint resolved) => FinishConnectingToRelay(entry, resolved);
    internal void DisconnectFromRelayForTest() => DisconnectFromRelay(userAsked: false);
    internal void SyncRelayPeersListForTest() => SyncRelayPeersList();
    internal void ForgetRelayForTest(string entry) => ForgetRelay(entry);
    internal IPEndPoint[] SelectedSendEndpointsForTest() => SelectedSendEndpoints();
    internal void RelayPasswordChangedForTest(byte[]? fingerprint) => RelayPasswordChanged(fingerprint);
    internal IReadOnlyCollection<Guid> RelayTickedForTest => relayTicked;
    internal void RelayPeerTickedForTest(int index, bool ticked) => OnRelayPeerTicked(index, ticked);
    internal bool ShouldOfferRelayForTest(IPEndPoint remote) => ShouldOfferRelay(remote);
    internal void SelectPeerForTest(PeerAnnouncement peer) => SelectPeer(peer, fromProfileRestore: true);
    internal void ApplyRelayProfileForTest(Profile profile) => ApplyRelayProfile(profile);

    /// <summary>Called when a profile is saved.</summary>
    private void GatherRelayProfile(Profile profile)
    {
        profile.RelayServer = relayAddressBox.Text.Trim();
        profile.RelayConnectOnStart = relayGroup.ConnectedRelay is not null;
        profile.RelayTickedIds = relayTicked.Select(id => id.ToString("D")).ToList();
    }
}
