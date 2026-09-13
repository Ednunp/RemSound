using System.Drawing;
using System.Windows.Forms;
using AudioPlugSharp;
using RemSound.App;
using RemSound.Core;

namespace RemSound.Plugin;

/// <summary>
/// RemSound as a VST3 plugin (v6.0).
///
/// <para>The same engine as the app — same protocol, same encryption, same jitter buffer, same relay
/// — with the sound card swapped for the DAW. Audio on the track goes to your peers; audio from a
/// peer arrives on the track, where it can be recorded, shaped and mixed like anything else.</para>
///
/// <para><b>As many people on a track as you want.</b> It began as one person per track (Ed,
/// 2026-08-15) and that is still the right default for recording, where a separate track per person
/// is what you want at the mix. But listening is not recording: "say that you want to listen to 3
/// people talking while they are listening to your production session" (Anthony Reyers, 2026-09-01),
/// and making that mean three plugin instances on three tracks is the tool getting in the way. So a
/// receiving instance carries a SET of peers, and the app mixes them into the one block it already
/// sends back — the plugin's audio path is unchanged, one reply per block and one resampler however
/// many people are on the track. The trade is that there is no per-peer trim inside the plugin;
/// RemSound's own per-peer volume, pan and EQ already shape what arrives here, so that control exists
/// where the user already knows it.</para>
///
/// <para>Splitting a single machine's devices into separate streams is a different question and is
/// still deliberately not done: each stream gets its own jitter buffer and its own drift correction,
/// so a guitar and a vocal from the same performance would slowly drift apart at the far end — worse
/// than latency, and it needs shared-clock work in the most delicate code we have.</para>
///
/// <para><b>Configuration comes from the app</b>, not from here. Password, peers and audio settings
/// are whatever you already set up in RemSound ("read the profile from the standalone app... it's
/// far easier"), so the plugin only has to answer what THIS instance is doing. That also means far
/// less UI to make accessible.</para>
///
/// <para><b>Known limit, stated plainly:</b> AudioPlugSharp's host interface exposes no way to report
/// plugin latency, so the DAW cannot delay-compensate for the network jitter buffer. SonoBus has the
/// same gap. Tracks recorded through the plugin will sit late by roughly the reported round trip and
/// need nudging. This is a framework limitation, not something we can fix from here — it is written
/// down so nobody rediscovers it as a bug.</para>
/// </summary>
public class RemSoundPlugin : AudioPluginBase
{
    /// <summary>What the wire speaks. The host may run at 44.1 or 96 kHz, so the boundary resamples —
    /// SonoBus has an open bug (their issue #1) where a mismatch audibly transposes the audio, and
    /// that is precisely the trap this constant exists to make visible.</summary>
    public const int WireSampleRate = PluginBridgeProtocol.WireSampleRate;

    private AudioIOPort<double>? monitorIn;
    private AudioIOPort<double>? peerOut;

    private PluginBridgeClient? bridge;
    private HostCaptureBackend? capture;
    private PeerRenderBridge? render;
    private int preparedBlockSize;
    private double preparedSampleRate;

    /// <summary>This instance's own id, so several plugins in one DAW are told apart in the log.</summary>
    private readonly Guid instanceId = Guid.NewGuid();

    /// <summary>The plugin's log, gated by "Enable logs" in RemSound's Preferences exactly like the
    /// app's. It exists from construction so that even a failure during Initialize gets written down —
    /// a plugin that dies before it starts is precisely the report that arrives with no evidence.</summary>
    private PluginLog? log;

    /// <summary>
    /// What this instance is doing. The two directions are INDEPENDENT: an instance can send this
    /// track, receive one peer onto it, do both at once, or do neither.
    ///
    /// <para>It used to be one job or the other, and the stated reason was that one job per instance
    /// made feedback impossible — a receiving instance never sends, so it cannot feed itself. That
    /// guarantee is kept, by a better means: <see cref="Process"/> hands the app the track's INPUT and
    /// never its output, so what leaves cannot contain what arrived, whichever switches are on. It is
    /// also what SonoBus does; its plugin has no mode at all, just independent send and receive mutes,
    /// and it transmits its input buffer for exactly this reason.</para>
    ///
    /// <para><b>Both default to off.</b> A plugin dropped on a track does nothing until it is told to,
    /// which is the safe direction: the alternative is a freshly-inserted instance quietly broadcasting
    /// a track to everybody the moment it loads.</para>
    /// </summary>
    internal bool SendEnabled { get; private set; }
    internal bool ReceiveEnabled { get; private set; }

    /// <summary>Level trim on each direction, 1 = unity. The DAW's own fader cannot help here: in
    /// every host the FX chain runs BEFORE the fader, so moving it changes what the listener hears
    /// downstream and not one sample of what this plugin captures. Anthony Reyers ran into exactly
    /// that on 2026-09-01, with two tracks at 0 dB summing to nearly +6 dBFS on the wire and no way to
    /// pull them down from the track. Per-instance, saved with the project, and reachable as plain
    /// parameters so a screen reader can set them without the window.</summary>
    private volatile float sendGain = 1f;
    private volatile float receiveGain = 1f;
    // The parameters carry DECIBELS and these carry the multiply the audio path uses. A DAW user
    // thinks in dB and "1,00 is unity" is not a level anybody recognises (Anthony Reyers, 2026-09-01);
    // 0 dB is. The conversion happens when the parameter moves, never per sample.
    private double sendLevelDb;
    private double receiveLevelDb;

    /// <summary>The quietest the level controls go, and it is a real OFF rather than very quiet: at
    /// the bottom of the range the gain is exactly zero, so "all the way down" means silence and not
    /// -60 dB of leak.</summary>
    internal const double MinLevelDb = -60;

    /// <summary>The loudest. Boost is here because a quiet source cannot always be fixed upstream; the
    /// reason the range is not larger is that anything past this is a gain staging problem, not a
    /// level.</summary>
    internal const double MaxLevelDb = 12;

    internal static float GainFromDb(double db)
        => db <= MinLevelDb ? 0f : (float)Math.Pow(10.0, Math.Min(db, MaxLevelDb) / 20.0);

    internal static double DbFromGain(float gain)
        => gain <= 0f ? MinLevelDb : Math.Clamp(20.0 * Math.Log10(gain), MinLevelDb, MaxLevelDb);

    /// <summary>The link to the app, for the editor panel to read peers and status from.</summary>
    internal PluginBridgeClient? Bridge => bridge;

    /// <summary>Set what this instance does. Either direction, both, or neither, and who it is
    /// receiving. Releasing anybody we are no longer receiving happens inside the client, so the app
    /// gets them back through the speakers immediately rather than after a timeout.</summary>
    internal void SetJob(bool send, bool receive, IReadOnlyList<System.Net.IPAddress> peers, bool all = false)
    {
        // Two threads reach this: the host's, when a parameter or the window moves, and our own
        // refresh timer when the peer list changes underneath us. Neither is the audio thread, and
        // both end up writing the same three fields and then telling the link about them - so without
        // this they can interleave and leave the client claiming a set the plugin no longer thinks it
        // has.
        lock (jobGate)
        {
            var next = peers as System.Net.IPAddress[] ?? [.. peers];
            var changed = send != SendEnabled || receive != ReceiveEnabled || all != allPeers || !SameSet(next, chosenPeers);
            SendEnabled = send;
            ReceiveEnabled = receive;
            allPeers = all;
            chosenPeers = next;
            // Join or leave the DAW's send bus. Leaving matters as much as joining: the bus waits for
            // every registered sender before it flushes a block, so an instance that stopped sending and
            // stayed registered would hold up every other track by a block, every block.
            if (bridge is { } busLink)
            {
                if (send) PluginSendBus.Register(instanceId, busLink);
                else PluginSendBus.Unregister(instanceId);
            }
            // Only claim people while we are actually receiving them. A claim takes somebody out of
            // RemSound's speakers, so holding one for a direction that is switched off would leave them
            // mute everywhere with nothing on screen to explain it.
            bridge?.SetReceivedPeers(receive ? next : []);
            // Only on a real change: this is called on every parameter touch, and a line per touch would
            // bury the one that mattered.
            if (changed) log?.Event($"job: {DescribeJob()}");
        }
    }

    /// <summary>Guards the job fields against the window and the refresh timer arriving at once. Never
    /// taken on the audio thread, which reads only the two direction flags and the two gains.</summary>
    private readonly object jobGate = new();

    private string DescribeJob() => (SendEnabled, ReceiveEnabled) switch
    {
        (false, false) => "idle - neither sending nor receiving",
        (true, false) => "sending this track to the peers",
        (false, true) => $"receiving {DescribePeers()} onto this track",
        (true, true) => $"sending this track AND receiving {DescribePeers()} onto it",
    };

    private string DescribePeers()
    {
        if (allPeers) return $"all peers ({chosenPeers.Length} connected)";
        return chosenPeers.Length switch
        {
            0 => "nobody",
            1 => chosenPeers[0].ToString(),
            _ => $"{chosenPeers.Length} peers ({string.Join(", ", chosenPeers.Select(p => p.ToString()))})",
        };
    }

    private static bool SameSet(System.Net.IPAddress[] a, System.Net.IPAddress[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++) if (!a[i].Equals(b[i])) return false;
        return true;
    }

    /// <summary>Who this instance is receiving RIGHT NOW: the remembered set, minus anybody who is not
    /// currently connected, or everybody when "all peers" is on.</summary>
    private System.Net.IPAddress[] chosenPeers = [];

    /// <summary>Take everybody, including whoever joins later. A dynamic set rather than a snapshot,
    /// which is the whole point of it: ticking it once means a track that follows the session.</summary>
    private bool allPeers;

    /// <summary>Gate seam: the loopback port this instance's bridge client talks to. Zero means the
    /// real one. A gate run must never disturb a RemSound the user has open, so the suite that drives
    /// a real instance against a real host points this at a host of its own. It is the seam the work
    /// log asked for: without it nothing could drive a real plugin against a real bridge, and the
    /// window and parameter paths could only be tested in halves.</summary>
    internal static int BridgePortForTest;

    // Gate seams: drive the real job change and the real parameter write, and read back what was
    // persisted, rather than a parallel copy of either.

    /// <summary>Choose these people, exactly as the window does: remember them by address FIRST, then
    /// apply the job. Doing only the second half would be a seam that behaves unlike the thing it is
    /// standing in for.</summary>
    internal void SetJobForTest(bool send, bool receive, System.Net.IPAddress? peer)
        => SetJobForTest(send, receive, peer is null ? [] : [peer]);
    internal void SetJobForTest(bool send, bool receive, IReadOnlyList<System.Net.IPAddress> peers, bool all = false)
    {
        savedPeerAddresses = [.. peers.Select(p => p.ToString())];
        SetJob(send, receive, peers, all);
    }
    internal void PushJobToParametersForTest(bool send, bool receive, System.Net.IPAddress? peer)
        => PushJobToParameters(send, receive, peer is null ? [] : [peer], false);
    internal string? SavedPeerAddressForTest => savedPeerAddresses.Length == 0 ? null : savedPeerAddresses[0];
    internal IReadOnlyList<string> SavedPeerAddressesForTest => savedPeerAddresses;
    internal float SendGainForTest => sendGain;
    internal float ReceiveGainForTest => receiveGain;
    internal void SetSendLevelForTest(float v) { sendGain = v; sendLevelDb = DbFromGain(v); }
    internal void SetReceiveLevelForTest(float v) { receiveGain = v; receiveLevelDb = DbFromGain(v); }
    internal bool AllPeersForTest => allPeers;
    internal double SendLevelDbForTest => sendLevelDb;
    internal double ReceiveLevelDbForTest => receiveLevelDb;

    /// <summary>Which peer this instance resolved to - for the gate, so "the parameter reached the
    /// engine" is checked rather than assumed.</summary>
    internal System.Net.IPAddress? ChosenPeerForTest => chosenPeers.Length == 0 ? null : chosenPeers[0];

    /// <summary>Everybody this instance resolved to.</summary>
    internal IReadOnlyList<System.Net.IPAddress> ChosenPeersForTest => chosenPeers;

    /// <summary>The peer list as this instance orders it - the gate's way of checking that the order
    /// is ours and deterministic rather than the app's arrival order.</summary>
    internal IReadOnlyList<(System.Net.IPAddress Address, string Name)> SortedPeersForTest => SortedKnownPeers();

    private AudioPluginParameter? sendParameter;
    private AudioPluginParameter? receiveParameter;
    private AudioPluginParameter? peerParameter;
    private AudioPluginParameter? peerIncludeParameter;
    private AudioPluginParameter? allPeersParameter;
    private AudioPluginParameter? sendLevelParameter;
    private AudioPluginParameter? receiveLevelParameter;
    private AudioPluginParameter? activeParameter;

    /// <summary>
    /// The cursor and the tick, as last seen. A set of people cannot ride on one continuous parameter,
    /// and fixed "peer 1..4" slots would cap it at however many slots there were. But a checked LIST is
    /// a cursor plus a tick, and those are two numbers: move the cursor to name somebody, and the tick
    /// says whether they are on this track. The only limit is the link's own
    /// (<see cref="PluginBridgeProtocol.MaxClaimedPeers"/>, the cursor's top value), and it reads aloud
    /// properly.
    ///
    /// <para>These two fields are what tells the cursor MOVING (which changes nothing and republishes
    /// the tick for the person now under it) from the tick BEING CHANGED (which adds or removes that
    /// person). Without them the tick would be re-applied to whoever the cursor landed on.</para>
    /// </summary>
    private int lastCursorPosition = -1;
    private bool lastIncludeValue;

    /// <summary>Set while <see cref="PushJobToParameters"/> is writing, so the parameter changes it
    /// makes do not come straight back in as decisions.
    ///
    /// <para>Every parameter raises PropertyChanged into <see cref="ApplyParameters"/>, and
    /// SetParameter writes EditValue and ProcessValue, so one push fired ApplyParameters several times
    /// over WHILE the push was half done (up to four times when there were three parameters). Each of
    /// those re-read a mixture of new and old values — most damagingly the old chosen address, which
    /// was assigned last — and called SetJob with the PREVIOUS peer. The user picked somebody in the
    /// window, the window claimed them
    /// correctly, and then the parameter push immediately claimed the person they had just moved away
    /// from. That is Anthony Reyers' "iPhone gave me HOMESERV's sound and the other way around",
    /// 2026-08-29: the first instance often escaped it because it had no previous peer to go back to.
    /// Only the second and later instances, which did, heard the wrong person.</para>
    ///
    /// <para>UI thread only — parameters are deliberately applied off the audio thread — so a plain
    /// field is the whole guard needed.</para></summary>
    private bool pushingParameters;

    /// <summary>What the last push told the parameters to say, and when. The <see cref="pushingParameters"/>
    /// flag only covers changes that arrive WHILE the push runs; the host does not always deliver them
    /// then. In Anthony Reyers' 00:16:12 session on 2026-08-29 a single window change produced five
    /// parameter callbacks in the same millisecond, all AFTER the push had returned, and the first of
    /// them resolved to the peer he had just moved away from and put them back on the track. He then
    /// chose the same person again three seconds later and it stuck, which is what "it flips sometimes"
    /// looks like from the outside.
    ///
    /// <para>So the guard is widened from "during the push" to "for a beat afterwards": a parameter
    /// reading that CONTRADICTS the decision just pushed is the parameters not having caught up yet,
    /// and is ignored. One that agrees with it, or arrives later than the settle window, is a real
    /// change and is applied normally. The window is long enough to cover a host's deferred delivery
    /// and far shorter than any human's second thought.</para></summary>
    private const int PushSettleMs = 250;
    private long lastPushTicks = long.MinValue / 2;
    private bool lastPushSend;
    private bool lastPushReceive;
    private string? lastPushPeerText;

    /// <summary>Take what the parameters now say and make it so, through the same <see cref="SetJob"/>
    /// the window ends in, deliberately: two routes to the same decision must not be two
    /// implementations of it, or the fallback would drift into behaving differently from the thing it
    /// is a fallback for.</summary>
    internal void ApplyParameters()
    {
        if (bridge is null) return;
        // The push is only mirroring a decision the caller has ALREADY applied through SetJob. Reading
        // it back mid-write can only produce a worse answer than the one we started with.
        if (pushingParameters) return;
        // EditValue, not ProcessValue: these are decisions a person makes, not automation curves the
        // host ramps per sample. ProcessValue only catches up on the audio thread, so reading it here
        // would apply the PREVIOUS choice - the setting would appear to lag one change behind.
        var active = activeParameter is null || activeParameter.EditValue >= 0.5;
        // Active is the user's own bypass: it switches BOTH directions off without forgetting which
        // ones they had on, so re-ticking it puts the instance back exactly as it was.
        var wantSend = active && sendParameter is not null && sendParameter.EditValue >= 0.5;
        var wantReceive = active && receiveParameter is not null && receiveParameter.EditValue >= 0.5;

        // Levels apply whatever the direction switches say, so a trim set while idle is still there
        // when the direction is switched on. The parameter is in dB; the audio path gets the multiply.
        if (sendLevelParameter is not null)
        {
            sendLevelDb = sendLevelParameter.EditValue;
            sendGain = GainFromDb(sendLevelDb);
        }
        if (receiveLevelParameter is not null)
        {
            receiveLevelDb = receiveLevelParameter.EditValue;
            receiveGain = GainFromDb(receiveLevelDb);
        }

        var wantAll = allPeersParameter is not null && allPeersParameter.EditValue >= 0.5;

        // THE CURSOR AND THE TICK. Moving the cursor must not change anything; it only says who the
        // tick is about, and the tick is then republished to describe that person. Changing the tick
        // is what adds or removes them. See lastCursorPosition.
        var sorted = SortedKnownPeers();
        var cursor = peerParameter is null ? 0 : (int)Math.Round(peerParameter.EditValue);
        var cursorAddress = cursor >= 1 && cursor <= sorted.Count ? sorted[cursor - 1].Address : null;
        var include = peerIncludeParameter is not null && peerIncludeParameter.EditValue >= 0.5;

        if (cursor != lastCursorPosition)
        {
            lastCursorPosition = cursor;
            var member = cursorAddress is not null && savedPeerAddresses.Contains(cursorAddress.ToString());
            lastIncludeValue = member;
            pushingParameters = true;
            try { SetParameter(peerIncludeParameter, member ? 1 : 0); }
            finally { pushingParameters = false; }
        }
        else if (include != lastIncludeValue)
        {
            lastIncludeValue = include;
            if (cursorAddress is not null) Remember(cursorAddress, include);
        }

        var peers = wantReceive ? EffectivePeers(wantAll, sorted) : [];

        // Still settling from a push we just made, and this reading disagrees with it? Then it is the
        // stale value arriving late, not a decision. See PushSettleMs.
        if (Environment.TickCount64 - lastPushTicks < PushSettleMs
            && (wantSend != lastPushSend || wantReceive != lastPushReceive
                || !string.Equals(Join(peers), lastPushPeerText, StringComparison.Ordinal)))
        {
            return;
        }

        SetJob(wantSend, wantReceive, peers, wantAll);
    }

    /// <summary>Add somebody to this instance's set, or take them off it. Held as an ADDRESS, so
    /// nobody joining or leaving can repoint it at a different human, and kept even while that person
    /// is disconnected so they come back to the same track when they return.</summary>
    private void Remember(System.Net.IPAddress peer, bool include)
    {
        var text = peer.ToString();
        var set = savedPeerAddresses.ToList();
        if (include)
        {
            if (!set.Contains(text)) set.Add(text);
        }
        else set.RemoveAll(a => a == text);
        savedPeerAddresses = [.. set];
    }

    /// <summary>Who to actually claim: everybody when "all peers" is on, otherwise the remembered set
    /// filtered down to the people who are currently connected. A remembered peer who has gone is kept
    /// in the set but not claimed — claiming somebody who is not there would be a request the app can
    /// only answer with silence.</summary>
    private System.Net.IPAddress[] EffectivePeers(bool all, IReadOnlyList<(System.Net.IPAddress Address, string Name)> sorted)
    {
        if (all) return [.. sorted.Select(p => p.Address)];
        return [.. sorted.Where(p => savedPeerAddresses.Contains(p.Address.ToString())).Select(p => p.Address)];
    }

    private System.Net.IPAddress[] EffectivePeers() => EffectivePeers(allPeers, SortedKnownPeers());

    /// <summary>
    /// The peer list in OUR order, not the app's.
    ///
    /// <para>The app reports peers in the order it happens to have learned about them, so position 3
    /// is a different person on a different day — and position is what a parameter can hold. Sorting
    /// by name, then by address to break ties, makes the numbering the same in the window, in the
    /// parameter list, and in every future session, which is what stops a saved cursor pointing at a
    /// stranger.</para>
    /// </summary>
    private IReadOnlyList<(System.Net.IPAddress Address, string Name)> SortedKnownPeers()
    {
        var peers = bridge?.KnownPeers;
        if (peers is null || peers.Count == 0) return [];
        var list = peers.ToList();
        list.Sort((a, b) =>
        {
            var byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : string.CompareOrdinal(a.Address.ToString(), b.Address.ToString());
        });
        return list;
    }

    private static string Join(IReadOnlyList<System.Net.IPAddress> peers)
        => peers.Count == 0 ? "" : string.Join(",", peers.Select(p => p.ToString()));

    /// <summary>
    /// Re-resolve who we are receiving, because the peer LIST may have changed underneath us.
    ///
    /// <para>Called on the plugin's own timer rather than from a parameter change, since nothing the
    /// user does is involved: somebody joined or left. It matters most with "all peers", where the
    /// whole point is that the track follows the session, but it also brings a remembered peer back
    /// onto the track when they reconnect. Cheap and safe to call repeatedly — an unchanged set is a
    /// no-op inside the client, and its queue of replies is not disturbed.</para>
    /// </summary>
    private void RefreshPeerSet()
    {
        lock (jobGate)
        {
            if (!ReceiveEnabled) return;
            var peers = EffectivePeers();
            if (SameSet(peers, chosenPeers)) return;
            chosenPeers = peers;
            bridge?.SetReceivedPeers(peers);
            log?.Event($"peer list changed - now {DescribePeers()}");
        }
    }

    /// <summary>Keep the parameters in step when the WINDOW is what changed. Without this, opening the
    /// host's parameter list after using the window would show stale values and one nudge would undo
    /// the user's choice.</summary>
    private void PushJobToParameters(bool send, bool receive, IReadOnlyList<System.Net.IPAddress> peers, bool all)
    {
        // BOTH values, not just EditValue. The host saves ProcessValue, so a window that wrote only
        // EditValue left every parameter sitting at its default — which is why the plugin reverted to
        // "send" within seconds of leaving the window, and why a saved project came back with job=0
        // whatever it had been doing. The saved state chunk recorded all three at their defaults, and
        // that was this bug written to disk.
        // THE ADDRESSES FIRST, and the whole push guarded. It used to go the other way round — job,
        // then index, then the address last — and every one of those writes re-entered
        // ApplyParameters, which resolves by address in preference to index. So it kept reading the
        // address of the person the user had just left. See `pushingParameters`.
        lastPushSend = send;
        lastPushReceive = receive;
        lastPushPeerText = receive ? Join(peers) : "";
        lastPushTicks = Environment.TickCount64;
        pushingParameters = true;
        try
        {
            SetParameter(allPeersParameter, all ? 1 : 0);
            // The cursor stays where the user left it; only the TICK is republished, so it describes
            // whoever the cursor is on now that the set has changed.
            var sorted = SortedKnownPeers();
            var cursor = lastCursorPosition >= 1 && lastCursorPosition <= sorted.Count ? sorted[lastCursorPosition - 1].Address : null;
            lastIncludeValue = cursor is not null && savedPeerAddresses.Contains(cursor.ToString());
            SetParameter(peerIncludeParameter, lastIncludeValue ? 1 : 0);
            SetParameter(sendParameter, send ? 1 : 0);
            SetParameter(receiveParameter, receive ? 1 : 0);
            SetParameter(sendLevelParameter, sendLevelDb);
            SetParameter(receiveLevelParameter, receiveLevelDb);
        }
        finally { pushingParameters = false; }
    }

    private static void SetParameter(AudioPluginParameter? parameter, double value)
    {
        if (parameter is null) return;
        parameter.EditValue = value;
        parameter.ProcessValue = value;
    }

    /// <summary>Everybody this instance has been told to receive, as ADDRESSES.
    ///
    /// <para>A parameter can only hold a number, and a position in a list is not a person: reopen a
    /// project after somebody joined or left and position 2 is somebody else. So the set is kept by
    /// address, written into the saved state, and positions are used for nothing but the cursor.</para>
    ///
    /// <para>Somebody who disconnects STAYS in here. They are not claimed while they are away — the
    /// app can only answer with silence for a peer who is not there — and they are picked up again the
    /// moment they come back, which is what a person expects of a track they set up once.</para></summary>
    private string[] savedPeerAddresses = [];

    public RemSoundPlugin()
    {
        Company = "RemSound";
        Website = "https://github.com/Ednunp/RemSound";
        PluginName = "RemSound";
        PluginCategory = "Fx|Network";
        PluginVersion = "6.0.0";
        // Stable identity: a DAW keys saved sessions off this, so it must never change once shipped
        // or every existing project silently loses its RemSound instances.
        PluginID = 0x52656D536E640600; // "RemSnd" + 06 00

        // THREE INHERITED PROPERTIES, EACH FATAL IF LEFT ALONE. All three were missed in the first
        // build, and each failed silently with nothing the host could show a user (Anthony Reyers,
        // 2026-08-16, who proved every one of them by controlled experiment against the real bridge).
        //
        // Contact: AudioPlugSharpFactory marshals Company, Website and Contact into a VST3
        // PFactoryInfo. A null string becomes a null char*, the copy dereferences it, the factory
        // constructor throws, and GetPluginFactory returns NULL — so the host sees a DLL containing no
        // plugins at all. THIS is why RemSound never appeared in any effects list.
        Contact = "https://github.com/Ednunp/RemSound";

        // HasUserInterface: defaults to FALSE in the base constructor, and the VST3 controller refuses
        // to build a view when it is false. The whole accessible window existed and was never once
        // asked to appear; what a screen reader found instead was the host's own generic parameter
        // panel, which shows our parameter and port names and so looks convincingly like ours.
        HasUserInterface = true;

        // CacheLoadContext: defaults to false, so every instance built a fresh load context and
        // reloaded the entire RemSound stack — two plugins meant two independent copies of Core,
        // Sender and Receiver. That is also what desynchronises AudioPlugSharp's processor/controller
        // pairing, and a controller initialising against a plugin whose Initialize() has not run walks
        // a null Parameters list in unguarded native code and takes the DAW down with it.
        CacheLoadContext = true;

        // PORTS IN THE CONSTRUCTOR, not in Initialize(). The host calls SetMaxAudioBufferSize — which
        // is what allocates each port's buffers — on its own schedule, and it did so before Initialize
        // had created the ports. The ports were then replaced by fresh ones that nobody ever sized, so
        // every single audio block threw ArgumentNullException inside PreProcess and no input was ever
        // read. That is why send mode was silent in both directions.
        //
        // Stereo in, stereo out. IN is the track feeding your peers; OUT is what arrives from them.
        // Both ports exist in both jobs so the DAW's routing never changes when the user switches an
        // instance between sending and receiving — a track that rewires itself mid-session is a nasty
        // surprise, especially for someone navigating by screen reader.
        InputPorts = [monitorIn = new AudioIOPortManaged("Track in", EAudioChannelConfiguration.Stereo)];
        OutputPorts = [peerOut = new AudioIOPortManaged("Peer out", EAudioChannelConfiguration.Stereo)];

        log = new PluginLog(instanceId);
        log.SnapshotSource = DescribeForLog;
        log.Event($"plugin constructed (RemSound {PluginVersion})");
    }

    /// <summary>The once-a-second line. Everything needed to answer "why did it sound wrong" without
    /// having to ask another question: what this instance was doing, whether the app was answering,
    /// what rate and block size the host was running, whether we were resampling, and how full the
    /// buffer was. Read on the log's timer thread, never on the audio thread.</summary>
    private PluginLogSnapshot DescribeForLog()
    {
        var b = bridge;
        return new PluginLogSnapshot(
            Job: (SendEnabled, ReceiveEnabled) switch
            {
                (false, false) => "idle",
                (true, false) => "send",
                (false, true) => "receive",
                (true, true) => "send+receive",
            },
            Peer: chosenPeers.Length == 0 ? null : (allPeers ? $"all ({chosenPeers.Length})" : string.Join(" ", chosenPeers.Select(p => p.ToString()))),
            Connected: b?.Connected ?? false,
            HostSampleRate: preparedSampleRate,
            BlockFrames: preparedBlockSize,
            Resampling: Math.Abs(preparedSampleRate - WireSampleRate) > 0.5,
            BlocksOut: b?.SentBlocks ?? 0,
            BlocksIn: b?.ServedBlocks ?? 0,
            ShortBlocks: b?.StarvedBlocks ?? 0,
            RingFrames: b?.RingFrames ?? 0,
            BytesOut: b?.BytesSent ?? 0,
            BytesIn: b?.BytesReceived ?? 0,
            KnownPeers: b?.KnownPeers.Count ?? 0);
    }

    /// <summary>The host telling us its largest block. Recorded as well as applied: if anything ever
    /// replaces a port after this has been called, the replacement would otherwise carry unallocated
    /// buffers and every block would throw. Cheap insurance against the exact fault that made send
    /// mode silent.</summary>
    public override void SetMaxAudioBufferSize(uint maxSamples, EAudioBitsPerSample bitsPerSample)
    {
        base.SetMaxAudioBufferSize(maxSamples, bitsPerSample);
        hostMaxSamples = maxSamples;
        hostBitsPerSample = bitsPerSample;
        log?.Event($"host buffer size: up to {maxSamples} samples, {bitsPerSample}");
    }

    private uint hostMaxSamples;
    private EAudioBitsPerSample hostBitsPerSample = EAudioBitsPerSample.Bits64;

    public override void Initialize()
    {
        base.Initialize();

        // Ports are built in the constructor, but re-apply whatever size the host has already asked
        // for, so they can never be left unallocated whichever order the host calls things in.
        if (hostMaxSamples > 0) base.SetMaxAudioBufferSize(hostMaxSamples, hostBitsPerSample);

        // The app owns the one connection; this is our end of the link to it. Opened here rather than
        // in the constructor because a DAW constructs plugins to inspect them without ever running
        // them, and a scan of the plugin folder should not open sockets.
        bridge = new PluginBridgeClient(BridgePortForTest > 0 ? BridgePortForTest : PluginBridgeProtocol.DefaultPort, id: instanceId);
        bridge.Notable += message => log?.Event($"link: {message}");
        // Through the per-process bus, not straight at the link: every SENDING instance in this DAW
        // adds into one block on the same sample boundaries and one of them carries it across, so
        // RemSound sees one stream per DAW however many tracks are feeding it. See PluginSendBus for
        // why the summing belongs on this side of the socket.
        capture = new HostCaptureBackend(samples => PluginSendBus.Submit(instanceId, samples.Span));
        render = new PeerRenderBridge(bridge);
        bridge.Hello();
        log?.Event($"initialised - host says {Host?.SampleRate ?? 0:0} Hz, up to {Host?.MaxAudioBufferSize ?? 0} frames per block; "
                 + $"said hello to RemSound on 127.0.0.1:{PluginBridgeProtocol.DefaultPort}");

        // THE SCREEN-READER FALLBACK. The window is the intended way to work this plugin, and it is
        // ordinary WinForms precisely so NVDA can read it. But keyboard focus across a host's plugin
        // frame is the one thing that cannot be proven outside a real DAW, and if a host gets it
        // wrong the window becomes unreachable with no way back.
        //
        // Named parameters are that way back: every DAW exposes a plain parameter list, and in Reaper
        // with OSARA that list is fully keyboard-navigable and spoken. So every decision the window
        // offers is also a parameter, with a name that makes sense read aloud out of context —
        // "Send this track to your peers", not "Mode".
        //
        // They are also what a host would automate, which is harmless here: nobody automates who is on
        // a track mid-take, but a host that saves parameter values gets the instance's setup restored
        // with the session for free.
        // TWO SWITCHES, NOT A MODE. Independent, so an instance can do both at once, and both OFF by
        // default so a freshly-inserted plugin never starts broadcasting a track on its own.
        AddParameter(sendParameter = new AudioPluginParameter
        {
            ID = "send",
            Name = "Send this track to your peers",
            ValueFormat = "0",
            MinValue = 0,
            MaxValue = 1,
            DefaultValue = 0,
        });
        AddParameter(receiveParameter = new AudioPluginParameter
        {
            ID = "receive",
            Name = "Receive a peer onto this track",
            ValueFormat = "0",
            MinValue = 0,
            MaxValue = 1,
            DefaultValue = 0,
        });
        // THE CURSOR AND THE TICK, which together are a checked list expressed as two knobs. A set of
        // people cannot ride on one continuous parameter, and fixed slots would need a knob per person
        // and cap at however many we made; this is two knobs, up to the most people the link carries on
        // one track (MaxClaimedPeers, the cursor's top value). Move the cursor to name somebody, then
        // tick to put them on the track. The tick READS BACK the person under the cursor, so it also
        // answers "is this one on?" without opening the window.
        AddParameter(peerParameter = new AudioPluginParameter
        {
            ID = "peer",
            // One-based when spoken: "peer 1" is the first person in the list, which is what somebody
            // counting down a list expects. A zero would be read as "none" by anyone sane.
            Name = "Peer in list",
            ValueFormat = "0",
            MinValue = 0,
            MaxValue = PluginBridgeProtocol.MaxClaimedPeers,
            DefaultValue = 0,
        });
        AddParameter(peerIncludeParameter = new AudioPluginParameter
        {
            ID = "peerinclude",
            Name = "Receive the peer in list",
            ValueFormat = "0",
            MinValue = 0,
            MaxValue = 1,
            DefaultValue = 0,
        });
        AddParameter(allPeersParameter = new AudioPluginParameter
        {
            ID = "allpeers",
            Name = "Receive all peers",
            ValueFormat = "0",
            MinValue = 0,
            MaxValue = 1,
            DefaultValue = 0,
        });
        // The levels the DAW's fader cannot give you: the FX chain runs before the fader in every
        // host, so the track fader changes what YOU hear and not one sample of what goes out.
        //
        // IN DECIBELS, because that is the unit a DAW user has in their hands all day and "1,00 is
        // unity" is not a level anybody recognises (Anthony Reyers, 2026-09-01). 0 dB is unity, the
        // bottom of the range is a real off. AudioPlugSharp does ship a DecibelParameter, and it is
        // not usable here: it normalises as 10^(dB/20) with the top of the range pinned at 0 dB, so
        // any boost at all pushes the host's normalised value past 1. A plain parameter carrying dB
        // with a dB format string normalises linearly over the range and reads correctly.
        AddParameter(sendLevelParameter = new AudioPluginParameter
        {
            ID = "sendlevel",
            Name = "Send level",
            ValueFormat = "{0:0.0} dB",
            MinValue = MinLevelDb,
            MaxValue = MaxLevelDb,
            DefaultValue = 0,
        });
        AddParameter(receiveLevelParameter = new AudioPluginParameter
        {
            ID = "receivelevel",
            Name = "Receive level",
            ValueFormat = "{0:0.0} dB",
            MinValue = MinLevelDb,
            MaxValue = MaxLevelDb,
            DefaultValue = 0,
        });
        AddParameter(activeParameter = new AudioPluginParameter
        {
            ID = "active",
            Name = "Active",
            ValueFormat = "0",
            MinValue = 0,
            MaxValue = 1,
            DefaultValue = 1,
        });

        // Applied off the audio thread. A parameter change sends a datagram and touches the claim
        // register; doing that from Process() would put a syscall on the DAW's audio thread for the
        // sake of a decision the user makes once.
        sendParameter.PropertyChanged += (_, _) => ApplyParameters();
        receiveParameter.PropertyChanged += (_, _) => ApplyParameters();
        peerParameter.PropertyChanged += (_, _) => ApplyParameters();
        peerIncludeParameter.PropertyChanged += (_, _) => ApplyParameters();
        allPeersParameter.PropertyChanged += (_, _) => ApplyParameters();
        sendLevelParameter.PropertyChanged += (_, _) => ApplyParameters();
        receiveLevelParameter.PropertyChanged += (_, _) => ApplyParameters();
        activeParameter.PropertyChanged += (_, _) => ApplyParameters();

        // ASK AGAIN, ON OUR OWN. The peer list used to arrive only in reply to a Hello, and a Hello was
        // only sent when the instance loaded, started, or its window opened — so with no window open
        // the list was whatever it had been at load. "Receive all peers" would never have noticed
        // somebody joining, and a remembered peer reconnecting would never have come back to the track.
        // One small datagram every two seconds fixes both, and it is also how the plugin now reconnects
        // by itself after RemSound is restarted underneath it.
        refreshTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                bridge?.Hello();
                RefreshPeerSet();
                ReportSkippedFrames();
            }
            catch { /* a timer tick must never take the DAW down */ }
        }, null, PeerRefreshMs, PeerRefreshMs);
    }

    /// <summary>Stale replies the bridge client dropped since the last tick, as one line. A few hundred
    /// frames at start are the app's first replies coming back later than a DAW block; a count that
    /// keeps growing means replies are routinely late. See PluginBridgeClient.ReadPeerBlock. (The line
    /// still starts "ring:", after the byte ring the client's reply queue replaced.)</summary>
    private void ReportSkippedFrames()
    {
        var total = bridge?.SkippedFrames ?? 0;
        var delta = total - reportedSkippedFrames;
        if (delta <= 0) return;
        reportedSkippedFrames = total;
        log?.Event($"ring: skipped {delta} stale frame(s) so this track stays in step (total {total})");
    }

    private long reportedSkippedFrames;

    private const int PeerRefreshMs = 2000;
    private System.Threading.Timer? refreshTimer;

    public override void Process()
    {
        base.Process();

        // The DAW's block for this track, and the buffer we owe it back. Spans are stack-only, so
        // they are taken one at a time rather than gathered into an array — a restriction that is
        // welcome here, because it keeps this method allocation-free.
        if (peerOut is null || monitorIn is null) return;
        var outLeft = peerOut.GetAudioBuffer(0);
        var outRight = peerOut.GetAudioBuffer(1);

        // The host can change its block size or rate between blocks. Growing buffers here would
        // allocate on the audio thread — the one thing that must never happen, because .NET stops
        // every thread in the process to collect, which inside a DAW means every plugin in the
        // session. So we resize only when it actually changed, and output silence for that one block.
        var blockSize = outLeft.Length;
        var rate = Host?.SampleRate ?? WireSampleRate;
        if (blockSize != preparedBlockSize || Math.Abs(rate - preparedSampleRate) > 0.5)
        {
            var first = preparedBlockSize == 0;
            var previousBlock = preparedBlockSize;
            var previousRate = preparedSampleRate;
            preparedBlockSize = blockSize;
            preparedSampleRate = rate;
            capture?.PrepareForBlockSize(blockSize, rate);
            render?.PrepareForBlockSize(blockSize, rate);
            // Buffers are resized OFF the audio thread's steady path, and only on a real change, so
            // this line is rare. If it ever appears repeatedly in a log, that is the finding: the host
            // is changing its block size every callback and we are allocating in its audio thread.
            log?.Event(first
                ? $"audio started: {rate:0} Hz, {blockSize} frames per block"
                  + (Math.Abs(rate - WireSampleRate) > 0.5 ? $" - resampling to and from {WireSampleRate} Hz" : " - no resampling needed")
                : $"host changed format: {previousRate:0} Hz/{previousBlock} frames -> {rate:0} Hz/{blockSize} frames (one block of silence while buffers resize)");
            outLeft.Clear();
            outRight.Clear();
            return;
        }

        var inLeft = monitorIn.GetAudioBuffer(0);
        var inRight = monitorIn.GetAudioBuffer(1);

        // ---- SEND THE INPUT, AND SEND IT FIRST ------------------------------------------------
        //
        // This single line is the whole feedback guarantee, and it replaces the old one. Until now the
        // rule was "one job per instance", so a receiving instance could not send and therefore could
        // not feed itself. Now an instance can do both at once, and what stops a loop is that we hand
        // the app the track's INPUT and never its output: what leaves cannot contain what arrived.
        // SonoBus keeps the same invariant the same way — it transmits its input buffer, and passing
        // one peer's audio on to another is a routing matrix you have to switch on deliberately.
        //
        // The ORDER matters too, not just the buffer. A host is allowed to hand us the same memory for
        // input and output (in-place processing), in which case writing the block below would overwrite
        // the input we are about to read. Submitting first makes that harmless whichever way the host
        // does it.
        if (SendEnabled) capture?.SubmitHostBlock(inLeft, inRight, sendGain);

        // ---- THE TRACK PASSES THROUGH ----------------------------------------------------------
        //
        // Always, in both directions and in neither. Effects are in SERIES: a plugin that outputs
        // silence silences everything upstream of it, and there is no second copy of the signal for it
        // to double against. The first build cleared the output while sending and cleared it while
        // receiving, and both made the plugin unusable on a normal FX chain (Anthony Reyers, 2026-08-28:
        // a track playing a song went mute the moment you set the plugin to receive - you could have
        // the peer or your own audio, never both).
        var throughFrames = Math.Min(blockSize, Math.Min(inLeft.Length, inRight.Length));
        for (var i = 0; i < throughFrames; i++) { outLeft[i] = inLeft[i]; outRight[i] = inRight[i]; }
        for (var i = throughFrames; i < blockSize; i++) { outLeft[i] = 0; outRight[i] = 0; }

        // ---- AND THE PEER IS SUMMED ON TOP -----------------------------------------------------
        //
        // Anything the buffer could not supply is left alone rather than repeated, so a short block
        // sounds like a gap and not like a stutter.
        //
        // Not attenuated beyond the user's own trim. Two full-scale signals can sum past 1.0, but the
        // receive level is right there and it is a float bus; halving both to be safe would quietly
        // cost 6 dB on the common case where the track is silent, and a hidden gain nobody asked for is
        // worse than a peak the user can see on the meter and fix.
        if (ReceiveEnabled) render?.FillHostBlock(outLeft, outRight, receiveGain, mixInto: true);
    }

    // ---- The window. An ordinary WinForms panel parented into the DAW's plugin frame, which is the
    // whole accessibility bet: real WinForms controls expose themselves to NVDA for free, while most
    // commercial plugin windows are custom-drawn and unreadable. ----------------------------------

    private Form? editorForm;
    private PluginEditorPanel? editorPanel;

    public override void InitializeEditor()
    {
        base.InitializeEditor();
        EditorWidth = 420;
        EditorHeight = 300;
    }

    public override void ShowEditor(IntPtr parentWindow)
    {
        base.ShowEditor(parentWindow);
        editorPanel = new PluginEditorPanel { Dock = DockStyle.Fill };

        // Everything the panel shows comes from the app over the link — the peer list it offers and
        // the status it reports are RemSound's, not a second copy of them kept here that could
        // disagree. Ed's call: "read the profile from the standalone app... it's far easier."
        // OUR order, not the app's, so the window and the parameter cursor number people identically.
        editorPanel.PeerSource = () => SortedKnownPeers().Select(p => (p.Address.ToString(), p.Name)).ToList();
        editorPanel.StatusSource = DescribeStatus;
        // What this instance is ALREADY doing. Without it the panel comes up on its own defaults and
        // announces them, which released the peer and flipped the job to send every time the user
        // opened the window (2026-08-28). The panel is a view of this instance, not a fresh decision
        // about it.
        editorPanel.InitialStateSource = () =>
            (SendEnabled, ReceiveEnabled, (IReadOnlyList<string>)savedPeerAddresses, allPeers,
             activeParameter is null || activeParameter.EditValue >= 0.5,
             (float)sendLevelDb, (float)receiveLevelDb);
        editorPanel.JobChanged += (send, receive, peerTexts, all, sendDb, receiveDb) =>
        {
            sendLevelDb = sendDb;
            receiveLevelDb = receiveDb;
            sendGain = GainFromDb(sendDb);
            receiveGain = GainFromDb(receiveDb);

            // The window can only show people who are CONNECTED, so what it reports back is the ticked
            // set plus anybody we were remembering who is not currently in the list. Taking the window's
            // answer verbatim would quietly forget a peer the moment they dropped off, and the track
            // would not pick them up again when they returned.
            var present = SortedKnownPeers().Select(p => p.Address.ToString()).ToHashSet(StringComparer.Ordinal);
            var kept = savedPeerAddresses.Where(a => !present.Contains(a));
            savedPeerAddresses = [.. peerTexts.Concat(kept).Distinct(StringComparer.Ordinal)];
            allPeers = all;

            var peers = EffectivePeers(all, SortedKnownPeers());
            SetJob(send, receive, peers, all);
            PushJobToParameters(send, receive, peers, all);
        };

        editorForm = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            TopLevel = false,
            Width = (int)EditorWidth,
            Height = (int)EditorHeight,
        };
        editorForm.Controls.Add(editorPanel);

        // Parent into the host's frame. A borderless child form rather than raw child controls, so
        // WinForms keeps its own message loop and focus handling intact across the boundary — which
        // is what a screen reader relies on to see the controls at all.
        if (parentWindow != IntPtr.Zero) SetParent(editorForm.Handle, parentWindow);
        editorForm.Show();
        // Whether the host gave us a window to parent into is worth knowing: a zero handle is the
        // shape of "the plugin window never appeared", and it is otherwise invisible from a bug report.
        log?.Event($"window opened ({EditorWidth}x{EditorHeight}, host frame {(parentWindow == IntPtr.Zero ? "NOT supplied" : "supplied")})");
    }

    public override void HideEditor()
    {
        log?.Event("window closed");
        editorForm?.Close();
        editorForm?.Dispose();
        editorForm = null;
        editorPanel = null;
        base.HideEditor();
    }

    public override void ResizeEditor(uint newWidth, uint newHeight)
    {
        base.ResizeEditor(newWidth, newHeight);
        if (editorForm is not null) editorForm.Size = new Size((int)newWidth, (int)newHeight);
    }

    /// <summary>The status line, in plain English. Says what is actually true rather than "OK" —
    /// including the awkward cases, because "the plugin isn't working" is the report we would
    /// otherwise get with nothing to go on.</summary>
    internal string DescribeStatus()
    {
        if (bridge is null) return "Starting up.";
        if (!bridge.Connected)
            return "RemSound is not answering." + Environment.NewLine
                 + "Start RemSound, or switch the link on in its DAW plugin menu.";
        if (!SendEnabled && !ReceiveEnabled)
            return "Connected to RemSound." + Environment.NewLine
                 + "Doing nothing yet - tick Send, or Receive and choose a peer.";
        if (!ReceiveEnabled)
            return "Sending this track to your peers." + Environment.NewLine
                 + $"{bridge.KnownPeers.Count} peer(s) connected in RemSound.";
        if (chosenPeers.Length == 0)
            return (SendEnabled ? "Sending this track to your peers." : "Connected to RemSound.") + Environment.NewLine
                 + (savedPeerAddresses.Length == 0
                    ? "Choose who to receive."
                    : $"{savedPeerAddresses.Length} chosen, none of them connected right now.");

        // DELIBERATELY STABLE TEXT. The first version put live block counts in here, so the status
        // changed every single second — and every change rewrote the control, which threw keyboard
        // focus back to the first field and made the window nearly unusable with a screen reader
        // (Anthony Reyers, 2026-08-16). A status line that never settles is not a status line.
        //
        // Trouble is still reported, but as a STATE rather than a running total: it appears when
        // blocks start arriving short and goes away when they stop, so the text changes twice rather
        // than sixty times a minute.
        var served = bridge.ServedBlocks;
        var starved = bridge.StarvedBlocks;
        var struggling = served > 200 && starved > served / 20;   // more than one block in twenty
        // "3 chosen, 2 connected" rather than just a count: somebody who has dropped off is still on
        // this track's list and will come back to it, and saying so is the difference between a
        // setting and an apparent fault.
        var who = allPeers
            ? $"all peers ({chosenPeers.Length} connected)"
            : savedPeerAddresses.Length == chosenPeers.Length
                ? (chosenPeers.Length == 1 ? chosenPeers[0].ToString() : $"{chosenPeers.Length} peers")
                : $"{savedPeerAddresses.Length} chosen, {chosenPeers.Length} connected";
        return (SendEnabled ? $"Sending this track, and receiving {who} onto it."
                            : $"Receiving {who} onto this track.") + Environment.NewLine
             + (struggling ? "Audio is arriving short - see the plugin log." : "Running normally.");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    /// <summary>The host has deactivated this instance — bypassed, removed, or the session closing.
    ///
    /// <para>Let the claimed peer go AT ONCE rather than waiting for the claim to lapse. An inactive
    /// plugin produces no audio, so a peer still claimed by one would be silent everywhere for five
    /// seconds with nothing on screen to explain it. Re-activating re-claims on the next block, since
    /// asking for audio IS the claim.</para>
    ///
    /// <para>The socket deliberately stays open: hosts stop and start instances routinely (transport
    /// changes, bypass), and tearing the link down each time would mean a fresh handshake and an empty
    /// buffer every time somebody hit bypass. If the DAW dies outright the claim lapses on its own —
    /// that timeout exists precisely for the case where nobody gets to say goodbye.</para></summary>
    public override void Stop()
    {
        base.Stop();
        capture?.Stop();
        // Off the bus before the host stops calling us, or the tracks that are still running wait a
        // block each time for somebody who is never coming.
        PluginSendBus.Unregister(instanceId);
        bridge?.SetReceivedPeers([]);
        log?.Event($"deactivated by the host - peer released. Totals: {bridge?.ServedBlocks ?? 0} blocks in, "
                 + $"{bridge?.SentBlocks ?? 0} out, {bridge?.StarvedBlocks ?? 0} short");
    }

    /// <summary>Save this instance with the host, and add the peer's ADDRESS to what the base class
    /// writes.
    ///
    /// <para>A parameter can only hold a number, and a position in a list is not a person: reopen a
    /// project after somebody joined or left and position two is somebody else. The address is
    /// appended after the base class's own state, and on restore it wins — the position is only the
    /// fallback for when that person is no longer connected.</para></summary>
    public override byte[] SaveState()
    {
        var baseState = base.SaveState() ?? [];
        // Tagged, so a payload this build did not write is never misread. See RestoreState. The payload is the "all peers" flag and
        // then the chosen set, by address: "v3|A|" or "v3|-|10.0.0.5,10.0.0.9".
        var payload = StateTag + (allPeers ? "A" : "-") + "|" + string.Join(",", savedPeerAddresses);
        var address = System.Text.Encoding.UTF8.GetBytes(payload);
        var combined = new byte[baseState.Length + address.Length + Marker.Length + sizeof(int)];
        Buffer.BlockCopy(baseState, 0, combined, 0, baseState.Length);
        Buffer.BlockCopy(Marker, 0, combined, baseState.Length, Marker.Length);
        BitConverter.GetBytes(address.Length).CopyTo(combined, baseState.Length + Marker.Length);
        Buffer.BlockCopy(address, 0, combined, baseState.Length + Marker.Length + sizeof(int), address.Length);
        return combined;
    }

    public override void RestoreState(byte[] stateData)
    {
        stateAllPeers = false;   // only our own chunk can turn it on
        var baseLength = stateData?.Length ?? 0;
        if (stateData is not null)
        {
            // Find OUR marker at the tail. A chunk without one still restores its parameters — refusing it would
            // lose the user's whole plugin instance. What follows the marker is read only if this build wrote it:
            // the plugin's test builds before 6.0 wrote other payloads, and 6.0 is the first release (Ed,
            // 2026-09-13), so a track saved by one of them opens with nobody chosen.
            var at = LastIndexOf(stateData, Marker);
            if (at >= 0 && at + Marker.Length + sizeof(int) <= stateData.Length)
            {
                var length = BitConverter.ToInt32(stateData, at + Marker.Length);
                var from = at + Marker.Length + sizeof(int);
                if (length >= 0 && from + length <= stateData.Length)
                {
                    var text = System.Text.Encoding.UTF8.GetString(stateData, from, length);
                    savedPeerAddresses = [];
                    if (text.StartsWith(StateTag, StringComparison.Ordinal))
                    {
                        var body = text[StateTag.Length..];
                        var bar = body.IndexOf('|');
                        // Into a field of its own, not straight onto allPeers: base.RestoreState
                        // below raises a parameter change for every value it restores, each of which
                        // re-enters ApplyParameters and assigns allPeers from the PARAMETER. The
                        // chunk's answer would be overwritten before it could be pushed.
                        stateAllPeers = bar > 0 && body[..bar] == "A";
                        var list = bar >= 0 ? body[(bar + 1)..] : body;
                        savedPeerAddresses = list.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    }
                    baseLength = at;
                }
            }
        }

        if (stateData is not null && baseLength != stateData.Length)
        {
            var trimmed = new byte[baseLength];
            Buffer.BlockCopy(stateData, 0, trimmed, 0, baseLength);
            base.RestoreState(trimmed);
        }
        else base.RestoreState(stateData!);

        // OUR CHUNK WINS on the things our chunk owns. The set of people is only in there, and the
        // all-peers flag is saved beside it, so restoring one from the chunk and the other from a
        // parameter is two sources for one decision — and they disagree the moment a host restores a
        // chunk without its parameters, or restores them in the other order.
        pushingParameters = true;
        try { SetParameter(allPeersParameter, stateAllPeers ? 1 : 0); }
        finally { pushingParameters = false; }

        ApplyParameters();
        log?.Event($"state restored - {DescribeJob()}, chosen {(savedPeerAddresses.Length == 0 ? "nobody" : string.Join(", ", savedPeerAddresses))}"
                 + $"{(allPeers ? " (all peers)" : "")}, send level {sendLevelDb:0.0} dB, receive level {receiveLevelDb:0.0} dB");
    }

    /// <summary>The "all peers" flag as our own state chunk carried it, held apart from the live one
    /// because restoring the base class's parameters overwrites the live one on the way past.</summary>
    private bool stateAllPeers;

    /// <summary>Prefix on our own state payload. A payload without it is not read, and the track opens with
    /// nobody chosen; its parameters still restore.</summary>
    private const string StateTag = "v3|";

    private static readonly byte[] Marker = System.Text.Encoding.ASCII.GetBytes("<!--RemSoundPeer:");

    private static int LastIndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = haystack.Length - needle.Length; i >= 0; i--)
        {
            var hit = true;
            for (var j = 0; j < needle.Length; j++) if (haystack[i + j] != needle[j]) { hit = false; break; }
            if (hit) return i;
        }
        return -1;
    }

    /// <summary>Tear the instance down completely. AudioPlugSharp gives a plugin no dispose hook — a
    /// host just unloads the process — so this exists for the gate, which creates real instances and
    /// must not leave their sockets and log timers running behind it.</summary>
    internal void CloseForTest()
    {
        refreshTimer?.Dispose();
        refreshTimer = null;
        PluginSendBus.Unregister(instanceId);
        bridge?.Dispose();
        bridge = null;
        capture?.Dispose();
        capture = null;
        log?.Event("closed");
        log?.Dispose();
        log = null;
    }

    public override void Start()
    {
        base.Start();
        capture?.Start([]);
        if (SendEnabled && bridge is { } sendLink) PluginSendBus.Register(instanceId, sendLink);
        log?.Event("activated by the host");
        // Ask again who is available: RemSound may have been started, or its peers changed, while
        // this instance sat inactive in a saved session.
        bridge?.Hello();
        if (ReceiveEnabled) bridge?.SetReceivedPeers(chosenPeers);
    }
}
