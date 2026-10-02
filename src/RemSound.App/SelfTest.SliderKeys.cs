using System.Net;
using RemSound.Core;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>SLIDER KEYS (Ed, 2026-10-02: "the master volume for received audio is also odd in that arrows up make it go
    /// down", then yes to the other volume, EQ and level sliders, pan left alone). The keys as Windows delivers them to each
    /// slider, which moves itself: Up and Page Up more, Down and Page Down less, Left and Right as on any slider, each slider's
    /// steps as before. The master volume's every press reaches the engine's volume, the profile and the log at once, as
    /// dragging it does. Each person's volume, every EQ band and push to talk's level go the same way; pan, which has no up,
    /// is as it was.</summary>
    private static string? SlidersUpMeansMore()
    {
        static int Press(TrackBar bar, int key)
        {
            SendMessage(bar.Handle, 0x0100, key, IntPtr.Zero);
            SendMessage(bar.Handle, 0x0101, key, IntPtr.Zero);
            return bar.Value;
        }
        // Up, Down, Page Up, Page Down, Right, Left from the middle: the value after each, back to the middle between.
        static string Walk(TrackBar bar, int middle)
        {
            var after = new List<int>();
            foreach (var key in new[] { 0x26, 0x28, 0x21, 0x22, 0x27, 0x25 })
            {
                bar.Value = middle;
                after.Add(Press(bar, key));
            }
            bar.Value = middle;
            return string.Join(",", after);
        }
        static string Expected(int middle, int small, int large) =>
            string.Join(",", middle + small, middle - small, middle + large, middle - large, middle + small, middle - small);

        var logged = new List<string>();
        MainForm? form = null;
        var said = new List<string>();
        try
        {
            var profile = Profile.NewBlank();
            profile.Password = RemSoundCrypto.Obfuscate("slider-keys-password");
            try { form = MetronomeWindow(logged, profile); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            var main = form;

            // The master volume, through to what is heard.
            var bar = Require(FieldOf<TrackBar>(main, "volumeBar"), "MainForm.volumeBar not found");
            bar.Value = 50;
            Check(Press(bar, 0x26) == 51, $"MASTER VOLUME, UP: louder by a step ({bar.Value})");
            Check(Math.Abs(main.ReceiveVolumeForTest - 0.51f) < 1e-6, $"and heard at once: the engine's volume follows ({main.ReceiveVolumeForTest})");
            Check(main.UnsavedChangesForTest, "and the profile marked unsaved, as dragging it marks it");
            Check(Logged(logged, "ui: listening volume → 51%"), "and logged");
            Check(Press(bar, 0x28) == 50, $"DOWN: quieter ({bar.Value})");
            Check(Press(bar, 0x21) == 55, $"PAGE UP: louder by 5 ({bar.Value})");
            Check(Press(bar, 0x22) == 50, $"PAGE DOWN: quieter by 5 ({bar.Value})");
            Check(Press(bar, 0x27) == 51 && Press(bar, 0x25) == 50, $"and Right and Left as on any slider ({bar.Value})");
            Check(Math.Abs(main.ReceiveVolumeForTest - 0.50f) < 1e-6 && Logged(logged, "ui: listening volume → 50%"), "each press heard and logged");
            bar.Value = 100;
            Check(Press(bar, 0x26) == 100 && Press(bar, 0x21) == 100, "held at 100");
            bar.Value = 0;
            Check(Press(bar, 0x28) == 0 && Press(bar, 0x22) == 0, "held at 0");
            said.Add("master volume 1 and 5, heard, kept and logged");

            // Each person's volume and every EQ band, with somebody connected and picked on the pan and EQ tab.
            var address = IPAddress.Parse("192.0.2.61");
            main.SelectPeerForTest(new PeerAnnouncement(Guid.NewGuid(), "Gate peer", RemPacket.DefaultPort, true, true, DateTime.UtcNow, address));
            Require(typeof(MainForm).GetMethod("RefreshPanEqPeerList", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
                "MainForm.RefreshPanEqPeerList not found").Invoke(main, null);
            var peers = Require(FieldOf<CheckedListBox>(main, "panEqPeerList"), "MainForm.panEqPeerList not found");
            Check(peers.Items.Count == 1, $"premise: the connected person is listed on the pan and EQ tab ({peers.Items.Count})");
            peers.SelectedIndex = 0;
            Require(FieldOf<CheckBox>(main, "enableAllPeerShapingBox"), "MainForm.enableAllPeerShapingBox not found").Checked = true;
            var peerVolume = Require(FieldOf<TrackBar>(main, "volumeSlider"), "MainForm.volumeSlider not found");
            Check(peerVolume.Enabled, "premise: their volume slider is available");
            var walked = Walk(peerVolume, 50);
            Check(walked == Expected(50, 1, 10), $"A PERSON'S VOLUME: Up and Page Up louder, Down and Page Down quieter ({walked})");
            peerVolume.Value = 50;
            Press(peerVolume, 0x21);
            var tone = Tone(1000);
            var unity = Rms(tone);
            main.ReceiverForTest.PeerDspForTest(address)?.Process(tone, tone.Length / 2);
            var level = Rms(tone) / unity;
            Check(peerVolume.Value == 60 && Math.Abs(level - 0.60) < 0.02, $"and Page Up is heard: that person at 60 % in the live sound (measured {level:0.000})");
            var bands = Require(FieldOf<List<TrackBar>>(main, "eqBandSliders"), "MainForm.eqBandSliders not found");
            Check(bands.Count >= 3, $"premise: the EQ's band sliders are built ({bands.Count})");
            foreach (var band in bands)
            {
                walked = Walk(band, 50);
                Check(walked == Expected(50, 1, 10), $"EQ BAND {band.AccessibleName}: Up and Page Up raise it, Down and Page Down lower it ({walked})");
            }
            said.Add($"a person's volume and {bands.Count} EQ bands 1 and 10");

            // Pan has no up: as it was, Up and Page Up toward the left as on any Windows slider.
            var pan = Require(FieldOf<TrackBar>(main, "panSlider"), "MainForm.panSlider not found");
            walked = Walk(pan, 50);
            Check(walked == "49,51,40,60,51,49", $"PAN: left as it was ({walked})");
            main.LogForTest.EventTapForTest = null;
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
        }

        // Push to talk's level.
        using (var dialog = new PushToTalkDialog(new PushToTalkSettings { Enabled = true, Trigger = PushToTalkTrigger.Level }, RemSoundSettingsStore.PushToTalkDefaultKey, _ => null))
        {
            var level = dialog.LevelBarForTest;
            Check(level.Enabled, "premise: triggered by sound level, the level slider is available");
            var walked = Walk(level, -40);
            Check(walked == Expected(-40, 1, 5), $"PUSH TO TALK LEVEL: Up and Page Up raise it, Down and Page Down lower it ({walked})");
            said.Add("push to talk's level 1 and 5");
        }
        return $"Up and Page Up more, Down and Page Down less, Left and Right as before: {string.Join("; ", said)}; pan as it was";
    }
}
