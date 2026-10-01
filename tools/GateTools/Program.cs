using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// GateTools - the gate's two helpers for keeping its runs silent and invisible (Ed, 2026-09-27: the gate and the
// --headless copies "should be silent and invisible", and NVDA kept speaking them).
//
//   GateTools watch --out <file> --stop-file <file> [--process RemSound] [--path-prefix <folder>] [--seconds N]
//   GateTools hidden <program> [its arguments...]
//
// HIDDEN runs a program on a desktop of its own, made for the purpose and never shown. A screen reader listens to the
// desktop the person is on, and a window on another desktop can never come to the front of theirs, so whatever the
// program opens - a dialog, a focus change, a window in front - nobody sees or hears it. Its output, input and exit code
// are the caller's, as if it had been run directly, and it lives no longer than this launcher: killed with it (a job
// object), so a caller's time limit cannot leave it running out of sight holding the caller's output open.
//
// WATCH listens to every accessibility event (WinEvent) on this desktop - the same stream NVDA listens to - keeps the ones
// that come from a RemSound window, and judges each by NVDA's own rule for events from a window that is not in front
// (eventHandler.shouldAcceptEvent in NVDA's source): it takes them from the foreground application, from a window that
// shares the foreground window's root owner, and from ANY window that is always-on-top, or whose top window is. A focus,
// foreground, alert or menu event NVDA takes is spoken. A top-level window shown where a monitor can see it is seen.
//
// --path-prefix keeps only copies run from under that folder (the gate's own), so a RemSound the person at the computer
// is using at the same time is left out of the judgement.
//
// Runs until the stop file appears (or N seconds pass), then writes a summary line and exits: 0 when nothing was spoken
// or seen, 1 when something was. Every event it kept is in the out file, one line each.
internal static class Program
{
    private static readonly HashSet<string> Watched = new(StringComparer.OrdinalIgnoreCase);
    private static string? pathPrefix;
    private static readonly Dictionary<uint, bool> PidWatched = new();
    private static readonly Dictionary<uint, string> PidName = new();
    private static StreamWriter? output;
    private static int spoken, seen, exposed, quiet;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static WinEventProc? proc;   // held so the GC never collects the delegate Windows calls

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "hidden") return Hidden.Run(args[1..]);
        if (args.Length > 0 && args[0] == "watch") args = args[1..];
        return Watch(args);
    }

    private static int Watch(string[] args)
    {
        string? outPath = null, stopFile = null;
        double seconds = 0;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out": outPath = args[++i]; break;
                case "--stop-file": stopFile = args[++i]; break;
                case "--process": Watched.Add(args[++i]); break;
                case "--path-prefix": pathPrefix = Path.GetFullPath(args[++i]); break;
                case "--seconds": seconds = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
            }
        }
        if (outPath is null || (stopFile is null && seconds <= 0))
        {
            Console.Error.WriteLine("usage: GateTools watch --out <file> --stop-file <file> [--process RemSound] [--path-prefix <folder>] [--seconds N]\n"
                + "       GateTools hidden <program> [arguments...]");
            return 2;
        }
        if (Watched.Count == 0) Watched.Add("RemSound");
        output = new StreamWriter(outPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        output.WriteLine($"GateTools watch started {DateTime.Now:yyyy-MM-dd HH:mm:ss}, watching {string.Join(", ", Watched)}");

        proc = OnEvent;
        var hook = SetWinEventHook(EventMin, EventMax, IntPtr.Zero, proc, 0, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
        if (hook == IntPtr.Zero)
        {
            output.WriteLine("RESULT: could not listen (SetWinEventHook failed)");
            return 2;
        }
        // Out-of-context events arrive through this thread's message queue: pump it, and look for the stop file five
        // times a second.
        var timer = SetTimer(IntPtr.Zero, IntPtr.Zero, 200, IntPtr.Zero);
        var deadline = seconds > 0 ? Clock.Elapsed.TotalSeconds + seconds : double.MaxValue;
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
            if (msg.message != WmTimer) continue;
            if ((stopFile is not null && File.Exists(stopFile)) || Clock.Elapsed.TotalSeconds >= deadline) break;
        }
        KillTimer(IntPtr.Zero, timer);
        UnhookWinEvent(hook);
        output.WriteLine($"RESULT: {spoken} spoken, {seen} seen on screen, {exposed} always-on-top windows a screen reader would listen to, "
            + $"{quiet} other events it would ignore");
        output.Dispose();
        return spoken + seen + exposed > 0 ? 1 : 0;
    }

    private static void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (hwnd == IntPtr.Zero || !Interesting(ev)) return;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!IsWatched(pid)) return;

            var root = GetAncestor(hwnd, GaRoot);
            var fg = GetForegroundWindow();
            var topmost = ((GetWindowLong(hwnd, GwlExStyle) | GetWindowLong(root, GwlExStyle)) & WsExTopmost) != 0;
            var cls = ClassOf(hwnd);
            // NVDA's shouldAcceptEvent, for the cases a desktop app like RemSound can hit.
            var foregroundApp = fg != IntPtr.Zero && (GetAncestor(hwnd, GaRootOwner) == GetAncestor(fg, GaRootOwner)
                                                      || IsDescendant(fg, hwnd) || IsDescendant(fg, GetAncestor(hwnd, GaRootOwner)));
            var accepted = foregroundApp || topmost || (ev == EventObjectShow && cls == "tooltips_class32");

            string? verdict = null;
            switch (ev)
            {
                case EventSystemForeground when hwnd == fg:
                    verdict = "SPOKEN (became the window in front)"; break;
                case EventObjectFocus when accepted:
                    verdict = "SPOKEN (focus " + (topmost ? "in an always-on-top window" : "in the window in front") + ")"; break;
                case EventSystemAlert when accepted:
                case EventSystemMenuStart when accepted:
                case EventSystemMenuPopupStart when accepted:
                    verdict = "SPOKEN (" + Name(ev) + ")"; break;
                case EventObjectShow when cls == "tooltips_class32":
                    verdict = "SPOKEN (a tooltip, if tooltips are reported)"; break;
                case EventObjectShow when idObject == ObjIdWindow && hwnd == root && IsWindowVisible(hwnd):
                    if (OnAMonitor(hwnd)) verdict = "SEEN (a window on screen)";
                    else if (topmost) verdict = "EXPOSED (an always-on-top window: a screen reader takes every event from it)";
                    break;
            }
            if (verdict is null)
            {
                if (accepted && ev is EventObjectNameChange or EventObjectValueChange or EventObjectStateChange or EventObjectLiveRegionChanged)
                    verdict = "HEARD IF FOCUSED (" + Name(ev) + ")";
                else { quiet++; return; }
            }
            if (verdict.StartsWith("SPOKEN", StringComparison.Ordinal)) spoken++;
            else if (verdict.StartsWith("SEEN", StringComparison.Ordinal)) seen++;
            else if (verdict.StartsWith("EXPOSED", StringComparison.Ordinal)) exposed++;
            output?.WriteLine($"{Clock.Elapsed.TotalSeconds,9:0.000}s  {PidName.GetValueOrDefault(pid, "?")}[{pid}]  {verdict}  "
                + $"{Name(ev)} class={cls} window=\"{TextOf(root)}\" control=\"{TextOf(hwnd)}\"{(topmost ? " topmost" : "")}");
        }
        catch (Exception ex)
        {
            output?.WriteLine($"watcher error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool Interesting(uint ev) => ev is EventSystemForeground or EventSystemAlert or EventSystemMenuStart
        or EventSystemMenuPopupStart or EventObjectShow or EventObjectFocus or EventObjectNameChange or EventObjectValueChange
        or EventObjectStateChange or EventObjectLiveRegionChanged;

    private static bool IsWatched(uint pid)
    {
        if (PidWatched.TryGetValue(pid, out var known)) return known;
        var watched = false;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            PidName[pid] = p.ProcessName;
            watched = Watched.Contains(p.ProcessName);
            if (watched && pathPrefix is not null)
            {
                var path = p.MainModule?.FileName ?? "";
                watched = path.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase);
                if (watched) output?.WriteLine($"{Clock.Elapsed.TotalSeconds,9:0.000}s  watching {p.ProcessName}[{pid}] {path}");
            }
            else if (watched) output?.WriteLine($"{Clock.Elapsed.TotalSeconds,9:0.000}s  watching {p.ProcessName}[{pid}]");
        }
        catch { /* gone already */ }
        PidWatched[pid] = watched;
        return watched;
    }

    private static bool IsDescendant(IntPtr parent, IntPtr child)
    {
        if (parent == IntPtr.Zero || child == IntPtr.Zero) return false;
        return parent == child || IsChild(parent, child);
    }

    private static bool OnAMonitor(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var r) || r.Right - r.Left <= 1 && r.Bottom - r.Top <= 1) return false;
        return MonitorFromRect(ref r, MonitorDefaultToNull) != IntPtr.Zero;
    }

    private static string Name(uint ev) => ev switch
    {
        EventSystemForeground => "foreground",
        EventSystemAlert => "alert",
        EventSystemMenuStart => "menu start",
        EventSystemMenuPopupStart => "menu popup",
        EventObjectShow => "show",
        EventObjectFocus => "focus",
        EventObjectNameChange => "name change",
        EventObjectValueChange => "value change",
        EventObjectStateChange => "state change",
        EventObjectLiveRegionChanged => "live region",
        _ => $"0x{ev:X}",
    };

    private static string ClassOf(IntPtr h) { var s = new StringBuilder(128); GetClassName(h, s, s.Capacity); return s.ToString(); }
    private static string TextOf(IntPtr h) { var s = new StringBuilder(256); GetWindowText(h, s, s.Capacity); return s.ToString().Replace('"', '\''); }

    private const uint EventMin = 0x0001, EventMax = 0x7FFFFFFF;
    private const uint EventSystemAlert = 0x0002, EventSystemForeground = 0x0003, EventSystemMenuStart = 0x0004, EventSystemMenuPopupStart = 0x0006;
    private const uint EventObjectShow = 0x8002, EventObjectFocus = 0x8005, EventObjectStateChange = 0x800A, EventObjectNameChange = 0x800C;
    private const uint EventObjectValueChange = 0x800E, EventObjectLiveRegionChanged = 0x8019;
    private const uint WinEventOutOfContext = 0x0000, WinEventSkipOwnProcess = 0x0002;
    private const int ObjIdWindow = 0, GwlExStyle = -20, WsExTopmost = 0x00000008;
    private const uint GaRoot = 2, GaRootOwner = 3, MonitorDefaultToNull = 0, WmTimer = 0x0113;

    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Msg { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Msg msg);
    [DllImport("user32.dll")] private static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hwnd, IntPtr id);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref Rect rect, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
}

/// <summary>Runs a program on a desktop nobody is looking at (see the note at the top).</summary>
internal static class Hidden
{
    private const string DesktopName = "RemSoundGate";

    internal static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: GateTools hidden <program> [arguments...]");
            return 2;
        }
        // The same desktop every time: made if it is not there, and gone again once nothing is using it.
        var desktop = CreateDesktop(DesktopName, IntPtr.Zero, IntPtr.Zero, 0, DesktopAllAccess, IntPtr.Zero);
        if (desktop == IntPtr.Zero)
        {
            Console.Error.WriteLine($"GateTools hidden: could not make the hidden desktop (error {Marshal.GetLastWin32Error()})");
            return 2;
        }
        try
        {
            var si = new StartupInfo
            {
                cb = Marshal.SizeOf<StartupInfo>(),
                lpDesktop = @"WinSta0\" + DesktopName,
                dwFlags = StartfUseStdHandles,
                hStdInput = Inheritable(GetStdHandle(StdInput)),
                hStdOutput = Inheritable(GetStdHandle(StdOutput)),
                hStdError = Inheritable(GetStdHandle(StdError)),
            };
            var commandLine = new StringBuilder(Quote(args[0]));
            foreach (var a in args[1..]) commandLine.Append(' ').Append(Quote(a));
            // Tied to this launcher: everything in the job dies when the launcher's handle to it closes, however it ends.
            var job = CreateJobObject(IntPtr.Zero, null);
            var limits = new JobExtendedLimits { BasicLimitInformation = new JobBasicLimits { LimitFlags = JobKillOnClose } };
            var size = Marshal.SizeOf<JobExtendedLimits>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limits, buffer, false);
                SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size);
            }
            finally { Marshal.FreeHGlobal(buffer); }
            if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, true, CreateSuspended, IntPtr.Zero, Environment.CurrentDirectory, ref si, out var pi))
            {
                Console.Error.WriteLine($"GateTools hidden: could not start {args[0]} (error {Marshal.GetLastWin32Error()})");
                return 2;
            }
            AssignProcessToJobObject(job, pi.hProcess);
            ResumeThread(pi.hThread);
            try
            {
                WaitForSingleObject(pi.hProcess, uint.MaxValue);
                return GetExitCodeProcess(pi.hProcess, out var code) ? (int)code : 2;
            }
            finally
            {
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
            }
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    private static IntPtr Inheritable(IntPtr handle)
    {
        if (handle != IntPtr.Zero && handle != new IntPtr(-1)) SetHandleInformation(handle, HandleFlagInherit, HandleFlagInherit);
        return handle;
    }

    /// <summary>One argument as Windows' own command-line reader splits it back out.</summary>
    private static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
        var sb = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { sb.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            sb.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        return sb.Append('\\', slashes * 2).Append('"').ToString();
    }

    private const uint DesktopAllAccess = 0x01FF;
    private const uint CreateSuspended = 0x00000004;
    private const uint JobKillOnClose = 0x00002000;
    private const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong a, b, c, d, e, f; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimits
    {
        public JobBasicLimits BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern uint ResumeThread(IntPtr thread);
    private const int StartfUseStdHandles = 0x00000100;
    private const int StdInput = -10, StdOutput = -11, StdError = -12;
    private const uint HandleFlagInherit = 0x00000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr devMode, uint flags, uint access, IntPtr security);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string? app, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string currentDirectory, ref StartupInfo startup, out ProcessInformation info);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll")] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}

