namespace RemSound.App;

/// <summary>
/// Is a path inside a folder? The one answer every "only delete what is ours" guard asks for.
///
/// <para>Two guards used to answer it with a bare prefix check. <c>...\VST3\RemSound</c> is a prefix of
/// <c>...\VST3\RemSoundX</c>, so a tampered plugin manifest could have removed another company's plugin with a
/// similar name, and uninstalling could have cleared the autostart entry of a copy in <c>Programs\RemSound2</c>.
/// A folder only contains what sits below its name followed by a separator. 2026-09-13 review.</para>
/// </summary>
internal static class PathContainment
{
    internal static bool IsInside(string? path, string? folder)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(folder)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;   // an unreadable path is never treated as ours
        }
    }
}
