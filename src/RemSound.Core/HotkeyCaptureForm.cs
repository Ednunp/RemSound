using System.Windows.Forms;

namespace RemSound.Core;

public sealed class HotkeyCaptureForm : Form
{
    private readonly Label instructionLabel = new() { AutoSize = true };
    private readonly TextBox hotkeyTextBox = new() { ReadOnly = true, Width = 360 };
    private readonly Button cancelButton = new() { Text = "Cancel", AutoSize = true };
    private HotkeyInfo? pendingHotkey;
    private bool capturingCombination;

    public HotkeyCaptureForm()
    {
        Text = "Change hotkey";
        Width = 420;
        Height = 180;
        KeyPreview = true;
        AccessibleName = "Change hotkey";

        instructionLabel.Text = "Hold the full key combination, then release it to save it automatically. Press Delete to leave this shortcut unassigned.";
        instructionLabel.MaximumSize = new Size(360, 0);
        hotkeyTextBox.AccessibleName = "Current hotkey";
        hotkeyTextBox.Text = "Press a hotkey combination.";
        hotkeyTextBox.KeyDown += CaptureKeyDown;
        hotkeyTextBox.KeyUp += CaptureKeyUp;

        cancelButton.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(12),
            AutoSize = true,
        };
        panel.Controls.Add(instructionLabel);
        panel.Controls.Add(hotkeyTextBox);
        panel.Controls.Add(cancelButton);
        Controls.Add(panel);

        Shown += (_, _) => hotkeyTextBox.Focus();
    }

    public HotkeyInfo? CapturedHotkey { get; private set; }

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
        if (!proposed.IsValid)
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
