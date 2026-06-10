using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Modal dialog for the three "what should RemSound do at launch" toggles:
///   * Start minimised — main window goes to the tray immediately after Show.
///   * Start RemSound automatically with this user — wires HKCU\...\Run.
///   * Start with a specific profile — skips the startup picker and loads the chosen
///     profile directly. Companion listbox of saved profiles appears alongside.
///
/// The dialog persists changes through <see cref="AppConfig"/> (StartMinimised /
/// StartWithProfileTitle) and through the Windows registry (the auto-start checkbox).
/// Each change is committed immediately, no OK/Apply button — same per-tick-saves
/// pattern as the rest of the Profiles and preferences tab.
///
/// Keyboard shape (per Ed's spec):
///   * Tab cycles: minimised checkbox → auto-start checkbox → specific-profile
///     checkbox → profiles list (when shown) → Close button.
///   * Esc or the Close button closes.
///   * Each checkbox has an Alt+letter mnemonic.
/// </summary>
internal sealed class StartupBehaviourDialog : Form
{
    private readonly AccessibleCheckBox startMinimisedBox = new()
    {
        Text = "Start minimised to tray (Alt+&M)",
        AccessibleName = "Start minimised to tray",
        AutoSize = true,
    };
    private readonly AccessibleCheckBox startWithUserBox = new()
    {
        Text = "Start RemSound automatically when this user logs in (Alt+&A)",
        AccessibleName = "Start RemSound automatically when this user logs in",
        AutoSize = true,
    };
    private readonly AccessibleCheckBox startWithProfileBox = new()
    {
        Text = "Start with a specific profile (Alt+&P)",
        AccessibleName = "Start with a specific profile",
        AutoSize = true,
    };
    private readonly Label profileListLabel = new()
    {
        Text = "Profile to start with (Alt+&L):",
        AutoSize = true,
        AccessibleName = "Profile to start with",
    };
    private readonly ListBox profileList = new()
    {
        IntegralHeight = false,
        Width = 360,
        Height = 140,
        AccessibleName = "Profile to start with",
    };
    private readonly Button closeButton = new()
    {
        Text = "Close",
        AutoSize = true,
        DialogResult = DialogResult.OK,
        AccessibleName = "Close",
    };

    public StartupBehaviourDialog(ProfileStore? profileStore)
    {
        Text = "Startup behaviour";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        KeyPreview = true; // form-level Esc handler
        ClientSize = new Size(540, 360);

        // === Layout ===
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 6, // 0 intro, 1 minimise, 2 auto-start, 3 specific-profile, 4 list (with label), 5 close
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 5; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var intro = new Label
        {
            Text = "These options control what RemSound does when it launches. They persist across sessions and affect every launch (whether started by the user or by Windows on login).",
            AutoSize = true,
            MaximumSize = new Size(500, 0),
            Anchor = AnchorStyles.Left,
        };
        root.Controls.Add(intro, 0, 0);

        // Each checkbox lives on its own row, plus the profile list row when relevant.
        root.Controls.Add(startMinimisedBox, 0, 1);
        root.Controls.Add(startWithUserBox, 0, 2);
        root.Controls.Add(startWithProfileBox, 0, 3);

        // Profile list: label + list stacked, hidden until startWithProfileBox is ticked.
        // FlowLayoutPanel keeps them tidy and the whole sub-block can be toggled visible
        // as a unit.
        var listSubPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(20, 0, 0, 0), // indent slightly so it visually belongs to the checkbox above
        };
        listSubPanel.Controls.Add(profileListLabel);
        listSubPanel.Controls.Add(profileList);
        root.Controls.Add(listSubPanel, 0, 4);

        var closePanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0),
        };
        closePanel.Controls.Add(closeButton);
        root.Controls.Add(closePanel, 0, 5);

        Controls.Add(root);

        // === Tab order ===
        startMinimisedBox.TabIndex = 0;
        startWithUserBox.TabIndex = 1;
        startWithProfileBox.TabIndex = 2;
        profileList.TabIndex = 3;
        closeButton.TabIndex = 4;

        // === Initial state ===
        var cfg = AppConfig.Load();
        startMinimisedBox.Checked = cfg.StartMinimised;
        startWithUserBox.Checked = StartupAutoStart.IsEnabled;
        var hasProfile = !string.IsNullOrWhiteSpace(cfg.StartWithProfileTitle);
        startWithProfileBox.Checked = hasProfile;

        // Populate profile list and select the saved choice if any.
        if (profileStore is not null)
        {
            foreach (var title in profileStore.ListProfileTitles())
            {
                profileList.Items.Add(title);
            }
        }
        if (hasProfile && cfg.StartWithProfileTitle is { } savedTitle)
        {
            var idx = profileList.Items.IndexOf(savedTitle);
            if (idx >= 0) profileList.SelectedIndex = idx;
        }

        UpdateProfileListVisibility();

        // === Wiring ===
        startMinimisedBox.CheckedChanged += (_, _) =>
        {
            var c = AppConfig.Load();
            c.StartMinimised = startMinimisedBox.Checked;
            try { c.Save(); } catch (Exception ex) { ShowSaveWarning("Could not save Start minimised preference: " + ex.Message); }
        };

        var suppressStartWithUserHandler = false;
        startWithUserBox.CheckedChanged += (_, _) =>
        {
            if (suppressStartWithUserHandler) return;
            // Source of truth for the auto-start state is the registry — we don't keep a
            // duplicate in AppConfig. So this just flips the registry entry directly.
            var ok = startWithUserBox.Checked
                ? StartupAutoStart.TryEnable()
                : StartupAutoStart.TryDisable();
            if (!ok)
            {
                MessageBox.Show(this,
                    "RemSound could not change the auto-start setting in the Windows registry. The setting did not change. (This usually means a policy or another security tool is blocking it.)",
                    "Auto-start change failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                // Re-read truth and reflect it WITHOUT re-firing this handler. The suppress flag
                // genuinely gates it; the old detach/re-attach targeted an empty handler that was
                // never in the invocation list, so it did nothing and the corrective set re-fired.
                var actual = StartupAutoStart.IsEnabled;
                if (startWithUserBox.Checked != actual)
                {
                    suppressStartWithUserHandler = true;
                    try { startWithUserBox.Checked = actual; }
                    finally { suppressStartWithUserHandler = false; }
                }
            }
        };

        startWithProfileBox.CheckedChanged += (_, _) =>
        {
            UpdateProfileListVisibility();
            if (startWithProfileBox.Checked)
            {
                if (profileList.Items.Count == 0)
                {
                    MessageBox.Show(this,
                        "You don't have any saved profiles yet. Save a profile first (Profiles and preferences tab → Save profile as), then come back here and pick it.",
                        "No saved profiles",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    // Untick without re-firing.
                    startWithProfileBox.Checked = false;
                    return;
                }
                if (profileList.SelectedIndex < 0) profileList.SelectedIndex = 0;
                CommitProfileSelection();
            }
            else
            {
                ClearProfileSelection();
            }
        };

        profileList.SelectedIndexChanged += (_, _) =>
        {
            if (!startWithProfileBox.Checked) return;
            if (profileList.SelectedIndex < 0) return;
            CommitProfileSelection();
        };
        profileList.DoubleClick += (_, _) =>
        {
            // Same effect as picking a row + closing — convenient for mouse users.
            if (startWithProfileBox.Checked && profileList.SelectedIndex >= 0)
            {
                CommitProfileSelection();
            }
            DialogResult = DialogResult.OK;
            Close();
        };

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
                e.SuppressKeyPress = true;
                e.Handled = true;
            }
        };
        AcceptButton = closeButton;
        CancelButton = closeButton;
        Load += (_, _) => startMinimisedBox.Focus();
    }

    private void UpdateProfileListVisibility()
    {
        var visible = startWithProfileBox.Checked;
        profileListLabel.Visible = visible;
        profileList.Visible = visible;
    }

    private void CommitProfileSelection()
    {
        if (profileList.SelectedItem is not string title || string.IsNullOrWhiteSpace(title)) return;
        var c = AppConfig.Load();
        c.StartWithProfileTitle = title;
        try { c.Save(); } catch (Exception ex) { ShowSaveWarning("Could not save the start-with-profile choice: " + ex.Message); }
    }

    private void ClearProfileSelection()
    {
        var c = AppConfig.Load();
        c.StartWithProfileTitle = null;
        try { c.Save(); } catch (Exception ex) { ShowSaveWarning("Could not save the start-with-profile choice: " + ex.Message); }
    }

    private void ShowSaveWarning(string message)
    {
        MessageBox.Show(this, message, "Startup behaviour", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
