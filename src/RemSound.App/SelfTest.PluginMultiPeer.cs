using System.Net;
using AudioPlugSharp;
using RemSound.Core;
using RemSound.Plugin;

namespace RemSound.App;

/// <summary>
/// SEVERAL PEOPLE ON ONE TRACK, and the levels in the unit a DAW user actually thinks in.
///
/// <para>One person per track was the right default and is still the right default for recording,
/// where a track each is what you want at the mix. Listening is a different job: "say that you want
/// to listen to 3 people talking while they are listening to your production session" (Anthony
/// Reyers, 2026-09-01), and making that mean three plugin instances is the tool getting in the way.
/// So a receiving instance carries a SET, and the app mixes it.</para>
///
/// <para>The hard part is not the mixing. It is that a set of people cannot ride on one continuous
/// parameter, and that positions in a peer list are not people — somebody joining or leaving shifts
/// every position after them. Everything here is about those two, because getting them wrong puts a
/// stranger on somebody's track and nothing on screen explains it.</para>
/// </summary>
internal static partial class SelfTest
{
    private const float AndreLevel = 0.25f;
    private const float ChrisLevel = 0.5f;

    /// <summary>The app mixes the set, and one instance's read of a peer is shared with every other
    /// instance on that peer without leaking the rest of its set into theirs.</summary>
    private static string PluginSeveralPeersOnOneTrack()
    {
        var andre = IPAddress.Parse("192.168.1.50");
        var chris = IPAddress.Parse("192.168.1.51");

        // Each peer at their own constant level, so a sum can be taken apart again by ear: 0,25 alone
        // is Andre, 0,5 alone is Chris, 0,75 is both. Anything else is a routing fault, stated as a
        // number rather than as "it sounded odd".
        int ReadPeer(IPAddress peer, Span<float> destination, int frames)
        {
            var floats = Math.Min(destination.Length, frames * PluginBridgeProtocol.WireChannels);
            var level = peer.Equals(andre) ? AndreLevel : peer.Equals(chris) ? ChrisLevel : 0f;
            for (var i = 0; i < floats; i++) destination[i] = level;
            return floats / PluginBridgeProtocol.WireChannels;
        }

        using var host = new PluginBridgeHost(ReadPeer, port: 0);
        host.PeerListSource = () => [(andre, "Andre"), (chris, "Chris")];

        using var track = new PluginBridgeClient(host.Port);
        track.Hello();
        Check(WaitUntil(() => track.KnownPeers.Count == 2), "the app must answer with its peer list");

        // --- One person, as it always was ------------------------------------------------------------
        track.SetReceivedPeers([andre]);
        var one = PumpPlugin(track, frames: 256, rounds: 40);
        Check(Math.Abs(one - AndreLevel) < 0.01f,
            $"one peer must arrive at their own level ({one:0.000}, expected {AndreLevel:0.000})");

        // --- TWO PEOPLE ON THE ONE TRACK --------------------------------------------------------------
        track.SetReceivedPeers([andre, chris]);
        var both = PumpPlugin(track, frames: 256, rounds: 60);
        Check(Math.Abs(both - (AndreLevel + ChrisLevel)) < 0.02f,
            $"two peers on one track must arrive SUMMED ({both:0.000}, expected {AndreLevel + ChrisLevel:0.000}). "
          + $"{AndreLevel:0.000} or {ChrisLevel:0.000} alone would mean the second request replaced the first rather than joining it");
        Check(host.Claims.IsClaimed(andre) && host.Claims.IsClaimed(chris),
            "both must be claimed, or whoever is not claimed is playing out of the speakers as well and the user hears them twice");

        // NOT divided by the size of the set. Everybody getting quieter the moment somebody joined the
        // track would be a gain change nobody asked for.
        Check(both > ChrisLevel + 0.05f, $"...and not averaged down ({both:0.000})");

        // --- A SECOND INSTANCE ON ONE OF THEM ----------------------------------------------------------
        // The sharp case for keying the fan-out by instance AND peer. Reading a peer drains their
        // jitter buffer, so the first instance to ask hands the same samples to every other claimant.
        // With the queue keyed by instance alone, the audio this track read for Andre AND Chris would be
        // handed to a track that only wanted Chris — that track would hear 0,75.
        using (var monitor = new PluginBridgeClient(host.Port))
        {
            monitor.Hello();
            monitor.SetReceivedPeers([chris]);
            var alone = 0f;
            for (var round = 0; round < 40; round++)
            {
                PumpPlugin(track, frames: 256, rounds: 1);
                alone = Math.Max(alone, PumpPlugin(monitor, frames: 256, rounds: 1));
            }
            Check(alone > 0.05f, $"a second instance on the same peer must get audio, not silence ({alone:0.000})");
            Check(alone < ChrisLevel + 0.05f,
                $"and ONLY that peer: it asked for Chris at {ChrisLevel:0.000} and got {alone:0.000}. Anything near "
              + $"{AndreLevel + ChrisLevel:0.000} is the other track's whole mix arriving on this one");
            Check(host.Claims.ClaimCount(chris) == 2,
                "both instances must be counted on Chris, so one closing does not un-mute him under the other");
            Check(host.Claims.ClaimCount(andre) == 1, "and Andre must still be held by the one track that asked for him");
        }

        // --- Dropping one of a set releases only that one -----------------------------------------------
        track.SetReceivedPeers([chris]);
        PumpPlugin(track, frames: 256, rounds: 20);
        Check(WaitUntil(() => !host.Claims.IsClaimed(andre)),
            "taking one person off a track must release JUST them, immediately - waiting for a timeout leaves them mute for five seconds");
        Check(host.Claims.IsClaimed(chris), "...and must not disturb the rest of the set");
        var remaining = PumpPlugin(track, frames: 256, rounds: 40);
        Check(Math.Abs(remaining - ChrisLevel) < 0.02f,
            $"what is left must be that person alone ({remaining:0.000}, expected {ChrisLevel:0.000})");

        track.SetReceivedPeers([]);
        Check(WaitUntil(() => !host.Claims.IsClaimed(chris)), "and emptying the set must give everybody back to the speakers");

        return $"one peer at {AndreLevel:0.00}, two summed to {both:0.000}, both claimed and neither averaged down; a second instance on "
             + "one of them gets that person alone and not the other track's mix; dropping one releases only that one";
    }

    /// <summary>Two tracks on the same person must hear the same sample at the same moment.</summary>
    private static string PluginTwoTracksOnOnePeerStayAligned()
    {
        var andre = IPAddress.Parse("192.168.1.50");

        // A COUNTING signal, not a tone: every frame carries its own position in the stream, so "which
        // samples did this track get" is answerable exactly rather than by correlation.
        var next = 0f;
        var starve = false;   // the peer's buffer is momentarily empty
        int ReadPeer(IPAddress peer, Span<float> destination, int frames)
        {
            if (starve) return 0;
            var count = Math.Min(frames, destination.Length / 2);
            for (var i = 0; i < count; i++)
            {
                destination[i * 2] = next;
                destination[i * 2 + 1] = next;
                next++;
            }
            return count;
        }

        using var host = new PluginBridgeHost(ReadPeer, port: 0);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        const int Frames = 128;
        var a = new float[Frames * 2];
        var b = new float[Frames * 2];

        // The first round is the JOIN. A track that arrives mid-round gets the block the other one was
        // just given - the round is still open - so the two are aligned from their very first sample.
        // (The design before this served the joiner nothing on its first round, which was patching from
        // one side the fault the rounds remove: per-instance cursors that could split.)
        host.ReadSharedForTest(first, andre, a, Frames);
        var joinRound = host.ReadSharedForTest(second, andre, b, Frames);
        Check(joinRound == Frames && a.AsSpan().SequenceEqual(b),
            $"a track joining mid-round must get the SAME block the other track was just served, so they are aligned from "
          + $"the first sample ({joinRound} frames, first sample {b[0]} against {a[0]})");

        // AND THE ORDER MUST NOT MATTER. This is the whole fix. The queue this replaced handed a
        // claimant the PREVIOUS round's block whenever its request arrived before the instance that
        // did the read, and which instance asks first is fixed by where the plugins sit in the DAW's
        // FX chains - so one track sat permanently one buffer behind the other. Anthony Reyers heard
        // it as "around 2/3 ms" at a 128-frame buffer (2026-09-01).
        var worst = 0f;
        for (var round = 0; round < 200; round++)
        {
            // Swap who asks first every round, which is the case the queue got wrong.
            if (round % 2 == 0)
            {
                host.ReadSharedForTest(first, andre, a, Frames);
                host.ReadSharedForTest(second, andre, b, Frames);
            }
            else
            {
                host.ReadSharedForTest(second, andre, b, Frames);
                host.ReadSharedForTest(first, andre, a, Frames);
            }
            worst = Math.Max(worst, Math.Abs(a[0] - b[0]));
            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] == b[i]) continue;
                Check(false, $"round {round}: the two tracks got different audio at sample {i} ({a[i]} against {b[i]}). "
                           + $"A difference of {Frames} is one whole buffer, which is the comb filtering you hear when both "
                           + "tracks are up");
                break;
            }
        }
        Check(worst == 0f, $"no round may be offset at all, and the worst was {worst} sample(s)");

        // A track that stops asking and comes back must rejoin the LIVE round, not work through the
        // backlog it missed: it gets exactly what the live track was just given.
        for (var i = 0; i < 100; i++) host.ReadSharedForTest(first, andre, a, Frames);
        host.ReadSharedForTest(second, andre, b, Frames);
        Check(a.AsSpan().SequenceEqual(b),
            $"a track that fell behind must come back on the live track's block, not {a[0] - b[0]} frames behind it");

        // --- THE STARVED PULL ----------------------------------------------------------------------
        // The first to ask finds the peer's buffer momentarily empty; the second asks a moment later
        // and the packet has arrived. With a cursor each, the first stayed put while the second moved
        // on - one block apart for good, with nothing to bring them back. A round is short for
        // EVERYBODY in it, and the next round retries the same position.
        starve = true;
        var firstGot = host.ReadSharedForTest(first, andre, a, Frames);   // the pull comes back empty
        starve = false;
        var secondGot = host.ReadSharedForTest(second, andre, b, Frames); // audio has arrived - but this round is closed
        Check(firstGot == 0 && secondGot == 0,
            $"when the round's pull came back empty, the SECOND asker must get nothing too ({firstGot} and {secondGot} "
          + "frames); handing it the block that arrived in between puts it one block ahead of the first for ever");
        for (var round = 0; round < 20; round++)
        {
            host.ReadSharedForTest(second, andre, b, Frames);
            host.ReadSharedForTest(first, andre, a, Frames);
            Check(a.AsSpan().SequenceEqual(b),
                $"round {round} after the starved pull: both tracks must be on the same block again ({a[0]} against {b[0]})");
        }

        // --- TWO DAWs ARE TWO SETS OF ROUNDS ---------------------------------------------------------
        // A second host reads its own contiguous stream, whatever the first host's tracks do in between,
        // and the first host's tracks stay together across it.
        var otherDaw = Guid.NewGuid();
        var third = Guid.NewGuid();
        var c = new float[Frames * 2];
        host.ReadSharedForTest(third, andre, c, Frames, otherDaw);
        var previous = c[0];
        for (var round = 0; round < 20; round++)
        {
            host.ReadSharedForTest(first, andre, a, Frames);
            host.ReadSharedForTest(third, andre, c, Frames, otherDaw);
            host.ReadSharedForTest(second, andre, b, Frames);
            Check(c[0] == previous + Frames,
                $"a second DAW must read its own contiguous stream ({c[0]} after {previous}), whatever the first DAW's "
              + "tracks are doing in between");
            Check(a.AsSpan().SequenceEqual(b), "...and the first DAW's two tracks must stay together across it");
            previous = c[0];
        }

        return "two tracks on one peer get byte-identical blocks over 200 rounds with the asking order swapped every round, "
             + "worst offset 0 samples; a joiner is aligned from its first sample; one that falls behind rejoins the live "
             + "round; an empty pull is empty for the whole round; a second DAW reads its own stream";
    }

    /// <summary>
    /// A SLOW START MUST LEAVE NO LASTING LAG, AND A BURSTY HOST MUST BE SERVED. Driven through the real
    /// link, because the fault lived in the plugin's queue rather than in the host's read.
    ///
    /// <para>What the logs showed (2026-09-04): at start, and after every re-activation, the app's first
    /// replies come back later than a DAW block. Each starved call still sends its request; the late
    /// replies pile up; and a queue played from the front then sits that many blocks behind for the
    /// rest of the session - a different number for each instance, 896 frames in one and 768 in the
    /// other, and two tracks on the same peer that far apart. Keeping only the freshest reply threw
    /// half the audio away under Reaper's bursts, and trimming "what was never used" was racy under
    /// the same bursts. So replies are now played by the DAW block they are for. This test makes both
    /// starts happen: the slow one, after which both tracks must be on the same block at once, and the
    /// bursty one, which must be served from the lead without a reply being thrown away.</para>
    /// </summary>
    private static string PluginSlowStartLeavesNoLastingLag()
    {
        var andre = IPAddress.Parse("192.168.1.50");
        var next = 0f;
        // Set: the app answers at once. Reset: its bridge thread stalls inside the pull, every request
        // sent meanwhile queues unanswered, and the replies come back in one burst when it is set again
        // - the slow start, made exact rather than left to the scheduler.
        using var appAnswers = new ManualResetEventSlim(true);
        int ReadPeer(IPAddress peer, Span<float> destination, int frames)
        {
            appAnswers.Wait();
            var count = Math.Min(frames, destination.Length / 2);
            for (var i = 0; i < count; i++)
            {
                destination[i * 2] = next;
                destination[i * 2 + 1] = next;
                next++;
            }
            return count;
        }

        PluginBridgeClient.ResetLeadForTest();
        using var host = new PluginBridgeHost(ReadPeer, port: 0);
        host.PeerListSource = () => [(andre, "Andre")];
        using var a = new PluginBridgeClient(host.Port);
        using var b = new PluginBridgeClient(host.Port);
        a.Hello();
        b.Hello();
        Check(WaitUntil(() => a.Connected && b.Connected), "both instances must be answered by the app");
        a.SetReceivedPeers([andre]);
        b.SetReceivedPeers([andre]);

        const int Frames = 128;
        var blockA = new float[Frames * 2];
        var blockB = new float[Frames * 2];

        // --- THE SLOW START -----------------------------------------------------------------------
        // The first track was activated a couple of blocks before the second - hosts activate plugins
        // one after another - and then both are called once per block through a start where NO reply
        // comes back in time: ten calls from the first track, eight from the second, every one starved
        // and every one still asking.
        appAnswers.Reset();
        for (var i = 0; i < 2; i++) a.ReadPeerBlock(blockA, Frames);
        for (var i = 0; i < 8; i++)
        {
            a.ReadPeerBlock(blockA, Frames);
            b.ReadPeerBlock(blockB, Frames);
        }
        appAnswers.Set();
        Check(WaitUntil(() => a.RingFrames >= 10 * Frames && b.RingFrames >= 8 * Frames, 3000),
            $"the late replies must have piled up, ten blocks in one queue and eight in the other ({a.RingFrames} and "
          + $"{b.RingFrames} frames) - that pile is the fault, and it has to be present for this test to be testing anything");

        // Steady state: one call each per block, replies arriving well inside the block. Both tracks
        // must be on the SAME block from the first steady call, byte for byte: each plays the block its
        // own ask maps to, less the same lead, and the pile behind that block is dropped in one go.
        var compared = 0;
        var starved = 0;
        for (var block = 0; block < 60; block++)
        {
            var gotA = a.ReadPeerBlock(blockA, Frames);
            var gotB = b.ReadPeerBlock(blockB, Frames);
            Thread.Sleep(3);
            if (block < 2) continue;
            if (gotA < Frames || gotB < Frames) { starved++; continue; }
            compared++;
            Check(blockA.AsSpan().SequenceEqual(blockB),
                $"block {block}: after the slow start the two tracks must be on the SAME block, and they are {blockA[0] - blockB[0]} "
              + $"frames apart (queues hold {a.RingFrames} and {b.RingFrames}). This is the 2,7 ms Anthony Reyers hears");
        }
        Check(starved <= 5, $"too many starved blocks to judge alignment ({starved} of 58) - the loopback was not keeping up");
        Check(compared >= 45, $"not enough blocks compared ({compared})");
        Check(a.SkippedFrames >= 4 * Frames && b.SkippedFrames >= 2 * Frames,
            $"the pile behind the block being played must have been dropped ({a.SkippedFrames} and {b.SkippedFrames} frames)");
        var skippedA = a.SkippedFrames;
        var skippedB = b.SkippedFrames;

        // --- THE BURSTY HOST ------------------------------------------------------------------------
        // Reaper's shape: four blocks back to back, then a device period of nothing. The lead has to
        // cover the burst - it is raised when a reply turns out late - and from then on every read in
        // a burst is served, nothing is thrown away, and the two tracks stay together.
        var shortInBursts = 0;
        for (var burst = 0; burst < 24; burst++)
        {
            for (var call = 0; call < 4; call++)
            {
                var gotA = a.ReadPeerBlock(blockA, Frames);
                var gotB = b.ReadPeerBlock(blockB, Frames);
                if (burst < 4) continue;   // the lead settles over the first bursts
                if (gotA < Frames || gotB < Frames) { shortInBursts++; continue; }
                Check(blockA.AsSpan().SequenceEqual(blockB), $"burst {burst}, call {call}: the two tracks must be on the same block ({blockA[0]} against {blockB[0]})");
            }
            Thread.Sleep(15);
        }
        Check(shortInBursts <= 4,
            $"a bursty host must be served from the lead ({shortInBursts} short reads in 80, lead {PluginBridgeClient.LeadBlocks} blocks)");
        Check(a.SkippedFrames == skippedA && b.SkippedFrames == skippedB,
            $"nothing of a burst may be thrown away ({a.SkippedFrames - skippedA} and {b.SkippedFrames - skippedB} frames were)");

        return $"after a ten-block pile both tracks were on the same block for {compared} of 58 blocks ({starved} not comparable), "
             + $"{skippedA} and {skippedB} stale frames dropped; twenty bursts of four served with {shortInBursts} short reads, "
             + $"nothing thrown away, lead settled at {PluginBridgeClient.LeadBlocks} blocks";
    }

    /// <summary>The levels are in decibels, and a level nudge no longer costs a tick of audio.</summary>
    private static string PluginLevelsInDecibels()
    {
        // --- The conversion ------------------------------------------------------------------------
        Check(Math.Abs(RemSoundPlugin.GainFromDb(0) - 1f) < 0.0001f,
            $"0 dB must be unity - that is the whole reason for the change, since \"1,00\" is not a level a DAW user recognises "
          + $"(got {RemSoundPlugin.GainFromDb(0):0.0000})");
        Check(Math.Abs(RemSoundPlugin.GainFromDb(-6) - 0.5012f) < 0.001f,
            $"-6 dB must be half the amplitude (got {RemSoundPlugin.GainFromDb(-6):0.0000})");
        Check(Math.Abs(RemSoundPlugin.GainFromDb(6) - 1.9953f) < 0.001f,
            $"+6 dB must be double it (got {RemSoundPlugin.GainFromDb(6):0.0000})");
        Check(RemSoundPlugin.GainFromDb(RemSoundPlugin.MinLevelDb) == 0f,
            "the bottom of the range must be a REAL off. All the way down meaning -60 dB of leak rather than silence is the "
          + "kind of surprise that gets found in a live session");
        Check(RemSoundPlugin.GainFromDb(RemSoundPlugin.MinLevelDb - 20) == 0f, "and below it, for a value out of a saved project");
        Check(Math.Abs(RemSoundPlugin.DbFromGain(1f)) < 0.001, "unity must convert to 0 dB");

        // --- The parameters ------------------------------------------------------------------------
        var plugin = new RemSoundPlugin { Host = new StubAudioHost() };
        try
        {
            plugin.Initialize();
            foreach (var id in new[] { "sendlevel", "receivelevel" })
            {
                var parameter = plugin.Parameters.First(p => p.ID == id);
                Check(Math.Abs(parameter.DefaultValue) < 0.001,
                    $"{id} must default to 0 dB, so switching a direction on changes nothing else (got {parameter.DefaultValue})");
                Check(parameter.MinValue <= RemSoundPlugin.MinLevelDb && parameter.MaxValue >= 12,
                    $"{id} must run from a real off to some boost ({parameter.MinValue}..{parameter.MaxValue})");
                // The host reads this to the user, and OSARA reads it aloud. A bare "-3,5" is a number
                // with no unit, which in a list of parameters is a guess.
                parameter.EditValue = -3.5;
                Check(parameter.DisplayValue.Contains("dB", StringComparison.OrdinalIgnoreCase),
                    $"{id} must SAY dB when the host asks it to describe itself (got \"{parameter.DisplayValue}\")");
                // Normalised values must stay inside 0..1, which is what rules out AudioPlugSharp's own
                // DecibelParameter here: it normalises as 10^(dB/20) with the ceiling pinned at 0 dB, so
                // any boost at all pushes the host past 1.
                foreach (var value in new[] { parameter.MinValue, 0, parameter.MaxValue })
                {
                    var normalised = parameter.GetValueNormalized(value);
                    Check(normalised is >= 0 and <= 1,
                        $"{id} at {value} dB normalises to {normalised:0.000}, outside the 0..1 a VST3 host will accept");
                }
            }

            var receiveLevel = plugin.Parameters.First(p => p.ID == "receivelevel");
            receiveLevel.EditValue = -6;
            plugin.ApplyParameters();
            Check(Math.Abs(plugin.ReceiveGainForTest - 0.5012f) < 0.001f,
                $"a level set in dB must reach the audio path as a multiply (got {plugin.ReceiveGainForTest:0.0000})");
        }
        finally { try { plugin.CloseForTest(); } catch { } }

        // --- A LEVEL NUDGE MUST NOT RESET THE RECEIVE RING ------------------------------------------
        // Every parameter touch re-applies the job, and re-applying it used to reset the ring even when
        // the set had not changed - so moving a level spinner threw away the jitter cushion and started
        // it again, which is a tick in the received audio each time. The log said it plainly: "now
        // receiving 100.81.123.86 (was 100.81.123.86)", three times while the window was open
        // (2026-09-01).
        var andre = IPAddress.Parse("192.168.1.50");
        int ReadPeer(IPAddress peer, Span<float> destination, int frames)
        {
            var floats = Math.Min(destination.Length, frames * PluginBridgeProtocol.WireChannels);
            for (var i = 0; i < floats; i++) destination[i] = 0.5f;
            return floats / PluginBridgeProtocol.WireChannels;
        }
        using var host = new PluginBridgeHost(ReadPeer, port: 0);
        using var client = new PluginBridgeClient(host.Port);
        client.Hello();
        client.SetReceivedPeers([andre]);
        PumpPlugin(client, frames: 64, rounds: 20);
        Check(WaitUntil(() => client.RingFrames > 0), "the ring must have built a cushion before this proves anything");

        var cushion = client.RingFrames;
        client.SetReceivedPeers([andre]);         // what a level nudge does, over and over
        client.SetReceivedPeers([andre]);
        Check(client.RingFrames == cushion,
            $"re-applying the SAME set must not touch the ring (had {cushion} frames, left {client.RingFrames}). "
          + "Throwing the cushion away is a tick in the audio every time the user moves a spinner");

        client.SetReceivedPeers([]);
        Check(client.RingFrames == 0, "...but stopping altogether must clear it, or the next peer starts with the last one's tail");

        return "0 dB is unity and the bottom of the range is real silence; the parameters say dB and normalise inside 0..1; "
             + "re-applying an unchanged set leaves the jitter cushion alone";
    }

    /// <summary>Who is on the track survives the peer list changing underneath it.</summary>
    private static string PluginPeerSetSurvivesTheListChanging()
    {
        var andre = IPAddress.Parse("192.168.1.50");
        var chris = IPAddress.Parse("192.168.1.51");
        var jonathan = IPAddress.Parse("192.168.1.49");

        int Silence(IPAddress peer, Span<float> destination, int frames) => 0;
        using var host = new PluginBridgeHost(Silence, port: 0);
        // DELIBERATELY OUT OF ORDER, and not alphabetical: the app reports peers in whatever order it
        // learned about them, and that order is different on different days.
        var listed = new List<(IPAddress, string)> { (chris, "Chris"), (andre, "Andre") };
        host.PeerListSource = () => listed;

        var previousPort = RemSoundPlugin.BridgePortForTest;
        RemSoundPlugin.BridgePortForTest = host.Port;
        var plugin = new RemSoundPlugin { Host = new StubAudioHost() };
        try
        {
            plugin.Initialize();
            Check(WaitUntil(() => plugin.SortedPeersForTest.Count == 2), "the peer list must reach the plugin");

            // --- The order is OURS ---------------------------------------------------------------
            Check(plugin.SortedPeersForTest.Select(p => p.Name).SequenceEqual(new[] { "Andre", "Chris" }),
                "the plugin must impose its own order on the peer list. A parameter can only hold a position, so a list that "
              + "arrives in arrival order means position 2 is a different person on a different day");

            // --- The cursor names somebody; the tick puts them on the track -------------------------
            var cursor = plugin.Parameters.First(p => p.ID == "peer");
            var include = plugin.Parameters.First(p => p.ID == "peerinclude");
            var receive = plugin.Parameters.First(p => p.ID == "receive");
            var all = plugin.Parameters.First(p => p.ID == "allpeers");
            receive.EditValue = 1;
            plugin.ApplyParameters();

            cursor.EditValue = 2;                    // Chris, in our order
            plugin.ApplyParameters();
            Check(plugin.ChosenPeersForTest.Count == 0,
                "MOVING THE CURSOR MUST CHOOSE NOBODY. Arrowing down a list and finding you had put somebody on your track by "
              + "passing over them would be a trap, and an invisible one");
            Check(Math.Abs(include.EditValue) < 0.5,
                "and the tick must read back FALSE for somebody who is not on the track - it is how you ask, without the window");

            include.EditValue = 1;
            plugin.ApplyParameters();
            Check(plugin.ChosenPeersForTest.Count == 1 && plugin.ChosenPeersForTest[0].Equals(chris),
                $"ticking must put the person under the cursor on the track (got {plugin.ChosenPeersForTest.Count} peer(s))");

            cursor.EditValue = 1;                    // Andre
            plugin.ApplyParameters();
            Check(plugin.ChosenPeersForTest.Count == 1 && plugin.ChosenPeersForTest[0].Equals(chris),
                "moving the cursor to somebody else must not take the first person off the track");
            Check(Math.Abs(include.EditValue) < 0.5,
                "and the tick must have followed the cursor, reading back for the person now under it rather than staying on");

            include.EditValue = 1;
            plugin.ApplyParameters();
            Check(plugin.ChosenPeersForTest.Count == 2,
                $"a second tick must ADD - one track carrying a conversation is the point ({plugin.ChosenPeersForTest.Count} peer(s))");

            // --- Somebody LEAVES ---------------------------------------------------------------------
            listed.RemoveAll(p => p.Item1.Equals(chris));
            plugin.Bridge?.Hello();
            Check(WaitUntil(() => plugin.SortedPeersForTest.Count == 1), "the shortened list must reach the plugin");
            plugin.ApplyParameters();
            Check(plugin.ChosenPeersForTest.Count == 1 && plugin.ChosenPeersForTest[0].Equals(andre),
                "somebody disconnecting must leave the rest of the track alone - and claiming a peer who is not there would be a "
              + "request the app can only answer with silence");
            Check(plugin.SavedPeerAddressesForTest.Contains(chris.ToString()),
                "...but they must stay REMEMBERED. A track set up once should not need setting up again every time somebody's "
              + "wifi drops");

            // ...and comes back.
            listed.Add((chris, "Chris"));
            plugin.Bridge?.Hello();
            Check(WaitUntil(() => plugin.SortedPeersForTest.Count == 2), "the peer must reappear in the list");
            plugin.ApplyParameters();
            Check(plugin.ChosenPeersForTest.Count == 2, "a remembered peer coming back must return to the track on their own");

            // --- All peers ----------------------------------------------------------------------------
            all.EditValue = 1;
            plugin.ApplyParameters();
            Check(plugin.AllPeersForTest && plugin.ChosenPeersForTest.Count == 2, "all peers must take everybody currently connected");
            listed.Add((jonathan, "Jonathan"));
            plugin.Bridge?.Hello();
            Check(WaitUntil(() => plugin.SortedPeersForTest.Count == 3), "the joiner must reach the plugin");
            plugin.ApplyParameters();
            Check(plugin.ChosenPeersForTest.Count == 3,
                $"...and somebody joining LATER must be picked up, which is the whole difference between \"all peers\" and "
              + $"ticking everybody once ({plugin.ChosenPeersForTest.Count} on the track)");

            // --- Saved and restored -----------------------------------------------------------------
            // Into a FRESH instance, as a DAW reopening the project does: restoring into the same one proved nothing about
            // the chosen set, which that instance never forgot (found 2026-09-24). And the exact set, not "at least two".
            // What the plugin remembers by address is the people ticked by hand; anyone "all peers" picked up comes back through
            // that tick instead, so it is not part of the remembered set.
            var chosenAtSave = plugin.SavedPeerAddressesForTest.OrderBy(a => a).ToList();
            Check(chosenAtSave.Count == 2, $"the two people ticked by hand must be what the track remembers by address ({chosenAtSave.Count})");
            var saved = plugin.SaveState();
            var reopened = new RemSoundPlugin { Host = new StubAudioHost() };
            try
            {
                reopened.Initialize();
                reopened.RestoreState(saved);
                Check(reopened.AllPeersForTest, "the all-peers tick must survive a save and reload");
                var restoredSet = reopened.SavedPeerAddressesForTest.OrderBy(a => a).ToList();
                Check(restoredSet.SequenceEqual(chosenAtSave),
                    $"and so must the chosen set, by address - exactly ({string.Join(", ", restoredSet)} restored, {string.Join(", ", chosenAtSave)} saved)");
            }
            finally { try { reopened.CloseForTest(); } catch { } }
        }
        finally
        {
            RemSoundPlugin.BridgePortForTest = previousPort;
            try { plugin.CloseForTest(); } catch { }
        }

        return "the plugin orders the peer list itself; the cursor names and the tick chooses; a peer leaving stays remembered and "
             + "returns on their own; all-peers picks up a joiner; both survive a save";
    }
}
