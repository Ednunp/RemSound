using RemSound.Core;

namespace RemSound.App;

/// <summary>Tiny single-line modal — "give this profile a name". Used by File → Rename
/// (and historically by File → Save As, before that flow moved to a real Windows
/// SaveFileDialog on 2026-05-10). Returns the trimmed name or null on cancel.
///
/// Two parameter knobs let the dialog title and prompt label change between use cases:
///   * Rename: title = "Rename profile", prompt = "Please enter a new name for your profile:"
///   * Legacy save-as (close-confirm path): title = "Save profile as", prompt = "Profile name:"
///     and pass <paramref name="store"/> non-null so the dialog refuses an existing-name unless
///     the user confirms overwrite.
///
/// When <paramref name="store"/> is null, no overwrite check is performed — the caller is
/// responsible for handling name collisions (rename has its own conflict logic in MainForm).</summary>
internal static class ProfileSaveAsPrompt
{
    public static string? Show(
        IWin32Window owner,
        ProfileStore? store,
        string? defaultName = null,
        string dialogTitle = "Save profile as",
        string promptLabel = "Profile name:")
    {
        var (dialog, textBox) = Build(defaultName, dialogTitle, promptLabel);
        using (dialog)
        {
            while (true)
            {
                if (dialog.ShowDialog(owner) != DialogResult.OK) return null;
                var name = textBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    MessageBox.Show(owner, "Please enter a profile name.", "RemSound", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    continue;
                }
                if (store is not null && store.Exists(name))
                {
                    var overwrite = MessageBox.Show(owner,
                        $"A profile named \"{name}\" already exists. Overwrite?",
                        "Confirm overwrite", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                    if (overwrite != DialogResult.Yes) continue;
                }
                return name;
            }
        }
    }

    /// <summary>Construction split from ShowDialog so the accessibility audit can inspect the real
    /// dialog (inline-built dialogs used to be invisible to the audit).</summary>
    internal static (Form Dialog, TextBox Input) Build(
        string? defaultName = null,
        string dialogTitle = "Save profile as",
        string promptLabel = "Profile name:")
    {
        var dialog = new Form
        {
            Text = dialogTitle,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(420, 140),
        };
        var textBox = new TextBox
        {
            Width = 380,
            Text = defaultName ?? "",
            AccessibleName = promptLabel.TrimEnd(':', ' '),
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
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            RowCount = 3,
            ColumnCount = 1,
        };
        panel.Controls.Add(new Label { Text = promptLabel, AutoSize = true }, 0, 0);
        panel.Controls.Add(textBox, 0, 1);
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
        };
        buttons.Controls.Add(okButton);
        buttons.Controls.Add(cancelButton);
        panel.Controls.Add(buttons, 0, 2);
        dialog.Controls.Add(panel);
        dialog.AcceptButton = okButton;
        dialog.CancelButton = cancelButton;
        return (dialog, textBox);
    }
}
