using System.Net;
using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>PASSWORDS (Ed, 2026-09-29, the live test with Andre: "a prompt or auto connect should only happen if people
    /// are using the same password, not just because they hit connect. that being said, it is good if you try to connect to
    /// someone and it says you're not using the same password. so we keep that prompt."). Andre's default profile had three
    /// of his own machines ticked on other passwords: every load reconnected them and a warning popped for each, and accept
    /// connections took one back as "a device you have ticked before". Now: a peer the profile ticked, or accept connections
    /// took for you, on another password is held - kept in the profile, sent nothing, named in the status line, no pop-up -
    /// and let go when the passwords match; a peer you tick yourself still brings the warning; and an address that has shown
    /// another password is never offered or accepted.</summary>
    private static string? PasswordsDecideConnecting()
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
            main.SomebodyToAnswerForTest = true;
            main.NobodyToAskForTest = () => false;

            // 1. Ticked by the profile, on another password: held quietly.
            var profileAddress = IPAddress.Parse("198.51.100.61");
            var profilePeer = new PeerAnnouncement(Guid.NewGuid(), "ONJTOP", RemPacket.DefaultPort, true, true, DateTime.UtcNow, profileAddress);
            main.SelectPeerForTest(profilePeer);   // as the profile's reconnect ticks
            main.ReceiverForTest.SetPeerSecurityForTest(profileAddress, PeerSecurityStatus.PasswordMismatch);
            DriveWhilePumping(main, () =>
            {
                main.BeginInvoke(main.CheckPeerSecurityForTest);
                Check(!WaitFor(BoxOpen, TimeSpan.FromSeconds(1.5)), "a peer the PROFILE ticked, on another password, must not pop a warning");
            }, TimeSpan.FromSeconds(10));
            Check(main.Invoke(() => main.IsHeldForPasswordForTest(profilePeer.InstanceId)), "it must be held for its password");
            var profileEndpoint = new IPEndPoint(profileAddress, RemPacket.DefaultPort);
            Check(main.Invoke(() => main.EndpointsToArmForTest([profileEndpoint]).Length == 0), "and sent nothing");
            Check(main.Invoke(() => main.StatusTextForTest()).Contains("ONJTOP: a different password, not connected", StringComparison.Ordinal),
                "and the status line must say so, by name");
            Check(Logged("uses a different password") && Logged("not warned about"), "and the log must say why there was no warning");

            // 2. The passwords match again: let go, and sent to.
            main.ReceiverForTest.SetPeerSecurityForTest(profileAddress, PeerSecurityStatus.Secure);
            main.Invoke(main.CheckPeerSecurityForTest);
            Check(main.Invoke(() => !main.IsHeldForPasswordForTest(profilePeer.InstanceId)
                                    && main.EndpointsToArmForTest([profileEndpoint]).Length == 1),
                "on the same password again it must be let go and sent to");
            Check(Logged("is on the same password now"), "and the log must say so");

            // 3. Ticked by you, on another password: the warning, as before.
            var yourAddress = IPAddress.Parse("198.51.100.62");
            var yourPeer = new PeerAnnouncement(Guid.NewGuid(), "Andre", RemPacket.DefaultPort, true, true, DateTime.UtcNow, yourAddress);
            main.SelectPeerByUserForTest(yourPeer);
            main.ReceiverForTest.SetPeerSecurityForTest(yourAddress, PeerSecurityStatus.PasswordMismatch);
            DriveWhilePumping(main, () =>
            {
                main.BeginInvoke(main.CheckPeerSecurityForTest);
                Check(WaitFor(BoxOpen, TimeSpan.FromSeconds(5)), "a peer YOU ticked, on another password, must bring the warning");
                CloseBoxes();
                WaitFor(() => main.Invoke(() => !main.SecurityWarningShowingForTest), TimeSpan.FromSeconds(5));
            }, TimeSpan.FromSeconds(20));
            Check(main.Invoke(() => !main.IsHeldForPasswordForTest(yourPeer.InstanceId)), "and it is not held quietly");

            // 4. Accept connections, automatic: never on another password; yes on ours - and then accepted FOR you.
            { var cfg = AppConfig.Load(); cfg.AcceptPeerConnections = PeerAcceptMode.Automatic; cfg.Save(); }
            var otherPassword = IPAddress.Parse("198.51.100.63");
            main.Invoke(() => main.NoteDifferentPasswordForTest(otherPassword));   // its proof opened with another key
            main.Invoke(() => main.SomeoneWantsToConnectForTest(new IPEndPoint(otherPassword, RemPacket.DefaultPort), samePassword: true));
            Check(main.Invoke(() => !main.IsSelectedForTest(otherPassword)), "an address that has shown another password must never be accepted");
            Check(Logged("uses a different password — not offered or accepted"), "and the log must say why");
            var samePassword = IPAddress.Parse("198.51.100.64");
            main.Invoke(() => main.SomeoneWantsToConnectForTest(new IPEndPoint(samePassword, RemPacket.DefaultPort), samePassword: true));
            Check(main.Invoke(() => main.IsSelectedForTest(samePassword)), "one on our password is accepted (automatic)");
            return "a peer the profile ticked, or accepted for you, on another password is held quietly and named, let go when the "
                 + "passwords match; one you tick is warned about; an address that has shown another password is never accepted";
        }
        finally
        {
            { var cfg = AppConfig.Load(); cfg.AcceptPeerConnections = restoreMode; cfg.Save(); }
            try { form?.Dispose(); } catch { /* teardown */ }
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
        }
    }
}
