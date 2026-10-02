using System.Buffers.Binary;
using System.Net;
using System.Text;
using RemSound.Core;

namespace RemSound.App.Simulator;

/// <summary>One server status run: your networks to and from the server, how often you send, and who else is on it.</summary>
/// <param name="Others">The other people on the server, each by the group they are in ("yours" for yours).</param>
internal sealed record ServerStatusScenario(string Name, SimNetwork ToServer, SimNetwork FromServer, IReadOnlyList<string> Others,
    double FrameMs = 2.5, double Seconds = 40, int Seed = 1, int Places = 64);

/// <summary>What the app knew at the end: its round trip to the server, the server's last status, and the status text.</summary>
internal sealed record ServerStatusResult(ServerStatusScenario Scenario, double? RoundTripMs, RelayGroupClient.ServerInfo? Info,
    double ExpectedLongestGapMs, int ExpectedLongGaps, string StatusText);

/// <summary>
/// THE SERVER'S STATUS, SIMULATED (Ed, 2026-10-01: "make sure you simulate"). Your real <see cref="RelayGroupClient"/> on a
/// virtual clock, a server that does what server/remsound-relay.py does with status and pings (server-v2.16) - each second
/// of your sound counted as it arrives, waits over two seconds taken for stopping rather than the network, the last minute
/// sent every five seconds with the server's size; a ping sent straight back - and your two networks between you, from
/// <see cref="Simulation"/>. The app's own reading of the status, and its own words for it
/// (<see cref="MainForm.ServerStatusText"/>), come out the end. Virtual time, one thread.
/// </summary>
internal static class ServerStatusSimulation
{
    private static readonly IPEndPoint ServerAt = new(IPAddress.Parse("10.77.0.200"), RemPacket.DefaultPort);
    private const double StopMs = 2000;   // the relay's STEADY_RESTART_MS
    private const double LongGapMs = 20;  // the relay's AUDIO_GAP_LONG_MS

    public static ServerStatusResult Run(ServerStatusScenario s)
    {
        double now = 0;
        var queue = new PriorityQueue<Action, (double, long)>();
        long order = 0;
        void At(double t, Action a) => queue.Enqueue(a, (t, order++));
        var upDice = new Random(s.Seed * 7 + 1);
        var downDice = new Random(s.Seed * 7 + 2);

        var you = new Guid(Enumerable.Range(0, 16).Select(i => (byte)(i * 13 + 5)).ToArray());
        using var group = new RelayGroupClient(you);
        group.ClockUsForTest = () => (long)(now * 1_000_000);
        group.SetIdentity("you", new byte[RelayGroupClient.GroupTagBytes]);
        group.Connect(ServerAt);

        // ---- the server: your sound second by second, the status every five seconds, pings straight back ----
        var buckets = new List<(int Second, int Packets, double Longest, int Long)>();
        double lastArrival = double.NaN;
        (int Packets, double Longest, int Long, int Seconds) lastSent = default;
        void SoundArrived()
        {
            var since = double.IsNaN(lastArrival) ? (double?)null : (now - lastArrival) * 1000;
            var second = (int)Math.Floor(now);
            if (buckets.Count == 0 || buckets[^1].Second != second) buckets.Add((second, 0, 0, 0));
            var b = buckets[^1];
            b.Packets++;
            if (since is { } gap && gap <= StopMs)
            {
                b.Longest = Math.Max(b.Longest, gap);
                if (gap > LongGapMs) b.Long++;
            }
            buckets[^1] = b;
            lastArrival = now;
        }
        (int Packets, double Longest, int Long, int Seconds) LastMinute()
        {
            var cutoff = (int)Math.Floor(now) - 60;
            var mine = buckets.Where(b => b.Second > cutoff).ToList();
            return (mine.Sum(b => b.Packets), mine.Count == 0 ? 0 : mine.Max(b => b.Longest), mine.Sum(b => b.Long), mine.Count);
        }
        byte[] StatusPacket()
        {
            var version = Encoding.ASCII.GetBytes("server-v2.16");
            var packet = new byte[RelayGroupClient.GroupHeaderSize + 2 + version.Length + 24];
            RemPacket.WriteHeader(packet, (RemPacketType)RelayGroupClient.TypeServerInfo, 0, 0);
            packet[4] = RelayGroupClient.GroupVersion;
            var at = RelayGroupClient.GroupHeaderSize;
            packet[at] = 1;
            packet[at + 1] = (byte)version.Length;
            version.CopyTo(packet, at + 2);
            var body = packet.AsSpan(at + 2 + version.Length);
            var people = 1 + s.Others.Count;
            var groups = s.Others.Append("yours").Distinct().Count();
            var (packets, longest, longGaps, seconds) = lastSent = LastMinute();
            BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)now);
            BinaryPrimitives.WriteUInt16LittleEndian(body[4..], (ushort)people);
            BinaryPrimitives.WriteUInt16LittleEndian(body[6..], (ushort)groups);
            BinaryPrimitives.WriteUInt16LittleEndian(body[8..], (ushort)Math.Max(0, s.Places - people));
            BinaryPrimitives.WriteUInt16LittleEndian(body[10..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(body[12..], (uint)packets);
            BinaryPrimitives.WriteUInt32LittleEndian(body[16..], (uint)(longest * 1000));
            BinaryPrimitives.WriteUInt16LittleEndian(body[20..], (ushort)longGaps);
            BinaryPrimitives.WriteUInt16LittleEndian(body[22..], (ushort)seconds);
            return packet;
        }
        void ToYou(byte[] packet)
        {
            var arrival = Simulation.Arrival(s.FromServer, now, downDice);
            if (s.FromServer.LossPercent > 0 && downDice.NextDouble() * 100 < s.FromServer.LossPercent) return;
            At(arrival, () => { var length = packet.Length; group.HandleInbound(packet, ref length, ServerAt, out _); });
        }
        double lastUp = 0;
        void ToServer(Action arrived)
        {
            if (s.ToServer.LossPercent > 0 && upDice.NextDouble() * 100 < s.ToServer.LossPercent) return;
            var arrival = Math.Max(Simulation.Arrival(s.ToServer, now, upDice), lastUp);   // one path: nothing overtakes
            lastUp = arrival;
            At(arrival, arrived);
        }

        // ---- the member list: you, and the others in your group, so you are in a group from the start ----
        var mates = s.Others.Select((g, i) => (g, i)).Where(x => x.g == "yours")
            .Select(x => (new Guid(Enumerable.Range(0, 16).Select(b => (byte)(x.i * 31 + b + 100)).ToArray()), $"mate {x.i + 1}")).ToList();
        var roster = new byte[RelayGroupClient.GroupHeaderSize + 1 + (mates.Count + 1) * RelayGroupClient.RosterEntryBytes + 1];
        RemPacket.WriteHeader(roster, (RemPacketType)RelayGroupClient.TypeRoster, 0, 0);
        roster[4] = RelayGroupClient.GroupVersion;
        var rAt = RelayGroupClient.GroupHeaderSize;
        roster[rAt++] = (byte)(mates.Count + 1);
        foreach (var (id, name) in mates.Prepend((you, "you")))
        {
            id.TryWriteBytes(roster.AsSpan(rAt, RelayGroupClient.ClientIdSize), bigEndian: true, out _);
            RelayGroupClient.EncodeName(name, roster.AsSpan(rAt + RelayGroupClient.ClientIdSize, RelayGroupClient.NameBytes));
            roster[rAt + RelayGroupClient.RosterEntryBytes - 1] = RelayGroupClient.RosterMemberFlagTicksUs;
            rAt += RelayGroupClient.RosterEntryBytes;
        }
        for (var t = 0.0; t < s.Seconds; t += 1.0) At(t, () => { var length = roster.Length; group.HandleInbound(roster, ref length, ServerAt, out _); });

        // ---- you: a packet every frame, a ping every second; the server's status every five ----
        for (var t = 0.01; t < s.Seconds; t += s.FrameMs / 1000) At(t, () => ToServer(SoundArrived));
        for (var t = 0.5; t < s.Seconds; t += 1.0)
            At(t, () =>
            {
                var ping = group.PingForTest();
                ToServer(() => ToYou(ping));
            });
        for (var t = 0.6; t < s.Seconds; t += 5.0) At(t, () => ToYou(StatusPacket()));

        while (queue.TryDequeue(out var action, out var when))
        {
            now = when.Item1;
            action();
        }
        var expected = lastSent;   // what the last status said, as the server worked it out when it sent it
        var info = group.LatestServerInfo;
        var text = MainForm.ServerStatusText(new MainForm.ServerStatusFacts("server.example.test", ServerAt, TimeSpan.FromSeconds(s.Seconds),
            group.ServerRoundTripMs, info, OlderServer: false, Sending: true,
            group.Members.Select(m => (m.Name, m.TicksUs)).ToList(), PhonePaired: false));
        return new ServerStatusResult(s, group.ServerRoundTripMs, info, expected.Longest, expected.Long, text);
    }
}
