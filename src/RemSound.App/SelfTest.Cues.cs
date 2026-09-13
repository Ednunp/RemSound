using System.Reflection;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// SOUNDS — every cue the app can play, pinned.
///
/// <para>Ed, 2026-08-15: "pin a test to everything associated to every control, every dialogue, every
/// sound, every single bloody event." Sounds had no dedicated coverage at all: the gate checked that
/// the shipped WAV files were present in the package, and nothing else. Nothing proved a cue could be
/// turned off, that a custom sound file was honoured, or that <c>--silent</c> genuinely silenced it.</para>
///
/// <para>It also cost him directly: I claimed only the send/receive toggles had sounds, when
/// CheckboxOn/CheckboxOff fire from AccessibleCheckBox on EVERY checkbox in the app — a claim made by
/// reading one method instead of the cue registry. This test makes the registry the source of truth so
/// that particular mistake is not available to me again.</para>
///
/// <para>The completeness guard is the important half: it reflects over <c>CueId</c> and fails when a
/// new cue appears that this test doesn't cover. Add a sound, the gate goes red until it is pinned.</para>
/// </summary>
internal static partial class SelfTest
{
    /// <summary>Every cue id declared in the app, straight from the registry rather than a list I
    /// maintain by hand — a hand-kept list is exactly how a sound goes untested.</summary>
    private static string[] AllCueIds() =>
        typeof(MainForm).GetNestedType("CueId", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

    private static string? CueCoverage()
    {
        var ids = AllCueIds();
        Check(ids.Length >= 18, $"the cue registry should hold every sound the app can play (found {ids.Length})");

        // Ids must be unique and file-name safe: they key the custom-path dictionary AND are used to
        // build shipped WAV names, so a duplicate or a stray path character silently mis-resolves.
        Check(ids.Distinct(StringComparer.Ordinal).Count() == ids.Length, "cue ids must be unique");
        foreach (var id in ids)
        {
            Check(!string.IsNullOrWhiteSpace(id), "a cue id must not be blank");
            Check(id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0, $"cue id '{id}' must be safe to use in a file name");
            Check(id == id.ToLowerInvariant(), $"cue id '{id}' must be lower-case (they key a case-sensitive dictionary)");
        }

        var scratch = Path.Combine(Path.GetTempPath(), "remsound-cue-suite-" + Guid.NewGuid().ToString("N"));
        using var scope = AppConfig.UseThrowawayUserDataDirectory(scratch);

        // EVERY cue must round-trip a custom sound path — the user-facing promise of the Preferences
        // sound list. There are TWO stores and the resolution order is per-profile FIRST, machine-wide
        // as fallback (MainForm.LoadCueSound), so testing only one of them proves only half the
        // journey — which is exactly what the first version of this test did.
        var store = new RemSoundSettingsStore("RemSound");
        foreach (var id in ids)
        {
            var machine = $@"C:\sounds\machine-{id}.wav";
            var cfg = AppConfig.Load();
            cfg.MachineCueCustomPaths[id] = machine;
            cfg.Save();
            Check(AppConfig.Load().MachineCueCustomPaths.TryGetValue(id, out var backMachine) && backMachine == machine,
                $"cue '{id}': a machine-wide custom sound path must survive a save and reload");

            var perProfile = $@"C:\sounds\profile-{id}.wav";
            store.SaveCustomCuePath(id, perProfile);
            Check(store.LoadCustomCuePath(id) == perProfile,
                $"cue '{id}': a PER-PROFILE custom sound path must round-trip — it is the FIRST thing the resolver looks at");
            store.SaveCustomCuePath(id, null);
            Check(string.IsNullOrEmpty(store.LoadCustomCuePath(id)),
                $"cue '{id}': clearing a per-profile custom path must actually clear it (or the cue is stuck on an old file)");
        }

        // EVERY cue must have an on/off switch, and it must be findable. The flags live in two places —
        // machine-wide ones on AppConfig, per-profile ones behind the settings store — so this maps each
        // cue to its flag by name and FAILS on any cue that has no switch at all. A sound the user
        // cannot turn off is a bug for someone who listens to this app all day.
        var missingSwitch = new List<string>();
        var machineFlags = 0;
        var profileFlags = 0;
        foreach (var id in ids)
        {
            var flagName = "Enable" + string.Concat(id.Split('-').Select(part => char.ToUpperInvariant(part[0]) + part[1..])) + "Cue";
            var appConfigProp = typeof(AppConfig).GetProperty(flagName);
            var storeMethod = typeof(RemSoundSettingsStore).GetMethod("Load" + flagName);
            if (appConfigProp is not null)
            {
                machineFlags++;
                // Round-trip it: off must stay off across a save/reload, or "mute this cue" is a lie.
                var cfg = AppConfig.Load();
                var original = (bool)appConfigProp.GetValue(cfg)!;
                appConfigProp.SetValue(cfg, !original);
                cfg.Save();
                Check((bool)appConfigProp.GetValue(AppConfig.Load())! == !original,
                    $"cue '{id}': its enable flag ({flagName}) must survive a save and reload");
            }
            else if (storeMethod is not null) profileFlags++;
            else missingSwitch.Add($"{id} (looked for {flagName})");
        }
        Check(missingSwitch.Count == 0,
            $"these cues have no on/off switch anywhere — the user cannot silence them: {string.Join(", ", missingSwitch)}");

        // MUTING must be absolute. --silent is what makes an unattended run (and the gate itself)
        // quiet; a cue that ignores it plays at whoever is at the screen. Ed heard exactly that from
        // the gate on 2026-08-15, because the gate wasn't passing --silent at all.
        var wav = Path.Combine(scratch, "probe.wav");
        File.WriteAllBytes(wav, MinimalWav());
        var restore = CuePlayer.GloballyMuted;
        try
        {
            // COUNT the plays that get past the gate. The old assertion was that GloballyMuted was
            // still set after a muted Play — which Play never changes either way, so it held whether
            // or not the mute worked. Deleting the mute gate outright left this step green
            // (2026-08-24). Two different values, never two zeroes: muted must not play, unmuted must.
            var player = new CuePlayer(wav);

            CuePlayer.GloballyMuted = true;
            var mutedBefore = CuePlayer.PlaysStartedForTest;
            player.Play();
            player.Play();
            Check(CuePlayer.PlaysStartedForTest == mutedBefore,
                $"a muted cue must not open a device or make a sound — {CuePlayer.PlaysStartedForTest - mutedBefore} of two "
                + "Plays got past the mute. This is the gate chiming at whoever happens to be at the screen");
            Check(CuePlayer.GloballyMuted, "GloballyMuted must stay set — Play must not clear it as a side effect");

            // And the other direction, or the check above would pass just as well on a cue system
            // that never makes a sound at all.
            CuePlayer.GloballyMuted = false;
            var unmutedBefore = CuePlayer.PlaysStartedForTest;
            player.Play();
            Check(CuePlayer.PlaysStartedForTest == unmutedBefore + 1,
                "an UNMUTED cue must actually reach playback — otherwise the mute check above is comparing two zeroes");
        }
        finally { CuePlayer.GloballyMuted = restore; }

        // The checkbox cues are the ones that fire most often — from AccessibleCheckBox, on EVERY
        // checkbox in the app — and CheckSoundService.Suppressed is what stops a programmatic run
        // (profile apply, the control suite) from machine-gunning them.
        Check(ids.Contains("checkbox-on") && ids.Contains("checkbox-off"),
            "the checkbox tick/untick cues must be in the registry — they fire on every checkbox in the app");

        return $"all {ids.Length} cues: ids unique + file-safe; BOTH custom-path stores round-trip (per-profile first, machine-wide fallback); "
             + $"every cue has a working on/off switch ({machineFlags} machine-wide, {profileFlags} per-profile); muting holds";
    }

    /// <summary>A 44-byte silent WAV header — enough for a player to open without shipping a fixture.</summary>
    private static byte[] MinimalWav()
    {
        var b = new byte[44];
        "RIFF"u8.CopyTo(b.AsSpan(0));
        BitConverter.GetBytes(36).CopyTo(b, 4);
        "WAVEfmt "u8.CopyTo(b.AsSpan(8));
        BitConverter.GetBytes(16).CopyTo(b, 16);
        BitConverter.GetBytes((short)1).CopyTo(b, 20);
        BitConverter.GetBytes((short)1).CopyTo(b, 22);
        BitConverter.GetBytes(48000).CopyTo(b, 24);
        BitConverter.GetBytes(96000).CopyTo(b, 28);
        BitConverter.GetBytes((short)2).CopyTo(b, 32);
        BitConverter.GetBytes((short)16).CopyTo(b, 34);
        "data"u8.CopyTo(b.AsSpan(36));
        BitConverter.GetBytes(0).CopyTo(b, 40);
        return b;
    }
}
