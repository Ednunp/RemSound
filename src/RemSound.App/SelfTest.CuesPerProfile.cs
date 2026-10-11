using System.Collections.Concurrent;
using System.Net;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// EVERY CUE THE PROFILE'S, AND ONE SWITCH TO MUTE THEM ALL (2026-10-08). Ed: "all sounds should be switchable for all
/// profiles. and the toggle should mute for all profiles", and "think about how people updating will work, my guess is
/// whatever they have will carry across into profiles but again you need to test that as much as possible".
/// </summary>
internal static partial class SelfTest
{
    /// <summary>UPDATING: a profile an older RemSound saved, opened by this one, sounds exactly as it did - its own eight
    /// cues as it saved them, everything else from the computer's older settings - and saved again, carries all of it
    /// itself, with none of the older fields. Whichever older profile is opened first, and however the others have been
    /// changed since, each gets the same answer from the computer's older settings.</summary>
    private static string? CuesCarryIntoEveryProfileOnUpdate()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-cues-update-" + Guid.NewGuid().ToString("N"));
        using var userData = AppConfig.UseThrowawayUserDataDirectory(scratch);

        // What an older RemSound kept on this computer, for every profile.
        var cfg = AppConfig.Load();
        cfg.EnableSendOnCue = false;
        cfg.EnableCheckboxOffCue = false;
        cfg.EnableStartupCue = false;
        cfg.EnableHelpCloseCue = false;
        cfg.EnableKeyboardClicks = false;
        cfg.MachineCueCustomPaths["tab-switch"] = @"C:\computer\tab.wav";
        cfg.MachineCueCustomPaths["send-off"] = @"C:\computer\send off.wav";
        cfg.StartupCueCustomPath = @"C:\computer\start.wav";
        cfg.DefaultCueSounds["connect"] = "connect 2.wav";
        cfg.DefaultCueSounds["help-open"] = "help open 3.wav";
        cfg.Save();

        // A profile exactly as an older RemSound wrote it: the old combined connection mute, disconnect switched back on
        // by its own switch, the save cue off, and two sounds of its own - one for a cue whose computer-wide file it
        // overrides.
        const string olderProfile = """
            {
              "Title": "Older",
              "MuteConnectionCues": true,
              "EnableDisconnectCue": true,
              "EnableSaveCue": false,
              "CustomCuePaths": { "connect": "C:\\old\\connect.wav", "tab-switch": "C:\\old\\tab.wav" }
            }
            """;
        var expectedOff = new HashSet<string> { "connect", "save", "send-on", "checkbox-off", "startup", "help-close" };
        var expectedOwn = new Dictionary<string, string>
        {
            ["connect"] = @"C:\old\connect.wav",
            ["tab-switch"] = @"C:\old\tab.wav",        // the profile's own, over the computer's
            ["send-off"] = @"C:\computer\send off.wav",
            ["startup"] = @"C:\computer\start.wav",
        };
        var expectedSound = new Dictionary<string, string> { ["connect"] = "connect 2.wav", ["help-open"] = "help open 3.wav" };

        void AsBefore(RemSoundSettingsStore store, string when)
        {
            foreach (var id in AllCueIds())
            {
                Check(store.LoadCueEnabled(id) == !expectedOff.Contains(id),
                    $"{when}: the {id} cue must be {(expectedOff.Contains(id) ? "off" : "on")}, as the older RemSound had it with this profile");
                expectedOwn.TryGetValue(id, out var own);
                Check(store.LoadCustomCuePath(id) == own,
                    $"{when}: the {id} cue's own sound must be {own ?? "none"} ({store.LoadCustomCuePath(id) ?? "none"})");
                expectedSound.TryGetValue(id, out var sound);
                Check(store.LoadCueSound(id) == sound,
                    $"{when}: the {id} cue's chosen sound must be {sound ?? "its first"} ({store.LoadCueSound(id) ?? "its first"})");
            }
            Check(!store.LoadKeyboardClicks(), $"{when}: the keyboard clicks must be off, as they were on this computer");
        }

        var older = System.Text.Json.JsonSerializer.Deserialize<Profile>(olderProfile)!;
        var opened = new RemSoundSettingsStore("RemSound");
        opened.ApplyProfile(older);
        AsBefore(opened, "OPENED AFTER THE UPDATE");

        // Saved: it carries everything itself, and none of the older fields.
        var saved = new Profile();
        opened.CopyTo(saved);
        var json = System.Text.Json.JsonSerializer.Serialize(saved);
        Check(json.Contains("\"Cues\"", StringComparison.Ordinal), "SAVED: the profile must carry its cues");
        Check(saved.Cues is { } carried && CueSettings.GlobalCueIds.All(g => !carried.Enabled.ContainsKey(g) && !carried.CustomPaths.ContainsKey(g) && !carried.Sounds.ContainsKey(g)),
            "SAVED: the global sounds are this computer's, so the profile must not carry them");
        Check(AppConfig.Load().GlobalCues is { } global && !global.IsOn(MainForm.CueId.Save) && global.IsOn(MainForm.CueId.Update),
            "THE GLOBAL SOUNDS: the first profile opened must fill them from what it played - this one's save sound was off, its update sound on");
        foreach (var gone in new[] { "MuteConnectionCues", "EnableConnectCue", "EnableDisconnectCue", "EnableSaveCue", "CustomCuePaths" })
            Check(!json.Contains($"\"{gone}\"", StringComparison.Ordinal), $"SAVED: the older field {gone} must not be written again");
        var reopened = new RemSoundSettingsStore("RemSound");
        reopened.ApplyProfile(System.Text.Json.JsonSerializer.Deserialize<Profile>(json)!);
        AsBefore(reopened, "SAVED AND OPENED AGAIN");

        // The computer's older settings changing now must not reach a profile that carries its own.
        cfg = AppConfig.Load();
        cfg.EnableSendOnCue = true;
        cfg.DefaultCueSounds["connect"] = "connect 1.wav";
        cfg.Save();
        var later = new RemSoundSettingsStore("RemSound");
        later.ApplyProfile(System.Text.Json.JsonSerializer.Deserialize<Profile>(json)!);
        Check(!later.LoadCueEnabled("send-on") && later.LoadCueSound("connect") == "connect 2.wav",
            "CARRIED: a profile that carries its cues must keep them, whatever the computer's older settings say later");
        cfg.EnableSendOnCue = false;
        cfg.DefaultCueSounds["connect"] = "connect 2.wav";
        cfg.Save();

        // Another older profile, opened after this computer's copy of the last profile's cues has moved on: it gets the
        // older settings, not the last profile's.
        cfg = AppConfig.Load();
        cfg.LastProfileCues = new CueSettings { KeyboardClicks = true, Enabled = { ["send-on"] = true, ["checkbox-off"] = true } };
        cfg.Save();
        var second = new RemSoundSettingsStore("RemSound");
        second.ApplyProfile(System.Text.Json.JsonSerializer.Deserialize<Profile>(olderProfile)!);
        AsBefore(second, "A SECOND OLDER PROFILE, OPENED LATER");

        // The files people really have: a profile and a global config saved by v5.9 and by v6.0 (the OLD FILES step's own).
        // What each cue should be is read from the raw JSON, not worked out by the code under test.
        foreach (var (version, profileJson, configJson) in new[] { ("v5.9", V59Profile, V59GlobalConfig), ("v6.0", V60Profile, V60GlobalConfig) })
        {
            File.WriteAllText(Path.Combine(AppConfig.UserDataDirectory, "global config.json"), configJson);
            using var rawProfile = System.Text.Json.JsonDocument.Parse(profileJson);
            using var rawConfig = System.Text.Json.JsonDocument.Parse(configJson);
            bool? Flag(System.Text.Json.JsonDocument doc, string name) =>
                doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False ? v.GetBoolean() : null;
            var mute = Flag(rawProfile, "MuteConnectionCues") == true;
            var released = new RemSoundSettingsStore("RemSound");
            released.ApplyProfile(System.Text.Json.JsonSerializer.Deserialize<Profile>(profileJson)!);
            foreach (var id in AllCueIds())
            {
                var flagName = "Enable" + string.Concat(id.Split('-').Select(part => char.ToUpperInvariant(part[0]) + part[1..])) + "Cue";
                var inProfile = CueSettings.KeptInTheProfileBefore.Any(k => k.CueId == id);
                var expected = inProfile
                    ? Flag(rawProfile, flagName) ?? !(mute && id is "connect" or "disconnect")
                    : Flag(rawConfig, flagName) ?? true;
                Check(released.LoadCueEnabled(id) == expected,
                    $"{version}: the {id} cue must be {(expected ? "on" : "off")}, as that version's {(inProfile ? "profile" : "global config")} had it");
            }
            Check(released.LoadKeyboardClicks() == (Flag(rawConfig, "EnableKeyboardClicks") ?? true), $"{version}: the key clicks must be as that version's config had them");
            var key = "key-" + version.Replace(".", "");
            Check(released.LoadCustomCuePath(key) == $"-{version.Replace(".", "")}-CustomCuePathsValue",
                $"{version}: the profile's own sound must win over the computer's for the same cue ({released.LoadCustomCuePath(key)})");
            Check(released.LoadCustomCuePath(MainForm.CueId.Startup) == $"-{version.Replace(".", "")}-StartupCueCustomPath",
                $"{version}: the startup sound's own file must be carried ({released.LoadCustomCuePath(MainForm.CueId.Startup)})");
            Check(released.LoadCueSound(key) == $"-{version.Replace(".", "")}-DefaultCueSoundsValue",
                $"{version}: the chosen sounds must be carried ({released.LoadCueSound(key)})");
        }

        try { Directory.Delete(scratch, recursive: true); } catch { /* temp */ }
        return $"all {AllCueIds().Length} cues of an older profile sound as before after the update - its own eight, the computer's older "
             + "switches, files and chosen sounds, the key clicks - saved they carry it all with no older field, and a second older "
             + "profile opened later gets the same; the real v5.9 and v6.0 files carry over cue for cue";
    }

    /// <summary>TWO PROFILES, TWO SETS OF SOUNDS: a cue an older RemSound kept on the computer - minimise and restore, the tab
    /// sound - is off in one profile and on in the other, in the window's own players and in the sounds that live outside
    /// it; this computer's copy follows the profile last opened; a new profile starts with the cues of the one you were
    /// in; and the startup cue plays once, as the run's first profile opens, as that profile has it.</summary>
    private static string? EachProfileHasItsOwnCues()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-cues-profiles-" + Guid.NewGuid().ToString("N"));
        using var userData = AppConfig.UseThrowawayUserDataDirectory(scratch);
        var restoreMuted = CuePlayer.GloballyMuted;
        var restoreNoDevice = CuePlayer.NoDeviceForTest;
        var restoreStartup = MainForm.StartupCuePlayedForTest;
        var restoreCues = ActiveCues.Current.Clone();
        var said = new List<string>();
        try
        {
            CuePlayer.GloballyMuted = false;
            CuePlayer.NoDeviceForTest = true;   // counted, never heard

            var quiet = new Profile { Cues = new CueSettings() };
            quiet.Cues.Enabled[MainForm.CueId.Show] = false;
            quiet.Cues.Enabled[MainForm.CueId.TabSwitch] = false;
            quiet.Cues.Enabled[MainForm.CueId.Startup] = false;
            var loud = new Profile { Cues = new CueSettings() };

            int Plays(MainForm form)
            {
                var before = CuePlayer.PlaysStartedForTest;
                form.PlayWindowVisibilityCue(show: true);
                TabSwitchSoundService.Play();
                return CuePlayer.PlaysStartedForTest - before;
            }

            MainForm.StartupCuePlayedForTest = true;
            using (var form = new MainForm(null, quiet, null, null, headless: true))
            {
                Check(Plays(form) == 0, "THE QUIET PROFILE: its restore and tab sounds are off, so neither may play");
                Check(!ActiveCues.Current.IsOn(MainForm.CueId.TabSwitch), "THE QUIET PROFILE: the tab sound outside the window must be off with it");
                Check(AppConfig.Load().LastProfileCues is { } kept && !kept.IsOn(MainForm.CueId.TabSwitch),
                    "THIS COMPUTER'S COPY: it must follow the profile last opened, for the next run's profile picker");
                Check(System.Text.Json.JsonSerializer.Serialize(form.CuesForANewProfile().OnlyProfileCues()) == System.Text.Json.JsonSerializer.Serialize(quiet.Cues.OnlyProfileCues()),
                    "A NEW PROFILE: made from this window, it must start with this profile's own cues");
            }
            using (var form = new MainForm(null, loud, null, null, headless: true))
            {
                Check(Plays(form) == 2, "THE LOUD PROFILE: its restore and tab sounds are on, so both must play");
                Check(ActiveCues.Current.IsOn(MainForm.CueId.TabSwitch), "THE LOUD PROFILE: the tab sound outside the window must be on with it");
            }
            // The blank template (the profile picker's New profile) starts with the cues last used.
            using (var form = new MainForm(null, null, null, null, headless: true))
                Check(form.SettingsForTest.LoadCueEnabled(MainForm.CueId.TabSwitch),
                    "THE BLANK TEMPLATE: it must start with the cues of the profile last opened (the loud one)");
            said.Add("two profiles, two sets of sounds, in the window and outside it; this computer's copy and a new profile follow");

            // The startup cue: once, as the run's first profile opens, as that profile has it.
            MainForm.StartupCuePlayedForTest = false;
            var before = CuePlayer.PlaysStartedForTest;
            using (var form = new MainForm(null, loud, null, null, headless: true)) { }
            Check(CuePlayer.PlaysStartedForTest == before + 1, $"STARTUP: the run's first profile, with the startup sound on, must play it once ({CuePlayer.PlaysStartedForTest - before})");
            before = CuePlayer.PlaysStartedForTest;
            using (var form = new MainForm(null, loud, null, null, headless: true)) { }
            Check(CuePlayer.PlaysStartedForTest == before, "STARTUP: a second window in the same run - a profile switch - must not play it again");
            MainForm.StartupCuePlayedForTest = false;
            using (var form = new MainForm(null, quiet, null, null, headless: true))
            {
                var log = new ConcurrentQueue<string>();
                form.LogForTest.EventTapForTest = log.Enqueue;
                MainForm.StartupCuePlayedForTest = false;
                before = CuePlayer.PlaysStartedForTest;
                form.PlayStartupCueOnceForTest();
                Check(CuePlayer.PlaysStartedForTest == before, "STARTUP: a first profile with the startup sound off must play nothing");
                Check(log.Any(l => l.Contains("startup cue: muted in settings", StringComparison.Ordinal)),
                    $"STARTUP: the log must say the startup sound was off in this profile ({string.Join(" | ", log.Where(l => l.Contains("startup", StringComparison.Ordinal)))})");
            }
            said.Add("the startup sound plays once, as the first profile opens, as that profile has it");
            return string.Join("; ", said);
        }
        finally
        {
            CuePlayer.GloballyMuted = restoreMuted;
            CuePlayer.NoDeviceForTest = restoreNoDevice;
            MainForm.StartupCuePlayedForTest = restoreStartup;
            ActiveCues.Use(restoreCues);
            try { Directory.Delete(scratch, recursive: true); } catch { /* temp */ }
        }
    }

    /// <summary>MUTE ALL AUDIO CUES: the Preferences tick box, driven through the control channel as a headless copy is,
    /// silences every cue - the window's, the tick box, tab and help sounds, the key clicks and the plugin's help sounds -
    /// in force at once, saved on this computer and logged; it changes nothing in the profile, marks nothing unsaved, and
    /// lets a preview the person asks for still play. Unticked, everything plays again. Changing a cue on the tab - one an
    /// older RemSound kept on the computer - changes the profile, not the computer.</summary>
    private static string? MuteAllCuesSilencesEveryProfile()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-cues-mute-" + Guid.NewGuid().ToString("N"));
        using var userData = AppConfig.UseThrowawayUserDataDirectory(scratch);
        var restoreMuted = CuePlayer.GloballyMuted;
        var restoreNoDevice = CuePlayer.NoDeviceForTest;
        var restoreAll = CuePlayer.AllCuesMuted;
        var restoreClicks = ActiveCues.ClicksAllowed;
        var restoreFactory = KeyClickService.PlayerFactoryForTest;
        var restoreClicksOn = KeyClickService.Enabled;
        var restoreCues = ActiveCues.Current.Clone();
        var restoreSink = UiChangeLog.Sink;
        var said = new List<string>();
        try
        {
            CuePlayer.GloballyMuted = false;
            CuePlayer.NoDeviceForTest = true;
            KeyClickService.PlayerFactoryForTest = KeyClickService.SilentPlayerForTest;   // no real device for the clicks
            ActiveCues.ClicksAllowed = true;
            var profile = new Profile { Cues = new CueSettings() };
            using var form = new MainForm(null, profile, null, null, headless: true);
            // After the window, which points the dialogs' log at its own.
            var logged = new ConcurrentQueue<string>();
            UiChangeLog.Sink = (what, value) => logged.Enqueue($"{what}: {value}");
            using var prefs = new PreferencesDialog(form.SettingsForTest, StoreWithOneProfileForTest(),
                () => false, _ => { }, () => { }, () => 0, () => { }, () => { }, () => { }, () => { }, () => { }, () => { },
                _ => { }, () => (default(RouterMappingStatus), (IPEndPoint?)null, ""), _ => { }, _ => { });
            _ = prefs.Handle;
            var engine = new RemoteControlEngine(() => prefs);
            var cuesBefore = System.Text.Json.JsonSerializer.Serialize(form.SettingsForTest.CueSnapshot());
            Check(KeyClickService.Enabled, "premise: the profile's key clicks are on");

            int Plays()
            {
                var before = CuePlayer.PlaysStartedForTest;
                form.PlayWindowVisibilityCue(show: true);
                TabSwitchSoundService.Play();
                HelpSoundService.PlayOpen();
                CheckSoundService.Play(true);
                return CuePlayer.PlaysStartedForTest - before;
            }
            Check(Plays() == 4, "premise: unmuted, the restore, tab, help and tick box sounds all play");

            Check(prefs.GlobalListCueIdsForTest.SequenceEqual(CueSettings.GlobalCueIds.OrderBy(g => g == "save" ? 0 : g == "profile-switch" ? 1 : g == "profile-menu-open" ? 2 : g == "update" ? 3 : g == "help-open" ? 4 : 5))
                  && prefs.ProfileListCueIdsForTest.Count == AllCueIds().Length - CueSettings.GlobalCueIds.Count
                  && prefs.ProfileListCueIdsForTest.All(id => !CueSettings.IsGlobal(id)),
                $"THE TWO LISTS: the global list must hold profile saved, switched, menu open, update and help opened and closed, the profile list every other cue (global: {string.Join(", ", prefs.GlobalListCueIdsForTest)})");
            // Ed (2026-10-08): the global list first, in tab order and on screen, and the key clicks box labelled as the profile's.
            var stops = new List<Control>();
            for (Control? c = prefs.MuteAllCuesBoxForTest; c is not null && stops.Count < 12; c = prefs.GetNextControl(c, true))
            {
                if (c.TabStop) stops.Add(c);
                if (c == prefs.KeyboardClicksBoxForTest) break;
            }
            var stopNames = stops.Select(c => c.AccessibleName ?? c.Text).ToList();
            Check(stopNames.Count >= 4 && stopNames[0] == "Turn all audio cues off" && stopNames[1] == "Global sounds, for every profile"
                  && stopNames[2] == "Profile sounds" && stopNames[3] == "Choose sound" && stops[^1] == prefs.KeyboardClicksBoxForTest,
                $"TAB ORDER: turn all off, then the global sounds, then the profile sounds, then Choose sound, ending on the key clicks ({string.Join(" > ", stopNames)})");
            var globalList = stops.FirstOrDefault(c => c.AccessibleName == "Global sounds, for every profile");
            var profileList = stops.FirstOrDefault(c => c.AccessibleName == "Profile sounds");
            Check(globalList?.Parent is TableLayoutPanel side && side == profileList?.Parent && side.GetColumn(globalList) == 0 && side.GetColumn(profileList) == 1,
                "ON SCREEN: the global list must sit on the left, the profile list on the right, the order Tab meets them");
            Check(prefs.KeyboardClicksBoxForTest.Text.Contains("for this profile", StringComparison.Ordinal)
                  && (prefs.KeyboardClicksBoxForTest.AccessibleName ?? "").Contains("for this profile", StringComparison.Ordinal),
                $"THE KEY CLICKS: the box must say, on screen and to a screen reader, that it is this profile's ({prefs.KeyboardClicksBoxForTest.Text})");
            said.Add("tab order: turn all off, global sounds, profile sounds, Choose sound ... key clicks; global on the left; the key clicks box says it is this profile's");
            var reply = engine.Execute("set \"Turn all audio cues off\" on");
            Check(reply.StartsWith("ok", StringComparison.Ordinal), $"THE CONTROL CHANNEL: the tick box must be reachable by its name ({reply})");
            Check(prefs.MuteAllCuesBoxForTest.Checked, "THE TICK BOX: it must be ticked");
            Check(CuePlayer.AllCuesMuted, "IN FORCE AT ONCE: every cue must be muted the moment it is ticked");
            Check(AppConfig.Load().MuteAllCues, "SAVED: it must be saved on this computer, for every profile");
            Check(logged.Any(l => l.Contains("MuteAllCues", StringComparison.Ordinal) && l.EndsWith("on", StringComparison.Ordinal)),
                $"LOGGED: ticking it must be recorded ({string.Join(" | ", logged)})");
            Check(Plays() == 0, "MUTED: no restore, tab, help or tick box sound may play");
            Check(!KeyClickService.Enabled, "MUTED: the key clicks must stop");
            Check(HelpSoundService.ChosenFile(open: true) is null && HelpSoundService.ChosenFile(open: false) is null,
                "MUTED: the DAW plugin must be given no help sounds, so it is quiet too");
            Check(!prefs.ChangedAnyProfileSetting, "THE PROFILE: a switch for every profile must not mark this one unsaved");
            Check(System.Text.Json.JsonSerializer.Serialize(form.SettingsForTest.CueSnapshot()) == cuesBefore,
                "THE PROFILE: muting everything must change nothing in it, so unticking brings its sounds back");
            var before = CuePlayer.PlaysStartedForTest;
            prefs.SelectCueForTest(1);   // arrowing the cue list previews the cue's sound
            Check(CuePlayer.PlaysStartedForTest == before + 1, "A PREVIEW: a sound the person asks to hear on this tab must still play");
            said.Add("ticked through the control channel: every cue, the key clicks and the plugin's help sounds silent, saved and logged, the profile untouched, previews still heard");

            reply = engine.Execute("set \"Turn all audio cues off\" off");
            Check(reply.StartsWith("ok", StringComparison.Ordinal) && !CuePlayer.AllCuesMuted && !AppConfig.Load().MuteAllCues,
                $"UNTICKED: every cue must be back, and the computer's setting with it ({reply})");
            Check(Plays() == 4 && KeyClickService.Enabled, "UNTICKED: the sounds and the key clicks must all play again");
            said.Add("unticked, all of them play again");

            // A global sound changed on the tab: this computer's, for every profile, and nothing in this one.
            var helpRow = Enumerable.Range(0, prefs.CueCountForTest).First(i => { prefs.SelectCueForTest(i); return prefs.SoundThatWouldPlayForTest() is { } f && Path.GetFileName(f).StartsWith("help open", StringComparison.OrdinalIgnoreCase); });
            prefs.SelectCueForTest(helpRow);
            prefs.SoundListForTest.SelectedIndex = 0;   // (none)
            Check(!prefs.ChangedAnyProfileSetting, "A GLOBAL SOUND: switching it off must not mark the profile unsaved - it is not the profile's");
            Check(AppConfig.Load().GlobalCues is { } globalNow && !globalNow.IsOn(MainForm.CueId.HelpOpen), "A GLOBAL SOUND: it must be off on this computer, at once");
            using (var other = new MainForm(null, new Profile { Cues = new CueSettings() }, null, null, headless: true))
                Check(!other.SettingsForTest.LoadCueEnabled(MainForm.CueId.HelpOpen), "A GLOBAL SOUND: off in one profile's Preferences, it must be off in another profile too");
            prefs.SelectCueForTest(helpRow);
            prefs.SoundListForTest.SelectedIndex = prefs.SoundListForTest.Items.Count - 1;
            Check(form.SettingsForTest.LoadCueEnabled(MainForm.CueId.HelpOpen), "A GLOBAL SOUND: choosing a sound for it must switch it back on");
            said.Add("a global sound changed on the tab is the computer's, in every profile, and leaves the profile untouched");

            // A cue an older RemSound kept on the computer, changed on the tab, changes the profile.
            var tabRow = Enumerable.Range(0, prefs.CueCountForTest).First(i => { prefs.SelectCueForTest(i); return prefs.SoundThatWouldPlayForTest() is { } f && Path.GetFileName(f).StartsWith("tab switch", StringComparison.OrdinalIgnoreCase); });
            prefs.SelectCueForTest(tabRow);
            prefs.SoundListForTest.SelectedIndex = 0;   // (none)
            Check(!form.SettingsForTest.LoadCueEnabled(MainForm.CueId.TabSwitch) && prefs.ChangedAnyProfileSetting,
                "THE TAB SOUND OFF: it must be off in the profile, and the profile marked changed");
            Check(AppConfig.Load().EnableTabSwitchCue, "THE TAB SOUND OFF: the computer's older setting must not be written");
            prefs.SoundListForTest.SelectedIndex = prefs.SoundListForTest.Items.Count - 1;   // its last built-in sound
            Check(form.SettingsForTest.LoadCueSound(MainForm.CueId.TabSwitch) is { } chosen && chosen.StartsWith("tab switch", StringComparison.OrdinalIgnoreCase),
                "A CHOSEN SOUND: it must be the profile's");
            Check(!AppConfig.Load().DefaultCueSounds.ContainsKey(MainForm.CueId.TabSwitch), "A CHOSEN SOUND: it must not be written to the computer");
            prefs.KeyboardClicksBoxForTest.Checked = false;
            Check(!form.SettingsForTest.LoadKeyboardClicks() && !KeyClickService.Enabled,
                "THE KEY CLICKS: off in the profile, and off at once");
            said.Add("a cue, its sound and the key clicks changed on the tab change the profile, not the computer");
            return string.Join("; ", said);
        }
        finally
        {
            UiChangeLog.Sink = restoreSink;
            CuePlayer.GloballyMuted = restoreMuted;
            CuePlayer.NoDeviceForTest = restoreNoDevice;
            CuePlayer.AllCuesMuted = restoreAll;
            ActiveCues.ClicksAllowed = restoreClicks;
            ActiveCues.Use(restoreCues);
            KeyClickService.Enabled = restoreClicksOn;
            KeyClickService.PlayerFactoryForTest = restoreFactory;
            try { Directory.Delete(scratch, recursive: true); } catch { /* temp */ }
        }
    }
}
