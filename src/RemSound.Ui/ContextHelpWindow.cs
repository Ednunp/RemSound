namespace RemSound.App;

/// <summary>
/// The window F1 opens: one control's help, in the manual's own words, and a way to the full manual.
///
/// <para>The keyboard lands on the words with all of them selected and the cursor at the start, so NVDA reads the whole
/// entry out as the window opens, as it reads a message box (Ed's choice, 2026-09-25), and the arrow keys then go through
/// it line by line from the top. Escape closes it; the keyboard goes back to the control F1 was pressed on.</para>
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
        ClientSize = new Size(560, 340);
        Padding = new Padding(12);

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

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Dock = DockStyle.Fill,
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

    /// <summary>Everything selected, the cursor at the start, set before the window takes the keyboard - so the screen
    /// reader finds the selection already there when it announces the words. WinForms selects the whole box itself on
    /// first focus, cursor at the END, unless a selection has been set first; Select(0, 0) is what tells it one has.</summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        HelpText.Select(0, 0);
        ContextHelp.SendMessage(HelpText.Handle, ContextHelp.EM_SETSEL, HelpText.TextLength, IntPtr.Zero);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        ContextHelp.PlayCloseSound?.Invoke();
        ContextHelp.Log?.Invoke($"context help: closed ({Key})");
    }
}
