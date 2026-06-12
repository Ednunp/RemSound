using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Default cue-sound resolution. The cue WAVs ship as numbered variants in <c>sounds\</c> -
/// "connect 1.wav", "connect 2.wav", ... - and the user picks which one is the default for each
/// cue in Preferences (stored machine-wide in <see cref="AppConfig.DefaultCueSounds"/>). This
/// helper discovers the variants for a cue and resolves which one is the active default.
///
/// The full resolution order used wherever a cue is loaded (MainForm, the startup cue, the
/// Preferences preview) is:
///   1. the user's per-profile custom WAV (handled by the caller) - highest priority;
///   2. the machine-wide chosen default variant, if it still exists on disk;
///   3. the first available variant (the lowest-numbered - the "1"s) - the shipped default;
///   4. nothing - the cue is silent.
///
/// The count of variants is never assumed: whatever "&lt;base&gt; &lt;n&gt;.wav" files are present
/// are offered, so adding more sounds later needs no code change. Matching is case-insensitive so
/// a stray capital (e.g. "Profile menu open 1.wav") still resolves.
/// </summary>
internal static class CueSounds
{
    /// <summary>The variant filenames available for a cue, sorted by their trailing number. The
    /// base name comes from <paramref name="defaultFileName"/> (the historical single name, e.g.
    /// "connect.wav" -> base "connect"), matched against "connect.wav" and "connect &lt;n&gt;.wav"
    /// in <c>sounds\</c>. Returns filenames only (no path); empty when none are present.</summary>
    public static IReadOnlyList<string> Variants(string defaultFileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(defaultFileName);
        var dir = AppConfig.SoundsDirectory;
        if (string.IsNullOrEmpty(baseName) || !Directory.Exists(dir)) return Array.Empty<string>();

        var matches = new List<(int Order, string Name)>();
        try
        {
            foreach (var full in Directory.EnumerateFiles(dir, "*.wav"))
            {
                var name = Path.GetFileName(full);
                var stem = Path.GetFileNameWithoutExtension(name);
                if (stem.Equals(baseName, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add((0, name)); // the bare, unnumbered name sorts first
                }
                else if (stem.Length > baseName.Length + 1
                         && stem.StartsWith(baseName + " ", StringComparison.OrdinalIgnoreCase)
                         && int.TryParse(stem[(baseName.Length + 1)..], out var n))
                {
                    matches.Add((n, name));
                }
            }
        }
        catch { return Array.Empty<string>(); }
        return matches.OrderBy(m => m.Order).Select(m => m.Name).ToList();
    }

    /// <summary>The "Sound N" label for a variant filename, for the Preferences listbox.
    /// "connect 2.wav" -> "Sound 2"; an unnumbered "connect.wav" -> "Sound 1".</summary>
    public static string VariantLabel(string defaultFileName, string variantFileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(defaultFileName);
        var stem = Path.GetFileNameWithoutExtension(variantFileName);
        if (stem.Length > baseName.Length + 1
            && stem.StartsWith(baseName + " ", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(stem[(baseName.Length + 1)..], out var n))
        {
            return $"Sound {n}";
        }
        return "Sound 1";
    }

    /// <summary>The chosen default variant filename for a cue: the machine-wide pick if it still
    /// exists among the variants, otherwise the first variant (the "1"). Null when the cue has no
    /// variants on disk at all.</summary>
    public static string? ResolveDefaultFileName(string cueId, string defaultFileName, AppConfig cfg)
    {
        var variants = Variants(defaultFileName);
        if (variants.Count == 0) return null;
        if (cfg.DefaultCueSounds.TryGetValue(cueId, out var chosen))
        {
            var match = variants.FirstOrDefault(v => v.Equals(chosen, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return variants[0];
    }

    /// <summary>Full path to the chosen default WAV for a cue, or null when none resolves.</summary>
    public static string? ResolveDefaultPath(string cueId, string defaultFileName, AppConfig cfg)
    {
        var name = ResolveDefaultFileName(cueId, defaultFileName, cfg);
        return name is null ? null : Path.Combine(AppConfig.SoundsDirectory, name);
    }
}
