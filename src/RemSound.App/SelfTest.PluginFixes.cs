using System.Collections.Concurrent;
using System.Net;
using AudioPlugSharp;
using RemSound.Core;
using RemSound.Plugin;

namespace RemSound.App;

/// <summary>
/// The DAW plugin's five fixes of 2026-09-25 (review; Ed: "fix all 5"): big buffers, a change of buffer size, Active in the
/// window, a plugin removed from its track, and the parameter watch's thread. Each driven through the real plugin, the
/// real link and a real RemSound end of it on a port of its own - never the real port.
/// </summary>
internal static partial class SelfTest
{
    private static readonly IPAddress PeerA = IPAddress.Parse("192.0.2.51");
    private static readonly IPAddress PeerB = IPAddress.Parse("192.0.2.52");

    /// <summary>A continuing ramp standing in for a peer's audio: each frame one step on from the last, both channels
    /// alike, so a block with anything missing, repeated or out of order shows it.</summary>
    private sealed class RampPeer
    {
        private long next;
        public const float Step = 1f / 65536f;
        public int Read(IPAddress _, Span<float> destination, int frames)
        {
            for (var i = 0; i < frames; i++)
            {
                var v = (next++ % 65536) * Step;
                destination[i * 2] = v;
                destination[i * 2 + 1] = v;
            }
            return frames;
        }

        /// <summary>True when the first <paramref name="frames"/> frames each go up one step from the one before (the ramp
        /// may wrap once).</summary>
        public static bool Continuous(ReadOnlySpan<float> interleaved, int frames)
        {
            for (var i = 1; i < frames; i++)
            {
                var d = interleaved[i * 2] - interleaved[(i - 1) * 2];
                var wrapped = interleaved[(i - 1) * 2] > 0.99f && interleaved[i * 2] < 0.01f;
                if (!wrapped && Math.Abs(d - Step) > Step / 4) return false;
                if (interleaved[i * 2 + 1] != interleaved[i * 2]) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// A DAW BUFFER OF 4096 OR MORE REACHES THE TRACK WHOLE, BOTH WAYS.
    ///
    /// <para>One link message carries at most 4096 frames, and nothing split a bigger block: at a buffer of 4096 (4460
    /// frames at 44.1 kHz, once it is 48 kHz) the person chosen never arrived on the track, and the track never reached
    /// them. Now a block goes in parts. Through the real link, both ends, on a port of its own.</para>
    /// </summary>
    private static string? ABigDawBufferReachesTheTrackWhole()
    {
        var peer = new RampPeer();
        using var host = new PluginBridgeHost(peer.Read, port: 0);
        var sent = new ConcurrentQueue<float[]>();
        host.TrackAudioReceived += (_, audio) => sent.Enqueue(audio.ToArray());
        using var client = new PluginBridgeClient(host.Port);
        client.Hello();
        client.SetReceivedPeers([PeerA]);

        // App to track: the block the DAW asks for comes back whole and in order, however big.
        foreach (var frames in new[] { 4096, 4460, 8192, 16384 })
        {
            var buffer = new float[frames * 2];
            var whole = false;
            for (var attempt = 0; attempt < 60 && !whole; attempt++)
            {
                var got = client.ReadPeerBlock(buffer, frames);
                whole = got == frames && RampPeer.Continuous(buffer, frames);
                if (!whole) Thread.Sleep(15);
            }
            Check(whole, $"THE SILENT TRACK: a DAW block of {frames} frames must arrive whole and in order - it never arrived at all");
        }

        // Track to app: a big block goes out as consecutive pieces, and all of it arrives, in order.
        var outgoing = new float[8920 * 2];
        for (var i = 0; i < 8920; i++) { outgoing[i * 2] = i * RampPeer.Step; outgoing[i * 2 + 1] = i * RampPeer.Step; }
        Check(client.SendTrackBlock(outgoing), "a track block of 8920 frames (4096 at 44.1 kHz, doubled) must be sent, not refused");
        Check(WaitFor(() => sent.Sum(b => b.Length) >= outgoing.Length, TimeSpan.FromSeconds(3)),
            $"THE UNHEARD TRACK: all of a big block must reach RemSound ({sent.Sum(b => b.Length)} of {outgoing.Length} samples)");
        var joined = sent.SelectMany(b => b).ToArray();
        Check(joined.AsSpan(0, outgoing.Length).SequenceEqual(outgoing), "and in order, exactly as the DAW sent it");

        // And while the app is busy: the biggest block a DAW can send, with the app held up on its first piece, as a busy
        // machine holds it up. The rest waits in the socket, and has to fit there.
        {
            using var hold = new ManualResetEventSlim(false);
            var busyPeer = new RampPeer();
            using var busyHost = new PluginBridgeHost(busyPeer.Read, port: 0);
            var got = new ConcurrentQueue<float[]>();
            var first = 0;
            busyHost.TrackAudioReceived += (_, audio) =>
            {
                got.Enqueue(audio.ToArray());
                if (Interlocked.Increment(ref first) == 1) hold.Wait(TimeSpan.FromSeconds(2));
            };
            using var busyClient = new PluginBridgeClient(busyHost.Port);
            busyClient.Hello();
            busyClient.SetReceivedPeers([PeerA]);
            Check(WaitFor(() => busyHost.InstanceCount > 0, TimeSpan.FromSeconds(3)), "premise: the busy app knows the plugin");
            var biggest = new float[PluginBridgeProtocol.MaxBlockFrames * 2];
            for (var i = 0; i < biggest.Length; i++) biggest[i] = i;
            Check(busyClient.SendTrackBlock(biggest), "the biggest block must be sent");
            Thread.Sleep(300);
            hold.Set();
            Check(WaitFor(() => got.Sum(b => b.Length) >= biggest.Length, TimeSpan.FromSeconds(3)),
                $"THE OVERFLOW: with the app busy for a moment, the biggest block must still all arrive - the socket must hold it ({got.Sum(b => b.Length)} of {biggest.Length} samples)");
        }

        // Through the DAW's send bus, which sums the tracks and used to drop anything past one message.
        PluginSendBus.ResetForTest();
        var busId = Guid.NewGuid();
        try
        {
            while (sent.TryDequeue(out _)) { }
            PluginSendBus.Register(busId, client);
            PluginSendBus.Submit(busId, outgoing);
            Check(WaitFor(() => sent.Sum(b => b.Length) >= outgoing.Length, TimeSpan.FromSeconds(3)),
                $"and the send bus must pass a big block on, not drop it ({sent.Sum(b => b.Length)} of {outgoing.Length})");
        }
        finally { PluginSendBus.Unregister(busId); PluginSendBus.ResetForTest(); }

        // A block whose parts do not all arrive is dropped whole, never played with a hole in it. A stand-in app end sends
        // the first part of one block, skips the rest, then a whole block.
        var fake = new PluginBridgeLink(0);
        IPEndPoint? plugin = null;
        var pluginHash = 0;
        fake.MessageReceived += (type, hash, _, _, from) => { if (type == PluginBridgeMessage.Hello) { plugin = from; pluginHash = hash; } };
        try
        {
            using var partial = new PluginBridgeClient(fake.Port);
            partial.Hello();
            Check(WaitFor(() => plugin is not null, TimeSpan.FromSeconds(3)), "premise: the stand-in app end must hear the plugin");
            var part = PluginBridgeProtocol.AudioRoundPartFloats;
            void SendPart(long round, int offset, int total, float value)
            {
                var n = Math.Min(part, total - offset);
                var payload = new byte[PluginBridgeProtocol.AudioRoundHeaderSize + n * sizeof(float)];
                PluginBridgeProtocol.WriteAudioRoundHeader(payload, round, -1, offset, total);
                var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(payload.AsSpan(PluginBridgeProtocol.AudioRoundHeaderSize));
                samples.Fill(value);
                fake.Send(plugin!, PluginBridgeMessage.PeerAudioRound, pluginHash, null, payload);
            }
            var total = part + 4000;
            SendPart(10, 0, total, 0.25f);                 // the start of a block whose end never comes
            SendPart(11, 0, total, 0.75f);                 // then a whole block
            SendPart(11, part, total, 0.75f);
            var buffer = new float[total];
            Check(WaitFor(() => partial.AbandonedBlocks == 1, TimeSpan.FromSeconds(3)),
                $"a block missing a part must be abandoned ({partial.AbandonedBlocks})");
            var got = partial.ReadPeerBlock(buffer, total / 2);
            Check(got == total / 2 && buffer.All(v => v == 0.75f),
                "THE HOLE: what is played must be the whole block that arrived, never the one with a part missing");
            // A stray later part of a block whose start never came, then a whole block: the stray is never played. First
            // forty whole big blocks, played as they come, so every one of the plugin's reusable slots has grown big enough
            // to take a stray part without complaint - which is when a stray could pass for a block.
            for (var r = 20; r < 60; r++)
            {
                SendPart(r, 0, total, 0.3f);
                SendPart(r, part, total, 0.3f);
                Thread.Sleep(5);
                partial.ReadPeerBlock(buffer, total / 2);
            }
            Thread.Sleep(200);
            while (partial.ReadPeerBlock(buffer, total / 2) > 0) { }   // nothing left queued
            SendPart(100, part, total, 0.5f);
            SendPart(101, 0, total, 0.9f);
            SendPart(101, part, total, 0.9f);
            Thread.Sleep(300);
            Array.Clear(buffer);
            got = partial.ReadPeerBlock(buffer, total / 2);
            Check(got == total / 2 && buffer.All(v => v == 0.9f),
                "THE HOLE: a part arriving without the start of its block must never become a block of its own");
        }
        finally { fake.Dispose(); }
        return "blocks of 4096, 4460, 8192 and 16384 frames arrive whole and in order; a big track block reaches RemSound, "
             + "through the send bus too; a block missing a part is dropped, not played with a hole";
    }

    /// <summary>
    /// THE PLUGIN PLAYS EVERY BLOCK, WHATEVER SIZE THE HOST HANDS IT.
    ///
    /// <para>The first block after any change of block size was silenced, the track's own sound included: a click at every
    /// change, crackle in a host that changes it all the time, and the first fifth of a second cut from anything Audacity
    /// applied the plugin to (measured 2026-09-25). The real plugin, a size that changes on every call, at 48 and 44.1
    /// kHz, sending as well, and the track must come through every block.</para>
    /// </summary>
    private static string? ThePluginPlaysEveryBlockWhateverItsSize()
    {
        using var host = new PluginBridgeHost((IPAddress _, Span<float> _, int _) => 0, port: 0);
        var previousPort = RemSoundPlugin.BridgePortForTest;
        RemSoundPlugin.BridgePortForTest = host.Port;
        try
        {
            foreach (var rate in new[] { 48000.0, 44100.0 })
            {
                var plugin = new RemSoundPlugin { Host = new StubAudioHost { SampleRate = rate, MaxAudioBufferSize = 4096 } };
                try
                {
                    plugin.SetMaxAudioBufferSize(4096, EAudioBitsPerSample.Bits64);
                    plugin.Initialize();
                    plugin.Start();
                    plugin.SetJobForTest(send: true, receive: false, peer: null);   // the send path resizes too
                    var input = (AudioIOPortManaged)plugin.InputPorts[0];
                    var output = (AudioIOPortManaged)plugin.OutputPorts[0];
                    // Then a host that announces a bigger block than it said at the start, without starting the plugin
                    // again: the buffers must grow, and that block must still play.
                    var sizes = new List<int> { 512, 256, 1024, 64, 4096, 100, 2048, 1, 512, 441 };
                    foreach (var size in sizes.Concat([8192, 512]))
                    {
                        if (size == 8192) plugin.SetMaxAudioBufferSize(8192, EAudioBitsPerSample.Bits64);
                        input = (AudioIOPortManaged)plugin.InputPorts[0];
                        output = (AudioIOPortManaged)plugin.OutputPorts[0];
                        input.SetCurrentBufferSize((uint)size);
                        output.SetCurrentBufferSize((uint)size);
                        var left = input.GetAudioBuffer(0);
                        var right = input.GetAudioBuffer(1);
                        for (var i = 0; i < size; i++) { left[i] = 0.5; right[i] = -0.5; }
                        plugin.Process();
                        var outLeft = output.GetAudioBuffer(0);
                        var outRight = output.GetAudioBuffer(1);
                        var through = 0;
                        for (var i = 0; i < size; i++) if (Math.Abs(outLeft[i] - 0.5) < 1e-6 && Math.Abs(outRight[i] + 0.5) < 1e-6) through++;
                        Check(through == size,
                            $"THE CLICK: at {rate:0} Hz a block of {size} frames, after a block of another size, must pass the track through "
                          + $"- {size - through} of its {size} samples were silenced");
                    }
                }
                finally { plugin.Stop(); plugin.CloseForTest(); }
            }
            return "at 48 and 44.1 kHz, sending as well, a block size that changes on every call - 512, 256, 1024, 64, 4096, 100, "
                 + "2048, 1, 512, 441 - passes the track through every block, the first included, and so does a block bigger than "
                 + "the host first announced";
        }
        finally { RemSoundPlugin.BridgePortForTest = previousPort; }
    }

    /// <summary>A real plugin on a RemSound end of its own that offers two people, ready once it knows them.</summary>
    private static (RemSoundPlugin Plugin, PluginBridgeHost Host, int PreviousPort) PluginWithTwoPeers(StubAudioHost? daw = null)
    {
        var host = new PluginBridgeHost((IPAddress _, Span<float> _, int _) => 0, port: 0)
        {
            PeerListSource = () => [(PeerA, "Andre"), (PeerB, "Chris")],
        };
        var previousPort = RemSoundPlugin.BridgePortForTest;
        RemSoundPlugin.BridgePortForTest = host.Port;
        var plugin = new RemSoundPlugin { Host = daw ?? new StubAudioHost() };
        plugin.SetMaxAudioBufferSize(512, EAudioBitsPerSample.Bits64);
        plugin.Initialize();
        plugin.Start();
        Check(WaitFor(() => plugin.SortedPeersForTest.Count == 2, TimeSpan.FromSeconds(5)),
            $"premise: the plugin must learn the two people RemSound offers ({plugin.SortedPeersForTest.Count})");
        return (plugin, host, previousPort);
    }

    /// <summary>
    /// THE PLUGIN KEEPS ITS CHOICES WHEN ACTIVE OR RECEIVE IS SWITCHED OFF.
    ///
    /// <para>Unticking Active in the plugin's window reported "nobody, neither direction", and the plugin kept that as the
    /// choice: close the window, and who was on the track, and Send and Receive, were gone for good, while Active showed
    /// ticked again. Driven through the plugin's own handling of what the window reports, and its saved project.</para>
    /// </summary>
    private static string? ThePluginKeepsItsChoicesWhenSwitchedOff()
    {
        var (plugin, host, previousPort) = PluginWithTwoPeers();
        try
        {
            string[] chris = [PeerB.ToString()];
            plugin.ApplyWindowJob(active: true, send: true, receive: true, chris, all: false, 0, 0);
            Check(plugin.SendEnabled && plugin.ReceiveEnabled && plugin.ChosenPeersForTest.Contains(PeerB),
                "premise: sending and receiving Chris");

            // Active off in the window: nothing runs, nothing is forgotten.
            plugin.ApplyWindowJob(active: false, send: true, receive: true, chris, all: false, 0, 0);
            Check(!plugin.SendEnabled && !plugin.ReceiveEnabled, "with Active off neither direction may run");
            var shown = plugin.WindowInitialState();
            Check(!shown.Active && shown.Send && shown.Receive && shown.Peers.Contains(PeerB.ToString()),
                $"THE FORGOTTEN TRACK: reopened, the window must show Active off with Send, Receive and Chris still chosen "
              + $"(active {shown.Active}, send {shown.Send}, receive {shown.Receive}, people {string.Join(",", shown.Peers)})");

            // Saved with the project and reopened, the same.
            var saved = plugin.SaveState();
            var reopened = new RemSoundPlugin { Host = new StubAudioHost() };
            try
            {
                reopened.SetMaxAudioBufferSize(512, EAudioBitsPerSample.Bits64);
                reopened.Initialize();
                reopened.Start();
                reopened.RestoreState(saved);
                var back = reopened.WindowInitialState();
                Check(!back.Active && back.Send && back.Receive && back.Peers.Contains(PeerB.ToString()),
                    "and a project saved like that must reopen the same way");
                Check(!reopened.SendEnabled && !reopened.ReceiveEnabled, "still not running");
            }
            finally { try { reopened.Stop(); reopened.CloseForTest(); } catch { /* teardown */ } }

            // Active on again: exactly as it was.
            plugin.ApplyWindowJob(active: true, send: true, receive: true, chris, all: false, 0, 0);
            Check(plugin.SendEnabled && plugin.ReceiveEnabled && plugin.ChosenPeersForTest.Contains(PeerB),
                "switched on again, it must be exactly as it was, Chris included");

            // Receive off on its own: Chris is let go, but still chosen.
            plugin.ApplyWindowJob(active: true, send: true, receive: false, chris, all: false, 0, 0);
            Check(!plugin.ReceiveEnabled && plugin.SendEnabled, "Receive off stops receiving, and only that");
            var receiveOff = plugin.WindowInitialState();
            Check(!receiveOff.Receive && receiveOff.Peers.Contains(PeerB.ToString()),
                "and reopened, the window must still show Chris chosen, ready for Receive to go back on");
            return "Active off stops both directions and keeps who and which; the window and a saved project show them; Active "
                 + "on puts everything back; Receive off keeps who was chosen";
        }
        finally
        {
            try { plugin.Stop(); plugin.CloseForTest(); } catch { /* teardown */ }
            host.Dispose();
            RemSoundPlugin.BridgePortForTest = previousPort;
        }
    }

    /// <summary>
    /// A PLUGIN REMOVED FROM ITS TRACK STOPS TAKING PART.
    ///
    /// <para>The last call a removed plugin gets is the host's Stop; the loader does not pass the host's terminate on. Its
    /// timers kept saying hello every two seconds, so RemSound listed it, and its log wrote a line a second, until the DAW
    /// closed. The real plugin on a RemSound end of its own: stopped, RemSound forgets it at once and hears nothing more;
    /// started again, it is back.</para>
    /// </summary>
    private static string? APluginRemovedFromItsTrackStopsTakingPart()
    {
        var (plugin, host, previousPort) = PluginWithTwoPeers();
        try
        {
            Check(WaitFor(() => host.InstanceCount == 1, TimeSpan.FromSeconds(3)), "premise: RemSound knows the plugin");
            plugin.Stop();
            Check(WaitFor(() => host.InstanceCount == 0, TimeSpan.FromSeconds(2)),
                "THE LINGERER: once the host stops the plugin - the last thing a removed plugin hears - RemSound must forget it at once");
            Thread.Sleep(2600);   // longer than the plugin's two-second hello
            Check(host.InstanceCount == 0, "and nothing from it may bring it back while it is stopped: its timers must stand down");
            plugin.Start();
            Check(WaitFor(() => host.InstanceCount == 1, TimeSpan.FromSeconds(3)), "started again, RemSound must know it again");
            Thread.Sleep(2600);
            Check(host.InstanceCount == 1, "and its hello keeps it known, as before");
            return "stopped, RemSound forgets the plugin at once and hears nothing more from it; started, it is back and stays";
        }
        finally
        {
            try { plugin.Stop(); plugin.CloseForTest(); } catch { /* teardown */ }
            host.Dispose();
            RemSoundPlugin.BridgePortForTest = previousPort;
        }
    }

    /// <summary>
    /// THE PARAMETER WATCH NEVER CALLS THE HOST FROM ITS OWN THREAD.
    ///
    /// <para>Moving the peer cursor from the DAW's parameter list, OSARA or automation is applied by the parameter watch, on
    /// a timer thread, and republishing the tick under the cursor called the host's BeginEdit, PerformEdit and EndEdit
    /// there. VST3 allows those only on the host's UI thread. A stand-in host records every edit call and its thread.</para>
    /// </summary>
    private static string? TheParameterWatchNeverCallsTheHostFromItsThread()
    {
        var daw = new StubAudioHost();
        var (plugin, host, previousPort) = PluginWithTwoPeers(daw);
        try
        {
            plugin.ApplyWindowJob(active: true, send: false, receive: true, [PeerB.ToString()], all: false, 0, 0);
            var cursor = plugin.Parameters.First(p => p.ID == "peer");
            var tick = plugin.Parameters.First(p => p.ID == "peerinclude");
            var chrisAt = plugin.SortedPeersForTest.ToList().FindIndex(p => p.Address.Equals(PeerB)) + 1;
            var andreAt = plugin.SortedPeersForTest.ToList().FindIndex(p => p.Address.Equals(PeerA)) + 1;
            while (daw.Edits.TryDequeue(out _)) { }

            // The host moves the cursor its own way, and the watch applies it - as its timer does, on a thread marked as its own.
            cursor.NormalizedEditValue = cursor.GetValueNormalized(chrisAt);
            plugin.WatchParametersAsTheTimerForTest();
            Check(tick.EditValue >= 0.5, "premise: on Chris the tick must read ticked - the readout still has to be right");
            Check(daw.Edits.IsEmpty,
                $"THE WRONG THREAD: republishing the tick from the watch must not call the host's edit methods ({daw.Edits.Count} calls from thread {Environment.CurrentManagedThreadId})");
            cursor.NormalizedEditValue = cursor.GetValueNormalized(andreAt);
            plugin.WatchParametersAsTheTimerForTest();
            Check(tick.EditValue < 0.5 && daw.Edits.IsEmpty, "on Andre, not ticked, and still no call to the host");

            // And the real timer, left to itself.
            cursor.NormalizedEditValue = cursor.GetValueNormalized(chrisAt);
            Check(WaitFor(() => tick.EditValue >= 0.5, TimeSpan.FromSeconds(2)), "the watch's own timer must pick the move up");
            Check(daw.Edits.IsEmpty, $"and it too must not call the host ({daw.Edits.Count} calls)");
            return "a cursor moved by the host is applied and the tick under it republished, from the watch and from its real "
                 + "timer, without a single edit call to the host from that thread";
        }
        finally
        {
            try { plugin.Stop(); plugin.CloseForTest(); } catch { /* teardown */ }
            host.Dispose();
            RemSoundPlugin.BridgePortForTest = previousPort;
        }
    }
}
