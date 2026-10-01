using System.Windows.Forms;

namespace RemSound.Core;

public sealed class HotkeyCaptureForm : Form
{
    private readonly Label instructionLabel = new() { AutoSize = true };
    private readonly TextBox hotkeyTextBox = new() { ReadOnly = true };
    // No Alt key, on purpose: this window exists to capture key combinations, and Alt+C here must be a shortcut being
    // chosen, not the Cancel button. Tab reaches it.
    private readonly Button cancelButton = new() { Text = "Cancel", AutoSize = true, UseMnemonic = false };
    private HotkeyInfo? pendingHotkey;
    private bool capturingCombination;
    private readonly bool allowSingleKey;

    public HotkeyCaptureForm() : this(allowSingleKey: false) { }

    /// <summary>With <paramref name="allowSingleKey"/>, a key on its own is taken as well as a combination - the push to talk
    /// key, whose default is Scroll Lock alone. Every other shortcut still needs Ctrl, Shift or Alt with it.</summary>
    public HotkeyCaptureForm(bool allowSingleKey)
    {
        this.allowSingleKey = allowSingleKey;
        Text = "Change hotkey";
        KeyPreview = true;
        AccessibleName = "Change hotkey";

        instructionLabel.Text = allowSingleKey
            ? "Press the key - on its own, or with Ctrl, Shift or Alt - then release it to save it automatically. Press Delete to leave this shortcut unassigned."
            : "Hold the full key combination, then release it to save it automatically. Press Delete to leave this shortcut unassigned.";
        // Sizes in this display's pixels (the sighted-user UI audit, 2026-09-28): a fixed 420 x 180 window cut its own
        // words at 150 %. The window is sized to what it holds, below.
        instructionLabel.MaximumSize = new Size(LogicalToDeviceUnits(360), 0);
        hotkeyTextBox.Width = LogicalToDeviceUnits(360);
        hotkeyTextBox.AccessibleName = "Current hotkey";
        hotkeyTextBox.Text = "Press a hotkey combination.";
        // Opens with nothing highlighted (Ed, 2026-09-28; the UI audit's finding 14): WinForms selects all of a text box on
        // its first focus unless a selection has been set, so an empty one is.
        hotkeyTextBox.Select(0, 0);
        hotkeyTextBox.KeyDown += CaptureKeyDown;
        hotkeyTextBox.KeyUp += CaptureKeyUp;

        cancelButton.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        // Escape on the button, as in any window; in the box Escape is taken first, as before.
        CancelButton = cancelButton;

        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(LogicalToDeviceUnits(12)),
            AutoSize = true,
        };
        panel.Controls.Add(instructionLabel);
        panel.Controls.Add(hotkeyTextBox);
        panel.Controls.Add(cancelButton);
        // Cancel at the right, where a dialog's buttons go; still last in the tab order.
        cancelButton.Anchor = AnchorStyles.Right;
        Controls.Add(panel);
        // As big as what it holds, and never dragged smaller than that.
        ClientSize = panel.GetPreferredSize(Size.Empty);
        MinimumSize = Size;

        Shown += (_, _) => hotkeyTextBox.Focus();
    }

    public HotkeyInfo? CapturedHotkey { get; private set; }

    private static int openNow;
    private bool counted;
    /// <summary>True while a window for choosing a key is open anywhere in RemSound. The push to talk key's hook stands
    /// aside meanwhile, or its key could never be chosen again. Counted from the moment the window loads (Shown comes a
    /// message later) until it closes or is thrown away.</summary>
    public static bool AnyOpen => Volatile.Read(ref openNow) > 0;

    protected override void OnLoad(EventArgs e)
    {
        if (!counted) { counted = true; Interlocked.Increment(ref openNow); }
        base.OnLoad(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Uncount();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        Uncount();
        base.Dispose(disposing);
    }

    private void Uncount()
    {
        if (!counted) return;
        counted = false;
        Interlocked.Decrement(ref openNow);
    }

    /// <summary>Test seam: a key-down as Windows delivers it to the window, and whether the window took it.</summary>
    internal bool KeyDownForTest(Keys keyData)
    {
        var msg = Message.Create(Handle, 0x0100, (IntPtr)(int)(keyData & Keys.KeyCode), IntPtr.Zero);
        return ProcessCmdKey(ref msg, keyData);
    }
    /// <summary>Test seam: the key coming up, as Windows delivers it - which is when a key is saved.</summary>
    internal bool KeyUpForTest(Keys keyData)
    {
        var msg = Message.Create(Handle, 0x0101, (IntPtr)(int)(keyData & Keys.KeyCode), IntPtr.Zero);
        return ProcessCmdKey(ref msg, keyData);
    }
    internal TextBox BoxForTest => hotkeyTextBox;
    internal Button CancelForTest => cancelButton;

    /// <summary>True if the user pressed a modifier key (Ctrl / Shift / Alt) at any point
    /// during this capture session. Used in conjunction with <see cref="SawAnyNonModifier"/>
    /// to detect "user tried to bind a combo but the non-modifier key was swallowed by a
    /// low-level keyboard hook" — see <see cref="SawAnyNonModifier"/>.</summary>
    public bool SawAnyModifier { get; private set; }

    /// <summary>True if the user pressed any non-Escape, non-modifier key during this
    /// capture session. If <see cref="SawAnyModifier"/> is true but this is false when the
    /// dialog closes without an OK result, we observed the modifier keys but never the
    /// final key the user was trying to bind — strong indicator that another app
    /// (NVDA / NVDA Remote / AutoHotkey / etc.) is intercepting the combination at a
    /// low-level keyboard hook before our window sees it. The caller can use that to
    /// show a clear "your combo is being hooked elsewhere" message.</summary>
    public bool SawAnyNonModifier { get; private set; }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Keys are a shortcut being chosen only in the box. Tab and Shift+Tab move, as anywhere: every key-down was taken
        // as a shortcut, so the Cancel button the comment above promises could not be reached, nor pressed once it was
        // (2026-09-25 sweep). Tab alone is no global shortcut, so nothing is lost.
        if (ActiveControl != hotkeyTextBox || keyData is Keys.Tab or (Keys.Tab | Keys.Shift)) return base.ProcessCmdKey(ref msg, keyData);
        if (msg.Msg is 0x0100 or 0x0104) { CaptureKeyData(keyData); return true; }
        if (msg.Msg is 0x0101 or 0x0105) { HandleKeyRelease(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void CaptureKeyDown(object? sender, KeyEventArgs e)
    {
        CaptureKeyData(e.KeyData);
        e.SuppressKeyPress = true;
        e.Handled = true;
    }

    private void CaptureKeyUp(object? sender, KeyEventArgs e)
    {
        HandleKeyRelease();
        e.SuppressKeyPress = true;
        e.Handled = true;
    }

    private void CaptureKeyData(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        var control = keyData.HasFlag(Keys.Control);
        var shift = keyData.HasFlag(Keys.Shift);
        var alt = keyData.HasFlag(Keys.Alt);

        if (key == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return; }

        // Plain Delete (no modifiers) clears the binding — the action becomes unassigned, mirroring Del
        // on the shortcuts list. Delete WITH modifiers is left to bind as a normal combination.
        if (key == Keys.Delete && !control && !shift && !alt)
        {
            CapturedHotkey = HotkeyInfo.Unset;
            DialogResult = DialogResult.OK;
            Close();
            return;
        }

        if (IsModifier(key))
        {
            SawAnyModifier = true;
            capturingCombination = true;
            hotkeyTextBox.Text = BuildModifierPrompt(control, alt, shift);
            return;
        }

        // Anything that passed the Escape + IsModifier filters is a "real" non-modifier key.
        // Tracking this lets the caller distinguish "user pressed Esc immediately" from
        // "user held modifiers but the non-modifier key was eaten by a low-level hook".
        SawAnyNonModifier = true;

        var proposed = new HotkeyInfo(key, control, shift, alt);
        if (!proposed.IsValid && !(allowSingleKey && key is not Keys.None))
        {
            hotkeyTextBox.Text = "Hotkey must include a modifier and a non-modifier key.";
            return;
        }

        capturingCombination = true;
        pendingHotkey = proposed;
        hotkeyTextBox.Text = proposed.ToString();
    }

    private void HandleKeyRelease()
    {
        if (!capturingCombination || pendingHotkey is null) return;
        CapturedHotkey = pendingHotkey;
        pendingHotkey = null;
        capturingCombination = false;
        BeginInvoke(() => { DialogResult = DialogResult.OK; Close(); });
    }

    private static bool IsModifier(Keys key) =>
        key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu
            or Keys.LControlKey or Keys.RControlKey
            or Keys.LShiftKey or Keys.RShiftKey
            or Keys.LMenu or Keys.RMenu;

    private static string BuildModifierPrompt(bool control, bool alt, bool shift)
    {
        var parts = new List<string>(3);
        if (control) parts.Add("Control");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        return parts.Count == 0 ? "Press a full key combination." : string.Join("+", parts) + "+...";
    }
}
