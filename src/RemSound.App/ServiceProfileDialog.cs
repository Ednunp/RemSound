using RemSound.Core;
using RemSound.Sender;

namespace RemSound.App;

/// <summary>
/// The "Configure RemSound service profile" dialog: a self-contained, modal editor for the send-only
/// service profile, built to match the main window — same house controls, same layout rows, and the
/// same screen-reader wiring on the lists (via <see cref="CheckedListAccessibility"/>), so it reads and
/// behaves exactly like the real tabs. Tab order mirrors the main window: Connectivity, then Audio send,
/// then Audio profile. Send-only and WASAPI-only: no receive controls, no ASIO, no "Send my audio"
/// toggle. Edits a <see cref="Profile"/> clone; nothing is persisted until the caller acts on OK.
/// </summary>
internal sealed class ServiceProfileDialog : Form
{
    private readonly Profile working;
    private readonly QuietTabControl tabs = new() { Dock = DockStyle.Fill };

    // --- Connectivity tab ---
    private readonly ListBox peersList = new() { Width = 460, Height = 120, AccessibleName = "Peers to send to (Alt+1)" };
    private readonly TextBox addPeerBox = new() { Width = 300, AccessibleName = "Add a peer — IP address or hostname (Alt+2)" };
    private readonly Button addPeerButton = new() { Text = "A&dd", AutoSize = true, AccessibleName = "Add peer" };
    private readonly Button removePeerButton = new() { Text = "&Remove", AutoSize = true, AccessibleName = "Remove selected peer" };
    private readonly Button passwordButton = new() { Text = "Set pass&word...", AutoSize = true, AccessibleName = "Set the service profile password" };
    private readonly Label passwordStatus = new() { AutoSize = true };

    // --- Audio send tab ---
    private readonly ListBox sendModeList = new() { Width = 460, Height = 40, IntegralHeight = false, AccessibleName = "How to send WASAPI audio (Alt+1)" };
    private readonly CheckedListBox outputsList = new() { CheckOnClick = true, Width = 460, Height = 110, AccessibleName = "WASAPI audio outputs to send (Alt+2)" };
    private readonly Label outputsStatus = new() { AutoSize = true, Text = "No output device selected." };
    private readonly AccessibleCheckBox sendAllAppsBox = new() { Text = "Send all applications (Alt+&3)", AccessibleName = "Send all applications", AutoSize = true, Checked = true };
    private readonly CheckedListBox appsList = new() { CheckOnClick = true, Width = 460, Height = 110, AccessibleName = "Applications to send (Alt+4)" };
    private readonly Label appsStatus = new() { AutoSize = true, Text = "No application selected." };
    private readonly CheckedListBox inputsList = new() { CheckOnClick = true, Width = 460, Height = 90, AccessibleName = "WASAPI audio inputs to send (Alt+5)" };
    private readonly Label inputsStatus = new() { AutoSize = true, Text = "No input device selected." };
    private MnemonicLabel? sendModeLabel, outputsLabel, appsLabel, inputsLabel;

    // --- Audio profile tab ---
    private readonly ComboBox codecBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360, AccessibleName = "Audio codec (Alt+C)" };
    private readonly ListBox sendRateBox = new() { Width = 360, Height = 40, IntegralHeight = false, AccessibleName = "Packet size (Alt+P)" };
    private readonly AccessibleCheckBox tightLatencyBox = new() { AutoSize = true };

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

        // Tabs in the main window's order: Connectivity, Audio send, Audio profile.
        BuildConnectivityTab();
        BuildAudioSendTab();
        BuildAudioProfileTab();
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

        AddListRow(panel, 0, "Peers to send to (Alt+&1)", peersList);

        var addLabel = new MnemonicLabel { Text = "Add a peer — IP address or hostname (Alt+&2)", AutoSize = true, Anchor = AnchorStyles.Left, MnemonicTarget = addPeerBox };
        panel.Controls.Add(addLabel, 0, 1);
        var addRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        addRow.Controls.Add(addPeerBox);
        addRow.Controls.Add(addPeerButton);
        addRow.Controls.Add(removePeerButton);
        panel.Controls.Add(addRow, 1, 1);

        addPeerButton.Click += (_, _) => AddPeerFromBox();
        removePeerButton.Click += (_, _) => { if (peersList.SelectedItem is string s) peersList.Items.Remove(s); };
        addPeerBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { AddPeerFromBox(); e.SuppressKeyPress = true; } };

        var pwWrap = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        pwWrap.Controls.Add(passwordButton);
        pwWrap.Controls.Add(passwordStatus);
        panel.Controls.Add(pwWrap, 1, 2);
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
        sendAllAppsBox.CheckedChanged += (_, _) => { if (!suppressAppEvents) ApplySendModeVisibility(); };

        sendModeLabel = AddListRow(panel, 0, "How to send WASAPI audio (Alt+&1)", sendModeList);
        outputsLabel = FormLayoutRows.AddCheckedListRow(panel, 1, "WASAPI audio outputs to send (Alt+&2)", outputsList, outputsStatus, l => l.Focus());
        var allAppsWrap = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        allAppsWrap.Controls.Add(sendAllAppsBox);
        panel.Controls.Add(allAppsWrap, 1, 2);
        appsLabel = FormLayoutRows.AddCheckedListRow(panel, 3, "Applications to send (Alt+&4)", appsList, appsStatus, l => l.Focus());
        inputsLabel = FormLayoutRows.AddCheckedListRow(panel, 4, "WASAPI audio inputs to send (Alt+&5)", inputsList, inputsStatus, l => l.Focus());

        CheckedListAccessibility.Wire(outputsList, outputsStatus, "output device");
        CheckedListAccessibility.Wire(appsList, appsStatus, "application");
        CheckedListAccessibility.Wire(inputsList, inputsStatus, "input device");

        page.Controls.Add(panel);
        tabs.TabPages.Add(page);
    }

    private void BuildAudioProfileTab()
    {
        var page = new TabPage("Audio profile");
        var panel = NewPanel();

        // Exact same codec choices, packet-size items and lock-to-clock label as the main window.
        codecBox.Items.AddRange(new object[]
        {
            new CodecChoice("PCM 48K 24 bit — uncompressed", AudioTransportCodec.Pcm, 0),
            new CodecChoice("Opus, broadcast quality — loss tolerant", AudioTransportCodec.Opus, 960),
            new CodecChoice("Opus, live latency — for jamming and monitoring", AudioTransportCodec.Opus, 120),
        });
        sendRateBox.Items.Add("Standard (5 ms PCM, 10/20 ms Opus)");
        sendRateBox.Items.Add("Small (2.5 ms PCM, 5/10 ms Opus, LAN only)");
        tightLatencyBox.Text = "Lock to au&dio clock, WASAPI sender";
        tightLatencyBox.AccessibleName = "Lock to audio clock (Alt+D) — sender uses the WASAPI capture event for timing instead of a Stopwatch tick. Tightens delay; brief clicks possible if the link can't keep up.";

        FormLayoutRows.AddRow(panel, 0, "Audio &codec (Alt+C)", codecBox, c => c.Focus());
        FormLayoutRows.AddRow(panel, 1, "&Packet size (Alt+P)", sendRateBox, c => c.Focus());
        var tightWrap = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        tightWrap.Controls.Add(tightLatencyBox);
        panel.Controls.Add(tightWrap, 1, 2);

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
            PopulateDeviceList(outputsList, AudioDeviceCatalog.LoadOutputs(), working.SelectedWasapiSendOutputs);
            PopulateDeviceList(inputsList, AudioDeviceCatalog.LoadInputs(), working.SelectedWasapiSendInputs);

            var appsMode = ProcessLoopbackCapture.IsSupported
                && string.Equals(working.WasapiSendMode, "applications", StringComparison.OrdinalIgnoreCase);
            sendModeList.SelectedIndex = appsMode ? 1 : 0;
            sendAllAppsBox.Checked = working.SendAllApplications;
            PopulateAppsList();

            SelectCodec();
            sendRateBox.SelectedIndex = Math.Clamp((int)working.SendRate, 0, sendRateBox.Items.Count - 1);
            tightLatencyBox.Checked = working.TightLatencyMode;

            peersList.Items.Clear();
            foreach (var p in (working.SelectedConnectedPeers.Count > 0 ? working.SelectedConnectedPeers : working.RememberedPeers).Distinct())
                peersList.Items.Add(p);
        }
        finally { suppressAppEvents = false; }
    }

    private void SaveToProfile()
    {
        working.SelectedWasapiSendOutputs = CheckedIds(outputsList);
        working.SelectedWasapiSendInputs = CheckedIds(inputsList);
        working.WasapiSendMode = sendModeList.SelectedIndex == 1 ? "applications" : "devices";
        working.SendAllApplications = sendAllAppsBox.Checked;
        working.SelectedSendApplications = appsList.CheckedItems.OfType<AppRow>().Select(a => a.ProcessName).Distinct().ToList();

        if (codecBox.SelectedItem is CodecChoice c) { working.Codec = c.Codec; working.OpusFrameSamplesPerChannel = c.OpusFrameSamples; }
        working.SendRate = (SendRate)Math.Max(0, sendRateBox.SelectedIndex);
        working.TightLatencyMode = tightLatencyBox.Checked;

        var peers = peersList.Items.OfType<string>().Distinct().ToList();
        working.SelectedConnectedPeers = peers;
        working.RememberedPeers = peers;
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

    private void SelectCodec()
    {
        foreach (var item in codecBox.Items.OfType<CodecChoice>())
            if (item.Codec == working.Codec && (working.Codec == AudioTransportCodec.Pcm || item.OpusFrameSamples == working.OpusFrameSamplesPerChannel))
            { codecBox.SelectedItem = item; return; }
        if (codecBox.Items.Count > 0) codecBox.SelectedIndex = 0;
    }

    private void ApplySendModeVisibility()
    {
        var supported = ProcessLoopbackCapture.IsSupported;
        if (sendModeLabel is not null) sendModeLabel.Visible = supported;
        SetRowVisible(sendModeList, supported);
        if (!supported && sendModeList.SelectedIndex != 0) { suppressAppEvents = true; sendModeList.SelectedIndex = 0; suppressAppEvents = false; }

        var appsMode = supported && sendModeList.SelectedIndex == 1;
        if (outputsLabel is not null) outputsLabel.Visible = !appsMode;
        SetRowVisible(outputsList, !appsMode);
        SetRowVisible(sendAllAppsBox, appsMode);
        var showApps = appsMode && !sendAllAppsBox.Checked;
        if (appsLabel is not null) appsLabel.Visible = showApps;
        SetRowVisible(appsList, showApps);
    }

    private static void SetRowVisible(Control c, bool visible)
    {
        c.Visible = visible;
        if (c.Parent is not null) c.Parent.Visible = visible;
    }

    private void AddPeerFromBox()
    {
        var text = addPeerBox.Text.Trim();
        if (text.Length == 0) return;
        if (!peersList.Items.OfType<string>().Any(p => string.Equals(p, text, StringComparison.OrdinalIgnoreCase)))
            peersList.Items.Add(text);
        addPeerBox.Clear();
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
            ClientSize = new Size(460, 200),
            AccessibleName = "Additional service options",
        };
        var connect = new AccessibleCheckBox { Text = "Play a sound when a peer &connects (Alt+C)", AccessibleName = "Play connect sound", AutoSize = true, Checked = working.EnableConnectCue ?? true };
        var disconnect = new AccessibleCheckBox { Text = "Play a sound when a peer &disconnects (Alt+D)", AccessibleName = "Play disconnect sound", AutoSize = true, Checked = working.EnableDisconnectCue ?? true };
        var logging = new AccessibleCheckBox { Text = "Enable service &logging (Alt+L)", AccessibleName = "Enable service logging", AutoSize = true, Checked = ServiceLoggingEnabled };
        var ok = new Button { Text = "&OK", AutoSize = true, DialogResult = DialogResult.OK };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12), AutoSize = true };
        foreach (var c in new Control[] { connect, disconnect, logging, ok }) { var w = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill }; w.Controls.Add(c); layout.Controls.Add(w); }
        dlg.Controls.Add(layout);
        dlg.AcceptButton = ok;

        if (ForegroundDialog.Show(owner => dlg.ShowDialog(owner)) == DialogResult.OK)
        {
            working.EnableConnectCue = connect.Checked;
            working.EnableDisconnectCue = disconnect.Checked;
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
