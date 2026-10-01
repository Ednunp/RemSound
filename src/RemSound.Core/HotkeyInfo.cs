using System.Windows.Forms;

namespace RemSound.Core;

public sealed record HotkeyInfo(Keys Key, bool Control, bool Shift, bool Alt)
{
    public static HotkeyInfo Default { get; } = new(Keys.M, true, true, true);

    /// <summary>Sentinel for "no hotkey assigned". Used by features that want a global hotkey
    /// to be opt-in rather than always-on (volume up/down, etc.). The hotkey controller skips
    /// registration silently when a hotkey is unset.</summary>
    public static HotkeyInfo Unset { get; } = new(Keys.None, false, false, false);

    public bool IsUnset => Key == Keys.None && !Control && !Shift && !Alt;

    public bool IsValid =>
        (Control || Shift || Alt) &&
        Key is not Keys.None and not Keys.ControlKey and not Keys.ShiftKey and not Keys.Menu;

    public override string ToString()
    {
        if (IsUnset) return "(not set)";
        var parts = new List<string>(4);
        if (Control) parts.Add("Control");
        if (Shift) parts.Add("Shift");
        if (Alt) parts.Add("Alt");
        parts.Add(Key == Keys.Scroll ? "Scroll Lock" : Key.ToString());
        return string.Join("+", parts);
    }
}
