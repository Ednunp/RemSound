using RemSound.Core;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>CUES, AS THE WINDOW PLAYS THEM (2026-09-28, from the review of every step). The cue registry step pins the
    /// ids, the stores and the global mute; it never asked the window which file a cue plays or whether a cue switched off
    /// stays quiet. Here: a profile's own sound for a cue wins over the machine's, which wins over the shipped default; and
    /// the save cue, switched off in the profile, plays nothing on a save where switched on it plays once - counted, with
    /// the sound kept off every device.</summary>
    private static string? CuesPlayTheChosenSoundOrNothing()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-cues-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var perProfile = Path.Combine(scratch, "profile connect.wav");
        var perMachine = Path.Combine(scratch, "machine connect.wav");
        File.WriteAllBytes(perProfile, [0]);
        File.WriteAllBytes(perMachine, [0]);
        var restoreMuted = CuePlayer.GloballyMuted;
        var restoreNoDevice = CuePlayer.NoDeviceForTest;
        var config = AppConfig.Load();
        var restoreMachine = config.MachineCueCustomPaths.TryGetValue(MainForm.CueId.Connect, out var m) ? m : null;
        var said = new List<string>();
        try
        {
            config.MachineCueCustomPaths[MainForm.CueId.Connect] = perMachine;
            config.Save();
            string? Plays(Profile profile)
            {
                using var form = new MainForm(null, profile, null, null, headless: true);
                return FieldOf<CuePlayer>(form, "connectSound")?.FilePathForTest;
            }

            var both = Profile.NewBlank();
            both.CustomCuePaths[MainForm.CueId.Connect] = perProfile;
            Check(Plays(both) == perProfile, $"with a sound of the profile's own and one for the machine, the profile's must play ({Plays(both)})");
            Check(Plays(Profile.NewBlank()) == perMachine, $"with only the machine's, the machine's must play ({Plays(Profile.NewBlank())})");
            config.MachineCueCustomPaths.Remove(MainForm.CueId.Connect);
            config.Save();
            var shipped = Plays(Profile.NewBlank());
            Check(shipped is not null && shipped != perMachine && shipped != perProfile && File.Exists(shipped),
                $"with neither, the shipped sound must play ({shipped ?? "nothing"})");
            said.Add("the profile's own sound, then the machine's, then the shipped one");

            // The save cue, off and on, through File > Save.
            CuePlayer.NoDeviceForTest = true;
            CuePlayer.GloballyMuted = false;
            foreach (var on in new[] { false, true })
            {
                var dir = Path.Combine(scratch, on ? "on" : "off");
                Directory.CreateDirectory(dir);
                var store = new ProfileStore(dir);
                var profile = Profile.NewBlank();
                profile.Title = "Cue " + (on ? "on" : "off");
                profile.Password = RemSoundCrypto.Obfuscate("a password for the gate");
                profile.EnableSaveCue = on;
                store.Save(profile);
                using var form = new MainForm(store, profile, profile.Title, null, headless: true);
                form.RememberSaveConfirmationSuppressedForTest();
                form.MarkProfileDirtyForTest();
                var before = CuePlayer.PlaysStartedForTest;
                var save = Require(form.MainMenuStrip?.Items.OfType<ToolStripMenuItem>().SelectMany(i => i.DropDownItems.OfType<ToolStripMenuItem>())
                    .FirstOrDefault(i => i.AccessibleName == "Save profile"), "the File menu's Save item");
                save.PerformClick();
                var played = CuePlayer.PlaysStartedForTest - before;
                Check(!form.UnsavedChangesForTest, "premise: the profile must have saved");
                Check(played == (on ? 1 : 0),
                    on ? $"switched on, the save cue must play once on a save ({played})" : $"SWITCHED OFF, the save cue must play nothing on a save ({played} played)");
            }
            said.Add("the save cue switched off plays nothing, switched on plays once");
            return string.Join("; ", said);
        }
        finally
        {
            CuePlayer.GloballyMuted = restoreMuted;
            CuePlayer.NoDeviceForTest = restoreNoDevice;
            var restore = AppConfig.Load();
            if (restoreMachine is null) restore.MachineCueCustomPaths.Remove(MainForm.CueId.Connect);
            else restore.MachineCueCustomPaths[MainForm.CueId.Connect] = restoreMachine;
            try { restore.Save(); } catch { /* the run's own settings folder */ }
            try { Directory.Delete(scratch, recursive: true); } catch { /* temp */ }
        }
    }
}
