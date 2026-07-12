using System.Windows.Forms;

namespace RemSound.App;

/// <summary>
/// Shared screen-reader wiring for a <see cref="CheckedListBox"/> — the exact behaviour the main
/// window's device lists use, factored out so dialogs (e.g. the service profile editor) announce
/// identically instead of each rolling its own. On focus and on arrow-key move it writes a spoken
/// status ("checked, VLC. Item 1 of 3. Press Space to toggle.") into both the list's
/// AccessibleDescription and a companion status label, so NVDA reads the item AND its checked state.
/// Also adds first-letter navigation that never accidentally toggles a check.
/// </summary>
internal static class CheckedListAccessibility
{
    public static void Wire(CheckedListBox list, Label statusLabel, string itemKind)
    {
        var lastIndex = 0;

        void Update(int? overrideIndex = null, bool? overrideChecked = null)
            => SetStatus(list, statusLabel, itemKind, overrideIndex, overrideChecked);

        void RestoreFocus()
        {
            if (list.Items.Count == 0) { Update(); return; }
            var target = list.SelectedIndex >= 0 ? list.SelectedIndex : Math.Clamp(lastIndex, 0, list.Items.Count - 1);
            if (list.SelectedIndex != target) list.SelectedIndex = target;
            lastIndex = target;
            Update();
        }

        list.SelectedIndexChanged += (_, _) =>
        {
            if (list.SelectedIndex >= 0) lastIndex = list.SelectedIndex;
            Update();
        };
        list.ItemCheck += (_, e) => Update(e.Index, e.NewValue == CheckState.Checked);
        list.Enter += (_, _) => RestoreFocus();
        list.GotFocus += (_, _) => RestoreFocus();
        list.MouseDown += (_, args) =>
        {
            var index = list.IndexFromPoint(args.Location);
            if (index >= 0) { list.SelectedIndex = index; lastIndex = index; }
        };
        // First-letter navigation that highlights the matching item without ever toggling its check
        // (the default CheckedListBox key handling has been seen to toggle on a unique single-letter
        // prefix). Spacebar still falls through so Space toggles as normal.
        list.KeyDown += (_, args) =>
        {
            if (args.Modifiers != Keys.None) return;
            char ch;
            if (args.KeyCode >= Keys.A && args.KeyCode <= Keys.Z) ch = (char)('a' + (args.KeyCode - Keys.A));
            else if (args.KeyCode >= Keys.D0 && args.KeyCode <= Keys.D9) ch = (char)('0' + (args.KeyCode - Keys.D0));
            else if (args.KeyCode >= Keys.NumPad0 && args.KeyCode <= Keys.NumPad9) ch = (char)('0' + (args.KeyCode - Keys.NumPad0));
            else return;

            var startIdx = list.SelectedIndex < 0 ? 0 : list.SelectedIndex + 1;
            for (var offset = 0; offset < list.Items.Count; offset++)
            {
                var idx = (startIdx + offset) % list.Items.Count;
                var text = list.Items[idx]?.ToString() ?? string.Empty;
                if (text.Length > 0 && char.ToLowerInvariant(text[0]) == ch) { list.SelectedIndex = idx; break; }
            }
            args.Handled = true;
            args.SuppressKeyPress = true;
        };

        Update();
    }

    // Exact copy of MainForm.UpdateCheckedListStatus so the spoken text is word-for-word identical.
    private static void SetStatus(CheckedListBox list, Label statusLabel, string itemKind, int? overrideIndex, bool? overrideChecked)
    {
        if (list.Items.Count == 0)
        {
            var emptyText = $"No {itemKind}s available.";
            statusLabel.Text = emptyText;
            list.AccessibleDescription = emptyText;
            return;
        }
        var index = overrideIndex ?? (list.SelectedIndex >= 0 ? list.SelectedIndex : 0);
        index = Math.Clamp(index, 0, list.Items.Count - 1);
        var isChecked = overrideChecked ?? list.GetItemChecked(index);
        var checkedText = isChecked ? "checked" : "not checked";
        var itemText = list.Items[index]?.ToString() ?? itemKind;
        var text = $"{checkedText}, {itemText}. Item {index + 1} of {list.Items.Count}. Press Space to toggle.";
        statusLabel.Text = text;
        list.AccessibleDescription = text;
        statusLabel.AccessibleDescription = text;
    }
}
