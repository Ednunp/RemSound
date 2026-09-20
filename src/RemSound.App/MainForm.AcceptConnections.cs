using System.Net;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// What happens when somebody else ticks you. Ed's request, 2026-09-20.
///
/// <para>RemSound has always needed both sides to tick each other, which is safe but means somebody who has ticked you
/// waits in silence until you happen to look. There are now three ways to handle it, in Preferences, General, kept
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
        if (selectedPeerEndpoints.Values.Any(ep => ep.Address.Equals(remote.Address))) return;
        // A relay's own address is not a person: the people behind it arrive in the relay list with names of their own.
        if (relayGroup.ConnectedRelay is { } relay && relay.Address.Equals(remote.Address)) return;
        if (!acceptAsked.Add($"peer:{remote.Address}")) return;

        var peer = KnownPeerAt(remote.Address) ?? CreateManualPeer(remote.Address.ToString(), remote.Address);
        var name = ResolvePeerDisplayName(peer);
        logFile.Event($"accept connections: {name} at {remote.Address} is sending to us and is not ticked (mode={mode})");
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
        manualPeers[peer.InstanceId] = peer;
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
        relayListSignature = "";        // the row's tick has changed, so the next refresh rebuilds the list
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
        relayListSignature = "";
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
        // Nobody is there to answer in a headless run, and an unanswerable question is a hang.
        if (headless) { logFile.Event($"accept connections: nobody to ask in a headless run — {question}"); return; }
        acceptPromptOpen = true;
        try
        {
            var answer = ForegroundDialog.Show(owner => MessageBox.Show(owner,
                $"{question}\n\nDo you want to allow this?", AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question));
            if (answer == DialogResult.Yes) onYes();
            else logFile.Event($"accept connections: declined — {question}");
        }
        finally { acceptPromptOpen = false; }
    }

    // ------- test seams ----------------------------------------------------------------------------------------------

    internal void SomeoneWantsToConnectForTest(IPEndPoint remote, bool samePassword) => OnSomeoneWantsToConnect(remote, samePassword);
    internal void RelayMemberWantsToConnectForTest(RelayGroupClient.Member member) => OnRelayMemberWantsToConnect(member);
    internal IReadOnlyCollection<string> AcceptAskedForTest => acceptAsked;
    internal int AcceptAskCountForTest => acceptAskCount;
}
