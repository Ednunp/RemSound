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
        Check(!stale.IsClaimed(andre.Address, t0 + PluginPeerClaims.ClaimTimeout + TimeSpan.FromSeconds(1)),
            "a claim with no heartbeat must LAPSE — a dead DAW must never mute a peer permanently");

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

        return "claims are reference-counted, lapse without a heartbeat, and a claimed peer is provably absent from the device mix";
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

        var beforeClaim = PeakOfMix(engine, mixBuffer);
        Check(beforeClaim > 0.6f, $"both peers must start out audible on the speakers (peak {beforeClaim:0.000})");

        // --- The claim, over the real link ---------------------------------------------------------
        plugin.ReceiveFrom(andre.Address);
        var peak = PumpPlugin(plugin, frames: 256, rounds: 60);
        Check(peak > 0.3f, $"the claimed peer's audio must actually REACH the plugin - this is the plugin working or not ({peak:0.000})");
        Check(peak < 0.9f, $"and arrive at its own level, not clipped or doubled ({peak:0.000})");

        Check(host.Claims.IsClaimed(andre.Address), "asking for audio must claim the peer - the ask IS the claim");
        var afterClaim = PeakOfMix(engine, mixBuffer);
        Check(afterClaim < beforeClaim - 0.2f,
            $"THE DOUBLE-AUDIO TEST: with the plugin receiving Andre, Andre must be gone from the speakers (before {beforeClaim:0.000}, after {afterClaim:0.000})");
        Check(afterClaim > 0.05f, $"...but Chris, who nobody claimed, must still be playing ({afterClaim:0.000})");

        // --- Switching peers must let the old one go AT ONCE ----------------------------------------
        plugin.ReceiveFrom(chris.Address);
        PumpPlugin(plugin, frames: 256, rounds: 20);
        Check(!host.Claims.IsClaimed(andre.Address),
            "switching a track to another peer must release the first IMMEDIATELY - waiting for a timeout would leave them mute for five seconds with nothing to explain it");
        Check(host.Claims.IsClaimed(chris.Address), "and claim the new one");

        // --- A second instance on the same peer ------------------------------------------------------
        using (var second = new PluginBridgeClient(host.Port))
        {
            second.Hello();
            second.ReceiveFrom(chris.Address);
            var secondPeak = PumpPlugin(second, frames: 256, rounds: 40);
            Check(secondPeak > 0.05f, $"a second plugin on the SAME peer must also get audio ({secondPeak:0.000})");
            Check(host.Claims.ClaimCount(chris.Address) == 2,
                "both instances must be counted, so one closing does not un-mute the peer under the other");
        }

        // --- Letting go: the peer returns to the speakers ----------------------------------------------
        plugin.ReceiveFrom(null);
        Check(WaitUntil(() => !host.Claims.IsClaimed(chris.Address)),
            "leaving a peer must reach the app - otherwise it stays mute in RemSound until a timeout the user cannot see");
        host.SweepForTest();
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
            orphan.ReceiveFrom(andre.Address);
            var quiet = PumpPlugin(orphan, frames: 256, rounds: 10);
            Check(quiet == 0f, "with no app listening the plugin must produce silence, not noise");
            Check(!orphan.Connected, "and must report that it is not connected, so the status line can say so plainly");
            Check(orphan.StarvedBlocks > 0, "starved blocks must be counted - a rising count is what tells a user the app is not keeping up");
        }

        return "peer list, claim, audio and release all cross the real link; a claimed peer provably leaves the speakers "
             + "and an unclaimed one stays; switching releases at once; two instances share a peer; with no app the plugin goes silent and says so";
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
            catch (Exception ex) { return Skip($"headless MainForm could not be constructed: {ex.GetType().Name}: {ex.Message}"); }

            var strip = form.MainMenuStrip ?? FindMenuStrip(form);
            Check(strip is not null, "the main window must have a menu strip");
            var menu = strip!.Items.OfType<ToolStripMenuItem>()
                .FirstOrDefault(m => m.AccessibleName == "DAW plugin menu");
            Check(menu is not null, "there must be a DAW plugin menu — the plugin is its own way of using RemSound, not a variation on the service");

            var items = menu!.DropDownItems.OfType<ToolStripMenuItem>().ToList();
            var install = items.FirstOrDefault(i => i.AccessibleName == "Install plugin");
            var remove = items.FirstOrDefault(i => i.AccessibleName == "Remove plugin");
            var shaping = items.FirstOrDefault(i => i.AccessibleName == "Apply pan and EQ to plugin audio");
            Check(install is not null, "the menu must offer Install");
            Check(remove is not null, "the menu must offer Remove");
            Check(shaping is not null, "the menu must carry the pan/EQ choice");
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
            }
            return "DAW plugin menu present with install, remove and the pan/EQ tick; the menu states what is actually installed; the setting round-trips and defaults to on";
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
        PluginBridgeProtocol.WriteHeader(buffer, PluginBridgeMessage.ClaimPeer, instance, peer, 8);
        Check(PluginBridgeProtocol.TryReadHeader(buffer.AsSpan(0, PluginBridgeProtocol.HeaderSize + 8),
                out var type, out var hash, out var readPeer, out var len),
            "a well-formed bridge message must parse");
        Check(type == PluginBridgeMessage.ClaimPeer && hash == instance && len == 8, "every header field must round-trip");
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
            "a length that overruns the buffer must be rejected, not read past the end");

        // --- The live link, and the latency it costs ----------------------------------------------
        using var host = new PluginBridgeLink(0);   // port 0: never collide with a running RemSound
        using var client = new PluginBridgeLink(0);
        var hostEnd = new IPEndPoint(IPAddress.Loopback, host.Port);

        // The host echoes a track block straight back as peer audio - a full ROUND TRIP, which is
        // strictly more than the real path costs (that is one hop each way, not two).
        host.MessageReceived += (t, h, p, payload, from) =>
        {
            if (t == PluginBridgeMessage.TrackAudio) host.Send(from, PluginBridgeMessage.PeerAudio, h, p, payload.Span);
        };

        var returned = new SemaphoreSlim(0);
        var receivedBytes = 0;
        client.MessageReceived += (t, _, _, payload, _) =>
        {
            if (t != PluginBridgeMessage.PeerAudio) return;
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
            return "installs per-user with no admin; nested files placed; reinstall refreshes; uninstall removes only its own files and leaves other plugins untouched";
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    /// <summary>Write a steady tone into a session so the mix has something measurable in it.</summary>
    private static void FillSession(SessionPlayout session, float amplitude)
    {
        var block = new byte[48000 * 8 / 4]; // 250 ms stereo float
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(block.AsSpan());
        for (var i = 0; i < floats.Length; i++) floats[i] = amplitude;
        session.Write(block);
        session.NoteFramesQueued(30);
    }

    /// <summary>Fill a session with a real tone rather than a flat level, so a test can measure what
    /// came out the far end and catch a rate conversion that transposed it.</summary>
    private static void FillSessionWithTone(SessionPlayout session, double toneHz, float amplitude)
    {
        const int Seconds = 4;   // plenty for the pump to drink from without running dry mid-measurement
        var block = new byte[48000 * 8 * Seconds];
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(block.AsSpan());
        var step = 2 * Math.PI * toneHz / 48000;
        for (var frame = 0; frame < floats.Length / 2; frame++)
        {
            var v = (float)(Math.Sin(frame * step) * amplitude);
            floats[frame * 2] = v;
            floats[frame * 2 + 1] = v;
        }
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
