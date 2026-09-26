using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Two ways the 2026-09-25 sweep found a profile could be lost or lost track of (Ed: "yes fix those 2").
/// <list type="bullet">
/// <item>A switch to a profile that could not be read opened a blank profile under that profile's own title - and a window
/// with that title works out its file from it, so the next save, auto-save or "Yes" at exit wrote the blank settings, and
/// an empty password, over the real file.</item>
/// <item>Rename moved the file but left the old title inside it, which is what every list reads: the picker, quick switch
/// and "Start with a specific profile" named a file that no longer existed.</item>
/// </list>
/// </summary>
internal static partial class SelfTest
{
    private static string? AProfileIsNeverSavedOverOrLostByRenaming()
    {
        var dir = Path.Combine(AppConfig.UserDataDirectory, "profile-safety-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var store = new ProfileStore(dir);
        var cfgBefore = AppConfig.Load();
        var startWithBefore = cfgBefore.StartWithProfileTitle;
        var recentBefore = cfgBefore.RecentProfiles.ToList();
        MainForm? form = null;
        try
        {
            // 1. A profile that can't be read: blank, untitled, no file - and the file is left exactly as it was.
            var gig = Profile.NewBlank();
            gig.Title = "Gig";
            gig.Password = RemSoundCrypto.Obfuscate("gig-password");
            store.Save(gig);
            var gigPath = store.PathFor("Gig");
            var good = Program.ReadProfileForSwitch(gigPath, "Gig");
            Check(good.Error is null && good.Title == "Gig" && good.Path == gigPath, "premise: a readable profile is read, under its title and file");

            File.WriteAllText(gigPath, "{ this is not a profile");
            var bad = Program.ReadProfileForSwitch(gigPath, "Gig");
            Check(bad.Error is not null && bad.Title is null && bad.Path is null,
                $"THE OVERWRITE: a profile that can't be read must become a blank profile with NO title and NO file - under its title, the next save wrote over it (title: {bad.Title ?? "none"}, file: {bad.Path ?? "none"})");

            try { form = new MainForm(store, bad.Profile, bad.Title, bad.Path, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            var path = FieldOf<string>(form, "currentProfilePath");
            Check(path is null, $"and the window it opens must have no file to save to (it has {path})");
            form.Dispose();
            form = null;

            // The switch says so in the next window's log.
            var lines = new List<string>();
            MainForm.LineForNextLog = "profile switch: could not read \"Gig\" (gate) - opened a new, blank, untitled profile instead";
            try { form = new MainForm(store, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            Check(MainForm.LineForNextLog is null, "THE LOG: the next window must take the line the switch left for it, once");
            form.Dispose();
            form = null;

            // 2. Rename: the file, the title inside it, the start-up choice and the recent list all follow.
            var band = Profile.NewBlank();
            band.Title = "Gig";
            store.Save(band);
            var cfg = AppConfig.Load();
            cfg.StartWithProfileTitle = "Gig";
            cfg.RecentProfiles.Insert(0, gigPath);
            cfg.Save();
            try { form = new MainForm(store, band, "Gig", gigPath, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            form.LogForTest.EventTapForTest = line => { lock (lines) lines.Add(line); };
            form.RenameCurrentProfileToForTest("Band");
            var bandPath = store.PathFor("Band");
            Check(File.Exists(bandPath) && !File.Exists(gigPath), "renaming must move the profile to its new file");
            Check(store.ListProfileTitles().Contains("Band") && !store.ListProfileTitles().Contains("Gig"),
                $"THE TITLE: every list must now offer it under its new name (they offer: {string.Join(", ", store.ListProfileTitles())})");
            Check(store.Load("Band") is { Title: "Band" }, "and choosing it by that name must open it");
            var after = AppConfig.Load();
            Check(after.StartWithProfileTitle == "Band", $"\"Start with a specific profile\" must follow the rename (it names \"{after.StartWithProfileTitle}\")");
            Check(after.RecentProfiles.Contains(bandPath) && !after.RecentProfiles.Contains(gigPath), "and so must the recent profiles");
            lock (lines)
                Check(lines.Any(l => l.Contains("renamed profile \"Gig\"", StringComparison.Ordinal)) && lines.Any(l => l.Contains("now name \"Band\"", StringComparison.Ordinal)),
                    "the rename, and what followed it, must be logged");
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            MainForm.LineForNextLog = null;
            var cfg = AppConfig.Load();
            cfg.StartWithProfileTitle = startWithBefore;
            cfg.RecentProfiles.Clear();
            cfg.RecentProfiles.AddRange(recentBefore);
            cfg.Save();
            try { Directory.Delete(dir, recursive: true); } catch { /* the run's own folder */ }
        }
        return "a profile that couldn't be read opened blank, untitled and with no file, and nothing was written over it; the next window "
             + "logged why; a rename moved the file with its title inside it, so every list offered the new name, and the start-up "
             + "choice and the recent list followed it";
    }
}
