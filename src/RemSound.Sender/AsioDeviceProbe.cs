using Microsoft.Win32;
using NAudio.Wave;

namespace RemSound.Sender;

/// <summary>
/// Public static helpers for the App layer to enumerate ASIO drivers and probe their channel
/// counts and channel names without needing access to the internal
/// <see cref="AsioCaptureBackend"/> / <see cref="AsioRenderBackend"/> implementation classes.
/// These are read-only queries: opening the driver briefly to read its info, then closing —
/// does NOT claim the device for streaming.
/// </summary>
public static class AsioDeviceProbe
{
    /// <summary>
    /// Names of all installed ASIO drivers. Tries several enumeration paths and merges results,
    /// because:
    ///   • NAudio's built-in <c>AsioOut.GetDriverNames()</c> reads <c>HKLM\SOFTWARE\ASIO</c> in
    ///     the registry view that matches the calling process. A 64-bit RemSound only sees the
    ///     64-bit hive; some ASIO drivers register only into the 32-bit <c>Wow6432Node</c> hive.
    ///   • A few drivers register under HKCU instead of HKLM.
    /// We scan both views and both hives, merge results (case-insensitive de-dup on the
    /// registry key name and the human-friendly Description), and return the descriptions.
    /// </summary>
    public static IReadOnlyList<string> EnumerateDriverNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var n in AsioOut.GetDriverNames()) names.Add(n);
        }
        catch { /* ignore — fall through to manual scan */ }

        // Manual scan covers cases NAudio's built-in helper misses.
        AddFromRegistry(RegistryHive.LocalMachine, RegistryView.Registry64, names);
        AddFromRegistry(RegistryHive.LocalMachine, RegistryView.Registry32, names);
        AddFromRegistry(RegistryHive.CurrentUser, RegistryView.Registry64, names);
        AddFromRegistry(RegistryHive.CurrentUser, RegistryView.Registry32, names);

        return names.ToList();
    }

    private static void AddFromRegistry(RegistryHive hive, RegistryView view, HashSet<string> names)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var asioKey = baseKey.OpenSubKey(@"SOFTWARE\ASIO");
            if (asioKey is null) return;
            foreach (var subKeyName in asioKey.GetSubKeyNames())
            {
                using var sub = asioKey.OpenSubKey(subKeyName);
                if (sub is null) continue;
                // Most drivers store a friendly "Description" value; if absent, the subkey name
                // itself is what NAudio uses.
                var description = sub.GetValue("Description") as string;
                names.Add(string.IsNullOrWhiteSpace(description) ? subKeyName : description);
            }
        }
        catch { /* ignore — that hive/view combo unavailable, fine */ }
    }

    /// <summary>
    /// Probes the named ASIO driver for full info (channel counts + per-channel names). Briefly
    /// opens the driver, reads metadata, disposes. Returns a result with empty arrays + −1
    /// counts on any failure.
    /// </summary>
    public static AsioDriverProbeResult ProbeDriverInfo(string driverName)
    {
        try
        {
            using var asio = new AsioOut(driverName);
            var inCount = asio.DriverInputChannelCount;
            var outCount = asio.DriverOutputChannelCount;
            var inNames = new List<string>(Math.Max(0, inCount));
            for (var i = 0; i < inCount; i++)
            {
                try { inNames.Add(asio.AsioInputChannelName(i)); }
                catch { inNames.Add($"Input {i + 1}"); }
            }
            var outNames = new List<string>(Math.Max(0, outCount));
            for (var i = 0; i < outCount; i++)
            {
                try { outNames.Add(asio.AsioOutputChannelName(i)); }
                catch { outNames.Add($"Output {i + 1}"); }
            }
            return new AsioDriverProbeResult(inCount, outCount, inNames, outNames);
        }
        catch
        {
            return new AsioDriverProbeResult(-1, -1, [], []);
        }
    }
}

public sealed record AsioDriverProbeResult(
    int InputChannelCount,
    int OutputChannelCount,
    IReadOnlyList<string> InputChannelNames,
    IReadOnlyList<string> OutputChannelNames);
