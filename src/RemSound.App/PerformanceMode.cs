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
///   <item>Laptop on battery: faster drain while this is engaged. Only the service engages it now,
///         whenever it is sending - Windows throttles a background service hardest. The app's
///         per-profile switch was removed 2026-09-29 (Ed): its audible part was the fast timer while
///         hidden, which the app keeps while streaming (<see cref="KeepFastTimerWhileStreaming"/>,
///         <see cref="MainForm.StreamingHoldDown"/>).</item>
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

    /// <summary>Gate seam: set for a whole self-test run. Priority mode is recorded and logged but not applied - a step that
    /// started the service host raised the gate's own priority, kept the machine awake and asked for a 1 ms timer (2026-09-24).</summary>
    internal static bool NoEffectForTest;

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

            // One mask for both ways: switching Priority mode off while streaming keeps the fast timer's flag.
            var mask = ThrottlingMaskFor(enable, streamingFastTimer);
            LastThrottlingMaskForTest = mask;

            // A gate run keeps the bookkeeping and the log line, and changes nothing about the process or the machine.
            if (NoEffectForTest)
            {
                currentlyEnabled = enable;
                log?.Invoke($"priority mode: {(enable ? "ON" : "OFF")} (a test run: nothing about the process or the machine was changed)");
                return;
            }

            if (enable)
            {
                TrySetThrottlingMask(mask, "priority mode", log);
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
                TrySetThrottlingMask(mask, "priority mode", log);
                TryStopPowerRequest(log);
                TryRestorePriority(log);
                TryEndTimePeriod(log);
                TryRelaxWorkingSet(log);
                currentlyEnabled = false;
                log?.Invoke(streamingFastTimer
                    ? "priority mode: OFF (all overrides cleared, defaults restored; the fast timer stays while streaming)"
                    : "priority mode: OFF (all overrides cleared, defaults restored)");
            }
        }
    }

    private static bool streamingFastTimer;
    private static bool realTimerFlagForTest;

    /// <summary>
    /// THE FAST TIMER WHILE STREAMING (Ed, 2026-09-28: the latency lab's "fast timer while hidden", made normal behaviour).
    /// Windows 11 ignores a process's 1 ms timer request (timeBeginPeriod, which every audio loop holds - see
    /// SystemTimerResolution) while none of its windows can be seen, unless the process says otherwise - which RemSound
    /// only ever did in Priority mode. So a tray-minimised RemSound's WASAPI mix tick of 10 ms rounded up to the 15.6 ms
    /// system tick and came out in 16-31 ms lumps (the burst pattern in Ed's logs). This sets ONLY that flag, none of
    /// Priority mode's other changes, from when audio starts moving until it stops (<see cref="MainForm.FastTimerShouldHold"/>).
    ///
    /// <para>Measured 2026-09-28 on Ed's 13th-gen Intel desktop (Windows 11), in a process with no visible window running
    /// the mix tick's own wait (MMCSS Pro Audio, the 1 ms timer, WaitOne of up to 10 ms): with nothing set the tick woke
    /// p50 6.3 ms late, p99 15.3, max 16.2; with this flag p50 0.4, p99 1.9, max 2.5, idle and with 48 busy threads in
    /// another process. The gate measures the same (the FAST TIMER step).</para>
    ///
    /// <para>Process-scoped and cheap. Priority mode, when on, already holds it, and keeps holding it when switched off
    /// while streaming (<see cref="ThrottlingMaskFor"/>). Windows 7 has no SetProcessInformation: the call throws, is
    /// caught and logged, and nothing changes. Idempotent: the once-a-second tick calls it every second and only a change
    /// does anything. Mostly for WASAPI: nothing on the ASIO path waits on a timer, and nothing there loses by it.</para>
    /// </summary>
    public static void KeepFastTimerWhileStreaming(bool streaming, Action<string>? log = null)
    {
        lock (gate)
        {
            if (streaming == streamingFastTimer) return;
            streamingFastTimer = streaming;
            var mask = ThrottlingMaskFor(currentlyEnabled, streaming);
            LastThrottlingMaskForTest = mask;
            var what = streaming ? "on while streaming" : "off, not streaming";
            if (NoEffectForTest && !realTimerFlagForTest)
            {
                log?.Invoke($"fast timer: {what} (a test run: nothing about the process was changed)");
                return;
            }
            if (currentlyEnabled)
            {
                log?.Invoke($"fast timer: {what} - priority mode is on and already keeps the fast timer");
                return;
            }
            if (TrySetThrottlingMask(mask, "fast timer", log))
                log?.Invoke(streaming
                    ? "fast timer: on while streaming - Windows told to honour the 1 ms timer even while RemSound is minimised or hidden"
                    : "fast timer: off, not streaming - the timer flag handed back to Windows");
        }
    }

    /// <summary>Whether the fast timer is held for streaming (for the log and the gate).</summary>
    internal static bool StreamingFastTimer { get { lock (gate) return streamingFastTimer; } }

    /// <summary>
    /// The power-throttling flags this process holds, in one place. Two flags, each "don't throttle me" when in the
    /// control mask with its state bit clear:
    /// <list type="bullet">
    /// <item>EXECUTION_SPEED - no downclock, no migration onto efficiency cores. Priority mode's main lever.</item>
    /// <item>IGNORE_TIMER_RESOLUTION - honour my timeBeginPeriod even while no window of mine can be seen. Priority
    /// mode's, and streaming's on its own.</item>
    /// </list>
    /// Neither: a control mask of 0, which hands both back to Windows' own decisions rather than forcing them on.
    /// </summary>
    internal static uint ThrottlingMaskFor(bool priorityMode, bool streaming) =>
        priorityMode ? ProcessPowerThrottlingExecutionSpeed | ProcessPowerThrottlingIgnoreTimerResolution
        : streaming ? ProcessPowerThrottlingIgnoreTimerResolution
        : 0u;

    /// <summary>The fast timer's flag alone, for the gate to recognise.</summary>
    internal const uint TimerFlagForTest = ProcessPowerThrottlingIgnoreTimerResolution;

    /// <summary>Gate seam: the flags the last change asked for, recorded whether or not a test run let it reach Windows.</summary>
    internal static uint LastThrottlingMaskForTest { get; private set; }

    /// <summary>Gate seam: for one step, the fast timer's flag really reaches Windows for this hidden test process, while
    /// every other Priority mode lever stays bookkeeping (<see cref="NoEffectForTest"/>). The flag is process-scoped and
    /// harmless. Disposing the scope hands the flag back to Windows and forgets the streaming state.</summary>
    internal static IDisposable RealTimerFlagForTest()
    {
        lock (gate) realTimerFlagForTest = true;
        return new RealTimerFlagScope();
    }

    private sealed class RealTimerFlagScope : IDisposable
    {
        public void Dispose()
        {
            lock (gate)
            {
                if (!realTimerFlagForTest) return;
                TrySetThrottlingMask(0u, "fast timer", null);
                streamingFastTimer = false;
                realTimerFlagForTest = false;
                LastThrottlingMaskForTest = 0u;
            }
        }
    }

    /// <summary>Gate seam: behave as on Windows 7, whose kernel32 has no SetProcessInformation.</summary>
    internal static bool ApiMissingForTest;

    /// <summary>Gate seam: the control mask Windows holds for this process now (GetProcessInformation), or null where it
    /// cannot say (Windows 7, or a failed call).</summary>
    internal static uint? ReadThrottlingControlMaskForTest()
    {
        try
        {
            var state = new PROCESS_POWER_THROTTLING_STATE { Version = ProcessPowerThrottlingCurrentVersion };
            return GetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>())
                ? state.ControlMask : null;
        }
        catch { return null; }
    }

    // === 1. Process power throttling (EcoQoS opt-out + timer-resolution honoring) ===

    /// <summary>Gate seam: stands in for SetProcessInformation(ProcessPowerThrottling) - given the ControlMask, says whether
    /// Windows took it and, if not, its error - so a test can be the Windows 10 that refuses the timer flag.</summary>
    internal static Func<uint, (bool Ok, int Error)>? SetThrottlingForTest;

    private const int ErrorInvalidParameter = 87;

    /// <summary>Set the process's power-throttling control mask (see <see cref="ThrottlingMaskFor"/>); the state mask is
    /// always 0, "not throttled", for every flag in control. Returns whether Windows took it. Never throws: Windows 7 has
    /// no such call.</summary>
    internal static bool TrySetThrottlingMask(uint controlMask, string who, Action<string>? log)
    {
        try
        {
            if (ApiMissingForTest) throw new EntryPointNotFoundException("Unable to find an entry point named 'SetProcessInformation' in DLL 'kernel32.dll'.");
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = ProcessPowerThrottlingCurrentVersion,
                ControlMask = controlMask,
                StateMask = 0u,
            };
            var (ok, error) = SetThrottling(state);
            // Windows 10 (22H2 reported) does not know the timer flag - it came with Windows 11 - and refuses the whole call as
            // an invalid parameter, which left the process without the full-speed flag too (and so the service, always in
            // priority mode while it sends). Each call REPLACES the process's throttling state, so the fallback is the same
            // mask without the timer flag, not a second call beside the first. Windows 10 needs no timer flag: it honours a
            // process's timer request whether or not its window shows. (Research 2026-09-28, scratchpad research-service.md.)
            if (!ok && error == ErrorInvalidParameter && (controlMask & ProcessPowerThrottlingIgnoreTimerResolution) != 0)
            {
                state.ControlMask = controlMask & ~ProcessPowerThrottlingIgnoreTimerResolution;
                (ok, error) = SetThrottling(state);
                log?.Invoke(ok
                    ? $"{who}: this Windows does not know the timer flag (it needs none) - "
                      + (state.ControlMask == 0 ? "nothing else to set" : "full speed set on its own")
                    : $"{who}: SetProcessInformation(power throttling, without the timer flag) failed (win32={error})");
                return ok;
            }
            if (!ok) log?.Invoke($"{who}: SetProcessInformation(power throttling) failed (win32={error})");
            return ok;
        }
        catch (Exception ex)
        {
            log?.Invoke($"{who}: power throttling threw: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static (bool Ok, int Error) SetThrottling(PROCESS_POWER_THROTTLING_STATE state)
    {
        if (SetThrottlingForTest is { } stand) return stand(state.ControlMask);
        var ok = SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
        return (ok, ok ? 0 : Marshal.GetLastWin32Error());
    }

    /// <summary>The full-speed flag, for the test that stands in for Windows (the timer flag is TimerFlagForTest above).</summary>
    internal const uint FullSpeedFlagForTest = ProcessPowerThrottlingExecutionSpeed;

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessInformation(IntPtr hProcess, int ProcessInformationClass,
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
