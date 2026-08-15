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
    private sealed record ControlSpec(
        string Field,
        string Governs,
        Action<MainForm, Control>? Exercise = null,
        Func<MainForm, object?>? Probe = null,
        bool GovernsNothing = false,
        bool BothIndependentOnly = false,
        bool Decorative = false);

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
        typeof(TrackBar).GetMethod("OnScroll", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.Invoke(t, [EventArgs.Empty]);
    }

    private static List<ControlSpec> BuildControlSpecs() =>
    [
        // ---- Connectivity: what streams, and to whom -------------------------------------------
        new("sendMyAudioCheckbox", "whether this machine sends audio at all",
            (f, c) => ((CheckBox)c).Checked = !((CheckBox)c).Checked,
            f => f.SendEnabledForTest),
        new("receiveAudioCheckbox", "whether this machine plays incoming audio",
            (f, c) => ((CheckBox)c).Checked = !((CheckBox)c).Checked,
            f => f.ReceiveEnabledForTest),
        new("lockPeerAddressesBox", "the allow-list gate that rejects strangers",
            (f, c) => ((CheckBox)c).Checked = !((CheckBox)c).Checked,
            f => f.SettingsForTest.LoadLockPeerAddresses()),
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
        new("volumeSlider", "the SELECTED PEER's volume (per-peer shaping has its own gate step)", GovernsNothing: true),
        new("volumeBar", "the master volume actually applied to received audio",
            (f, c) => DragSlider(c, -25),
            f => f.ReceiverForTest.Volume),
        new("panSlider", "the selected peer's pan position", GovernsNothing: true),
        new("panEqPeerList", "which peer the pan/EQ controls apply to", GovernsNothing: true),
        new("enableAllPeerShapingBox", "whether per-peer pan/EQ shaping is applied at all",
            (f, c) => ((CheckBox)c).Checked = !((CheckBox)c).Checked,
            f => f.AllPeerShapingEnabledForTest),
        new("addBandButton", "adds an EQ band to the selected peer", GovernsNothing: true),
        new("deleteBandButton", "removes the selected EQ band", GovernsNothing: true),
        new("resetPeerEqButton", "resets the selected peer's EQ", GovernsNothing: true),

        // ---- Audio profile: the transport itself ------------------------------------------------
        // THE REGRESSION THAT STARTED THIS SUITE. Each of these must reach the live audio path.
        new("maxLatencyBox", "the receive latency target the audio path actually uses",
            (f, c) => ((NumericUpDown)c).Value = 137,
            f => f.ReceiverForTest.TargetLatencyMsFor(RenderRoute.WasapiLane)),
        new("maxLatencyAsioBox", "the ASIO lane's own latency target",
            (f, c) => ((NumericUpDown)c).Value = 41,
            f => f.ReceiverForTest.TargetLatencyMsFor(RenderRoute.AsioLane),
            BothIndependentOnly: true),
        new("codecBox", "the codec the sender actually encodes with",
            (f, c) => SelectNext(c),
            f => f.SenderForTest.Codec),
        new("smoothnessBox", "buffer smoothness in the live playout",
            (f, c) => SelectNext(c),
            f => f.ReceiverForTest.SmoothnessValue),
        new("continuousTuneBox", "whether auto-tune runs",
            (f, c) => ((CheckBox)c).Checked = !((CheckBox)c).Checked,
            f => f.AutoTuneTimerEnabledForTest),
        new("continuousTuneAsioBox", "whether auto-tune runs on the ASIO lane",
            (f, c) => ((CheckBox)c).Checked = !((CheckBox)c).Checked,
            f => f.SettingsForTest.LoadContinuousAutoTuneAsioEnabled(),
            BothIndependentOnly: true),
        new("continuousIntervalBox", "how often auto-tune re-checks",
            (f, c) => SelectNext(c),
            f => f.AutoTuneTimerIntervalForTest),
        new("priorityModeBox", "the high-priority / keep-awake levers",
            (f, c) => ((CheckBox)c).Checked = !((CheckBox)c).Checked,
            f => f.SettingsForTest.LoadPriorityMode()),

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
            f => f.SendModeIndexForTest),
        new("sendRateBox", "the packet size / send rate the sender actually uses",
            (f, c) => SelectNext(c),
            f => f.SettingsForTest.LoadSendRate()),
        new("artefactBox", "the gap-concealment artifact used in the live playout",
            (f, c) => SelectNext(c),
            f => f.ReceiverForTest.ConcealmentArtifactValue),
        new("asioDriverBox", "the ASIO driver choice — which also decides one-slider vs two-slider mode", GovernsNothing: true),
        new("eqModeList", "simple vs parametric EQ", GovernsNothing: true),
        new("parametricBandList", "which EQ band is being edited", GovernsNothing: true),
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
        var restoreDriver = settings.LoadAsioDriverName();
        settings.SaveAsioDriverName(mode == AudioMode.BothIndependent ? "RemSound Test ASIO Driver" : null);
        // Prove the configuration actually took before auditing anything in its name — a suite that
        // silently runs the same mode twice is exactly the blind spot this whole exercise is about.
        if (settings.LoadAudioMode() != mode)
            throw new CheckFailed($"could not put the app into {mode} for the suite (it reports {settings.LoadAudioMode()})");

        MainForm? form = null;
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
            if (actualMode != mode)
                throw new CheckFailed($"the app did not enter {mode} for the suite (it reports {actualMode}) — the audit below would have been a lie");

            // Which lanes have a ticked output — the routing axis, normally set by
            // CompositeRenderBackend from the device lists.
            form.ReceiverForTest.SetActiveOutputLanes(config.WasapiLaneActive, config.AsioLaneActive);

            // ROUTING ASSERTION: an arriving stream must land on the lane this configuration implies,
            // and must read the control that governs that lane. This is the exact relationship the
            // dead-slider bug broke, checked now in every configuration rather than assumed.
            var probe = form.ReceiverForTest.GetOrCreateSessionForTest(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 47850), 7);
            if (probe.Route != config.ExpectedRoute)
                throw new CheckFailed($"{config.Name}: an arriving stream landed on {probe.Route}, expected {config.ExpectedRoute} — the latency controls would govern the wrong stream");

            var specs = BuildControlSpecs();
            var problems = new List<string>();
            var effectsProven = 0;
            var uiChecked = 0;

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
                if (!control.Enabled) continue;

                object? before, after;
                // Name it BEFORE driving it: if a control ever blocks (a modal dialog, a driver open),
                // the log's last line names the culprit instead of leaving a silent hang.

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
                if (Equals(before, after))
                    problems.Add($"{spec.Field}: moving it changed NOTHING — it claims to govern {spec.Governs} (before={before}, after={after})");
                else
                    effectsProven++;
            }

            if (problems.Count > 0)
                throw new CheckFailed($"{config.Name}: " + string.Join("; ", problems));
            return $"{config.Name}: streams land on {probe.Route}; {uiChecked} controls audited (UI + accessibility + theme), {effectsProven} proven to reach what they govern";
        }
        finally
        {
            try { form?.Dispose(); } catch { }

        }
    }

    /// <summary>The structural guard: every interactive control on the real window must appear in the
    /// spec table. Add a control without saying what it governs and how to prove it, and this fails —
    /// which is what stops the suite quietly falling behind the UI the way the old tests did.</summary>
    private static string? EveryControlIsSpecified()
    {
        MainForm? form = null;
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
