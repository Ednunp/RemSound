using System.Net;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// The app's own plugin wiring, through the real main window. The bridge's behaviour is proved elsewhere against a
/// playout engine and a sender wired BY HAND in the test - so deleting the window's two wiring lines (the claim register
/// reaching the speakers, the track audio reaching the send lane) left every one of those steps green (found 2026-09-24).
/// Those two lines are the difference between hearing a peer once and hearing them twice, and between a DAW track going
/// out and going nowhere.
/// </summary>
internal static partial class SelfTest
{
    private static string? PluginLinkIsWiredInTheApp()
    {
        MainForm? form = null;
        try
        {
            var cfg = AppConfig.Load();
            cfg.EnableDawPluginLink = true;
            cfg.Save();
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }

            // Port 0: never the real one.
            var host = form.OpenPluginLinkForTest(0);
            Check(host is not null, "with the setting on, the window must open the plugin link");

            // 1. The claim register the SPEAKERS consult is the link's own - so a plugin taking a peer takes them off the
            // speakers. A different register, or none, is the double audio this whole design exists to prevent.
            Check(ReferenceEquals(form.ReceiverForTest.PluginClaimsForTest, host!.Claims),
                "the receiver must consult the plugin link's claim register - otherwise a peer on a DAW track also plays out of the speakers");
            var peer = IPAddress.Parse("192.0.2.70");
            using (var plugin = new PluginBridgeClient(host.Port))
            {
                plugin.Hello();
                plugin.SetReceivedPeers([peer]);
                var block = new float[512];
                plugin.ReadPeerBlock(block, 256);
                Check(WaitUntil(() => form.ReceiverForTest.PluginClaimsForTest?.IsClaimed(peer) == true),
                    "and a plugin taking a peer must show as claimed in the register the speakers read");

                // 2. A track's audio reaches the window's send lane.
                var track = new float[256 * PluginBridgeProtocol.WireChannels];
                for (var i = 0; i < track.Length; i++) track[i] = 0.25f;
                for (var i = 0; i < 20; i++) { plugin.SendTrackBlock(track); Thread.Sleep(2); }
                Check(WaitUntil(() => form.PluginTrackSourceForTest.AnyHostSending),
                    "a DAW track sent over the link must reach the window's own send lane - the subscription is the whole of the send direction");
            }

            // 3. Switching the link off lets everything go: the speakers stop consulting a register nobody can release.
            form.ClosePluginLinkForTest();
            Check(form.ReceiverForTest.PluginClaimsForTest is null,
                "switching the link off must drop the claim register, or a peer claimed at that moment stays off the speakers for good");
            Check(!form.PluginTrackSourceForTest.AnyHostSending, "and let the send lane go");
            return "through the real window: the speakers consult the link's own claim register and see a plugin's claim, a DAW track "
                 + "reaches the window's send lane, and switching the link off drops the register and the lane";
        }
        finally { try { form?.Dispose(); } catch { } }
    }
}
