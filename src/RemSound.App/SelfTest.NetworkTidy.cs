using System.Net;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Five smaller network faults from the 2026-09-25 sweep (Ed: "yes all 10"): discovery's announce list grew without
/// limit; every change on a server list re-selected everyone ticked; a ticked person who left showed in both lists; the
/// first-time server message saved an old copy of the settings; and the server tick list could drop somebody present.
/// </summary>
internal static partial class SelfTest
{
    private static string? NetworkTidyHolds()
    {
        // 6. Discovery: whoever announces is announced back to - capped, and let go once quiet.
        var discovery = new PeerDiscoveryService("Tidy check");
        var now = DateTime.UtcNow;
        discovery.SetUnicastPeerAddresses([IPAddress.Parse("10.1.1.1")]);
        for (var i = 0; i < 200; i++) discovery.HeardFromForTest(IPAddress.Parse($"10.2.{i / 250}.{i % 250 + 1}"), now);
        var targets = discovery.AnnounceTargetsForTest(now);
        Check(targets.Count <= PeerDiscoveryService.MaxHeardFrom + 1 && targets.Contains(IPAddress.Parse("10.1.1.1")),
            $"THE FLOOD: 200 addresses announcing must not all be announced back to - capped at {PeerDiscoveryService.MaxHeardFrom}, the app's own hints kept ({targets.Count})");
        var later = discovery.AnnounceTargetsForTest(now + PeerDiscoveryService.HeardFromExpiry + TimeSpan.FromSeconds(1));
        Check(later.Count == 1 && later[0].Equals(IPAddress.Parse("10.1.1.1")), $"and once they have gone quiet, only the hints are left ({later.Count})");

        // 10. The server tick list: somebody present goes in, however many absent people are ticked.
        var client = new RelayGroupClient(Guid.NewGuid());
        var relay = new IPEndPoint(IPAddress.Parse("203.0.113.90"), RemPacket.DefaultPort);
        client.Connect(relay);
        client.NoteRelay(relay);
        var present = Guid.NewGuid();
        Feed(client, relay, RosterPacket([(present, "Here now")], paired: false), RelayInbound.Consumed, "a member list");
        var absent = Enumerable.Range(0, 80).Select(_ => Guid.NewGuid()).ToList();
        client.SetTicked(absent.Append(present), pairPartner: false);
        var hello = client.HelloForTest();
        var at = RelayGroupClient.GroupHeaderSize + RelayGroupClient.NameBytes + RelayGroupClient.GroupTagBytes;   // after the name and the group tag
        var count = hello[at];
        var ids = Enumerable.Range(0, count).Select(i => new Guid(hello.AsSpan(at + 1 + i * RelayGroupClient.ClientIdSize, 16), bigEndian: true)).ToList();
        Check(count == RelayGroupClient.MaxTickedIds && ids.Contains(present),
            $"THE DROPPED TICK: with more than {RelayGroupClient.MaxTickedIds} ticked, the person on the server now must be in the hello ({count} ids, present: {ids.Contains(present)})");

        // 7, 8. A server list changing: the ticked are not re-selected; a ticked person who leaves is not in both lists.
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        MainForm? form = null;
        try
        {
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            var lines = new List<string>();
            form.LogForTest.EventTapForTest = line => { lock (lines) lines.Add(line); };
            var server = new IPEndPoint(IPAddress.Parse("203.0.113.91"), RemPacket.DefaultPort);
            form.ConnectToRelayForTest("tidy.example.test", server);
            var alice = Guid.NewGuid();
            Feed(form.RelayGroupForTest, server, RosterPacket([(alice, "Alice", true)], paired: false), RelayInbound.Consumed, "a member list");
            form.SyncRelayPeersListForTest();
            form.RelayPeerTickedForTest(0, true);
            form.SyncRelayPeersListForTest();
            int Selections() { lock (lines) return lines.Count(l => l.Contains("peer selected: Alice", StringComparison.Ordinal)); }
            var selectedAtFirst = Selections();
            Check(selectedAtFirst >= 1, "premise: Alice ticked and selected");
            for (var i = 0; i < 5; i++)
            {
                Feed(form.RelayGroupForTest, server, RosterPacket([(alice, "Alice", true), (Guid.NewGuid(), $"Joiner {i}", false)], paired: false), RelayInbound.Consumed, "a member list");
                form.SyncRelayPeersListForTest();
            }
            Check(Selections() == selectedAtFirst,
                $"THE CHURN: people joining the server must not re-select Alice - each one wrote a log line and threw away the tuner's readings ({Selections() - selectedAtFirst} more selections)");

            // Alice leaves: in Connected peers, marked offline - not in the server list as well.
            Feed(form.RelayGroupForTest, server, RosterPacket([(Guid.NewGuid(), "Someone else", false)], paired: false), RelayInbound.Consumed, "a member list");
            form.SyncRelayPeersListForTest();
            var rows = form.RelayPeersListForTest.Items.Cast<object>().Select(o => o?.ToString() ?? "").ToList();
            Check(!rows.Any(r => r.Contains("Alice", StringComparison.Ordinal)),
                $"THE DOUBLE LISTING: somebody you are connected to who leaves must not also show in the server list ({string.Join(" | ", rows)})");
            form.DisconnectFromRelayForTest();
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            CuePlayer.GloballyMuted = restoreMuted;
        }

        // 9. The first-time server message saves onto the settings as they are when it closes.
        var saved = AppConfig.Load();
        var restoreSuppressed = saved.RelayNoticeSuppressed;
        var restoreRelays = saved.RememberedRelays.ToList();
        form = null;
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        try
        {
            saved.RelayNoticeSuppressed = false;
            saved.Save();
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            var main = form;
            var show = Require(typeof(MainForm).GetMethod("ShowRelayNotice", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
                "MainForm.ShowRelayNotice not found");
            DriveWhilePumping(main, () =>
            {
                main.BeginInvoke(() => show.Invoke(main, null));
                Check(WaitFor(() => main.Invoke(() => Application.OpenForms.OfType<RelayNoticeDialog>().Any()), TimeSpan.FromSeconds(5)), "premise: the message is showing");
                // Something else writes the settings while it is open.
                var meanwhile = AppConfig.Load();
                meanwhile.RememberedRelays.Insert(0, "written.while.open.test");
                meanwhile.Save();
                main.Invoke(() =>
                {
                    var notice = Application.OpenForms.OfType<RelayNoticeDialog>().First();
                    Check(notice.Owner is not MainForm,
                        "THE OWNER: the message must belong to the window brought to the front, not a main window that may be in the tray");
                    Require(FieldOf<AccessibleCheckBox>(notice, "dontShowAgainBox"), "RelayNoticeDialog.dontShowAgainBox not found").Checked = true;
                    notice.Close();
                });
                Check(WaitFor(() => main.Invoke(() => !Application.OpenForms.OfType<RelayNoticeDialog>().Any()), TimeSpan.FromSeconds(5)), "the message must close");
            }, TimeSpan.FromSeconds(20));
            var after = AppConfig.Load();
            Check(after.RelayNoticeSuppressed, "premise: \"don't show this again\" is saved");
            Check(after.RememberedRelays.Contains("written.while.open.test"),
                "THE OLD COPY: what was written while the message was open must survive its save");
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            Windowless.SetActiveForTest(false);
            var restore = AppConfig.Load();
            restore.RelayNoticeSuppressed = restoreSuppressed;
            restore.RememberedRelays.Clear();
            restore.RememberedRelays.AddRange(restoreRelays);
            restore.Save();
        }
        return "discovery's announce list stayed capped and let quiet addresses go; with 80 absent people ticked, the one on the server was "
             + "in the hello; people joining a server did not re-select the ticked; a ticked person who left was not in both lists; and "
             + "the first-time message kept what was written while it was open";
    }
}
