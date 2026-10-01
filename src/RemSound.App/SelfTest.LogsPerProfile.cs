using RemSound.Core;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>LOGS PER PROFILE (Ed, 2026-09-29: "some profiles have it others do not. because a test with andre should always
    /// have it where as using with my laptop probably not"). A profile's choice is kept with it and in its file; a profile's
    /// window logs by its choice whatever this computer says, and this computer's setting follows it; a profile that never
    /// chose follows this computer's; the retired LoggingEnabled key in an old profile brings nothing back; a new profile
    /// (File, New profile) starts with logs off; the Preferences tick box changes the profile's, at once and unsaved.</summary>
    private static string? LogsPerProfile()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-logs-per-profile-" + Guid.NewGuid().ToString("N"));
        using (AppConfig.UseThrowawayUserDataDirectory(scratch))
        {
            void Computer(bool on) { var c = AppConfig.Load(); c.LoggingEnabled = on; c.Save(); }
            bool ComputerSays() => AppConfig.Load().LoggingEnabled;

            // Kept with the profile and in its file; the retired key brings nothing back.
            var store = new RemSoundSettingsStore("RemSound");
            var chose = Profile.NewBlank();
            chose.EnableLogs = true;
            store.ApplyProfile(chose);
            var back = new Profile();
            store.CopyTo(back);
            Check(back.EnableLogs == true, "a profile's logs must round-trip the profile");
            var reread = System.Text.Json.JsonSerializer.Deserialize<Profile>(System.Text.Json.JsonSerializer.Serialize(chose))!;
            Check(reread.EnableLogs == true, "and its file");
            var old = System.Text.Json.JsonSerializer.Deserialize<Profile>("""{ "Title": "July", "LoggingEnabled": true }""")!;
            Check(old.EnableLogs is null, "the retired LoggingEnabled key in an old profile must bring nothing back - it has not chosen");
            Check(Profile.NewForUser().EnableLogs == false && Profile.NewBlank().EnableLogs is null,
                "File, New profile starts with logs off; the blank template otherwise has not chosen");

            bool Window(Profile profile, out bool unsaved, Action<MainForm>? then = null)
            {
                using var form = new MainForm(null, profile, null, null, headless: true);
                then?.Invoke(form);
                unsaved = form.UnsavedChangesForTest;
                return form.LogForTest.Enabled;
            }

            // The profile's choice beats this computer's, and the computer follows.
            Computer(false);
            var andre = Profile.NewBlank();
            andre.EnableLogs = true;
            Check(Window(andre, out _), "a profile with logs on must log, with this computer's off");
            Check(ComputerSays(), "and this computer's setting must follow it, so the next start and the plugin log too");
            var laptop = Profile.NewBlank();
            laptop.EnableLogs = false;
            Check(!Window(laptop, out _), "a profile with logs off must not log, with this computer's on");
            Check(!ComputerSays(), "and this computer's setting must follow it");

            // Never chosen: this computer's, untouched.
            Computer(true);
            Check(Window(Profile.NewBlank(), out _), "a profile that never chose must follow this computer's setting (on)");
            Check(ComputerSays(), "and leave it as it was");
            Computer(false);
            Check(!Window(Profile.NewBlank(), out _), "and off");

            // The Preferences tick box: the profile's, at once, unsaved, and the computer follows.
            var ticked = Window(Profile.NewBlank(), out var unsaved, form =>
            {
                form.ApplyLoggingFromPreferencesForTest(true);
                Check(form.SettingsForTest.LoadEnableLogs() == true, "the tick box must change the profile's own choice");
            });
            Check(ticked && unsaved, "at once, and mark the profile unsaved");
            Check(ComputerSays(), "with this computer's setting following");
        }
        return "kept with the profile and in its file, the old key ignored; a profile's on or off beats this computer's, which "
             + "follows; never chosen follows this computer's; File, New profile starts off; the tick box changes the profile's at once";
    }
}
