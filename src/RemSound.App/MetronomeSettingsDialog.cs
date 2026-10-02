using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// METRONOME SETTINGS (Ed, 2026-09-28: "you select what time sig it is, plus you select a sound to play, plus you set
/// whether you want to hear it in recording or daw"), from the Metronome menu. The tempo, the time signature, the click
/// sound (moving along the list plays it), its volume, where you hear it, whether RemSound's recordings carry it, and what
/// happens when someone asks to be the master metronome. All of it is the profile's. While you follow a master its tempo
/// and signature are theirs: shown, not changed here, with a line saying whose. Last, the metronome's own shortcut keys
/// (Ed, 2026-10-01: "a little mini hotkeys in the metronome settings for just the metronome hotkeys", with a note "just like
/// the push to talk ones that they can also be defined in global hotkeys settings"): the same keys as their rows in Keyboard
/// shortcuts, kept on this computer. Above them all, the metronome itself (Ed, 2026-10-02: "before any of the metronome
/// settings, there should be a button to listen to the metronome with current settings and it should change to stop
/// metronome once playing. changing speed, volume, click sounds etc all have an impact real time"): the Metronome menu's
/// own Start and Stop (Mute and Unmute while following a master), and every change in the window heard at once. OK keeps
/// the changes; Cancel and Escape put the profile's settings back, and stop a metronome this window started.
/// PLAN-metronome-2026-09-28.md holds every decision.
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
    private readonly UpMeansMoreTrackBar volumeBar = new()
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
    // The metronome's shortcut keys, listed as Keyboard shortcuts lists them: Enter changes one, Del clears it.
    private readonly ListBox keysList = new()
    {
        AccessibleName = "Metronome shortcut keys, also in Keyboard shortcuts",
        AccessibleDescription = "Press Enter to change the shortcut, or Del to clear it",
        IntegralHeight = false,
        // Wide enough for the first row's whole name and a long key ("... while following a master: Control+Shift+Alt+F12").
        Width = Dpi.Px(600),
        Height = Dpi.Px(84),   // its five rows, and no more: the window must fit a 1920 x 1080 screen at 200 %
    };
    private readonly Label keysNote = new()
    {
        Text = "Press Enter on a shortcut, or double-click it, to change it; Del clears it.",
        AutoSize = true,
        MaximumSize = Dpi.Px(460, 0),
    };
    private readonly Button okButton = new() { Text = "&OK", AutoSize = true, DialogResult = DialogResult.OK };
    private readonly Button cancelButton = new() { Text = "&Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
    private readonly MetronomePreview preview = new();
    // The metronome itself, first: Listen and Stop, or Mute and Unmute while following a master.
    private readonly Button listenButton = new() { AutoSize = true };
    private readonly System.Windows.Forms.Timer stateWatch = new() { Interval = 250 };
    private readonly Func<(bool Playing, bool Following, bool Muted)> metronomeState;
    private readonly Action startStop;
    private readonly Action<MetronomeSettings> heardLive;

    /// <summary>The metronome's keys, in the order the window lists them (MainFormHotkeyController.MetronomeKeys).</summary>
    internal static readonly string[] KeyNames =
        ["Start or stop the metronome (mute or unmute while following a master)", "Tempo up a beat a minute", "Tempo down a beat a minute",
         "Volume up", "Volume down"];
    private readonly HotkeyInfo[] keys;
    private readonly HotkeyInfo[] initialKeys;
    private readonly Func<IWin32Window, HotkeyInfo?> chooseKey;

    private static readonly MetronomeHearIn[] HearOrder = [MetronomeHearIn.RemSound, MetronomeHearIn.Daw];
    private static readonly MetronomeMasterAnswer[] AskOrder = [MetronomeMasterAnswer.Ask, MetronomeMasterAnswer.AcceptAutomatically];

    /// <summary>The settings as they stand (after OK, what to keep).</summary>
    public MetronomeSettings Result => working.Clone();

    /// <summary>True after OK when anything in the profile's settings changed.</summary>
    public bool ChangedSettings { get; private set; }

    /// <summary>The shortcut keys as they stand, in <see cref="KeyNames"/>' order; the caller keeps those that changed.</summary>
    public IReadOnlyList<HotkeyInfo> ShortcutKeys => keys;

    /// <summary>After OK: which of the shortcut keys changed.</summary>
    public IReadOnlyList<int> ChangedKeys { get; private set; } = [];

    /// <param name="current">The profile's settings.</param>
    /// <param name="master">While following: the master's name and timeline, whose tempo and signature are shown instead.</param>
    /// <param name="loopbackSent">The outputs the click would play on that this profile sends a loopback of (their names),
    /// or none.</param>
    /// <param name="shortcutKeys">The metronome's five keys, in <see cref="KeyNames"/>' order; all unset when left out.</param>
    /// <param name="chooseKey">Opens the usual window for choosing a key and returns the key chosen, or null when
    /// cancelled.</param>
    public MetronomeSettingsDialog(MetronomeSettings current, (string Name, MetronomeTimeline Timeline)? master,
        IReadOnlyList<string> loopbackSent, IReadOnlyList<string>? sounds = null, Func<string, MetronomeSound?>? loadSound = null,
        IReadOnlyList<HotkeyInfo>? shortcutKeys = null, Func<IWin32Window, HotkeyInfo?>? chooseKey = null,
        Func<(bool Playing, bool Following, bool Muted)>? metronomeState = null, Action? startStop = null,
        Action<MetronomeSettings>? heardLive = null)
    {
        working = current?.Clone() ?? new MetronomeSettings();
        initial = working.Clone();
        soundNames = sounds ?? MetronomeSoundLibrary.Names();
        this.loadSound = loadSound ?? MetronomeSoundLibrary.Load;
        keys = KeyNames.Select((_, i) => shortcutKeys is not null && i < shortcutKeys.Count ? shortcutKeys[i] : HotkeyInfo.Unset).ToArray();
        initialKeys = (HotkeyInfo[])keys.Clone();
        this.chooseKey = chooseKey ?? (_ => null);
        var stopped = (Playing: false, Following: false, Muted: false);
        this.metronomeState = metronomeState ?? (() => stopped);
        this.startStop = startStop ?? (() => stopped.Playing = !stopped.Playing);
        this.heardLive = heardLive ?? (_ => { });

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

        tempoBox.ValueChanged += (_, _) => { if (tempoBox.Enabled) { working.Bpm = (int)tempoBox.Value; HearLive(); } };
        signatureList.SelectedIndexChanged += (_, _) =>
        {
            if (!signatureList.Enabled || signatureList.SelectedIndex < 0) return;
            var s = MetronomeSignature.Offered[signatureList.SelectedIndex];
            working.BeatsPerBar = s.BeatsPerBar;
            working.BeatUnit = s.BeatUnit;
            HearLive();
        };
        // Only a move along the list plays: opening the window, or coming back to the list, is silent. With the metronome
        // itself sounding, that is the preview: the new sound is heard on its next click.
        soundList.SelectedIndexChanged += (_, _) =>
        {
            if (soundList.SelectedIndex < 0) return;
            working.Sound = soundNames[soundList.SelectedIndex];
            HearLive();
            if (!Sounding) preview.Play(this.loadSound(working.Sound), (int)Math.Round(60_000.0 / (double)tempoBox.Value), working.Gain);
        };
        FormClosed += (_, _) => { preview.Dispose(); stateWatch.Dispose(); };
        volumeBar.ValueChanged += (_, _) => { working.Volume = volumeBar.Value; ShowVolume(); HearLive(); };
        hearList.SelectedIndexChanged += (_, _) =>
        {
            if (hearList.SelectedIndex < 0) return;
            working.HearIn = HearOrder[hearList.SelectedIndex];
            ShowHear();
            HearLive();
        };
        recordBox.CheckedChanged += (_, _) => { working.IncludeInRecordings = recordBox.Checked; HearLive(); };
        askList.SelectedIndexChanged += (_, _) => { if (askList.SelectedIndex >= 0) working.WhenAsked = AskOrder[askList.SelectedIndex]; };
        listenButton.Click += (_, _) =>
        {
            var before = this.metronomeState();
            this.startStop();
            var after = this.metronomeState();
            // A metronome this window started is stopped again by Cancel; one already going, or stopped here, is left.
            if (!before.Following && !before.Playing && after.Playing) StartedHere = true;
            else if (!after.Playing) StartedHere = false;
            ShowListen();
        };
        // The Metronome menu, a hotkey or a master can start or stop it while the window is open: the button follows.
        stateWatch.Tick += (_, _) => ShowListen();
        stateWatch.Start();
        ShowListen();
        keysList.DoubleClick += (_, _) => ChangeSelectedKey();
        // Del clears the shortcut picked back to "(not set)", as in Keyboard shortcuts. Enter is ProcessCmdKey's.
        keysList.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Delete) return;
            ClearSelectedKey();
            e.Handled = true;
            e.SuppressKeyPress = true;
        };
        okButton.Click += (_, _) =>
        {
            ChangedSettings = !working.SameAs(initial);
            ChangedKeys = Enumerable.Range(0, keys.Length).Where(i => keys[i] != initialKeys[i]).ToList();
        };
        ShowHear();
        ShowKeys();

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
        Whole(Wrap(listenButton));
        Labelled("&Tempo, beats per minute (Alt+T):", tempoBox);
        Labelled("Time &signature (Alt+S):", signatureList);
        Whole(masterNote);
        Labelled("Click so&und (Alt+U):", soundList);
        Labelled("&Volume (Alt+V):", volumeBar);
        Labelled("&Hear the click (Alt+H):", hearList);
        Whole(Wrap(recordBox));
        Whole(loopbackNote);
        Labelled("&When someone asks to be the master metronome (Alt+W):", askList);
        // The note Ed asked for, as push to talk's key says it: the same keys are in Keyboard shortcuts.
        Labelled("Metronome shortcut &keys - also in Keyboard shortcuts (Alt+K):", keysList);
        // Its label on two lines: on one, beside the list, it made the window wider than a 1920 x 1080 screen at 200 % (full
        // gate, 2026-10-02).
        grid.Controls.OfType<MnemonicLabel>().Single(l => l.MnemonicTarget == keysList).MaximumSize = new Size(Dpi.Px(300), 0);
        Whole(keysNote);
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

        ContextHelp.Mark(listenButton, "dialog.metronome.listen");
        ContextHelp.Mark(tempoBox, "dialog.metronome.tempo");
        ContextHelp.Mark(signatureList, "dialog.metronome.signature");
        ContextHelp.Mark(soundList, "dialog.metronome.sound");
        ContextHelp.Mark(volumeBar, "dialog.metronome.volume");
        ContextHelp.Mark(hearList, "dialog.metronome.hear");
        ContextHelp.Mark(recordBox, "dialog.metronome.record");
        ContextHelp.Mark(askList, "dialog.metronome.when-asked");
        ContextHelp.Mark(keysList, "dialog.metronome.keys");
        ContextHelp.Mark(okButton, "dialog.metronome.ok");
        ContextHelp.Mark(cancelButton, "dialog.metronome.cancel");
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        listenButton.Focus();
    }

    private void ShowVolume() => volumeBar.AccessibleName = $"Volume: {volumeBar.Value} %";

    /// <summary>True when this window's button started the metronome (and it is still going): Cancel stops it again.</summary>
    public bool StartedHere { get; private set; }

    /// <summary>True once anything was heard live: Cancel then has settings to put back.</summary>
    public bool ChangedLive { get; private set; }

    /// <summary>The metronome is sounding: going and not muted.</summary>
    private bool Sounding => metronomeState() is { Playing: true } s && !(s.Following && s.Muted);

    /// <summary>The window's settings as they stand, to the metronome now (Ed: "all have an impact real time").</summary>
    private void HearLive()
    {
        ChangedLive = true;
        heardLive(working.Clone());
    }

    /// <summary>The button says what pressing it does, as the Metronome menu's first item does.</summary>
    private void ShowListen()
    {
        var (playing, following, muted) = metronomeState();
        var (text, name) = following
            ? muted ? ("Unmute metronome (Alt+&L)", "Unmute metronome") : ("Mute metronome (Alt+&L)", "Mute metronome")
            : playing ? ("Stop metronome (Alt+&L)", "Stop metronome") : ("&Listen to the metronome (Alt+L)", "Listen to the metronome");
        if (listenButton.Text != text) listenButton.Text = text;
        if (listenButton.AccessibleName != name) listenButton.AccessibleName = name;
    }

    /// <summary>Each key's row says what it does and the key, as Keyboard shortcuts says it; the row picked stays picked.</summary>
    private void ShowKeys()
    {
        var at = keysList.SelectedIndex;
        keysList.BeginUpdate();
        keysList.Items.Clear();
        for (var i = 0; i < KeyNames.Length; i++) keysList.Items.Add($"{KeyNames[i]}: {keys[i]}");
        keysList.SelectedIndex = at >= 0 && at < keysList.Items.Count ? at : 0;
        keysList.EndUpdate();
    }

    private void ChangeSelectedKey()
    {
        var i = keysList.SelectedIndex;
        if (i < 0 || chooseKey(this) is not { } chosen) return;
        keys[i] = chosen;
        ShowKeys();
        keysList.Focus();
    }

    private void ClearSelectedKey()
    {
        var i = keysList.SelectedIndex;
        if (i < 0 || keys[i].IsUnset) return;
        keys[i] = HotkeyInfo.Unset;
        ShowKeys();
    }

    /// <summary>Enter on the shortcut list changes the shortcut picked, as in Keyboard shortcuts; anywhere else it is OK.
    /// This runs before the window's OK button would take it.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Enter && keysList.Focused)
        {
            ChangeSelectedKey();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

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
    internal ListBox KeysListForTest => keysList;
    internal Button ListenButtonForTest => listenButton;
    internal void WatchStateForTest() => ShowListen();
    internal Label KeysNoteForTest => keysNote;
    internal void ChangeSelectedKeyForTest() => ChangeSelectedKey();
    internal Label MasterNoteForTest => masterNote;
    /// <summary>The two notes are shown (Visible reads false on a window not yet on screen).</summary>
    internal bool MasterNoteShownForTest { get; private set; }
    internal bool LoopbackNoteShownForTest { get; private set; }
    internal Label LoopbackNoteForTest => loopbackNote;
    internal MetronomePreview PreviewForTest => preview;
    internal Button OkButtonForTest => okButton;
}
