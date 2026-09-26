namespace RemSound.App;

/// <summary>
/// The notice shown when someone connects to a relay server, in Ed's own words (2026-09-20). A relay only ever shows
/// you the people on your own password, which is the one thing about it that surprises people, so it is said once,
/// plainly, with a way to stop saying it.
/// </summary>
internal sealed class RelayNoticeDialog : Form
{
    private readonly AccessibleCheckBox dontShowAgainBox = new()
    {
        Text = "&Don't show this message again",
        AccessibleName = "Don't show this message again",
        AutoSize = true,
    };

    /// <summary>Build it without showing it, so the dialog audits can reach it headlessly.</summary>
    internal static RelayNoticeDialog Build() => new();

    private RelayNoticeDialog()
    {
        Text = "Connecting to a server";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        // A read-only multiline box rather than a label: a screen reader can arrow through it, and the text is
        // selectable, the same shape as the app's other readouts.
        var message = new TextBox
        {
            Text = "Please note: when you connect to a server, you will only be able to see and connect to users "
                 + "on the server who have the same password as the one set in your profile.",
            ReadOnly = true,
            Multiline = true,
            Width = 420,
            Height = 60,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Control,
            TabStop = true,
            AccessibleName = "Server notice",
        };

        var ok = new Button { Text = "&OK", DialogResult = DialogResult.OK, AutoSize = true, AccessibleName = "OK" };
        var layout = new TableLayoutPanel { ColumnCount = 1, RowCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
        layout.Controls.Add(message, 0, 0);
        layout.Controls.Add(dontShowAgainBox, 0, 1);
        layout.Controls.Add(ok, 0, 2);
        message.TabIndex = 0;
        dontShowAgainBox.TabIndex = 1;
        ok.TabIndex = 2;
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = ok;
        ContextHelp.Mark(message, "dialog.relay-server-notice.notice");
        ContextHelp.Mark(dontShowAgainBox, "dialog.relay-server-notice.dont-show-again");
        ContextHelp.Mark(ok, "dialog.relay-server-notice.ok");
    }

    /// <summary>Shows the notice. Returns true when the user asked not to see it again. Off by default, like every
    /// other "remember this" in RemSound.</summary>
    public static bool ShowNotice(IWin32Window? owner)
    {
        using var dialog = new RelayNoticeDialog();
        // Owned by the helper ForegroundDialog brings to the front: owned by a main window sitting in the tray, it opened
        // behind whatever was on screen (2026-09-25 sweep).
        ForegroundDialog.Show(front => dialog.ShowDialog(front));
        return dialog.dontShowAgainBox.Checked;
    }
}
