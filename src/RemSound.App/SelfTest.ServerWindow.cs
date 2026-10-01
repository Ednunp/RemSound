using System.Net;
using RemSound.Core;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>THE SERVER WINDOW (Ed, 2026-09-29): "discovered peers on server is supposed to be invisible unless a server is
    /// connected"; "when you connect to a server it should close the connect to server window"; "it says the button is now
    /// disconnect ... that should just be silent when you connect"; "it should be remembered servers 1st then the name box
    /// to enter one". The window on its own, with the connecting played by the test, and then the main window's list on,
    /// off and on a server.</summary>
    private static string? ServerWindowAndList()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        Windowless.SetActiveForTest(true);
        try
        {
            // ---- The window.
            string? on = null;
            string? asked = null;
            string status = "Not connected to a server.";
            var remembered = new List<string> { "remote.example.com", "192.168.1.82" };
            RelayConnectDialog Make() => new(() => on, entry => asked = entry, () => on = null, () => remembered,
                entry => remembered.Remove(entry), () => status);
            using (var dialog = Make())
            {
                dialog.StartPosition = FormStartPosition.Manual;
                dialog.Show();
                for (var i = 0; i < 5; i++) Application.DoEvents();
                var list = dialog.RememberedListForTest;
                var box = dialog.AddressBoxForTest;
                Check(dialog.ActiveControl == list, $"REMEMBERED FIRST: the window must open on the remembered servers (it opens on {dialog.ActiveControl?.AccessibleName})");
                Check(list.SelectedItem as string == "remote.example.com" && box.Text == "remote.example.com",
                    "on the one gone to last, with it in the box");
                dialog.SelectNextControl(list, forward: true, tabStopOnly: true, nested: true, wrap: true);
                Check(dialog.ActiveControl == box, $"then Tab goes to the box to type one ({dialog.ActiveControl?.AccessibleName})");
                Check(list.TabIndex < box.TabIndex && list.Top < box.Top, "the list above and before the box, for eyes and keys alike");
                list.SelectedIndex = 1;
                Check(box.Text == "192.168.1.82", "moving through the list puts that server in the box");

                var mark = HeadlessRecords.Speech.Mark;
                dialog.ActionForTest();
                Check(asked == "192.168.1.82", $"Connect goes to the server in the box ({asked})");
                Check(dialog.Visible, "still connecting: the window stays");
                on = asked;
                status = "On 192.168.1.82 with 1 other.";
                dialog.TickForTest();
                for (var i = 0; i < 5; i++) Application.DoEvents();
                Check(!dialog.Visible && dialog.DialogResult == DialogResult.OK, "CONNECTED: the window closes");
                var said = HeadlessRecords.Speech.Since(mark);
                Check(!said.Any(s => s.Contains("button is now", StringComparison.OrdinalIgnoreCase) || s.StartsWith("Connected", StringComparison.Ordinal)),
                    $"SILENT: connecting says nothing about the button ({string.Join(" | ", said)})");
            }

            // A server that cannot be found: the window stays, and says so.
            on = null;
            asked = null;
            status = "Not connected to a server.";
            using (var dialog = Make())
            {
                dialog.StartPosition = FormStartPosition.Manual;
                dialog.Show();
                for (var i = 0; i < 5; i++) Application.DoEvents();
                dialog.ActionForTest();
                status = "Couldn't find remote.example.com.";
                var mark = HeadlessRecords.Speech.Mark;
                dialog.TickForTest();
                Check(dialog.Visible, "NOT FOUND: the window stays open to try another");
                Check(HeadlessRecords.Speech.Since(mark).Any(s => s.Contains("Couldn't find", StringComparison.Ordinal)), "and says why");
                dialog.Close();
            }

            // No remembered servers: it opens on the box.
            remembered.Clear();
            using (var dialog = Make())
            {
                dialog.StartPosition = FormStartPosition.Manual;
                dialog.Show();
                for (var i = 0; i < 5; i++) Application.DoEvents();
                Check(dialog.ActiveControl == dialog.AddressBoxForTest, "with nothing remembered, it opens on the box");
                dialog.Close();
            }

            // ---- The main window's list of people on a server: not there until you are on one.
            var scratch = Path.Combine(Path.GetTempPath(), "remsound-server-window-" + Guid.NewGuid().ToString("N"));
            using var userData = AppConfig.UseThrowawayUserDataDirectory(scratch);
            MainForm? form = null;
            try
            {
                try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
                catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
                var label = Require(form.RelayPeersLabelForTest, "the server list's label");
                var wrapper = Require(form.RelayPeersListForTest.Parent, "the server list's row");
                Check(!IsSetVisibleForAltAudit(label) && !IsSetVisibleForAltAudit(wrapper),
                    "NOT ON A SERVER: Discovered peers on server must not be there at all");
                form.ConnectToRelayForTest("127.0.0.1:47999", new IPEndPoint(IPAddress.Loopback, 47999));
                Check(IsSetVisibleForAltAudit(label) && IsSetVisibleForAltAudit(wrapper), "on a server, it is there");
                form.DisconnectFromRelayForTest();
                Check(!IsSetVisibleForAltAudit(label) && !IsSetVisibleForAltAudit(wrapper), "and gone again once off it");
            }
            finally { try { form?.Dispose(); } catch { /* teardown */ } }
            return "the window opens on the remembered servers (the box when there are none), Tab to the box, the list fills "
                 + "the box; connecting closes it without a word, a server not found keeps it open and says so; the main "
                 + "window's list of people on a server is there only while on one";
        }
        finally
        {
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
        }
    }
}
