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
/// which job this instance is doing. One person per track: an instance either sends this DAW track,
/// or receives one chosen peer onto it.</para>
///
/// <para><b>Talks to the world only through callbacks.</b> This assembly holds the house accessible
/// controls and nothing else — no Core, no audio engine — so the panel takes its peer list, its job
/// changes and its status text as delegates. Handing it a bridge client directly would drag the whole
/// engine into the shared UI assembly for one screen's worth of text.</para>
/// </summary>
internal sealed class PluginEditorPanel : TableLayoutPanel
{
    private readonly ListBox jobList = new()
    {
        Width = 320,
        Height = 40,
        IntegralHeight = false,
        AccessibleName = "What this plugin does (Alt+W)",
    };

    private readonly ListBox peerList = new()
    {
        Width = 320,
        Height = 80,
        IntegralHeight = false,
        AccessibleName = "Peer to receive from (Alt+P)",
    };

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

    private sealed record PeerEntry(string Address, string Name)
    {
        public override string ToString() => Name;
    }

    /// <summary>Who this instance could receive. Called about once a second, so a peer that appears in
    /// RemSound turns up here without the user having to close and reopen the plugin.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<IReadOnlyList<(string Address, string Name)>>? PeerSource { get; set; }

    /// <summary>Raised when the user changes what this instance does: sending, or receiving the peer
    /// at the given address (null when sending).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<bool, string?>? JobChanged { get; set; }

    /// <summary>The plain-English status line. Read rather than pushed, so the panel never has to be
    /// told about something changing behind it.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<string>? StatusSource { get; set; }

    /// <summary>Is this instance sending its track, rather than receiving a peer?</summary>
    public bool IsSending => jobList.SelectedIndex == 0;

    /// <summary>The address of the chosen peer, or null when sending or when none is chosen.</summary>
    public string? ChosenPeerAddress => peerList.SelectedItem is PeerEntry entry ? entry.Address : null;

    public PluginEditorPanel()
    {
        ColumnCount = 2;
        RowCount = 4;
        AutoSize = true;
        Padding = new Padding(12);
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        // One person per track (Ed, 2026-08-15): an instance either sends this track, or receives
        // ONE peer onto it. Splitting a single machine's devices into separate streams was
        // deliberately dropped — independent jitter buffers would let a guitar and a vocal from the
        // same performance drift apart at the far end, which is worse than latency.
        jobList.Items.Add("Send this track to my peers");
        jobList.Items.Add("Receive one peer onto this track");
        jobList.SelectedIndex = 0;

        peerList.Items.Add(NoPeers);
        peerList.SelectedIndex = 0;

        activeBox.Checked = true;
        statusReadout.Text = "Not connected." + Environment.NewLine + "Latency: not receiving.";

        FormLayoutRows.AddRow(this, 0, "&What this plugin does (Alt+W)", jobList, c => c.Focus());
        FormLayoutRows.AddRow(this, 1, "&Peer to receive from (Alt+P)", peerList, c => c.Focus());
        Controls.Add(activeBox, 0, 2);
        SetColumnSpan(activeBox, 2);
        FormLayoutRows.AddRow(this, 3, "&Status (Alt+S)", statusReadout, c => c.Focus());

        jobList.TabIndex = 0;
        peerList.TabIndex = 1;
        activeBox.TabIndex = 2;
        statusReadout.TabIndex = 3;

        // Receiving is the only job that needs a peer chosen; sending goes to everyone, exactly as
        // the app does today. Kept as enable/disable rather than hide, so the tab order never shifts
        // under a screen-reader user mid-session.
        jobList.SelectedIndexChanged += (_, _) => { UpdatePeerListEnabled(); AnnounceJob(); };
        peerList.SelectedIndexChanged += (_, _) => AnnounceJob();
        // Unticking Active is the user's own bypass: it hands the peer back to RemSound's speakers
        // rather than leaving them nowhere. Same effect as the DAW bypassing the plugin, reachable
        // from inside the window for anyone whose host makes bypass hard to find by keyboard.
        activeBox.CheckedChanged += (_, _) => AnnounceJob();

        refreshTimer.Tick += (_, _) => Refresh(fromTimer: true);
        UpdatePeerListEnabled();
    }

    private const string NoPeers = "(no peers yet — set them up in RemSound)";

    // ---- Test seams. Real controls driven the way a user drives them, so the gate exercises the
    // actual handlers rather than a parallel copy of the logic. -----------------------------------
    internal int PeerItemCountForTest => peerList.Items.Count;
    internal IReadOnlyList<string> PeerLabelsForTest => peerList.Items.Cast<object>().Select(i => i.ToString() ?? "").ToList();
    internal int SelectedPeerIndexForTest => peerList.SelectedIndex;
    internal bool PeerListEnabledForTest => peerList.Enabled;
    internal string StatusTextForTest => statusReadout.Text;
    internal void SelectPeerForTest(int index) => peerList.SelectedIndex = index;
    internal void SelectJobForTest(int index) => jobList.SelectedIndex = index;
    internal void SetActiveForTest(bool active) => activeBox.Checked = active;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Refresh(fromTimer: false);
        refreshTimer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { refreshTimer.Stop(); refreshTimer.Dispose(); }
        base.Dispose(disposing);
    }

    /// <summary>Tell the engine what this instance is doing now. The peer is reported ONLY while this
    /// instance is actually receiving one — switching to sending, or unticking Active, must hand that
    /// person back to RemSound's speakers rather than leaving them claimed by a plugin that is no
    /// longer playing them. Getting this wrong leaves someone mute with nothing on screen to explain
    /// it, which is the worst failure this feature has.</summary>
    private void AnnounceJob()
    {
        var sending = IsSending || !activeBox.Checked;
        JobChanged?.Invoke(sending, sending ? null : ChosenPeerAddress);
    }

    private void UpdatePeerListEnabled() => peerList.Enabled = jobList.SelectedIndex == 1;

    /// <summary>Pull the peer list and the status text. Named and internal rather than an inline
    /// timer lambda so the gate can drive the REAL refresh instead of waiting a second for a tick.</summary>
    internal void Refresh(bool fromTimer)
    {
        var peers = PeerSource?.Invoke() ?? [];
        var chosen = ChosenPeerAddress;

        // Only rebuild when the list has actually CHANGED. Rebuilding under a screen-reader user
        // every second would re-announce the list and move their place in it — the list would be
        // unusable, and the cause would be invisible to anyone testing by eye.
        if (PeerListDiffers(peers))
        {
            peerList.BeginUpdate();
            peerList.Items.Clear();
            if (peers.Count == 0) peerList.Items.Add(NoPeers);
            else foreach (var (address, name) in peers) peerList.Items.Add(new PeerEntry(address, name));
            // Keep the user on the same person if they are still there.
            var restored = false;
            for (var i = 0; i < peerList.Items.Count && chosen is not null; i++)
            {
                if (peerList.Items[i] is not PeerEntry entry || entry.Address != chosen) continue;
                peerList.SelectedIndex = i;
                restored = true;
                break;
            }
            if (!restored && peerList.Items.Count > 0) peerList.SelectedIndex = 0;
            peerList.EndUpdate();
        }

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
