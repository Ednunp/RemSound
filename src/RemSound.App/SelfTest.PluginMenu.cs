using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// The DAW plugin menu's Install and Remove, chosen from the real menu through the control channel, the way a person
/// chooses them - in a run that touches nothing real, so into the run's own folder and never the VST3 folder every DAW on
/// the computer loads from. Until 2026-09-24 the install folder ignored the throwaway settings, so neither item could be
/// tested at all without replacing or deleting the real plugin.
/// </summary>
internal static partial class SelfTest
{

    /// <summary>Every file in a folder with its size and time, or "absent".</summary>
    private static string FolderFingerprint(string folder) =>
        !Directory.Exists(folder)
            ? "absent"
            : string.Join("|", Directory.GetFiles(folder, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Select(f => { var i = new FileInfo(f); return $"{Path.GetRelativePath(folder, f)}:{i.Length}:{i.LastWriteTimeUtc.Ticks}"; }));
}
