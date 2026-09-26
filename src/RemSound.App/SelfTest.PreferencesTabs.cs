using System.Windows.Forms;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Preferences, laid out the way Ed asked for it, checked the way he meets it: opened from the real main window's menu,
/// through the control channel, and walked in the order Tab moves - not the order the controls happen to be added in.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>The four things about who can reach you, and in what order (Ed, 2026-09-24): "we make a new tab after
    /// general in preferences. we call it connectivity. in there, move accept connections from other peers,
    /// automatically open my router, clear remembered peers, clear remembered servers."</summary>
    private static readonly string[] ConnectivityTabStops =
    {
        "Accept connections from other peers",
        "Automatically open my router for incoming connections via UPnP",
        "Clear remembered peers list",
        "Clear remembered servers list",
    };

    /// <summary>
    /// PREFERENCES: a Connectivity tab straight after General, holding exactly those four, in that order, and each still
    /// doing what it did on General.
    /// </summary>
    private static string? PreferencesConnectivityTab()
    {
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        MainForm? form = null;
        RemoteControlServer? server = null;
        var pipe = "RemSound.control.selftest.prefs." + Guid.NewGuid().ToString("N")[..8];
        var summary = "";
        try
        {
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;
            var main = form;
            server = new RemoteControlServer(pipe, () => main, new RemoteControlEngine(() => main));
            server.Start();
            string Ask(string command)
            {
                var reply = RemoteControl.Send(command, pipe, out var reached, connectTimeoutMs: 5000);
                Check(reached, $"the control channel did not answer \"{command}\": {reply}");
                return reply;
            }

            // Start from the default, so choosing Automatic below is a change the setting has to follow.
            var cfg = AppConfig.Load();
            cfg.AcceptPeerConnections = PeerAcceptMode.Prompt;
            cfg.Save();

            DriveWhilePumping(main, () =>
            {
                var opened = Ask("menu Options > Preferences");
                Check(opened.Contains("\"Preferences\"", StringComparison.Ordinal), $"Options, Preferences must open Preferences (got: {Head(opened)})");

                // The tabs themselves, in order: Connectivity comes straight after General.
                var pages = main.Invoke(() => Application.OpenForms.OfType<PreferencesDialog>().Single()
                    .Controls.OfType<TabControl>().Single().TabPages.Cast<TabPage>().Select(p => p.Text).ToList());
                Check(pages.Count >= 2 && pages[0] == "General" && pages[1] == "Connectivity",
                    $"Connectivity must be the tab straight after General (tabs: {string.Join(", ", pages)})");

                // Connectivity: exactly the four, in the order Ed gave, as Tab meets them.
                Check(Ask("tab Connectivity").StartsWith("ok", StringComparison.Ordinal), "the Connectivity tab must be reachable by name");
                var order = Ask("taborder");
                var at = ConnectivityTabStops.Select(n => order.IndexOf("\"" + n + "\"", StringComparison.Ordinal)).ToArray();
                for (var i = 0; i < at.Length; i++)
                    Check(at[i] >= 0, $"Tab on the Connectivity tab must reach \"{ConnectivityTabStops[i]}\" (tab order: {Head(order)})");
                for (var i = 1; i < at.Length; i++)
                    Check(at[i] > at[i - 1], $"\"{ConnectivityTabStops[i]}\" must come after \"{ConnectivityTabStops[i - 1]}\", in the order Ed gave (tab order: {Head(order)})");
                foreach (var stranger in new[] { "Browse for RemSound profiles folder", "Auto-save non-read-only profiles", "Clear remembered applications list" })
                    Check(!order.Contains("\"" + stranger + "\"", StringComparison.Ordinal), $"\"{stranger}\" belongs on General, not Connectivity (tab order: {Head(order)})");

                // General: what stayed, and none of what moved.
                Check(Ask("tab General").StartsWith("ok", StringComparison.Ordinal), "the General tab must be reachable by name");
                var general = Ask("taborder");
                foreach (var stayed in new[] { "Browse for RemSound profiles folder", "Auto-save non-read-only profiles", "Clear remembered applications list" })
                    Check(general.Contains("\"" + stayed + "\"", StringComparison.Ordinal), $"\"{stayed}\" must still be on General (tab order: {Head(general)})");
                foreach (var moved in ConnectivityTabStops)
                    Check(!general.Contains("\"" + moved + "\"", StringComparison.Ordinal), $"\"{moved}\" has moved to Connectivity and must not also be on General (tab order: {Head(general)})");

                // A moved control still does its job from its new home.
                Ask("tab Connectivity");
                var chose = Ask("select \"Accept connections from other peers\" Automatically");
                Check(chose.StartsWith("ok", StringComparison.Ordinal), $"the accept-connections list must still take a choice (got: {chose})");
                Check(AppConfig.Load().AcceptPeerConnections == PeerAcceptMode.Automatic,
                    "and the choice must still be saved, the way it was from General");
                Check(Ask("answer Close").StartsWith("ok", StringComparison.Ordinal), "Preferences closes with its own button");
            }, TimeSpan.FromSeconds(60));

            summary = "opened from the real Options menu: Connectivity is the tab after General; Tab on it meets accept connections, "
                    + "the router, clear peers and clear servers in that order and nothing from General; General keeps the rest; "
                    + "and the accept-connections choice still saves from its new tab";
        }
        finally
        {
            server?.Dispose();
            try { form?.Dispose(); } catch { /* teardown */ }
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
        }
        return summary;
    }
}
