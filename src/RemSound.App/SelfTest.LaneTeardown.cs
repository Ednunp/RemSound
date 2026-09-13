using System.Net;
using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>
    /// UNTICKING ONE OUTPUT LANE MUST NOT LEAVE A RING BEING WRITTEN AND NEVER READ.
    ///
    /// <para>Ed, 2026-09-07, 22:19:19. Both lanes were live on the receiving machine. He unticked the
    /// ASIO output so the interface was free for a capture test. From that exact second the ASIO lane
    /// stopped rendering — correctly — and a session ring began overflowing at 288,960 bytes per
    /// second and never stopped. It ran that way until the machine hibernated.</para>
    ///
    /// <para>The cost was not the wasted bytes. The ring filled to the one-second safety cap, was
    /// emergency-trimmed, and refilled, over and over — the 275 to 800 ms sawtooth he could see in the
    /// readout, on a lane nobody was listening to. Meanwhile the surviving lane's render cost went
    /// from about 5 ms a second to between 60 and 140, because it was feeding and overflowing a lane
    /// with no consumer in real time, forever. That overload starved the output he WAS listening on,
    /// and every starve is an underrun, and every underrun makes the auto-tune raise the buffer. So a
    /// lane he had switched off made the lane he was using audibly late.</para>
    ///
    /// <para><b>What this asserts, and why it is this and not something narrower.</b> Not "the mirror
    /// is disposed" — that names one mechanism, and I guessed at the mechanism twice and was wrong
    /// twice. The property that actually matters, whatever the plumbing does, is that after a lane is
    /// switched off nothing is still being written into a ring that nobody drains. Overflow is the
    /// direct evidence of that, and it is what the field log showed.</para>
    ///
    /// <para><b>Both ways round.</b> Nothing in the read path is lane-specific, so the test turns off
    /// ASIO with WASAPI surviving, and then WASAPI with ASIO surviving. Ed asked for both, and a fix
    /// that only covered the lane he happened to untick would be half a fix.</para>
    ///
    /// <para><b>Configuration.</b> This is the both-lanes configuration only, and deliberately so: it
    /// is the only one that HAS a second lane to switch off. In WASAPI-only and ASIO-only there is one
    /// active lane, no mirror replicas are made, and there is nothing that can be orphaned — so those
    /// two configurations cannot reach this bug rather than being untested for it.</para>
    /// </summary>
    private static string? AuditUntickedLaneIsNotFedForever()
    {
        var summaries = new List<string>();
        foreach (var (turnedOff, kept, label) in new[]
                 {
                     (RenderRoute.AsioLane, RenderRoute.WasapiLane, "ASIO off, WASAPI kept"),
                     (RenderRoute.WasapiLane, RenderRoute.AsioLane, "WASAPI off, ASIO kept"),
                 })
        {
            summaries.Add(RunUntickedLaneCase(turnedOff, kept, label));
            summaries.Add(RunStalledLaneCase(turnedOff, kept, label.Replace("off", "stalled")));
        }
        return string.Join("; ", summaries);
    }

    /// <summary>
    /// THE CASE THAT ACTUALLY BIT, and it is not the untick.
    ///
    /// <para>Unticking works — the case above proves it, and it passed the first time it was run. On
    /// Ed's machine the ASIO output stayed TICKED. What stopped was the rendering: <c>renderAsioMs</c>
    /// went to 0.0 at 22:19:19 and never came back, while the lane stayed flagged active because its
    /// device was still selected. A lane can reach that state several ways — the far end stops sending
    /// on that lane, the driver reports no output channel pairs, the device stalls without raising an
    /// error — and none of them tell the engine anything.</para>
    ///
    /// <para>So the engine kept fanning every stream out to a lane with no consumer, and that copy
    /// overflowed at 288,960 bytes a second for hours. The fix cannot be about ticking, because the
    /// tick never changed. It has to be about reading: a lane that is not being read is not a lane,
    /// whatever its device list says.</para>
    /// </summary>
    private static string RunStalledLaneCase(RenderRoute stalled, RenderRoute kept, string label)
    {
        const int frames = 480, bytesPerFrame = 8, blockBytes = frames * bytesPerFrame;
        const int capacity = 256 * 1024;

        var engine = new PlayoutEngine(new ReceiverDiagnostics());
        engine.SetLaneActive(RenderRoute.WasapiLane, true);
        engine.SetLaneActive(RenderRoute.AsioLane, true);
        engine.SetMaxLatencyMs(RenderRoute.Mixed, 60);
        engine.SetMaxLatencyMs(RenderRoute.WasapiLane, 60);
        engine.SetMaxLatencyMs(RenderRoute.AsioLane, 60);

        var peer = new IPEndPoint(IPAddress.Parse("10.0.0.11"), 47830);
        var session = engine.GetOrCreateSession(peer, 1, capacity);

        var audio = new byte[blockBytes];
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(audio.AsSpan());
        for (var i = 0; i < floats.Length; i++) floats[i] = 0.25f;
        var arming = new byte[48000 * 8 / 8];
        var armFloats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(arming.AsSpan());
        for (var i = 0; i < armFloats.Length; i++) armFloats[i] = 0.25f;
        session.Write(arming);
        session.NoteFramesQueued(60);

        var outBuf = new byte[blockBytes];
        var mixBuf = new float[frames * 2];
        var sessionBuf = new float[frames * 2];

        for (var i = 0; i < 20; i++)
        {
            session.Write(audio);
            engine.ReadForRoute(outBuf, 0, blockBytes, RenderRoute.WasapiLane, mixBuf, sessionBuf, false);
            engine.ReadForRoute(outBuf, 0, blockBytes, RenderRoute.AsioLane, mixBuf, sessionBuf, false);
        }

        // THE LANE STAYS TICKED. Nothing is unticked, no device is removed, no error is raised — the
        // lane simply stops being read, exactly as the field log shows.
        // The stall window is three real seconds by design. Push the lane's last-read stamp past it
        // rather than sleeping — the same trick the drift tracker's test uses, and for the same reason.
        Check(LaneActivity.DefaultStallSeconds == 3.0,
            $"{label}: the shipped stall window must stay 3 s (got {LaneActivity.DefaultStallSeconds}) — shorter and a "
            + "device having a bad moment under load gets treated as dead");
        engine.RewindLaneReadClockForTest(stalled, LaneActivity.DefaultStallSeconds + 1.0);

        var beforeDrops = engine.AggregateDrops;
        const int ticks = 400;   // four seconds of audio at real-time rate
        for (var i = 0; i < ticks; i++)
        {
            session.Write(audio);
            engine.ReadForRoute(outBuf, 0, blockBytes, kept, mixBuf, sessionBuf, false);
        }

        var overflowed = engine.AggregateDrops - beforeDrops;
        Check(overflowed == 0,
            $"{label}: a lane that has stopped being read must stop being FED — {overflowed} bytes overflowed in "
            + $"{ticks} render ticks with the lane still ticked. This is the one that bit: on 2026-09-07 it ran at "
            + "288,960 bytes a second for hours, and the CPU it burned starved the lane the user was listening to");

        // HEARD ONCE, AND STILL RECORDED. The lane that is still reading must play this peer at the level
        // it was sent, not twice over. When the stalled lane is the one the primary lives on, the primary
        // falls through onto the kept lane — and the kept lane has its own mirror of the same peer, which
        // used to play as well: about 6 dB hot, and comb-filtered as the two copies drift apart. And the
        // receive recording must carry on from the kept lane; it used to follow the first ticked lane
        // only, so a stalled first output stopped every recording while the peer still played.
        var recordedBlocks = 0;
        engine.OnReceivedSamples = (_, lane) => { if (lane == kept) recordedBlocks++; };
        // And the split-track tap, which rides on each copy of the stream rather than on the mix.
        var peerTrackBlocks = 0;
        engine.SetRecordTap((_, _) => peerTrackBlocks++, raw: false);
        var peak = 0f;
        var underrunsBefore = engine.AggregateUnderrunsFor(kept);
        for (var i = 0; i < 50; i++)
        {
            session.Write(audio);
            engine.ReadForRoute(outBuf, 0, blockBytes, kept, mixBuf, sessionBuf, false);
            foreach (var f in System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(outBuf.AsSpan()))
                peak = Math.Max(peak, Math.Abs(f));
        }
        var underruns = engine.AggregateUnderrunsFor(kept) - underrunsBefore;
        engine.OnReceivedSamples = null;
        engine.SetRecordTap(null, false);
        Check(peerTrackBlocks == 50,
            $"{label}: a split recording must get this peer's block exactly once per render from the lane still reading (got "
            + $"{peerTrackBlocks} for 50 renders) — none means a stalled output silences the peer's own track, and twice means "
            + "the peer is recorded once per copy");
        // A copy that is no longer fed must not be READ either. Read, it runs dry and every block after
        // that is a gap filled with concealment noise, mixed into a peer who is otherwise playing fine.
        Check(underruns == 0,
            $"{label}: nothing playing on the lane still reading may run dry — {underruns} underrun(s) in 50 blocks while the "
            + "peer was fed steadily. A copy the engine has stopped feeding is still being read");
        Check(peak > 0.15f,
            $"{label}: the lane still reading must still be playing the peer (peak {peak:0.000}, fed at 0.250)");
        Check(peak < 0.35f,
            $"{label}: the peer must be heard ONCE on the lane still reading — fed at 0.250 it peaked at {peak:0.000}. "
            + "About twice the level is the primary falling through onto this lane while its mirror plays here too");
        Check(recordedBlocks > 0,
            $"{label}: the receive recording must carry on from the lane still reading ({recordedBlocks} blocks recorded) "
            + "— otherwise a stalled output silently stops every recording while the peer goes on playing");

        return $"{label}: a stalled lane stops being fed ({overflowed} overflow bytes over {ticks} ticks); the lane still "
             + $"reading plays the peer once (peak {peak:0.00}) and records it";
    }

    private static string RunUntickedLaneCase(RenderRoute turnedOff, RenderRoute kept, string label)
    {
        const int frames = 480;                       // 10 ms, one render callback
        const int bytesPerFrame = 8;                  // stereo float
        const int blockBytes = frames * bytesPerFrame;
        // A small ring on purpose: big enough to hold a healthy cushion, small enough that a lane
        // nobody drains overflows within the test rather than within a session.
        const int capacity = 256 * 1024;              // about 0.7 s of stereo float

        var engine = new PlayoutEngine(new ReceiverDiagnostics());
        engine.SetLaneActive(RenderRoute.WasapiLane, true);
        engine.SetLaneActive(RenderRoute.AsioLane, true);
        engine.SetMaxLatencyMs(RenderRoute.Mixed, 60);
        engine.SetMaxLatencyMs(RenderRoute.WasapiLane, 60);
        engine.SetMaxLatencyMs(RenderRoute.AsioLane, 60);

        var peer = new IPEndPoint(IPAddress.Parse("10.0.0.9"), 47830);
        var session = engine.GetOrCreateSession(peer, 1, capacity);
        session.NoteFramesQueued(30);

        var audio = new byte[blockBytes];
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(audio.AsSpan());
        for (var i = 0; i < floats.Length; i++) floats[i] = 0.25f;

        var outBuf = new byte[blockBytes];
        var mixBuf = new float[frames * 2];
        var sessionBuf = new float[frames * 2];

        // Arm first. A session plays nothing until its buffer has reached the target, so a
        // write-one-block-read-one-block loop from empty would never produce a sample and every check
        // below would be measuring silence.
        var arming = new byte[48000 * 8 / 8];   // 125 ms of stereo float
        var armFloats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(arming.AsSpan());
        for (var i = 0; i < armFloats.Length; i++) armFloats[i] = 0.25f;
        session.Write(arming);
        session.NoteFramesQueued(60);

        // BOTH LANES LIVE. Prime the pair so the mirror really exists and really drains — otherwise
        // the check below would pass on an engine that never made one.
        var primedPeak = 0f;
        for (var i = 0; i < 20; i++)
        {
            session.Write(audio);
            engine.ReadForRoute(outBuf, 0, blockBytes, RenderRoute.WasapiLane, mixBuf, sessionBuf, false);
            foreach (var f in System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(outBuf.AsSpan()))
                primedPeak = Math.Max(primedPeak, Math.Abs(f));
            engine.ReadForRoute(outBuf, 0, blockBytes, RenderRoute.AsioLane, mixBuf, sessionBuf, false);
        }
        Check(primedPeak > 0.01f,
            $"{label}: the engine must be playing audio on both lanes before the lane is switched off (peak "
            + $"{primedPeak:0.000}) — everything below is measured against this starting point");
        Check(engine.AggregateDrops == 0,
            $"{label}: nothing may overflow while BOTH lanes are being read — {engine.AggregateDrops} bytes did, so the "
            + "test was already unhealthy before the lane was switched off and proves nothing about switching it off");

        // THE USER SWITCHES ONE OUTPUT OFF. From here the app stops rendering that lane, exactly as
        // the field log shows: renderAsioMs went to 0.0 and stayed there.
        engine.SetLaneActive(turnedOff, false);

        var beforeDrops = engine.AggregateDrops;
        // Two seconds of audio at real-time rate, read only on the lane that is still ticked. Long
        // enough that a ring nobody drains passes its capacity three times over.
        const int ticks = 200;
        for (var i = 0; i < ticks; i++)
        {
            session.Write(audio);
            engine.ReadForRoute(outBuf, 0, blockBytes, kept, mixBuf, sessionBuf, false);
        }

        // The premise: the surviving lane really is producing audio. Without this the drop check could
        // pass on an engine that had simply stopped doing anything at all.
        var peak = 0f;
        for (var i = 0; i < 200; i++)
        {
            session.Write(audio);
            engine.ReadForRoute(outBuf, 0, blockBytes, kept, mixBuf, sessionBuf, false);
            var got = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(outBuf.AsSpan());
            foreach (var f in got) peak = Math.Max(peak, Math.Abs(f));
            if (peak > 0.01f) break;
        }
        Check(peak > 0.01f,
            $"{label}: the lane that is still ticked must still be playing audio (peak {peak:0.000}) — a silent engine "
            + "would pass the overflow check for the wrong reason");

        var overflowed = engine.AggregateDrops - beforeDrops;
        Check(overflowed == 0,
            $"{label}: after an output lane is switched off, nothing may still be written into a ring that nobody reads "
            + $"— {overflowed} bytes overflowed in {ticks} render ticks. In the field this ran at 288,960 bytes a second "
            + "for hours, sawtoothing the readout between 275 and 800 ms and starving the lane the user WAS listening to");

        return $"{label}: no orphaned ring ({overflowed} overflow bytes over {ticks} ticks)";
    }

    /// <summary>
    /// EVERY PEER IS HEARD ONCE — IN ALL THREE CONFIGURATIONS, THROUGH THE READ EACH ONE REALLY USES.
    ///
    /// <para>2026-09-13 review, finding 4. The engine fans each stream out to one copy per active output
    /// lane and marks the extra copies as mirrors. The per-lane reads keep every copy on its own lane. The
    /// all-sessions read — the one a machine with no ASIO driver renders from — filtered by nothing, so any
    /// mirror in the engine was summed on top of its primary: the peer twice, about 6 dB hot and
    /// comb-filtered. Two ordinary things made a mirror there. A new engine believes both lanes are active
    /// until it is told which outputs are ticked. And an ASIO pair left ticked after the driver was
    /// unchosen still flagged the ASIO lane active, with no ASIO backend to read it, so that copy was fed
    /// for good as well.</para>
    ///
    /// <para>Each configuration is flagged the way the render backend flags the engine and read the way
    /// that configuration renders. The level must be the level sent, and nothing may overflow.</para>
    /// </summary>
    private static string? AuditEachPeerIsHeardOnceInEveryConfiguration()
    {
        var findings = new List<string>();
        foreach (var configuration in AudioConfigurations.All)
        {
            // An ASIO driver is chosen in two of the three, and WASAPI-only is also reachable with one, so the
            // per-lane reads are what render here. Lanes flagged as CompositeRenderBackend flags them: active
            // when an output of that kind is ticked.
            var engine = new PlayoutEngine(new ReceiverDiagnostics());
            engine.SetLaneActive(RenderRoute.WasapiLane, configuration.UsesWasapi());
            engine.SetLaneActive(RenderRoute.AsioLane, configuration.UsesAsio());
            RenderRoute[] lanes = configuration switch
            {
                AudioConfiguration.WasapiOnly => [RenderRoute.WasapiLane],
                AudioConfiguration.AsioOnly => [RenderRoute.AsioLane],
                _ => [RenderRoute.WasapiLane, RenderRoute.AsioLane],
            };
            var (peak, drops, recordedPerRender) = PlayOnePeerForTest(engine, lanes, readAllSessions: false);
            Check(peak > 0.15f && peak < 0.35f,
                $"{configuration.Describe()}: each output must play the peer ONCE, at the level it was sent (peak {peak:0.000}, "
                + "sent 0.250)");
            Check(Math.Abs(recordedPerRender - 1.0) < 0.01,
                $"{configuration.Describe()}: a split recording must get the peer's audio once per render, however many outputs "
                + $"play it (got {recordedPerRender:0.00} times) — every copy carries the tap, and only the recording lane may use it");
            Check(drops == 0,
                $"{configuration.Describe()}: nothing may be fed and left unread ({drops} bytes overflowed)");
            findings.Add($"{configuration.Describe()}: heard once at {peak:0.00}");
        }

        // WASAPI ONLY WITH NO ASIO DRIVER CHOSEN renders through the all-sessions read. Two ways a second copy
        // of a peer could get into it.
        //
        // 1. A new engine, before its outputs are known: both lane flags start on, so a mirror is made.
        var fresh = new PlayoutEngine(new ReceiverDiagnostics());
        var (freshPeak, _, _) = PlayOnePeerForTest(fresh, [], readAllSessions: true);
        Check(freshPeak > 0.15f && freshPeak < 0.35f,
            $"a new engine read through the all-sessions path must play a peer ONCE (peak {freshPeak:0.000}, sent 0.250) — "
            + "about twice that is a mirror summed on top of its primary");

        // 2. An ASIO pair still ticked after the driver was unchosen, beside a WASAPI output tick.
        var stale = new PlayoutEngine(new ReceiverDiagnostics());
        using (var render = new CompositeRenderBackend(AudioMode.WasapiOnly, null, stale))
        {
            // An id no device has, so nothing is opened; the WASAPI lane still counts as ticked.
            render.SetOutputDevices(["{0.0.0.00000000}.{remsound-gate-no-such-device}", AsioDeviceId.Format(0)]);
        }
        var (stalePeak, staleDrops, _) = PlayOnePeerForTest(stale, [], readAllSessions: true);
        Check(staleDrops == 0,
            $"an ASIO pair left ticked with no driver chosen must not make a copy that nothing reads ({staleDrops} bytes "
            + "overflowed)");
        Check(stalePeak > 0.15f && stalePeak < 0.35f,
            $"...and must not put the peer into the output twice (peak {stalePeak:0.000}, sent 0.250)");

        return string.Join("; ", findings)
             + $"; with no driver chosen, a new engine ({freshPeak:0.00}) and a stale ASIO tick ({stalePeak:0.00}) each play the peer once";
    }

    /// <summary>One peer at a steady 0.25, armed, then played for three seconds through the given lane
    /// reads, or through the all-sessions read. Returns the loudest sample, the bytes that overflowed, and how
    /// many renders' worth of the peer the split-recording tap received per render — all over the settled
    /// second half.</summary>
    private static (float Peak, long Drops, double RecordedPerRender) PlayOnePeerForTest(PlayoutEngine engine, RenderRoute[] lanes, bool readAllSessions)
    {
        const int frames = 480, blockBytes = frames * 8;
        engine.SetMaxLatencyMs(RenderRoute.Mixed, 60);
        engine.SetMaxLatencyMs(RenderRoute.WasapiLane, 60);
        engine.SetMaxLatencyMs(RenderRoute.AsioLane, 60);
        var session = engine.GetOrCreateSession(new IPEndPoint(IPAddress.Parse("10.0.0.21"), 47830), 1, 256 * 1024);
        var measuring = false;
        var recordedFloats = 0L;
        engine.SetRecordTap((_, block) => { if (measuring) recordedFloats += block.Length; }, raw: false);

        var audio = new byte[blockBytes];
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(audio.AsSpan()).Fill(0.25f);
        var arming = new byte[48000];
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(arming.AsSpan()).Fill(0.25f);
        session.Write(arming);
        session.NoteFramesQueued(60);

        var outBuf = new byte[blockBytes];
        var mixBuf = new float[frames * 2];
        var sessionBuf = new float[frames * 2];
        var dropsBefore = 0L;
        var peak = 0f;
        for (var tick = 0; tick < 300; tick++)
        {
            if (tick == 150)
            {
                dropsBefore = engine.AggregateDrops;
                measuring = true;
            }
            session.Write(audio);
            var reads = readAllSessions ? 1 : lanes.Length;
            for (var r = 0; r < reads; r++)
            {
                if (readAllSessions) engine.Read(outBuf, 0, blockBytes);
                else engine.ReadForRoute(outBuf, 0, blockBytes, lanes[r], mixBuf, sessionBuf, false);
                if (tick < 150) continue;
                foreach (var f in System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(outBuf.AsSpan()))
                    peak = Math.Max(peak, Math.Abs(f));
            }
        }
        engine.SetRecordTap(null, false);
        return (peak, engine.AggregateDrops - dropsBefore, recordedFloats / (150.0 * frames * 2));
    }
}
