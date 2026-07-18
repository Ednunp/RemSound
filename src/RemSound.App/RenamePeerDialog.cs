namespace RemSound.App;

/// <summary>Modal dialog for giving a peer a friendly name. Shows the machine name for reference, a
/// text box for the custom name, and a "Clear custom name" button that wipes it back to the machine
/// name. OK (or Clear) returns DialogResult.OK; the caller reads <see cref="FriendlyName"/> — null
/// meaning "no custom name" (cleared or left blank). Cancel/Escape makes no change.</summary>
internal sealed class RenamePeerDialog : Form
{
    private readonly TextBox nameBox = new()
    {
        Width = 260,
        TabIndex = 0,
        AccessibleName = "Friendly name (Alt+N)",
    };

    /// <summary>The chosen name, valid only when ShowDialog returned OK. Null = no custom name.</summary>
    public string? FriendlyName { get; private set; }

    public RenamePeerDialog(string machineName, string? currentCustomName)
    {
        Text = "Rename peer";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(380, 170);

        nameBox.Text = currentCustomName ?? "";

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 2,
            RowCount = 4,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var heading = Theme.Heading("Rename peer");
        grid.Controls.Add(heading, 0, 0);
        grid.SetColumnSpan(heading, 2);

        var machineLabel = new Label
        {
            Text = $"Machine name: {machineName}",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 0, 8),
        };
        grid.Controls.Add(machineLabel, 0, 1);
        grid.SetColumnSpan(machineLabel, 2);

        // Plain label with '&' wires the mnemonic to the next control in tab order (the text box).
        var nameLabel = new Label { Text = "Friendly &name (Alt+N)", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 8, 0) };
        grid.Controls.Add(nameLabel, 0, 2);
        grid.Controls.Add(nameBox, 1, 2);

        var clearButton = new Button { Text = "&Clear custom name (Alt+C)", AutoSize = true, AccessibleName = "Clear custom name", TabIndex = 1 };
        var okButton = new Button { Text = "&OK", AutoSize = true, DialogResult = DialogResult.OK, TabIndex = 2 };
        // Cancel keeps Esc (it's the CancelButton) rather than a mnemonic — Alt+C is the Clear button here.
        var cancelButton = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel, TabIndex = 3 };

        clearButton.Click += (_, _) =>
        {
            FriendlyName = null;
            DialogResult = DialogResult.OK;
            Close();
        };
        okButton.Click += (_, _) =>
        {
            var t = nameBox.Text.Trim();
            FriendlyName = t.Length == 0 ? null : t;   // blank box = clear, same as the Clear button
        };

        var buttonRow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, AutoSize = true, TabIndex = 2 };
        buttonRow.Controls.Add(clearButton);
        buttonRow.Controls.Add(okButton);
        buttonRow.Controls.Add(cancelButton);
        grid.Controls.Add(buttonRow, 1, 3);

        Controls.Add(grid);
        AcceptButton = okButton;
        CancelButton = cancelButton;

        Shown += (_, _) => { nameBox.Focus(); nameBox.SelectAll(); };
    }
}
