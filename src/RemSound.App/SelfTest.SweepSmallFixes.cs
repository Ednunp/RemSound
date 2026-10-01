using System.Net;
using NAudio.Wave;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Five small faults from the 2026-09-25 sweep (Ed: "fix them all please"): Save as from a locked profile wrote a locked
/// copy; Browse left a switched-off sound off; RemSound looked for a screen reader once and, finding none, never spoke
/// again that session; the Server window never said a connect had failed; and the start-up repair's answer opened behind
/// everything. One step each since 2026-09-28 (the review of every step): as one step, a run for the areas a change
/// touched - profiles, sounds, speech, the relay, dialogs - never picked their halves up.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>Save as from a locked profile: the copy is editable, and the original stays locked.</summary>
    private static string? SaveAsFromALockedProfileIsEditable()
    {
        var dir = Path.Combine(AppConfig.UserDataDirectory, "sweep-small-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        MainForm? form = null;
        try
        {
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
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            try { Directory.Delete(dir, recursive: true); } catch { /* the run's own folder */ }
        }
        return "Save as from a locked profile wrote an editable copy, the window held it as editable, and the original stayed locked";
    }

    /// <summary>Browse turns a switched-off sound on - Browse's own code, after its file picker.</summary>
    private static string? BrowseSwitchesASoundOn()
    {
        var dir = Path.Combine(AppConfig.UserDataDirectory, "sweep-small-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            using var prefs = (PreferencesDialog)DialogFactories().First(d => d.Name == "Preferences").Make();
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
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* the run's own folder */ } }
        return "choosing a sound with Browse switched a silent sound on";
    }

    /// <summary>No screen reader when RemSound first speaks: it looks again, at most every few seconds.</summary>
    private static string? SpeechFindsTheScreenReaderLater()
    {
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
        return "with no screen reader RemSound looked again after a few seconds and spoke once NVDA was there, loading Tolk once";
    }

    /// <summary>The Server window on what happens: an address that connects at once closes it, without a word (Ed,
    /// 2026-09-29: "when you connect to a server it should close the connect to server window ... that should just be
    /// silent when you connect"); a name that can't be found is said, once.</summary>
    private static string? TheServerWindowSaysWhatHappens()
    {
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        try
        {
            string? on = null;
            var line = "Not connected to a server.";
            RelayConnectDialog Make() => new(() => on, typed => { line = $"Connecting to {typed}..."; if (typed.StartsWith("203.", StringComparison.Ordinal)) { on = typed; line = $"Connected to {typed}."; } },
                () => { on = null; line = "Not connected to a server."; }, () => Array.Empty<string>(), _ => { }, () => line);
            using (var quick = Make())
            {
                _ = quick.Handle;
                var box = Require(FieldOf<TextBox>(quick, "addressBox"), "RelayConnectDialog.addressBox not found");
                var before = HeadlessRecords.Speech.Mark;
                box.Text = "203.0.113.5";
                quick.ActionForTest();
                var said = HeadlessRecords.Speech.Since(before);
                Check(quick.DialogResult == DialogResult.OK && !said.Any(s => s.Contains("button is now", StringComparison.OrdinalIgnoreCase)),
                    $"THE ADDRESS: an address that connects there and then must close the window without a word about the button (said: {string.Join(" | ", said)})");
            }
            on = null;
            line = "Not connected to a server.";
            using var server = Make();
            _ = server.Handle;
            var addressBox = Require(FieldOf<TextBox>(server, "addressBox"), "RelayConnectDialog.addressBox not found");

            var mark = HeadlessRecords.Speech.Mark;
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
        return "an address that connected at once closed the Server window without a word; \"Connecting...\" and a name it couldn't find were said, once";
    }

    /// <summary>The start-up repair's answer comes up in front of everything, owned by the top-most window, as every box
    /// reachable from the tray is.</summary>
    private static string? TheRepairAnswerIsInFront()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        MainForm? form = null;
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
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
        }
        return "the start-up repair's answer opened in front of everything";
    }
}
