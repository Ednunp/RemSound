using System.Globalization;
using RemSound.Core;

namespace RemSound.App;

/// <summary>Modal dialog for adding one parametric EQ band. The user sets a start frequency, an end
/// frequency and a gain; all three are spin-or-type boxes that refuse non-numbers and clamp to range.
/// The two frequency boxes start EMPTY (no prepopulated value to mislead the user); the gain defaults
/// to a gentle boost. While the dialog is open it previews the in-progress band live (so the peer's
/// sound changes as the values move); OK keeps the band, Cancel/Escape drops it and reverts.</summary>
internal sealed class AddBandDialog : Form
{
    private readonly NumericUpDown startFreq = new()
    {
        Minimum = (decimal)PeerEqBands.ParametricMinHz,
        Maximum = (decimal)PeerEqBands.ParametricMaxHz,
        Value = (decimal)PeerEqBands.ParametricMinHz,
        Increment = 10,
        DecimalPlaces = 0,
        Width = 120,
        TextAlign = HorizontalAlignment.Right,
        TabIndex = 0,
        AccessibleName = "Start frequency in Hertz (Alt+S)",
    };

    private readonly NumericUpDown endFreq = new()
    {
        Minimum = (decimal)PeerEqBands.ParametricMinHz,
        Maximum = (decimal)PeerEqBands.ParametricMaxHz,
        Value = (decimal)PeerEqBands.ParametricMinHz,
        Increment = 10,
        DecimalPlaces = 0,
        Width = 120,
        TextAlign = HorizontalAlignment.Right,
        TabIndex = 1,
        AccessibleName = "End frequency in Hertz (Alt+E)",
    };

    private readonly NumericUpDown gainDb = new()
    {
        Minimum = -(decimal)PeerEqBands.MaxGainDb,
        Maximum = (decimal)PeerEqBands.MaxGainDb,
        Value = 3,
        Increment = 0.5m,       // allow half-dB steps; typing any value like 1.5 works too
        DecimalPlaces = 1,
        Width = 120,
        TextAlign = HorizontalAlignment.Right,
        TabIndex = 2,
        AccessibleName = "Gain in dB (Alt+G)",
    };

    private readonly Action<ParametricBand?>? livePreview;
    private ParametricBand? resultBand;
    private bool accepted;

    /// <summary>The band the user built, valid only when <see cref="Form.ShowDialog()"/> returned OK.</summary>
    public ParametricBand Result => resultBand ?? new ParametricBand();

    /// <param name="livePreview">Called with the in-progress band on every value change so the caller
    /// can apply it to the peer in real time, and with null when the band is incomplete or the dialog
    /// is cancelled/closed so the caller reverts to the saved shaping.</param>
    public AddBandDialog(Action<ParametricBand?>? livePreview = null)
    {
        this.livePreview = livePreview;

        Text = "Add EQ band";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(340, 200);

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 2,
            RowCount = 4,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        AddRow(grid, 0, "&Start frequency in Hz (Alt+S)", startFreq);
        AddRow(grid, 1, "&End frequency in Hz (Alt+E)", endFreq);
        AddRow(grid, 2, "&Gain in dB (Alt+G)", gainDb);

        var okButton = new Button { Text = "OK", AutoSize = true, DialogResult = DialogResult.None, TabIndex = 0 };
        var cancelButton = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel, TabIndex = 1 };
        okButton.Click += (_, _) => TryAccept();

        // OK before Cancel, in both tab order and left-to-right layout.
        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            TabIndex = 3,
        };
        buttonRow.Controls.Add(okButton);
        buttonRow.Controls.Add(cancelButton);
        grid.Controls.Add(buttonRow, 1, 3);

        Controls.Add(grid);
        AcceptButton = okButton;
        CancelButton = cancelButton;

        foreach (var box in new[] { startFreq, endFreq, gainDb })
        {
            box.ValueChanged += (_, _) => Preview();
            box.TextChanged += (_, _) => Preview();
        }

        Shown += (_, _) =>
        {
            // Blank the two frequency boxes so nothing is prepopulated; the user must enter both.
            startFreq.Text = "";
            endFreq.Text = "";
            startFreq.Focus();
        };
    }

    // The mnemonic hint is embedded in each NumericUpDown's AccessibleName; the label carries the
    // visible '&' so Alt+letter moves focus to the box (NumericUpDown has no '&' of its own).
    private static void AddRow(TableLayoutPanel grid, int row, string labelText, NumericUpDown box)
    {
        var label = new Label { Text = labelText, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) };
        grid.Controls.Add(label, 0, row);
        grid.Controls.Add(box, 1, row);
    }

    private static bool TryReadHz(NumericUpDown box, out float hz)
    {
        hz = 0f;
        var t = box.Text.Trim();
        if (t.Length == 0) return false;
        if (!float.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out hz)) return false;
        return hz >= PeerEqBands.ParametricMinHz && hz <= PeerEqBands.ParametricMaxHz;
    }

    private bool TryReadDb(out float db)
    {
        db = 0f;
        var t = gainDb.Text.Trim();
        if (t.Length == 0) return false;
        if (!float.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out db)) return false;
        db = Math.Clamp(db, -PeerEqBands.MaxGainDb, PeerEqBands.MaxGainDb);
        return true;
    }

    private void Preview()
    {
        if (TryReadHz(startFreq, out float s) && TryReadHz(endFreq, out float e) && e > s)
            livePreview?.Invoke(new ParametricBand { StartHz = s, EndHz = e, GainDb = TryReadDb(out float d) ? d : 0f });
        else
            livePreview?.Invoke(null);   // incomplete / invalid → nothing to preview
    }

    private void TryAccept()
    {
        if (!TryReadHz(startFreq, out float s) || !TryReadHz(endFreq, out float e))
        {
            Warn("Enter both frequencies",
                $"Enter a start and an end frequency, each between {PeerEqBands.ParametricMinHz:0} and {PeerEqBands.ParametricMaxHz:0} Hz.");
            startFreq.Focus();
            return;
        }
        if (e <= s)
        {
            Warn("End frequency must be higher than start",
                $"The end frequency ({e:0} Hz) must be higher than the start frequency ({s:0} Hz). Adjust one of them and try again.");
            endFreq.Focus();
            return;
        }
        resultBand = new ParametricBand { StartHz = s, EndHz = e, GainDb = TryReadDb(out float d) ? d : 0f };
        accepted = true;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Warn(string heading, string text)
    {
        var page = new TaskDialogPage
        {
            Caption = "Add EQ band",
            Heading = heading,
            Text = text,
            Icon = TaskDialogIcon.Warning,
            Buttons = { TaskDialogButton.OK },
        };
        TaskDialog.ShowDialog(this, page);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (!accepted) livePreview?.Invoke(null);   // revert the preview on cancel / close
    }
}
