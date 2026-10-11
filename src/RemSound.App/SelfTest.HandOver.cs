using System.Net;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// THE SERVER HAND-OVER (2026-10-10; a user: the server "isn't always allowing RemSound to switch in and out to take its
/// place or give up its place"). The send-only service and the app are one member on a server; when one leaves for the
/// other to take its place, nothing of the leaver may reach the server outside the group (it took a two-person pair slot
/// there - a ghost the other half then paired with), and its goodbye must not be lost to one dropped datagram.
/// </summary>
internal static partial class SelfTest
{
    private static string? ConnectivityTabOrder()
    {
        using var form = new MainForm(null, new Profile(), null, null, headless: true);
        _ = form.Handle;
        var wanted = form.ConnectivityOrderForTest();
        var order = new List<Control>();
        var seen = new HashSet<Control>();
        for (var c = form.GetNextControl(form, true); c is not null && seen.Add(c); c = form.GetNextControl(c, true)) order.Add(c);
        var at = wanted.Select(w => order.IndexOf(w.Control)).ToArray();
        for (var i = 0; i < wanted.Length; i++)
            Check(at[i] >= 0, $"Tab on the main window must reach \"{wanted[i].Name}\"");
        for (var i = 1; i < wanted.Length; i++)
            Check(at[i] > at[i - 1], $"\"{wanted[i].Name}\" must come after \"{wanted[i - 1].Name}\", in the order Ed gave");
        // And on screen: the same rows, top to bottom.
        var panel = (TableLayoutPanel)wanted[1].Control.Parent!;
        var rows = wanted.Select(w => panel.GetRow(w.Control.Parent == panel ? w.Control : w.Control.Parent!)).ToArray();
        for (var i = 1; i < rows.Length; i++)
            Check(rows[i] > rows[i - 1], $"ON SCREEN: \"{wanted[i].Name}\" (row {rows[i]}) must sit below \"{wanted[i - 1].Name}\" (row {rows[i - 1]})");
        return "Tab and the rows on screen both go: " + string.Join(", ", wanted.Select(w => w.Name));
    }

    private static string? TheServerHandOver()
    {
        var said = new List<string>();
        var relay = new IPEndPoint(IPAddress.Parse("203.0.113.9"), RemPacket.DefaultPort);
        var (_, fingerprint) = RemSoundCrypto.ForPlainPassword("hand-over");
        var audio = PlainPacket(RemPacketType.Audio, 7, [1, 2, 3, 4, 5]);
        int Byes(List<(byte[] Data, IPEndPoint To)> sent)
        {
            lock (sent) return sent.Count(s => s.To.Equals(relay) && s.Data.Length > 5 && s.Data[4] == RelayGroupClient.GroupVersion && s.Data[5] == RelayGroupClient.TypeBye);
        }
        RelayGroupClient Member(List<(byte[] Data, IPEndPoint To)> sent, bool listed)
        {
            var me = new RelayGroupClient(Guid.NewGuid());
            me.SetIdentity("Me", fingerprint);
            me.Start((data, length, to) => { lock (sent) sent.Add((data.AsSpan(0, length).ToArray(), to)); return true; });
            me.Connect(relay);
            if (listed) Feed(me, relay, RosterPacket([(Guid.NewGuid(), "Alice"), (me.ClientId, "Me")], paired: false), RelayInbound.Consumed, "a member list");
            return me;
        }
        var waitForRepeats = RelayGroupClient.ByeCopies * RelayGroupClient.ByeRepeatMs + 300;

        // --- Leaving: the goodbye three times, and nothing ordinary to the server afterwards -------------------------------
        var sent = new List<(byte[] Data, IPEndPoint To)>();
        var leaver = Member(sent, listed: true);
        Check(leaver.IsInGroup(relay), "premise: in the group");
        leaver.Stop();
        Thread.Sleep(waitForRepeats);
        // Three, as agreed (the hand-over review): a number of its own here, so changing the code's constant can't pass this.
        const int ThreeGoodbyes = 3;
        Check(Byes(sent) == ThreeGoodbyes, $"THE GOODBYE: it must go {ThreeGoodbyes} times, so one lost datagram doesn't hold the other half out for five seconds ({Byes(sent)} went)");
        lock (sent) sent.Clear();
        Check(leaver.TryRoute(audio, relay, (_, _) => { }), "AFTER THE GOODBYE: sound for the server just left must be taken, not sent the ordinary way");
        Check(Byes(sent) == 0 && sent.Count == 0, "AFTER THE GOODBYE: nothing at all may reach it, or the relay gives that sound a pair slot - a ghost of this computer");
        said.Add($"the goodbye goes {ThreeGoodbyes} times; afterwards nothing reaches the server outside the group");

        // --- Leaving and coming straight back: no late goodbye after the new hello; the server is ours again -------------
        var back = new List<(byte[] Data, IPEndPoint To)>();
        var returner = Member(back, listed: true);
        returner.Disconnect();
        returner.Connect(relay);
        Thread.Sleep(waitForRepeats);
        Check(Byes(back) == 1, $"BACK AT ONCE: the goodbye's repeats must stop when we connect again, or a late one undoes our own hello ({Byes(back)} went)");
        Check(!returner.TryRoute(audio, relay, (_, _) => { }), "BACK AT ONCE: connected again, the server is ours: until its list comes, packets go the ordinary way as always");
        returner.Stop();
        said.Add("leaving and connecting straight back stops the repeats, and the server is used as before");

        // --- A hello the relay took before its list reached us: still a goodbye ------------------------------------------
        var early = new List<(byte[] Data, IPEndPoint To)>();
        var unlisted = Member(early, listed: false);
        Check(!unlisted.IsInGroup(relay), "premise: no list has come yet");
        unlisted.Stop();
        Thread.Sleep(waitForRepeats);
        Check(Byes(early) == ThreeGoodbyes, $"NO LIST YET: a goodbye must still go, since the relay may already hold our place ({Byes(early)} went)");
        said.Add("a goodbye goes even when the server's list hasn't arrived yet");

        // --- The service sends to nobody before it says goodbye -----------------------------------------------------------
        using (var sender = new RemSound.Sender.AudioSender())
        {
            var presence = new ServiceNetworkPresence(sender, null);
            try
            {
                var peers = new List<IPEndPoint> { new(IPAddress.Loopback, RemPacket.DefaultPeerDialPort), relay };
                presence.Start(FreeUdpPort(), peers);
                sender.SetReceivers(peers);
                Check(sender.ReceiverCountForTest == 2, "premise: the service is sending to a peer and the server");
                presence.Stop();
                Check(sender.ReceiverCountForTest == 0, "THE SERVICE: stepping aside, it must send to nobody, so none of its sound reaches the server after its goodbye");
            }
            finally { presence.Dispose(); }
        }
        said.Add("the service sends to nobody before its goodbye");

        // --- The log says whether the app and the service are one person, and whether the service was told to step aside -
        Guid app = Guid.NewGuid(), other = Guid.NewGuid(), machine = Guid.NewGuid();
        string Short(Guid id) => id.ToString("N")[..8];
        var same = MainForm.ServerIdentityLines(false, app, true, app, machine);
        Check(same.Count == 1 && same[0].Contains("one person", StringComparison.Ordinal) && same[0].Contains(Short(app), StringComparison.Ordinal),
            $"THE LOG: one id, it must say one person, with the id the connecting line shows ({string.Join(" | ", same)})");
        var viaMachine = MainForm.ServerIdentityLines(false, machine, true, null, machine);
        Check(viaMachine.Count == 1 && viaMachine[0].Contains("one person", StringComparison.Ordinal), "THE LOG: a service with no id of its own is the computer's, so an app on the computer's id is the same person");
        var apart = MainForm.ServerIdentityLines(false, app, true, other, machine);
        Check(apart.Count == 1 && apart[0].Contains("two different people", StringComparison.Ordinal) && apart[0].Contains(Short(other), StringComparison.Ordinal)
              && apart[0].Contains("Configure the service profile again", StringComparison.Ordinal),
            $"THE LOG: different ids, it must say so, give both, and say how to make them one ({string.Join(" | ", apart)})");
        var unreadable = MainForm.ServerIdentityLines(false, app, false, null, machine);
        Check(unreadable.Count == 1 && unreadable[0].Contains("not known", StringComparison.Ordinal), "THE LOG: unreadable service settings must say it isn't known, never guess");
        var held = MainForm.ServerIdentityLines(true, app, true, app, machine);
        Check(held.Count == 2 && held[0].StartsWith("presence: this copy could not take the hold", StringComparison.Ordinal),
            "THE LOG: a copy that couldn't take its hold must say the service may keep sending");
        said.Add("the log says whether the app and the service are one person on a server, with both ids, and when the service wasn't told to step aside");
        return string.Join("; ", said);
    }
}
