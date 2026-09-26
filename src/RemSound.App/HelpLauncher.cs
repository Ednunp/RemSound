using System.Diagnostics;

namespace RemSound.App;

/// <summary>
/// The manual and F1 help. F1 shows help for the control you are on, in the manual's own words (<see cref="ContextHelp"/>);
/// Shift+F1 opens the whole manual in the browser (Ed, 2026-09-25). Until then F1 opened the whole manual from anywhere.
/// Both keys are caught app-wide by <see cref="HelpKeyMessageFilter"/>, installed once at startup before the first
/// window, so they work in every window and dialog, the profile picker included.
///
/// File location: <c>&lt;exe&gt;\readme.html</c> (resolved via <see cref="AppContext.BaseDirectory"/>).
/// The .csproj copies it from the project root via a Content/Link rule so a fresh
/// <c>dotnet publish</c> always lands a current copy next to the executable.
/// </summary>
internal static class HelpLauncher
{
    private static string ManualPath => Path.Combine(AppContext.BaseDirectory, "readme.html");

    /// <summary>Gate seam: where the browser would have gone, instead of opening one.</summary>
    internal static Action<string>? BrowserForTest;

    /// <summary>The whole manual, from the top, in the default browser (Shift+F1, and Help, Open user manual).</summary>
    public static void OpenManual() => OpenManualAt(null);

    /// <summary>The manual in the default browser, at a help key's entry, or at the top for null. Shows a message if the
    /// file is missing or the browser can't be started - better to say why than silently swallow the key.</summary>
    public static void OpenManualAt(string? key)
    {
        var path = ManualPath;
        if (!File.Exists(path))
        {
            AppMessageBox.Show(
                $"Manual not found at:\n\n{path}\n\nThe readme.html file should sit next to RemSound.exe. Unzipping RemSound again, or reinstalling it, will put it back.",
                "Manual not found",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }
        var target = new Uri(path).AbsoluteUri + (key is null ? "" : "#" + HelpManual.AnchorFor(key));
        RemSoundLog.Current?.Event(key is null ? "manual: opening the whole manual in the browser" : $"context help: opening the manual in the browser at {key}");
        if (BrowserForTest is { } seam) { seam(target); return; }
        if (Windowless.Active)
        {
            RemSoundLog.Current?.Event($"manual: a headless copy opens no browser; it would have opened {target}");
            return;
        }
        try
        {
            // Shell-executed, so Windows picks the .html handler (Edge / Chrome / Firefox / whatever the user defaulted);
            // a place in the manual goes through a one-line page (shared with the plugin, in HelpManual).
            HelpManual.OpenInBrowser(path, key);
        }
        catch (Exception ex)
        {
            AppMessageBox.Show(
                $"Could not open the manual:\n\n{ex.Message}",
                "Manual open failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    /// <summary>F1 with the keyboard on <paramref name="hwnd"/>: that control's help, or the whole manual where there is
    /// none. Shown after the key has been dealt with rather than inside the message filter, so its window runs its own
    /// message loop at the top level.</summary>
    internal static void F1(IntPtr hwnd)
    {
        // A menu open - the main menus or the tray menu - means the menu item the keyboard is on.
        if (ContextHelp.OpenMenuItem() is { Owner: { } menu } item)
        {
            menu.BeginInvoke(() =>
            {
                if (!ContextHelp.ShowForItem(item))
                {
                    RemSoundLog.Current?.Event($"context help: F1 on the menu item \"{ContextHelp.NameOfItem(item)}\", which has no context help - opened the whole manual");
                    OpenManual();
                }
            });
            return;
        }
        var control = Control.FromChildHandle(hwnd);
        if (control?.FindForm() is ContextHelpWindow) return;   // F1 inside the help window itself
        if (control is not null && !control.IsDisposed && ContextHelp.Find(control) is not null)
        {
            control.BeginInvoke(() => { if (!control.IsDisposed) ContextHelp.Show(control); });
            return;
        }
        RemSoundLog.Current?.Event($"context help: F1 on {(control is null ? "a window that is not RemSound's own" : $"\"{ContextHelp.NameOf(control)}\"")}, which has no context help - opened the whole manual");
        OpenManual();
    }

    /// <summary>Give the help window, which lives in the shared library, the app's sounds, log and browser. Called at
    /// startup, and by the checks, so they drive exactly what a person gets.</summary>
    internal static void WireContextHelp()
    {
        ContextHelp.PlayOpenSound = HelpSoundService.PlayOpen;
        ContextHelp.PlayCloseSound = HelpSoundService.PlayClose;
        ContextHelp.Log = line => RemSoundLog.Current?.Event(line);
        ContextHelp.OpenManualInBrowser = OpenManualAt;
        // From the tray menu the main window may be hidden: help goes in front of everything, as every dialog that can
        // appear from the tray does.
        ContextHelp.ShowInFront = window => ForegroundDialog.Show(owner => window.ShowDialog(owner));
    }

    /// <summary>Install the F1 message filter on the current thread's message loop. Call once from <c>Program.Main</c>
    /// before any form is shown. Idempotent — calling it twice would register two filters which is wasteful but not
    /// harmful.</summary>
    public static void Install()
    {
        Application.AddMessageFilter(new HelpKeyMessageFilter());
    }
}

/// <summary>
/// Catches F1 and Shift+F1 anywhere in the application before they reach the focused control. Ctrl+F1 and Alt+F1 fall
/// through unchanged. Single-instance state is fine because the filter chain is per-thread and RemSound is a
/// single-threaded WinForms app.
/// </summary>
internal sealed class HelpKeyMessageFilter : IMessageFilter
{
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int VK_F1 = 0x70;

    /// <summary>Gate seam: the modifier keys held, instead of asking the keyboard.</summary>
    internal static Func<Keys>? ModifiersForTest;

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WM_KEYDOWN && m.Msg != WM_SYSKEYDOWN) return false;
        if (m.WParam.ToInt32() != VK_F1) return false;
        var held = (ModifiersForTest?.Invoke() ?? Control.ModifierKeys) & (Keys.Control | Keys.Shift | Keys.Alt);
        if (held == Keys.Shift)
        {
            RemSoundLog.Current?.Event("manual: Shift+F1 - the whole manual");
            HelpLauncher.OpenManual();
            return true;
        }
        if (held != Keys.None) return false;
        HelpLauncher.F1(m.HWnd);
        return true; // consumed — no further dispatch
    }
}
