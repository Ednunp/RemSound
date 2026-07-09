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
