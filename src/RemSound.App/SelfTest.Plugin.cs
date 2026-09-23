using System.Net;
using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App;

/// <summary>
/// THE DOUBLE-AUDIO GUARD — the plugin must take a peer OFF this machine's speakers, not add a
/// second copy of them.
///
/// <para>Ed, 2026-08-15: "how are we going to deal with the sound of a peer coming through remsound
/// and also through the vst? ... having both playing would be bad", and then: "you need to build a
/// lot of testing around that, double audio tests, the muting etc etc".</para>
///
/// <para>This is the failure that would be hardest to diagnose from a user report. Two copies of the
/// same peer, a few milliseconds apart, do not sound like "it's playing twice" — they sound like a
/// phasing, flanging fault in the audio engine, and the natural instinct would be to go hunting in
/// the jitter buffer. So it is tested at the level where it would actually break: the real
/// PlayoutEngine, with real sessions, checking what the DEVICE mix contains.</para>
/// </summary>
internal static partial class SelfTest
{
    private static string? PluginDoubleAudioGuard()
    {
        var andre = new IPEndPoint(IPAddress.Parse("192.168.1.50"), 47830);
        var chris = new IPEndPoint(IPAddress.Parse("192.168.1.51"), 47830);
        var instance = Guid.NewGuid();
        var other = Guid.NewGuid();

        // --- The registry's own rules, before any audio is involved -----------------------------
        var claims = new PluginPeerClaims();
        Check(!claims.IsClaimed(andre.Address), "nothing is claimed until a plugin says so");

        claims.Claim(andre.Address, instance);
        Check(claims.IsClaimed(andre.Address), "a plugin receiving a peer must claim it");
        Check(!claims.IsClaimed(chris.Address), "claiming one peer must not silence another");

        // Reference counting: two instances on the same peer, and it stays claimed until BOTH let go.
        // Otherwise removing one plugin brings the peer back through the speakers UNDERNEATH the
        // other one — double audio again, but only sometimes, which is worse.
        claims.Claim(andre.Address, other);
        Check(claims.ClaimCount(andre.Address) == 2, "two instances on one peer must both be counted");
        claims.Release(andre.Address, instance);
        Check(claims.IsClaimed(andre.Address), "the peer stays claimed while the second instance holds it");
        claims.Release(andre.Address, other);
        Check(!claims.IsClaimed(andre.Address), "once everybody lets go, the peer returns to the speakers");

        // ReleaseAll: a disposing plugin must not have to remember which peer it held.
        claims.Claim(andre.Address, instance);
        claims.Claim(chris.Address, instance);
        claims.ReleaseAll(instance);
        Check(!claims.IsClaimed(andre.Address) && !claims.IsClaimed(chris.Address),
            "disposing an instance must drop every claim it held, wherever it pointed");

        // FAIL-SAFE DIRECTION: a plugin that dies without releasing (DAW crashed, process killed)
        // must not leave a peer mute forever. Silence with no way to fix it is a far worse failure
        // than a moment of double audio, so the claim lapses.
        var t0 = new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);
        var stale = new PluginPeerClaims();
        stale.Claim(andre.Address, instance, t0);
        Check(stale.IsClaimed(andre.Address, t0.AddSeconds(1)), "a fresh claim holds");

        // ABSOLUTE seconds, not "ClaimTimeout + 1". Measuring the lapse against the very constant
        // that sets it proves only that a claim lapses EVENTUALLY, never WHEN: stretch the timeout to
        // 50 seconds and the bar stretches with it, so the test stays green while a peer sits mute for
        // most of a minute after a DAW crash with nothing to explain it. Found 2026-08-24.
        //
        // Five seconds is the decision. A plugin renews its claim once per audio block — a few
        // milliseconds — so five seconds is around a thousand missed blocks: unambiguously a dead DAW,
        // and short enough that the person on the other end is not left wondering.
        Check(PluginPeerClaims.ClaimTimeout == TimeSpan.FromSeconds(5),
            $"a claim must lapse after 5 seconds. The plugin renews once per audio block, so this is the gap between "
            + $"'the DAW is busy' and 'the DAW is gone', and it is also how long a peer stays mute after a crash "
            + $"(got {PluginPeerClaims.ClaimTimeout.TotalSeconds}s)");
        Check(stale.IsClaimed(andre.Address, t0.AddSeconds(4)),
            "a claim must still hold four seconds in — a DAW that misses a few blocks under load has not died");
        Check(!stale.IsClaimed(andre.Address, t0.AddSeconds(6)),
            "a claim with no heartbeat for six seconds must LAPSE — a dead DAW must never mute a peer permanently");

        // --- And now where it actually matters: the real mix --------------------------------------
        // Two peers sending; the plugin takes one. The device mix must contain exactly the other.
        var engine = new PlayoutEngine(new ReceiverDiagnostics());
        engine.SetLaneActive(RenderRoute.WasapiLane, true);
        engine.SetLaneActive(RenderRoute.AsioLane, false);
        engine.SetMaxLatencyMs(RenderRoute.Mixed, 30);

        var andreSession = engine.GetOrCreateSession(andre, 1, 1024 * 1024);
        var chrisSession = engine.GetOrCreateSession(chris, 2, 1024 * 1024);
        FillSession(andreSession, 0.5f);   // Andre: loud
        FillSession(chrisSession, 0.25f);  // Chris: quieter, so the two are distinguishable

        var live = new PluginPeerClaims();
        engine.SetPluginPeerClaims(live);

        var buffer = new byte[960 * 8];
        var bothPeers = PeakOfMix(engine, buffer);
        Check(bothPeers > 0.6f, $"with nobody claimed the mix must carry BOTH peers (peak {bothPeers:0.000})");

        live.Claim(andre.Address, instance);
        var chrisOnly = PeakOfMix(engine, buffer);
        Check(chrisOnly > 0.05f, $"the unclaimed peer must still be audible (peak {chrisOnly:0.000})");
        Check(chrisOnly < bothPeers - 0.2f,
            $"the CLAIMED peer must be gone from the speakers — that is the double-audio bug (both {bothPeers:0.000}, after claim {chrisOnly:0.000})");

        live.Claim(chris.Address, other);
        var silence = PeakOfMix(engine, buffer);
        Check(silence < 0.02f, $"with every peer claimed the app's own output must be silent (peak {silence:0.000})");

        live.ReleaseAll(instance);
        live.ReleaseAll(other);
        var backAgain = PeakOfMix(engine, buffer);
        Check(backAgain > 0.05f, $"releasing every claim must bring the audio back to the speakers (peak {backAgain:0.000})");

        // --- ALL THREE CONFIGURATIONS ------------------------------------------------------------
        // Not optional here. The related doubling bug — a claimed peer summed twice — bit ONLY when
        // both kinds of output were ticked, because that is when a stream gains a mirror replica. So
        // a double-audio guard proven in one configuration proves nothing about the one where the bug
        // actually lived. Each configuration gets its own engine, because what is ticked is what
        // decides how many copies of a stream exist. 2026-08-24.
        foreach (var configuration in AudioConfigurations.All)
        {
            var perConfig = new PlayoutEngine(new ReceiverDiagnostics());
            perConfig.SetIndependentLaneLatency(configuration.HasTwoLanes());
            perConfig.SetLaneActive(RenderRoute.WasapiLane, configuration.UsesWasapi());
            perConfig.SetLaneActive(RenderRoute.AsioLane, configuration.UsesAsio());
            perConfig.SetMaxLatencyMs(RenderRoute.Mixed, 30);
            perConfig.SetMaxLatencyMs(RenderRoute.WasapiLane, 30);
            perConfig.SetMaxLatencyMs(RenderRoute.AsioLane, 30);

            var a = perConfig.GetOrCreateSession(andre, 1, 1024 * 1024);
            var c = perConfig.GetOrCreateSession(chris, 2, 1024 * 1024);
            FillSession(a, 0.5f);
            FillSession(c, 0.25f);

            var perConfigClaims = new PluginPeerClaims();
            perConfig.SetPluginPeerClaims(perConfigClaims);
            var mix = new byte[960 * 8];

            var beforeAny = PeakOfMix(perConfig, mix);
            Check(beforeAny > 0.05f, $"in {configuration.Describe()} the speakers must carry audio before any claim (peak {beforeAny:0.000})");

            perConfigClaims.Claim(andre.Address, instance);
            var afterClaim = PeakOfMix(perConfig, mix);
            Check(afterClaim < beforeAny - 0.15f,
                $"in {configuration.Describe()} the CLAIMED peer must leave the speakers — that is the double-audio bug, and it "
                + $"hides in whichever configuration nobody tested (before {beforeAny:0.000}, after {afterClaim:0.000})");

            perConfigClaims.ReleaseAll(instance);
            var released = PeakOfMix(perConfig, mix);
            Check(released > 0.05f, $"in {configuration.Describe()} releasing the claim must bring the peer back (peak {released:0.000})");
        }

        return "claims are reference-counted, lapse without a heartbeat, and a claimed peer is provably absent from the "
             + "device mix in all three audio configurations";
    }

    /// <summary>THE WHOLE LOOP, end to end: a peer arriving over the network, claimed by a plugin
    /// through the real bridge, landing on a DAW track — and provably gone from the speakers.
    ///
    /// <para>The claim-register test above proves the RULES. This proves the WIRING, which is where
    /// it would actually break: a claim that never reaches the app, audio read from the wrong peer,
    /// a release that doesn't arrive, a plugin that quietly gets silence. Every one of those would
    /// look identical to the user — "the plugin doesn't work" — and none would be caught by testing
    /// the register on its own.</para></summary>
    private static string? PluginBridgeEndToEnd()
    {
        var andre = new IPEndPoint(IPAddress.Parse("192.168.1.50"), 47830);
        var chris = new IPEndPoint(IPAddress.Parse("192.168.1.51"), 47830);

        var engine = new PlayoutEngine(new ReceiverDiagnostics());
        engine.SetLaneActive(RenderRoute.WasapiLane, true);
        engine.SetLaneActive(RenderRoute.AsioLane, false);
        engine.SetMaxLatencyMs(RenderRoute.Mixed, 30);

        var andreSession = engine.GetOrCreateSession(andre, 1, 4 * 1024 * 1024);
        var chrisSession = engine.GetOrCreateSession(chris, 2, 4 * 1024 * 1024);
        FillSession(andreSession, 0.5f);
        FillSession(chrisSession, 0.25f);

        // Port 0, never the real one: a gate run must not disturb a RemSound the user has open.
        using var host = new PluginBridgeHost(engine.ReadClaimedPeer, port: 0);
        engine.SetPluginPeerClaims(host.Claims);
        host.PeerListSource = () => [(andre.Address, "Andre"), (chris.Address, "Chris")];

        using var plugin = new PluginBridgeClient(host.Port);
        var mixBuffer = new byte[960 * 8];

        // --- Hello: the plugin must learn who it can receive --------------------------------------
        plugin.Hello();
        Check(WaitUntil(() => plugin.KnownPeers.Count == 2),
            "the app must answer Hello with its peer list - that list IS the plugin's peer chooser");
        Check(plugin.Connected, "answering Hello is how the plugin knows RemSound is running");
        Check(plugin.KnownPeers.Any(p => p.Name == "Andre"),
            "peers must arrive with their NAMES, not just addresses - a list of IP addresses is no use to anyone");

        // A peer list that CHANGES WITHOUT CHANGING SIZE. Somebody leaving as somebody else joins is
        // the ordinary case in a session, and the client compared only the COUNT — so at the moment
        // the track's peer list changed underneath the user, the log said nothing at all. Found by
        // reading the code 2026-08-24; this fails against the count-only version.
        var swapNotices = new List<string>();
        plugin.Notable += swapNotices.Add;
        host.PeerListSource = () => [(andre.Address, "Andre"), (IPAddress.Parse("192.168.1.77"), "Jonathan")];
        plugin.Hello();
        Check(WaitUntil(() => plugin.KnownPeers.Any(p => p.Name == "Jonathan")),
            "the swapped-in peer must reach the plugin");
        Check(swapNotices.Any(m => m.Contains("peer list", StringComparison.OrdinalIgnoreCase)),
            "a peer list that swaps one person for another — same COUNT, different people — must still be reported. "
            + "Comparing the length alone stays silent at exactly the moment the user's choices changed");
        plugin.Notable -= swapNotices.Add;
        host.PeerListSource = () => [(andre.Address, "Andre"), (chris.Address, "Chris")];
        plugin.Hello();
        Check(WaitUntil(() => plugin.KnownPeers.Any(p => p.Name == "Chris")), "and the original list must come back");

        var beforeClaim = PeakOfMix(engine, mixBuffer);
        Check(beforeClaim > 0.6f, $"both peers must start out audible on the speakers (peak {beforeClaim:0.000})");

        // --- The claim, over the real link ---------------------------------------------------------
        plugin.SetReceivedPeers([andre.Address]);
        var peak = PumpPlugin(plugin, frames: 256, rounds: 60);
        Check(peak > 0.3f, $"the claimed peer's audio must actually REACH the plugin - this is the plugin working or not ({peak:0.000})");
        Check(peak < 0.9f, $"and arrive at its own level, not clipped or doubled ({peak:0.000})");

        Check(host.Claims.IsClaimed(andre.Address), "asking for audio must claim the peer - the ask IS the claim");
        var afterClaim = PeakOfMix(engine, mixBuffer);
        Check(afterClaim < beforeClaim - 0.2f,
            $"THE DOUBLE-AUDIO TEST: with the plugin receiving Andre, Andre must be gone from the speakers (before {beforeClaim:0.000}, after {afterClaim:0.000})");
        Check(afterClaim > 0.05f, $"...but Chris, who nobody claimed, must still be playing ({afterClaim:0.000})");

        // --- Switching peers must let the old one go AT ONCE ----------------------------------------
        plugin.SetReceivedPeers([chris.Address]);
        PumpPlugin(plugin, frames: 256, rounds: 20);
        Check(!host.Claims.IsClaimed(andre.Address),
            "switching a track to another peer must release the first IMMEDIATELY - waiting for a timeout would leave them mute for five seconds with nothing to explain it");
        Check(host.Claims.IsClaimed(chris.Address), "and claim the new one");

        // --- A second instance on the same peer ------------------------------------------------------
        using (var second = new PluginBridgeClient(host.Port))
        {
            second.Hello();
            second.SetReceivedPeers([chris.Address]);
            var secondPeak = PumpPlugin(second, frames: 256, rounds: 40);
            Check(secondPeak > 0.05f, $"a second plugin on the SAME peer must also get audio ({secondPeak:0.000})");
            Check(host.Claims.ClaimCount(chris.Address) == 2,
                "both instances must be counted, so one closing does not un-mute the peer under the other");
        }

        // --- Letting go: the peer returns to the speakers ----------------------------------------------
        plugin.SetReceivedPeers([]);
        Check(WaitUntil(() => !host.Claims.IsClaimed(chris.Address)),
            "leaving a peer must reach the app - otherwise it stays mute in RemSound until a timeout the user cannot see");
        host.Sweep();
        // Top both peers up first. The pump above genuinely drained them — a claimed peer's buffer
        // really is consumed by the plugin — so without this the final check would be measuring an
        // empty buffer rather than whether the claim was lifted.
        FillSession(andreSession, 0.5f);
        FillSession(chrisSession, 0.25f);
        var restored = PeakOfMix(engine, mixBuffer);
        Check(restored > 0.5f, $"with every claim gone, both peers must be back on the speakers (peak {restored:0.000})");

        // --- What happens when RemSound ISN'T there ----------------------------------------------------
        // A plugin loaded with the app closed, or the link switched off, must go quiet and SAY so -
        // not crash a DAW, and not sit there looking like it is working.
        using (var orphan = new PluginBridgeClient(FreeLoopbackPort()))
        {
            orphan.Hello();
            orphan.SetReceivedPeers([andre.Address]);
            var quiet = PumpPlugin(orphan, frames: 256, rounds: 10);
            Check(quiet == 0f, "with no app listening the plugin must produce silence, not noise");
            Check(!orphan.Connected, "and must report that it is not connected, so the status line can say so plainly");
            Check(orphan.StarvedBlocks > 0, "starved blocks must be counted - a rising count is what tells a user the app is not keeping up");
        }

        return "peer list, claim, audio and release all cross the real link; a claimed peer provably leaves the speakers "
             + "and an unclaimed one stays; switching releases at once; two instances share a peer; with no app the plugin goes silent and says so";
    }

    /// <summary>SEVERAL INSTANCES AT ONCE — three, five, thirty tracks, each on its own person or
    /// several on the same one, none of them disturbing the others.
    ///
    /// <para>Anthony Reyers, 2026-08-28: "having two instances in two tracks that try to receive the
    /// same or different peer makes everything garbled, the peer is never received and the first
    /// peer's audio garbles up". Two separate faults wearing one symptom.</para>
    ///
    /// <para><b>Different people.</b> The claimed-peer read could match a session by StreamId alone,
    /// as a fallback for a peer whose network path had moved. But a stream id is chosen by the SENDER
    /// and in every single-lane mode each peer sends on the same one, so two claimed peers collided as
    /// a matter of routine: the read for the first adopted the second's session, summed them together
    /// and drained the second's ring, and the second's own instance then found the session marked as
    /// somebody else's and skipped it. One track garbled, one silent — his report exactly. Note that
    /// every other plugin test here gives each peer a DIFFERENT stream id, which is why the gate never
    /// saw it; this one deliberately gives them all the same one, as the senders really do.</para>
    ///
    /// <para><b>The same person.</b> Reading a peer CONSUMES their jitter buffer, so instances sharing
    /// one peer took alternate slices of it and each got a stuttering fraction while the ring ran dry
    /// under all of them. The claim register always allowed this; the read path could not honour it.</para>
    ///
    /// <para>Levels are the measurement, not merely "some audio arrived": a peak proves WHOSE audio it
    /// is, and a sum of two people is a different number from either of them. Served-block counts are
    /// the measurement for sharing, because a stream split three ways still peaks correctly and only
    /// the count shows that two thirds of it went missing.</para></summary>
    private static string? SeveralPluginInstancesAtOnce()
    {
        var andre = new IPEndPoint(IPAddress.Parse("192.168.1.60"), 47830);
        var chris = new IPEndPoint(IPAddress.Parse("192.168.1.61"), 47830);
        var jonathan = new IPEndPoint(IPAddress.Parse("192.168.1.62"), 47830);
        // The same id for all three, because that is what a real sender does.
        const ushort SharedStreamId = 1;

        // --- THREE INSTANCES, THREE DIFFERENT PEOPLE, ALL THREE CONFIGURATIONS --------------------
        // Not exempt from the axis: this is a rendering read, and it filters mirror replicas, which
        // only exist when two output lanes are ticked. A pass in one configuration would prove nothing
        // about the one where a peer gains a second copy of itself.
        var levels = "";
        foreach (var configuration in AudioConfigurations.All)
        {
            var engine = new PlayoutEngine(new ReceiverDiagnostics());
            engine.SetIndependentLaneLatency(configuration.HasTwoLanes());
            engine.SetLaneActive(RenderRoute.WasapiLane, configuration.UsesWasapi());
            engine.SetLaneActive(RenderRoute.AsioLane, configuration.UsesAsio());
            engine.SetMaxLatencyMs(RenderRoute.Mixed, 30);
            engine.SetMaxLatencyMs(RenderRoute.WasapiLane, 30);
            engine.SetMaxLatencyMs(RenderRoute.AsioLane, 30);

            var a = engine.GetOrCreateSession(andre, SharedStreamId, 4 * 1024 * 1024);
            var c = engine.GetOrCreateSession(chris, SharedStreamId, 4 * 1024 * 1024);
            var j = engine.GetOrCreateSession(jonathan, SharedStreamId, 4 * 1024 * 1024);
            // Three distinct levels, so a peak says WHO arrived and not merely that something did.
            for (var i = 0; i < 4; i++) { FillSession(a, 0.5f); FillSession(c, 0.25f); FillSession(j, 0.125f); }

            var claims = new PluginPeerClaims();
            engine.SetPluginPeerClaims(claims);
            claims.Claim(andre.Address, Guid.NewGuid());
            claims.Claim(chris.Address, Guid.NewGuid());
            claims.Claim(jonathan.Address, Guid.NewGuid());

            var peakAndre = PeakOfClaimedPeer(engine, andre.Address);
            var peakChris = PeakOfClaimedPeer(engine, chris.Address);
            var peakJonathan = PeakOfClaimedPeer(engine, jonathan.Address);
            levels += $"{configuration}: {peakAndre:0.00}/{peakChris:0.00}/{peakJonathan:0.00}  ";

            Check(peakAndre > 0.4f && peakAndre < 0.6f,
                $"[{configuration}] the first instance must get ITS OWN peer at their own level, not a mix of everybody "
                + $"(expected ~0.50, got {peakAndre:0.000}) - summing two peers together is the garbling that was reported");
            Check(peakChris > 0.2f && peakChris < 0.3f,
                $"[{configuration}] the second instance must get its own peer too (expected ~0.25, got {peakChris:0.000})");
            Check(peakJonathan > 0.06f && peakJonathan < 0.19f,
                $"[{configuration}] and the third (expected ~0.125, got {peakJonathan:0.000}) - a peer that reads as silence "
                + "is one whose session another instance took ownership of");
        }

        // --- SEVERAL INSTANCES ON THE SAME PERSON, OVER THE REAL LINK ----------------------------
        // Bridge-level, so one configuration: the fan-out lives in PluginBridgeHost and copies raw
        // float between app and DAW, nowhere near an output lane.
        var shared = new PlayoutEngine(new ReceiverDiagnostics());
        shared.SetLaneActive(RenderRoute.WasapiLane, true);
        shared.SetLaneActive(RenderRoute.AsioLane, false);
        shared.SetMaxLatencyMs(RenderRoute.Mixed, 30);
        var sharedSession = shared.GetOrCreateSession(andre, SharedStreamId, 8 * 1024 * 1024);
        // Enough for every instance to have read the whole stream SEPARATELY, so a starved instance
        // is proof of the split and never of an empty buffer.
        for (var i = 0; i < 8; i++) FillSession(sharedSession, 0.5f);

        using var host = new PluginBridgeHost(shared.ReadClaimedPeer, port: 0);
        shared.SetPluginPeerClaims(host.Claims);
        host.PeerListSource = () => [(andre.Address, "Andre")];

        const int Instances = 4;
        var clients = new PluginBridgeClient[Instances];
        try
        {
            for (var k = 0; k < Instances; k++)
            {
                clients[k] = new PluginBridgeClient(host.Port);
                clients[k].Hello();
                clients[k].SetReceivedPeers([andre.Address]);
            }

            var block = new float[256 * 2];
            var peaks = new float[Instances];
            for (var round = 0; round < 60; round++)
            {
                for (var k = 0; k < Instances; k++)
                {
                    var got = clients[k].ReadPeerBlock(block, 256);
                    for (var i = 0; i < got * 2; i++) peaks[k] = Math.Max(peaks[k], Math.Abs(block[i]));
                }
                Thread.Sleep(2);   // the replies arrive on the bridge thread, exactly as in a DAW
            }

            Check(host.Claims.ClaimCount(andre.Address) == Instances,
                $"all {Instances} instances must be counted on the one peer, so one closing cannot un-mute them under the others "
                + $"(got {host.Claims.ClaimCount(andre.Address)})");

            var best = 0L;
            for (var k = 0; k < Instances; k++) best = Math.Max(best, clients[k].ServedBlocks);
            for (var k = 0; k < Instances; k++)
            {
                Check(peaks[k] > 0.4f,
                    $"instance {k + 1} of {Instances} on the same peer must get that peer at full level (got {peaks[k]:0.000})");
                Check(clients[k].ServedBlocks * 10 >= best * 6,
                    $"instance {k + 1} of {Instances} must get a WHOLE copy of the stream, not a share of it "
                    + $"(served {clients[k].ServedBlocks}, best-served instance got {best}) - reading a peer consumes their "
                    + "buffer, so without fan-out each instance ends up with roughly one Nth of the audio");
            }

            // One leaving must not disturb the rest: the others keep their claim and keep their audio.
            clients[0].SetReceivedPeers([]);
            Check(WaitUntil(() => host.Claims.ClaimCount(andre.Address) == Instances - 1),
                "one instance letting go must drop exactly one claim, leaving the others holding the peer");
            var afterLeaving = PumpPlugin(clients[1], frames: 256, rounds: 30);
            Check(afterLeaving > 0.4f,
                $"and the instances still listening must carry on undisturbed (peak {afterLeaving:0.000})");

            return $"three instances on three different people each get only their own, at their own level, in all three "
                 + $"configurations ({levels.TrimEnd()}); {Instances} instances on ONE person each get a whole copy of the "
                 + $"stream over the real link, and one leaving does not disturb the rest";
        }
        finally
        {
            foreach (var client in clients) client?.Dispose();
        }
    }

    /// <summary>THE STALL Anthony Reyers hit: audio for a few seconds, then the peer is handed back.
    ///
    /// <para>Reproduced from his app log. A peer's stream session is PRUNED when it goes briefly idle
    /// and a fresh one opens moments later — which happens constantly in real use, because a peer
    /// reachable on both a LAN address and a Tailscale address alternates between them. His session
    /// shows that churn 44 times in 24 minutes.</para>
    ///
    /// <para>From the moment of the first prune, the app served the plugin nothing: blocksOut froze at
    /// 5756 and unknownPeer climbed by 375 a second for the next 201 seconds, while the app still
    /// reported the peer as claimed. So this drives the same shape — claim, then churn the session —
    /// and asserts audio keeps flowing.</para></summary>
    private static string? PluginSurvivesSessionChurn()
    {
        var peer = new IPEndPoint(IPAddress.Parse("100.118.45.53"), 47830);
        var engine = new PlayoutEngine(new ReceiverDiagnostics());
        engine.SetLaneActive(RenderRoute.WasapiLane, true);
        engine.SetLaneActive(RenderRoute.AsioLane, false);
        engine.SetMaxLatencyMs(RenderRoute.Mixed, 30);

        var claims = new PluginPeerClaims();
        engine.SetPluginPeerClaims(claims);
        var instance = Guid.NewGuid();
        claims.Claim(peer.Address, instance);

        var block = new float[128 * 2];
        var served = 0;
        var starved = 0;

        // Four rounds of the churn from the log: a session opens, carries audio, is pruned, and a new
        // one opens under a NEW stream id (his log shows the sender reusing 7582, but a new id is the
        // harder case and both must work).
        for (ushort round = 1; round <= 4; round++)
        {
            var session = engine.GetOrCreateSession(peer, round, 4 * 1024 * 1024);
            FillSession(session, 0.5f);

            var producedThisRound = 0;
            for (var i = 0; i < 40; i++)
            {
                var got = engine.ReadClaimedPeer(peer.Address, block, 128);
                if (got > 0) { producedThisRound++; served++; } else starved++;
            }
            Check(producedThisRound > 20,
                $"round {round}: the plugin must get audio from the peer's CURRENT session "
              + $"({producedThisRound} of 40 reads produced audio). A session that opened after the claim must still be readable");

            engine.RemoveSession(peer, round);   // the prune
        }

        // --- THE ACTUAL STALL: one person, two addresses ------------------------------------------
        // Anthony's peer was an iPhone reachable both over Tailscale (100.118.45.53) and on the LAN
        // (192.168.69.44). The SAME stream id, 7582, appears on both — one sender, one stream, two
        // paths. When the audio moved to the other path the plugin was left holding an address that
        // no longer carried anything, and RemSound served it nothing for the next three minutes while
        // still reporting the peer as claimed.
        var lanAddress = new IPEndPoint(IPAddress.Parse("192.168.69.44"), 47830);
        const ushort SharedStream = 7582;

        // The peer starts on the address the plugin was given, and audio flows.
        var viaTailscale = engine.GetOrCreateSession(peer, SharedStream, 4 * 1024 * 1024);
        FillSession(viaTailscale, 0.5f);
        Check(engine.ReadClaimedPeer(peer.Address, block, 128) > 0, "audio must flow on the address the plugin was given");

        // Now the sender moves to its other path, exactly as the log shows: the old session is pruned
        // and the same stream reappears from a different address.
        engine.RemoveSession(peer, SharedStream);
        var viaLan = engine.GetOrCreateSession(lanAddress, SharedStream, 4 * 1024 * 1024);
        FillSession(viaLan, 0.5f);

        var afterMove = 0;
        for (var i = 0; i < 40; i++) if (engine.ReadClaimedPeer(peer.Address, block, 128) > 0) afterMove++;
        Check(afterMove > 20,
            $"THE STALL: when a peer moves to another address the plugin must keep receiving them ({afterMove} of 40 reads "
          + "produced audio). A peer is a PERSON, not an address - the same sender on a second network path is still the "
          + "person the plugin claimed, and holding the old address is what silenced the track after a few seconds");

        // ...and they must not come back through the speakers on the new address either, or the user
        // hears them twice the moment the path changes.
        var mix = new byte[960 * 8];
        FillSession(viaLan, 0.5f);
        var leak = PeakOfMix(engine, mix);
        Check(leak < 0.05f,
            $"a claimed peer must stay off the speakers on EVERY address they are reachable at, not just the one the "
          + $"plugin named (peak {leak:0.000})");

        return $"survives 4 rounds of session churn ({served} reads produced audio); and follows a peer that moves to a "
             + "second network path, without letting them back onto the speakers";
    }

    /// <summary>Drive the plugin's audio thread the way a DAW would: read a block, let the reply land,
    /// read the next. Returns the loudest sample seen, so a test can assert on real audio rather than
    /// on "a message went past".</summary>
    private static float PumpPlugin(PluginBridgeClient client, int frames, int rounds)
    {
        var block = new float[frames * 2];
        var peak = 0f;
        for (var i = 0; i < rounds; i++)
        {
            var got = client.ReadPeerBlock(block, frames);
            for (var j = 0; j < got * 2; j++) peak = Math.Max(peak, Math.Abs(block[j]));
            Thread.Sleep(2);   // the reply arrives on the bridge thread, exactly as it does in a DAW
        }
        return peak;
    }

    /// <summary>Wait for something that depends on a datagram arriving. Loopback is fast but not
    /// instantaneous, and a fixed sleep would be either flaky or slow - this is both quick and firm.</summary>
    private static bool WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(5);
        }
        return condition();
    }

    /// <summary>A loopback port with nothing listening on it - for the "RemSound isn't running" case.</summary>
    private static int FreeLoopbackPort()
    {
        using var probe = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    /// <summary>The DAW plugin menu must exist, say the truth about what is installed, and carry the
    /// pan/EQ choice — Ed's layout: "build a menu called DAW plugin and have installed, or uninstall
    /// and the item to disable or enable eq pan etc in there".</summary>
    private static string? DawPluginMenu()
    {
        MainForm? form = null;
        try
        {
            var profile = Profile.NewBlank();
            profile.Password = RemSoundCrypto.Obfuscate("menu-test-password");
            try { form = new MainForm(null, profile, null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }

            var strip = form.MainMenuStrip ?? FindMenuStrip(form);
            Check(strip is not null, "the main window must have a menu strip");
            var menu = strip!.Items.OfType<ToolStripMenuItem>()
                .FirstOrDefault(m => m.AccessibleName == "DAW plugin menu");
            Check(menu is not null, "there must be a DAW plugin menu — the plugin is its own way of using RemSound, not a variation on the service");

            var items = menu!.DropDownItems.OfType<ToolStripMenuItem>().ToList();
            var install = items.FirstOrDefault(i => i.AccessibleName == "Install plugin");
            var remove = items.FirstOrDefault(i => i.AccessibleName == "Remove plugin");
            var shaping = items.FirstOrDefault(i => i.AccessibleName == "Apply pan and EQ to plugin audio");
            var link = items.FirstOrDefault(i => i.AccessibleName == "Let plugins connect to RemSound");
            Check(install is not null, "the menu must offer Install");
            Check(remove is not null, "the menu must offer Remove");
            Check(shaping is not null, "the menu must carry the pan/EQ choice");
            Check(link is not null,
                "the menu must carry the link on/off switch - somebody with no DAW is entitled to have RemSound listening on nothing");
            Check(link!.CheckOnClick, "the link item must be a tick, so a screen reader announces its state");
            // Pin to non-null locals so the checks below read plainly.
            var installItem = install!;
            var removeItem = remove!;
            var shapingItem = shaping!;
            Check(shapingItem.CheckOnClick, "the pan/EQ item must be a tick, so a screen reader announces its state");

            // Opening the menu must reflect reality rather than offering an action that will fail.
            // Drives the REAL refresh the menu runs on open, via a seam — reflecting into WinForms
            // internals to fake the event was brittle and simply returned null.
            form.RefreshDawPluginMenuForTest();
            var installed = PluginInstaller.IsInstalled();
            Check(removeItem.Enabled == installed,
                $"Remove must be enabled only when a plugin is actually installed (installed={installed}, enabled={removeItem.Enabled})");
            var installText = installItem.Text ?? "";
            Check(installText.Contains(installed ? "Reinstall" : "Install", StringComparison.OrdinalIgnoreCase),
                $"Install must rename itself to Reinstall when one is present (installed={installed}, got '{installText}')");

            // The setting behind the tick must persist, in an isolated config — never the real one.
            var scratch = Path.Combine(Path.GetTempPath(), "remsound-plugin-menu-" + Guid.NewGuid().ToString("N"));
            using (AppConfig.UseThrowawayUserDataDirectory(scratch))
            {
                Check(AppConfig.Load().ApplyPeerShapingToPlugin, "pan and EQ must reach the DAW by DEFAULT — what you hear is the least surprising behaviour");
                var cfg = AppConfig.Load();
                cfg.ApplyPeerShapingToPlugin = false;
                cfg.Save();
                Check(!AppConfig.Load().ApplyPeerShapingToPlugin, "turning it off must survive a save and reload");

                // ON by default: a plugin that quietly does nothing until you find a hidden switch is
                // worse than no plugin at all.
                Check(AppConfig.Load().EnableDawPluginLink, "plugins must be able to connect by DEFAULT");
                var linkCfg = AppConfig.Load();
                linkCfg.EnableDawPluginLink = false;
                linkCfg.Save();
                Check(!AppConfig.Load().EnableDawPluginLink, "switching the link off must survive a save and reload");
            }
            return "DAW plugin menu present with install, remove, the pan/EQ tick and the link switch; the menu states what is actually installed; both settings round-trip and default to on";
        }
        finally { try { form?.Dispose(); } catch { } }
    }

    /// <summary>The app-plugin link: framing, rejection of anything that isn't ours, loopback-only
    /// enforcement, and — the number Ed actually asked for — HOW MUCH LATENCY IT ADDS.
    ///
    /// "let's see how latent it makes it by doing that. hopefully negligible." So it is measured here,
    /// on this machine, rather than asserted.</summary>
    private static string? PluginBridgeLinkTest()
    {
        var peer = IPAddress.Parse("192.168.1.50");
        var instance = PluginBridgeProtocol.InstanceHash(Guid.NewGuid());

        // --- Framing: a message must survive the round trip exactly -----------------------------
        var buffer = new byte[PluginBridgeProtocol.HeaderSize + 64];
        PluginBridgeProtocol.WriteHeader(buffer, PluginBridgeMessage.ClaimPeers, instance, peer, 8);
        Check(PluginBridgeProtocol.TryReadHeader(buffer.AsSpan(0, PluginBridgeProtocol.HeaderSize + 8),
                out var type, out var hash, out var readPeer, out var len),
            "a well-formed bridge message must parse");
        Check(type == PluginBridgeMessage.ClaimPeers && hash == instance && len == 8, "every header field must round-trip");
        Check(readPeer is not null && readPeer.Equals(peer), "the peer address must round-trip");

        PluginBridgeProtocol.WriteHeader(buffer, PluginBridgeMessage.Hello, instance, null, 0);
        Check(PluginBridgeProtocol.TryReadHeader(buffer.AsSpan(0, PluginBridgeProtocol.HeaderSize), out _, out _, out var noPeer, out _)
              && noPeer is null, "a message about no particular peer must report no peer, not 0.0.0.0");

        // --- Rejection: a local socket receives whatever the OS hands it -------------------------
        Check(!PluginBridgeProtocol.TryReadHeader(new byte[4], out _, out _, out _, out _), "a runt must be rejected, not throw");
        var wrongMagic = (byte[])buffer.Clone();
        wrongMagic[0] = 82; wrongMagic[1] = 77; wrongMagic[2] = 78; wrongMagic[3] = 68; // "RMND"
        Check(!PluginBridgeProtocol.TryReadHeader(wrongMagic, out _, out _, out _, out _),
            "a NETWORK packet must never parse as a bridge message - mixing the two would leak decrypted audio onto the wire");
        var futureVersion = (byte[])buffer.Clone();
        futureVersion[4] = 99;
        Check(!PluginBridgeProtocol.TryReadHeader(futureVersion, out _, out _, out _, out _), "an unknown version must be rejected");
        var badType = (byte[])buffer.Clone();
        badType[5] = 200;
        Check(!PluginBridgeProtocol.TryReadHeader(badType, out _, out _, out _, out _), "an unknown message type must be rejected");
        var lying = (byte[])buffer.Clone();
        lying[6] = 255; lying[7] = 255;
        Check(!PluginBridgeProtocol.TryReadHeader(lying, out _, out _, out _, out _),
            "a length beyond the maximum payload must be rejected, not read past the end");

        // And a length that overruns the BUFFER while staying UNDER the maximum. The case above
        // claims 65535 bytes, which the size cap rejects on its own — so the overrun check was never
        // once exercised, and deleting it left this step green (found 2026-08-24). This is the shape
        // that matters: the bridge socket accepts whatever the OS hands it, so any local process can
        // send a 24-byte datagram claiming 1000 bytes of payload, and without the check the caller
        // slices past the end of the buffer on the audio path.
        Check(1000 < PluginBridgeProtocol.MaxAudioBytes,
            "this case only isolates the overrun check while the claim stays UNDER the size cap");
        var overrun = new byte[PluginBridgeProtocol.HeaderSize + 8];
        PluginBridgeProtocol.WriteHeader(overrun, PluginBridgeMessage.ClaimPeers, instance, peer, 8);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(overrun.AsSpan(6), 1000);
        Check(!PluginBridgeProtocol.TryReadHeader(overrun, out _, out _, out _, out _),
            "a payload length that overruns the received datagram must be rejected even when it is within the size cap — "
            + "otherwise the caller reads past the end of the buffer on the audio thread");

        // --- The live link, and the latency it costs ----------------------------------------------
        using var host = new PluginBridgeLink(0);   // port 0: never collide with a running RemSound
        using var client = new PluginBridgeLink(0);
        var hostEnd = new IPEndPoint(IPAddress.Loopback, host.Port);

        // EVERY PAYLOAD SIZE, byte-exact, including both ends of the range.
        //
        // Send used to reserve the maximum payload on the stack whatever it was actually sending, and
        // now reserves what it needs. That is a change of arithmetic on the hot path, so the risk it
        // carries is an off-by-one at a boundary — an empty control message, or a full-sized block.
        // These cases are here to catch that. They would NOT have failed against the old code: this
        // guards the fix rather than proving the bug, which is worth saying plainly. 2026-08-24.
        var sizes = new List<int>();
        var echoed = new System.Collections.Concurrent.BlockingCollection<byte[]>();
        host.MessageReceived += (_, _, _, payload, _) => echoed.Add(payload.ToArray());
        foreach (var size in new[] { 0, 1, 4, 1000, PluginBridgeProtocol.MaxAudioBytes })
        {
            var body = new byte[size];
            for (var i = 0; i < size; i++) body[i] = (byte)(i * 7 + 3);
            Check(client.Send(hostEnd, PluginBridgeMessage.TrackAudio, instance, null, body),
                $"a {size}-byte payload must send");
            Check(echoed.TryTake(out var got, 2000) && got.AsSpan().SequenceEqual(body),
                $"a {size}-byte payload must arrive byte-exact — the send buffer is now sized to the payload, so the "
                + "ends of the range are where an arithmetic slip would show");
            sizes.Add(size);
        }
        Check(!client.Send(hostEnd, PluginBridgeMessage.TrackAudio, instance, null, new byte[PluginBridgeProtocol.MaxAudioBytes + 1]),
            "and one byte past the maximum must still be refused rather than truncated");
        Check(sizes.Count == 5, "every payload size in the sweep must have been exercised");

        // The host echoes a track block straight back as peer audio - a full ROUND TRIP, which is
        // strictly more than the real path costs (that is one hop each way, not two).
        host.MessageReceived += (t, h, p, payload, from) =>
        {
            if (t == PluginBridgeMessage.TrackAudio) host.Send(from, PluginBridgeMessage.PeerAudioRound, h, p, payload.Span);
        };

        var returned = new SemaphoreSlim(0);
        var receivedBytes = 0;
        client.MessageReceived += (t, _, _, payload, _) =>
        {
            if (t != PluginBridgeMessage.PeerAudioRound) return;
            receivedBytes = payload.Length;
            returned.Release();
        };

        // One DAW block of stereo float at 48k/256 frames - a typical low-latency buffer (5.3 ms).
        var block = new byte[256 * 2 * sizeof(float)];
        Random.Shared.NextBytes(block);

        Check(client.Send(hostEnd, PluginBridgeMessage.TrackAudio, instance, peer, block), "a block must send");
        Check(returned.Wait(TimeSpan.FromSeconds(5)), "the block must come back - the link must actually carry audio");
        Check(receivedBytes == block.Length, $"the block must arrive whole ({receivedBytes} of {block.Length} bytes)");

        // MEASURE. Warm up first so JIT and socket setup do not land in the numbers.
        for (var i = 0; i < 20; i++) { client.Send(hostEnd, PluginBridgeMessage.TrackAudio, instance, peer, block); returned.Wait(1000); }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int Runs = 200;
        var completed = 0;
        for (var i = 0; i < Runs; i++)
        {
            if (!client.Send(hostEnd, PluginBridgeMessage.TrackAudio, instance, peer, block)) continue;
            if (returned.Wait(1000)) completed++;
        }
        sw.Stop();
        Check(completed >= Runs * 0.95, $"the link must be reliable on loopback ({completed} of {Runs} round trips completed)");
        var roundTripMs = sw.Elapsed.TotalMilliseconds / Math.Max(1, completed);
        var oneWayMs = roundTripMs / 2;

        // The honest bar: the hop must cost far less than one audio block, or it would be the
        // DOMINANT term in the plugin's latency rather than a rounding error.
        Check(oneWayMs < 1.0,
            $"the app-plugin hop must be negligible against an audio block (measured {oneWayMs:0.000} ms one way, block is 5.3 ms)");

        // Loopback-only must be ENFORCED, not merely intended: a bug that let this reach a routable
        // address would stream a peer's DECRYPTED audio onto the network in the clear.
        Check(!client.Send(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 47831), PluginBridgeMessage.TrackAudio, instance, peer, block),
            "the link must REFUSE to send anywhere but loopback - decrypted audio must never leave the machine");
        Check(!client.Send(new IPEndPoint(IPAddress.Parse("192.168.1.99"), 47831), PluginBridgeMessage.TrackAudio, instance, peer, block),
            "not even to a local-network address");

        return $"framing round-trips and rejects network packets, runts, bad versions and lying lengths; loopback-only enforced; "
             + $"measured {oneWayMs:0.000} ms one way ({roundTripMs:0.000} ms round trip over {completed} runs) - against a 5.3 ms audio block";
    }

    /// <summary>Walk the control tree for the menu strip — a headless form may not have it hooked to
    /// the Form.MainMenuStrip property.</summary>
    private static MenuStrip? FindMenuStrip(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is MenuStrip ms) return ms;
            var nested = FindMenuStrip(c);
            if (nested is not null) return nested;
        }
        return null;
    }

    /// <summary>
    /// AUDIT TICK1: the once-a-second work that must ALWAYS happen must not be behind the log switch.
    ///
    /// <para>Two things had drifted below a <c>if (!DiagnosticsGate.Enabled) return;</c> at the top of
    /// the app's per-second tick, so with "write log file" turned off and auto-tune off, neither ran:
    /// the plugin sweep and the recording-losing-audio warning. They were appended to the end of the
    /// method as it grew, under a return nobody looked up at.</para>
    ///
    /// <para>The sweep is the one that matters. It is the fix for Anthony Reyers's 2026-08-16 report —
    /// a DAW killed outright never says goodbye, and until the sweep ran every second the peer it had
    /// claimed stayed silent in RemSound until some other code path happened to ask. "Intermittent,
    /// the worst possible bug." That fix was quietly conditional on logging being switched on.</para>
    ///
    /// <para>Driven, not read: the tick is run for real with the gate off, and the claim is watched to
    /// see whether it is actually reaped.</para>
    /// </summary>
    private static string? AuditTickWorkIsNotBehindTheLogSwitch()
    {
        MainForm form;
        try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
        catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }

        var restoreGate = DiagnosticsGate.Enabled;
        try
        {
            using var _ = form;
            var host = form.OpenPluginLinkForTest(0);
            if (host is null) return Skip("the plugin link could not be opened on this machine");

            using var plugin = new PluginBridgeClient(host.Port);
            plugin.Hello();
            Check(WaitUntil(() => host.InstanceCount == 1), "the plugin must register with the app before the tick is tested");

            // Everything off: no logging, no auto-tune. This is a perfectly ordinary way to run the
            // app, and it is the state in which the sweep silently stopped happening.
            DiagnosticsGate.Enabled = false;

            // Let the instance go stale. The claim timeout is the sweep's own threshold, so waiting it
            // out is the only honest way to reach the state a dead DAW leaves behind.
            Thread.Sleep(PluginPeerClaims.ClaimTimeout + TimeSpan.FromMilliseconds(600));
            Check(DiagnosticsGate.Enabled == false, "the diagnostics gate must still be off — something re-enabled it mid-test");

            form.SnapshotTickForTest();

            Check(host.InstanceCount == 0,
                "with logging OFF, the per-second tick must STILL sweep a plugin instance that has gone quiet. It sat "
                + "below the diagnostics early-return, so a user who turned the log file off lost the fix for a DAW "
                + "that was killed — the peer it had claimed stays silent in RemSound with nothing to explain it");
            return "with the log switch off and auto-tune off, the per-second tick still reaps a plugin whose DAW died";
        }
        finally { DiagnosticsGate.Enabled = restoreGate; }
    }

    /// <summary>Installing and removing the plugin must be exact. The VST3 folder is shared with
    /// every other plugin the user owns, so an over-enthusiastic uninstall would delete somebody
    /// else's work — this proves it removes only what it placed, and that a portable install needs
    /// no administrator rights (Ed, 2026-08-15: "a lot of people run it as portable").</summary>
    private static string? PluginInstallRoundTrip()
    {
        // The target must live in the USER's profile. A path under Program Files would need
        // elevation, which defeats the point of a portable copy.
        var dir = PluginInstaller.InstallDirectory;
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Check(dir.StartsWith(localApp, StringComparison.OrdinalIgnoreCase),
            $"the plugin must install per-user (no admin), not machine-wide (got {dir})");
        Check(dir.Contains("VST3", StringComparison.OrdinalIgnoreCase), "it must land in the VST3 folder a DAW scans");

        // Exercise the real install/uninstall against throwaway folders, including a BYSTANDER file
        // that must survive — the case that would otherwise delete another plugin.
        var root = Path.Combine(Path.GetTempPath(), "remsound-plugin-install-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "plugin");
        var target = Path.Combine(root, "VST3", "RemSound");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        File.WriteAllText(Path.Combine(source, "RemSound.Plugin.dll"), "plugin");
        File.WriteAllText(Path.Combine(source, "RemSoundBridge.vst3"), "bridge");
        File.WriteAllText(Path.Combine(source, "sub", "extra.dll"), "nested");
        try
        {
            var (ok, msg) = PluginInstaller.InstallForTest(source, target);
            Check(ok, $"install must succeed: {msg}");
            Check(File.Exists(Path.Combine(target, "RemSound.Plugin.dll")), "the plugin dll must be placed");
            Check(File.Exists(Path.Combine(target, "sub", "extra.dll")), "nested files must be placed too, not flattened or skipped");
            Check(PluginInstaller.IsInstalledAt(target), "an installed plugin must report itself installed");

            // Reinstall over the top — what a RemSound update does. Must refresh, not fail or double up.
            Check(PluginInstaller.InstallForTest(source, target).Ok, "reinstalling over an existing install must succeed (that is what an update does)");

            // A file that is NOT ours, sitting in the same folder. Uninstall must leave it alone.
            var bystander = Path.Combine(target, "SomeoneElsesPlugin.vst3");
            File.WriteAllText(bystander, "not ours");

            var (rok, rmsg) = PluginInstaller.UninstallForTest(target);
            Check(rok, $"uninstall must succeed: {rmsg}");
            Check(!File.Exists(Path.Combine(target, "RemSound.Plugin.dll")), "our files must be gone");
            Check(File.Exists(bystander), "a file we did not install MUST survive — the VST3 folder is shared with every other plugin");
            Check(!PluginInstaller.IsInstalledAt(target), "after removal it must no longer report itself installed");

            // Removing when nothing is installed must say so rather than throw or claim success.
            Check(!PluginInstaller.UninstallForTest(target).Ok, "removing a plugin that isn't installed must report that plainly");

            // --- A manifest that points OUTSIDE our folder ------------------------------------------
            // UninstallCore refuses any manifest line that escapes the install directory. Nothing
            // exercised it: every case above uses a manifest we wrote ourselves, so deleting the
            // guard left this step green (2026-08-24). The manifest is a plain text file in a folder
            // shared with every plugin the user owns — a corrupt line, a hand-edit, a symlink in the
            // source tree — and without the guard an uninstall walks out of its own folder and starts
            // deleting somebody else's work.
            Check(PluginInstaller.InstallForTest(source, target).Ok, "reinstall for the tampered-manifest case must succeed");
            var neighbour = Path.Combine(root, "VST3", "AnotherVendor.vst3");
            File.WriteAllText(neighbour, "somebody else's plugin, one folder up");
            var manifest = Path.Combine(target, PluginInstaller.ManifestName);
            File.WriteAllLines(manifest, File.ReadAllLines(manifest).Concat([@"..\AnotherVendor.vst3"]));

            var (tok, _) = PluginInstaller.UninstallForTest(target);
            Check(tok, "an uninstall must still complete when the manifest has a bad line, not abort half way");
            Check(File.Exists(neighbour),
                "a manifest line pointing OUTSIDE the install folder must be refused — without that guard an uninstall "
                + "deletes another vendor's plugin out of the shared VST3 folder, and the user finds out when their DAW "
                + "stops loading it");
            Check(!File.Exists(Path.Combine(target, "RemSound.Plugin.dll")),
                "and the legitimate lines in the same manifest must still have been removed");

            return "installs per-user with no admin; nested files placed; reinstall refreshes; uninstall removes only "
                 + "its own files, leaves other plugins untouched, and refuses a manifest line that escapes its folder";
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    /// <summary>A CLAIMED PEER WHO CHANGES NETWORK PATH MUST KEEP PLAYING ON THE PLUGIN'S TRACK.
    ///
    /// <para>Two ways that goes wrong, one of each here. First: the session at the address the plugin
    /// claimed is still in the snapshot but has run dry, because the peer has moved off that path.
    /// Counting that as the answer returned silence and skipped the adoption pass entirely, so the
    /// track went quiet while the app itself still played the peer. Mine, 2026-08-29. Second: the
    /// stream arrives on a path the claimed address was NEVER seen on, so there is no stream
    /// ownership to adopt either — the plugin is holding one address of a person who answers on two,
    /// which is every phone on a LAN and a tailnet at once. That one needs the app to say which
    /// addresses are the same person.</para>
    ///
    /// <para>Levels rather than "something arrived": the wrong session and the right one both produce
    /// audio, and only the level says which.</para></summary>
    private static string? AClaimedPeerWhoChangesPath()
    {
        var lan = new IPEndPoint(IPAddress.Parse("192.168.1.70"), 47830);
        var tailnet = new IPEndPoint(IPAddress.Parse("100.70.0.1"), 47830);
        const ushort SharedStreamId = 1;

        var report = "";
        foreach (var configuration in AudioConfigurations.All)
        {
            // --- 1. THE OLD PATH IS STILL THERE AND DRY ------------------------------------------
            var engine = NewEngineFor(configuration);
            var stale = engine.GetOrCreateSession(lan, SharedStreamId, 4 * 1024 * 1024);
            var moved = engine.GetOrCreateSession(tailnet, SharedStreamId, 4 * 1024 * 1024);
            for (var i = 0; i < 4; i++) FillSession(moved, 0.25f);
            Check(stale is not null, "the stale session must exist, or this proves nothing");

            var claims = new PluginPeerClaims();
            engine.SetPluginPeerClaims(claims);
            claims.Claim(lan.Address, Guid.NewGuid());

            var afterMove = PeakOfClaimedPeer(engine, lan.Address);
            Check(afterMove > 0.2f && afterMove < 0.3f,
                $"[{configuration}] a claimed peer whose stream moved to another address must still reach the track "
                + $"(expected ~0.25, got {afterMove:0.000}) - 0.000 is the empty session at the old address being "
                + "taken for the answer");

            // --- 2. NEVER SEEN ON THE CLAIMED PATH AT ALL ----------------------------------------
            // Different stream id as well, so there is no ownership to fall back on: only knowing that
            // the two addresses are one person can rescue this.
            var second = NewEngineFor(configuration);
            var onlyPath = second.GetOrCreateSession(lan, 7, 4 * 1024 * 1024);
            for (var i = 0; i < 4; i++) FillSession(onlyPath, 0.5f);
            var otherClaims = new PluginPeerClaims();
            second.SetPluginPeerClaims(otherClaims);
            otherClaims.Claim(tailnet.Address, Guid.NewGuid());

            var withoutGroups = PeakOfClaimedPeer(second, tailnet.Address);
            Check(withoutGroups == 0f,
                $"[{configuration}] with nothing saying the two addresses are one person there is nothing to find "
                + $"(got {withoutGroups:0.000}) - if this passes audio the test below proves nothing");

            second.SetPeerAddressGroups(new[] { new[] { lan.Address, tailnet.Address } });
            var withGroups = PeakOfClaimedPeer(second, tailnet.Address);
            Check(withGroups > 0.4f && withGroups < 0.6f,
                $"[{configuration}] once the app says both addresses are the same peer, the claim must cover both "
                + $"(expected ~0.50, got {withGroups:0.000})");

            report += $"{configuration}: moved {afterMove:0.00}, other path {withGroups:0.00}  ";
        }

        return "a claimed peer keeps playing across a path change, and across a second path they answer on - " + report.Trim();
    }

    /// <summary>An engine set up for one audio configuration. The three plugin tests were each building
    /// this by hand; same six lines, three times.</summary>
    private static PlayoutEngine NewEngineFor(AudioConfiguration configuration)
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

    /// <summary>Read one claimed peer the way the bridge does, and report the peak. Goes straight at
    /// <see cref="PlayoutEngine.ReadClaimedPeer"/> so the peer-matching can be checked across all
    /// three output configurations without standing a whole DAW link up three times.</summary>
    private static float PeakOfClaimedPeer(PlayoutEngine engine, IPAddress peer, int frames = 256, int rounds = 8)
    {
        var block = new float[frames * 2];
        var peak = 0f;
        for (var round = 0; round < rounds; round++)
        {
            var got = engine.ReadClaimedPeer(peer, block, frames);
            for (var i = 0; i < got * 2; i++) peak = Math.Max(peak, Math.Abs(block[i]));
        }
        return peak;
    }

    /// <summary>Write a steady level into a session so the mix has something measurable in it.</summary>
    private static void FillSession(SessionPlayout session, float amplitude)
    {
        var block = new byte[48000 * 8 / 4]; // 250 ms stereo float
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(block.AsSpan());
        for (var i = 0; i < floats.Length; i++) floats[i] = amplitude;
        session.Write(block);
        session.NoteFramesQueued(30);
    }

    /// <summary>Pull one block from the engine the way an output device does, and report the peak —
    /// "is this peer in the mix" answered by measuring the audio, not by reading a flag.</summary>
    private static float PeakOfMix(PlayoutEngine engine, byte[] buffer)
    {
        Array.Clear(buffer);
        engine.Read(buffer, 0, buffer.Length);
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer.AsSpan());
        var peak = 0f;
        foreach (var f in floats) peak = Math.Max(peak, Math.Abs(f));
        return peak;
    }
}

internal static partial class SelfTest
{
    /// <summary>
    /// THREE CONCURRENT STREAMS FROM ONE SENDER — can the receiver actually take them?
    ///
    /// <para>The plugin's send path wants a lane of its own alongside the app's own capture. In
    /// BothIndependent the capture already holds two lanes, so that makes three streams at once from
    /// a single peer, where two is what has shipped since May. Reading the code says it should work:
    /// sessions are keyed by (endpoint, streamId), and the supersede rule only kills a session whose
    /// announced Lane MATCHES the newcomer's. But "should" is how the v5.6 iteration-count change got
    /// shipped, so this proves it instead.</para>
    ///
    /// <para>Both halves matter. The first shows three distinct lanes coexist. The second shows the
    /// supersede rule still fires when two streams DO share a lane — without it, a receiver that had
    /// quietly stopped superseding anything at all would sail through the first half for entirely the
    /// wrong reason, and the test would be worthless exactly when it mattered.</para>
    ///
    /// <para>Real socket, real datagrams, real dispatch path. No test seam in the middle of it.</para>
    /// </summary>
    private static string ThreeConcurrentStreamsFromOneSender()
    {
        int port;
        using (var probe = new System.Net.Sockets.UdpClient(0, System.Net.Sockets.AddressFamily.InterNetwork))
        {
            port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        }

        using var receiver = new AudioReceiver();
        try { receiver.Start(port); }
        catch (Exception ex) { return Skip($"could not bind test port {port}: {ex.Message}"); }
        // Decode only — no output device is ever opened, so the gate never makes a sound. Playback
        // still has to be ENABLED or the packet handler discards audio before a session exists.
        receiver.SetOutputDevices(Array.Empty<string>());
        receiver.SetPlaybackEnabled(true);

        using var wire = new System.Net.Sockets.UdpClient(System.Net.Sockets.AddressFamily.InterNetwork);
        var target = new IPEndPoint(IPAddress.Loopback, port);
        uint sequence = 0;

        void AnnounceStream(ushort streamId, RenderRoute lane)
        {
            // 48 kHz stereo PCM, 480 samples per channel — a format IsUsable accepts, so the only
            // thing under test is the multi-stream bookkeeping and not format validation.
            var format = new AudioFormatInfo(48000, 2, 16, 1, 4, 192000,
                (int)AudioTransportCodec.Pcm, 480, lane);
            var packet = new byte[RemPacket.HeaderSize + 64];
            RemPacket.WriteHeader(packet, RemPacketType.Format, streamId, sequence++);
            var written = RemPacket.WriteFormatPayload(packet.AsSpan(RemPacket.HeaderSize), format);
            wire.Send(packet, RemPacket.HeaderSize + written, target);
        }

        // The receive path is a socket thread, so settle rather than assume the datagram landed.
        int LiveAfter(int expected)
        {
            for (var i = 0; i < 100 && receiver.LiveSessionCountForTest != expected; i++) Thread.Sleep(10);
            return receiver.LiveSessionCountForTest;
        }

        // --- Part one: three lanes, three streams, all alive together. -----------------------------
        AnnounceStream(101, RenderRoute.Mixed);
        AnnounceStream(102, RenderRoute.WasapiLane);
        AnnounceStream(103, RenderRoute.AsioLane);

        var live = LiveAfter(3);
        Check(live == 3,
            $"three streams from one sender, each announcing a different lane, must all stay live — got {live}");

        // Re-announcing an existing stream must not open a second one, and must not disturb the
        // others: this is the 250 ms format resend every sender does four times a second, and it is
        // exactly what took the two lanes down before the lane-match qualifier went in on 11 May.
        AnnounceStream(101, RenderRoute.Mixed);
        AnnounceStream(102, RenderRoute.WasapiLane);
        AnnounceStream(103, RenderRoute.AsioLane);
        Thread.Sleep(50);   // settle: the count is ALREADY 3, so LiveAfter would return without waiting
        live = receiver.LiveSessionCountForTest;
        Check(live == 3, $"a format resend must not disturb any of the three sessions — got {live}");
        Check(receiver.SessionsOpenedCount == 3,
            $"a format resend must not open a second session for a stream that already exists — opened {receiver.SessionsOpenedCount}");

        // --- Part two: prove the guard still bites, or part one proved nothing. --------------------
        // A fourth stream on a lane that is ALREADY taken is a sender rotating its streamId, and the
        // one it replaces has to go, or a rotation would leak an empty session every time.
        AnnounceStream(104, RenderRoute.AsioLane);
        // Wait for the packet to be PROCESSED — a fourth session opened — before asking what survived.
        // Without this the check below passes the instant the datagram is still in flight, since the
        // count is already 3 and never moved. That is a test measuring nothing, which is worse than
        // no test: it reports PASS on a receiver that dropped the packet entirely.
        for (var i = 0; i < 200 && receiver.SessionsOpenedCount < 4; i++) Thread.Sleep(10);
        Check(receiver.SessionsOpenedCount == 4,
            $"the fourth stream must actually have been processed before this proves anything — opened {receiver.SessionsOpenedCount}");
        live = LiveAfter(3);
        Check(live == 3,
            $"a new streamId on an ALREADY-USED lane must supersede the old one, not add a fourth session — got {live}");

        return $"three streams from one peer (lanes mixed/wasapi/asio) coexist; format resends leave all three "
             + $"alone; a fourth stream reusing a lane supersedes rather than accumulates. "
             + $"{receiver.SessionsOpenedCount} sessions opened in total, {live} live at the end.";
    }
}
