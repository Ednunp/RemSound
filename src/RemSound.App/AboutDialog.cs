using System.Reflection;

namespace RemSound.App;

/// <summary>
/// About dialog. Shows the running version (read from the assembly) and the release notes for
/// the newest few releases (the notes constant below, cut to <see cref="ShownVersions"/> blocks by
/// <see cref="TrimToLastVersions"/>).
///
/// Layout follows the same NVDA-friendly conventions the rest of the app uses: a small
/// modal dialog with a heading label, a read-only multi-line text box for the notes that
/// the user can tab into and arrow through, and a Close button as the AcceptButton /
/// CancelButton. Escape dismisses.
/// </summary>
internal sealed class AboutDialog : Form
{
    /// <summary>Markdown-ish release notes shown in the About box's scrolling text area.
    /// Bumped per release. Only the newest few releases live here: older notes are in
    /// RELEASE_HISTORY.md at the repository root and on the GitHub Releases page. When a
    /// release is added, move the oldest block there. (The whole history, back to v1.0, was
    /// compiled into the exe until 2026-09-13 and never shown.)</summary>
    private const string ReleaseNotes =
        """
        RemSound v6.2.4

        An offline peer no longer drops the online ones. With a ticked peer switched off or offline, especially over Tailscale, RemSound could disconnect the peers that were still there, saying they had stopped answering.

        RemSound v6.2.3

        Further logging improvements. The log now holds just what happened, and every line starts with INFO, WARN or ERROR and a category, so you can search for the problems. A peer disconnecting says why, and each connection ends with a short summary. A run of audible gaps is one line when it starts and one when it ends. Everything RemSound measures goes into a spreadsheet file beside the log, with a row for every minute, and every second around a problem.

        RemSound v6.2.2

        Logs are now much smaller and easier to read: RemSound writes a short summary once a minute, keeps full detail only around a moment when something goes wrong, and names each file by date. The Logging tab in Preferences can open the current log or the logs folder for you, and the manual now links to the Mac version of RemSound.

        RemSound v6.2.1

        - Fixed some bugs in the web listener.
        - The web listener can't be turned on without a password.
        - Press Enter on any slider to put it back to its default.
        - The metronome warns that it's only accurate when both computers use ASIO.

        RemSound v6.2

        Listen in a web browser. Hear what RemSound sends from a web browser on another computer, with nothing to install there: handy where you can't install RemSound, such as at work. It goes through Tailscale Funnel, behind a password of your own. Press Web listener on the Connectivity tab to switch it on, set the password, copy your web address and turn on Funnel. The page says which computer you're hearing, and has Stop listening, Volume, Pan and a three-band EQ. It can start by itself when it loads, each browser can save its own settings, and up to four people can listen at once.

        Audio cues for each profile. Preferences, Audio cues now has two lists. Global sounds are the six that belong to RemSound itself: profile saved, profile switched, the profile menu opening, update, and context help opening and closing. Profile sounds are everything else, kept with each profile, so a jamming profile can stay quiet while another keeps every sound. Keyboard clicks go with the profile too, and Turn all audio cues off silences the lot. The sounds you have now carry over the first time you open each profile.

        Fixes:
        - Handing over between RemSound and the send-only service on a server is more dependable. The one stepping aside goes quiet before it says goodbye, so it no longer leaves a ghost that keeps a phone out for up to a minute. The goodbye now goes three times, so one lost on the way no longer means 5 to 7 seconds of silence.
        - A phone, an older app or a profile without a password now takes over a place on a server straight away, instead of waiting up to a minute.
        - The log says whether RemSound and the service count as the same person on a server, and when the service couldn't be told to step aside.

        The quicker hand-over for phones, older apps and profiles without a password needs server-v2.17.

        Older releases, back to v1.0, are on the RemSound releases page on GitHub.
        """;

    /// <summary>How many version blocks the About box displays at most. The complete history reached
    /// ~70 KB in one TextBox, and reading a control value that size crashes some screen readers
    /// (reported 2026-08-11) — the box's job is "what's new", not the whole biography.</summary>
    private const int ShownVersions = 5;

    /// <summary>The notes constant, exposed for the self-test that pins the About box's displayed
    /// size (the screen-reader-crash regression guard).</summary>
    internal static string ReleaseNotesForTest => ReleaseNotes;

    /// <summary>The line under the version at the top of the box.</summary>
    internal const string CreatedBy = "Created by Ed Pourtahmasbi";

    internal Label? HeadingForTest { get; private set; }

    /// <summary>Pure, testable: cut <paramref name="notes"/> after its first <paramref name="maxVersions"/>
    /// version blocks (lines starting "RemSound v"), appending a plain pointer to the full history.
    /// The pointer is appended only when it trims: notes with <paramref name="maxVersions"/> blocks or
    /// fewer pass through unchanged, ending with whatever closing line they carry themselves.</summary>
    internal static string TrimToLastVersions(string notes, int maxVersions)
    {
        var count = 0;
        var lines = notes.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].TrimEnd('\r').StartsWith("RemSound v", StringComparison.Ordinal)) continue;
            if (++count <= maxVersions) continue;
            return string.Join("\n", lines, 0, i).TrimEnd()
                + "\r\n\r\nThat's the newest "
                + maxVersions
                + " releases. Notes for every version back to the beginning are on the RemSound releases page on GitHub.";
        }
        return notes;
    }

    public AboutDialog()
    {
        Text = "About RemSound";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;
        ClientSize = Dpi.Px(560, 420);

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

        // Who made it, under the version (Ed, 2026-09-28), as in the readme, the licence and the program's file details.
        var headingLabel = new Label
        {
            Text = $"RemSound version {version}\r\n{CreatedBy}",
            AutoSize = true,
            // The house heading (UI audit 2026-09-28, finding 13): Semibold at the type ramp's subtitle size, not 11 pt Bold.
            Font = Theme.HeadingFont(),
            AccessibleName = $"RemSound version {version}, {char.ToLowerInvariant(CreatedBy[0])}{CreatedBy[1..]}",
        };
        HeadingForTest = headingLabel;

        var notesBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            TabStop = true,
            Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            Text = TrimToLastVersions(ReleaseNotes, ShownVersions),
            AccessibleName = "Release notes (tab into and arrow to read)",
        };

        var closeButton = new Button
        {
            Text = "&Close",
            AutoSize = true,
            DialogResult = DialogResult.OK,
            TabIndex = 1,
        };
        closeButton.Click += (_, _) => Close();
        notesBox.TabIndex = 0;
        // Opens at the top with nothing highlighted (finding 14), rather than as one solid block of selection.
        FormLayoutRows.OpenWithNothingSelected(notesBox);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = Dpi.Pad(12),
            ColumnCount = 1,
            RowCount = 3,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
        };
        buttons.Controls.Add(closeButton);

        root.Controls.Add(headingLabel, 0, 0);
        root.Controls.Add(notesBox, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);

        AcceptButton = closeButton;
        CancelButton = closeButton;
        ContextHelp.Mark(notesBox, "dialog.about.release-notes");
        ContextHelp.Mark(closeButton, "dialog.about.close");

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                Close();
                e.SuppressKeyPress = true;
                e.Handled = true;
            }
        };
    }
}
