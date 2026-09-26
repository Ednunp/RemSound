using System.Windows.Forms;

namespace RemSound.App;

/// <summary>
/// The RemSound plugin's window — the panel a DAW hosts inside its plugin frame.
///
/// <para>Built as an ordinary WinForms panel, deliberately, and this is the whole accessibility bet:
/// real WinForms controls expose themselves to NVDA for free, whereas most commercial plugin windows
/// are custom-drawn and unreadable — the leading NVDA add-on for them resorts to OCR. If this works,
/// RemSound would be one of the very few plugins a blind engineer can actually operate.</para>
///
/// <para>It can be shown WITHOUT a DAW (<c>--plugin-window</c>) so the controls can be tested with a
/// screen reader before any plugin plumbing exists. That order matters: if it isn't readable as a
/// plain window it will not be readable inside a host. Passing standalone does NOT prove it works in
/// a DAW — keyboard focus across the host boundary is the remaining unknown — but failing standalone
/// would have settled it immediately. Ed tested this window and it read correctly.</para>
///
/// <para>Deliberately SMALL. Password, peers and audio settings stay in the main app (Ed's call:
/// "read the profile from the standalone app... it's far easier"), so the plugin only has to answer
/// what this instance does and how loud each direction is.</para>
///
/// <para><b>Two switches, not a mode.</b> Send and receive are independent tick boxes, so one
/// instance can do both at once and a track needs one plugin rather than two. Both start OFF: an
/// instance that broadcast a track the moment it was inserted would be a nasty surprise, and for a
/// screen-reader user an invisible one.</para>
///
/// <para><b>A checked list, not a chooser.</b> One track can carry several people — "listen to 3
/// people talking while they are listening to your production session" — and making that mean three
/// plugin instances would be the tool getting in the way. The list is therefore checkable, with an
/// "all peers" tick above it for the case where the answer is simply everybody.</para>
///
/// <para><b>Talks to the world only through callbacks.</b> This assembly holds the house accessible
/// controls and nothing else — no Core, no audio engine — so the panel takes its peer list, its job
/// changes and its status text as delegates. Handing it a bridge client directly would drag the whole
/// engine into the shared UI assembly for one screen's worth of text.</para>
/// </summary>
internal sealed class PluginEditorPanel : TableLayoutPanel
{
    private readonly AccessibleCheckBox sendBox = new()
    {
        Text = "Se&nd this track to my peers (Alt+N)",
        AutoSize = true,
        AccessibleName = "Send this track to my peers",
    };

    private readonly AccessibleCheckBox receiveBox = new()
    {
        Text = "&Receive peers onto this track (Alt+R)",
        AutoSize = true,
        AccessibleName = "Receive peers onto this track",
    };

    private readonly AccessibleCheckBox allPeersBox = new()
    {
        Text = "Receive all peers, including whoever &joins later (Alt+J)",
        AutoSize = true,
        AccessibleName = "Receive all peers, including whoever joins later",
    };

    // A CHECKED list, not a plain one. A track can carry several people — three of them talking while
    // you work is the case that asked for this — and a single-selection list would mean one plugin
    // instance per person. NVDA reads a CheckedListBox item as its name plus checked or unchecked, and
    // space toggles it, so the control says everything it needs to without any custom drawing.
    private readonly CheckedListBox peerList = new()
    {
        Width = 320,
        Height = 96,
        IntegralHeight = false,
        CheckOnClick = true,
        AccessibleName = "Peers to receive (Alt+P)",
    };

    // Spin boxes rather than sliders, on purpose. NVDA reads a NumericUpDown's value on every arrow
    // press and the number it reads is the number the engine uses; a TrackBar announces a position on
    // an arbitrary scale and cannot be typed into. In DECIBELS: 0 is unity, which is the number a DAW
    // user recognises, and the bottom of the range is a real off rather than very quiet.
    private readonly NumericUpDown sendLevel = new()
    {
        Width = 90,
        DecimalPlaces = 1,
        Increment = 1m,
        Minimum = MinLevelDb,
        Maximum = MaxLevelDb,
        Value = 0m,
        AccessibleName = "Send level in decibels, 0 is unity (Alt+L)",
    };

    private readonly NumericUpDown receiveLevel = new()
    {
        Width = 90,
        DecimalPlaces = 1,
        Increment = 1m,
        Minimum = MinLevelDb,
        Maximum = MaxLevelDb,
        Value = 0m,
        AccessibleName = "Receive level in decibels, 0 is unity (Alt+V)",
    };

    /// <summary>The range of both level controls, in dB. Mirrors the plugin's own limits; kept here as
    /// plain numbers because this assembly deliberately has no reference to the engine.</summary>
    private const decimal MinLevelDb = -60m;
    private const decimal MaxLevelDb = 12m;

    /// <summary>Who this instance is receiving, as addresses. The panel's own memory rather than a
    /// read of the control, because somebody who disconnects leaves the list and must NOT be forgotten
    /// — they come back to the same track when they return.</summary>
    private readonly HashSet<string> checkedAddresses = new(StringComparer.Ordinal);

    private readonly AccessibleCheckBox activeBox = new()
    {
        Text = "&Active (Alt+A)",
        AutoSize = true,
        AccessibleName = "Active",
    };

    private readonly TextBox statusReadout = new()
    {
        Multiline = true,
        ReadOnly = true,
        TabStop = true,
        Width = 320,
        Height = 60,
        BorderStyle = BorderStyle.FixedSingle,
        AccessibleName = "Plugin status (Alt+S)",
    };

    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 1000 };
    private string lastStatus = "";

    /// <summary>Non-zero while the panel is writing its OWN controls — seeding them from the engine,
    /// or rebuilding the peer list. Setting a tick box, a level, a list item's check or the list's
    /// selection fires the same event a keystroke does, and WinForms gives no way to tell the two apart,
    /// so the difference has to be recorded here. Without it every programmatic write is
    /// indistinguishable from a user decision: a tick or a level would be announced back to the engine
    /// as one, and a rebuild would move the remembered cursor.</summary>
    private int suppressAnnounce;

    /// <summary>Where the keyboard was in the list, by ADDRESS. A rebuild puts the user back on the
    /// same person rather than on the same position: somebody joining or leaving shifts every position
    /// after them, and landing on whoever inherited the number is exactly the confusion this whole
    /// design is trying to avoid.</summary>
    private string? cursorAddress;

    private sealed record PeerEntry(string Address, string Name)
    {
        public override string ToString() => Name;
    }

    /// <summary>Who this instance could receive. Called about once a second, so a peer that appears in
    /// RemSound turns up here without the user having to close and reopen the plugin.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<IReadOnlyList<(string Address, string Name)>>? PeerSource { get; set; }

    /// <summary>Raised when the user changes anything about what this instance does: Active, then the CHOICES - either
    /// direction, who is ticked, whether to take all peers, either level in dB - reported as chosen whatever Active and
    /// Receive say. The engine decides what runs. The choices used to be reported masked (nobody, neither direction,
    /// while Active was off), and the engine kept that as the choice: close the window and they were gone
    /// (review 2026-09-25).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<bool, bool, bool, IReadOnlyList<string>, bool, float, float>? JobChanged { get; set; }

    /// <summary>The plain-English status line. Read rather than pushed, so the panel never has to be
    /// told about something changing behind it.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<string>? StatusSource { get; set; }

    /// <summary>What the instance is ALREADY doing, read once as the panel comes up.
    ///
    /// <para>A panel is built fresh every time the DAW opens the window, and it used to be born on its
    /// own defaults and then announce those defaults back at the engine as though the user had chosen
    /// them. Opening the window therefore threw away whatever the instance was doing: the peer was
    /// released and the job flipped, about 20 ms before the window was even shown (Anthony Reyers,
    /// 2026-08-28, five times out of five in one session). The panel must open showing the truth, not
    /// asserting a default.</para></summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<(bool Send, bool Receive, IReadOnlyList<string> Peers, bool AllPeers, bool Active, float SendLevelDb, float ReceiveLevelDb)>? InitialStateSource { get; set; }

    /// <summary>Is this instance sending its track?</summary>
    public bool SendChecked => sendBox.Checked;

    /// <summary>Is this instance receiving a peer onto its track?</summary>
    public bool ReceiveChecked => receiveBox.Checked;

    /// <summary>The addresses this instance is receiving, including anybody who is currently
    /// disconnected.</summary>
    public IReadOnlyList<string> ChosenPeerAddresses => [.. checkedAddresses];

    /// <summary>Take everybody, and follow the session as people come and go.</summary>
    public bool AllPeersChecked => allPeersBox.Checked;

    public PluginEditorPanel()
    {
        ColumnCount = 2;
        RowCount = 9;
        AutoSize = true;
        Padding = new Padding(12);
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        peerList.Items.Add(NoPeers);
        peerList.SelectedIndex = 0;

        activeBox.Checked = true;
        // Placeholder until the first real status arrives. It used to read "Latency: not receiving",
        // naming a quantity this panel never reports — nothing else here mentions latency and the
        // live wording (RemSoundPlugin.DescribeStatus) doesn't either. 2026-08-23.
        statusReadout.Text = "Not connected." + Environment.NewLine + "Doing nothing yet.";

        Controls.Add(sendBox, 0, 0);
        SetColumnSpan(sendBox, 2);
        Controls.Add(receiveBox, 0, 1);
        SetColumnSpan(receiveBox, 2);
        Controls.Add(allPeersBox, 0, 2);
        SetColumnSpan(allPeersBox, 2);
        FormLayoutRows.AddRow(this, 3, "&Peers to receive (Alt+P)", peerList, c => c.Focus());
        FormLayoutRows.AddRow(this, 4, "Send &level in dB (Alt+L)", sendLevel, c => c.Focus());
        FormLayoutRows.AddRow(this, 5, "Receive le&vel in dB (Alt+V)", receiveLevel, c => c.Focus());
        Controls.Add(activeBox, 0, 6);
        SetColumnSpan(activeBox, 2);
        FormLayoutRows.AddRow(this, 7, "&Status (Alt+S)", statusReadout, c => c.Focus());
        Controls.Add(helpButton, 0, 8);
        SetColumnSpan(helpButton, 2);

        sendBox.TabIndex = 0;
        receiveBox.TabIndex = 1;
        allPeersBox.TabIndex = 2;
        peerList.TabIndex = 3;
        sendLevel.TabIndex = 4;
        receiveLevel.TabIndex = 5;
        activeBox.TabIndex = 6;
        statusReadout.TabIndex = 7;
        helpButton.TabIndex = 8;

        // Context help (2026-09-25). Inside a DAW the host runs the message loop, so WinForms never sees a key before
        // the control does: F1 is caught on each control. Where the host keeps F1 for itself, the button shows the lot.
        ContextHelp.Mark(sendBox, "plugin.send");
        ContextHelp.Mark(receiveBox, "plugin.receive");
        ContextHelp.Mark(allPeersBox, "plugin.receive-all");
        ContextHelp.Mark(peerList, "plugin.peers");
        ContextHelp.Mark(sendLevel, "plugin.send-level");
        ContextHelp.Mark(receiveLevel, "plugin.receive-level");
        ContextHelp.Mark(activeBox, "plugin.active");
        ContextHelp.Mark(statusReadout, "plugin.status");
        ContextHelp.Mark(helpButton, "plugin.help-button");
        foreach (var control in new Control[] { sendBox, receiveBox, allPeersBox, peerList, sendLevel, receiveLevel, activeBox, statusReadout, helpButton })
            control.KeyDown += OnHelpKey;
        helpButton.Click += (_, _) => ContextHelp.ShowWhole(helpButton, "Plugin window", "plugin.window", WindowHelpKeys);

        // Only receiving needs a peer chosen; sending goes to everyone, exactly as the app does today.
        // Kept as enable/disable rather than hide, so the tab order never shifts under a screen-reader
        // user mid-session.
        sendBox.CheckedChanged += (_, _) => AnnounceJob();
        receiveBox.CheckedChanged += (_, _) => { UpdatePeerListEnabled(); AnnounceJob(); };
        allPeersBox.CheckedChanged += (_, _) => { UpdatePeerListEnabled(); AnnounceJob(); };
        // ItemCheck, not ItemCheckChanged: WinForms raises it BEFORE the item's state has moved, so the
        // new value comes from the event and the announcement is deferred to the message loop, by which
        // time the control agrees with us. Moving the SELECTION announces nothing — it is only the
        // cursor, and a list where arrowing past somebody put them on your track would be a trap.
        peerList.ItemCheck += (_, e) =>
        {
            if (suppressAnnounce > 0) return;
            if (e.Index < 0 || e.Index >= peerList.Items.Count) return;
            if (peerList.Items[e.Index] is not PeerEntry entry) return;
            if (e.NewValue == CheckState.Checked) checkedAddresses.Add(entry.Address);
            else checkedAddresses.Remove(entry.Address);
            // Deferred so the control agrees with us by the time it is read, and guarded because a
            // host can close the window between the tick and the invoke - an exception on the way out
            // of a plugin window is a crashed DAW, not a logged warning.
            try
            {
                if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(AnnounceJob));
                else AnnounceJob();
            }
            catch (ObjectDisposedException) { /* the window went away underneath the tick */ }
            catch (InvalidOperationException) { /* ...or its handle did */ }
        };
        peerList.SelectedIndexChanged += (_, _) =>
        {
            if (suppressAnnounce > 0) return;
            cursorAddress = peerList.SelectedItem is PeerEntry entry ? entry.Address : null;
        };
        sendLevel.ValueChanged += (_, _) => AnnounceJob();
        receiveLevel.ValueChanged += (_, _) => AnnounceJob();
        // Unticking Active is the user's own bypass: it hands the peer back to RemSound's speakers and
        // stops the track going out, without forgetting which directions were on. Same effect as the
        // DAW bypassing the plugin, reachable from inside the window for anyone whose host makes
        // bypass hard to find by keyboard.
        activeBox.CheckedChanged += (_, _) => AnnounceJob();

        refreshTimer.Tick += (_, _) => Refresh(fromTimer: true);
        UpdatePeerListEnabled();
    }

    private const string NoPeers = "(no peers yet — set them up in RemSound)";

    private readonly Button helpButton = new()
    {
        Text = "Context &help (Alt+H)",
        AccessibleName = "Context help",
        AutoSize = true,
    };

    /// <summary>The window's controls' help, in tab order: what the Context help button shows.</summary>
    internal static readonly string[] WindowHelpKeys =
    [
        "plugin.send", "plugin.receive", "plugin.receive-all", "plugin.peers", "plugin.send-level", "plugin.receive-level",
        "plugin.active", "plugin.status", "plugin.help-button",
    ];

    /// <summary>F1 on a control: its context help. Shift+F1: the whole manual.</summary>
    private void OnHelpKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.F1 || sender is not Control control) return;
        if (e.Modifiers == Keys.None) ContextHelp.Show(control);
        else if (e.Modifiers == Keys.Shift) ContextHelp.OpenManualInBrowser?.Invoke(null);
        else return;
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    internal Button HelpButtonForTest => helpButton;

    // ---- Test seams. Real controls driven the way a user drives them, so the gate exercises the
    // actual handlers rather than a parallel copy of the logic. -----------------------------------
    internal int PeerItemCountForTest => peerList.Items.Count;
    internal IReadOnlyList<string> PeerLabelsForTest => peerList.Items.Cast<object>().Select(i => i.ToString() ?? "").ToList();
    internal int SelectedPeerIndexForTest => peerList.SelectedIndex;
    internal bool PeerListEnabledForTest => peerList.Enabled;
    internal string StatusTextForTest => statusReadout.Text;
    internal void SelectPeerForTest(int index) => peerList.SelectedIndex = index;
    internal void SetPeerCheckedForTest(int index, bool on) => peerList.SetItemChecked(index, on);
    internal bool PeerCheckedForTest(int index) => peerList.GetItemChecked(index);
    internal IReadOnlyList<string> CheckedPeersForTest => ChosenPeerAddresses;
    internal void SetAllPeersForTest(bool on) => allPeersBox.Checked = on;
    internal void SetSendForTest(bool on) => sendBox.Checked = on;
    internal void SetReceiveForTest(bool on) => receiveBox.Checked = on;
    internal void SetSendLevelForTest(float db) => sendLevel.Value = ClampLevel(db);
    internal void SetReceiveLevelForTest(float db) => receiveLevel.Value = ClampLevel(db);
    internal decimal LevelMinimumForTest => sendLevel.Minimum;
    internal decimal LevelMaximumForTest => sendLevel.Maximum;
    internal void SetActiveForTest(bool active) => activeBox.Checked = active;

    /// <summary>How many times the peer list has actually been cleared and repopulated.
    ///
    /// <para>Here because the screen-reader rule cannot be checked by its OUTCOME. The gate asserted
    /// that an unchanged refresh left the selected index where it was — but the rebuild restores the
    /// selection by address afterwards, so the index lands back in the right place whether or not the
    /// list was rebuilt, and forcing a rebuild every tick left the step green (2026-08-24). The harm
    /// is the rebuild itself: NVDA re-announces the list every time it is repopulated, once a second,
    /// which makes the control unusable and looks perfectly fine to anyone testing by eye.</para></summary>
    internal int PeerListRebuildsForTest { get; private set; }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Seed BEFORE the first refresh. The refresh rebuilds the peer list, and that rebuild is what
        // used to announce the panel's defaults; seeding first means the rebuild has the user's real
        // job and peer to preserve rather than a default to assert.
        ApplyInitialState();
        Refresh(fromTimer: false);
        refreshTimer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { refreshTimer.Stop(); refreshTimer.Dispose(); }
        base.Dispose(disposing);
    }

    /// <summary>Tell the engine what the person has chosen, and whether it is Active. The engine hands a person back to
    /// RemSound's speakers the moment this instance stops receiving them - unticking Receive, or unticking Active - so
    /// nobody is left claimed by a plugin that is no longer playing them; but the choice itself is reported as it
    /// stands, so switching back on puts back exactly what was there.</summary>
    private void AnnounceJob()
    {
        if (suppressAnnounce > 0) return;
        JobChanged?.Invoke(activeBox.Checked, sendBox.Checked, receiveBox.Checked, ChosenPeerAddresses, allPeersBox.Checked,
                           (float)sendLevel.Value, (float)receiveLevel.Value);
    }

    /// <summary>Show what the instance is already doing, before anything is announced. Runs under the
    /// suppression flag: these writes are the panel catching up with the engine, and reporting them
    /// back as user choices is the whole bug this exists to stop.</summary>
    private void ApplyInitialState()
    {
        if (InitialStateSource?.Invoke() is not { } state) return;
        suppressAnnounce++;
        try
        {
            sendBox.Checked = state.Send;
            receiveBox.Checked = state.Receive;
            allPeersBox.Checked = state.AllPeers;
            activeBox.Checked = state.Active;
            sendLevel.Value = ClampLevel(state.SendLevelDb);
            receiveLevel.Value = ClampLevel(state.ReceiveLevelDb);
            checkedAddresses.Clear();
            foreach (var address in state.Peers) checkedAddresses.Add(address);
            UpdatePeerListEnabled();
        }
        finally { suppressAnnounce--; }
    }

    /// <summary>A NumericUpDown throws if it is handed a value outside its own range, and a level
    /// arriving from a saved project is outside our control. Clamped rather than trusted: an exception
    /// here would take the window down as it opens.</summary>
    private static decimal ClampLevel(float value) => Math.Clamp((decimal)value, MinLevelDb, MaxLevelDb);

    /// <summary>The list is for choosing individuals, so it is off when there is nobody to choose
    /// between — either the instance is not receiving, or it is taking everybody. Disabled rather than
    /// hidden, so the tab order never shifts under a screen-reader user mid-session.</summary>
    private void UpdatePeerListEnabled() => peerList.Enabled = receiveBox.Checked && !allPeersBox.Checked;

    /// <summary>Pull the peer list and the status text. Named and internal rather than an inline
    /// timer lambda so the gate can drive the REAL refresh instead of waiting a second for a tick.</summary>
    internal void Refresh(bool fromTimer)
    {
        var peers = PeerSource?.Invoke() ?? [];

        // Only rebuild when the list has actually CHANGED. Rebuilding under a screen-reader user
        // every second would re-announce the list and move their place in it — the list would be
        // unusable, and the cause would be invisible to anyone testing by eye.
        if (PeerListDiffers(peers))
        {
            PeerListRebuildsForTest++;
            // Every write below raises an event exactly as a keystroke does. Suppressed, because none
            // of them is the user changing anything: they are the list being rebuilt underneath
            // choices that have not moved.
            suppressAnnounce++;
            try
            {
                peerList.BeginUpdate();
                peerList.Items.Clear();
                if (peers.Count == 0) peerList.Items.Add(NoPeers);
                else foreach (var (address, name) in peers) peerList.Items.Add(new PeerEntry(address, name));

                // The ticks come from OUR memory, not from the control that was just cleared, so
                // somebody who dropped off and came back is still on the track they were put on.
                for (var i = 0; i < peerList.Items.Count; i++)
                {
                    if (peerList.Items[i] is not PeerEntry entry) continue;
                    peerList.SetItemChecked(i, checkedAddresses.Contains(entry.Address));
                }

                // Keep the keyboard on the same person if they are still there, rather than on the
                // same position, which is now somebody else.
                var restored = false;
                for (var i = 0; i < peerList.Items.Count && cursorAddress is not null; i++)
                {
                    if (peerList.Items[i] is not PeerEntry entry || entry.Address != cursorAddress) continue;
                    peerList.SelectedIndex = i;
                    restored = true;
                    break;
                }
                if (!restored && peerList.Items.Count > 0) peerList.SelectedIndex = 0;
                cursorAddress = peerList.SelectedItem is PeerEntry cursor ? cursor.Address : null;
                peerList.EndUpdate();
            }
            finally { suppressAnnounce--; }

            // NOTHING is announced. A rebuild only ever changes who is on SCREEN; who is on the track
            // is held by address and is unaffected by somebody joining or leaving, which is the whole
            // reason it is held that way. The engine notices a peer coming or going on its own.
        }

        // Only ever write the control when the TEXT has actually changed. Rewriting it resets keyboard
        // focus to the first field in the window, so a status line carrying a live counter would throw
        // the user back to the start every second — which is precisely what happened in the first
        // build. The guard is here, and the text upstream is kept stable, because either alone would
        // let the fault back in.
        var status = StatusSource?.Invoke();
        if (string.IsNullOrEmpty(status) || status == lastStatus) return;
        lastStatus = status;
        statusReadout.Text = status;
    }

    private bool PeerListDiffers(IReadOnlyList<(string Address, string Name)> peers)
    {
        if (peers.Count == 0) return peerList.Items.Count != 1 || peerList.Items[0] as string != NoPeers;
        if (peerList.Items.Count != peers.Count) return true;
        for (var i = 0; i < peers.Count; i++)
        {
            if (peerList.Items[i] is not PeerEntry entry) return true;
            if (entry.Address != peers[i].Address || entry.Name != peers[i].Name) return true;
        }
        return false;
    }

    /// <summary>The window icon, injected by whoever hosts the panel. Kept as a property rather than a
    /// reference to the app's Theme so this assembly stays dependency-free (see the project file).</summary>
    internal static Icon? WindowIcon { get; set; }

    /// <summary>Show the panel in a plain window, with no DAW involved, so the controls can be tested
    /// with a screen reader. Test seam only — the real plugin parents this panel into the host's
    /// frame.</summary>
    public static void ShowStandalone(Action<PluginEditorPanel>? wire = null)
    {
        using var form = new Form
        {
            Text = "RemSound plugin window (test)",
            AccessibleRole = AccessibleRole.Dialog,
            AccessibleName = "RemSound plugin window",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            StartPosition = FormStartPosition.CenterScreen,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            KeyPreview = true,
            Icon = WindowIcon,
        };
        var panel = new PluginEditorPanel { Dock = DockStyle.Fill };
        wire?.Invoke(panel);
        var close = new Button { Text = "&Close", AutoSize = true, DialogResult = DialogResult.OK, AccessibleName = "Close" };
        var root = new TableLayoutPanel { ColumnCount = 1, RowCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        root.Controls.Add(panel, 0, 0);
        root.Controls.Add(close, 0, 1);
        form.Controls.Add(root);
        form.AcceptButton = close;
        form.CancelButton = close;
        form.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) form.Close(); };
        form.ShowDialog();
    }
}
