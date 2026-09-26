using System.Net;
using System.Reflection;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// TWO DEVICES WITH ONE NAME ARE NEVER MIXED UP. Ed's live phone test, 2026-09-25: his iPhone at .8 and his wife's at .241,
/// both "iPhone" (every iPhone's name until somebody changes it), both running RemSound. His phone was accepted - and a
/// second later the stream went to hers. The accepted phone had been filed as a hand-typed NAME, and the rule that joins a
/// typed name to the one device of that name found hers (his own dropped out of the count) and moved the pin before his
/// phone had answered its first ping. It was also remembered as "iPhone", which finds either phone next time.
/// </summary>
internal static partial class SelfTest
{
    private static string? TwoDevicesWithOneNameAreNeverMixedUp()
    {
        var saved = AppConfig.Load();
        var restoreMode = saved.AcceptPeerConnections;
        MainForm? form = null;
        try
        {
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            object Field(string name) =>
                Require(Require(typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic), $"MainForm.{name} not found")
                    .GetValue(form), $"MainForm.{name} was null");
            var settings = (RemSoundSettingsStore)Field("settings");
            settings.SaveLockPeerAddresses(false);   // the joining rule runs only with the lock off, as Ed's copy had it
            var discovery = (PeerDiscoveryService)Field("discovery");
            var manualPeers = (Dictionary<Guid, PeerAnnouncement>)Field("manualPeers");
            var pinned = (Dictionary<Guid, IPEndPoint>)Field("selectedPeerEndpoints");
            var refresh = Require(typeof(MainForm).GetMethod("RefreshKnownPeers", BindingFlags.Instance | BindingFlags.NonPublic), "MainForm.RefreshKnownPeers not found");
            void Refresh() => refresh.Invoke(form, null);

            const int Port = 47830;
            var his = new PeerAnnouncement(Guid.NewGuid(), "iPhone", Port, true, true, DateTime.UtcNow, IPAddress.Parse("192.168.1.8"));
            var hers = new PeerAnnouncement(Guid.NewGuid(), "iPhone", Port, true, true, DateTime.UtcNow, IPAddress.Parse("192.168.1.241"));
            discovery.RecordForTest(his);
            discovery.RecordForTest(hers);
            Refresh();

            // His phone ticks us (audio arriving - the path every release has; the password proof lands on the same decision).
            SetAcceptMode(PeerAcceptMode.Automatic);
            form.SomeoneWantsToConnectForTest(new IPEndPoint(his.Address, 50001), samePassword: true);
            IPEndPoint[] Sending() => form!.SelectedSendEndpointsForTest();
            Check(Sending().Any(e => e.Address.Equals(his.Address)) && !Sending().Any(e => e.Address.Equals(hers.Address)),
                $"THE WRONG PHONE: accepting his phone must connect HIS phone, not the other device of that name (sending to: {string.Join(", ", Sending().Select(e => e.ToString()))})");

            // The refresh that moved it to her phone on the night - several, as the window runs them.
            for (var i = 0; i < 3; i++) Refresh();
            Check(Sending().Any(e => e.Address.Equals(his.Address)) && !Sending().Any(e => e.Address.Equals(hers.Address)),
                $"THE WRONG PHONE: the stream must stay on the phone that was accepted and never move to the other device of that name (sending to: {string.Join(", ", Sending().Select(e => e.ToString()))})");
            Check(pinned.TryGetValue(his.InstanceId, out var pin) && pin.Address.Equals(his.Address), "pinned to his phone, by its own identity");
            Check(!manualPeers.ContainsKey(his.InstanceId), "a phone discovery hears must be held by its own identity, not filed as a hand-typed entry");

            // Remembered by address, because "iPhone" would find either phone next time.
            var remembered = settings.LoadRememberedPeers();
            Check(remembered.Contains("192.168.1.8") && !remembered.Contains("iPhone"),
                $"THE NAME: a device whose name another device shares must be remembered by its address (remembered: {string.Join(", ", remembered)})");

            // His phone goes quiet (asleep); hers is still announcing. Still nothing moves to hers.
            discovery.BackdateForTest(his.InstanceId, TimeSpan.FromMinutes(5));
            for (var i = 0; i < 3; i++) Refresh();
            Check(!Sending().Any(e => e.Address.Equals(hers.Address)),
                "with his phone quiet and hers announcing, the stream must still never move to hers");

            // A name somebody TYPED still joins the one device of that name - the rule's own job, kept.
            var typed = new PeerAnnouncement(Guid.NewGuid(), "Studio", Port, true, true, DateTime.UtcNow, IPAddress.Parse("10.0.0.5"));
            manualPeers[typed.InstanceId] = typed;
            pinned[typed.InstanceId] = new IPEndPoint(typed.Address, Port);
            var studio = new PeerAnnouncement(Guid.NewGuid(), "Studio", Port, true, true, DateTime.UtcNow, IPAddress.Parse("10.0.0.6"));
            discovery.RecordForTest(studio);
            Refresh();
            Check(pinned.TryGetValue(studio.InstanceId, out var joined) && joined.Address.Equals(studio.Address) && !pinned.ContainsKey(typed.InstanceId),
                "a name somebody typed must still join the ONE device of that name on the network - that rule's own job");
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
            SetAcceptMode(restoreMode);
        }
        return "two phones both called \"iPhone\": the accepted one stayed on its own identity and address through every refresh, "
             + "even once it went quiet and the other kept announcing; it was remembered by address, not by the shared name; and a "
             + "name somebody typed still joined the one device of that name";
    }
}
