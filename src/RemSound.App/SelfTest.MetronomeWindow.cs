using System.Net;
using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App;

/// <summary>
/// THE METRONOME IN THE WINDOW (Ed, 2026-09-28): the solo metronome playing with nothing connected and receiving off, its
/// clicks through the real WASAPI stage and ASIO output, never on an output that is sent as loopback, the menu, the hotkey,
/// the control channel, the settings window and the profile. Every step that plays anything plays it into buffers: no
/// device is ever opened.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>A window for a metronome step: headless, on a blank profile, its log tapped.</summary>
    private static MainForm MetronomeWindow(List<string> logged, Profile? profile = null)
    {
        var form = new MainForm(null, profile ?? Profile.NewBlank(), null, null, headless: true);
        _ = form.Handle;
        form.LogForTest.EventTapForTest = line => { lock (logged) logged.Add(line); };
        return form;
    }

    private static bool Logged(List<string> logged, params string[] words)
    {
        lock (logged) return logged.Any(l => words.All(w => l.Contains(w, StringComparison.Ordinal)));
    }

    /// <summary>
    /// METRONOME SOLO (the plan's "Outputs while nobody is connected"): with nothing connected and Receive audio off, starting
    /// the metronome opens the outputs for it alone - the ticked ones handed over as receiving would - while what arrives
    /// stays unplayed; stopping closes them again. Each logged with the configuration. WASAPI only, ASIO only and both.
    /// </summary>
    private static string? MetronomeSoloHoldsTheOutputs()
    {
        var said = new List<string>();
        foreach (var configuration in AudioConfigurations.All)
        {
            var logged = new List<string>();
            MainForm? form = null;
            try
            {
                try { form = MetronomeWindow(logged); }
                catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
                var main = form;
                SetAudioConfigurationForTest(main, configuration);
                var receiver = main.ReceiverForTest;
                var what = configuration.Describe();
                Check(!receiver.IsRunning && !receiver.OutputsRunning && !receiver.IsListenerRunning, $"{what}: premise - nothing connected, receiving off, no outputs open");
                var handedOver = receiver.SetOutputDevicesCallsForTest;

                main.MetronomeStartStopForTest("the menu");
                Check(main.MetronomeForTest.Playing, $"{what}: the metronome must be playing");
                Check(receiver.OutputsRunning && receiver.OutputsHeldForMetronome, $"{what}: THE OUTPUTS must open for the metronome alone");
                Check(!receiver.IsRunning && !receiver.PlaybackEnabled, $"{what}: and receiving must stay off - nothing that arrives is played");
                Check(receiver.SetOutputDevicesCallsForTest > handedOver, $"{what}: the ticked outputs must be handed over, as switching receiving on does");
                Check(receiver.Metronome.Schedule is { Audible: true }, $"{what}: the outputs must have a schedule to click from");
                Check(Logged(logged, "metronome: outputs opened for the metronome alone", what), $"{what}: and the log must say so, with the configuration");

                // Audio arriving is still dropped at the door: receiving is off.
                var peer = new IPEndPoint(IPAddress.Parse("192.168.77.31"), 47830);
                receiver.SetAllowedSenders([peer]);
                var (_, fingerprint) = RemSoundCrypto.ForPlainPassword("metronome-solo");
                var announce = new byte[RemPacket.HeaderSize + RemPacket.FormatPayloadWithFingerprintSize];
                RemPacket.WriteHeader(announce, RemPacketType.Format, 9, 1);
                var n = RemPacket.WriteFormatPayload(announce.AsSpan(RemPacket.HeaderSize), new AudioFormatInfo(48000, 2, 24, 1, 6, 288_000, (int)AudioTransportCodec.Pcm, 240), fingerprint);
                receiver.InjectExternalPacket(announce, RemPacket.HeaderSize + n, peer);
                Check(receiver.LiveSessionCountForTest == 0, $"{what}: a stream arriving while the metronome holds the outputs must not be played (receiving is off)");

                main.MetronomeStartStopForTest("the menu");
                Check(!main.MetronomeForTest.Playing && !receiver.OutputsRunning && !receiver.OutputsHeldForMetronome, $"{what}: stopped, the outputs must close again");
                Check(Logged(logged, "metronome: outputs closed again", what), $"{what}: and the log must say so");
                said.Add(what);
            }
            finally
            {
                try { form?.Dispose(); } catch { /* teardown */ }
            }
        }
        return "with nothing connected and receiving off, the outputs opened for the metronome alone and closed when it stopped, a stream "
             + "arriving meanwhile unplayed, each logged - " + string.Join(", ", said);
    }

    /// <summary>
    /// METRONOME OUTPUTS (measured to the sample, through the real output code): the WASAPI stage a card pulls through and the
    /// ASIO output's broadcast, each with its own click lane and delay - the WASAPI stage by the device's stated delay until it
    /// has measured one, then by the measured one; ASIO by the driver's stated delay. Each onset lands on
    /// round((t - (now + delay)) * rate). In both at once each output keeps its own delay and the same beat is heard at the
    /// same moment on both. A step in the measured delay is taken once two blocks agree.
    /// </summary>
    private static string? MetronomeThroughTheRealOutputs()
    {
        var sound = new MetronomeSound("test", Impulse(0.8f), Impulse(0.4f));
        var said = new List<string>();
        foreach (var configuration in AudioConfigurations.All)
        {
            long clock = 1_000_000;
            var run = MetronomeTimeline.Stopped(111_000, new MetronomeSignature(4, 4)).Start(clock);
            var outputs = new MetronomeOutputs { Schedule = new MetronomeSchedule(run, null, MetronomeClockMap.Identity, sound, 1, false, 1, () => clock) };
            var heard = new Dictionary<string, List<double>>();
            var problems = new List<string>();
            void Look(string name, float[] block, int channels, int frames, double ear)
            {
                for (var f = 0; f < frames; f++)
                {
                    var v = block[f * channels];
                    if (v < 0.39f) continue;
                    var beat = run.BeatAtOrAfter(ear + f * 1e6 / 48000 - 30);
                    var expected = (int)Math.Round((run.BeatTimeUs(beat) - ear) * 48000 / 1e6);
                    if (expected != f) problems.Add($"{name} beat {beat} at frame {f}, expected {expected}");
                    heard[name].Add(ear + f * 1e6 / 48000);
                }
            }

            // ASIO: the driver's callback, 64 frames at a time for four seconds, on a four-channel interface (pair 1 plays).
            if (configuration.UsesAsio())
            {
                heard["ASIO"] = [];
                var asio = AsioRenderBackend.BroadcastForTest(new SilenceSource(), 4, outputs, 5.333);
                clock = 1_000_000;
                double exact = clock;
                for (var i = 0; i < 4 * 48000 / 64; i++)
                {
                    var buffer = new byte[64 * 4 * sizeof(float)];
                    asio.Read(buffer, 0, buffer.Length);
                    var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer).ToArray();
                    Look("ASIO", floats, 4, 64, clock + 5_333.0);
                    exact += 64 * 1e6 / 48000;
                    clock = (long)Math.Round(exact);
                }
            }
            // WASAPI: the card pulls 10 ms through the stage, the producer's 10 ms feed before each, for four seconds.
            double pulled = 0;
            if (configuration.UsesWasapi())
            {
                heard["WASAPI"] = [];
                var stage = new MultiOutputPlayout.SimulatedStage("gate card", null, outputs, "{gate}.card") { StatedLatencyMs = 20 };
                var feed = new byte[MultiOutputPlayout.SimulatedStage.ProducerFrameBytes];
                clock = 1_000_000;
                for (var i = 0; i < 400; i++)
                {
                    stage.Feed(feed, 0, feed.Length);
                    var buffer = new byte[480 * 2 * sizeof(float)];
                    // Stated 20 ms for the first second; then the card says what it has played, 15 ms behind what it was given.
                    if (i == 100) stage.PlayedSeconds = () => pulled - 0.015;
                    var delay = i < 100 ? 20_000.0 : 15_000.0;
                    var ear = clock + delay;
                    stage.Read(buffer, 0, buffer.Length);
                    pulled += 0.010;
                    var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer).ToArray();
                    // Where the lane put this block: the stated delay, then (once the step is taken) the measured one.
                    var laneEar = stage.ClickLane.LastEarUs;
                    if (i < 100 || i > 102)
                    {
                        Check(Math.Abs(laneEar - ear) < 1, $"{configuration.Describe()}: block {i}: the WASAPI stage must place by {(i < 100 ? "the stated 20 ms" : "the measured 15 ms")} ({(laneEar - clock) / 1000:0.000} ms)");
                    }
                    Look("WASAPI", floats, 2, 480, laneEar);
                    clock += 10_000;
                }
                Check(stage.ClickLane.Snaps == 1, $"{configuration.Describe()}: the measured delay arriving must be taken as one step once two blocks agree ({stage.ClickLane.Snaps} steps)");
            }
            Check(problems.Count == 0, $"{configuration.Describe()}: every onset on its exact sample: {string.Join("; ", problems.Take(4))}");
            foreach (var (name, onsets) in heard)
            {
                var expected = Enumerable.Range(0, 60).Count(b => run.BeatTimeUs(b) >= 1_000_000 + (name == "ASIO" ? 5_333 : 20_000) && run.BeatTimeUs(b) < 1_000_000 + 4_000_000 + 5_333);
                Check(onsets.Count >= expected - 1 && onsets.Count <= expected + 1 && onsets.Count > 4, $"{configuration.Describe()}: {name} must click once a beat ({onsets.Count}, about {expected})");
            }
            if (heard.Count == 2)
            {
                // The same beats, heard at the same moments, whatever each output's delay.
                var worst = heard["ASIO"].Zip(heard["WASAPI"], (a, w) => Math.Abs(a - w)).Max();
                Check(worst <= 1e6 / 48000, $"{configuration.Describe()}: the same beat heard within a sample on both outputs ({worst:0.0} us)");
            }
            said.Add($"{configuration.Describe()}: {string.Join(" and ", heard.Keys)} on the sample");
        }
        return string.Join("; ", said) + "; the WASAPI stage by its stated delay, then its measured one in a single step";
    }

    /// <summary>Add a device of the test's own to one of the window's device lists, ticked, the way the window's own
    /// refreshes do it (no device handler fires).</summary>
    private static void AddTickedDeviceForTest(MainForm form, string listField, AudioDeviceChoice choice)
    {
        var list = Require(FieldOf<CheckedListBox>(form, listField), $"MainForm.{listField} not found");
        var suppress = Require(typeof(MainForm).GetField("suppressDeviceCheckChange", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            "MainForm.suppressDeviceCheckChange not found - a programmatic tick would fire the real device handlers");
        var previous = suppress.GetValue(form);
        suppress.SetValue(form, true);
        try { list.SetItemChecked(list.Items.Add(choice), true); }
        finally { suppress.SetValue(form, previous); }
    }

    /// <summary>
    /// METRONOME NEVER SENT (the plan's "never captured"): a profile that sends a loopback of the output the click would play
    /// on - the click would go to everyone - never plays it there, still plays it on its other outputs, and the window and the
    /// log say so. WASAPI only: the looped output silent, another WASAPI output clicking. ASIO only: the ASIO output clicks and
    /// nothing is said, as no output the click plays on is sent. Both: the looped WASAPI output silent, ASIO clicking.
    /// </summary>
    private static string? MetronomeNeverOnALoopbackSentOutput()
    {
        var said = new List<string>();
        foreach (var configuration in AudioConfigurations.All)
        {
            var what = configuration.Describe();
            var logged = new List<string>();
            MainForm? form = null;
            try
            {
                try { form = MetronomeWindow(logged); }
                catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
                var main = form;
                SetAudioConfigurationForTest(main, configuration);
                // The same card ticked as an output (when this configuration has WASAPI outputs) and ticked to send as loopback.
                if (configuration.UsesWasapi()) AddTickedDeviceForTest(main, "receiveOutputDevicesList", new AudioDeviceChoice("Gate speakers", "{gate}.speakers"));
                AddTickedDeviceForTest(main, "sendOutputDevicesList", new AudioDeviceChoice("Gate speakers", "{gate}.speakers", CaptureKind.Loopback));
                main.MetronomeStartStopForTest("the menu");
                var names = main.MetronomeLoopbackSentNamesForTest();
                var metronomeOutputs = main.ReceiverForTest.Metronome;
                Check(metronomeOutputs.IsLoopbackSent("{gate}.speakers"), $"{what}: the output sent as loopback must be kept from the click");
                if (configuration.UsesWasapi())
                {
                    Check(names.SequenceEqual(["Gate speakers"]), $"{what}: the window must name the output the click is kept off ({string.Join(", ", names)})");
                    Check(Logged(logged, "metronome: the click would be sent: it plays on the device you send (Gate speakers)", what), $"{what}: and the log must say so, with the configuration");
                    using var dialog = new MetronomeSettingsDialog(new MetronomeSettings(), null, names);
                    Check(dialog.LoopbackNoteShownForTest && dialog.LoopbackNoteForTest.Text.Contains("The click would be sent: it plays on the device you send (Gate speakers)", StringComparison.Ordinal),
                        $"{what}: and the settings window must say so ({dialog.LoopbackNoteForTest.Text})");
                    Check(dialog.HearListForTest.AccessibleDescription?.Contains("Gate speakers", StringComparison.Ordinal) == true, $"{what}: in words a screen reader reads, too");
                }
                else Check(names.Count == 0 && !Logged(logged, "the click would be sent"), $"{what}: no output the click plays on is sent, so nothing is said ({string.Join(", ", names)})");

                // The audio itself: the looped card, another card, and the ASIO output, each placing clicks from the schedule.
                var heard = new Dictionary<string, int>();
                var outputs = new List<(string Name, Func<byte[], int> Pull)>();
                if (configuration.UsesWasapi())
                {
                    foreach (var (name, id) in new[] { ("looped card", "{gate}.speakers"), ("other card", "{gate}.headphones") })
                    {
                        var stage = new MultiOutputPlayout.SimulatedStage(name, null, metronomeOutputs, id) { StatedLatencyMs = 10 };
                        var feed = new byte[MultiOutputPlayout.SimulatedStage.ProducerFrameBytes];
                        outputs.Add((name, b => { stage.Feed(feed, 0, feed.Length); return stage.Read(b, 0, b.Length); }));
                    }
                }
                if (configuration.UsesAsio())
                {
                    var asio = AsioRenderBackend.BroadcastForTest(new SilenceSource(), 2, metronomeOutputs, 5);
                    outputs.Add(("ASIO output", b => asio.Read(b, 0, b.Length)));
                }
                var until = DateTime.UtcNow.AddSeconds(1.6);
                foreach (var (name, _) in outputs) heard[name] = 0;
                while (DateTime.UtcNow < until)
                {
                    foreach (var (name, pull) in outputs)
                    {
                        var buffer = new byte[480 * 2 * sizeof(float)];
                        pull(buffer);
                        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer);
                        foreach (var v in floats) if (Math.Abs(v) > 0.01f) { heard[name]++; break; }
                    }
                    Thread.Sleep(10);
                }
                if (configuration.UsesWasapi())
                {
                    Check(heard["looped card"] == 0, $"{what}: NEVER SENT - the output sent as loopback must never play the click ({heard["looped card"]} blocks with sound)");
                    Check(heard["other card"] > 0, $"{what}: another WASAPI output must still click");
                }
                if (configuration.UsesAsio()) Check(heard["ASIO output"] > 0, $"{what}: the ASIO output must still click");
                main.MetronomeStartStopForTest("the menu");
                said.Add($"{what}: {string.Join(", ", heard.Select(h => $"{h.Key} {(h.Value > 0 ? "clicked" : "silent")}"))}");
            }
            finally
            {
                try { form?.Dispose(); } catch { /* teardown */ }
            }
        }
        return string.Join("; ", said);
    }

    /// <summary>
    /// METRONOME WINDOW: opened from the Metronome menu through the control channel; Cancel changes nothing; every control
    /// changed as a person would and OK keeps them in the profile, marked unsaved, in the metronome and in the log. The record
    /// box can be used only with "In RemSound". Moving along the sound list plays that sound's bar start and a beat - exactly a
    /// beat apart at the window's tempo, measured - and is silent when RemSound's sounds are muted. Following a master, the
    /// window shows the master's tempo and signature, unchangeable, and says whose.
    /// </summary>
    private static string? MetronomeWindowSavesToTheProfile()
    {
        // The preview, measured offline: the accent at the start, the beat exactly a beat later.
        var clickSound = Require(MetronomeSoundLibrary.Load("Click"), "the Click sound must load");
        var rendered = MetronomePreview.Render(clickSound, 500, 0.5f);
        var beatAt = 500 * MetronomePreview.SampleRate / 1000 * 2;
        Check(rendered[0] == clickSound.Accent[0] * 0.5f && rendered[beatAt] == clickSound.Beat[0] * 0.5f && Math.Abs(rendered[beatAt - 2]) < 1e-6,
            "the preview must be the accent, then the beat exactly 500 ms later at 120 bpm, at the window's volume");

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
        var pipe = "RemSound.control.selftest.metronome." + Guid.NewGuid().ToString("N")[..8];
        try
        {
            try { form = MetronomeWindow(logged); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            var main = form;
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

            DriveWhilePumping(main, () =>
            {
                Check(Ask("menu Metronome > Metronome settings").Contains("ok", StringComparison.Ordinal), "the menu must open Metronome settings");
                Check(Ok(Ask("set \"Tempo in beats per minute\" 90")), "the tempo box must take 90");
                Check(Ok(Ask("answer Cancel")), "Cancel closes it");
            }, TimeSpan.FromSeconds(30));
            Check(main.SettingsForTest.LoadMetronomeSettings().Bpm == 120 && !main.UnsavedChangesForTest, "CANCEL: a change then Cancel must leave the tempo at 120 and nothing unsaved");

            CuePlayer.GloballyMuted = false;
            var previewPlays = 0;
            DriveWhilePumping(main, () =>
            {
                Ask("menu Metronome > Metronome settings");
                Check(Ok(Ask("set \"Tempo in beats per minute\" 90")), "the tempo box must take 90");
                Check(Ok(Ask("select \"Time signature\" 6/8")), "the signature list must take 6/8");
                Check(Ok(Ask("select \"Click sound\" Woodblock")), "the sound list must take Woodblock");
                previewPlays = main.Invoke(() => Dialog().PreviewForTest.PlaysForTest);
                var preview = main.Invoke(() => (Dialog().PreviewForTest.LastSoundForTest, Dialog().PreviewForTest.LastBeatMsForTest));
                Check(previewPlays == 1 && preview == ("Woodblock", 667), $"moving along the sound list must play that sound, a beat apart at 90 bpm ({previewPlays}, {preview})");
                Check(Ok(Ask("set \"Volume: 70 %\" 55")), "the volume slider must take 55");
                Check(main.Invoke(() => Dialog().VolumeBarForTest.AccessibleName) == "Volume: 55 %", "the slider's name must say its level");
                Check(Ok(Ask("select \"Hear the click\" \"In the DAW (choose Metronome on a track's RemSound plugin)\"")), "the hear list must take the DAW");
                Check(main.Invoke(() => !Dialog().RecordBoxForTest.Enabled), "in the DAW, the recording box cannot be used");
                Check(Ok(Ask("select \"Hear the click\" \"In RemSound (your outputs)\"")), "and back to RemSound");
                Check(Ok(Ask("set \"Include the click in RemSound's recordings\" on")), "the recording box must tick");
                Check(Ok(Ask("select \"When someone asks to be the master metronome\" \"Accept automatically\"")), "the ask list must take accept automatically");
                Check(Ok(Ask("answer OK")), "OK closes it");
            }, TimeSpan.FromSeconds(30));
            var saved = main.SettingsForTest.LoadMetronomeSettings();
            Check(saved is { Bpm: 90, BeatsPerBar: 6, BeatUnit: 8, Sound: "Woodblock", Volume: 55, HearIn: MetronomeHearIn.RemSound, IncludeInRecordings: true,
                    WhenAsked: MetronomeMasterAnswer.AcceptAutomatically }, $"OK must keep every setting in the profile ({saved.Describe()})");
            Check(main.UnsavedChangesForTest, "and mark the profile unsaved");
            var engine = main.MetronomeForTest;
            Check(engine.Timeline.MilliBpm == 90_000 && engine.Timeline.Signature == new MetronomeSignature(6, 8) && engine.Sound?.Name == "Woodblock"
                  && Math.Abs(engine.Settings.Gain - 0.55f) < 1e-6, "and reach the metronome at once");
            Check(Logged(logged, "ui: metronome", "90 bpm 6/8", "sound Woodblock", "volume 55 %"), "and the change must be logged");

            // Muted, a move along the list is silent.
            CuePlayer.GloballyMuted = true;
            using (var quiet = new MetronomeSettingsDialog(saved, null, []))
            {
                quiet.SoundListForTest.SelectedIndex = 0;
                Check(quiet.PreviewForTest.PlaysForTest == 0, "with RemSound's sounds muted, a move along the sound list must be silent");
            }
            CuePlayer.GloballyMuted = false;
            using (var opened = new MetronomeSettingsDialog(saved, null, []))
                Check(opened.PreviewForTest.PlaysForTest == 0, "opening the window must be silent");

            // Following a master: the master's tempo and signature, shown and not changeable, and whose they are.
            using var following = new MetronomeSettingsDialog(saved, ("Ed's desktop", new MetronomeTimeline(3, true, 97_000, 7, 8, 0, 0)), []);
            Check(following.TempoBoxForTest.Value == 97 && !following.TempoBoxForTest.Enabled, $"following, the tempo must be the master's and not changeable ({following.TempoBoxForTest.Value})");
            Check(following.SignatureListForTest.SelectedItem as string == "7/8" && !following.SignatureListForTest.Enabled, "and the signature the master's");
            Check(following.MasterNoteShownForTest && following.MasterNoteForTest.Text == "Tempo and time signature are Ed's desktop's, the master metronome.",
                $"and a line must say whose ({following.MasterNoteForTest.Text})");
            following.SoundListForTest.SelectedIndex = 2;
            Check(following.Result.Bpm == 90 && following.Result.BeatsPerBar == 6, "the profile's own tempo and signature are left as they were");
            Check(following.Result.Sound == following.SoundListForTest.SelectedItem as string, "while the sound stays the follower's own to choose");
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
        return "Cancel changed nothing; OK kept tempo, signature, sound, volume, where heard, recording and the answer to a master in the "
             + "profile (unsaved), in the metronome and in the log; the preview played a beat apart and was silent when muted or opening; "
             + "following, the master's tempo and signature were shown and not changeable";
    }

    /// <summary>
    /// METRONOME MENU, HOTKEY AND CONTROL CHANNEL: the Metronome menu's Start and Stop chosen through the control channel,
    /// the hotkey's action, and the control channel's metronome commands - each starts or stops it, says so in the menu item
    /// and the status, and is logged with the configuration; the control channel's settings reach the profile and the
    /// metronome; "In the DAW" takes the click off RemSound's outputs; nonsense is refused. Mute and unmute are for a
    /// follower only (the master test drives them).
    /// </summary>
    private static string? MetronomeMenuHotkeyAndControl()
    {
        Windowless.InstallHook();
        Windowless.SetActiveForTest(true);
        var restoreWait = RemoteControl.CommandWait;
        RemoteControl.CommandWait = TimeSpan.FromSeconds(1);
        var logged = new List<string>();
        MainForm? form = null;
        RemoteControlServer? server = null;
        var pipe = "RemSound.control.selftest.metronome-menu." + Guid.NewGuid().ToString("N")[..8];
        try
        {
            try { form = MetronomeWindow(logged); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
            var main = form;
            var engine = main.MetronomeForTest;
            server = new RemoteControlServer(pipe, () => main, new RemoteControlEngine(() => main));
            server.Start();
            var replies = new List<string>();
            string Ask(string command)
            {
                var reply = RemoteControl.Send(command, pipe, out var reached, connectTimeoutMs: 5000);
                Check(reached, $"the control channel did not answer \"{command}\": {reply}");
                return reply;
            }
            Check(main.MetronomeStartStopItemForTest.AccessibleName == "Start metronome" && main.MetronomeStatusForTest is null, "a new window's item must say Start metronome, and the status nothing");
            var menus = "";
            DriveWhilePumping(main, () => { menus = Ask("menus"); Ask("menu Metronome > Start metronome"); }, TimeSpan.FromSeconds(20));
            Check(menus.Split(Environment.NewLine).Any(l => l.TrimEnd().StartsWith("Metronome", StringComparison.Ordinal)) && menus.Contains("Start metronome", StringComparison.Ordinal)
                  && menus.Contains("Be the master metronome", StringComparison.Ordinal) && menus.Contains("Metronome settings", StringComparison.Ordinal),
                $"the menu bar must have the Metronome menu with its three items ({menus})");
            Check(engine.Playing && main.MetronomeStartStopItemForTest.AccessibleName == "Stop metronome", "THE MENU: Start must start it, and the item then say Stop metronome");
            Check(main.MetronomeStatusForTest == "Metronome: playing 120 bpm 4/4", $"the status must say it ({main.MetronomeStatusForTest})");
            Check(Logged(logged, "metronome: started from the menu", ActiveConfigurationWords(main)), "and it must be logged, with the configuration");

            var toggle = Require(FieldOf<MainFormHotkeyController>(main, "hotkeyController")?.ToggleMetronome, "the metronome's hotkey action must be wired");
            toggle();
            Check(!engine.Playing && main.MetronomeStartStopItemForTest.AccessibleName == "Start metronome", "THE HOTKEY: its action must stop it");
            Check(Logged(logged, "metronome: stopped from the hotkey"), "and it must be logged");

            DriveWhilePumping(main, () =>
            {
                replies.Add(Ask("metronome start"));
                replies.Add(Ask("metronome tempo 90"));
                replies.Add(Ask("metronome signature 7/8"));
                replies.Add(Ask("metronome sound Beep"));
                replies.Add(Ask("metronome hear daw"));
                replies.Add(Ask("metronome state"));
                replies.Add(Ask("metronome hear remsound"));
                replies.Add(Ask("metronome mute"));
                replies.Add(Ask("metronome tempo 500"));
                replies.Add(Ask("metronome signature 11/16"));
                replies.Add(Ask("metronome stop"));
            }, TimeSpan.FromSeconds(30));
            Check(replies[0].StartsWith("ok: Metronome: playing 120 bpm 4/4", StringComparison.Ordinal), $"metronome start must start it ({replies[0]})");
            Check(replies[1].StartsWith("ok", StringComparison.Ordinal) && engine.Timeline.MilliBpm == 90_000 && main.SettingsForTest.LoadMetronomeSettings().Bpm == 90,
                $"metronome tempo must reach the metronome and the profile ({replies[1]})");
            Check(main.SettingsForTest.LoadMetronomeSettings() is { BeatsPerBar: 7, BeatUnit: 8, Sound: "Beep" }, "and the signature and the sound");
            Check(replies[5].Contains("heard in the DAW", StringComparison.Ordinal), $"metronome hear daw must say so ({replies[5]})");
            Check(replies[7].StartsWith("error", StringComparison.Ordinal) && replies[8].StartsWith("error", StringComparison.Ordinal) && replies[9].StartsWith("error", StringComparison.Ordinal),
                $"mute on your own metronome, a tempo of 500 and 11/16 must be refused ({replies[7]} / {replies[8]} / {replies[9]})");
            Check(replies[10].StartsWith("ok: Metronome: stopped", StringComparison.Ordinal) || replies[10].StartsWith("ok", StringComparison.Ordinal) && !engine.Playing,
                $"metronome stop must stop it ({replies[10]})");
            Check(main.UnsavedChangesForTest, "settings changed through the control channel must mark the profile unsaved");
            Check(Logged(logged, "metronome: started from the control channel") && Logged(logged, "ui: metronome", "90 bpm 7/8"), "each must be logged");

            // In the DAW: RemSound's outputs never get the click; the DAW's schedule does.
            main.ChangeMetronomeSettingsForTest(s => s.HearIn = MetronomeHearIn.Daw);
            main.MetronomeStartStopForTest("the menu");
            Check(main.ReceiverForTest.Metronome.Schedule is null && main.MetronomeDawScheduleForTest is { Audible: true } && !main.ReceiverForTest.OutputsRunning,
                "IN THE DAW: RemSound's outputs get no click and are not held open; the DAW's schedule plays");
            main.ChangeMetronomeSettingsForTest(s => s.HearIn = MetronomeHearIn.RemSound);
            Check(main.ReceiverForTest.Metronome.Schedule is { Audible: true } && main.MetronomeDawScheduleForTest is null && main.ReceiverForTest.OutputsRunning,
                "back in RemSound: the outputs play it, held open for it, and the DAW gets none");
            main.MetronomeStartStopForTest("the menu");
            main.LogForTest.EventTapForTest = null;
        }
        finally
        {
            server?.Dispose();
            RemoteControl.CommandWait = restoreWait;
            try { form?.Dispose(); } catch { /* teardown */ }
            Windowless.SetActiveForTest(false);
        }
        return "the menu started it and said Stop, the hotkey's action stopped it, the control channel started, set tempo, signature, sound and "
             + "where heard, and stopped it - each logged with the configuration and in the status; nonsense refused; in the DAW, RemSound's outputs had no click";
    }

    /// <summary>The configuration a window is in, as its log lines say it.</summary>
    private static string ActiveConfigurationWords(MainForm main)
    {
        var active = Require(typeof(MainForm).GetMethod("ActiveAudioConfiguration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            "MainForm.ActiveAudioConfiguration not found");
        return ((AudioConfiguration)Require(active.Invoke(main, null), "ActiveAudioConfiguration returned nothing")).Describe();
    }

    /// <summary>METRONOME PROFILE: a new profile's metronome is 120 bpm 4/4, Click at 70 %, heard in RemSound, not recorded,
    /// a master asked about; every setting kept with the profile and in its file; a profile saved by v6.0 loads those
    /// defaults; the start/stop key is kept on this computer, unset unless chosen, and listed in Keyboard shortcuts.</summary>
    private static string? MetronomeInTheProfile()
    {
        var fresh = Profile.NewBlank().Metronome;
        Check(fresh is { Bpm: 120, BeatsPerBar: 4, BeatUnit: 4, Sound: "Click", Volume: 70, HearIn: MetronomeHearIn.RemSound, IncludeInRecordings: false,
                WhenAsked: MetronomeMasterAnswer.Ask } && new Profile().Metronome.SameAs(fresh), $"a new profile's metronome ({fresh.Describe()})");
        var store = new RemSoundSettingsStore("RemSound");
        var backup = new Profile();
        store.CopyTo(backup);
        try
        {
            var p = Profile.NewBlank();
            p.Metronome = new MetronomeSettings { Bpm = 77, BeatsPerBar = 9, BeatUnit = 8, Sound = "Cowbell", Volume = 12, HearIn = MetronomeHearIn.Daw,
                IncludeInRecordings = true, WhenAsked = MetronomeMasterAnswer.AcceptAutomatically };
            store.ApplyProfile(p);
            var back = new Profile();
            store.CopyTo(back);
            Check(back.Metronome.SameAs(p.Metronome), $"every setting must round-trip the profile ({back.Metronome.Describe()})");
            var reread = System.Text.Json.JsonSerializer.Deserialize<Profile>(System.Text.Json.JsonSerializer.Serialize(p))!;
            Check(reread.Metronome.SameAs(p.Metronome), "and survive the profile file");
            var old = System.Text.Json.JsonSerializer.Deserialize<Profile>(V60Profile)!;
            Check(old.Metronome.SameAs(fresh), "a profile saved by v6.0, before the metronome, must load the defaults");
        }
        finally { store.ApplyProfile(backup); }

        var scratch = Path.Combine(Path.GetTempPath(), "remsound-metronome-key-" + Guid.NewGuid().ToString("N"));
        using (AppConfig.UseThrowawayUserDataDirectory(scratch))
        {
            var keys = new RemSoundSettingsStore("RemSound");
            Check(keys.LoadMetronomeHotkey().IsUnset, "the key is unset unless chosen");
            keys.SaveMetronomeHotkey(new HotkeyInfo(Keys.F13, true, false, false));
            Check(new RemSoundSettingsStore("RemSound").LoadMetronomeHotkey() == new HotkeyInfo(Keys.F13, true, false, false), "a key chosen is kept on this computer");
            var controller = new MainFormHotkeyController(keys, () => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { },
                () => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { });
            using var shortcuts = controller.BuildKeyboardShortcutsDialogForAudit();
            var rows = shortcuts.Controls.Cast<Control>().SelectMany(c => c.Controls.Cast<Control>().Prepend(c)).OfType<ListBox>()
                .SelectMany(l => l.Items.Cast<object>().Select(i => i.ToString() ?? "")).ToList();
            Check(rows.Any(r => r.StartsWith("Start or stop the metronome", StringComparison.Ordinal) && r.EndsWith("Control+F13", StringComparison.Ordinal)),
                $"Keyboard shortcuts must list the metronome's key ({rows.LastOrDefault()})");
        }
        return "120 bpm 4/4, Click at 70 %, in RemSound, not recorded, a master asked about; every setting kept with the profile and in the file; "
             + "a v6.0 profile loads the defaults; the key kept on this computer, unset unless chosen, and listed in Keyboard shortcuts";
    }

    /// <summary>METRONOME SERVICE: the send-only service has no metronome - a master's offer reaching its listener goes
    /// nowhere, whatever its profile says.</summary>
    private static string? MetronomeIsNotInTheService()
    {
        var profile = new Profile { Title = "service metronome", WasapiSendMode = "devices" };
        profile.SelectedWasapiSendOutputs.Add("fake-device-id");
        profile.SelectedConnectedPeers.Add("127.0.0.1:47999");
        profile.Password = RemSoundCrypto.Obfuscate("hunter2horse42stable");
        profile.Metronome = new MetronomeSettings { WhenAsked = MetronomeMasterAnswer.AcceptAutomatically };
        var restorePresencePort = ServiceSendHost.PresencePortForTest;
        ServiceSendHost.PresencePortForTest = FreeUdpPort();
        using var restorePort = new Disposer(() => ServiceSendHost.PresencePortForTest = restorePresencePort);
        using var host = new ServiceSendHost(() => profile);
        host.ApplyProfile(profile);
        var presence = Require(FieldOf<ServiceNetworkPresence>(host, "presence"), "ServiceSendHost.presence not found");
        var receiver = Require(FieldOf<AudioReceiver>(presence, "receiver"), "ServiceNetworkPresence.receiver not found");
        Check(receiver.OnMetronomeReceived is null && !receiver.OutputsRunning, "the service must read no metronome message and hold no output for one");
        var key = Require(RemSoundCrypto.ForPlainPassword("hunter2horse42stable").Key, "a key");
        var offer = MetronomeWire.BuildPacket(key, new MetronomeMessage(MetronomeKind.Offer, 7) { MasterName = "Master" }, 1);
        receiver.InjectExternalPacket(offer, offer.Length, new IPEndPoint(IPAddress.Loopback, 47999));
        Check(!receiver.OutputsRunning, "and an offer arriving changes nothing");
        return "a service profile set to accept a master automatically: the service reads no metronome message and plays nothing";
    }

    private sealed class SilenceSource : NAudio.Wave.IWaveProvider
    {
        public NAudio.Wave.WaveFormat WaveFormat { get; } = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public int Read(byte[] buffer, int offset, int count) { Array.Clear(buffer, offset, count); return count; }
    }
}
