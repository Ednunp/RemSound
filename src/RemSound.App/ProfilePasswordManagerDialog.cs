using System.Text.Json;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// The "password manager" view from Options: every saved profile listed with its password in a
/// plain, editable box. Type a new password against any profile and OK writes them all back to
/// disk. Plain (readable) boxes on purpose — a masked field reads as bullets to a screen reader,
/// which is useless; the security model already accepts the password is recoverable from the
/// profile file. Only the password field of each changed profile is rewritten; nothing else in
/// the profile is touched. Returns true if any password was changed. 2026-05-31.
/// </summary>
internal static class ProfilePasswordManagerDialog
{
    public static bool Show(IWin32Window owner, ProfileStore store)
    {
        var titles = store.ListProfileTitles();

        using var dialog = new Form
        {
            Text = "Profile passwords",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.Sizable,
            MinimizeBox = false,
            MaximizeBox = true,
            ShowInTaskbar = false,
            ClientSize = new Size(520, 420),
            MinimumSize = new Size(420, 240),
            AccessibleName = "Profile passwords",
        };

        var intro = new Label
        {
            Text = titles.Count == 0
                ? "You don't have any saved profiles yet. Create one with File → Save as, and it will ask you for a password."
                : "Each profile has its own password. You and the person you connect to must use the same password. Edit any box and press OK to save.",
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 48,
            Padding = new Padding(12, 10, 12, 4),
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            ColumnCount = 2,
            RowCount = titles.Count,
            Padding = new Padding(12, 0, 12, 8),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var rows = new List<(string Title, string Original, TextBox Box)>();
        foreach (var title in titles)
        {
            string current;
            try { current = RemSoundCrypto.Deobfuscate(store.Load(title)?.Password); }
            catch { current = ""; }

            var label = new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 10, 6) };
            var box = new TextBox { Text = current, Anchor = AnchorStyles.Left | AnchorStyles.Right, AccessibleName = $"Password for profile {title}", Tag = KeyClickService.PasswordFieldTag };
            grid.Controls.Add(label);
            grid.Controls.Add(box);
            rows.Add((title, current, box));
        }

        var okButton = new Button { Text = "&OK", AutoSize = true, DialogResult = DialogResult.OK };
        var cancelButton = new Button { Text = "&Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
        buttons.Controls.Add(okButton);
        buttons.Controls.Add(cancelButton);

        dialog.Controls.Add(grid);
        dialog.Controls.Add(buttons);
        dialog.Controls.Add(intro);
        dialog.AcceptButton = okButton;
        dialog.CancelButton = cancelButton;

        if (dialog.ShowDialog(owner) != DialogResult.OK) return false;

        var changedAny = false;
        foreach (var (title, original, box) in rows)
        {
            var now = box.Text.Trim();
            if (now == original) continue;
            try
            {
                var path = store.PathFor(title);
                if (!File.Exists(path)) continue;
                var profile = JsonSerializer.Deserialize<Profile>(File.ReadAllText(path));
                if (profile is null) continue;
                profile.Password = RemSoundCrypto.Obfuscate(now);
                File.WriteAllText(path, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
                changedAny = true;
            }
            catch
            {
                // Skip a profile we couldn't rewrite; the others still save.
            }
        }
        return changedAny;
    }
}
