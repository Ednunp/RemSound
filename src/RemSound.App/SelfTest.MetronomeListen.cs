using System.Net;
using RemSound.Core;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>METRONOME LISTEN (Ed, 2026-10-02: "before any of the metronome settings, there should be a button to listen to
    /// the metronome with current settings and it should change to stop metronome once playing. changing speed, volume, click
    /// sounds etc all have an impact real time"). Through the control channel, as a person uses the window: Listen starts the
    /// metronome and becomes Stop metronome; tempo, volume and sound changed in the window are heard at once, not yet kept in
    /// the profile; Cancel puts the profile's settings back and stops the metronome the window started; OK keeps them and
    /// leaves it going. The button follows a start from elsewhere, and says Mute or Unmute while following a master. The first
    /// shortcut says what it does while following.</summary>
    private static string? MetronomeListenInTheWindow()
    {
        Check(MetronomeSettingsDialog.KeyNames[0] == "Start or stop the metronome (mute or unmute while following a master)",
            $"THE FIRST SHORTCUT: says it mutes or unmutes while following ({MetronomeSettingsDialog.KeyNames[0]})");
        // Up means louder (Ed, 2026-10-02: "right now down arrow makes it go louder which is not logical"): the keys as Windows
        // delivers them to the slider, which moves itself.
        using (var window = new MetronomeSettingsDialog(new MetronomeSettings { Volume = 50 }, null, []))
        {
            var bar = window.VolumeBarForTest;
            _ = bar.Handle;
            int Press(int key) { SendMessage(bar.Handle, 0x0100, key, IntPtr.Zero); SendMessage(bar.Handle, 0x0101, key, IntPtr.Zero); return bar.Value; }
            Check(Press(0x26) == 55, $"THE VOLUME: Up must make it louder, a step of 5 ({bar.Value})");
            Check(Press(0x28) == 50, $"Down quieter ({bar.Value})");
            Check(Press(0x21) == 60, $"Page Up louder by 10 ({bar.Value})");
            Check(Press(0x22) == 50, $"Page Down quieter by 10 ({bar.Value})");
            Check(Press(0x27) == 55 && Press(0x25) == 50, $"and Right and Left as on any slider ({bar.Value})");
            Check(bar.AccessibleName == "Volume: 50 %", $"and its name says the level ({bar.AccessibleName})");
        }
        var restoreMuted = CuePlayer.GloballyMuted;
        var restoreNoDevice = CuePlayer.NoDeviceForTest;
        CuePlayer.NoDeviceForTest = true;
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        var restoreWait = RemoteControl.CommandWait;
        RemoteControl.CommandWait = TimeSpan.FromSeconds(1);
        var logged = new List<string>();
        MainForm? form = null;
        RemoteControlServer? server = null;
        var pipe = "RemSound.control.selftest.listen." + Guid.NewGuid().ToString("N")[..8];
        try
        {
            try { form = MetronomeWindow(logged); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            var main = form;
            var engine = main.MetronomeForTest;
            server = new RemoteControlServer(pipe, () => main, new RemoteControlEngine(() => main));
            server.Start();
            string Ask(string command)
            {
                var reply = RemoteControl.Send(command, pipe, out var reached, connectTimeoutMs: 5000);
                Check(reached, $"the control channel did not answer \"{command}\": {reply}");
                return reply;
            }
            bool Ok(string reply) => reply.StartsWith("ok", StringComparison.Ordinal);
            MetronomeSettingsDialog Dialog() => main.Invoke(() => Application.OpenForms.OfType<MetronomeSettingsDialog>().Single());
            Check(!engine.Playing && main.SettingsForTest.LoadMetronomeSettings() is { Bpm: 120, Volume: 70, Sound: "Click" }, "premise: stopped, at the defaults");

            CuePlayer.GloballyMuted = false;
            DriveWhilePumping(main, () =>
            {
                Check(Ask("menu Metronome > Metronome settings").Contains("ok", StringComparison.Ordinal), "the menu must open Metronome settings");
                Check(main.Invoke(() => Dialog().ListenButtonForTest.AccessibleName) == "Listen to the metronome", "THE BUTTON: Listen to the metronome, first");
                Check(Ok(Ask("click \"Listen to the metronome\"")), "Listen must press");
                Check(main.Invoke(() => engine.Playing) && main.Invoke(() => Dialog().ListenButtonForTest.AccessibleName) == "Stop metronome",
                    "LISTEN: the metronome must start, and the button become Stop metronome");
                Check(Ok(Ask("set \"Tempo in beats per minute\" 90")), "the tempo box must take 90");
                Check(main.Invoke(() => engine.Timeline.MilliBpm) == 90_000 && main.SettingsForTest.LoadMetronomeSettings().Bpm == 120,
                    $"HEARD AT ONCE: the tempo in the metronome now ({main.Invoke(() => engine.Timeline.MilliBpm)}), not yet in the profile");
                Check(Ok(Ask("set \"Volume: 70 %\" 40")), "the volume slider must take 40");
                Check(Math.Abs(main.Invoke(() => engine.Settings.Gain) - 0.40f) < 1e-6, "and the volume");
                var previews = main.Invoke(() => Dialog().PreviewForTest.PlaysForTest);
                Check(Ok(Ask("select \"Click sound\" Woodblock")), "the sound list must take Woodblock");
                Check(main.Invoke(() => engine.Sound?.Name) == "Woodblock", "and the sound");
                Check(main.Invoke(() => Dialog().PreviewForTest.PlaysForTest) == previews, "with the metronome sounding, no preview on top of it");
                Check(Ok(Ask("answer Cancel")), "Cancel closes it");
            }, TimeSpan.FromSeconds(30));
            Check(!engine.Playing && engine.Timeline.MilliBpm == 120_000 && Math.Abs(engine.Settings.Gain - 0.70f) < 1e-6 && engine.Sound?.Name == "Click",
                $"CANCEL: the profile's settings back in the metronome, and the metronome the window started stopped ({engine.Timeline.MilliBpm}, {engine.Settings.Gain}, {engine.Sound?.Name}, playing {engine.Playing})");
            Check(main.SettingsForTest.LoadMetronomeSettings().Bpm == 120 && !main.UnsavedChangesForTest, "and nothing kept or unsaved");
            Check(Logged(logged, "metronome: settings window cancelled - the profile's settings heard again"), "and the log says so");

            DriveWhilePumping(main, () =>
            {
                Ask("menu Metronome > Metronome settings");
                Check(Ok(Ask("click \"Listen to the metronome\"")), "Listen must press again");
                Check(Ok(Ask("set \"Tempo in beats per minute\" 100")), "the tempo box must take 100");
                Check(Ok(Ask("answer OK")), "OK closes it");
            }, TimeSpan.FromSeconds(30));
            Check(engine.Playing && engine.Timeline.MilliBpm == 100_000 && main.SettingsForTest.LoadMetronomeSettings().Bpm == 100 && main.UnsavedChangesForTest,
                "OK: kept in the profile (unsaved) and the metronome left going at it");
            main.MetronomeStartStopForTest("test");
            Check(!engine.Playing, "premise: stopped again");

            // The button follows the metronome started from elsewhere, and says Mute or Unmute while following.
            using (var dialog = main.BuildMetronomeSettingsDialogForTest(out _))
            {
                Check(dialog.ListenButtonForTest.AccessibleName == "Listen to the metronome", "a new window says Listen");
                main.MetronomeStartStopForTest("the Metronome menu");
                dialog.WatchStateForTest();
                Check(dialog.ListenButtonForTest.AccessibleName == "Stop metronome", "STARTED ELSEWHERE: the button follows (Stop metronome)");
                main.MetronomeStartStopForTest("the Metronome menu");
            }
            engine.AnswerOffer(new MetronomeOffer(0x7e59, new IPEndPoint(IPAddress.Parse("198.51.100.82"), 47999), "Andre"), accept: true);
            Check(engine.Role == MetronomeRole.Follower, "premise: following Andre's master metronome");
            using (var following = main.BuildMetronomeSettingsDialogForTest(out _))
            {
                var expected = engine.Muted ? "Unmute metronome" : "Mute metronome";
                Check(following.ListenButtonForTest.AccessibleName == expected, $"FOLLOWING: the button mutes or unmutes ({following.ListenButtonForTest.AccessibleName})");
            }
            engine.StopFollowing("test", sendLeave: false);
            main.LogForTest.EventTapForTest = null;
        }
        finally
        {
            server?.Dispose();
            RemoteControl.CommandWait = restoreWait;
            try { form?.Dispose(); } catch { /* teardown */ }
            Windowless.SetActiveForTest(false);
            CuePlayer.GloballyMuted = restoreMuted;
            CuePlayer.NoDeviceForTest = restoreNoDevice;
        }
        return "Listen started the metronome and became Stop metronome; tempo, volume and sound were heard at once and not kept until OK; "
             + "Cancel put the profile's settings back and stopped what the window started; OK kept them and left it going; the button "
             + "followed a start from the menu and said Mute or Unmute while following; the first shortcut says so too";
    }
}
