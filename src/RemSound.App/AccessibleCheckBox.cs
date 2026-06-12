using System.Runtime.InteropServices;

namespace RemSound.App;

/// <summary>
/// Direct NotifyWinEvent shim. WinForms focus changes nominally fire MSAA EVENT_OBJECT_FOCUS,
/// but in some scenarios (focus moving from a key handler that runs synchronously in
/// ProcessCmdKey, focus into a control inside a wrapper container, etc.) NVDA's screen-reader
/// listener doesn't pick up the announcement. Re-firing the event explicitly forces it.
///
/// Same pattern documented in claude-notes.md for the AccessibleCheckBox state-change fix —
/// "the load-bearing piece is the FOCUS re-fire."
/// </summary>
internal static class WinEventNotifier
{
    private const uint EVENT_OBJECT_FOCUS = 0x8005;
    private const int OBJID_CLIENT = unchecked((int)0xFFFFFFFC);
    private const int CHILDID_SELF = 0;

    [DllImport("user32.dll")]
    private static extern void NotifyWinEvent(uint eventMin, nint hwnd, int idObject, int idChild);

    public static void NotifyFocus(Control control)
    {
        if (control.IsHandleCreated)
        {
            NotifyWinEvent(EVENT_OBJECT_FOCUS, control.Handle, OBJID_CLIENT, CHILDID_SELF);
        }
    }
}

/// <summary>
/// CheckBox variant that fires the right MSAA WinEvents on every state change so NVDA
/// reliably announces "checked" / "not checked" — including for spacebar toggles while the
/// checkbox already has focus, which is the failure mode plain WinForms CheckBox has on
/// .NET 10. The recipe (proven in the loxone desktop app):
///   1. Fire EVENT_OBJECT_STATECHANGE so any listener knows the toggle state changed.
///   2. If the checkbox is currently focused, ALSO re-fire EVENT_OBJECT_FOCUS — this is what
///      forces NVDA to re-announce the focused control, bringing the new state with it.
/// We call <c>user32.NotifyWinEvent</c> directly because the managed
/// <see cref="Control.AccessibilityNotifyClients"/> path only fires the state event without
/// the focus re-fire, which leaves NVDA silent.
/// </summary>
internal sealed class AccessibleCheckBox : CheckBox
{
    private const uint EVENT_OBJECT_FOCUS = 0x8005;
    private const uint EVENT_OBJECT_STATECHANGE = 0x800A;
    private const int OBJID_CLIENT = unchecked((int)0xFFFFFFFC);
    private const int CHILDID_SELF = 0;

    [DllImport("user32.dll")]
    private static extern void NotifyWinEvent(uint eventMin, nint hwnd, int idObject, int idChild);

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        if (!IsHandleCreated) return;

        NotifyWinEvent(EVENT_OBJECT_STATECHANGE, Handle, OBJID_CLIENT, CHILDID_SELF);
        if (Focused)
        {
            NotifyWinEvent(EVENT_OBJECT_FOCUS, Handle, OBJID_CLIENT, CHILDID_SELF);
            // Audible tick/untick feedback. Gated on Focused so it fires for a genuine user toggle
            // (click or spacebar) but stays silent for the bulk programmatic checking on profile load.
            CheckSoundService.Play(Checked);
        }
    }
}
