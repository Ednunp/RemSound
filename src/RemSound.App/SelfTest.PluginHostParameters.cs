using System.Net;
using AudioPlugSharp;
using RemSound.Core;
using RemSound.Plugin;

namespace RemSound.App;

/// <summary>
/// The plugin's parameters, changed the way a HOST changes them. 2026-09-24, the first scripted test in Reaper: "Send
/// this track" switched on from Reaper left the plugin idle, and every switch read "0" in the parameter list OSARA
/// reads aloud. Every earlier test set parameters through EditValue, the way the plugin's own window does - which
/// raises the event the plugin listens for - so none of them could see that the host's way in raises nothing.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>What the gate found at the real plugin pointer before it started, to prove it left it alone.</summary>
    private static (bool Exists, string Text, DateTime WrittenUtc)? realPluginPointerAtStart;

    private static (bool Exists, string Text, DateTime WrittenUtc) ReadRealPluginPointer()
    {
        var path = AppConfig.DefaultPluginPointerPath;
        return File.Exists(path) ? (true, File.ReadAllText(path), File.GetLastWriteTimeUtc(path)) : (false, "", default);
    }

    /// <summary>
    /// A change made in the host's parameter list is acted on, and the switches say "on" and "off".
    /// </summary>
    private static string? PluginActsOnTheHostsParameterChanges()
    {
        using var host = new PluginBridgeHost((IPAddress _, Span<float> _, int _) => 0, port: 0);
        var previousPort = RemSoundPlugin.BridgePortForTest;
        RemSoundPlugin.BridgePortForTest = host.Port;
        // Let the plugin write its log where this run can read it (the throwaway folder, never the real one).
        AppConfig.WritePluginPointer(loggingEnabled: true);
        var plugin = new RemSoundPlugin { Host = new StubAudioHost() };
        try
        {
            plugin.SetMaxAudioBufferSize(512, EAudioBitsPerSample.Bits64);
            plugin.Initialize();
            plugin.Start();
            var send = plugin.Parameters.First(p => p.ID == "send");
            var active = plugin.Parameters.First(p => p.ID == "active");
            var cursor = plugin.Parameters.First(p => p.ID == "peer");

            Check(send.DisplayValue == "off" && active.DisplayValue == "on",
                $"a switch must say whether it is on (send '{send.DisplayValue}', active '{active.DisplayValue}') - they all read \"0\"");
            Check(cursor.DisplayValue == "none", $"the peer cursor at 0 must say it is on nobody (got '{cursor.DisplayValue}')");

            // THE HOST'S WAY IN: exactly what AudioPlugSharp's VST3 controller does when Reaper sets a parameter.
            send.NormalizedEditValue = 1;
            Check(send.DisplayValue == "on", "and the switch must read on once the host has switched it on");
            plugin.WatchParametersForTest();
            Check(plugin.SendEnabled, "THE BUG: Send switched on by the host must make the plugin send - it stayed idle");

            // The watch runs by itself: off again from the host, with nobody calling anything.
            send.NormalizedEditValue = 0;
            Check(WaitFor(() => !plugin.SendEnabled, TimeSpan.FromSeconds(2)), "and the watch must apply a host change by itself, within a second or so");

            // Active is a switch like the others: off from the host takes both directions off.
            send.NormalizedEditValue = 1;
            active.NormalizedEditValue = 0;
            plugin.WatchParametersForTest();
            Check(!plugin.SendEnabled, "Active switched off by the host must stop the sending");
            active.NormalizedEditValue = 1;
            plugin.WatchParametersForTest();
            Check(plugin.SendEnabled, "and on again must bring it back as it was");

            // And SAVED with the project. The host saves each parameter's process value, which a change from its own list
            // does not touch: switched on from Reaper, it worked, and was off again when the project reopened (2026-09-25).
            var saved = plugin.SaveState();
            var reopened = new RemSoundPlugin { Host = new StubAudioHost() };
            try
            {
                reopened.SetMaxAudioBufferSize(512, EAudioBitsPerSample.Bits64);
                reopened.Initialize();
                reopened.Start();
                reopened.RestoreState(saved);
                var reopenedSend = reopened.Parameters.First(p => p.ID == "send");
                Check(reopenedSend.EditValue >= 0.5,
                    $"THE LOSS: Send switched on from the host's parameter list must still be on when the project reopens (it reads {reopenedSend.DisplayValue})");
            }
            finally { try { reopened.CloseForTest(); } catch { /* teardown */ } }

            // The window's way still works, and does not make the watch apply it a second time.
            send.EditValue = 0;
            Check(!plugin.SendEnabled, "a change from the window must still apply at once");

            // Said in the plugin's log.
            var logs = Directory.Exists(AppConfig.LogsDirectory) ? Directory.GetFiles(AppConfig.LogsDirectory, "RemSoundPlugin-*.log") : [];
            var text = string.Join("\n", logs.Select(f => { try { using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); return new StreamReader(s).ReadToEnd(); } catch { return ""; } }));
            Check(text.Contains("parameters: changed by the host, not the window - applying them", StringComparison.Ordinal),
                "the plugin's log must say when it applied a change the host made");
        }
        finally
        {
            try { plugin.CloseForTest(); } catch { /* teardown */ }
            RemSoundPlugin.BridgePortForTest = previousPort;
        }
        return "a switch changed the way Reaper changes it is acted on, at once through the watch and by itself within a second; "
            + "Active from the host stops and restores sending; the window's way still applies at once; switches read on and off, "
            + "the cursor reads none; the log says so";
    }

    /// <summary>
    /// GATE GUARD: no plugin instance in the run talked to the real RemSound port. Several steps used to start a real
    /// instance without pointing it anywhere, and with the person's RemSound open - the usual case - those instances
    /// said hello to it, and one switched sending on and pushed two loud blocks at it, bound for whoever it was
    /// connected to (found 2026-09-24).
    /// </summary>
    private static string? NoPluginInTheRunReachedTheRealApp()
    {
        Check(RemSoundPlugin.BridgePortForTest > 0 && RemSoundPlugin.BridgePortForTest != PluginBridgeProtocol.DefaultPort,
            $"the run's own plugin port must still be in place at the end - a step set it back to the real one ({RemSoundPlugin.BridgePortForTest})");
        Check(RemSoundPlugin.RealPortInitialisationsForTest == 0,
            $"THE LEAK: {RemSoundPlugin.RealPortInitialisationsForTest} plugin instance(s) in this run talked to the real RemSound port {PluginBridgeProtocol.DefaultPort}");
        return $"every plugin instance in the run talked to the run's own listener (port {RemSoundPlugin.BridgePortForTest}) or its step's own host, never the real RemSound";
    }

    /// <summary>
    /// GATE GUARD: nothing in the run told the network this computer is there. The service's presence broadcast its
    /// discovery announcement to the LAN from two steps, claiming a sender at a test port, until 2026-09-24 - every
    /// RemSound on the network saw it come and go.
    /// </summary>
    private static string? NothingInTheRunAnnouncedItself()
    {
        Check(PeerDiscoveryService.NoNetworkForTest, "the run's no-announcements switch must still be on at the end - a step turned it off");
        Check(PeerDiscoveryService.RealStartsForTest == 0,
            $"THE BROADCAST: discovery really opened its sockets {PeerDiscoveryService.RealStartsForTest} time(s) during this run, announcing this computer on the network");
        return "discovery never opened a socket in this run - nothing was announced on the network";
    }

    /// <summary>
    /// GATE GUARD: the gate never touches the real plugin pointer - the file every plugin in every DAW reads to find
    /// RemSound's log folder. On 2026-09-24 a gate run left it pointing at a deleted temp folder with logging off.
    /// </summary>
    private static string? TheGateLeavesTheRealPluginPointerAlone()
    {
        Check(realPluginPointerAtStart is not null, "the pointer was not read at the start of the run");
        Check(!string.Equals(Path.GetFullPath(AppConfig.PluginPointerPath), Path.GetFullPath(AppConfig.DefaultPluginPointerPath), StringComparison.OrdinalIgnoreCase),
            $"a gate run must write its plugin pointer somewhere of its own, not the real one ({AppConfig.PluginPointerPath})");
        var now = ReadRealPluginPointer();
        var before = realPluginPointerAtStart!.Value;
        // What it SAYS is what matters: where the logs are and whether logging is on. The person's own RemSound rewrites
        // the same words every time it starts, and one started during a gate run on 2026-09-24 - a changed timestamp
        // with the same words is theirs, not a clobber.
        Check(now.Exists == before.Exists && now.Text == before.Text,
            $"THE CLOBBER: this run changed the real plugin pointer (before: '{before.Text.Trim()}', now: '{now.Text.Trim()}')");
        if (!before.Exists) return "there is no real plugin pointer, and the run made none";
        return now.WrittenUtc == before.WrittenUtc
            ? "the real plugin pointer is exactly as the run found it"
            : "the real plugin pointer says what it said at the start (it was rewritten with the same words during the run - a RemSound starting does that)";
    }
}
