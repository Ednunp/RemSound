namespace RemSound.App;

/// <summary>
/// Small dialog for viewing and changing the active profile's encryption password. Shows the
/// current password in a PLAIN (readable) edit box, not a masked one, on purpose: this audience
/// uses a screen reader, and a masked password field reads as a run of bullets, which is
/// useless. Letting NVDA read the actual characters is far more usable, and the security model
/// already accepts that the password is recoverable from the profile file anyway.
///
/// Returns the new password on OK (which may be empty — that clears the password), or null on
/// Cancel. The result is trimmed so an invisible trailing space can't cause a maddening
/// "we typed the same password but it won't connect" mismatch between two peers. 2026-05-31.
/// </summary>
internal static class ProfilePasswordDialog
{
    /// <summary>The pure "should this password entry be rejected, and why" decision, shared by both
    /// password dialogs and unit-testable without any UI (2026-07-27). Returns the plain-English
    /// advice to show, or null to accept. Rules: an empty entry is not judged here (the
    /// requireNonEmpty gate owns that); a CHANGED entry is always judged; an UNCHANGED entry is
    /// exempt UNLESS <paramref name="requireStrong"/> — the streaming gate's mode, where the whole
    /// point is that the current password already failed the rule, so re-entering it must be
    /// refused. Both sides are compared trimmed (fixes the App-review trim inconsistency).</summary>
    internal static string? RejectionAdviceFor(string entered, string current, bool requireStrong)
    {
        entered = entered.Trim();
        if (entered.Length == 0) return null;
        if (!requireStrong && string.Equals(entered, current.Trim(), StringComparison.Ordinal)) return null;
        return RemSound.Core.PasswordStrength.Critique(entered);
    }

    public static string? Show(string profileTitle, string currentPassword, bool requireNonEmpty = false, bool requireStrong = false)
    {
        var (dialog, textBox) = Build(profileTitle, currentPassword, requireNonEmpty, requireStrong);
        using (dialog)
        {
            // Run with a foreground 1×1 owner so the prompt jumps to the front even when RemSound is
            // sitting minimised in the tray — e.g. a quick profile switch to a passwordless-but-
            // streaming profile trips the password gate mid-switch, and the user must be able to read
            // and answer it there and then. Centres on screen, forces focus, then closes.
            return ForegroundDialog.Show(owner => dialog.ShowDialog(owner)) == DialogResult.OK
                ? textBox.Text.Trim()
                : null;
        }
    }

    /// <summary>Construction split from ShowDialog so the accessibility audit can inspect the real
    /// dialog (inline-built dialogs used to be invisible to the audit).</summary>
    internal static (Form Dialog, TextBox Input) Build(string profileTitle, string currentPassword, bool requireNonEmpty = false, bool requireStrong = false)
    {
        var dialog = new Form
        {
            Text = "Change profile password",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(460, 150),
            AccessibleName = "Change profile password",
        };

        var label = new Label
        {
            Text = $"Password for profile “{profileTitle}”:",
            AutoSize = true,
        };
        var textBox = new TextBox
        {
            Text = currentPassword,
            Dock = DockStyle.Top,
            Width = 400,
            AccessibleName = $"Password for profile {profileTitle}",
            // Mark as a password field so the key-click hook layers in the passkey sound.
            Tag = KeyClickService.PasswordFieldTag,
        };
        var hint = new Label
        {
            Text = "Both you and the person you're connecting to must use the same password.",
            AutoSize = true,
        };

        var okButton = new Button { Text = "&OK", AutoSize = true };
        var cancelButton = new Button { Text = "&Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };

        // OK is validated by hand (no auto-close DialogResult) so we can block an empty entry when
        // a password is being REQUIRED. The trigger is the OK/Enter action, not typing — and
        // deliberately clearing an already-set password to blank is still allowed, because that
        // path passes requireNonEmpty: false. So this only catches "was asked for a password,
        // entered nothing, pressed OK", which used to silently leave audio dead.
        void TryAccept()
        {
            var entered = textBox.Text.Trim();
            if (requireNonEmpty && entered.Length == 0)
            {
                var page = new TaskDialogPage
                {
                    Caption = "Password required",
                    Heading = "Enter a password",
                    Text = "A password is required before audio can flow. You and the person you're connecting to must use the same one.",
                    Icon = TaskDialogIcon.Warning,
                    Buttons = { TaskDialogButton.OK },
                    DefaultButton = TaskDialogButton.OK,
                    AllowCancel = true,
                };
                TaskDialog.ShowDialog(dialog, page);
                textBox.Focus();
                textBox.SelectAll();
                return;
            }
            // Strength gate (2026-07-27) — the decision lives in the pure RejectionAdviceFor so a
            // test can pin it without a modal dialog (and both password dialogs share one rule).
            if (RejectionAdviceFor(entered, currentPassword, requireStrong) is { } advice)
            {
                var page = new TaskDialogPage
                {
                    Caption = "Choose a stronger password",
                    Heading = "That password is too easy to guess",
                    Text = advice,
                    Icon = TaskDialogIcon.Warning,
                    Buttons = { TaskDialogButton.OK },
                    DefaultButton = TaskDialogButton.OK,
                    AllowCancel = true,
                };
                TaskDialog.ShowDialog(dialog, page);
                textBox.Focus();
                textBox.SelectAll();
                return;
            }
            dialog.DialogResult = DialogResult.OK;
            dialog.Close();
        }

        okButton.Click += (_, _) => TryAccept();
        textBox.KeyDown += (_, args) =>
        {
            if (args.KeyCode == Keys.Enter)
            {
                args.Handled = true;
                args.SuppressKeyPress = true;
                TryAccept();
            }
        };

        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 4, ColumnCount = 1 };
        panel.Controls.Add(label, 0, 0);
        panel.Controls.Add(textBox, 0, 1);
        panel.Controls.Add(hint, 0, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        buttons.Controls.Add(okButton);
        buttons.Controls.Add(cancelButton);
        panel.Controls.Add(buttons, 0, 3);
        dialog.Controls.Add(panel);
        dialog.AcceptButton = okButton;
        dialog.CancelButton = cancelButton;
        return (dialog, textBox);
    }
}
