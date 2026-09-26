using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace RemSound.App;

/// <summary>
/// RemSound with no window: <c>--headless</c>.
///
/// <para>Ed, 2026-09-24: "a way of driving remsound entirely without a window like I think you can with reaper."
/// A copy started with <c>--headless</c> is the real app — its profile, its audio, its network, everything a normal
/// start does, down the same code — except that no window of it ever appears or takes focus, and it makes no sound
/// and says nothing. It is driven through <see cref="RemoteControlServer"/> instead.</para>
///
/// <para><b>How no window appears.</b> Not by teaching each of the app's forty-odd dialogs and eighty-odd message
/// boxes about it: one hook on the UI thread sees every top-level window the moment it is created and the moment it
/// asks to be activated. While <see cref="Hiding"/>, it puts the window off-screen and off the taskbar and Alt+Tab,
/// and refuses it activation, so focus never leaves whatever the user is doing and a screen reader has nothing to
/// announce. The main window takes exactly the path a <c>--minimized</c> start takes (shown, then parked in the
/// tray), so nothing about start-up differs from a normal run but where the window is. A dialog or question the app
/// raises still happens, still waits for an answer, and is answered through the control channel, which lists it.</para>
///
/// <para><b>Handing over.</b> The tray icon is still there. Choosing Show RemSound from it hands the window to the
/// person at the keyboard: <see cref="HandOver"/> stops the hiding, and from then on every window behaves normally.
/// The control channel keeps working either way.</para>
/// </summary>
internal static class Windowless
{
    /// <summary>This copy was started with <c>--headless</c>.</summary>
    internal static bool Active { get; private set; }

    /// <summary>Somebody opened the window from the tray, so windows behave normally again.</summary>
    internal static bool HandedOver { get; private set; }

    /// <summary>This copy was started with <c>--silent</c>: a throwaway test launch, with nobody at it. Kept apart from the
    /// sound switch, which the control channel's <c>sounds on</c> and the self-test both move for their own reasons.</summary>
    internal static bool SilentLaunch { get; set; }

    /// <summary>Nobody is there to answer a question or read a notice: a --silent launch, a --headless copy not yet handed
    /// over, or a run with its sounds held off. The start-up notices and warnings ask this, not the sound switch alone:
    /// a headless copy told "sounds on" to test a cue went back to popping them - one of them opening the window
    /// (2026-09-25).</summary>
    internal static bool NobodyToAsk => SilentLaunch || Hiding || CuePlayer.GloballyMuted;

    /// <summary>Windows are being kept out of sight and out of focus right now.</summary>
    internal static bool Hiding => Active && !HandedOver;

    /// <summary>Where a hidden window is put. Far enough off every monitor that no layout reaches it.</summary>
    internal static readonly Point OffScreen = new(-32000, -32000);

    /// <summary>The UI thread's id, for finding the app's own native dialogs (a message box is not a Form).</summary>
    internal static uint UiThreadId { get; private set; }

    private static HookProc? hookProc;   // held so the GC never collects the delegate the hook calls
    private static IntPtr hook;

    /// <summary>Turn it on, on the UI thread, before any window exists. Idempotent.</summary>
    internal static void Start()
    {
        Active = true;
        HandedOver = false;
        WinEventNotifier.Quiet = true;
        InstallHook();
    }

    /// <summary>Install the window hook on the calling (UI) thread without switching the mode on. The mode is what
    /// <see cref="Start"/> is for; the self-test uses this with <see cref="SetActiveForTest"/> to prove the hook on a
    /// window of its own.</summary>
    internal static void InstallHook()
    {
        UiThreadId = GetCurrentThreadId();
        if (hook != IntPtr.Zero) return;
        hookProc = HookCallback;
        hook = SetWindowsHookEx(WH_CBT, hookProc, IntPtr.Zero, UiThreadId);
    }

    /// <summary>The person at the keyboard asked for the window (Show RemSound in the tray). From here windows behave
    /// normally. The main window was parked off-screen and off the taskbar, so put it back where it can be seen and
    /// reached with Alt+Tab.</summary>
    internal static void HandOver(Form main)
    {
        if (!Hiding) return;
        HandedOver = true;
        WinEventNotifier.Quiet = false;
        // A person is at it now: they hear it, as they would any RemSound. Speech, the cues (unless it was started
        // --silent) and the key clicks all stayed off after a hand-over, so the speak-status hotkey said nothing and a
        // deletion was never announced (2026-09-25).
        ScreenReader.Suppressed = false;
        if (!SilentLaunch) CuePlayer.GloballyMuted = false;
        try { KeyClickService.Enabled = RemSound.Core.AppConfig.Load().EnableKeyboardClicks; } catch { /* clicks stay as they were */ }
        try
        {
            BringOnScreen(main, appWindow: true);
            // And anything already open in front of it - a question waiting since before the hand-over - or the window they
            // can now see is disabled behind something they can't (2026-09-25).
            foreach (var form in Application.OpenForms.Cast<Form>().Where(f => f != main && f.Visible).ToList())
                BringOnScreen(form, appWindow: false);
        }
        catch { /* best-effort: the window still shows, just possibly without a taskbar button */ }
        RemSoundLog.Current?.Event("headless: the window was opened from the tray - windows, speech and sounds behave normally from now on; the control channel stays open");
    }

    /// <summary>Put a window that was parked off-screen back in the middle of the screen, reachable with Alt+Tab.</summary>
    private static void BringOnScreen(Form form, bool appWindow)
    {
        if (form.IsHandleCreated)
        {
            var ex = GetWindowLong(form.Handle, GWL_EXSTYLE);
            SetWindowLong(form.Handle, GWL_EXSTYLE, appWindow ? (ex & ~WS_EX_TOOLWINDOW) | WS_EX_APPWINDOW : ex & ~WS_EX_TOOLWINDOW);
        }
        if (form.Left <= OffScreen.X / 2 || form.Top <= OffScreen.Y / 2)
        {
            var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768);
            form.Location = new Point(area.X + Math.Max(0, (area.Width - form.Width) / 2), area.Y + Math.Max(0, (area.Height - form.Height) / 2));
        }
    }

    /// <summary>The person at the keyboard has pressed on the tray icon, so its menu is theirs: while it is open, windows
    /// are left alone. Only the hiding is paused - nothing is handed over until they choose Show RemSound.</summary>
    internal static bool TrayMenuOpen { get; private set; }

    internal static void PersonOpeningTrayMenu()
    {
        if (!Hiding) return;
        TrayMenuOpen = true;
    }

    internal static void TrayMenuClosed() => TrayMenuOpen = false;

    /// <summary>Gate seam: switch the hiding on or off without a real <c>--headless</c> start.</summary>
    internal static void SetActiveForTest(bool active)
    {
        Active = active;
        HandedOver = false;
        TrayMenuOpen = false;
        WinEventNotifier.Quiet = active;
    }

    private static IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && Hiding && !TrayMenuOpen)
        {
            try
            {
                if (code == HCBT_CREATEWND)
                {
                    var cbt = Marshal.PtrToStructure<CBT_CREATEWND>(lParam);
                    var cs = Marshal.PtrToStructure<CREATESTRUCT>(cbt.lpcs);
                    if ((cs.style & WS_CHILD) == 0)
                    {
                        cs.x = OffScreen.X;
                        cs.y = OffScreen.Y;
                        cs.dwExStyle = (cs.dwExStyle & ~WS_EX_APPWINDOW) | WS_EX_TOOLWINDOW;
                        Marshal.StructureToPtr(cs, cbt.lpcs, false);
                    }
                }
                else if (code == HCBT_ACTIVATE)
                {
                    // wParam is the window about to be activated. WinForms positions a form (centre on its owner, say)
                    // after it is created, so put it back off-screen here, then refuse the activation outright: focus
                    // stays wherever the user has it, and a screen reader hears nothing.
                    SetWindowPos(wParam, IntPtr.Zero, OffScreen.X, OffScreen.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                    var ex = GetWindowLong(wParam, GWL_EXSTYLE);
                    if ((ex & WS_EX_TOOLWINDOW) == 0) SetWindowLong(wParam, GWL_EXSTYLE, (ex & ~WS_EX_APPWINDOW) | WS_EX_TOOLWINDOW);
                    return (IntPtr)1;
                }
            }
            catch { /* never let a hook failure take the UI thread down */ }
        }
        return CallNextHookEx(hook, code, wParam, lParam);
    }

    // ---------------- native dialogs (a MessageBox is not a Form) ----------------

    /// <summary>One of the app's own native dialogs — a message box — as the control channel shows it.</summary>
    internal sealed record NativeDialog(IntPtr Handle, string Title, string Text, IReadOnlyList<(int Id, string Text)> Buttons);

    /// <summary>The app's open native dialogs (window class #32770) on the UI thread. Safe from any thread.</summary>
    internal static List<NativeDialog> NativeDialogs()
    {
        var found = new List<NativeDialog>();
        if (UiThreadId == 0) return found;
        EnumThreadWindows(UiThreadId, (h, _) =>
        {
            if (ClassOf(h) != "#32770") return true;
            var text = new StringBuilder();
            var buttons = new List<(int, string)>();
            EnumChildWindows(h, (c, _) =>
            {
                var cls = ClassOf(c);
                if (cls == "Button") buttons.Add((GetDlgCtrlID(c), Clean(TextOf(c))));
                else if (cls == "Static" && TextOf(c) is { Length: > 0 } t) text.Append(text.Length > 0 ? " " : "").Append(t.Trim());
                return true;
            }, IntPtr.Zero);
            found.Add(new NativeDialog(h, TextOf(h), text.ToString(), buttons));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Press a native dialog's button the way the dialog itself would see it pressed: WM_COMMAND with the
    /// button's id. BM_CLICK is documented to fail on a dialog that is not active, and these never are.</summary>
    internal static void PressNativeButton(NativeDialog dialog, int buttonId) =>
        PostMessage(dialog.Handle, WM_COMMAND, (IntPtr)(buttonId & 0xFFFF), IntPtr.Zero);

    /// <summary>Fill in a native dialog's name box — the file-name box of a file picker, the folder box of a folder
    /// picker: the first edit box in it. WM_SETTEXT, so it works on a dialog that is never active.</summary>
    internal static bool TypeIntoNameBox(NativeDialog dialog, string text)
    {
        // The file-name field is the combo box with id 1148 (cmb13) in every Windows file dialog, old and new. Once the
        // new ones have loaded, the FIRST edit box is the search box, and a name typed there opens nothing (2026-09-24,
        // the full gate: the picker stayed open while the same check passed alone).
        const int FileNameCombo = 1148;
        var edit = IntPtr.Zero;
        var firstEdit = IntPtr.Zero;
        EnumChildWindows(dialog.Handle, (c, _) =>
        {
            var cls = ClassOf(c);
            if (cls == "Edit" && firstEdit == IntPtr.Zero) firstEdit = c;
            if (cls is "ComboBoxEx32" or "ComboBox" && GetDlgCtrlID(c) == FileNameCombo)
            {
                EnumChildWindows(c, (inner, _) =>
                {
                    if (ClassOf(inner) != "Edit") return true;
                    edit = inner;
                    return false;
                }, IntPtr.Zero);
                if (edit != IntPtr.Zero) return false;
            }
            return true;
        }, IntPtr.Zero);
        if (edit == IntPtr.Zero) edit = firstEdit;
        if (edit == IntPtr.Zero) return false;
        SendMessage(edit, WM_SETTEXT, IntPtr.Zero, text);
        return true;
    }

    /// <summary>"&amp;Yes" → "Yes".</summary>
    internal static string Clean(string text) => text.Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&").Trim();

    private static string ClassOf(IntPtr h) { var s = new StringBuilder(64); GetClassName(h, s, s.Capacity); return s.ToString(); }
    private static string TextOf(IntPtr h) { var s = new StringBuilder(1024); GetWindowText(h, s, s.Capacity); return s.ToString(); }

    // ---------------- interop ----------------

    private const int WH_CBT = 5;
    private const int HCBT_CREATEWND = 3;
    private const int HCBT_ACTIVATE = 5;
    private const int WS_CHILD = 0x40000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;
    private const int GWL_EXSTYLE = -20;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_SETTEXT = 0x000C;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    private delegate bool EnumProc(IntPtr h, IntPtr l);

    [StructLayout(LayoutKind.Sequential)]
    private struct CBT_CREATEWND { public IntPtr lpcs; public IntPtr hwndInsertAfter; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CREATESTRUCT
    {
        public IntPtr lpCreateParams;
        public IntPtr hInstance;
        public IntPtr hMenu;
        public IntPtr hwndParent;
        public int cy;
        public int cx;
        public int y;
        public int x;
        public int style;
        public IntPtr lpszName;
        public IntPtr lpszClass;
        public int dwExStyle;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, EnumProc proc, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr h);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, string l);
}
