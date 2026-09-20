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
    // The server the service goes to. It has no window of its own to press Connect in, so the address IS the
    // instruction: set one and the service joins it every time it starts, clear it and it never does.
    private readonly TextBox serverBox = new() { Width = 320, AccessibleName = "Server address (Alt+V)" };
    private readonly Label serverNote = new() { AutoSize = true };

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
    // Alt+N: C is "Peers to send to" and A is "Add peer by IP", and these buttons sit outside the tabs,
    // so they share a letter space with both.
    private readonly Button cancelButton = new() { Text = "Ca&ncel", AutoSize = true, DialogResult = DialogResult.Cancel };
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

        // The server. Nobody is at a screen when the service runs, so there is no Connect button here: an address
        // means "join this every time you start", and an empty box means "never".
        var serverHeader = Theme.SectionHeader("Server");
        panel.Controls.Add(serverHeader, 0, 4);
        panel.SetColumnSpan(serverHeader, 2);
        var serverLabel = new MnemonicLabel { Text = "Ser&ver address (Alt+V)", AutoSize = true, Anchor = AnchorStyles.Left, MnemonicTarget = serverBox };
        serverLabel.Click += (_, _) => serverBox.Focus();
        panel.Controls.Add(serverLabel, 0, 5);
        panel.Controls.Add(serverBox, 1, 5);
        serverNote.Text = "Leave empty for no server. The service joins it whenever it starts.";
        panel.Controls.Add(serverNote, 1, 6);
        serverBox.TextChanged += (_, _) => UpdateServerNote();
        UpdateServerNote();

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

            serverBox.Text = working.RelayServer ?? "";
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

        // Audio profile is fixed for the service (no tab): Opus live-jamming frame and Small packets, also re-forced at
        // runtime in ServiceSendHost. Lock to the audio clock is always on there and is not a profile setting, so nothing is
        // stored for it (TightLatencyMode was set here and read by nothing).
        working.Codec = ServiceCodec;
        working.OpusFrameSamplesPerChannel = ServiceOpusFrameSamples;
        working.SendRate = ServiceSendRate;

        // Ticked peers are the ones the service sends to; keep every listed peer as remembered.
        working.SelectedConnectedPeers = peersList.CheckedItems.OfType<string>().Distinct().ToList();
        working.RememberedPeers = peersList.Items.OfType<string>().Distinct().ToList();

        // The server. An address here means join it on every start; an empty box means never. The people ticked there
        // are kept as they were — the service learns who is on a server from the server, not from this window.
        var server = serverBox.Text.Trim();
        working.RelayServer = server.Length == 0 ? null : server;
        working.RelayConnectOnStart = server.Length > 0;
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

    /// <summary>The line under the address. With a server set, the service can only reach the people its profile
    /// already names unless it is also told to accept whoever ticks it — so say so, next to the box that causes it.</summary>
    internal void SetServerForTest(string address) => serverBox.Text = address;
    internal Profile ResultForTest { get { SaveToProfile(); return working; } }

    private void UpdateServerNote()
    {
        var typed = serverBox.Text.Trim().Length > 0;
        var accepts = PendingAcceptOnRelay ?? ServiceStore.LoadAcceptRelayConnectionsAutomatically();
        serverNote.Text = !typed
            ? "Leave empty for no server. The service joins it whenever it starts."
            : accepts
                ? "The service joins this whenever it starts, and accepts anyone on it who ticks the service."
                : "The service joins this whenever it starts. To let people on it reach the service, turn on "
                  + "\"Accept people who tick this service on a server\" in Additional options.";
    }

    private void UpdatePasswordStatus()
        => passwordStatus.Text = string.IsNullOrEmpty(working.Password) ? "No password set." : "Password set.";

    private void ShowAdditionalOptions()
    {
        // Reopening it before this dialog closes shows what was chosen last time, not the stored value.
        var stored = ServiceStore.LoadStartupVolume();
        var start = PendingStartupVolume ?? (stored.Enabled, stored.Percent, stored.BootOnly);
        var startAccept = PendingAcceptOnRelay ?? ServiceStore.LoadAcceptRelayConnectionsAutomatically();
        var (dlg, logging, volume, accept) = BuildAdditionalOptions(ServiceLoggingEnabled, start.Enabled, start.Percent, start.BootOnly, startAccept);
        using (dlg)
        {
            if (ForegroundDialog.Show(owner => dlg.ShowDialog(owner)) == DialogResult.OK)
                AcceptAdditionalOptions(logging.Checked, volume.Enabled.Checked, (int)volume.Percent.Value, volume.When.SelectedIndex == 0, accept.Checked);
        }
    }

    /// <summary>The startup volume chosen in Additional options, waiting for THIS dialog's OK. Null when
    /// Additional options was never accepted, which leaves the stored value exactly as it was.</summary>
    internal (bool Enabled, int Percent, bool BootOnly)? PendingStartupVolume { get; private set; }

    /// <summary>Whether the service should tick back somebody who ticks it on a relay, waiting for THIS dialog's OK.
    /// Null when Additional options was never accepted, which leaves the stored value exactly as it was.</summary>
    internal bool? PendingAcceptOnRelay { get; private set; }

    /// <summary>OK in Additional options. It HOLDS the choices rather than saving them: they are saved by
    /// the caller along with the service profile, only if this dialog is accepted too.
    ///
    /// <para>It used to write the startup volume to the machine-wide store the moment its own OK was
    /// pressed, so cancelling the service dialog afterwards kept a change the user had just cancelled —
    /// and this class promises that nothing is persisted until the caller acts on OK. The logging flag
    /// beside it was always held this way; the volume now matches. 2026-09-13 review.</para></summary>
    internal void AcceptAdditionalOptions(bool loggingEnabled, bool volumeEnabled, int volumePercent, bool volumeBootOnly,
        bool acceptOnRelay = false)
    {
        ServiceLoggingEnabled = loggingEnabled;
        PendingStartupVolume = (volumeEnabled, Math.Clamp(volumePercent, 0, 100), volumeBootOnly);
        PendingAcceptOnRelay = acceptOnRelay;
        UpdateServerNote();   // the note beside the address depends on this answer
    }

    /// <summary>Construction split from ShowDialog so the accessibility audit can inspect this inner
    /// dialog too. No connect/disconnect cue checkboxes here (removed 2026-07-19, review sweep): the
    /// headless service NEVER plays cues — nothing in the service host touches CuePlayer, and a
    /// logged-out session couldn't render them anyway. The cue fields stay on Profile for the app.
    /// Startup volume (2026-07-26 feature): unmute + set the default output's level when the service
    /// starts — the WHEN list picks "first start after each boot" (default) or "every start".</summary>
    internal static (Form Dialog, AccessibleCheckBox Logging,
        (AccessibleCheckBox Enabled, NumericUpDown Percent, ComboBox When) Volume, AccessibleCheckBox AcceptOnRelay)
        BuildAdditionalOptions(bool loggingEnabled, bool volumeEnabled = false, int volumePercent = 50, bool volumeBootOnly = true,
            bool acceptOnRelay = false)
    {
        var dlg = new Form
        {
            Text = "Additional service options",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(560, 280),
            AccessibleName = "Additional service options",
        };
        var logging = new AccessibleCheckBox { Text = "Enable service &logging (Alt+L)", AccessibleName = "Enable service logging", AutoSize = true, Checked = loggingEnabled };

        var volEnabled = new AccessibleCheckBox
        {
            Text = "Set the machine's &volume when the service starts",
            AccessibleName = "Set the machine's volume when the service starts",
            AutoSize = true,
            Checked = volumeEnabled,
        };
        var volPercent = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 100,
            Value = Math.Clamp(volumePercent, 0, 100),
            Width = 70,
            AccessibleName = "Volume percent",
        };
        var volPercentLabel = new MnemonicLabel { Text = "Volume &percent (also unmutes):", MnemonicTarget = volPercent, AutoSize = true };
        var volWhen = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280, AccessibleName = "When to set the volume" };
        volWhen.Items.Add("Only the first start after each boot");
        volWhen.Items.Add("Every time the service starts");
        volWhen.SelectedIndex = volumeBootOnly ? 0 : 1;
        var volWhenLabel = new MnemonicLabel { Text = "&When:", MnemonicTarget = volWhen, AutoSize = true };
        void SyncVolumeEnabled()
        {
            volPercent.Enabled = volEnabled.Checked;
            volWhen.Enabled = volEnabled.Checked;
        }
        volEnabled.CheckedChanged += (_, _) => SyncVolumeEnabled();
        SyncVolumeEnabled();

        // The service has no screen to ask at, so it gets the two answers that mean something here: tick them back, or
        // leave it to the profile. The app's own three-way setting (ask / automatically / manual) is in Preferences.
        var acceptRelay = new AccessibleCheckBox
        {
            Text = "&Accept people who tick this service on a server",
            AccessibleName = "Accept people who tick this service on a server",
            AutoSize = true,
            Checked = acceptOnRelay,
        };

        var ok = new Button { Text = "&OK", AutoSize = true, DialogResult = DialogResult.OK };
        // A real Cancel, and Escape bound to it. There was only OK, so Escape did nothing at all.
        var cancel = new Button { Text = "&Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12), AutoSize = true };
        foreach (var single in new Control[] { logging, acceptRelay, volEnabled })
        {
            var w = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            w.Controls.Add(single);
            layout.Controls.Add(w);
        }
        var percentRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        percentRow.Controls.Add(volPercentLabel);
        percentRow.Controls.Add(volPercent);
        layout.Controls.Add(percentRow);
        var whenRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        whenRow.Controls.Add(volWhenLabel);
        whenRow.Controls.Add(volWhen);
        layout.Controls.Add(whenRow);
        var okRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        okRow.Controls.Add(ok);
        okRow.Controls.Add(cancel);
        layout.Controls.Add(okRow);
        dlg.Controls.Add(layout);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        return (dlg, logging, (volEnabled, volPercent, volWhen), acceptRelay);
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
