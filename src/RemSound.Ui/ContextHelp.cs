using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RemSound.App;

/// <summary>
/// F1 help for the control you are on (Ed, 2026-09-25: "say i'm on the jitter buffer, I hit f1, it opens help and brings
/// screen reader focus to that control").
///
/// <para>Each control is marked with a HELP KEY where it is made (<see cref="Mark{T}"/>), and the same key is the id of
/// its entry in the manual (<see cref="HelpManual"/>). F1 walks up from the control that has the keyboard to the nearest
/// one with a key - so a tab page's key covers anything on it that has none of its own - and shows that entry in
/// <see cref="ContextHelpWindow"/>. Escape closes it and the keyboard goes back where it was.</para>
///
/// <para>Here rather than in the app so the DAW plugin can use it too. Everything the app owns - the sounds, the log,
/// the browser - comes in by injection, as the controls' sounds do, so this library still depends on nothing.</para>
/// </summary>
internal static class ContextHelp
{
    private static readonly ConditionalWeakTable<Control, string> keys = new();

    /// <summary>Give a control its help key. Returns the control, so it can be used where the control is made.</summary>
    public static T Mark<T>(T control, string key) where T : Control
    {
        keys.AddOrUpdate(control, key);
        return control;
    }

    /// <summary>The control's own help key, or null.</summary>
    public static string? KeyOf(Control control) => keys.TryGetValue(control, out var key) ? key : null;

    private static readonly ConditionalWeakTable<ToolStripItem, string> itemKeys = new();

    /// <summary>Give a menu item its help key. Returns the item.</summary>
    public static T MarkItem<T>(T item, string key) where T : ToolStripItem
    {
        itemKeys.AddOrUpdate(item, key);
        return item;
    }

    /// <summary>Mark every item in a menu, however deep, whose name is in <paramref name="keys"/>. An item missing from
    /// it is left unmarked, for the gate's guard to name.</summary>
    public static void MarkItemsByName(ToolStripItemCollection items, IReadOnlyDictionary<string, string> keys)
    {
        foreach (ToolStripItem item in items)
        {
            if (item is ToolStripSeparator) continue;
            if (keys.TryGetValue(NameOfItem(item), out var key)) MarkItem(item, key);
            if (item is ToolStripDropDownItem { HasDropDownItems: true } dropDown) MarkItemsByName(dropDown.DropDownItems, keys);
        }
    }

    /// <summary>The menu item's own help key, or null.</summary>
    public static string? KeyOfItem(ToolStripItem item) => itemKeys.TryGetValue(item, out var key) ? key : null;

    private static readonly List<WeakReference<ToolStrip>> menus = [];

    /// <summary>A menu F1 looks in when one is open: the main window's menu bar, the tray menu. Called where it is made.</summary>
    public static void TrackMenu(ToolStrip menu)
    {
        lock (menus)
        {
            // Menus of windows since closed let go of: two were added at every profile switch and never removed.
            menus.RemoveAll(w => !w.TryGetTarget(out var m) || m.IsDisposed);
            menus.Add(new WeakReference<ToolStrip>(menu));
        }
    }

    internal static int TrackedMenusForTest { get { lock (menus) return menus.Count; } }

    /// <summary>Shows a window in front of everything and waits for it to close - how the app shows help from the tray
    /// menu, whose main window may be hidden. Set by the host; without it, help is shown over the menu's own window.</summary>
    public static Action<Form>? ShowInFront { get; set; }

    /// <summary>The menu item the keyboard is on in an open menu - the deepest one open - or null when no menu is open.
    /// An open submenu with nothing chosen in it yet counts as being on the item that opened it.</summary>
    public static ToolStripItem? OpenMenuItem()
    {
        List<ToolStrip> roots;
        lock (menus) roots = menus.Select(w => w.TryGetTarget(out var m) ? m : null).OfType<ToolStrip>().Where(m => !m.IsDisposed).ToList();
        foreach (var root in roots)
        {
            if (root is ToolStripDropDown { Visible: false }) continue;
            ToolStripItem? found = null;
            var level = root.Items.Cast<ToolStripItem>().ToList();
            // On the menu bar itself, a name counts as the one the keyboard is on only while the bar has the keyboard (Alt
            // pressed, arrowing along the names): a name left marked as selected after its menu closed must not turn every
            // later F1 into that menu's help.
            var mayTakeSelected = root is ToolStripDropDown || KeyboardActive(root);
            while (true)
            {
                var open = level.OfType<ToolStripDropDownItem>().FirstOrDefault(i => i.DropDown.Visible);
                if (open is not null)
                {
                    found = open;
                    level = open.DropDownItems.Cast<ToolStripItem>().ToList();
                    mayTakeSelected = true;
                    continue;
                }
                if (mayTakeSelected && level.FirstOrDefault(i => i.Selected && i is not ToolStripSeparator) is { } selected) found = selected;
                break;
            }
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>Whether a menu bar has the keyboard (WinForms' own record, which is not public). False when it can't be
    /// read, which only means F1 on a closed menu's name gives the help of the control behind it.</summary>
    private static bool KeyboardActive(ToolStrip strip)
    {
        try
        {
            return typeof(ToolStrip).GetProperty("KeyboardActive", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.GetValue(strip) is true;
        }
        catch { return false; }
    }

    /// <summary>The name a person knows a menu item by, without its "&amp;" or its shortcut.</summary>
    public static string NameOfItem(ToolStripItem item)
    {
        var name = !string.IsNullOrWhiteSpace(item.AccessibleName) ? item.AccessibleName! : item.Text ?? "";
        name = name.Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&");
        var alt = name.IndexOf(" (Alt+", StringComparison.Ordinal);
        return (alt > 0 ? name[..alt] : name).Trim();
    }

    /// <summary>
    /// F1 on a menu item: the menus close, its help shows, and when it closes the menus open again with the keyboard back
    /// on the item. An item with no help of its own gets the help of the menu it is in. False when neither has any.
    /// </summary>
    public static bool ShowForItem(ToolStripItem item)
    {
        if (Open is not null) return true;
        string? key = null;
        ToolStripItem? about = null;
        for (ToolStripItem? i = item; i is not null; i = i.OwnerItem)
        {
            if (KeyOfItem(i) is { } k) { key = k; about = i; break; }
        }
        if (key is null || about is null) return false;

        var name = NameOfItem(item);
        var own = ReferenceEquals(about, item);
        if (!own) Log?.Invoke($"context help: F1 on the menu item \"{name}\" - nothing of its own, showing \"{NameOfItem(about)}\" instead");
        Log?.Invoke($"context help: F1 on the menu item \"{name}\" -> {key}");

        // The way back: every item from the top of the menu down to this one, and where a tray menu was.
        var path = new List<ToolStripItem>();
        for (ToolStripItem? i = item; i is not null; i = i.OwnerItem) path.Insert(0, i);
        var root = path[0].Owner;
        var trayAt = root is ContextMenuStrip tray ? tray.Bounds.Location : (Point?)null;
        var ownerForm = root?.FindForm();
        CloseMenus(root);

        var window = Build(own ? name : NameOfItem(about), key);
        PlayOpenSound?.Invoke();
        Open = window;
        try
        {
            if (ShowInFront is { } inFront && (ownerForm is null || !ownerForm.Visible)) inFront(window);
            else window.ShowDialog(ownerForm);
        }
        finally
        {
            Open = null;
            window.Dispose();
        }
        Reopen(path, root, trayAt);
        return true;
    }

    private static void CloseMenus(ToolStrip? root)
    {
        try
        {
            switch (root)
            {
                case ToolStripDropDown dropDown: dropDown.Close(ToolStripDropDownCloseReason.Keyboard); break;
                case not null:
                    foreach (var top in root.Items.OfType<ToolStripDropDownItem>().Where(i => i.DropDown.Visible)) top.HideDropDown();
                    break;
            }
        }
        catch { /* a menu already gone */ }
    }

    /// <summary>Open the menus again along the way to the item, and put the keyboard on it.</summary>
    private static void Reopen(List<ToolStripItem> path, ToolStrip? root, Point? trayAt)
    {
        try
        {
            if (root is null || root.IsDisposed) return;
            if (root is ContextMenuStrip tray && trayAt is { } at)
            {
                // A tray menu has the keyboard only while its program is the one in front - the tray icon arranges that
                // when it opens the menu, and this has to as well, or the menu came back with the keys going elsewhere.
                BeforeTrayMenuReopens?.Invoke();
                tray.Show(at);
            }
            // A submenu can rebuild itself as it opens (Recent profiles, the tray's Profiles), so after opening each step
            // the next item is looked for again, by its text, among what the submenu now holds.
            for (var i = 0; i < path.Count - 1; i++)
            {
                if (path[i] is not ToolStripDropDownItem step || step.IsDisposed) break;
                step.ShowDropDown();
                var wanted = path[i + 1];
                if (!step.DropDownItems.Contains(wanted)
                    && step.DropDownItems.Cast<ToolStripItem>().FirstOrDefault(x => x.Text == wanted.Text) is { } again)
                    path[i + 1] = again;
            }
            var last = path[^1];
            if (!last.IsDisposed && last.Available) last.Select();
            Log?.Invoke($"context help: back on the menu item \"{NameOfItem(last)}\"");
        }
        catch (Exception ex) { Log?.Invoke($"context help: could not open the menu again: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>Put the program in front before a tray menu is opened again after help. Set by the host.</summary>
    public static Action? BeforeTrayMenuReopens { get; set; }

    /// <summary>The sound as help opens, and as it closes. Set by the host.</summary>
    public static Action? PlayOpenSound { get; set; }
    public static Action? PlayCloseSound { get; set; }

    /// <summary>Where the host's log lines go.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>Opens the manual in the browser, at a help key's entry (or at the top for null). Set by the host.</summary>
    public static Action<string?>? OpenManualInBrowser { get; set; }

    /// <summary>The manual to read. The app's is next to RemSound.exe; the plugin carries its own copy.</summary>
    public static Func<string> ManualPath { get; set; } = () => Path.Combine(AppContext.BaseDirectory, "readme.html");

    /// <summary>The help window that is open now, if any. F1 inside it does nothing more.</summary>
    internal static ContextHelpWindow? Open { get; private set; }

    /// <summary>What F1 on <paramref name="start"/> would show: the control whose key it is, and the key. A tab strip
    /// means the tab it is showing. Null when nothing up the chain has a key.</summary>
    public static (Control Owner, string Key)? Find(Control start)
    {
        if (start is TabControl { SelectedTab: { } page }) start = page;
        for (Control? c = start; c is not null; c = c.Parent)
        {
            if (KeyOf(c) is { } key) return (c, key);
        }
        return null;
    }

    /// <summary>The name a person knows a control by: what the screen reader reads for it, up to its "(Alt+X)" - so
    /// "Artefact sound type (Alt+A) — controls how audio gaps sound" is "Artefact sound type".</summary>
    public static string NameOf(Control control)
    {
        for (Control? c = control; c is not null; c = c.Parent)
        {
            var name = c switch
            {
                TabPage page => page.Text.Replace("&", "") + " tab",
                Form form => form.Text,
                _ when !string.IsNullOrWhiteSpace(c.AccessibleName) => c.AccessibleName,
                // A button or tick box with no name of its own is read by its label.
                ButtonBase b when !string.IsNullOrWhiteSpace(b.Text) => b.Text.Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&"),
                _ => null,
            };
            if (name is null) continue;
            var alt = name.IndexOf(" (Alt+", StringComparison.Ordinal);
            return (alt > 0 ? name[..alt] : name).Trim();
        }
        return control.GetType().Name;
    }

    /// <summary>
    /// F1 on <paramref name="start"/>: show its help and, when it closes, put the keyboard back on it. Returns false when
    /// nothing up the chain has help, so the caller can fall back to the whole manual.
    /// </summary>
    public static bool Show(Control start)
    {
        if (Open is not null) return true;   // F1 inside the help window itself
        if (start.FindForm() is ContextHelpWindow) return true;
        if (Find(start) is not (var owner, var key)) return false;

        // Help of its own, or its tab's (or window's) standing in for it. The title names whichever the words are about.
        var name = NameOf(start);
        var own = NearestKeyed(start) is not null;
        var about = own ? name : NameOf(owner);
        if (!own) Log?.Invoke($"context help: F1 on \"{name}\" - nothing of its own, showing \"{about}\" instead");
        Log?.Invoke($"context help: F1 on \"{name}\" ({start.FindForm()?.Text ?? "no window"}) -> {key}");
        var window = Build(about, key);

        var ownerForm = start.FindForm();
        PlayOpenSound?.Invoke();
        Open = window;
        try
        {
            window.ShowDialog(OwnerFor(ownerForm));
        }
        finally
        {
            Open = null;
            window.Dispose();
        }
        // Back where F1 was pressed. The dialog closing usually does this by itself; saying so makes it certain. Select,
        // not Focus: it also sets the window's own record of which control is active, which is what Windows' focus is put
        // back to when the window is next activated.
        if (!start.IsDisposed && start.Enabled) start.Select();
        return true;
    }

    /// <summary>
    /// Help for a whole window at once: every entry in <paramref name="keys"/>, one after another - the DAW plugin's
    /// Context help button, for music software that keeps F1 for itself. The manual button opens <paramref name="anchorKey"/>.
    /// </summary>
    public static void ShowWhole(Control from, string title, string anchorKey, IReadOnlyList<string> keys)
    {
        if (Open is not null) return;
        string text;
        var path = ManualPath();
        try
        {
            var html = File.ReadAllText(path);
            text = string.Join("\r\n\r\n", keys.Select(k => HelpManual.EntryText(html, k)).OfType<string>());
            if (text.Length == 0) text = "The manual has no entries for this window yet.";
        }
        catch (Exception ex)
        {
            text = $"The manual could not be read, so there is no help to show.\r\n{path}\r\n{ex.Message}";
            Log?.Invoke($"context help: the manual could not be read at {path}: {ex.GetType().Name}: {ex.Message}");
        }
        Log?.Invoke($"context help: the whole {title} ({anchorKey})");
        var window = new ContextHelpWindow(title, text, anchorKey);
        PlayOpenSound?.Invoke();
        Open = window;
        try { window.ShowDialog(OwnerFor(from.FindForm())); }
        finally
        {
            Open = null;
            window.Dispose();
        }
        if (!from.IsDisposed && from.Enabled) from.Select();
    }

    /// <summary>The window to show help over. A window that is not top-level - the plugin's, parented into the music
    /// software's frame - can't own a dialog; the frame it sits in can.</summary>
    private static IWin32Window? OwnerFor(Form? form)
    {
        if (form is null || form.TopLevel) return form;
        try { return new RootWindow(GetAncestor(form.Handle, 2 /* GA_ROOT */)); }
        catch { return null; }
    }

    private sealed class RootWindow(IntPtr handle) : IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    /// <summary>The help window for a key, not shown: its words are the manual's entry, or say plainly that there is
    /// none. Split out so the checks and the dialog audits can build it.</summary>
    internal static ContextHelpWindow Build(string name, string key)
    {
        string text;
        var path = ManualPath();
        try
        {
            var html = File.ReadAllText(path);
            text = HelpManual.EntryText(html, key)
                ?? $"The manual has no entry for this yet ({HelpManual.AnchorFor(key)}). Shift+F1 opens the whole manual.";
        }
        catch (Exception ex)
        {
            text = $"The manual could not be read, so there is no help to show.\r\n{path}\r\n{ex.Message}\r\n"
                 + "Unzipping RemSound again, or reinstalling it, puts the manual back.";
            Log?.Invoke($"context help: the manual could not be read at {path}: {ex.GetType().Name}: {ex.Message}");
        }
        return new ContextHelpWindow(name, text, key);
    }

    private static Control? NearestNamed(Control c)
    {
        for (Control? p = c; p is not null; p = p.Parent) if (!string.IsNullOrWhiteSpace(p.AccessibleName)) return p;
        return null;
    }

    private static Control? NearestKeyed(Control c)
    {
        // The control F1 was pressed on counts as having help of its own when it, or the named control it is part of
        // (the text box inside a number box, say), carries the key.
        var named = NearestNamed(c) ?? c;
        for (Control? p = c; p is not null; p = p.Parent)
        {
            if (KeyOf(p) is not null) return p;
            if (ReferenceEquals(p, named)) break;
        }
        return null;
    }

    /// <summary>F1 was pressed with the keyboard on this window (the window the key went to). False when it is not one
    /// of ours or has no help, so the caller can fall back.</summary>
    public static bool ShowForWindow(IntPtr hwnd)
    {
        var control = Control.FromChildHandle(hwnd);
        return control is not null && Show(control);
    }

    [DllImport("user32.dll")]
    internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    internal const int EM_SETSEL = 0x00B1;
    internal const int EM_GETSEL = 0x00B0;
}
