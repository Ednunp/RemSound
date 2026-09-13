using System.Net;
using System.Reflection;
using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App;

/// <summary>
/// Regression cover for the 2026-08-23 line-by-line audit of the send and receive paths
/// (AUDIT-FINDINGS.md). One test per finding that can be pinned headlessly.
///
/// <para>These are all written to FAIL against the code as it was. Where a test could pass whether
/// or not the fix is present, it has been rewritten until it cannot — a test that cannot fail is
/// decoration, and this audit found two of its own earlier tests in that state.</para>
/// </summary>
internal static partial class SelfTest
{
    // ---------------------------------------------------------------------------------------
    // S1 — turning sending off must stop the ASIO lane delivering audio
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Park must actually stop audio reaching the sender lane — WITHOUT closing the driver.
    ///
    /// <para>Both halves are load-bearing and pull in opposite directions, which is why this is
    /// measured rather than asserted. Keeping the driver OPEN across a stop is deliberate: closing
    /// and reopening it is what hangs Audient for five seconds, and that is why the composite never
    /// stops the borrowed ASIO child. But nothing else stood the lane down either, so with "Send my
    /// audio" off the driver went on handing buffers to the lane, which went on encoding,
    /// encrypting and transmitting them to every armed peer.</para>
    ///
    /// <para>Testing that Park() was called would prove nothing. The bug was a LIVE CALLBACK still
    /// delivering, so the test invokes the callback the driver's thread invokes and counts what
    /// arrives at the far end.</para>
    /// </summary>
    private static string? AuditAsioParkStopsDelivery()
    {
        var delivered = 0;
        // A driver name that cannot exist: the constructor only spins up the apartment thread, so
        // no hardware is touched and this runs anywhere.
        using var backend = new RemSound.Sender.AsioCaptureBackend(
            "RemSound self-test — no such ASIO driver", _ => delivered++, _ => { });

        var block = new ReadOnlyMemory<float>(new float[960]);

        // Before: the callback delivers, which is the whole point of the lane.
        backend.CallbackForTest(block);
        Check(delivered == 1, "the ASIO lane must deliver audio to its callback while sending");

        // Park — Ed's own mechanism from "park the driver on switch-away instead of closing it".
        backend.Park();
        backend.CallbackForTest(block);
        backend.CallbackForTest(block);
        Check(delivered == 1,
            $"after Park, the driver's callback must deliver NOTHING to the sender lane — this is the "
            + $"'send off but still transmitting' bug (delivered {delivered}, expected 1)");

        // And the park must not have closed anything: a second park is a no-op, not a teardown.
        backend.Park();
        Check(!backend.IsRunning, "no driver was ever opened in this test, so nothing should report running");

        // Unparking re-points the callback at a live lane again.
        var reDelivered = 0;
        backend.SetCallback(_ => reDelivered++);
        backend.CallbackForTest(block);
        Check(reDelivered == 1, "re-pointing the callback must bring delivery back (this is what Start does)");

        // --- And the part that actually broke: STOPPING THE SENDER must park the lane. ------------
        // Park itself was never the bug; the bug was that nothing called it, so an ASIO lane kept
        // capturing and transmitting with "Send my audio" off. Driven through AudioSender's real
        // public surface. Constructing the backend opens no driver, so this runs anywhere.
        using var sender = new RemSound.Sender.AudioSender();
        sender.SetAudioMode(AudioMode.BothIndependent, "RemSound self-test — no such ASIO driver");
        var persistent = sender.PersistentAsioForTest;
        Check(persistent is not null, "BothIndependent with a driver name must create the persistent ASIO lane");
        Check(!persistent!.CallbackDetachedForTest,
            "after selecting an ASIO mode the lane's callback must be live, or the test proves nothing");

        sender.Stop();
        Check(persistent.CallbackDetachedForTest,
            "stopping the sender must PARK the ASIO lane — otherwise the driver keeps handing buffers to "
            + "the sender lane and audio is still encoded, encrypted and transmitted with sending switched off");

        return "parked lane delivers nothing; stopping the sender parks it; the driver is never closed; re-pointing restores delivery";
    }

    // ---------------------------------------------------------------------------------------
    // S5 — the audio loops must stay on their own MMCSS-boosted thread
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The mix loop and the output producer loop must not be async.
    ///
    /// <para>Both applied an MMCSS "Pro Audio" boost to the thread <c>Task.Run</c> started them on,
    /// and both then did <c>await Task.Delay(50, ct)</c> in their catch block. MMCSS characteristics
    /// are per-THREAD, so the first await resumed the loop on some other thread-pool thread and every
    /// iteration after that ran unboosted for the rest of the session — a silent, permanent
    /// demotion of the audio path triggered by one logged hiccup.</para>
    ///
    /// <para>This is checked by reflection on the compiler's own marker rather than by reading the
    /// source, so it stays true no matter how the wait is written. Any future edit that reintroduces
    /// an await in either loop turns this red.</para>
    /// </summary>
    private static string? AuditAudioLoopsAreNotAsync()
    {
        CheckNotAsync(typeof(RemSound.Sender.MixingEngine), "MixLoop", "the WASAPI capture mix tick");
        CheckNotAsync(typeof(RemSound.Receiver.MultiOutputPlayout), "ProduceLoop", "the output producer tick");
        return "neither audio loop is async, so neither can lose its Pro Audio thread to a continuation";
    }

    private static void CheckNotAsync(Type type, string methodName, string what)
    {
        var method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new CheckFailed($"{type.Name}.{methodName} not found — the audio-loop guard is testing nothing");
        var isAsync = method.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>() is not null;
        Check(!isAsync,
            $"{what} ({type.Name}.{methodName}) is async — an await moves it off its MMCSS Pro Audio thread "
            + "and silently demotes the audio path for the rest of the session. Wait with ct.WaitHandle.WaitOne instead.");
    }

    // ---------------------------------------------------------------------------------------
    // R4 — a Format packet is unauthenticated network input and must fail closed
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Garbage in a Format packet must be rejected before anything acts on it, and every format a
    /// real RemSound sends must still be accepted.
    ///
    /// <para>The second half is the cross-port half and matters as much as the first: this validation
    /// sits on a wire surface that an iPhone, a Pi relay and a send-only service all speak, and
    /// rejecting something one of them legitimately sends would take their audio away silently. The
    /// accepted cases below are the exact values <c>SenderLane.EnsureFormatPacketSent</c> writes.</para>
    /// </summary>
    private static string? AuditFormatValidation()
    {
        // --- What a real RemSound sends. All of these MUST pass. ---
        // PCM, standard 5 ms and tight 2.5 ms — straight from SenderLane.
        AcceptFormat(new AudioFormatInfo(48000, 2, 24, 1, 6, 288_000, (int)AudioTransportCodec.Pcm, 240), "PCM 5 ms");
        AcceptFormat(new AudioFormatInfo(48000, 2, 24, 1, 6, 288_000, (int)AudioTransportCodec.Pcm, 120), "PCM tight 2.5 ms");
        // Opus at every frame size the UI can produce, including the 60 ms maximum.
        foreach (var frame in new[] { 120, 240, 480, 960, 2880 })
        {
            AcceptFormat(new AudioFormatInfo(48000, 2, 16, 1, 4, 192_000, (int)AudioTransportCodec.Opus, frame), $"Opus {frame} samples");
        }
        // Mono, and the lower Opus rates: nothing in the Windows app sends these today, but they are
        // legal and a future port might. Rejecting them would be a silent interop break.
        AcceptFormat(new AudioFormatInfo(48000, 1, 16, 1, 4, 192_000, (int)AudioTransportCodec.Opus, 480), "mono Opus");
        AcceptFormat(new AudioFormatInfo(24000, 2, 16, 1, 4, 96_000, (int)AudioTransportCodec.Opus, 480), "Opus at 24 kHz");

        // --- What must be rejected. ---
        // Zero channels divided by zero once per packet in the PCM path — a log flood at packet rate.
        RejectFormat(new AudioFormatInfo(48000, 0, 24, 1, 6, 288_000, (int)AudioTransportCodec.Pcm, 240), "zero channels");
        RejectFormat(new AudioFormatInfo(48000, 8, 24, 1, 6, 288_000, (int)AudioTransportCodec.Pcm, 240), "eight channels");
        // A huge frame size sized the Opus decode scratch — a remote-controlled multi-megabyte
        // allocation per packet on the network thread.
        RejectFormat(new AudioFormatInfo(48000, 2, 16, 1, 4, 192_000, (int)AudioTransportCodec.Opus, 10_000_000), "absurd frame size");
        RejectFormat(new AudioFormatInfo(48000, 2, 16, 1, 4, 192_000, (int)AudioTransportCodec.Opus, 0), "zero frame size");
        RejectFormat(new AudioFormatInfo(0, 2, 24, 1, 6, 288_000, (int)AudioTransportCodec.Pcm, 240), "zero sample rate");
        RejectFormat(new AudioFormatInfo(48000, 2, 24, 1, 6, 288_000, 99, 240), "unknown codec");
        // Opus at a rate libopus does not support — the decoder constructor would throw, and that
        // throw used to escape into the packet handler.
        RejectFormat(new AudioFormatInfo(44100, 2, 16, 1, 4, 192_000, (int)AudioTransportCodec.Opus, 480), "Opus at 44.1 kHz");

        return "every format the sender emits is accepted; zero channels, absurd frame sizes, unknown codecs and illegal Opus rates are refused";
    }

    private static void AcceptFormat(AudioFormatInfo format, string what)
    {
        Check(format.IsUsable(out var why),
            $"a format a real RemSound sends must be accepted — {what} was rejected as \"{why}\". "
            + "This is the cross-port half: refusing it takes that peer's audio away with no error anywhere.");
    }

    private static void RejectFormat(AudioFormatInfo format, string what)
    {
        Check(!format.IsUsable(out _), $"an unusable format must be refused before anything acts on it — {what} was accepted");
    }

    // ---------------------------------------------------------------------------------------
    // R5 — a malformed Format packet must not cost a working peer four seconds of audio
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A bad Format packet arriving mid-stream must leave the existing session untouched.
    ///
    /// <para>The old code disposed the existing session BEFORE building its replacement. When the
    /// replacement's constructor threw — which a corrupt Opus rate or channel count made easy — the
    /// disposed session stayed in the table. Its decoder was gone so every audio packet dropped, and
    /// the next GOOD format packet matched the disposed session's own format on the early-return
    /// path, so it was never replaced. The peer stayed silent until the idle prune reaped it four
    /// seconds later.</para>
    ///
    /// <para>Driven through the receiver's real packet entry point with real bytes on the wire, not
    /// by calling an internal method — the ordering bug lived in the handler, so the handler is what
    /// has to be exercised.</para>
    ///
    /// <para><b>Honest scope.</b> What this actually discriminates is the R4 validation: break that
    /// and this goes red on the rejected-count assertion. It does NOT independently catch the R5
    /// re-ordering, because with R4 in place the StreamSession constructor can no longer be made to
    /// throw from a wire format at all — every value that used to reach it is now refused earlier.
    /// The re-ordering was kept anyway as the second line of defence (it costs nothing and makes the
    /// failure cheap if a future format field slips through), but it is deliberately recorded here as
    /// uncovered rather than left looking tested. Verified by breaking both fixes and watching which
    /// assertions moved.</para>
    /// </summary>
    private static string? AuditBadFormatKeepsTheExistingSession()
    {
        var peer = new IPEndPoint(IPAddress.Parse("192.168.77.10"), 47830);
        using var receiver = new AudioReceiver();
        receiver.SetAllowedSenders([peer]);
        receiver.SetPlaybackEnabled(true);

        const ushort streamId = 7;
        var good = new AudioFormatInfo(48000, 2, 16, 1, 4, 192_000, (int)AudioTransportCodec.Opus, 480);

        receiver.InjectExternalPacket(BuildFormatPacket(streamId, 1, good), FormatPacketLength(good), peer);
        Check(receiver.ActiveFormatsFromAddress(peer.Address).Count == 1,
            "a good format packet must open a session");

        // Now the corrupt one: Opus at a sample rate libopus rejects. Same peer, same stream id, so
        // it takes the format-CHANGE path — the one that disposed first and asked questions later.
        var bad = new AudioFormatInfo(44100, 2, 16, 1, 4, 192_000, (int)AudioTransportCodec.Opus, 480);
        receiver.InjectExternalPacket(BuildFormatPacket(streamId, 2, bad), FormatPacketLength(bad), peer);

        var after = receiver.ActiveFormatsFromAddress(peer.Address);
        Check(after.Count == 1,
            $"a malformed format packet must not remove the working session (found {after.Count})");
        Check(after[0].SampleRate == 48000 && after[0].FrameSamplesPerChannel == 480,
            $"the session must still be the GOOD format, not replaced or half-replaced (got {after[0]})");
        Check(receiver.FormatPacketsRejected >= 1,
            "the rejected format must be counted, so a peer that has genuinely gone wrong is a number and not just silence");

        // And a good packet afterwards must still be honoured — the old failure left a disposed
        // session in place that matched every subsequent format and blocked its own replacement.
        var changed = new AudioFormatInfo(48000, 2, 16, 1, 4, 192_000, (int)AudioTransportCodec.Opus, 960);
        receiver.InjectExternalPacket(BuildFormatPacket(streamId, 3, changed), FormatPacketLength(changed), peer);
        var final = receiver.ActiveFormatsFromAddress(peer.Address);
        Check(final.Count == 1 && final[0].FrameSamplesPerChannel == 960,
            "a good format packet after a bad one must still take effect");

        return "a corrupt format is counted and dropped; the working session survives it and still accepts the next real change";
    }

    private static int FormatPacketLength(AudioFormatInfo format) =>
        RemPacket.HeaderSize + RemPacket.FormatPayloadWithFingerprintSize;

    private static byte[] BuildFormatPacket(ushort streamId, uint sequence, AudioFormatInfo format)
    {
        var packet = new byte[RemPacket.HeaderSize + RemPacket.FormatPayloadWithFingerprintSize];
        RemPacket.WriteHeader(packet, RemPacketType.Format, streamId, sequence);
        RemPacket.WriteFormatPayload(packet.AsSpan(RemPacket.HeaderSize), format, null);
        return packet;
    }

    // ---------------------------------------------------------------------------------------
    // R2 — a plugin must get a claimed peer ONCE, not once per output lane
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// With two output lanes active, a claimed peer must reach the DAW at its real level.
    ///
    /// <para>The engine keeps a mirror replica of every stream per extra active output lane, so each
    /// sound card can play it on its own clock. The speaker paths filter those by output lane.
    /// <c>ReadClaimedPeer</c> does not filter by lane — a plugin's track has no output lane — so it
    /// summed the primary AND its mirror and handed the DAW two copies: about 6 dB hot, and
    /// comb-filtered as the two rings drifted apart. It only bit with both a WASAPI and an ASIO
    /// output ticked, which is why it survived to here.</para>
    ///
    /// <para>Measured against the SAME peer read with one lane active, so the test is a comparison of
    /// levels rather than a hard-coded number — it fails if the audio is doubled, and it would also
    /// fail if a future change halved it.</para>
    /// </summary>
    private static string? AuditClaimedPeerIsNotDoubled()
    {
        var peer = new IPEndPoint(IPAddress.Parse("192.168.77.20"), 47830);
        var instance = Guid.NewGuid();

        var oneLane = ClaimedPeerPeak(peer, instance, bothLanes: false);
        Check(oneLane > 0.2f, $"the claimed peer must reach the plugin at all (peak {oneLane:0.000})");

        var twoLanes = ClaimedPeerPeak(peer, instance, bothLanes: true);
        Check(twoLanes > 0.2f, $"the claimed peer must still reach the plugin with two outputs ticked (peak {twoLanes:0.000})");

        // Doubling would land at ~2x. Allow a wide tolerance for drift-corrector differences between
        // the two rings; anything approaching 1.5x is the bug.
        Check(twoLanes < oneLane * 1.4f,
            $"a claimed peer must reach the plugin ONCE however many outputs are ticked — with two lanes it "
            + $"came out {twoLanes / oneLane:0.00}x louder, which is the primary and its mirror being summed "
            + $"(one lane {oneLane:0.000}, two lanes {twoLanes:0.000})");

        return $"a claimed peer reads at the same level with one output lane or two ({oneLane:0.000} vs {twoLanes:0.000}) — no mirror doubling";
    }

    private static float ClaimedPeerPeak(IPEndPoint peer, Guid instance, bool bothLanes)
    {
        var engine = new PlayoutEngine(new ReceiverDiagnostics());
        engine.SetLaneActive(RenderRoute.WasapiLane, true);
        engine.SetLaneActive(RenderRoute.AsioLane, bothLanes);
        engine.SetMaxLatencyMs(RenderRoute.Mixed, 30);

        var session = engine.GetOrCreateSession(peer, 1, 4 * 1024 * 1024);
        FillSession(session, 0.4f);

        var claims = new PluginPeerClaims();
        engine.SetPluginPeerClaims(claims);
        claims.Claim(peer.Address, instance);

        const int frames = 480;
        var destination = new float[frames * 2];
        // First read primes the drift resampler's delay line; measure the second.
        engine.ReadClaimedPeer(peer.Address, destination, frames);
        Array.Clear(destination);
        engine.ReadClaimedPeer(peer.Address, destination, frames);

        var peak = 0f;
        foreach (var f in destination) peak = Math.Max(peak, Math.Abs(f));
        return peak;
    }

    // ---------------------------------------------------------------------------------------
    // S9 / R6 — comments and contracts that the audit found were claiming more than the code did
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The two audio-thread taps that were running unguarded must be isolated from the audio path.
    ///
    /// <para>A recorder callback or a per-peer filter that threw used to unwind out of the render
    /// read into NAudio's WasapiOut loop, which catches it and raises PlaybackStopped — so
    /// MultiOutputPlayout logged <c>output "X" lost the device</c> and stopped the output. A recorder
    /// bug presented as a hardware fault. The mixed taps either side were already wrapped; these two
    /// were missed.</para>
    /// </summary>
    private static string? AuditRecorderCannotKillTheOutput()
    {
        var peer = new IPEndPoint(IPAddress.Parse("192.168.77.30"), 47830);

        // ALL THREE CONFIGURATIONS. The record tap runs inside the per-session read, and which lane
        // that read comes from depends entirely on what is ticked — so a guard proven only in the
        // WASAPI-only case proves nothing about the other two, which is precisely the trap.
        var totalCalls = 0;
        foreach (var configuration in AudioConfigurations.All)
        {
            var engine = new PlayoutEngine(new ReceiverDiagnostics());
            engine.SetIndependentLaneLatency(configuration.HasTwoLanes());
            engine.SetLaneActive(RenderRoute.WasapiLane, configuration.UsesWasapi());
            engine.SetLaneActive(RenderRoute.AsioLane, configuration.UsesAsio());
            engine.SetMaxLatencyMs(RenderRoute.Mixed, 30);
            engine.SetMaxLatencyMs(RenderRoute.WasapiLane, 30);
            engine.SetMaxLatencyMs(RenderRoute.AsioLane, 30);

            var session = engine.GetOrCreateSession(peer, 1, 4 * 1024 * 1024);
            FillSession(session, 0.4f);

            var calls = 0;
            engine.SetRecordTap((_, _) => { calls++; throw new InvalidOperationException("recorder blew up"); }, raw: false);

            var buffer = new byte[960 * 8];
            var peak = 0f;
            try
            {
                peak = PeakOfMix(engine, buffer);
            }
            catch (Exception ex)
            {
                throw new CheckFailed(
                    $"in {configuration.Describe()}, a throwing recorder tap escaped the render read as {ex.GetType().Name} — "
                    + "in a real session that reaches NAudio's render loop and is reported as the output losing its device");
            }

            Check(calls > 0, $"in {configuration.Describe()} the record tap must actually have been called, or this proves nothing");
            Check(peak > 0.05f, $"in {configuration.Describe()} the audio must keep flowing through a failing recorder (peak {peak:0.000})");
            totalCalls += calls;
        }

        return $"a throwing per-peer record tap is contained in all three configurations ({totalCalls} tap calls survived); "
             + "the mix keeps playing and the output is never reported as lost";
    }

    // ---------------------------------------------------------------------------------------
    // From Ed's 2026-08-23 hardware test
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A read-only readout must hold still while a screen reader is reading it.
    ///
    /// <para>Setting Text on a multiline TextBox puts the caret back at the top. The total-latency
    /// readout refreshes once a second and its achieved figure changes on nearly every tick, so
    /// arrowing through it was impossible — "I keep getting bounced about". The connection-status
    /// readout already had this rule; the latency one, added later, did not.</para>
    ///
    /// <para>Also pins the staleness half. The status readout recorded the new text as rendered
    /// BEFORE checking focus, so a change that landed while the box was focused was never written
    /// afterwards — it waited for the next change. Both now share one decision, so they cannot drift
    /// apart again.</para>
    /// </summary>
    private static string? AuditReadoutHoldsStillWhileRead()
    {
        Check(MainForm.ShouldWriteReadout("old", "new", focused: false),
            "a changed readout must be written when nobody is reading it");
        Check(!MainForm.ShouldWriteReadout("old", "new", focused: true),
            "a readout must NOT be rewritten while it has focus — that resets the caret and throws a "
            + "screen-reader user back to line one every tick");
        Check(!MainForm.ShouldWriteReadout("same", "same", focused: false),
            "identical text must not be rewritten");

        // The staleness half: text that arrived during focus must be written on the first tick after
        // focus leaves, NOT held back until the value happens to change again.
        const string held = "arrived while you were reading";
        Check(!MainForm.ShouldWriteReadout("stale", held, focused: true), "held back while focused");
        Check(MainForm.ShouldWriteReadout("stale", held, focused: false),
            "the text that arrived while the box was focused must be written as soon as focus leaves");

        return "a readout is never rewritten under a reader's caret, and whatever arrived meanwhile lands the moment focus goes";
    }

    /// <summary>
    /// The measured-latency figure must not redraw the readout for wobble.
    ///
    /// <para>Every rewrite of a TextBox makes NVDA speak, focused or not — Ed heard "edit, edit"
    /// while sitting on a different control, which is why skipping the write while focused could
    /// never have fixed it. The achieved figure is recomputed every second and moves a millisecond
    /// or two on its own, so the text differed on nearly every tick and the box spoke roughly once a
    /// second, all session.</para>
    ///
    /// <para>What must still get through immediately: the first reading, and audio starting or
    /// stopping. Those change what the line means rather than nudging a number.</para>
    /// </summary>
    private static string? AuditLatencyReadoutIgnoresWobble()
    {
        Check(MainForm.ShouldCommitLatencyFigure(-1, 42), "the first reading must always be shown");
        Check(MainForm.ShouldCommitLatencyFigure(0, 42), "audio STARTING must be shown at once, not waited out");
        Check(MainForm.ShouldCommitLatencyFigure(42, 0), "audio STOPPING must be shown at once — a stale figure would be a lie");

        Check(!MainForm.ShouldCommitLatencyFigure(42, 43), "a 1 ms wobble must not redraw the box");
        Check(!MainForm.ShouldCommitLatencyFigure(42, 41), "a 1 ms wobble downward must not redraw the box");
        Check(!MainForm.ShouldCommitLatencyFigure(42, 44), "a 2 ms wobble must not redraw the box");
        Check(MainForm.ShouldCommitLatencyFigure(42, 46), "a real 4 ms move must be shown");
        Check(MainForm.ShouldCommitLatencyFigure(42, 20), "a big drop must be shown");

        // The numbers Ed's laptop actually produced, replayed against the filter: 38 probes, 25 of
        // them distinct. Count how many would have redrawn the box. Anything close to one a second
        // is the bug, whatever the unit tests above say.
        double[] observed =
        [
            68.5, 67.5, 68.5, 69.5, 68.5, 67.5, 68.5, 69.5, 68.5, 67.5, 68.5, 65.5, 65.5, 70.0, 72.0,
            26.5, 24.5, 24.5, 26.5, 24.5, 24.5,
        ];
        var committed = -1.0;
        var redraws = 0;
        foreach (var live in observed)
        {
            if (!MainForm.ShouldCommitLatencyFigure(committed, live)) continue;
            committed = live;
            redraws++;
        }
        Check(redraws <= 4,
            $"replaying Ed's own 21 readings must not redraw the box more than a handful of times — got {redraws}. "
            + "Every redraw is NVDA speaking over him.");

        return $"wobble under {MainForm.LatencyReadoutDeadbandMs:0} ms is held; start, stop and real moves get through; "
             + $"Ed's 21 recorded readings redraw the box {redraws} times instead of 21";
    }

    /// <summary>
    /// The total-latency readout must sit with the jitter-buffer controls it reports on, in the TAB
    /// ORDER and not merely on screen.
    ///
    /// <para>Ed asked for it moved out of last place: "it makes more sense after the jitter buffer
    /// and auto-tune". The trap is that this panel sets no TabIndex, so WinForms walks its children
    /// in the order they were ADDED — renumbering the table rows would have moved the box visually
    /// while leaving it last in the keyboard walk, which for a screen-reader user is the only order
    /// that exists. So this asserts the walk, not the layout.</para>
    /// </summary>
    private static string? AuditLatencyReadoutSitsWithItsControls()
    {
        MainForm form;
        try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
        catch (Exception ex) { return Skip($"headless main window could not be built: {ex.GetType().Name}: {ex.Message}"); }

        using (form)
        {
            // Reach the controls the same way the control suite does — by private field, so no test
            // seam has to be added to the form just to be able to check its tab order.
            Control ByField(string field) =>
                Require(Require(typeof(MainForm).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic),
                            $"MainForm.{field} not found — a renamed field would leave this test comparing nothing")
                        .GetValue(form) as Control,
                    $"MainForm.{field} is not a Control — this test is comparing nothing");

            var jitterBox = ByField("maxLatencyBox");
            var readoutBox = ByField("measuredLatencyReadout");
            var smoothnessBox = ByField("smoothnessBox");

            // Walk the whole window the way Tab does, and record where each one lands.
            var order = new List<Control>();
            var seen = new HashSet<Control>();
            for (var c = form.GetNextControl(form, true); c is not null && seen.Add(c); c = form.GetNextControl(c, true))
            {
                order.Add(c);
            }

            var jitter = order.IndexOf(jitterBox);
            var readout = order.IndexOf(readoutBox);
            var smoothness = order.IndexOf(smoothnessBox);

            Check(readout >= 0, "the total-latency readout must be reachable by Tab at all");
            Check(jitter >= 0 && smoothness >= 0,
                "the jitter-buffer and smoothness controls must be in the tab walk, or this test is comparing nothing");
            Check(readout > jitter,
                $"the readout must come AFTER the jitter-buffer control it reports on (jitter at {jitter}, readout at {readout})");
            Check(readout < smoothness,
                $"the readout must come BEFORE buffer smoothness — it belongs with the jitter-buffer settings, not "
                + $"stranded past the unrelated controls (readout at {readout}, smoothness at {smoothness})");

            return $"tab order reaches the jitter buffer, then the total-latency readout, then smoothness ({jitter} → {readout} → {smoothness})";
        }
    }

    /// <summary>
    /// The latency estimate must prefer the DEVICE'S OWN figure over its own guesses, and must never
    /// dress an unknown as a measurement.
    ///
    /// <para>Four of the estimate's five terms were constants. Capture added a flat 10 ms — the size
    /// RemSound asks Windows for, not what the device delivers — on all 2,547 samples of Ed's
    /// 2026-08-23 WASAPI session, because no WASAPI backend reported a period at all. Render took a
    /// callback gap, doubled it, then clamped up to a 10 ms floor, so 2,541 of those samples read
    /// exactly 10.0 on a card whose real period was about 1.4 ms.</para>
    ///
    /// <para>The rules pinned here: a device that states its period wins; a device that stays silent
    /// leaves the old estimate alone rather than contributing a zero; the output figure is doubled
    /// and the input is not; and asking for a smaller buffer than the device's period does not get
    /// you a smaller number, because Windows will not go below it. That last one is why a Realtek can
    /// legitimately report HIGHER than the constant it replaces — right beats flattering.</para>
    /// </summary>
    private static string? AuditLatencyUsesDeviceReportedFigures()
    {
        // 100-ns conversion, since every WASAPI period arrives in those units.
        Check(Math.Abs(DeviceLatency.HnsToMs(100_000) - 10.0) < 0.001, "100,000 hundred-ns units is 10 ms");
        Check(DeviceLatency.HnsToMs(0) == 0 && DeviceLatency.HnsToMs(-5) == 0, "no period reported must stay zero, not go negative");

        // ASIO states a buffer size in frames; that IS the capture wait.
        Check(Math.Abs(DeviceLatency.FramesToMs(64, 48000) - 1.3333) < 0.01, "64 frames at 48 kHz is about 1.33 ms");
        Check(DeviceLatency.FramesToMs(0, 48000) == 0, "no buffer size reported must stay zero");

        // UNKNOWN MUST STAY UNKNOWN. Returning 0 is what makes the caller keep its own estimate;
        // returning a plausible-looking number instead is the exact habit this whole change exists
        // to break.
        Check(DeviceLatency.CaptureMs(0, 10) == 0, "a device that won't state a period must yield 0, not the requested size");
        Check(DeviceLatency.RenderMs(0, 5) == 0, "same on the output side");

        // The device wins when it is slower than what we asked for — Windows will not go below its
        // engine period in shared mode.
        Check(Math.Abs(DeviceLatency.CaptureMs(21.3, 10) - 21.3) < 0.001,
            "a 21.3 ms device period beats the 10 ms we asked for — the number going UP is correct, not a regression");
        // And what we asked for wins when the device is faster, because we asked for a bigger buffer.
        Check(Math.Abs(DeviceLatency.CaptureMs(3.0, 10) - 10.0) < 0.001,
            "asking for 10 ms on a 3 ms device gets 10 ms of buffering, not 3");

        // Output is doubled, input is not: the device plays one buffer while we fill the next.
        Check(Math.Abs(DeviceLatency.RenderMs(10.0, 5) - 20.0) < 0.001, "output latency is two periods");
        Check(Math.Abs(DeviceLatency.CaptureMs(10.0, 5) - 10.0) < 0.001, "input latency is ONE period — no doubling");
        Check(DeviceLatency.RenderMs(10.0, 5) > DeviceLatency.CaptureMs(10.0, 5),
            "the same device must report more latency on output than on input");

        // NO FLOOR. The old code clamped the output up to 10 ms; a genuinely fast card must be
        // allowed to say so, which is the whole point on an ASIO interface.
        Check(DeviceLatency.RenderMs(1.4, 1) < 10.0,
            $"a fast card must be allowed to report under 10 ms — got {DeviceLatency.RenderMs(1.4, 1):0.00} ms; "
            + "the old clamp floor is exactly what made every reading say 10.0");

        return "the device's own figure wins; silence leaves the estimate alone rather than reporting zero; "
             + "output is doubled and input is not; and a fast card is no longer clamped up to 10 ms";
    }

    /// <summary>
    /// WASAPI and ASIO run AT THE SAME TIME, so neither lane may be reported with the other's output
    /// figures.
    ///
    /// <para>Ed leaves both lanes active and switches between them, and the readout shows a line for
    /// each. My first cut of the device-reported latency answered "ASIO if it is running, otherwise
    /// WASAPI" from one property — so with both live, the WASAPI line would have shown ASIO's
    /// numbers, hiding the very difference the box exists to show. Ed caught it before he tested it.
    /// 2026-08-24.</para>
    ///
    /// <para>Driven through the real CompositeRenderBackend in the both-lanes configuration, with no
    /// device open, so it runs anywhere: nothing is open, so every per-lane figure must be zero AND
    /// the two lanes must be answered independently rather than one falling through to the other.</para>
    /// </summary>
    private static string? AuditOutputLatencyIsReportedPerLane()
    {
        // THE LANE-PICKING RULE, fed two DIFFERENT values. This is the assertion that actually bites:
        // an earlier version of this test only checked that everything read zero with no device open,
        // and a broken implementation that returned the same figure for both lanes sailed through it,
        // because zero equals zero. Given 22 and 5 it cannot.
        const double wasapiFigure = 22.0;
        const double asioFigure = 5.0;
        Check(CompositeRenderBackend.ForLane(RenderRoute.WasapiLane, wasapiFigure, asioFigure) == wasapiFigure,
            "the WASAPI lane must be answered with the WASAPI figure");
        Check(CompositeRenderBackend.ForLane(RenderRoute.AsioLane, wasapiFigure, asioFigure) == asioFigure,
            "the ASIO lane must be answered with the ASIO figure — reporting one lane with the other's "
            + "number hides the exact difference the readout exists to show, and both lanes run at once");
        Check(CompositeRenderBackend.ForLane(RenderRoute.WasapiLane, wasapiFigure, asioFigure)
              != CompositeRenderBackend.ForLane(RenderRoute.AsioLane, wasapiFigure, asioFigure),
            "the two lanes must not resolve to the same value when their figures differ");
        // Mixed is the single-lane world, where the WASAPI backend is the only one there is.
        Check(CompositeRenderBackend.ForLane(RenderRoute.Mixed, wasapiFigure, asioFigure) == wasapiFigure,
            "a single-lane setup reports through the WASAPI backend");

        var engine = new PlayoutEngine(new ReceiverDiagnostics());

        // ALL THREE CONFIGURATIONS. With no device open nothing may claim a figure in any of them —
        // a backend that invents one here would invent one in the field — and a lane that is not part
        // of the configuration must never be answered for at all.
        foreach (var configuration in AudioConfigurations.All)
        {
            using var backend = new CompositeRenderBackend(
                configuration.UsesAsio() ? AudioMode.BothIndependent : AudioMode.WasapiOnly,
                configuration.UsesAsio() ? "RemSound self-test — no such ASIO driver" : null,
                engine);
            backend.SetOutputDevices([]);   // nothing ticked, so nothing is open

            foreach (var route in new[] { RenderRoute.WasapiLane, RenderRoute.AsioLane, RenderRoute.Mixed })
            {
                Check(backend.ReportedOutputLatencyMsFor(route) == 0,
                    $"in {configuration.Describe()}, {route} must report no output latency with nothing open — "
                    + "a plausible-looking number in place of an unknown is the habit this whole change exists to break");
                Check(backend.OutputQueueMsFor(route) == 0,
                    $"in {configuration.Describe()}, {route} must report no queued audio with nothing open");
            }

            // A configuration without ASIO must have NOTHING to say about the ASIO lane, and vice
            // versa. That separation is what stops one lane's number being shown against the other.
            if (!configuration.UsesAsio())
            {
                Check(backend.ReportedOutputLatencyMsFor(RenderRoute.AsioLane) == 0,
                    $"{configuration.Describe()} must never answer for the ASIO lane");
            }
        }

        // ASIO has no intermediate buffer at all — it pulls straight from the playout engine. That is
        // a real structural difference between the lanes, not a rounding one, so it is pinned.
        using var asioBackend = new AsioRenderBackend("RemSound self-test — no such ASIO driver", engine);
        Check(asioBackend.OutputQueueMsFor(RenderRoute.AsioLane) == 0,
            "ASIO pulls straight from the engine and must never report a device queue — if this ever "
            + "becomes non-zero, the ASIO path has grown a buffer nobody meant to add");

        // --- AND THE CALLER, not just the rule ----------------------------------------------------
        // Everything above pins the formatter and the lane-picker given the right input. It says
        // nothing about whether the app ASKS the right question — and that is where this bug actually
        // lived: the readout passed "is an ASIO driver chosen" where it meant "are two lanes live".
        // With a driver chosen and no ASIO output ticked, it named both lanes and showed a WASAPI
        // line for a lane carrying nothing.
        //
        // Driven through the real window with the mode set to BothIndependent and NO outputs ticked,
        // which is the ASIO-driver-chosen-but-nothing-selected state. A test of the formatter alone
        // passes whether or not this is right, which is exactly why it needs its own check.
        MainForm form;
        try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
        catch (Exception ex) { return Skip($"headless main window could not be built: {ex.GetType().Name}: {ex.Message}"); }

        using (form)
        {
            var update = Require(typeof(MainForm).GetMethod("UpdateMeasuredLatencyReadout", BindingFlags.Instance | BindingFlags.NonPublic),
                "UpdateMeasuredLatencyReadout not found — this check would prove nothing");
            var readoutField = Require(typeof(MainForm).GetField("measuredLatencyReadout", BindingFlags.Instance | BindingFlags.NonPublic),
                "MainForm.measuredLatencyReadout not found — this check would prove nothing");
            var readout = Require(readoutField.GetValue(form) as Control, "the readout control must be reachable");

            // ALL THREE CONFIGURATIONS, driven through the real window by ticking the very lists
            // ActiveAudioConfiguration() reads — not by handing the formatter a boolean.
            //
            // BOTH DIRECTIONS are asserted. The previous version drove ONE state and only checked the
            // negative ("must not name lanes"), which a readout that never named a lane at all would
            // have passed — silently costing the both-lanes user the per-lane breakdown this box
            // exists for. One-sided assertions are how a coverage hole hides behind a green step.
            foreach (var configuration in AudioConfigurations.All)
            {
                SetAudioConfigurationForTest(form, configuration);
                update.Invoke(form, null);
                var shown = readout.Text ?? "";
                var namesWasapi = shown.Contains("WASAPI", StringComparison.OrdinalIgnoreCase);
                var namesAsio = shown.Contains("ASIO", StringComparison.OrdinalIgnoreCase);

                if (configuration.HasTwoLanes())
                {
                    Check(namesWasapi && namesAsio,
                        $"in {configuration.Describe()} the readout must name BOTH lanes — two lanes run at once with their "
                        + $"own buffers and their own device latency, so an unlabelled figure could be either. Got: \"{shown}\"");
                }
                else
                {
                    Check(!namesWasapi && !namesAsio,
                        $"in {configuration.Describe()} there is ONE lane, so the readout must not name lanes. An ASIO driver "
                        + $"is chosen in ASIO-only too, so asking the audio MODE instead of the configuration is what made "
                        + $"this name both. Got: \"{shown}\"");
                }
            }
        }

        return "both lanes are answered independently; nothing is claimed with no device open; ASIO reports no device queue "
             + "by construction; and the readout is driven through the real window in ALL THREE configurations, which must "
             + "name both lanes in one and neither lane in the other two";
    }

    /// <summary>
    /// Put a headless window into the ASIO-driver-chosen state, and PROVE it took.
    ///
    /// <para>There is no <c>SaveAudioMode</c>: the mode is derived from whether an ASIO driver name
    /// is set. A reflection call to a method that does not exist silently does nothing, so two of my
    /// own tests were switching mode by asking for a method that was deleted in 2026-05 and then
    /// asserting against the unchanged default — passing whether the code was right or wrong. This
    /// helper asks the right way and then CHECKS the mode actually moved, so the same silent no-op
    /// cannot happen again. 2026-08-24.</para>
    /// </summary>
    private static void SetAsioDriverForTest(MainForm form, string? driverName)
    {
        var settingsField = Require(typeof(MainForm).GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic),
            "MainForm.settings not found — the mode cannot be driven, so the test would prove nothing");
        var settings = Require(settingsField.GetValue(form),
            "the settings store is not reachable — the mode cannot be driven, so the test would prove nothing");
        var save = Require(settings.GetType().GetMethod("SaveAsioDriverName"),
            "SaveAsioDriverName not found — the audio mode is derived from it, so without it nothing can be driven");
        save.Invoke(settings, [driverName]);

        var load = Require(settings.GetType().GetMethod("LoadAudioMode"), "LoadAudioMode not found");
        var expected = string.IsNullOrWhiteSpace(driverName) ? AudioMode.WasapiOnly : AudioMode.BothIndependent;
        var actual = load.Invoke(settings, [AudioMode.WasapiOnly]);
        Check(Equals(actual, expected),
            $"setting the ASIO driver to \"{driverName ?? "(none)"}\" must put the app in {expected} (got {actual}) — "
            + "a test that thinks it switched mode and did not is worse than no test at all");
    }

    /// <summary>
    /// Put a headless window into one of the THREE audio configurations for real — by ticking the
    /// very checked-lists <c>ActiveAudioConfiguration()</c> reads — and prove it took.
    ///
    /// <para>Without this a gate step can only hand a formatter a boolean, which tests the wording
    /// and nothing about whether the app asks the right question. That gap is where the bug lived
    /// both times: the readout passed "is an ASIO driver chosen" where it meant "are two lanes live".
    /// Driving the real controls means all three configurations can be walked in one loop, and a step
    /// that quietly stops reaching the configuration fails instead of passing. 2026-08-24.</para>
    /// </summary>
    private static void SetAudioConfigurationForTest(MainForm form, AudioConfiguration configuration)
    {
        // An ASIO driver must be chosen for an ASIO tick to count as a lane at all — ticking a pair
        // with no driver is a stale setting, not a configuration (see AudioConfigurations.From).
        SetAsioDriverForTest(form, configuration.UsesAsio() ? "RemSound self-test — no such ASIO driver" : null);
        TickDeviceListForTest(form, "receiveOutputDevicesList", configuration.UsesWasapi());
        TickDeviceListForTest(form, "asioReceiveOutputDevicesList", configuration.UsesAsio());

        // PROVE it took. A reflection helper that silently does nothing leaves the test asserting
        // against the default configuration three times over and calling it coverage.
        var active = Require(typeof(MainForm).GetMethod("ActiveAudioConfiguration", BindingFlags.Instance | BindingFlags.NonPublic),
            "MainForm.ActiveAudioConfiguration not found — the configuration cannot be driven, so the test would prove nothing");
        var got = Require(active.Invoke(form, null), "ActiveAudioConfiguration returned nothing");
        Check(Equals(got, configuration),
            $"driving the window into {configuration.Describe()} must actually put it there (it reports {got}) — "
            + "a test that thinks it switched configuration and did not is worse than no test at all");
    }

    /// <summary>Tick or clear one synthetic entry in a device checked-list, so the window reports the
    /// configuration we want. The lists are what <c>ActiveAudioConfiguration()</c> counts.</summary>
    private static void TickDeviceListForTest(MainForm form, string fieldName, bool ticked)
    {
        var field = Require(typeof(MainForm).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic),
            $"MainForm.{fieldName} not found — the configuration cannot be driven, so the test would prove nothing");
        var list = Require(field.GetValue(form) as CheckedListBox,
            $"MainForm.{fieldName} is not a checked-list — the configuration cannot be driven");

        // Use the app's OWN mechanism for a tick nobody clicked. Every programmatic refresh in
        // MainForm raises this flag first, because the ItemCheck handlers re-open devices and marshal
        // work onto the window handle — which a headless form has not got. Reaching for anything else
        // here would be inventing a second way to do something the app already does.
        var suppressField = Require(typeof(MainForm).GetField("suppressDeviceCheckChange", BindingFlags.Instance | BindingFlags.NonPublic),
            "MainForm.suppressDeviceCheckChange not found — a programmatic tick would fire the real device handlers");
        var previous = suppressField.GetValue(form);
        suppressField.SetValue(form, true);
        try
        {
            list.Items.Clear();
            list.Items.Add("RemSound self-test — synthetic output device");
            list.SetItemChecked(0, ticked);
        }
        finally { suppressField.SetValue(form, previous); }
    }

    /// <summary>
    /// The jitter-buffer controls must say "jitter buffer" in EVERY audio mode.
    ///
    /// <para>These controls rename themselves depending on whether an ASIO driver is chosen. The
    /// rename from "latency" reached the WASAPI-plus-ASIO wording and nothing else, so the same
    /// control called itself "Audio latency in milliseconds" the moment you left that mode — and the
    /// ASIO row said "ASIO latency in milliseconds" with a tickbox beside it already reading
    /// "Continuous auto-tune ASIO jitter buffer". Two names for one thing, in one row.</para>
    ///
    /// <para>Ed: "make sure whatever mode we're in it says the right thing." So this drives the mode
    /// switch and checks both wordings, rather than trusting the one that happened to be on screen.
    /// The total-latency READOUT is deliberately exempt: it reports the jitter buffer plus what the
    /// hardware adds, which genuinely is total latency and is the one place the word belongs.</para>
    /// </summary>
    private static string? AuditJitterBufferWordingInEveryMode()
    {
        MainForm form;
        try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
        catch (Exception ex) { return Skip($"headless main window could not be built: {ex.GetType().Name}: {ex.Message}"); }

        using (form)
        {
            var apply = Require(typeof(MainForm).GetMethod("UpdateBothIndependentVisibility", BindingFlags.Instance | BindingFlags.NonPublic),
                "UpdateBothIndependentVisibility not found — this test would check nothing");
            object? Field(string name) =>
                Require(typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic),
                    $"MainForm.{name} not found — a renamed field would leave this test checking nothing").GetValue(form);

            var checkedModes = 0;
            foreach (var mode in new[] { AudioMode.WasapiOnly, AudioMode.BothIndependent })
            {
                // Drive it the way the app actually works — and prove it took. This used to call a
                // SaveAudioMode that was deleted in 2026-05, via a null-conditional that silently did
                // nothing, so both passes ran against the unchanged default and the "both modes"
                // in this test's name was a fiction.
                SetAsioDriverForTest(form, mode == AudioMode.BothIndependent ? "RemSound self-test — no such ASIO driver" : null);
                apply.Invoke(form, null);
                checkedModes++;

                foreach (var fieldName in new[] { "wasapiLatencyLabel", "asioLatencyLabel", "continuousIntervalLabel" })
                {
                    if (Field(fieldName) is not Control c) continue;
                    Check(!c.Text.Contains("latenc", StringComparison.OrdinalIgnoreCase),
                        $"in {mode}, {fieldName} still says \"{c.Text}\" — a jitter-buffer control must never be called latency, "
                        + "or it cannot be told apart from the Total latency readout beside it");
                }
                foreach (var fieldName in new[] { "maxLatencyBox", "maxLatencyAsioBox", "continuousTuneBox", "continuousTuneAsioBox", "continuousIntervalBox" })
                {
                    if (Field(fieldName) is not Control c) continue;
                    var spoken = c.AccessibleName ?? "";
                    Check(!spoken.Contains("latenc", StringComparison.OrdinalIgnoreCase),
                        $"in {mode}, {fieldName} is announced as \"{spoken}\" — the spoken name is the one that matters most here");
                    Check(!c.Text.Contains("latenc", StringComparison.OrdinalIgnoreCase),
                        $"in {mode}, {fieldName} shows \"{c.Text}\"");
                }

                // NAMING THE THING IT TUNES is the other half, and the half a "does it say latency"
                // check sails straight past. Renaming this combo to a bare "Auto-tune interval"
                // passed that check and told the user nothing — Ed: "how the hell is anyone going to
                // know what the auto tune interval is for?" Every auto-tune control has to say it
                // adjusts the jitter buffer, in both modes.
                foreach (var fieldName in new[] { "continuousIntervalLabel", "continuousIntervalBox", "continuousTuneBox", "continuousTuneAsioBox" })
                {
                    if (Field(fieldName) is not Control c) continue;
                    var shown = c.Text ?? "";
                    var spoken = c.AccessibleName ?? "";
                    // Prefer the SPOKEN name where there is one. A ComboBox's Text is its selected
                    // VALUE ("5 seconds"), not what the control is — reading Text first made this
                    // check assert against the wrong string entirely. Labels have no AccessibleName,
                    // so they fall through to their Text, which for a label is the name.
                    var names = string.IsNullOrEmpty(spoken) ? shown : spoken;
                    Check(names.Contains("jitter buffer", StringComparison.OrdinalIgnoreCase),
                        $"in {mode}, {fieldName} reads \"{names}\" — an auto-tune control must say WHAT it tunes, "
                        + "or the user is left guessing what the interval applies to");
                    if (!string.IsNullOrEmpty(spoken))
                    {
                        Check(spoken.Contains("jitter buffer", StringComparison.OrdinalIgnoreCase),
                            $"in {mode}, {fieldName} is ANNOUNCED as \"{spoken}\" — the spoken name is the one that matters here");
                    }
                }
            }

            Check(checkedModes == 2, "both audio modes must actually have been exercised");

            // === ALL THREE CONFIGURATIONS ===
            //
            // There are three, and they are decided by which OUTPUT DEVICES are ticked, not by the
            // audio-mode setting: WASAPI only, ASIO only, and both. The mode flag only says whether
            // an ASIO driver has been chosen at all. Looping over modes tests the wrong axis, which
            // is how ASIO-only got missed — it was shown a WASAPI control it wasn't using, and a
            // test that looped over two modes sailed past it. Ed has had to point the three
            // configurations out more than once; this is the guard so it stops happening.
            // The canonical list, not a hand-written one. A hand-written list is exactly how ASIO-only
            // kept getting missed: three entries typed out look complete until somebody types two.
            foreach (var configuration in AudioConfigurations.All)
            {
                var label = MainForm.AutoTuneIntervalLabel(configuration);
                Check(label.Contains("jitter buffer", StringComparison.OrdinalIgnoreCase),
                    $"in {configuration.Describe()} the interval label reads \"{label}\" — it must say what it tunes");
                if (configuration.HasTwoLanes())
                {
                    Check(label.Contains("WASAPI", StringComparison.OrdinalIgnoreCase) && label.Contains("ASIO", StringComparison.OrdinalIgnoreCase),
                        $"with BOTH lanes live the interval drives both auto-tunes, so it must name both — got \"{label}\"");
                }
                else
                {
                    Check(!label.Contains("WASAPI", StringComparison.OrdinalIgnoreCase) && !label.Contains("ASIO", StringComparison.OrdinalIgnoreCase),
                        $"in {configuration.Describe()} only one jitter buffer is in play, so the label must NOT name a lane — "
                        + $"naming the one you are not using is exactly the ASIO-only bug. Got \"{label}\"");
                }
            }
            Check(MainForm.AutoTuneIntervalLabel(AudioConfiguration.AsioOnly) == MainForm.AutoTuneIntervalLabel(AudioConfiguration.WasapiOnly),
                "ASIO-only and WASAPI-only must read identically — one jitter buffer is one jitter buffer. Unlike the "
                + "measured-latency readout, this label carries no per-lane FIGURES, only wording, so identical really is "
                + "the right claim here");

            // === AND THE WIRING, not just the rule ====================================================
            //
            // Everything above pins AutoTuneIntervalLabel, a pure function. It says nothing about
            // whether the control in front of the user ever RECEIVES that text — and rule-proven,
            // wiring-unproven is how a fix gets called done while the app still misbehaves. Deleting
            // the UpdateAutoTuneIntervalWording() call from UpdateBothIndependentVisibility left every
            // check above green. So: drive the real window into each configuration and read the
            // control's own text back. 2026-08-24.
            foreach (var configuration in AudioConfigurations.All)
            {
                SetAudioConfigurationForTest(form, configuration);
                apply.Invoke(form, null);

                var expected = MainForm.AutoTuneIntervalLabel(configuration);
                if (Field("continuousIntervalLabel") is Control intervalLabel)
                {
                    Check(intervalLabel.Text == expected,
                        $"in {configuration.Describe()} the interval label on screen reads \"{intervalLabel.Text}\" but the rule "
                        + $"says \"{expected}\" — the label is only right if something actually pushes it");
                }
                if (Field("continuousIntervalBox") is Control intervalBox)
                {
                    var spoken = intervalBox.AccessibleName ?? "";
                    Check(spoken == expected.Replace("&", ""),
                        $"in {configuration.Describe()} the interval combo is ANNOUNCED as \"{spoken}\" but the rule says "
                        + $"\"{expected.Replace("&", "")}\" — the spoken name is the one Ed actually gets");
                }
            }

            // And the readout keeps the word, because for that box it is the correct one.
            if (Field("measuredLatencyReadout") is Control readout)
            {
                Check((readout.AccessibleName ?? "").Contains("Total latency", StringComparison.OrdinalIgnoreCase),
                    "the Total latency readout must KEEP its name — it reports jitter buffer plus hardware, which really is total latency");
            }

            return "all three configurations checked (WASAPI only, ASIO only, both): nothing says \"latency\", every auto-tune control names the jitter buffer, a single-lane setup names no lane, and the Total latency readout keeps its own name";
        }
    }

    /// <summary>
    /// THE RENDER LOG MUST NAME THE CONFIGURATION, not just the mode.
    ///
    /// <para>The started line used to carry only the mode label, and the BothIndependent label reads
    /// "independent lanes (WASAPI + ASIO, no mix)". BothIndependent only means an ASIO driver has been
    /// CHOSEN — so an ASIO-only rig with every WASAPI output unticked logged a line naming WASAPI.</para>
    ///
    /// <para>That is not hypothetical and it is not harmless. On 2026-08-24 it convinced me Ed had
    /// both lanes live while he was telling me he had switched WASAPI off, and I told him so. He was
    /// right; the log was misleading. The mode-not-configuration trap, this time in the diagnostics —
    /// where the cost is a wrong diagnosis handed to the person who reported the fault.</para>
    /// </summary>
    private static string? AuditRenderLogNamesTheConfiguration()
    {
        // The real BothIndependent mode label — the one whose text says "WASAPI + ASIO" regardless.
        const string modeLabel = "independent lanes (WASAPI + ASIO, no mix)";

        foreach (var configuration in AudioConfigurations.All)
        {
            var label = RemSound.Receiver.CompositeRenderBackend.RenderStartLabel(
                modeLabel, configuration.UsesWasapi(), configuration.UsesAsio());

            Check(label.Contains(configuration.Describe(), StringComparison.Ordinal),
                $"the render log must name {configuration.Describe()} outright — got \"{label}\"");

            // The configuration must be read BEFORE any mode text, because the mode text names both
            // kinds of output whatever is ticked.
            var configPart = label.Split(", mode=", StringSplitOptions.None)[0];
            Check(configPart.Contains("WASAPI", StringComparison.OrdinalIgnoreCase) == configuration.UsesWasapi(),
                $"in {configuration.Describe()} the log's configuration must mention WASAPI only when a WASAPI output is "
                + $"actually ticked — got \"{configPart}\". A log that names a lane carrying nothing sends whoever reads it "
                + "chasing the wrong machine");
            Check(configPart.Contains("ASIO", StringComparison.OrdinalIgnoreCase) == configuration.UsesAsio(),
                $"in {configuration.Describe()} the log's configuration must mention ASIO only when an ASIO output is "
                + $"actually ticked — got \"{configPart}\"");
        }

        return "the render log leads with the configuration that is actually ticked, in all three, so the mode label's "
             + "\"WASAPI + ASIO\" can no longer be read as both lanes being live";
    }

    /// <summary>
    /// THE CAPTURE FIGURE ON THE WIRE — round-trip, and BOTH compatibility directions.
    ///
    /// <para>Appending to the format payload is only safe if old and new peers keep understanding
    /// each other, so both directions are proved rather than reasoned about. A new sender's 46-byte
    /// payload must parse identically for a reader that stops at 44 (the iOS app, an old desktop, the
    /// service before it updates), and an old sender's 36- or 44-byte payload must still parse here,
    /// yielding "not stated" rather than a wrong number or a rejection.</para>
    ///
    /// <para>Rejection is the failure that matters: a Format packet is what STARTS a stream, so a
    /// peer that refuses one goes completely silent rather than merely glitching. 2026-08-24.</para>
    /// </summary>
    private static string? AuditCaptureLatencyOnTheWire()
    {
        var fingerprint = new byte[RemPacket.PasswordFingerprintSize];
        for (var i = 0; i < fingerprint.Length; i++) fingerprint[i] = (byte)(i + 1);

        // ALL THREE CONFIGURATIONS, on every lane each one actually announces. The capture stage
        // itself is sender-side and has no lane — this machine mixes every ticked source into one
        // outgoing stream — but the packet the figure rides in DOES carry a lane byte, sitting
        // immediately in front of the fingerprint and the new field. So the claim worth proving per
        // configuration is that appending disturbs neither the lane a stream announces nor anything
        // else already in the payload, whichever lane that is.
        foreach (var configuration in AudioConfigurations.All)
        {
            var lanes = configuration.HasTwoLanes()
                ? new[] { RenderRoute.WasapiLane, RenderRoute.AsioLane }
                : [configuration.SingleRoute()!.Value];

            foreach (var lane in lanes)
            {
                // A real ASIO input figure — the kind the old code replaced with a flat 10 ms guess.
                var perLane = new AudioFormatInfo(48000, 2, 16, 1, 4, 192_000,
                    (int)AudioTransportCodec.Opus, 480, lane, CaptureLatencyMs: 0.7);
                var perLaneBuffer = new byte[RemPacket.FormatPayloadWithCaptureSize];
                var wrote = RemPacket.WriteFormatPayload(perLaneBuffer, perLane, fingerprint);
                Check(wrote == RemPacket.FormatPayloadWithCaptureSize,
                    $"in {configuration.Describe()} a sender with room must emit the "
                    + $"{RemPacket.FormatPayloadWithCaptureSize}-byte payload on {lane} (wrote {wrote})");
                Check(RemPacket.TryReadFormat(perLaneBuffer, out var perLaneBack, out var perLaneFingerprint),
                    $"in {configuration.Describe()} our own format packet for {lane} must parse — if this fails, that "
                    + "stream never starts at all");
                Check(Math.Abs(perLaneBack.CaptureLatencyMs - 0.7) < 0.051,
                    $"in {configuration.Describe()} the capture figure must survive on {lane} at 0.1 ms resolution "
                    + $"(sent 0.7, read {perLaneBack.CaptureLatencyMs})");
                Check(perLaneBack.Lane == lane,
                    $"in {configuration.Describe()} appending must not disturb the Lane byte in front of it "
                    + $"(sent {lane}, read {perLaneBack.Lane})");
                Check(perLaneFingerprint is not null && perLaneFingerprint.SequenceEqual(fingerprint),
                    $"in {configuration.Describe()} appending must not disturb the fingerprint in front of it");
            }
        }

        var format = new AudioFormatInfo(48000, 2, 16, 1, 4, 192_000,
            (int)AudioTransportCodec.Opus, 480, RenderRoute.AsioLane, CaptureLatencyMs: 0.7);

        var buffer = new byte[RemPacket.FormatPayloadWithCaptureSize];
        var written = RemPacket.WriteFormatPayload(buffer, format, fingerprint);
        Check(written == RemPacket.FormatPayloadWithCaptureSize,
            $"a sender with room must emit the {RemPacket.FormatPayloadWithCaptureSize}-byte payload (wrote {written})");

        // --- NEW SENDER -> OLD READER. Truncated at 44, exactly as a reader that has never heard of
        // the field would see it. Everything it used to understand must be untouched.
        Check(RemPacket.TryReadFormat(buffer.AsSpan(0, RemPacket.FormatPayloadWithFingerprintSize),
                out var asOldReader, out var oldFingerprint),
            "a reader that stops at 44 bytes must still accept a 46-byte sender's packet — refusing it would take "
            + "that peer's audio away completely, because the format packet is what starts the stream");
        Check(asOldReader.SampleRate == format.SampleRate && asOldReader.Channels == format.Channels
              && asOldReader.Codec == format.Codec && asOldReader.FrameSamplesPerChannel == format.FrameSamplesPerChannel
              && asOldReader.Lane == format.Lane,
            "every field an older reader already understood must read back unchanged");
        Check(oldFingerprint is not null && oldFingerprint.SequenceEqual(fingerprint),
            "the fingerprint must still reach a reader that stops at 44");
        Check(asOldReader.CaptureLatencyMs == 0,
            "a reader that stops short must report the capture figure as NOT STATED, not as zero-because-fast");

        // --- OLD SENDER -> NEW READER. 44 bytes and 36 bytes, both still valid.
        var legacy44 = new byte[RemPacket.FormatPayloadWithFingerprintSize];
        buffer.AsSpan(0, RemPacket.FormatPayloadWithFingerprintSize).CopyTo(legacy44);
        Check(RemPacket.TryReadFormat(legacy44, out var fromOld, out _),
            "a 44-byte payload from an older peer must still parse");
        Check(fromOld.CaptureLatencyMs == 0,
            "an older peer states nothing, and 'nothing' must read as 0 so the receiver falls back to its own estimate "
            + "rather than believing the far end has a zero-latency sound card");

        var legacy36 = new byte[RemPacket.FormatPayloadExtendedSize];
        buffer.AsSpan(0, RemPacket.FormatPayloadExtendedSize).CopyTo(legacy36);
        Check(RemPacket.TryReadFormat(legacy36, out var fromOlder, out var noFingerprint),
            "a 36-byte payload from a pre-encryption peer must still parse");
        Check(noFingerprint is null && fromOlder.CaptureLatencyMs == 0 && fromOlder.Lane == RenderRoute.AsioLane,
            "the oldest extended payload must still yield its Lane, no fingerprint and no capture figure");

        // --- The range, including the ends. A silly figure must clamp rather than wrap: a wrapped
        // ushort would read as a very SMALL latency, which is the dangerous direction.
        foreach (var ms in new[] { 0.0, 0.1, 0.7, 10.0, 42.5, 6553.4, 99999.0 })
        {
            var wide = new byte[RemPacket.FormatPayloadWithCaptureSize];
            RemPacket.WriteFormatPayload(wide, format with { CaptureLatencyMs = ms }, fingerprint);
            Check(RemPacket.TryReadFormat(wide, out var back, out _), $"a {ms} ms capture figure must still produce a parseable packet");
            var expected = Math.Min(ms, ushort.MaxValue / RemPacket.CaptureLatencyTicksPerMs);
            Check(Math.Abs(back.CaptureLatencyMs - expected) < 0.051,
                $"{ms} ms must travel as {expected} ms (read {back.CaptureLatencyMs}) — a wrapped value would read as a "
                + "tiny latency and quietly flatter the total");
        }

        // A negative can only come from a broken device query; it must not wrap to enormous.
        var negative = new byte[RemPacket.FormatPayloadWithCaptureSize];
        RemPacket.WriteFormatPayload(negative, format with { CaptureLatencyMs = -5 }, fingerprint);
        Check(RemPacket.TryReadFormat(negative, out var negBack, out _) && negBack.CaptureLatencyMs == 0,
            "a negative capture figure must go out as 'not stated', never wrap to a huge one");

        // --- No fingerprint means no room for the append, and that must stay a clean 36 bytes rather
        // than a zero-filled fingerprint a reader would believe.
        var noFp = new byte[RemPacket.FormatPayloadWithCaptureSize];
        var noFpLen = RemPacket.WriteFormatPayload(noFp, format, default);
        Check(noFpLen == RemPacket.FormatPayloadExtendedSize,
            $"with no fingerprint the payload must stop at {RemPacket.FormatPayloadExtendedSize} bytes (got {noFpLen}) — "
            + "writing the capture figure at offset 44 would leave an all-zero fingerprint in front of it, and a reader "
            + "would take that for a real one and report the wrong password warning");

        // --- AND THE READOUT MUST ACTUALLY PREFER IT ----------------------------------------------
        // Carrying the figure is pointless if the box goes on using the local guess. Ed's own case
        // first: the peer stated 0.7 while this machine would have estimated 10.
        Check(MainForm.PickCaptureLatencyMs(0.7, 0, 10.0) == 0.7,
            "a figure the SENDER measured must beat this machine's estimate — the capture happened on their hardware, "
            + "and estimating it here is guessing about a machine we cannot see");
        Check(MainForm.PickCaptureLatencyMs(0.7, 5.0, 10.0) == 0.7,
            "the peer's stated figure must beat even our own device's, because the journey starts at THEIR microphone");
        Check(MainForm.PickCaptureLatencyMs(0, 5.0, 10.0) == 5.0,
            "with nothing stated, our own device's measured figure is the next best thing");
        Check(MainForm.PickCaptureLatencyMs(0, 0, 10.0) == 10.0,
            "with nothing stated and no device to ask, the estimate is all there is");
        Check(MainForm.PickCaptureLatencyMs(0, 0, 0) == 0,
            "and when nothing at all is known the answer is nothing, never a plausible-looking number");

        return "the sender's capture figure round-trips at 0.1 ms on every lane in all three configurations; a 46-byte "
             + "packet still parses for a reader that stops at 44; 36- and 44-byte packets from older peers still parse "
             + "and read as NOT STATED; out-of-range values clamp instead of wrapping; and the readout prefers what the "
             + "peer measured over what this machine would have guessed";
    }

    /// <summary>
    /// A DIALOG'S log line must reach the real log, STAMPED with the audio configuration.
    ///
    /// <para>The dialogs record through <see cref="UiChangeLog"/> rather than through a logger of
    /// their own, and the main window points that at its configuration-stamping logger. Two things
    /// could quietly break: the window could stop wiring it (dialog changes vanish entirely), or it
    /// could be wired to something that does not stamp (dialog lines arrive with no way to tell which
    /// configuration the user was in). The dialog suite proves a control SAYS something; only this
    /// proves the saying arrives somewhere useful.</para>
    ///
    /// <para>Driven in ALL THREE configurations, because the stamp is the thing under test and a
    /// stamp hard-coded to one configuration is exactly the failure worth catching. 2026-08-24.</para>
    /// </summary>
    private static string? AuditDialogLoggingIsStamped()
    {
        MainForm form;
        try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
        catch (Exception ex) { return Skip($"headless main window could not be built: {ex.GetType().Name}: {ex.Message}"); }

        using (form)
        {
            Check(UiChangeLog.Sink is not null,
                "the main window must point UiChangeLog at its own logger — without that wiring every preference, "
                + "recording setting and service option a user changes in a dialog is recorded nowhere at all");

            var captured = new List<string>();
            form.LogForTest.EventTapForTest = line => { lock (captured) captured.Add(line); };
            try
            {
                foreach (var configuration in AudioConfigurations.All)
                {
                    SetAudioConfigurationForTest(form, configuration);
                    captured.Clear();

                    // Exactly what a dialog does when a user changes something in it.
                    UiChangeLog.Record("preference: SelfTestProbe", "on");

                    var line = captured.FirstOrDefault(l => l.Contains("SelfTestProbe", StringComparison.Ordinal));
                    line = Require(line, $"in {configuration.Describe()} a dialog's change reached no log at all — "
                        + "UiChangeLog is wired to nothing, so every dialog in the app is recording into a void");
                    Check(line.Contains($"[{configuration.Describe()}]", StringComparison.Ordinal),
                        $"in {configuration.Describe()} a dialog's line arrived as \"{line}\" — without the configuration "
                        + "stamp there is no way to tell afterwards which world the user changed it in, and the same "
                        + "setting means different things in each");
                    Check(line.Contains("on", StringComparison.Ordinal),
                        $"in {configuration.Describe()} the VALUE must survive the trip (got \"{line}\")");
                }
            }
            finally { form.LogForTest.EventTapForTest = null; }
        }

        return "a dialog's change reaches the real log, carrying its value and stamped with the audio configuration, "
             + "in all three configurations";
    }

    /// <summary>
    /// LOGGING OFF MUST MEAN NOTHING IS WRITTEN — including after it is switched off mid-session.
    ///
    /// <para>Ed, 2026-08-24: "please also make sure that as part of the tests, every logged event is
    /// gated. if i turn logs off, no more logging." Every write path checks the switch today, and
    /// nothing proved it — which is the same standard of evidence that let ten controls log nothing
    /// at all. Asserted against BYTES ON DISK rather than by reading the source, because the question
    /// is whether anything reaches the file, not whether a particular `if` is still present.</para>
    ///
    /// <para>The mid-session half is the one that matters most. Switching logging off in Preferences
    /// has to stop the writing THEN — a check that only tests the never-enabled case passes happily
    /// for a logger that keeps writing to a file it has already opened.</para>
    ///
    /// <para>It also guards the test tap added the same day. <c>EventTapForTest</c> deliberately fires
    /// BEFORE the switch, so the gate can prove a control reports what it did without writing files.
    /// That is right for a test hook and would be a leak in the product, so this drives the log with
    /// a tap attached and requires the file to stay untouched anyway.</para>
    ///
    /// <para>Not per-configuration: the log switch is one machine-wide setting writing one file, and
    /// nothing about it differs between WASAPI-only, ASIO-only and both.</para>
    /// </summary>
    private static string? AuditLoggingIsGated()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-loggate-" + Guid.NewGuid().ToString("N"));
        using var scope = AppConfig.UseThrowawayUserDataDirectory(scratch);

        // The writer keeps the file OPEN, so a plain ReadAllText throws. Share the handle rather than
        // closing the log to inspect it — closing it would change the very thing under test.
        static string ReadShared(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }

        static long BytesOnDisk()
        {
            try
            {
                var dir = AppConfig.LogsDirectory;
                if (!Directory.Exists(dir)) return 0;
                return Directory.GetFiles(dir, "*.log", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            }
            catch { return 0; }
        }

        static void DriveEveryWritePath(RemSoundLog log, string marker)
        {
            log.Event($"{marker} event");
            log.Snapshot(true, true, true, "Opus", 30, 30, 20, 1, 2, $"{marker} sender", 3, 4, 0, 0,
                $"{marker} receive", "ok", 0, 0, 30, 30);
        }

        var tapped = new List<string>();
        using (var log = new RemSoundLog())
        {
            // The tap is attached for the WHOLE test: it must never be a way for the file to grow.
            log.EventTapForTest = line => tapped.Add(line);

            // --- OFF FROM THE START -------------------------------------------------------------
            log.Enabled = false;
            for (var i = 0; i < 20; i++) DriveEveryWritePath(log, "never-enabled");
            Check(log.Path is null,
                $"with logging off the log must not even open a file (it opened \"{log.Path}\") — a file created and "
                + "left empty is still a file appearing in a folder the user asked to stay clean");
            Check(BytesOnDisk() == 0,
                $"with logging off, {BytesOnDisk()} bytes were written. Every write path must check the switch, not "
                + "just the one somebody remembered");
            Check(tapped.Count > 0,
                "the test tap must still see the events — it sits in front of the switch on purpose, so the gate can "
                + "prove a control reports what it did without writing files. If this ever stops firing, the control "
                + "suite's log checks are proving nothing");

            // --- ON: it must actually write, or the rest of this test proves nothing -------------
            log.Enabled = true;
            log.Event("marker-while-on");
            var whileOn = BytesOnDisk();
            Check(whileOn > 0, "with logging ON something must actually reach the file, or the OFF checks are vacuous");
            var path = Require(log.Path, "an enabled log must have opened a file");
            Check(ReadShared(path).Contains("marker-while-on", StringComparison.Ordinal),
                "the enabled log must contain what it was asked to write");

            // --- OFF AGAIN, MID-SESSION, with the file already open ------------------------------
            log.Enabled = false;
            for (var i = 0; i < 20; i++) DriveEveryWritePath(log, "after-switch-off");
            Check(BytesOnDisk() == whileOn,
                $"switching logging off mid-session must stop the writing THEN (the file grew from {whileOn} to "
                + $"{BytesOnDisk()} bytes). The file is already open at this point, so a path that checks the switch "
                + "only when opening keeps writing for the rest of the session");
            Check(!ReadShared(path).Contains("after-switch-off", StringComparison.Ordinal),
                "nothing written after the switch went off may appear in the file");

            // Disposing while off must not add a parting line either.
            var beforeDispose = BytesOnDisk();
            log.Dispose();
            Check(BytesOnDisk() == beforeDispose,
                "disposing a switched-off log must not write a closing line — 'off' has to mean off all the way to the end");
        }

        return "with logging off nothing reaches disk — not from events, not from state snapshots, not from disposal, "
             + "and not through the test tap; switching it off mid-session stops the writing on the spot";
    }

    /// <summary>
    /// THE RENDER-PERIOD COUNTER RESETS AS IT IS READ — so the tick must read it ONCE and share it.
    ///
    /// <para>This is the trap behind the regression Ed found on 2026-08-24: "asio auto tune is still
    /// going quite high. it hit 44 in my latest test ... i've never seen it do this." Earlier that
    /// day I made the latency probe read each lane's period, not realising the auto-tune's per-lane
    /// windows read the same self-resetting counter later in the same tick. Every enqueue then saw
    /// zero and was skipped, both lane windows stayed permanently empty, and each lane fell back to
    /// the MACHINE-WIDE window — the single thing those per-lane windows exist to prevent. On a rig
    /// with a Bluetooth headset on WASAPI and an Audient on ASIO, the ASIO lane was being sized by
    /// the Bluetooth device's callback period instead of its own 2 ms, and its buffer climbed to
    /// 44 ms and stayed there.</para>
    ///
    /// <para>Two halves, because neither is enough alone. The first drives the real counter and pins
    /// the reset-on-read behaviour, so nobody "fixes" it into something harmless and leaves every
    /// caller reading a stale peak. The second reads the source and requires the tick to ask for each
    /// route exactly once — the only place a second reader can be introduced, and precisely what I
    /// did. Driving the whole tick to prove it would need a live render callback on two real devices,
    /// which no gate can have.</para>
    ///
    /// <para>Not per-configuration: the counter and the sharing rule are the same in all three. What
    /// DIFFERS is the damage — in both-lanes the machine-wide fallback is the worst of two genuinely
    /// different devices, which is why Ed's Roger On sized his Audient.</para>
    /// </summary>
    private static string? AuditRenderPeriodIsReadOncePerTick()
    {
        // --- The trap itself, driven -------------------------------------------------------------
        var diag = new ReceiverDiagnostics();
        RemSound.Core.DiagnosticsGate.Enabled = true;
        try
        {
            // ALL THREE CONFIGURATIONS, on every lane each one actually renders. The counter behaves
            // identically everywhere — what differs is the DAMAGE when it is read twice. In a
            // single-lane configuration the machine-wide fallback is roughly the same device, so a
            // starved window costs little; in both-lanes it is the worst of two genuinely different
            // devices, which is how Ed's Bluetooth headset came to size his Audient. Which lane gets
            // starved is a per-configuration question, so it is asked per configuration.
            foreach (var configuration in AudioConfigurations.All)
            {
                var lanes = configuration.HasTwoLanes()
                    ? new[] { RenderRoute.WasapiLane, RenderRoute.AsioLane }
                    : [configuration.SingleRoute()!.Value];

                foreach (var route in lanes)
                {
                    diag.RecordRenderRead(1920, route);
                    Thread.Sleep(6);
                    diag.RecordRenderRead(1920, route);

                    var first = diag.MaxRenderCallbackGapMsFor(route);
                    var second = diag.MaxRenderCallbackGapMsFor(route);
                    Check(first > 0,
                        $"in {configuration.Describe()}, {route}: two render callbacks 6 ms apart must produce a "
                        + $"measurable period (got {first} ms) — without this the rest of the check proves nothing");
                    Check(second == 0,
                        $"in {configuration.Describe()}, {route}: the period accessor RESETS as it reads (got {second} ms "
                        + "on the second read). If that ever changes, say so here — but until it does, EVERY caller must "
                        + "assume one read per tick and share the value, because the second reader silently gets nothing");
                }
            }
        }
        finally { RemSound.Core.DiagnosticsGate.Enabled = false; }

        // --- And the tick must obey it ------------------------------------------------------------
        var root = FindSourceRoot();
        if (root is null) return Skip("the source tree is not reachable from here (set REMSOUND_SOURCE_ROOT)");
        var mainForm = Path.Combine(root, "src", "RemSound.App", "MainForm.cs");
        if (!File.Exists(mainForm)) return Skip($"{mainForm} not found");
        var source = File.ReadAllText(mainForm);

        var calls = CountOccurrences(source, "receiver.MaxRenderCallbackGapMsFor(");
        Check(calls == 3,
            $"MainForm asks the receiver for a lane's render period {calls} times; it must be exactly 3 — one per route, "
            + "together, in the shared read at the top of the tick. A fourth call is a second reader, and the second "
            + "reader gets ZERO: whichever window it feeds goes silently empty and starts being sized by another "
            + "device's hardware. That is the 44 ms ASIO buffer Ed found");
        Check(source.Contains("periodByRoute", StringComparison.Ordinal),
            "the shared per-route periods must still be read once into periodByRoute and handed to both the latency "
            + "probe and the auto-tune's per-lane windows");
        // The auto-tune's own window must take the shared value, never a fresh read.
        var laneGapIdx = source.IndexOf("var laneGap =", StringComparison.Ordinal);
        Check(laneGapIdx >= 0, "the auto-tune's per-lane render-period enqueue was not found — this check would prove nothing");
        var laneGapLine = source.Substring(laneGapIdx, Math.Min(160, source.Length - laneGapIdx));
        Check(!laneGapLine.Contains("MaxRenderCallbackGapMsFor", StringComparison.Ordinal),
            "the auto-tune's per-lane window must use the period already read this tick, not call the resetting "
            + $"accessor again — got: {laneGapLine.Split('\n')[0].Trim()}");

        return "the render-period counter is proved to reset on read, and the tick asks for each route exactly once "
             + "and shares it with both the latency probe and the auto-tune's per-lane windows";
    }

    /// <summary>
    /// RENDER TIME MUST BE ATTRIBUTED TO THE LANE THAT SPENT IT.
    ///
    /// <para>Ed, 2026-08-24, on his ASIO buffer climbing to 44 with a wireless headset added
    /// alongside: "why did the roger on cause the asio to ride up? that's not supposed to happen
    /// they're supposed to work independently of each other." He is right that they are independent
    /// where it counts — separate rings, separate resamplers, no shared cache and no shared lock in
    /// the render path, all verified. The only coupling left is the machine itself, and the evidence
    /// was that the render path went from 8 ms per second to 50 when the second output appeared.</para>
    ///
    /// <para>But the log summed every lane's render time into ONE number, so it could not say which
    /// lane had spent it — which is the difference between "the ASIO lane is expensive" and "the ASIO
    /// lane was starved while the other lane hogged the render path". Those want opposite fixes. The
    /// figure is now split per lane, and this proves the split is real and lands on the right
    /// lane rather than being three copies of the same total.</para>
    /// </summary>
    private static string? AuditRenderWorkIsAttributedPerLane()
    {
        RemSound.Core.DiagnosticsGate.Enabled = true;
        try
        {
            foreach (var configuration in AudioConfigurations.All)
            {
                var engine = new PlayoutEngine(new ReceiverDiagnostics());
                engine.SetIndependentLaneLatency(configuration.HasTwoLanes());
                engine.SetLaneActive(RenderRoute.WasapiLane, configuration.UsesWasapi());
                engine.SetLaneActive(RenderRoute.AsioLane, configuration.UsesAsio());
                engine.SetMaxLatencyMs(RenderRoute.Mixed, 30);
                engine.SetMaxLatencyMs(RenderRoute.WasapiLane, 30);
                engine.SetMaxLatencyMs(RenderRoute.AsioLane, 30);

                var session = engine.GetOrCreateSession(new IPEndPoint(IPAddress.Parse("192.168.1.63"), 47830), 1, 1024 * 1024);
                FillSession(session, 0.4f);

                engine.TakeRenderTicksByRoute();   // clear whatever session setup happened to cost

                // Read ONLY the lane this configuration renders on, then require the time to have
                // landed on that lane and nowhere else. A split that is really the same total copied
                // three times passes any check that only asks "is it greater than zero".
                var lane = configuration.SingleRoute() ?? RenderRoute.WasapiLane;
                var surface = lane == RenderRoute.AsioLane ? engine.AsioLaneOutput : engine.WasapiLaneOutput;
                var buffer = new byte[960 * 8];
                for (var i = 0; i < 40; i++) { Array.Clear(buffer); surface.Read(buffer, 0, buffer.Length); }

                var (wasapiMs, asioMs, mixedMs) = engine.TakeRenderTicksByRoute();
                var spentOnLane = lane == RenderRoute.AsioLane ? asioMs : wasapiMs;
                var spentElsewhere = lane == RenderRoute.AsioLane ? wasapiMs : asioMs;

                Check(spentOnLane > 0,
                    $"in {configuration.Describe()} forty reads on {lane} recorded no render time against it — the split "
                    + "is not being attributed at all, so the log still cannot say which lane spent the time");
                Check(spentElsewhere == 0,
                    $"in {configuration.Describe()} reading only {lane} charged {spentElsewhere} ticks to the OTHER lane. "
                    + "A split that lands on the wrong lane is worse than no split: it would say the quiet lane was the "
                    + "expensive one, and send the next investigation in the opposite direction");
                Check(mixedMs == 0,
                    $"in {configuration.Describe()} a lane-filtered read must not be charged to the single-lane (Mixed) "
                    + $"path (got {mixedMs} ticks)");

                // Taking it must RESET it — the next second must not inherit this one's total.
                var (againW, againA, againM) = engine.TakeRenderTicksByRoute();
                Check(againW == 0 && againA == 0 && againM == 0,
                    $"in {configuration.Describe()} the per-lane render time must reset when taken, or every second "
                    + $"reports the sum of the whole session (got {againW}/{againA}/{againM})");
            }
        }
        finally { RemSound.Core.DiagnosticsGate.Enabled = false; }

        return "render time lands on the lane that actually spent it, never on the other lane or the single-lane path, "
             + "and resets when taken — in all three configurations";
    }

    /// <summary>
    /// WHEN THE LAST STREAM GOES, THE READOUT MUST SAY "NOT RECEIVING".
    ///
    /// <para>Ed, 2026-08-24: "i killed the sender ... and the latency readout stayed active. it should
    /// have said stopped receiving." On that occasion it was telling the truth — his send-only service
    /// took the stream over three seconds after the app closed, so audio really was still arriving —
    /// but the question exposed a gap in the evidence. The pieces were each tested: a lane with no
    /// sessions reports no hardware figure, and a zero hardware figure formats as "not receiving".
    /// Nothing joined them up, so when he asked whether the chain worked I could not say from the
    /// tests, only from a log.</para>
    ///
    /// <para>So: drive the real engine, take the stream away, and require the words the user reads. A
    /// readout stuck on a stale figure after the far end goes quiet would tell somebody their audio
    /// was fine while they heard silence, which is the worst direction for this box to be wrong
    /// in.</para>
    /// </summary>
    private static string? AuditReadoutSaysNotReceivingWhenStreamsGo()
    {
        var peer = new IPEndPoint(IPAddress.Parse("192.168.1.64"), 47830);
        const ushort streamId = 11;
        const int setBufferMs = 30;

        foreach (var configuration in AudioConfigurations.All)
        {
            var engine = new PlayoutEngine(new ReceiverDiagnostics());
            engine.SetIndependentLaneLatency(configuration.HasTwoLanes());
            engine.SetLaneActive(RenderRoute.WasapiLane, configuration.UsesWasapi());
            engine.SetLaneActive(RenderRoute.AsioLane, configuration.UsesAsio());
            engine.SetMaxLatencyMs(RenderRoute.Mixed, setBufferMs);

            var lane = configuration.SingleRoute() ?? RenderRoute.WasapiLane;
            var session = engine.GetOrCreateSession(peer, streamId, 1024 * 1024);
            Check(session.Route == lane, $"in {configuration.Describe()} the stream must land on {lane} to start with");
            Check(engine.HasSessionsForRoute(lane),
                $"in {configuration.Describe()} the lane must report a live stream while one exists — otherwise the "
                + "rest of this proves nothing");

            // WHILE RECEIVING: a real hardware figure, and the box must NOT say "not receiving".
            var live = MainForm.LaneHardwareMs(
                laneHasSessions: engine.HasSessionsForRoute(lane), reportedOutputMs: 3.6,
                lanePeriodMs: 2, fallbackPeriodMs: 2, sharedMs: 3.2, outputQueueMs: 0);
            Check(live > 0, $"in {configuration.Describe()} a live lane must produce a hardware figure (got {live})");
            var whileLive = MainForm.FormatMeasuredLatency(configuration, setBufferMs, live, setBufferMs, live);
            Check(!whileLive.Contains("not receiving", StringComparison.Ordinal),
                $"in {configuration.Describe()} the box must not claim silence while audio is arriving (got: {whileLive})");

            // THE SENDER GOES AWAY — the stream is pruned, exactly as PruneIdleSessions does.
            Check(engine.RemoveSession(peer, streamId),
                $"in {configuration.Describe()} removing the only stream must report that it removed something");
            Check(!engine.HasSessionsForRoute(lane),
                $"in {configuration.Describe()} with its stream gone the lane must stop reporting a live stream — if it "
                + "does not, every figure downstream keeps being calculated for audio that is not arriving");

            var afterwards = MainForm.LaneHardwareMs(
                laneHasSessions: engine.HasSessionsForRoute(lane), reportedOutputMs: 3.6,
                lanePeriodMs: 2, fallbackPeriodMs: 2, sharedMs: 3.2, outputQueueMs: 0);
            Check(afterwards == 0,
                $"in {configuration.Describe()} a lane with no stream must report NO hardware figure, even though the "
                + $"device is still open and still stating its latency (got {afterwards})");

            var shown = MainForm.FormatMeasuredLatency(configuration, setBufferMs, afterwards, setBufferMs, afterwards);
            Check(shown.Contains("not receiving", StringComparison.Ordinal),
                $"in {configuration.Describe()} the box must say \"not receiving\" once the last stream has gone — "
                + $"leaving a stale figure up tells somebody their audio is fine while they are hearing silence. "
                + $"Got: \"{shown}\"");
            Check(!shown.Contains("Total latency approximately", StringComparison.Ordinal),
                $"in {configuration.Describe()} it must not still quote a total for a journey nothing is making "
                + $"(got: \"{shown}\")");
        }

        return "a lane whose last stream has gone stops reporting a hardware figure and the box says \"not receiving\" "
             + "— proved end to end, in all three configurations";
    }

    /// <summary>
    /// AN OUTPUT WINDOWS KILLS UNDER US MUST COME BACK ON ITS OWN.
    ///
    /// <para>Ed, 2026-08-26: "i went into the laptops sound control panel ... messed about with some
    /// roger level settings ... and then when i closed it, no sound. I had to uncheck and recheck the
    /// roger for it to work." And separately, after hibernate: "something about bringing the soundcard
    /// back did not work."</para>
    ///
    /// <para>Both are the same hole. An invalidated output is flagged and re-opened by the next
    /// <c>SetOutputDevices</c> — which the hot-plug notifier fires when a device is added or removed.
    /// Changing a device's properties does neither: the device stays present, the endpoint is
    /// invalidated, no notification arrives, and nothing re-applies. Unticking and re-ticking fixed it
    /// because that IS a manual SetOutputDevices — the call the recovery had been waiting for.
    /// <c>OnPropertyValueChanged</c> is deliberately ignored by the notifier as too chatty, and that
    /// is the right call for deciding the device SET — so the fix is to stop depending on a
    /// notification at all and let the per-second tick notice.</para>
    ///
    /// <para>The second trigger covers the resume case: a device that is ticked but never opened,
    /// because it was not back yet when the post-resume re-init ran 1.5 seconds after wake. A wireless
    /// headset takes far longer than that. One attempt, failed, nothing tried again.</para>
    ///
    /// <para>Same in all three configurations, and said rather than left silent: this is the WASAPI
    /// output path. ASIO owns its driver outright and recovers through its own park/re-open, so it
    /// raises no faulted flag — which is why <c>CompositeRenderBackend.HasFaultedOutput</c> reports
    /// the WASAPI side only.</para>
    /// </summary>
    private static string? AuditFaultedOutputReopensItself()
    {
        foreach (var configuration in AudioConfigurations.All)
        {
            var where = configuration.Describe();

            // Nothing wrong: leave the audio alone. Re-applying devices for no reason would break
            // audio every second, which is far worse than the fault being fixed.
            Check(!MainForm.ShouldReopenOutputs(anyFaulted: false, missingCount: 0, withinRetryInterval: false),
                $"in {where} a healthy output must never be re-opened — tearing a live device down once a second "
                + "would be a worse bug than the one this fixes");

            // Ed's control-panel case: the device is still present and still ticked, but its endpoint
            // was invalidated. Nothing external will ever ask for a re-apply.
            Check(MainForm.ShouldReopenOutputs(anyFaulted: true, missingCount: 0, withinRetryInterval: false),
                $"in {where} an output Windows invalidated must be re-opened without waiting for a hot-plug "
                + "notification — the device never left, so no notification is coming, and the user is sitting in "
                + "silence until they untick and re-tick it by hand");

            // The resume case: ticked, not open, nothing retrying.
            Check(MainForm.ShouldReopenOutputs(anyFaulted: false, missingCount: 1, withinRetryInterval: false),
                $"in {where} a ticked device that is not open must be retried — after a resume a wireless device can "
                + "come back long after the post-resume re-init has already given up");

            // Rate limit: a device that is genuinely gone must not be hammered every tick.
            Check(!MainForm.ShouldReopenOutputs(anyFaulted: true, missingCount: 2, withinRetryInterval: true),
                $"in {where} the retry must respect its interval — a device that is truly unplugged would otherwise "
                + "be re-opened every single tick for as long as it stays away");
        }

        // The interval itself, pinned in absolute seconds rather than against its own constant: a
        // measurement against the value that sets it proves only that SOME delay exists.
        Check(MainForm.FaultedOutputRetryIntervalForTest == TimeSpan.FromSeconds(3),
            $"the re-open retry must be 3 seconds — long enough not to spin on a dead card, short enough that a "
            + $"person changing a setting in the sound panel does not notice the gap "
            + $"(got {MainForm.FaultedOutputRetryIntervalForTest.TotalSeconds}s)");

        return "an output invalidated by Windows, and a ticked output that never opened, are both re-opened by the "
             + "app itself on a 3-second retry — in all three configurations, with a healthy output never disturbed";
    }

    /// <summary>
    /// THE WHOLE CHAIN: a faulted output must make the per-second tick re-apply the devices.
    ///
    /// <para>Ed, 2026-08-27: "your fix for the roger did not work." His log shows the endpoint dying
    /// — <c>lost the device: COMException: 0x88890004</c>, which is AUDCLNT_E_DEVICE_INVALIDATED —
    /// and then nothing for one hundred and eight seconds until he unticked and re-ticked it by
    /// hand.</para>
    ///
    /// <para>The test that shipped with that fix checked <c>ShouldReopenOutputs</c>, a pure function,
    /// and it was green. It proved the RULE and never once proved the WIRING: that a faulted output
    /// actually reaches the rule, that the tick actually calls the heal, or that the heal actually
    /// re-applies. That is the same rule-tested / wiring-untested gap this whole campaign keeps
    /// finding, committed by me on the very day I wrote the lesson up.</para>
    ///
    /// <para>So this drives the real window's real tick and watches the real log.</para>
    /// </summary>
    private static string? AuditFaultedOutputHealRunsInTheTick()
    {
        MainForm form;
        try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
        catch (Exception ex) { return Skip($"headless main window could not be built: {ex.GetType().Name}: {ex.Message}"); }

        using (form)
        {
            var lines = new List<string>();
            form.LogForTest.EventTapForTest = line => { lock (lines) lines.Add(line); };
            try
            {
                // Nothing wrong: the tick must not touch the devices. Re-applying for no reason would
                // break audio every second, which is worse than the fault being fixed.
                form.ReceiverForTest.ForceFaultedOutputForTest = false;
                lines.Clear();
                form.SnapshotTickForTest();
                Check(!lines.Any(l => l.Contains("re-opening", StringComparison.OrdinalIgnoreCase)),
                    $"a healthy tick must not re-open anything — got: [{string.Join(" | ", lines)}]");

                // AN OUTPUT FAULTS. The tick — not a hot-plug notification, which cannot come when the
                // device never left — must notice within one tick and re-apply.
                form.ReceiverForTest.ForceFaultedOutputForTest = true;
                lines.Clear();
                form.SnapshotTickForTest();
                var reopened = lines.FirstOrDefault(l => l.Contains("re-opening", StringComparison.OrdinalIgnoreCase));
                Check(reopened is not null,
                    "a faulted output must make the per-second tick re-apply the devices. Nothing else is coming: the "
                    + "device never left, so no hot-plug notification will arrive, and the user is left with silence "
                    + "until they untick and re-tick it by hand. This is the check that was missing when the fix "
                    + $"shipped. Tick logged: [{(lines.Count == 0 ? "NOTHING AT ALL" : string.Join(" | ", lines))}]");
                Check(reopened!.Contains("[", StringComparison.Ordinal),
                    $"the re-open line must carry the audio configuration like every other change — got: {reopened}");

                // It must not then re-apply on EVERY tick — a device that is genuinely gone would be
                // hammered once a second for as long as it stays away.
                lines.Clear();
                form.SnapshotTickForTest();
                Check(!lines.Any(l => l.Contains("re-opening", StringComparison.OrdinalIgnoreCase)),
                    $"the retry interval must hold off the next attempt — got: [{string.Join(" | ", lines)}]");

                // AND IT MUST NOTICE RECOVERY, so the log shows the round trip rather than an
                // unexplained gap.
                form.ReceiverForTest.ForceFaultedOutputForTest = false;
                lines.Clear();
                form.SnapshotTickForTest();
                Check(lines.Any(l => l.Contains("recovered", StringComparison.OrdinalIgnoreCase)),
                    $"once the output is healthy again the log must say so — an episode that starts and never ends "
                    + $"reads as still broken. Got: [{string.Join(" | ", lines)}]");
            }
            finally
            {
                form.LogForTest.EventTapForTest = null;
                form.ReceiverForTest.ForceFaultedOutputForTest = false;
            }
        }

        return "a faulted output makes the real per-second tick re-apply the devices, once per episode, stamped with "
             + "the configuration, and the recovery is logged too";
    }

    /// <summary>
    /// THE LOG MUST SAY WHICH BUILD IT IS.
    ///
    /// <para>Every test build in a release cycle reports the same version, so a log from a machine
    /// running a week-old copy is indistinguishable from one running this morning's. On 2026-08-27
    /// that cost an entire diagnosis: Ed reported that a fix was not working, his log showed the
    /// fault it was supposed to heal, and nothing anywhere could tell me whether the build he ran
    /// contained the fix at all. "It must have been an old build" is not an answer anybody can check.</para>
    ///
    /// <para>Each assembly is stamped separately, because these deploy as loose files that sync
    /// between machines — a half-delivered update (the app new, the receiver old) is a real
    /// possibility and would otherwise be invisible.</para>
    /// </summary>
    private static string? AuditLogIdentifiesTheBuild()
    {
        var line = MainForm.DescribeAssemblyBuilds();
        Check(!string.IsNullOrWhiteSpace(line) && line != "(unavailable)",
            "the build stamp must produce something — without it a log cannot say which build wrote it");

        foreach (var assembly in new[] { "RemSound=", "RemSound.Receiver=", "RemSound.Core=", "RemSound.Sender=" })
        {
            Check(line.Contains(assembly, StringComparison.Ordinal),
                $"the build stamp must name {assembly.TrimEnd('=')} — a half-delivered update that leaves one assembly behind is "
                + $"exactly what this exists to make visible. Got: {line}");
        }

        // The ids must be REAL and DISTINCT, not a constant or the same value repeated. A stamp that
        // reads the same for every assembly and every build would look informative and say nothing.
        var ids = new List<string>();
        foreach (var part in line.Split(", ", StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var rest = part[(eq + 1)..];
            var id = rest.Split(' ')[0];
            Check(id.Length == 8 && id.All(Uri.IsHexDigit),
                $"each build id must be 8 hex characters of the assembly's own module id — got \"{id}\" in: {line}");
            Check(id != "00000000", $"a zeroed build id identifies nothing (in: {line})");
            ids.Add(id);
        }
        Check(ids.Count >= 4, $"all four assemblies must be stamped (got {ids.Count}) in: {line}");
        Check(ids.Distinct().Count() == ids.Count,
            $"the assemblies must not share one id — a stamp repeating the same value cannot show a half-delivered "
            + $"update, which is the whole point of stamping them apart. Got: {line}");

        return $"the log names every RemSound assembly with its own build id and file time, so a log can always say "
             + $"exactly which build wrote it ({ids.Count} assemblies stamped)";
    }
}
