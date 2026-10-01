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
        Width = Dpi.Px(120),
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
        Width = Dpi.Px(120),
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
        Width = Dpi.Px(120),
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
        // Sized to what it holds, with OK and Cancel on one row at the bottom right (UI audit 2026-09-28, findings 5 and 15):
        // a fixed 340 x 200 cut all three number boxes at 150 %, and the buttons were stacked in the middle.
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = Dpi.Pad(12),
            ColumnCount = 2,
            RowCount = 5,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var heading = Theme.Heading("Add EQ band");
        grid.Controls.Add(heading, 0, 0);
        grid.SetColumnSpan(heading, 2);

        AddRow(grid, 1, "&Start frequency in Hz (Alt+S):", startFreq);
        AddRow(grid, 2, "&End frequency in Hz (Alt+E):", endFreq);
        AddRow(grid, 3, "&Gain in dB (Alt+G):", gainDb);

        var okButton = new Button { Text = "&OK", AutoSize = true, DialogResult = DialogResult.None, TabIndex = 0 };
        var cancelButton = new Button { Text = "&Cancel", AutoSize = true, DialogResult = DialogResult.Cancel, TabIndex = 1 };
        okButton.Click += (_, _) => TryAccept();

        // OK before Cancel, in both tab order and left-to-right layout.
        var buttonRow = new FlowLayoutPanel
        {
            Anchor = AnchorStyles.Right,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            Margin = Dpi.Pad(0, 8, 0, 0),
            TabIndex = 3,
        };
        buttonRow.Controls.Add(okButton);
        buttonRow.Controls.Add(cancelButton);
        grid.Controls.Add(buttonRow, 0, 4);
        grid.SetColumnSpan(buttonRow, 2);

        Controls.Add(grid);
        AcceptButton = okButton;
        CancelButton = cancelButton;
        ContextHelp.Mark(startFreq, "dialog.add-eq-band.start");
        ContextHelp.Mark(endFreq, "dialog.add-eq-band.end");
        ContextHelp.Mark(gainDb, "dialog.add-eq-band.gain");
        ContextHelp.Mark(okButton, "dialog.add-eq-band.ok");
        ContextHelp.Mark(cancelButton, "dialog.add-eq-band.cancel");

        foreach (var box in new[] { startFreq, endFreq, gainDb })
        {
            box.ValueChanged += (_, _) => Preview();
            box.TextChanged += (_, _) => Preview();
            // Select the whole value when the box gains focus, so a typed number REPLACES what's there
            // instead of being inserted next to it (which parsed as junk and reverted). Deferred so it
            // runs after focus settles.
            box.Enter += (_, _) => box.BeginInvoke(() => box.Select(0, box.Text.Length));
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
    // A MnemonicLabel aimed at its own box, not a plain Label: a plain label sends Alt+letter to the NEXT
    // control in tab order, and the boxes' explicit tab order put each label before the box of the row
    // BELOW. Alt+S landed on End frequency, Alt+E on Gain and Alt+G on OK. 2026-09-13 review.
    private static void AddRow(TableLayoutPanel grid, int row, string labelText, NumericUpDown box)
    {
        var label = new MnemonicLabel { Text = labelText, AutoSize = true, Anchor = AnchorStyles.Left, Margin = Dpi.Pad(0, 3, 8, 3), MnemonicTarget = box };
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
        AppTaskDialog.ShowDialog(this, page);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (!accepted) livePreview?.Invoke(null);   // revert the preview on cancel / close
    }
}
