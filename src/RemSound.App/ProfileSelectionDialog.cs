using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Modal dialog shown at app startup to pick which profile to load. Listbox of saved
/// profile titles plus a synthetic "New profile" entry for a fresh unsaved-defaults
/// session. Enter or OK selects; Esc does nothing (deliberately disabled — picking is
/// required); Alt+F4 closes the dialog and exits the app; Del on a profile prompts to
/// delete it with a yes/no confirm. The user can also browse to a custom profiles
/// folder, which persists in <c>remsound.config.json</c> next to the exe.
///
/// On OK, exposes:
///   * <see cref="SelectedTitle"/> — the chosen title, or null for blank template.
///   * <see cref="SelectedProfile"/> — the loaded <see cref="Profile"/>, or null for blank.
///   * <see cref="Store"/> — the (possibly-rebuilt) profile store. If the user clicked
///     Browse and changed the folder, this points at the new folder; the caller should
///     use this reference rather than the one it passed in.
/// </summary>
internal sealed class ProfileSelectionDialog : Form
{
    // The synthetic top-of-list entry that starts a fresh, unsaved session. A distinct marker TYPE
    // (not a magic string) so it can never be confused with a real profile a user happens to name
    // "New profile" — identity is by type; the display text is its ToString().
    private const string NewProfileLabel = "New profile";
    private static readonly NewProfileMarker NewProfileEntry = new();
    private sealed record NewProfileMarker
    {
        public override string ToString() => NewProfileLabel;
    }

    private ProfileStore store;
    private readonly ListBox listBox;
    private readonly Label folderLabel;

    public string? SelectedTitle { get; private set; }
    public Profile? SelectedProfile { get; private set; }
    /// <summary>Current profile store. If the user clicked Browse during the dialog,
    /// this is rebuilt to point at the new folder; otherwise it's the same instance the
    /// caller passed in.</summary>
    public ProfileStore Store => store;

    public ProfileSelectionDialog(ProfileStore store)
    {
        this.store = store;

        Text = "RemSound — pick a profile";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(480, 420);
        // Esc is deliberately ignored (no CancelButton). Alt+F4 routes through the
        // window manager to FormClosing → DialogResult.Cancel, which the caller treats
        // as "user wants to quit".
        KeyPreview = true;

        listBox = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            AccessibleName = "Profiles",
        };
        listBox.KeyDown += OnListKeyDown;
        listBox.DoubleClick += (_, _) => Accept();

        var instructions = new Label
        {
            Text = "Select a profile and press Enter, or pick \"" + NewProfileLabel + "\" to start a new one.",
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 36,
            Padding = new Padding(8, 8, 8, 4),
        };

        // Folder status + Browse button row at the bottom. Browse opens a folder picker;
        // on OK we save AppConfig, rebuild the store, and refresh the list. Status label
        // shows the active folder so the user can verify where their profiles are coming
        // from. NVDA reads the label as a sibling of the listbox.
        folderLabel = new Label
        {
            Text = "Profiles folder: " + store.BaseDirectory,
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 28,
            Padding = new Padding(8, 4, 8, 4),
            AccessibleName = "Profiles folder: " + store.BaseDirectory,
        };

        var okButton = new Button { Text = "&OK", AutoSize = true, DialogResult = DialogResult.None };
        okButton.Click += (_, _) => Accept();
        var deleteButton = new Button { Text = "&Delete", AutoSize = true };
        deleteButton.Click += (_, _) => DeleteSelected();
        var browseButton = new Button { Text = "&Browse for profiles folder…", AutoSize = true };
        browseButton.Click += (_, _) => BrowseForFolder();
        var resetFolderButton = new Button { Text = "&Reset to default folder", AutoSize = true };
        resetFolderButton.Click += (_, _) => ResetToDefaultFolder();
        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.LeftToRight,
            Height = 80,
            AutoSize = false,
            Padding = new Padding(8),
            WrapContents = true,
        };
        buttonRow.Controls.Add(okButton);
        buttonRow.Controls.Add(deleteButton);
        buttonRow.Controls.Add(browseButton);
        buttonRow.Controls.Add(resetFolderButton);

        Controls.Add(listBox);
        Controls.Add(buttonRow);
        Controls.Add(folderLabel);
        Controls.Add(instructions);

        AcceptButton = okButton; // makes Enter work in the form context too

        Load += (_, _) =>
        {
            RefreshList();
            listBox.Focus();
        };
    }

    private void RefreshList()
    {
        // Remember the previously-selected title (NOT the displayed text — that includes any
        // " (read-only)" suffix, which would prevent matching across a refresh).
        var prevSelectedTitle = GetSelectedTitle();
        listBox.BeginUpdate();
        listBox.Items.Clear();
        listBox.Items.Add(NewProfileEntry);
        foreach (var t in store.ListProfileTitles())
        {
            // Wrap each title in a ProfileListItem so the displayed text can carry a
            // "(read-only)" suffix while the underlying title stays clean for store lookups.
            // ListBox.ToString() is what NVDA reads and what's shown visually; the inner
            // .Title is what code paths key off. Locked profiles get the suffix so users
            // know what they're picking before they hit Enter. 2026-05-22.
            listBox.Items.Add(new ProfileListItem(t, store.IsProfileReadOnly(t)));
        }
        // Try to restore selection by title; fall back to first item.
        var newIdx = 0;
        if (!string.IsNullOrEmpty(prevSelectedTitle))
        {
            for (var i = 0; i < listBox.Items.Count; i++)
            {
                if (string.Equals(TitleOfItem(listBox.Items[i]), prevSelectedTitle, StringComparison.Ordinal))
                {
                    newIdx = i;
                    break;
                }
            }
        }
        listBox.SelectedIndex = Math.Min(newIdx, listBox.Items.Count - 1);
        listBox.EndUpdate();
        // Keep the folder label in sync so it always reflects what the listbox is reading.
        folderLabel.Text = "Profiles folder: " + store.BaseDirectory;
        folderLabel.AccessibleName = folderLabel.Text;
    }

    /// <summary>Returns the currently-selected profile title, unwrapping the ProfileListItem if
    /// needed. Returns null for the synthetic "New profile" entry and when nothing is selected
    /// (callers distinguish those by checking the item type directly).</summary>
    private string? GetSelectedTitle()
    {
        var item = listBox.SelectedItem;
        return TitleOfItem(item);
    }

    private static string? TitleOfItem(object? item) => item switch
    {
        null => null,
        NewProfileMarker => null, // the synthetic "New profile" entry has no saved title
        string s => s,
        ProfileListItem p => p.Title,
        _ => item.ToString(),
    };

    /// <summary>Listbox wrapper for a saved profile. The displayed text (which NVDA reads
    /// and which appears visually) decorates the title with "(read-only)" when the profile
    /// JSON has the lock flag set; the Title property stays clean so store lookups by title
    /// keep working. 2026-05-22.</summary>
    private sealed record ProfileListItem(string Title, bool ReadOnly)
    {
        public override string ToString() => ReadOnly ? $"{Title} (read-only)" : Title;
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            Accept();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Delete)
        {
            DeleteSelected();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void Accept()
    {
        var item = listBox.SelectedItem;
        if (item is null) return;
        if (item is NewProfileMarker)
        {
            // New profile = a fresh unsaved session; a null title/profile signals that to the caller.
            SelectedTitle = null;
            SelectedProfile = null;
        }
        else
        {
            var selected = TitleOfItem(item);
            if (string.IsNullOrEmpty(selected)) return;
            SelectedTitle = selected;
            SelectedProfile = store.Load(selected);
            if (SelectedProfile is null)
            {
                MessageBox.Show(this,
                    $"Could not read profile \"{selected}\". Starting a new profile instead.",
                    "RemSound", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                SelectedTitle = null;
            }
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    private void DeleteSelected()
    {
        var item = listBox.SelectedItem;
        if (item is null or NewProfileMarker) return; // can't delete the "New profile" entry
        var selected = TitleOfItem(item);
        if (string.IsNullOrEmpty(selected)) return;
        var result = MessageBox.Show(this,
            $"Delete profile \"{selected}\"? This cannot be undone.",
            "Confirm delete", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (result != DialogResult.Yes) return;
        if (!store.Delete(selected))
        {
            MessageBox.Show(this,
                $"Could not delete \"{selected}\".",
                "RemSound", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        RefreshList();
    }

    /// <summary>Open a folder picker, persist the choice to AppConfig, and rebuild the
    /// profile store + list against the new folder. No-op on cancel. If the new folder
    /// has no profiles yet, the listbox simply shows just the blank-template entry; the
    /// user can save into the new folder later.</summary>
    private void BrowseForFolder()
    {
        using var picker = new FolderBrowserDialog
        {
            Description = "Choose a folder for RemSound profiles",
            UseDescriptionForTitle = true,
            SelectedPath = store.BaseDirectory,
            ShowNewFolderButton = true,
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        ApplyFolder(picker.SelectedPath);
    }

    private void ResetToDefaultFolder()
    {
        // Clearing the AppConfig field and reloading swings the store back to the legacy
        // default (per-machine subfolder under the exe). Cheap and reversible — user can
        // Browse to a custom folder again any time.
        var cfg = AppConfig.Load();
        cfg.ProfilesDirectory = null;
        try { cfg.Save(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save app config: {ex.Message}",
                "RemSound", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        store = cfg.CreateStore();
        RefreshList();
    }

    private void ApplyFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;
        var cfg = AppConfig.Load();
        cfg.ProfilesDirectory = folderPath;
        try { cfg.Save(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save app config: {ex.Message}",
                "RemSound", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        store = cfg.CreateStore();
        RefreshList();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Eat plain Esc — selection is required, no implicit cancel.
        if (keyData == Keys.Escape) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
