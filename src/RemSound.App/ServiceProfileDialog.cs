using RemSound.Core;
using RemSound.Sender;

namespace RemSound.App;

/// <summary>
/// The "Configure RemSound service profile" dialog: a self-contained, modal editor for the send-only
/// service profile, built to match the main window — same house controls, same layout rows, and the
/// same screen-reader wiring on the lists (via <see cref="CheckedListAccessibility"/>), so it reads and
/// behaves exactly like the real tabs. Two tabs: Connectivity, then Audio send. The Audio profile tab
/// was removed (2026-07-17) — codec/packet/lock-to-clock are fixed to known-good live values so nobody
/// mis-tunes the service. Send-only and WASAPI-only: no receive controls, no ASIO, no "Send my audio"
/// toggle. Edits a <see cref="Profile"/> clone; nothing is persisted until the caller acts on OK.
/// </summary>
internal sealed class ServiceProfileDialog : Form
{
    private readonly Profile working;
    private readonly QuietTabControl tabs = new() { Dock = DockStyle.Fill };

    // --- Connectivity tab (mirrors the main window: a wired peer list + Add peer by IP) ---
    private readonly CheckedListBox peersList = new() { CheckOnClick = true, Width = 460, Height = 120, AccessibleName = "Peers to send to (Alt+C)" };
    private readonly Label peersStatus = new() { AutoSize = true, Text = "No peers." };
    private readonly Button manualAddButton = new() { Text = "Add peer by IP (Alt+&A)", AutoSize = true, AccessibleName = "Add peer by IP" };
    private readonly Button passwordButton = new() { Text = "Set service profile pass&word...", AutoSize = true, AccessibleName = "Set service profile password" };
    private readonly Label passwordStatus = new() { AutoSize = true };

    // --- Audio send tab ---
    private readonly ListBox sendModeList = new() { Width = 460, Height = 40, IntegralHeight = false, AccessibleName = "How to send WASAPI audio (Alt+1)" };
    private readonly CheckedListBox outputsList = new() { CheckOnClick = true, Width = 460, Height = 110, AccessibleName = "WASAPI audio outputs to send (Alt+2)" };
    private readonly Label outputsStatus = new() { AutoSize = true, Text = "No output device selected." };
    private readonly CheckedListBox appsList = new() { CheckOnClick = true, Width = 460, Height = 110, AccessibleName = "Applications to send (Alt+3)" };
    private readonly Label appsStatus = new() { AutoSize = true, Text = "No application selected." };
    // No WASAPI-inputs (microphone/line-in) list: the service sends this machine's OUTPUT audio (or
    // specific apps) only — capturing a mic from an unattended, logged-out box isn't a use case (Ed).
    private MnemonicLabel? sendModeLabel, outputsLabel, appsLabel;

    // No "Audio profile" tab: the service always sends the fixed live-jamming transport (see
    // ServiceAudioDefaults in Core — ONE set of numbers shared with ServiceSendHost, which re-forces
    // them at runtime, so the saved profile and the running stream can never disagree).
    private const AudioTransportCodec ServiceCodec = ServiceAudioDefaults.Codec;
    private const int ServiceOpusFrameSamples = ServiceAudioDefaults.OpusFrameSamplesPerChannel;
    private const SendRate ServiceSendRate = ServiceAudioDefaults.Rate;

    // --- Button row ---
    private readonly Button saveButton = new() { Text = "&Save and Close", AutoSize = true, DialogResult = DialogResult.OK };
    private readonly Button cancelButton = new() { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
    private readonly Button additionalButton = new() { Text = "Additional &options...", AutoSize = true, AccessibleName = "Additional options" };

    private bool suppressAppEvents;

    public Profile Result => working;
    public bool ServiceLoggingEnabled { get; private set; }

    public ServiceProfileDialog(Profile current, bool serviceLoggingEnabled)
    {
        working = CloneProfile(current);
        working.Title = ServiceControl.ServiceProfileTitle;
        ServiceLoggingEnabled = serviceLoggingEnabled;

        Text = "Configure RemSound service profile";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;
        ClientSize = new Size(560, 580);
        AccessibleName = "Configure RemSound service profile";
        if (Theme.AppIcon is { } icon) Icon = icon;

        // Two tabs only now: Connectivity and Audio send. The Audio profile tab was removed — the codec,
        // packet size and lock-to-clock are fixed to known-good live-jamming values (see fields above).
        BuildConnectivityTab();
        BuildAudioSendTab();
        BuildButtonRow();

        LoadFromProfile();
        ApplySendModeVisibility();
        UpdatePasswordStatus();

        AcceptButton = saveButton;
        CancelButton = cancelButton;
        saveButton.Click += (_, _) => SaveToProfile();
        additionalButton.Click += (_, _) => ShowAdditionalOptions();
    }

    // ---------------- layout ----------------

    private static TableLayoutPanel NewPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Padding = new Padding(12) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return panel;
    }

    private void BuildConnectivityTab()
    {
        var page = new TabPage("Connectivity");
        var panel = NewPanel();

        var header = Theme.SectionHeader("Peers");
        panel.Controls.Add(header, 0, 0);
        panel.SetColumnSpan(header, 2);

        // The peer list, wired for the screen reader exactly like the main window. A ticked peer is one
        // the service sends to; untick to keep it in the list but not send. Delete removes it entirely.
        FormLayoutRows.AddCheckedListRow(panel, 1, "Peers to send to (Alt+&C)", peersList, peersStatus, l => l.Focus());
        CheckedListAccessibility.Wire(peersList, peersStatus, "peer");
        peersList.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete && peersList.SelectedIndex >= 0)
            {
                peersList.Items.RemoveAt(peersList.SelectedIndex);
                e.Handled = true; e.SuppressKeyPress = true;
            }
        };

        // Add a peer by address — the same "Add peer by IP" prompt the main window uses.
        panel.Controls.Add(new Label { Text = "Manual peer", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        panel.Controls.Add(manualAddButton, 1, 2);
        manualAddButton.Click += (_, _) =>
        {
            var entry = ManualPeerPrompt.Show(this);
            if (string.IsNullOrWhiteSpace(entry)) return;
            if (!peersList.Items.OfType<string>().Any(p => string.Equals(p, entry, StringComparison.OrdinalIgnoreCase)))
            {
                var idx = peersList.Items.Add(entry.Trim());
                peersList.SetItemChecked(idx, true);
            }
        };

        var pwWrap = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        pwWrap.Controls.Add(passwordButton);
        pwWrap.Controls.Add(passwordStatus);
        panel.Controls.Add(pwWrap, 1, 3);
        passwordButton.Click += (_, _) => SetPassword();

        page.Controls.Add(panel);
        tabs.TabPages.Add(page);
    }

    private void BuildAudioSendTab()
    {
        var page = new TabPage("Audio send");
        var panel = NewPanel();

        sendModeList.Items.Add("Send whole audio devices");
        sendModeList.Items.Add("Send specific applications");
        sendModeList.SelectedIndex = 0;
        sendModeList.SelectedIndexChanged += (_, _) => { if (!suppressAppEvents) ApplySendModeVisibility(); };

        sendModeLabel = AddListRow(panel, 0, "How to send WASAPI audio (Alt+&1)", sendModeList);
        outputsLabel = FormLayoutRows.AddCheckedListRow(panel, 1, "WASAPI audio outputs to send (Alt+&2)", outputsList, outputsStatus, l => l.Focus());
        appsLabel = FormLayoutRows.AddCheckedListRow(panel, 2, "Applications to send (Alt+&3)", appsList, appsStatus, l => l.Focus());

        CheckedListAccessibility.Wire(outputsList, outputsStatus, "output device");
        CheckedListAccessibility.Wire(appsList, appsStatus, "application");

        // "Use Windows default output" is exclusive, same as the main app: while it's ticked the specific
        // output cards can't be, and ticking it clears them.
        outputsList.ItemCheck += (_, e) =>
        {
            if (suppressAppEvents) return;
            if (AudioDefaultFollower.VetoRealDeviceCheck(outputsList, e)) return;
            if (outputsList.Items[e.Index] is AudioDeviceChoice { IsDefaultFollower: true } && e.NewValue == CheckState.Checked)
                BeginInvoke(new Action(() =>
                {
                    suppressAppEvents = true;
                    try { AudioDefaultFollower.UncheckRealDevices(outputsList); }
                    finally { suppressAppEvents = false; }
                }));
        };

        page.Controls.Add(panel);
        tabs.TabPages.Add(page);
    }

    private void BuildButtonRow()
    {
        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.Controls.Add(tabs, 0, 0);

        var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8) };
        row.Controls.Add(saveButton);
        row.Controls.Add(cancelButton);
        row.Controls.Add(additionalButton);
        outer.Controls.Add(row, 0, 1);
        Controls.Add(outer);
    }

    private MnemonicLabel AddListRow(TableLayoutPanel panel, int row, string label, Control list)
    {
        var l = new MnemonicLabel { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, MnemonicTarget = list };
        l.Click += (_, _) => list.Focus();
        panel.Controls.Add(l, 0, row);
        var wrap = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, TabStop = false };
        wrap.Controls.Add(list);
        panel.Controls.Add(wrap, 1, row);
        return l;
    }

    // ---------------- data in/out ----------------

    private void LoadFromProfile()
    {
        suppressAppEvents = true;
        try
        {
            // Offer "Use Windows default output" as the first output choice (Christopher's request):
            // send whatever this machine currently plays, following the Windows default. Same shared
            // follower + sentinel the main window uses, so a ticked follower persists and resolves identically.
            var outputChoices = new List<AudioDeviceChoice> { AudioDefaultFollower.LoopbackSendChoice() };
            outputChoices.AddRange(AudioDeviceCatalog.LoadOutputs());
            PopulateDeviceList(outputsList, outputChoices, working.SelectedWasapiSendOutputs);
            // Default is exclusive: if the profile carries the follower, drop any specific outputs it also
            // carried (already inside the suppressAppEvents guard set at the top of LoadFromProfile).
            if (AudioDefaultFollower.IsFollowerChecked(outputsList)) AudioDefaultFollower.UncheckRealDevices(outputsList);

            var appsMode = ProcessLoopbackCapture.IsSupported
                && string.Equals(working.WasapiSendMode, "applications", StringComparison.OrdinalIgnoreCase);
            sendModeList.SelectedIndex = appsMode ? 1 : 0;
            PopulateAppsList();

            peersList.Items.Clear();
            var allPeers = working.RememberedPeers.Concat(working.SelectedConnectedPeers)
                .Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var selectedPeers = new HashSet<string>(working.SelectedConnectedPeers, StringComparer.OrdinalIgnoreCase);
            foreach (var p in allPeers)
            {
                var idx = peersList.Items.Add(p);
                // Ticked = the service sends to it. If the profile carried no explicit selection, tick all.
                if (selectedPeers.Count == 0 || selectedPeers.Contains(p)) peersList.SetItemChecked(idx, true);
            }
        }
        finally { suppressAppEvents = false; }
    }

    private void SaveToProfile()
    {
        working.SelectedWasapiSendOutputs = CheckedIds(outputsList);
        // The service never sends WASAPI inputs (no mic list any more) — clear any legacy selection so a
        // profile saved by an older build can't keep a mic streaming.
        working.SelectedWasapiSendInputs = new List<string>();
        working.WasapiSendMode = sendModeList.SelectedIndex == 1 ? "applications" : "devices";
        // Applications mode = pick specific apps only (matches the main app; the "send all applications"
        // option was removed from both). Force the legacy flag off so a stale profile that still carries
        // SendAllApplications=true can't make the service quietly capture the whole system.
        working.SendAllApplications = false;
        working.SelectedSendApplications = appsList.CheckedItems.OfType<AppRow>().Select(a => a.ProcessName).Distinct().ToList();

        // Audio profile is fixed for the service (no tab): Opus live-jamming frame, Small packets, locked
        // to the audio clock. These are also re-forced at runtime in ServiceSendHost.
        working.Codec = ServiceCodec;
        working.OpusFrameSamplesPerChannel = ServiceOpusFrameSamples;
        working.SendRate = ServiceSendRate;
        working.TightLatencyMode = true;

        // Ticked peers are the ones the service sends to; keep every listed peer as remembered.
        working.SelectedConnectedPeers = peersList.CheckedItems.OfType<string>().Distinct().ToList();
        working.RememberedPeers = peersList.Items.OfType<string>().Distinct().ToList();
    }

    private static void PopulateDeviceList(CheckedListBox list, IReadOnlyList<AudioDeviceChoice> devices, IReadOnlyList<string> checkedIds)
    {
        list.Items.Clear();
        var wanted = new HashSet<string>(checkedIds, StringComparer.OrdinalIgnoreCase);
        foreach (var d in devices)
        {
            if (d.DeviceId is null) continue;
            var idx = list.Items.Add(d);
            if (wanted.Contains(d.DeviceId)) list.SetItemChecked(idx, true);
        }
    }

    private static List<string> CheckedIds(CheckedListBox list) =>
        list.CheckedItems.OfType<AudioDeviceChoice>().Where(c => c.DeviceId is not null).Select(c => c.DeviceId!).Distinct().ToList();

    private void PopulateAppsList()
    {
        var ticked = new HashSet<string>(working.SelectedSendApplications, StringComparer.OrdinalIgnoreCase);
        var running = ProcessLoopbackCapture.IsSupported ? AudioAppEnumerator.Snapshot() : Array.Empty<AudioApp>();
        var runningNames = new HashSet<string>(running.Select(a => a.ProcessName), StringComparer.OrdinalIgnoreCase);
        appsList.Items.Clear();
        foreach (var a in running) AddApp(a.ProcessName, a.DisplayName, running: true, ticked);
        foreach (var name in ticked) if (!runningNames.Contains(name)) AddApp(name, name, running: false, ticked);

        void AddApp(string proc, string display, bool running, HashSet<string> tickedSet)
        {
            var idx = appsList.Items.Add(new AppRow(proc, display, running));
            if (tickedSet.Contains(proc)) appsList.SetItemChecked(idx, true);
        }
    }

    private void ApplySendModeVisibility()
    {
        var supported = ProcessLoopbackCapture.IsSupported;
        if (sendModeLabel is not null) sendModeLabel.Visible = supported;
        SetRowVisible(sendModeList, supported);
        if (!supported && sendModeList.SelectedIndex != 0)
        {
            suppressAppEvents = true;
            try { sendModeList.SelectedIndex = 0; }
            finally { suppressAppEvents = false; } // a throw must not leave events suppressed for good
        }

        var appsMode = supported && sendModeList.SelectedIndex == 1;
        if (outputsLabel is not null) outputsLabel.Visible = !appsMode;
        SetRowVisible(outputsList, !appsMode);
        // Applications mode always shows the specific-apps list (no "send all" option any more).
        if (appsLabel is not null) appsLabel.Visible = appsMode;
        SetRowVisible(appsList, appsMode);
    }

    private static void SetRowVisible(Control c, bool visible)
    {
        c.Visible = visible;
        if (c.Parent is not null) c.Parent.Visible = visible;
    }

    private void SetPassword()
    {
        var current = string.IsNullOrEmpty(working.Password) ? "" : RemSoundCrypto.Deobfuscate(working.Password);
        var result = ProfilePasswordDialog.Show(ServiceControl.ServiceProfileTitle, current);
        if (result is null) return;
        working.Password = string.IsNullOrEmpty(result) ? null : RemSoundCrypto.Obfuscate(result);
        UpdatePasswordStatus();
    }

    private void UpdatePasswordStatus()
        => passwordStatus.Text = string.IsNullOrEmpty(working.Password) ? "No password set." : "Password set.";

    private void ShowAdditionalOptions()
    {
        using var dlg = new Form
        {
            Text = "Additional service options",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(460, 110),
            AccessibleName = "Additional service options",
        };
        // No connect/disconnect cue checkboxes here (removed 2026-07-19, review sweep): the dialog used
        // to offer them, but the headless service NEVER plays cues — nothing in the service host touches
        // CuePlayer, and a logged-out session couldn't render them anyway. Offering a switch that does
        // nothing is worse than not offering it. The cue fields stay on Profile for the app's own use.
        var logging = new AccessibleCheckBox { Text = "Enable service &logging (Alt+L)", AccessibleName = "Enable service logging", AutoSize = true, Checked = ServiceLoggingEnabled };
        var ok = new Button { Text = "&OK", AutoSize = true, DialogResult = DialogResult.OK };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12), AutoSize = true };
        foreach (var c in new Control[] { logging, ok }) { var w = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill }; w.Controls.Add(c); layout.Controls.Add(w); }
        dlg.Controls.Add(layout);
        dlg.AcceptButton = ok;

        if (ForegroundDialog.Show(owner => dlg.ShowDialog(owner)) == DialogResult.OK)
        {
            ServiceLoggingEnabled = logging.Checked;
        }
    }

    private static Profile CloneProfile(Profile p) =>
        System.Text.Json.JsonSerializer.Deserialize<Profile>(System.Text.Json.JsonSerializer.Serialize(p)) ?? new Profile();

    private sealed class AppRow
    {
        public string ProcessName { get; }
        private readonly string display;
        private readonly bool running;
        public AppRow(string processName, string displayName, bool running) { ProcessName = processName; display = displayName; this.running = running; }
        public override string ToString() => running ? display : $"{display} (not running)";
    }
}
