using System.Net;
using RemSound.Core;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>METRONOME HANDOVER (Ed, 2026-09-29, the live test: Andre led and Ed was asked, fine; then Ed took the lead -
    /// "said I was master but he never got a prompt and never heard the metronome": Andre's copy, leading, had declined at
    /// once without asking him, and Ed's was told nothing). Both ways now: leading, the window says who follows and who said
    /// no, and its status line keeps it; asked to hand over while it leads, it asks - No is sent, Yes lets its followers go
    /// and follows the new master.</summary>
    private static string? MetronomeHandoverBothWays()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        var logged = new List<string>();
        MainForm? form = null;
        StandInMetronomePeer? andre = null;
        MainForm.MasterWithoutServerForTest = true;   // the asking and answering, directly between windows
        try
        {
            int port;
            try { (form, port) = MetronomeLoopbackWindow("metronome-handover", new MetronomeSettings(), null, logged); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            var main = form;
            main.SomebodyToAnswerForTest = true;
            main.NobodyToAskForTest = () => false;
            var engine = main.MetronomeForTest;
            var control = new RemoteControlEngine(() => main);
            var key = Require(RemSoundCrypto.ForPlainPassword("metronome-handover").Key, "a key");
            var window = new IPEndPoint(IPAddress.Loopback, port);
            bool Asking() => main.Invoke(() => Application.OpenForms.OfType<HeadlessQuestionForm>().Any());
            string Question() => main.Invoke(() => Application.OpenForms.OfType<HeadlessQuestionForm>().First().Question);
            var speechMark = HeadlessRecords.Speech.Mark;
            bool Said(string words) => HeadlessRecords.Speech.Since(speechMark).Any(l => l.Contains(words, StringComparison.Ordinal));
            var takeOver = "";

            DriveWhilePumping(main, () =>
            {
                andre = new StandInMetronomePeer(key, window, "Andre");
                var peer = andre;
                // A change to the stand-in is made on its own thread: wait for it, or the window's offer can get there first.
                void Apply(Action<MetronomeEngine> change)
                {
                    using var done = new ManualResetEventSlim();
                    peer.Do(e => { change(e); done.Set(); });
                    Check(done.Wait(3000), "the stand-in must take the change");
                }
                Apply(e => e.AskAboutOffer = o => e.AnswerOffer(o, accept: false));
                main.Invoke(() => main.SelectPeerForTest(new PeerAnnouncement(Guid.NewGuid(), "Andre", peer.Endpoint.Port, true, true, DateTime.UtcNow, IPAddress.Loopback)));

                // 1. Leading, and Andre says no: the window is told, and its status keeps it.
                main.Invoke(() => main.SetMetronomeMasterForTest(true));
                string Trail() { lock (logged) return string.Join(" | ", logged.Where(l => l.Contains("metronome", StringComparison.Ordinal)).TakeLast(8)) + " || Andre got: " + string.Join(",", peer.Received) + " || said: " + string.Join(" / ", HeadlessRecords.Speech.Since(speechMark)); }
                Check(WaitFor(() => Said("Andre said no to following your metronome"), TimeSpan.FromSeconds(5)),
                    $"NO, TOLD: leading, the window must say who said no ({Trail()})");
                Check(main.Invoke(() => main.MetronomeStatusForTest)?.Contains("Andre said no", StringComparison.Ordinal) == true,
                    $"and its status line must keep it ({main.Invoke(() => main.MetronomeStatusForTest)})");

                // 2. Offered again, and Andre says yes: the window is told.
                Apply(e => e.AskAboutOffer = o => e.AnswerOffer(o, accept: true));
                main.Invoke(() => { main.SetMetronomeMasterForTest(false); main.SetMetronomeMasterForTest(true); });
                Check(WaitFor(() => Said("Andre is following your metronome"), TimeSpan.FromSeconds(5)), "YES, TOLD: the window must say who follows");
                Check(WaitFor(() => main.Invoke(() => engine.Followers.Count) == 1, TimeSpan.FromSeconds(3)), "and count the follower");

                // 3. Andre asks to take over while the window leads: ASKED, never declined unasked. No is sent.
                peer.Do(e => e.BecomeMaster("Andre"));
                Check(WaitFor(Asking, TimeSpan.FromSeconds(5)), "TAKE OVER: leading, the window must be asked when another would take over");
                takeOver = Question();
                Check(takeOver.Contains("Andre would like to take over as the master metronome", StringComparison.Ordinal)
                      && takeOver.Contains("Hand over?", StringComparison.Ordinal), $"in the handover's own words ({takeOver})");
                Check(main.Invoke(() => control.Execute("answer No")).StartsWith("ok", StringComparison.Ordinal), "the question must take No");
                Check(WaitFor(() => peer.Received.Contains(MetronomeKind.Decline), TimeSpan.FromSeconds(3)), "NO: Andre must be told");
                Check(main.Invoke(() => engine.Role) == MetronomeRole.Master, "and the window still leads");

                // 4. Asked again, a new offer, and Yes: the window hands over and follows.
                peer.Do(e => { e.ReleaseMaster(); e.BecomeMaster("Andre"); });
                Check(WaitFor(Asking, TimeSpan.FromSeconds(5)), "asked again at a new offer");
                Check(main.Invoke(() => control.Execute("answer Yes")).StartsWith("ok", StringComparison.Ordinal), "the question must take Yes");
                Check(WaitFor(() => main.Invoke(() => engine.Role == MetronomeRole.Follower && engine.MasterName == "Andre"), TimeSpan.FromSeconds(3)),
                    "YES: the window must hand over and follow Andre");
                Check(WaitFor(() => peer.Engine!.Followers.Count == 1, TimeSpan.FromSeconds(3)), "and Andre count it");
            }, TimeSpan.FromSeconds(60));
            Check(Logged(logged, "asks to take over as the master metronome (asking)") && Logged(logged, "handing over to Andre"), "each logged");
            main.LogForTest.EventTapForTest = null;
            return "leading, told who said no (kept in the status) and who follows; asked when another would take over - No sent and "
                 + "still leading, Yes handed over and followed";
        }
        finally
        {
            MainForm.MasterWithoutServerForTest = false;
            andre?.Dispose();
            try { form?.Dispose(); } catch { /* teardown */ }
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
        }
    }
}
