using RemSound.Core;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>MAIN WINDOW ALT KEYS (2026-09-29). Two changes built side by side each took Alt+Z - the Metronome menu and the
    /// ASIO buffer size - and nothing noticed: a menu's Alt key and a control's are checked in different places. On the main
    /// window a top-level menu's key works from every tab, so no control on any tab may use it. The window is built, every
    /// top-level menu's key read from its text, and every control on the window (every tab, shown or not) checked against them.</summary>
    private static string? MainWindowAltKeys()
    {
        MainForm? form = null;
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        try
        {
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }

            var menus = new Dictionary<char, string>();
            foreach (var item in form.MainMenuStrip?.Items.OfType<ToolStripMenuItem>() ?? [])
                if (AltKeyOf(item.Text) is { } key)
                {
                    Check(!menus.ContainsKey(key), $"two menus both open with Alt+{key}: \"{menus.GetValueOrDefault(key)}\" and \"{item.Text}\"");
                    menus[key] = item.Text ?? "";
                }
            Check(menus.Count >= 7, $"the main window's menus must be read - {menus.Count} found");

            var clashes = new List<string>();
            var controls = 0;
            void Walk(Control parent)
            {
                foreach (Control c in parent.Controls)
                {
                    if (c is not MenuStrip && AltKeyOf(c.Text) is { } key)
                    {
                        controls++;
                        if (menus.TryGetValue(key, out var menu)) clashes.Add($"\"{c.Text}\" and the {menu.Replace("&", "")} menu");
                    }
                    Walk(c);
                }
            }
            Walk(form);
            Check(controls > 30, $"the window's controls with an Alt key must be read - {controls} found");
            Check(clashes.Count == 0, $"an Alt key that opens a menu is also a control's, so one of them can never be reached: {string.Join("; ", clashes)}");
            return $"{menus.Count} menus ({string.Join(", ", menus.Keys.Order())}) and {controls} controls with an Alt key: none shared";
        }
        finally
        {
            try { form?.Dispose(); } catch { /* best effort */ }
            CuePlayer.GloballyMuted = restoreMuted;
        }
    }

    /// <summary>The key a control's or menu's text gives Alt: the letter after a single '&amp;', upper case; null if none.</summary>
    internal static char? AltKeyOf(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (text[i] != '&') continue;
            if (text[i + 1] == '&') { i++; continue; } // "&&" is a plain ampersand
            return char.ToUpperInvariant(text[i + 1]);
        }
        return null;
    }
}
