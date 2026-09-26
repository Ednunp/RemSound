using System.Net;
using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App;

/// <summary>
/// Waiting properly (2026-09-25 sweep; Ed: "yes. fix everything that you suggested"). The password warning stopped the
/// 1 Hz tick for as long as it waited, and with it the output heal, the plugin sweep and the tuner; the quick-switch
/// hotkey closed the window from inside another window's wait; and a --silent copy (not --headless) that met a real peer
/// could ring a question up in front of Ed.
/// </summary>
internal static partial class SelfTest
{
    private static string? WarningsAndQuestionsWaitProperly()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        var restoreMode = AppConfig.Load().AcceptPeerConnections;
        MainForm? form = null;
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        try
        {
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            var main = form;
            var lines = new List<string>();
            main.LogForTest.EventTapForTest = line => { lock (lines) lines.Add(line); };
            bool Logged(string words) { lock (lines) return lines.Any(l => l.Contains(words, StringComparison.Ordinal)); }
            bool BoxOpen() => main.Invoke(() => Application.OpenForms.OfType<HeadlessQuestionForm>().Any());
            void CloseBoxes() => main.Invoke(() => { foreach (var f in Application.OpenForms.OfType<HeadlessQuestionForm>().ToList()) f.Close(); });

            // A peer, selected, whose password does not match.
            var peerAddress = IPAddress.Parse("198.51.100.44");
            var peer = new PeerAnnouncement(Guid.NewGuid(), "Wrong password", RemPacket.DefaultPort, true, true, DateTime.UtcNow, peerAddress);
            main.SelectPeerForTest(peer);
            main.ReceiverForTest.SetPeerSecurityForTest(peerAddress, PeerSecurityStatus.PasswordMismatch);

            // 1. With somebody to read it, the warning shows - and the tick carries on under it.
            main.SomebodyToAnswerForTest = true;
            main.NobodyToAskForTest = () => false;
            main.StartStatusTimerForTest();
            DriveWhilePumping(main, () =>
            {
                main.BeginInvoke(main.CheckPeerSecurityForTest);
                Check(WaitFor(BoxOpen, TimeSpan.FromSeconds(5)), "premise: the password warning shows");
                var snapshotAt = main.Invoke(() => main.LastSnapshotUtcForTest);
                var syncsAt = main.Invoke(() => main.PeerListSyncsForTest);
                Thread.Sleep(2500);
                Check(main.Invoke(() => main.StatusTimerRunningForTest && main.LastSnapshotUtcForTest > snapshotAt),
                    "THE FROZEN TICK: while the password warning waits, the once-a-second tick must keep running - it heals outputs, sweeps the plugin and tunes");
                Check(main.Invoke(() => main.PeerListSyncsForTest) == syncsAt,
                    "THE FLASH: but the peer lists must not be rebuilt under the warning - that knocked it out of the foreground (Ed, 2026-06-11)");
                CloseBoxes();
                Check(WaitFor(() => main.Invoke(() => !main.SecurityWarningShowingForTest), TimeSpan.FromSeconds(5)), "the warning must close");
                Check(WaitFor(() => main.Invoke(() => main.PeerListSyncsForTest) > syncsAt, TimeSpan.FromSeconds(3)),
                    "and once it has closed the lists are kept up to date again");
            }, TimeSpan.FromSeconds(30));

            // 2. The quick-switch hotkey while another window waits for an answer: not now, and said.
            DriveWhilePumping(main, () =>
            {
                main.BeginInvoke(() => { using var other = new Form { Text = "Preferences" }; other.ShowDialog(main); });
                Check(WaitFor(() => main.Invoke(() => Application.OpenForms.Cast<Form>().Any(f => f.Text == "Preferences" && f.Modal)), TimeSpan.FromSeconds(5)),
                    "premise: another window is waiting");
                var mark = HeadlessRecords.Speech.Mark;
                main.BeginInvoke(main.QuickProfileSwitchForTest);
                Check(WaitFor(() => HeadlessRecords.Speech.Since(mark).Any(s => s.Contains("Close the Preferences window first, then switch profile.", StringComparison.Ordinal)),
                        TimeSpan.FromSeconds(5)),
                    $"THE LOST WINDOW: the quick switch must wait for the open window, and say so (said: {string.Join(" | ", HeadlessRecords.Speech.Since(mark))})");
                Check(!main.Invoke(() => Application.OpenForms.OfType<QuickProfileSwitchDialog>().Any()), "and must not open over it");
                main.Invoke(() => { foreach (var f in Application.OpenForms.Cast<Form>().Where(f => f.Text == "Preferences").ToList()) f.Close(); });
            }, TimeSpan.FromSeconds(20));

            // 5. A silent copy: nobody to ask, whatever the window flag says - no question, no offer, no warning.
            main.NobodyToAskForTest = null;
            Check(main.NobodyToAnswerForTest == Windowless.NobodyToAsk,
                "THE RULE: whether there is somebody to answer must come from Windowless.NobodyToAsk, the rule every start-up notice keeps");
            main.NobodyToAskForTest = () => true;
            SetAcceptMode(PeerAcceptMode.Prompt);
            var asker = new IPEndPoint(IPAddress.Parse("198.51.100.45"), RemPacket.DefaultPeerDialPort);
            var server = new IPEndPoint(peerAddress, RemPacket.DefaultPort);
            main.ReceiverForTest.SetPeerSecurityForTest(peerAddress, PeerSecurityStatus.Secure);
            main.Invoke(main.CheckPeerSecurityForTest);   // forget the warning above, so the next one counts
            main.ReceiverForTest.SetPeerSecurityForTest(peerAddress, PeerSecurityStatus.PasswordMismatch);
            DriveWhilePumping(main, () =>
            {
                main.BeginInvoke(() => main.SomeoneWantsToConnectForTest(asker, samePassword: true));
                main.BeginInvoke(() => main.OfferRelayForTest(server));
                main.BeginInvoke(main.CheckPeerSecurityForTest);
                Thread.Sleep(1500);
                var asked = BoxOpen();
                if (asked) CloseBoxes();
                Check(!asked, "THE DING: a silent copy must ask nobody anything - not whether to accept, not whether to connect to a server, and not warn");
            }, TimeSpan.FromSeconds(20));
            Check(Logged("accept connections: nobody to ask here"), "THE LOG: the unasked question must be logged");
            Check(Logged("answers as a relay — nobody to ask here"), "THE LOG: the unmade offer must be logged");
            Check(Logged($"security: nobody here to warn about {peerAddress}"), "THE LOG: the unshown warning must be logged");
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            Windowless.SetActiveForTest(false);
            SetAcceptMode(restoreMode);
            CuePlayer.GloballyMuted = restoreMuted;
        }
        return "the password warning showed while the tick kept running and the peer lists waited; the quick switch waited for an "
             + "open window and said so; and a silent copy asked nobody anything, logging each unasked question instead";
    }
}
