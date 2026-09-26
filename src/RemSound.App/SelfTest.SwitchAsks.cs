using System.Windows.Forms;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// SWITCHING PROFILE ASKS ABOUT UNSAVED CHANGES, AS NEW PROFILE AND EXIT DO.
///
/// <para>Review 2026-09-25, Ed agreed the fix. A Recent profile, the quick-switch hotkey and File, Open all switched
/// straight away and threw unsaved changes away without a word, while New profile and Exit asked first. Driven here
/// through the real window, with the question answered through the control channel the way --control answers it in a
/// headless copy: Cancel stays with the changes, No switches without saving, Yes saves and then switches, and a
/// read-only profile switches without asking.</para>
/// </summary>
internal static partial class SelfTest
{
    private static string? SwitchingProfileAsksAboutUnsavedChanges()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        var writes = 0;
        var forms = new List<MainForm>();
        try
        {
            var store = StoreWithOneProfileForTest();
            var other = Profile.NewBlank();
            other.Title = "Other profile";
            store.Save(other);
            var otherPath = store.PathFor("Other profile");
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var markDirty = Require(typeof(MainForm).GetMethod("MarkProfileDirty", flags, Type.EmptyTypes), "MainForm.MarkProfileDirty not found");
            var switchTo = Require(typeof(MainForm).GetMethod("SwitchToRecentProfile", flags, [typeof(string)]),
                "MainForm.SwitchToRecentProfile(string) not found");
            var readOnly = Require(typeof(MainForm).GetField("currentProfileReadOnly", flags), "MainForm.currentProfileReadOnly not found");
            ProfileStore.MidWriteForTest = () => Interlocked.Increment(ref writes);

            MainForm OpenDirty()
            {
                MainForm form;
                try { form = new MainForm(store, store.Load("Audit profile") ?? Profile.NewBlank(), "Audit profile", null, headless: true); }
                catch (Exception ex) { throw new StepSkipped(MainWindowCouldNotBeBuilt(ex)); }
                forms.Add(form);
                _ = form.Handle;
                markDirty.Invoke(form, null);
                Check(form.UnsavedChangesForTest, "premise: the profile must have unsaved changes");
                return form;
            }

            // Switch to the other profile and answer the question (or expect none). Returns whether the window closed.
            bool Switch(MainForm main, string? answer)
            {
                var engine = new RemoteControlEngine(() => main);
                bool Gone() => main.IsDisposed || !main.IsHandleCreated;
                bool QuestionSays(string words)
                {
                    try { return !Gone() && main.Invoke(() => engine.Execute("windows")).Contains(words, StringComparison.Ordinal); }
                    catch (ObjectDisposedException) { return false; }   // it closed while being asked
                    catch (InvalidOperationException) { return false; }
                }
                var closed = false;
                DriveWhilePumping(main, () =>
                {
                    main.BeginInvoke(() => switchTo.Invoke(main, [otherPath]));
                    if (answer is null)
                    {
                        Check(WaitFor(Gone, TimeSpan.FromSeconds(5)), "a read-only profile must switch without asking");
                    }
                    else
                    {
                        const string Question = "Save them before switching to \"Other profile\"";
                        WaitFor(() => Gone() || QuestionSays(Question), TimeSpan.FromSeconds(5));
                        Check(!Gone() && QuestionSays(Question),
                            "THE LOSS: switching profile with unsaved changes must ask first, as New profile and Exit do"
                            + (Gone() ? " (it switched without a word)" : ""));
                        Check(main.Invoke(() => engine.Execute($"answer {answer}")).StartsWith("ok", StringComparison.Ordinal),
                            $"the question must be answerable ({answer})");
                        Thread.Sleep(400);
                    }
                    // Read without asking the window: one that closed has no handle left to ask.
                    closed = Gone();
                }, TimeSpan.FromSeconds(30));
                return closed;
            }

            bool Logged(long mark, string words) => HeadlessRecords.Log.Since(mark).Any(l => l.Contains(words, StringComparison.Ordinal));
            const string Going = "switching to \"Other profile\"";

            // Cancel: stay, with the changes, and nothing saved.
            var first = OpenDirty();
            var mark = HeadlessRecords.Log.Mark;
            Check(!Switch(first, "Cancel"), "Cancel must stay in this profile");
            Check(Logged(mark, $"unsaved changes: stayed rather than {Going}"), "and the log must say so");
            Check(first.NextProfileTitleToLoad is null && first.UnsavedChangesForTest && writes == 0,
                $"and keep the changes, unsaved, going nowhere (next: {first.NextProfileTitleToLoad ?? "none"}, saves: {writes})");

            // No: switch, without saving.
            mark = HeadlessRecords.Log.Mark;
            Check(Switch(first, "No"), "No must switch");
            Check(Logged(mark, $"unsaved changes: discarded before {Going}"), "and the log must say the changes were discarded");
            Check(first.NextProfileTitleToLoad == "Other profile", $"to the profile chosen ({first.NextProfileTitleToLoad ?? "none"})");
            Check(writes == 0, $"without saving ({writes} saves)");

            // Yes: save, then switch.
            var second = OpenDirty();
            mark = HeadlessRecords.Log.Mark;
            Check(Switch(second, "Yes"), "Yes must switch");
            Check(Logged(mark, $"unsaved changes: saved before {Going}"), "and the log must say they were saved");
            Check(writes == 1, $"after saving the changes once ({writes} saves)");
            Check(second.NextProfileTitleToLoad == "Other profile", "to the profile chosen");

            // Read-only: its changes are throwaway by design, so no question, as at exit.
            var third = OpenDirty();
            readOnly.SetValue(third, true);
            Check(Switch(third, null), "a read-only profile must switch");
            Check(writes == 1, "without saving");

            // File, Open goes through a Windows file picker, so it is checked by the same question being asked in its code,
            // after the picker and before anything is switched.
            var root = FindSourceRoot();
            if (root is null) return Skip("the switches ask; the source is not reachable to check File, Open (set REMSOUND_SOURCE_ROOT)");
            var body = SourceMethodBody(File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "MainForm.cs")), "private void OpenProfileFromPicker()");
            var ask = body.IndexOf("OfferToSaveBeforeLeaving(", StringComparison.Ordinal);
            Check(ask > body.IndexOf("dialog.ShowDialog(this)", StringComparison.Ordinal) && ask < body.IndexOf("NextProfilePathToLoad = ", StringComparison.Ordinal),
                "File, Open must ask about unsaved changes after the file is chosen and before it switches");
            return "switching profile with unsaved changes asks: Cancel stays with them, No switches without saving, Yes saves then "
                 + "switches; a read-only profile switches without asking; File, Open asks the same question";
        }
        finally
        {
            ProfileStore.MidWriteForTest = null;
            foreach (var form in forms)
            {
                try
                {
                    if (form.IsDisposed) continue;
                    Require(typeof(MainForm).GetField("unsavedChanges", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
                        "MainForm.unsavedChanges not found").SetValue(form, false);
                    form.Dispose();
                }
                catch { /* teardown */ }
            }
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
        }
    }
}
