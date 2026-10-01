namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>POP-UPS AND MENUS (2026-09-29, the live test with Andre: "when a you don't have same passwords prompt comes up,
    /// even if you kill that with ok, it stops you being able to access the menu bar"). A pop-up RemSound raises by itself - the
    /// wrong-password warning, the connection prompts, the update notice - is forced to the front through ForegroundDialog.
    /// A window with a menu bar and its File menu open, then a pop-up: the menu is closed before the pop-up shows, and when the
    /// pop-up is answered the window that had the keyboard is given it back. Minimised, or with another program in front, it
    /// is left alone.</summary>
    private static string? PopUpsCloseMenusAndGiveTheKeyboardBack()
    {
        var restoreActive = ForegroundDialog.ActiveFormForTest;
        var restoreActivate = ForegroundDialog.ActivateForTest;
        // Shown without taking activation: an ordinary Show made it the process's active window, and when it closed Windows
        // handed activation to some other hidden window of the run - after which every menu a later step opened closed
        // itself at once (the F1 sweep's "File menu ... premise", full gate 2026-10-01). Which window "has the keyboard" is
        // the ActiveFormForTest seam's to say here, not Windows'.
        using var window = new NoActivationForm { Text = "pop-up test window", ShowInTaskbar = false, Width = 400, Height = 200 };
        var strip = new MenuStrip();
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(new ToolStripMenuItem("&Open"));
        strip.Items.Add(file);
        window.Controls.Add(strip);
        window.MainMenuStrip = strip;
        var activated = new List<Form>();
        try
        {
            window.Show();
            Application.DoEvents();
            ForegroundDialog.ActivateForTest = f => activated.Add(f);

            // The window has the keyboard and its File menu is open when the pop-up comes.
            ForegroundDialog.ActiveFormForTest = () => window;
            file.ShowDropDown();
            Application.DoEvents();
            Check(file.DropDown.Visible, "the test's File menu must be open before the pop-up (it would prove nothing otherwise)");
            bool? openUnderThePopUp = null;
            ForegroundDialog.Show(_ => { openUnderThePopUp = file.DropDown.Visible; });
            Application.DoEvents();
            Check(openUnderThePopUp == false, "an open menu must be closed BEFORE the pop-up shows - it was still open under it");
            Check(!file.DropDown.Visible, "and still closed after the pop-up was answered");
            Check(activated.Count == 1 && ReferenceEquals(activated[0], window),
                $"the window that had the keyboard must be given it back after the pop-up ({activated.Count} window(s) activated)");

            // Another program in front (RemSound in the tray): nothing is taken back.
            activated.Clear();
            ForegroundDialog.ActiveFormForTest = () => null;
            ForegroundDialog.Show(_ => { });
            Check(activated.Count == 0, "with another program in front, a pop-up must not pull RemSound's window forward afterwards");

            // Minimised: left alone.
            ForegroundDialog.ActiveFormForTest = () => window;
            window.WindowState = FormWindowState.Minimized;
            Application.DoEvents();
            ForegroundDialog.Show(_ => { });
            Check(activated.Count == 0, "a minimised window must be left minimised after a pop-up");
            return "a menu open when a pop-up is forced to the front is closed first, the window that had the keyboard gets it back, "
                 + "and a minimised window or another program in front is left alone";
        }
        finally
        {
            ForegroundDialog.ActiveFormForTest = restoreActive;
            ForegroundDialog.ActivateForTest = restoreActivate;
            try { window.Close(); } catch { /* teardown */ }
        }
    }

    /// <summary>A window that shows without becoming the active one.</summary>
    private sealed class NoActivationForm : Form
    {
        protected override bool ShowWithoutActivation => true;
    }
}
