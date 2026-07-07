using RemSound.Core;

namespace RemSound.App;

/// <summary>Modal dialog for adding one parametric EQ band. The user sets a start frequency, an end
/// frequency and a gain; all three are spin-or-type boxes that refuse non-numbers and clamp to range.
/// While the dialog is open it previews the in-progress band live (so the peer's sound changes as the
/// values move); OK keeps the band, Cancel/Escape drops it and reverts the preview.</summary>
internal sealed class AddBandDialog : Form
{
    private readonly NumericUpDown startFreq = new()
    {
        Minimum = (decimal)PeerEqBands.ParametricMinHz,
        Maximum = (decimal)PeerEqBands.ParametricMaxHz,
        Value = 200,
        Increment = 10,
        DecimalPlaces = 0,
        Width = 120,
        TextAlign = HorizontalAlignment.Right,
        AccessibleName = "Start frequency in Hertz (Alt+S)",
    };

    private readonly NumericUpDown endFreq = new()
    {
        Minimum = (decimal)PeerEqBands.ParametricMinHz,
        Maximum = (decimal)PeerEqBands.ParametricMaxHz,
        Value = 2000,
        Increment = 10,
        DecimalPlaces = 0,
        Width = 120,
        TextAlign = HorizontalAlignment.Right,
        AccessibleName = "End frequency in Hertz (Alt+E)",
    };

    private readonly NumericUpDown gainDb = new()
    {
        Minimum = -(decimal)PeerEqBands.MaxGainDb,
        Maximum = (decimal)PeerEqBands.MaxGainDb,
        Value = 3,
        Increment = 1,
        DecimalPlaces = 0,
        Width = 120,
        TextAlign = HorizontalAlignment.Right,
        AccessibleName = "Gain in dB (Alt+G)",
    };

    private readonly Action<ParametricBand?>? livePreview;
    private bool accepted;

    /// <summary>The band the user built, valid only when <see cref="Form.ShowDialog()"/> returned OK.</summary>
    public ParametricBand Result => new()
    {
        StartHz = (float)startFreq.Value,
        EndHz = (float)endFreq.Value,
        GainDb = (float)gainDb.Value,
    };

    /// <param name="livePreview">Called with the in-progress band on every value change so the caller
    /// can apply it to the peer in real time, and with null when the dialog is cancelled/closed so the
    /// caller reverts to the saved shaping.</param>
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

        var okButton = new Button { Text = "OK", AutoSize = true, DialogResult = DialogResult.None };
        var cancelButton = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        okButton.Click += (_, _) => TryAccept();

        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
        };
        buttonRow.Controls.Add(cancelButton);
        buttonRow.Controls.Add(okButton);
        grid.Controls.Add(buttonRow, 1, 3);

        Controls.Add(grid);
        AcceptButton = okButton;
        CancelButton = cancelButton;

        startFreq.ValueChanged += (_, _) => Preview();
        endFreq.ValueChanged += (_, _) => Preview();
        gainDb.ValueChanged += (_, _) => Preview();

        Shown += (_, _) => { startFreq.Focus(); Preview(); };
    }

    // The mnemonic hint is embedded in each NumericUpDown's AccessibleName; the label carries the
    // visible '&' so Alt+letter moves focus to the box (NumericUpDown has no '&' of its own).
    private static void AddRow(TableLayoutPanel grid, int row, string labelText, NumericUpDown box)
    {
        var label = new Label { Text = labelText, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) };
        // A plain Label with '&' wires the mnemonic to the next control in tab order (the box).
        grid.Controls.Add(label, 0, row);
        grid.Controls.Add(box, 1, row);
    }

    private ParametricBand Current() => new()
    {
        StartHz = (float)startFreq.Value,
        EndHz = (float)endFreq.Value,
        GainDb = (float)gainDb.Value,
    };

    private void Preview() => livePreview?.Invoke(Current());

    private void TryAccept()
    {
        if (endFreq.Value <= startFreq.Value)
        {
            var page = new TaskDialogPage
            {
                Caption = "Add EQ band",
                Heading = "End frequency must be higher than start",
                Text = $"The end frequency ({endFreq.Value:0} Hz) must be higher than the start frequency ({startFreq.Value:0} Hz). Adjust one of them and try again.",
                Icon = TaskDialogIcon.Warning,
                Buttons = { TaskDialogButton.OK },
            };
            TaskDialog.ShowDialog(this, page);
            endFreq.Focus();
            return;
        }
        accepted = true;
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (!accepted) livePreview?.Invoke(null);   // revert the preview on cancel / close
    }
}
