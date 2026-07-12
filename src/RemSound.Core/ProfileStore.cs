using System.Text.Json;

namespace RemSound.Core;

/// <summary>
/// File-backed store for <see cref="Profile"/> instances. One profile = one JSON file
/// under <c>&lt;exe&gt;\profiles\&lt;machine name&gt;\&lt;title&gt;.json</c>.
///
/// Profile names are user-supplied "plain English" strings; the store sanitises them
/// for the filesystem (replaces invalid chars with underscores) but keeps the original
/// string as the in-file Title. Two profiles whose sanitised filenames collide will
/// overwrite each other — fine in practice; very rare.
///
/// Per-machine subfolder: profiles\&lt;machine&gt;\ — keeps each machine's profiles
/// separate by default. To share a profile between machines, copy the .json file from
/// one machine's folder into the other machine's folder. The profile content is fully
/// portable; device IDs that don't exist on the loading machine are silently dropped
/// at apply time.
/// </summary>
public sealed class ProfileStore
{
    private readonly string baseDir;

    public ProfileStore()
    {
        var machineFolder = SanitiseFsName(Environment.MachineName);
        // 2026-06-10: profiles live under "user settings and logs"\profiles\<machine>\.
        // AppConfig.MigrateLegacyLayoutIfNeeded moves any pre-existing profiles here at startup.
        baseDir = Path.Combine(AppConfig.ProfilesBaseDirectory, machineFolder);
        try { Directory.CreateDirectory(baseDir); }
        catch { /* permissions; List/Save will surface this when actually used */ }
    }

    /// <summary>Construct a profile store pointing at an explicit directory. Used when the
    /// user has picked a custom profiles folder via the "Browse for profile folder" button
    /// — typically a Dropbox / OneDrive / shared-drive path, or a per-project folder.
    /// No per-machine subfolder is appended; the supplied path IS the profiles folder, so
    /// the same path on multiple machines shares profiles. Throws if the path is null or
    /// empty (caller should validate before constructing).</summary>
    public ProfileStore(string customDirectory)
    {
        if (string.IsNullOrWhiteSpace(customDirectory))
            throw new ArgumentException("Custom profile directory cannot be null or empty", nameof(customDirectory));
        baseDir = customDirectory;
        try { Directory.CreateDirectory(baseDir); }
        catch { /* permissions; List/Save will surface this when actually used */ }
    }

    /// <summary>Folder this store reads from and writes into.</summary>
    public string BaseDirectory => baseDir;

    /// <summary>Reserved title of the send-only Windows service's profile. It lives in the same folder
    /// as normal profiles (so the service loads it via <see cref="Load"/>), but it's edited only through
    /// the Service menu's config dialog and hidden from the normal picker by <see cref="ListProfileTitles"/>.</summary>
    public const string ReservedServiceProfileTitle = "RemSound service";

    /// <summary>Returns the user-facing titles of every profile in the folder, sorted
    /// alphabetically (case-insensitive). Excludes the synthetic blank-template and the reserved
    /// service profile; the caller decides whether to surface the blank template.</summary>
    public IReadOnlyList<string> ListProfileTitles()
    {
        if (!Directory.Exists(baseDir)) return [];
        try
        {
            return Directory.GetFiles(baseDir, "*.json")
                .Select(p => TryReadTitle(p) ?? Path.GetFileNameWithoutExtension(p))
                .Where(static t => !string.IsNullOrWhiteSpace(t))
                .Where(static t => !string.Equals(t, ReservedServiceProfileTitle, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Returns whether the profile with the given title has its ReadOnly flag set
    /// on disk, without doing a full <see cref="Load"/>. Used by the startup profile picker
    /// to label locked profiles in the list ("Title (read-only)") so the user knows what
    /// they're picking. Returns false on any error — the picker treats unreadable profiles
    /// as not-read-only, which is the safer default (the worst case is the user gets the
    /// normal save-prompt behaviour on close, which is what they're already used to).</summary>
    public bool IsProfileReadOnly(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var path = PathFor(title);
        if (!File.Exists(path)) return false;
        try
        {
            var json = File.ReadAllText(path);
            var profile = JsonSerializer.Deserialize<Profile>(json);
            return profile?.ReadOnly ?? false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Loads a profile by title. Returns null if the file is missing or
    /// unreadable. Malformed JSON is treated as "not found" rather than throwing —
    /// the caller can surface a diagnostic and fall back to a blank template.</summary>
    public Profile? Load(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var path = PathFor(title);
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            var profile = JsonSerializer.Deserialize<Profile>(json);
            // Force the in-file Title to whatever was on disk; defends against the user
            // renaming the .json filename without editing the JSON.
            if (profile is not null && string.IsNullOrWhiteSpace(profile.Title))
            {
                profile.Title = title;
            }
            return profile;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Writes a profile to disk. Title must be non-empty; caller is responsible
    /// for prompting the user or generating one. Throws if filesystem write fails.</summary>
    public void Save(Profile profile)
    {
        if (profile is null) throw new ArgumentNullException(nameof(profile));
        if (string.IsNullOrWhiteSpace(profile.Title))
            throw new ArgumentException("Profile title cannot be empty", nameof(profile));
        Directory.CreateDirectory(baseDir);
        var path = PathFor(profile.Title);
        var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
        WriteFileAtomic(path, json);
    }

    /// <summary>Write text crash-safely: write a sibling temp file, then atomically move it over the
    /// target. A crash, power-loss, or the updater force-closing mid-write then leaves either the old
    /// file or the complete new one — never a truncated file that the catch-all loaders would
    /// silently read as a blank profile.</summary>
    private static void WriteFileAtomic(string path, string contents)
    {
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, contents);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
            throw;
        }
    }

    /// <summary>Deletes the profile by title. Returns true if a file was removed,
    /// false if it didn't exist or couldn't be deleted.</summary>
    public bool Delete(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var path = PathFor(title);
        if (!File.Exists(path)) return false;
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True if a profile with the given title (sanitised filename) already exists.</summary>
    public bool Exists(string title) => !string.IsNullOrWhiteSpace(title) && File.Exists(PathFor(title));

    /// <summary>Rename a profile on disk. Loads the JSON, updates the in-file Title field,
    /// writes it under the new sanitised filename, then deletes the old file. Returns true
    /// on success. Fails (returns false, no changes made) if the source doesn't exist, the
    /// new title is empty/identical, or a file already exists at the destination filename.
    /// 2026-05-06.</summary>
    public bool Rename(string oldTitle, string newTitle)
    {
        if (string.IsNullOrWhiteSpace(oldTitle) || string.IsNullOrWhiteSpace(newTitle)) return false;
        if (string.Equals(oldTitle, newTitle, StringComparison.Ordinal)) return false;
        var oldPath = PathFor(oldTitle);
        var newPath = PathFor(newTitle);
        if (!File.Exists(oldPath)) return false;
        if (File.Exists(newPath) && !string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var profile = Load(oldTitle);
            if (profile is null) return false;
            profile.Title = newTitle;
            var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
            WriteFileAtomic(newPath, json);
            if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(oldPath);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The on-disk path for a profile of the given title in this store's base
    /// directory. Sanitises filesystem-invalid characters in the title before joining.
    /// Public so callers (e.g. MainForm at startup) can record where a loaded profile
    /// lives, which matters once Save As lets the user write outside <see cref="BaseDirectory"/>.</summary>
    public string PathFor(string title) => Path.Combine(baseDir, SanitiseFsName(title) + ".json");

    /// <summary>Read just the Title field of a profile JSON to surface the user-supplied
    /// name even if it differs from the sanitised filename. Cheap; the file is small.</summary>
    private static string? TryReadTitle(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty(nameof(Profile.Title), out var titleProp)
                && titleProp.ValueKind == JsonValueKind.String)
            {
                return titleProp.GetString();
            }
        }
        catch { /* fall through */ }
        return null;
    }

    private static string SanitiseFsName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "untitled";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }
}
