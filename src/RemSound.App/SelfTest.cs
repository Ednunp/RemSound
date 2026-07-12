using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App;

/// <summary>
/// The in-app self-test, run by <c>--selftest</c>. Modelled on Andre's Sensor Readout: a list of
/// named steps, each timed and reported PASS / FAIL / SKIP, a one-line summary, and an exit code
/// (0 = every step passed or skipped, 1 = at least one failed) so a build-and-publish script can
/// gate on it.
///
/// The steps run INSIDE a real RemSound process on purpose — that's the only way to exercise the
/// genuine audio path, encryption, wire format and config/profile code rather than a stand-in.
/// Everything here is read-only or temp-folder-scoped: a self-test never touches the user's real
/// settings, profiles or logs, and never makes a sound.
/// </summary>
internal static class SelfTest
{
    private sealed class Result
    {
        public string Name = "";
        public string Status = "";   // PASS | FAIL | SKIP
        public string Message = "";
        public long Ms;
    }

    /// <summary>A step asserts with <see cref="Check"/> (failure) or bails with <see cref="Skip"/>
    /// (not applicable on this machine, e.g. no audio device). Both are signalled by exception so a
    /// step body reads as straight-line code.</summary>
    private sealed class CheckFailed : Exception { public CheckFailed(string m) : base(m) { } }
    private sealed class StepSkipped : Exception { public StepSkipped(string m) : base(m) { } }

    private static void Check(bool condition, string failMessage)
    {
        if (!condition) throw new CheckFailed(failMessage);
    }

    private static string Skip(string why) => throw new StepSkipped(why);

    public static int Run(string[] args)
    {
        var seconds = int.TryParse(ValueAfter(args, "--seconds"), out var s) && s is > 0 and <= 30 ? s : 3;

        Console.WriteLine($"RemSound self-test {CommandLine.AppVersion}  ({DateTime.Now:yyyy-MM-dd HH:mm:ss})");
        Console.WriteLine();

        var results = new List<Result>();
        RunStep(results, "Audio round-trip (PCM)", () => AudioRoundTrip(opus: false, seconds));
        RunStep(results, "Audio round-trip (Opus)", () => AudioRoundTrip(opus: true, seconds));
        RunStep(results, "Encryption round-trip", Encryption);
        RunStep(results, "Packet framing and rejection", PacketFraming);
        RunStep(results, "Server wire-format compatibility", ServerWireCompat);
        RunStep(results, "App settings save and reload", SettingsRoundTrip);
        RunStep(results, "Per-peer shaping DSP", PeerShapingDsp);
        RunStep(results, "Multi-output fan-out (both lanes)", FanOutToBothOutputs);
        RunStep(results, "Per-application send enumeration", AppSendEnumeration);
        RunStep(results, "Per-application capture lifecycle", AppSendCaptureLifecycle);
        RunStep(results, "Lifecycle churn (modes, sources, pan/EQ, send/receive)", LifecycleChurn);
        RunStep(results, "Service app-yield token", ServiceInteractivePresence);
        RunStep(results, "Service sender parity (crypto + Opus frame)", ServiceSenderParity);
        RunStep(results, "Service send host (headless stream + yield)", ServiceSendHostStream);
        RunStep(results, "Service registration args", ServiceRegistrationArgs);
        RunStep(results, "Service real lifecycle (install/start/stop/uninstall)", ServiceRealLifecycle);
        RunStep(results, "Recording engine (all formats + source gate + mono)", RecordingEngine);
        RunStep(results, "Recording split tracks (per-peer + own)", RecordingSplitTracks);
        RunStep(results, "Recording churn / soak", RecordingChurn);
        RunStep(results, "v5 settings and shaping round-trip", V5ConfigRoundTrip);
        RunStep(results, "Profile save and reload", ProfileRoundTrip);
        RunStep(results, "What's-new update marker", WhatsNewMarkerRoundTrip);
        RunStep(results, "Diagnostics report privacy", DiagnosticsPrivacy);
        RunStep(results, "Bundled resources present", ResourcesPresent);
        RunStep(results, "Dialog accessibility (names + mnemonics)", AccessibilityAudit);
        RunStep(results, "Main window coverage (all tabs + controls)", MainWindowCoverage);
        RunStep(results, "Main window profile round-trip (controls load + save)", MainWindowProfileRoundTrip);

        var failed = results.Count(r => r.Status == "FAIL");
        var skipped = results.Count(r => r.Status == "SKIP");
        var passed = results.Count(r => r.Status == "PASS");

        Console.WriteLine();
        if (failed == 0)
        {
            Console.WriteLine($"RESULT: PASS - {passed} passed{(skipped > 0 ? $", {skipped} skipped" : "")} of {results.Count}.");
            return 0;
        }
        var names = string.Join(", ", results.Where(r => r.Status == "FAIL").Select(r => r.Name));
        Console.WriteLine($"RESULT: FAIL - {failed} failed, {passed} passed{(skipped > 0 ? $", {skipped} skipped" : "")} of {results.Count}.");
        Console.WriteLine($"        Failed: {names}");
        return 1;
    }

    private static void RunStep(List<Result> results, string name, Func<string?> body)
    {
        var sw = Stopwatch.StartNew();
        var r = new Result { Name = name };
        try { r.Message = body() ?? ""; r.Status = "PASS"; }
        catch (StepSkipped sk) { r.Status = "SKIP"; r.Message = sk.Message; }
        catch (CheckFailed cf) { r.Status = "FAIL"; r.Message = cf.Message; }
        catch (Exception ex) { r.Status = "FAIL"; r.Message = $"{ex.GetType().Name}: {ex.Message}"; }
        sw.Stop();
        r.Ms = sw.ElapsedMilliseconds;
        results.Add(r);
        Console.WriteLine($"  [{r.Status}] {name}  ({r.Ms} ms){(r.Message.Length > 0 ? "  - " + r.Message : "")}");
    }

    // ---------------- steps ----------------

    /// <summary>Capture the default output as loopback → encode → send to 127.0.0.1 → receive →
    /// decode, for a few seconds, with the receiver rendering to nothing (no sound). PASS when
    /// packets flow both ways. SKIP on a machine with no usable output device (e.g. a headless CI
    /// box) so the suite stays green where there's simply nothing to capture.</summary>
    private static string? AudioRoundTrip(bool opus, int seconds)
    {
        var r = AudioLoopback.Run(opus, seconds);
        if (!r.Ran) return Skip(r.SkipReason ?? "audio loopback unavailable");
        Check(r.Flowed, $"audio did not flow end-to-end (sent={r.PacketsSent}, received={r.PacketsReceived})");
        return $"sent={r.PacketsSent}, received={r.PacketsReceived}";
    }

    /// <summary>Audio encryption: the right password decrypts to the original, the wrong one fails
    /// (silence, never garbage), fingerprints match/differ correctly, and the on-disk password
    /// scramble round-trips without leaving the password in plain text.</summary>
    private static string? Encryption()
    {
        var message = Encoding.UTF8.GetBytes("RemSound self-test payload 0123456789 the quick brown fox");
        var keyA = RemSoundCrypto.DeriveKey("correct horse battery staple");
        var keyB = RemSoundCrypto.DeriveKey("a different password entirely");

        var cipher = RemSoundCrypto.Encrypt(keyA, message);
        Check(RemSoundCrypto.TryDecrypt(keyA, cipher, out var plain) && plain.AsSpan().SequenceEqual(message),
            "the right password must decrypt to the original bytes");
        Check(!RemSoundCrypto.TryDecrypt(keyB, cipher, out _),
            "the wrong password must fail to decrypt (silence, not garbage)");

        Check(RemSoundCrypto.FingerprintsEqual(RemSoundCrypto.Fingerprint("shared"), RemSoundCrypto.Fingerprint("shared")),
            "the same password must produce the same fingerprint");
        Check(!RemSoundCrypto.FingerprintsEqual(RemSoundCrypto.Fingerprint("shared"), RemSoundCrypto.Fingerprint("other")),
            "different passwords must produce different fingerprints");

        const string pw = "p@ss w0rd!";
        Check(RemSoundCrypto.Obfuscate(pw) != pw, "a stored password must not be plain text");
        Check(RemSoundCrypto.Deobfuscate(RemSoundCrypto.Obfuscate(pw)) == pw, "the stored-password scramble must round-trip");
        return "AES-256-GCM, PBKDF2 fingerprint, on-disk scramble";
    }

    /// <summary>The "what's new after a successful update" marker round-trips: present after Write,
    /// Consume removes it exactly once, and a second Consume is a no-op. This is the contract the bug
    /// fix rests on — a failed update writes no marker (no popup); a success writes one (shown once).</summary>
    private static string? WhatsNewMarkerRoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rs-selftest-whatsnew-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Check(!WhatsNewMarker.Exists(dir), "a fresh folder must have no marker");
            WhatsNewMarker.Write(dir);
            Check(WhatsNewMarker.Exists(dir), "marker must exist after Write");
            Check(WhatsNewMarker.Consume(dir), "Consume must report it removed the marker");
            Check(!WhatsNewMarker.Exists(dir), "marker must be gone after Consume");
            Check(!WhatsNewMarker.Consume(dir), "a second Consume must be a no-op (shown exactly once)");
            return "write / exists / consume-once / idempotent";
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>The packet header writes and reads back for every type, and malformed packets
    /// (too short, bad magic, wrong version) are rejected rather than mis-parsed. Plus the PCM
    /// multi-part sub-header round-trips.</summary>
    private static string? PacketFraming()
    {
        Span<byte> header = stackalloc byte[RemPacket.HeaderSize];
        foreach (var type in new[] { RemPacketType.Format, RemPacketType.Audio, RemPacketType.Heartbeat, RemPacketType.Control })
        {
            RemPacket.WriteHeader(header, type, streamId: 7, sequence: 42);
            Check(RemPacket.TryReadHeader(header, out var t, out var sid, out var seq) && t == type && sid == 7 && seq == 42,
                $"header round-trip failed for {type}");
        }

        Check(!RemPacket.TryReadHeader(new byte[5], out _, out _, out _), "a too-short packet must be rejected");
        Check(!RemPacket.TryReadHeader(new byte[RemPacket.HeaderSize], out _, out _, out _), "a zero/bad-magic packet must be rejected");

        var wrongVersion = new byte[RemPacket.HeaderSize];
        RemPacket.WriteHeader(wrongVersion, RemPacketType.Audio, 1, 1);
        wrongVersion[4] = 99;
        Check(!RemPacket.TryReadHeader(wrongVersion, out _, out _, out _), "a wrong-version packet must be rejected");

        Span<byte> sub = stackalloc byte[RemPcmFrame.SubHeaderSize];
        RemPcmFrame.WriteSubHeader(sub, frameId: 12345, partIndex: 1, totalParts: 3);
        Check(RemPcmFrame.TryReadSubHeader(sub, out var fid, out var pi, out var tp) && fid == 12345 && pi == 1 && tp == 3,
            "PCM sub-header round-trip failed");
        return "header + PCM sub-header round-trip, malformed rejected";
    }

    /// <summary>
    /// Client-to-server compatibility guard. The Pi relay (<c>server/remsound-relay.py</c>)
    /// forwards packets by reading ONLY the wire header at fixed byte offsets — it never looks at
    /// the audio. These are the exact field positions and values it assumes. If RemSound's header
    /// ever changes shape, this step FAILS, which is the reminder that the relay must be updated
    /// and a new <c>server-*</c> release cut before shipping. Ideally we never touch the server —
    /// this check is how we keep proving that, in case the network stack changes underneath us.
    /// </summary>
    private static string? ServerWireCompat()
    {
        // Golden contract the relay parses (see remsound-relay.py: MAGIC, V1_VERSION, header offsets).
        Check(RemPacket.HeaderSize == 12, "the relay reads a 12-byte header; RemPacket.HeaderSize must stay 12");
        Check(RemPacket.Version == 1, "the relay matches version byte 1 (V1_VERSION); RemPacket.Version must stay 1");
        Check(RemPacket.DefaultPort == 47830, "the relay listens on UDP 47830; RemPacket.DefaultPort must stay 47830");

        // Packet-type values both ends agree on — changing any breaks interop with the relay/peers.
        Check((byte)RemPacketType.Format == 1 && (byte)RemPacketType.Audio == 2
              && (byte)RemPacketType.KeepAlive == 3 && (byte)RemPacketType.Heartbeat == 4
              && (byte)RemPacketType.Control == 5,
            "packet type values must stay Format=1, Audio=2, KeepAlive=3, Heartbeat=4, Control=5");

        // Build a real header and assert the byte-level layout the relay reads.
        Span<byte> h = stackalloc byte[RemPacket.HeaderSize];
        RemPacket.WriteHeader(h, RemPacketType.Audio, streamId: 0x1234, sequence: 0xAABBCCDD);
        Check(h[0] == (byte)'R' && h[1] == (byte)'M' && h[2] == (byte)'N' && h[3] == (byte)'D',
            "magic must be ASCII 'RMND' at offset 0 (the relay's first-four-byte check)");
        Check(h[4] == 1, "version byte must be at offset 4");
        Check(h[5] == (byte)RemPacketType.Audio, "type byte must be at offset 5");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(h.Slice(6, 2)) == 0x1234,
            "streamId must be a little-endian uint16 at offset 6 (the relay's pairing key)");
        Check(BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(8, 4)) == 0xAABBCCDD,
            "sequence must be a little-endian uint32 at offset 8");
        return "12-byte 'RMND' header; relay-visible fields unchanged";
    }

    /// <summary>Per-peer volume/pan/EQ DSP: nothing-to-do builds a null chain, the master-off state
    /// bypasses, a real volume actually attenuates the signal, and the parametric range→peaking maths
    /// is sane. This is the receive-side shaping that also feeds recordings.</summary>
    private static string? PeerShapingDsp()
    {
        Check(PeerDspChain.Build(null, enabled: true) is null, "no shaping must build a null (do-nothing) chain");
        Check(PeerDspChain.Build(new PeerShaping(), enabled: true) is null, "default (unity) shaping must build a null chain");

        var half = new PeerShaping { Volume = 0.5f };
        Check(PeerDspChain.Build(half, enabled: false) is null, "master switch off must bypass shaping (null chain)");

        var chain = PeerDspChain.Build(half, enabled: true);
        Check(chain is { IsNoOp: false }, "a 50% volume must build a real chain");
        var buf = new float[8];
        Array.Fill(buf, 1.0f);
        chain!.Process(buf, buf.Length / 2);   // 4 stereo frames
        Check(buf.All(v => Math.Abs(v - 0.5f) < 0.001f), $"volume 50% must halve the signal (got {buf[0]:0.000})");

        var para = new PeerShaping { EqMode = PeerEqMode.Parametric16Band };
        para.ParametricBands.Add(new ParametricBand { StartHz = 200, EndHz = 800, GainDb = 6 });
        Check(PeerDspChain.Build(para, enabled: true) is { IsNoOp: false }, "a parametric band must build a real chain");
        PeerEqBands.ParametricToPeaking(200, 800, out var centre, out var q);
        Check(centre > 200 && centre < 800 && q is > 0.1f and < 12f,
            $"parametric range→peaking must give a sane centre ({centre:0} Hz) and Q ({q:0.00})");
        return "unity→null, master-off→null, volume, parametric";
    }

    /// <summary>Proves the "every received stream plays to EVERY active output" fan-out: with both output
    /// lanes active (BothIndependent), one incoming stream must produce audio on BOTH the WASAPI and the
    /// ASIO lane surface — the WASAPI lane from the primary session, the ASIO lane from its mirror
    /// replica. Before the fan-out, only the lane matching the sender's capture tag played and the other
    /// output was silent (the bug Ed hit: ASIO-sent audio never reached the WASAPI output).</summary>
    private static string? FanOutToBothOutputs()
    {
        // Driven inside RemSound.Receiver (PlayoutEngine/SessionPlayout are internal there).
        var err = ReceiverSelfChecks.FanOutToBothOutputs();
        Check(err is null, err ?? "");
        return "one stream played to both output lanes (WASAPI + ASIO fan-out)";
    }

    /// <summary>Per-application send plumbing: the enumerator returns a well-formed snapshot without
    /// throwing (it may be empty on a silent/headless box — that's fine), the "proc:PID" id round-trips,
    /// and the Windows-version support gate answers consistently. Does NOT open a real process-loopback
    /// capture — that needs a live playing app + hardware, validated separately.</summary>
    private static string? AppSendEnumeration()
    {
        var apps = RemSound.Sender.AudioAppEnumerator.Snapshot();
        Check(apps is not null, "enumerator returned null");
        foreach (var a in apps!)
            Check(!string.IsNullOrWhiteSpace(a.ProcessName), "an app had an empty process name");

        Check(ProcessLoopbackId.TryParse(ProcessLoopbackId.Format(1234), out var pid) && pid == 1234,
            "proc:PID id did not round-trip");
        Check(!ProcessLoopbackId.TryParse("asio:0", out _), "ASIO id wrongly parsed as a process id");

        var supported = RemSound.Sender.ProcessLoopbackCapture.IsSupported;
        Check(supported == OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041),
            "support gate disagrees with the OS build check");
        return $"enumerated {apps.Count} app(s); process-loopback supported={supported}";
    }

    /// <summary>Exercises the process-loopback capture's real start → run → teardown cycle several times
    /// against our OWN process, on hardware. This is the regression guard for the ASIO-toggle hard crash:
    /// a bad COM teardown (releasing objects from the wrong thread / mid-native-call) would take the whole
    /// test process down with an access violation, failing the gate. SKIP on Windows too old to support
    /// process loopback.</summary>
    private static string? AppSendCaptureLifecycle()
    {
        if (!RemSound.Sender.ProcessLoopbackCapture.IsSupported)
            return Skip("process loopback needs Windows 10 build 19041+");

        var pid = Process.GetCurrentProcess().Id;
        var cycles = 0;
        for (var i = 0; i < 3; i++)
        {
            var capture = new RemSound.Sender.ProcessLoopbackCapture(pid);
            var frames = 0L;
            capture.DataAvailable += (_, e) => Interlocked.Add(ref frames, e.BytesRecorded);
            capture.StartRecording();
            Thread.Sleep(150);         // let activation + the capture loop run and then be torn down
            capture.Dispose();          // teardown while the capture thread is live — the crash scenario
            cycles++;
        }
        return $"ran {cycles} start/stop/dispose cycles on pid {pid} with no crash";
    }

    /// <summary>Soak test for runtime lifecycle transitions — the class of bug that hard-crashed when Ed
    /// toggled the ASIO driver mid-app-send. Drives a REAL sender+receiver pair over loopback through a
    /// matrix of transitions in every combination: audio mode, send sources (incl. process-loopback torn
    /// down and rebuilt), receive outputs, per-peer pan/EQ on and off, codec, and tight-latency — then a
    /// rapid reconfigure loop. Any unsafe teardown crashes the whole test process and fails the gate;
    /// otherwise it also checks handles don't run away across the churn. These transitions take an age to
    /// cover by hand and regress easily, so they live here.
    ///
    /// Real ASIO hardware cycling is OPT-IN via the REMSOUND_TEST_ASIO env var ("1" = first installed
    /// driver, or a driver name) so routine builds never open — and possibly hang or lock — a real audio
    /// interface. Without it the churn still covers the WASAPI + process-loopback teardown paths that
    /// actually crashed.</summary>
    private static string? LifecycleChurn()
    {
        const int port = 47844;
        var ownPid = Process.GetCurrentProcess().Id;
        var procOk = RemSound.Sender.ProcessLoopbackCapture.IsSupported;

        string? deviceId = null;
        try { deviceId = AudioDeviceCatalog.LoadOutputs().FirstOrDefault(o => o.DeviceId is not null)?.DeviceId; }
        catch { /* headless / no devices — still churn modes, proc capture and DSP */ }

        string? asioDriver = null;
        var asioEnv = Environment.GetEnvironmentVariable("REMSOUND_TEST_ASIO");
        if (!string.IsNullOrWhiteSpace(asioEnv))
        {
            try
            {
                var drivers = RemSound.Sender.AsioDeviceProbe.EnumerateDriverNames();
                asioDriver = string.Equals(asioEnv, "1", StringComparison.Ordinal)
                    ? drivers.FirstOrDefault()
                    : drivers.FirstOrDefault(d => string.Equals(d, asioEnv, StringComparison.OrdinalIgnoreCase));
            }
            catch { /* driver probe failed — fall back to WASAPI-only churn */ }
        }

        // DSP states: none, a plain volume cut, and a full pan + parametric-EQ chain.
        var panEq = new PeerShaping { Volume = 0.7f, Pan = -0.3f, EqMode = PeerEqMode.Parametric16Band };
        panEq.ParametricBands.Add(new ParametricBand { StartHz = 200, EndHz = 800, GainDb = 5 });
        var dspStates = new PeerDspChain?[]
        {
            null,
            PeerDspChain.Build(new PeerShaping { Volume = 0.5f }, enabled: true),
            PeerDspChain.Build(panEq, enabled: true),
        };

        // Send spec sets: empty, device loopback, process-loopback (own pid), and both together — so the
        // process-loopback capture is repeatedly torn down and rebuilt (the crash path).
        var loop = deviceId is null ? null : new CaptureSourceSpec(deviceId, CaptureKind.Loopback, "loopback");
        var proc = procOk ? new CaptureSourceSpec(ProcessLoopbackId.Format(ownPid), CaptureKind.ProcessLoopback, "self") : null;
        var specSets = new List<List<CaptureSourceSpec>> { new() };
        if (loop is not null) specSets.Add(new() { loop });
        if (proc is not null) specSets.Add(new() { proc });
        if (loop is not null && proc is not null) specSets.Add(new() { loop, proc });

        var recvSets = new List<string[]> { Array.Empty<string>() };
        if (deviceId is not null) recvSets.Add(new[] { deviceId });

        var handlesBefore = SafeHandleCount();
        var transitions = 0;

        using (var receiver = new AudioReceiver())
        using (var sender = new RemSound.Sender.AudioSender())
        {
            try { receiver.Start(port); }
            catch (Exception ex) { return Skip($"could not bind test port {port}: {ex.Message}"); }
            receiver.SetOutputDevices(Array.Empty<string>());   // decode only — never make a sound
            sender.SetReceivers(new[] { new IPEndPoint(IPAddress.Loopback, port) });
            sender.Start();

            var modes = new List<(AudioMode mode, string? driver)> { (AudioMode.WasapiOnly, null) };
            if (asioDriver is not null) modes.Add((AudioMode.BothIndependent, asioDriver));
            var codecs = new[] { AudioTransportCodec.Pcm, AudioTransportCodec.Opus };

            // Soak: with REMSOUND_TEST_SOAK=<seconds> set, repeat the whole transition matrix until the
            // deadline (a real minutes-long soak); unset, it runs the matrix once in the normal gate.
            int.TryParse(Environment.GetEnvironmentVariable("REMSOUND_TEST_SOAK"), out var soakSeconds);
            var deadline = Environment.TickCount64 + Math.Max(0, soakSeconds) * 1000L;

            var i = 0;
            do
            {
                foreach (var (mode, driver) in modes)
                {
                    sender.SetAudioMode(mode, driver);
                    receiver.SetAudioMode(mode, driver);
                    foreach (var specs in specSets)
                    {
                        sender.Configure(specs);
                        foreach (var recv in recvSets) receiver.SetOutputDevices(recv);
                        foreach (var dsp in dspStates)
                        {
                            receiver.SetPeerDsp(IPAddress.Loopback, dsp);
                            sender.ConfigureCodec(codecs[i % codecs.Length]);
                            sender.SetTightLatency(i % 2 == 0);
                            Thread.Sleep(15);
                            transitions++;
                            i++;
                        }
                    }
                }
            }
            while (Environment.TickCount64 < deadline);

            // Rapid WASAPI-only reconfigure loop: hammer the process-loopback capture teardown/rebuild —
            // the mechanism that actually crashed. No mode changes here, so it never abuses real hardware.
            sender.SetAudioMode(AudioMode.WasapiOnly, null);
            receiver.SetAudioMode(AudioMode.WasapiOnly, null);
            for (var k = 0; k < 24; k++)
            {
                sender.Configure(specSets[k % specSets.Count]);
                receiver.SetPeerDsp(IPAddress.Loopback, dspStates[k % dspStates.Length]);
                Thread.Sleep(10);
                transitions++;
            }

            // Gentle ASIO on/off cycling (opt-in only), with a process-loopback source live across the
            // toggle — the exact Ed repro. Generous settle time between toggles: some ASIO drivers
            // (e.g. Audient) stall for seconds on a quick close+reopen, so we must NOT hammer them.
            if (asioDriver is not null)
            {
                for (var k = 0; k < 4; k++)
                {
                    var toBoth = k % 2 == 0;
                    var mode = toBoth ? AudioMode.BothIndependent : AudioMode.WasapiOnly;
                    var driver = toBoth ? asioDriver : null;
                    sender.SetAudioMode(mode, driver);
                    receiver.SetAudioMode(mode, driver);
                    if (proc is not null) sender.Configure(new List<CaptureSourceSpec> { proc });
                    Thread.Sleep(600);
                    transitions++;
                }
                sender.SetAudioMode(AudioMode.WasapiOnly, null);
                receiver.SetAudioMode(AudioMode.WasapiOnly, null);
            }

            sender.Stop();
            receiver.Stop();
        }

        SettleForLeakCheck();
        var handleGrowth = SafeHandleCount() - handlesBefore;
        Check(handleGrowth < 400, $"handle growth across the churn is too high ({handleGrowth}) — a transition may be leaking");

        return $"{transitions} transitions; specSets={specSets.Count}, dsp={dspStates.Length}, "
             + $"asio={(asioDriver ?? "skipped (set REMSOUND_TEST_ASIO)")}, proc={procOk}, handles+{handleGrowth}";
    }

    private static int SafeHandleCount()
    {
        try { using var p = Process.GetCurrentProcess(); p.Refresh(); return p.HandleCount; }
        catch { return 0; }
    }

    /// <summary>Force pending finalizers/GC and give the OS a moment to release handles, so a leak check
    /// after a churn reflects genuine leaks rather than not-yet-collected disposables (which pile up
    /// under fast churn and would otherwise false-flag a long soak).</summary>
    private static void SettleForLeakCheck()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(60);
        }
    }

    /// <summary>Records a short synthetic tone to disk in every output format and checks each file is
    /// written with real content — the thing Ed can't face testing by ear on every change. Drives the
    /// real <see cref="AudioRecorder"/> writer (WAV / MP3 / OGG-Opus / FLAC encoders and their native
    /// bits) headlessly by feeding its audio-thread taps directly, then asserting the file exists and is
    /// non-trivial. Also covers the received/sent source gate and mono downmix.</summary>
    private static string? RecordingEngine()
    {
        var temp = Path.Combine(Path.GetTempPath(), "remsound-rec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var summary = new List<string>();

            // 1. Every format, Both source, stereo — the file must exist with real content.
            foreach (var (fmt, ext) in new[]
            {
                (RecordingFileFormat.Wav, "wav"), (RecordingFileFormat.Mp3, "mp3"),
                (RecordingFileFormat.Ogg, "ogg"), (RecordingFileFormat.Flac, "flac"),
            })
            {
                var path = Path.Combine(temp, $"both.{ext}");
                var len = RecordTone(temp, path,
                    new RecordingSettings { FileFormat = fmt, Source = RecordingSource.Both, ChannelMode = RecordingChannelMode.Stereo },
                    feedReceived: true, feedSent: true);
                Check(len > 200, $"{ext.ToUpperInvariant()} recording must have real content (got {len} bytes)");
                summary.Add($"{ext}={len}B");
            }

            // 2. Source gate: a SentOnly recorder fed only RECEIVED audio must stay (near) empty.
            var sentOnlyPath = Path.Combine(temp, "gate.wav");
            var gateLen = RecordTone(temp, sentOnlyPath,
                new RecordingSettings { FileFormat = RecordingFileFormat.Wav, Source = RecordingSource.SentOnly },
                feedReceived: true, feedSent: false);
            var fullLen = RecordTone(temp, Path.Combine(temp, "full.wav"),
                new RecordingSettings { FileFormat = RecordingFileFormat.Wav, Source = RecordingSource.SentOnly },
                feedReceived: false, feedSent: true);
            Check(gateLen < fullLen / 2, $"a SentOnly recorder must ignore received audio (gate={gateLen}B vs full={fullLen}B)");

            // 3. Mono downmix produces a valid (smaller) WAV.
            var monoLen = RecordTone(temp, Path.Combine(temp, "mono.wav"),
                new RecordingSettings { FileFormat = RecordingFileFormat.Wav, Source = RecordingSource.Both, ChannelMode = RecordingChannelMode.Mono },
                feedReceived: true, feedSent: false);
            Check(monoLen > 200, $"mono WAV must have real content (got {monoLen} bytes)");

            return string.Join(", ", summary) + $"; gate ok; mono={monoLen}B";
        }
        finally { try { Directory.Delete(temp, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>Split-track (multi-track) recording: with SplitTracks on and one connected peer, the
    /// recorder must write a FOLDER of tracks — one per peer plus your own send — not a single mixed file.
    /// Drives the real RecordingController via a settings-injection seam (so it never touches the shared
    /// settings store), feeds the "your send" track through the tap the controller wires onto the sender,
    /// and asserts the track files land with content.</summary>
    private static string? RecordingSplitTracks()
    {
        var temp = Path.Combine(Path.GetTempPath(), "remsound-split-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        using var receiver = new AudioReceiver();
        using var sender = new RemSound.Sender.AudioSender();
        try
        {
            var controller = new RecordingController(sender, receiver, new RemSoundSettingsStore("RemSound"), _ => { })
            {
                SettingsSourceForTest = () => new RecordingSettings
                {
                    SplitTracks = true,
                    Source = RecordingSource.Both,
                    FileFormat = RecordingFileFormat.Wav,
                    Folder = temp,
                },
                ConnectedPeersProvider = () => new[] { (IPAddress.Loopback, "TestPeer") },
            };

            controller.Start();
            Check(controller.IsRecording, "split recording should be running after Start");

            // Feed the "your send" track through the tap Start wired onto the sender.
            var tap = sender.OnSentSamples;
            if (tap is not null)
            {
                var chunk = new float[480 * 2];
                var phase = 0.0;
                for (var c = 0; c < 60; c++)
                {
                    for (var i = 0; i < chunk.Length; i += 2)
                    {
                        var s = (float)(0.2 * Math.Sin(phase));
                        phase += 2 * Math.PI * 440 / 48000;
                        chunk[i] = s; chunk[i + 1] = s;
                    }
                    tap(chunk.AsMemory(), RenderRoute.Mixed);
                    Thread.Sleep(2);
                }
            }
            controller.Stop();
            for (var i = 0; i < 40 && Directory.GetFiles(temp, "*.wav", SearchOption.AllDirectories).Length == 0; i++) Thread.Sleep(25);

            var files = Directory.GetFiles(temp, "*.wav", SearchOption.AllDirectories);
            Check(files.Length >= 2, $"split recording must make one file per peer plus your own (found {files.Length})");
            Check(files.Any(f => new FileInfo(f).Length > 200), "at least one split track (your own send) must have real content");
            return $"split recording made {files.Length} track files, one with content";
        }
        finally { try { Directory.Delete(temp, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>Load/soak: rapidly start, feed, stop and dispose recordings across every format, checking
    /// nothing leaks handles across the churn — catches recorder/encoder lifecycle leaks and races that a
    /// single recording wouldn't surface. Set the env var REMSOUND_TEST_SOAK=&lt;seconds&gt; to keep
    /// hammering for that long (a real soak run); unset it does one quick round in the normal gate.</summary>
    private static string? RecordingChurn()
    {
        var temp = Path.Combine(Path.GetTempPath(), "remsound-recchurn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            int.TryParse(Environment.GetEnvironmentVariable("REMSOUND_TEST_SOAK"), out var soakSeconds);
            var deadline = Environment.TickCount64 + Math.Max(0, soakSeconds) * 1000L;
            var formats = new[] { RecordingFileFormat.Wav, RecordingFileFormat.Mp3, RecordingFileFormat.Ogg, RecordingFileFormat.Flac };
            var handlesBefore = SafeHandleCount();
            var cycles = 0;
            do
            {
                foreach (var fmt in formats)
                {
                    var path = Path.Combine(temp, $"c{cycles}.{AudioRecorder.ExtensionFor(fmt)}");
                    RecordTone(temp, path, new RecordingSettings { FileFormat = fmt, Source = RecordingSource.Both }, feedReceived: true, feedSent: true);
                    try { File.Delete(path); } catch { /* best-effort */ }
                    cycles++;
                }
            }
            while (Environment.TickCount64 < deadline);
            SettleForLeakCheck();
            var growth = SafeHandleCount() - handlesBefore;
            Check(growth < 500, $"handle growth across {cycles} record cycles is too high ({growth}) — a recorder may be leaking");
            return $"{cycles} record start/stop/dispose cycles; handles+{growth}" + (soakSeconds > 0 ? $"; soak={soakSeconds}s" : "");
        }
        finally { try { Directory.Delete(temp, recursive: true); } catch { /* best-effort */ } }
    }

    // Records ~0.4s of a 440 Hz tone with the given settings to an explicit path and returns the file
    // size. Feeds the recorder's audio-thread taps directly, pacing so the writer thread drains the ring.
    private static long RecordTone(string temp, string path, RecordingSettings settings, bool feedReceived, bool feedSent)
    {
        long finishedBytes = -1;
        using (var rec = new AudioRecorder(settings, null, (_, b) => finishedBytes = b, path))
        {
            const int rate = 48000;
            var totalFrames = (int)(rate * 0.4);
            var chunk = new float[480 * 2];
            var phase = 0.0;
            var done = 0;
            while (done < totalFrames)
            {
                var frames = Math.Min(chunk.Length / 2, totalFrames - done);
                for (var i = 0; i < frames; i++)
                {
                    var s = (float)(0.2 * Math.Sin(phase));
                    phase += 2 * Math.PI * 440 / rate;
                    chunk[i * 2] = s; chunk[i * 2 + 1] = s;
                }
                var mem = chunk.AsMemory(0, frames * 2);
                if (feedReceived) rec.WriteReceived(mem, RenderRoute.Mixed);
                if (feedSent) rec.WriteSent(mem, RenderRoute.Mixed);
                done += frames;
                Thread.Sleep(2);
            }
            rec.Stop();
        }
        for (var i = 0; i < 60 && !File.Exists(path); i++) Thread.Sleep(25);
        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    /// <summary>The REAL Windows-service lifecycle end to end: install → started → stopped → uninstalled,
    /// driving the actual SCM (sc.exe + ServiceController). Needs administrator rights, so it's OPT-IN via
    /// REMSOUND_TEST_SERVICE=1 and skips cleanly otherwise — the normal gate runs unprivileged. It clears
    /// any leftover registration first (so a stray copy from an aborted run can't fail it) and always
    /// removes the service afterwards, even on failure. Run it elevated:
    ///   set REMSOUND_TEST_SERVICE=1 &amp;&amp; RemSound.exe --selftest   (from an elevated prompt)</summary>
    private static string? ServiceRealLifecycle()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("REMSOUND_TEST_SERVICE"), "1", StringComparison.Ordinal))
            return Skip("set REMSOUND_TEST_SERVICE=1 (elevated) to run the real install/start/stop/uninstall lifecycle");
        if (!IsAdministrator())
            return Skip("the real service lifecycle needs administrator rights");

        // Start from a known state — remove any service left over from a previous aborted run. DoUninstall
        // stops it first, so this also clears a leftover RUNNING copy.
        if (ServiceControl.IsInstalled()) ServiceControl.DoUninstall();
        Check(!ServiceControl.IsInstalled(), "pre-clean: no leftover RemSound service should remain");

        try
        {
            Check(ServiceControl.DoInstall() == 0, "DoInstall should succeed");
            Check(ServiceControl.IsInstalled(), "the service should report installed after DoInstall");
            Check(ServiceControl.Query() == ServiceState.Stopped, "an auto-start service is Stopped until started");

            Check(ServiceControl.DoStart() == 0, "DoStart should succeed");
            Check(ServiceControl.Query() == ServiceState.Running, "the service should be Running after DoStart");

            Check(ServiceControl.DoStop() == 0, "DoStop should succeed");
            Check(ServiceControl.Query() == ServiceState.Stopped, "the service should be Stopped after DoStop");

            Check(ServiceControl.DoUninstall() == 0, "DoUninstall should succeed");
            Check(!ServiceControl.IsInstalled(), "the service should be gone after DoUninstall");

            return "installed → started → stopped → uninstalled (real SCM)";
        }
        finally
        {
            // Safety net: never leave a service registered (its binPath would point at a throwaway exe).
            try { if (ServiceControl.IsInstalled()) ServiceControl.DoUninstall(); } catch { /* best-effort */ }
        }
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>The sc.exe "create" argument string quotes a spaced exe path correctly — a real footgun
    /// (a broken binPath silently installs a service that can't start). Pure/side-effect-free, so it
    /// never touches the SCM or needs admin.</summary>
    private static string? ServiceRegistrationArgs()
    {
        const string exe = @"C:\Program Files\RemSound\RemSound.exe";
        var args = ServiceControl.BuildCreateArgs(exe);
        Check(args.StartsWith($"create {ServiceControl.ServiceName} "), "must be a create for the named service");
        Check(args.Contains("start= auto"), "service must be auto-start");
        // The exe path must be wrapped in ESCAPED quotes inside the binPath value, followed by the run
        // verb, so a path with spaces survives sc.exe's parsing.
        Check(args.Contains("\\\"" + exe + "\\\" " + ServiceControl.RunVerb),
            $"exe path must be escaped-quoted with the run verb (got: {args})");
        Check(args.Contains($"DisplayName= \"{ServiceControl.DisplayName}\""), "must set the display name");
        return "sc create args quoted correctly for a spaced path";
    }

    /// <summary>The service must configure the sender EXACTLY like the main app: derive both the audio key
    /// AND the fingerprint from the password (a missing fingerprint gets the encrypted stream rejected at
    /// the peer), and apply the send-rate-adjusted Opus frame (the "Small" rate halves it). Guards the
    /// divergences found auditing the service against the main app.</summary>
    private static string? ServiceSenderParity()
    {
        var profile = new Profile
        {
            Title = "parity",
            Codec = AudioTransportCodec.Opus,
            OpusFrameSamplesPerChannel = 960,   // broadcast
            SendRate = SendRate.Tight,          // "Small" — should halve the Opus frame to 480
            WasapiSendMode = "devices",
        };
        profile.SelectedWasapiSendOutputs.Add("fake-device-id");     // a source so ApplyProfile proceeds
        profile.SelectedConnectedPeers.Add("127.0.0.1:47999");
        const string pw = "hunter2";
        profile.Password = RemSoundCrypto.Obfuscate(pw);

        using var host = new ServiceSendHost(() => profile);
        Check(host.ApplyProfile(profile), "ApplyProfile should proceed with a source + peer + password");
        var cfg = host.SenderConfigForTest;

        Check(cfg.Key is { Length: > 0 } && cfg.Key.SequenceEqual(RemSoundCrypto.DeriveKey(pw)),
            "the service must set the audio key = DeriveKey(password)");
        Check(cfg.Fingerprint is { Length: > 0 } && cfg.Fingerprint.SequenceEqual(RemSoundCrypto.Fingerprint(pw)),
            "the service must set the audio FINGERPRINT = Fingerprint(password), or the peer rejects the stream");
        Check(cfg.Codec == AudioTransportCodec.Opus, "codec must round-trip to the sender");
        Check(cfg.Frame == MainForm.EffectiveOpusFrameSamples(AudioTransportCodec.Opus, 960, SendRate.Tight) && cfg.Frame == 480,
            $"the Small send rate must halve the Opus frame like the main app (got {cfg.Frame})");
        return "key + fingerprint + effective Opus frame match the main app";
    }

    /// <summary>The lock-screen service's app-yield token: while a hold is active the service must see an
    /// interactive app present; once released (or on crash — the OS frees the mutex) it must see none.
    /// Uses a unique token name so the test is immune to a real RemSound running alongside the gate.</summary>
    private static string? ServiceInteractivePresence()
    {
        var name = @"Global\RemSound.Interactive.selftest." + Guid.NewGuid().ToString("N");
        Check(!InteractivePresence.IsInteractiveAppRunning(name), "no app should be seen before any hold");
        using (var hold = InteractivePresence.AcquireHold(name))
        {
            Check(hold is not null, "AcquireHold should succeed");
            Check(InteractivePresence.IsInteractiveAppRunning(name), "an app must be seen while the hold is active");
            // A second, independent check must also see it (the service polls repeatedly).
            Check(InteractivePresence.IsInteractiveAppRunning(name), "repeated checks must stay consistent while held");
        }
        var released = false;
        for (var i = 0; i < 40 && !released; i++)
        {
            if (!InteractivePresence.IsInteractiveAppRunning(name)) released = true; else Thread.Sleep(25);
        }
        Check(released, "no app should be seen after the hold is released");
        return "held → present; released → absent";
    }

    /// <summary>End-to-end proof of the send-only service host, headless (no window, no message pump):
    /// a temp send-only profile streams a captured device to a local receiver over loopback. Drives the
    /// real yield mechanism — ApplyProfile streams, Suspend stops, Resume re-reads and streams again —
    /// and then the RunLoop against the presence token: holding the token suspends the host, releasing it
    /// resumes. SKIPs on a box with no capturable output device.</summary>
    private static string? ServiceSendHostStream()
    {
        const int port = 47846;
        string? deviceId;
        try { deviceId = AudioDeviceCatalog.LoadOutputs().FirstOrDefault(o => o.DeviceId is not null)?.DeviceId; }
        catch (Exception ex) { return Skip("could not enumerate outputs: " + ex.Message); }
        if (deviceId is null) return Skip("no usable output device to capture from");

        // Unit-level checks first (no hardware): spec + endpoint building from a profile.
        var probe = new Profile { WasapiSendMode = "devices" };
        probe.SelectedWasapiSendOutputs.Add("dev-a");
        probe.SelectedConnectedPeers.Add("127.0.0.1:47846");
        probe.SelectedConnectedPeers.Add("10.0.0.9");          // no explicit port → the standard peer port
        probe.SelectedConnectedPeers.Add("bad::garbage::host");
        Check(ServiceSendHost.BuildSendSpecs(probe).Any(s => s.DeviceId == "dev-a" && s.Kind == CaptureKind.Loopback),
            "a WASAPI send output must become a loopback spec");
        var eps = ServiceSendHost.BuildEndpoints(probe);
        Check(eps.Any(e => e.Address.ToString() == "127.0.0.1" && e.Port == 47846), "a host:port peer must resolve to an endpoint");
        Check(eps.Any(e => e.Address.ToString() == "10.0.0.9" && e.Port == RemPacket.DefaultPeerDialPort),
            "a peer with no port must use the standard peer port (not the local audio port) — same as the main app");

        using var receiver = new AudioReceiver();
        try { receiver.Start(port); }
        catch (Exception ex) { return Skip($"could not bind test port {port}: {ex.Message}"); }
        receiver.SetOutputDevices(Array.Empty<string>()); // decode only — never make a sound

        var profile = new Profile
        {
            Title = "selftest-service",
            WasapiSendMode = "devices",
            Codec = AudioTransportCodec.Pcm,
        };
        profile.SelectedWasapiSendOutputs.Add(deviceId);
        profile.SelectedConnectedPeers.Add($"127.0.0.1:{port}");

        using var host = new ServiceSendHost(() => profile);

        Check(host.ApplyProfile(profile), "ApplyProfile should start streaming");
        Check(host.IsSending, "host should report sending after ApplyProfile");
        Thread.Sleep(500);
        var afterStart = receiver.PacketsReceived;
        Check(afterStart > 0, $"packets must flow from the service host (got {afterStart})");

        host.Suspend();
        Check(!host.IsSending, "host should report not sending after Suspend");
        Thread.Sleep(200);
        var atSuspend = receiver.PacketsReceived;
        Thread.Sleep(400);
        Check(receiver.PacketsReceived == atSuspend, "no packets must flow while suspended");

        Check(host.Resume(), "Resume should restart streaming");
        Thread.Sleep(500);
        Check(receiver.PacketsReceived > atSuspend, "packets must flow again after Resume");

        // Now the full RunLoop + presence token, with a unique token so a real app can't interfere.
        host.Suspend();
        var tokenName = @"Global\RemSound.Interactive.selftest." + Guid.NewGuid().ToString("N");
        var loopResult = RunLoopYieldCheck(host, receiver, tokenName);
        Check(loopResult is null, loopResult ?? "");

        return $"streamed headless; start/suspend/resume verified; {afterStart} pkts; yield loop ok";
    }

    // Drives ServiceSendHost.RunLoop against a presence token (unique name via a tiny shim): with the
    // token held the host must stay suspended; released, it must resume and packets must flow.
    private static string? RunLoopYieldCheck(ServiceSendHost host, AudioReceiver receiver, string tokenName)
    {
        using var cts = new CancellationTokenSource();
        // Hold the token BEFORE the loop starts so the host yields from the outset.
        var hold = InteractivePresence.AcquireHold(tokenName);
        if (hold is null) return "could not acquire the presence token for the yield check";
        var loop = new Thread(() => host.RunLoopWithToken(cts.Token, tokenName, pollMs: 100, resumeSettleMs: 200)) { IsBackground = true };
        loop.Start();
        try
        {
            Thread.Sleep(500);
            if (host.IsSending) return "host must stay suspended while the interactive token is held";
            var held = receiver.PacketsReceived;
            Thread.Sleep(300);
            if (receiver.PacketsReceived != held) return "no packets must flow while the token is held";

            hold.Dispose(); hold = null; // app "closes" — host should resume after the settle
            var resumed = false;
            for (var i = 0; i < 40 && !resumed; i++) { Thread.Sleep(50); if (host.IsSending) resumed = true; }
            if (!resumed) return "host must resume after the token is released";
            var before = receiver.PacketsReceived;
            Thread.Sleep(400);
            if (receiver.PacketsReceived <= before) return "packets must flow after the host resumes";
            return null;
        }
        finally
        {
            cts.Cancel();
            loop.Join(2000);
            hold?.Dispose();
        }
    }

    /// <summary>The v5 machine-wide settings and per-peer shaping survive a JSON save/reload: new
    /// AppConfig defaults, the named-peers book, the main tab order, per-peer shaping with parametric
    /// bands, and the new recording default. All in-memory — the real config/profiles aren't touched.</summary>
    private static string? V5ConfigRoundTrip()
    {
        var fresh = new AppConfig();
        Check(fresh.ShowPanEqTab, "ShowPanEqTab must default to true");
        Check(fresh.ThemeMode == "system", "ThemeMode must default to 'system'");
        Check(fresh.ShowDiscoveredPeers && fresh.ShowRememberedPeers, "the peer lists must default to shown");

        var cfg = new AppConfig
        {
            ThemeMode = "dark",
            MainTabOrder = ["audioio", "connectivity", "paneq", "audioprofile"],
            ShowDiscoveredPeers = false,
        };
        cfg.NamedPeers["ANDRE-PC"] = new NamedPeer
        {
            MachineName = "ANDRE-PC",
            FriendlyName = "Andre's desktop",
            LastAddress = "100.72.4.13",
            LastSeenUtc = new DateTime(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc),
        };
        var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
        var back = JsonSerializer.Deserialize<AppConfig>(json);
        Check(back is not null, "config must deserialise");
        Check(back!.ThemeMode == "dark" && !back.ShowDiscoveredPeers, "theme and list toggles must round-trip");
        Check(back.MainTabOrder is { Count: 4 } && back.MainTabOrder[0] == "audioio", "tab order must round-trip");
        Check(back.NamedPeers.TryGetValue("ANDRE-PC", out var np)
              && np.FriendlyName == "Andre's desktop" && np.LastAddress == "100.72.4.13",
            "named peers must round-trip");

        var shaping = new PeerShaping { Volume = 0.7f, Pan = -0.5f, EqMode = PeerEqMode.Parametric16Band };
        shaping.ParametricBands.Add(new ParametricBand { StartHz = 100, EndHz = 500, GainDb = 3.5f });
        var sback = JsonSerializer.Deserialize<PeerShaping>(JsonSerializer.Serialize(shaping));
        Check(sback is not null && sback.EqMode == PeerEqMode.Parametric16Band
              && sback.ParametricBands.Count == 1 && Math.Abs(sback.ParametricBands[0].GainDb - 3.5f) < 0.001f,
            "peer shaping (with parametric bands) must round-trip");

        Check(new RecordingSettings().Source == RecordingSource.Both, "recording source must default to Both");
        return "config defaults, named peers, tab order, parametric shaping, recording default";
    }

    /// <summary>App settings survive a save-and-reload (the same JSON serialisation
    /// <see cref="AppConfig.Save"/> / <see cref="AppConfig.Load"/> use) without touching the real
    /// config on disk.</summary>
    private static string? SettingsRoundTrip()
    {
        var original = new AppConfig
        {
            LoggingEnabled = true,
            StartMinimised = true,
            EnableStartupCue = false,
            UpdateCheckFrequency = UpdateCheckFrequency.EveryHour,
            StartWithProfileTitle = "Studio link",
            ProfilesDirectory = @"X:\some\profiles\folder",
        };
        var json = JsonSerializer.Serialize(original, new JsonSerializerOptions { WriteIndented = true });
        var loaded = JsonSerializer.Deserialize<AppConfig>(json);
        Check(loaded is not null, "config must deserialise");
        Check(loaded!.LoggingEnabled == original.LoggingEnabled
              && loaded.StartMinimised == original.StartMinimised
              && loaded.EnableStartupCue == original.EnableStartupCue
              && loaded.UpdateCheckFrequency == original.UpdateCheckFrequency
              && loaded.StartWithProfileTitle == original.StartWithProfileTitle
              && loaded.ProfilesDirectory == original.ProfilesDirectory,
            "settings must survive a save/reload unchanged");
        return null;
    }

    /// <summary>A profile saved through <see cref="ProfileStore"/> reloads with its fields intact.
    /// Runs entirely inside a throwaway temp folder — the user's real profiles are never touched.</summary>
    private static string? ProfileRoundTrip()
    {
        var temp = Path.Combine(Path.GetTempPath(), "remsound-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProfileStore(temp);
            var p = Profile.NewBlank();
            p.Title = "selftest roundtrip";
            p.ReceiveAudioOn = true;
            p.SendAudioOn = false;
            p.Volume = 73;
            p.AudioPort = 47830;
            p.AsioDriverName = "Some ASIO Driver";
            p.SelectedWasapiSendInputs.Add("device-id-abc");
            // Per-application send mode (issue #20) is per-profile — round-trip it too.
            p.WasapiSendMode = "applications";
            p.SendAllApplications = false;
            p.SelectedSendApplications.Add("vlc");
            p.SelectedSendApplications.Add("firefox");
            store.Save(p);

            var back = store.Load("selftest roundtrip");
            Check(back is not null, "the profile must load back from disk");
            Check(back!.Title == p.Title
                  && back.Volume == 73
                  && back.ReceiveAudioOn && !back.SendAudioOn
                  && back.AudioPort == 47830
                  && back.AsioDriverName == "Some ASIO Driver"
                  && back.SelectedWasapiSendInputs.Contains("device-id-abc"),
                "profile fields must survive a save/reload");
            Check(back.WasapiSendMode == "applications"
                  && !back.SendAllApplications
                  && back.SelectedSendApplications.Contains("vlc")
                  && back.SelectedSendApplications.Contains("firefox"),
                "per-application send settings must survive a save/reload");
            return null;
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>The diagnostics report lists a profile's title but never its password (plain or
    /// scrambled). Guards against a future change accidentally dumping profile contents into a
    /// support bundle. Uses a throwaway temp profiles folder with a known canary password.</summary>
    private static string? DiagnosticsPrivacy()
    {
        var temp = Path.Combine(Path.GetTempPath(), "remsound-selftest-priv-" + Guid.NewGuid().ToString("N"));
        const string canaryTitle = "PrivacyCanaryProfile";
        const string canaryPassword = "SENTINEL-PW-DO-NOT-LEAK-7f3a91";
        try
        {
            Directory.CreateDirectory(temp);
            var store = new ProfileStore(temp);
            var p = Profile.NewBlank();
            p.Title = canaryTitle;
            p.Password = canaryPassword;
            store.Save(p);

            var report = CommandLine.BuildDiagnosticsReport(new AppConfig { ProfilesDirectory = temp }, runLiveAudioProbe: false);
            Check(report.Contains("RemSound diagnostics") && report.Contains(Environment.MachineName),
                "the diagnostics report must contain its basic header");
            Check(report.Contains(canaryTitle), "the diagnostics report should list the profile title");
            Check(!report.Contains(canaryPassword), "the diagnostics report must NOT contain a profile password (plain text)");
            Check(!report.Contains(RemSoundCrypto.Obfuscate(canaryPassword)),
                "the diagnostics report must NOT contain a profile password (scrambled form)");
            return "title listed, password withheld";
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>The files a shipped RemSound needs at runtime are actually next to the exe: the
    /// bundled manual, the cue sounds, and the native Opus library.</summary>
    private static string? ResourcesPresent()
    {
        var root = AppContext.BaseDirectory;
        Check(File.Exists(Path.Combine(root, "readme.html")), "readme.html (the F1 manual) must ship next to the exe");

        // The shipped DEFAULT cues live install-side in "default sounds\" next to the exe
        // (AppConfig.SoundsDirectory). An empty/absent folder means the shipped build had no sounds -
        // exactly the bug that shipped the v3.9 zip with no cue sounds.
        var soundsDir = AppConfig.SoundsDirectory;
        Check(Directory.Exists(soundsDir), "the shipped 'default sounds' folder must exist next to the exe");
        // Cues ship as numbered variants ("connect 1.wav", ...); each required cue must have at
        // least one variant present.
        foreach (var cue in new[]
        {
            "connect.wav", "disconnect.wav", "start up.wav",
            "send on.wav", "send off.wav", "recieve on.wav", "recieve off.wav", "minimise.wav", "maximise.wav",
            "check.wav", "uncheck.wav",
        })
        {
            Check(CueSounds.Variants(cue).Count > 0,
                $"no sound variant present for the '{Path.GetFileNameWithoutExtension(cue)}' cue (was the shipped 'default sounds' folder empty?)");
        }
        // Keyboard-click typing sounds + the password passkey sound.
        Check(File.Exists(Path.Combine(soundsDir, "key 1.wav")), "keyboard-click sound 'key 1.wav' must be present");
        Check(File.Exists(Path.Combine(soundsDir, "passkey.wav")), "password 'passkey.wav' must be present");

        // Native Opus (Concentus.Native) keeps the encoder off the allocation-heavy managed fallback.
        var nativeOpus = Path.Combine(root, "runtimes", "win-x64", "native", "opus.dll");
        Check(File.Exists(nativeOpus), "native opus.dll must ship under runtimes\\win-x64\\native\\");
        return "manual, cue sounds, native Opus";
    }

    /// <summary>Headless accessibility audit of the dialogs that can be built without hardware: every
    /// actionable control announces a name to a screen reader, and the Alt-key mnemonic letters are
    /// unique within a container so keyboard navigation is never ambiguous. The main window can't be
    /// built headlessly (its constructor opens audio devices, registers hotkeys and binds sockets),
    /// so it's out of scope here. A dialog that won't construct in this context is skipped, not
    /// failed.</summary>
    private static string? AccessibilityAudit()
    {
        var factories = new (string Name, Func<Form> Make)[]
        {
            ("Recording settings", () => new RecordingSettingsDialog(new RecordingSettings())),
            ("Preferences", () => new PreferencesDialog(
                new RemSoundSettingsStore("RemSound"), null,
                () => false, _ => { }, () => { }, () => 0, () => { }, () => { }, _ => { },
                () => (default(RouterMappingStatus), (IPEndPoint?)null, ""),
                _ => { }, _ => { })),
            ("Service profile", () => new ServiceProfileDialog(RemSound.Core.Profile.NewBlank(), false)),
            ("About", () => new AboutDialog()),
            ("Add EQ band", () => new AddBandDialog()),
            ("Rename peer", () => new RenamePeerDialog("TestMachine", null)),
            ("Keyboard shortcut import", () => new KeyboardShortcutImportDialog(Array.Empty<string>())),
            ("Profile selection", () => new ProfileSelectionDialog(new ProfileStore(
                Path.Combine(Path.GetTempPath(), "remsound-selftest-picker-" + Guid.NewGuid().ToString("N"))))),
        };

        var audited = new List<string>();
        var skipped = new List<string>();
        var violations = new List<string>();

        foreach (var (name, make) in factories)
        {
            Form? form = null;
            try { form = make(); }
            catch (Exception ex) { skipped.Add($"{name} ({ex.GetType().Name})"); continue; }
            try { AuditForm(name, form, violations); audited.Add(name); }
            finally { try { form.Dispose(); } catch { /* ignore */ } }
        }

        if (audited.Count == 0) return Skip("no dialog could be constructed in this context");
        Check(violations.Count == 0, string.Join("; ", violations));
        var detail = $"audited {audited.Count} ({string.Join(", ", audited)})";
        if (skipped.Count > 0) detail += $"; skipped {skipped.Count}";
        return detail;
    }

    /// <summary>Constructs the ENTIRE main window in headless mode (no audio backend, no hotkeys, no
    /// timers, no sockets — see MainForm's `headless` flag) and audits every tab and control: accessible
    /// names present, Alt mnemonics unique per group, and the tab order forms no cycle. This is the
    /// "check all the tabs" coverage — the whole main-window surface, proven on every build.</summary>
    private static string? MainWindowCoverage()
    {
        Form? form;
        try { form = new MainForm(null, RemSound.Core.Profile.NewBlank(), null, null, headless: true); }
        catch (Exception ex) { return Skip($"headless MainForm could not be constructed: {ex.GetType().Name}: {ex.Message}"); }

        try
        {
            var violations = new List<string>();
            AuditForm("Main window", form, violations);
            Check(violations.Count == 0, string.Join("; ", violations));

            var tabs = CountControls(form, c => c is TabPage);
            var interactive = CountControls(form, c => c is CheckBox or Button or ComboBox or ListBox or TrackBar or TextBox);
            Check(tabs >= 3, $"the main window's tabs should be present (found {tabs})");
            Check(interactive >= 15, $"the main window's interactive controls should be present (found {interactive})");
            return $"audited the whole main window: {tabs} tabs, {interactive} interactive controls — names, mnemonics and tab order clean";
        }
        finally { try { form.Dispose(); } catch { /* ignore */ } }
    }

    /// <summary>Functional round-trip through the REAL main-window controls: apply a profile to the
    /// controls, read it back, and assert every persisted value survived — proving each control's load
    /// AND save logic, not just that it exists. Uses the headless form with no peers (so nothing tries
    /// to connect). Device ticks need real hardware ids so they're covered by the profile-store
    /// round-trip test instead; this covers the hardware-independent controls.</summary>
    private static string? MainWindowProfileRoundTrip()
    {
        MainForm mf;
        try { mf = new MainForm(null, RemSound.Core.Profile.NewBlank(), null, null, headless: true); }
        catch (Exception ex) { return Skip($"headless MainForm could not be constructed: {ex.GetType().Name}: {ex.Message}"); }

        using (mf)
        {
            var input = new Profile
            {
                Title = "roundtrip",
                Volume = 42,
                Muted = true,
                ReceiveAudioOn = true,
                SendAudioOn = true,
                EnableAllPeerShaping = true,
                WasapiSendMode = "applications",
                SendAllApplications = false,
            };
            input.SelectedSendApplications.Add("vlc");
            input.SelectedSendApplications.Add("firefox");

            var back = mf.ApplyThenCaptureForTest(input);

            Check(back.Volume == 42, $"volume must round-trip through the controls (got {back.Volume})");
            Check(back.Muted, "mute must round-trip through the controls");
            Check(back.ReceiveAudioOn && back.SendAudioOn, "send/receive toggles must round-trip");
            Check(back.EnableAllPeerShaping, "the peer-shaping master switch must round-trip");

            var covered = "volume, mute, send/receive, shaping";
            if (RemSound.Sender.ProcessLoopbackCapture.IsSupported)
            {
                Check(back.WasapiSendMode == "applications", $"send mode must round-trip (got {back.WasapiSendMode})");
                Check(!back.SendAllApplications, "the send-all-applications toggle must round-trip");
                Check(back.SelectedSendApplications.Contains("vlc") && back.SelectedSendApplications.Contains("firefox"),
                    "the selected applications must round-trip through the app list");
                covered += ", send-mode, apps";
            }
            return $"round-tripped through the real controls: {covered}";
        }
    }

    private static int CountControls(Control root, Func<Control, bool> predicate)
    {
        var n = 0;
        void Walk(Control p) { foreach (Control c in p.Controls) { if (predicate(c)) n++; Walk(c); } }
        Walk(root);
        return n;
    }

    private static void AuditForm(string formName, Form form, List<string> violations)
    {
        var all = new List<Control>();
        void Walk(Control parent) { foreach (Control c in parent.Controls) { all.Add(c); Walk(c); } }
        Walk(form);

        // Mnemonic uniqueness, per immediate container (the practical Alt-key scope).
        foreach (var group in all.Where(c => TryMnemonic(c.Text, out _)).GroupBy(c => c.Parent))
        {
            var counts = new Dictionary<char, int>();
            foreach (var c in group)
            {
                if (TryMnemonic(c.Text, out var letter))
                    counts[letter] = counts.TryGetValue(letter, out var n) ? n + 1 : 1;
            }
            foreach (var dup in counts.Where(kv => kv.Value > 1))
                violations.Add($"{formName}: Alt+{char.ToUpperInvariant(dup.Key)} is used by {dup.Value} controls in one group");
        }

        // Self-labelling controls (buttons, check boxes, radio buttons) must announce something.
        foreach (var c in all.Where(c => c is ButtonBase))
        {
            var name = !string.IsNullOrWhiteSpace(c.AccessibleName) ? c.AccessibleName : c.Text;
            if (string.IsNullOrWhiteSpace(name))
                violations.Add($"{formName}: a {c.GetType().Name} has no accessible name or text");
        }

        // Tab-order sanity: the GetNextControl walk must TERMINATE — a cycle would trap a keyboard /
        // screen-reader user pressing Tab forever. Guards against a malformed tab order.
        var guard = 0;
        var seen = new HashSet<Control>();
        for (Control? cur = form.GetNextControl(form, true); cur is not null && guard < 10000; cur = form.GetNextControl(cur, true))
        {
            guard++;
            if (!seen.Add(cur)) { violations.Add($"{formName}: tab order forms a cycle at {cur.GetType().Name}"); break; }
        }
        if (guard >= 10000) violations.Add($"{formName}: tab-order walk did not terminate");
    }

    /// <summary>Extract the Alt mnemonic letter from a WinForms caption ('&amp;X' marks X; '&amp;&amp;'
    /// is a literal ampersand). Returns false when there is no mnemonic.</summary>
    private static bool TryMnemonic(string? text, out char letter)
    {
        letter = '\0';
        if (string.IsNullOrEmpty(text)) return false;
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (text[i] != '&') continue;
            if (text[i + 1] == '&') { i++; continue; } // escaped "&&" is a literal ampersand
            letter = char.ToLowerInvariant(text[i + 1]);
            return char.IsLetterOrDigit(letter);
        }
        return false;
    }

    // ---------------- helper ----------------

    private static string? ValueAfter(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase) && !args[i + 1].StartsWith('-'))
                return args[i + 1];
        }
        return null;
    }
}
