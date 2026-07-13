using System.Text.Json;

namespace RemSound.Core;

/// <summary>
/// Machine-wide store for the send-only Windows service's profile and its settings, kept in ProgramData —
/// deliberately OUTSIDE the user's profiles folder. Two reasons:
///
/// <list type="number">
/// <item>The service runs as SYSTEM (session 0), whose per-user data folder is NOT the interactive user's.
/// A profile saved in the user's folder would be invisible to the service. ProgramData resolves to the
/// same absolute path for every account, so the user (config dialog) and the service (SYSTEM) read and
/// write the exact same file.</item>
/// <item>Isolation: the service profile must never appear in the normal profile machinery — the startup
/// picker, File→Open, Recent profiles, or the password manager — and the user shouldn't stumble on it in
/// their profiles folder. Living somewhere else entirely guarantees that; the only way in is the Service
/// menu's config dialog.</item>
/// </list>
/// </summary>
public static class ServiceStore
{
    /// <summary>Test-only redirect so a self-test can round-trip without writing real ProgramData.</summary>
    internal static string? TestDirectoryOverride;

    /// <summary>ProgramData\RemSound\service — same path whether resolved by the user or by SYSTEM.</summary>
    public static string Directory => TestDirectoryOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemSound", "service");

    private static string ProfilePath => Path.Combine(Directory, "service-profile.json");
    private static string SettingsPath => Path.Combine(Directory, "service-settings.json");

    /// <summary>The configured service profile, or null if none has been set up yet. Never throws.</summary>
    public static Profile? LoadProfile()
    {
        try
        {
            return File.Exists(ProfilePath) ? JsonSerializer.Deserialize<Profile>(File.ReadAllText(ProfilePath)) : null;
        }
        catch { return null; }
    }

    public static void SaveProfile(Profile profile)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(ProfilePath, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Whether the service writes its own log. Machine-wide (the service can't read the user's
    /// per-account setting). Off by default.</summary>
    public static bool LoadLoggingEnabled()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return false;
            return (JsonSerializer.Deserialize<ServiceSettings>(File.ReadAllText(SettingsPath))?.LoggingEnabled) ?? false;
        }
        catch { return false; }
    }

    public static void SaveLoggingEnabled(bool enabled)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new ServiceSettings { LoggingEnabled = enabled }));
    }

    /// <summary>True once a service profile has been configured.</summary>
    public static bool IsConfigured() => File.Exists(ProfilePath);

    // === Running status (written by the service on start, read by the app's Service menu) ===
    private static string StatusPath => Path.Combine(Directory, "status.json");

    /// <summary>The version + start time the service last recorded — so the interactive app can show
    /// which version is running and when it (re)started, which is how you SEE a self-update land.</summary>
    public sealed class ServiceStatus
    {
        public string? Version { get; set; }
        public DateTime StartedUtc { get; set; }
    }

    public static void SaveStatus(ServiceStatus status)
    {
        try { System.IO.Directory.CreateDirectory(Directory); File.WriteAllText(StatusPath, JsonSerializer.Serialize(status)); }
        catch { /* best-effort */ }
    }

    public static ServiceStatus? LoadStatus()
    {
        try { return File.Exists(StatusPath) ? JsonSerializer.Deserialize<ServiceStatus>(File.ReadAllText(StatusPath)) : null; }
        catch { return null; }
    }

    // === Update log (ALWAYS written, not gated on the service-logging toggle) ===
    // Updates are rare but important, so we always keep a small trail of them where the user can find it.
    public static string UpdateLogPath => Path.Combine(Directory, "update.log");
    private static string UpdatePendingPath => Path.Combine(Directory, "update-pending");

    /// <summary>Append a timestamped line to the service update log. Never throws. Truncates if it ever
    /// grows large (update events are infrequent, so it normally stays tiny).</summary>
    public static void AppendUpdateLog(string line)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            try { if (File.Exists(UpdateLogPath) && new FileInfo(UpdateLogPath).Length > 200_000) File.Delete(UpdateLogPath); } catch { }
            File.AppendAllText(UpdateLogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
        }
        catch { /* best-effort */ }
    }

    /// <summary>Marker dropped when a self-update restart is triggered; the next start consumes it and logs
    /// completion — so the update log shows the update actually finished (and its absence flags a stuck one).</summary>
    public static void SetUpdatePending()
    {
        try { System.IO.Directory.CreateDirectory(Directory); File.WriteAllText(UpdatePendingPath, ""); } catch { }
    }

    public static bool ConsumeUpdatePending()
    {
        try { if (File.Exists(UpdatePendingPath)) { File.Delete(UpdatePendingPath); return true; } } catch { }
        return false;
    }

    private sealed class ServiceSettings { public bool LoggingEnabled { get; set; } }
}
