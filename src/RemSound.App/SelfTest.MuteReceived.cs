using System.Net;
using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>MUTE RECEIVED AUDIO (Ed, 2026-10-02: "might as well add the mute and global hotkey for it", the Mac's "Mute
    /// received audio" on Windows). Measured in the engine, WASAPI only, ASIO only and both: muted, every speaker lane plays
    /// silence while the peer's buffer keeps being read, a DAW plugin's claimed peer still reaches the plugin at full level,
    /// and unmuting is heard on the next read. In the window: the box comes straight after Receive audio with Alt+M, and
    /// drives the engine's mute, the profile and the log; a profile kept muted opens muted and shows it; the hotkey is unset
    /// unless chosen, kept on this computer, listed in Keyboard shortcuts and flips the box; a peer's remote mute toggle shows
    /// in the box; the control channel ticks it by its name.</summary>
    private static string? MuteReceivedAudio()
    {
        // 1. The sound itself, in each configuration.
        var said = new List<string>();
        foreach (var configuration in AudioConfigurations.All)
        {
            var name = configuration.Describe();
            var engine = NewEngineFor(configuration);
            var peer = new IPEndPoint(IPAddress.Parse("198.51.100.90"), 47999);
            var session = engine.GetOrCreateSession(peer, 4242, 4 * 1024 * 1024);
            FillSession(session, 0.5f);
            var heard = SpeakerPeak(engine, configuration);
            Check(heard > 0.2f, $"{name}: premise - unmuted, the speakers must play the peer (peak {heard:0.000})");

            engine.IsMuted = true;
            FillSession(session, 0.5f);
            var before = session.BufferedBytes;
            var muted = 0f;
            for (var i = 0; i < 5; i++) muted = Math.Max(muted, SpeakerPeak(engine, configuration));
            Check(muted == 0f, $"{name}: MUTED, every speaker lane must play silence (peak {muted:0.000})");
            Check(session.BufferedBytes < before,
                $"{name}: and the peer's buffer must still be read, so nothing piles up and unmuting is instant ({before} then {session.BufferedBytes} bytes)");

            // A peer a DAW plugin has taken still reaches the plugin: the mute is the speakers'.
            var claims = new PluginPeerClaims();
            engine.SetPluginPeerClaims(claims);
            var daw = new IPEndPoint(IPAddress.Parse("198.51.100.91"), 47999);
            claims.Claim(daw.Address, Guid.NewGuid());
            FillSession(engine.GetOrCreateSession(daw, 4243, 4 * 1024 * 1024), 0.5f);
            var block = new float[128 * 2];
            var plugin = 0f;
            for (var i = 0; i < 10; i++)
                if (engine.ReadClaimedPeer(daw.Address, block, 128) > 0)
                    foreach (var f in block) plugin = Math.Max(plugin, Math.Abs(f));
            Check(plugin > 0.4f, $"{name}: THE PLUGIN: a claimed peer must still reach the DAW plugin at full level while muted (peak {plugin:0.000})");

            engine.IsMuted = false;
            FillSession(session, 0.5f);
            var back = SpeakerPeak(engine, configuration);
            Check(back > 0.2f, $"{name}: UNMUTED, heard on the next read (peak {back:0.000})");
            said.Add($"{name} {heard:0.00} / {muted:0.00} / {back:0.00}, plugin {plugin:0.00}");
        }

        // 2. The hotkey, kept on this computer and listed.
        var muteKey = new HotkeyInfo(Keys.F21, true, false, false);
        var scratch = Path.Combine(Path.GetTempPath(), "remsound-mute-received-" + Guid.NewGuid().ToString("N"));
        using (AppConfig.UseThrowawayUserDataDirectory(scratch))
        {
            var keys = new RemSoundSettingsStore("RemSound");
            Check(keys.LoadMuteReceivedHotkey().IsUnset, "the mute key is unset unless chosen");
            keys.SaveMuteReceivedHotkey(muteKey);
            Check(new RemSoundSettingsStore("RemSound").LoadMuteReceivedHotkey() == muteKey, "a key chosen is kept on this computer");
            var controller = new MainFormHotkeyController(keys, () => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { },
                () => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { });
            using var shortcuts = controller.BuildKeyboardShortcutsDialogForAudit();
            var rows = AllControls(shortcuts).OfType<ListBox>().SelectMany(l => l.Items.Cast<object>().Select(i => i.ToString() ?? "")).ToList();
            Check(rows.LastOrDefault() == "Mute received audio: Control+F21",
                $"Keyboard shortcuts must list it, last ({rows.LastOrDefault()})");
        }

        // 3. The window.
        var logged = new List<string>();
        MainForm? form = null;
        MainForm? reopened = null;
        try
        {
            try { form = MetronomeWindow(logged); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            var main = form;
            var box = main.MuteReceivedBoxForTest;
            Check(box.Text == "Mute received audio (Alt+&M)" && box.AccessibleName == "Mute received audio",
                $"THE BOX: Mute received audio, Alt+M ({box.Text} / {box.AccessibleName})");
            Check(!box.Checked && !main.ReceiveMutedForTest, "a new profile is not muted");

            // Tab meets it straight after Receive audio, walked as Tab walks.
            var receive = Require(FieldOf<CheckBox>(main, "receiveAudioCheckbox"), "MainForm.receiveAudioCheckbox not found");
            var walk = new List<Control>();
            var seen = new HashSet<Control>();
            for (var c = main.GetNextControl(main, true); c is not null && seen.Add(c); c = main.GetNextControl(c, true))
                if (IsSetVisibleForAltAudit(c) && c.Enabled && c.TabStop) walk.Add(c);
            var receiveAt = walk.IndexOf(receive);
            var muteAt = walk.IndexOf(box);
            Check(receiveAt >= 0 && muteAt == receiveAt + 1, $"TAB: Mute received audio must come straight after Receive audio (receive {receiveAt}, mute {muteAt})");

            box.Checked = true;
            Check(main.ReceiveMutedForTest, "TICKED: the engine is muted");
            Check(main.UnsavedChangesForTest && main.CaptureProfileForTest("t").Muted, "and the profile keeps it (unsaved until saved)");
            Check(Logged(logged, "ui: mute received audio → on [", "]"), "and the log says so, with the configuration");
            box.Checked = false;
            Check(!main.ReceiveMutedForTest && !main.CaptureProfileForTest("t").Muted && Logged(logged, "ui: mute received audio → off"),
                "UNTICKED: unmuted, in the profile and the log");

            // The hotkey's action flips the box.
            var hotkeys = Require(FieldOf<MainFormHotkeyController>(main, "hotkeyController"), "the window's hotkey controller");
            var toggle = Require(hotkeys.ToggleMuteReceived, "the mute key's action must be wired");
            toggle();
            Check(box.Checked && main.ReceiveMutedForTest, "THE HOTKEY: a press mutes, and the box shows it");
            toggle();
            Check(!box.Checked && !main.ReceiveMutedForTest, "and the next unmutes");

            // A peer's remote mute toggle shows in the box.
            var remote = new IPEndPoint(IPAddress.Parse("198.51.100.92"), 47830);
            main.ToggleMuteReceivedFromRemoteForTest(remote);
            Check(box.Checked && main.ReceiveMutedForTest, "REMOTE: a peer's mute toggle must tick the box, not mute where nothing shows it");
            Check(Logged(logged, "remote-control APPLIED kind=MuteToggle muted=True from=198.51.100.92"), "and the log line stays the one both ports write");
            main.ToggleMuteReceivedFromRemoteForTest(remote);
            Check(!box.Checked && !main.ReceiveMutedForTest, "and the next one unticks it");

            // The control channel, by the name a screen reader reads.
            var control = new RemoteControlEngine(() => main);
            control.Execute("tab Audio inputs and outputs");
            var set = control.Execute("set \"Mute received audio\" on");
            Check(set.StartsWith("ok", StringComparison.Ordinal) && box.Checked && main.ReceiveMutedForTest, $"THE CONTROL CHANNEL: ticks it by name ({set})");
            control.Execute("set \"Mute received audio\" off");
            Check(!main.ReceiveMutedForTest, "and unticks it");

            // A profile kept muted opens muted, and says so: the profile put on the window's controls as its first showing does.
            var kept = Profile.NewBlank();
            kept.Muted = true;
            reopened = MetronomeWindow(logged);
            var captured = reopened.ApplyThenCaptureForTest(kept);
            Check(reopened.MuteReceivedBoxForTest.Checked && reopened.ReceiveMutedForTest,
                $"A PROFILE KEPT MUTED: opens muted, with the box ticked so it can be seen (box {reopened.MuteReceivedBoxForTest.Checked}, engine {reopened.ReceiveMutedForTest})");
            Check(!reopened.UnsavedChangesForTest && captured.Muted, "with nothing unsaved, and kept again as it was");
            var unmuted = Profile.NewBlank();
            reopened.ApplyThenCaptureForTest(unmuted);
            Check(!reopened.MuteReceivedBoxForTest.Checked && !reopened.ReceiveMutedForTest, "and a profile kept unmuted, opened after it, unmutes");
            main.LogForTest.EventTapForTest = null;
            reopened.LogForTest.EventTapForTest = null;
        }
        finally
        {
            try { reopened?.Dispose(); } catch { /* teardown */ }
            try { form?.Dispose(); } catch { /* teardown */ }
        }
        return $"muted, every speaker lane silent while the buffer is read and a plugin's peer still served - {string.Join("; ", said)}; "
             + "the box after Receive audio with Alt+M drives the engine, the profile and the log; the hotkey is kept, listed and "
             + "flips it; a peer's remote toggle and the control channel show in it; a profile kept muted opens ticked";
    }
}
