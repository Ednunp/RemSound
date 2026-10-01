using RemSound.Core;

namespace RemSound.App.Simulator;

/// <summary>
/// A recorded network, replayed (2026-09-30, the simulator review's first improvement). From one stream of a
/// <see cref="NetworkRecording"/>: when each packet arrived, by its nominal place in the stream (its sequence number) and
/// the stream's own period - fitted along the fastest packets, so the two machines' clocks drifting apart is folded in.
/// Lost packets are the places that never came; doubled ones came twice.
///
/// <para>A simulated packet takes the arrival, loss or doubling of its place: its sequence number, when the simulated
/// stream runs at the recording's packet rate; otherwise the place nominally nearest it in time (its sequence number x its
/// own period), so a recording made at one packet rate replays at any other, bursts and losses kept in time. It arrives at
/// the stream's start plus its place's recorded arrival (measured from the fastest packet's line), and never before its
/// own send plus the network's one-way delay. The start is the first packet's send, raised whenever a later packet shows
/// it must be later. At the recording's own rate the arrival is the recorded one, unchanged: packets that came together
/// come together again, in the order they were sent - summed from fractions, a bunch's times differed by a billionth of a
/// second, came out backwards, and the receiver threw them away as overtaken (the first round trip, 2026-09-30). No dice
/// are drawn: every hiccup the receiver met is in the recording already.</para>
/// </summary>
internal sealed class NetworkReplay
{
    public string Name { get; }
    /// <summary>The recorded stream's period between packets, in seconds.</summary>
    public double Period { get; }
    /// <summary>How many places the recording covers (lost ones included); it loops after that.</summary>
    public int Places => arrive.Length;
    public int Lost { get; }
    public int Doubled { get; }
    // Each place's arrival, in seconds after the fastest packet's line would have it (so the fastest are at 0); NaN = never
    // came. The line is anchor + place x Period.
    private readonly double[] arrive;
    private readonly double[] again;   // a second arrival, the same way; NaN = came once
    private readonly Dictionary<ushort, (uint FirstSequence, double Start)> streams = new();
    private double lastArrival;        // the latest arrival handed out, for what the recording does not hold

    private NetworkReplay(string name, double period, double[] arrive, double[] again)
    {
        Name = name;
        Period = period;
        this.arrive = arrive;
        this.again = again;
        Lost = arrive.Count(double.IsNaN);
        Doubled = again.Count(d => !double.IsNaN(d));
    }

    /// <summary>The same recording, from the start again: a run keeps what it has learnt about its streams in its own copy.</summary>
    public NetworkReplay Fresh() => new(Name, Period, arrive, again);

    /// <summary>How much later than the fastest packets each place arrived (seconds), for tests and reports.</summary>
    public double DelayOf(int place) => arrive[place] - place * Period;

    /// <summary>The stream with the most packets unless one is named. Throws when there is nothing to replay.</summary>
    public static NetworkReplay From(NetworkRecording rec, int stream = -1, string? name = null)
    {
        if (stream < 0)
            stream = rec.Packets.GroupBy(p => p.Stream).OrderByDescending(g => g.Count()).Select(g => g.Key).DefaultIfEmpty(-1).First();
        var packets = rec.Packets.Where(p => p.Stream == stream).ToList();
        if (packets.Count < 8) throw new InvalidOperationException($"stream {stream} has {packets.Count} packets - too few to replay");
        // Sequence numbers unwrapped against the first one heard (the wire's counter is 32 bits and may wrap).
        var first = packets[0].Sequence;
        var places = packets.Select(p => (long)unchecked((int)(p.Sequence - first))).ToList();
        var lowest = places.Min();
        places = places.Select(n => n - lowest).ToList();
        var arrivals = packets.Select(p => p.Ms / 1000).ToList();
        var count = (int)places.Max() + 1;

        // The period: end to end first, then corrected along the lower envelope - the fastest packet in the first quarter
        // and in the last - since those met the least delay.
        int iMin = places.IndexOf(places.Min()), iMax = places.IndexOf(places.Max());
        var period = (arrivals[iMax] - arrivals[iMin]) / Math.Max(1, places[iMax] - places[iMin]);
        (long N, double R) Fastest(Func<long, bool> inPart)
        {
            var best = (N: -1L, R: double.MaxValue);
            for (var i = 0; i < places.Count; i++)
                if (inPart(places[i]) && arrivals[i] - places[i] * period < best.R) best = (places[i], arrivals[i] - places[i] * period);
            return best;
        }
        var early = Fastest(n => n < count / 4);
        var late = Fastest(n => n >= count - count / 4);
        if (early.N >= 0 && late.N > early.N) period += (late.R - early.R) / (late.N - early.N);

        // The fastest packet's line: every place's arrival measured from its anchor, so the fastest arrive on it.
        var anchor = double.MaxValue;
        for (var i = 0; i < places.Count; i++) anchor = Math.Min(anchor, arrivals[i] - places[i] * period);
        var arrive = Enumerable.Repeat(double.NaN, count).ToArray();
        var again = Enumerable.Repeat(double.NaN, count).ToArray();
        for (var i = 0; i < places.Count; i++)
        {
            var n = (int)places[i];
            if (double.IsNaN(arrive[n])) arrive[n] = arrivals[i] - anchor;
            else if (double.IsNaN(again[n])) again[n] = arrivals[i] - anchor;
        }
        return new NetworkReplay(name ?? $"replayed: {rec.Fact("machine") ?? "a recording"} stream {stream}", period, arrive, again);
    }

    /// <summary>When a packet sent at <paramref name="sentAt"/> arrives: none if its recorded place was lost, two if it was
    /// doubled. <paramref name="ownPeriod"/> is the simulated stream's own period between packets; <paramref name="oneWay"/>
    /// the network's one-way delay; both in seconds.</summary>
    public IReadOnlyList<double> ArrivalsFor(ReadOnlySpan<byte> packet, double sentAt, double oneWay, double ownPeriod)
    {
        if (!RemPacket.TryReadHeader(packet, out var type, out var streamId, out var sequence)) return [sentAt + oneWay];
        // Format packets and the like are never lost (the sender repeats them, and the recording holds only audio), and
        // queue behind the audio sent before them, as a network's queue keeps its order.
        if (type != RemPacketType.Audio) return [Math.Max(sentAt + oneWay, lastArrival)];
        if (!streams.TryGetValue(streamId, out var st)) st = (sequence, sentAt);
        var k = unchecked((int)(sequence - st.FirstSequence));
        // Its place, and its time from the stream's start: at the recording's own packet rate the place is the sequence
        // number and the time the recorded one exactly; otherwise the place nominally nearest in time, and the difference.
        var sameRate = Math.Abs(ownPeriod - Period) <= Period * 1e-3;
        var unwrapped = sameRate ? k : (long)Math.Round(k * ownPeriod / Period);
        var laps = unwrapped / arrive.Length;
        var place = (int)(unwrapped % arrive.Length);
        if (place < 0) { place += arrive.Length; laps--; }
        var shift = laps * arrive.Length * Period + (sameRate ? 0 : k * ownPeriod - unwrapped * Period);
        var a = arrive[place];
        streams[streamId] = st;
        if (double.IsNaN(a)) return [];
        // The start is never earlier than any packet that came allows.
        if (sentAt - (shift + a) > st.Start) streams[streamId] = st = (st.FirstSequence, sentAt - (shift + a));
        var at = Math.Max(sentAt, st.Start + (shift + a)) + oneWay;
        lastArrival = Math.Max(lastArrival, at);
        var twice = again[place];
        return double.IsNaN(twice) ? [at] : [at, Math.Max(sentAt, st.Start + (shift + twice)) + oneWay];
    }
}
