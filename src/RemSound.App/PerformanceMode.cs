using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RemSound.App;

/// <summary>
/// "Full CPU speed" mode for the current profile. When enabled, pulls every documented
/// Windows lever to keep our process running at full clock — no EcoQoS downclocking, no
/// migration to E-cores, no deep-C-state idling, 1 ms scheduler quantum, High priority
/// class, normal memory priority, a locked working-set floor. Every mechanism is scoped to
/// this process: nothing affects other apps,
/// and nothing here changes the system power plan (which is global) or anyone else's
/// scheduling. Untoggling reverses every change cleanly.
///
/// Mechanisms layered on:
/// <list type="number">
///   <item><b>Process power throttling — opt out of EXECUTION_SPEED</b>. This is the
///         main one. Tells Windows' scheduler "this is not a background process; don't
///         downclock it, don't migrate its threads onto efficiency cores on hybrid CPUs".
///         Without this, Windows aggressively saves power on processes it thinks are
///         idle, which is the root cause of the "cold start sounds awful, warms up after
///         a few seconds" behaviour the user reported. Same API Discord, Spotify, OBS
///         and similar real-time apps use.</item>
///   <item><b>PowerSetRequest(EXECUTION_REQUIRED)</b>. Tells the kernel the system as a
///         whole can't enter deep low-power C-states while this process is active. Belt
///         and braces with #1 — covers the case where #1 isn't honoured (very old build
///         of Windows 10, some custom OEM image, etc.).</item>
///   <item><b>Process priority class = High</b>. Standard for low-latency audio apps;
///         bumps our dispatch priority above almost everything except OS internals. We
///         deliberately do <i>not</i> use Realtime — that class can starve OS services
///         (including the audio service itself) and is widely documented as causing
///         worse audio behaviour, not better.</item>
///   <item><b>timeBeginPeriod(1)</b>. Asks Windows for a 1 ms scheduler quantum. Helps
///         any code path that does timer-based waiting. Since Windows 10 build 1803,
///         this is scoped per-process — so we don't damage other apps' scheduling. We
///         match every <c>timeBeginPeriod</c> with a <c>timeEndPeriod</c> when the
///         feature is disabled, otherwise the OS keeps the elevated rate forever (the
///         old global-period gotcha is long gone but we're tidy anyway).</item>
///   <item><b>PowerSetRequest(SYSTEM_REQUIRED)</b>, on the same request as #2; prevents the
///         system from idle-sleeping. Negligible cost. No display request — we don't need the
///         screen on while we're running, just the CPU. This used to be SetThreadExecutionState,
///         which belongs to the THREAD that calls it: the service switches this mode on and off
///         from different threads, so switching it off could leave "system required" set on
///         another thread for good. A power request belongs to the process. 2026-09-13 review.</item>
///   <item><b>Memory priority = Normal</b>. Windows can demote a process that looks idle, and a
///         demoted process has its pages trimmed first; this asserts the normal priority.</item>
///   <item><b>Minimum working set locked (32 MB)</b>, so Windows can't trim the process during a
///         quiet spell and stall the first frames that follow it. Released when the mode is
///         switched off.</item>
/// </list>
///
/// Trade-offs the user should know about:
/// <list type="bullet">
///   <item>Laptop on battery: faster drain while this is engaged. The app engages it only while
///         audio is moving and releases it after a quiet hold-down
///         (<see cref="MainForm.PriorityModeHoldDown"/>, 30 s); the service engages it whenever it
///         is sending. Worth the energy for a "live session" profile; not worth it for a
///         "background listening" one. That's why the app's switch lives on the Profile, not on
///         AppConfig.</item>
///   <item>Desktop on mains: usually a couple of watts more, no practical downside.</item>
/// </list>
///
/// Idempotent — calling <see cref="Apply"/> with the same value twice is a no-op. The
/// internal state tracks the active power request handle so <see cref="Apply(bool, Action{string})"/>
/// with <c>enable: false</c> releases every resource it acquired. Safe to call from the
/// UI thread; none of the underlying API calls block in any meaningful sense.
/// </summary>
internal static class PerformanceMode
{
    private static readonly object gate = new();
    private static bool currentlyEnabled;
    private static IntPtr powerRequestHandle = IntPtr.Zero;
    private static ProcessPriorityClass? priorityBeforeBoost;

    /// <summary>Apply or reverse Full-CPU-speed mode. <paramref name="log"/> receives a
    /// short status line so the activation is visible in the diagnostic log file.
    /// Failures on individual mechanisms (e.g. an OEM image rejecting one of the Win32
    /// calls) are logged but don't abort the rest of the application of the mode.</summary>
    public static void Apply(bool enable, Action<string>? log = null)
    {
        lock (gate)
        {
            if (enable == currentlyEnabled)
            {
                log?.Invoke($"performance mode: already {(enable ? "on" : "off")}, no change");
                return;
            }

            if (enable)
            {
                TrySetPowerThrottling(disable: true, log);
                TryStartPowerRequest(log);
                TryRaisePriority(log);
                TryBeginTimePeriod(log);
                TrySetMemoryPriorityNormal(log);
                TryLockWorkingSetMin(log);
                currentlyEnabled = true;
                log?.Invoke("priority mode: ON (EcoQoS off, timer resolution honored, power-request set, priority High, 1 ms quantum, system-required, memory priority normal, working-set min locked)");
            }
            else
            {
                TrySetPowerThrottling(disable: false, log);
                TryStopPowerRequest(log);
                TryRestorePriority(log);
                TryEndTimePeriod(log);
                TryRelaxWorkingSet(log);
                currentlyEnabled = false;
                log?.Invoke("priority mode: OFF (all overrides cleared, defaults restored)");
            }
        }
    }

    // === 1. Process power throttling (EcoQoS opt-out + timer-resolution honoring) ===

    private static void TrySetPowerThrottling(bool disable, Action<string>? log)
    {
        try
        {
            // We control two flags at once:
            //   * EXECUTION_SPEED — StateMask=0 means "don't throttle this process"
            //     (no downclock, no E-core migration). The main lever.
            //   * IGNORE_TIMER_RESOLUTION — StateMask=0 means "honour my
            //     timeBeginPeriod request even if other apps don't want fine grain".
            //     Pairs naturally with the timeBeginPeriod(1) call further down.
            // When we revert (disable=false) we set ControlMask=0, which hands both
            // flags back to Windows' system-default decision-making rather than
            // forcing them on.
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = ProcessPowerThrottlingCurrentVersion,
                ControlMask = disable
                    ? (ProcessPowerThrottlingExecutionSpeed | ProcessPowerThrottlingIgnoreTimerResolution)
                    : 0u,
                StateMask = 0u,
            };
            var ok = SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling,
                ref state, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
            if (!ok) log?.Invoke($"priority mode: SetProcessInformation(power throttling) failed (win32={Marshal.GetLastWin32Error()})");
        }
        catch (Exception ex)
        {
            log?.Invoke($"priority mode: power throttling threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // === 2. Kernel power request (no deep C-states) ===

    private static void TryStartPowerRequest(Action<string>? log)
    {
        try
        {
            var ctx = new REASON_CONTEXT
            {
                Version = PowerRequestContextVersion,
                Flags = PowerRequestContextSimpleString,
                Reason = "RemSound is running with Full CPU speed enabled.",
            };
            var handle = PowerCreateRequest(ref ctx);
            if (handle == new IntPtr(-1))
            {
                log?.Invoke($"performance mode: PowerCreateRequest failed (win32={Marshal.GetLastWin32Error()})");
                return;
            }
            if (!PowerSetRequest(handle, PowerRequestExecutionRequired))
            {
                log?.Invoke($"performance mode: PowerSetRequest failed (win32={Marshal.GetLastWin32Error()})");
                CloseHandle(handle);
                return;
            }
            // Keep the system out of idle sleep on the same handle (the class remarks say why not SetThreadExecutionState).
            // A failure here leaves the execution request in place.
            if (!PowerSetRequest(handle, PowerRequestSystemRequired))
                log?.Invoke($"performance mode: PowerSetRequest(system required) failed (win32={Marshal.GetLastWin32Error()})");
            powerRequestHandle = handle;
        }
        catch (Exception ex)
        {
            log?.Invoke($"performance mode: power request threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void TryStopPowerRequest(Action<string>? log)
    {
        if (powerRequestHandle == IntPtr.Zero) return;
        try
        {
            PowerClearRequest(powerRequestHandle, PowerRequestSystemRequired);
            PowerClearRequest(powerRequestHandle, PowerRequestExecutionRequired);
            CloseHandle(powerRequestHandle);
        }
        catch (Exception ex)
        {
            log?.Invoke($"performance mode: power-request release threw: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            powerRequestHandle = IntPtr.Zero;
        }
    }

    // === 3. Process priority class ===

    private static void TryRaisePriority(Action<string>? log)
    {
        try
        {
            using var proc = Process.GetCurrentProcess();
            priorityBeforeBoost = proc.PriorityClass;
            proc.PriorityClass = ProcessPriorityClass.High;
        }
        catch (Exception ex)
        {
            log?.Invoke($"performance mode: priority raise threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void TryRestorePriority(Action<string>? log)
    {
        if (priorityBeforeBoost is null) return;
        try
        {
            using var proc = Process.GetCurrentProcess();
            proc.PriorityClass = priorityBeforeBoost.Value;
        }
        catch (Exception ex)
        {
            log?.Invoke($"performance mode: priority restore threw: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            priorityBeforeBoost = null;
        }
    }

    // === 4. timeBeginPeriod / timeEndPeriod ===

    private static bool timePeriodActive;

    private static void TryBeginTimePeriod(Action<string>? log)
    {
        if (timePeriodActive) return;
        try
        {
            if (timeBeginPeriod(1) == 0)
            {
                timePeriodActive = true;
            }
            else
            {
                log?.Invoke("performance mode: timeBeginPeriod(1) failed");
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"performance mode: timeBeginPeriod threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void TryEndTimePeriod(Action<string>? log)
    {
        if (!timePeriodActive) return;
        try
        {
            timeEndPeriod(1);
        }
        catch (Exception ex)
        {
            log?.Invoke($"performance mode: timeEndPeriod threw: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            timePeriodActive = false;
        }
    }

    // === 6. Process memory priority ===

    // Setting our process's memory priority explicitly to NORMAL. The OS default is
    // already NORMAL (5), but Windows can demote "background-looking" processes
    // (especially after long idle periods or under memory pressure), and a demoted
    // process gets its working-set pages evicted first when the trimmer runs. This
    // call is a defensive assertion: "I am a foreground-class process, treat my
    // pages accordingly".
    private static void TrySetMemoryPriorityNormal(Action<string>? log)
    {
        try
        {
            var info = new MEMORY_PRIORITY_INFORMATION { MemoryPriority = MemoryPriorityNormal };
            var ok = SetProcessInformationMemory(GetCurrentProcess(), ProcessMemoryPriority,
                ref info, Marshal.SizeOf<MEMORY_PRIORITY_INFORMATION>());
            if (!ok) log?.Invoke($"priority mode: SetProcessInformation(memory) failed (win32={Marshal.GetLastWin32Error()})");
        }
        catch (Exception ex)
        {
            log?.Invoke($"priority mode: memory priority threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // === 7. Lock the minimum working-set size ===

    // Windows trims a process's working set proactively when the machine looks idle.
    // For RemSound that's exactly the moment we'd rather it didn't — the first packet
    // after an idle stretch hits a cold cache and pages have to be brought back in,
    // which audibly stalls the first frames. Locking a minimum working set (32 MB —
    // enough for the JIT'd code + audio scratch buffers plus headroom for NAudio's
    // and the GC's reserves) tells Windows "you may not trim below this floor".
    // Released cleanly when priority mode is turned off.
    private const long MinWorkingSetBytes = 32L * 1024 * 1024;

    private static void TryLockWorkingSetMin(Action<string>? log)
    {
        try
        {
            // dwMaximumWorkingSetSize = -1 means "leave unchanged".
            var min = (IntPtr)MinWorkingSetBytes;
            var maxUnchanged = (IntPtr)(-1);
            if (!SetProcessWorkingSetSizeEx(GetCurrentProcess(), min, maxUnchanged, QUOTA_LIMITS_HARDWS_MIN_ENABLE))
            {
                log?.Invoke($"priority mode: SetProcessWorkingSetSizeEx(lock) failed (win32={Marshal.GetLastWin32Error()})");
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"priority mode: working-set lock threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void TryRelaxWorkingSet(Action<string>? log)
    {
        try
        {
            var unchanged = (IntPtr)(-1);
            if (!SetProcessWorkingSetSizeEx(GetCurrentProcess(), unchanged, unchanged, QUOTA_LIMITS_HARDWS_MIN_DISABLE))
            {
                log?.Invoke($"priority mode: SetProcessWorkingSetSizeEx(unlock) failed (win32={Marshal.GetLastWin32Error()})");
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"priority mode: working-set unlock threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // === Native interop ===

    private const int ProcessMemoryPriority = 0; // PROCESS_INFORMATION_CLASS.ProcessMemoryPriority
    private const int ProcessPowerThrottling = 4; // PROCESS_INFORMATION_CLASS.ProcessPowerThrottling
    private const uint ProcessPowerThrottlingCurrentVersion = 1;
    private const uint ProcessPowerThrottlingExecutionSpeed = 0x1;
    private const uint ProcessPowerThrottlingIgnoreTimerResolution = 0x4;
    private const int PowerRequestSystemRequired = 1;
    private const int PowerRequestExecutionRequired = 3;
    private const uint PowerRequestContextVersion = 0;
    private const uint PowerRequestContextSimpleString = 0x1;
    private const uint MemoryPriorityNormal = 5;
    private const int QUOTA_LIMITS_HARDWS_MIN_ENABLE = 0x1;
    private const int QUOTA_LIMITS_HARDWS_MIN_DISABLE = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_PRIORITY_INFORMATION
    {
        public uint MemoryPriority;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct REASON_CONTEXT
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string Reason;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr hProcess, int ProcessInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE ProcessInformation, int ProcessInformationSize);

    // Same kernel32 entry point as SetProcessInformation, but with a different struct type
    // in the ref parameter — declare a separate P/Invoke so the marshaller picks the right
    // signature without us needing to manually pin and StructureToPtr the buffer.
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetProcessInformation")]
    private static extern bool SetProcessInformationMemory(IntPtr hProcess, int ProcessInformationClass,
        ref MEMORY_PRIORITY_INFORMATION ProcessInformation, int ProcessInformationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSizeEx(IntPtr hProcess,
        IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize, int Flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr PowerCreateRequest(ref REASON_CONTEXT Context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerSetRequest(IntPtr PowerRequest, int RequestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerClearRequest(IntPtr PowerRequest, int RequestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uMilliseconds);
}
