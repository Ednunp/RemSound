using RemSound.Core;

// Lets the in-app self-test (--selftest, in RemSound.exe) exercise the internal reassembly and
// decryptor paths directly — same pattern as the Sender and Core assemblies.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("RemSound")]

namespace RemSound.Receiver;

/// <summary>
/// Assembles multi-part PCM transport frames back into a single contiguous payload.
/// PCM frames at 48 kHz × 24-bit × 2 ch × 10 ms = 2880 bytes, split into 2 UDP parts.
///
/// On a healthy LAN, parts arrive in order. If a part is missed we drop the whole frame
/// rather than wait — at 10 ms cadence, waiting more than ~5 ms is worse than a single dropped frame.
/// </summary>
internal sealed class PcmFrameAssembler
{
    private uint pendingFrameId;
    private byte pendingPartIndex; // index of the NEXT expected part
    private byte pendingTotalParts;
    private readonly byte[] assemblyBuffer = new byte[8192]; // largest reasonable PCM frame
    private int assemblyWritten;
    private long rejectionCount;
    private long discardedPartialCount;

    /// <summary>
    /// Number of incoming parts that were rejected outright (out-of-order, malformed, overflow).
    /// Each rejection means at least one PCM frame's audio is lost.
    /// </summary>
    public long RejectionCount => Interlocked.Read(ref rejectionCount);

    /// <summary>
    /// Number of partially-assembled frames discarded because a new frame started before the
    /// previous one's parts all arrived. Each discard means a half-finished frame's audio is lost.
    /// </summary>
    public long DiscardedPartialCount => Interlocked.Read(ref discardedPartialCount);

    public bool TryAssemble(ReadOnlySpan<byte> partBytes, uint frameId, byte partIndex, byte totalParts, out ReadOnlySpan<byte> assembled)
    {
        assembled = default;

        if (totalParts == 0)
        {
            Interlocked.Increment(ref rejectionCount);
            return false;
        }

        // First part of a new frame? Start fresh, regardless of whether the previous one finished.
        if (partIndex == 0)
        {
            // If we had a partial frame waiting, count it as a discard — its audio is lost.
            if (pendingTotalParts != 0 && assemblyWritten > 0)
            {
                Interlocked.Increment(ref discardedPartialCount);
            }
            pendingFrameId = frameId;
            pendingPartIndex = 0;
            pendingTotalParts = totalParts;
            assemblyWritten = 0;
        }
        else if (frameId != pendingFrameId || partIndex != pendingPartIndex || totalParts != pendingTotalParts)
        {
            // Mismatch — we missed the start, or this is from a different frame. Discard.
            assemblyWritten = 0;
            pendingTotalParts = 0;
            Interlocked.Increment(ref rejectionCount);
            return false;
        }

        if (assemblyWritten + partBytes.Length > assemblyBuffer.Length)
        {
            // Frame larger than expected — defensive, should never happen with our packetization.
            assemblyWritten = 0;
            pendingTotalParts = 0;
            Interlocked.Increment(ref rejectionCount);
            return false;
        }

        partBytes.CopyTo(assemblyBuffer.AsSpan(assemblyWritten));
        assemblyWritten += partBytes.Length;
        pendingPartIndex++;

        if (pendingPartIndex == pendingTotalParts)
        {
            assembled = assemblyBuffer.AsSpan(0, assemblyWritten);
            // Reset for next frame after the caller consumes.
            pendingTotalParts = 0;
            assemblyWritten = 0;
            return true;
        }

        return false;
    }

    public void Reset()
    {
        pendingFrameId = 0;
        pendingPartIndex = 0;
        pendingTotalParts = 0;
        assemblyWritten = 0;
    }
}
