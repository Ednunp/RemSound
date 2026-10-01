using System.Net;
using RemSound.Core;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>ACCEPT ASK, ANSWERED (2026-09-28, from the review of every step). "Ask" is the default for somebody who
    /// ticks you, and the step above shows it asks once - but never answered it. Here the question is answered through the
    /// control channel, as the person's keyboard would: Yes connects that person and says so; No leaves them unconnected,
    /// is logged, and is not asked again however often their packets keep coming.</summary>
    private static string? AcceptAskAnsweredYesAndNo()
    {
        var saved = AppConfig.Load();
        var restoreMode = saved.AcceptPeerConnections;
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        MainForm? form = null;
        try
        {
            saved.AcceptPeerConnections = PeerAcceptMode.Prompt;
            saved.Save();
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            var main = form;
            main.SomebodyToAnswerForTest = true;
            main.NobodyToAskForTest = () => false;
            var logged = new List<string>();
            main.LogForTest.EventTapForTest = line => { lock (logged) logged.Add(line); };
            var engine = new RemoteControlEngine(() => main);
            bool Connected(IPEndPoint ep) => main.Invoke(() => main.SelectedSendEndpointsForTest().Any(e => e.Address.Equals(ep.Address)));
            bool Asking() => main.Invoke(() => Application.OpenForms.OfType<HeadlessQuestionForm>().Any());
            var yes = new IPEndPoint(IPAddress.Parse("198.51.100.41"), RemPacket.DefaultPeerDialPort);
            var no = new IPEndPoint(IPAddress.Parse("198.51.100.42"), RemPacket.DefaultPeerDialPort);

            DriveWhilePumping(main, () =>
            {
                main.BeginInvoke(() => main.SomeoneWantsToConnectForTest(yes, samePassword: true));
                Check(WaitFor(Asking, TimeSpan.FromSeconds(5)), "somebody with the password ticking us must be asked about");
                var question = main.Invoke(() => Application.OpenForms.OfType<HeadlessQuestionForm>().First().Question);
                Check(question.Contains("198.51.100.41", StringComparison.Ordinal) && question.Contains("would like to connect", StringComparison.Ordinal),
                    $"the question must say who wants to connect ({question.Replace('\n', ' ')})");
                Check(!Connected(yes), "and nobody is connected while the question is open");
                var said = main.Invoke(() => engine.Execute("answer Yes"));
                Check(said.StartsWith("ok", StringComparison.Ordinal), $"the question must take Yes through the control channel ({said})");
                Check(WaitFor(() => Connected(yes) && !Asking(), TimeSpan.FromSeconds(5)), "YES: that person must be connected, and the question gone");

                main.BeginInvoke(() => main.SomeoneWantsToConnectForTest(no, samePassword: true));
                Check(WaitFor(Asking, TimeSpan.FromSeconds(5)), "the next person must be asked about too");
                said = main.Invoke(() => engine.Execute("answer No"));
                Check(said.StartsWith("ok", StringComparison.Ordinal), $"the question must take No ({said})");
                Check(WaitFor(() => !Asking(), TimeSpan.FromSeconds(5)) && !Connected(no), "NO: that person must be left unconnected");
                var asked = main.AcceptAskCountForTest;
                for (var i = 0; i < 3; i++) main.Invoke(() => main.SomeoneWantsToConnectForTest(no, samePassword: true));
                Thread.Sleep(100);
                Check(!Asking() && main.AcceptAskCountForTest == asked && !Connected(no),
                    $"and must not be asked about again as their packets keep coming ({main.AcceptAskCountForTest - asked} more questions)");
            }, TimeSpan.FromSeconds(30));
            lock (logged)
            {
                Check(logged.Any(l => l.Contains("accept connections: connected to", StringComparison.Ordinal) && l.Contains("198.51.100.41", StringComparison.Ordinal)),
                    "the log must say who was connected");
                Check(logged.Any(l => l.Contains("accept connections: declined", StringComparison.Ordinal) && l.Contains("198.51.100.42", StringComparison.Ordinal)),
                    "and who was declined");
            }
            return "asked, and answered through the control channel: Yes connected that person and the question went; No left them "
                 + "unconnected, was logged, and they were not asked about again";
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
            var restore = AppConfig.Load();
            restore.AcceptPeerConnections = restoreMode;
            try { restore.Save(); } catch { /* the run's own settings folder */ }
        }
    }
}
