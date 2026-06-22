using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// One-time upgrade dialog: keyboard shortcuts moved from per-profile to machine-wide storage, so the
/// user is offered the choice of copying their shortcut set from one of their existing profiles (whose
/// old per-profile shortcuts are still readable in the profile files) — or starting fresh at the
/// defaults. Accessible: a profile list plus two clearly-labelled buttons, focus landing on the list.
/// </summary>
internal sealed class KeyboardShortcutImportDialog : Form
{
    private readonly ListBox profileList = new()
    {
        IntegralHeight = false,
        Width = 400,
        Height = 150,
        AccessibleName = "Profile to copy keyboard shortcuts from",
        TabIndex = 0,
    };

    /// <summary>The profile the user chose to copy shortcuts from, or null if they chose to start fresh.
    /// Only meaningful when <see cref="Form.ShowDialog()"/> returned <see cref="DialogResult.OK"/>.</summary>
    public string? ChosenProfileTitle { get; private set; }

    public KeyboardShortcutImportDialog(IReadOnlyList<string> profileTitles)
    {
        Text = "Keyboard shortcuts";
        AccessibleName = "Bring your keyboard shortcuts across";
        AccessibleRole = AccessibleRole.Dialog;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        ClientSize = new Size(470, 330);

        var info = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(440, 0),
            Text = "Keyboard shortcuts are now shared across all your profiles, instead of being saved "
                 + "separately in each one.\n\n"
                 + "You can copy your shortcuts from one of your existing profiles, or start fresh with "
                 + "the defaults. Pick a profile below, then choose what to do.",
        };

        var listLabel = new MnemonicLabel
        {
            Text = "Copy shortcuts from this &profile:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            MnemonicTarget = profileList,
        };

        foreach (var t in profileTitles) profileList.Items.Add(t);
        if (profileList.Items.Count > 0) profileList.SelectedIndex = 0;

        var useButton = new Button
        {
            Text = "&Use the shortcuts from this profile",
            AccessibleName = "Use the shortcuts from this profile",
            AutoSize = true,
            TabIndex = 1,
        };
        useButton.Click += (_, _) =>
        {
            if (profileList.SelectedItem is string title)
            {
                ChosenProfileTitle = title;
                DialogResult = DialogResult.OK;
                Close();
            }
        };

        var freshButton = new Button
        {
            Text = "Start &fresh with the defaults",
            AccessibleName = "Start fresh with the defaults",
            AutoSize = true,
            TabIndex = 2,
        };
        freshButton.Click += (_, _) =>
        {
            ChosenProfileTitle = null;
            DialogResult = DialogResult.OK;
            Close();
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0),
        };
        buttons.Controls.Add(useButton);
        buttons.Controls.Add(freshButton);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 4,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(info, 0, 0);
        layout.Controls.Add(listLabel, 0, 1);
        layout.Controls.Add(profileList, 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);

        AcceptButton = useButton;

        // Escape leaves the choice unmade — the caller treats a non-OK result as "ask again next launch",
        // so the user is never forced into a decision they didn't mean to make.
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); e.Handled = true; e.SuppressKeyPress = true; }
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Land focus on the profile list so NVDA announces the dialog and the first profile.
        ActiveControl = null;
        profileList.Focus();
        WinEventNotifier.NotifyFocus(profileList);
    }
}
