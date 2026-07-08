using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Recording settings dialog. Up to five listboxes laid out left-to-right:
///   * Recording &source (Alt+S)            — what audio gets captured
///   * File &format (Alt+F)                 — WAV / MP3 / OGG-Opus / FLAC
///   * Audio format &attributes (Alt+A)     — bit depth (WAV/FLAC) or bitrate (MP3/Ogg)
///   * FLAC compression &level (Alt+L)      — 0..8; visible only when FLAC is selected
///   * &Channels (Alt+C)                    — Stereo / Mono
///
/// Channel mode was originally folded into every attribute row (16-bit stereo / 16-bit
/// mono / 24-bit stereo / 24-bit mono / …) which doubled the attribute count for no real
/// benefit. Pulling it out into its own listbox keeps each list focused on one decision.
///
/// FLAC has TWO independent quality knobs (bit depth + compression level); the bit depth
/// stays in the attributes column, the compression level gets its own conditional column
/// that only appears when FLAC is selected. Compression level 0 = fastest encode and
/// biggest file; 8 = slowest and smallest. The libFLAC reference default is 5, which is
/// where the slider defaults if nothing's been set. All levels produce bit-identical
/// audio — it's a pure encode-time-vs-file-size tradeoff.
///
/// The attribute and compression lists repopulate whenever the file-format selection
/// changes, so the user always sees only the choices that make sense for the format.
///
/// Settings are written back to the profile only when the user presses OK. Cancel / Esc
/// discards. The dialog also exposes <see cref="ChangedAnything"/> so the caller can
/// MarkProfileDirty after a successful OK.
///
/// Reachable from the Record menu → "Recording settings...".
/// </summary>
internal sealed class RecordingSettingsDialog : Form
{
    private readonly RecordingSettings working;  // mutated as the user interacts

    private readonly Label sourceLabel = new()
    {
        Text = "Recording &source (Alt+S):",
        AutoSize = true,
        Padding = new Padding(0, 0, 0, 4),
    };

    private readonly ListBox sourceList = new()
    {
        AccessibleName = "Recording source",
        SelectionMode = SelectionMode.One,
        IntegralHeight = false,
        Height = 120,
    };

    private readonly Label formatLabel = new()
    {
        Text = "File &format (Alt+F):",
        AutoSize = true,
        Padding = new Padding(0, 0, 0, 4),
    };

    private readonly ListBox formatList = new()
    {
        AccessibleName = "File format",
        SelectionMode = SelectionMode.One,
        IntegralHeight = false,
        Height = 120,
    };

    private readonly Label attributesLabel = new()
    {
        Text = "Audio format &attributes (Alt+A):",
        AutoSize = true,
        Padding = new Padding(0, 0, 0, 4),
    };

    private readonly ListBox attributesList = new()
    {
        AccessibleName = "Audio format attributes",
        SelectionMode = SelectionMode.One,
        IntegralHeight = false,
        Height = 200,
    };

    // FLAC compression level. Wrapped in its own column that toggles visibility on the
    // current file-format selection — visible for FLAC, hidden otherwise. Held as fields
    // so the column-visibility refresh in <see cref="UpdateFlacCompressionVisibility"/>
    // can flip them together.
    private readonly Label flacCompressionLabel = new()
    {
        Text = "FLAC compression &level (Alt+L):",
        AutoSize = true,
        Padding = new Padding(0, 0, 0, 4),
    };

    private readonly ListBox flacCompressionList = new()
    {
        AccessibleName = "FLAC compression level",
        SelectionMode = SelectionMode.One,
        IntegralHeight = false,
        Height = 200,
    };

    // Channel mode (Stereo / Mono) extracted out of the per-format attributes so the
    // attributes list only carries the bit-depth-or-bitrate decision. Always visible —
    // every format has a channel-count choice.
    private readonly Label channelLabel = new()
    {
        // Alt+C — the natural letter for Channels. The Cancel button gives this letter
        // up (it's on Alt+N) so the mnemonic is unambiguous.
        Text = "&Channels (Alt+C):",
        AutoSize = true,
        Padding = new Padding(0, 0, 0, 4),
    };

    private readonly ListBox channelList = new()
    {
        AccessibleName = "Channels",
        SelectionMode = SelectionMode.One,
        IntegralHeight = false,
        Height = 80,
    };

    // The compression column is wrapped in a Panel held here so its Visible can be
    // toggled atomically with the column's ColumnStyle width — visible+20% width when
    // FLAC, hidden+0% width otherwise. Kept as a field so UpdateFlacCompressionVisibility
    // and the layout-building code share the same instance.
    private Control? compressionColumn;
    private TableLayoutPanel? grid;

    private readonly AccessibleCheckBox splitTracksBox = new()
    {
        Text = "Split recording into separate &tracks — one file per peer, plus your own (Alt+T)",
        AccessibleName = "Split recording into separate tracks, one file per peer plus your own send",
        AutoSize = true,
    };
    private readonly AccessibleCheckBox bypassShapingBox = new()
    {
        Text = "Bypass pan and EQ when recording — record the &raw audio (Alt+R)",
        AccessibleName = "Bypass pan and EQ when recording, record the raw audio",
        AutoSize = true,
    };

    private readonly Button okButton = new()
    {
        Text = "&OK",
        AutoSize = true,
        DialogResult = DialogResult.OK,
    };

    private readonly Button cancelButton = new()
    {
        // Alt+N rather than the conventional Alt+C — Channels takes Alt+C as its natural
        // mnemonic. Esc still dismisses the dialog (CancelButton wiring below), so the
        // less-conventional letter has no real cost.
        Text = "Ca&ncel",
        AutoSize = true,
        DialogResult = DialogResult.Cancel,
    };

    /// <summary>True if the user pressed OK and any setting actually changed. The caller
    /// uses this to mark the profile dirty.</summary>
    public bool ChangedAnything { get; private set; }

    /// <summary>The final settings (after OK). Equals the input settings if Cancel was
    /// pressed — caller should ignore this on a non-OK DialogResult.</summary>
    public RecordingSettings Result => working;

    public RecordingSettingsDialog(RecordingSettings current)
    {
        working = current?.Clone() ?? new RecordingSettings();

        splitTracksBox.Checked = working.SplitTracks;
        splitTracksBox.CheckedChanged += (_, _) => working.SplitTracks = splitTracksBox.Checked;
        bypassShapingBox.Checked = working.BypassShaping;
        bypassShapingBox.CheckedChanged += (_, _) => working.BypassShaping = bypassShapingBox.Checked;
        var initialSnapshot = working.Clone();

        Text = "Recording settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;
        ClientSize = new Size(900, 380);

        PopulateSourceList();
        PopulateFormatList();
        PopulateAttributesList();
        PopulateFlacCompressionList();
        PopulateChannelList();

        SelectFromSource(working.Source);
        SelectFromFormat(working.FileFormat);
        SelectFromAttributes(working);
        SelectFromFlacCompression(working);
        SelectFromChannelMode(working);

        sourceList.SelectedIndexChanged += (_, _) =>
        {
            if (sourceList.SelectedIndex < 0) return;
            working.Source = SourceOrder[sourceList.SelectedIndex];
        };

        formatList.SelectedIndexChanged += (_, _) =>
        {
            if (formatList.SelectedIndex < 0) return;
            var newFormat = (RecordingFileFormat)formatList.SelectedIndex;
            if (newFormat == working.FileFormat) return;
            working.FileFormat = newFormat;
            PopulateAttributesList();
            SelectFromAttributes(working);
            UpdateFlacCompressionVisibility();
        };

        attributesList.SelectedIndexChanged += (_, _) =>
        {
            if (attributesList.SelectedIndex < 0) return;
            ApplyAttributesSelection();
        };

        flacCompressionList.SelectedIndexChanged += (_, _) =>
        {
            if (flacCompressionList.SelectedIndex < 0) return;
            // The compression list always carries levels 0..8 at indices 0..8 — pure
            // 1:1 mapping. Saved even when FLAC isn't the active format so flipping
            // formats back-and-forth preserves the user's compression choice.
            working.FlacCompressionLevel = flacCompressionList.SelectedIndex;
        };

        channelList.SelectedIndexChanged += (_, _) =>
        {
            if (channelList.SelectedIndex < 0) return;
            working.ChannelMode = (RecordingChannelMode)channelList.SelectedIndex;
        };

        okButton.Click += (_, _) =>
        {
            ChangedAnything = !SettingsEqual(initialSnapshot, working);
        };

        // Five columns side by side, OK/Cancel row beneath. The FLAC-compression column
        // (index 3) is collapsed to zero width when the selected file format isn't FLAC,
        // so non-FLAC formats see a clean 4-column layout without a blank gap.
        grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 5,
            RowCount = 2,
        };
        for (var i = 0; i < 5; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20f));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var sourceColumn = MakeColumn(sourceLabel, sourceList);
        var formatColumn = MakeColumn(formatLabel, formatList);
        var attributesColumn = MakeColumn(attributesLabel, attributesList);
        compressionColumn = MakeColumn(flacCompressionLabel, flacCompressionList);
        var channelColumn = MakeColumn(channelLabel, channelList);
        grid.Controls.Add(sourceColumn, 0, 0);
        grid.SetRowSpan(sourceColumn, 2);
        grid.Controls.Add(formatColumn, 1, 0);
        grid.SetRowSpan(formatColumn, 2);
        grid.Controls.Add(attributesColumn, 2, 0);
        grid.SetRowSpan(attributesColumn, 2);
        grid.Controls.Add(compressionColumn, 3, 0);
        grid.SetRowSpan(compressionColumn, 2);
        grid.Controls.Add(channelColumn, 4, 0);
        grid.SetRowSpan(channelColumn, 2);

        // Initial column-visibility refresh, AFTER the grid has all five children so we
        // can flip the compression column's ColumnStyle width together with its Visible.
        UpdateFlacCompressionVisibility();

        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 0, 12, 12),
        };
        buttonRow.Controls.Add(cancelButton);
        buttonRow.Controls.Add(okButton);

        // Order: dialog body first (grid), then buttons docked beneath.
        Controls.Add(grid);
        Controls.Add(buttonRow);

        // The two per-recording toggles sit across the top of the dialog. Added last so docking
        // resolves them to the top strip, above the option columns.
        var optionsRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(12, 12, 12, 0),
        };
        optionsRow.Controls.Add(splitTracksBox);
        optionsRow.Controls.Add(bypassShapingBox);
        Controls.Add(optionsRow);

        AcceptButton = okButton;
        CancelButton = cancelButton;

        // Tab order left-to-right across the visible columns. The FLAC compression list
        // sits between attributes and channels in tab order so the Alt+L mnemonic and Tab
        // both land there when it's visible; when hidden it's still in tab order but
        // skipped because Visible=false controls aren't focusable.
        // The two toggles (top of the dialog) come first in tab order, then the option columns, then
        // the buttons. The CONTAINERS are ordered too — without this the grid's controls (sourceList)
        // sat first and grabbed initial focus, landing the user partway down, past the checkboxes.
        optionsRow.TabIndex = 0;
        grid.TabIndex = 1;
        buttonRow.TabIndex = 2;
        splitTracksBox.TabIndex = 0;
        bypassShapingBox.TabIndex = 1;
        sourceList.TabIndex = 0;
        formatList.TabIndex = 1;
        attributesList.TabIndex = 2;
        flacCompressionList.TabIndex = 3;
        channelList.TabIndex = 4;
        okButton.TabIndex = 5;
        cancelButton.TabIndex = 6;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Land on the first checkbox at the top, not partway down on the option columns.
        splitTracksBox.Focus();
    }

    /// <summary>Show or hide the FLAC compression column based on the current file-format
    /// selection. Driven from the formatList change handler and once at dialog open.
    /// When hidden, the column's ColumnStyle width is set to 0% and the other four columns
    /// each take 25% — visually the dialog reads as a clean 4-column layout. When visible,
    /// all five columns share 20% each.</summary>
    private void UpdateFlacCompressionVisibility()
    {
        if (grid is null || compressionColumn is null) return;
        var isFlac = working.FileFormat == RecordingFileFormat.Flac;
        compressionColumn.Visible = isFlac;
        if (isFlac)
        {
            for (var i = 0; i < 5; i++) grid.ColumnStyles[i].Width = 20f;
        }
        else
        {
            // Distribute the hidden column's space evenly across the four remaining ones.
            for (var i = 0; i < 5; i++) grid.ColumnStyles[i].Width = (i == 3) ? 0f : 25f;
        }
    }

    private static Control MakeColumn(Label label, ListBox list)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        list.Dock = DockStyle.Fill;
        panel.Controls.Add(label, 0, 0);
        panel.Controls.Add(list, 0, 1);
        return panel;
    }

    private void PopulateSourceList()
    {
        sourceList.BeginUpdate();
        sourceList.Items.Clear();
        // Display order is decoupled from the enum via SourceOrder (both first as the default, then
        // received, then sent). Add the labels in that same order.
        sourceList.Items.Add("Record both sent and received audio");
        sourceList.Items.Add("Record all received audio");
        sourceList.Items.Add("Record all sent audio");
        sourceList.EndUpdate();
    }

    private void PopulateFormatList()
    {
        formatList.BeginUpdate();
        formatList.Items.Clear();
        // Order MUST match RecordingFileFormat enum values 0..3.
        formatList.Items.Add("WAV (uncompressed)");
        formatList.Items.Add("MP3");
        formatList.Items.Add("Ogg-Opus");
        formatList.Items.Add("FLAC (lossless)");
        formatList.EndUpdate();
    }

    // === Per-format attribute tables ===
    // All four formats record at the engine's 48 kHz mix rate, so labels include "48 kHz"
    // to make the sample rate explicit (it's not a choice — it's a statement of fact about
    // what gets written, which removes a common surprise for users who expected to see a
    // rate picker). Channel mode is NOT part of any row — it has its own dedicated listbox
    // since 2026-05-15 (was previously folded into every row, doubling the count for no
    // real benefit).

    private static readonly (int Bits, string Label)[] WavAttributes =
    {
        (16, "16-bit PCM, 48 kHz"),
        (24, "24-bit PCM, 48 kHz"),
        (32, "32-bit float, 48 kHz"),
    };

    private static readonly (int Kbps, string Label)[] Mp3Attributes =
    {
        (128, "128 kbps, 48 kHz"),
        (192, "192 kbps, 48 kHz"),
        (256, "256 kbps, 48 kHz"),
        (320, "320 kbps, 48 kHz"),
    };

    // OGG-Opus is VBR — kbps numbers are the encoder's target average. Opus' music-quality
    // sweet spot starts around 96 kbps; we expose 96 / 128 / 192 / 256 so users have a
    // smaller-file option without it sounding obviously lossy on dense material.
    private static readonly (int Kbps, string Label)[] OggOpusAttributes =
    {
        (96,  "96 kbps, 48 kHz"),
        (128, "128 kbps, 48 kHz"),
        (192, "192 kbps, 48 kHz"),
        (256, "256 kbps, 48 kHz"),
    };

    // FLAC is integer-PCM only — quality knob in this column is just bit depth. The
    // compression level is its own listbox (visible only when FLAC is selected).
    private static readonly (int Bits, string Label)[] FlacAttributes =
    {
        (16, "16-bit, 48 kHz"),
        (24, "24-bit, 48 kHz"),
    };

    private void PopulateAttributesList()
    {
        attributesList.BeginUpdate();
        attributesList.Items.Clear();
        switch (working.FileFormat)
        {
            case RecordingFileFormat.Wav:
                foreach (var (_, label) in WavAttributes) attributesList.Items.Add(label);
                break;
            case RecordingFileFormat.Mp3:
                foreach (var (_, label) in Mp3Attributes) attributesList.Items.Add(label);
                break;
            case RecordingFileFormat.Ogg:
                foreach (var (_, label) in OggOpusAttributes) attributesList.Items.Add(label);
                break;
            case RecordingFileFormat.Flac:
                foreach (var (_, label) in FlacAttributes) attributesList.Items.Add(label);
                break;
            default:
                attributesList.Items.Add("Default settings");
                break;
        }
        attributesList.EndUpdate();
    }

    /// <summary>Populate the FLAC compression listbox with all 9 levels (0..8). Levels are
    /// identified verbatim — the user picks by index, which maps 1:1 to the level number.
    /// Headers on the "fastest" and "smallest" ends are tagged so the trade-off is obvious
    /// without needing a separate explanatory label.</summary>
    private void PopulateFlacCompressionList()
    {
        flacCompressionList.BeginUpdate();
        flacCompressionList.Items.Clear();
        flacCompressionList.Items.Add("0 — fastest encode, biggest file");
        flacCompressionList.Items.Add("1");
        flacCompressionList.Items.Add("2");
        flacCompressionList.Items.Add("3");
        flacCompressionList.Items.Add("4");
        flacCompressionList.Items.Add("5 — default (libFLAC reference)");
        flacCompressionList.Items.Add("6");
        flacCompressionList.Items.Add("7");
        flacCompressionList.Items.Add("8 — slowest encode, smallest file");
        flacCompressionList.EndUpdate();
    }

    /// <summary>Populate the channel listbox. Order MUST match RecordingChannelMode enum
    /// values 0 / 1 so the SelectedIndex → enum mapping is direct.</summary>
    private void PopulateChannelList()
    {
        channelList.BeginUpdate();
        channelList.Items.Clear();
        channelList.Items.Add("Stereo");
        channelList.Items.Add("Mono");
        channelList.EndUpdate();
    }

    // Display order for the recording-source list (decoupled from the RecordingSource enum values): both
    // at the top as the default, then received, then sent.
    private static readonly RecordingSource[] SourceOrder =
        [RecordingSource.Both, RecordingSource.ReceivedOnly, RecordingSource.SentOnly];

    private void SelectFromSource(RecordingSource src)
    {
        var idx = Array.IndexOf(SourceOrder, src);
        if (idx >= 0 && idx < sourceList.Items.Count) sourceList.SelectedIndex = idx;
    }

    private void SelectFromFormat(RecordingFileFormat fmt)
    {
        var idx = (int)fmt;
        if (idx >= 0 && idx < formatList.Items.Count) formatList.SelectedIndex = idx;
    }

    private void SelectFromAttributes(RecordingSettings s)
    {
        switch (s.FileFormat)
        {
            case RecordingFileFormat.Wav:
                for (var i = 0; i < WavAttributes.Length; i++)
                {
                    if (WavAttributes[i].Bits == s.WavBitsPerSample)
                    {
                        attributesList.SelectedIndex = i;
                        return;
                    }
                }
                attributesList.SelectedIndex = 1; // 24-bit default
                break;
            case RecordingFileFormat.Mp3:
                for (var i = 0; i < Mp3Attributes.Length; i++)
                {
                    if (Mp3Attributes[i].Kbps == s.Mp3BitrateKbps)
                    {
                        attributesList.SelectedIndex = i;
                        return;
                    }
                }
                attributesList.SelectedIndex = 3; // 320 kbps default
                break;
            case RecordingFileFormat.Ogg:
                for (var i = 0; i < OggOpusAttributes.Length; i++)
                {
                    if (OggOpusAttributes[i].Kbps == s.OggOpusBitrateKbps)
                    {
                        attributesList.SelectedIndex = i;
                        return;
                    }
                }
                attributesList.SelectedIndex = 2; // 192 kbps default
                break;
            case RecordingFileFormat.Flac:
                for (var i = 0; i < FlacAttributes.Length; i++)
                {
                    if (FlacAttributes[i].Bits == s.FlacBitsPerSample)
                    {
                        attributesList.SelectedIndex = i;
                        return;
                    }
                }
                attributesList.SelectedIndex = 1; // 24-bit default
                break;
            default:
                if (attributesList.Items.Count > 0) attributesList.SelectedIndex = 0;
                break;
        }
    }

    private void ApplyAttributesSelection()
    {
        var idx = attributesList.SelectedIndex;
        if (idx < 0) return;
        switch (working.FileFormat)
        {
            case RecordingFileFormat.Wav:
                if (idx < WavAttributes.Length) working.WavBitsPerSample = WavAttributes[idx].Bits;
                break;
            case RecordingFileFormat.Mp3:
                if (idx < Mp3Attributes.Length) working.Mp3BitrateKbps = Mp3Attributes[idx].Kbps;
                break;
            case RecordingFileFormat.Ogg:
                if (idx < OggOpusAttributes.Length) working.OggOpusBitrateKbps = OggOpusAttributes[idx].Kbps;
                break;
            case RecordingFileFormat.Flac:
                if (idx < FlacAttributes.Length) working.FlacBitsPerSample = FlacAttributes[idx].Bits;
                break;
            default:
                break;
        }
    }

    /// <summary>Select the FLAC compression list row that matches the current settings.
    /// Levels 0..8 map directly to list indices 0..8. Out-of-range or unset values fall
    /// back to level 5 (the libFLAC reference default).</summary>
    private void SelectFromFlacCompression(RecordingSettings s)
    {
        var level = s.FlacCompressionLevel;
        if (level < 0 || level > 8) level = 5;
        flacCompressionList.SelectedIndex = level;
    }

    /// <summary>Select the channel-mode row matching the current setting. Falls back to
    /// Stereo (index 0) for any unrecognised value.</summary>
    private void SelectFromChannelMode(RecordingSettings s)
    {
        var idx = (int)s.ChannelMode;
        if (idx < 0 || idx >= channelList.Items.Count) idx = 0;
        channelList.SelectedIndex = idx;
    }

    private static bool SettingsEqual(RecordingSettings a, RecordingSettings b) =>
        a.Source == b.Source
        && a.FileFormat == b.FileFormat
        && a.ChannelMode == b.ChannelMode
        && a.WavBitsPerSample == b.WavBitsPerSample
        && a.Mp3BitrateKbps == b.Mp3BitrateKbps
        && a.OggOpusBitrateKbps == b.OggOpusBitrateKbps
        && a.FlacBitsPerSample == b.FlacBitsPerSample
        && a.FlacCompressionLevel == b.FlacCompressionLevel
        && string.Equals(a.Folder ?? string.Empty, b.Folder ?? string.Empty, StringComparison.OrdinalIgnoreCase);
}
