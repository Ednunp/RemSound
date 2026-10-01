namespace RemSound.App;

/// <summary>
/// Ctrl+A in a list that can hold several selections selects every row, as it does in Windows' own lists. WinForms' ListBox
/// has no Ctrl+A of its own (Ed, 2026-09-29: "can't control a in multi select list box ... we should be able to so you can
/// delete all peers at once"). Wired once per list; a list that holds one selection at a time is left as it is.
/// </summary>
internal static class ListSelection
{
    public static void AllowSelectAll(ListBox list) => list.KeyDown += (_, e) => OnKeyDown(list, e);

    /// <summary>What Ctrl+A does to <paramref name="list"/> (the gate calls it as the key would).</summary>
    internal static void OnKeyDown(ListBox list, KeyEventArgs e)
    {
        if (e.KeyData != (Keys.Control | Keys.A) || list.SelectionMode is not (SelectionMode.MultiExtended or SelectionMode.MultiSimple)) return;
        list.BeginUpdate();
        try { for (var i = 0; i < list.Items.Count; i++) list.SetSelected(i, true); }
        finally { list.EndUpdate(); }
        e.Handled = e.SuppressKeyPress = true;
    }
}
