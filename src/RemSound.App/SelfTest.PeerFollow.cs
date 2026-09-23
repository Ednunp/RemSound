using System.Net;
using System.Reflection;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// A REMEMBERED PEER THE DNS GOT WRONG IS STILL FOUND, and a pinned address that dies is still left.
///
/// <para>A profile reconnects its peers 140 ms after launch, before discovery has heard anybody, so a
/// name is resolved by DNS and pinned wherever DNS says. For a phone that is a stale lease as often
/// as not: the same iPhone resolved to .28, .36, .44, .48, 129.11 and 129.35 across a month of logs.
/// A second later discovery hears the real phone, under its own instance id, at the address it is
/// actually on - and the two were only ever joined when the addresses happened to agree. On
/// 2026-09-04 they did not: the app heartbeated 192.168.69.36 for four minutes, "unreachable" in
/// every snapshot, while the phone pinged us from .48 the whole time. No session, no sound.</para>
///
/// <para>The rule meant to rescue that - follow a selected peer to its new address once the old one
/// has been unreachable for a spell - had never fired in any log since it was written on 2026-05-31,
/// because it demanded a heartbeat answer from the candidate, and the heartbeat only ever pings the
/// pinned address. A question that could not be answered.</para>
/// </summary>
internal static partial class SelfTest
{
    private static string? RememberedPeerFollowsDiscovery()
    {
        MainForm form;
        try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
        catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }

        using (form)
        {
            // By private field and private method, the same way the other window audits reach in.
            FieldInfo FieldInfo(string name) =>
                Require(typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic),
                    $"MainForm.{name} not found - a renamed field would leave this test driving nothing");
            object Field(string name) =>
                Require(FieldInfo(name).GetValue(form), $"MainForm.{name} was null - this test would be driving nothing");
            MethodInfo Method(string name) =>
                Require(typeof(MainForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic),
                    $"MainForm.{name} not found - this test would be driving nothing");

            if (((RemSoundSettingsStore)Field("settings")).LoadLockPeerAddresses())
            {
                return Skip("this machine has 'lock peer addresses' on, which switches off the very merge under test");
            }

            var manualPeers = (Dictionary<Guid, PeerAnnouncement>)Field("manualPeers");
            var pinned = (Dictionary<Guid, IPEndPoint>)Field("selectedPeerEndpoints");
            var labels = (Dictionary<Guid, string>)Field("selectedPeerLabels");
            var remembered = (Dictionary<string, Guid>)Field("rememberedPeerInstanceIds");
            var downSince = (Dictionary<Guid, DateTime>)Field("endpointUnreachableSinceUtc");
            var discovery = (PeerDiscoveryService)Field("discovery");
            var heartbeatField = FieldInfo("heartbeatService");
            var list = (System.Windows.Forms.CheckedListBox)Field("connectedPeersList");
            var refresh = Method("RefreshKnownPeers");
            var syncConnected = Method("SyncConnectedList");

            void Refresh() => refresh.Invoke(form, null);
            string Rows()
            {
                syncConnected.Invoke(form, null);
                return string.Join(" | ", list.Items.Cast<object>().Select(i => i?.ToString() ?? ""));
            }
            // A pong from this address, so the heartbeat calls the endpoint Healthy. Real packet,
            // real parser: the point is that the app's own "does it answer" question says yes.
            static HeartbeatService Answering(IPEndPoint endpoint)
            {
                var heartbeat = new HeartbeatService();
                heartbeat.SetTrackedPeers([endpoint]);
                var pong = new byte[RemPacket.HeaderSize + RemPacket.HeartbeatPayloadSize];
                RemPacket.WriteHeader(pong, RemPacketType.Heartbeat, 0xFFFF, 1);
                RemPacket.WriteHeartbeatPayload(pong.AsSpan(RemPacket.HeaderSize), HeartbeatKind.Pong, 0);
                heartbeat.HandleInjectedPacket(pong, pong.Length, new IPEndPoint(endpoint.Address, 50016));
                return heartbeat;
            }

            const int Port = 47830;
            var dnsSaid = IPAddress.Parse("192.168.69.36");
            var lan = IPAddress.Parse("192.168.69.48");
            var vpn = IPAddress.Parse("100.118.45.53");

            // --- 1. DNS was wrong and discovery is right ------------------------------------------
            // Exactly the 2026-09-04 log: the profile's "iPhone" resolved to .36, the phone is on .48.
            var guess = new PeerAnnouncement(Guid.NewGuid(), "iPhone", Port, true, true, DateTime.UtcNow, dnsSaid);
            manualPeers[guess.InstanceId] = guess;
            pinned[guess.InstanceId] = new IPEndPoint(dnsSaid, Port);
            labels[guess.InstanceId] = "iPhone";
            remembered["iPhone"] = guess.InstanceId;

            var phone = Guid.NewGuid();
            discovery.RecordForTest(new PeerAnnouncement(phone, "iPhone", Port, true, true, DateTime.UtcNow, lan));
            Refresh();

            Check(pinned.TryGetValue(phone, out var nowPinned) && nowPinned.Address.Equals(lan) && !pinned.ContainsKey(guess.InstanceId),
                "a remembered NAME that DNS resolved to an address that never answered must join the discovered peer of that "
              + $"name, at the address discovery heard them on (pinned: {string.Join(", ", pinned.Values)}). Before this the "
              + "app heartbeated the DNS guess for the whole session while the phone sat one row down in Discovered");
            Check(!manualPeers.ContainsKey(guess.InstanceId), "the DNS guess must be gone, or the next refresh re-creates the duplicate");
            Check(remembered.TryGetValue("iPhone", out var mapped) && mapped == phone,
                "the remembered entry must now name the discovered peer, so ticking it later selects them rather than asking DNS again");
            Check(labels.GetValueOrDefault(phone) == "iPhone", $"the label follows too (labels: {string.Join(", ", labels.Values)})");
            Check(Rows().Contains("iPhone (192.168.69.48)"), $"and the connected row names the phone where it is (rows: {Rows()})");

            // --- 2. A typed address that ANSWERS is the user's choice, and stays ------------------
            var serverVpn = IPAddress.Parse("100.81.123.86");
            var serverLan = IPAddress.Parse("192.168.69.69");
            var typed = new PeerAnnouncement(Guid.NewGuid(), "HOMESERV", Port, true, true, DateTime.UtcNow, serverVpn);
            manualPeers[typed.InstanceId] = typed;
            pinned[typed.InstanceId] = new IPEndPoint(serverVpn, Port);
            labels[typed.InstanceId] = "HOMESERV";
            heartbeatField.SetValue(form, Answering(new IPEndPoint(serverVpn, Port)));
            var server = Guid.NewGuid();
            discovery.RecordForTest(new PeerAnnouncement(server, "homeserv", Port, true, true, DateTime.UtcNow, serverLan));
            Refresh();
            Check(pinned.TryGetValue(server, out var kept) && kept.Address.Equals(serverVpn) && !pinned.ContainsKey(typed.InstanceId),
                "the identity joins (case-insensitively) but a typed address that is answering heartbeats is NOT swapped for "
              + $"the one discovery heard last - that is the user's choice of path (pinned: {string.Join(", ", pinned.Values)})");

            // --- 3. Two discovered peers of one name: ambiguous, nothing moves --------------------
            var studio = new PeerAnnouncement(Guid.NewGuid(), "Studio", Port, true, true, DateTime.UtcNow, IPAddress.Parse("10.0.0.5"));
            manualPeers[studio.InstanceId] = studio;
            pinned[studio.InstanceId] = new IPEndPoint(studio.Address, Port);
            labels[studio.InstanceId] = "Studio";
            discovery.RecordForTest(new PeerAnnouncement(Guid.NewGuid(), "Studio", Port, true, true, DateTime.UtcNow, IPAddress.Parse("10.0.0.6")));
            discovery.RecordForTest(new PeerAnnouncement(Guid.NewGuid(), "STUDIO", Port, true, true, DateTime.UtcNow, IPAddress.Parse("10.0.0.7")));
            Refresh();
            Check(pinned.ContainsKey(studio.InstanceId) && manualPeers.ContainsKey(studio.InstanceId),
                "two discovered peers of the same name is ambiguous, and a typed entry of that name must be left exactly as it is");

            // --- 4. The follow rule can fire now ---------------------------------------------------
            // The phone from step 1 moves for real: the LAN path is gone, it announces from the VPN.
            heartbeatField.SetValue(form, null);                  // nothing answers at .48 any more
            discovery.RecordForTest(new PeerAnnouncement(phone, "iPhone", Port, true, true, DateTime.UtcNow, vpn));
            Refresh();
            Check(pinned[phone].Address.Equals(lan),
                "the first unhealthy tick must NOT move the pin - the grace period is what stops a blip from re-routing audio");
            downSince[phone] = DateTime.UtcNow - TimeSpan.FromSeconds(10);   // ten seconds down
            Refresh();
            Check(pinned[phone].Address.Equals(vpn),
                "after the grace, with discovery hearing the phone from its other address, the pin must follow. It never had: "
              + "the rule asked whether the candidate answered heartbeats, and the heartbeat only ever pings the pinned address "
              + $"(pinned: {pinned[phone]})");

            // --- 5. And it still never leaves an address that answers (#16) -----------------------
            heartbeatField.SetValue(form, Answering(new IPEndPoint(vpn, Port)));
            discovery.RecordForTest(new PeerAnnouncement(phone, "iPhone", Port, true, true, DateTime.UtcNow, lan));   // heard on the LAN again
            downSince[phone] = DateTime.UtcNow - TimeSpan.FromSeconds(10);
            Refresh();
            Check(pinned[phone].Address.Equals(vpn),
                "a pinned address that is answering must never be left for the one discovery happened to hear last - that "
              + "ping-pong is what tore sessions down fast enough to crash the app (#16)");
            Check(!downSince.ContainsKey(phone), "...and its unreachable clock is reset, so the next blip starts from zero");

            return "a remembered name DNS got wrong joins the discovered peer at its real address; a typed address that answers "
                 + "stays; two peers of one name are left alone; a pinned address that dies is followed after the grace, "
                 + "and one that answers is never left";
        }
    }
}
