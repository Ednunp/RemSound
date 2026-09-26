using System.Collections.Concurrent;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// The lock-screen service sends to the people ticked in its profile, and to nobody else.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>
    /// THE SERVICE SENDS ONLY TO THE PEOPLE TICKED: NOBODY TICKED IS NOBODY.
    ///
    /// <para>Review 2026-09-25, Ed agreed the fix. Unticking every peer in the service profile made the service send to
    /// all of them: an empty tick list was read as "everyone in the list". And the window showed them all ticked again
    /// when it was reopened, so the choice could not even be seen, and pressing OK saved them all ticked. The main app
    /// has always sent to nobody when nobody is ticked. The window is driven here through the control channel, the way
    /// --control drives a headless copy.</para>
    /// </summary>
    private static string? ServiceSendsOnlyToThePeopleTicked()
    {
        const string A = "192.0.2.41", B = "192.0.2.42";
        const string PeerList = "\"Peers to send to\"";

        // The host: nobody ticked is nobody, however many are listed; one ticked is that one.
        var nobody = Profile.NewBlank();
        nobody.RememberedPeers = [A, B];
        nobody.SelectedConnectedPeers = [];
        var none = ServiceSendHost.BuildEndpoints(nobody);
        Check(none.Count == 0,
            $"THE LEAK: with nobody ticked the service must send to nobody, not to everyone in its list ({string.Join(", ", none)})");
        var justB = Profile.NewBlank();
        justB.RememberedPeers = [A, B];
        justB.SelectedConnectedPeers = [B];
        var one = ServiceSendHost.BuildEndpoints(justB);
        Check(one.Count == 1 && one[0].Address.ToString() == B, $"with one ticked it must send to that one only ({string.Join(", ", one)})");

        // And its log says why it sends nothing: with nobody at the screen, the log is the only way to find out.
        var lines = new ConcurrentQueue<string>();
        using (var host = new ServiceSendHost(() => nobody, lines.Enqueue))
            Check(!host.ApplyProfile(nobody), "with nobody ticked the service must not start sending");
        Check(lines.Any(l => l.Contains("nobody is ticked in the service profile", StringComparison.Ordinal)),
            $"the service log must say nobody is ticked (it said: {string.Join(" | ", lines)})");

        // The window: untick both through the control channel, and what it saves ticks nobody but still lists both.
        var both = Profile.NewBlank();
        both.RememberedPeers = [A, B];
        both.SelectedConnectedPeers = [A, B];
        Profile saved;
        using (var dialog = new ServiceProfileDialog(both, false))
        {
            _ = dialog.Handle;
            var engine = new RemoteControlEngine(() => dialog);
            foreach (var peer in new[] { A, B })
            {
                var said = engine.Execute($"check {PeerList} {peer} off");
                Check(said.StartsWith("ok", StringComparison.Ordinal), $"the service's peer list must be drivable from the control channel ({said})");
            }
            saved = dialog.ResultForTest;
        }
        Check(saved.SelectedConnectedPeers.Count == 0 && saved.RememberedPeers.Count == 2,
            $"unticking both must save nobody ticked, with both still listed ({saved.SelectedConnectedPeers.Count} ticked, {saved.RememberedPeers.Count} listed)");
        Check(ServiceSendHost.BuildEndpoints(saved).Count == 0, "and the service must send what was saved: to nobody");

        // Reopened, it must show nobody ticked. It showed everybody, and pressing OK then saved them all ticked again.
        using (var reopened = new ServiceProfileDialog(saved, false))
        {
            _ = reopened.Handle;
            var engine = new RemoteControlEngine(() => reopened);
            var got = engine.Execute($"get {PeerList}");
            Check(got.Contains("2 items, 0 ticked", StringComparison.Ordinal),
                $"THE HIDDEN CHOICE: reopened, the window must show nobody ticked, not everybody (get said: {got.Replace('\n', ' ')})");
            Check(reopened.ResultForTest.SelectedConnectedPeers.Count == 0, "and pressing OK must keep nobody ticked");
        }
        using (var partly = new ServiceProfileDialog(justB, false))
        {
            _ = partly.Handle;
            var got = new RemoteControlEngine(() => partly).Execute($"get {PeerList}");
            Check(got.Contains($"\"{A}\" not ticked", StringComparison.Ordinal) && got.Contains($"\"{B}\" ticked", StringComparison.Ordinal),
                $"a profile with some ticked must show exactly those (get said: {got.Replace('\n', ' ')})");
        }
        return "with nobody ticked the service sends to nobody and its log says so; one ticked is that one; the window, driven "
             + "through the control channel, saves nobody ticked and shows it so when reopened";
    }
}
