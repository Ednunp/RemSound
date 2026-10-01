using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// METRONOME SETTINGS (Ed, 2026-09-28: "you select what time sig it is, plus you select a sound to play, plus you set
/// whether you want to hear it in recording or daw"), from the Metronome menu. The tempo, the time signature, the click
/// sound (moving along the list plays it), its volume, where you hear it, whether RemSound's recordings carry it, and what
/// happens when someone asks to be the master metronome. All of it is the profile's. While you follow a master its tempo
/// and signature are theirs: shown, not changed here, with a line saying whose. Nothing changes until OK; Cancel and Escape
/// discard. PLAN-metronome-2026-09-28.md holds every decision.
/// </summary>
internal sealed class MetronomeSettingsDialog : Form
{
    private readonly MetronomeSettings working;
    private readonly MetronomeSettings initial;
    private readonly IReadOnlyList<string> soundNames;
    private readonly Func<string, MetronomeSound?> loadSound;

    private readonly NumericUpDown tempoBox = new()
    {
        Minimum = MetronomeTimeline.MinBpm,
        Maximum = MetronomeTimeline.MaxBpm,
        AccessibleName = "Tempo in beats per minute",
        Width = Dpi.Px(80),
    };
    private readonly ListBox signatureList = new()
    {
        AccessibleName = "Time signature",
        IntegralHeight = false,
        Width = Dpi.Px(300),
        Height = Dpi.Px(76),
    };
    private readonly Label masterNote = new() { AutoSize = true, Visible = false, MaximumSize = Dpi.Px(460, 0) };
    private readonly ListBox soundList = new()
    {
        AccessibleName = "Click sound",
        IntegralHeight = false,
        Width = Dpi.Px(300),
        Height = Dpi.Px(76),
    };
    private readonly TrackBar volumeBar = new()
    {
        Minimum = 0,
        Maximum = 100,
        SmallChange = 5,
        LargeChange = 10,
        TickFrequency = 10,
        Width = Dpi.Px(300),
    };
    private readonly ListBox hearList = new()
    {
        AccessibleName = "Hear the click",
        IntegralHeight = false,
        Width = Dpi.Px(380),
        Height = Dpi.Px(40),
    };
    private readonly AccessibleCheckBox recordBox = new()
    {
        Text = "Include the click in RemSound's &recordings (Alt+R)",
        AccessibleName = "Include the click in RemSound's recordings",
        AutoSize = true,
    };
    private readonly Label loopbackNote = new() { AutoSize = true, Visible = false, MaximumSize = Dpi.Px(460, 0) };
    private readonly ListBox askList = new()
    {
        AccessibleName = "When someone asks to be the master metronome",
        IntegralHeight = false,
        Width = Dpi.Px(300),
        Height = Dpi.Px(40),
    };
    private readonly Button okButton = new() { Text = "&OK", AutoSize = true, DialogResult = DialogResult.OK };
    private readonly Button cancelButton = new() { Text = "&Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
    private readonly MetronomePreview preview = new();

    private static readonly MetronomeHearIn[] HearOrder = [MetronomeHearIn.RemSound, MetronomeHearIn.Daw];
    private static readonly MetronomeMasterAnswer[] AskOrder = [MetronomeMasterAnswer.Ask, MetronomeMasterAnswer.AcceptAutomatically];

    /// <summary>The settings as they stand (after OK, what to keep).</summary>
    public MetronomeSettings Result => working.Clone();

    /// <summary>True after OK when anything changed.</summary>
    public bool ChangedSettings { get; private set; }

    /// <param name="current">The profile's settings.</param>
    /// <param name="master">While following: the master's name and timeline, whose tempo and signature are shown instead.</param>
    /// <param name="loopbackSent">The outputs the click would play on that this profile sends a loopback of (their names),
    /// or none.</param>
    public MetronomeSettingsDialog(MetronomeSettings current, (string Name, MetronomeTimeline Timeline)? master,
        IReadOnlyList<string> loopbackSent, IReadOnlyList<string>? sounds = null, Func<string, MetronomeSound?>? loadSound = null)
    {
        working = current?.Clone() ?? new MetronomeSettings();
        initial = working.Clone();
        soundNames = sounds ?? MetronomeSoundLibrary.Names();
        this.loadSound = loadSound ?? MetronomeSoundLibrary.Load;

        Text = "Metronome settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        signatureList.Items.AddRange(MetronomeSignature.Offered.Select(s => (object)s.ToString()).ToArray());
        soundList.Items.AddRange(soundNames.Select(n => (object)n).ToArray());
        hearList.Items.AddRange(["In RemSound (your outputs)", "In the DAW (choose Metronome on a track's RemSound plugin)"]);
        askList.Items.AddRange(["Ask me", "Accept automatically"]);

        tempoBox.Value = working.ClampedBpm;
        signatureList.SelectedIndex = Math.Max(0, Array.IndexOf(MetronomeSignature.Offered, working.Signature) is var si and >= 0 ? si : 2);
        var soundIndex = soundNames.ToList().FindIndex(n => n.Equals(working.Sound, StringComparison.OrdinalIgnoreCase));
        if (soundList.Items.Count > 0) soundList.SelectedIndex = soundIndex >= 0 ? soundIndex : 0;
        volumeBar.Value = Math.Clamp(working.Volume, 0, 100);
        hearList.SelectedIndex = working.HearIn == MetronomeHearIn.Daw ? 1 : 0;
        recordBox.Checked = working.IncludeInRecordings;
        askList.SelectedIndex = working.WhenAsked == MetronomeMasterAnswer.AcceptAutomatically ? 1 : 0;
        ShowVolume();

        if (master is { } m)
        {
            // Following: the master's tempo and signature, shown and not changed here.
            tempoBox.Value = Math.Clamp((decimal)Math.Round(m.Timeline.Bpm), tempoBox.Minimum, tempoBox.Maximum);
            var mi = Array.IndexOf(MetronomeSignature.Offered, m.Timeline.Signature);
            if (mi >= 0) signatureList.SelectedIndex = mi;
            tempoBox.Enabled = false;
            signatureList.Enabled = false;
            masterNote.Text = $"Tempo and time signature are {m.Name}'s, the master metronome.";
            masterNote.Visible = true;
            MasterNoteShownForTest = true;
            // The tempo and signature cannot be reached by the keyboard now, so the first list says whose they are.
            soundList.AccessibleDescription = masterNote.Text;
        }
        if (loopbackSent.Count > 0)
        {
            loopbackNote.Text = $"The click would be sent: it plays on the device you send ({string.Join(", ", loopbackSent)}). It is not played there.";
            loopbackNote.Visible = true;
            LoopbackNoteShownForTest = true;
            hearList.AccessibleDescription = loopbackNote.Text;
        }

        tempoBox.ValueChanged += (_, _) => { if (tempoBox.Enabled) working.Bpm = (int)tempoBox.Value; };
        signatureList.SelectedIndexChanged += (_, _) =>
        {
            if (!signatureList.Enabled || signatureList.SelectedIndex < 0) return;
            var s = MetronomeSignature.Offered[signatureList.SelectedIndex];
            working.BeatsPerBar = s.BeatsPerBar;
            working.BeatUnit = s.BeatUnit;
        };
        // Only a move along the list plays: opening the window, or coming back to the list, is silent.
        soundList.SelectedIndexChanged += (_, _) =>
        {
            if (soundList.SelectedIndex < 0) return;
            working.Sound = soundNames[soundList.SelectedIndex];
            preview.Play(this.loadSound(working.Sound), (int)Math.Round(60_000.0 / (double)tempoBox.Value), working.Gain);
        };
        FormClosed += (_, _) => preview.Dispose();
        volumeBar.ValueChanged += (_, _) => { working.Volume = volumeBar.Value; ShowVolume(); };
        hearList.SelectedIndexChanged += (_, _) =>
        {
            if (hearList.SelectedIndex < 0) return;
            working.HearIn = HearOrder[hearList.SelectedIndex];
            ShowHear();
        };
        recordBox.CheckedChanged += (_, _) => working.IncludeInRecordings = recordBox.Checked;
        askList.SelectedIndexChanged += (_, _) => { if (askList.SelectedIndex >= 0) working.WhenAsked = AskOrder[askList.SelectedIndex]; };
        okButton.Click += (_, _) => ChangedSettings = !working.SameAs(initial);
        ShowHear();

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Padding = Dpi.Pad(12), Dock = DockStyle.Fill };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var row = 0;
        // A tick box sits in a panel of its own: straight in a table cell, NVDA does not hear it change.
        FlowLayoutPanel Wrap(Control c)
        {
            var wrap = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            wrap.Controls.Add(c);
            return wrap;
        }
        void Whole(Control c) { grid.Controls.Add(c, 0, row); grid.SetColumnSpan(c, 2); row++; }
        void Labelled(string label, Control c) { FormLayoutRows.AddRow(grid, row, label, c, x => x.Focus()); row++; }
        Labelled("&Tempo, beats per minute (Alt+T):", tempoBox);
        Labelled("Time &signature (Alt+S):", signatureList);
        Whole(masterNote);
        Labelled("Click so&und (Alt+U):", soundList);
        Labelled("&Volume (Alt+V):", volumeBar);
        Labelled("&Hear the click (Alt+H):", hearList);
        Whole(Wrap(recordBox));
        Whole(loopbackNote);
        Labelled("&When someone asks to be the master metronome (Alt+W):", askList);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(okButton);
        Whole(buttons);
        // Tab goes in the order the window reads, top to bottom, then OK and Cancel.
        for (var i = 0; i < grid.Controls.Count; i++) grid.Controls[i].TabIndex = i;
        okButton.TabIndex = 0;
        cancelButton.TabIndex = 1;
        Controls.Add(grid);
        AcceptButton = okButton;
        CancelButton = cancelButton;

        ContextHelp.Mark(tempoBox, "dialog.metronome.tempo");
        ContextHelp.Mark(signatureList, "dialog.metronome.signature");
        ContextHelp.Mark(soundList, "dialog.metronome.sound");
        ContextHelp.Mark(volumeBar, "dialog.metronome.volume");
        ContextHelp.Mark(hearList, "dialog.metronome.hear");
        ContextHelp.Mark(recordBox, "dialog.metronome.record");
        ContextHelp.Mark(askList, "dialog.metronome.when-asked");
        ContextHelp.Mark(okButton, "dialog.metronome.ok");
        ContextHelp.Mark(cancelButton, "dialog.metronome.cancel");
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        (tempoBox.Enabled ? (Control)tempoBox : soundList).Focus();
    }

    private void ShowVolume() => volumeBar.AccessibleName = $"Volume: {volumeBar.Value} %";

    /// <summary>The click is recorded only when it is heard in RemSound: a DAW records its own tracks.</summary>
    private void ShowHear() => recordBox.Enabled = working.HearIn == MetronomeHearIn.RemSound;

    // Test seams: the controls, as a person reaches them.
    internal NumericUpDown TempoBoxForTest => tempoBox;
    internal ListBox SignatureListForTest => signatureList;
    internal ListBox SoundListForTest => soundList;
    internal TrackBar VolumeBarForTest => volumeBar;
    internal ListBox HearListForTest => hearList;
    internal AccessibleCheckBox RecordBoxForTest => recordBox;
    internal ListBox AskListForTest => askList;
    internal Label MasterNoteForTest => masterNote;
    /// <summary>The two notes are shown (Visible reads false on a window not yet on screen).</summary>
    internal bool MasterNoteShownForTest { get; private set; }
    internal bool LoopbackNoteShownForTest { get; private set; }
    internal Label LoopbackNoteForTest => loopbackNote;
    internal MetronomePreview PreviewForTest => preview;
    internal Button OkButtonForTest => okButton;
}
