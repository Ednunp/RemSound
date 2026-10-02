namespace RemSound.Core;

/// <summary>
/// HEAR MYSELF, MEASURED (2026-10-01). While the server sends this computer's own sound back (RelayGroupClient.SetHearMyself),
/// each packet comes back with the stream and sequence it went out with, and the sending lane already keeps when its first
/// sample reached the input (the timing reports the server's metronome uses). The two together give, packet by packet, how
/// long our sound took from the input to back here: capture, sending, the network there and back, and the server.
///
/// <para>The sent side is fed on the UI thread once a second (<see cref="NoteSent"/>), the arrivals on the network thread
/// (<see cref="NoteArrival"/>), which allocates nothing: a lookup in a dictionary sized up front, or a slot in a ring. Either
/// side may come first - a packet's echo can be back before the report that names it is read - so each side looks for the
/// other.</para>
/// </summary>
public sealed class HearMyselfMeter
{
    /// <summary>Sent packets kept waiting for their echo, and echoes kept waiting for their report: oldest dropped first.</summary>
    public const int MaxWaiting = 512;

    private readonly object gate = new();
    private readonly Dictionary<(ushort Stream, uint Sequence), long> waiting = new(MaxWaiting * 2);
    private readonly Queue<(ushort Stream, uint Sequence)> waitingOrder = new(MaxWaiting * 2);
    private readonly (ushort Stream, uint Sequence, long ArrivedUs, bool Used)[] early = new (ushort, uint, long, bool)[MaxWaiting];
    private int earlyNext;
    private readonly long[] trips = new long[MaxWaiting];
    private int tripCount;
    private long arrivals;

    /// <summary>Packets that went out, each with when its first sample reached the input (microseconds, <see cref="MetronomeClock"/>).</summary>
    public void NoteSent(IReadOnlyList<(ushort Stream, uint Sequence, long CapturedUs)> packets)
    {
        lock (gate)
        {
            foreach (var (stream, sequence, captured) in packets)
            {
                var back = Array.FindIndex(early, e => !e.Used && e.ArrivedUs != 0 && e.Stream == stream && e.Sequence == sequence);
                if (back >= 0)
                {
                    early[back].Used = true;
                    AddTrip(early[back].ArrivedUs - captured);
                    continue;
                }
                if (waiting.ContainsKey((stream, sequence))) continue;
                waiting[(stream, sequence)] = captured;
                waitingOrder.Enqueue((stream, sequence));
            }
            while (waitingOrder.Count > MaxWaiting) waiting.Remove(waitingOrder.Dequeue());
        }
    }

    /// <summary>One of our own audio packets came back from the server at <paramref name="arrivedUs"/>.</summary>
    public void NoteArrival(ushort stream, uint sequence, long arrivedUs)
    {
        lock (gate)
        {
            arrivals++;
            if (waiting.Remove((stream, sequence), out var captured))
            {
                AddTrip(arrivedUs - captured);
                return;
            }
            early[earlyNext] = (stream, sequence, arrivedUs, false);
            earlyNext = (earlyNext + 1) % early.Length;
        }
    }

    private void AddTrip(long us)
    {
        if (tripCount < trips.Length) trips[tripCount++] = us;
    }

    /// <summary>The second just gone: how many of our own packets came back, how many of them were timed, and the typical
    /// (median) and longest time from the input to back here. Counted afresh each call.</summary>
    public (long Arrivals, int Timed, double? TypicalMs, double? LongestMs) TakeSecond()
    {
        lock (gate)
        {
            var result = (arrivals, tripCount, (double?)null, (double?)null);
            if (tripCount > 0)
            {
                var span = trips.AsSpan(0, tripCount);
                span.Sort();
                var median = tripCount % 2 == 1 ? span[tripCount / 2] : (span[tripCount / 2 - 1] + span[tripCount / 2]) / 2.0;
                result = (arrivals, tripCount, median / 1000.0, span[^1] / 1000.0);
            }
            arrivals = 0;
            tripCount = 0;
            return result;
        }
    }

    /// <summary>Forget everything: the packets and echoes still waiting for each other, and the second so far.</summary>
    public void Reset()
    {
        lock (gate)
        {
            waiting.Clear();
            waitingOrder.Clear();
            Array.Clear(early);
            earlyNext = 0;
            tripCount = 0;
            arrivals = 0;
        }
    }
}
