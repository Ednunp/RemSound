using System.ComponentModel;
using System.Net;
using NAudio.Wave;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// The 2026-09-13 review, stage 2 continued: things that were changed without a trace, files that could be torn, logs
/// that buried themselves, and a few rules written down twice.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>
    /// SETTINGS CHANGED IN A DIALOG ARE ALL RECORDED, AND THE CHECKS NEVER TOUCH THIS PC'S START-UP ENTRY.
    ///
    /// <para>2026-09-13 review (Dialogs #3, #4). Accept remote volume, Start RemSound automatically, a cue turned off or put
    /// back to its built-in sound, logging on and off, and Recording settings all changed things with nothing in the log —
    /// and the dialog suite stayed green because it only watched the machine-wide configuration. The same suite ticked
    /// "Start RemSound automatically" for real, rewriting the start-up entry of whichever PC ran the gate.</para>
    ///
    /// <para>The dialog suite now watches the profile settings and the start-up entry too, so it holds every one of those
    /// controls to saying what it changed. This step checks the two that suite cannot drive, and the stand-in itself.</para>
    /// </summary>
    private static string? AuditDialogChangesAreRecorded()
    {
        var realBefore = StartupAutoStart.RealRegistryValueForTest();
        var restore = StartupAutoStart.UseMemoryForTest;
        StartupAutoStart.UseMemoryForTest = true;
        try
        {
            StartupAutoStart.MemoryValueForTest = null;
            Check(!StartupAutoStart.IsEnabled, "the stand-in starts with no start-up entry");
            Check(StartupAutoStart.TryEnable(@"C:\Programs\RemSound\RemSound.exe") && StartupAutoStart.IsEnabled,
                "turning start-up on must set the stand-in entry");
            Check(StartupAutoStart.TryDisableIfPointsInto(@"C:\Programs\RemSound2") && StartupAutoStart.IsEnabled,
                "removing a different folder's install must leave the entry alone");
            Check(StartupAutoStart.TryDisableIfPointsInto(@"C:\Programs\RemSound") && !StartupAutoStart.IsEnabled,
                "removing this folder's install must clear the entry pointing into it");
            Check(StartupAutoStart.TryEnable(@"C:\Other\RemSound.exe") && StartupAutoStart.TryDisable() && !StartupAutoStart.IsEnabled,
                "turning start-up off must clear the stand-in entry");
            Check(StartupAutoStart.RealRegistryValueForTest() == realBefore,
                "none of that may touch this PC's real start-up entry");
        }
        finally
        {
            StartupAutoStart.UseMemoryForTest = restore;
            StartupAutoStart.MemoryValueForTest = null;
        }

        var root = FindSourceRoot();
        if (root is null) return Skip("the stand-in works, but the source tree is not reachable (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var main = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "MainForm.cs"));
        Check(SourceMethodBody(main, "private void OpenRecordingSettingsDialog()").Contains("LogUiChange(\"recording settings\"", StringComparison.Ordinal),
            "Recording settings must record what OK changed — the dialog suite cannot see it, because nothing is saved until OK");
        var prefs = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "PreferencesDialog.cs"));
        var off = prefs.IndexOf("if (!loggingBox.Checked) UiChangeLog.Record(\"logging\", \"off\");", StringComparison.Ordinal);
        var apply = prefs.IndexOf("applyLoggingEnabled(loggingBox.Checked);", StringComparison.Ordinal);
        var on = prefs.IndexOf("if (loggingBox.Checked) UiChangeLog.Record(\"logging\", \"on\");", StringComparison.Ordinal);
        Check(off >= 0 && apply > off && on > apply,
            "logging must be recorded while the log is open: before it goes off, after it comes on — the other way round the line is lost");
        return "the start-up stand-in behaves and leaves the real entry alone; Recording settings and logging record their changes";
    }

    /// <summary>
    /// THE LONG-RUN REPORT COUNTS FRAMES SENT ON A SEND-ONLY MACHINE.
    ///
    /// <para>2026-09-13 review (MainForm #4). The send-only branch of the per-second line read the frames-sent counter, which
    /// resets as it is read, and never passed it on, so the long-run report said "send frames=0/s" on every machine that
    /// only sends.</para>
    /// </summary>
    private static string? AuditLongRunCountsSendOnlyFrames()
    {
        var root = FindSourceRoot();
        if (root is null) return Skip("the source tree is not reachable from here (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var main = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "MainForm.cs"));
        Check(CountOccurrences(main, "NoteLongRunSendFrames(sndAudFr);") == 1,
            "the send-only branch must hand the frames it read to the long-run report");
        Check(SourceMethodBody(main, "private void NoteLongRunSendFrames(long framesSent)").Length > 0
              || main.Contains("private void NoteLongRunSendFrames(long framesSent) => longRunSendFrames += framesSent;", StringComparison.Ordinal),
            "the frames must land in the same total the report divides");
        return "the send-only branch feeds the long-run send frames";
    }

    /// <summary>
    /// RE-OPENING AN OUTPUT FOLLOWS THE ONE PINNED RULE.
    ///
    /// <para>2026-09-13 review (MainForm #8). The re-open decision is a pure rule the gate pins, and the per-second heal
    /// restated it inline, so the pinned rule and the one that ran could drift apart unnoticed.</para>
    /// </summary>
    private static string? AuditOutputReopenUsesTheRule()
    {
        var root = FindSourceRoot();
        if (root is null) return Skip("the source tree is not reachable from here (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var main = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "MainForm.cs"));
        var heal = SourceMethodBody(main, "private void HealFaultedOutputsIfAny()");
        Check(heal.Contains("ShouldReopenOutputs(", StringComparison.Ordinal),
            "the per-second heal must decide through ShouldReopenOutputs, the rule the gate pins");
        return "the heal decides through the pinned rule";
    }

    /// <summary>
    /// THE PLUGIN LOG LINE HAS ONE WRITER.
    ///
    /// <para>2026-09-13 review (MainForm #2.4). The gate's seam for the plugin link's once-a-second line was a copy of the
    /// block the per-second tick runs, so the gate checked the copy.</para>
    /// </summary>
    private static string? AuditPluginLogLineHasOneWriter()
    {
        var root = FindSourceRoot();
        if (root is null) return Skip("the source tree is not reachable from here (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var main = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "MainForm.cs"));
        Check(CountOccurrences(main, "logFile.Event($\"vst plugin: {line}\")") == 1,
            "the plugin summary line must be written in exactly one place");
        Check(main.Contains("internal void WritePluginLogLineForTest() => WritePluginLogLine();", StringComparison.Ordinal),
            "the gate's seam must call the writer the app uses, not carry its own copy");
        Check(CountOccurrences(main, "WritePluginLogLine();") >= 2,
            "the per-second tick must call the same writer");
        return "one writer, called by the tick and by the gate";
    }

    /// <summary>
    /// THE STATUS LINE GIVES NO SHARED BUFFER FIGURE.
    ///
    /// <para>2026-09-13 review (MainForm #13). The status line read out one shared buffer and target, which is only true with
    /// a single output lane. The jitter buffer boxes and the Total latency box give those figures per lane.</para>
    /// </summary>
    private static string? AuditStatusLineHasNoSharedBuffer()
    {
        var root = FindSourceRoot();
        if (root is null) return Skip("the source tree is not reachable from here (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var status = SourceMethodBody(File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "MainForm.cs")), "private void UpdateStatus()");
        Check(status.Length > 0, "UpdateStatus must still exist to be checked");
        Check(!status.Contains("CurrentBufferMs", StringComparison.Ordinal) && !status.Contains("TargetLatencyMs", StringComparison.Ordinal),
            "the status line must not read out one shared buffer or target");
        return "the status line leaves buffer figures to the per-lane boxes";
    }

    /// <summary>
    /// THE SERVICE DIALOG STORES NOTHING FOR LOCK TO AUDIO CLOCK.
    ///
    /// <para>2026-09-13 review (Dialogs #10). It set a profile flag that nothing reads: the service always locks to the audio
    /// clock.</para>
    /// </summary>
    private static string? AuditServiceDialogStoresNoLockToClock()
    {
        var root = FindSourceRoot();
        if (root is null) return Skip("the source tree is not reachable from here (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var dialog = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "ServiceProfileDialog.cs"));
        Check(!dialog.Contains("TightLatencyMode = ", StringComparison.Ordinal),
            "the service dialog must not store a lock-to-clock flag that nothing reads");
        return "no dead flag stored";
    }

    /// <summary>
    /// A FAILED SERVICE QUERY READS AS NOT INSTALLED ONLY WHEN IT ISN'T.
    ///
    /// <para>2026-09-13 review (Dialogs #11). Every failed status query counted as "not installed", access denied included,
    /// which offered to install a service that was already there. And two comments said start/stop rights went to every
    /// signed-in user, when the code grants them to the installing user only.</para>
    /// </summary>
    private static string? AuditServiceQueryFailureMapping()
    {
        Check(ServiceControl.StateForQueryFailure(new InvalidOperationException("no such service", new Win32Exception(1060))) == ServiceState.NotInstalled,
            "a service that does not exist is not installed");
        Check(ServiceControl.StateForQueryFailure(new InvalidOperationException("denied", new Win32Exception(5))) == ServiceState.Unknown,
            "access denied says nothing about whether the service is installed");
        Check(ServiceControl.StateForQueryFailure(new InvalidOperationException("no detail")) == ServiceState.Unknown,
            "a failure with no reason is not a verdict");
        Check(ServiceControl.StateForQueryFailure(new TimeoutException()) == ServiceState.Unknown,
            "any other failure is unknown");

        var root = FindSourceRoot();
        if (root is null) return Skip("the rule holds, but the source tree is not reachable (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var control = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "ServiceControl.cs"));
        Check(!control.Contains("authenticated users", StringComparison.OrdinalIgnoreCase),
            "no comment may say every signed-in user gets start/stop rights — only the installing user does, and it is a security claim");
        Check(SourceMethodBody(control, "public static ServiceState Query()").Contains("StateForQueryFailure(", StringComparison.Ordinal),
            "the status query must map its failures through the rule above");
        return "only a missing service reads as not installed";
    }

    /// <summary>
    /// PROFILE AND SERVICE FILES ARE WRITTEN CRASH-SAFELY.
    ///
    /// <para>2026-09-13 review (MainForm #4.3, Dialogs #8, Core #22). Renaming a profile, saving to a chosen path, locking
    /// it, changing its password and the password manager all rewrote the file in place, and so did every service file. A
    /// crash or forced close mid-write left a truncated file: a profile that loads blank, a service with no profile.</para>
    /// </summary>
    private static string? AuditProfileWritesAreCrashSafe()
    {
        var dir = Path.Combine(Path.GetTempPath(), "remsound-atomic-" + Guid.NewGuid().ToString("N"));
        var savedOverride = ServiceStore.TestDirectoryOverride;
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "Studio.json");
            ProfileStore.WriteProfileFile(path, new Profile { Title = "Studio", Volume = 37 });
            ProfileStore.WriteProfileFile(path, new Profile { Title = "Studio", Volume = 64 });
            var back = System.Text.Json.JsonSerializer.Deserialize<Profile>(File.ReadAllText(path));
            Check(back is { Volume: 64 }, "a profile written to a chosen path must read back whole, with the latest values");
            Check(!Directory.EnumerateFiles(dir, "*.tmp").Any(), "a finished profile write must leave no temporary file behind");

            ServiceStore.TestDirectoryOverride = Path.Combine(dir, "service");
            ServiceStore.SaveProfile(new Profile { Title = "Service" });
            ServiceStore.SaveStartupVolume(true, 55, bootOnly: true);
            ServiceStore.SaveStartupVolume(true, 60, bootOnly: false);
            Check(ServiceStore.LoadProfile() is { Title: "Service" }, "the service profile must read back whole");
            Check(ServiceStore.LoadStartupVolume() == (true, 60, false), "the service settings must read back whole, with the latest values");
            Check(!Directory.EnumerateFiles(ServiceStore.Directory, "*.tmp").Any(), "a finished service write must leave no temporary file behind");
        }
        finally
        {
            ServiceStore.TestDirectoryOverride = savedOverride;
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }

        var root = FindSourceRoot();
        if (root is null) return Skip("the writes are whole, but the source tree is not reachable (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        foreach (var file in new[] { "MainForm.cs", "ProfilePasswordManagerDialog.cs" })
        {
            var text = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", file));
            Check(!text.Contains("File.WriteAllText(", StringComparison.Ordinal),
                $"{file} must write profiles through ProfileStore's crash-safe write, not File.WriteAllText");
        }
        var store = File.ReadAllText(Path.Combine(root, "src", "RemSound.Core", "ServiceStore.cs"));
        Check(CountOccurrences(store, "File.WriteAllText(") == 1,
            "every whole file the service keeps must go through its crash-safe write — File.WriteAllText may appear only inside it");
        return "profile and service files are written to a temporary file and moved into place";
    }

    /// <summary>
    /// HEARTBEATS LOG FAILURES, NOT EVERY PING.
    ///
    /// <para>2026-09-13 review (Core #22). The heartbeat wrote a line for every ping sent, every ping received and every
    /// pong — about three lines a second for each peer, burying everything else in the log. Only what is worth reading
    /// stays: a failed send, and a pong from an address nobody is tracking.</para>
    /// </summary>
    private static string? AuditHeartbeatLogsOnlyWhatMatters()
    {
        var lines = new List<string>();
        using var hb = new HeartbeatService(line => { lock (lines) lines.Add(line); });
        byte[]? sent = null;
        hb.SendTransport = (buf, len, _) => { sent = buf.AsSpan(0, len).ToArray(); return true; };

        var peer = new IPEndPoint(IPAddress.Parse("10.9.9.9"), 47830);
        hb.SetTrackedPeers([peer]);

        var ping = new byte[RemPacket.HeaderSize + RemPacket.HeartbeatPayloadSize];
        RemPacket.WriteHeader(ping, RemPacketType.Heartbeat, 0xFFFF, 1);
        RemPacket.WriteHeartbeatPayload(ping.AsSpan(RemPacket.HeaderSize), HeartbeatKind.Ping, 1000);
        hb.HandleInjectedPacket(ping, ping.Length, new IPEndPoint(peer.Address, 51000));
        Check(sent is not null, "premise: a ping must still be answered");

        var pong = new byte[RemPacket.HeaderSize + RemPacket.HeartbeatPayloadSize];
        RemPacket.WriteHeader(pong, RemPacketType.Heartbeat, 0xFFFF, 2);
        RemPacket.WriteHeartbeatPayload(pong.AsSpan(RemPacket.HeaderSize), HeartbeatKind.Pong, 0);
        hb.HandleInjectedPacket(pong, pong.Length, new IPEndPoint(peer.Address, 51000));
        lock (lines)
            Check(lines.Count == 0, $"an ordinary ping and pong must write nothing to the log (wrote: {string.Join(" / ", lines)})");

        hb.HandleInjectedPacket(pong, pong.Length, new IPEndPoint(IPAddress.Parse("10.1.2.3"), 51000));
        lock (lines)
            Check(lines.Count == 1 && lines[0].Contains("matched no tracked peer", StringComparison.Ordinal),
                $"a pong from an address nobody tracks must still be logged (wrote: {string.Join(" / ", lines)})");
        return "ordinary heartbeats are silent; a stray pong is logged";
    }

    /// <summary>
    /// THE PLUGIN LINK FORGETS A DAW ONCE ITS LAST PLUGIN GOES.
    ///
    /// <para>2026-09-13 review (Core #19). The link kept which process each DAW was, and a block counter per DAW, forever:
    /// every DAW ever opened stayed in memory, and a new program that happened to get an old process id joined the old
    /// DAW's group.</para>
    /// </summary>
    private static string? AuditPluginBridgeForgetsFinishedDaws()
    {
        using var host = new PluginBridgeHost(null, port: 0);
        host.HelloForTest(1, 4242);
        host.HelloForTest(2, 4242);          // a second plugin in the same DAW
        host.StartBlockCountForTest(1);
        Check(host.GroupBookkeepingForTest == (1, 1), $"premise: one DAW with a block count (got {host.GroupBookkeepingForTest})");

        host.GoodbyeForTest(1);
        Check(host.GroupBookkeepingForTest == (1, 1),
            "a DAW with a plugin still open must keep its bookkeeping — removing it would split one DAW's plugins apart");

        host.GoodbyeForTest(2);
        Check(host.GroupBookkeepingForTest == (0, 0),
            $"when a DAW's last plugin goes, its process record and block count must go too (left {host.GroupBookkeepingForTest})");

        host.HelloForTest(3, 4242);          // a new program that got the same process id
        Check(host.GroupBookkeepingForTest == (1, 0), "a new program reusing the id starts fresh");
        return "a DAW's records last exactly as long as its plugins";
    }

    /// <summary>
    /// THE POST-DECODE STEP PROBE IS FED FOR OPUS STREAMS.
    ///
    /// <para>2026-09-13 review (Receiver #9). The probe that shows where a click entered the chain was fed only on the PCM
    /// path, so its columns read 0 for every Opus stream — the codec most people send.</para>
    /// </summary>
    private static string? AuditOpusStepProbeIsFed()
    {
        var root = FindSourceRoot();
        if (root is null) return Skip("the source tree is not reachable from here (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var session = File.ReadAllText(Path.Combine(root, "src", "RemSound.Receiver", "StreamSession.cs"));
        var emit = SourceMethodBody(session, "private void EmitDecoded(ReadOnlySpan<short> shortScratch, int sampleCountPerChannel)");
        Check(emit.Length > 0, "EmitDecoded must still exist to be checked");
        Check(emit.Contains("postDecodeStepProbe.ScanStereo(", StringComparison.Ordinal),
            "decoded Opus audio must pass through the post-decode step probe, as PCM does");
        return "Opus feeds the post-decode probe";
    }

    /// <summary>
    /// TIGHT-LATENCY CAPTURE READS INTEGER PCM.
    ///
    /// <para>2026-09-13 review (Sender #10). With tight latency on and a single input, capture that delivered integer PCM
    /// stopped at once and stayed stopped — not marked faulted, so nothing tried again — while the class promised a fallback
    /// that did not exist. It now converts 16, 24 and 32-bit integer PCM, plain or extensible.</para>
    /// </summary>
    private static string? AuditPushModeReadsIntegerCapture()
    {
        Check(RemSound.Sender.PushModeWasapiBackend.SampleKindOf(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)) == (true, 32), "32-bit float");
        foreach (var bits in new[] { 16, 24, 32 })
            Check(RemSound.Sender.PushModeWasapiBackend.SampleKindOf(new WaveFormat(48000, bits, 2)) == (false, bits), $"{bits}-bit integer PCM");
        Check(RemSound.Sender.PushModeWasapiBackend.SampleKindOf(new WaveFormatExtensible(48000, 24, 2)) == (false, 24), "24-bit extensible PCM");
        Check(RemSound.Sender.PushModeWasapiBackend.SampleKindOf(new WaveFormatExtensible(48000, 32, 2)) == (true, 32), "32-bit extensible float");
        Check(RemSound.Sender.PushModeWasapiBackend.SampleKindOf(new WaveFormat(48000, 8, 2)) is null, "8-bit PCM is not readable here, and must say so");

        var dst = new float[4];
        var pcm16 = new byte[6];
        BitConverter.GetBytes((short)32767).CopyTo(pcm16, 2);
        BitConverter.GetBytes(short.MinValue).CopyTo(pcm16, 4);
        Check(RemSound.Sender.PushModeWasapiBackend.ConvertToFloat(pcm16, 6, false, 16, dst) == 3
              && dst[0] == 0f && Math.Abs(dst[1] - 32767f / 32768f) < 1e-6 && dst[2] == -1f,
            $"16-bit samples must scale to -1..1 (got {dst[0]}, {dst[1]}, {dst[2]})");

        var pcm24 = new byte[] { 0xFF, 0xFF, 0x7F, 0x00, 0x00, 0x80 };
        Check(RemSound.Sender.PushModeWasapiBackend.ConvertToFloat(pcm24, 6, false, 24, dst) == 2
              && Math.Abs(dst[0] - 8388607f / 8388608f) < 1e-6 && dst[1] == -1f,
            $"24-bit samples must scale to -1..1, sign included (got {dst[0]}, {dst[1]})");

        var pcm32 = BitConverter.GetBytes(int.MinValue);
        Check(RemSound.Sender.PushModeWasapiBackend.ConvertToFloat(pcm32, 4, false, 32, dst) == 1 && dst[0] == -1f,
            $"32-bit integer samples must scale to -1..1 (got {dst[0]})");

        var floats = new byte[8];
        BitConverter.GetBytes(0.25f).CopyTo(floats, 0);
        BitConverter.GetBytes(-0.5f).CopyTo(floats, 4);
        Check(RemSound.Sender.PushModeWasapiBackend.ConvertToFloat(floats, 8, true, 32, dst) == 2 && dst[0] == 0.25f && dst[1] == -0.5f,
            "float samples must come through unchanged");
        return "float and 16/24/32-bit integer capture are read; anything else is refused with a diagnostic";
    }
}
