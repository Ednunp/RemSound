using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// PUSH TO TALK, the window's part (Ed, 2026-09-28). The sender's gate does the holding (PushToTalkGate); this keeps it in
/// step with the profile, turns the key into talking (hold, tap, double tap - PushToTalkKeyState), plays the two cues in key
/// mode, logs it, and says it in the status. PLAN-push-to-talk-2026-09-28.md holds every decision.
/// </summary>
public sealed partial class MainForm
{
    private readonly Button pushToTalkButton = new() { AutoSize = true };
    private readonly PushToTalkKeyState pushToTalkKeys = new();
    private PushToTalkKeyHook? pushToTalkHook;
    private PushToTalkSettings pushToTalk = new();
    private CuePlayer? pushToTalkOnSound;
    private CuePlayer? pushToTalkOffSound;

    private bool PushToTalkByKey => pushToTalk is { Enabled: true, Trigger: PushToTalkTrigger.Key };

    private void WirePushToTalkButton()
    {
        pushToTalkButton.Click += (_, _) => OpenPushToTalkDialog();
        UpdatePushToTalkButton();
    }

    /// <summary>The button says the state (Ed: "push to talk settings, currently disabled. or enabled").</summary>
    private void UpdatePushToTalkButton()
    {
        var state = pushToTalk.Enabled ? "enabled" : "disabled";
        pushToTalkButton.Text = $"Push to talk settings, currently {state} (Alt+&7)";
        pushToTalkButton.AccessibleName = $"Push to talk settings, currently {state}";
    }

    private void OpenPushToTalkDialog()
    {
        using var dialog = new PushToTalkDialog(settings.LoadPushToTalkSettings(), settings.LoadPushToTalkHotkey(), ChoosePushToTalkKey);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (dialog.ChangedKey)
        {
            // Computer-wide, like every shortcut: saved now, not with the profile.
            settings.SavePushToTalkHotkey(dialog.Key);
            LogUiChange("push to talk key", dialog.Key.ToString());
            UpdateHotkeyAnnouncements();
        }
        if (dialog.ChangedSettings)
        {
            settings.SavePushToTalkSettings(dialog.Result);
            MarkProfileDirty();
        }
        ApplyPushToTalk(dialog.Result, dialog.ChangedSettings ? "push to talk" : null);
    }

    /// <summary>The usual window for choosing a key, a single key allowed. The hook stands aside while it is open
    /// (HotkeyCaptureForm.AnyOpen), or the push to talk key could never be chosen again.</summary>
    private HotkeyInfo? ChoosePushToTalkKey(IWin32Window owner)
    {
        using var capture = MainFormHotkeyController.NewHotkeyCaptureForm(allowSingleKey: true);
        return ForegroundDialog.Show(o => capture.ShowDialog(o)) == DialogResult.OK ? capture.CapturedHotkey : null;
    }

    /// <summary>
    /// Everything push to talk does follows from here: the sender's gate, the key's behaviour (anything held or locked is
    /// let go - a change of rules starts from silence), the hook, the button and the status. Called when a profile loads
    /// and when the dialog is closed with OK.
    /// </summary>
    private void ApplyPushToTalk(PushToTalkSettings value, string? logAs)
    {
        pushToTalk = value.Clone();
        pushToTalkKeys.Configure(pushToTalk.HoldToSpeak, pushToTalk.DoubleTapToHold, pushToTalk.DoubleTapSpeed);
        sender.PushToTalk.Configure(pushToTalk);
        sender.PushToTalk.SetKeyTalking(false);
        ArmPushToTalkKey();
        UpdatePushToTalkButton();
        if (logAs is not null) LogUiChange(logAs, pushToTalk.Describe());
    }

    /// <summary>Watch the key while push to talk is on by key - and only in a copy somebody is using. A --headless copy
    /// or the self-test never takes the key from the person at the computer; they are driven through
    /// <see cref="PushToTalkKeyForTest"/> and the control channel's "talk" command instead.</summary>
    private void ArmPushToTalkKey()
    {
        if (!PushToTalkByKey || headless || Windowless.QuietForRun)
        {
            pushToTalkHook?.Disarm();
            return;
        }
        pushToTalkHook ??= new PushToTalkKeyHook(OnPushToTalkKey);
        pushToTalkHook.Arm(settings.LoadPushToTalkHotkey());
    }

    /// <summary>The key went down or up - from the hook, or the control channel.</summary>
    private void OnPushToTalkKey(bool down, long ms)
    {
        if (!PushToTalkByKey) return;
        var changed = down ? pushToTalkKeys.Press(ms) : pushToTalkKeys.Release(ms);
        if (!changed) return;
        var talking = pushToTalkKeys.Talking;
        sender.PushToTalk.SetKeyTalking(talking);
        logFile.Event($"push to talk: {(talking ? "talking" : "stopped talking")} (key, {PushToTalkSettings.KeyBehaviour(pushToTalk.HoldToSpeak, pushToTalk.DoubleTapToHold)})");
        // The two cues, key mode only: in sound-level mode they would sound with every phrase. As the profile has them.
        if (talking) { if (settings.LoadCueEnabled(CueId.PushToTalkOn)) pushToTalkOnSound?.Play(); }
        else if (settings.LoadCueEnabled(CueId.PushToTalkOff)) pushToTalkOffSound?.Play();
    }

    /// <summary>Sound-level mode opens and shuts on the audio threads; the per-second tick writes it down, once a second
    /// at most.</summary>
    private void NotePushToTalkTransitions()
    {
        var (opens, closes) = sender.PushToTalk.TakeTransitions();
        if (pushToTalk is not { Enabled: true, Trigger: PushToTalkTrigger.Level } || (opens == 0 && closes == 0)) return;
        logFile.Event($"push to talk: sound reached {pushToTalk.LevelDb} dB - opened {opens} time(s), closed {closes} in the last second");
    }

    /// <summary>For the status: whether you are talking, when push to talk is on.</summary>
    private string? PushToTalkStatus() => !pushToTalk.Enabled
        ? null
        : sender.PushToTalk.IsOpen ? "Push to talk: talking" : "Push to talk: not talking";

    // Gate seams.
    internal Button PushToTalkButtonForTest => pushToTalkButton;
    internal void PushToTalkKeyForTest(bool down, long ms) => OnPushToTalkKey(down, ms);
    internal void ApplyPushToTalkForTest(PushToTalkSettings value) => ApplyPushToTalk(value, "push to talk");
    internal PushToTalkSettings PushToTalkForTest => pushToTalk.Clone();
    internal bool PushToTalkHookInstalledForTest => pushToTalkHook?.Installed == true;
    internal string? PushToTalkStatusForTest => PushToTalkStatus();
    internal void NotePushToTalkTransitionsForTest() => NotePushToTalkTransitions();
}
