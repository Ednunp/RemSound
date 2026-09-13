using System.Diagnostics;
using RemSound.Core;

namespace RemSound.Receiver;

/// <summary>
/// When each output lane was last actually READ, so the engine can tell a lane that is switched on
/// from a lane that is doing anything.
///
/// <para><b>Why this exists.</b> Ed, 2026-09-07, 22:19:19. Both output lanes were live on his
/// receiving machine. The ASIO lane stopped rendering — <c>renderAsioMs</c> went to 0.0 and never came
/// back — while its output stayed ticked, so every flag in the app still said the lane was active. The
/// engine went on fanning every stream out to it, and that copy overflowed at 288,960 bytes a second
/// for hours. It sawtoothed between 275 and 800 ms against the one-second safety cap, and the CPU
/// burned feeding a lane nobody was listening to starved the lane he WAS listening to: render cost on
/// the surviving lane went from about 5 ms a second to between 60 and 140. Every starve is an
/// underrun, and every underrun makes the auto-tune raise the buffer. A lane he had finished with made
/// the lane he was using audibly late.</para>
///
/// <para><b>Why ticked is not the right question.</b> The obvious fix — tear the lane down when its
/// output is unticked — was already there and already worked; the gate proved it on the first run.
/// Ed's ASIO output was never unticked. A lane can stop consuming while every flag still says it is
/// there: the far end stops sending on that lane, the driver opens with no output channel pairs
/// mapped, the device stalls without raising an error. None of those tell anyone anything. So the
/// question the engine has to ask is not "is this lane selected" but "is this lane taking audio", and
/// that is only answerable by watching the reads.</para>
///
/// <para>A lane that has never been read at all is treated as consuming. Nothing may be torn down
/// before its first render callback has had a chance to arrive — a device that takes a moment to open
/// is normal, and killing it during that moment would turn a slow start into no audio.</para>
/// </summary>
internal sealed class LaneActivity
{
    /// <summary>How long a lane may go unread before it stops being fed. Comfortably longer than any
    /// legitimate gap between render callbacks (a lumpy WASAPI card runs ~20 ms, a laggy one under
    /// load a few hundred), and short enough that the runaway is measured in seconds rather than the
    /// hours it ran for in the field.</summary>
    public const double DefaultStallSeconds = 3.0;

    private readonly long[] lastReadTicks = new long[8];
    private readonly long stallTicks;

    /// <param name="stallSeconds">Overridable ONLY so the gate can measure this in milliseconds
    /// instead of seconds. Every shipped caller takes the default, and the test pins that.</param>
    public LaneActivity(double stallSeconds = DefaultStallSeconds)
        => stallTicks = (long)(stallSeconds * Stopwatch.Frequency);

    /// <summary>This lane was just read. Called at the top of every per-route render read; a couple of
    /// nanoseconds on the audio thread and no lock.</summary>
    public void Note(RenderRoute route)
        => Volatile.Write(ref lastReadTicks[(int)route & 7], Stopwatch.GetTimestamp());

    /// <summary>Is this lane still taking audio? True if it has been read within the stall window, or
    /// if it has never been read at all — see the class note on not killing a lane before it starts.</summary>
    public bool IsConsuming(RenderRoute route)
    {
        var stamp = Volatile.Read(ref lastReadTicks[(int)route & 7]);
        if (stamp == 0) return true;   // never read yet — give it its chance
        return Stopwatch.GetTimestamp() - stamp <= stallTicks;
    }

    /// <summary>Forget this lane's history, so it is treated as never-read and gets a clean chance
    /// again. Used when a lane's device set changes.</summary>
    public void Forget(RenderRoute route) => Volatile.Write(ref lastReadTicks[(int)route & 7], 0);

    /// <summary>Push a lane's last-read stamp into the past so it reads as stalled immediately. Public
    /// to the assembly only so the gate can drive a stall without sleeping for the real window.</summary>
    internal void RewindForTest(RenderRoute route, double seconds)
        => Volatile.Write(ref lastReadTicks[(int)route & 7],
            Stopwatch.GetTimestamp() - (long)(seconds * Stopwatch.Frequency));
}
