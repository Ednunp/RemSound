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
