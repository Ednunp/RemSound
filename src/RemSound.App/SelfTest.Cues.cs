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

        // EVERY cue is the profile's (2026-10-08, Ed: "all sounds should be switchable for all profiles"): its own file, its
        // chosen built-in sound and its on/off switch must each survive the profile being saved and opened again - through
        // the settings store, the Profile and its JSON, which is the journey a real save and load takes. Testing the store
        // alone would prove half of it, which is what the first version of this test did with its two stores.
        var store = new RemSoundSettingsStore("RemSound");
        var backup = new Profile();
        store.CopyTo(backup);
        var missingSwitch = new List<string>();
        try
        {
            foreach (var id in ids)
            {
                var own = $@"C:\sounds\profile-{id}.wav";
                store.SaveCustomCuePath(id, own);
                store.SaveCueSound(id, $"{id} 2.wav");
                store.SaveCueEnabled(id, false);
                var saved = new Profile();
                store.CopyTo(saved);
                var reopened = System.Text.Json.JsonSerializer.Deserialize<Profile>(System.Text.Json.JsonSerializer.Serialize(saved))!;
                var fresh = new RemSoundSettingsStore("RemSound");
                fresh.ApplyProfile(reopened);
                // A profile's own cue rides in the profile; a global sound on the computer - either way it must come back.
                Check(fresh.LoadCustomCuePath(id) == own,
                    $"cue '{id}': its own sound file must survive the profile being saved and opened ({fresh.LoadCustomCuePath(id) ?? "lost"})");
                Check(fresh.LoadCueSound(id) == $"{id} 2.wav",
                    $"cue '{id}': its chosen built-in sound must survive the profile being saved and opened ({fresh.LoadCueSound(id) ?? "lost"})");
                if (fresh.LoadCueEnabled(id)) missingSwitch.Add(id);
                store.SaveCustomCuePath(id, null);
                Check(string.IsNullOrEmpty(store.LoadCustomCuePath(id)),
                    $"cue '{id}': clearing its own file must actually clear it (or the cue is stuck on an old file)");
                store.SaveCueEnabled(id, true);
                Check(store.LoadCueEnabled(id), $"cue '{id}': switching it back on must stick");
            }
        }
        finally { store.ApplyProfile(backup); }
        // A sound the user cannot turn off is a bug for someone who listens to this app all day.
        Check(missingSwitch.Count == 0,
            $"these cues came back ON after being switched off and the profile saved and opened - the user cannot silence them: {string.Join(", ", missingSwitch)}");

        // And the computer's older settings must not reach a profile that carries its own: with the send-on cue switched
        // off in this computer's old settings, a profile that has it on must keep it on.
        {
            var cfg = AppConfig.Load();
            cfg.EnableSendOnCue = false;
            cfg.Save();
            var carries = new Profile { Cues = new CueSettings() };
            var applied = new RemSoundSettingsStore("RemSound");
            applied.ApplyProfile(carries);
            Check(applied.LoadCueEnabled(MainForm.CueId.SendOn),
                "a profile that carries its own cues must not take an older computer-wide 'off' - that is the split this change ended");
            cfg.EnableSendOnCue = true;
            cfg.Save();
        }

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

        // "Mute all audio cues" (2026-10-08): the person's own switch, separate from the silent run's. Muted, no cue may
        // reach a device; a preview the person asked for on the Audio cues tab still plays; unmuted, a cue plays again.
        // Counted both ways, for the reason above.
        var restoreAll = CuePlayer.AllCuesMuted;
        var restoreGlobal = CuePlayer.GloballyMuted;
        try
        {
            CuePlayer.GloballyMuted = false;
            var player = new CuePlayer(wav);
            CuePlayer.AllCuesMuted = true;
            var before = CuePlayer.PlaysStartedForTest;
            player.Play();
            Check(CuePlayer.PlaysStartedForTest == before, "with every cue muted in Preferences, a cue must not reach a device");
            new CuePlayer(wav) { Preview = true }.Play();
            Check(CuePlayer.PlaysStartedForTest == before + 1, "a preview the person asked for must still play with every cue muted");
            Check(!CuePlayer.GloballyMuted, "muting every cue must not turn on the silent-run flag, which also stops questions and update checks");
            CuePlayer.AllCuesMuted = false;
            player.Play();
            Check(CuePlayer.PlaysStartedForTest == before + 2, "unmuted again, the cue must play");
        }
        finally { CuePlayer.AllCuesMuted = restoreAll; CuePlayer.GloballyMuted = restoreGlobal; }

        // The checkbox cues are the ones that fire most often — from AccessibleCheckBox, on EVERY
        // checkbox in the app — and CheckSoundService.Suppressed is what stops a programmatic run
        // (profile apply, the control suite) from machine-gunning them.
        Check(ids.Contains("checkbox-on") && ids.Contains("checkbox-off"),
            "the checkbox tick/untick cues must be in the registry — they fire on every checkbox in the app");

        return $"all {ids.Length} cues: ids unique + file-safe; each cue's own file, chosen sound and on/off switch survive a profile save and "
             + "open; the computer's older settings don't reach a profile that carries its own; the silent run and 'Mute all audio cues' both hold";
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
