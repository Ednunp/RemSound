using System.Windows.Forms;

namespace RemSound.App;

internal enum SingleInstanceDecision
{
    /// <summary>Bring the already-running copy to the front; this copy exits.</summary>
    SwitchToRunning,

    /// <summary>Force the running copy to close, then start fresh.</summary>
    ForceClose,

    /// <summary>Do nothing; this copy exits.</summary>
    Cancel,
}

/// <summary>
/// Shown when a SECOND copy of RemSound is launched while one is already running. Offers three
/// choices, with the safe one (switch to the running copy) as the default so a habitual Enter
/// can't accidentally kill a healthy session that might be mid-recording. The force-close
/// option is the deliberate recovery path for a stuck copy (Andre's "make it go away"). Built
/// as a native TaskDialog to match the rest of RemSound's dialogs and because NVDA reads its
/// heading, body and buttons cleanly. Shown through <see cref="ForegroundDialog"/> so it surfaces
/// front-and-centre with focus even when this copy was relaunched in the background by the updater;
/// ForegroundDialog supplies its own momentary owner, since the main window doesn't exist yet.
/// </summary>
internal static class SingleInstanceDialog
{
    public static SingleInstanceDecision Ask()
    {
        var switchButton = new TaskDialogButton("&Switch to the running copy");
        var forceButton = new TaskDialogButton("&Force the running copy to close and start fresh");
        var cancelButton = new TaskDialogButton("&Cancel") { AllowCloseDialog = true };

        var page = new TaskDialogPage
        {
            Caption = "RemSound is already running",
            Heading = "RemSound is already running",
            Text =
                "Only one copy of RemSound can run at a time. What would you like to do?\n\n"
                + "• Switch to the running copy — bring the copy that's already running back to the "
                + "front. It may be tucked away in the system tray, down by the clock.\n\n"
                + "• Force the running copy to close and start fresh — use this only if the running "
                + "copy is stuck or not responding. If it's in the middle of a recording, that "
                + "recording will be lost.\n\n"
                + "• Cancel — do nothing.",
            Icon = TaskDialogIcon.Warning,
            Buttons = { switchButton, forceButton, cancelButton },
            DefaultButton = switchButton,
            AllowCancel = true,
        };

        // Front-and-centre with focus, even when this copy was relaunched in the background by the
        // auto-updater — a foreground-refused process would otherwise open this behind everything,
        // dinging away where a screen-reader user can't find it. ForegroundDialog supplies its own
        // momentary owner, so this works before any main window exists.
        var clicked = ForegroundDialog.Show(owner => TaskDialog.ShowDialog(owner, page));
        if (clicked == forceButton) return SingleInstanceDecision.ForceClose;
        if (clicked == switchButton) return SingleInstanceDecision.SwitchToRunning;
        return SingleInstanceDecision.Cancel;
    }
}
