namespace RemSound.App;

/// <summary>
/// The window F1 opens: one control's help, in the manual's own words, and a way to the full manual.
///
/// <para>The keyboard lands on the words with the cursor at the start and nothing selected, so a sighted person does not
/// meet a solid block of highlight (Ed, 2026-09-28: "someone can read it with nvda no problem anyway"; until then all of
/// it was selected so NVDA read it all out on arrival). The arrow keys go through it line by line from the top. Escape
/// closes it; the keyboard goes back to the control F1 was pressed on.</para>
/// </summary>
internal sealed class ContextHelpWindow : Form
{
    internal TextBox HelpText { get; }
    internal Button OpenInManualButton { get; }
    internal Button CloseButton { get; }

    /// <summary>The help key the words came from: the entry the manual button opens.</summary>
    internal string Key { get; }

    public ContextHelpWindow(string name, string text, string key)
    {
        Key = key;
        Text = $"Context help: {name}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = Dpi.Px(560, 340);
        Padding = Dpi.Pad(12);

        HelpText = new TextBox
        {
            Text = text,
            Multiline = true,
            ReadOnly = true,
            WordWrap = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            AccessibleName = "Help",
            TabIndex = 0,
        };
        OpenInManualButton = new Button
        {
            Text = "&Open the manual in your default browser",
            AccessibleName = "Open the manual in your default browser",
            AutoSize = true,
            TabIndex = 1,
        };
        CloseButton = new Button
        {
            Text = "&Close",
            AccessibleName = "Close",
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
            TabIndex = 2,
        };
        OpenInManualButton.Click += (_, _) =>
        {
            ContextHelp.OpenManualInBrowser?.Invoke(Key);
            Close();
        };

        // Right-aligned, in the order they always had (UI audit 2026-09-28, finding 11).
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            WrapContents = false,
            TabIndex = 1,
        };
        buttons.Controls.Add(OpenInManualButton);
        buttons.Controls.Add(CloseButton);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(HelpText, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        Controls.Add(layout);

        CancelButton = CloseButton;
        ActiveControl = HelpText;
    }

    /// <summary>Nothing selected, the cursor at the start, set before the window takes the keyboard. WinForms selects the
    /// whole box itself on first focus unless a selection has been set first; Select(0, 0) is what tells it one has.</summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        FormLayoutRows.OpenWithNothingSelected(HelpText);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        ContextHelp.PlayCloseSound?.Invoke();
        ContextHelp.Log?.Invoke($"context help: closed ({Key})");
    }
}
