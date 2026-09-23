using RemSound.Core;
using RemSound.Receiver;

namespace RemSound.App;

/// <summary>
/// The second batch from the 2026-09-13 review, stage 2: things people could hear or trip over.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>
    /// A DESELECTED OR SWITCHED ASIO DRIVER IS GIVEN TIME TO CLOSE PROPERLY.
    ///
    /// <para>2026-09-13 review (Receiver/Sender #7). Choosing "(none)" or a different ASIO driver released the old
    /// driver in the background through Dispose — the SHUTDOWN path, which gives a driver 3 seconds because the OS
    /// reclaims the card at exit anyway. The Audient takes 5 to 8 seconds to close. So the close was abandoned half
    /// way, and the next thing to open the card could find it still held. The release now waits as long as an
    /// interactive close deserves; it runs on a worker, so nothing is kept waiting for it.</para>
    ///
    /// <para>No driver opens here: the capture backend reports the bound it was asked to close with.</para>
    /// </summary>
    private static string? AuditAsioDriverReleaseIsNotRushed()
    {
        var found = new List<string>();
        foreach (var (why, nextDriver) in new[]
                 {
                     ("deselecting the driver", (string?)null),
                     ("switching to another driver", "RemSound self-test - another driver"),
                 })
        {
            using var sender = new RemSound.Sender.AudioSender();
            sender.SetAudioMode(AudioMode.BothIndependent, "RemSound self-test - no such ASIO driver");
            var backend = Require(sender.PersistentAsioForTest, "choosing an ASIO driver must create its capture backend");
            var bounds = new System.Collections.Concurrent.ConcurrentQueue<int>();
            backend.CloseBoundObservedForTest = bounds.Enqueue;

            sender.SetAudioMode(nextDriver is null ? AudioMode.WasapiOnly : AudioMode.BothIndependent, nextDriver);

            Check(WaitUntil(() => !bounds.IsEmpty), $"{why} must release the old driver");
            bounds.TryPeek(out var first);
            Check(first == RemSound.Sender.AsioCaptureBackend.CloseTimeoutMs,
                $"{why} must close the old driver with the interactive bound ({RemSound.Sender.AsioCaptureBackend.CloseTimeoutMs} ms), "
                + $"not a {first} ms one — the Audient takes 5 to 8 s to close, so a shorter wait abandons the close and the "
                + "card can still be held when it is opened next");
            found.Add($"{why}: {first / 1000} s");
        }
        return "the old ASIO driver is let go with the interactive close bound — " + string.Join("; ", found);
    }

    /// <summary>
    /// A STREAM THAT COMES BACK LATE AFTER A WAKE CANNOT UNDO A RAISE.
    ///
    /// <para>2026-09-13 review (MainForm #2). After a wake restart the tune from before the sleep is put back, and for the
    /// next minute a returning stream or output puts it back again. The tuner's hold ends after ten seconds, so a stream kept
    /// away by a slow network can arrive after the tuner has raised on real underruns — and putting the lower pre-sleep tune
    /// back then dropped the buffer in one step. The descent must never be sped up.</para>
    /// </summary>
    private static string? AuditLateStreamAfterWakeKeepsARaise()
    {
        var proven = new List<string>();
        foreach (var configuration in AudioConfigurations.All)
        {
            MainForm form;
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }

            using (form)
            {
                var receiver = form.ReceiverForTest;
                try
                {
                    SetAudioConfigurationForTest(form, configuration);
                    receiver.SetIndependentLaneLatency(configuration != AudioConfiguration.WasapiOnly);
                    var name = configuration.Describe();

                    var sleptAt = DateTime.UtcNow - TimeSpan.FromSeconds(98);
                    var reference = new TuneHistory();
                    FeedFieldShapedTuneHistory(sample => { reference.Note(sample); form.NoteTuneHistoryForTest(sample); }, sleptAt);
                    Check(reference.BeforeSleep(sleptAt) is not null, $"{name}: premise — the fed history must give a tune");
                    var before = reference.BeforeSleep(sleptAt)!.Value;
                    form.RewindTickClockForTest(TimeSpan.FromSeconds(98));
                    form.SnapshotTickForTest();
                    form.SimulateSystemResumeForTest();
                    var restored = form.CurrentTuneForTest();
                    Check(restored.MainSliderMs == before.MainSliderMs && restored.AsioSliderMs == before.AsioSliderMs,
                        $"{name}: premise — the wake must put back the tune from before the sleep ({before.MainSliderMs}/{before.AsioSliderMs}; "
                        + $"got {restored.MainSliderMs}/{restored.AsioSliderMs})");

                    // THE TUNER RAISES on real underruns while the restarted streams are still on their way back...
                    var raised = new TuneSnapshot(DateTime.UtcNow, before.MainSliderMs + 9, before.AsioSliderMs + 9,
                        before.MixedFloorMs + 6, before.WasapiFloorMs + 6, before.AsioFloorMs + 6);
                    form.SetTuneForTest(raised);

                    // ...and THEN a stream turns up late, and every output comes back.
                    form.NewStreamSessionsForTest();
                    foreach (var lane in new[] { RenderRoute.Mixed, RenderRoute.WasapiLane, RenderRoute.AsioLane })
                    {
                        form.NoteLaneAudibilityForTest(lane, consuming: false);
                        form.NoteLaneAudibilityForTest(lane, consuming: true);
                    }

                    var after = form.CurrentTuneForTest();
                    Check(after.MainSliderMs >= raised.MainSliderMs && after.AsioSliderMs >= raised.AsioSliderMs,
                        $"{name}: a stream arriving late after the wake must not put the sliders back below the raise the tuner has just "
                        + $"made (raised to {raised.MainSliderMs}/{raised.AsioSliderMs}, now {after.MainSliderMs}/{after.AsioSliderMs}) — dropping "
                        + "it in one step is the descent sped up");
                    Check(after.MixedFloorMs >= raised.MixedFloorMs && after.WasapiFloorMs >= raised.WasapiFloorMs
                          && after.AsioFloorMs >= raised.AsioFloorMs,
                        $"{name}: ...nor the learned floors (raised to {raised.MixedFloorMs}/{raised.WasapiFloorMs}/{raised.AsioFloorMs}, now "
                        + $"{after.MixedFloorMs}/{after.WasapiFloorMs}/{after.AsioFloorMs})");
                    proven.Add(name);
                }
                finally { receiver.SetPlaybackEnabled(false); }
            }
        }
        return $"in {string.Join(", ", proven)}, a stream or output returning after a wake keeps any raise the tuner has made since the restart";
    }

    /// <summary>
    /// A PEER OR SOURCE CHANGE THROWS AWAY EVERY READING THE TUNER DECIDES FROM — AND KEEPS WHAT IT LEARNED.
    ///
    /// <para>2026-09-13 review (MainForm #9). Selecting a peer or changing a capture source cleared only the two shared gap
    /// windows. The tuner reads each lane's own render-gap and low-water windows first, so a descent step straight after the
    /// change could still be decided on readings from before it. Three routines each cleared a different subset; the change
    /// now goes through the one that clears everything.</para>
    /// </summary>
    private static string? AuditPeerOrSourceChangeForgetsEveryReading()
    {
        var proven = new List<string>();
        foreach (var configuration in AudioConfigurations.All)
        {
            MainForm form;
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }

            using (form)
            {
                SetAudioConfigurationForTest(form, configuration);
                var name = configuration.Describe();
                RenderRoute[] tuned = configuration switch
                {
                    AudioConfiguration.WasapiOnly => [RenderRoute.Mixed],
                    AudioConfiguration.AsioOnly => [RenderRoute.AsioLane],
                    _ => [RenderRoute.WasapiLane, RenderRoute.AsioLane],
                };
                var floors = new Dictionary<RenderRoute, int>();
                foreach (var lane in tuned)
                {
                    form.SeedTuneEvidenceForTest(lane, renderGapMs: 12, floorMs: 30);
                    var seeded = form.TuneEvidenceForTest(lane);
                    Check(seeded.LaneRenderGaps > 0 && seeded.FloorMs > 0,
                        $"{name}: premise — the {lane} lane must hold readings and a learned floor before the change");
                    floors[lane] = seeded.FloorMs;
                }

                form.InvalidateAutoTuneHistoryForTest();

                foreach (var lane in tuned)
                {
                    var e = form.TuneEvidenceForTest(lane);
                    Check(e.LaneRenderGaps == 0 && e.SharedRenderGaps == 0,
                        $"{name}: after a peer or source change the {lane} lane's OWN readings must go too (lane window {e.LaneRenderGaps}, "
                        + $"shared {e.SharedRenderGaps}) — the tuner reads the lane's window first");
                    Check(e.NeedsBaseline, $"{name}: ...and the {lane} lane's underrun count must be re-based");
                    Check(e.FloorMs == floors[lane],
                        $"{name}: ...while what the {lane} lane LEARNED is kept (floor {floors[lane]} before, {e.FloorMs} after)");
                }
                proven.Add(name);
            }
        }
        return $"in {string.Join(", ", proven)}, a peer or source change clears every reading on every tuned lane and keeps the learned floor";
    }

    /// <summary>
    /// EVERY ROUTE A TUNER TUNES NOTICES ITS OUTPUT STOPPING AND COMING BACK — THE MIXED ROUTE INCLUDED.
    ///
    /// <para>2026-09-13 review (MainForm #3). A lane whose output leaves and returns forgets the floor it learned about the
    /// old device. That was wired for the WASAPI and ASIO lanes only. With no ASIO driver chosen — the commonest setup —
    /// the tuner tunes the Mixed route, which was never asked, and which answered "taking audio" whatever happened. So a
    /// headset that left and came back kept its old floor for good.</para>
    /// </summary>
    private static string? AuditEveryTunedRouteNoticesItsOutputStopping()
    {
        const int blockBytes = 480 * 8;
        var buffer = new byte[blockBytes];
        var mix = new float[960];
        var session = new float[960];
        var proven = new List<string>();
        foreach (var configuration in AudioConfigurations.All)
        {
            // Rendered the way each configuration renders: WASAPI only, with no driver chosen, through the all-sessions read
            // (the Mixed route); the other two through each lane's own read.
            var engine = new PlayoutEngine(new ReceiverDiagnostics());
            engine.SetLaneActive(RenderRoute.WasapiLane, configuration.UsesWasapi());
            engine.SetLaneActive(RenderRoute.AsioLane, configuration.UsesAsio());
            RenderRoute[] routes = configuration switch
            {
                AudioConfiguration.WasapiOnly => [RenderRoute.Mixed],
                AudioConfiguration.AsioOnly => [RenderRoute.AsioLane],
                _ => [RenderRoute.WasapiLane, RenderRoute.AsioLane],
            };
            void ReadOnce(RenderRoute route)
            {
                if (route == RenderRoute.Mixed) engine.Read(buffer, 0, blockBytes);
                else engine.ReadForRoute(buffer, 0, blockBytes, route, mix, session, false);
            }
            foreach (var route in routes)
            {
                ReadOnce(route);
                Check(engine.LaneIsConsuming(route), $"{configuration.Describe()}: premise — the {route} route being read counts as taking audio");
                engine.RewindLaneReadClockForTest(route, LaneActivity.DefaultStallSeconds + 1);
                Check(!engine.LaneIsConsuming(route),
                    $"{configuration.Describe()}: when the {route} route stops being read it must say so — the tuner forgets a departed "
                    + "output's floor only when it hears that");
                ReadOnce(route);
                Check(engine.LaneIsConsuming(route),
                    $"{configuration.Describe()}: ...and say it is taking audio again the moment it is read again");
            }
            proven.Add($"{configuration.Describe()} ({string.Join(", ", routes)})");
        }

        var root = FindSourceRoot();
        if (root is null) return Skip("the source tree is not reachable from here (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var mainForm = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "MainForm.cs"));
        Check(System.Text.RegularExpressions.Regex.IsMatch(mainForm,
                @"foreach \(var route in new\[\] \{ RenderRoute\.Mixed, RenderRoute\.WasapiLane, RenderRoute\.AsioLane \}\)\s*NoteLaneAudibility\("),
            "the per-second tick must ask about all three routes — the Mixed route is what the tuner tunes with no ASIO driver chosen");
        return $"stopping and coming back is noticed on every tuned route: {string.Join("; ", proven)}; and the tick asks about all three";
    }

    /// <summary>
    /// THE TOTAL LATENCY BOX SAYS SOMETHING TRUE WITH LOGGING AND AUTO-TUNE BOTH OFF.
    ///
    /// <para>2026-09-13 review (MainForm #1). The box was only ever written from inside the diagnostics block, which runs
    /// only while logging or an auto-tune is on. With both off it kept the text it was built with, "jitter buffer 0 ms, not
    /// receiving", for the whole session — a false figure read out to a screen reader. The measured parts need that
    /// instrumentation, so with it off the box shows the jitter buffer as set and says where the rest comes from.</para>
    /// </summary>
    private static string? AuditTotalLatencyBoxIsTrueWithLoggingOff()
    {
        var restoreGate = DiagnosticsGate.Enabled;
        var proven = new List<string>();
        try
        {
            foreach (var configuration in AudioConfigurations.All)
            {
                MainForm form;
                try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
                catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }

                using (form)
                {
                    var name = configuration.Describe();
                    var receiver = form.ReceiverForTest;
                    SetAudioConfigurationForTest(form, configuration);
                    receiver.SetIndependentLaneLatency(configuration != AudioConfiguration.WasapiOnly);

                    form.LogForTest.Enabled = false;
                    form.SetContinuousTuneForTest(false);
                    const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    var settingsField = Require(typeof(MainForm).GetField("settings", Private), "MainForm.settings not found");
                    var settings = Require(settingsField.GetValue(form), "MainForm.settings is empty");
                    Require(settings.GetType().GetMethod("SaveContinuousAutoTuneAsioEnabled"), "SaveContinuousAutoTuneAsioEnabled not found")
                        .Invoke(settings, [false]);
                    Require(typeof(MainForm).GetMethod("UpdateDiagnosticsGate", Private), "MainForm.UpdateDiagnosticsGate not found").Invoke(form, null);
                    Check(!DiagnosticsGate.Enabled, $"{name}: premise — with logging and both auto-tunes off the diagnostics switch must be off");

                    var box = Require(form.ControlByFieldNameForTest("measuredLatencyReadout") as TextBox, "the Total latency box was not found");
                    form.SnapshotTickForTest();

                    var text = box.Text;
                    Check(!text.Contains("jitter buffer 0 ms", StringComparison.Ordinal),
                        $"{name}: with logging and auto-tune off the Total latency box must not keep the text it was built with (it reads "
                        + $"\"{text}\") — that says a 0 ms jitter buffer, which is false");
                    var route = configuration == AudioConfiguration.AsioOnly ? RenderRoute.AsioLane : RenderRoute.WasapiLane;
                    var setMs = receiver.TargetLatencyMsFor(route);
                    Check(text.Contains($"jitter buffer {setMs} ms", StringComparison.Ordinal),
                        $"{name}: the box must show the jitter buffer as it is set ({setMs} ms); it reads \"{text}\"");
                    Check(text.Contains("auto-tune or logging", StringComparison.Ordinal),
                        $"{name}: ...and say the measured figures need auto-tune or logging on, rather than leave them out silently; it reads \"{text}\"");
                    proven.Add(name);
                }
            }
        }
        finally { DiagnosticsGate.Enabled = restoreGate; }
        return $"in {string.Join(", ", proven)}, with logging and auto-tune off the Total latency box shows the jitter buffer as set and says what it cannot measure";
    }

    /// <summary>
    /// "SEND MY AUDIO" WITH NOTHING TICKED DOES NOT REDO THE PEER SET-UP EVERY SECOND, AND A PLUGIN SENDING ON ITS OWN IS
    /// KEPT RUNNING.
    ///
    /// <para>2026-09-13 review (MainForm #5 and #14). With "Send my audio" on and no capture source ticked, the sender never
    /// counts as running, so the once-a-second check re-ran the whole audio set-up every second — and that set-up threw away
    /// its record of which peers were armed, re-armed them, and wrote "audio receivers updated" to the log every second. The
    /// armed set is now re-pushed only when the chosen peers actually change.</para>
    ///
    /// <para>The same check asked only whether "Send my audio" was ticked, while the set-up itself also counts a DAW plugin
    /// that is sending — so if the sender stopped while only a plugin was sending, nothing started it again. Both now ask
    /// the same question.</para>
    ///
    /// <para>The rule is checked directly. That the two places use it is checked in the source, because driving them needs a
    /// live connection, which a gate run must not open.</para>
    /// </summary>
    private static string? AuditAudioSetupIsNotRedoneEverySecond()
    {
        var a = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("192.168.1.20"), 47830);
        var b = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("192.168.1.21"), 47830);
        Check(!MainForm.SendEndpointsChanged([a, b], [a, b]), "the same peers are not a change");
        Check(!MainForm.SendEndpointsChanged([a, b], [b, a]), "...nor the same peers in a different order");
        Check(MainForm.SendEndpointsChanged([a], [a, b]), "a peer added is a change");
        Check(MainForm.SendEndpointsChanged([a, b], [a]), "a peer removed is a change");
        Check(MainForm.SendEndpointsChanged([], [a]), "the first peer chosen is a change");

        var root = FindSourceRoot();
        if (root is null) return Skip("the source tree is not reachable from here (set REMSOUND_SOURCE_ROOT, as run-tests.ps1 does)");
        var text = File.ReadAllText(Path.Combine(root, "src", "RemSound.App", "MainForm.cs"));
        var apply = SourceMethodBody(text, "private void ApplyAudioRuntime()");
        var ensure = SourceMethodBody(text, "private void EnsureRequestedAudioRunning()");
        Check(apply.Length > 0 && ensure.Length > 0, "ApplyAudioRuntime and EnsureRequestedAudioRunning must still exist to be checked");
        var clears = apply.Split('\n').Where(l => l.Contains("activeAudioReceiverSignature = null", StringComparison.Ordinal)).ToList();
        Check(clears.Count > 0 && clears.All(l => l.Contains("SendEndpointsChanged(", StringComparison.Ordinal)),
            "the audio set-up may forget which peers are armed ONLY when the chosen peers changed — forgetting it every time is what "
            + "re-armed them and logged it once a second with \"Send my audio\" on and nothing ticked");
        Check(ensure.Contains("WantsToSend", StringComparison.Ordinal) && apply.Contains("WantsToSend", StringComparison.Ordinal),
            "the once-a-second check and the set-up must ask the same question about sending — a DAW plugin sending counts");
        return "the armed peers are re-pushed only when the chosen peers change, and the once-a-second check counts a sending plugin";
    }

    /// <summary>The body of the method whose declaration starts with <paramref name="signature"/>, by brace matching, or "".</summary>
    private static string SourceMethodBody(string text, string signature)
    {
        var at = text.IndexOf(signature, StringComparison.Ordinal);
        if (at < 0) return "";
        var open = text.IndexOf('{', at);
        if (open < 0) return "";
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return text[open..(i + 1)];
        }
        return "";
    }
}
