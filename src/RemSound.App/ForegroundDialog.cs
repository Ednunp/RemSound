using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RemSound.App;

/// <summary>
/// Shows a dialog FRONT-AND-CENTRE with keyboard focus, WITHOUT disturbing the main window — it can
/// stay minimised in the tray the whole time. We give the dialog a momentary, top-most, off-taskbar
/// 1×1 owner window at screen centre and force THAT owner to the foreground (the AttachThreadInput
/// dance bypasses Windows' focus-stealing lock), so the modal dialog opens on top with focus and a
/// screen reader lands on it wherever RemSound happens to be sitting. Used for every warning/notice
/// the app raises (mic-privacy, Realtek, About-after-update, config-moved), so a minimised RemSound
/// never leaves a blind user with a dialog dinging away behind everything.
/// </summary>
internal static class ForegroundDialog
{
    /// <summary>Run <paramref name="show"/> with a foreground 1×1 owner; returns its result.</summary>
    public static T Show<T>(Func<IWin32Window, T> show)
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 800, 600);
        using var owner = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Size = new Size(1, 1),
            Location = new Point(area.X + area.Width / 2, area.Y + area.Height / 2),
            TopMost = true,
        };
        owner.Show();
        ForceForeground(owner.Handle);
        try { return show(owner); }
        finally { try { owner.Close(); } catch { /* ignore */ } }
    }

    /// <summary>Void convenience overload.</summary>
    public static void Show(Action<IWin32Window> show) =>
        Show<object?>(owner => { show(owner); return null; });

    /// <summary>Force <paramref name="hWnd"/> to the foreground even when RemSound isn't the active
    /// app. A plain SetForegroundWindow from a background process is refused by Windows; attaching
    /// our input queue to the current foreground thread for the call lifts that restriction. The
    /// owner is top-most regardless, so this is belt-and-braces for focus.</summary>
    private static void ForceForeground(IntPtr hWnd)
    {
        try
        {
            var foreThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            var thisThread = GetCurrentThreadId();
            if (foreThread != 0 && foreThread != thisThread)
            {
                AttachThreadInput(foreThread, thisThread, true);
                try
                {
                    BringWindowToTop(hWnd);
                    SetForegroundWindow(hWnd);
                }
                finally { AttachThreadInput(foreThread, thisThread, false); }
            }
            else
            {
                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
            }
        }
        catch { /* best-effort; the owner is top-most anyway */ }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
}
