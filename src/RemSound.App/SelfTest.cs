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
        RunStep(results, "v5 settings and shaping round-trip", V5ConfigRoundTrip);
        RunStep(results, "Profile save and reload", ProfileRoundTrip);
        RunStep(results, "What's-new update marker", WhatsNewMarkerRoundTrip);
        RunStep(results, "Diagnostics report privacy", DiagnosticsPrivacy);
        RunStep(results, "Bundled resources present", ResourcesPresent);
        RunStep(results, "Dialog accessibility (names + mnemonics)", AccessibilityAudit);

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
