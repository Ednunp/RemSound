namespace RemSound.App;

internal static class FormLayoutRows
{
    /// <summary>How wide a list may stretch, in 96-DPI pixels. Wider than this a line of text is hard to follow, and
    /// Microsoft's layout guidance says to stop stretching at a sensible width (UI audit 2026-09-28, finding 16).</summary>
    internal const int MaxFillWidth = 600;

    /// <summary>How narrow a stretching list may get, in 96-DPI pixels, before its row scrolls sideways instead.</summary>
    internal const int MinFillWidth = 160;

    public static void AddRow(TableLayoutPanel panel, int row, string labelText, Control control, Action<Control> focusControl)
    {
        var label = new MnemonicLabel { Text = labelText, AutoSize = true, Anchor = AnchorStyles.Left, MnemonicTarget = control };
        label.Click += (_, _) => focusControl(control);
        // Beside a tall control the label sits level with its top, not halfway down it (finding 8). A one-line control
        // keeps it centred, which is level with its text.
        if (IsTall(control, label)) LevelWithTop(label, control.Margin.Top);
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
        // For sighted people (UI audit 2026-09-28): the label level with the list's top (finding 8); the list as wide as
        // its column, up to a limit (findings 4 and 16); and the status sentence under it wrapped within the column in
        // secondary grey (finding 9, stage one - the words are exactly the same). Positions and colours only.
        LevelWithTop(label, container.Margin.Top + list.Margin.Top);
        statusLabel.ForeColor = SystemColors.GrayText;
        FillWidth(container, list, statusLabel);
        return label;
    }

    /// <summary>Tall enough that a label beside it should sit level with its top rather than centred on it: a list, a
    /// text box of several lines, a slider, or anything over twice the label's own height.</summary>
    internal static bool IsTall(Control control, Label label) =>
        control is ListBox or TrackBar or TextBoxBase { Multiline: true } || control.Height > 2 * label.PreferredHeight;

    /// <summary>Put a label beside a tall control level with the control's top: anchored to the top of its row, a
    /// little lower than the control's edge so its words line up with the control's first line (Microsoft's layout
    /// guidance puts a label's text a few pixels down from the top of the control it names). <paramref name="controlTop"/>
    /// is how far the control's top sits below the top of the row.</summary>
    internal static void LevelWithTop(Label label, int controlTop)
    {
        label.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        label.Margin = new Padding(label.Margin.Left, controlTop + Dpi.Px(3), label.Margin.Right, label.Margin.Bottom);
    }

    /// <summary>A control placed straight in a table cell stretches across its column, up to <see cref="MaxFillWidth"/>,
    /// instead of keeping one fixed width whatever the window's size.</summary>
    internal static void StretchInCell(Control control)
    {
        // A text box of several lines keeps the height it was given: left to size itself, the table measures it as one line.
        if (control is TextBoxBase { Multiline: true }) control.AutoSize = false;
        control.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        // In line with the lists in wrapper panels above and below it, whose panel's own 3-pixel margin sets them in by that much.
        control.Margin = new Padding(control.Margin.Left + 3, control.Margin.Top, control.Margin.Right + 3, control.Margin.Bottom);
        control.MinimumSize = new Size(Dpi.Px(MinFillWidth), 0);
        // The height it has, not 0: a maximum of 0 is taken as a real limit for a control that does not size itself, and
        // squeezed a text box to nothing.
        control.MaximumSize = new Size(Dpi.Px(MaxFillWidth), control.Height);
    }

    /// <summary>A read-only message box with no scroll bar made tall enough for all its words at its width, however big the
    /// display's text is. A fixed height cut the last lines off (UI audit 2026-09-28).</summary>
    internal static void FitReadOnlyText(TextBox box)
    {
        var words = TextRenderer.MeasureText(box.Text, box.Font, new Size(Math.Max(1, box.Width - Dpi.Px(6)), int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        box.Height = Math.Max(box.Height, words.Height + Dpi.Px(6));
    }

    /// <summary>A read-only message that opens with the cursor at its start and nothing highlighted (Ed, 2026-09-28: "someone
    /// can read it with nvda no problem anyway"; UI audit finding 14). WinForms selects all of a text box the first time it
    /// takes the keyboard unless a selection has been set; this sets an empty one, before the box is shown.</summary>
    internal static void OpenWithNothingSelected(TextBox box) => box.Select(0, 0);

    /// <summary>A control in a top-down wrapper panel (the wrapping NVDA needs, see above) as wide as the wrapper, up to
    /// <see cref="MaxFillWidth"/>, and a label under it wrapped to the same width. A top-down panel never stretches its
    /// children to its own width, so this follows the wrapper's size as the window's changes.</summary>
    internal static void FillWidth(FlowLayoutPanel wrapper, Control? stretch, Label? wrap)
    {
        void Fit()
        {
            var room = wrapper.ClientSize.Width - wrapper.Padding.Horizontal;
            if (room <= 0) return;
            var width = room;
            if (stretch is not null)
            {
                width = Math.Clamp(room - stretch.Margin.Horizontal, Dpi.Px(MinFillWidth), Dpi.Px(MaxFillWidth));
                if (stretch.Width != width) stretch.Width = width;
            }
            if (wrap is not null)
            {
                var most = new Size(Math.Max(1, stretch is null ? room - wrap.Margin.Horizontal : width), 0);
                if (wrap.MaximumSize != most) wrap.MaximumSize = most;
            }
        }
        wrapper.SizeChanged += (_, _) => Fit();
        Fit();
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
/// CreateAccessibilityInstance throws InvalidOperationException. RemSound targets .NET 10, and the
/// main window, Preferences and the service dialog all use this control, so a regression would show
/// at once. If it does, the fallback is to remove the override and accept the announcement.
/// </summary>
internal sealed class QuietTabControl : TabControl
{
    /// <summary>How a tab switch makes its sound. Injected so this control carries no dependency on
    /// the app's cue machinery — see AccessibleCheckBox.PlayToggleSound. The app wires it at startup;
    /// the plugin leaves it null and is silent.</summary>
    public static Action? PlayTabSwitchSound { get; set; }

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
        // Injected for the same reason as AccessibleCheckBox.PlayToggleSound — see there.
        if (ContainsFocus) PlayTabSwitchSound?.Invoke();
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
