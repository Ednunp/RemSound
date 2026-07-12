using RemSound.Core;
using RemSound.Sender;

namespace RemSound.App;

/// <summary>
/// The "Configure RemSound service profile" dialog: a self-contained, modal editor for the send-only
/// service profile. Three tabs — Audio send, Audio profile, Connectivity — plus a button row of
/// Save and Close / Cancel / Additional options. Being modal, there are no menus and no profile
/// switching to worry about. It edits a <see cref="Profile"/> clone; nothing is persisted until the
/// caller acts on an OK result. Send-only and WASAPI-only: no receive controls, no ASIO, and no
/// "Send my audio" toggle (it always sends).
/// </summary>
internal sealed class ServiceProfileDialog : Form
{
    private readonly Profile working;

    private readonly QuietTabControl tabs = new() { Dock = DockStyle.Fill };

    // --- Audio send tab ---
    private readonly ListBox sendModeList = new() { Width = 460, Height = 38, IntegralHeight = false, AccessibleName = "How to send WASAPI audio (Alt+6)" };
    private readonly CheckedListBox outputsList = new() { CheckOnClick = true, Width = 460, Height = 110, AccessibleName = "WASAPI audio outputs to send (Alt+4)" };
    private readonly AccessibleCheckBox sendAllAppsBox = new() { Text = "Send all applications (Alt+&7)", AccessibleName = "Send all applications", AutoSize = true, Checked = true };
    private readonly CheckedListBox appsList = new() { CheckOnClick = true, Width = 460, Height = 110, AccessibleName = "Applications to send (Alt+8)" };
    private readonly CheckedListBox inputsList = new() { CheckOnClick = true, Width = 460, Height = 90, AccessibleName = "WASAPI audio inputs to send (Alt+5)" };
    private MnemonicLabel? sendModeLabel, outputsLabel, appsLabel, inputsLabel;

    // --- Audio profile tab ---
    private readonly ComboBox codecBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360, AccessibleName = "Audio codec (Alt+C)" };
    private readonly ComboBox sendRateBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360, AccessibleName = "Send rate (Alt+E)" };
    private readonly AccessibleCheckBox tightLatencyBox = new() { Text = "&Lock to audio clock — steadier timing, slightly higher latency (Alt+L)", AccessibleName = "Lock to audio clock", AutoSize = true };

    // --- Connectivity tab ---
    private readonly ListBox peersList = new() { Width = 360, Height = 120, AccessibleName = "Peers to send to (Alt+P)" };
    private readonly TextBox addPeerBox = new() { Width = 260, AccessibleName = "Add a peer address or hostname (Alt+A)" };
    private readonly Button addPeerButton = new() { Text = "A&dd", AutoSize = true, AccessibleName = "Add peer" };
    private readonly Button removePeerButton = new() { Text = "&Remove", AutoSize = true, AccessibleName = "Remove selected peer" };
    private readonly Button passwordButton = new() { Text = "Set pass&word...", AutoSize = true, AccessibleName = "Set the service profile password" };
    private readonly Label passwordStatus = new() { AutoSize = true };

    // --- Button row ---
    private readonly Button saveButton = new() { Text = "&Save and Close", AutoSize = true, DialogResult = DialogResult.OK };
    private readonly Button cancelButton = new() { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
    private readonly Button additionalButton = new() { Text = "Additional &options...", AutoSize = true, AccessibleName = "Additional options" };

    private bool suppressAppEvents;

    /// <summary>The edited profile (valid after an OK result).</summary>
    public Profile Result => working;

    /// <summary>Whether the service should write its own log — machine-wide, so it's returned separately
    /// from the profile. Seeded from AppConfig; the caller writes it back to AppConfig on OK.</summary>
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
        ClientSize = new Size(540, 560);
        AccessibleName = "Configure RemSound service profile";

        BuildAudioSendTab();
        BuildAudioProfileTab();
        BuildConnectivityTab();
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

    private TableLayoutPanel NewColumn() => new()
    {
        Dock = DockStyle.Fill,
        ColumnCount = 2,
        AutoScroll = true,
        Padding = new Padding(10),
    };

    private void BuildAudioSendTab()
    {
        var page = new TabPage("Audio send");
        var panel = NewColumn();
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        sendModeList.Items.Add("Send whole audio devices");
        sendModeList.Items.Add("Send specific applications");
        sendModeList.SelectedIndex = 0;
        sendModeList.SelectedIndexChanged += (_, _) => { if (!suppressAppEvents) ApplySendModeVisibility(); };
        sendAllAppsBox.CheckedChanged += (_, _) => { if (!suppressAppEvents) ApplySendModeVisibility(); };

        sendModeLabel = AddListRow(panel, 0, "How to send WASAPI audio (Alt+&6)", sendModeList);
        outputsLabel = AddListRow(panel, 1, "WASAPI audio outputs to send (Alt+&4)", outputsList);
        var allAppsWrap = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        allAppsWrap.Controls.Add(sendAllAppsBox);
        panel.Controls.Add(allAppsWrap, 1, 2);
        appsLabel = AddListRow(panel, 3, "Applications to send (Alt+&8)", appsList);
        inputsLabel = AddListRow(panel, 4, "WASAPI audio inputs to send (Alt+&5)", inputsList);

        page.Controls.Add(panel);
        tabs.TabPages.Add(page);
    }

    private void BuildAudioProfileTab()
    {
        var page = new TabPage("Audio profile");
        var panel = NewColumn();
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        codecBox.Items.AddRange(new object[]
        {
            new CodecChoice("Uncompressed PCM (best quality, most bandwidth)", AudioTransportCodec.Pcm, 480),
            new CodecChoice("Opus broadcast quality (20 ms, loss tolerant)", AudioTransportCodec.Opus, 960),
            new CodecChoice("Opus live latency (2.5 ms, for jamming)", AudioTransportCodec.Opus, 120),
        });
        sendRateBox.Items.AddRange(new object[] { SendRate.Standard, SendRate.Tight });

        AddControlRow(panel, 0, "Audio &codec (Alt+C)", codecBox);
        AddControlRow(panel, 1, "Send rat&e (Alt+E)", sendRateBox);
        var tightWrap = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        tightWrap.Controls.Add(tightLatencyBox);
        panel.Controls.Add(tightWrap, 1, 2);

        page.Controls.Add(panel);
        tabs.TabPages.Add(page);
    }

    private void BuildConnectivityTab()
    {
        var page = new TabPage("Connectivity");
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Padding = new Padding(10) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddListRow(panel, 0, "Peers to send to (Alt+&P)", peersList);

        var addRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        addRow.Controls.Add(addPeerBox);
        addRow.Controls.Add(addPeerButton);
        addRow.Controls.Add(removePeerButton);
        var addLabel = new MnemonicLabel { Text = "Add peer (Alt+&A)", AutoSize = true, Anchor = AnchorStyles.Left, MnemonicTarget = addPeerBox };
        panel.Controls.Add(addLabel, 0, 1);
        panel.Controls.Add(addRow, 1, 1);

        addPeerButton.Click += (_, _) => AddPeerFromBox();
        removePeerButton.Click += (_, _) => { if (peersList.SelectedItem is string s) { peersList.Items.Remove(s); } };
        addPeerBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { AddPeerFromBox(); e.SuppressKeyPress = true; } };

        var pwWrap = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        pwWrap.Controls.Add(passwordButton);
        pwWrap.Controls.Add(passwordStatus);
        panel.Controls.Add(pwWrap, 1, 2);
        passwordButton.Click += (_, _) => SetPassword();

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

    private void AddControlRow(TableLayoutPanel panel, int row, string label, Control control)
    {
        var l = new MnemonicLabel { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, MnemonicTarget = control };
        l.Click += (_, _) => control.Focus();
        panel.Controls.Add(l, 0, row);
        panel.Controls.Add(control, 1, row);
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
            sendRateBox.SelectedItem = working.SendRate;
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
        if (sendRateBox.SelectedItem is SendRate r) working.SendRate = r;
        working.TightLatencyMode = tightLatencyBox.Checked;

        var peers = peersList.Items.OfType<string>().Distinct().ToList();
        working.SelectedConnectedPeers = peers;
        working.RememberedPeers = peers;
    }

    private void PopulateDeviceList(CheckedListBox list, IReadOnlyList<AudioDeviceChoice> devices, IReadOnlyList<string> checkedIds)
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
        if (result is null) return; // cancelled
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

    /// <summary>One row in the applications list. Identity is the process name; display adds a
    /// "(not running)" hint for a remembered-but-closed app.</summary>
    private sealed class AppRow
    {
        public string ProcessName { get; }
        private readonly string display;
        private readonly bool running;
        public AppRow(string processName, string displayName, bool running) { ProcessName = processName; display = displayName; this.running = running; }
        public override string ToString() => running ? display : $"{display} (not running)";
    }
}
