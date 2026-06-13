namespace RemSound.App;

internal static class FormLayoutRows
{
    public static void AddRow(TableLayoutPanel panel, int row, string labelText, Control control, Action<Control> focusControl)
    {
        var label = new MnemonicLabel { Text = labelText, AutoSize = true, Anchor = AnchorStyles.Left, MnemonicTarget = control };
        label.Click += (_, _) => focusControl(control);
        panel.Controls.Add(label, 0, row);
        panel.Controls.Add(control, 1, row);
    }

    public static MnemonicLabel AddCheckedListRow(TableLayoutPanel panel, int row, string labelText, CheckedListBox list, Label statusLabel, Action<CheckedListBox> focusList)
    {
        // Restored the FlowLayoutPanel wrapping (matches the legacy/working RSound layout).
        // Removing it caused NVDA to mis-pair labels with controls (off-by-one shift across
        // the form), so the keyboard-shortcut announcements went to the wrong controls. The
        // wrapping isn't ideal for label.ProcessMnemonic forwarding, but ProcessCmdKey handles
        // the actual Alt+letter activation explicitly so we don't need to rely on that path.
        // Returns the label so callers can update its text on mode changes (e.g. ASIO ⇄ WASAPI).
        var label = new MnemonicLabel { Text = labelText, AutoSize = true, Anchor = AnchorStyles.Left, MnemonicTarget = list };
        label.Click += (_, _) => focusList(list);
        panel.Controls.Add(label, 0, row);
        var container = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            TabStop = false,
        };
        container.Controls.Add(list);
        container.Controls.Add(statusLabel);
        panel.Controls.Add(container, 1, row);
        return label;
    }
}

/// <summary>
/// Form that exposes a delegate hook for ProcessCmdKey, so the dialog's Alt+letter activations
/// can be wired up from the closure-based dialog construction code without subclassing Form per
/// dialog type. Every Alt+key combo passes through the delegate first; if it returns true the
/// keystroke is consumed.
/// </summary>
internal sealed class CmdKeyForm : Form
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<Keys, bool>? CmdKeyHandler { get; set; }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (CmdKeyHandler is { } handler && handler(keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }
}

/// <summary>
/// TabControl subclass that suppresses the parent role announcement NVDA picks up from the
/// modern .NET 10 UIA exposure. The base class reports itself as a UIA Tab control type;
/// NVDA reads "tab control" before each tab's name. By returning AccessibleRole.None from
/// our custom AccessibleObject we hide the parent's role from screen readers entirely.
///
/// Andre's accessible-readout app reads cleanly because it's compiled against .NET Framework
/// 4.x, whose WinForms TabControl exposes less detail to MSAA/UIA. .NET 10 added more, and
/// Microsoft removed the opt-out — so this subclass is the only path.
///
/// Risk: dotnet/winforms#11831 was filed against .NET 8 reporting that overriding
/// CreateAccessibilityInstance throws InvalidOperationException. Status uncertain on .NET 10.
/// If we hit that exception at runtime, the fallback is to remove the override and accept
/// the announcement.
/// </summary>
internal sealed class QuietTabControl : TabControl
{
    protected override AccessibleObject CreateAccessibilityInstance()
        => new QuietAcc(this);

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        base.OnSelectedIndexChanged(e);
        // Audible tab-switch cue, app-wide (every QuietTabControl gets it for free). Gated on
        // ContainsFocus so a genuine user switch - arrow keys with the strip focused, or Ctrl+Tab
        // with focus on a control inside the active page - clicks, while the programmatic
        // SelectedIndex set done while a window is being built or its last tab restored (nothing
        // here focused yet) stays silent.
        if (ContainsFocus) TabSwitchSoundService.Play();
    }

    private sealed class QuietAcc : ControlAccessibleObject
    {
        public QuietAcc(Control owner) : base(owner) { }
        // None hides the role from screen readers. NVDA falls through to the focused TabItem
        // child whose role is "tab" — and reads only that. No "tab control" prefix.
        public override AccessibleRole Role => AccessibleRole.None;
        // Empty name so NVDA doesn't read a parent name either.
        public override string? Name { get => string.Empty; set { } }
    }
}

/// <summary>
/// Label that forwards its Alt+letter mnemonic activation to an explicit target control rather
/// than to "the next focusable control" (the default WinForms behaviour). Necessary because
/// SelectNextControl is unreliable across container boundaries — when a list is wrapped in a
/// FlowLayoutPanel for layout purposes, the default mnemonic walk skips past the panel and
/// focuses whatever comes after it in the parent panel.
/// </summary>
internal sealed class MnemonicLabel : Label
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Control? MnemonicTarget { get; set; }

    protected override bool ProcessMnemonic(char charCode)
    {
        if (!UseMnemonic || !IsMnemonic(charCode, Text)) return false;
        if (MnemonicTarget is { } target && target.CanFocus)
        {
            target.Focus();
            // Force NVDA to re-announce. Same load-bearing pattern the AccessibleCheckBox uses
            // for state changes — the WinForms-built-in focus event isn't always picked up by
            // the screen reader, especially when focus moves into a control wrapped in a
            // FlowLayoutPanel via a synchronous Focus() call.
            WinEventNotifier.NotifyFocus(target);
            return true;
        }
        return false;
    }
}
