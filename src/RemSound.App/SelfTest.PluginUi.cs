using System.Net;
using RemSound.Core;
using RemSound.Plugin;
using RemSound.Receiver;

namespace RemSound.App;

/// <summary>
/// The plugin's own window, and the two rate conversions either side of it.
///
/// <para>The window is the accessibility bet: it is an ordinary WinForms panel precisely so a screen
/// reader can read it, which most plugin windows cannot manage. That property is fragile in a way
/// that is invisible to anyone testing by eye — a list that rebuilds itself every second reads
/// perfectly in a screenshot and is unusable with NVDA, because every rebuild re-announces the list
/// and throws away where the user was in it.</para>
///
/// <para>The rate conversion is the other silent failure: a host at 44.1 kHz with no resampling
/// transposes everyone by a semitone and a bit. SonoBus ships with exactly that bug open. It would
/// arrive as "my friend sounds strange", which is a long way from "the sample rate is wrong".</para>
/// </summary>
internal static partial class SelfTest
{
    private static string? PluginWindowWiring()
    {
        using var panel = new PluginEditorPanel();

        var peers = new List<(string, string)> { ("192.168.1.50", "Andre"), ("192.168.1.51", "Chris") };
        panel.PeerSource = () => peers;
        var status = "Not connected.";
        panel.StatusSource = () => status;

        (bool Sending, string? Peer)? lastJob = null;
        panel.JobChanged += (sending, peer) => lastJob = (sending, peer);

        // --- The peer list comes from the app, with NAMES -----------------------------------------
        panel.Refresh(fromTimer: false);
        Check(panel.PeerItemCountForTest == 2, $"the peer list must be filled from the app ({panel.PeerItemCountForTest} items)");
        Check(panel.PeerLabelsForTest.SequenceEqual(new[] { "Andre", "Chris" }),
            "peers must be shown by NAME — a list of IP addresses is no use to somebody choosing who to put on a track");

        // THE SCREEN-READER RULE: refreshing must not rebuild an unchanged list. A rebuild
        // re-announces every item and moves the user's place — the control would be unusable, and
        // nothing about it would look wrong to a sighted tester.
        panel.SelectPeerForTest(1);
        panel.Refresh(fromTimer: true);
        panel.Refresh(fromTimer: true);
        Check(panel.SelectedPeerIndexForTest == 1,
            "a refresh with an unchanged list must leave the user exactly where they were");

        // ...but a real change must land, and must keep the user on the same PERSON if they are
        // still there, rather than on the same row number.
        peers.Insert(0, ("192.168.1.49", "Jonathan"));
        panel.Refresh(fromTimer: true);
        Check(panel.PeerItemCountForTest == 3, "a peer appearing in RemSound must appear here without reopening the plugin");
        Check(panel.ChosenPeerAddress == "192.168.1.51",
            $"the user must stay on the same PERSON when the list changes, not the same row (landed on {panel.ChosenPeerAddress})");

        // --- One person per track: the job the user picked must reach the engine -------------------
        panel.SelectJobForTest(1);   // receive
        Check(lastJob is { Sending: false }, "choosing 'receive' must tell the engine to receive");
        Check(lastJob?.Peer == "192.168.1.51", $"...and which peer (got {lastJob?.Peer})");
        Check(panel.PeerListEnabledForTest, "the peer chooser must be usable when receiving");

        panel.SelectJobForTest(0);   // send
        Check(lastJob is { Sending: true, Peer: null },
            "choosing 'send' must release the peer — otherwise they stay mute in RemSound with the plugin no longer playing them");
        Check(!panel.PeerListEnabledForTest,
            "the peer chooser must be disabled, not hidden, when sending — hiding it would shift the tab order under a screen-reader user mid-session");

        // --- Active is the user's own bypass ------------------------------------------------------
        panel.SelectJobForTest(1);
        Check(lastJob is { Sending: false }, "back to receiving");
        panel.SetActiveForTest(false);
        Check(lastJob is { Sending: true, Peer: null },
            "unticking Active must hand the peer back to RemSound's speakers, not leave them playing nowhere");
        panel.SetActiveForTest(true);
        Check(lastJob?.Peer == "192.168.1.51", "re-ticking Active must take the peer back");

        // --- The status line must say what is true, including the awkward cases -------------------
        status = "Receiving Andre onto this track.";
        panel.Refresh(fromTimer: true);
        Check(panel.StatusTextForTest.Contains("Receiving Andre"), "the status line must show what the engine reports");

        return "peer list arrives from the app with names; an unchanged refresh never moves the user; a changed list keeps them on the same person; "
             + "job and peer reach the engine; Active works as a bypass that returns the peer";
    }

    /// <summary>The plugin has to actually BE in this copy of RemSound, and be complete.
    ///
    /// <para>The install menu copies a <c>plugin\</c> folder next to the exe into the user's own VST3
    /// folder. If the build ever stops producing that folder, every other plugin test still passes —
    /// they drive throwaway directories — and the shipped app quietly reports that it has no plugin to
    /// install. That is precisely the class of gap Ed has been caught by before: built is not shipped.</para>
    ///
    /// <para>The named files are the ones whose absence is silent. A .vst3 with no
    /// <c>.runtimeconfig.json</c> beside it does not fail loudly — the DAW just doesn't list it, and
    /// the user is left believing the install did nothing.</para></summary>
    private static string? PluginPayloadPresent()
    {
        var folder = PluginInstaller.SourceDirectory;
        Check(Directory.Exists(folder),
            $"this build has no plugin folder ({folder}) - 'Install plugin' would find nothing to install");

        var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetFileName(f)).ToList();

        Check(files.Any(f => f.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase)),
            "there must be a .vst3 in the plugin folder - that IS the plugin");
        Check(files.Any(f => f.Equals("RemSound.Plugin.dll", StringComparison.OrdinalIgnoreCase)),
            "the managed plugin assembly must ship - the .vst3 is only a loader for it");
        Check(files.Any(f => f.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)),
            "the runtimeconfig must ship beside the .vst3 - without it the DAW simply never lists the plugin, with no error to explain why");
        Check(files.Any(f => f.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)),
            "the deps.json must ship - it is how the loader finds the managed assemblies");
        Check(files.Any(f => f.Equals("Ijwhost.dll", StringComparison.OrdinalIgnoreCase)),
            "Ijwhost.dll must ship - it is the shim that starts the .NET runtime inside the DAW");
        Check(files.Any(f => f.Equals("RemSound.Ui.dll", StringComparison.OrdinalIgnoreCase)),
            "the shared accessible controls must ship - they are the entire reason the plugin window can be read by a screen reader");

        // And the real installer must succeed FROM this real folder, into a throwaway target. Testing
        // the installer only against invented files would never catch a payload that cannot be copied.
        var target = Path.Combine(Path.GetTempPath(), "remsound-plugin-payload-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (ok, message) = PluginInstaller.InstallForTest(folder, target);
            Check(ok, $"installing the REAL plugin folder must succeed: {message}");
            Check(PluginInstaller.IsInstalledAt(target), "...and be detected as installed afterwards");
            var placed = Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length;
            Check(placed >= files.Count, $"every file must arrive ({placed} placed, {files.Count} in the folder plus its manifest)");
        }
        finally
        {
            try { if (Directory.Exists(target)) Directory.Delete(target, recursive: true); } catch { }
        }

        return $"the plugin ships with this build ({files.Count} files incl. the .vst3, its runtimeconfig, deps, Ijwhost and the accessible controls) "
             + "and the real installer copies it whole";
    }

    /// <summary>Both rate conversions, driven with a real tone and measured — because "it ran" is not
    /// evidence that a resampler is correct, and the failure it guards against (everyone a semitone
    /// sharp) arrives as "my friend sounds strange", not as an error.</summary>
    private static string? PluginRateConversion()
    {
        const double HostRate = 44100.0;
        const double ToneHz = 1000.0;
        const int HostBlock = 512;

        // --- Out of the DAW: host rate -> 48k wire -------------------------------------------------
        var captured = new List<float>();
        var capture = new HostCaptureBackend(samples => captured.AddRange(samples.Span.ToArray()));
        capture.PrepareForBlockSize(HostBlock, HostRate);
        capture.Start([]);

        var left = new double[HostBlock];
        var right = new double[HostBlock];
        var phase = 0.0;
        var step = 2 * Math.PI * ToneHz / HostRate;
        for (var block = 0; block < 200; block++)
        {
            for (var i = 0; i < HostBlock; i++) { left[i] = right[i] = Math.Sin(phase); phase += step; }
            capture.SubmitHostBlock(left, right);
        }
        capture.Stop();

        Check(captured.Count > 0, "the DAW's block must reach the wire at all");
        // A 44.1k host produces MORE 48k frames than it consumed. If this came out equal, the
        // resampler was skipped and the far end would hear everyone sharp.
        var wireFrames = captured.Count / 2;
        var expectedWire = 200 * HostBlock * (48000.0 / HostRate);
        Check(Math.Abs(wireFrames - expectedWire) / expectedWire < 0.02,
            $"a 44.1k host must produce ~{expectedWire:0} wire frames, not {wireFrames} — a mismatch here IS the transposition bug");
        var capturedHz = DominantHz(captured, 48000, stride: 2);
        Check(Math.Abs(capturedHz - ToneHz) < 25,
            $"a 1 kHz tone from a 44.1k DAW must still be 1 kHz on the wire (measured {capturedHz:0} Hz)");

        // --- Into the DAW: 48k wire -> host rate ----------------------------------------------------
        // Driven through the REAL bridge, so this measures the whole receive path rather than the
        // resampler in isolation.
        var peer = new IPEndPoint(IPAddress.Parse("192.168.1.50"), 47830);
        var engine = new PlayoutEngine(new ReceiverDiagnostics());
        engine.SetLaneActive(RenderRoute.WasapiLane, true);
        engine.SetMaxLatencyMs(RenderRoute.Mixed, 30);
        var session = engine.GetOrCreateSession(peer, 1, 8 * 1024 * 1024);

        // Feed the tone in AS IT IS CONSUMED, the way a real peer sends it. Dumping four seconds in
        // at once would leave the session massively over-buffered against its 30 ms target, and the
        // drift corrector would do exactly what it is supposed to — race to drain it — which changes
        // the pitch on purpose and would make this measure the wrong thing entirely.
        var tone = new ToneStream(ToneHz, 0.5f);
        tone.WriteInto(session, frames: 48000 / 5);   // 200 ms head start, near the target depth
        session.NoteFramesQueued(30);

        using var host = new PluginBridgeHost(engine.ReadClaimedPeer, port: 0);
        engine.SetPluginPeerClaims(host.Claims);
        using var client = new PluginBridgeClient(host.Port);
        client.ReceiveFrom(peer.Address);

        var render = new PeerRenderBridge(client);
        render.PrepareForBlockSize(HostBlock, HostRate);

        var outLeft = new double[HostBlock];
        var outRight = new double[HostBlock];
        var received = new List<float>();
        var wirePerBlock = (int)Math.Ceiling(HostBlock * 48000.0 / HostRate);
        for (var block = 0; block < 300; block++)
        {
            tone.WriteInto(session, wirePerBlock);     // the peer keeps sending
            var filled = render.FillHostBlock(outLeft, outRight);
            // Skip the first blocks: the ring is still filling, and a partly-empty block is silence
            // followed by audio, which would read as a spurious zero crossing.
            if (block > 20) for (var i = 0; i < filled; i++) received.Add((float)outLeft[i]);
            Thread.Sleep(2);
        }

        Check(received.Count > HostBlock, $"the peer's audio must reach the DAW track at the host's rate ({received.Count} frames)");
        var receivedHz = DominantHz(received, (int)HostRate, stride: 1);
        Check(Math.Abs(receivedHz - ToneHz) < 25,
            $"a 1 kHz peer must still be 1 kHz on a 44.1k DAW track (measured {receivedHz:0} Hz) — this is SonoBus's open bug, avoided");

        return $"44.1k DAW -> 48k wire: {wireFrames} frames, tone held at {capturedHz:0} Hz; "
             + $"48k wire -> 44.1k DAW track through the real bridge: tone held at {receivedHz:0} Hz";
    }


    /// <summary>A continuous tone written into a session in real-time-sized pieces, phase carried
    /// across the writes so the result is one unbroken sine rather than a series of restarts (which
    /// would put a discontinuity at every seam and ruin any pitch measurement).</summary>
    private sealed class ToneStream(double toneHz, float amplitude)
    {
        private long frame;

        public void WriteInto(Receiver.SessionPlayout session, int frames)
        {
            var block = new byte[frames * 2 * sizeof(float)];
            var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(block.AsSpan());
            var step = 2 * Math.PI * toneHz / 48000;
            for (var i = 0; i < frames; i++)
            {
                var v = (float)(Math.Sin(frame * step) * amplitude);
                floats[i * 2] = v;
                floats[i * 2 + 1] = v;
                frame++;
            }
            session.Write(block);
        }
    }

    /// <summary>Frequency by zero crossings. Crude, and entirely sufficient: the failure being
    /// guarded against shifts the pitch by nearly 9%, not by a hair.</summary>
    private static double DominantHz(IReadOnlyList<float> samples, int sampleRate, int stride)
    {
        var crossings = 0;
        var counted = 0;
        var previous = 0f;
        for (var i = 0; i < samples.Count; i += stride)
        {
            var value = samples[i];
            if (counted > 0 && ((previous < 0 && value >= 0) || (previous >= 0 && value < 0))) crossings++;
            previous = value;
            counted++;
        }
        if (counted < 2) return 0;
        // Two crossings per cycle.
        return crossings / 2.0 * sampleRate / counted;
    }
}
