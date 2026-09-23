using System.Net;
using System.Reflection;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// THE CONNECTED-PEER LIST MUST HOLD STILL, and it must name the address the audio is going to.
///
/// <para>Discovery keeps one announcement per peer and overwrites it every time one arrives, so
/// somebody on the LAN and on a VPN at once alternates between two addresses about every 1,5
/// seconds. The row was built from that record, and the address is part of the list's "stable
/// identity" signature, so the whole list was cleared and rebuilt twice a second — under a screen
/// reader that re-announces every row and throws away the user's place in it, for as long as such a
/// peer is connected. It also named an address the audio was not going to, because the send endpoint
/// is pinned and only moves under strict conditions.</para>
///
/// <para>Reported from use as "homeserv was also changing IP every second" (Anthony Reyers,
/// 2026-09-01). Both halves are one fix: build the row from the pinned endpoint.</para>
/// </summary>
internal static partial class SelfTest
{
    private static string? ConnectedListHoldsStillForAMultiHomedPeer()
    {
        MainForm form;
        try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
        catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }

        using (form)
        {
            // By private field and private method, the same way the other window audits reach in, so
            // the form does not grow a test seam for the sake of one check.
            object Field(string name) =>
                Require(Require(typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic),
                            $"MainForm.{name} not found — a renamed field would leave this test comparing nothing")
                        .GetValue(form),
                    $"MainForm.{name} was null — this test would be comparing nothing");

            var known = (Dictionary<Guid, PeerAnnouncement>)Field("knownPeers");
            var pinned = (Dictionary<Guid, IPEndPoint>)Field("selectedPeerEndpoints");
            var list = (System.Windows.Forms.CheckedListBox)Field("connectedPeersList");
            var sync = Require(typeof(MainForm).GetMethod("SyncConnectedList", BindingFlags.Instance | BindingFlags.NonPublic),
                "MainForm.SyncConnectedList not found — this test would be driving nothing");
            var signatureField = Require(typeof(MainForm).GetField("lastConnectedListSignature", BindingFlags.Instance | BindingFlags.NonPublic),
                "MainForm.lastConnectedListSignature not found — the rebuild check would be proving nothing");

            void Sync() => sync.Invoke(form, null);
            string Signature() => signatureField.GetValue(form) as string ?? "";
            string Row() => list.Items.Count > 0 ? list.Items[0]?.ToString() ?? "" : "(no rows)";

            var id = Guid.NewGuid();
            var lan = IPAddress.Parse("192.168.69.48");
            var vpn = IPAddress.Parse("100.118.45.53");
            const int Port = 47830;

            // The peer is on the LAN and on a VPN at once, and the app has pinned the LAN path.
            known[id] = new PeerAnnouncement(id, "HOMESERV", Port, true, true, DateTime.UtcNow, lan);
            pinned[id] = new IPEndPoint(lan, Port);
            Sync();
            var firstSignature = Signature();
            var firstRow = Row();
            Check(firstRow.Contains("192.168.69.48"),
                $"the row must name the pinned address to begin with (row reads \"{firstRow}\")");

            // Now discovery hears them on the OTHER interface, which is the announcement that used to
            // rewrite the row and the signature. The pinned endpoint has not moved, so nothing the
            // user can see should move either.
            for (var flip = 0; flip < 6; flip++)
            {
                known[id] = new PeerAnnouncement(id, "HOMESERV", Port, true, true, DateTime.UtcNow, flip % 2 == 0 ? vpn : lan);
                Sync();
                Check(Signature() == firstSignature,
                    $"flip {flip}: a peer announcing from its other interface must NOT change the list signature. It does, and the "
                  + "list is cleared and rebuilt - which for a screen reader means the whole thing is re-announced and the user's "
                  + "place in it is lost, about once a second, for as long as that peer is connected");
                Check(Row() == firstRow,
                    $"flip {flip}: and the row must still name the address the audio is actually going to, not the one discovery "
                  + $"happened to hear last (row reads \"{Row()}\", expected \"{firstRow}\")");
            }

            // A REAL move must still show. The whole point is that the row follows the pin, so when the
            // app genuinely re-pins - the old path unreachable, the new one proven - the row has to say
            // so, or this fix would have traded a noisy list for a lying one.
            pinned[id] = new IPEndPoint(vpn, Port);
            Sync();
            Check(Row().Contains("100.118.45.53"),
                $"when the app really moves a peer to its other path, the row must follow (row reads \"{Row()}\")");
            Check(Signature() != firstSignature, "...and that IS a change of identity, so the signature must move with it");

            return "six announcements alternating between a peer's two addresses leave the list signature and the row untouched; "
                 + "the row names the pinned endpoint; a real re-pin still moves both";
        }
    }
}
