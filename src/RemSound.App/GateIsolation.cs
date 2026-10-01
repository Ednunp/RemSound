using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// THE GATE'S OWN COPIES (Ed, 2026-09-28: a full gate run paused his desktop service for a couple of minutes). A copy that
/// run-tests.ps1 starts carries <see cref="Variable"/>: it listens on that port, and the next one for the DAW plugin link,
/// instead of the real 47830 and 47831, and it never takes the token that makes the lock-screen service stand aside - so a
/// service someone has installed keeps sending while the gate runs, and nothing the gate starts can take its packets. A
/// value that is, or would put the plugin link on, one of the real ports is refused (the copy then runs as normal).
/// </summary>
internal static class GateIsolation
{
    public const string Variable = "REMSOUND_GATE_PORT";

    /// <summary>The two ports a gate copy uses, or null when the value is missing, not a port, or touches a real one.</summary>
    public static (int Audio, int PluginLink)? Read(string? value)
    {
        if (!int.TryParse(value, out var port) || port < 1024 || port > 65534) return null;
        int[] real = [RemPacket.DefaultPort, PluginBridgeProtocol.DefaultPort, PeerDiscoveryService.DefaultDiscoveryPort];
        if (real.Contains(port) || real.Contains(port + 1)) return null;
        return (port, port + 1);
    }

    /// <summary>The line a gate copy logs at start, so the gate can see it was isolated.</summary>
    public static string Describe((int Audio, int PluginLink) ports) =>
        $"gate copy: listening on {ports.Audio} (plugin link {ports.PluginLink}), not the real ports; the lock-screen service is not asked to stand aside";
}
