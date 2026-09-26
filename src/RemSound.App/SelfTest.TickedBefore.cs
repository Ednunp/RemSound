using System.Net;
using System.Reflection;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// A DEVICE TICKED BEFORE IS LET BACK IN WHEN IT CONNECTS TO LISTEN (Ed, 2026-09-26: "build option 2"). A phone that only
/// listens sends nothing that proves the password until its app sends the proof - only pings. So a device you have ticked
/// is remembered by its own identity, and its ping is treated as somebody wanting to connect, as Accept connections says.
/// </summary>
internal static partial class SelfTest
{
    private static string? ADeviceTickedBeforeIsLetBackIn()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        var restoreMode = AppConfig.Load().AcceptPeerConnections;
        var restoreIds = AppConfig.Load().TickedDeviceIds.ToList();
        var phone = new PeerAnnouncement(Guid.NewGuid(), "iPhone", RemPacket.DefaultPort, true, true, DateTime.UtcNow, IPAddress.Parse("192.168.1.8"));
        var hers = new PeerAnnouncement(Guid.NewGuid(), "iPhone", RemPacket.DefaultPort, true, true, DateTime.UtcNow, IPAddress.Parse("192.168.1.241"));
        var lines = new List<string>();

        static byte[] Ping()
        {
            var packet = new byte[RemPacket.HeaderSize + RemPacket.HeartbeatPayloadSize];
            RemPacket.WriteHeader(packet, RemPacketType.Heartbeat, 0xFFFF, 1);
            RemPacket.WriteHeartbeatPayload(packet.AsSpan(RemPacket.HeaderSize), HeartbeatKind.Ping, 12345);
            return packet;
        }

        // One session: a window that hears both phones, and pings from each.
        bool Session(Action<MainForm> before, out bool phoneIn, out bool hersIn)
        {
            MainForm? form = null;
            HeartbeatService? heartbeat = null;
            try
            {
                form = new MainForm(null, Profile.NewBlank(), null, null, headless: true);
                _ = form.Handle;
                form.LogForTest.EventTapForTest = line => { lock (lines) lines.Add(line); };
                var discovery = (PeerDiscoveryService)Require(typeof(MainForm).GetField("discovery", BindingFlags.Instance | BindingFlags.NonPublic), "MainForm.discovery not found").GetValue(form)!;
                discovery.RecordForTest(phone);
                discovery.RecordForTest(hers);
                Require(typeof(MainForm).GetMethod("RefreshKnownPeers", BindingFlags.Instance | BindingFlags.NonPublic), "MainForm.RefreshKnownPeers not found").Invoke(form, null);
                heartbeat = form.StartHeartbeatForTest((_, _, _) => true);
                before(form);
                var ping = Ping();
                heartbeat.HandleInjectedPacket(ping, ping.Length, new IPEndPoint(phone.Address, 50001));
                heartbeat.HandleInjectedPacket(ping, ping.Length, new IPEndPoint(hers.Address, 50002));
                var until = DateTime.UtcNow.AddSeconds(2);
                while (DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(10); }
                var sending = form.SelectedSendEndpointsForTest();
                phoneIn = sending.Any(e => e.Address.Equals(phone.Address));
                hersIn = sending.Any(e => e.Address.Equals(hers.Address));
                return true;
            }
            finally
            {
                try { heartbeat?.Dispose(); } catch { /* teardown */ }
                try { form?.Dispose(); } catch { /* teardown */ }
            }
        }

        try
        {
            var cleared = AppConfig.Load();
            cleared.TickedDeviceIds.Clear();
            cleared.Save();
            SetAcceptMode(PeerAcceptMode.Automatic);

            // Never ticked: a listening phone's ping is nobody's proof of anything.
            Session(_ => { }, out var phoneIn, out var hersIn);
            Check(!phoneIn && !hersIn, "a device never ticked must not be let in by a ping - pings prove nothing");

            // Ticked once, by hand, in the Discovered list.
            MainForm? tickForm = null;
            try
            {
                tickForm = new MainForm(null, Profile.NewBlank(), null, null, headless: true);
                var discovery = (PeerDiscoveryService)Require(typeof(MainForm).GetField("discovery", BindingFlags.Instance | BindingFlags.NonPublic), "MainForm.discovery not found").GetValue(tickForm)!;
                discovery.RecordForTest(phone);
                var remember = Require(typeof(MainForm).GetMethod("EnsurePeerRemembered", BindingFlags.Instance | BindingFlags.NonPublic), "MainForm.EnsurePeerRemembered not found");
                remember.Invoke(tickForm, [phone]);   // what ticking a device in the Discovered list does
            }
            finally { try { tickForm?.Dispose(); } catch { /* teardown */ } }
            Check(AppConfig.Load().TickedDeviceIds.Contains(phone.InstanceId.ToString("D")), "a device you tick must be remembered by its own identity");

            // A new session: the phone connects to listen again.
            Session(_ => { }, out phoneIn, out hersIn);
            Check(phoneIn,
                "THE UNACCEPTED LISTENER: a device you have ticked before must be let back in when it connects just to listen - Ed's phone had to be ticked by hand every time");
            Check(!hersIn, "THE OTHER IPHONE: a device with the same name that was never ticked must stay out - it is a different device");
            Check(lines.Any(l => l.Contains("is a device you have ticked before", StringComparison.Ordinal)), "THE LOG: the reason it was let in must be logged");

            // Manual: nothing happens by itself, as with everybody else.
            SetAcceptMode(PeerAcceptMode.Manual);
            Session(_ => { }, out phoneIn, out _);
            Check(!phoneIn, "on Manual a device ticked before must still wait for you, as everybody does");
            Check(!lines.Any(l => l.Contains("(mode=Manual)", StringComparison.Ordinal)),
                "THE UNWANTED QUESTION: on Manual nothing happens by itself - not even a question");

            // Clearing the remembered peers forgets it.
            SetAcceptMode(PeerAcceptMode.Automatic);
            Session(form => form.ClearRememberedPeersForTest(), out _, out _);
            Check(AppConfig.Load().TickedDeviceIds.Count == 0, "clearing the remembered peers list must forget the devices ticked too");
            Session(_ => { }, out phoneIn, out _);
            Check(!phoneIn, "and a forgotten device is not let back in");
        }
        finally
        {
            var restore = AppConfig.Load();
            restore.TickedDeviceIds.Clear();
            restore.TickedDeviceIds.AddRange(restoreIds);
            restore.Save();
            try { SetAcceptMode(restoreMode); } catch { /* best effort */ }
            CuePlayer.GloballyMuted = restoreMuted;
        }
        return "a phone never ticked stayed out; ticked once it was remembered by its own identity and let back in when it connected "
             + "to listen, while another iPhone never ticked stayed out; Manual waited; clearing the remembered peers forgot it";
    }
}
