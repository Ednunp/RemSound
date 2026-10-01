using RemSound.Core;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>LISTS: CTRL+A (Ed, 2026-09-29: "can't control a in multi select list box i.e. peers etc, we should be able to so you
    /// can delete all peers at once for example"). WinForms' ListBox has no Ctrl+A. A list that can hold several selections -
    /// Manage named peers, the EQ's band list - selects every row on Ctrl+A; one that holds one at a time is left alone; and
    /// Manage named peers' Delete forgets every selected name at once.</summary>
    private static string? ListsSelectAllAndDeleteSeveral()
    {
        var ctrlA = new KeyEventArgs(Keys.Control | Keys.A);
        using (var several = new ListBox { SelectionMode = SelectionMode.MultiExtended })
        {
            several.Items.AddRange(["one", "two", "three"]);
            ListSelection.OnKeyDown(several, ctrlA);
            Check(several.SelectedItems.Count == 3 && ctrlA.Handled, $"Ctrl+A in a list that holds several must select every row ({several.SelectedItems.Count} of 3)");
        }
        using (var one = new ListBox { SelectionMode = SelectionMode.One })
        {
            one.Items.AddRange(["one", "two"]);
            var key = new KeyEventArgs(Keys.Control | Keys.A);
            ListSelection.OnKeyDown(one, key);
            Check(!key.Handled, "a list that holds one selection at a time must be left to itself");
        }

        MainForm? window = null;
        Form? dialog = null;
        try
        {
            try { window = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            window.NamePeerForTest("machine:SELFTEST-A", "SELFTEST-A", "198.51.100.71", "Alpha");
            window.NamePeerForTest("machine:SELFTEST-B", "SELFTEST-B", "198.51.100.72", "Bravo");
            window.NamePeerForTest("machine:SELFTEST-C", "SELFTEST-C", "198.51.100.73", "Charlie");
            var before = window.NamedPeerCountForTest;
            dialog = window.BuildManageNamedPeersDialog();
            var list = Require(AllControls(dialog).OfType<ListBox>().FirstOrDefault(), "the Manage named peers list");
            Check(list.SelectionMode == SelectionMode.MultiExtended, "Manage named peers' list must hold several selections");
            // The keys go in as the list gets them, so the list's own wiring is what answers - not the helper called directly.
            var press = Require(typeof(Control).GetMethod("OnKeyDown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance),
                "Control.OnKeyDown");
            press.Invoke(list, [new KeyEventArgs(Keys.Control | Keys.A)]);
            Check(list.SelectedItems.Count == list.Items.Count && list.Items.Count >= 3, $"Ctrl+A must select every named peer ({list.SelectedItems.Count} of {list.Items.Count})");
            var chosen = list.SelectedItems.Count;

            // The EQ's band list, the other list that holds several: rows of its own, Ctrl+A through its own wiring.
            var bands = window.ParametricBandListForTest;
            bands.Items.AddRange(["band one", "band two", "band three"]);
            press.Invoke(bands, [new KeyEventArgs(Keys.Control | Keys.A)]);
            Check(bands.SelectedItems.Count == 3, $"Ctrl+A in the EQ's band list must select every band ({bands.SelectedItems.Count} of 3)");
            bands.Items.Clear();

            press.Invoke(list, [new KeyEventArgs(Keys.Delete)]);
            Check(window.NamedPeerCountForTest == before - chosen && chosen >= 3,
                $"Delete must forget every selected name at once ({before} before, {chosen} selected, {window.NamedPeerCountForTest} after)");

            // And the Delete button, the same: two chosen of three, both gone.
            window.NamePeerForTest("machine:SELFTEST-A", "SELFTEST-A", "198.51.100.71", "Alpha");
            window.NamePeerForTest("machine:SELFTEST-B", "SELFTEST-B", "198.51.100.72", "Bravo");
            window.NamePeerForTest("machine:SELFTEST-C", "SELFTEST-C", "198.51.100.73", "Charlie");
            dialog.Dispose();
            dialog = window.BuildManageNamedPeersDialog();
            list = Require(AllControls(dialog).OfType<ListBox>().FirstOrDefault(), "the Manage named peers list");
            before = window.NamedPeerCountForTest;
            list.SetSelected(0, true);
            list.SetSelected(1, true);
            var delete = Require(AllControls(dialog).OfType<Button>().FirstOrDefault(b => b.AccessibleName == "Delete"), "the Delete button");
            var click = Require(typeof(Control).GetMethod("OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance),
                "Control.OnClick");
            click.Invoke(delete, [EventArgs.Empty]);   // PerformClick does nothing on a window that is not shown
            Check(window.NamedPeerCountForTest == before - 2, $"the Delete button must forget both selected names ({before} before, {window.NamedPeerCountForTest} after)");
            return "Ctrl+A selects every row of a list that holds several and leaves a single-selection list alone; Manage named peers "
                 + "takes several, and the Delete key or button forgets them all";
        }
        finally
        {
            try { dialog?.Dispose(); } catch { /* teardown */ }
            try { window?.Dispose(); } catch { /* teardown */ }
        }
    }
}
