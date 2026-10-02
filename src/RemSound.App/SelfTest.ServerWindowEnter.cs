using System.Runtime.InteropServices;

namespace RemSound.App;

internal static partial class SelfTest
{
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>A key pressed on a control as Windows delivers it: offered to the window's keyboard handling first (where a
    /// dialog's Enter and Escape are taken), then to the control - what the control channel's key command does.</summary>
    private static void PressKeyOn(Control c, Keys key)
    {
        const int WmKeyDown = 0x0100, WmKeyUp = 0x0101;
        var down = Message.Create(c.Handle, WmKeyDown, (IntPtr)(int)key, (IntPtr)1);
        if (c.PreProcessControlMessage(ref down) != PreProcessControlState.MessageProcessed)
            SendMessage(c.Handle, down.Msg, down.WParam, down.LParam);
        var up = Message.Create(c.Handle, WmKeyUp, (IntPtr)(int)key, unchecked((IntPtr)0xC0000001));
        if (c.PreProcessControlMessage(ref up) != PreProcessControlState.MessageProcessed)
            SendMessage(c.Handle, up.Msg, up.WParam, up.LParam);
    }

    /// <summary>SERVER WINDOW ENTER (Ed, 2026-10-01: "on the remembered servers list, you should be able to press enter and it
    /// connect to the server. at the moment you have to tab across to the connect button"). Enter on a remembered server goes
    /// to it, as a real key press arrives - not on any server, and on another one (it moves you there); on the one you are
    /// already on, it stays. Enter in the address box still connects to what is typed.</summary>
    private static string? EnterOnARememberedServerConnects()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        Windowless.SetActiveForTest(true);
        try
        {
            string? on = null;
            var asked = new List<string>();
            var left = 0;
            var remembered = new List<string> { "remote.example.com", "192.168.1.82" };
            RelayConnectDialog Make() => new(() => on, entry => asked.Add(entry), () => { left++; on = null; }, () => remembered,
                entry => remembered.Remove(entry), () => on is null ? "Not connected to a server." : $"On {on} with 1 other.");
            void Settle() { for (var i = 0; i < 5; i++) Application.DoEvents(); }

            // Not on a server: Enter on the second remembered server goes there.
            using (var dialog = Make())
            {
                dialog.StartPosition = FormStartPosition.Manual;
                dialog.Show();
                Settle();
                var list = dialog.RememberedListForTest;
                list.Focus();
                list.SelectedIndex = 1;
                Settle();
                PressKeyOn(list, Keys.Enter);
                Settle();
                Check(asked.SequenceEqual(["192.168.1.82"]),
                    $"NOT ON A SERVER: Enter on a remembered server must connect to it ({(asked.Count == 0 ? "nothing was asked" : string.Join(", ", asked))})");
                Check(left == 0, "and must not disconnect anything");
            }

            // On one server: Enter on another remembered one moves you there.
            asked.Clear();
            on = "remote.example.com";
            using (var dialog = Make())
            {
                dialog.StartPosition = FormStartPosition.Manual;
                dialog.Show();
                Settle();
                var list = dialog.RememberedListForTest;
                list.Focus();
                list.SelectedIndex = list.Items.IndexOf("192.168.1.82");
                Settle();
                PressKeyOn(list, Keys.Enter);
                Settle();
                Check(asked.SequenceEqual(["192.168.1.82"]),
                    $"ON ANOTHER SERVER: Enter on a remembered server must go to it ({(asked.Count == 0 ? "nothing was asked" : string.Join(", ", asked))}; {left} disconnect(s))");

                // On the one you are on: nothing changes - no disconnect, no second connect.
                asked.Clear();
                left = 0;
                on = "192.168.1.82";
                list.SelectedIndex = list.Items.IndexOf("192.168.1.82");
                Settle();
                PressKeyOn(list, Keys.Enter);
                Settle();
                Check(asked.Count == 0 && left == 0 && on == "192.168.1.82",
                    $"THE ONE YOU ARE ON: Enter on it must leave you there ({asked.Count} connect(s), {left} disconnect(s))");
            }

            // The address box: Enter still connects to what is typed.
            asked.Clear();
            on = null;
            using (var dialog = Make())
            {
                dialog.StartPosition = FormStartPosition.Manual;
                dialog.Show();
                Settle();
                var box = dialog.AddressBoxForTest;
                box.Focus();
                box.Text = "typed.example.com";
                PressKeyOn(box, Keys.Enter);
                Settle();
                Check(asked.SequenceEqual(["typed.example.com"]), $"THE BOX: Enter must connect to what is typed ({string.Join(", ", asked)})");
            }
            return "Enter on a remembered server connects to it, moves you there from another server, and leaves you where you "
                 + "are on the one you are on; Enter in the box connects to what is typed";
        }
        finally
        {
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
        }
    }
}
