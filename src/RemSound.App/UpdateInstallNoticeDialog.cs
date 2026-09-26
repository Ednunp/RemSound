using System;
using System.Windows.Forms;

namespace RemSound.App;

/// <summary>
/// Brief heads-up shown when the silent-install path is about to swap the app out from
/// under the user. Why this exists: the startup-on-launch update check + the
/// "silently install" tick combine into a UX where the user opens RemSound, expects it
/// to start streaming, and instead the app exits and rebuilds itself a few seconds in.
/// Without a notice the user has no idea why the window vanished — they assume a crash.
///
/// The dialog is deliberately small and short-lived:
///   * Default focus and AcceptButton sit on "Install now" so a screen-reader user
///     pressing Enter to confirm the dialog gets the same outcome as the countdown
///     elapsing.
///   * "Skip this version" returns <see cref="DialogResult.Ignore"/> so the caller can
///     log the skip and decline to install on this launch.
///   * "Postpone" (or Esc) returns <see cref="DialogResult.Cancel"/> — the install will
///     be re-attempted on the next periodic poll or next launch.
///   * A countdown timer auto-triggers Install after a few seconds so the silent path
///     remains effectively silent — the user who walks away from their desk during boot
///     gets the install they asked for, while a user who's at the keyboard has a moment
///     to intervene. The countdown label's text and accessible name update each second, but
///     the label is not a live region, so a screen reader is not told about each tick; the
///     current count is there when the user reads the label.
///
/// NVDA accessibility: the dialog uses standard WinForms <see cref="Button"/> and
/// <see cref="Label"/>; AccessibleName is set explicitly on the heading and countdown
/// so the screen reader reads both as the dialog opens. AcceptButton + CancelButton
/// are wired so Enter / Esc do the obvious thing.
///
/// The dialog is intentionally NOT a TaskDialog — the verification-checkbox
/// "Do not show me this message again" pattern doesn't fit here because the suppression
/// would defeat the entire point of the notice (silent install with NO indication is
/// exactly the problem we're solving). If the user doesn't want the heads-up, they can
/// untick "Silently install updates" in Preferences.
/// </summary>
internal sealed class UpdateInstallNoticeDialog : Form
{
    /// <summary>Seconds to wait before auto-confirming Install. Short enough that a user
    /// who walks away from their desk during boot still gets the update they asked for;
    /// long enough that someone at the keyboard can read the version and pick a button.</summary>
    private const int CountdownSeconds = 8;

    private readonly Label headingLabel;
    private readonly Label countdownLabel;
    private readonly Button installNowButton;
    private readonly Button skipButton;
    private readonly Button postponeButton;
    private readonly System.Windows.Forms.Timer countdownTimer = new();
    private int secondsRemaining = CountdownSeconds;
    internal int SecondsRemainingForTest => secondsRemaining;

    public UpdateInstallNoticeDialog(UpdateInfo info)
    {
        if (info is null) throw new ArgumentNullException(nameof(info));

        Text = "Installing RemSound update";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        KeyPreview = true;
        ClientSize = new Size(500, 220);
        AccessibleName = "Installing RemSound update";

        headingLabel = new Label
        {
            Text = $"RemSound {info.Tag} is ready to install.",
            AccessibleName = $"RemSound {info.Tag} is ready to install",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Padding = new Padding(0, 0, 0, 8),
        };

        // Body — explains what's about to happen so the user isn't surprised by the exit.
        // Wrapped Label rather than TextBox because TextBox steals focus from the default
        // button and breaks the NVDA reading order.
        var bodyLabel = new Label
        {
            Text = "RemSound will install the update and restart automatically. Your session will pick up again once the new version is running.",
            AccessibleName = "RemSound will install the update and restart automatically. Your session will pick up again once the new version is running.",
            AutoSize = false,
            Width = 460,
            Height = 50,
            Padding = new Padding(0, 0, 0, 8),
        };

        countdownLabel = new Label
        {
            Text = FormatCountdownText(secondsRemaining),
            AccessibleName = FormatCountdownText(secondsRemaining),
            AutoSize = true,
            Padding = new Padding(0, 0, 0, 8),
        };

        installNowButton = new Button
        {
            Text = "&Install now",
            AccessibleName = "Install now",
            AutoSize = true,
            DialogResult = DialogResult.OK,
            TabIndex = 0,
        };
        skipButton = new Button
        {
            Text = "&Skip this version",
            AccessibleName = "Skip this version",
            AutoSize = true,
            DialogResult = DialogResult.Ignore,
            TabIndex = 1,
        };
        postponeButton = new Button
        {
            Text = "&Postpone",
            AccessibleName = "Postpone",
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
            TabIndex = 2,
        };

        // Any explicit click stops the countdown — the user has made a choice and we
        // shouldn't elapse-fire underneath them.
        installNowButton.Click += (_, _) => countdownTimer.Stop();
        skipButton.Click += (_, _) => countdownTimer.Stop();
        postponeButton.Click += (_, _) => countdownTimer.Stop();

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 14, 16, 12),
            ColumnCount = 1,
            RowCount = 4,
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(headingLabel, 0, 0);
        body.Controls.Add(bodyLabel, 0, 1);
        body.Controls.Add(countdownLabel, 0, 2);

        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 0, 16, 12),
        };
        // RightToLeft fills back to front, so add the rightmost button (Install now) first.
        buttonRow.Controls.Add(installNowButton);
        buttonRow.Controls.Add(postponeButton);
        buttonRow.Controls.Add(skipButton);

        Controls.Add(body);
        Controls.Add(buttonRow);

        AcceptButton = installNowButton;
        CancelButton = postponeButton;
        ContextHelp.Mark(installNowButton, "dialog.update-install-notice.install-now");
        ContextHelp.Mark(skipButton, "dialog.update-install-notice.skip");
        ContextHelp.Mark(postponeButton, "dialog.update-install-notice.postpone");

        // Esc = postpone (matches CancelButton). Avoids the "I just opened the app, where
        // did the window go" surprise if the user mashes Esc to dismiss whatever popped up.
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                countdownTimer.Stop();
                DialogResult = DialogResult.Cancel;
                Close();
                e.SuppressKeyPress = true;
                e.Handled = true;
            }
        };

        // Tick once per second, decrement, refresh the label, and fire OK when we hit zero.
        countdownTimer.Interval = 1000;
        countdownTimer.Tick += (_, _) =>
        {
            // Paused while context help is open (F1 on one of these buttons): reading what a button does must not run
            // the clock out and install behind the help window (2026-09-25).
            if (ContextHelp.Open is not null) return;
            secondsRemaining--;
            if (secondsRemaining <= 0)
            {
                countdownTimer.Stop();
                DialogResult = DialogResult.OK;
                Close();
                return;
            }
            countdownLabel.Text = FormatCountdownText(secondsRemaining);
            countdownLabel.AccessibleName = countdownLabel.Text;
        };

        // Start the countdown when the dialog appears, not when it's constructed —
        // construction can happen a moment before ShowDialog hands the user focus.
        Shown += (_, _) =>
        {
            countdownTimer.Start();
            // Make sure the default action is Install (matches AcceptButton). Without this
            // tab focus might rest on the first added Control rather than the intended
            // primary action.
            installNowButton.Focus();
        };

        FormClosed += (_, _) => countdownTimer.Stop();
    }

    private static string FormatCountdownText(int seconds)
    {
        return seconds == 1
            ? "Installing in 1 second... Press Skip or Postpone to choose otherwise."
            : $"Installing in {seconds} seconds... Press Skip or Postpone to choose otherwise.";
    }
}
