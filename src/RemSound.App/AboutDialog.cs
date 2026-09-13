using System.Reflection;

namespace RemSound.App;

/// <summary>
/// About dialog. Shows the running version, a short blurb about RemSound and the latest
/// release notes (built in below — bumped per release alongside the project's
/// <see cref="System.Version"/> property).
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
        RemSound v6.0

        RemSound now works inside your music software.

        There is a plugin. Put it on a track and one person goes on that track: a track you are sending goes out to your peers, and a peer you are receiving arrives on a track of their own, where you can record them, shape them and mix them like anything else. Add it twice for two people and each gets their own track.

        You install it from the new DAW plugin menu, which puts it in your own plugin folder — no administrator password, nothing outside your account touched. Keep RemSound open while you work; it holds the connection and the plugin asks it for audio, so your password, peers and audio settings stay where you already set them.

        When a track takes somebody, they stop coming out of RemSound's own output, so you never hear anyone twice. Let the track go and they come straight back. A peer's volume, pan and EQ come through to the track as well; untick "Apply pan and EQ to plugin audio" in the DAW plugin menu if you would rather have the raw signal.

        The plugin's window is built from ordinary Windows controls, so a screen reader reads it the way it reads RemSound itself. If your music software makes that window awkward to reach, the same three choices are also plugin parameters, which every DAW lists.

        One limit worth knowing: your music software cannot allow for the network delay, so a track recorded through the plugin sits a little late and needs nudging back.

        If you do not use a DAW, nothing changes. RemSound listens for plugins on your own machine only, and "Let plugins connect to RemSound" in the DAW plugin menu turns even that off. Section 25 of the manual walks through all of it.

        The rest of this release is a careful pass over sending, receiving and recording. Turning off Send my audio now stops the ASIO side too. With both kinds of output ticked, recordings keep every block, and a WAV recording stops cleanly at the 4 GB limit. The Total latency box reads the outputs you are actually using and no longer counts the jitter buffer twice. An output that Windows quietly stops comes back on its own within a few seconds, including after sleep, and waking the computer restarts the audio with the buffer sizes you had before. Several capture sources sent at once now stay in time with each other. Every Alt key has been checked against what the screen says, and with logging on, what you change is written to the log.

        RemSound v5.9

        The jitter buffer control now works properly while you're listening.

        On most setups, moving the jitter buffer control while sound was playing did nothing at all — neither up nor down. It only took effect if you set it before connecting. Two things were wrong: the control was writing its value where the audio never looked, and even when it landed, a change crept in so slowly that a big move took over two minutes to arrive. Both are fixed. Move it now and the delay follows within a few seconds, with no gap or click — raising it stretches the sound very slightly for a moment while the extra cushion builds up, which is the change happening. Automatic tuning of the jitter buffer was affected by the same fault and now works too.

        Setups with separate WASAPI and ASIO controls were the one case that already worked, and they keep their two independent settings exactly as before.

        RemSound v5.8

        A repair for the lock-screen service's settings folder.

        Some machines ended up with the service's settings folder locked so tightly that nothing could use it — saving the service profile failed with "access denied", the service's log files wouldn't open, and on some machines the service itself couldn't start. This release fixes the cause and heals affected machines automatically: the service applies the fix on its own when it updates, RemSound checks the folder every time it starts and offers a one-click repair if anything is wrong, and there's a "Repair service folder access" item in the Service menu you can run any time.

        The service's log files are also readable again from every account on the machine, so you can always open them in Notepad if you're curious or need to send one in. And if a service action ever fails, the message now says what actually went wrong instead of just showing a bare error code.

        This About box also went on a diet: it now shows the newest five releases instead of the whole history, which had grown large enough to upset some screen readers. The full history is on the RemSound releases page on GitHub.

        RemSound v5.7

        Stronger security, and it works with every version again.

        This release puts the password handling back the way it was, so RemSound talks to older versions and the iPhone app again. You still set a password — your audio is always encrypted — we just suggest a strong one now instead of requiring it. Nothing changes for you if you already use a good password.

        Signed updates. Every release is now digitally signed, and the updater refuses anything that isn't genuinely from us — so even if the download page were ever tampered with, a fake update couldn't install itself on your machine.

        Remote volume, now password-protected. The remote volume and mute controls are locked to your password, so only someone who shares it can use them.

        Set the machine's volume when the service starts. In the service's Additional options you can have an unattended machine unmute itself and set its Windows volume to a level you choose — on the first start after each boot, or on every service start.

        Updates on your schedule. In Preferences you can restrict automatic updates to a time range — say 1am to 6am — so an update never closes RemSound and interrupts you. Found outside the range, it quietly waits and installs the moment the range opens.

        Plus: the app releases its high-priority and keep-awake settings when you're not actually streaming (kinder to laptops), diagnostic logs cap their own size on long sessions, and a raft of behind-the-scenes hardening.

        RemSound v5.5

        A close-hang fix, and keyboard shortcuts everywhere.

        Fixed a hang when UPnP is on. With the "open my router's port automatically" option turned on, closing RemSound could hang — on the way out it asks the router to close the port again, and a slow or fussy router left the window stuck waiting. Closing now never waits on the router: it tidies up in the background and shuts straight away.

        Keyboard shortcuts on every dialog. Every button in every pop-up now has an Alt-key shortcut — including the "RemSound is already running" dialog, which previously had none. So you can always choose an option from the keyboard instead of tabbing to it.

        Older releases, back to v1.0, are on the RemSound releases page on GitHub.
        """;

    /// <summary>How many version blocks the About box displays at most. The complete history reached
    /// ~70 KB in one TextBox, and reading a control value that size crashes some screen readers
    /// (reported 2026-08-11) — the box's job is "what's new", not the whole biography.</summary>
    private const int ShownVersions = 5;

    /// <summary>The notes constant, exposed for the self-test that pins the About box's displayed
    /// size (the screen-reader-crash regression guard).</summary>
    internal static string ReleaseNotesForTest => ReleaseNotes;

    /// <summary>Pure, testable: cut <paramref name="notes"/> after its first <paramref name="maxVersions"/>
    /// version blocks (lines starting "RemSound v"), appending a plain pointer to the full history.
    /// Notes with fewer blocks pass through unchanged.</summary>
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
        ClientSize = new Size(560, 420);

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

        var headingLabel = new Label
        {
            Text = $"RemSound version {version}",
            AutoSize = true,
            Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 11f, FontStyle.Bold),
            AccessibleName = $"RemSound version {version}",
        };

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

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
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
