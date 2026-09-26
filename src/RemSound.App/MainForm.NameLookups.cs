using System.Net;
using System.Net.NetworkInformation;

namespace RemSound.App;

/// <summary>
/// Names that could not be looked up yet, and a server that may have moved (2026-09-25 sweep; Ed: "yes all 10").
///
/// <para>A profile that starts with Windows names its server (remote.ednun.com) and its peers by name, and at sign-in the
/// name often cannot be looked up yet - the Wi-Fi is not up, the VPN's names are not ready. The app tried once, said
/// "Couldn't find", and never tried again: on nobody's server for the whole session, from the tray with no way to know.
/// Worse, the next save wrote "don't connect at start" for the server and left the peer out of the profile, so the next
/// start did not even try. The lock-screen service got its retry on 2026-09-25; the app gets the same here: every 30
/// seconds and whenever the network changes, and saving keeps what the profile named.</para>
///
/// <para>And a server named by its name that stops answering may simply have moved - a home connection with no fixed
/// address (Ed's own server, after the move to EE). After 30 seconds of silence, or a network change, the name is looked
/// up again, and a new address is followed.</para>
/// </summary>
public sealed partial class MainForm
{
    /// <summary>Peers the profile names that could not be looked up yet.</summary>
    private readonly HashSet<string> pendingPeerNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The server the profile connects to at start, when its name could not be looked up yet.</summary>
    private string? pendingRelayName;

    private DateTime nextNameRetryUtc = DateTime.MinValue;
    private DateTime? relayNotAnsweringSinceUtc;
    private DateTime nextRelayRecheckUtc = DateTime.MinValue;
    private bool relayRecheckRunning;
    private bool networkChangeHooked;

    internal static TimeSpan NameRetryInterval = TimeSpan.FromSeconds(30);
    internal static TimeSpan RelayMovedAfter = TimeSpan.FromSeconds(30);

    /// <summary>Gate seams: stand-ins for the name lookups.</summary>
    internal static Func<string, IPAddress?>? PeerResolveForTest;
    internal static Func<string, IPEndPoint?>? RelayResolveForTest;

    private void HookNetworkChangeForLookups()
    {
        if (networkChangeHooked) return;
        networkChangeHooked = true;
        NetworkChange.NetworkAddressChanged += OnNetworkChangedForLookups;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChangedForLookups;
    }

    private void UnhookNetworkChangeForLookups()
    {
        if (!networkChangeHooked) return;
        networkChangeHooked = false;
        NetworkChange.NetworkAddressChanged -= OnNetworkChangedForLookups;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChangedForLookups;
    }

    private void OnNetworkAvailabilityChangedForLookups(object? sender, NetworkAvailabilityEventArgs e) => OnNetworkChangedForLookups(sender, e);

    private void OnNetworkChangedForLookups(object? sender, EventArgs e)
    {
        try { BeginInvoke(() => RetryNameLookups(networkChanged: true)); }
        catch (ObjectDisposedException) { /* closing */ }
        catch (InvalidOperationException) { /* no handle yet, or closing */ }
    }

    /// <summary>On the once-a-second tick: anything waiting to be looked up, once every <see cref="NameRetryInterval"/>;
    /// a server by name that has stopped answering, after <see cref="RelayMovedAfter"/>.</summary>
    private void RetryNameLookupsIfDue() => RetryNameLookups(networkChanged: false);

    private void RetryNameLookups(bool networkChanged)
    {
        if (IsDisposed) return;
        var now = DateTime.UtcNow;
        if ((pendingPeerNames.Count > 0 || pendingRelayName is not null) && (networkChanged || now >= nextNameRetryUtc))
        {
            nextNameRetryUtc = now + NameRetryInterval;
            foreach (var name in pendingPeerNames.ToList()) _ = ReconnectOneSavedPeerAsync(name);
            if (pendingRelayName is { } relayName && relayGroup.ConnectedRelay is null)
                ConnectToRelay(relayName, userAsked: false);
        }
        RecheckRelayAddressIfDue(now, networkChanged);
    }

    /// <summary>The server we are on, by name, has been silent a while (or the network changed): look the name up again,
    /// and follow it if it now says somewhere else.</summary>
    private void RecheckRelayAddressIfDue(DateTime now, bool networkChanged)
    {
        if (relayRecheckRunning || relayGroup.ConnectedRelay is not { } current) return;
        var entry = relayEntryText;
        if (string.IsNullOrWhiteSpace(entry) || ResolveWithoutLookup(entry) is not null) return;   // an address cannot move
        var silentLongEnough = relayNotAnsweringSinceUtc is { } since && now - since >= RelayMovedAfter;
        if (!networkChanged && !(silentLongEnough && now >= nextRelayRecheckUtc)) return;
        nextRelayRecheckUtc = now + RelayMovedAfter;
        relayRecheckRunning = true;
        Task.Run(() => LookUpRelay(entry)).ContinueWith(task =>
        {
            var found = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
            try
            {
                BeginInvoke(() =>
                {
                    relayRecheckRunning = false;
                    if (found is null || relayGroup.ConnectedRelay is not { } still || !still.Equals(current)) return;
                    if (found.Equals(current)) return;
                    logFile.Event($"relay: \"{entry}\" now looks up to {found}, not {current} - it has moved; following it");
                    DisconnectFromRelay(userAsked: false);
                    FinishConnectingToRelay(entry, found, userAsked: false);
                });
            }
            catch (ObjectDisposedException) { relayRecheckRunning = false; }
            catch (InvalidOperationException) { relayRecheckRunning = false; }
        }, TaskScheduler.Default);
    }

    /// <summary>The state line's own record of whether the server is answering, for <see cref="RecheckRelayAddressIfDue"/>.</summary>
    private void NoteRelayAnswering(bool answering)
    {
        if (answering) relayNotAnsweringSinceUtc = null;
        else relayNotAnsweringSinceUtc ??= DateTime.UtcNow;
    }

    private static IPEndPoint? LookUpRelay(string entry) => RelayResolveForTest is { } seam ? seam(entry) : ResolveRelayAddress(entry);

    private static Task<IPAddress?> LookUpPeer(string entry) =>
        PeerResolveForTest is { } seam ? Task.FromResult(seam(entry)) : ResolvePeerAddressAsync(entry);

    // ---- gate seams -------------------------------------------------------------------------------------------------
    internal IReadOnlyCollection<string> PendingPeerNamesForTest => pendingPeerNames;
    internal string? PendingRelayNameForTest => pendingRelayName;
    internal void RetryNameLookupsForTest(bool networkChanged) => RetryNameLookups(networkChanged);
    internal void ReconnectSavedPeersForTest(IReadOnlyList<string> entries) => ReconnectSavedPeers(entries);
    internal void RelaySilentSinceForTest(DateTime since) => relayNotAnsweringSinceUtc = since;
    internal DateTime? RelayNotAnsweringSinceForTest => relayNotAnsweringSinceUtc;
    internal List<string> GatherSelectedPeerEntriesForTest() => GatherSelectedPeerEntries();
    internal void GatherRelayProfileForTest(RemSound.Core.Profile profile) => GatherRelayProfile(profile);
}
