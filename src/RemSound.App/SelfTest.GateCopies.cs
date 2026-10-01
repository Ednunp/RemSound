using RemSound.Core;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>GATE COPIES (Ed, 2026-09-28: a full gate run paused his desktop service's stream for a couple of minutes, because
    /// the copies it starts took the token that makes the lock-screen service stand aside). A copy carrying REMSOUND_GATE_PORT
    /// listens on that port and the next, never on a real one, and does not take the token; run-tests.ps1 gives every copy it
    /// starts one, and its cold start connects to that copy's own port rather than the real one.</summary>
    private static string? GateCopiesLeaveTheServiceAlone()
    {
        Check(GateIsolation.Read("49200") == (49200, 49201), "a free port must give the audio port and the next one for the plugin link");
        foreach (var bad in new[] { null, "", "abc", "80", "70000", "47830", "47829", "47831", "47821", "47820" })
            Check(GateIsolation.Read(bad) is null, $"\"{bad}\" must be refused - not a port, or it touches a real one");
        var root = FindSourceRoot();
        if (root is null) return Skip("the ports are decided right, but the source tree is not reachable to check the start-up and the gate script (set REMSOUND_SOURCE_ROOT)");
        var program = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "Program.cs"));
        Check(program.Contains("using var presenceHold = gatePorts is null ? InteractivePresence.AcquireHold() : null;", StringComparison.Ordinal),
            "a gate copy must not take the token that makes the lock-screen service stand aside");
        Check(program.Contains("MainForm.LocalAudioPortForTest = gp.Audio;", StringComparison.Ordinal)
              && program.Contains("MainForm.PluginLinkPortForTest = gp.PluginLink;", StringComparison.Ordinal),
            "and must listen on its own ports");
        var gate = File.ReadAllText(Path.Combine(root, "run-tests.ps1"));
        var set = gate.IndexOf("$env:REMSOUND_GATE_PORT = \"$gatePort\"", StringComparison.Ordinal);
        var cold = gate.IndexOf("Stage \"Cold start and clean close", StringComparison.Ordinal);
        Check(set > 0 && cold > set, "run-tests.ps1 must give its copies their own port before the first one starts");
        Check(gate.Contains("'--connect', \"127.0.0.1:$gatePort\"", StringComparison.Ordinal) && !gate.Contains("'--connect', '127.0.0.1',", StringComparison.Ordinal),
            "and its cold start must connect to that copy's own port, not the real one");
        return "a gate copy listens on its own two ports and leaves the service's token alone; the gate script gives every copy one";
    }
}
