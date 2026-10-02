using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// PUSH TO TALK SETTINGS (Ed, 2026-09-28), opened from the button after "Send my audio" on the Audio inputs and outputs
/// tab. In Ed's order: enable; the path it holds; the key (computer-wide, also in Keyboard shortcuts); what triggers it - a
/// key (hold to speak, double tap to hold, and how fast a double tap is) or sound reaching a level (the slider); and the
/// two bypasses, for recording and
/// for the DAW plugin. Everything but the key is the profile's. Like Recording settings, nothing changes until OK; Cancel
/// and Escape discard. PLAN-push-to-talk-2026-09-28.md holds every decision.
/// </summary>
internal sealed class PushToTalkDialog : Form
{
    private readonly PushToTalkSettings working;
    private readonly PushToTalkSettings initial;
    private readonly HotkeyInfo initialKey;
    private readonly Func<IWin32Window, HotkeyInfo?> chooseKey;

    private readonly AccessibleCheckBox enableBox = new()
    {
        Text = "&Enable push to talk (Alt+E)",
        AccessibleName = "Enable push to talk",
        AutoSize = true,
    };
    private readonly ListBox pathList = new()
    {
        AccessibleName = "Push to talk affected audio path",
        IntegralHeight = false,
        Width = Dpi.Px(300),
        Height = Dpi.Px(58),
    };
    private readonly Button keyButton = new() { AutoSize = true };
    private readonly ListBox triggerList = new()
    {
        AccessibleName = "Push to talk is triggered by",
        IntegralHeight = false,
        Width = Dpi.Px(300),
        Height = Dpi.Px(40),
    };
    private readonly AccessibleCheckBox holdBox = new()
    {
        Text = "&Hold to speak - sound goes out only while the key is held (Alt+H)",
        AccessibleName = "Hold to speak, sound goes out only while the key is held",
        AutoSize = true,
    };
    private readonly AccessibleCheckBox doubleTapBox = new()
    {
        Text = "&Double tap to hold - a double tap locks talking on, one tap releases it (Alt+D)",
        AccessibleName = "Double tap to hold, a double tap locks talking on, one tap releases it",
        AutoSize = true,
    };
    // Slow, medium, fast (Ed, 2026-09-28). Moving along it taps a key twice, that far apart.
    private readonly ListBox speedList = new()
    {
        AccessibleName = "Double tap speed",
        IntegralHeight = false,
        Width = Dpi.Px(300),
        Height = Dpi.Px(58),
    };
    private readonly DoubleTapPreview preview = new();
    private static readonly PushToTalkDoubleTapSpeed[] SpeedOrder =
        [PushToTalkDoubleTapSpeed.Slow, PushToTalkDoubleTapSpeed.Medium, PushToTalkDoubleTapSpeed.Fast];
    // Up raises the level (Ed, 2026-10-02).
    private readonly UpMeansMoreTrackBar levelBar = new()
    {
        Minimum = PushToTalkSettings.MinLevelDb,
        Maximum = PushToTalkSettings.MaxLevelDb,
        SmallChange = 1,
        LargeChange = 5,
        TickFrequency = 10,
        Width = Dpi.Px(300),
    };
    private readonly AccessibleCheckBox bypassRecordingBox = new()
    {
        Text = "Bypass push to talk for &recording - record everything you send, talking or not (Alt+R)",
        AccessibleName = "Bypass push to talk for recording, record everything you send, talking or not",
        AutoSize = true,
    };
    private readonly AccessibleCheckBox bypassPluginBox = new()
    {
        Text = "Bypass push to talk for the DAW pl&ugin - a DAW track goes out talking or not (Alt+U)",
        AccessibleName = "Bypass push to talk for the DAW plugin, a DAW track goes out talking or not",
        AutoSize = true,
    };
    private readonly Button okButton = new() { Text = "&OK", AutoSize = true, DialogResult = DialogResult.OK };
    private readonly Button cancelButton = new() { Text = "&Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };

    private static readonly PushToTalkPath[] PathOrder = [PushToTalkPath.Wasapi, PushToTalkPath.Asio, PushToTalkPath.Both];
    private static readonly PushToTalkTrigger[] TriggerOrder = [PushToTalkTrigger.Key, PushToTalkTrigger.Level];

    /// <summary>The settings as they stand (after OK, what to keep).</summary>
    public PushToTalkSettings Result => working.Clone();

    /// <summary>The key as it stands; the caller saves it on OK when it changed.</summary>
    public HotkeyInfo Key { get; private set; }

    /// <summary>True after OK when anything in the profile's settings changed.</summary>
    public bool ChangedSettings { get; private set; }

    /// <summary>True after OK when the key changed.</summary>
    public bool ChangedKey { get; private set; }

    /// <param name="chooseKey">Opens the usual window for choosing a key (a single key allowed) and returns the key chosen,
    /// or null when cancelled.</param>
    public PushToTalkDialog(PushToTalkSettings current, HotkeyInfo key, Func<IWin32Window, HotkeyInfo?> chooseKey)
    {
        working = current?.Clone() ?? new PushToTalkSettings();
        initial = working.Clone();
        initialKey = key;
        Key = key;
        this.chooseKey = chooseKey;

        Text = "Push to talk settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        pathList.Items.AddRange(["WASAPI", "ASIO", "Both WASAPI and ASIO"]);
        triggerList.Items.AddRange(["A key", "Sound reaching a level"]);
        speedList.Items.AddRange(SpeedOrder.Select(s => (object)PushToTalkSettings.DoubleTapSpeedName(s)).ToArray());

        enableBox.Checked = working.Enabled;
        pathList.SelectedIndex = Array.IndexOf(PathOrder, working.Path) is var p and >= 0 ? p : 2;
        triggerList.SelectedIndex = working.Trigger == PushToTalkTrigger.Level ? 1 : 0;
        holdBox.Checked = working.HoldToSpeak;
        doubleTapBox.Checked = working.DoubleTapToHold;
        speedList.SelectedIndex = Array.IndexOf(SpeedOrder, working.DoubleTapSpeed) is var sp and >= 0 ? sp : 1;
        levelBar.Value = Math.Clamp(working.LevelDb, PushToTalkSettings.MinLevelDb, PushToTalkSettings.MaxLevelDb);
        bypassRecordingBox.Checked = working.BypassForRecording;
        bypassPluginBox.Checked = working.BypassForPlugin;
        ShowKey();
        ShowLevel();
        ShowTrigger();

        enableBox.CheckedChanged += (_, _) => working.Enabled = enableBox.Checked;
        pathList.SelectedIndexChanged += (_, _) => { if (pathList.SelectedIndex >= 0) working.Path = PathOrder[pathList.SelectedIndex]; };
        triggerList.SelectedIndexChanged += (_, _) =>
        {
            if (triggerList.SelectedIndex < 0) return;
            working.Trigger = TriggerOrder[triggerList.SelectedIndex];
            ShowTrigger();
        };
        holdBox.CheckedChanged += (_, _) => working.HoldToSpeak = holdBox.Checked;
        doubleTapBox.CheckedChanged += (_, _) => { working.DoubleTapToHold = doubleTapBox.Checked; ShowTrigger(); };
        // Only a move along the list taps: opening the window, or coming back to the list, is silent.
        speedList.SelectedIndexChanged += (_, _) =>
        {
            if (speedList.SelectedIndex < 0) return;
            working.DoubleTapSpeed = SpeedOrder[speedList.SelectedIndex];
            preview.Play(PushToTalkSettings.DoubleTapMs(working.DoubleTapSpeed));
        };
        FormClosed += (_, _) => preview.Dispose();
        levelBar.ValueChanged += (_, _) => { working.LevelDb = levelBar.Value; ShowLevel(); };
        bypassRecordingBox.CheckedChanged += (_, _) => working.BypassForRecording = bypassRecordingBox.Checked;
        bypassPluginBox.CheckedChanged += (_, _) => working.BypassForPlugin = bypassPluginBox.Checked;
        keyButton.Click += (_, _) =>
        {
            if (chooseKey(this) is { } chosen) { Key = chosen; ShowKey(); }
        };
        okButton.Click += (_, _) =>
        {
            ChangedSettings = !working.SameAs(initial);
            ChangedKey = Key != initialKey;
        };

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
        Whole(Wrap(enableBox));
        Labelled("Push to talk affected audio &path (Alt+P):", pathList);
        Whole(Wrap(keyButton));
        Labelled("Push to talk is &triggered by (Alt+T):", triggerList);
        Whole(Wrap(holdBox));
        Whole(Wrap(doubleTapBox));
        Labelled("Double tap &speed (Alt+S):", speedList);
        Labelled("Send when sound reaches this &level (Alt+L):", levelBar);
        Whole(Wrap(bypassRecordingBox));
        Whole(Wrap(bypassPluginBox));
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

        ContextHelp.Mark(enableBox, "dialog.push-to-talk.enable");
        ContextHelp.Mark(pathList, "dialog.push-to-talk.path");
        ContextHelp.Mark(keyButton, "dialog.push-to-talk.key");
        ContextHelp.Mark(triggerList, "dialog.push-to-talk.trigger");
        ContextHelp.Mark(holdBox, "dialog.push-to-talk.hold");
        ContextHelp.Mark(doubleTapBox, "dialog.push-to-talk.double-tap");
        ContextHelp.Mark(speedList, "dialog.push-to-talk.double-tap-speed");
        ContextHelp.Mark(levelBar, "dialog.push-to-talk.level");
        ContextHelp.Mark(bypassRecordingBox, "dialog.push-to-talk.bypass-recording");
        ContextHelp.Mark(bypassPluginBox, "dialog.push-to-talk.bypass-plugin");
        ContextHelp.Mark(okButton, "dialog.push-to-talk.ok");
        ContextHelp.Mark(cancelButton, "dialog.push-to-talk.cancel");
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        enableBox.Focus();
    }

    /// <summary>The key's button says the key, and that the same key is in Keyboard shortcuts.</summary>
    private void ShowKey()
    {
        keyButton.Text = $"Push to talk shortcut &key: {Key} - also in Keyboard shortcuts (Alt+K)";
        keyButton.AccessibleName = $"Push to talk shortcut key: {Key}. Also in Keyboard shortcuts. Press to change it";
    }

    private void ShowLevel() => levelBar.AccessibleName = $"Send when sound reaches this level: {levelBar.Value} dB";

    /// <summary>Only what the chosen trigger uses can be changed: the key's two boxes, or the level. The double tap speed
    /// only while double tap to hold is ticked.</summary>
    private void ShowTrigger()
    {
        var byKey = working.Trigger == PushToTalkTrigger.Key;
        holdBox.Enabled = byKey;
        doubleTapBox.Enabled = byKey;
        speedList.Enabled = byKey && working.DoubleTapToHold;
        levelBar.Enabled = !byKey;
    }

    // Test seams: the controls, as a person reaches them.
    internal AccessibleCheckBox EnableBoxForTest => enableBox;
    internal ListBox PathListForTest => pathList;
    internal Button KeyButtonForTest => keyButton;
    internal ListBox TriggerListForTest => triggerList;
    internal AccessibleCheckBox HoldBoxForTest => holdBox;
    internal AccessibleCheckBox DoubleTapBoxForTest => doubleTapBox;
    internal ListBox SpeedListForTest => speedList;
    internal DoubleTapPreview PreviewForTest => preview;
    internal TrackBar LevelBarForTest => levelBar;
    internal AccessibleCheckBox BypassRecordingBoxForTest => bypassRecordingBox;
    internal AccessibleCheckBox BypassPluginBoxForTest => bypassPluginBox;
    internal Button OkButtonForTest => okButton;
}
