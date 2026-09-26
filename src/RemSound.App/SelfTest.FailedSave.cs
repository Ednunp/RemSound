using System.Windows.Forms;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// A SAVE THAT FAILS KEEPS YOU WHERE YOU ARE, WITH YOUR CHANGES.
///
/// <para>The profile save caught its own error and said nothing to whoever asked for it. So "Yes, save and exit" exited
/// after a failed save, throwing away the changes it had just offered to keep; New profile carried on the same way; and
/// auto-save logged "saved" either way (found 2026-09-25). Driven here through the real window: its save, and its close
/// with the unsaved-changes question answered Yes, with the write killed half way each time.</para>
/// </summary>
internal static partial class SelfTest
{
    private static string? AFailedSaveKeepsYouWhereYouAre()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        MainForm? form = null;
        var writes = 0;
        try
        {
            var store = StoreWithOneProfileForTest();
            var profile = store.Load("Audit profile") ?? Profile.NewBlank();
            try { form = new MainForm(store, profile, "Audit profile", null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            var main = form;
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var markDirty = Require(typeof(MainForm).GetMethod("MarkProfileDirty", flags, Type.EmptyTypes), "MainForm.MarkProfileDirty not found");
            var save = Require(typeof(MainForm).GetMethod("SaveProfileTo", flags, [typeof(string), typeof(bool), typeof(bool)]),
                "MainForm.SaveProfileTo(string, bool, bool) not found");
            var engine = new RemoteControlEngine(() => main);
            string Answer(string button) => main.Invoke(() => engine.Execute($"answer {button}"));
            bool QuestionSays(string words) => main.Invoke(() => engine.Execute("windows")).Contains(words, StringComparison.Ordinal);

            markDirty.Invoke(main, null);
            Check(main.UnsavedChangesForTest, "premise: the profile must have unsaved changes");
            ProfileStore.MidWriteForTest = () => { Interlocked.Increment(ref writes); throw new IOException("gate: the disk went away"); };

            // 1. The save itself says it failed, and the changes are still unsaved.
            DriveWhilePumping(main, () =>
            {
                bool? saved = null;
                main.BeginInvoke(() => saved = (bool)save.Invoke(main, ["Audit profile", false, false])!);
                Check(WaitFor(() => QuestionSays("Could not save profile"), TimeSpan.FromSeconds(5)), "a failed save must say so");
                Check(Answer("OK").StartsWith("ok", StringComparison.Ordinal), "and its message must be answerable");
                Check(WaitFor(() => saved is not null, TimeSpan.FromSeconds(5)), "and the save must then come back");
                Check(saved == false, "a save whose write failed must say it failed to whoever asked for it");
                Check(main.Invoke(() => main.UnsavedChangesForTest), "and the changes must still count as unsaved");
            }, TimeSpan.FromSeconds(30));
            Check(writes >= 1, "premise: the save must have reached the write that was made to fail");

            // 2. Exit: Yes, save and exit - the save fails - RemSound stays open with the changes.
            DriveWhilePumping(main, () =>
            {
                main.BeginInvoke(() => main.Close());
                Check(WaitFor(() => QuestionSays("Save them before exiting"), TimeSpan.FromSeconds(5)), "closing with unsaved changes must ask first");
                Check(Answer("Yes").StartsWith("ok", StringComparison.Ordinal), "the question must be answerable");
                Check(WaitFor(() => QuestionSays("Could not save profile"), TimeSpan.FromSeconds(5)), "the save must fail, and say so");
                Answer("OK");
                Thread.Sleep(300);
                // Read without asking the window: one that exited has no handle left to ask.
                var exited = main.IsDisposed || !main.IsHandleCreated;
                Check(!exited && main.Invoke(() => main.UnsavedChangesForTest),
                    "THE LOSS: \"Yes, save and exit\" with a save that failed must leave RemSound open with the changes, not exit and throw them away");
            }, TimeSpan.FromSeconds(30));

            return "a save whose write fails says so and reports failure, the changes stay unsaved, and \"Yes, save and exit\" stays open instead of exiting";
        }
        finally
        {
            ProfileStore.MidWriteForTest = null;
            try
            {
                if (form is not null && !form.IsDisposed)
                {
                    Require(typeof(MainForm).GetField("unsavedChanges", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
                        "MainForm.unsavedChanges not found").SetValue(form, false);
                    form.Dispose();
                }
            }
            catch { /* teardown */ }
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
        }
    }
}
