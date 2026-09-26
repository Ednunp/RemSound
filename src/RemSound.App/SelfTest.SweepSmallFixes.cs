using System.Net;
using NAudio.Wave;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Five small faults from the 2026-09-25 sweep (Ed: "fix them all please"): Save as from a locked profile wrote a locked
/// copy; Browse left a switched-off sound off; RemSound looked for a screen reader once and, finding none, never spoke
/// again that session; the Server window never said a connect had failed; and the start-up repair's answer opened behind
/// everything.
/// </summary>
internal static partial class SelfTest
{
    private static string? SweepSmallFixesHold()
    {
        var dir = Path.Combine(AppConfig.UserDataDirectory, "sweep-small-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        MainForm? form = null;
        try
        {
            // 1. Save as from a locked profile: the copy is editable.
            var store = new ProfileStore(dir);
            var locked = Profile.NewBlank();
            locked.Title = "Locked";
            locked.ReadOnly = true;
            locked.Password = RemSoundCrypto.Obfuscate("locked-password");   // no password question in the way
            store.Save(locked);
            try { form = new MainForm(store, locked, "Locked", store.PathFor("Locked"), headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            var readOnlyField = Require(typeof(MainForm).GetField("currentProfileReadOnly", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
                "MainForm.currentProfileReadOnly not found");
            Check(readOnlyField.GetValue(form) is true, "premise: the window holds a locked profile");
            var copyPath = store.PathFor("Copy");
            form.SaveProfileAsToForTest(copyPath);
            Check(File.Exists(copyPath) && !store.IsProfileReadOnly("Copy"),
                "THE LOCK: Save as from a locked profile must write an editable copy, as the window says it is - a locked copy skips the save question, and later changes were lost");
            Check(readOnlyField.GetValue(form) is false, "and the window must hold it as editable");
            Check(store.IsProfileReadOnly("Locked"), "while the original stays locked");
            form.Dispose();
            form = null;

            // 2. Browse turns a switched-off sound on - Browse's own code, after its file picker.
            using (var prefs = (PreferencesDialog)DialogFactories().First(d => d.Name == "Preferences").Make())
            {
                _ = prefs.Handle;
                prefs.SelectCueForTest(0);
                var wasOn = prefs.SelectedCueEnabledForTest;
                var own = Path.Combine(dir, "my sound.wav");
                using (var writer = new WaveFileWriter(own, new WaveFormat(48000, 16, 1))) writer.Write(new byte[960], 0, 960);
                prefs.SetSelectedCueEnabledForTest(false);
                prefs.UseOwnFileForTest(own);
                Check(prefs.SelectedCueEnabledForTest,
                    "THE SOUND: choosing a sound with Browse must switch that sound on - the missing-sound message tells people to do exactly that, and it stayed silent");
                prefs.ClearOwnFileForTest();
                prefs.SetSelectedCueEnabledForTest(wasOn);
            }

            // 3. No screen reader when RemSound first speaks: it looks again, at most every few seconds.
            var clock = 1_000_000L;
            var hasSpeech = false;
            var loads = 0;
            var spoken = new List<string>();
            var tolk = new TolkScreenReaderOutput
            {
                LoadForTest = () => { loads++; return true; },
                HasSpeechForTest = () => hasSpeech,
                OutputForTest = text => { spoken.Add(text); return true; },
                ClockForTest = () => clock,
            };
            Check(!tolk.Speak("first") && tolk.AttemptsForTest == 1, "premise: with no screen reader running, nothing is spoken");
            clock += 1000;
            tolk.Speak("again");
            Check(tolk.AttemptsForTest == 1, "within a few seconds it must not look again - speech is asked for often");
            hasSpeech = true;                                                  // NVDA is up now
            clock += (long)TolkScreenReaderOutput.RetryAfter.TotalMilliseconds;
            Check(tolk.Speak("now") && spoken.SequenceEqual(["now"]),
                "THE SILENCE: once a screen reader is running, RemSound must find it the next time it has something to say - it used to stay silent all session");
            Check(loads == 1, $"Tolk itself is loaded once, however often it looks for the screen reader ({loads})");
            tolk.Speak("and again");
            Check(tolk.AttemptsForTest == 2 && spoken.Count == 2, "and once found, it speaks without looking again");

            // 4. The Server window speaks what happens: an address that connects at once, and a name that can't be found.
            Windowless.InstallHook();
            Windowless.SetActiveForTest(true);
            try
            {
                string? on = null;
                var line = "Not connected to a server.";
                using var server = new RelayConnectDialog(() => on, typed => { line = $"Connecting to {typed}..."; if (typed.StartsWith("203.", StringComparison.Ordinal)) { on = typed; line = $"Connected to {typed}."; } },
                    () => { on = null; line = "Not connected to a server."; }, () => Array.Empty<string>(), _ => { }, () => line);
                _ = server.Handle;
                var addressBox = Require(FieldOf<TextBox>(server, "addressBox"), "RelayConnectDialog.addressBox not found");

                var mark = HeadlessRecords.Speech.Mark;
                addressBox.Text = "203.0.113.5";
                server.ActionForTest();
                Check(HeadlessRecords.Speech.Since(mark).Any(s => s.Contains("Connected. The button is now Disconnect.", StringComparison.Ordinal)),
                    $"THE ADDRESS: an address that connects there and then must be announced (said: {string.Join(" | ", HeadlessRecords.Speech.Since(mark))})");
                server.ActionForTest();   // disconnect

                mark = HeadlessRecords.Speech.Mark;
                addressBox.Text = "no-such-server.example";
                server.ActionForTest();
                Check(HeadlessRecords.Speech.Since(mark).Any(s => s.Contains("Connecting to no-such-server.example", StringComparison.Ordinal)),
                    "and \"Connecting...\" must be said");
                line = "Couldn't find no-such-server.example.";
                server.TickForTest();
                Check(HeadlessRecords.Speech.Since(mark).Any(s => s.EndsWith("Couldn't find no-such-server.example.", StringComparison.Ordinal)),
                    "THE FAILURE: a name that can't be found must be said, not only written under the buttons");
                var count = HeadlessRecords.Speech.Since(mark).Count;
                server.TickForTest();
                Check(HeadlessRecords.Speech.Since(mark).Count == count, "and said once, not at every tick");
            }
            finally { Windowless.SetActiveForTest(false); }

            // 5. The start-up repair's answer comes up in front of everything, owned by the top-most window, as every box
            // reachable from the tray is.
            Windowless.InstallHook();
            Windowless.SetActiveForTest(true);
            try
            {
                try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
                catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
                _ = form.Handle;
                var main = form;
                string? owner = null;
                DriveWhilePumping(main, () =>
                {
                    main.BeginInvoke(() => main.ReportServiceActionResultForTest("access repair", 0));
                    Check(WaitFor(() => main.Invoke(() => Application.OpenForms.OfType<HeadlessQuestionForm>().Any()), TimeSpan.FromSeconds(5)), "the repair's answer must be shown");
                    owner = main.Invoke(() => Application.OpenForms.OfType<HeadlessQuestionForm>().First().Owner is Form { TopMost: true, ShowInTaskbar: false } ? "in front" : "the window");
                    main.Invoke(() => Application.OpenForms.OfType<HeadlessQuestionForm>().First().Close());
                }, TimeSpan.FromSeconds(20));
                Check(owner == "in front", $"THE FRONT: the start-up repair's answer must open in front of everything, not behind the hidden window (owned by {owner})");
            }
            finally { Windowless.SetActiveForTest(false); }
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            CuePlayer.GloballyMuted = restoreMuted;
            try { Directory.Delete(dir, recursive: true); } catch { /* the run's own folder */ }
        }
        return "Save as from a locked profile wrote an editable copy and left the original locked; Browse switched a silent sound on; "
             + "with no screen reader RemSound looked again after a few seconds and spoke once NVDA was there, loading Tolk once; the "
             + "Server window announced an address that connected at once, \"Connecting...\" and a name it couldn't find, once; and the "
             + "repair's answer opened in front of everything";
    }
}
