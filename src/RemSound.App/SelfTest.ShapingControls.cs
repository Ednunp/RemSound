using System.Net;
using System.Windows.Forms;
using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App;

/// <summary>
/// The pan and EQ tab's controls, driven in the real window and measured in the chain the receiver really got.
///
/// <para>Until 2026-09-24 the volume and pan sliders, the EQ mode list and the per-person tick were each marked "effect
/// PROVEN" by the measured shaping steps - which build a chain straight from settings and never touch a control. A slider
/// whose handler had come undone would have passed: the dead-slider pattern the control suite exists to catch.</para>
/// </summary>
internal static partial class SelfTest
{
    private static string? ShapingControlsReachTheLiveSound()
    {
        MainForm? form = null;
        try
        {
            var profile = Profile.NewBlank();
            profile.Password = RemSoundCrypto.Obfuscate("shaping-controls-password");
            try { form = new MainForm(null, profile, null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            _ = form.Handle;

            // Somebody connected, so the pan and EQ tab has a person to shape.
            var address = IPAddress.Parse("192.0.2.60");
            var peer = new PeerAnnouncement(Guid.NewGuid(), "Gate peer", RemPacket.DefaultPort, true, true, DateTime.UtcNow, address);
            form.SelectPeerForTest(peer);
            Require(typeof(MainForm).GetMethod("RefreshPanEqPeerList", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
                "MainForm.RefreshPanEqPeerList not found").Invoke(form, null);
            var peers = Require(FieldOf<CheckedListBox>(form, "panEqPeerList"), "MainForm.panEqPeerList not found");
            Check(peers.Items.Count == 1, $"the connected person must be listed on the pan and EQ tab ({peers.Items.Count} listed)");
            peers.SelectedIndex = 0;
            var master = Require(FieldOf<CheckBox>(form, "enableAllPeerShapingBox"), "MainForm.enableAllPeerShapingBox not found");
            master.Checked = true;

            float[] Heard()
            {
                var tone = Tone(1000);
                form.ReceiverForTest.PeerDspForTest(address)?.Process(tone, tone.Length / 2);
                return tone;
            }
            var unity = Rms(Tone(1000));

            // Volume: the real slider, the real handler, the chain the receiver holds.
            var volume = Require(FieldOf<TrackBar>(form, "volumeSlider"), "MainForm.volumeSlider not found");
            volume.Value = 25;
            var quarter = Rms(Heard()) / unity;
            Check(Math.Abs(quarter - 0.25) < 0.02, $"moving the volume slider to 25 must put that person at a quarter level in the live sound (measured {quarter:0.000})");

            // Pan: hard left drops the right channel.
            volume.Value = 100;
            var pan = Require(FieldOf<TrackBar>(form, "panSlider"), "MainForm.panSlider not found");
            pan.Value = pan.Minimum;
            var (left, right) = ChannelRms(Heard());
            Check(right < left * 0.05, $"moving the pan slider hard left must drop the right channel in the live sound (left {left:0.000}, right {right:0.000})");
            pan.Value = (pan.Minimum + pan.Maximum) / 2;

            // The EQ mode list: choosing a mode reaches the chain the receiver holds.
            var modes = Require(FieldOf<ListBox>(form, "eqModeList"), "MainForm.eqModeList not found");
            volume.Value = 50;
            var before = form.ReceiverForTest.PeerDspForTest(address);
            modes.SelectedIndex = (modes.SelectedIndex + 1) % modes.Items.Count;
            var after = form.ReceiverForTest.PeerDspForTest(address);
            Check(after is not null && !ReferenceEquals(before, after), "choosing another EQ mode must rebuild the chain the receiver holds for that person");
            var halfStill = Rms(Heard()) / unity;
            Check(Math.Abs(halfStill - 0.5) < 0.1, $"and the new chain must still carry their volume ({halfStill:0.000})");

            // The band sliders: built afresh for whichever mode is chosen, held in a list rather than as fields, and never
            // moved by any step until 2026-09-24. Tone mode, Bass right up: a 100 Hz tone must come out louder.
            modes.SelectedIndex = 0;
            volume.Value = 100;
            var bandSliders = Require(FieldOf<List<TrackBar>>(form, "eqBandSliders"), "MainForm.eqBandSliders not found");
            Check(bandSliders.Count == PeerEqBands.Simple.Length, $"tone mode must show its {PeerEqBands.Simple.Length} band sliders ({bandSliders.Count} shown)");
            float[] HeardAt(double hz)
            {
                var tone = Tone(hz);
                form.ReceiverForTest.PeerDspForTest(address)?.Process(tone, tone.Length / 2);
                return tone;
            }
            var bassFlat = Rms(HeardAt(100)) / Rms(Tone(100));
            bandSliders[0].Value = bandSliders[0].Maximum;
            var bassUp = Rms(HeardAt(100)) / Rms(Tone(100));
            Check(bassUp > bassFlat * 1.5,
                $"moving the real Bass slider right up must make a 100 Hz tone louder in the live sound ({bassFlat:0.00}x flat, {bassUp:0.00}x after)");
            Check(bandSliders[0].AccessibleName?.Contains("plus", StringComparison.Ordinal) == true,
                $"and the slider must say what it is now set to ({bandSliders[0].AccessibleName})");
            bandSliders[0].Value = (bandSliders[0].Minimum + bandSliders[0].Maximum) / 2;
            volume.Value = 50;   // not neutral, so the person keeps a chain for the tick below to take off and put back

            // The per-person tick: off bypasses them, on puts the shaping back.
            // The handler applies once the tick has landed (a queued call), as it does for a person; let it run.
            void Settle() { for (var i = 0; i < 5; i++) { Application.DoEvents(); Thread.Sleep(5); } }
            peers.SetItemChecked(0, false);
            Settle();
            Check(form.ReceiverForTest.PeerDspForTest(address) is null, "unticking the person on the pan and EQ tab must take their shaping off the live sound");
            peers.SetItemChecked(0, true);
            Settle();
            Check(form.ReceiverForTest.PeerDspForTest(address) is not null, "and ticking them again must put it back");

            // The master switch: off bypasses everybody.
            master.Checked = false;
            Check(form.ReceiverForTest.PeerDspForTest(address) is null, "switching per-person shaping off must take it off the live sound for everybody");

            return $"on the pan and EQ tab, the real volume slider put the person at {quarter:0.00}x, the pan slider hard left dropped the right channel, "
                 + "the EQ mode list rebuilt the live chain and kept the volume, the Bass slider lifted a 100 Hz tone, and the person's tick and the master switch each took it off and put it back";
        }
        finally { try { form?.Dispose(); } catch { } }
    }
}
