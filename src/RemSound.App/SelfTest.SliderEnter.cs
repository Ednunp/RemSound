using System.Net;
using RemSound.Core;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>SLIDER ENTER (Ed, 2026-10-12, after the web listener page: "coudl enter do the same for them as well?", then yes
    /// to Enter on a slider meaning reset even in a window whose OK is Enter). Enter, delivered as Windows delivers it - offered
    /// to the window's keyboard handling first, where a dialog's Enter is taken - puts each slider back where it starts, as a
    /// move by hand: the master volume to 100 %, heard, kept and logged; a person's volume to 100 %, heard; pan to the centre;
    /// every EQ band to flat; the metronome's volume to 70 % and push to talk's level to -40 dB, neither window closing.
    /// Enter anywhere else in those windows is still OK, and the control channel's key command does the same.</summary>
    private static string? EnterPutsEverySliderBack()
    {
        var logged = new List<string>();
        MainForm? form = null;
        var said = new List<string>();
        try
        {
            var profile = Profile.NewBlank();
            profile.Password = RemSoundCrypto.Obfuscate("slider-enter-password");
            try { form = MetronomeWindow(logged, profile); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            var main = form;

            // The master volume: from 40 back to 100, as if dragged there.
            var bar = Require(FieldOf<TrackBar>(main, "volumeBar"), "MainForm.volumeBar not found");
            bar.Value = 40;
            PressKeyOn(bar, Keys.Enter);
            Check(bar.Value == 100, $"MASTER VOLUME: Enter must put it back to 100 % ({bar.Value})");
            Check(Math.Abs(main.ReceiveVolumeForTest - 1.0f) < 1e-6, $"and heard at once: the engine's volume follows ({main.ReceiveVolumeForTest})");
            Check(main.UnsavedChangesForTest, "and the profile marked unsaved, as dragging it marks it");
            Check(Logged(logged, "ui: listening volume → 100%"), "and logged");
            // Through the control channel too, as the headless copy is driven.
            bar.Value = 25;
            var engine = new RemoteControlEngine(() => main);
            var reply = engine.Execute("key \"Master volume for received audio\" enter");
            Check(reply.StartsWith("ok", StringComparison.Ordinal) && bar.Value == 100, $"THE CONTROL CHANNEL: key ... enter must do the same ({reply}; {bar.Value})");
            said.Add("the master volume to 100 %, heard, kept and logged");

            // A person's volume, pan and every EQ band, with somebody connected and picked on the pan and EQ tab.
            var address = IPAddress.Parse("192.0.2.62");
            main.SelectPeerForTest(new PeerAnnouncement(Guid.NewGuid(), "Gate peer", RemPacket.DefaultPort, true, true, DateTime.UtcNow, address));
            Require(typeof(MainForm).GetMethod("RefreshPanEqPeerList", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
                "MainForm.RefreshPanEqPeerList not found").Invoke(main, null);
            var peers = Require(FieldOf<CheckedListBox>(main, "panEqPeerList"), "MainForm.panEqPeerList not found");
            Check(peers.Items.Count == 1, $"premise: the connected person is listed on the pan and EQ tab ({peers.Items.Count})");
            peers.SelectedIndex = 0;
            Require(FieldOf<CheckBox>(main, "enableAllPeerShapingBox"), "MainForm.enableAllPeerShapingBox not found").Checked = true;
            var peerVolume = Require(FieldOf<TrackBar>(main, "volumeSlider"), "MainForm.volumeSlider not found");
            Check(peerVolume.Enabled, "premise: their volume slider is available");
            peerVolume.Value = 30;
            PressKeyOn(peerVolume, Keys.Enter);
            var tone = Tone(1000);
            var unity = Rms(tone);
            main.ReceiverForTest.PeerDspForTest(address)?.Process(tone, tone.Length / 2);
            var level = Rms(tone) / unity;
            Check(peerVolume.Value == 100 && Math.Abs(level - 1.0) < 0.02, $"A PERSON'S VOLUME: Enter must put it back to 100 %, heard in the live sound ({peerVolume.Value}, measured {level:0.000})");
            var pan = Require(FieldOf<TrackBar>(main, "panSlider"), "MainForm.panSlider not found");
            pan.Value = 20;
            PressKeyOn(pan, Keys.Enter);
            Check(pan.Value == 50, $"PAN: Enter must put it back to the centre ({pan.Value})");
            var bands = Require(FieldOf<List<TrackBar>>(main, "eqBandSliders"), "MainForm.eqBandSliders not found");
            Check(bands.Count >= 3, $"premise: the EQ's band sliders are built ({bands.Count})");
            foreach (var band in bands)
            {
                band.Value = 80;
                PressKeyOn(band, Keys.Enter);
                Check(band.Value == 50, $"EQ BAND {band.AccessibleName}: Enter must put it back to flat ({band.Value})");
            }
            said.Add($"a person's volume to 100 % (heard), pan to the centre and {bands.Count} EQ bands to flat");
            main.LogForTest.EventTapForTest = null;
        }
        finally
        {
            try { form?.Dispose(); } catch { /* teardown */ }
        }

        // The two windows are shown (kept out of sight): a window's OK only takes Enter while it is on screen, so a window never
        // shown would pass "stays open" whatever the slider did.
        var restoreMuted = CuePlayer.GloballyMuted;
        CuePlayer.GloballyMuted = true;
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        try
        {
        // Metronome settings: Enter on the volume resets it and leaves the window open; Enter on a list is still OK.
        using (var dialog = new MetronomeSettingsDialog(new MetronomeSettings { Volume = 30 }, null, []))
        {
            dialog.Show();
            Application.DoEvents();
            var volume = dialog.VolumeBarForTest;
            PressKeyOn(volume, Keys.Enter);
            Check(volume.Value == MetronomeSettings.DefaultVolume && volume.Value == 70, $"METRONOME VOLUME: Enter must put it back to 70 % ({volume.Value})");
            Check(dialog.DialogResult == DialogResult.None, $"and the window must stay open: Enter on the slider is not OK there ({dialog.DialogResult})");
            PressKeyOn(dialog.SignatureListForTest, Keys.Enter);
            Check(dialog.DialogResult == DialogResult.OK, $"ENTER ELSEWHERE: on the time signature list it must still be OK ({dialog.DialogResult})");
        }
        said.Add("the metronome's volume to 70 %, the window open, Enter elsewhere still OK");

        // Push to talk: the level the same way.
        using (var dialog = new PushToTalkDialog(new PushToTalkSettings { Enabled = true, Trigger = PushToTalkTrigger.Level, LevelDb = -60 },
                   RemSoundSettingsStore.PushToTalkDefaultKey, _ => null))
        {
            dialog.Show();
            Application.DoEvents();
            var levelBar = dialog.LevelBarForTest;
            Check(levelBar.Value == -60, $"premise: the level opens at -60 dB ({levelBar.Value})");
            PressKeyOn(levelBar, Keys.Enter);
            Check(levelBar.Value == PushToTalkSettings.DefaultLevelDb && levelBar.Value == -40, $"PUSH TO TALK LEVEL: Enter must put it back to -40 dB ({levelBar.Value})");
            Check(dialog.DialogResult == DialogResult.None, $"and the window must stay open ({dialog.DialogResult})");
            PressKeyOn(dialog.TriggerListForTest, Keys.Enter);
            Check(dialog.DialogResult == DialogResult.OK, $"ENTER ELSEWHERE: on the trigger list it must still be OK ({dialog.DialogResult})");
        }
        said.Add("push to talk's level to -40 dB, the window open, Enter elsewhere still OK");
        }
        finally
        {
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
        }
        return string.Join("; ", said);
    }
}
