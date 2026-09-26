using System.Net;
using System.Reflection;
using NAudio.Wave;
using RemSound.Core;
using RemSound.Plugin;
using RemSound.Receiver;
using RemSound.Sender;

namespace RemSound.App;

/// <summary>
/// The audio and plugin faults from the 2026-09-25 sweep (Ed: "yes all 7"), each measured on the real engine where it can
/// be. The plugin's splices have a step of their own (SelfTest.PluginReceiveSplices).
/// </summary>
internal static partial class SelfTest
{
    private static PlayoutEngine EngineFor(AudioConfiguration configuration)
    {
        var engine = new PlayoutEngine(new ReceiverDiagnostics());
        engine.SetIndependentLaneLatency(configuration.HasTwoLanes());
        engine.SetLaneActive(RenderRoute.WasapiLane, configuration.UsesWasapi());
        engine.SetLaneActive(RenderRoute.AsioLane, configuration.UsesAsio());
        engine.SetMaxLatencyMs(RenderRoute.Mixed, 30);
        engine.SetMaxLatencyMs(RenderRoute.WasapiLane, 30);
        engine.SetMaxLatencyMs(RenderRoute.AsioLane, 30);
        return engine;
    }

    /// <summary>
    /// A PEER'S EQ IS NOT SHARED BETWEEN THEIR STREAMS. Every stream from one person ran through ONE filter chain, one
    /// after the other, so each block started from the other stream's filter state - with EQ on, somebody sending their
    /// mic and their DAW track (or WASAPI and ASIO sources) was distorted. Measured: a silent second stream must change
    /// nothing. (Not "together equals each alone, summed": a shared chain is still linear, and passed that.)
    /// </summary>
    private static string? APeersEqIsNotSharedBetweenTheirStreams()
    {
        var peer = new IPEndPoint(IPAddress.Parse("10.9.9.9"), 50000);
        var shaping = new PeerShaping { Enabled = true, EqMode = PeerEqMode.Simple3Band, SimpleBandsDb = [9f, 0f, 9f] };
        var worst = 0.0;
        foreach (var configuration in AudioConfigurations.All)
        {
            float[] Render(bool secondStream)
            {
                var engine = EngineFor(configuration);
                var s1 = engine.GetOrCreateSession(peer, 1, 8 * 1024 * 1024);
                var s2 = secondStream ? engine.GetOrCreateSession(peer, 2, 8 * 1024 * 1024) : null;
                engine.SetPeerDsp(peer.Address, PeerDspChain.Build(shaping, true));
                const int Frames = 480;
                var zero = new byte[Frames * 8];
                var outBytes = new byte[Frames * 8];
                var result = new List<float>();
                long ph = 0;
                for (var block = 0; block < 300; block++)
                {
                    var f1 = new float[Frames * 2];
                    var f2 = new float[Frames * 2];
                    for (var i = 0; i < Frames; i++, ph++)
                    {
                        f1[2 * i] = f1[2 * i + 1] = 0.05f * MathF.Sin(2 * MathF.PI * 150 * ph / 48000f);
                        f2[2 * i] = f2[2 * i + 1] = 0.05f * MathF.Sin(2 * MathF.PI * 5000 * ph / 48000f);
                    }
                    var b1 = new byte[Frames * 8];
                    var b2 = new byte[Frames * 8];
                    Buffer.BlockCopy(f1, 0, b1, 0, b1.Length);
                    Buffer.BlockCopy(f2, 0, b2, 0, b2.Length);
                    s1.Write(b1); s1.NoteFramesQueued(30);
                    if (s2 is not null) { s2.Write(zero); s2.NoteFramesQueued(30); }   // a stream that is there, and silent
                    engine.Read(outBytes, 0, outBytes.Length);
                    if (block >= 100)
                    {
                        var o = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(outBytes.AsSpan());
                        for (var i = 0; i < Frames; i++) result.Add(o[2 * i]);
                    }
                }
                return [.. result];
            }
            // A shared chain is still a linear system, so "together equals each alone, summed" cannot see it. What it does is
            // make a stream's sound depend on the person's OTHER stream: a silent second stream must change nothing.
            var alone = Render(secondStream: false);
            var withSilent = Render(secondStream: true);
            double maxDiff = 0, peak = 0;
            for (var i = 0; i < alone.Length; i++)
            {
                maxDiff = Math.Max(maxDiff, Math.Abs(withSilent[i] - alone[i]));
                peak = Math.Max(peak, Math.Abs(alone[i]));
            }
            Check(peak > 0.02, $"{configuration.Describe()}: premise - the shaped stream must be heard (peak {peak:0.000})");
            Check(maxDiff < 0.001,
                $"{configuration.Describe()}: THE SHARED EQ: a silent second stream from the same person must not change the first one's sound - each needs its own filter state (difference {maxDiff:0.0000} on a {peak:0.000} peak)");
            worst = Math.Max(worst, maxDiff);
        }
        return $"in all three configurations, with EQ on, a silent second stream from the same person left the first one's sound exactly as it was (largest difference {worst:0.000000})";
    }

    /// <summary>
    /// WITH "RECEIVE AUDIO" OFF, THE OUTPUTS STAY CLOSED - the ASIO interface included, which a music program cannot open while
    /// RemSound holds it. Ticking an output, a profile's ticks put back, a driver chosen, a wake: all opened them anyway.
    /// </summary>
    private static string? OutputsStayClosedWithReceiveOff()
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var apply = Require(typeof(MainForm).GetMethod("ApplyReceiveDevices", flags, Type.EmptyTypes), "MainForm.ApplyReceiveDevices() not found");
        var applyStarting = Require(typeof(MainForm).GetMethod("ApplyReceiveDevices", flags, [typeof(bool)]), "MainForm.ApplyReceiveDevices(bool) not found");
        foreach (var configuration in AudioConfigurations.All)
        {
            MainForm? form = null;
            try
            {
                try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
                catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
                SetAudioConfigurationForTest(form, configuration);
                Require(FieldOf<CheckBox>(form, "receiveAudioCheckbox"), "MainForm.receiveAudioCheckbox not found").Checked = false;
                var receiver = form.ReceiverForTest;
                Check(!receiver.IsRunning, $"{configuration.Describe()}: premise - Receive audio is off");
                var before = receiver.SetOutputDevicesCallsForTest;
                apply.Invoke(form, null);
                Check(receiver.SetOutputDevicesCallsForTest == before,
                    $"{configuration.Describe()}: THE CLOSED OUTPUTS: with Receive audio off, applying the ticked outputs must not open them");
                applyStarting.Invoke(form, [true]);
                Check(receiver.SetOutputDevicesCallsForTest == before + 1,
                    $"{configuration.Describe()}: and switching receiving on must open them");
            }
            finally { try { form?.Dispose(); } catch { /* teardown */ } }
        }
        return "in all three configurations, with Receive audio off the ticked outputs were remembered but not opened, and switching receiving on opened them";
    }

    /// <summary>
    /// A RECORDING MOVES ON WHILE NO PEER IS PLAYING. The record hooks fired only on blocks some peer played, so split peer
    /// tracks stopped while your own went on, and every later word landed early against your track; a received-only file
    /// lost the time.
    /// </summary>
    private static string? ARecordingMovesOnWhileNoPeerPlays()
    {
        var peer = new IPEndPoint(IPAddress.Parse("192.168.77.31"), 47830);
        foreach (var configuration in AudioConfigurations.All)
        {
            var engine = EngineFor(configuration);
            var boundaries = 0;
            long recordedFloats = 0;
            engine.OnRecordBlockComplete = _ => boundaries++;
            engine.OnReceivedSamples = (samples, _) => recordedFloats += samples.Length;
            var session = engine.GetOrCreateSession(peer, 1, 4 * 1024 * 1024);
            FillSession(session, 0.1f);                      // a quarter of a second of them
            var buffer = new byte[480 * 8];
            const int Blocks = 60;
            for (var i = 0; i < 20; i++) engine.Read(buffer, 0, buffer.Length);
            // Then they go, as the receiver prunes a peer four seconds after their audio stops: nobody left to play.
            engine.RemoveSession(peer, 1);
            for (var i = 20; i < Blocks; i++) engine.Read(buffer, 0, buffer.Length);
            Check(boundaries == Blocks,
                $"{configuration.Describe()}: THE DRIFT: every block read must move a recording on, played or silent ({boundaries} of {Blocks})");
            Check(recordedFloats == Blocks * 480L * 2,
                $"{configuration.Describe()}: and the mix recorded must be as long as the time that passed ({recordedFloats} of {Blocks * 480L * 2} samples)");
        }
        return "in all three configurations, every block read moved the recording on - silence while nobody was playing - so every track stays as long as the time that passed";
    }

    /// <summary>
    /// A DAW TRACK RECEIVING A PEER KEEPS PLAYING WHEN THE WASAPI OUTPUT STOPS BEING READ, with both kinds of output ticked.
    /// The primary copy is fed only while its own lane reads; the plugin read the primary alone and got nothing.
    /// </summary>
    private static string? APluginTrackPlaysWhenTheWasapiOutputStops()
    {
        long Run(bool wasapiStalls)
        {
            var engine = EngineFor(AudioConfiguration.Both);
            var peer = new IPEndPoint(IPAddress.Parse("10.9.9.9"), 47830);
            var session = engine.GetOrCreateSession(peer, 7, 8 * 1024 * 1024);
            var claims = new PluginPeerClaims();
            claims.Claim(peer.Address, Guid.NewGuid());
            engine.SetPluginPeerClaims(claims);
            var scratch = new byte[480 * 8];
            engine.WasapiLaneOutput.Read(scratch, 0, scratch.Length);
            engine.AsioLaneOutput.Read(scratch, 0, scratch.Length);
            if (wasapiStalls) engine.RewindLaneReadClockForTest(RenderRoute.WasapiLane, 5.0);
            var block = new float[480 * 2];
            Array.Fill(block, 0.5f);
            var bytes = new byte[block.Length * 4];
            Buffer.BlockCopy(block, 0, bytes, 0, bytes.Length);
            var dest = new float[480 * 2];
            long produced = 0;
            for (var n = 0; n < 200; n++)
            {
                session.Write(bytes);
                session.NoteFramesQueued(30);
                engine.AsioLaneOutput.Read(scratch, 0, scratch.Length);
                if (!wasapiStalls) engine.WasapiLaneOutput.Read(scratch, 0, scratch.Length);
                Array.Clear(dest);
                produced += engine.ReadClaimedPeer(peer.Address, dest, 480);
            }
            return produced;
        }
        var reading = Run(wasapiStalls: false);
        var stalled = Run(wasapiStalls: true);
        Check(reading > 50_000, $"premise: with both outputs read, the plugin gets the peer ({reading} frames)");
        Check(stalled > reading / 2,
            $"THE SILENT TRACK: with the WASAPI output no longer read and the ASIO one still going, the DAW track must still get the peer ({stalled} frames, against {reading} with both read)");
        return $"both outputs ticked, a peer on a DAW track: {reading} frames with both read, {stalled} with the WASAPI output stopped";
    }

    /// <summary>
    /// A PLUGIN SET TO RECEIVE SAYS PLAINLY WHEN "RECEIVE AUDIO" IS OFF IN REMSOUND, rather than blaming "audio arriving short".
    /// </summary>
    private static string? APluginSaysWhenReceiveIsOffInRemSound()
    {
        var (plugin, host, previousPort) = PluginWithTwoPeers();
        try
        {
            var receiving = false;
            host.AppReceivingSource = () => receiving;
            plugin.ApplyWindowJob(active: true, send: false, receive: true, [PeerB.ToString()], all: false, 0, 0);
            Check(WaitFor(() => plugin.DescribeStatus().StartsWith("Receive audio is off in RemSound.", StringComparison.Ordinal), TimeSpan.FromSeconds(8)),
                $"THE STATUS: with Receive audio off in RemSound, a plugin set to receive must say so (it says: {plugin.DescribeStatus()})");
            receiving = true;
            Check(WaitFor(() => !plugin.DescribeStatus().Contains("Receive audio is off", StringComparison.Ordinal), TimeSpan.FromSeconds(8)),
                $"and stop saying it once Receive audio is on again (it says: {plugin.DescribeStatus()})");
            receiving = false;
            plugin.ApplyWindowJob(active: true, send: true, receive: true, [PeerB.ToString()], all: false, 0, 0);
            Check(WaitFor(() => plugin.DescribeStatus().Contains("this track is still being sent", StringComparison.Ordinal), TimeSpan.FromSeconds(8)),
                $"while it is sending too, it must say the track is still going out (it says: {plugin.DescribeStatus()})");
        }
        finally
        {
            try { plugin.Stop(); plugin.CloseForTest(); } catch { /* teardown */ }
            host.Dispose();
            RemSoundPlugin.BridgePortForTest = previousPort;
        }
        return "a plugin set to receive said \"Receive audio is off in RemSound\" while it was, stopped saying it when it came back on, and said the track was still being sent when it was";
    }

    /// <summary>
    /// AN ASIO INPUT OR OUTPUT THAT FAILS TO OPEN IS TRIED AGAIN. The output reported the pairs it WANTED as open, so the
    /// heal never saw it missing; the input was tried once. Driven with a driver that does not exist, so it fails every time.
    /// </summary>
    private static string? AFailedAsioOpenIsTriedAgain()
    {
        var restoreRender = AsioRenderBackend.ReopenBackoff;
        var restoreCapture = AsioCaptureBackend.ReopenBackoff;
        try
        {
            AsioRenderBackend.ReopenBackoff = TimeSpan.FromMilliseconds(300);
            AsioCaptureBackend.ReopenBackoff = TimeSpan.FromMilliseconds(300);
            var engine = new PlayoutEngine(new ReceiverDiagnostics());
            using (var output = new AsioRenderBackend("RemSound self-test - no such ASIO driver", engine))
            {
                output.SetOutputDevices([AsioDeviceId.Format(0)]);
                Check(output.OpenAttemptsForTest == 1, "premise: the output is tried");
                Check(output.ActiveDeviceIds.Count == 0,
                    $"THE MISSING OUTPUT: an output that failed to open must not be reported open - the heal can only retry what it sees missing (reported: {string.Join(", ", output.ActiveDeviceIds)})");
                output.SetOutputDevices([AsioDeviceId.Format(0)]);
                Check(output.OpenAttemptsForTest == 1, "tried moments ago: not tried again yet");
                Thread.Sleep(400);
                output.SetOutputDevices([AsioDeviceId.Format(0)]);
                Check(output.OpenAttemptsForTest == 2, "THE RETRY: after the back-off, the heal's next ask must try the output again");
            }
            using (var input = new AsioCaptureBackend("RemSound self-test - no such ASIO driver", _ => { }))
            {
                input.Start([new CaptureSourceSpec(AsioDeviceId.Format(0), CaptureKind.Input, "self-test")]);
                Check(input.OpenAttemptsForTest == 1 && !input.IsRunning, "premise: the input is tried and fails");
                input.HealSources();
                Check(input.OpenAttemptsForTest == 1, "tried moments ago: the heal waits");
                Thread.Sleep(400);
                input.HealSources();
                Check(input.OpenAttemptsForTest == 2, "THE RETRY: after the back-off, the heal must try the input again");
                input.Stop();
                Thread.Sleep(400);
                input.HealSources();
                Check(input.OpenAttemptsForTest == 2, "and never once it was stopped on purpose");
            }
        }
        finally
        {
            AsioRenderBackend.ReopenBackoff = restoreRender;
            AsioCaptureBackend.ReopenBackoff = restoreCapture;
        }
        return "an output that failed to open was reported missing and tried again after the back-off, not before; an input the same, "
             + "through the heal; and nothing was tried once stopped on purpose";
    }
}
