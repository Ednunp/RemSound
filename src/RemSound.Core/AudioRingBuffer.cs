namespace RemSound.Core;

/// <summary>
/// Single-producer / single-consumer byte ring buffer for an audio pipeline. Used on both the
/// receive side (network → playout) and the send side (composite mixing across capture backends).
/// Producer thread calls <see cref="Write(ReadOnlySpan{byte})"/>; consumer thread calls
/// <see cref="Read(Span{byte})"/> or <see cref="ReadFloats(Span{float})"/>.
///
/// Design choices for predictability:
///   * Power-of-two capacity for cheap mod via mask.
///   * No locks; head/tail are written by exactly one thread each. Reads of the other side use Volatile.Read
///     to get the latest published value.
///   * On overflow the oldest data is dropped, not silently retained — the playout target is the source of truth.
///   * On underrun the consumer gets silence and an underrun count is incremented.
/// </summary>
public sealed class AudioRingBuffer
{
    private readonly byte[] storage;
    private readonly int mask;
    // head is advanced by the consumer (Read); tail is advanced by the producer (Write).
    private int head;
    private int tail;
    private long underruns;
    private long drops;

    public AudioRingBuffer(int capacityBytes)
    {
        // Round up to next power of two.
        var capacity = 1;
        while (capacity < Math.Max(64, capacityBytes)) capacity <<= 1;
        storage = new byte[capacity];
        mask = capacity - 1;
    }

    public int CapacityBytes => storage.Length;

    public int BufferedBytes => (Volatile.Read(ref tail) - Volatile.Read(ref head)) & 0x7FFFFFFF;

    public long UnderrunCount => Interlocked.Read(ref underruns);

    public long DropCount => Interlocked.Read(ref drops);

    public void Reset()
    {
        Volatile.Write(ref head, 0);
        Volatile.Write(ref tail, 0);
    }

    /// <summary>Producer side. Writes the entire span; if the buffer is full, drops the oldest bytes to make room.</summary>
    public void Write(ReadOnlySpan<byte> source)
    {
        if (source.Length > storage.Length)
        {
            // A single write bigger than the whole ring can't fit. Keep only the NEWEST storage.Length
            // bytes and count the older excess as dropped, so an oversized write degrades to dropped
            // audio instead of throwing (a CopyTo overflow) on this lock-free real-time producer path.
            // Unreachable in normal use — writes are MTU-bounded and the ring is far larger — but this
            // turns an undocumented precondition into a safe, real invariant.
            Interlocked.Add(ref drops, source.Length - storage.Length);
            source = source[^storage.Length..];
        }

        var currentTail = tail;
        var currentHead = Volatile.Read(ref head);
        var available = storage.Length - ((currentTail - currentHead) & 0x7FFFFFFF);

        if (source.Length > available)
        {
            // Drop oldest to make room. Advance head by the deficit.
            //
            // This breaks the strict SPSC rule that only the consumer touches `head` — exactly as
            // TrimFromProducer does, and for the same reason: the alternative is an unbounded queue
            // or a lost write on a real-time producer path. The race is a lost update if the consumer
            // advances head at the same instant, which makes the next `available` read too large and
            // lets the consumer play a few stale bytes. It needs both sides to hit the same window
            // while the ring is already full, so it is rare and self-corrects within one read.
            // Written down because the trim path documented this and the overflow path did not.
            // 2026-08-23 audit, finding R6.
            var deficit = source.Length - available;
            Volatile.Write(ref head, (currentHead + deficit) & 0x7FFFFFFF);
            Interlocked.Add(ref drops, deficit);
        }

        var writeIndex = currentTail & mask;
        var firstChunk = Math.Min(source.Length, storage.Length - writeIndex);
        source[..firstChunk].CopyTo(storage.AsSpan(writeIndex));
        if (firstChunk < source.Length)
        {
            source[firstChunk..].CopyTo(storage.AsSpan(0));
        }

        Volatile.Write(ref tail, (currentTail + source.Length) & 0x7FFFFFFF);
    }

    /// <summary>
    /// Consumer-side: discard the oldest <paramref name="bytesToDrop"/> bytes (or the whole
    /// buffered amount if smaller). Used when the user lowers the delay knob, to bring the
    /// buffer down to the new target instantly instead of waiting for adaptive rate to drain it.
    /// Must be called only from the consumer thread (advances <c>head</c>, which the SPSC
    /// invariant treats as consumer-owned).
    /// </summary>
    public void DropOldest(int bytesToDrop)
    {
        if (bytesToDrop <= 0) return;
        var currentHead = head;
        var currentTail = Volatile.Read(ref tail);
        var available = (currentTail - currentHead) & 0x7FFFFFFF;
        var actual = Math.Min(bytesToDrop, available);
        if (actual <= 0) return;
        Volatile.Write(ref head, (currentHead + actual) & 0x7FFFFFFF);
        Interlocked.Add(ref drops, actual);
    }

    /// <summary>Consumer side: the loudest float sample in the next <paramref name="bytes"/> about to be read (0 when
    /// empty) - is the sound about to play quiet enough to skip from without anyone hearing where?</summary>
    public float PeakAhead(int bytes)
    {
        var currentHead = head;
        var available = (Volatile.Read(ref tail) - currentHead) & 0x7FFFFFFF;
        var n = Math.Min(bytes, available) & ~3;
        var peak = 0f;
        for (var i = 0; i < n; i += 4)
        {
            var v = Math.Abs(BitConverter.ToSingle(storage, (currentHead + i) & mask));
            if (v > peak) peak = v;
        }
        return peak;
    }

    private float[] fadeFrom = [];
    private float[] fadeInto = [];

    /// <summary>
    /// Consumer side: skip about <paramref name="skipBytes"/> of stereo float audio without a click (2026-09-28, Ed:
    /// coming down from a silly delay should snap). The sound about to play is crossfaded, equal-power, into the sound the
    /// skip lands on, over <paramref name="fadeFrames"/>. The landing point moves by up to <paramref name="alignFrames"/>
    /// either way to where the two line up best (normalised correlation), so a held note carries on without a dip. The
    /// blend is written over the landing point and the head moved on to it: the next read plays the blend, then carries on
    /// from there. Returns the bytes skipped; with too little buffered to blend, it drops them plainly.
    /// </summary>
    public int SkipWithCrossfade(int skipBytes, int fadeFrames, int alignFrames)
    {
        const int FrameBytes = 2 * sizeof(float);
        var currentHead = head;
        var available = (Volatile.Read(ref tail) - currentHead) & 0x7FFFFFFF;
        var availableFrames = available / FrameBytes;
        var skipFrames = Math.Min(skipBytes, available) / FrameBytes;
        if (skipFrames <= 0) return 0;
        alignFrames = Math.Max(0, Math.Min(alignFrames, skipFrames - 1));
        fadeFrames = Math.Min(fadeFrames, availableFrames - skipFrames - alignFrames);
        if (fadeFrames < 16)
        {
            DropOldest(skipFrames * FrameBytes);
            return skipFrames * FrameBytes;
        }
        float Sample(int frame, int channel) => BitConverter.ToSingle(storage, (currentHead + (frame * 2 + channel) * sizeof(float)) & mask);

        // Where to land: the offset whose next few milliseconds look most like the next few here.
        var probe = Math.Min(fadeFrames, 240);
        var best = skipFrames;
        var bestScore = double.NegativeInfinity;
        for (var offset = -alignFrames; offset <= alignFrames; offset++)
        {
            var land = skipFrames + offset;
            double dot = 0, here = 0, there = 0;
            for (var f = 0; f < probe; f++)
            {
                double a = Sample(f, 0) + Sample(f, 1), b = Sample(land + f, 0) + Sample(land + f, 1);
                dot += a * b;
                here += a * a;
                there += b * b;
            }
            var score = dot / Math.Sqrt(here * there + 1e-12);
            if (score > bestScore) { bestScore = score; best = land; }
        }
        skipFrames = best;
        // Two stretches that line up (a held note) are blended in straight lines, which keeps their level; two that do not
        // (most skips in music) are blended equal-power, which keeps theirs. Either the other way dips or swells 3 dB.
        var alike = bestScore >= 0.5;

        // The blend, from copies (the two stretches may overlap), written over the landing point.
        var floats = fadeFrames * 2;
        if (fadeFrom.Length < floats) { fadeFrom = new float[floats]; fadeInto = new float[floats]; }
        for (var f = 0; f < fadeFrames; f++)
            for (var c = 0; c < 2; c++)
            {
                fadeFrom[f * 2 + c] = Sample(f, c);
                fadeInto[f * 2 + c] = Sample(skipFrames + f, c);
            }
        Span<byte> four = stackalloc byte[4];
        for (var f = 0; f < fadeFrames; f++)
        {
            var t = (f + 0.5) / fadeFrames;
            var gainOut = alike ? (float)(1 - t) : (float)Math.Cos(t * Math.PI / 2);
            var gainIn = alike ? (float)t : (float)Math.Sin(t * Math.PI / 2);
            for (var c = 0; c < 2; c++)
            {
                BitConverter.TryWriteBytes(four, fadeFrom[f * 2 + c] * gainOut + fadeInto[f * 2 + c] * gainIn);
                var at = (currentHead + ((skipFrames + f) * 2 + c) * sizeof(float)) & mask;
                four.CopyTo(storage.AsSpan(at, 4));
            }
        }
        var skipped = skipFrames * FrameBytes;
        Volatile.Write(ref head, (currentHead + skipped) & 0x7FFFFFFF);
        Interlocked.Add(ref drops, skipped);
        return skipped;
    }

    /// <summary>
    /// Producer-side trim. If the buffer currently holds more than <paramref name="targetBytes"/>,
    /// advances head to discard the oldest excess. Returns the number of bytes dropped. Same
    /// semantics as the overflow-drop path inside <see cref="Write"/>: producer can advance
    /// head, accepting a rare race against the consumer's own head advance — the alternative
    /// (an unbounded queue while no consumer exists) is worse.
    ///
    /// Used by <see cref="SessionPlayout.NoteFramesQueued"/> to soft-cap the playout queue when
    /// audio piles up faster than it's being consumed (e.g. a delay between "receive on" and
    /// "output device selected" — the listener should hear live audio when render starts, not
    /// the multi-second backlog that arrived during the gap).
    /// </summary>
    public int TrimFromProducer(int targetBytes)
    {
        var currentHead = Volatile.Read(ref head);
        var currentTail = Volatile.Read(ref tail);
        var available = (currentTail - currentHead) & 0x7FFFFFFF;
        if (available <= targetBytes) return 0;
        var excess = available - targetBytes;
        Volatile.Write(ref head, (currentHead + excess) & 0x7FFFFFFF);
        Interlocked.Add(ref drops, excess);
        return excess;
    }

    /// <summary>
    /// Float-typed convenience over <see cref="Read(Span{byte})"/>. Returns the count of floats
    /// that came from the buffer (silence-fill is included in destination but not counted here).
    /// Use this from the playout read path on the render thread.
    /// </summary>
    public int ReadFloats(Span<float> destination)
    {
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(destination);
        var bytesRead = Read(bytes);
        return bytesRead / sizeof(float);
    }

    /// <summary>Consumer side. Reads up to destination.Length bytes. Any shortfall is filled with silence (zero).
    /// Returns the number of bytes that came from the buffer (the silence-fill is included in the destination
    /// but is reflected in the underrun counter, not the return value).</summary>
    public int Read(Span<byte> destination)
    {
        var currentHead = head;
        var currentTail = Volatile.Read(ref tail);
        var available = (currentTail - currentHead) & 0x7FFFFFFF;
        var toRead = Math.Min(destination.Length, available);

        if (toRead > 0)
        {
            var readIndex = currentHead & mask;
            var firstChunk = Math.Min(toRead, storage.Length - readIndex);
            storage.AsSpan(readIndex, firstChunk).CopyTo(destination);
            if (firstChunk < toRead)
            {
                storage.AsSpan(0, toRead - firstChunk).CopyTo(destination[firstChunk..]);
            }
            Volatile.Write(ref head, (currentHead + toRead) & 0x7FFFFFFF);
        }

        if (toRead < destination.Length)
        {
            destination[toRead..].Clear();
            Interlocked.Increment(ref underruns);
        }

        return toRead;
    }
}
