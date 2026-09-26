using AudioPlugSharp;

namespace RemSound.Plugin;

/// <summary>
/// An on/off switch that says "on" or "off".
///
/// <para>The switches used to be plain parameters with the format "0", which String.Format reads as the literal text
/// "0": every switch read "0" whatever it was set to (2026-09-24, the first scripted test in Reaper). The parameter
/// list is this plugin's screen-reader way in - OSARA reads it aloud - so a switch that cannot say whether it is on is
/// no way in at all.</para>
/// </summary>
internal sealed class SwitchParameter : AudioPluginParameter
{
    public override string DisplayValue => EditValue >= 0.5 ? "on" : "off";
}

/// <summary>
/// The peer cursor ("Peer in list"): says who it is on - "2: Andre" - not just a number, so a screen reader moving it
/// through the list hears the people. "none" at 0; the bare number where nobody is at that place yet.
/// </summary>
internal sealed class PeerCursorParameter : AudioPluginParameter
{
    private readonly Func<int, string?> nameAt;

    public PeerCursorParameter(Func<int, string?> nameAt) => this.nameAt = nameAt;

    public override string DisplayValue
    {
        get
        {
            var position = (int)Math.Round(EditValue);
            if (position <= 0) return "none";
            string? name = null;
            try { name = nameAt(position); }
            catch { /* asked for from the host's thread: a name we cannot get is just a number */ }
            return string.IsNullOrWhiteSpace(name) ? $"{position}" : $"{position}: {name}";
        }
    }
}
