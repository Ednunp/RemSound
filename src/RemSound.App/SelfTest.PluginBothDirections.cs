using System.Net;
using AudioPlugSharp;
using RemSound.Core;
using RemSound.Plugin;

namespace RemSound.App;

/// <summary>
/// ONE INSTANCE, BOTH DIRECTIONS — and the rule that stops it feeding itself.
///
/// <para>Until 2026-09-01 an instance did one job or the other, and the stated reason was that this
/// made feedback impossible: a receiving instance never sends, so it cannot send back what it just
/// received. Allowing both at once removes that argument, so it has to be replaced by a stronger one
/// rather than deleted. The replacement is: <c>Process</c> hands the app the track's INPUT and never
/// its output. What leaves cannot contain what arrived, whichever switches are on.</para>
///
/// <para>That is a claim about audio, so it is tested as one. A real plugin instance, a real bridge
/// host with a real peer feeding it a known level, and then a look at what actually crossed the link
/// — not at a flag saying it should have been fine.</para>
/// </summary>
internal static partial class SelfTest
{
    private const double TrackLevel = 0.25;
    private const float PeerLevel = 0.5f;

    private static string PluginBothDirectionsAtOnce()
    {
        PluginSendBus.ResetForTest();

        // A peer who is always sending, at a level nothing else in this test uses, so anything that
        // leaks it into the send direction is unmistakable.
        int ReadPeer(IPAddress peer, Span<float> destination, int frames)
        {
            var floats = Math.Min(destination.Length, frames * PluginBridgeProtocol.WireChannels);
            for (var i = 0; i < floats; i++) destination[i] = PeerLevel;
            return floats / PluginBridgeProtocol.WireChannels;
        }

        // Port 0, never the real one: a gate run must not disturb a RemSound the user has open.
        using var host = new PluginBridgeHost(ReadPeer, port: 0);
        var sentBlocks = new List<float[]>();
        host.TrackAudioReceived += (_, audio) => { lock (sentBlocks) sentBlocks.Add(audio.ToArray()); };

        var previousPort = RemSoundPlugin.BridgePortForTest;
        RemSoundPlugin.BridgePortForTest = host.Port;
        var plugin = new RemSoundPlugin { Host = new StubAudioHost() };
        try
        {
            plugin.SetMaxAudioBufferSize(512, EAudioBitsPerSample.Bits64);
            plugin.Initialize();
            plugin.Start();

            var input = (AudioIOPortManaged)plugin.InputPorts[0];
            var output = (AudioIOPortManaged)plugin.OutputPorts[0];

            // Run one block with the track at a known level, and hand back the loudest sample the DAW
            // would have heard on its output. The first block after a format change is deliberately
            // silent (buffers resize there rather than on the audio thread), so callers run a few.
            double RunBlock()
            {
                var left = input.GetAudioBuffer(0);
                var right = input.GetAudioBuffer(1);
                for (var i = 0; i < left.Length; i++) { left[i] = TrackLevel; right[i] = TrackLevel; }
                plugin.Process();
                var outLeft = output.GetAudioBuffer(0);
                var peak = 0.0;
                for (var i = 0; i < outLeft.Length; i++) peak = Math.Max(peak, Math.Abs(outLeft[i]));
                return peak;
            }

            float SentPeak()
            {
                lock (sentBlocks)
                {
                    var peak = 0f;
                    foreach (var block in sentBlocks)
                        foreach (var s in block) peak = Math.Max(peak, Math.Abs(s));
                    return peak;
                }
            }
            void ClearSent() { lock (sentBlocks) sentBlocks.Clear(); }
            int SentCount() { lock (sentBlocks) return sentBlocks.Count; }

            // --- Neither direction: the track still passes through ---------------------------------
            RunBlock();                       // the silent format-change block
            ClearSent();
            for (var i = 0; i < 5; i++) RunBlock();
            var idlePeak = RunBlock();
            Check(Math.Abs(idlePeak - TrackLevel) < 0.001,
                $"with neither direction ticked the track must still pass through untouched - effects are in series, and a "
              + $"plugin that outputs silence silences everything upstream of it (peak {idlePeak:0.000})");
            Thread.Sleep(60);
            Check(SentCount() == 0,
                $"and NOTHING may be sent - a plugin that broadcasts a track the moment it is inserted is the surprise this "
              + $"defaults-off design exists to prevent ({SentCount()} blocks crossed the link)");

            // --- Send only --------------------------------------------------------------------------
            plugin.SetJobForTest(send: true, receive: false, peer: null);
            ClearSent();
            for (var i = 0; i < 8; i++) RunBlock();
            Check(WaitUntil(() => SentCount() > 0), "ticking send must put the track on the link");
            var sendOnlyPeak = SentPeak();
            Check(Math.Abs(sendOnlyPeak - TrackLevel) < 0.01f,
                $"what is sent must be the track at its own level (peak {sendOnlyPeak:0.000}, track is {TrackLevel:0.000})");

            // --- BOTH AT ONCE, which is the whole point ---------------------------------------------
            plugin.SetJobForTest(send: true, receive: true, peer: IPAddress.Loopback);
            // The peer's audio arrives asynchronously: asking for it IS the claim, so the ring fills a
            // block or two after the first request rather than during it.
            var heard = 0.0;
            for (var round = 0; round < 200 && heard < TrackLevel + PeerLevel - 0.02; round++)
            {
                heard = RunBlock();
                Thread.Sleep(2);
            }
            Check(heard > TrackLevel + 0.1,
                $"with both ticked the DAW track must carry its own audio AND the peer summed on top "
              + $"(heard {heard:0.000}, track alone is {TrackLevel:0.000})");
            Check(heard < TrackLevel + PeerLevel + 0.05,
                $"...and at their honest sum, not doubled (heard {heard:0.000}, expected about {TrackLevel + PeerLevel:0.000})");

            // THE FEEDBACK PROOF. The output the DAW just got is 0,75. If the plugin sent its OUTPUT
            // rather than its INPUT, that is the number that would appear on the link, and the peer
            // would be listening to themselves. It must still be the track alone.
            ClearSent();
            for (var i = 0; i < 20; i++) { RunBlock(); Thread.Sleep(1); }
            Check(WaitUntil(() => SentCount() > 0), "the send direction must still be running while receiving");
            var bothPeak = SentPeak();
            Check(bothPeak < TrackLevel + 0.05f,
                $"WHAT IS SENT MUST BE THE INPUT, NEVER THE OUTPUT. While receiving a peer at {PeerLevel:0.000} and playing a "
              + $"track at {TrackLevel:0.000}, the link carried {bothPeak:0.000}. Anything near {TrackLevel + PeerLevel:0.000} means the peer is "
              + $"being sent back to themselves, which is the loop that one-job-per-instance used to prevent");
            Check(bothPeak > TrackLevel - 0.05f,
                $"...and it must still carry the track, or this proves only that sending stopped ({bothPeak:0.000})");

            // --- The levels ---------------------------------------------------------------------------
            // The DAW's fader cannot reach either of these: in every host the FX chain runs before the
            // fader, so the track fader changes what you hear downstream and not one sample of what
            // goes out. Anthony Reyers hit exactly that with two tracks at 0 dB summing to nearly
            // +6 dBFS on the wire and nothing on the track able to pull them down.
            plugin.SetSendLevelForTest(0.5f);
            ClearSent();
            for (var i = 0; i < 20; i++) { RunBlock(); Thread.Sleep(1); }
            Check(WaitUntil(() => SentCount() > 0), "blocks must still be crossing after a level change");
            var trimmed = SentPeak();
            Check(Math.Abs(trimmed - TrackLevel / 2) < 0.02f,
                $"the send level must trim what LEAVES (sent {trimmed:0.000}, expected about {TrackLevel / 2:0.000})");
            var stillHeard = RunBlock();
            Check(stillHeard > TrackLevel - 0.02,
                $"...and must NOT touch the track's own passthrough - a send trim that also turned the track down would be a "
              + $"hidden gain nobody asked for (heard {stillHeard:0.000})");

            plugin.SetSendLevelForTest(1f);
            plugin.SetReceiveLevelForTest(0f);
            var muted = 0.0;
            for (var round = 0; round < 60; round++) { muted = RunBlock(); Thread.Sleep(2); }
            Check(Math.Abs(muted - TrackLevel) < 0.02,
                $"a receive level of zero must leave the track alone and nothing else (heard {muted:0.000}, track is {TrackLevel:0.000})");

            return $"neither direction sends nothing and still passes the track; send alone carries {sendOnlyPeak:0.000}; both at once "
                 + $"puts {heard:0.000} on the track while the link carries {bothPeak:0.000} - the input, never the output; "
                 + $"the send trim reaches the wire without touching the passthrough";
        }
        finally
        {
            try { plugin.Stop(); } catch { /* teardown is best-effort */ }
            try { plugin.CloseForTest(); } catch { /* teardown is best-effort */ }
            RemSoundPlugin.BridgePortForTest = previousPort;
            PluginSendBus.ResetForTest();
        }
    }

    /// <summary>
    /// A TRACK SAVED BY ONE OF THE PLUGIN'S TEST BUILDS OPENS WITH NOBODY CHOSEN.
    ///
    /// <para>2026-09-13, Ed: there was no plugin before 6.0, and 6.0 is the first release, so the conversions for
    /// projects saved by the test builds went — the one-job build's bare address, and the "v2|" single peer with its
    /// linear levels. Such a track must still OPEN, because refusing the chunk loses the whole plugin instance, and it
    /// opens with nobody chosen. A track this build saved must still bring its people back.</para>
    /// </summary>
    private static string? PluginTestBuildTrackOpensWithNobodyChosen()
    {
        // Build the test-build chunks from real ones, by replacing the payload this build writes with
        // the payload a test build wrote. Deriving them from a real save rather than hand-rolling the
        // bytes keeps the test honest if the surrounding format ever changes.
        byte[] Chunk(string payloadText, string? address)
        {
            var plugin = new RemSoundPlugin { Host = new StubAudioHost() };
            try
            {
                plugin.Initialize();
                if (address is not null)
                    plugin.PushJobToParametersForTest(send: false, receive: true, peer: IPAddress.Parse(address));
                var state = plugin.SaveState();
                var marker = System.Text.Encoding.ASCII.GetBytes("<!--RemSoundPeer:");
                var at = IndexOfTail(state, marker);
                Check(at >= 0, "the state chunk must carry our marker, or this test is building the wrong thing");
                var from = at + marker.Length + sizeof(int);
                var text = System.Text.Encoding.UTF8.GetString(state, from, state.Length - from);
                Check(text.StartsWith("v3|", StringComparison.Ordinal),
                    $"this build must tag its own state, or an older chunk cannot be told apart from a new one (payload '{text}')");

                var payload = System.Text.Encoding.UTF8.GetBytes(payloadText);
                var older = new byte[from + payload.Length];
                Buffer.BlockCopy(state, 0, older, 0, from);
                BitConverter.GetBytes(payload.Length).CopyTo(older, at + marker.Length);
                Buffer.BlockCopy(payload, 0, older, from, payload.Length);
                return older;
            }
            finally { try { plugin.CloseForTest(); } catch { /* best-effort */ } }
        }

        // This build's own chunk: its people must come back.
        var ours = new RemSoundPlugin { Host = new StubAudioHost() };
        try
        {
            ours.Initialize();
            ours.RestoreState(Chunk("v3|-|192.168.1.50", "192.168.1.50"));
            Check(ours.SavedPeerAddressesForTest.SequenceEqual(new[] { "192.168.1.50" }),
                "a track this build saved must come back with the same person chosen");
        }
        finally { try { ours.CloseForTest(); } catch { /* best-effort */ } }

        // The one-job test build wrote the bare address with no tag; the one-peer build after it tagged it "v2|".
        foreach (var (chunk, what) in new[]
        {
            (Chunk("192.168.1.50", "192.168.1.50"), "the one-job test build"),
            (Chunk("v2|192.168.1.50", "192.168.1.50"), "the one-peer test build"),
        })
        {
            var plugin = new RemSoundPlugin { Host = new StubAudioHost() };
            try
            {
                plugin.Initialize();
                try { plugin.RestoreState(chunk); }
                catch (Exception ex)
                {
                    Check(false, $"a track saved by {what} must open — refusing it loses the whole plugin instance ({ex.GetType().Name}: {ex.Message})");
                }
                Check(plugin.SavedPeerAddressesForTest.Count == 0,
                    $"a track saved by {what} must open with nobody chosen — its payload is not read (got {string.Join(", ", plugin.SavedPeerAddressesForTest)})");
            }
            finally { try { plugin.CloseForTest(); } catch { /* best-effort */ } }
        }

        return "a track this build saved comes back with its person; one saved by either test build opens with nobody chosen";
    }

    private static int IndexOfTail(byte[] haystack, byte[] needle)
    {
        for (var i = haystack.Length - needle.Length; i >= 0; i--)
        {
            var match = true;
            for (var j = 0; j < needle.Length && match; j++) match = haystack[i + j] == needle[j];
            if (match) return i;
        }
        return -1;
    }
}
