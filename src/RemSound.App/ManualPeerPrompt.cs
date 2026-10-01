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
            ClientSize = Dpi.Px(440, 130),
        };
        // No port hint — RemSound now uses a single canonical UDP port (RemPacket.DefaultPort,
        // 47830 as of 2026-05-05) for Tailscale, LAN, and relay peers alike. Users just type a
        // bare IP or hostname; the port is implied. Advanced users can still suffix `:port` for
        // a non-standard server, but it's no longer the common path that needed onboarding.
        var textBox = new TextBox
        {
            Dock = DockStyle.Top,
            Width = Dpi.Px(380),
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
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = Dpi.Pad(12), RowCount = 3, ColumnCount = 1 };
        panel.Controls.Add(new Label { Text = "Peer IP address or hostname:", AutoSize = true }, 0, 0);
        panel.Controls.Add(textBox, 0, 1);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        // OK then Cancel, as Windows orders them (Ed, 2026-09-28): right to left, so Cancel goes in first; OK first for Tab too.
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(okButton);
        okButton.TabIndex = 0;
        cancelButton.TabIndex = 1;
        panel.Controls.Add(buttons, 0, 2);
        dialog.Controls.Add(panel);
        dialog.AcceptButton = okButton;
        dialog.CancelButton = cancelButton;
        ContextHelp.Mark(textBox, "dialog.manual-peer-prompt.address");
        ContextHelp.Mark(okButton, "dialog.manual-peer-prompt.ok");
        ContextHelp.Mark(cancelButton, "dialog.manual-peer-prompt.cancel");
        return (dialog, textBox);
    }
}
