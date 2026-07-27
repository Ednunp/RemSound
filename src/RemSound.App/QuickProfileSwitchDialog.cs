using System.Runtime.InteropServices;

namespace RemSound.App;

/// <summary>
/// Small, NVDA-friendly pop-up that lists every profile and lets the user switch to one from
/// anywhere in Windows — it's opened by the global "Quick profile switch" hotkey. The currently
/// loaded profile is marked "(current)" and pre-selected, so the screen reader announces where you
/// are the moment it opens. Arrow up/down to browse, Enter or click to switch, Escape to close
/// without changing anything. The window forces itself to the foreground so the screen reader lands
/// in it even when RemSound is sitting in the tray or another app has focus.
/// </summary>
internal sealed class QuickProfileSwitchDialog
{
    public sealed record ProfileEntry(string Title, string Path, bool IsCurrent);

    /// <summary>Shows the popup. Returns the chosen profile's path, or null if the user cancelled
    /// (Escape / Close) or there were no profiles to show.</summary>
    public static string? Show(IReadOnlyList<ProfileEntry> profiles)
    {
        if (profiles.Count == 0) return null;
        var (dialog, list, chosenPath) = Build(profiles);
        using (dialog)
        {
            dialog.Shown += (_, _) => list.Focus();
            // Via ForegroundDialog: this is opened by a GLOBAL hotkey, so RemSound is usually
            // minimised in the tray when it fires — the old ad-hoc SetForegroundWindow only flashed
            // the taskbar there (Windows blocks foreground from a background process), leaving a blind
            // user Alt-Tabbing for it. ForegroundDialog's 1×1-owner + foreground-lock dance actually
            // surfaces it with focus wherever RemSound is sitting (Ed, 2026-07-27 dialog-focus sweep).
            return ForegroundDialog.Show(owner => dialog.ShowDialog(owner)) == DialogResult.OK ? chosenPath() : null;
        }
    }

    /// <summary>Audit seam: the real dialog, built with a placeholder profile, never shown. Inline-built
    /// dialogs used to be invisible to the accessibility audit — how missing mnemonics slipped through.</summary>
    internal static Form BuildForAudit() => Build(new[] { new ProfileEntry("Audit profile", "audit", true) }).Dialog;

    private static (Form Dialog, ListBox List, Func<string?> ChosenPath) Build(IReadOnlyList<ProfileEntry> profiles)
    {
        var dialog = new Form
        {
            Text = "Quick profile switch",
            AccessibleName = "Quick profile switch",
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            TopMost = true,
            KeyPreview = true,
            ClientSize = new Size(440, 340),
        };

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            ColumnCount = 1,
            RowCount = 3, // 0 intro, 1 list, 2 close button
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var intro = new Label
        {
            Text = "Arrow up and down to a profile, then press Enter to switch to it. Escape closes without changing.",
            AutoSize = true,
            MaximumSize = new Size(410, 0),
            Anchor = AnchorStyles.Left,
        };
        root.Controls.Add(intro, 0, 0);

        var list = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            // NVDA reads this on first focus, then each item's text as the selection moves.
            AccessibleName = "Profiles",
            TabIndex = 0,
        };
        var currentIndex = 0;
        for (var i = 0; i < profiles.Count; i++)
        {
            var p = profiles[i];
            list.Items.Add(p.IsCurrent ? $"{p.Title} (current)" : p.Title);
            if (p.IsCurrent) currentIndex = i;
        }
        list.SelectedIndex = currentIndex;
        root.Controls.Add(list, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0),
        };
        var closeButton = new Button { Text = "&Close", AutoSize = true, DialogResult = DialogResult.Cancel, TabIndex = 1 };
        buttons.Controls.Add(closeButton);
        root.Controls.Add(buttons, 0, 2);

        dialog.Controls.Add(root);
        dialog.CancelButton = closeButton;

        string? chosenPath = null;
        void Commit()
        {
            var idx = list.SelectedIndex;
            if (idx < 0 || idx >= profiles.Count) return;
            chosenPath = profiles[idx].Path;
            dialog.DialogResult = DialogResult.OK;
            dialog.Close();
        }

        // Enter or a click/double-click on a row switches to it (Ed: "clicking on a profile
        // switches it"). MouseClick fires after the click has moved the selection, so the clicked
        // row is the selected one.
        list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                Commit();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };
        list.DoubleClick += (_, _) => Commit();
        list.MouseClick += (_, _) => Commit();

        dialog.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                dialog.DialogResult = DialogResult.Cancel;
                dialog.Close();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };

        return (dialog, list, () => chosenPath);
    }
}
