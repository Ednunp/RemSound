namespace RemSound.Sender;

/// <summary>
/// The two decisions a WASAPI capture loop makes — how long to wait for the device, and how much to
/// take when it wakes.
///
/// <para><b>Why these are ours now.</b> Until 2026-09-09 this loop lived inside NAudio's
/// <c>WasapiCapture.DoRecording</c> and RemSound simply subclassed it. NAudio waits <b>three</b>
/// buffer-lengths for the device's event and then drains <b>one</b> buffer. On a card whose loopback
/// event fires every period that is fine — the wait returns immediately each time and nothing is
/// missed.</para>
///
/// <para>On a card whose loopback event never signals at all, it is a disaster, and the arithmetic is
/// exact: the wait times out after three buffers, one buffer is taken, and WASAPI has already
/// overwritten the other two. One in three. That is the 33 % an outside report measured in September
/// 2026 — audio that never left the sending machine, on hardware where this had been happening since
/// the code was written. Initialising the client still succeeds and nothing raises an error, so there
/// was no way to see it from the sending end.</para>
///
/// <para>A bug in a loop we did not own could not be tested. That is the real reason to bring it
/// in-house: not because NAudio's loop is badly written — it is a reasonable loop for a card that
/// signals — but because a capture path with no seam is a capture path nobody can prove anything
/// about, and this one was wrong for years.</para>
/// </summary>
internal static class WasapiCaptureLoop
{
    /// <summary>
    /// How long to wait for the device event before looking anyway.
    ///
    /// <para>Half a buffer, not three. The point is that the wait is a CEILING rather than a
    /// schedule: where the event fires we are woken at the device's own clock long before this
    /// expires, so a good card behaves exactly as it always did. Where it never fires, half a buffer
    /// guarantees we look while the data is still there — the same safety margin NAudio's own
    /// non-event path uses.</para>
    ///
    /// <para>Floored at 2 ms so a tiny buffer cannot spin the thread, capped at 50 ms so a very large
    /// one still gets looked at often enough to matter.</para>
    /// </summary>
    public const int MinPollMs = 2;
    public const int MaxPollMs = 50;

    public static int PollTimeoutMs(int bufferMs) => Math.Clamp(bufferMs / 2, MinPollMs, MaxPollMs);

    /// <summary>
    /// Take EVERYTHING the device has queued, not one packet.
    ///
    /// <para>This is the half that actually fixes it. Waking often is no use if each wake takes one
    /// packet and leaves the rest to be overwritten; conversely, draining fully makes the loop robust
    /// to a late wake-up for any reason — a scheduler stall, a GC pause, a busy machine — not just to
    /// an event that never fires.</para>
    /// </summary>
    /// <returns>Total frames taken this pass.</returns>
    public static int Drain(ICapturePacketReader reader)
    {
        var total = 0;
        while (reader.NextPacketFrames > 0)
        {
            var frames = reader.ConsumePacket();
            if (frames <= 0) break;   // a reader that says it has data and then gives none must not spin
            total += frames;
        }
        return total;
    }
}

/// <summary>The sliver of a WASAPI capture client the loop actually touches, so the loop can be driven
/// by a simulated device in the gate instead of only by real hardware.</summary>
internal interface ICapturePacketReader
{
    /// <summary>Frames in the next queued packet, or 0 when the device has nothing waiting.</summary>
    int NextPacketFrames { get; }

    /// <summary>Take the next packet and hand it on. Returns the frames taken.</summary>
    int ConsumePacket();
}
