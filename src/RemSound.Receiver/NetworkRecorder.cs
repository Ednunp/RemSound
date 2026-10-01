using System.Diagnostics;
using System.Globalization;
using System.Net;
using RemSound.Core;

namespace RemSound.Receiver;

/// <summary>
/// The last minute of audio-packet arrivals, kept in memory so a moment that sounded wrong can be saved and replayed in the
/// simulator (2026-09-30: <see cref="NetworkRecording"/>). One note per packet, taken as it comes off the socket - before
/// any decision about it - on <see cref="AudioClock"/>, so a simulated run records in its own virtual time. The store
/// grows with the packet rate (a minute at 400 packets a second is about 400 KB) up to a ceiling, and an entry older than
/// a minute is overwritten first. Once a second the owner adds the receiver's own counters, so a saved minute says what the
/// receiver itself heard go wrong in it. Off until its owner switches it on - the app's own receiver, a simulated run that
/// records - so the performance rig's many receivers keep no minute of their own (it cost them 770 KB, 2026-09-30).
/// </summary>
public sealed class NetworkRecorder
{
    /// <summary>Recording at all. Nothing is kept, or allocated, while off.</summary>
    public bool Enabled { get; set; }

    public const double KeepSeconds = 60;
    private const int FirstCapacity = 4096;
    private const int MaxCapacity = 1 << 18; // about 4 MB at the most: 60 s of 4,000+ packets a second

    private readonly record struct Entry(long Ticks, uint Sequence, ushort Bytes, ushort Stream);

    /// <summary>The receiver's counters at one moment (lifetime totals): what the saved minute's live figures come from.</summary>
    public readonly record struct Counters(long Ticks, long DropoutsAll, long DropoutsAsio, long DropoutsWasapi, long UnderrunsAsio,
        long UnderrunsWasapi, long UnderrunsMixed, long LateDropped, long OpusRepaired, long Unrepaired, long Concealed);

    private readonly object gate = new();
    private Entry[] ring = [];
    private int next;      // where the next entry goes
    private int count;     // entries held
    private readonly List<(IPEndPoint From, ushort StreamId)> streams = new();
    private IPEndPoint? lastFrom;
    private ushort lastStreamId;
    private int lastStream = -1;
    private readonly Queue<Counters> seconds = new();

    /// <summary>A packet came in.</summary>
    public void Note(IPEndPoint from, ushort streamId, uint sequence, int bytes)
    {
        if (!Enabled) return;
        var ticks = AudioClock.GetTimestamp();
        lock (gate)
        {
            int stream;
            if (lastStream >= 0 && streamId == lastStreamId && from.Equals(lastFrom)) stream = lastStream;
            else
            {
                stream = streams.FindIndex(s => s.StreamId == streamId && s.From.Equals(from));
                if (stream < 0)
                {
                    if (streams.Count >= ushort.MaxValue) return;
                    stream = streams.Count;
                    streams.Add((from, streamId));
                }
                (lastFrom, lastStreamId, lastStream) = (from, streamId, stream);
            }
            if (ring.Length == 0) ring = new Entry[FirstCapacity];
            if (count == ring.Length)
            {
                // Full: grow while the oldest entry is still inside the minute, else overwrite it.
                var oldest = ring[next];
                if (ring.Length < MaxCapacity && (ticks - oldest.Ticks) < KeepSeconds * Stopwatch.Frequency) Grow();
                else count--;
            }
            ring[next] = new Entry(ticks, sequence, (ushort)Math.Min(bytes, ushort.MaxValue), (ushort)stream);
            next = (next + 1) % ring.Length;
            count++;
        }
    }

    private void Grow()
    {
        var bigger = new Entry[ring.Length * 2];
        // Oldest first: from next (the oldest, the ring being full) round to next - 1.
        for (var i = 0; i < count; i++) bigger[i] = ring[(next + i) % ring.Length];
        ring = bigger;
        next = count;
    }

    /// <summary>Once a second: the receiver's counters now. Two minutes of them are kept, so the saved minute's start is
    /// always among them, however often they come.</summary>
    public void NoteSecond(Counters now)
    {
        if (!Enabled) return;
        lock (gate)
        {
            seconds.Enqueue(now);
            while (seconds.Count > 2 * KeepSeconds + 10) seconds.Dequeue();
        }
    }

    /// <summary>How many packets are held (for tests).</summary>
    public int HeldForTest { get { lock (gate) return count; } }
    public int CapacityForTest { get { lock (gate) return ring.Length; } }

    /// <summary>The last minute as a recording. <paramref name="describe"/> says what each stream is (its lane, format,
    /// codec and frame) when the receiver knows; <paramref name="facts"/> come first in the file (the machine, the
    /// configuration, the buffers). The live figures are the counters' change across the packets' own span.</summary>
    public NetworkRecording Take(IEnumerable<KeyValuePair<string, string>> facts,
        Func<IPEndPoint, ushort, (string Lane, string Format, int Codec, int FrameSamples)?> describe)
    {
        Entry[] held;
        List<(IPEndPoint From, ushort StreamId)> known;
        Counters[] counters;
        lock (gate)
        {
            held = new Entry[count];
            if (count > 0)
            {
                var first = (next - count + ring.Length) % ring.Length;
                for (var i = 0; i < count; i++) held[i] = ring[(first + i) % ring.Length];
            }
            known = streams.ToList();
            counters = seconds.ToArray();
        }
        var rec = new NetworkRecording();
        foreach (var (k, v) in facts) rec.AddFact(k, v);
        if (held.Length == 0) return rec;
        var newest = held[^1].Ticks;
        var from = Array.FindIndex(held, e => newest - e.Ticks <= KeepSeconds * Stopwatch.Frequency);
        var kept = held[from..];
        var start = kept[0].Ticks;
        // Streams renumbered from 0, in the order they first appear in the kept minute.
        var renumber = new Dictionary<int, int>();
        foreach (var e in kept)
        {
            if (renumber.ContainsKey(e.Stream)) continue;
            var index = renumber.Count;
            renumber[e.Stream] = index;
            var (addr, id) = known[e.Stream];
            var d = describe(addr, id);
            rec.Streams.Add(new NetworkRecording.Stream(index, addr.ToString(), id, d?.Lane ?? "unknown", d?.Format ?? "no Format packet seen",
                d?.Codec ?? -1, d?.FrameSamples ?? 0));
        }
        foreach (var e in kept)
            rec.Packets.Add(new NetworkRecording.Packet((e.Ticks - start) * 1000.0 / Stopwatch.Frequency, renumber[e.Stream], e.Sequence, e.Bytes));

        // What the receiver heard go wrong over the same span: the change between the counters nearest its start and the
        // newest ones.
        var spanFrom = counters.Where(c => c.Ticks <= start).DefaultIfEmpty(counters.FirstOrDefault()).Last();
        if (counters.Length >= 2 && spanFrom.Ticks != 0)
        {
            var last = counters[^1];
            string S(long v) => v.ToString(CultureInfo.InvariantCulture);
            rec.AddFact("live seconds", ((last.Ticks - spanFrom.Ticks) / (double)Stopwatch.Frequency).ToString("0.0", CultureInfo.InvariantCulture));
            rec.AddFact("live audible gaps", S(last.DropoutsAll - spanFrom.DropoutsAll));
            rec.AddFact("live audible gaps ASIO", S(last.DropoutsAsio - spanFrom.DropoutsAsio));
            rec.AddFact("live audible gaps WASAPI", S(last.DropoutsWasapi - spanFrom.DropoutsWasapi));
            rec.AddFact("live underruns ASIO", S(last.UnderrunsAsio - spanFrom.UnderrunsAsio));
            rec.AddFact("live underruns WASAPI", S(last.UnderrunsWasapi - spanFrom.UnderrunsWasapi));
            rec.AddFact("live underruns mixed", S(last.UnderrunsMixed - spanFrom.UnderrunsMixed));
            rec.AddFact("live late or doubled dropped", S(last.LateDropped - spanFrom.LateDropped));
            rec.AddFact("live Opus repaired", S(last.OpusRepaired - spanFrom.OpusRepaired));
            rec.AddFact("live gaps not repaired", S(last.Unrepaired - spanFrom.Unrepaired));
            rec.AddFact("live concealed", S(last.Concealed - spanFrom.Concealed));
        }
        return rec;
    }
}
