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
            Control? ByField(string field) =>
                typeof(MainForm).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(form) as Control;

            var jitterBox = ByField("maxLatencyBox");
            var readoutBox = ByField("measuredLatencyReadout");
            var smoothnessBox = ByField("smoothnessBox");
            Check(jitterBox is not null && readoutBox is not null && smoothnessBox is not null,
                "the jitter-buffer, readout and smoothness controls must all be found by field name, or this test is comparing nothing");

            // Walk the whole window the way Tab does, and record where each one lands.
            var order = new List<Control>();
            var seen = new HashSet<Control>();
            for (var c = form.GetNextControl(form, true); c is not null && seen.Add(c); c = form.GetNextControl(c, true))
            {
                order.Add(c);
            }

            var jitter = order.IndexOf(jitterBox!);
            var readout = order.IndexOf(readoutBox!);
            var smoothness = order.IndexOf(smoothnessBox!);

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
            // The audio mode is DERIVED from whether an ASIO driver name is set — there is no
            // SaveAudioMode, and a reflection call to one silently does nothing. Ask, then verify.
            SetAsioDriverForTest(form, "RemSound self-test — no such ASIO driver");

            var update = typeof(MainForm).GetMethod("UpdateMeasuredLatencyReadout", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new CheckFailed("UpdateMeasuredLatencyReadout not found — this check would prove nothing");
            update.Invoke(form, null);

            var readout = typeof(MainForm).GetField("measuredLatencyReadout", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(form) as Control;
            Check(readout is not null, "the readout control must be reachable");
            var shown = readout!.Text ?? "";
            Check(!shown.Contains("WASAPI", StringComparison.OrdinalIgnoreCase) && !shown.Contains("ASIO", StringComparison.OrdinalIgnoreCase),
                $"with an ASIO driver chosen but NO ASIO output ticked there is one lane, so the readout must not name lanes — "
                + $"asking the audio MODE instead of the configuration is what made it name both. Got: \"{shown}\"");
        }

        return "both lanes are answered independently; nothing is claimed with no device open; ASIO reports no device queue "
             + "by construction; and the readout asks the CONFIGURATION rather than the audio mode";
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
        var settings = typeof(MainForm).GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(form)
            ?? throw new CheckFailed("the settings store is not reachable — the mode cannot be driven, so the test would prove nothing");
        var save = settings.GetType().GetMethod("SaveAsioDriverName")
            ?? throw new CheckFailed("SaveAsioDriverName not found — the audio mode is derived from it, so without it nothing can be driven");
        save.Invoke(settings, [driverName]);

        var load = settings.GetType().GetMethod("LoadAudioMode")
            ?? throw new CheckFailed("LoadAudioMode not found");
        var expected = string.IsNullOrWhiteSpace(driverName) ? AudioMode.WasapiOnly : AudioMode.BothIndependent;
        var actual = load.Invoke(settings, [AudioMode.WasapiOnly]);
        Check(Equals(actual, expected),
            $"setting the ASIO driver to \"{driverName ?? "(none)"}\" must put the app in {expected} (got {actual}) — "
            + "a test that thinks it switched mode and did not is worse than no test at all");
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
            var apply = typeof(MainForm).GetMethod("UpdateBothIndependentVisibility", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new CheckFailed("UpdateBothIndependentVisibility not found — this test would check nothing");
            object? Field(string name) =>
                typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(form);

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
                "ASIO-only and WASAPI-only must read identically — one jitter buffer is one jitter buffer");

            // And the readout keeps the word, because for that box it is the correct one.
            if (Field("measuredLatencyReadout") is Control readout)
            {
                Check((readout.AccessibleName ?? "").Contains("Total latency", StringComparison.OrdinalIgnoreCase),
                    "the Total latency readout must KEEP its name — it reports jitter buffer plus hardware, which really is total latency");
            }

            return "all three configurations checked (WASAPI only, ASIO only, both): nothing says \"latency\", every auto-tune control names the jitter buffer, a single-lane setup names no lane, and the Total latency readout keeps its own name";
        }
    }
}
