using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Windows.Forms;
using NAudio.Wave;
using NAudio.Wave.Asio;
using RemSound.Core;
using RemSound.Receiver;
using RemSound.Sender;

namespace RemSound.App;

/// <summary>
/// WHAT THE LATENCY LAB LEFT BEHIND. The lab (Ed, 2026-09-26) put every idea from the hunt for lower ASIO latency than
/// Jamulus behind its own switch. After the live test with Andre (2026-09-29) per-stream buffers are normal behaviour and
/// the lean margins and rare-dropout tuning are one per-profile tick box (SelfTest.LowestDelayTune.cs); and since
/// 2026-10-01 the last switch, Jamulus-style Opus frames, is automatic (JamulusStyle) and the lab is gone. These steps prove
/// per-stream buffers, and that the Jamulus-style stream - the one thing here that changes what goes on the wire - reaches
/// only a copy that plays through ASIO and has said so. The research is in PLAN-latency-hunt-2026-09-26.md.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>STREAM BUFFERS: per-stream buffers on from the start and lean margins off; the per-stream formula, normal and
    /// lean, and the effective target.</summary>
    private static string StreamBuffersStartOnAndSizeByFormula()
    {
        using (var fresh = new AudioReceiver())
            Check(fresh.PerStreamBuffers && !fresh.LeanTuning, "a receiver must size each stream from its own arrivals from the start, with the usual margins");
        Check(!Profile.NewBlank().AutoTuneLowestDelay, "a new profile's lowest-delay auto-tune must be off");

        // The formula, by hand: 2.5 ms Opus (3 ms rounded up), a 3 ms gap, a 1 ms render period.
        Check(AudioReceiver.PerStreamNeedMs(3, 1, 3, lean: false) == 10, "normal margins: gap 3 + render floor 2 + safety 5 = 10 ms");
        Check(AudioReceiver.PerStreamNeedMs(3, 1, 3, lean: true) == 6, "lean margins: gap 3 + render 1 + safety 2 = 6 ms");
        Check(AudioReceiver.PerStreamNeedMs(0, 0, 20, lean: false) == 30, "a 20 ms codec frame keeps its floor of one and a half frames, 30 ms");
        Check(AudioReceiver.PerStreamNeedMs(0, 0, 20, lean: true) == 20, "lean margins lower that floor to one frame, 20 ms");

        // The Total latency box's buffer figure: the setting when the streams play at it, else what they play at, held
        // against the deadband.
        Check(MainForm.BufferFigureToShow(-1, 30, 30) == 30 && MainForm.BufferFigureToShow(12, 40, 40) == 40,
            "the box must show the setting whenever the streams play at it - a slider move shows at once");
        Check(MainForm.BufferFigureToShow(-1, 30, 12) == 12, "a stream played below the setting must be shown at what it plays at");
        Check(MainForm.BufferFigureToShow(12, 30, 11) == 12 && MainForm.BufferFigureToShow(12, 30, 12 - MainForm.LatencyReadoutDeadbandMsForTest) == 12 - MainForm.LatencyReadoutDeadbandMsForTest,
            "and held until it has moved the readout's deadband, so a stream settling 1 ms a second does not rewrite the box every second");

        // The usual auto-tune keeps the long-standing margins; the lean ones go with the lowest-delay tune, which stands it aside.
        Check(MainForm.TuneRenderFloorMs == 2 && MainForm.TuneSafetyMs == 5 && MainForm.CodecFloorMs(3) == 5,
            "the usual auto-tune keeps its 2 ms render floor, 5 ms safety and 1.5-frame floor");

        using var receiver = new AudioReceiver();
        var playout = receiver.GetOrCreateSessionForTest(new IPEndPoint(IPAddress.Parse("10.1.2.3"), 47830), 7);
        playout.OwnNeedMs = 0;
        Check(playout.EffectiveTargetMs(30) == 30, "with no measured need of its own, a stream plays at the output's target");
        playout.OwnNeedMs = 12;
        Check(playout.EffectiveTargetMs(30) == 12, "a stream's own need below the output's target is what it plays at");
        playout.OwnNeedMs = 45;
        Check(playout.EffectiveTargetMs(30) == 30, "a stream's own need is never allowed ABOVE the output's target: the output's setting stays the ceiling");

        // Coming down: only the output's own setting lowered starts the quick descent (and its snap); a stream's own need
        // creeping down takes the steady path (the simulator, 2026-09-29: counted as a lowering, every second of it pulled
        // the pitch about 7 cents and could snap a network stall's backlog away).
        var frames = new float[2 * 480];
        void Read(int outputTargetMs) => playout.ReadFloats(frames, 480, outputTargetMs, outputTargetMs);
        playout.OwnNeedMs = 20;
        Read(30);
        playout.OwnNeedMs = 19;
        Read(30);
        Check(!playout.DescentActive, "a stream's own need coming down 1 ms must not start the quick descent");
        Read(25);
        Check(!playout.DescentActive, "nor the output's setting lowered while the stream plays below it anyway");
        playout.OwnNeedMs = 0;
        Read(15);
        Check(playout.DescentActive, "the output's setting lowered below where the stream plays must start it");
        return "per-stream buffers on from the start with the usual margins, the lowest-delay tune off in a new profile; the "
             + "usual auto-tune keeps 2/5 ms margins; the per-stream need is 10 ms normally and 6 ms lean for a 2.5 ms stream with "
             + "a 3 ms gap, with the codec floor kept; a stream plays below the output only by its own need, never above it; only "
             + "the output's setting lowered starts the quick descent";
    }

    /// <summary>STREAM BUFFERS: two streams from one peer, one smooth and one lumpy - each sized from its own arrivals from
    /// the start (after a few seconds of evidence), coming down at most 1 ms a second; leaner with the lowest-delay tune's
    /// margins; and back to the output's target when the simulator turns it off to compare.</summary>
    private static string PerStreamBuffersFollowTheirOwnArrivals()
    {
        var peer = new IPEndPoint(IPAddress.Parse("192.168.77.10"), 47830);
        const ushort Smooth = 3101, Lumpy = 3102;
        try
        {
            using var receiver = new AudioReceiver();
            receiver.SetOutputDevices([]);
            receiver.SetPlaybackEnabled(true);
            var (rawKey, fingerprint) = RemSoundCrypto.ForPlainPassword("latency-lab");
            receiver.AudioKey = Require(rawKey, "the gate password must derive a key");
            receiver.AudioFingerprint = fingerprint;

            uint seq = 1;
            void Inject(RemPacketType type, ushort stream, ReadOnlySpan<byte> payload)
            {
                var packet = new byte[RemPacket.HeaderSize + payload.Length];
                RemPacket.WriteHeader(packet, type, stream, seq++);
                payload.CopyTo(packet.AsSpan(RemPacket.HeaderSize));
                receiver.InjectExternalPacket(packet, packet.Length, peer);
            }
            void Announce(ushort stream, RenderRoute lane)
            {
                var format = new AudioFormatInfo(48000, 2, 16, 1, 4, 192000, (int)AudioTransportCodec.Opus, 120, lane);
                var payload = new byte[64];
                var n = RemPacket.WriteFormatPayload(payload, format);
                Inject(RemPacketType.Format, stream, payload.AsSpan(0, n));
            }
            // A real sender's two streams are its two capture lanes: ASIO (smooth) and WASAPI (10 ms lumps).
            Announce(Smooth, RenderRoute.AsioLane);
            Announce(Lumpy, RenderRoute.WasapiLane);
            Check(receiver.LiveSessionCountForTest == 2, $"a peer's ASIO and WASAPI streams must open two sessions ({receiver.LiveSessionCountForTest})");

            // The real path records arrivals: three audio packets on the smooth stream (they need not decode).
            for (var i = 0; i < 3; i++) Inject(RemPacketType.Audio, Smooth, RemSoundCrypto.Encrypt(receiver.AudioKey!, new byte[40]));
            var tuned = true;   // the output's auto-tune: per-stream buffers only while it is on
            var first = receiver.UpdatePerStreamNeeds(_ => 2, _ => tuned);
            var smoothReport = first.Single(r => r.Stream.Contains($"stream={Smooth}", StringComparison.Ordinal));
            Check(smoothReport.Packets == 3, $"a packet arriving on a stream must be counted on THAT stream's own timing ({smoothReport.Packets} counted)");

            var smoothArrivals = Require(receiver.ArrivalsForTest(peer, Smooth), "the smooth stream's arrival timing");
            var lumpyArrivals = Require(receiver.ArrivalsForTest(peer, Lumpy), "the lumpy stream's arrival timing");
            var smoothPlayout = receiver.GetOrCreateSessionForTest(peer, Smooth);
            var lumpyPlayout = receiver.GetOrCreateSessionForTest(peer, Lumpy);
            var routeTarget = receiver.TargetLatencyMsFor(smoothPlayout.Route);
            Check(receiver.TargetLatencyMsFor(lumpyPlayout.Route) == routeTarget, "both streams' outputs must start at the same target for this test to compare them");
            var ms = Stopwatch.Frequency / 1000.0;
            var clock = Stopwatch.GetTimestamp();
            void OneSecond()
            {
                // Smooth: a packet every 2.5 ms. Lumpy: every 10 ms with one missing, a 20 ms hole - WASAPI's lumps. Both
                // run evenly across the second's edge, so the only holes are the ones meant.
                for (var k = 0; k < 400; k++) smoothArrivals.RecordAt(clock + (long)(k * 2.5 * ms));
                for (var k = 0; k < 100; k++) if (k != 50) lumpyArrivals.RecordAt(clock + (long)(k * 10 * ms));
                clock += (long)(1000 * ms);
                receiver.UpdatePerStreamNeeds(_ => 2, _ => tuned);
            }

            // The first seconds: measured, not yet applied - a few seconds of evidence before going below the output.
            Check(receiver.PerStreamBuffers, "premise: per-stream buffers are on without being asked");
            OneSecond();   // with the first reading above, two seconds held
            Check(smoothPlayout.OwnNeedMs == 0 && lumpyPlayout.OwnNeedMs == 0,
                $"the first seconds must give no stream a buffer of its own yet (smooth {smoothPlayout.OwnNeedMs} ms, lumpy {lumpyPlayout.OwnNeedMs} ms)");
            Check(smoothPlayout.EffectiveTargetMs(routeTarget) == routeTarget, "and every stream plays at the output's target meanwhile");

            // Then down from the output's target, at most 1 ms a second.
            OneSecond();
            Check(smoothPlayout.OwnNeedMs == routeTarget - 1,
                $"the first second with evidence must take the smooth stream only 1 ms below the output's {routeTarget} ms (got {smoothPlayout.OwnNeedMs} ms): a buffer must never be dropped in one go");
            for (var s = 0; s < routeTarget + 5; s++) OneSecond();
            var smoothNeed = AudioReceiver.PerStreamNeedMs(3, 2, 3, lean: false);
            var lumpyNeed = AudioReceiver.PerStreamNeedMs(20, 2, 3, lean: false);
            Check(smoothPlayout.OwnNeedMs == Math.Min(smoothNeed, routeTarget),
                $"the smooth stream must settle at its own need, {smoothNeed} ms (got {smoothPlayout.OwnNeedMs} ms)");
            Check(lumpyPlayout.OwnNeedMs == Math.Min(lumpyNeed, routeTarget),
                $"the lumpy stream must settle at ITS own need, {lumpyNeed} ms, not the smooth one's (got {lumpyPlayout.OwnNeedMs} ms)");
            Check(smoothPlayout.OwnNeedMs < lumpyPlayout.OwnNeedMs,
                "the whole point: the smooth stream must end up with less buffer than the lumpy one on the same output");
            Check(smoothPlayout.EffectiveTargetMs(routeTarget) == smoothPlayout.OwnNeedMs, "and that is the depth it plays at");
            Check(receiver.PlayingTargetMsFor(smoothPlayout.Route) == Math.Max(smoothPlayout.OwnNeedMs, lumpyPlayout.OwnNeedMs),
                $"what the output plays at, for the Total latency box, must be its deepest stream's ({receiver.PlayingTargetMsFor(smoothPlayout.Route)} ms)");

            // The output's auto-tune off: the number set is what every stream gets, at once (Ed, 2026-09-29).
            tuned = false;
            OneSecond();
            Check(smoothPlayout.OwnNeedMs == 0 && lumpyPlayout.OwnNeedMs == 0 && smoothPlayout.EffectiveTargetMs(routeTarget) == routeTarget,
                $"with the output's auto-tune off every stream must play at the output's setting at once (smooth {smoothPlayout.OwnNeedMs} ms)");
            Check(receiver.PlayingTargetMsFor(smoothPlayout.Route) == routeTarget, "and the output plays at its setting");
            tuned = true;
            for (var s = 0; s < routeTarget + 5; s++) OneSecond();
            Check(smoothPlayout.OwnNeedMs == Math.Min(smoothNeed, routeTarget), $"auto-tune back on, the smooth stream must settle at {smoothNeed} ms again ({smoothPlayout.OwnNeedMs} ms)");

            // The lowest-delay tune's lean margins: lower again, still 1 ms a second.
            receiver.LeanTuning = true;
            var leanNeed = AudioReceiver.PerStreamNeedMs(3, 2, 3, lean: true);
            OneSecond();
            Check(smoothPlayout.OwnNeedMs == smoothNeed - 1, $"lean margins must take the smooth stream down 1 ms a second too (got {smoothPlayout.OwnNeedMs} ms)");
            for (var s = 0; s < 10; s++) OneSecond();
            Check(smoothPlayout.OwnNeedMs == leanNeed, $"and settle it at its lean need, {leanNeed} ms (got {smoothPlayout.OwnNeedMs} ms)");
            receiver.LeanTuning = false;
            OneSecond();
            Check(smoothPlayout.OwnNeedMs == smoothNeed, $"the usual margins back must raise it at once to {smoothNeed} ms (got {smoothPlayout.OwnNeedMs} ms)");

            // Off - only the simulator does this, to compare: straight back to the output's target.
            receiver.PerStreamBuffers = false;
            OneSecond();
            Check(smoothPlayout.OwnNeedMs == 0 && smoothPlayout.EffectiveTargetMs(routeTarget) == routeTarget,
                "turned off, every stream must go straight back on the output's target");
            return $"each stream's own packets are counted; after a few seconds of evidence the smooth stream came down 1 ms a second from "
                 + $"{routeTarget} ms to {smoothNeed} ms and the lumpy one stopped at {lumpyNeed} ms, the output playing at the deeper; with "
                 + $"the output's auto-tune off both went straight back to its setting; lean margins took the smooth one to "
                 + $"{leanNeed} ms; off (the simulator's comparison) put both back on the output";
        }
        finally
        {
            JamulusStyle.Reset();
        }
    }

    /// <summary>ASIO BUFFER SIZE: a size is fitted to what the driver allows, and NAudio still keeps the record we set it
    /// through. (Out of the latency lab into the profile, 2026-09-28; see SelfTest.AsioBufferSize.cs for the setting.)</summary>
    private static string AsioBufferSizeIsFittedToTheDriver()
    {
        Check(AsioBufferSizing.Fit(0, 32, 1024, 256, -1) == 256, "'the driver's own' must ask for the driver's preferred size");
        Check(AsioBufferSizing.Fit(64, 32, 1024, 256, -1) == 64, "a power of two inside the range is asked for as it is");
        Check(AsioBufferSizing.Fit(48, 32, 1024, 256, -1) == 64, "with granularity -1 only powers of two are allowed: 48 becomes 64");
        Check(AsioBufferSizing.Fit(16, 32, 1024, 256, -1) == 32, "below the driver's smallest becomes its smallest");
        Check(AsioBufferSizing.Fit(2048, 32, 1024, 256, -1) == 1024, "above the driver's largest becomes its largest");
        Check(AsioBufferSizing.Fit(100, 64, 2048, 256, 32) == 128, "a positive granularity steps up from the smallest: 64 + 2 x 32 = 128");
        Check(AsioBufferSizing.Fit(128, 48, 960, 480, 48) == 144, "48 + 2 x 48 = 144 is the first allowed size at or above 128");
        Check(AsioBufferSizing.Fit(64, 256, 256, 256, 0) == 256, "a driver with one fixed size (granularity 0) gets that size");

        var field = typeof(AsioOut).GetField("driver", BindingFlags.Instance | BindingFlags.NonPublic);
        Check(field is not null && field.FieldType == typeof(AsioDriverExt),
            "NAudio's AsioOut must still keep its AsioDriverExt in a private field called \"driver\" - the ASIO buffer size setting reaches the driver's size through it");
        Check(!typeof(AsioDriverCapability).IsValueType && typeof(AsioDriverCapability).GetField("BufferPreferredSize") is not null,
            "AsioDriverCapability must still be a class with a public BufferPreferredSize field, or setting it would change a copy and the driver would never be asked");
        return "sizes fitted by the ASIO rules (powers of two, steps from the smallest, one fixed size, the driver's range), "
             + "and NAudio still holds the capability record the size is set through";
    }

    /// <summary>JAMULUS-STYLE OPUS: our custom-mode Opus library loads, and 64- and 128-sample frames go through it and come
    /// back as the same tone, at a constant packet size, with concealment for a lost frame. (The app sends 64; a 128-sample
    /// stream from a copy that still had the lab's choice is still played.)</summary>
    private static string JamulusStyleOpusRoundTrips()
    {
        Check(OpusCustomNative.TryLoad(), $"remsound-opus.dll must load with custom modes: {OpusCustomNative.LoadMessage}");
        var said = new List<string>();
        foreach (var frames in new[] { 128, 64 })
        {
            using var encoder = new OpusCustomEncoder(frames);
            using var decoder = new OpusCustomDecoder(frames);
            var input = new float[frames * 2];
            var output = new float[frames * 2];
            var packet = new byte[1500];
            var sizes = new HashSet<int>();
            double inEnergy = 0, outEnergy = 0;
            var crossings = 0;
            var last = 0f;
            var total = 48000 / 2 / frames * frames; // half a second
            for (var start = 0; start < total; start += frames)
            {
                for (var i = 0; i < frames; i++)
                {
                    var v = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * (start + i) / 48000.0));
                    input[2 * i] = input[2 * i + 1] = v;
                }
                var len = encoder.Encode(input, packet);
                Check(len > 0, $"{frames}-sample encode failed ({len})");
                sizes.Add(len);
                var got = decoder.Decode(packet.AsSpan(0, len), output);
                Check(got == frames, $"{frames}-sample decode gave {got} samples");
                if (start < 48000 / 10) continue; // let the codec settle
                for (var i = 0; i < frames; i++)
                {
                    inEnergy += input[2 * i] * input[2 * i];
                    outEnergy += output[2 * i] * output[2 * i];
                    if ((last < 0) != (output[2 * i] < 0)) crossings++;
                    last = output[2 * i];
                }
            }
            // Silence too: with a variable bitrate an encoder shrinks silent frames to a few bytes, so only constant
            // bitrate keeps them the same size as the tone's.
            Array.Clear(input);
            for (var k = 0; k < 20; k++)
            {
                var len = encoder.Encode(input, packet);
                Check(len > 0, $"{frames}-sample encode of silence failed ({len})");
                sizes.Add(len);
            }
            var ratio = Math.Sqrt(outEnergy / inEnergy);
            var seconds = (total - 48000 / 10) / 48000.0;
            var hz = crossings / 2.0 / seconds;
            Check(ratio is > 0.7 and < 1.3, $"{frames}-sample frames must bring the tone back at about its own level (output/input {ratio:0.00})");
            Check(Math.Abs(hz - 440) < 20, $"{frames}-sample frames must bring back a 440 Hz tone (heard {hz:0} Hz)");
            Check(sizes.Count == 1 && sizes.Single() == encoder.BytesPerFrame,
                $"constant bitrate: every {frames}-sample packet must be {encoder.BytesPerFrame} bytes (saw {string.Join(", ", sizes)})");
            var made = decoder.Conceal(output);
            Check(made == frames && output.All(float.IsFinite), $"a lost {frames}-sample frame must be made up by concealment ({made} samples)");
            said.Add($"{frames} samples: level {ratio:0.00}, {hz:0} Hz, {encoder.BytesPerFrame}-byte packets");
        }
        return $"{OpusCustomNative.LoadMessage}; {string.Join("; ", said)}; a lost frame is concealed";
    }

    /// <summary>JAMULUS-STYLE SENDING: what the ASIO lane sends, and to whom. The 64-sample Jamulus-style stream goes, in
    /// place of Opus, ONLY to a RemSound that has said it plays through ASIO, and everyone else keeps the normal stream; a PCM
    /// choice stays PCM for everyone. This copy's own Format packets say whether IT plays through ASIO. And PCM as the app
    /// picks it - tight latency, always on - is one packet per callback, which is why the lab's own "ASIO as PCM per callback"
    /// switch went (Ed, 2026-09-28).</summary>
    private static string TheAsioLaneSendsEachStreamToTheRightPeople()
    {
        Check(OpusCustomNative.TryLoad(), $"remsound-opus.dll must load: {OpusCustomNative.LoadMessage}");
        try
        {
            JamulusStyle.Reset();
            CustomOpusPeers.Clear();
            using var asked = new WireSink();
            using var other = new WireSink();
            var askedAt = asked.Endpoint;                                            // 127.0.0.1: will ask for it
            var otherAt = new IPEndPoint(IPAddress.Parse("127.0.0.2"), other.Endpoint.Port); // never asks
            using var sender = new AudioSender();
            sender.SetReceivers([askedAt, otherAt]);
            sender.ConfigureCodec(AudioTransportCodec.Opus, 120);
            var (key, fingerprint) = RemSoundCrypto.ForPlainPassword("latency-lab-send");
            sender.AudioKey = Require(key, "a key");
            sender.AudioFingerprint = fingerprint;
            sender.UseAsioLaneForTest();
            var callback = new float[32 * 2];
            void Callbacks(int n) { for (var i = 0; i < n; i++) sender.FeedAsioLaneForTest(callback); }
            static List<(RemPacketType Type, ushort Stream, byte[] Packet)> LabCaught(WireSink sink)
            {
                Thread.Sleep(150);
                var list = new List<(RemPacketType, ushort, byte[])>();
                foreach (var p in sink.Snapshot())
                    if (RemPacket.TryReadHeader(p, out var type, out var stream, out _)) list.Add((type, stream, p));
                return list;
            }
            static AudioFormatInfo? LabFormatOf(byte[] packet) =>
                RemPacket.TryReadFormat(packet.AsSpan(RemPacket.HeaderSize), out var f, out _) ? f : null;

            // Nobody has said they play through ASIO, Opus 2.5 ms: 320 frames make 2 packets, and this copy, not playing
            // through ASIO itself, asks nobody for the frames.
            Callbacks(10);
            var offOther = LabCaught(other);
            Check(offOther.Count(p => p.Type == RemPacketType.Audio) == 2,
                $"with nobody asking, ten 32-frame callbacks of 2.5 ms Opus must make 2 packets ({offOther.Count(p => p.Type == RemPacketType.Audio)})");
            Check(offOther.Where(p => p.Type == RemPacketType.Format).All(p => LabFormatOf(p.Packet)?.LabFlags == 0),
                "a copy that does not play through ASIO must not say in its Format packets that it can take Jamulus-style frames");
            Check(!offOther.Any(p => LabFormatOf(p.Packet)?.Codec == (int)AudioTransportCodec.OpusCustom),
                "and with nobody asking, no Jamulus-style stream at all");
            Check(!sender.AsioLaneLabState.Custom && sender.AsioLaneLabState.CustomFrames == 0,
                "and the lane must not even make the frames for nobody (an encoder running for no one is the ASIO thread's time wasted)");

            // PCM as the app picks it: tight latency is always on (MainForm, the service), so each callback is one packet on
            // the ASIO lane - and on the WASAPI lane beside it, a 10 ms block cut only where it would not fit in one.
            sender.SetTightLatency(true);
            sender.ConfigureCodec(AudioTransportCodec.Pcm);
            sender.UseDefaultLaneForTest(RenderRoute.WasapiLane);
            var pcmFrom = other.Snapshot().Count();
            Callbacks(10);
            var pcmStream = sender.AsioLaneStreamIdForTest;
            var pcm = LabCaught(other).Skip(pcmFrom).ToList();
            Check(pcm.Count(p => p.Type == RemPacketType.Audio && p.Stream == pcmStream) == 10,
                $"PCM must go out one packet per ASIO callback: 10 callbacks, {pcm.Count(p => p.Type == RemPacketType.Audio && p.Stream == pcmStream)} packets");
            Check(pcm.Where(p => p.Type == RemPacketType.Format && p.Stream == pcmStream).Select(p => LabFormatOf(p.Packet)).Any(f => f?.Codec == (int)AudioTransportCodec.Pcm),
                "and announce itself as PCM");
            var wasapiFrom = other.Snapshot().Count();
            sender.FeedDefaultLaneForTest(new float[160 * 2]);
            sender.FeedDefaultLaneForTest(new float[160 * 2]);
            var wasapiStream = sender.DefaultLaneStreamIdForTest;
            var wasapiPcm = LabCaught(other).Skip(wasapiFrom).Count(p => p.Type == RemPacketType.Audio && p.Stream == wasapiStream);
            Check(wasapiPcm == 2, $"and one packet per callback on the WASAPI lane too: 2 blocks of 160 frames, {wasapiPcm} packets");

            // PCM chosen and 127.0.0.1 playing through ASIO: the Jamulus-style frames take the place of Opus, not of PCM.
            CustomOpusPeers.Note(askedAt.Address);
            var pcmAskedFrom = asked.Snapshot().Count();
            Callbacks(10);
            var pcmAsked = LabCaught(asked).Skip(pcmAskedFrom).ToList();
            Check(pcmAsked.Count(p => p.Type == RemPacketType.Audio && p.Stream == sender.AsioLaneStreamIdForTest) == 10
                  && !pcmAsked.Any(p => LabFormatOf(p.Packet)?.Codec == (int)AudioTransportCodec.OpusCustom),
                "with PCM chosen, a peer that plays through ASIO must still get PCM, a packet a callback, and no Jamulus-style stream");
            sender.ConfigureCodec(AudioTransportCodec.Opus, 120);

            // Jamulus-style, 64 samples, with only 127.0.0.1 playing through ASIO - and this copy playing through ASIO too, so
            // its Format packets say so.
            JamulusStyle.PlaysThroughAsio = true;
            CustomOpusPeers.Note(askedAt.Address);
            var askedFrom = asked.Snapshot().Count();
            var otherFrom = other.Snapshot().Count();
            Callbacks(20);
            var mainStream = sender.AsioLaneStreamIdForTest; // after: the codec change gave the normal stream a new id
            var toAsked = LabCaught(asked).Skip(askedFrom).ToList();
            var toOther = LabCaught(other).Skip(otherFrom).ToList();
            var customFormats = toAsked.Where(p => p.Type == RemPacketType.Format).Select(p => (p.Stream, F: LabFormatOf(p.Packet)))
                .Where(x => x.F?.Codec == (int)AudioTransportCodec.OpusCustom).ToList();
            Check(customFormats.Count > 0 && customFormats.All(x => x.F!.FrameSamplesPerChannel == 64 && (x.F.LabFlags & AudioFormatInfo.LabFlagCanDecodeCustomOpus) != 0),
                "the peer that asked must be told it is getting a 64-sample Jamulus-style stream");
            var customStream = customFormats[0].Stream;
            var customAudio = toAsked.Where(p => p.Type == RemPacketType.Audio && p.Stream == customStream).ToList();
            Check(customAudio.Count == 10, $"640 frames at 64 samples a frame must be 10 Jamulus-style packets to the peer that asked ({customAudio.Count})");
            Check(!toAsked.Any(p => p.Type == RemPacketType.Audio && p.Stream == mainStream),
                "the peer that asked must NOT also get the normal ASIO stream - it would hear everything twice");
            Check(!toOther.Any(p => p.Stream == customStream) && !toOther.Any(p => p.Type == RemPacketType.Format && LabFormatOf(p.Packet)?.Codec == (int)AudioTransportCodec.OpusCustom),
                "a peer that has NOT asked must never be sent a Jamulus-style packet or format - an iPhone would play it as noise");
            Check(toOther.Count(p => p.Type == RemPacketType.Audio && p.Stream == mainStream) == 5,
                $"and it keeps getting the normal stream: 640 frames of 2.5 ms Opus is 5 packets ({toOther.Count(p => p.Type == RemPacketType.Audio && p.Stream == mainStream)})");
            Check(toOther.Where(p => p.Type == RemPacketType.Format).All(p => (LabFormatOf(p.Packet)?.LabFlags & AudioFormatInfo.LabFlagCanDecodeCustomOpus) != 0),
                "while this copy plays through ASIO, every Format packet must say it can take the Jamulus-style stream, or the far copy never learns it");
            Check(RemSoundCrypto.TryDecrypt(key!, customAudio[0].Packet.AsSpan(RemPacket.HeaderSize), out var plain), "a Jamulus-style packet must be encrypted with the profile's key");
            using (var decoder = new OpusCustomDecoder(64))
                Check(decoder.Decode(plain, new float[128]) == 64, "and decode to 64 samples a channel");

            // The far copy stops playing through ASIO: it stops saying so, is forgotten (CustomOpusPeers, a few seconds), and is
            // back on the normal stream.
            CustomOpusPeers.Clear();
            var backFrom = asked.Snapshot().Count();
            Callbacks(20);
            var back = LabCaught(asked).Skip(backFrom).ToList();
            Check(back.Any(p => p.Type == RemPacketType.Audio && p.Stream == mainStream) && !back.Any(p => p.Stream == customStream),
                "a peer no longer saying it plays through ASIO must be put straight back on the normal stream");
            return "nobody asking: 2 Opus packets per 10 callbacks and no flag; PCM as picked: a packet per callback on both lanes, "
                 + "and PCM still to a peer that plays through ASIO; Opus: 10 packets of 64 samples to the one peer that plays through "
                 + "ASIO and none of the normal stream, the other peer 5 normal packets and nothing Jamulus-style, every Format "
                 + "flagged while this copy plays through ASIO; encrypted and decodable; the peer back on the normal stream once it stops asking";
        }
        finally
        {
            JamulusStyle.Reset();
            CustomOpusPeers.Clear();
        }
    }

    /// <summary>SERVER PATH: audio arriving through a server is received at real-time priority - MMCSS "Capture", as the
    /// direct receive thread has always been - from the very first packet and for as long as the thread runs, and its
    /// gaps are timed. It was the latency lab's "server receive boost" switch (2026-09-26) until Ed made it normal
    /// behaviour (2026-09-28).
    ///
    /// <para>What the receive thread actually runs at is read ON that thread, for each packet, through the sender's own
    /// seam: its priority as Windows reports it, the priority the scheduler is running it at, and whether MMCSS took it
    /// on. Until 2026-09-28 this read the sender's "boosted" flag, which says what was asked for and not what the thread
    /// got - a boost made on the wrong thread, or one never taken off, left the flag right and the thread wrong. And it
    /// waits for each packet to be seen rather than sleeping.</para></summary>
    private static string TheServerPathIsAlwaysBoosted()
    {
        var restoreGate = DiagnosticsGate.Enabled;
        try
        {
            DiagnosticsGate.Enabled = true;
            var said = new List<string>();
            using var sender = new AudioSender();
            sender.Diagnostic = line => { lock (said) said.Add(line); };
            var seen = new List<ReceiveThreadReading>();
            sender.InboundThreadSeenForTest = boost =>
            {
                var reading = ReceiveThreadReading.Now(boost);
                lock (seen) seen.Add(reading);
            };
            sender.StartReceiving();
            using var probe = new System.Net.Sockets.UdpClient(0, System.Net.Sockets.AddressFamily.InterNetwork);
            var to = new IPEndPoint(IPAddress.Loopback, sender.LocalPortForTest);
            var ping = new byte[RemPacket.HeaderSize + 9];
            // One packet, and what the receive thread said about itself when it took it.
            ReceiveThreadReading Next()
            {
                int before;
                lock (seen) before = seen.Count;
                probe.Send(ping, ping.Length, to);
                Check(WaitUntil(() => { lock (seen) return seen.Count > before; }), "a packet sent to the server path must reach its receive thread");
                lock (seen) return seen[before];
            }

            var first = Next();
            Check(first.Thread == "RemSound.SenderReceive", $"the reading must come from the server path's own receive thread (it came from \"{first.Thread}\")");
            Check(first.Mode?.StartsWith("MMCSS Capture", StringComparison.Ordinal) == true,
                $"from the very first packet the receive thread must be taken on by MMCSS as a Capture task, with no switch to ask ({first})");
            Check(first.Priority >= 2 && first.Dynamic >= 16,
                $"and running at real-time priority on that thread, not merely asked for: Highest or above, and in the real-time band "
                + $"the scheduler keeps for MMCSS ({first})");
            string[] lines;
            lock (said) lines = said.ToArray();
            Check(lines.Any(l => l.Contains("server receive thread at real-time priority", StringComparison.Ordinal)),
                $"and the log must say so, once, when the thread starts (said: {string.Join(" | ", lines)})");

            // The gaps: timed as the thread itself saw them, not merely more than nothing. The second packet is held back 8 ms
            // so there is a gap to time; a third makes sure the thread has finished with the second (one thread, one packet
            // at a time), whose gap is recorded just after the reading. The worst gap since the sender started must then be
            // at least that one, and no longer than the longest the thread saw.
            var a = Next();
            WaitUntil(() => Stopwatch.GetElapsedTime(a.At) >= TimeSpan.FromMilliseconds(8), 100);
            var b = Next();
            var c = Next();
            double Gap(ReceiveThreadReading x, ReceiveThreadReading y) => Stopwatch.GetElapsedTime(x.At, y.At).TotalMilliseconds;
            var heldBack = Gap(a, b);
            var longest = new[] { Gap(first, a), heldBack, Gap(b, c) }.Max();
            var gapTimed = sender.TakeInboundMaxGapMs();
            Check(heldBack >= 8 && gapTimed >= Math.Floor(heldBack) - 1 && gapTimed <= longest + 1,
                $"the server path's gaps between packets must be timed as they happened, so the lab line can show them: the receive "
                + $"thread saw {heldBack:0.0} ms (the longest {longest:0.0}), and the lab was told {gapTimed} ms");
            Check(new[] { a, b, c }.All(r => r.Mode?.StartsWith("MMCSS Capture", StringComparison.Ordinal) == true && r.Priority >= 2 && r.Dynamic >= 16),
                $"and it must stay at real-time priority packet after packet ({a}; {b}; {c})");
            return $"from the first packet: {first}; gaps timed as seen ({gapTimed} ms for {heldBack:0.0}); still boosted three packets on";
        }
        finally
        {
            DiagnosticsGate.Enabled = restoreGate;
        }
    }

    /// <summary>What a thread says about itself, read on it: its name, its priority as Windows reports it
    /// (GetThreadPriority: 0 normal, 2 highest, 15 time-critical), the priority the scheduler is running it at now and its
    /// base (16 and up is the real-time band MMCSS lifts a thread into), the MMCSS boost in force if any, and when.</summary>
    private sealed record ReceiveThreadReading(string? Thread, int Priority, int Dynamic, int Base, string? Mode, long At)
    {
        public static ReceiveThreadReading Now(WindowsAudioThreadBoost boost)
        {
            var info = default(ThreadBasicInformation);
            var status = NtQueryInformationThread(GetCurrentThread(), 0, ref info, System.Runtime.InteropServices.Marshal.SizeOf<ThreadBasicInformation>(), out _);
            return new(System.Threading.Thread.CurrentThread.Name, GetThreadPriority(GetCurrentThread()),
                status == 0 ? info.Priority : -1, status == 0 ? info.BasePriority : -1, boost.Mode, Stopwatch.GetTimestamp());
        }

        public override string ToString() => $"priority {Priority}, running at {Dynamic} (base {Base}), {Mode ?? "no MMCSS"}";
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ThreadBasicInformation
    {
        public int ExitStatus;
        public IntPtr TebBaseAddress;
        public IntPtr UniqueProcess;
        public IntPtr UniqueThread;
        public IntPtr AffinityMask;
        public int Priority;
        public int BasePriority;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern int GetThreadPriority(IntPtr thread);
    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern int NtQueryInformationThread(IntPtr thread, int infoClass, ref ThreadBasicInformation info, int size, out int returned);

    /// <summary>JAMULUS-STYLE HEARTBEAT: a copy that only LISTENS, through ASIO, still tells the sender it can take
    /// Jamulus-style frames - the flag rides in every ping and pong (Ed, live test 2026-09-26: "why am I sending from the
    /// laptop?"). A copy that does not play through ASIO sends a heartbeat byte for byte what it always was, and a reader that
    /// knows nothing of the flag still reads the longer one.</summary>
    private static string TheHeartbeatCarriesTheJamulusFlag()
    {
        Check(OpusCustomNative.TryLoad(), $"remsound-opus.dll must load: {OpusCustomNative.LoadMessage}");
        try
        {
            JamulusStyle.Reset();
            CustomOpusPeers.Clear();
            var buffer = new byte[RemPacket.HeartbeatPayloadWithFlagsSize];
            Check(RemPacket.WriteHeartbeatPayload(buffer, HeartbeatKind.Ping, 1234) == RemPacket.HeartbeatPayloadSize,
                "with no flag a heartbeat must stay the 9 bytes every port has always sent");
            Check(RemPacket.WriteHeartbeatPayload(buffer, HeartbeatKind.Pong, 1234, AudioFormatInfo.LabFlagCanDecodeCustomOpus) == RemPacket.HeartbeatPayloadWithFlagsSize,
                "with the flag it is one byte longer");
            Check(RemPacket.TryReadHeartbeat(buffer, out var kind, out var tick) && kind == HeartbeatKind.Pong && tick == 1234,
                "a reader that knows nothing of the flag must still read the longer heartbeat, as the phones' readers do (a minimum of 9)");
            Check(RemPacket.TryReadHeartbeat(buffer, out _, out _, out var flags) && flags == AudioFormatInfo.LabFlagCanDecodeCustomOpus,
                "and a reader that knows it must find the flag");

            using var heartbeat = new HeartbeatService(null);
            byte[] Ping(byte labFlags)
            {
                var packet = new byte[RemPacket.HeaderSize + RemPacket.HeartbeatPayloadWithFlagsSize];
                RemPacket.WriteHeader(packet, RemPacketType.Heartbeat, 0xFFFF, 1);
                var n = RemPacket.WriteHeartbeatPayload(packet.AsSpan(RemPacket.HeaderSize), HeartbeatKind.Ping, 99, labFlags);
                return packet[..(RemPacket.HeaderSize + n)];
            }
            var quiet = new IPEndPoint(IPAddress.Parse("10.66.0.5"), 47830);
            var listener = new IPEndPoint(IPAddress.Parse("10.66.0.6"), 47830);
            heartbeat.HandlePacketForTest(Ping(0), quiet);
            Check(!CustomOpusPeers.CanDecode(quiet.Address), "a ping without the flag must not count as asking for Jamulus-style frames");
            heartbeat.HandlePacketForTest(Ping(AudioFormatInfo.LabFlagCanDecodeCustomOpus), listener);
            Check(CustomOpusPeers.CanDecode(listener.Address),
                "a ping WITH the flag must count - that is how a copy that only listens asks for the new frames");

            // And this copy's own pong carries it while it plays through ASIO, and does not otherwise.
            byte[]? sent = null;
            heartbeat.SendTransport = (bytes, length, _) => { sent = bytes[..length]; return true; };
            heartbeat.HandlePacketForTest(Ping(0), quiet);
            Check(sent is { Length: RemPacket.HeaderSize + RemPacket.HeartbeatPayloadSize }, $"not playing through ASIO, our pong must be the usual 9-byte heartbeat ({sent?.Length} bytes)");
            JamulusStyle.PlaysThroughAsio = true;
            heartbeat.HandlePacketForTest(Ping(0), quiet);
            Check(sent is not null && RemPacket.TryReadHeartbeat(sent.AsSpan(RemPacket.HeaderSize), out _, out _, out var ours) && ours == AudioFormatInfo.LabFlagCanDecodeCustomOpus,
                "playing through ASIO, our pong must say we can take the new frames");
            // And our own pings, which a copy that only listens sends without being asked.
            heartbeat.SetPingOnlyTargets([quiet]);
            sent = null;
            heartbeat.SendPingsForTest();
            Check(sent is not null && RemPacket.TryReadHeartbeat(sent.AsSpan(RemPacket.HeaderSize), out var pingKind, out _, out var pingFlags)
                  && pingKind == HeartbeatKind.Ping && pingFlags == AudioFormatInfo.LabFlagCanDecodeCustomOpus,
                "playing through ASIO, our pings must carry the flag too");
            JamulusStyle.PlaysThroughAsio = false;
            sent = null;
            heartbeat.SendPingsForTest();
            Check(sent is { Length: RemPacket.HeaderSize + RemPacket.HeartbeatPayloadSize },
                $"and once it stops playing through ASIO they must be the usual 9-byte heartbeat ({sent?.Length} bytes)");
            return "the flag rides in pings and pongs only while this copy plays through ASIO; the longer heartbeat still reads as "
                 + "before; a listening copy's flag is heard";
        }
        finally
        {
            JamulusStyle.Reset();
            CustomOpusPeers.Clear();
        }
    }

    /// <summary>LATENCY LAB DROPOUT TUNER: the "keep dropouts rare" rules, second by second - until it meets its first gap,
    /// half the way to the floor every two quiet seconds (Ed, 2026-09-28: 40 minutes from 500 ms was absurd); from then on
    /// down 1 ms after 5 quiet seconds, up 2 at once for three gaps in a second or four in twenty, fewer gaps only hold, and
    /// after going up it waits thirty seconds before coming down again. Written against the constants, so changing a rule
    /// means changing this.</summary>
    private static string TheDropoutTunerKeepsItsRules()
    {
        var quiet = DropoutTuner.QuietSecondsToLower;
        var wait = DropoutTuner.QuietSecondsToLowerAfterRaise;
        var burst = DropoutTuner.RaiseWhenGapsInOneSecond;
        var many = DropoutTuner.RaiseWhenGapsInWindow;
        var step = DropoutTuner.RaiseStepMs;
        Check(quiet == 5 && wait == 30 && burst == 3 && many == 4 && step == 2 && DropoutTuner.QuietSecondsToStepBig == 2,
            "the tuner's rules have changed - say so here, in the manual and in the plan, then update this check");
        // Far above, before any gap: half the way to the floor every two quiet seconds.
        var big = new DropoutTuner();
        var far = big.Tick(0, 500, 1, 200);
        Check(far == 500, $"one quiet second must not yet move it ({far} ms)");
        far = big.Tick(0, far, 1, 200);
        Check(far == 251, $"two quiet seconds with no gap yet must take it half the way to the floor, 500 to 251 ({far} ms)");
        far = big.Tick(0, big.Tick(0, far, 1, 200), 1, 200);
        Check(far == 126, $"and two more, half again ({far} ms)");
        far = big.Tick(1, far, 1, 200);
        Check(far == 126, $"its first gap must hold ({far} ms)");
        for (var i = 0; i < quiet - 1; i++) far = big.Tick(0, far, 1, 200);
        Check(far == 126, $"and from then on it feels for the edge: {quiet - 1} quiet seconds must not yet lower it ({far} ms)");
        far = big.Tick(0, far, 1, 200);
        Check(far == 125, $"the {quiet}th must lower it 1 ms ({far} ms)");
        // From here, the edge-feeling rules, with the first gap met.
        var t = new DropoutTuner();
        var ms = t.Tick(1, 20, 1, 200);
        Check(ms == 20, $"premise: a first gap holds ({ms} ms)");
        for (var i = 0; i < quiet - 1; i++) ms = t.Tick(0, ms, 1, 200);
        Check(ms == 20, $"{quiet - 1} quiet seconds must not yet lower the buffer ({ms} ms)");
        ms = t.Tick(0, ms, 1, 200);
        Check(ms == 19, $"the {quiet}th quiet second must lower it 1 ms ({ms} ms)");
        ms = t.Tick(1, ms, 1, 200);
        Check(ms == 19, $"one gap must hold, not raise ({ms} ms)");
        for (var i = 0; i < quiet - 1; i++) ms = t.Tick(0, ms, 1, 200);
        Check(ms == 19, $"a gap restarts the quiet count ({ms} ms)");
        ms = t.Tick(burst, ms, 1, 200);
        Check(ms == 19 + step, $"{burst} gaps in one second must raise it {step} ms at once ({ms} ms)");
        var raised = ms;
        for (var i = 0; i < wait - 1; i++) ms = t.Tick(0, ms, 1, 200);
        Check(ms == raised, $"after a raise it must wait {wait} s before coming down ({ms} ms after {wait - 1} s)");
        ms = t.Tick(0, ms, 1, 200);
        Check(ms == raised - 1, $"and then come down 1 ms ({ms} ms)");
        var held = ms;
        for (var i = 0; i < many - 1; i++) ms = t.Tick(1, ms, 1, 200);
        Check(ms == held, $"{many - 1} scattered gaps must still only hold ({ms} ms)");
        ms = t.Tick(1, ms, 1, 200);
        Check(ms == held + step, $"the {many}th gap within twenty seconds must raise it ({ms} ms)");
        var afterFirst = ms;
        ms = t.Tick(burst, ms, 1, 200);
        Check(ms == afterFirst + 2 * step, $"still in trouble straight after a raise, the next raise must be twice as big ({ms - afterFirst} ms)");
        var afterSecond = ms;
        ms = t.Tick(burst, ms, 1, 200);
        Check(ms == afterSecond + 4 * step, $"and twice again ({ms - afterSecond} ms)");
        var floor = new DropoutTuner();
        var low = 60;
        for (var i = 0; i < 100; i++) low = floor.Tick(0, low, 2, 200);
        Check(low == 2, $"it must never go below its floor ({low} ms)");
        Check(new DropoutTuner().Tick(burst, 199, 1, 200) == 200, "and never above its ceiling");
        return $"before any gap, half the way down every 2 quiet s (500 -> 251 -> 126); then {quiet} quiet s -> down 1; fewer gaps hold "
             + $"and restart the count; {burst} in a second or {many} in 20 s -> up {step}; {wait} s wait after a raise; floor and ceiling kept";
    }

    /// <summary>The switches that left the lab, by the names an old global config holds them: five in Ed's review of 2026-09-28,
    /// three after the live test of 2026-09-29 (per-stream buffers made normal; lean margins and rare-dropout tuning made the
    /// profile's lowest-delay tick box).</summary>
    private static readonly string[] RetiredLabConfigKeys =
        ["LabAcceptRareDropouts", "LabBoostServerReceiveThread", "LabKeepFastTimerWhenHidden", "LabAsioPcmPerCallback", "LabAsioBufferFrames",
         "LabPerStreamBuffers", "LabLeanAsioMargins", "LabRareDropoutTuning", "LabAsioCustomOpusFrames"];

    /// <summary>LATENCY LAB RETIRED SWITCHES: a global config written while the lab had nine switches still opens with
    /// every other setting as saved - a config that fails to read falls back to defaults without a word - and nothing
    /// reads the retired ones: the lab is gone (the last switch, Jamulus-style frames, automatic since 2026-10-01), and the
    /// ASIO buffer size is read only so the start can say it no longer applies. Saving again drops them all.</summary>
    private static string LatencyLabRetiredSwitchesStillLoad()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-lab-retired-" + Guid.NewGuid().ToString("N"));
        using var userData = AppConfig.UseThrowawayUserDataDirectory(scratch);
        Directory.CreateDirectory(AppConfig.UserDataDirectory);
        var path = Path.Combine(AppConfig.UserDataDirectory, "global config.json");
        File.WriteAllText(path, """
{
  "LoggingEnabled": true,
  "LabPerStreamBuffers": true,
  "LabLeanAsioMargins": false,
  "LabAcceptRareDropouts": true,
  "LabBoostServerReceiveThread": true,
  "LabKeepFastTimerWhenHidden": true,
  "LabAsioBufferFrames": 64,
  "LabAsioPcmPerCallback": true,
  "LabAsioCustomOpusFrames": 128,
  "LabRareDropoutTuning": true
}
""");
        try
        {
            var loaded = AppConfig.Load();
            Check(loaded.LoggingEnabled, "a config naming the retired switches must still open, every other setting as it was saved");
            Check(loaded.LabAsioBufferFrames == 64, "the old ASIO buffer size must still be read, so the start can say it no longer applies");
            Check(Type.GetType("RemSound.Core.LatencyLab, RemSound.Core") is null, "the lab itself must be gone: nothing may switch anything from it");
            foreach (var gone in RetiredLabConfigKeys.Where(k => k != "LabAsioBufferFrames"))
                Check(typeof(AppConfig).GetProperty(gone) is null, $"{gone} left the lab: nothing may read it any more");
            loaded.LabAsioBufferFrames = 0;   // as the start does, once it has said so
            loaded.Save();
            var written = File.ReadAllText(path);
            foreach (var gone in RetiredLabConfigKeys)
                Check(!written.Contains($"\"{gone}\"", StringComparison.Ordinal), $"saving again must drop {gone}");
            return "an old nine-switch config opens with its other settings; the lab is gone, eight retired names are read by "
                 + "nothing, the ASIO buffer size only to be reported; saving again drops all nine";
        }
        finally { JamulusStyle.Reset(); }
    }

    /// <summary>What the lab's tab had: removed, made normal behaviour or moved to the profile (Ed's review, 2026-09-28, and
    /// the live test, 2026-09-29), and the last of them made automatic (2026-10-01). The start of each one's name, so no
    /// rewording can bring one back unseen.</summary>
    private static readonly string[] LatencyLabTabRetired =
    {
        "On the ASIO output, accept a rare dropout", "Give audio arriving through a server", "Keep the fast Windows timer",
        "ASIO buffer size", "Send the ASIO channels as PCM", "Size each incoming stream's buffer", "Leaner auto-tune margins",
        "Auto-tune the ASIO buffer to keep dropouts rare", "Switch every latency lab setting off", "Jamulus-style Opus frames",
    };

    /// <summary>JAMULUS-STYLE AUTOMATIC (Ed, 2026-10-01: "latency lab tab is still there with opus switch but that is supposed
    /// to be automatic now. so that needs coming out"). Preferences, opened from the real Options menu, has no Latency lab tab
    /// and none of its switches. The main window loads the library at start-up and says so; and its once-a-second tick says
    /// this computer plays through ASIO - what its heartbeats and Format packets tell the others - exactly when it is
    /// receiving with an ASIO output ticked: in all three configurations, receiving and not, each change logged with the
    /// configuration.</summary>
    private static string? JamulusStyleIsAutomatic()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        MainForm? form = null;
        RemoteControlServer? server = null;
        var pipe = "RemSound.control.selftest.jamulus." + Guid.NewGuid().ToString("N")[..8];
        var logged = new List<string>();
        try
        {
            JamulusStyle.Reset();
            Check(OpusCustomNative.TryLoad(), $"premise: remsound-opus.dll must load ({OpusCustomNative.LoadMessage})");
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            var main = form;
            Check(HeadlessRecords.Log.Last(500, "Jamulus-style Opus: library").Count > 0,
                "the window must say at start-up whether the Jamulus-style library loaded");
            main.LogForTest.EventTapForTest = line => { lock (logged) logged.Add(line); };

            // Preferences: no lab.
            server = new RemoteControlServer(pipe, () => main, new RemoteControlEngine(() => main));
            server.Start();
            string Ask(string command)
            {
                var reply = RemoteControl.Send(command, pipe, out var reached, connectTimeoutMs: 5000);
                Check(reached, $"the control channel did not answer \"{command}\": {reply}");
                return reply;
            }
            DriveWhilePumping(main, () =>
            {
                Check(Ask("menu Options > Preferences").Contains("\"Preferences\"", StringComparison.Ordinal), "Options, Preferences must open Preferences");
                Check(Ask("tab Logging").StartsWith("ok", StringComparison.Ordinal), "premise: Preferences' tabs must be reachable by name");
                Check(Ask("tab Latency lab").StartsWith("error", StringComparison.Ordinal), "Preferences must have no Latency lab tab any more");
                var controls = Ask("list Preferences");
                Check(controls.Contains("Play keyboard clicks", StringComparison.Ordinal), $"premise: Preferences' controls must be listed ({Head(controls)})");
                foreach (var gone in LatencyLabTabRetired.Where(g => g != "ASIO buffer size"))
                    Check(!controls.Contains(gone, StringComparison.OrdinalIgnoreCase), $"\"{gone}...\" left with the lab and must be nowhere in Preferences");
                Check(Ask("answer Close").StartsWith("ok", StringComparison.Ordinal), "Preferences closes with its own button");
            }, TimeSpan.FromSeconds(60));

            // The rule, through the window's own tick, in every configuration.
            var said = new List<string>();
            foreach (var configuration in AudioConfigurations.All)
            {
                SetAudioConfigurationForTest(main, configuration);
                foreach (var receiving in new[] { false, true, false })
                {
                    main.ReceiverRunningForTest = () => receiving;
                    lock (logged) logged.Clear();
                    var before = JamulusStyle.PlaysThroughAsio;
                    main.SnapshotTickForTest();
                    var want = receiving && configuration.UsesAsio();
                    Check(JamulusStyle.PlaysThroughAsio == want,
                        $"{configuration.Describe()}, {(receiving ? "receiving" : "not receiving")}: this computer must {(want ? "" : "NOT ")}say it plays through ASIO");
                    Check(JamulusStyle.CanTakeIt == want, "and, with the library loaded, ask for the frames exactly then");
                    string[] lines;
                    lock (logged) lines = logged.Where(l => l.StartsWith("Jamulus-style Opus:", StringComparison.Ordinal)).ToArray();
                    if (want != before)
                        Check(lines.Length == 1 && lines[0].Contains($"[{configuration.Describe()}]", StringComparison.Ordinal),
                            $"a change must be logged once, with the configuration (logged: {string.Join(" | ", lines)})");
                    else
                        Check(lines.Length == 0, $"no change must log nothing (logged: {string.Join(" | ", lines)})");
                    if (want) said.Add(configuration.Describe());
                }
            }
            return $"Preferences has no Latency lab tab and none of its switches; the library loads at start-up and is logged; this "
                 + $"computer asks for Jamulus-style frames only while receiving through ASIO ({string.Join(", ", said)}), not on WASAPI "
                 + "only or when not receiving, each change logged once with the configuration";
        }
        finally
        {
            if (form is not null) { form.LogForTest.EventTapForTest = null; form.ReceiverRunningForTest = null; }
            server?.Dispose();
            try { form?.Dispose(); } catch { /* teardown */ }
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
            JamulusStyle.Reset();
        }
    }
}
