using System.Runtime.InteropServices;

namespace RemSound.Core;

/// <summary>
/// Boosts the calling thread to MMCSS Pro Audio class plus ThreadPriority.Highest.
/// Dispose on the same thread that constructed it. Designed for capture/render/network audio threads.
/// </summary>
public sealed class WindowsAudioThreadBoost : IDisposable
{
    private readonly IntPtr avrtHandle;
    private readonly ThreadPriority previousPriority;
    private readonly int ownerThreadId;

    /// <summary>ERROR_TOO_MANY_THREADS: Windows lets one process hold MMCSS on at most 32 threads at once - measured
    /// 2026-09-29, the 33rd refused, and a thread that exits gives its registration back.</summary>
    private const int TooManyThreads = 565;
    private static int live;

    /// <summary>How many threads hold an MMCSS registration through this class right now, in this process.</summary>
    public static int LiveRegistrations => Volatile.Read(ref live);

    public WindowsAudioThreadBoost(string taskName)
    {
        ownerThreadId = Environment.CurrentManagedThreadId;
        previousPriority = Thread.CurrentThread.Priority;
        Thread.CurrentThread.Priority = ThreadPriority.Highest;
        Mode = "ThreadPriority.Highest";

        if (!OperatingSystem.IsWindows()) return;

        avrtHandle = AvSetMmThreadCharacteristics(taskName, out _);
        var refused = avrtHandle == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        if (avrtHandle == IntPtr.Zero && !string.Equals(taskName, "Audio", StringComparison.OrdinalIgnoreCase))
        {
            avrtHandle = AvSetMmThreadCharacteristics("Audio", out _);
            if (avrtHandle != IntPtr.Zero) taskName = "Audio";
            else refused = Marshal.GetLastWin32Error();
        }
        // Say why MMCSS said no, so a log line reading "ThreadPriority.Highest" on an audio thread explains itself.
        if (avrtHandle == IntPtr.Zero)
            Mode = refused == TooManyThreads
                ? $"ThreadPriority.Highest (MMCSS refused: {LiveRegistrations} threads of this process already hold it, Windows allows 32)"
                : $"ThreadPriority.Highest (MMCSS refused, win32={refused})";

        if (avrtHandle != IntPtr.Zero)
        {
            Interlocked.Increment(ref live);
            // Critical sits one notch above the MMCSS task's default priority (High).
            // It's still well below RealTime — the OS keeps a reservation for system
            // services above us so the audio stack and screen reader can't be starved.
            // Empirically the bump helps the receive listener and the render thread when
            // the machine is also doing other foreground work (browser, NVDA reading a
            // page) by getting us off the queue ahead of those tasks' worker threads.
            AvSetMmThreadPriority(avrtHandle, AvrtPriority.Critical);
            Mode = $"MMCSS {taskName} (priority Critical)";
        }
    }

    public string Mode { get; }

    public void Dispose()
    {
        if (Environment.CurrentManagedThreadId != ownerThreadId) return;
        if (avrtHandle != IntPtr.Zero && AvRevertMmThreadCharacteristics(avrtHandle)) Interlocked.Decrement(ref live);
        Thread.CurrentThread.Priority = previousPriority;
    }

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "AvSetMmThreadCharacteristicsW")]
    private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, out uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvSetMmThreadPriority(IntPtr avrtHandle, AvrtPriority priority);

    [DllImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);

    private enum AvrtPriority
    {
        Low = -1,
        Normal = 0,
        High = 1,
        Critical = 2,
    }
}
