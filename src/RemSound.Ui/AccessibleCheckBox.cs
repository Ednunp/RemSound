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

    /// <summary>Land NVDA on a named control so a freshly-shown window or dialog announces itself
    /// instead of surfacing silently. Focuses the first focusable leaf inside <paramref name="page"/>
    /// (falling back to <paramref name="fallback"/>), forcing a genuine focus CHANGE (ActiveControl=null
    /// first) and re-firing the MSAA focus event. Used by the Preferences dialog on open and by the
    /// main window when it's restored from the tray. The page sits inside a <see cref="QuietTabControl"/>
    /// whose own accessible object is deliberately role-less and nameless, so focusing the tab control
    /// itself would be silent — this finds a real leaf (the first interactive control on whatever tab
    /// is showing) instead. Best run deferred (BeginInvoke), after the show/foreground settles.</summary>
    public static void AnnounceByFocusingLeaf(ContainerControl form, Control? page, Control fallback)
    {
        var leaf = FirstFocusableLeaf(page) ?? fallback;
        form.ActiveControl = null;
        leaf.Focus();
        if (leaf.IsHandleCreated) NotifyFocus(leaf);
    }

    /// <summary>The first visible, enabled, tab-stop control inside <paramref name="container"/>,
    /// searched depth-first in child order (which matches the order controls were added). Returns a
    /// real leaf (button / list / combo / checkbox) — never a layout panel or the role-less tab strip.</summary>
    private static Control? FirstFocusableLeaf(Control? container)
    {
        if (container is null) return null;
        foreach (Control c in container.Controls)
        {
            if (c is { CanSelect: true, TabStop: true, Visible: true, Enabled: true }) return c;
            if (FirstFocusableLeaf(c) is { } nested) return nested;
        }
        return null;
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
internal class AccessibleCheckBox : CheckBox
{
    private const uint EVENT_OBJECT_FOCUS = 0x8005;
    private const uint EVENT_OBJECT_STATECHANGE = 0x800A;
    private const int OBJID_CLIENT = unchecked((int)0xFFFFFFFC);
    private const int CHILDID_SELF = 0;

    [DllImport("user32.dll", EntryPoint = "NotifyWinEvent")]
    private static extern void NotifyWinEventNative(uint eventMin, nint hwnd, int idObject, int idChild);

    /// <summary>Every WinEvent this control raises, in order, while recording is switched on.
    ///
    /// <para>Here because the FOCUS re-fire below is the single load-bearing accessibility line in
    /// this workspace, and nothing watched it. Delete it and the app looks and behaves identically
    /// to anyone with sight — while NVDA goes completely silent every time Ed toggles any checkbox
    /// in the application with the spacebar. The whole gate stayed green with it removed
    /// (2026-08-24). Observing the real WinEvent from inside a headless gate needs a message pump
    /// that is not there, so the calls are routed through this shim instead: production still makes
    /// exactly the same P/Invoke, and the gate can see which events were raised and in what
    /// order.</para></summary>
    internal static List<uint>? RaisedWinEventsForTest;

    private static void NotifyWinEvent(uint eventMin, nint hwnd, int idObject, int idChild)
    {
        RaisedWinEventsForTest?.Add(eventMin);
        NotifyWinEventNative(eventMin, hwnd, idObject, idChild);
    }

    /// <summary>The two event ids the gate asserts on, named so the test reads as English.</summary>
    internal const uint StateChangeEventForTest = EVENT_OBJECT_STATECHANGE;
    internal const uint FocusEventForTest = EVENT_OBJECT_FOCUS;

    /// <summary>Optional gate, called with the new Checked state just before the generic checkbox
    /// tick/untick sound plays; return true to suppress it. The send/receive checkboxes set this so
    /// that when their OWN dedicated cue (send/receive turned on/off) is enabled, only that cue
    /// plays - the generic checkbox sound doesn't double up on top of it. When their dedicated cue
    /// is set to "(none)", this returns false and the generic checkbox sound plays as normal.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<bool, bool>? SuppressCheckSound { get; set; }

    /// <summary>How a toggle makes its sound. INJECTED rather than called directly so this control
    /// carries no dependency on the app's cue machinery — the ACCESSIBILITY work here (the WinEvent
    /// re-fire that makes NVDA announce a spacebar toggle at all) is what the VST plugin needs to
    /// reuse, and it must not drag the whole application in behind it. The app wires this to
    /// CheckSoundService.Play at startup; the plugin leaves it null and is simply silent.</summary>
    public static Action<bool>? PlayToggleSound { get; set; }

    /// <summary>Does this checkbox currently hold keyboard focus? Just <see cref="Control.Focused"/>,
    /// but virtual so the gate can stand in for it.
    ///
    /// <para>A headless form has never been shown, so nothing on it can ever report Focused — which
    /// means the focus half of the recipe below, the load-bearing half, could not be driven at all
    /// without either showing a window (stealing focus from whoever is at the machine) or leaving it
    /// untested. It was untested. 2026-08-24.</para></summary>
    internal virtual bool HasKeyboardFocus => Focused;

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        if (!IsHandleCreated) return;

        NotifyWinEvent(EVENT_OBJECT_STATECHANGE, Handle, OBJID_CLIENT, CHILDID_SELF);
        if (HasKeyboardFocus)
        {
            NotifyWinEvent(EVENT_OBJECT_FOCUS, Handle, OBJID_CLIENT, CHILDID_SELF);
            // Audible tick/untick feedback. Gated on Focused so it fires for a genuine user toggle
            // (click or spacebar) but stays silent for the bulk programmatic checking on profile
            // load - and skipped when a control has its own dedicated cue (send/receive).
            if (SuppressCheckSound?.Invoke(Checked) != true) PlayToggleSound?.Invoke(Checked);
        }
    }
}
