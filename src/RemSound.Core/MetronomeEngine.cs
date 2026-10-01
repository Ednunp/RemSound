using System.Net;

namespace RemSound.Core;

/// <summary>Where you hear your own metronome.</summary>
public enum MetronomeHearIn
{
    /// <summary>In RemSound's own outputs (the default).</summary>
    RemSound = 0,
    /// <summary>In the DAW: a track whose RemSound plugin chooses "Metronome" in its peer list.</summary>
    Daw = 1,
}

/// <summary>What happens when someone asks to be the master metronome.</summary>
public enum MetronomeMasterAnswer
{
    Ask = 0,
    AcceptAutomatically = 1,
}

/// <summary>
/// THE METRONOME'S SETTINGS (Ed, 2026-09-28), per profile - they belong with the session's setup. The global hotkey is
/// computer-wide with the other shortcuts (<see cref="AppConfig.MetronomeHotkey"/>). Anything that remembers or does
/// something on its own is off in a new profile: the click stays out of recordings and a master is asked about.
/// </summary>
public sealed class MetronomeSettings
{
    public const int DefaultVolume = 70;

    public int Bpm { get; set; } = MetronomeTimeline.DefaultBpm;
    public int BeatsPerBar { get; set; } = 4;
    public int BeatUnit { get; set; } = 4;
    public string Sound { get; set; } = MetronomeSoundLibrary.DefaultName;
    /// <summary>0 to 100 %. An addition to Ed's list: a click needs a level.</summary>
    public int Volume { get; set; } = DefaultVolume;
    public MetronomeHearIn HearIn { get; set; } = MetronomeHearIn.RemSound;
    /// <summary>With "In RemSound": the click is recorded too (a track of its own when recording split). Off unless chosen.</summary>
    public bool IncludeInRecordings { get; set; }
    public MetronomeMasterAnswer WhenAsked { get; set; } = MetronomeMasterAnswer.Ask;

    public MetronomeSignature Signature => new((byte)Math.Clamp(BeatsPerBar, 1, 32), (byte)(BeatUnit is 1 or 2 or 4 or 8 or 16 or 32 ? BeatUnit : 4));

    public int ClampedBpm => Math.Clamp(Bpm, MetronomeTimeline.MinBpm, MetronomeTimeline.MaxBpm);

    public float Gain => Math.Clamp(Volume, 0, 100) / 100f;

    public MetronomeSettings Clone() => (MetronomeSettings)MemberwiseClone();

    public bool SameAs(MetronomeSettings o) =>
        Bpm == o.Bpm && BeatsPerBar == o.BeatsPerBar && BeatUnit == o.BeatUnit && string.Equals(Sound, o.Sound, StringComparison.Ordinal)
        && Volume == o.Volume && HearIn == o.HearIn && IncludeInRecordings == o.IncludeInRecordings && WhenAsked == o.WhenAsked;

    public static string HearName(MetronomeHearIn h) => h == MetronomeHearIn.Daw ? "in the DAW" : "in RemSound";

    public string Describe() =>
        $"{ClampedBpm} bpm {Signature}, sound {Sound}, volume {Math.Clamp(Volume, 0, 100)} %, heard {HearName(HearIn)}, "
        + $"recorded {(IncludeInRecordings && HearIn == MetronomeHearIn.RemSound ? "yes" : "no")}, "
        + $"a master is {(WhenAsked == MetronomeMasterAnswer.AcceptAutomatically ? "accepted automatically" : "asked about")}";
}

public enum MetronomeRole
{
    /// <summary>Your own metronome, on your own clock.</summary>
    Solo,
    /// <summary>You lead: your Start and Stop start and stop everyone who accepted.</summary>
    Master,
    /// <summary>You follow someone else's: their tempo and signature, your own sound.</summary>
    Follower,
}

/// <summary>Somebody asking to be the master metronome.</summary>
public sealed record MetronomeOffer(ulong SessionId, IPEndPoint From, string MasterName);

/// <summary>
/// THE METRONOME IN THE DAW (PLAN-metronome-2026-09-28, "Timing" 4): with "In the DAW" chosen, "Metronome" is one more entry
/// in the RemSound plugin's peer list, at an address no peer can have - 0.0.0.1, in 0.0.0.0/8, which nothing routes - and
/// refused wherever a real peer's address is taken (<see cref="PeerAddress"/>). A track that claims it gets the clicks in
/// its blocks, placed by the plugin's playback schedule: it plays each block's reply a lead of blocks after asking for it
/// (<see cref="PluginBridgeClient.DefaultLeadBlocks"/>), so a block read now is heard about that many blocks from now, plus
/// the DAW's own output delay, which RemSound cannot know - about one DAW block of accuracy. A plugin of v6.0 sees one more
/// name in its list and needs nothing new.
/// </summary>
public static class MetronomeDaw
{
    public static readonly IPAddress PseudoPeer = new([0, 0, 0, 1]);
    public const string Name = "Metronome";

    public static bool IsPseudoPeer(IPAddress? address) => address is not null && address.Equals(PseudoPeer);

    /// <summary>From a block being read for a DAW track to it being played: the plugin's lead of blocks of this size.</summary>
    public static double PlayDelayUs(int frames) => PluginBridgeClient.DefaultLeadBlocks * frames * 1e6 / PluginBridgeProtocol.WireSampleRate;
}

/// <summary>
/// THE METRONOME ITSELF (PLAN-metronome-2026-09-28): the solo metronome, the master and the follower, and the messages
/// between them. The window owns one; everything here runs on the window's thread except <see cref="TryAnswerSync"/>,
/// which the network thread calls so a clock reading is answered the moment it arrives. It sends nothing itself (the window
/// seals and sends what <see cref="Send"/> is given) and plays nothing itself: it publishes a
/// <see cref="MetronomeSchedule"/> for the outputs to place clicks from.
///
/// <para>Master: ticking "Be the master metronome" offers to everyone connected, again every 2 s for 10 s to reach late or
/// lossy peers; whoever accepts gets the timeline on every change (three times, 50 ms apart) and every second, and has its
/// clock readings answered. Only they ever get anything but the offer, so a peer that does not know type 12 - an older
/// RemSound, a phone - sees one offer and nothing more. Follower: accepts or declines (asking first unless set to accept),
/// reads the master's clock every 250 ms for 3 s and every second after, plays the master's timeline on its own sound once
/// four readings are in, and stops following after 5 s with no timeline or when the master lets go.</para>
/// </summary>
public sealed class MetronomeEngine
{
    public const long OfferRepeatUs = 2_000_000;
    public const long OfferWindowUs = 10_000_000;
    public const long StateRepeatUs = 50_000;
    public const int StateRepeats = 3;
    public const long StateEveryUs = 1_000_000;
    public const long SyncFastUs = 250_000;
    public const long SyncFastForUs = 3_000_000;
    public const long SyncSlowUs = 1_000_000;
    public const long FollowerTimeoutUs = 5_000_000;
    public const long EstimateLogEveryUs = 60_000_000;

    private readonly Func<long> clock;
    private readonly Action<string> log;

    private MetronomeTimeline own = MetronomeTimeline.Stopped();
    private MetronomeTimeline? ownPrevious;
    private long runId;
    private MetronomeSettings settings = new();
    private MetronomeSound? sound;

    // Master.
    private ulong masterSession;
    private string myName = "";
    private readonly Dictionary<IPAddress, OfferState> offered = [];
    private volatile IPAddress[] followerAddresses = [];
    private readonly Dictionary<IPAddress, IPEndPoint> followers = [];
    private long nextStateUs;
    private readonly List<(long DueUs, MetronomeMessage Message, IPEndPoint To)> pending = [];

    // Follower.
    private ulong followSession;
    private IPEndPoint? masterEndpoint;
    private string masterName = "";
    private MetronomeTimeline? masterTimeline;
    private MetronomeTimeline? masterPrevious;
    private long followingSinceUs;
    private long lastStateUs;
    private long nextSyncUs;
    private uint syncSeq;
    private readonly Dictionary<uint, long> outstanding = [];
    private bool estimateLogged;
    private long nextEstimateLogUs;
    private readonly HashSet<(ulong, IPAddress)> answered = [];
    private readonly HashSet<(ulong, IPAddress)> declined = [];
    private MetronomeOffer? offerBeingAsked;

    private sealed class OfferState
    {
        public required IPEndPoint Endpoint;
        public required string Name;
        public long FirstUs;
        public long LastUs;
        public bool Answered;
        public bool Accepted;
        public bool Noted;
        /// <summary>Offers sent so far: one at once, then one on each 2 s of the window (six in all).</summary>
        public int Offers = 1;
    }

    public MetronomeEngine(Func<long>? clock = null, Action<string>? log = null)
    {
        this.clock = clock ?? MetronomeClock.NowUs;
        this.log = log ?? (_ => { });
    }

    /// <summary>Send a message to a peer. The window seals it with the password and sends it on the audio socket.</summary>
    public Action<MetronomeMessage, IPEndPoint>? Send { get; set; }
    /// <summary>What the outputs place clicks from (null: nothing to play).</summary>
    public Action<MetronomeSchedule?>? SchedulePublished { get; set; }
    /// <summary>Something the menu shows changed: the role, playing or not, muted or not, who leads.</summary>
    public Action? Changed { get; set; }
    /// <summary>Who is connected, to offer to: each peer's endpoint and name.</summary>
    public Func<IReadOnlyList<(IPEndPoint Endpoint, string Name)>>? Peers { get; set; }
    /// <summary>Is this peer reached through a server? (The server passes the metronome on only once it is updated.)</summary>
    public Func<IPEndPoint, bool>? ThroughServer { get; set; }
    /// <summary>Somebody asks to lead and the setting is "Ask me": the window asks, then calls <see cref="AnswerOffer"/>.</summary>
    public Action<MetronomeOffer>? AskAboutOffer { get; set; }
    /// <summary>A follower stopped following, with the reason in words (the window says it).</summary>
    public Action<string>? StoppedFollowing { get; set; }
    /// <summary>What the master is told of its offer, each once: someone following, someone saying no, someone never
    /// answering (Ed, 2026-09-29, the live test: "said I was master but he never got a prompt and never heard the
    /// metronome" - it needs to go both ways). The window says it and puts it in the status.</summary>
    public Action<string>? MasterNotice { get; set; }

    public MetronomeClockEstimator Estimator { get; } = new();

    // ---------------------------------------------------------------------------------------------------------------
    // The server's metronome (2026-09-29, PLAN-metronome-server-2026-09-29.md; Ed: "the metronome has to be per server").
    // Through a server, the server keeps the beat and sends each machine its own grid, early by exactly how long that
    // machine's sound takes to reach the server - so every note played on its click reaches the server on the beat. Every
    // click through a server, the master's own too, comes from this machine's grid. The master sends the server its setting
    // (and repeats it each second); asking to lead and answering stay the sealed messages, passed on by the server.

    /// <summary>A grid older than this is gone: the server stopped sending, or we left it.</summary>
    public const long ServerGridStaleUs = 3_000_000;
    /// <summary>A master whose claim the server keeps refusing - somebody else leads it and did not hand over - lets go.</summary>
    public const long ServerClaimGiveUpUs = 6_000_000;
    private const long ServerRepeatUs = 1_000_000;
    private ServerGrid? serverGrid;
    private MetronomeTimeline? serverPrevious;
    private long lastGridUs;
    private long claimSinceUs;
    private long nextServerSendUs;

    /// <summary>Connected through a server: the metronome is the server's. The window knows the relay.</summary>
    public Func<bool>? OnServerNow { get; set; }
    /// <summary>A message for the server's metronome (ServerMetronomeWire); the window sends it to the relay.</summary>
    public Action<byte[]>? ToServer { get; set; }
    /// <summary>The name of a server member by its client id, for the words (null: not known).</summary>
    public Func<Guid, string?>? ServerMemberName { get; set; }

    public bool OnServer => OnServerNow?.Invoke() == true;

    /// <summary>This machine's grid from the server, while it keeps coming.</summary>
    public ServerGrid? ServerGridNow => serverGrid is { } g && clock() - lastGridUs < ServerGridStaleUs ? g : null;

    /// <summary>The server's grid for this machine arrived (on the window's thread).</summary>
    public void OnServerGrid(ServerGrid grid)
    {
        var before = ServerGridNow;
        var changed = before is null || before.Timeline.Version != grid.Timeline.Version;
        if (changed)
        {
            serverPrevious = before is { Running: true } b && grid.Running && grid.Timeline.AnchorBeat > b.Timeline.AnchorBeat ? b.Timeline : null;
            if (grid.Running && before is not { Running: true }) runId++;
        }
        serverGrid = grid;
        lastGridUs = clock();
        if (changed || before!.Measured != grid.Measured || before.YouLead != grid.YouLead)
            log($"metronome: the server's grid - {grid.Timeline.Describe()}{(grid.Measured ? "" : ", this machine's delay not measured yet")}"
                + (grid.YouLead ? ", you lead it" : grid.Master == Guid.Empty ? ", nobody leads it" : $", {ServerMemberName?.Invoke(grid.Master) ?? "someone"} leads it"));
        if (grid.YouLead) claimSinceUs = 0;
        Publish();
        if (changed || before!.YouLead != grid.YouLead) Changed?.Invoke();
    }

    /// <summary>The master's setting, to the server: playing or not, tempo and signature.</summary>
    private void SendSetToServer()
    {
        if (Role != MetronomeRole.Master || !OnServer) return;
        ToServer?.Invoke(ServerMetronomeWire.BuildSet(own.Running, own.MilliBpm, own.BeatsPerBar, own.BeatUnit));
    }

    /// <summary>The master, once a second through a server: claim it until the server grants it, then repeat the setting.
    /// Refused for <see cref="ServerClaimGiveUpUs"/> - somebody else leads it and did not hand over - it lets go and says so.</summary>
    private void TickServerMaster(long now)
    {
        if (now < nextServerSendUs) return;
        nextServerSendUs = now + ServerRepeatUs;
        var grid = ServerGridNow;
        if (grid is { YouLead: true }) { SendSetToServer(); return; }
        ToServer?.Invoke(ServerMetronomeWire.BuildClaim());
        SendSetToServer();
        if (claimSinceUs == 0) { claimSinceUs = now; return; }
        if (now - claimSinceUs < ServerClaimGiveUpUs || grid is null || grid.Master == Guid.Empty) return;
        var who = ServerMemberName?.Invoke(grid.Master) ?? "Someone else";
        log($"metronome: the server's metronome stays with {who} - letting go of being the master metronome");
        MasterNotice?.Invoke($"{who} leads the server's metronome");
        claimSinceUs = 0;
        ReleaseMaster();
    }

    public long NowUs() => clock();

    public MetronomeRole Role => masterSession != 0 ? MetronomeRole.Master : followSession != 0 ? MetronomeRole.Follower : MetronomeRole.Solo;

    public MetronomeSettings Settings => settings.Clone();

    public MetronomeSound? Sound => sound;

    /// <summary>What plays: your own timeline, or the master's while you follow.</summary>
    public MetronomeTimeline Timeline => Role == MetronomeRole.Follower
        ? (OnServer ? ServerGridNow?.Timeline : masterTimeline) ?? MetronomeTimeline.Stopped()
        : own;

    /// <summary>Is a metronome running - yours, or the master's you follow (muted or not)?</summary>
    public bool Playing => Timeline.Running;

    /// <summary>Your own clicks silenced while you follow a master (you cannot stop theirs).</summary>
    public bool Muted { get; private set; }

    public string MasterName => masterName;
    public IPEndPoint? MasterEndpoint => masterEndpoint;
    public ulong SessionId => masterSession != 0 ? masterSession : followSession;
    public IReadOnlyCollection<IPEndPoint> Followers => followers.Values;
    public bool FollowerReady => Role == MetronomeRole.Follower && Estimator.Settled;

    /// <summary>Anything to do on the timer: a master, a follower, or messages still to go.</summary>
    public bool NeedsTicks => Role != MetronomeRole.Solo || pending.Count > 0;

    // ---------------------------------------------------------------------------------------------------------------
    // Settings and your own metronome

    /// <summary>New settings (and the sound they name, loaded). A tempo or signature change while playing lands on the next
    /// bar line; a master tells its followers.</summary>
    public void Configure(MetronomeSettings value, MetronomeSound? loadedSound)
    {
        settings = value.Clone();
        sound = loadedSound;
        var changed = own.Change(clock(), (uint)settings.ClampedBpm * 1000, settings.Signature);
        if (changed != own)
        {
            if (own.Running) ownPrevious = own;
            own = changed;
            if (Role == MetronomeRole.Master) { BroadcastState(); SendSetToServer(); }
        }
        Publish();
    }

    /// <summary>The Start/Stop item and the hotkey: start or stop your own (and, as master, everyone's), or - following a
    /// master - mute or unmute your clicks.</summary>
    public void StartStop()
    {
        if (Role == MetronomeRole.Follower) { SetMuted(!Muted); return; }
        if (own.Running) Stop(); else Start();
    }

    public void Start()
    {
        if (Role == MetronomeRole.Follower || own.Running) return;
        own = own.Start(clock());
        ownPrevious = null;
        runId++;
        log($"metronome: started, {own.Describe()}{(Role == MetronomeRole.Master ? $", leading {followers.Count} follower(s)" : "")}");
        if (Role == MetronomeRole.Master) { BroadcastState(); SendSetToServer(); }
        Publish();
        Changed?.Invoke();
    }

    public void Stop()
    {
        if (Role == MetronomeRole.Follower || !own.Running) return;
        own = own.Stop();
        ownPrevious = null;
        log($"metronome: stopped{(Role == MetronomeRole.Master ? $", for {followers.Count} follower(s) too" : "")}");
        if (Role == MetronomeRole.Master) { BroadcastState(); SendSetToServer(); }
        Publish();
        Changed?.Invoke();
    }

    public void SetMuted(bool muted)
    {
        if (Role != MetronomeRole.Follower || Muted == muted) return;
        Muted = muted;
        log($"metronome: {(muted ? "muted" : "unmuted")} - {masterName}'s master metronome {(Playing ? "plays on" : "is stopped")}");
        Publish();
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Master

    /// <summary>Tick "Be the master metronome": a new session, and an offer to everyone connected.</summary>
    public void BecomeMaster(string name)
    {
        if (Role == MetronomeRole.Master) return;
        if (Role == MetronomeRole.Follower) StopFollowing("you became the master metronome", sendLeave: true);
        masterSession = MetronomeWire.NewSessionId();
        myName = name;
        offered.Clear();
        followers.Clear();
        followerAddresses = [];
        log($"metronome: you are the master metronome (session {masterSession:x16}); offering to everyone connected"
            + (OnServer ? ", and claiming the server's metronome" : ""));
        OfferToNewPeers();
        if (OnServer)
        {
            claimSinceUs = 0;
            nextServerSendUs = 0;
            TickServerMaster(clock());
        }
        Changed?.Invoke();
    }

    /// <summary>Untick it: everyone following is let go, and your own metronome carries on as it was.</summary>
    public void ReleaseMaster()
    {
        if (Role != MetronomeRole.Master) return;
        foreach (var to in followers.Values) SendNow(new MetronomeMessage(MetronomeKind.Release, masterSession), to);
        if (OnServer) ToServer?.Invoke(ServerMetronomeWire.BuildRelease());
        log($"metronome: no longer the master metronome; {followers.Count} follower(s) let go");
        masterSession = 0;
        offered.Clear();
        followers.Clear();
        followerAddresses = [];
        pending.Clear();
        Changed?.Invoke();
    }

    private void OfferToNewPeers()
    {
        if (Role != MetronomeRole.Master || Peers is null) return;
        var now = clock();
        foreach (var (endpoint, name) in Peers())
        {
            if (offered.ContainsKey(endpoint.Address)) continue;
            offered[endpoint.Address] = new OfferState { Endpoint = endpoint, Name = name, FirstUs = now, LastUs = now };
            SendNow(new MetronomeMessage(MetronomeKind.Offer, masterSession) { MasterName = myName }, endpoint);
            log($"metronome: offered to lead {name} at {endpoint}");
        }
    }

    private void BroadcastState()
    {
        if (Role != MetronomeRole.Master) return;
        var now = clock();
        foreach (var to in followers.Values) QueueState(to, now, StateRepeats);
        nextStateUs = now + StateEveryUs;
    }

    private void QueueState(IPEndPoint to, long now, int times)
    {
        for (var i = 0; i < times; i++)
            pending.Add((now + i * StateRepeatUs, new MetronomeMessage(MetronomeKind.State, masterSession) { State = own }, to));
        SendDue(now);
    }

    /// <summary>The network thread's half: a follower's clock reading, answered at once with the stamp taken as it was
    /// read. Null when it is not from one of this master's followers in this session. The window stamps t2 and sends.</summary>
    public MetronomeMessage? TryAnswerSync(MetronomeMessage request, IPEndPoint from, long t1)
    {
        var session = Volatile.Read(ref masterSession);
        if (request.Kind != MetronomeKind.SyncRequest || session == 0 || request.SessionId != session) return null;
        if (Array.IndexOf(followerAddresses, from.Address) < 0) return null;
        return new MetronomeMessage(MetronomeKind.SyncReply, session) { Seq = request.Seq, T0 = request.T0, T1 = t1 };
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Follower

    /// <summary>The answer to an offer (the person's, or automatic).</summary>
    public void AnswerOffer(MetronomeOffer offer, bool accept)
    {
        if (offerBeingAsked == offer) offerBeingAsked = null;
        answered.Add((offer.SessionId, offer.From.Address));
        if (!accept)
        {
            declined.Add((offer.SessionId, offer.From.Address));
            SendNow(new MetronomeMessage(MetronomeKind.Decline, offer.SessionId), offer.From);
            log($"metronome: declined {offer.MasterName}'s offer to lead");
            return;
        }
        if (Role == MetronomeRole.Master)
        {
            // Handing over: whoever follows you is let go, and you follow the new master.
            log($"metronome: handing over to {offer.MasterName}");
            ReleaseMaster();
        }
        if (Role == MetronomeRole.Follower) StopFollowing($"you accepted {offer.MasterName} instead", sendLeave: true);
        if (own.Running) { own = own.Stop(); log("metronome: your own metronome stopped - following a master"); }
        followSession = offer.SessionId;
        masterEndpoint = offer.From;
        masterName = offer.MasterName;
        masterTimeline = null;
        masterPrevious = null;
        Muted = false;
        Estimator.Reset();
        outstanding.Clear();
        estimateLogged = false;
        var now = clock();
        followingSinceUs = lastStateUs = nextSyncUs = now;
        SendNow(new MetronomeMessage(MetronomeKind.Accept, followSession), offer.From);
        log($"metronome: following {offer.MasterName}'s master metronome at {offer.From} (session {followSession:x16})");
        SendSyncIfDue(now);
        Publish();
        Changed?.Invoke();
    }

    /// <summary>Stop following: the clicks stop, and the master is told unless it is the one that went.</summary>
    public void StopFollowing(string why, bool sendLeave)
    {
        if (Role != MetronomeRole.Follower) return;
        if (sendLeave && masterEndpoint is not null) SendNow(new MetronomeMessage(MetronomeKind.Leave, followSession), masterEndpoint);
        log($"metronome: stopped following {masterName}'s master metronome - {why}");
        var who = masterName;
        followSession = 0;
        masterEndpoint = null;
        masterTimeline = null;
        masterPrevious = null;
        Muted = false;
        outstanding.Clear();
        Publish();
        Changed?.Invoke();
        StoppedFollowing?.Invoke($"Stopped following {who}'s master metronome: {why}.");
    }

    private void SendSyncIfDue(long now)
    {
        if (Role != MetronomeRole.Follower || masterEndpoint is null || now < nextSyncUs) return;
        nextSyncUs = now + (now - followingSinceUs < SyncFastForUs ? SyncFastUs : SyncSlowUs);
        foreach (var old in outstanding.Where(kv => now - kv.Value > 2 * SyncSlowUs).Select(kv => kv.Key).ToList()) outstanding.Remove(old);
        var seq = ++syncSeq;
        var t0 = clock();
        outstanding[seq] = t0;
        SendNow(new MetronomeMessage(MetronomeKind.SyncRequest, followSession) { Seq = seq, T0 = t0 }, masterEndpoint);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Messages

    /// <summary>A message that opened with the password, on the window's thread, with the moment it was read.</summary>
    public void OnMessage(MetronomeMessage m, IPEndPoint from, long receivedUs)
    {
        switch (m.Kind)
        {
            case MetronomeKind.Offer:
                OnOffer(new MetronomeOffer(m.SessionId, from, string.IsNullOrWhiteSpace(m.MasterName) ? from.Address.ToString() : m.MasterName));
                break;
            case MetronomeKind.Accept or MetronomeKind.Decline:
                if (Role != MetronomeRole.Master || m.SessionId != masterSession) return;
                OnAnswer(from, m.Kind == MetronomeKind.Accept);
                break;
            case MetronomeKind.Leave:
                if (Role == MetronomeRole.Master && m.SessionId == masterSession && followers.Remove(from.Address))
                {
                    followerAddresses = [.. followers.Keys];
                    log($"metronome: {NameOf(from)} stopped following");
                    Changed?.Invoke();
                }
                break;
            case MetronomeKind.Release:
                if (Role == MetronomeRole.Follower && m.SessionId == followSession && from.Address.Equals(masterEndpoint?.Address))
                    StopFollowing($"{masterName} is no longer the master metronome", sendLeave: false);
                break;
            case MetronomeKind.State:
                if (Role == MetronomeRole.Follower && m.SessionId == followSession && from.Address.Equals(masterEndpoint?.Address) && m.State is { } s)
                    OnState(s, from, receivedUs);
                break;
            case MetronomeKind.SyncReply:
                if (Role != MetronomeRole.Follower || m.SessionId != followSession || !outstanding.Remove(m.Seq, out var t0) || t0 != m.T0) return;
                OnSyncReply(t0, m.T1, m.T2, receivedUs);
                break;
        }
    }

    private void OnOffer(MetronomeOffer offer)
    {
        var key = (offer.SessionId, offer.From.Address);
        if (Role == MetronomeRole.Follower && offer.SessionId == followSession && offer.From.Address.Equals(masterEndpoint?.Address))
        {
            SendNow(new MetronomeMessage(MetronomeKind.Accept, followSession), offer.From);   // our accept was lost: again
            return;
        }
        if (declined.Contains(key)) { SendNow(new MetronomeMessage(MetronomeKind.Decline, offer.SessionId), offer.From); return; }
        if (answered.Contains(key) || offerBeingAsked is { } asking && asking.SessionId == offer.SessionId) return;
        if (Role == MetronomeRole.Master)
        {
            // Someone else wants to lead while you do. Handing over is yours to choose, whatever the settings say for other
            // offers - it used to be declined here at once, unasked, and the other side was left thinking it led (the live
            // test, 2026-09-29). With nobody to ask (no window), it is declined as before.
            if (AskAboutOffer is not { } askMaster) { AnswerOffer(offer, accept: false); return; }
            log($"metronome: {offer.MasterName} at {offer.From} asks to take over as the master metronome (asking)");
            offerBeingAsked = offer;
            askMaster(offer);
            return;
        }
        log($"metronome: {offer.MasterName} at {offer.From} asks to be the master metronome ({(settings.WhenAsked == MetronomeMasterAnswer.AcceptAutomatically ? "accepting automatically" : "asking")})");
        if (settings.WhenAsked == MetronomeMasterAnswer.AcceptAutomatically) { AnswerOffer(offer, accept: true); return; }
        offerBeingAsked = offer;
        if (AskAboutOffer is { } ask) ask(offer);
    }

    /// <summary>The window could not ask (a question already open, or nobody there): it may be asked again at the next offer.</summary>
    public void OfferNotAsked(MetronomeOffer offer)
    {
        if (offerBeingAsked == offer) offerBeingAsked = null;
    }

    private void OnAnswer(IPEndPoint from, bool accept)
    {
        if (!offered.TryGetValue(from.Address, out var o))
        {
            o = new OfferState { Endpoint = from, Name = from.Address.ToString(), FirstUs = clock(), LastUs = clock() };
            offered[from.Address] = o;
        }
        var wasFollower = followers.ContainsKey(from.Address);
        o.Answered = true;
        o.Accepted = accept;
        if (accept)
        {
            followers[from.Address] = from;
            followerAddresses = [.. followers.Keys];
            if (!wasFollower)
            {
                log($"metronome: {o.Name} accepted - following you ({followers.Count} follower(s))");
                MasterNotice?.Invoke($"{o.Name} is following your metronome");
            }
            var now = clock();
            QueueState(from, now, StateRepeats);
            // The three just queued are its first second's worth; everyone's next is a second from now at the soonest.
            nextStateUs = Math.Max(nextStateUs, now + StateEveryUs);
        }
        else
        {
            if (followers.Remove(from.Address)) followerAddresses = [.. followers.Keys];
            log($"metronome: {o.Name} declined to follow you");
            MasterNotice?.Invoke($"{o.Name} said no to following your metronome");
        }
        Changed?.Invoke();
    }

    private string NameOf(IPEndPoint ep) => offered.TryGetValue(ep.Address, out var o) ? o.Name : ep.Address.ToString();

    private void OnState(MetronomeTimeline s, IPEndPoint from, long receivedUs)
    {
        lastStateUs = receivedUs;
        masterEndpoint = from;
        if (!s.IsValid) return;
        if (masterTimeline is { } current && s.Version <= current.Version) return;
        var before = masterTimeline;
        if (before is { Running: true } && s.Running && s.AnchorBeat > before.AnchorBeat) masterPrevious = before;
        else masterPrevious = null;
        if (s.Running && before is not { Running: true }) runId++;
        masterTimeline = s;
        log($"metronome: {masterName}'s timeline - {s.Describe()}");
        Publish();
        if (before?.Running != s.Running) Changed?.Invoke();
    }

    private void OnSyncReply(long t0, long t1, long t2, long t3)
    {
        var wasSettled = Estimator.Settled;
        if (!Estimator.Add(t0, t1, t2, t3)) return;
        if (!wasSettled && Estimator.Settled || (Estimator.Settled && !estimateLogged))
        {
            estimateLogged = true;
            nextEstimateLogUs = t3 + EstimateLogEveryUs;
            log($"metronome: clock settled against {masterName}'s - {Estimator.Describe()}");
        }
        else if (Estimator.Settled && t3 >= nextEstimateLogUs)
        {
            nextEstimateLogUs = t3 + EstimateLogEveryUs;
            log($"metronome: clock against {masterName}'s - {Estimator.Describe()}");
        }
        if (Estimator.Settled) Publish();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The timer

    /// <summary>Every ~50 ms while <see cref="NeedsTicks"/>: messages due, offers to repeat, the timeline every second,
    /// clock readings, and the follower's timeout.</summary>
    public void Tick()
    {
        var now = clock();
        SendDue(now);
        if (Role == MetronomeRole.Master && OnServer) TickServerMaster(now);
        if (Role == MetronomeRole.Master)
        {
            OfferToNewPeers();
            foreach (var o in offered.Values)
            {
                if (o.Answered) continue;
                // On a fixed grid from the first offer, so a late timer never loses one: at 2, 4, 6, 8 and 10 s.
                if (o.Offers <= OfferWindowUs / OfferRepeatUs)
                {
                    if (now >= o.FirstUs + o.Offers * OfferRepeatUs)
                    {
                        o.Offers++;
                        o.LastUs = now;
                        SendNow(new MetronomeMessage(MetronomeKind.Offer, masterSession) { MasterName = myName }, o.Endpoint);
                    }
                }
                // Given the time an answer to the last one would take, it is noted once.
                else if (!o.Noted && now - o.FirstUs > OfferWindowUs + OfferRepeatUs)
                {
                    o.Noted = true;
                    var server = ThroughServer?.Invoke(o.Endpoint) == true;
                    log(server
                        ? $"metronome: {o.Name} never answered - they are reached through a server, which does not pass the metronome on yet; it works on direct connections only"
                        : $"metronome: {o.Name} never answered the offer (an older RemSound, a phone, or not there)");
                    MasterNotice?.Invoke(server
                        ? $"{o.Name} did not answer: the metronome does not pass through a server yet"
                        : $"{o.Name} did not answer the metronome offer");
                }
            }
            if (now >= nextStateUs && followers.Count > 0)
            {
                foreach (var to in followers.Values) SendNow(new MetronomeMessage(MetronomeKind.State, masterSession) { State = own }, to);
                nextStateUs = now + StateEveryUs;
            }
        }
        else if (Role == MetronomeRole.Follower && OnServer)
        {
            // Through a server the grid carries the beat and the clocks: no readings to take. It keeps coming each second.
            if (now - lastGridUs > FollowerTimeoutUs && now - followingSinceUs > FollowerTimeoutUs)
                StopFollowing($"nothing heard from the server's metronome for {FollowerTimeoutUs / 1_000_000} seconds", sendLeave: true);
        }
        else if (Role == MetronomeRole.Follower)
        {
            SendSyncIfDue(now);
            if (now - lastStateUs > FollowerTimeoutUs)
                StopFollowing($"nothing heard from {masterName} for {FollowerTimeoutUs / 1_000_000} seconds", sendLeave: true);
        }
    }

    private void SendDue(long now)
    {
        for (var i = 0; i < pending.Count;)
        {
            if (pending[i].DueUs > now) { i++; continue; }
            var (_, message, to) = pending[i];
            pending.RemoveAt(i);
            Send?.Invoke(message, to);
        }
    }

    private void SendNow(MetronomeMessage message, IPEndPoint to) => Send?.Invoke(message, to);

    // ---------------------------------------------------------------------------------------------------------------
    // What the outputs play

    /// <summary>The schedule as things stand, and published.</summary>
    public MetronomeSchedule? CurrentSchedule { get; private set; }

    public void Publish()
    {
        var grid = ServerGridNow;
        CurrentSchedule = OnServer && Role != MetronomeRole.Solo
            ? (grid is { Measured: true } g
                ? new MetronomeSchedule(g.Timeline, serverPrevious, MetronomeClockMap.Identity, sound, settings.Gain, Role == MetronomeRole.Follower && Muted, runId, clock)
                : null)
            : Role switch
        {
            MetronomeRole.Follower when masterTimeline is { } t && Estimator.Settled =>
                new MetronomeSchedule(t, masterPrevious, Estimator.Map, sound, settings.Gain, Muted, runId, clock),
            MetronomeRole.Follower => null,
            _ => own.Running || ownPrevious is not null
                ? new MetronomeSchedule(own, ownPrevious, MetronomeClockMap.Identity, sound, settings.Gain, false, runId, clock)
                : null,
        };
        SchedulePublished?.Invoke(CurrentSchedule);
    }

    /// <summary>"; B and C said no", from the offers so far - nothing when there are none.</summary>
    private string Names(Func<OfferState, bool> which, string what)
    {
        var names = offered.Values.Where(which).Select(o => o.Name).ToList();
        return names.Count == 0 ? "" : $"; {string.Join(" and ", names)} {what}";
    }

    /// <summary>One line for the log and the status: what the metronome is doing.</summary>
    public string Describe() => Role switch
    {
        MetronomeRole.Follower => $"following {masterName}'s master metronome{(OnServer ? " through the server" : "")}, {(Playing ? $"playing {Timeline.Bpm:0.###} bpm {Timeline.Signature}" : "stopped")}"
                                  + (Muted ? ", muted" : "")
                                  + (OnServer ? (ServerGridNow is { Measured: true } ? "" : ", timing this computer") : Estimator.Settled ? "" : ", clock not settled yet"),
        MetronomeRole.Master => $"master metronome{(OnServer ? " through the server" : "")} for {followers.Count} follower(s), {(own.Running ? $"playing {own.Bpm:0.###} bpm {own.Signature}" : "stopped")}"
                                + (OnServer && ServerGridNow is not { YouLead: true } ? ", waiting for the server" : "")
                                + Names(o => o.Answered && !o.Accepted, "said no") + Names(o => o.Noted && !o.Answered, "did not answer"),
        _ => own.Running ? $"playing {own.Bpm:0.###} bpm {own.Signature}" : "stopped",
    };
}
