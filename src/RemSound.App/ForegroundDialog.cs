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
    /// app — including the hardest case: a process the auto-updater RELAUNCHED in the background while
    /// RemSound was minimised. Windows refuses SetForegroundWindow to a background-spawned process (it
    /// only flashes the taskbar button — invisible to a screen-reader user), so we (1) drop the system
    /// foreground-lock timeout to 0 for the duration of the call, and (2) attach our input queue to the
    /// current foreground thread. Both the timeout and the attachment are restored immediately after.</summary>
    private static void ForceForeground(IntPtr hWnd)
    {
        uint savedTimeout = 0;
        var loweredTimeout = false;
        try
        {
            // Lift Windows' foreground-stealing lock for this one call — the key fix for a dialog that
            // must surface from a background-relaunched (post-update) process, where AttachThreadInput
            // alone isn't enough and the dialog would otherwise open behind everything.
            if (SystemParametersInfoGet(SPI_GETFOREGROUNDLOCKTIMEOUT, 0, out savedTimeout, 0))
                loweredTimeout = SystemParametersInfoSet(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, IntPtr.Zero, SPIF_SENDCHANGE);

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
        finally
        {
            if (loweredTimeout)
            {
                try { SystemParametersInfoSet(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, new IntPtr((long)savedTimeout), SPIF_SENDCHANGE); }
                catch { /* restoring the user's setting is best-effort */ }
            }
        }
    }

    private const uint SPI_GETFOREGROUNDLOCKTIMEOUT = 0x2000;
    private const uint SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001;
    private const uint SPIF_SENDCHANGE = 0x0002;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")] private static extern bool SystemParametersInfoGet(uint uiAction, uint uiParam, out uint pvParam, uint fWinIni);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")] private static extern bool SystemParametersInfoSet(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);
}
