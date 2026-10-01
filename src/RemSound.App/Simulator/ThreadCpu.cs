using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RemSound.App.Simulator;

/// <summary>
/// The performance suite's meters (2026-09-28). CPU is the calling thread's OWN time, read from Windows' per-thread cycle
/// count (QueryThreadCycleTime) - not the wall clock - so whatever else the machine is doing stays out of the figure: a
/// thread that is not running is not counted. The cycle count is in the processor's time-stamp units, which tick at a
/// fixed rate whatever the clock speed does; <see cref="CyclesPerSecond"/> is that rate, measured once by spinning.
/// GetThreadTimes (the kernel's own tally, in 15.6 ms steps) is kept beside it as a cross-check.
/// </summary>
internal static class ThreadCpu
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessHandleCount(IntPtr process, out uint count);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();

    /// <summary>This thread's cycles so far.</summary>
    public static ulong Cycles()
    {
        QueryThreadCycleTime(GetCurrentThread(), out var c);
        return c;
    }

    /// <summary>This thread's user plus kernel time so far, in seconds (the kernel's tally: 15.6 ms steps).</summary>
    public static double TimesSeconds()
    {
        GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user);
        return (kernel + user) / 1e7;
    }

    private static double cyclesPerSecond;

    /// <summary>The rate the cycle count ticks at: spun for 40 ms five times on this thread, the fastest reading kept (a
    /// spin that lost the processor for a moment counts too few cycles for its time, never too many).</summary>
    public static double CyclesPerSecond
    {
        get
        {
            if (cyclesPerSecond > 0) return cyclesPerSecond;
            var best = 0.0;
            for (var i = 0; i < 5; i++)
            {
                var c0 = Cycles();
                var t0 = Stopwatch.GetTimestamp();
                while (Stopwatch.GetTimestamp() - t0 < Stopwatch.Frequency / 25) { }
                var c1 = Cycles();
                var seconds = (Stopwatch.GetTimestamp() - t0) / (double)Stopwatch.Frequency;
                best = Math.Max(best, (c1 - c0) / seconds);
            }
            cyclesPerSecond = best;
            return best;
        }
    }

    public static double ToSeconds(ulong cycles) => cycles / CyclesPerSecond;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemCpuSetInformation(byte[]? information, uint bufferLength, out uint returnedLength, IntPtr process, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetThreadSelectedCpuSets(IntPtr thread, uint[]? cpuSetIds, uint count);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetThreadInformation(IntPtr thread, int informationClass, ref PowerThrottlingState information, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    /// <summary>The logical processors of the fastest kind, and how many kinds there are. A hybrid processor (Intel's
    /// 12th generation on) has performance cores and efficiency cores, and the same work takes about half as long again on
    /// an efficiency core - measured here 2026-09-28: whole runs of benchmarks came out 1.5 times the others, all of a
    /// piece, as Windows moved the work between them. So a benchmark thread is held to the fastest kind.</summary>
    private static (uint[] Ids, int Kinds, int Fastest)? fastest;

    private static (uint[] Ids, int Kinds, int Fastest) FastestCpuSets()
    {
        if (fastest is { } known) return known;
        var result = (Ids: Array.Empty<uint>(), Kinds: 1, Fastest: 0);
        try
        {
            GetSystemCpuSetInformation(null, 0, out var needed, GetCurrentProcess(), 0);
            if (needed > 0)
            {
                var buffer = new byte[needed];
                if (GetSystemCpuSetInformation(buffer, needed, out var got, GetCurrentProcess(), 0))
                {
                    var sets = new List<(uint Id, byte Class)>();
                    for (var at = 0; at + 20 <= got;)
                    {
                        var size = BitConverter.ToInt32(buffer, at);
                        if (size <= 0) break;
                        var type = BitConverter.ToInt32(buffer, at + 4);
                        if (type == 0) sets.Add((BitConverter.ToUInt32(buffer, at + 8), buffer[at + 18]));
                        at += size;
                    }
                    if (sets.Count > 0)
                    {
                        var top = sets.Max(s => s.Class);
                        result = (sets.Where(s => s.Class == top).Select(s => s.Id).ToArray(), sets.Select(s => s.Class).Distinct().Count(), top);
                    }
                }
            }
        }
        catch { /* no CPU set information: leave the thread where Windows puts it */ }
        fastest = result;
        return result;
    }

    /// <summary>Hold this thread to the fastest kind of core (see <see cref="FastestCpuSets"/>), and ask Windows not to
    /// slow it down to save power (a thread of a window-less process can be put on an efficiency footing). Only this
    /// thread, and only while it lives: nothing of the machine's is changed. Returns what it did, for the report.</summary>
    public static string HoldToFastestCores()
    {
        var said = new List<string>();
        var (ids, kinds, top) = FastestCpuSets();
        if (kinds > 1 && ids.Length > 0 && SetThreadSelectedCpuSets(GetCurrentThread(), ids, (uint)ids.Length))
            said.Add($"held to the {ids.Length} logical processors of the fastest of {kinds} kinds of core (efficiency class {top})");
        else said.Add(kinds > 1 ? "could not be held to the fastest cores" : "one kind of core, so left where Windows puts it");
        var state = new PowerThrottlingState { Version = 1, ControlMask = 1, StateMask = 0 };   // execution speed: never throttled
        if (SetThreadInformation(GetCurrentThread(), 3 /* ThreadPowerThrottling */, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>()))
            said.Add("not slowed to save power");
        return string.Join(", ", said);
    }

    /// <summary>What <see cref="HoldToFastestCores"/> did on the last benchmark thread.</summary>
    public static string LastHold { get; set; } = "not held";

    /// <summary>The process's open handles, without building a Process object.</summary>
    public static int HandleCount() => GetProcessHandleCount(GetCurrentProcess(), out var n) ? (int)n : -1;

    /// <summary>The machine, as a baseline is keyed by it: the processor's name and its logical processors.</summary>
    public static string MachineKey()
    {
        var cpu = ProcessorName();
        return $"{cpu} x{Environment.ProcessorCount}";
    }

    /// <summary>The processor's own name, from the registry (where Windows keeps what the processor says it is).</summary>
    public static string ProcessorName()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("ProcessorNameString") is string name && name.Trim().Length > 0) return name.Trim();
        }
        catch { /* not readable: fall back */ }
        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown processor";
    }

    /// <summary>Whether this build's audio code runs optimised (a Release build) or not (Debug). A baseline is kept for each:
    /// a Debug build's audio path costs several times a Release one's, so the two can never be compared.</summary>
    public static string BuildKind()
    {
        var attribute = typeof(RemSound.Receiver.AudioReceiver).Assembly
            .GetCustomAttributes(typeof(DebuggableAttribute), false).OfType<DebuggableAttribute>().FirstOrDefault();
        return attribute is { IsJITOptimizerDisabled: true } ? "Debug" : "Release";
    }
}

/// <summary>The process as a soak or stress step watches it: the managed heap after a full collection, the handles, the
/// threads, and the private bytes.</summary>
internal readonly record struct ProcessState(long HeapBytes, int Handles, int Threads, long PrivateBytes)
{
    public double HeapMb => HeapBytes / 1048576.0;
    public double PrivateMb => PrivateBytes / 1048576.0;

    /// <summary>Everything the garbage collector can free, freed first (twice, around the finalisers), then read.</summary>
    public static ProcessState Sample()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var heap = GC.GetTotalMemory(false);
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return new ProcessState(heap, ThreadCpu.HandleCount(), p.Threads.Count, p.PrivateMemorySize64);
    }

    /// <summary>A sample taken again (a quarter of a second apart, for up to three seconds) while it shows more handles or
    /// threads than <paramref name="before"/>, keeping the lowest: the runtime's own workers - the compiler's background
    /// thread, the pool - come and go on their own, and one that is just finishing is not a leak (seen 2026-09-28: a Debug
    /// build's stress run ended one thread and five handles up, gone a moment later). A leak does not go away.</summary>
    public static ProcessState Settled(ProcessState before)
    {
        var best = Sample();
        for (var i = 0; i < 12 && (best.Handles > before.Handles || best.Threads > before.Threads); i++)
        {
            Thread.Sleep(250);
            var again = Sample();
            if (again.Handles + again.Threads < best.Handles + best.Threads) best = again;
        }
        return best;
    }

    public override string ToString() => $"heap {HeapMb:0.0} MB, {Handles} handles, {Threads} threads, private {PrivateMb:0} MB";
}
