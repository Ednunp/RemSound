namespace RemSound.App;

/// <summary>
/// Where the DAW plugin goes (Ed, 2026-09-25: "give people a choice of where to install it. install in default location x,
/// or another button to browse for a location"). The standard place is the per-user VST3 folder every current music
/// program scans; some, Audacity for one, only look in the shared folder under Program Files, which is what the other
/// button is for. The folder chosen is remembered, so updates go there too.
/// </summary>
internal sealed class PluginInstallPlaceDialog : Form
{
    private readonly string standard;
    private readonly string current;

    internal readonly TextBox MessageBoxForTest;
    internal readonly Button StandardButton;
    internal readonly Button ChooseButton;
    internal readonly Button CancelChoiceButton;

    /// <summary>The folder the plugin is to go in - its own RemSound folder, in full - once the dialog closes with OK.</summary>
    public string? ChosenTarget { get; private set; }

    internal PluginInstallPlaceDialog(string standard, string current, bool installed)
    {
        this.standard = standard;
        this.current = current;
        Text = "Install the DAW plugin";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var words = "Where should RemSound put the DAW plugin?\r\n\r\n"
                  + $"The standard place is {standard}. Most music software looks there, and it needs no administrator permission.";
        if (installed)
            words += $"\r\n\r\nIt is installed now in {current}. Installing it in a different folder moves it: RemSound removes the copy it put there.";
        words += "\r\n\r\nWherever it goes, RemSound keeps it up to date there.";

        // A read-only box rather than a label, as in the app's other messages: a screen reader can arrow through it.
        MessageBoxForTest = new TextBox
        {
            Text = words,
            ReadOnly = true,
            Multiline = true,
            Width = 520,
            Height = 130,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Control,
            TabStop = true,
            AccessibleName = "Where to install the plugin",
        };
        StandardButton = new Button
        {
            Text = "Install in the &standard place (Alt+S)",
            AccessibleName = "Install in the standard place",
            AccessibleDescription = standard,
            AutoSize = true,
        };
        ChooseButton = new Button
        {
            Text = "&Choose another folder... (Alt+C)",
            AccessibleName = "Choose another folder...",
            AutoSize = true,
        };
        CancelChoiceButton = new Button { Text = "C&ancel", DialogResult = DialogResult.Cancel, AutoSize = true, AccessibleName = "Cancel" };

        StandardButton.Click += (_, _) =>
        {
            ChosenTarget = this.standard;
            DialogResult = DialogResult.OK;
        };
        ChooseButton.Click += (_, _) =>
        {
            if (PickFolder() is not { } picked) return;
            ChosenTarget = PluginInstaller.TargetFor(picked);
            DialogResult = DialogResult.OK;
        };

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
        buttons.Controls.Add(StandardButton);
        buttons.Controls.Add(ChooseButton);
        buttons.Controls.Add(CancelChoiceButton);
        var layout = new TableLayoutPanel { ColumnCount = 1, RowCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
        layout.Controls.Add(MessageBoxForTest, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        MessageBoxForTest.TabIndex = 0;
        buttons.TabIndex = 1;
        Controls.Add(layout);
        AcceptButton = StandardButton;
        CancelButton = CancelChoiceButton;

        ContextHelp.Mark(MessageBoxForTest, "dialog.plugin-place.message");
        ContextHelp.Mark(StandardButton, "dialog.plugin-place.standard");
        ContextHelp.Mark(ChooseButton, "dialog.plugin-place.choose");
        ContextHelp.Mark(CancelChoiceButton, "dialog.plugin-place.cancel");

        // The keyboard starts on the standard place, the choice most people want; the message is one Shift+Tab away.
        Shown += (_, _) => StandardButton.Focus();
    }

    /// <summary>Windows' own folder picker, starting where the plugin is now (or the standard place). Null when cancelled.</summary>
    private string? PickFolder()
    {
        var start = Path.GetDirectoryName(current.TrimEnd('\\', '/'));
        if (start is null || !Directory.Exists(start)) start = Path.GetDirectoryName(standard.TrimEnd('\\', '/'));
        using var picker = new FolderBrowserDialog
        {
            Description = "Choose the folder for the RemSound DAW plugin",
            UseDescriptionForTitle = true,
            SelectedPath = start is not null && Directory.Exists(start) ? start : "",
            ShowNewFolderButton = true,
        };
        if (picker.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(picker.SelectedPath)) return null;
        return picker.SelectedPath;
    }

    /// <summary>Build it without showing it, so the dialog audits can reach it headlessly.</summary>
    internal static PluginInstallPlaceDialog Build() =>
        new(PluginInstaller.StandardDirectory, @"C:\Program Files\Common Files\VST3\RemSound", installed: true);

    /// <summary>Ask where the plugin should go. Null when cancelled.</summary>
    public static string? Ask(IWin32Window? owner)
    {
        using var dialog = new PluginInstallPlaceDialog(PluginInstaller.StandardDirectory, PluginInstaller.InstallDirectory, PluginInstaller.IsInstalled());
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.ChosenTarget : null;
    }
}

/// <summary>
/// Ed's message after the plugin is installed somewhere other than the standard place (2026-09-25), in his words: updates
/// go to that folder, but only that folder.
/// </summary>
internal sealed class PluginFolderNoticeDialog : Form
{
    internal readonly TextBox NoticeBox;
    internal readonly AccessibleCheckBox DontShowAgainBox = new()
    {
        Text = "&Do not show me this message again (Alt+D)",
        AccessibleName = "Do not show me this message again",
        AutoSize = true,
    };
    internal readonly Button OkButton;

    internal static string Words(string folder) =>
        $"Please note: RemSound will now put any plugin updates in {folder}. However, if the plugin is manually moved or copied "
      + "to a new location, the new update will not automatically be installed and will need to be copied manually.";

    internal PluginFolderNoticeDialog(string folder)
    {
        Text = "Plugin updates";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        NoticeBox = new TextBox
        {
            Text = Words(folder),
            ReadOnly = true,
            Multiline = true,
            Width = 460,
            Height = 80,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Control,
            TabStop = true,
            AccessibleName = "Plugin updates notice",
        };
        OkButton = new Button { Text = "&OK", DialogResult = DialogResult.OK, AutoSize = true, AccessibleName = "OK" };
        var layout = new TableLayoutPanel { ColumnCount = 1, RowCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
        layout.Controls.Add(NoticeBox, 0, 0);
        layout.Controls.Add(DontShowAgainBox, 0, 1);
        layout.Controls.Add(OkButton, 0, 2);
        NoticeBox.TabIndex = 0;
        DontShowAgainBox.TabIndex = 1;
        OkButton.TabIndex = 2;
        Controls.Add(layout);
        AcceptButton = OkButton;
        CancelButton = OkButton;
        ContextHelp.Mark(NoticeBox, "dialog.plugin-folder-notice.notice");
        ContextHelp.Mark(DontShowAgainBox, "dialog.plugin-folder-notice.dont-show-again");
        ContextHelp.Mark(OkButton, "dialog.plugin-folder-notice.ok");
    }

    /// <summary>Build it without showing it, so the dialog audits can reach it headlessly.</summary>
    internal static PluginFolderNoticeDialog Build() => new(@"C:\Program Files\Common Files\VST3\RemSound");

    /// <summary>Shows the message. True when "Do not show me this message again" was ticked. Off by default.</summary>
    public static bool ShowNotice(IWin32Window? owner, string folder)
    {
        using var dialog = new PluginFolderNoticeDialog(folder);
        dialog.ShowDialog(owner);
        return dialog.DontShowAgainBox.Checked;
    }
}
