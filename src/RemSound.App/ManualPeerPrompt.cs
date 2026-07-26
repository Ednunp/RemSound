namespace RemSound.App;

internal static class ManualPeerPrompt
{
    public static string? Show(IWin32Window owner)
    {
        var (dialog, textBox) = Build();
        using (dialog)
        {
            return dialog.ShowDialog(owner) == DialogResult.OK ? textBox.Text.Trim() : null;
        }
    }

    /// <summary>Construction split from ShowDialog so the accessibility audit can inspect the real
    /// dialog (inline-built dialogs used to be invisible to the audit — which is exactly how missing
    /// mnemonics slipped through).</summary>
    internal static (Form Dialog, TextBox Input) Build()
    {
        var dialog = new Form
        {
            Text = "Add manual peer",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(440, 130),
        };
        // No port hint — RemSound now uses a single canonical UDP port (RemPacket.DefaultPort,
        // 47830 as of 2026-05-05) for Tailscale, LAN, and relay peers alike. Users just type a
        // bare IP or hostname; the port is implied. Advanced users can still suffix `:port` for
        // a non-standard server, but it's no longer the common path that needed onboarding.
        var textBox = new TextBox
        {
            Dock = DockStyle.Top,
            Width = 380,
            AccessibleName = "Peer IP address or hostname",
        };
        var okButton = new Button { Text = "&OK", AutoSize = true, DialogResult = DialogResult.OK };
        var cancelButton = new Button { Text = "&Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        textBox.KeyDown += (_, args) =>
        {
            if (args.KeyCode == Keys.Enter)
            {
                dialog.DialogResult = DialogResult.OK;
                dialog.Close();
                args.Handled = true;
                args.SuppressKeyPress = true;
            }
        };
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 3, ColumnCount = 1 };
        panel.Controls.Add(new Label { Text = "Peer IP address or hostname:", AutoSize = true }, 0, 0);
        panel.Controls.Add(textBox, 0, 1);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        buttons.Controls.Add(okButton);
        buttons.Controls.Add(cancelButton);
        panel.Controls.Add(buttons, 0, 2);
        dialog.Controls.Add(panel);
        dialog.AcceptButton = okButton;
        dialog.CancelButton = cancelButton;
        return (dialog, textBox);
    }
}
