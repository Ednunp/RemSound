using System.Runtime.InteropServices;
using RemSound.Core;

namespace RemSound.App;

internal sealed class MainFormHotkeyController : IDisposable
{
    private readonly RemSoundSettingsStore settingsStore;
    private readonly Action toggleSend;
    private readonly Action toggleReceive;
    private readonly Action toggleTray;
    private readonly Action volumeUp;
    private readonly Action volumeDown;
    // Toggle recording — fires the same ToggleRecording path the Record menu item and the
    // in-app Ctrl+R shortcut hit. The in-app Ctrl+R only fires when MainForm has focus; this
    // hotkey works system-wide via RegisterHotKey. Default binding is unset so we don't
    // collide with anything on a fresh install — users who want it pick their own combo.
    private readonly Action toggleRecording;
    // Remote control hotkeys: trigger this machine to send a Control packet to its connected
    // peers. The local volume slider on this machine isn't touched — receivers that have opted
    // in handle the change. See Profile.AcceptRemoteVolumeCommands and the RemPacketType.Control
    // wire format.
    //   * sendRemote*           → adjust the receiver's RemSound app volume slider (in-app).
    //   * sendSystem*           → adjust the receiver's Windows default-output-device volume
    //                              (system-wide on the receiving machine — affects every app
    //                              there, including the screen reader).
    private readonly Action sendRemoteVolumeUp;
    private readonly Action sendRemoteVolumeDown;
    private readonly Action sendRemoteMuteToggle;
    private readonly Action sendSystemVolumeUp;
    private readonly Action sendSystemVolumeDown;
    private readonly Action sendSystemMuteToggle;
    private readonly Action quickProfileSwitch;
    private Form? owner;
    private HotkeyInfo sendMuteHotkey;
    private HotkeyInfo receiveMuteHotkey;
    private HotkeyInfo trayHotkey;
    private HotkeyInfo volumeUpHotkey;
    private HotkeyInfo volumeDownHotkey;
    private HotkeyInfo toggleRecordingHotkey;
    private HotkeyInfo remoteVolumeUpHotkey;
    private HotkeyInfo remoteVolumeDownHotkey;
    private HotkeyInfo remoteMuteToggleHotkey;
    private HotkeyInfo systemVolumeUpHotkey;
    private HotkeyInfo systemVolumeDownHotkey;
    private HotkeyInfo systemMuteToggleHotkey;
    private HotkeyInfo quickProfileSwitchHotkey;
    private GlobalHotkey? sendMuteGlobalHotkey;
    private GlobalHotkey? receiveMuteGlobalHotkey;
    private GlobalHotkey? trayGlobalHotkey;
    private GlobalHotkey? volumeUpGlobalHotkey;
    private GlobalHotkey? volumeDownGlobalHotkey;
    private GlobalHotkey? toggleRecordingGlobalHotkey;
    private GlobalHotkey? remoteVolumeUpGlobalHotkey;
    private GlobalHotkey? remoteVolumeDownGlobalHotkey;
    private GlobalHotkey? remoteMuteToggleGlobalHotkey;
    private GlobalHotkey? systemVolumeUpGlobalHotkey;
    private GlobalHotkey? systemVolumeDownGlobalHotkey;
    private GlobalHotkey? systemMuteToggleGlobalHotkey;
    private GlobalHotkey? quickProfileSwitchGlobalHotkey;

    /// <summary>Optional log sink. MainForm wires this to <c>logFile.Event(...)</c> so each
    /// hotkey change writes a clear trail of "user opened capture", "captured X", "registered X
    /// successfully" / "registration FAILED with Win32 error N" to the diagnostic log. Lets
    /// us tell the difference between a capture that didn't fire, a save that didn't persist,
    /// and a Windows-side RegisterHotKey rejection.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Optional callback fired when the user successfully captures and saves a new
    /// hotkey via the Keyboard shortcuts dialog. MainForm wires this to MarkProfileDirty so
    /// the unsaved-changes prompt fires on close and the user gets a Save reminder. Without
    /// this hook, hotkey edits silently bypass the dirty-flag and the user finds out their
    /// new bindings never made it into the profile JSON.</summary>
    public Action? OnHotkeyChanged { get; set; }

    public MainFormHotkeyController(
        RemSoundSettingsStore settingsStore,
        Action toggleSend,
        Action toggleReceive,
        Action toggleTray,
        Action volumeUp,
        Action volumeDown,
        Action toggleRecording,
        Action sendRemoteVolumeUp,
        Action sendRemoteVolumeDown,
        Action sendRemoteMuteToggle,
        Action sendSystemVolumeUp,
        Action sendSystemVolumeDown,
        Action sendSystemMuteToggle,
        Action quickProfileSwitch)
    {
        this.settingsStore = settingsStore;
        this.toggleSend = toggleSend;
        this.toggleReceive = toggleReceive;
        this.toggleTray = toggleTray;
        this.volumeUp = volumeUp;
        this.volumeDown = volumeDown;
        this.toggleRecording = toggleRecording;
        this.sendRemoteVolumeUp = sendRemoteVolumeUp;
        this.sendRemoteVolumeDown = sendRemoteVolumeDown;
        this.sendRemoteMuteToggle = sendRemoteMuteToggle;
        this.sendSystemVolumeUp = sendSystemVolumeUp;
        this.sendSystemVolumeDown = sendSystemVolumeDown;
        this.sendSystemMuteToggle = sendSystemMuteToggle;
        this.quickProfileSwitch = quickProfileSwitch;
        sendMuteHotkey = settingsStore.LoadSendMuteHotkey();
        receiveMuteHotkey = settingsStore.LoadReceiveMuteHotkey();
        trayHotkey = settingsStore.LoadTrayHotkey();
        volumeUpHotkey = settingsStore.LoadVolumeUpHotkey();
        volumeDownHotkey = settingsStore.LoadVolumeDownHotkey();
        toggleRecordingHotkey = settingsStore.LoadToggleRecordingHotkey();
        remoteVolumeUpHotkey = settingsStore.LoadRemoteVolumeUpHotkey();
        remoteVolumeDownHotkey = settingsStore.LoadRemoteVolumeDownHotkey();
        remoteMuteToggleHotkey = settingsStore.LoadRemoteMuteToggleHotkey();
        systemVolumeUpHotkey = settingsStore.LoadSystemVolumeUpHotkey();
        systemVolumeDownHotkey = settingsStore.LoadSystemVolumeDownHotkey();
        systemMuteToggleHotkey = settingsStore.LoadSystemMuteToggleHotkey();
        quickProfileSwitchHotkey = settingsStore.LoadQuickProfileSwitchHotkey();
    }

    public void Initialize(Form ownerForm)
    {
        owner = ownerForm;
        sendMuteGlobalHotkey = new GlobalHotkey(ownerForm);
        receiveMuteGlobalHotkey = new GlobalHotkey(ownerForm);
        trayGlobalHotkey = new GlobalHotkey(ownerForm);
        volumeUpGlobalHotkey = new GlobalHotkey(ownerForm);
        volumeDownGlobalHotkey = new GlobalHotkey(ownerForm);
        toggleRecordingGlobalHotkey = new GlobalHotkey(ownerForm);
        remoteVolumeUpGlobalHotkey = new GlobalHotkey(ownerForm);
        remoteVolumeDownGlobalHotkey = new GlobalHotkey(ownerForm);
        remoteMuteToggleGlobalHotkey = new GlobalHotkey(ownerForm);
        systemVolumeUpGlobalHotkey = new GlobalHotkey(ownerForm);
        systemVolumeDownGlobalHotkey = new GlobalHotkey(ownerForm);
        systemMuteToggleGlobalHotkey = new GlobalHotkey(ownerForm);
        quickProfileSwitchGlobalHotkey = new GlobalHotkey(ownerForm);
        sendMuteGlobalHotkey.Pressed += () => InvokeOnOwner(toggleSend);
        receiveMuteGlobalHotkey.Pressed += () => InvokeOnOwner(toggleReceive);
        trayGlobalHotkey.Pressed += () => InvokeOnOwner(toggleTray);
        volumeUpGlobalHotkey.Pressed += () => InvokeOnOwner(volumeUp);
        volumeDownGlobalHotkey.Pressed += () => InvokeOnOwner(volumeDown);
        toggleRecordingGlobalHotkey.Pressed += () => InvokeOnOwner(toggleRecording);
        remoteVolumeUpGlobalHotkey.Pressed += () => InvokeOnOwner(sendRemoteVolumeUp);
        remoteVolumeDownGlobalHotkey.Pressed += () => InvokeOnOwner(sendRemoteVolumeDown);
        remoteMuteToggleGlobalHotkey.Pressed += () => InvokeOnOwner(sendRemoteMuteToggle);
        systemVolumeUpGlobalHotkey.Pressed += () => InvokeOnOwner(sendSystemVolumeUp);
        systemVolumeDownGlobalHotkey.Pressed += () => InvokeOnOwner(sendSystemVolumeDown);
        systemMuteToggleGlobalHotkey.Pressed += () => InvokeOnOwner(sendSystemMuteToggle);
        quickProfileSwitchGlobalHotkey.Pressed += () => InvokeOnOwner(quickProfileSwitch);
        RegisterSendMuteHotkey();
        RegisterReceiveMuteHotkey();
        RegisterTrayHotkey();
        RegisterVolumeUpHotkey();
        RegisterVolumeDownHotkey();
        RegisterToggleRecordingHotkey();
        RegisterRemoteVolumeUpHotkey();
        RegisterRemoteVolumeDownHotkey();
        RegisterRemoteMuteToggleHotkey();
        RegisterSystemVolumeUpHotkey();
        RegisterSystemVolumeDownHotkey();
        RegisterSystemMuteToggleHotkey();
        RegisterQuickProfileSwitchHotkey();
    }

    public void ShowKeyboardShortcutsDialog(IWin32Window dialogOwner)
    {
        // Modeled on the SpaceBlaster menu dialogs:
        //   * A ListBox fills the dialog. Each row is one bindable hotkey shown as
        //     "Action: current binding" — self-describing for NVDA on arrow-up/down.
        //   * Enter on the list (or double-click) → opens the capture form for that row.
        //   * Escape (or the Close button) closes the dialog.
        //   * Tab cycles list → Close button. No "Change selected" intermediate button —
        //     2026-05-08 cleanup; the workflow is "arrow + Enter" exclusively, removing
        //     the extra Tab-to-button step the user had to make for every change.
        //
        // Why a ListBox instead of one Button per row: the hotkey count grew to eleven
        // (5 local + 3 remote-app + 3 system-volume) and the per-row Button stack made
        // arrow-key / Tab navigation slow. ListBox is one focusable control with native
        // arrow-key navigation and NVDA reads each item as the selection moves — much
        // quicker to triage which binding you want to change.
        // CmdKeyForm gives us a ProcessCmdKey hook that runs BEFORE the form's
        // ProcessDialogKey path (which is what would fire AcceptButton on Enter). We
        // need that to make Enter-on-the-list rebind a hotkey instead of closing the
        // dialog. Without this, AcceptButton swallowed Enter regardless of which
        // control had focus and the user got bounced straight back to the Profiles
        // and preferences tab. (KeyPreview + the form-level KeyDown wasn't enough on
        // its own — that fires AFTER ProcessCmdKey/ProcessDialogKey, so AcceptButton
        // had already won.)
        using var dialog = new CmdKeyForm
        {
            Text = "Keyboard shortcuts",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            KeyPreview = true, // form-level Esc handler
            ClientSize = new Size(640, 440),
        };

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 3, // 0 intro, 1 list, 2 buttons
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var introLabel = new Label
        {
            Text = "Arrow up and down to pick a shortcut. Press Enter to rebind it, Del to clear it. Escape closes the dialog.\n\n"
                 + "The remote-control rows send commands to connected peers; they only have an effect on peers that have 'Accept remote volume commands from peers' enabled.",
            AutoSize = true,
            MaximumSize = new Size(600, 0),
            Anchor = AnchorStyles.Left,
        };
        root.Controls.Add(introLabel, 0, 0);

        var list = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            // NVDA reads this on first focus, then the per-item text on each arrow move.
            AccessibleName = "Keyboard shortcuts",
            TabIndex = 0,
        };
        root.Controls.Add(list, 0, 1);

        var buttonsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0),
        };
        var closeButton = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.OK, TabIndex = 1 };
        buttonsPanel.Controls.Add(closeButton);
        root.Controls.Add(buttonsPanel, 0, 2);

        dialog.Controls.Add(root);

        // The list rows correspond to the order below. Index → which Change* helper to call.
        // Stable ordering keeps the user's muscle memory between sessions: local hotkeys
        // first, then the remote-app trio, then the Windows system-volume trio.
        void RefreshList()
        {
            var prev = list.SelectedIndex;
            list.BeginUpdate();
            list.Items.Clear();
            // Order matches the case-block dispatchers in ChangeSelected and UnsetSelected
            // below. Local-action hotkeys first (rows 0..5: send / receive / tray / volume×2 /
            // recording), then the remote-app trio (rows 6..8), then the Windows-system trio
            // (rows 9..11). Toggle recording joined the local group at index 5 in v1.5
            // (2026-05-15) — natural fit alongside the other "this machine" toggles.
            list.Items.Add($"Toggle sending audio: {sendMuteHotkey}");
            list.Items.Add($"Toggle receiving audio: {receiveMuteHotkey}");
            list.Items.Add($"Show or hide window: {trayHotkey}");
            list.Items.Add($"Volume up for received sound on this machine: {volumeUpHotkey}");
            list.Items.Add($"Volume down for received sound on this machine: {volumeDownHotkey}");
            list.Items.Add($"Start / Stop recording: {toggleRecordingHotkey}");
            list.Items.Add($"Send remote volume up to peers: {remoteVolumeUpHotkey}");
            list.Items.Add($"Send remote volume down to peers: {remoteVolumeDownHotkey}");
            list.Items.Add($"Send remote receive mute toggle to peers: {remoteMuteToggleHotkey}");
            list.Items.Add($"Send Windows global volume up to peers: {systemVolumeUpHotkey}");
            list.Items.Add($"Send Windows global volume down to peers: {systemVolumeDownHotkey}");
            list.Items.Add($"Send Windows global mute toggle to peers: {systemMuteToggleHotkey}");
            list.Items.Add($"Quick profile switch (open a list of all profiles): {quickProfileSwitchHotkey}");
            if (prev >= 0 && prev < list.Items.Count)
            {
                list.SelectedIndex = prev;
            }
            else if (list.Items.Count > 0)
            {
                list.SelectedIndex = 0;
            }
            list.EndUpdate();
        }

        void ChangeSelected()
        {
            // Pass `dialog` (the shortcuts dialog itself) as the modal owner of the
            // HotkeyCaptureForm — NOT the original `dialogOwner` (which is MainForm).
            // Original bug: when MainForm was the owner, the new capture form was modal
            // to MainForm rather than to the shortcuts dialog, which (a) put it behind
            // the still-modal-to-MainForm shortcuts dialog in the Z-order, sometimes
            // invisibly so, and (b) created two parallel modal-to-MainForm chains. With
            // `dialog` as the owner, the capture form sits cleanly on top of the
            // shortcuts dialog, the shortcuts dialog is correctly disabled while it's
            // showing, and focus returns to the shortcuts list when it closes.
            switch (list.SelectedIndex)
            {
                case 0: ChangeSendMuteHotkey(dialog); break;
                case 1: ChangeReceiveMuteHotkey(dialog); break;
                case 2: ChangeTrayHotkey(dialog); break;
                case 3: ChangeVolumeUpHotkey(dialog); break;
                case 4: ChangeVolumeDownHotkey(dialog); break;
                case 5: ChangeToggleRecordingHotkey(dialog); break;
                case 6: ChangeRemoteVolumeUpHotkey(dialog); break;
                case 7: ChangeRemoteVolumeDownHotkey(dialog); break;
                case 8: ChangeRemoteMuteToggleHotkey(dialog); break;
                case 9: ChangeSystemVolumeUpHotkey(dialog); break;
                case 10: ChangeSystemVolumeDownHotkey(dialog); break;
                case 11: ChangeSystemMuteToggleHotkey(dialog); break;
                case 12: ChangeQuickProfileSwitchHotkey(dialog); break;
                default: return;
            }
            RefreshList();
            // Move focus back to the list so the user can immediately arrow to another
            // row without an extra Tab. Without this, focus stays on the Change button
            // (which is what was clicked / Enter'd) — which is fine but feels sticky.
            list.Focus();
        }

        // Clear the selected row's binding (set it to "(not set)"). Mirrors the same path
        // the rebinding flow uses: writes Unset into the in-memory settings cache, calls
        // RegisterIfSet (which unregisters because the new info IsUnset), marks the
        // profile dirty, and refreshes the list display. NB: we do this from the list's
        // KeyDown rather than ProcessCmdKey because Del isn't intercepted by the form-
        // level AcceptButton dance and works fine via the standard event.
        void UnsetSelected()
        {
            switch (list.SelectedIndex)
            {
                case 0: ApplyUnset("send-mute", h => sendMuteHotkey = h, RegisterSendMuteHotkey, settingsStore.SaveSendMuteHotkey); break;
                case 1: ApplyUnset("receive-mute", h => receiveMuteHotkey = h, RegisterReceiveMuteHotkey, settingsStore.SaveReceiveMuteHotkey); break;
                case 2: ApplyUnset("tray", h => trayHotkey = h, RegisterTrayHotkey, settingsStore.SaveTrayHotkey); break;
                case 3: ApplyUnset("volume-up", h => volumeUpHotkey = h, RegisterVolumeUpHotkey, settingsStore.SaveVolumeUpHotkey); break;
                case 4: ApplyUnset("volume-down", h => volumeDownHotkey = h, RegisterVolumeDownHotkey, settingsStore.SaveVolumeDownHotkey); break;
                case 5: ApplyUnset("toggle-recording", h => toggleRecordingHotkey = h, RegisterToggleRecordingHotkey, settingsStore.SaveToggleRecordingHotkey); break;
                case 6: ApplyUnset("send-remote-volume-up", h => remoteVolumeUpHotkey = h, RegisterRemoteVolumeUpHotkey, settingsStore.SaveRemoteVolumeUpHotkey); break;
                case 7: ApplyUnset("send-remote-volume-down", h => remoteVolumeDownHotkey = h, RegisterRemoteVolumeDownHotkey, settingsStore.SaveRemoteVolumeDownHotkey); break;
                case 8: ApplyUnset("send-remote-mute-toggle", h => remoteMuteToggleHotkey = h, RegisterRemoteMuteToggleHotkey, settingsStore.SaveRemoteMuteToggleHotkey); break;
                case 9: ApplyUnset("send-system-volume-up", h => systemVolumeUpHotkey = h, RegisterSystemVolumeUpHotkey, settingsStore.SaveSystemVolumeUpHotkey); break;
                case 10: ApplyUnset("send-system-volume-down", h => systemVolumeDownHotkey = h, RegisterSystemVolumeDownHotkey, settingsStore.SaveSystemVolumeDownHotkey); break;
                case 11: ApplyUnset("send-system-mute-toggle", h => systemMuteToggleHotkey = h, RegisterSystemMuteToggleHotkey, settingsStore.SaveSystemMuteToggleHotkey); break;
                case 12: ApplyUnset("quick-profile-switch", h => quickProfileSwitchHotkey = h, RegisterQuickProfileSwitchHotkey, settingsStore.SaveQuickProfileSwitchHotkey); break;
                default: return;
            }
            RefreshList();
            list.Focus();
        }

        // Helper for UnsetSelected — assign Unset to the field, re-register (which
        // unregisters since IsUnset is true), persist to the settings cache, log, and
        // mark the profile dirty so the close-prompt fires.
        void ApplyUnset(string description, Action<HotkeyInfo> setField, Action register, Action<HotkeyInfo> save)
        {
            setField(HotkeyInfo.Unset);
            register();
            save(HotkeyInfo.Unset);
            Log?.Invoke($"unset {description}: cleared (was bound, now (not set))");
            OnHotkeyChanged?.Invoke();
        }

        // Enter-on-the-list rebinds via ProcessCmdKey at the form, so that the form's
        // AcceptButton dispatch (Close) doesn't get the keystroke first. ProcessCmdKey
        // runs ahead of ProcessDialogKey in WinForms' message pipeline; returning true
        // marks the key as consumed and the AcceptButton path is skipped. When focus
        // is anywhere else (e.g. the Close button) we let Enter fall through, so
        // Tab-to-Close + Enter still closes the dialog naturally.
        dialog.CmdKeyHandler = keyData =>
        {
            if (keyData != Keys.Enter) return false;
            if (dialog.ActiveControl != list) return false;
            ChangeSelected();
            return true;
        };
        list.DoubleClick += (_, _) => ChangeSelected();
        // Del on the list clears the highlighted binding back to "(not set)". No confirm
        // dialog — the user can rebind in two key presses (Enter + capture) if they hit Del
        // by mistake. Mirrors the SpaceBlaster-style "list + Del" idiom Ed asked for.
        list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete)
            {
                UnsetSelected();
                e.SuppressKeyPress = true;
                e.Handled = true;
            }
        };

        dialog.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                dialog.DialogResult = DialogResult.Cancel;
                dialog.Close();
                e.SuppressKeyPress = true;
                e.Handled = true;
            }
        };

        RefreshList();
        // Enter on Close closes — works because list KeyDown above handled Enter when
        // focus was on the list. AcceptButton fires only when no control consumed Enter.
        dialog.AcceptButton = closeButton;
        dialog.CancelButton = closeButton;
        dialog.Load += (_, _) => list.Focus();
        dialog.ShowDialog(dialogOwner);
    }

    public void Dispose()
    {
        sendMuteGlobalHotkey?.Dispose();
        receiveMuteGlobalHotkey?.Dispose();
        trayGlobalHotkey?.Dispose();
        volumeUpGlobalHotkey?.Dispose();
        volumeDownGlobalHotkey?.Dispose();
        toggleRecordingGlobalHotkey?.Dispose();
        remoteVolumeUpGlobalHotkey?.Dispose();
        remoteVolumeDownGlobalHotkey?.Dispose();
        remoteMuteToggleGlobalHotkey?.Dispose();
        systemVolumeUpGlobalHotkey?.Dispose();
        systemVolumeDownGlobalHotkey?.Dispose();
        systemMuteToggleGlobalHotkey?.Dispose();
        quickProfileSwitchGlobalHotkey?.Dispose();
    }

    public HotkeyInfo SendMuteHotkey => sendMuteHotkey;
    public HotkeyInfo ReceiveMuteHotkey => receiveMuteHotkey;
    public HotkeyInfo TrayHotkey => trayHotkey;
    public HotkeyInfo VolumeUpHotkey => volumeUpHotkey;
    public HotkeyInfo VolumeDownHotkey => volumeDownHotkey;
    public HotkeyInfo ToggleRecordingHotkey => toggleRecordingHotkey;
    public HotkeyInfo RemoteVolumeUpHotkey => remoteVolumeUpHotkey;
    public HotkeyInfo RemoteVolumeDownHotkey => remoteVolumeDownHotkey;
    public HotkeyInfo RemoteMuteToggleHotkey => remoteMuteToggleHotkey;
    public HotkeyInfo SystemVolumeUpHotkey => systemVolumeUpHotkey;
    public HotkeyInfo SystemVolumeDownHotkey => systemVolumeDownHotkey;
    public HotkeyInfo SystemMuteToggleHotkey => systemMuteToggleHotkey;
    public HotkeyInfo QuickProfileSwitchHotkey => quickProfileSwitchHotkey;

    /// <summary>Open the capture dialog, log what came back, and (on a successful capture)
    /// run <paramref name="apply"/> with the captured hotkey. Centralises the boilerplate
    /// the eleven per-row Change methods used to duplicate. The <paramref name="description"/>
    /// is what shows in the diagnostic log so a user / developer can see the trail of
    /// "capture send-system-volume-down: OK = Ctrl+Shift+Alt+J / register …: OK" or
    /// "capture …: cancelled (DialogResult=Cancel)" / "register …: FAILED Win32 1409".</summary>
    private void ChangeHotkey(IWin32Window dialogOwner, string description, Action<HotkeyInfo> apply)
    {
        using var dialog = new HotkeyCaptureForm();
        var result = dialog.ShowDialog(dialogOwner);
        if (result == DialogResult.OK && dialog.CapturedHotkey is not null)
        {
            Log?.Invoke($"capture {description}: OK = {dialog.CapturedHotkey}");
            apply(dialog.CapturedHotkey);
            // Mark the active profile dirty so the unsaved-changes prompt fires on close.
            // The previous design relied on MarkProfileDirty being called from each UI event
            // hook in MainForm — but the hotkey controller is its own object that doesn't
            // know about that flag. Without this callback, hotkey edits silently slipped
            // past the dirty-check and the user closed without being prompted to save.
            OnHotkeyChanged?.Invoke();
        }
        else
        {
            // Detect the "low-level hook ate your combination" case. If the capture form
            // observed modifier presses but never received the non-modifier key the user
            // was trying to bind, something else (NVDA / NVDA Remote / AutoHotkey / similar
            // accessibility / hotkey-manager tool that hooks at WH_KEYBOARD_LL level) is
            // intercepting the combination before Windows can deliver it to our window.
            // RegisterHotKey would have succeeded if we'd ever reached that point, so the
            // existing 1409-style warning never fires for this case — that's why the user
            // saw "no popup" even though their combination genuinely was unusable.
            //
            // The popup is shown TopMost via the same Win32 path the register-warning uses,
            // so it's guaranteed visible regardless of modal-stack Z-order.
            Log?.Invoke($"capture {description}: cancelled (DialogResult={result}, sawModifier={dialog.SawAnyModifier}, sawNonModifier={dialog.SawAnyNonModifier})");
            if (dialog.SawAnyModifier && !dialog.SawAnyNonModifier)
            {
                Log?.Invoke($"capture {description}: warning user about likely low-level hook interception");
                ShowRegisterWarning(
                    "RemSound saw your modifier keys (Ctrl, Shift, Alt) but never received the non-modifier key you were pressing with them.\n\n"
                    + "That almost always means another app on this machine — NVDA, NVDA Remote, AutoHotkey, or a similar tool — is intercepting that key combination at a low level, before it can reach RemSound. The combination is unusable as a RemSound hotkey on this PC until the conflicting tool is reconfigured or that combination is freed up.\n\n"
                    + "Try a different key combination.");
            }
        }
    }

    private void ChangeSendMuteHotkey(IWin32Window dialogOwner) => ChangeHotkey(dialogOwner, "send-mute", h =>
    {
        sendMuteHotkey = h;
        RegisterSendMuteHotkey();
        settingsStore.SaveSendMuteHotkey(h);
    });

    private void ChangeReceiveMuteHotkey(IWin32Window dialogOwner) => ChangeHotkey(dialogOwner, "receive-mute", h =>
    {
        receiveMuteHotkey = h;
        RegisterReceiveMuteHotkey();
        settingsStore.SaveReceiveMuteHotkey(h);
    });

    private void ChangeTrayHotkey(IWin32Window dialogOwner) => ChangeHotkey(dialogOwner, "tray", h =>
    {
        trayHotkey = h;
        RegisterTrayHotkey();
        settingsStore.SaveTrayHotkey(h);
    });

    private void ChangeVolumeUpHotkey(IWin32Window dialogOwner) => ChangeHotkey(dialogOwner, "volume-up", h =>
    {
        volumeUpHotkey = h;
        RegisterVolumeUpHotkey();
        settingsStore.SaveVolumeUpHotkey(h);
    });

    private void ChangeVolumeDownHotkey(IWin32Window dialogOwner) => ChangeHotkey(dialogOwner, "volume-down", h =>
    {
        volumeDownHotkey = h;
        RegisterVolumeDownHotkey();
        settingsStore.SaveVolumeDownHotkey(h);
    });

    private void ChangeToggleRecordingHotkey(IWin32Window dialogOwner) => ChangeHotkey(dialogOwner, "toggle-recording", h =>
    {
        toggleRecordingHotkey = h;
        RegisterToggleRecordingHotkey();
        settingsStore.SaveToggleRecordingHotkey(h);
    });

    private void ChangeRemoteVolumeUpHotkey(IWin32Window dialogOwner) => ChangeHotkey(dialogOwner, "send-remote-volume-up", h =>
    {
        remoteVolumeUpHotkey = h;
        RegisterRemoteVolumeUpHotkey();
        settingsStore.SaveRemoteVolumeUpHotkey(h);
    });

    private void ChangeRemoteVolumeDownHotkey(IWin32Window dialogOwner) => ChangeHotkey(dialogOwner, "send-remote-volume-down", h =>
    {
        remoteVolumeDownHotkey = h;
        RegisterRemoteVolumeDownHotkey();
        settingsStore.SaveRemoteVolumeDownHotkey(h);
    });

    private void ChangeRemoteMuteToggleHotkey(IWin32Window dialogOwner) => ChangeHotkey(dialogOwner, "send-remote-mute-toggle", h =>
    {
        remoteMuteToggleHotkey = h;
        RegisterRemoteMuteToggleHotkey();
        settingsStore.SaveRemoteMuteToggleHotkey(h);
    });

    private void ChangeSystemVolumeUpHotkey(IWin32Window dialogOwner) => ChangeHotkey(dialogOwner, "send-system-volume-up", h =>
    {
        systemVolumeUpHotkey = h;
        RegisterSystemVolumeUpHotkey();
        settingsStore.SaveSystemVolumeUpHotkey(h);
    });

    private void ChangeSystemVolumeDownHotkey(IWin32Window dialogOwner) => ChangeHotkey(dialogOwner, "send-system-volume-down", h =>
    {
        systemVolumeDownHotkey = h;
        RegisterSystemVolumeDownHotkey();
        settingsStore.SaveSystemVolumeDownHotkey(h);
    });

    private void ChangeSystemMuteToggleHotkey(IWin32Window dialogOwner) => ChangeHotkey(dialogOwner, "send-system-mute-toggle", h =>
    {
        systemMuteToggleHotkey = h;
        RegisterSystemMuteToggleHotkey();
        settingsStore.SaveSystemMuteToggleHotkey(h);
    });

    private void ChangeQuickProfileSwitchHotkey(IWin32Window dialogOwner) => ChangeHotkey(dialogOwner, "quick-profile-switch", h =>
    {
        quickProfileSwitchHotkey = h;
        RegisterQuickProfileSwitchHotkey();
        settingsStore.SaveQuickProfileSwitchHotkey(h);
    });

    // Hotkeys come in two flavours and need different Windows-side registration:
    //   * Toggle hotkeys (mute, tray show/hide) — re-firing on hold would flip state back
    //     and forth. Registered with MOD_NOREPEAT (allowRepeat=false). One press, one fire.
    //   * Step hotkeys (volume up/down, both local-receive and remote-app and remote-system
    //     variants) — holding the key is the natural way to ramp through a range. Registered
    //     WITHOUT MOD_NOREPEAT so Windows fires WM_HOTKEY at the user's keyboard auto-repeat
    //     rate, exactly mirroring how the physical volume keys feel. The remote-control
    //     packet send-path is light-weight enough that a held key doesn't strain the link;
    //     the receiver-side COM volume call is hoisted out of the COM-enumeration cost via
    //     SystemVolumeHelper's cached endpoint reference.
    private void RegisterSendMuteHotkey() => RegisterIfSet(sendMuteGlobalHotkey, sendMuteHotkey, "toggle sending");
    private void RegisterReceiveMuteHotkey() => RegisterIfSet(receiveMuteGlobalHotkey, receiveMuteHotkey, "toggle receiving");
    private void RegisterTrayHotkey() => RegisterIfSet(trayGlobalHotkey, trayHotkey, "tray");
    private void RegisterVolumeUpHotkey() => RegisterIfSet(volumeUpGlobalHotkey, volumeUpHotkey, "volume up", allowRepeat: true);
    private void RegisterVolumeDownHotkey() => RegisterIfSet(volumeDownGlobalHotkey, volumeDownHotkey, "volume down", allowRepeat: true);
    // Toggle recording is a one-shot toggle (press → flip Start/Stop). MOD_NOREPEAT
    // (allowRepeat: false, the default) prevents a held key from flipping the recording
    // state on/off/on/off at keyboard auto-repeat rate.
    private void RegisterToggleRecordingHotkey() => RegisterIfSet(toggleRecordingGlobalHotkey, toggleRecordingHotkey, "toggle recording");
    private void RegisterRemoteVolumeUpHotkey() => RegisterIfSet(remoteVolumeUpGlobalHotkey, remoteVolumeUpHotkey, "send remote volume up", allowRepeat: true);
    private void RegisterRemoteVolumeDownHotkey() => RegisterIfSet(remoteVolumeDownGlobalHotkey, remoteVolumeDownHotkey, "send remote volume down", allowRepeat: true);
    private void RegisterRemoteMuteToggleHotkey() => RegisterIfSet(remoteMuteToggleGlobalHotkey, remoteMuteToggleHotkey, "send remote mute toggle");
    private void RegisterSystemVolumeUpHotkey() => RegisterIfSet(systemVolumeUpGlobalHotkey, systemVolumeUpHotkey, "send Windows global volume up", allowRepeat: true);
    private void RegisterSystemVolumeDownHotkey() => RegisterIfSet(systemVolumeDownGlobalHotkey, systemVolumeDownHotkey, "send Windows global volume down", allowRepeat: true);
    private void RegisterSystemMuteToggleHotkey() => RegisterIfSet(systemMuteToggleGlobalHotkey, systemMuteToggleHotkey, "send Windows global mute toggle");
    // Quick profile switch is a one-shot (press → open the popup); MOD_NOREPEAT (the default) keeps
    // a held key from re-opening it repeatedly.
    private void RegisterQuickProfileSwitchHotkey() => RegisterIfSet(quickProfileSwitchGlobalHotkey, quickProfileSwitchHotkey, "quick profile switch");

    private void RegisterIfSet(GlobalHotkey? globalHotkey, HotkeyInfo hotkey, string description, bool allowRepeat = false)
    {
        if (globalHotkey is null) return;
        globalHotkey.Unregister();
        if (hotkey.IsUnset)
        {
            Log?.Invoke($"register {description}: SKIPPED (unset)");
            return;
        }
        if (globalHotkey.Register(hotkey, allowRepeat))
        {
            Log?.Invoke($"register {description}: OK = {hotkey}");
        }
        else
        {
            // Win32 error 1409 = ERROR_HOTKEY_ALREADY_REGISTERED. Anything else is unusual
            // (e.g. invalid VK code, no handle). Logging the raw code lets us distinguish
            // "another app/process owns this combo" from genuine registration weirdness.
            var err = globalHotkey.LastWin32ErrorOnRegister;
            var hint = err switch
            {
                1409 => "another app or another RemSound process already registered this combo",
                _ => "Win32 error",
            };
            Log?.Invoke($"register {description}: FAILED = {hotkey} (Win32 error {err}: {hint})");
            ShowRegisterWarning($"Could not register {description} hotkey {hotkey}. " + (err == 1409
                ? "Another app — or another running copy of RemSound — is already using that combo. The hotkey is saved in your profile, so the binding will take effect once the conflict is resolved."
                : $"Windows reported error {err}. The hotkey is saved in your profile but Windows didn't accept the registration."));
        }
    }

    private void InvokeOnOwner(Action action)
    {
        if (owner is null || owner.IsDisposed) return;
        owner.BeginInvoke(action);
    }

    private void ShowRegisterWarning(string message)
    {
        // Use the Win32 MessageBox API directly with MB_TOPMOST + MB_SETFOREGROUND so the
        // popup is guaranteed to sit above every other window on the desktop, including
        // any modal dialog stack RemSound currently has open. The previous WinForms
        // MessageBox.Show(parent, …) calls were sometimes hiding behind the still-modal
        // Keyboard shortcuts dialog — the user reported "no popup" when in fact the popup
        // had been created and then occluded.
        //
        // MB_SETFOREGROUND on its own is sometimes ignored by Windows under foreground-lock
        // rules, but MB_TOPMOST overrides that. Together they're the most reliable way to
        // get a hotkey-conflict warning into the user's face at the moment the conflict is
        // detected.
        var hwnd = (Form.ActiveForm?.Handle) ?? owner?.Handle ?? IntPtr.Zero;
        const uint MB_OK = 0x00000000;
        const uint MB_ICONWARNING = 0x00000030;
        const uint MB_TOPMOST = 0x00040000;
        const uint MB_SETFOREGROUND = 0x00010000;
        MessageBoxW(hwnd, message, "RemSound — hotkey conflict", MB_OK | MB_ICONWARNING | MB_TOPMOST | MB_SETFOREGROUND);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);
}
