using System.Net;
using RemSound.Core;

namespace RemSound.App;

internal static partial class SelfTest
{
    /// <summary>LOWEST-DELAY AUTO-TUNE (Ed, 2026-09-29, after the live test with Andre: the latency lab's two auto-tune switches
    /// - "one is the dropouts one and the other is basically to be less cautious on asio. so I think we just make that wasapi
    /// as well and put it as one switch next to auto tune check interval"). The tick box sits beside the interval, off in a new
    /// profile and kept with it, usable only while an auto-tune is on. Ticked, in every configuration, every lane with audio
    /// and its auto-tune on is tuned by counting its audible gaps - down while quiet, up at once when they come - with the
    /// usual tune standing aside there, and each stream's own buffer takes the lean margins; a lane with no audio is left
    /// alone; unticked, the usual tune has every lane again, from a fresh baseline.</summary>
    private static string? LowestDelayTuneOnEveryLane()
    {
        // Kept with the profile, and in its file.
        var store = new RemSoundSettingsStore("RemSound");
        var backup = new Profile();
        store.CopyTo(backup);
        try
        {
            var p = Profile.NewBlank();
            Check(!p.AutoTuneLowestDelay, "a new profile's lowest-delay auto-tune must be off");
            p.AutoTuneLowestDelay = true;
            store.ApplyProfile(p);
            var back = new Profile();
            store.CopyTo(back);
            Check(back.AutoTuneLowestDelay, "it must round-trip the profile");
            var reread = System.Text.Json.JsonSerializer.Deserialize<Profile>(System.Text.Json.JsonSerializer.Serialize(p))!;
            Check(reread.AutoTuneLowestDelay, "and survive the profile file");
        }
        finally { store.ApplyProfile(backup); }

        var said = new List<string>();
        foreach (var configuration in AudioConfigurations.All)
        {
            var logged = new List<string>();
            MainForm? form = null;
            try
            {
                var profile = Profile.NewBlank();
                profile.MaxLatencyMs = 100;
                profile.MaxLatencyMsAsio = 100;
                try { form = new MainForm(null, profile, null, null, headless: true); }
                catch (Exception ex) { return MainWindowCouldNotBeBuilt(ex); }
                var main = form;
                main.LogForTest.EventTapForTest = line => { lock (logged) logged.Add(line); };
                SetAudioConfigurationForTest(main, configuration);
                var name = configuration.Describe();
                var box = Require(FieldOf<CheckBox>(main, "lowestDelayTuneBox"), "MainForm.lowestDelayTuneBox not found");
                var interval = Require(FieldOf<ComboBox>(main, "continuousIntervalBox"), "MainForm.continuousIntervalBox not found");
                var wasapiTune = Require(FieldOf<CheckBox>(main, "continuousTuneBox"), "MainForm.continuousTuneBox not found");
                var asioTune = Require(FieldOf<CheckBox>(main, "continuousTuneAsioBox"), "MainForm.continuousTuneAsioBox not found");
                var wasapiSlider = Require(FieldOf<NumericUpDown>(main, "maxLatencyBox"), "MainForm.maxLatencyBox not found");
                var asioSlider = Require(FieldOf<NumericUpDown>(main, "maxLatencyAsioBox"), "MainForm.maxLatencyAsioBox not found");

                var table = box.Parent as TableLayoutPanel;
                Check(table is not null && interval.Parent?.Parent == table && table.GetRow(box) == table.GetRow(interval.Parent!) + 1
                      && table.GetColumn(box) == table.GetColumn(interval.Parent!),
                    $"{name}: the tick box must sit straight under the auto-tune interval's row");
                Check(table!.Controls.GetChildIndex(box) == table.Controls.GetChildIndex(interval.Parent!) + 1,
                    $"{name}: and come next after it in Tab order");
                Check(!box.Enabled, $"{name}: with no auto-tune on the tick box must be greyed, as the interval is");

                // The lanes this configuration carries audio on, and their sliders.
                var lanes = configuration switch
                {
                    AudioConfiguration.WasapiOnly => new[] { (Route: RenderRoute.Mixed, Slider: wasapiSlider) },
                    AudioConfiguration.AsioOnly => [(RenderRoute.AsioLane, asioSlider)],
                    _ => [(RenderRoute.WasapiLane, wasapiSlider), (RenderRoute.AsioLane, asioSlider)],
                };
                wasapiTune.Checked = true;
                if (configuration != AudioConfiguration.WasapiOnly) asioTune.Checked = true;
                Check(box.Enabled, $"{name}: with an auto-tune on the tick box must be usable");
                foreach (var (route, _) in lanes)
                {
                    Check(main.TuneOwnerForTest(route) == (true, false), $"{name}: before it is ticked, the usual tune must have the {route} lane");
                    Check(main.AutoTuneOnForTest(route), $"{name}: and per-stream buffers must follow the {route} lane's auto-tune, on");
                }

                var gaps = new Dictionary<RenderRoute, long>();
                main.DropoutGapsForTest = r => gaps.GetValueOrDefault(r);
                main.HasAudioForTest = r => lanes.Any(l => l.Route == r);
                box.Checked = true;
                Check(main.SettingsForTest.LoadAutoTuneLowestDelay() && main.UnsavedChangesForTest, $"{name}: ticking it must keep it in the profile, unsaved");
                Check(main.ReceiverForTest.LeanTuning, $"{name}: and give each stream's own buffer the lean margins at once");
                Check(Logged(logged, "lowest-delay auto-tune", "on", ActiveConfigurationWords(main)), $"{name}: and be logged with the configuration");
                foreach (var (route, _) in lanes)
                    Check(main.TuneOwnerForTest(route) == (false, true), $"{name}: ticked, the lowest-delay tune must have the {route} lane and the usual tune stand aside");

                // Quiet seconds: down, in big steps while it has not met a gap.
                var untouched = configuration == AudioConfiguration.AsioOnly ? wasapiSlider : null;
                for (var s = 0; s < 3; s++) main.TickLowestDelayTuneForTest();
                foreach (var (route, slider) in lanes)
                    Check(slider.Value < 100, $"{name}: quiet seconds must bring the {route} buffer down from 100 ms ({slider.Value} ms)");
                Check(untouched is null || untouched.Value == 100, $"{name}: a lane with no audio must be left alone ({untouched?.Value} ms)");
                Check(Logged(logged, "lowest-delay auto-tune", "100 ms ->", ActiveConfigurationWords(main)), $"{name}: each move must be logged with the configuration");

                // Gaps: three in one second, up at once.
                foreach (var (route, slider) in lanes)
                {
                    var before = (int)slider.Value;
                    gaps[route] = gaps.GetValueOrDefault(route) + DropoutTuner.RaiseWhenGapsInOneSecond;
                    main.TickLowestDelayTuneForTest();
                    Check((int)slider.Value == before + DropoutTuner.RaiseStepMs,
                        $"{name}: {DropoutTuner.RaiseWhenGapsInOneSecond} gaps in a second on the {route} lane must raise it {DropoutTuner.RaiseStepMs} ms at once ({before} to {slider.Value} ms)");
                }

                // Per-stream buffers go with each output's own auto-tune: off, the number set is what you get.
                asioTune.Checked = false;
                wasapiTune.Checked = false;
                foreach (var (route, _) in lanes)
                    Check(!main.AutoTuneOnForTest(route), $"{name}: with its auto-tune off, the {route} lane must have no per-stream buffers");
                wasapiTune.Checked = true;
                if (configuration != AudioConfiguration.WasapiOnly) asioTune.Checked = true;

                // Unticked: the usual tune has every lane again, from a fresh baseline.
                foreach (var (route, _) in lanes) main.ClearLaneBaselineForTest(route);   // so the untick has to set it again
                box.Checked = false;
                Check(!main.ReceiverForTest.LeanTuning, $"{name}: unticked, the usual margins must be back");
                foreach (var (route, _) in lanes)
                {
                    Check(main.TuneOwnerForTest(route) == (true, false), $"{name}: unticked, the usual tune must have the {route} lane again");
                    Check(main.LaneNeedsBaselineForTest(route), $"{name}: and start the {route} lane from a fresh baseline, not act on what it stood aside for");
                }
                said.Add($"{name}: {string.Join(", ", lanes.Select(l => $"{l.Route} {l.Slider.Value} ms"))}");
                main.LogForTest.EventTapForTest = null;
            }
            finally { try { form?.Dispose(); } catch { /* teardown */ } }
        }
        return "off in a new profile and kept with it; beside the interval, greyed until an auto-tune is on; ticked, every lane with "
             + "audio came down while quiet and rose 2 ms at 3 gaps, the usual tune aside and lean margins on, a lane with no audio "
             + "left alone; unticked, the usual tune back from a fresh baseline - " + string.Join("; ", said);
    }
}
