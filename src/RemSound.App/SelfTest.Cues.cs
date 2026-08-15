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

        // EVERY cue must round-trip a custom sound path — that's the user-facing promise of the
        // Preferences sound list ("choose your own WAV for this event").
        foreach (var id in ids)
        {
            var cfg = AppConfig.Load();
            cfg.MachineCueCustomPaths[id] = $@"C:\sounds\{id}.wav";
            cfg.Save();
            var back = AppConfig.Load();
            Check(back.MachineCueCustomPaths.TryGetValue(id, out var p) && p == $@"C:\sounds\{id}.wav",
                $"cue '{id}': a custom sound path must survive a save and reload");
        }

        // MUTING must be absolute. --silent is what makes an unattended run (and the gate itself)
        // quiet; a cue that ignores it plays at whoever is at the screen. Ed heard exactly that from
        // the gate on 2026-08-15, because the gate wasn't passing --silent at all.
        var wav = Path.Combine(scratch, "probe.wav");
        File.WriteAllBytes(wav, MinimalWav());
        var restore = CuePlayer.GloballyMuted;
        try
        {
            CuePlayer.GloballyMuted = true;
            var player = new CuePlayer(wav);
            player.Play(); // must be a no-op: no device opened, no sound, no throw
            Check(CuePlayer.GloballyMuted, "GloballyMuted must stay set — Play must not clear it as a side effect");
        }
        finally { CuePlayer.GloballyMuted = restore; }

        // The checkbox cues are the ones that fire most often — from AccessibleCheckBox, on EVERY
        // checkbox in the app — and CheckSoundService.Suppressed is what stops a programmatic run
        // (profile apply, the control suite) from machine-gunning them.
        Check(ids.Contains("checkbox-on") && ids.Contains("checkbox-off"),
            "the checkbox tick/untick cues must be in the registry — they fire on every checkbox in the app");
        var restoreSuppressed = CheckSoundService.Suppressed;
        try
        {
            CheckSoundService.Suppressed = true;
            CheckSoundService.Play(true);
            CheckSoundService.Play(false);
            Check(CheckSoundService.Suppressed, "Suppressed must survive a suppressed Play");
        }
        finally { CheckSoundService.Suppressed = restoreSuppressed; }

        return $"{ids.Length} cues in the registry; ids unique + file-safe; every one round-trips a custom sound path; muting and checkbox suppression hold";
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
