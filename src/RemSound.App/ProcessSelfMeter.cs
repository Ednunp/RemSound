using System;
using System.Diagnostics;

namespace RemSound.App;

/// <summary>
/// Process-wide CPU / memory / allocation / GC meter. Sampled once a second by the diag-
/// log emitter (gated behind <see cref="RemSound.Core.DiagnosticsGate"/>) so we get a
/// continuous baseline of "how heavy is RemSound right now?" alongside every audio-pipeline
/// stat we already track. Item 1 + 3 of <c>RemSoundefficiency.md</c> — the "build the
/// measurement layer first" finding.
///
/// All readings are deltas since the previous <see cref="Take"/> call, so consumers see
/// "this second's CPU" not "since process start". The first call returns zeros for the
/// delta-based fields (no previous sample to compare to) and the steady-state fields
/// already meaningful at that point (memory, working set).
///
/// Threading: <see cref="Take"/> is called from the App's status-tick handler on the UI
/// thread. Snapshot fields are mutated by that same single thread; no locks needed.
/// </summary>
internal sealed class ProcessSelfMeter : IDisposable
{
    private TimeSpan prevTotalCpu;
    private long prevAllocBytes;
    private DateTime prevSampleUtc;
    // Cached Process handle. Process.GetCurrentProcess() allocates a new object each call
    // and the underlying handle is the same for the process lifetime — caching it saves an
    // allocation per Take.
    private readonly Process selfProcess = Process.GetCurrentProcess();

    /// <summary>One-second meter reading.</summary>
    /// <param name="CpuPercentOneCore">CPU used in the last sample interval as a percentage
    /// of one CPU core (so a fully-loaded core reads 100, two cores read 200, etc.). Zero
    /// on the first call (no previous sample). Includes time across all of the app's
    /// threads — kernel + user.</param>
    /// <param name="ManagedHeapMb">Managed heap occupancy in megabytes right now. The
    /// .NET garbage collector's view of "stuff RemSound is holding"; doesn't include
    /// unmanaged buffers held via NAudio / Concentus / etc.</param>
    /// <param name="WorkingSetMb">Working set in megabytes — what Task Manager shows for
    /// the process. Includes managed heap, unmanaged buffers, and pages currently resident.</param>
    /// <param name="AllocatedKbPerSecond">Bytes allocated to the managed heap in this
    /// interval, divided by 1024 and normalised to per-second. A steady-state RemSound
    /// should run in the single-digit-kilobytes-per-second range; sustained megabytes is
    /// a leak somewhere in the hot path.</param>
    /// <param name="ElapsedMs">Wall-clock milliseconds since the previous sample, so the
    /// caller can sanity-check the delta calculation. Roughly 1000 in steady state.</param>
    /// <param name="PrivateBytesMb">Private committed bytes (PrivateMemorySize64) — managed heap
    /// PLUS native heap allocations, minus shared/file-backed pages. The honest "how much memory
    /// is this process actually holding" figure. If this climbs while ManagedHeapMb stays flat,
    /// the leak is native (NAudio / a driver), not .NET objects.</param>
    /// <param name="GcHeapMb">Managed heap size the GC reports right now
    /// (GCMemoryInfo.HeapSizeBytes), including free gaps. Climbing here = a managed leak.</param>
    /// <param name="GcFragmentedMb">Free-but-uncompacted bytes inside the managed heap
    /// (GCMemoryInfo.FragmentedBytes). Under SustainedLowLatency (no gen2 compaction) this is the
    /// signal for "the leak is GC fragmentation" — it would grow while GcHeapMb roughly tracks it
    /// and ManagedHeapMb (live set) stays flat. A compacting GC would reclaim it.</param>
    /// <param name="GcCommittedMb">Total bytes the GC has committed from the OS
    /// (GCMemoryInfo.TotalCommittedBytes). The managed share of the working set.</param>
    /// <param name="HandleCount">Open OS handle count. A steady climb here = a handle leak
    /// (e.g. audio clients / events created and never released), which inflates native memory
    /// without touching the managed heap.</param>
    /// <param name="ThreadCount">Process thread count. A climb = a thread leak (each thread costs
    /// ~1 MB of stack), another way native/working-set memory grows with the managed heap flat.</param>
    public readonly record struct Snapshot(
        double CpuPercentOneCore,
        double ManagedHeapMb,
        double WorkingSetMb,
        double AllocatedKbPerSecond,
        double ElapsedMs,
        double PrivateBytesMb,
        double GcHeapMb,
        double GcFragmentedMb,
        double GcCommittedMb,
        int HandleCount,
        int ThreadCount);

    public Snapshot Take()
    {
        var now = DateTime.UtcNow;
        // TotalProcessorTime is "user + kernel time across every thread", refreshed lazily.
        // Refresh() asks the OS for the current value; without it the property is sticky
        // from the first access. Done explicitly so the math below is meaningful.
        selfProcess.Refresh();
        var totalCpu = selfProcess.TotalProcessorTime;
        var workingSet = selfProcess.WorkingSet64;
        // GC.GetTotalAllocatedBytes(precise: true) is the official .NET counter for
        // "total bytes allocated across all threads since process start". precise: true
        // forces a fast cross-thread sync; the cost is a thread-list walk (cheap). We
        // need precise=true because the audio threads allocate too and we want their
        // contribution included.
        var totalAllocBytes = GC.GetTotalAllocatedBytes(precise: true);
        // GetTotalMemory(false) doesn't trigger a collection; we just want the current
        // size of the heap as the GC knows it.
        var managedHeapBytes = GC.GetTotalMemory(false);

        // Leak-classification extras (added 2026-06-06 for the ONJTOP unmanaged-growth hunt).
        // All cheap, all read once per second, all behind the diag/log gate via the caller.
        //   * PrivateMemorySize64 — committed managed + native, the "is the process really
        //     holding more" number. Native leak ⇒ this climbs with the managed heap flat.
        //   * GCMemoryInfo — HeapSize / Fragmented / Committed distinguish a managed leak or
        //     SustainedLowLatency fragmentation from a genuine native leak.
        //   * Handle / thread counts — catch a handle or thread leak (e.g. WASAPI clients/events
        //     created per glitch and never released) that grows native memory off-heap.
        var privateBytes = selfProcess.PrivateMemorySize64;
        var gcInfo = GC.GetGCMemoryInfo();
        int handleCount;
        try { handleCount = selfProcess.HandleCount; } catch { handleCount = 0; }
        int threadCount;
        try { threadCount = selfProcess.Threads.Count; } catch { threadCount = 0; }

        double cpuPercent = 0;
        double allocKbps = 0;
        double elapsedMs = 0;
        if (prevSampleUtc != default)
        {
            elapsedMs = (now - prevSampleUtc).TotalMilliseconds;
            if (elapsedMs > 0)
            {
                var cpuDeltaMs = (totalCpu - prevTotalCpu).TotalMilliseconds;
                cpuPercent = cpuDeltaMs / elapsedMs * 100.0;
                var allocDelta = totalAllocBytes - prevAllocBytes;
                allocKbps = allocDelta / 1024.0 * (1000.0 / elapsedMs);
            }
        }

        prevTotalCpu = totalCpu;
        prevAllocBytes = totalAllocBytes;
        prevSampleUtc = now;

        return new Snapshot(
            CpuPercentOneCore: cpuPercent,
            ManagedHeapMb: managedHeapBytes / (1024.0 * 1024.0),
            WorkingSetMb: workingSet / (1024.0 * 1024.0),
            AllocatedKbPerSecond: allocKbps,
            ElapsedMs: elapsedMs,
            PrivateBytesMb: privateBytes / (1024.0 * 1024.0),
            GcHeapMb: gcInfo.HeapSizeBytes / (1024.0 * 1024.0),
            GcFragmentedMb: gcInfo.FragmentedBytes / (1024.0 * 1024.0),
            GcCommittedMb: gcInfo.TotalCommittedBytes / (1024.0 * 1024.0),
            HandleCount: handleCount,
            ThreadCount: threadCount);
    }

    /// <summary>Release the cached Process handle deterministically rather than at GC finalization —
    /// small, but consistent with the app's resource-lifecycle discipline. Called when MainForm closes.</summary>
    public void Dispose() => selfProcess.Dispose();
}
