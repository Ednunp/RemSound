using Microsoft.Win32;

namespace RemSound.App;

/// <summary>
/// Wires "run RemSound automatically when this user logs in" via the per-user Run
/// registry key at <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>. This is the
/// same mechanism many Windows apps use for "launch on login"; the entry shows up in Task
/// Manager's Startup tab so the user can also toggle it off there if they ever want to.
///
/// Why HKCU\...\Run rather than dropping a .lnk into the Startup folder:
///   - No COM interop / IShellLink wrangling; just RegistryKey.SetValue.
///   - User-scoped (HKCU): no admin elevation needed and only affects this user.
///   - Manageable via Task Manager → Startup, which is where Windows users now expect to
///     find login-launched apps.
///
/// All methods catch all exceptions and return success bools — flipping the toggle in the
/// Startup behaviour dialog should never throw, even on policy-locked machines.
/// </summary>
internal static class StartupAutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RemSound";

    /// <summary>True when an entry called "RemSound" exists under the per-user Run key.
    /// Reads the registry each call (cheap; single key open + value read). Never throws.</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                if (key is null) return false;
                var value = key.GetValue(ValueName) as string;
                return !string.IsNullOrWhiteSpace(value);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Add or update the Run-key entry to point at the currently-running exe.
    /// Quotes the path so spaces work. Returns true on success.</summary>
    public static bool TryEnable()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            // Fallback: use AppContext.BaseDirectory. .NET hosting produces a
            // different process path for self-contained vs framework-dependent
            // publish, but BaseDirectory is reliable.
            exePath = System.IO.Path.Combine(AppContext.BaseDirectory, "RemSound.exe");
        }
        return TryEnable(exePath);
    }

    /// <summary>Add or update the Run-key entry to point at a specific exe path. Used by the
    /// self-installer to re-point login-launch at the freshly INSTALLED copy while it's still
    /// running from the portable one, so auto-start doesn't keep launching the old folder.
    /// Quotes the path so spaces work. Returns true on success.</summary>
    public static bool TryEnable(string exePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(exePath)) return false;
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return false;
            // Wrap in double-quotes so a path containing spaces (e.g. C:\Program Files\)
            // parses correctly when Windows launches it.
            key.SetValue(ValueName, $"\"{exePath}\"");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Remove the Run-key entry. Returns true if the entry is gone afterwards
    /// (whether we deleted it or it never existed). Returns false only on registry
    /// access errors.</summary>
    public static bool TryDisable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return true; // No Run subkey at all → nothing to disable.
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Remove the Run-key entry ONLY if it currently points at an exe inside
    /// <paramref name="folder"/>. Used by the uninstaller so removing an INSTALLED copy never wipes a
    /// DIFFERENT copy's autostart entry (e.g. a portable copy the user still wants launching at login)
    /// that happens to share the single "RemSound" value name. Returns true if the entry is gone
    /// afterwards or was left alone because it points elsewhere; false only on registry error.</summary>
    public static bool TryDisableIfPointsInto(string folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder)) return false;
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return true; // No Run subkey → nothing to disable.
            var value = (key.GetValue(ValueName) as string)?.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(value)) return true; // nothing set for us.
            var target = System.IO.Path.GetFullPath(folder)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            // Only our own installed copy's entry (its exe lives inside the folder being removed).
            if (value.StartsWith(target, StringComparison.OrdinalIgnoreCase))
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
