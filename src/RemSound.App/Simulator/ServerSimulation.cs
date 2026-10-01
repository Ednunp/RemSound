using System.Diagnostics;
using System.Net;
using RemSound.Core;
using RemSound.Receiver;
using RemSound.Sender;

namespace RemSound.App.Simulator;

/// <summary>One person sending through the server: their tone, the network from them to the server, their sound card, and
/// whether they have ticked the listener.</summary>
internal sealed record ServerSender(string Name, double ToneHz, SimNetwork ToServer, SimCard? Card = null, bool TicksListener = true);

/// <summary>Several people playing to one listener through a server.</summary>
/// <param name="FromServer">The network from the server to the listener.</param>
/// <param name="ServerDelayMs">How long the server holds a packet (the Pi measured 0.07 ms).</param>
/// <param name="ServerLatePercent">...and how often it is late passing one on (a busy server), by up to <paramref name="ServerLateMs"/>.</param>
/// <param name="ListenerTicks">Which senders the listener has ticked, by place in the list; null is all of them.</param>
internal sealed record ServerScenario(string Name, IReadOnlyList<ServerSender> Senders, SimNetwork FromServer, int BufferMs, double Seconds = 20,
    SimCodec Codec = SimCodec.Opus2_5, double ServerDelayMs = 0.07, double ServerLatePercent = 0, double ServerLateMs = 0, int Seed = 1,
    IReadOnlyList<int>? ListenerTicks = null, SimCard? ListenerCard = null, double AnalyseFromSecond = 1);

/// <summary>One sender as the listener heard them: whether at all, how loud, and every break in their tone.</summary>
internal sealed record ServerSenderResult(string Name, double ToneHz, bool Heard, double Level, int BreakUps, int QuietMs, string Address);

/// <param name="OrdinarySound">Sound that reached the server in the ordinary one-to-one form rather than group-framed:
/// the real server would take it for a pair's. None, with nobody paired.</param>
internal sealed record ServerResult(ServerScenario Scenario, IReadOnlyList<ServerSenderResult> Senders, SimAnalysis.Findings Clicks, long Underruns,
    long Forwarded, long Refused, long SessionsOpened, double RealSeconds, long OrdinarySound = 0);

/// <summary>
/// THE SERVER HOP (2026-09-30, the simulator review's sixth improvement). Several real senders and one real receiver, each
/// with its own real <see cref="RelayGroupClient"/>, and between them a server that does to sound what
/// server/remsound-relay.py does: a group-framed packet from a member goes, unchanged, to every other member of the same
/// group who has ticked the sender and whom the sender has ticked, after the server's own delay; the member list goes to
/// everyone whenever somebody says hello. Two networks each way: sender to server, server to listener. The senders frame,
/// the listener unframes and credits each sender to the address its group client gives them, and the receiver mixes
/// them - all the app's own code. Each sender plays its own tone (multiples of 100 Hz, so each can be heard apart in the
/// mix); the first also sends the clicks that time the delay. Virtual time, one thread, as <see cref="Simulation"/>.
/// </summary>
internal static class ServerSimulation
{
    public const float ToneLevel = 0.25f;
    private static readonly IPEndPoint ServerAt = new(IPAddress.Parse("10.77.0.100"), RemPacket.DefaultPort);

    // The relay's own packet types (server/remsound-relay.py): what it passes on, and what it reads itself.
    private static readonly HashSet<byte> Forwardable = [1, 2, 3, 4, 5, 12];

    private sealed class Member
    {
        public required Guid Id;
        public required string Name;
        public required RelayGroupClient Group;
        public AudioReceiver? Receiver;
        public required SimNetwork FromServer;
        public required Random Dice;
        public byte[] Tag = [];
        public HashSet<Guid>? Ticked;   // null: no list, which the relay reads as everyone
        public double LastArrival;
    }

    public static ServerResult Run(ServerScenario s)
    {
        var wall = Stopwatch.StartNew();
        double now = 0;
        var baseTicks = Stopwatch.GetTimestamp();
        AudioClock.UseSimulated(() => baseTicks + (long)(now * Stopwatch.Frequency));
        var queue = new PriorityQueue<Action, (double, long)>();
        long order = 0;
        void At(double t, Action a) => queue.Enqueue(a, (t, order++));

        JamulusStyle.Reset();
        CustomOpusPeers.Clear();
        var diagnosticsWere = DiagnosticsGate.Enabled;
        DiagnosticsGate.Enabled = false;
        SessionPlayout.ConcealSeedForTest = s.Seed;
        var (key, fingerprint) = RemSoundCrypto.ForPlainPassword("the band");
        var disposables = new List<IDisposable>();
        var relayGate = new object();
        var members = new List<Member>();
        long forwarded = 0, refused = 0, ordinarySound = 0;
        try
        {
            // ---- the server ----
            byte[] Roster(Member to)
            {
                // The member list as the relay builds it for one member: everyone in their group, each with whether they
                // have ticked the one it is going to, then the list's own flags.
                var listed = members.Where(m => m.Tag.AsSpan().SequenceEqual(to.Tag)).ToList();
                var packet = new byte[RelayGroupClient.GroupHeaderSize + 1 + listed.Count * RelayGroupClient.RosterEntryBytes + 1];
                RemPacket.WriteHeader(packet, (RemPacketType)RelayGroupClient.TypeRoster, 0, 0);
                packet[4] = RelayGroupClient.GroupVersion;
                var at = RelayGroupClient.GroupHeaderSize;
                packet[at++] = (byte)listed.Count;
                foreach (var m in listed)
                {
                    m.Id.TryWriteBytes(packet.AsSpan(at, RelayGroupClient.ClientIdSize), bigEndian: true, out _);
                    RelayGroupClient.EncodeName(m.Name, packet.AsSpan(at + RelayGroupClient.ClientIdSize, RelayGroupClient.NameBytes));
                    packet[at + RelayGroupClient.RosterEntryBytes - 1] = m.Ticked is null || m.Ticked.Contains(to.Id) ? RelayGroupClient.RosterMemberFlagTicksUs : (byte)0;
                    at += RelayGroupClient.RosterEntryBytes;
                }
                return packet;
            }
            void ToMember(Member m, byte[] packet)
            {
                var length = packet.Length;
                if (m.Group.HandleInbound(packet, ref length, ServerAt, out var source) != RelayInbound.Delivered || source is null) return;
                m.Receiver?.InjectExternalPacket(packet, length, source);
            }
            // A hello (the control side: said at once, whatever the networks are doing - and from the group clients' own
            // timers in a long run, so under a lock).
            void Hello(byte[] data, int length)
            {
                if (length < RelayGroupClient.GroupHeaderSize + RelayGroupClient.NameBytes + RelayGroupClient.GroupTagBytes || data[4] != RelayGroupClient.GroupVersion) return;
                var id = new Guid(data.AsSpan(RemPacket.HeaderSize, RelayGroupClient.ClientIdSize), bigEndian: true);
                List<(Member, byte[])> lists;
                lock (relayGate)
                {
                    var from = members.FirstOrDefault(m => m.Id == id);
                    if (from is null || data[5] != RelayGroupClient.TypeHello) return;
                    var body = data.AsSpan(RelayGroupClient.GroupHeaderSize, length - RelayGroupClient.GroupHeaderSize);
                    from.Tag = body.Slice(RelayGroupClient.NameBytes, RelayGroupClient.GroupTagBytes).ToArray();
                    var listAt = RelayGroupClient.NameBytes + RelayGroupClient.GroupTagBytes;
                    if (body.Length > listAt)
                    {
                        var ticked = new HashSet<Guid>();
                        for (var i = 0; i < body[listAt] && listAt + 1 + (i + 1) * RelayGroupClient.ClientIdSize <= body.Length; i++)
                            ticked.Add(new Guid(body.Slice(listAt + 1 + i * RelayGroupClient.ClientIdSize, RelayGroupClient.ClientIdSize), bigEndian: true));
                        from.Ticked = ticked;
                    }
                    else from.Ticked = null;
                    lists = members.Where(m => m.Tag.Length > 0).Select(m => (m, Roster(m))).ToList();
                }
                foreach (var (m, roster) in lists) ToMember(m, roster);
            }
            // Sound (and the rest the relay passes on): to everyone in the group the two-way ticks allow.
            void Forward(byte[] data)
            {
                if (data.Length >= RemPacket.HeaderSize && data[4] == RemPacket.Version && data[5] is 1 or 2) ordinarySound++;
                if (data.Length < RelayGroupClient.GroupHeaderSize || data[4] != RelayGroupClient.GroupVersion || !Forwardable.Contains(data[5])) return;
                var id = new Guid(data.AsSpan(RemPacket.HeaderSize, RelayGroupClient.ClientIdSize), bigEndian: true);
                List<Member> targets;
                lock (relayGate)
                {
                    var from = members.FirstOrDefault(m => m.Id == id);
                    if (from is null) return;
                    targets = members.Where(o => o != from && o.Tag.AsSpan().SequenceEqual(from.Tag)).ToList();
                    var allowed = targets.Where(o => (from.Ticked is null || from.Ticked.Contains(o.Id)) && (o.Ticked is null || o.Ticked.Contains(from.Id))).ToList();
                    refused += targets.Count - allowed.Count;
                    targets = allowed;
                }
                foreach (var to in targets)
                {
                    forwarded++;
                    var held = s.ServerDelayMs / 1000;
                    if (s.ServerLatePercent > 0 && to.Dice.NextDouble() * 100 < s.ServerLatePercent) held += to.Dice.NextDouble() * s.ServerLateMs / 1000;
                    if (to.FromServer.LossPercent > 0 && to.Dice.NextDouble() * 100 < to.FromServer.LossPercent) continue;
                    var arrival = Math.Max(Simulation.Arrival(to.FromServer, now + held, to.Dice), to.LastArrival);
                    to.LastArrival = arrival;
                    var copy = (byte[])data.Clone();   // the listener's group client rewrites it in place
                    At(arrival, () => ToMember(to, copy));
                }
            }

            Member Join(string name, SimNetwork fromServer, int diceSeed, AudioSender? sender, AudioReceiver? receiver)
            {
                var id = new Guid(Enumerable.Range(0, 16).Select(i => (byte)(diceSeed * 31 + i * 7 + 1)).ToArray());
                var group = new RelayGroupClient(id);
                disposables.Add(group);
                var member = new Member { Id = id, Name = name, Group = group, Receiver = receiver, FromServer = fromServer, Dice = new Random(s.Seed * 1000 + diceSeed) };
                lock (relayGate) members.Add(member);
                group.SetIdentity(name, fingerprint);
                group.Connect(ServerAt);
                if (sender is not null) sender.RelayRouter = group;
                if (receiver is not null) receiver.RelayOfMember = group.RelayOf;
                group.Start((data, length, _) => { Hello(data, length); return true; });
                return member;
            }

            // ---- the listener: ASIO only, the buffer under test ----
            var receiver = new AudioReceiver();
            disposables.Add(receiver);
            receiver.SetOutputDevices([]);
            receiver.AudioKey = key;
            receiver.AudioFingerprint = fingerprint;
            receiver.SetActiveOutputLanes(wasapiActive: false, asioActive: true);
            receiver.SetIndependentLaneLatency(true);
            receiver.SetMaxLatencyMsFor(RenderRoute.AsioLane, s.BufferMs);
            receiver.SetMaxLatencyMsFor(RenderRoute.WasapiLane, s.BufferMs);
            receiver.SetPlaybackEnabled(true);
            var listener = Join("the listener", s.FromServer, 0, null, receiver);

            // ---- the senders ----
            var rng = new Random(s.Seed);
            var senders = new List<(ServerSender Who, AudioSender Sender, Member Member)>();
            for (var i = 0; i < s.Senders.Count; i++)
            {
                var who = s.Senders[i];
                var sender = new AudioSender();
                disposables.Add(sender);
                sender.AudioKey = key;
                sender.AudioFingerprint = fingerprint;
                Simulation.ConfigureCodec(sender, s.Codec);
                sender.UseAsioLaneForTest();
                sender.UseDefaultLaneForTest(RenderRoute.WasapiLane);
                var member = Join(who.Name, SimNetwork.Perfect, i + 1, sender, null);
                senders.Add((who, sender, member));
                var dice = new Random(s.Seed * 100 + i + 1);
                double lastArrival = 0;
                sender.PacketTapForTest = (bytes, target) =>
                {
                    if (!target.Equals(ServerAt)) return;
                    if (who.ToServer.LossPercent > 0 && dice.NextDouble() * 100 < who.ToServer.LossPercent) return;
                    var arrival = Math.Max(Simulation.Arrival(who.ToServer, now, dice), lastArrival);
                    lastArrival = arrival;
                    At(arrival, () => Forward(bytes));
                };
            }

            // ---- who has ticked whom, as the app does it: the relay is told, sound goes to the relay for them, and theirs
            // is let in at the address the group client credits them to ----
            var listenerTicks = (s.ListenerTicks ?? Enumerable.Range(0, senders.Count).ToList()).Select(i => senders[i].Member.Id).ToHashSet();
            listener.Group.SetTicked(listenerTicks, pairPartner: false);
            receiver.SetAllowedSenders(listener.Group.Members.Where(m => listenerTicks.Contains(m.Id)).Select(m => new IPEndPoint(m.Address.Address, 0)).ToList());
            foreach (var (who, sender, member) in senders)
            {
                var ticks = who.TicksListener ? new[] { listener.Id } : [];
                member.Group.SetTicked(ticks, pairPartner: false);
                var ticked = member.Group.Members.Where(m => ticks.Contains(m.Id)).Select(m => m.Address);
                sender.SetReceivers(MainForm.SendTargets(ticked, member.Group, relayPairTicked: false));
            }

            // ---- each sender's card: its tone on the left, and the first sender's clicks on the right ----
            for (var i = 0; i < senders.Count; i++)
            {
                var (who, sender, _) = senders[i];
                var card = who.Card ?? Simulation.DefaultCard(asio: true);
                var rate = Simulation.SampleRate * (1 + card.DriftPpm * 1e-6);
                long captured = 0;
                var clicks = i == 0;
                Simulation.ScheduleCard(card, card.Frames / rate, s.Seconds, rng, At, () =>
                {
                    var block = new float[card.Frames * 2];
                    for (var f = 0; f < card.Frames; f++)
                    {
                        var n = captured + f;
                        block[2 * f] = (float)(ToneLevel * Math.Sin(2 * Math.PI * who.ToneHz * n / Simulation.SampleRate));
                        block[2 * f + 1] = clicks && n % (long)(Simulation.ClickEverySec * Simulation.SampleRate) == 0 ? Simulation.ClickLevel : 0f;
                    }
                    captured += card.Frames;
                    sender.FeedAsioLaneForTest(block);
                });
            }

            // ---- the listener's card ----
            var listenerCard = s.ListenerCard ?? Simulation.DefaultCard(asio: true);
            var listenerPeriod = listenerCard.Frames / (Simulation.SampleRate * (1 + listenerCard.DriftPpm * 1e-6));
            var output = new List<float>();
            var times = new List<(int Frame, double Time)>();
            var provider = receiver.SimulationOutput(RenderRoute.AsioLane);
            var readBytes = new byte[listenerCard.Frames * 8];
            Simulation.ScheduleCard(listenerCard, listenerPeriod, s.Seconds, rng, At, () =>
            {
                Array.Clear(readBytes);
                provider.Read(readBytes, 0, readBytes.Length);
                times.Add((output.Count / 2, now + listenerPeriod));
                output.AddRange(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(readBytes).ToArray());
            });
            for (var sec = 1; sec <= (int)s.Seconds; sec++)
                At(sec, () => receiver.UpdatePerStreamNeeds(_ => (int)Math.Ceiling(listenerPeriod * 1000), _ => false));

            // ---- run ----
            while (queue.TryDequeue(out var action, out var when))
            {
                if (when.Item1 > s.Seconds + 1) break;
                now = when.Item1;
                action();
            }

            // ---- listen: each sender's tone on its own, and the first sender's clicks ----
            var fromFrame = (int)(s.AnalyseFromSecond * Simulation.SampleRate);
            var heardBy = listener.Group.Members.ToDictionary(m => m.Id, m => m.Address.ToString());
            var results = senders.Select(x =>
            {
                var (heard, level, breakUps, quietMs) = ToneTrack(output, fromFrame, x.Who.ToneHz);
                return new ServerSenderResult(x.Who.Name, x.Who.ToneHz, heard, level, breakUps, quietMs, heardBy.GetValueOrDefault(x.Member.Id, "not listed"));
            }).ToList();
            var listened = output.Skip(fromFrame * 2).ToList();
            var listenedTimes = times.Where(t => t.Frame >= fromFrame).Select(t => (t.Frame - fromFrame, t.Time)).ToList();
            var clickFindings = SimAnalysis.Analyse(listened, listenedTimes, Simulation.SampleRate);
            return new ServerResult(s, results, clickFindings, receiver.TuneBlockingUnderrunsFor(RenderRoute.AsioLane), forwarded, refused,
                receiver.SessionsOpenedCount, wall.Elapsed.TotalSeconds, ordinarySound);
        }
        finally
        {
            foreach (var d in disposables.AsEnumerable().Reverse()) { try { d.Dispose(); } catch { /* teardown */ } }
            SessionPlayout.ConcealSeedForTest = null;
            AudioClock.UseSimulated(null);
            DiagnosticsGate.Enabled = diagnosticsWere;
            JamulusStyle.Reset();
            CustomOpusPeers.Clear();
        }
    }

    /// <summary>
    /// One tone in the mix, 10 ms at a time (the tones are multiples of 100 Hz, so each fills whole cycles of a 10 ms window
    /// and none leaks into another's reading): whether it is there, its level, and its break-ups - runs of windows under four
    /// fifths of its usual level (2.5 ms of silence in one is a quarter gone), from when it starts to the end.
    /// </summary>
    internal static (bool Heard, double Level, int BreakUps, int QuietMs) ToneTrack(IReadOnlyList<float> interleaved, int fromFrame, double hz)
    {
        const int Window = Simulation.SampleRate / 100;
        var levels = new List<double>();
        for (var start = fromFrame; start + Window <= interleaved.Count / 2; start += Window)
        {
            double re = 0, im = 0;
            for (var i = 0; i < Window; i++)
            {
                var w = 2 * Math.PI * hz * i / Simulation.SampleRate;
                var v = interleaved[2 * (start + i)];
                re += v * Math.Cos(w);
                im += v * Math.Sin(w);
            }
            levels.Add(2 * Math.Sqrt(re * re + im * im) / Window);
        }
        if (levels.Count == 0) return (false, 0, 0, 0);
        var sorted = levels.OrderBy(l => l).ToList();
        var usual = sorted[sorted.Count / 2];
        if (usual < 0.02) return (false, usual, 0, 0);
        var first = levels.FindIndex(l => l >= usual / 2);
        int breakUps = 0, quiet = 0;
        var inBreak = false;
        for (var i = first; i < levels.Count - 1; i++)   // the last window may be the run's end
        {
            if (levels[i] < usual * 0.8)
            {
                quiet += 10;
                if (!inBreak) breakUps++;
                inBreak = true;
            }
            else inBreak = false;
        }
        return (true, usual, breakUps, quiet);
    }

    public static string Describe(ServerResult r) =>
        $"{r.Scenario.Name}: {r.Scenario.Senders.Count} through the server at {r.Scenario.BufferMs} ms -> "
        + string.Join("; ", r.Senders.Select(x => x.Heard
            ? $"{x.Name} ({x.ToneHz:0} Hz, as {x.Address}) level {x.Level:0.00}, {x.BreakUps} break-ups ({x.QuietMs} quiet ms)"
            : $"{x.Name} NOT HEARD"))
        + $"; delay {r.Clicks.DelayMedianMs:0.0} ms (min {r.Clicks.DelayMinMs:0.0}, max {r.Clicks.DelayMaxMs:0.0}); underruns {r.Underruns}, "
        + $"{r.Forwarded} passed on by the server, {r.Refused} not (unticked); {r.SessionsOpened} streams; {r.RealSeconds:0.00} s to run";
}
