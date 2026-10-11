using System.Net;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// A SERVER IS NEVER A PERSON (Ed, 2026-10-01: "a server should never be in a profile peer list. you can connect to them if
/// the profile is connected but we shouldn't mix them up with peers"). Found that evening in his logs: his profile had his
/// own server's internet address saved as a peer, and with the app also on that server at its home address, the server
/// saw his PC arrive by two routes, paired the two and sent him his own sound. Once he left the server, Accept connections
/// took that sound for somebody calling and ticked the server's home address as a peer - which set off "You have entered
/// the address of a server" while he was typing another server's name.
///
/// <para>So every address that has turned out to be a server - one he connected to as a server, or one that sent the
/// address check only a server sends - is remembered on this computer, and from then on it is never ticked, accepted,
/// remembered or reconnected as a peer; where it already is one, it is taken out. Connecting to it is Connect to server's
/// job, and the profile keeps that separately (its own server). The "that's a server" question is asked only about an
/// address the person typed themselves.</para>
/// </summary>
public sealed partial class MainForm
{
    /// <summary>How many server addresses this computer remembers, newest kept.</summary>
    internal const int MaxKnownServers = 64;

    private HashSet<IPAddress>? knownServers;
    private readonly HashSet<string> serverRefusalSaid = new(StringComparer.Ordinal);
    /// <summary>Addresses the person typed as a peer this run: only about those is the "that's a server" question asked.</summary>
    private readonly HashSet<IPAddress> typedPeerAddresses = [];

    private static IPAddress Plain(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>Every server address this computer knows: those saved, and the remembered servers written as addresses.</summary>
    private HashSet<IPAddress> KnownServers
    {
        get
        {
            if (knownServers is not null) return knownServers;
            var set = new HashSet<IPAddress>();
            foreach (var text in AppConfig.Load().KnownServerAddresses)
                if (IPAddress.TryParse(text, out var a) && !IPAddress.IsLoopback(Plain(a))) set.Add(Plain(a));
            foreach (var entry in settings.LoadRememberedRelays())
                if (IPAddress.TryParse(RelayHostOf(entry), out var a) && !IPAddress.IsLoopback(Plain(a))) set.Add(Plain(a));
            return knownServers = set;
        }
    }

    /// <summary>The host part of a remembered server entry ("host" or "host:port").</summary>
    private static string RelayHostOf(string entry)
    {
        var text = entry.Trim();
        var colon = text.LastIndexOf(':');
        return colon > 0 && text.IndexOf(':') == colon ? text[..colon] : text;
    }

    /// <summary>Whether this address is a server: one this computer knows, or the one it is on now.</summary>
    internal bool IsKnownServer(IPAddress address)
    {
        var a = Plain(address);
        if (RelayGroupClient.IsMemberAddress(a)) return false;   // a person on a server, at the address the server gives them
        // This computer's own loopback is never taken for a server: a relay run here and a second RemSound here share that
        // address, told apart only by their ports. Taken for one, a test relay on 127.0.0.1 turned every later step's
        // loopback peers away for the rest of the full gate (2026-10-02: eight steps that pass on their own).
        if (IPAddress.IsLoopback(a)) return false;
        return KnownServers.Contains(a) || relayGroup.ConnectedRelay is { } relay && Plain(relay.Address).Equals(a);
    }

    /// <summary>This address is a server: remember it on this computer, and take it out of the peers wherever it is one.</summary>
    private void NoteServerAddress(IPAddress address, string why)
    {
        var a = Plain(address);
        if (RelayGroupClient.IsMemberAddress(a) || IPAddress.IsLoopback(a)) return;   // loopback: see IsKnownServer
        if (KnownServers.Add(a))
        {
            try
            {
                var cfg = AppConfig.Load();
                cfg.KnownServerAddresses.RemoveAll(t => IPAddress.TryParse(t, out var x) && Plain(x).Equals(a));
                cfg.KnownServerAddresses.Add(a.ToString());
                if (cfg.KnownServerAddresses.Count > MaxKnownServers)
                    cfg.KnownServerAddresses.RemoveRange(0, cfg.KnownServerAddresses.Count - MaxKnownServers);
                cfg.Save();
            }
            catch (Exception ex) { logFile.Event($"servers: could not save {a} as a server: {ex.GetType().Name}: {ex.Message}"); }
            logFile.Event($"servers: {a} is a server ({why}) - it is connected to, never ticked as a peer");
        }
        TakeServerOutOfPeers(a);
    }

    /// <summary>Untick anybody at a server's address and forget it from the remembered peers. Said in the log when there was
    /// anything to take out.</summary>
    private void TakeServerOutOfPeers(IPAddress server)
    {
        var taken = new List<string>();
        foreach (var (id, ep) in selectedPeerEndpoints.ToList())
        {
            if (!Plain(ep.Address).Equals(server)) continue;
            taken.Add(selectedPeerLabels.GetValueOrDefault(id, ep.Address.ToString()));
            DeselectPeer(id);
            manualPeers.Remove(id);
        }
        foreach (var (id, peer) in manualPeers.ToList())
            if (Plain(peer.Address).Equals(server)) manualPeers.Remove(id);
        var remembered = settings.LoadRememberedPeers().ToList();
        bool IsServerEntry(string entry) =>
            IPAddress.TryParse(entry.Trim(), out var literal) && Plain(literal).Equals(server)
            || rememberedPeerInstanceIds.TryGetValue(entry, out var pid) && knownPeers.TryGetValue(pid, out var known) && Plain(known.Address).Equals(server);
        var forgotten = remembered.Where(IsServerEntry).ToList();
        if (forgotten.Count > 0)
        {
            settings.SaveRememberedPeers(remembered.Except(forgotten, StringComparer.OrdinalIgnoreCase));
            foreach (var entry in forgotten) rememberedPeerInstanceIds.Remove(entry);
            taken.AddRange(forgotten.Select(e => $"remembered \"{e}\""));
        }
        if (taken.Count == 0) return;
        logFile.Event($"servers: {server} was among the peers ({string.Join(", ", taken.Distinct())}) - taken out; a server is "
                    + "connected to with Connect to server, never ticked as a person");
        MarkProfileDirty();
        RefreshKnownPeers();
        SyncAllPeerLists();
        ApplyAudioRuntime();
        PushDiscoveryUnicastHints();
    }

    /// <summary>Said once an address: something wanted a server as a peer, and was refused.</summary>
    private void SayServerRefused(IPAddress address, string what)
    {
        if (serverRefusalSaid.Add($"{what}|{Plain(address)}"))
            logFile.Event($"servers: {Plain(address)} is a server, not a person - {what}");
    }

    /// <summary>A server's address check arrived from somebody we are talking to (UI thread): that address is a server. Asked
    /// about only if the person typed it as a peer themselves this run; otherwise it is simply taken out.</summary>
    private void ServerAnsweredAt(IPEndPoint remote)
    {
        // Only from somewhere we are talking to or have ticked: a stray or forged check from anywhere else says nothing.
        if (!AddrCheckTalkingTo(remote.Address) && !selectedPeerEndpoints.Values.Any(ep => Plain(ep.Address).Equals(Plain(remote.Address)))) return;
        var typed = typedPeerAddresses.Contains(Plain(remote.Address))
                    && selectedPeerEndpoints.Values.Any(ep => Plain(ep.Address).Equals(Plain(remote.Address)));
        NoteServerAddress(remote.Address, "it sent the address check only a server sends");
        if (typed) OfferRelayForPeerAddress(remote);
    }

    /// <summary>THE SERVER SOUNDS (Ed's, 2026-10-01): one as you connect to a server, the other as you disconnect from it - by
    /// the Server window, a profile reconnecting at start, or changing server. Not when you join a group or people arrive
    /// on it: the peer sounds are for people (Ed: "you hear the peer sounds when you join a group").</summary>
    private void PlayServerSound(bool connected)
    {
        // As the profile has them (2026-10-08). The log says what really happened: a silent run or "Mute all audio cues"
        // plays nothing, and said "played" all the same.
        var on = settings.LoadCueEnabled(connected ? CueId.ServerConnect : CueId.ServerDisconnect);
        var sound = connected ? serverConnectSound : serverDisconnectSound;
        logFile.Event($"server sound: {(connected ? "connected to the server" : "disconnected from the server")} ({(on ? CueOutcome(true, sound) : "switched off")})");
        if (!on) return;
        sound?.Play();
    }
    internal string? ServerConnectSoundFileForTest => serverConnectSound?.FilePathForTest;
    internal string? ServerDisconnectSoundFileForTest => serverDisconnectSound?.FilePathForTest;
    internal bool IsKnownServerForTest(IPAddress address) => IsKnownServer(address);
    internal void NoteServerAddressForTest(IPAddress address) => NoteServerAddress(address, "the self-test said so");
    internal void ServerAnsweredAtForTest(IPEndPoint remote) => ServerAnsweredAt(remote);
    internal Task AddManualPeerForTest(string text) => AddManualPeerAsync(text);
    internal void ForgetKnownServersForTest() => knownServers = null;
    internal void MarkTypedPeerForTest(IPAddress address) => typedPeerAddresses.Add(Plain(address));
}
