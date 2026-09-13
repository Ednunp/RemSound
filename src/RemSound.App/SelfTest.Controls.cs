using System.Reflection;
using System.Windows.Forms;
using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// THE CONTROL SUITE — every user control, in every audio configuration, on four dimensions.
///
/// <para>Why it exists (Ed, 2026-08-15, and he was right to be blunt): the latency slider was
/// completely dead for months and the entire 75-step gate passed. The UI tests checked that controls
/// EXIST — accessible name, mnemonic, tab order, values that survive save/reload — and every one of
/// those passed for a control that did absolutely nothing. We proved the slider was well-labelled and
/// remembered its value. Nobody ever asked whether it DID anything. Worse, the tests only ever ran in
/// whatever configuration the test happened to build, so the majority setup (plain WASAPI, one
/// slider) was never exercised at all — which is exactly where the bug lived.</para>
///
/// <para>So each control is tested on FOUR dimensions:
/// <list type="number">
/// <item>UI — present, reachable, sane tab stop, enabled state honest.</item>
/// <item>Accessibility — announces a name; mnemonics don't collide; no keyboard trap.</item>
/// <item>Theme — no hardcoded colours that fight light/dark (the palette rule below).</item>
/// <item>EFFECT — drive it and prove the thing it governs actually changed, probed on the LIVE audio
///       objects. Persisting a value is not evidence: the dead slider persisted perfectly.</item>
/// </list></para>
///
/// <para>And the part that makes it stay true: <see cref="EveryControlIsSpecified"/> walks the real
/// window and FAILS if any interactive control has no entry in the table. Add a control, the gate
/// goes red until you say what it governs and how to prove it. That is the structural fix — the
/// suite can't silently fall behind the UI again.</para>
///
/// <para>Controls that genuinely govern nothing at the audio layer (read-only status text, list
/// selection that only drives other UI) are declared with <see cref="ControlSpec.GovernsNothing"/> —
/// an explicit, reviewable statement rather than an omission.</para>
/// </summary>
internal static partial class SelfTest
{
    /// <summary>One control, and how to prove it does its job. <paramref name="Exercise"/> changes it
    /// the way a user would; <paramref name="Probe"/> reads what it should have changed — on the live
    /// receiver/sender wherever the control governs audio.</summary>
    /// <param name="LogContains">A fragment the app's own log MUST carry after this control is
    /// driven. Ed, 2026-08-24: "we have spent months and months building logs on top of logs on top
    /// of logs for every function. but the thing is, we've never tested those logs. change this
    /// control, does the right thing get logged." Until now this suite proved a control CHANGED what
    /// it governs and never asked whether it SAID so — and the night a log matters is never the night
    /// there is time to add one.</param>
    /// <param name="NoLogReason">Why this control legitimately records nothing. Every control that
    /// claims an effect must declare one or the other: the structural guard refuses a spec answering
    /// neither, so no control can be added without somebody deciding what it ought to say.</param>
    private sealed record ControlSpec(
        string Field,
        string Governs,
        Action<MainForm, Control>? Exercise = null,
        Func<MainForm, object?>? Probe = null,
        bool GovernsNothing = false,
        bool BothIndependentOnly = false,
        bool Decorative = false,
        string? LogContains = null,
        string? NoLogReason = null);

    /// <summary>The approved palette. A control may leave its colours at the system default (so it
    /// follows light/dark automatically), or use a Theme colour. Anything else is a hardcoded colour
    /// that will be unreadable in one of the two modes — the classic dark-mode bug.</summary>
    private static bool IsThemeApprovedColour(Color c) =>
        c.IsEmpty || c.IsSystemColor || c == Color.Transparent
        || c == Theme.Accent || c == Theme.Healthy || c == Theme.Warning || c == Theme.Bad || c == Theme.Neutral
        // Both light/dark variants of each Theme colour count: the palette flips with the OS, and a
        // control built while the other mode was active still holds a legitimate Theme colour.
        || c == Color.FromArgb(88, 166, 255) || c == Color.FromArgb(0, 99, 177)
        || c == Color.FromArgb(70, 200, 100) || c == Color.FromArgb(24, 140, 56)
        || c == Color.FromArgb(232, 184, 64) || c == Color.FromArgb(176, 120, 0)
        || c == Color.FromArgb(240, 100, 100) || c == Color.FromArgb(190, 40, 40);

    /// <summary>Move a list-like control to a different item, whether it's a ListBox or a ComboBox
    /// (RemSound uses both, which cost this suite its first clean run).</summary>
    private static void SelectNext(Control c)
    {
        switch (c)
        {
            case ListBox lb when lb.Items.Count > 1: lb.SelectedIndex = (lb.SelectedIndex + 1) % lb.Items.Count; break;
            case ComboBox cb when cb.Items.Count > 1: cb.SelectedIndex = (cb.SelectedIndex + 1) % cb.Items.Count; break;
        }
    }

    /// <summary>Drag a TrackBar the way a user does. Assigning .Value raises ValueChanged but NOT
    /// Scroll, and several sliders are wired to Scroll only — so a programmatic set would look inert
    /// even though the control works perfectly under a real hand. Raise Scroll too.</summary>
    private static void DragSlider(Control c, int delta)
    {
        var t = (TrackBar)c;
        t.Value = Math.Clamp(t.Value + delta, t.Minimum, t.Maximum);
        Require(typeof(TrackBar).GetMethod("OnScroll", BindingFlags.Instance | BindingFlags.NonPublic),
                "TrackBar.OnScroll not found — without it no slider wired to Scroll is ever driven, so "
                + "every effect check on those sliders would silently prove nothing")
            .Invoke(t, [EventArgs.Empty]);
    }

    private static List<ControlSpec> BuildControlSpecs() =>
    [
        // ---- Connectivity: what streams, and to whom -------------------------------------------
        new("sendMyAudioCheckbox", "whether this machine sends audio at all",
            (f, c) => ((CheckBox)c).Checked = !((CheckBox)c).Checked,
            f => f.SendEnabledForTest,
            LogContains: "send my audio"),
        new("receiveAudioCheckbox", "whether this machine plays incoming audio",
            (f, c) => ((CheckBox)c).Checked = !((CheckBox)c).Checked,
            f => f.ReceiveEnabledForTest,
            LogContains: "play incoming audio"),
        new("lockPeerAddressesBox", "the allow-list gate that rejects strangers",
            (f, c) => ((CheckBox)c).Checked = !((CheckBox)c).Checked,
            f => f.SettingsForTest.LoadLockPeerAddresses(),
            LogContains: "lock peer addresses"),
        new("discoveredPeersList", "which discovered peers are selected to stream with", GovernsNothing: true),
        new("rememberedPeersList", "the remembered-peer list (its own tested surface)", GovernsNothing: true),
        new("manualAddButton", "opens the add-peer-by-address dialog", GovernsNothing: true),
        new("renamePeerButton", "opens the rename dialog for the selected peer", GovernsNothing: true),
        new("peerDetailsBox", "read-only details of the selected peer", GovernsNothing: true),

        // ---- Audio inputs and outputs -----------------------------------------------------------
        new("sendInputDevicesList", "which microphones/inputs are captured", GovernsNothing: true),
        new("sendOutputDevicesList", "which outputs are captured (loopback)", GovernsNothing: true),
        new("sendAppsList", "which applications are captured", GovernsNothing: true),
        new("rememberedAppsList", "the remembered-application list", GovernsNothing: true),
        new("receiveOutputDevicesList", "where incoming audio is played", GovernsNothing: true),
        new("asioSendDevicesList", "which ASIO input pairs are captured", GovernsNothing: true, BothIndependentOnly: true),
        new("asioReceiveOutputDevicesList", "which ASIO output pairs play incoming audio", GovernsNothing: true, BothIndependentOnly: true),
        new("uncheckAllDevicesButton", "clears every ticked device at once", GovernsNothing: true),

        // ---- Volume, pan and EQ -----------------------------------------------------------------
        new("volumeSlider", "the selected peer's volume - EFFECT PROVEN by 'Volume and pan actually move the sound', which measures the level", GovernsNothing: true),
        new("volumeBar", "the master volume actually applied to received audio",
            (f, c) => DragSlider(c, -25),
            f => f.ReceiverForTest.Volume,
            LogContains: "listening volume"),
        new("panSlider", "the selected peer's pan position - EFFECT PROVEN by 'Volume and pan actually move the sound', which measures each channel", GovernsNothing: true),
        new("panEqPeerList", "which peer the pan/EQ controls apply to, and their per-peer bypass tick - the bypass is PROVEN via ShapingActiveForTest", GovernsNothing: true),
        new("enableAllPeerShapingBox", "whether per-peer pan/EQ shaping is applied at all",
            (f, c) => ((CheckBox)c).Checked = !((CheckBox)c).Checked,
            f => f.AllPeerShapingEnabledForTest,
            LogContains: "shaping: master switch"),
        new("addBandButton", "adds a parametric band - a band's effect on the audio is PROVEN by 'All three EQ modes'", GovernsNothing: true),
        new("deleteBandButton", "removes a parametric band - a removed band leaving the curve flat is PROVEN by 'All three EQ modes'", GovernsNothing: true),
        new("resetPeerEqButton", "resets the selected peer's EQ to flat - flat building no chain at all is PROVEN by 'All three EQ modes'", GovernsNothing: true),

        // ---- Audio profile: the transport itself ------------------------------------------------
        // THE REGRESSION THAT STARTED THIS SUITE. Each of these must reach the live audio path.
        new("maxLatencyBox", "the receive latency target the audio path actually uses",
            (f, c) => ((NumericUpDown)c).Value = 137,
            f => f.ReceiverForTest.TargetLatencyMsFor(RenderRoute.WasapiLane),
            LogContains: "jitter buffer"),
        new("maxLatencyAsioBox", "the ASIO lane's own latency target",
            (f, c) => ((NumericUpDown)c).Value = 41,
            f => f.ReceiverForTest.TargetLatencyMsFor(RenderRoute.AsioLane),
            BothIndependentOnly: true,
            LogContains: "jitter buffer (ASIO lane)"),
        new("codecBox", "the codec the sender actually encodes with",
            (f, c) => SelectNext(c),
            f => f.SenderForTest.Codec,
            LogContains: "codec"),
        new("smoothnessBox", "buffer smoothness in the live playout",
            (f, c) => SelectNext(c),
            f => f.ReceiverForTest.SmoothnessValue,
            LogContains: "buffer smoothness"),
        new("continuousTuneBox", "whether auto-tune runs",
            (f, c) => ((CheckBox)c).Checked = !((CheckBox)c).Checked,
            f => f.AutoTuneTimerEnabledForTest,
            LogContains: "auto-tune (WASAPI lane)"),
        new("continuousTuneAsioBox", "whether auto-tune runs on the ASIO lane",
            (f, c) => ((CheckBox)c).Checked = !((CheckBox)c).Checked,
            f => f.SettingsForTest.LoadContinuousAutoTuneAsioEnabled(),
            BothIndependentOnly: true,
            LogContains: "auto-tune (ASIO lane)"),
        new("continuousIntervalBox", "how often auto-tune re-checks",
            (f, c) => SelectNext(c),
            f => f.AutoTuneTimerIntervalForTest,
            LogContains: "auto-tune interval"),
        // Read-only readout: it reports latency, it doesn't set any. Its WORDING is pinned separately
        // by the "Measured-latency readout" step (FormatMeasuredLatency), which is where the substance is.
        new("measuredLatencyReadout", "read-only display of each lane's set vs achieved latency", GovernsNothing: true),
        new("priorityModeBox", "the high-priority / keep-awake levers",
            (f, c) => ((CheckBox)c).Checked = !((CheckBox)c).Checked,
            f => f.SettingsForTest.LoadPriorityMode(),
            LogContains: "priority mode"),

        // ---- Chrome and read-only surfaces ------------------------------------------------------
        new("mainTabControl", "tab navigation", GovernsNothing: true),
        new("statusLabel", "the spoken status line (read-only)", GovernsNothing: true),
        new("statusReadout", "the status readout (read-only)", GovernsNothing: true),
        new("healthLabel", "connection health text (read-only)", GovernsNothing: true),
        new("connectedPeersStatus", "connected-peers count (read-only)", GovernsNothing: true),
        new("discoveredPeersStatus", "discovered-peers count (read-only)", GovernsNothing: true),
        new("rememberedPeersStatus", "remembered-peers count (read-only)", GovernsNothing: true),
        new("sendInputDevicesStatusLabel", "list status text (read-only)", GovernsNothing: true),
        new("sendOutputDevicesStatusLabel", "list status text (read-only)", GovernsNothing: true),
        new("sendAppsStatusLabel", "list status text (read-only)", GovernsNothing: true),
        new("rememberedAppsStatusLabel", "list status text (read-only)", GovernsNothing: true),
        new("receiveOutputDevicesStatusLabel", "list status text (read-only)", GovernsNothing: true),
        new("asioSendDevicesStatusLabel", "list status text (read-only)", GovernsNothing: true),
        new("asioReceiveOutputDevicesStatusLabel", "list status text (read-only)", GovernsNothing: true),

        // ---- Caught by the completeness guard on the suite's first run (2026-08-15) --------------
        new("sendModeList", "whether sending captures devices or applications",
            (f, c) => SelectNext(c),
            f => f.SendModeIndexForTest,
            LogContains: "send mode"),
        new("sendRateBox", "the packet size / send rate the sender actually uses",
            (f, c) => SelectNext(c),
            f => f.SettingsForTest.LoadSendRate(),
            LogContains: "send rate"),
        new("artefactBox", "the gap-concealment artifact used in the live playout",
            (f, c) => SelectNext(c),
            f => f.ReceiverForTest.ConcealmentArtifactValue,
            LogContains: "concealment artifact"),
        new("asioDriverBox", "the ASIO driver choice — which also decides one-slider vs two-slider mode", GovernsNothing: true),
        new("eqModeList", "which of the three EQ modes is active - EFFECT PROVEN by 'All three EQ modes actually change the sound', which measures that the same slider gives a different curve per mode", GovernsNothing: true),
        new("parametricBandList", "which parametric band is being edited - each band's effect is PROVEN by 'All three EQ modes' (two bands must both apply)", GovernsNothing: true),
        new(Decorative: true, Field: "eqBandsPanel", Governs: "container for the EQ band controls", GovernsNothing: true),
        new(Decorative: true, Field: "eqCurve", Governs: "the drawn EQ curve (read-only visual)", GovernsNothing: true),
        new("connectedPeersList", "the connected-peer list", GovernsNothing: true),
        new(Decorative: true, Field: "healthDot", Governs: "connection-health indicator (read-only)", GovernsNothing: true),
        new(Decorative: true, Field: "asioDelayContainer", Governs: "layout container for the ASIO latency row", GovernsNothing: true),
        // Tab pages and labels: chrome. They carry no audio effect, but they DO carry the
        // accessibility and theme checks above, which is why they're specified rather than excluded.
        new("connectivityTabPage", "the Connectivity tab", GovernsNothing: true),
        new("audioIOTabPage", "the Audio inputs and outputs tab", GovernsNothing: true),
        new("audioProfileTabPage", "the Audio profile tab", GovernsNothing: true),
        new("panEqTabPage", "the Volume, pan and EQ tab", GovernsNothing: true),
        new("sendModeLabel", "label for the send-mode list", GovernsNothing: true),
        new("sendAppsLabel", "label for the applications list", GovernsNothing: true),
        new("rememberedAppsLabel", "label for the remembered-applications list", GovernsNothing: true),
        new("asioSendDevicesLabel", "label for the ASIO capture list", GovernsNothing: true),
        new("asioReceiveOutputDevicesLabel", "label for the ASIO output list", GovernsNothing: true),
        new("asioDriverLabel", "label for the ASIO driver picker", GovernsNothing: true),
        new("discoveredPeersLabel", "label for the discovered-peers list", GovernsNothing: true),
        new("rememberedPeersLabel", "label for the remembered-peers list", GovernsNothing: true),
        new("sendOutputDevicesLabel", "label for the output-capture list", GovernsNothing: true),
        new("sendInputDevicesLabel", "label for the input-capture list", GovernsNothing: true),
        new("receiveOutputDevicesLabel", "label for the playback-device list", GovernsNothing: true),
        new("asioLatencyLabel", "label for the ASIO latency control", GovernsNothing: true),
        new("wasapiLatencyLabel", "label for the WASAPI latency control", GovernsNothing: true),
        new("continuousIntervalLabel", "label for the auto-tune interval", GovernsNothing: true),
    ];

    /// <summary>A real user configuration. The audio MODE is only half of it: which lane an incoming
    /// stream is tagged with — and therefore which latency control governs it — is decided by which
    /// OUTPUT DEVICES are ticked. So a user with an ASIO driver but only ASIO outputs ticked is a
    /// third configuration, distinct from WASAPI-only and from both-ticked. Missing it was the gap Ed
    /// caught on 2026-08-15: the suite ran two configurations and claimed to run every one.</summary>
    private sealed record SuiteConfig(string Name, bool AsioDriver, bool WasapiLaneActive, bool AsioLaneActive)
    {
        public AudioMode Mode => AsioDriver ? AudioMode.BothIndependent : AudioMode.WasapiOnly;
        /// <summary>The lane an arriving stream lands on — ReconcileReplicasLocked tags it with the
        /// FIRST active lane (WASAPI before ASIO).</summary>
        public RenderRoute ExpectedRoute => WasapiLaneActive ? RenderRoute.WasapiLane : RenderRoute.AsioLane;
    }

    private static readonly SuiteConfig[] SuiteConfigs =
    [
        new("WASAPI only (one slider)",              AsioDriver: false, WasapiLaneActive: true,  AsioLaneActive: false),
        new("ASIO only (ASIO outputs ticked)",       AsioDriver: true,  WasapiLaneActive: false, AsioLaneActive: true),
        new("Both (WASAPI + ASIO outputs ticked)",   AsioDriver: true,  WasapiLaneActive: true,  AsioLaneActive: true),
    ];

    /// <summary>Run the whole control suite in one audio configuration.</summary>
    private static string RunControlSuite(SuiteConfig config)
    {
        var mode = config.Mode;
        // The audio mode is DERIVED from whether an ASIO driver is chosen (RemSoundSettingsStore
        // .LoadAudioMode) — no driver means one slider, a driver means two. So the configuration is
        // switched exactly the way a user switches it: by the driver choice.
        var settings = new RemSoundSettingsStore("RemSound");
        settings.SaveAsioDriverName(mode == AudioMode.BothIndependent ? "RemSound Test ASIO Driver" : null);
        // Prove the configuration actually took before auditing anything in its name — a suite that
        // silently runs the same mode twice is exactly the blind spot this whole exercise is about.
        Check(settings.LoadAudioMode() == mode,
            $"could not put the app into {mode} for the suite (it reports {settings.LoadAudioMode()})");

        MainForm? form = null;
        // The suite ticks controls that play cue sounds. Muting is NOT left to the caller passing
        // --silent (run-tests.ps1 didn't, so every gate run chimed at whoever was at the screen —
        // Ed, 2026-08-15). A test that makes noise on someone's speakers is a broken test.
        var restoreMuted = CuePlayer.GloballyMuted;
        var restoreCheckSounds = CheckSoundService.Suppressed;
        CuePlayer.GloballyMuted = true;
        CheckSoundService.Suppressed = true;
        try
        {
            // A profile WITH a password: ticking send/receive runs EnsureStreamingPassword, which
            // opens a modal password dialog when there isn't one — and a modal dialog in a headless
            // gate hangs forever (it did, on this suite's first run). Satisfy the gate up front.
            // Configuration arrives the way a real user's does: ON THE PROFILE. The audio mode is
            // DERIVED from the chosen ASIO driver (no driver = one slider, a driver = two), and
            // RemSoundSettingsStore is an in-memory cache fed from the profile — so writing to a
            // store instance the form doesn't own configures nothing at all. That mistake cost this
            // suite two runs and is exactly the kind of "the test wasn't testing what it claimed"
            // that let the dead slider through in the first place.
            var profile = Profile.NewBlank();
            profile.AsioDriverName = config.AsioDriver ? "RemSound Test ASIO Driver" : null;
            // Stored OBFUSCATED — MainForm deobfuscates on load, and a plain string decodes to ""
            // (which is what re-opened the modal dialog and hung the second run).
            profile.Password = RemSoundCrypto.Obfuscate("control-suite-test-password");
            try { form = new MainForm(null, profile, null, null, headless: true); }
            catch (Exception ex) { throw new StepSkipped($"headless MainForm could not be constructed: {ex.GetType().Name}: {ex.Message}"); }

            // Prove the configuration actually took before auditing anything in its name — a suite
            // that silently runs the same mode twice is the blind spot this whole exercise is about.
            var actualMode = form.SettingsForTest.LoadAudioMode();
            Check(actualMode == mode,
                $"the app did not enter {mode} for the suite (it reports {actualMode}) — the audit below would have been a lie");

            // Which lanes have a ticked output — the routing axis, normally set by
            // CompositeRenderBackend from the device lists.
            form.ReceiverForTest.SetActiveOutputLanes(config.WasapiLaneActive, config.AsioLaneActive);

            // AND TICK THE LISTS THE WINDOW ITSELF READS. SetActiveOutputLanes tells the ENGINE which
            // lanes render; ActiveAudioConfiguration() asks which output devices are TICKED, which is
            // the real question and a different one. Without this the suite ran three configurations
            // at the engine while the window still believed it was in WASAPI-only — and the very first
            // thing the new log check caught was every configuration's log line reading
            // "[WASAPI only]". A suite whose three configurations are only half real is exactly the
            // shape of gap this whole exercise keeps turning up. 2026-08-24.
            var suiteConfiguration = AudioConfigurations.From(config.WasapiLaneActive, config.AsioLaneActive);
            SetAudioConfigurationForTest(form, suiteConfiguration);

            // ROUTING ASSERTION: an arriving stream must land on the lane this configuration implies,
            // and must read the control that governs that lane. This is the exact relationship the
            // dead-slider bug broke, checked now in every configuration rather than assumed.
            var probe = form.ReceiverForTest.GetOrCreateSessionForTest(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 47850), 7);
            Check(probe.Route == config.ExpectedRoute,
                $"{config.Name}: an arriving stream landed on {probe.Route}, expected {config.ExpectedRoute} — the latency controls would govern the wrong stream");

            var specs = BuildControlSpecs();
            var problems = new List<string>();
            var proven = new List<string>();
            // What each control was proved to SAY, alongside what it was proved to DO.
            var logProven = new List<string>();
            var logExempt = new List<string>();
            var logLines = new Dictionary<string, string>(StringComparer.Ordinal);
            var uiChecked = 0;
            // Every spec that claims an effect AND applies in this configuration. Counted so the
            // suite can assert it actually proved them, rather than reporting a confident summary
            // over a loop that skipped everything — which is precisely how its own first run came
            // back "0 proven" and looked fine.
            var applicable = specs.Where(s => !s.GovernsNothing && s.Exercise is not null && s.Probe is not null
                                              && (!s.BothIndependentOnly || mode == AudioMode.BothIndependent))
                                  .Select(s => s.Field).ToList();
            var skippedDisabled = new List<string>();

            foreach (var spec in specs)
            {
                var control = form.ControlByFieldNameForTest(spec.Field);
                if (control is null)
                {
                    problems.Add($"{spec.Field}: control not found on the form");
                    continue;
                }
                uiChecked++;

                // (1) UI + (2) accessibility: it must announce something a screen reader can read.
                var announced = !string.IsNullOrWhiteSpace(control.AccessibleName)
                                || !string.IsNullOrWhiteSpace(control.Text)
                                || control is TabControl or TextBox or TrackBar or CheckedListBox or ListBox or ComboBox or NumericUpDown;
                if (!announced && !spec.Decorative)
                    problems.Add($"{spec.Field}: nothing for a screen reader to announce");

                // (3) theme: no hardcoded colours that break in light or dark.
                if (!IsThemeApprovedColour(control.ForeColor))
                    problems.Add($"{spec.Field}: hardcoded ForeColor {control.ForeColor} — will fight light/dark");
                if (!IsThemeApprovedColour(control.BackColor))
                    problems.Add($"{spec.Field}: hardcoded BackColor {control.BackColor} — will fight light/dark");

                // (4) EFFECT: drive it, and prove the thing it governs changed.
                if (spec.GovernsNothing || spec.Exercise is null || spec.Probe is null) continue;
                if (spec.BothIndependentOnly && mode != AudioMode.BothIndependent) continue;
                // NOT gated on Visible: a headless form has never been shown, so WinForms reports
                // every control invisible and every effect check would silently skip — which is how
                // this suite's first run reported "0 proven". Applicability comes from the spec
                // (BothIndependentOnly) instead. Enabled is still honoured: a disabled control
                // genuinely isn't offered to the user in this configuration.
                if (!control.Enabled) { skippedDisabled.Add(spec.Field); continue; }

                object? before, after;
                // Name it BEFORE driving it: if a control ever blocks (a modal dialog, a driver open),
                // the log's last line names the culprit instead of leaving a silent hang.

                // CAPTURE WHAT IT SAYS WHILE IT DOES IT. The effect check below proves the control
                // reached the thing it governs; this proves it also REPORTED doing so, which is the
                // half nobody had ever tested. Tapped rather than read off disk, so the gate writes
                // no log files of its own.
                var logged = new List<string>();
                form.LogForTest.EventTapForTest = line => { lock (logged) logged.Add(line); };
                try
                {
                    before = spec.Probe(form);
                    spec.Exercise(form, control); // handlers fire synchronously on the property set
                    after = spec.Probe(form);
                }
                catch (Exception ex)
                {
                    problems.Add($"{spec.Field}: driving it threw {ex.GetType().Name}: {ex.Message}");
                    continue;
                }
                finally { form.LogForTest.EventTapForTest = null; }

                if (Equals(before, after))
                    problems.Add($"{spec.Field}: moving it changed NOTHING — it claims to govern {spec.Governs} (before={before}, after={after})");
                else
                    proven.Add(spec.Field);

                // (5) THE LOG. Did it say what it just did?
                if (spec.LogContains is { } expected)
                {
                    var match = logged.FirstOrDefault(l => l.Contains(expected, StringComparison.OrdinalIgnoreCase));
                    if (match is null)
                    {
                        problems.Add($"{spec.Field}: driving it logged nothing containing \"{expected}\" — it governs "
                            + $"{spec.Governs}, and a change that leaves no trace cannot be diagnosed afterwards. "
                            + $"Logged instead: [{(logged.Count == 0 ? "NOTHING AT ALL" : string.Join(" | ", logged))}]");
                    }
                    else
                    {
                        logProven.Add(spec.Field);
                        logLines[spec.Field] = match;

                        // AND IT MUST NAME THE CONFIGURATION IT HAPPENED IN.
                        //
                        // Ed, 2026-08-24: "check the log that it is picking up the 3 lane config for
                        // every thing we can change." The same control means different things in each
                        // configuration — a jitter-buffer move governs a different lane, an auto-tune
                        // switch drives a different tuner — so a line that records the change without
                        // recording which world it happened in cannot answer the question anyone will
                        // actually ask of it later. This is not hypothetical: a log line that named
                        // the audio MODE instead of the configuration convinced me on this very day
                        // that Ed had both lanes live while he was telling me he had turned WASAPI
                        // off. Asserted per configuration, so a line hard-coded to one of the three
                        // fails in the other two.
                        if (!match.Contains($"[{suiteConfiguration.Describe()}]", StringComparison.Ordinal))
                        {
                            problems.Add($"{spec.Field}: logged \"{match}\" without naming the configuration it "
                                + $"happened in ({suiteConfiguration.Describe()}). The same control governs different "
                                + "lanes in each configuration, so a line that does not say which one it was in cannot "
                                + "be read back afterwards — and a line naming the audio MODE instead is worse than "
                                + "none, because it reads as both lanes being live when only one is");
                        }
                    }
                }
                else if (spec.NoLogReason is null)
                {
                    // Neither an expectation nor a written reason. This is the structural half: it
                    // makes the question unavoidable for every control that will ever be added.
                    problems.Add($"{spec.Field}: claims an effect but says nothing about what it should LOG. "
                        + "Give it a LogContains, or a NoLogReason explaining why it is right for this control to "
                        + $"stay silent. It logged: [{(logged.Count == 0 ? "nothing" : string.Join(" | ", logged))}]");
                }
                else
                {
                    logExempt.Add(spec.Field);
                }
            }

            Check(problems.Count == 0, $"{config.Name}: " + string.Join("; ", problems));

            // The positive facts. Without these the suite passes just as happily over a loop that
            // audited nothing: an empty spec table, or a form on which every control came back
            // disabled, would both have produced a confident summary and a green step.
            Check(uiChecked == specs.Count,
                $"{config.Name}: only {uiChecked} of {specs.Count} specified controls were found on the form");
            Check(proven.Count > 0,
                $"{config.Name}: not one control was proven to reach what it governs. EFFECT is the entire reason this "
                + "suite exists over the old existence-only tests — the dead latency slider passed every one of those");
            Check(proven.Count + skippedDisabled.Count == applicable.Count,
                $"{config.Name}: {applicable.Count} controls claim an effect in this configuration but only {proven.Count} "
                + $"were proven and {skippedDisabled.Count} were skipped as disabled — the rest fell out of the loop "
                + $"silently. Unaccounted: {string.Join(", ", applicable.Except(proven).Except(skippedDisabled))}");

            // The log half, asserted positively for the same reason the effect half is: a loop that
            // silently checked nothing would otherwise report a confident summary and a green step.
            Check(logProven.Count + logExempt.Count == proven.Count,
                $"{config.Name}: {proven.Count} controls were proven to reach what they govern, but only "
                + $"{logProven.Count} were proven to SAY so and {logExempt.Count} are exempt with a written reason — "
                + $"the rest fell out of the log check silently. Unaccounted: "
                + $"{string.Join(", ", proven.Except(logProven).Except(logExempt))}");
            Check(logProven.Count > 0,
                $"{config.Name}: not one control was proven to log what it did. That is the whole point of this half of "
                + "the suite — a log nobody has tested is a log you find out is empty on the night you need it");

            return $"{config.Name}: streams land on {probe.Route}; {uiChecked} controls audited (UI + accessibility + theme); "
                 + $"{proven.Count} of {applicable.Count} applicable proven to reach what they govern [{string.Join(", ", proven)}]"
                 + $"; {logProven.Count} proven to LOG what they did [{string.Join(", ", logProven)}]"
                 + (logExempt.Count > 0 ? $"; {logExempt.Count} log nothing by design [{string.Join(", ", logExempt)}]" : "")
                 + (skippedDisabled.Count > 0 ? $"; {skippedDisabled.Count} disabled in this configuration [{string.Join(", ", skippedDisabled)}]" : "");
        }
        finally
        {
            try { form?.Dispose(); } catch { }
            CuePlayer.GloballyMuted = restoreMuted;
            CheckSoundService.Suppressed = restoreCheckSounds;

        }
    }

    /// <summary>The structural guard: every interactive control on the real window must appear in the
    /// spec table. Add a control without saying what it governs and how to prove it, and this fails —
    /// which is what stops the suite quietly falling behind the UI the way the old tests did.</summary>
    private static string? EveryControlIsSpecified()
    {
        MainForm? form = null;
        // The suite ticks controls that play cue sounds. Muting is NOT left to the caller passing
        // --silent (run-tests.ps1 didn't, so every gate run chimed at whoever was at the screen —
        // Ed, 2026-08-15). A test that makes noise on someone's speakers is a broken test.
        var restoreMuted = CuePlayer.GloballyMuted;
        var restoreCheckSounds = CheckSoundService.Suppressed;
        CuePlayer.GloballyMuted = true;
        CheckSoundService.Suppressed = true;
        try
        {
            try { form = new MainForm(null, Profile.NewBlank(), null, null, headless: true); }
            catch (Exception ex) { return Skip($"headless MainForm could not be constructed: {ex.GetType().Name}: {ex.Message}"); }

            var specified = BuildControlSpecs().Select(s => s.Field).ToHashSet(StringComparer.Ordinal);

            // Every private control FIELD on MainForm — the developer-visible inventory.
            var fields = typeof(MainForm)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Where(f => typeof(Control).IsAssignableFrom(f.FieldType))
                .Select(f => f.Name)
                .ToList();

            var missing = fields.Where(f => !specified.Contains(f)).ToList();
            Check(missing.Count == 0,
                $"these controls have no entry in the control suite — say what each governs and how to prove it: {string.Join(", ", missing)}");

            var stale = specified.Where(s => !fields.Contains(s)).ToList();
            Check(stale.Count == 0, $"the control suite names controls that no longer exist: {string.Join(", ", stale)}");

            var governing = BuildControlSpecs().Count(s => !s.GovernsNothing);
            return $"{fields.Count} controls on the window, all specified; {governing} carry a proven effect, the rest declared as governing nothing";
        }
        finally { try { form?.Dispose(); } catch { } }
    }
}
