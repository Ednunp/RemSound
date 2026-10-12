using System.Collections.Concurrent;
using RemSound.Core;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>METRONOME ASIO WARNING (Ed, 2026-10-12: "any time you go 1st time into metronome settings or become master
    /// metronome or start the metronome, any one of those, you get a dialogue saying warning metronome should only be used
    /// with 2 computers running asio devices for accuracy ... do not show me this message again checkbox"). Each of the three
    /// asks first - answered through the control channel, as the keyboard would - and then still happens; stopping and
    /// handing back don't ask, nor does the settings window's own Listen button straight after the window asked; with nobody
    /// there to answer it never asks; the tick hides it from then on.</summary>
    private static string? MetronomeAsioWarning()
    {
        var page = MainForm.BuildMetronomeAsioWarning(out var tick);
        Check(page.Heading == "Metronome accuracy" && (page.Text ?? "").Contains("only be used with both computers running ASIO devices", StringComparison.Ordinal),
            $"the warning must say the metronome wants ASIO on both computers ({page.Heading}: {page.Text})");
        Check(tick.Text == "Do not show me this message again" && !tick.Checked, "it must carry Ed's tick, unticked");
        Check(page.Buttons.Count == 1 && page.Buttons[0] == TaskDialogButton.OK, "and an OK button");

        var scratch = Path.Combine(Path.GetTempPath(), "remsound-metronome-asio-" + Guid.NewGuid().ToString("N"));
        using var userData = AppConfig.UseThrowawayUserDataDirectory(scratch);
        Check(AppConfig.Load().ShowMetronomeAsioWarning, "it must be on until hidden");
        // Only this warning: the no-server message, which starting with no server also shows, has its own step.
        var cfg = AppConfig.Load();
        cfg.ShowMetronomeNoServerMessage = false;
        cfg.Save();
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        var restoreMaster = MainForm.MasterWithoutServerForTest;
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        MainForm? form = null;
        try
        {
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            var main = form;
            var said = new ConcurrentQueue<string>();
            main.LogForTest.EventTapForTest = said.Enqueue;
            int Shown() => said.Count(l => l.Contains("metronome: the ASIO warning shown", StringComparison.Ordinal));
            var engine = new RemoteControlEngine(() => main);

            // Nobody there to answer (a headless copy, the gate): never asked, and the metronome still starts.
            main.MetronomeStartStopForTest("the menu");
            Check(Shown() == 0 && main.MetronomeForTest.Playing, "NOBODY TO ANSWER: it must not ask, and the metronome must still start");
            main.MetronomeStartStopForTest("the menu");

            main.SomebodyToAnswerForTest = true;
            main.NobodyToAskForTest = () => false;
            bool Asking() => main.Invoke(() => Application.OpenForms.OfType<HeadlessQuestionForm>().Any());
            string Question() => main.Invoke(() => Application.OpenForms.OfType<HeadlessQuestionForm>().First().Question);
            bool Playing() => main.Invoke(() => main.MetronomeForTest.Playing);
            MetronomeSettingsDialog? window = null;
            DriveWhilePumping(main, () =>
            {
                // Starting it.
                main.BeginInvoke(() => main.MetronomeStartStopForTest("the menu"));
                Check(WaitFor(Asking, TimeSpan.FromSeconds(5)), "START: starting the metronome must ask first");
                Check(Question().Contains("both computers running ASIO devices", StringComparison.Ordinal), $"START: with the warning's words ({Question().Replace('\n', ' ')})");
                Check(!Playing(), "START: and nothing starts while it is open");
                var reply = main.Invoke(() => engine.Execute("answer OK"));
                Check(reply.StartsWith("ok", StringComparison.Ordinal) && WaitFor(() => !Asking() && Playing(), TimeSpan.FromSeconds(5)),
                    $"START: OK must start the metronome ({reply})");
                main.Invoke(() => main.MetronomeStartStopForTest("the menu"));
                Check(!Asking() && !Playing(), "STOP: stopping must not ask");

                // Becoming the master.
                MainForm.MasterWithoutServerForTest = true;
                main.BeginInvoke(() => main.SetMetronomeMasterForTest(true));
                Check(WaitFor(Asking, TimeSpan.FromSeconds(5)), "MASTER: becoming the master metronome must ask first");
                reply = main.Invoke(() => engine.Execute("answer OK"));
                Check(reply.StartsWith("ok", StringComparison.Ordinal) && WaitFor(() => !Asking() && main.Invoke(() => main.MetronomeForTest.Role) == MetronomeRole.Master, TimeSpan.FromSeconds(5)),
                    $"MASTER: OK must make you the master ({reply})");
                main.Invoke(() => main.SetMetronomeMasterForTest(false));
                Check(!Asking() && main.Invoke(() => main.MetronomeForTest.Role) != MetronomeRole.Master, "HANDING BACK: must not ask");
                MainForm.MasterWithoutServerForTest = restoreMaster;

                // Opening Metronome settings; its own Listen button straight after doesn't ask again.
                main.BeginInvoke(() => { window = main.BuildMetronomeSettingsDialogForTest(out _); window.Show(main); });
                Check(WaitFor(Asking, TimeSpan.FromSeconds(5)), "SETTINGS: opening Metronome settings must ask first");
                reply = main.Invoke(() => engine.Execute("answer OK"));
                Check(reply.StartsWith("ok", StringComparison.Ordinal) && WaitFor(() => !Asking() && main.Invoke(() => window is not null), TimeSpan.FromSeconds(5)),
                    $"SETTINGS: OK must open the window ({reply})");
                main.Invoke(() => window!.ListenButtonForTest.PerformClick());
                Check(!Asking() && Playing(), "SETTINGS: the window's Listen button must start it without asking again");
                main.Invoke(() => window!.ListenButtonForTest.PerformClick());
                main.Invoke(() => window!.Dispose());

                // The tick.
                main.Invoke(() => main.MetronomeAsioWarningAnswered(dontShowAgain: false));
                Check(AppConfig.Load().ShowMetronomeAsioWarning, "OK without the tick must leave it on");
                main.Invoke(() => main.MetronomeAsioWarningAnswered(dontShowAgain: true));
                Check(!AppConfig.Load().ShowMetronomeAsioWarning, "THE TICK: Do not show me this message again must hide it");
                main.Invoke(() => main.MetronomeStartStopForTest("the menu"));
                Check(!Asking() && Playing(), "HIDDEN: starting must not ask any more");
                main.Invoke(() => main.MetronomeStartStopForTest("the menu"));
            }, TimeSpan.FromSeconds(60));
            Check(Shown() == 3, $"each of the three must have asked exactly once ({Shown()})");
            Check(said.Any(l => l.Contains("the ASIO warning shown - starting it from the menu", StringComparison.Ordinal))
                  && said.Any(l => l.Contains("the ASIO warning shown - becoming the master metronome", StringComparison.Ordinal))
                  && said.Any(l => l.Contains("the ASIO warning shown - opening Metronome settings", StringComparison.Ordinal)),
                "the log must say each time it asked, and why");
            Check(said.Any(l => l.Contains("the ASIO warning is hidden from now on", StringComparison.Ordinal)), "and that it is hidden");
            main.LogForTest.EventTapForTest = null;
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            Windowless.SetActiveForTest(false);
            MainForm.MasterWithoutServerForTest = restoreMaster;
            CuePlayer.GloballyMuted = restoreMuted;
        }
        return "starting the metronome, becoming the master and opening Metronome settings each ask first, and still happen after OK; "
             + "stopping, handing back and the window's own Listen don't; nobody to answer, never asked; the tick hides it; logged";
    }

    /// <summary>For a step that answers the metronome's other questions: the accuracy warning hidden while it runs, as for someone
    /// who has ticked it away. Disposing puts it back.</summary>
    private static IDisposable WithoutMetronomeAccuracyWarning()
    {
        var cfg = AppConfig.Load();
        var was = cfg.ShowMetronomeAsioWarning;
        cfg.ShowMetronomeAsioWarning = false;
        cfg.Save();
        return new PutBack(() =>
        {
            var back = AppConfig.Load();
            back.ShowMetronomeAsioWarning = was;
            try { back.Save(); } catch { /* the run's own settings folder */ }
        });
    }

    private sealed class PutBack(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
