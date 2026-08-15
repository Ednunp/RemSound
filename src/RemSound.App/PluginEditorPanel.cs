using System.Windows.Forms;

namespace RemSound.App;

/// <summary>
/// The RemSound plugin's window — the panel a DAW will host inside its plugin frame.
///
/// <para>Built as an ordinary WinForms panel, deliberately, and this is the whole accessibility bet:
/// real WinForms controls expose themselves to NVDA for free, whereas most commercial plugin windows
/// are custom-drawn and unreadable — the leading NVDA add-on for them resorts to OCR. If this works,
/// RemSound would be one of the very few plugins a blind engineer can actually operate.</para>
///
/// <para>It can be shown WITHOUT a DAW (<c>--plugin-window</c>) so the controls can be tested with a
/// screen reader before any plugin plumbing exists. That order matters: if it isn't readable as a
/// plain window it will not be readable inside a host, and there is no point building the rest.
/// Passing standalone does NOT prove it works in a DAW — keyboard focus across the host boundary is
/// the remaining unknown — but failing standalone would settle it immediately.</para>
///
/// <para>Deliberately SMALL. Password, peers and audio settings stay in the main app (Ed's call:
/// "read the profile from the standalone app... it's far easier"), so the plugin only has to answer
/// which job this instance is doing. One person per track: an instance either sends this DAW track,
/// or receives one chosen peer onto it.</para>
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

        // Populated live from the app's peer list; sample entries here so the window can be tested
        // for readability before any of the plumbing exists.
        peerList.Items.Add("(no peers yet — set them up in RemSound)");
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
        jobList.SelectedIndexChanged += (_, _) => UpdatePeerListEnabled();
        UpdatePeerListEnabled();
    }

    private void UpdatePeerListEnabled() => peerList.Enabled = jobList.SelectedIndex == 1;

    /// <summary>Show the panel in a plain window, with no DAW involved, so the controls can be tested
    /// with NVDA. Test seam only — the real plugin parents this panel into the host's frame.</summary>
    public static void ShowStandalone()
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
            Icon = Theme.AppIcon,
        };
        var panel = new PluginEditorPanel { Dock = DockStyle.Fill };
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
