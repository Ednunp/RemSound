namespace RemSound.App;

/// <summary>
/// The Web listener window (Ed, 2026-10-10), opened from the Web listener button on the Connectivity tab. In order: the
/// tick box that lets a web browser listen through Tailscale Funnel, the password box, the tick box that has the page start
/// by itself when it loads (Ed, 2026-10-11), this computer's web address with
/// Copy address and Turn on Tailscale Funnel beside it, a status box saying how many browsers are listening, then OK and
/// Cancel. The tick and the password are saved in the profile.
///
/// <para>What's in the password box is the password, shown, and it is set as you move out of the box (Ed: "once you
/// press tab away from that box or move away from it, password is set"). The tick box and the password take effect at
/// once, so they can be tried while the window is open; OK keeps them, and Cancel (or Escape, or closing the window)
/// puts back what they were when it opened.</para>
///
/// <para>The address and the Funnel button (Ed: "show the computer's address there somewhere with a button to copy to
/// clipboard ... and some way of quickly sending you to tailscale funnel"): Tailscale is asked once as the window opens
/// and again after the button, never on a timer. The button runs Tailscale's own command; the first time, Tailscale
/// answers with a page to approve Funnel for the account, which opens in the browser.</para>
/// </summary>
internal sealed class WebListenerDialog : Form
{
    /// <summary>How the window reaches Tailscale. A seam: the gate gives it pretend answers, so no test ever runs Tailscale.</summary>
    internal sealed record FunnelHooks(
        Func<CancellationToken, Task<TailscaleFunnel.State>> Query,
        Func<Action<string>, CancellationToken, Task<(bool Ok, string Message)>> TurnOn,
        Action<string> OpenPage);

    private readonly AccessibleCheckBox enableBox = new()
    {
        Text = "Let a &web browser listen to what I send, through Tailscale Funnel (Alt+W)",
        AccessibleName = "Let a web browser listen to what I send, through Tailscale Funnel",
        AutoSize = true,
    };

    private readonly TextBox passwordBox = new()
    {
        Width = Dpi.Px(320),
        AccessibleName = "Password",
        // A password field: the key clicks add the password sound, and the control channel never reads it back.
        Tag = KeyClickService.PasswordFieldTag,
    };

    private readonly AccessibleCheckBox autoStartBox = new()
    {
        Text = "Start listening when the web page &loads (Alt+L)",
        AccessibleName = "Start listening when the web page loads",
        AutoSize = true,
    };

    private readonly TextBox addressBox = new()
    {
        ReadOnly = true,
        Width = Dpi.Px(420),
        AccessibleName = "Web address",
        TabStop = true,
    };

    private readonly Button copyButton = new() { Text = "Cop&y address (Alt+Y)", AutoSize = true, AccessibleName = "Copy address" };
    private readonly Button funnelButton = new() { Text = "Turn on Tailscale &Funnel (Alt+F)", AutoSize = true, AccessibleName = "Turn on Tailscale Funnel" };

    private readonly TextBox statusBox = new()
    {
        ReadOnly = true,
        Multiline = true,
        Width = Dpi.Px(420),
        Height = Dpi.Px(64),
        AccessibleName = "Status",
        TabStop = true,
    };

    private readonly Button okButton = new() { Text = "&OK", AutoSize = true };
    private readonly Button cancelButton = new() { Text = "&Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 1000 };
    private readonly CancellationTokenSource closing = new();

    internal const string NeedsPasswordText = "The web listener needs a password. Type one in the Password box, or untick Let a web browser listen.";

    private readonly Func<string> status;
    private readonly Action<bool, string, bool> apply;
    private readonly FunnelHooks? funnel;
    private readonly bool openedEnabled;
    private readonly bool openedAutoStart;
    private readonly string openedPassword;
    private string committedPassword;
    private TailscaleFunnel.State? funnelState;
    private string funnelNote = "";

    /// <summary>What the profile has when the window closes: OK keeps the changes, anything else puts back what it opened with.</summary>
    public bool ListenEnabled => DialogResult == DialogResult.OK ? enableBox.Checked : openedEnabled;
    public string Password => DialogResult == DialogResult.OK ? committedPassword : openedPassword;
    public bool AutoStart => DialogResult == DialogResult.OK ? autoStartBox.Checked : openedAutoStart;
    public bool Changed => DialogResult == DialogResult.OK && !SameAsOpened;
    private bool SameAsOpened => enableBox.Checked == openedEnabled && committedPassword == openedPassword && autoStartBox.Checked == openedAutoStart;

    /// <param name="status">The running listener's account of itself (on or off, port, browsers listening).</param>
    /// <param name="apply">Puts the tick, the password and the start-by-itself tick in force at once (the main window saves
    /// them in the profile and opens, changes or closes the listener).</param>
    /// <param name="funnel">How to reach Tailscale; null when there is nothing to ask (the address then says so).</param>
    public WebListenerDialog(bool enabled, string password, bool autoStart, Func<string> status, Action<bool, string, bool> apply, FunnelHooks? funnel = null)
    {
        this.status = status;
        this.apply = apply;
        this.funnel = funnel;
        openedEnabled = enabled;
        openedPassword = committedPassword = password;
        openedAutoStart = autoStart;

        Text = "Web listener";
        AccessibleName = "Web listener";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        enableBox.Checked = enabled;
        passwordBox.Text = password;
        autoStartBox.Checked = autoStart;
        addressBox.Text = funnel is null ? "Not known." : "Asking Tailscale...";

        var passwordLabel = new MnemonicLabel { Text = "&Password (Alt+P):", AutoSize = true, MnemonicTarget = passwordBox, Padding = Dpi.Pad(0, 8, 0, 0) };
        passwordLabel.Click += (_, _) => passwordBox.Focus();
        var addressLabel = new MnemonicLabel { Text = "Web &address (Alt+A):", AutoSize = true, MnemonicTarget = addressBox, Padding = Dpi.Pad(0, 8, 0, 0) };
        addressLabel.Click += (_, _) => addressBox.Focus();
        var statusLabel = new MnemonicLabel { Text = "&Status (Alt+S):", AutoSize = true, MnemonicTarget = statusBox, Padding = Dpi.Pad(0, 8, 0, 0) };
        statusLabel.Click += (_, _) => statusBox.Focus();

        var addressButtons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Dock = DockStyle.Fill };
        addressButtons.Controls.Add(copyButton);
        addressButtons.Controls.Add(funnelButton);
        copyButton.TabIndex = 0;
        funnelButton.TabIndex = 1;

        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = Dpi.Pad(12), ColumnCount = 1, RowCount = 10, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.Controls.Add(enableBox, 0, 0);
        panel.Controls.Add(passwordLabel, 0, 1);
        panel.Controls.Add(passwordBox, 0, 2);
        panel.Controls.Add(autoStartBox, 0, 3);
        panel.Controls.Add(addressLabel, 0, 4);
        panel.Controls.Add(addressBox, 0, 5);
        panel.Controls.Add(addressButtons, 0, 6);
        panel.Controls.Add(statusLabel, 0, 7);
        panel.Controls.Add(statusBox, 0, 8);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        // OK then Cancel, as Windows orders them: right to left, so Cancel goes in first; OK first for Tab too.
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(okButton);
        okButton.TabIndex = 0;
        cancelButton.TabIndex = 1;
        panel.Controls.Add(buttons, 0, 9);
        // Tab order: tick box, password, start by itself, address, Copy address / Turn on Funnel, status, OK / Cancel. The
        // two button rows are wrapped, so the wrapper carries the order.
        enableBox.TabIndex = 0;
        passwordLabel.TabIndex = 1;
        passwordBox.TabIndex = 2;
        autoStartBox.TabIndex = 3;
        addressLabel.TabIndex = 4;
        addressBox.TabIndex = 5;
        addressButtons.TabIndex = 6;
        statusLabel.TabIndex = 7;
        statusBox.TabIndex = 8;
        buttons.TabIndex = 9;
        Controls.Add(panel);
        AcceptButton = okButton;
        CancelButton = cancelButton;

        enableBox.CheckedChanged += (_, _) =>
        {
            apply(enableBox.Checked, committedPassword, autoStartBox.Checked);
            RefreshStatus();
        };
        autoStartBox.CheckedChanged += (_, _) => apply(enableBox.Checked, committedPassword, autoStartBox.Checked);
        passwordBox.Validated += (_, _) => CommitPassword();
        copyButton.Click += (_, _) => CopyAddress();
        funnelButton.Click += async (_, _) => await TurnOnFunnelAsync();
        cancelButton.Click += (_, _) => Close();
        okButton.Click += (_, _) =>
        {
            CommitPassword();   // a password still being typed counts
            // The web listener always needs a password (Ed, 2026-10-12: "you should always need a password if you set up web
            // listener"): OK with the tick on and no password says so and goes to the password box.
            if (enableBox.Checked && committedPassword.Length == 0)
            {
                AppMessageBox.Show(this, NeedsPasswordText, "Password needed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                // The window's own focus, so it is the password box the window comes back to, active or not.
                ActiveControl = passwordBox;
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        };
        FormClosing += (_, _) =>
        {
            refresh.Stop();
            closing.Cancel();
            // Cancel, Escape or the close box: back to what it opened with, if anything changed.
            if (DialogResult != DialogResult.OK && !SameAsOpened)
                apply(openedEnabled, openedPassword, openedAutoStart);
        };
        refresh.Tick += (_, _) => RefreshStatus();
        Shown += async (_, _) =>
        {
            refresh.Start();
            await AskTailscaleAsync();
        };

        ContextHelp.Mark(enableBox, "dialog.web-listener.enable");
        ContextHelp.Mark(passwordBox, "dialog.web-listener.password");
        ContextHelp.Mark(autoStartBox, "dialog.web-listener.auto-start");
        ContextHelp.Mark(addressBox, "dialog.web-listener.address");
        ContextHelp.Mark(copyButton, "dialog.web-listener.copy-address");
        ContextHelp.Mark(funnelButton, "dialog.web-listener.funnel");
        ContextHelp.Mark(statusBox, "dialog.web-listener.status");
        ContextHelp.Mark(okButton, "dialog.web-listener.ok");
        ContextHelp.Mark(cancelButton, "dialog.web-listener.cancel");
        RefreshStatus();
    }

    /// <summary>What's in the box is the password, trimmed as every RemSound password is, set on leaving the box.</summary>
    private void CommitPassword()
    {
        var typed = passwordBox.Text.Trim();
        if (typed == committedPassword) return;
        committedPassword = typed;
        apply(enableBox.Checked, committedPassword, autoStartBox.Checked);
        RefreshStatus();
    }

    /// <summary>Ask Tailscale where the page is and whether Funnel is on, and show it.</summary>
    private async Task AskTailscaleAsync()
    {
        if (funnel is null) return;
        try
        {
            var state = await funnel.Query(closing.Token);
            if (IsDisposed) return;
            funnelState = state;
            addressBox.Text = state.Address is { } address
                ? state.On ? address : address + " (once Tailscale Funnel is on)"
                : "None yet.";
            funnelNote = state.Note;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            addressBox.Text = "Not known.";
            funnelNote = "Tailscale could not be asked: " + ex.Message;
        }
        RefreshStatus();
    }

    private void CopyAddress()
    {
        if (funnelState is not { On: true, Address: { } address })
        {
            AppMessageBox.Show(this, funnelState is { Installed: false }
                    ? "There's no web address yet: Tailscale isn't installed on this computer. Turn on Tailscale Funnel takes you to its download page."
                    : "There's no web address yet. Press Turn on Tailscale Funnel first.",
                "No address yet", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            Clipboard.SetText(address);
            ScreenReader.Speak("Web address copied to the clipboard.");
        }
        catch (Exception ex)
        {
            AppMessageBox.Show(this, "The address could not be copied: " + ex.Message, "Copy address", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Turn on Funnel for the listener's port: Tailscale's own command, which the first time opens a page to
    /// approve Funnel for the account and waits for it. Without Tailscale, its download page instead.</summary>
    private async Task TurnOnFunnelAsync()
    {
        if (funnel is null) return;
        if (funnelState is { Installed: false })
        {
            funnel.OpenPage(TailscaleFunnel.DownloadPage);
            return;
        }
        funnelButton.Enabled = false;
        funnelNote = "Turning on Tailscale Funnel...";
        RefreshStatus();
        try
        {
            void ShowPage(string page)
            {
                funnel.OpenPage(page);
                funnelNote = "Approve Tailscale Funnel in the page that opened in your browser. This window updates once you have.";
                RefreshStatus();
            }
            // Tailscale's output arrives on its own thread while it waits for the approval; anything else, here.
            var (ok, message) = await funnel.TurnOn(page => { if (InvokeRequired) BeginInvoke(() => ShowPage(page)); else ShowPage(page); }, closing.Token);
            if (IsDisposed) return;
            funnelNote = message;
            RefreshStatus();
            if (ok) await AskTailscaleAsync();
        }
        catch (OperationCanceledException) { return; }
        finally
        {
            if (!IsDisposed) funnelButton.Enabled = true;
        }
    }

    private void RefreshStatus()
    {
        var text = (enableBox.Checked ? status() : "Off.")
                   + (committedPassword.Length == 0 ? " No password is set, so no browser can listen." : " A password is set.")
                   + (funnelNote.Length > 0 ? "\r\n" + funnelNote : "");
        // Only when it changes, so a screen reader in the box isn't interrupted every second.
        if (statusBox.Text != text) statusBox.Text = text;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            refresh.Dispose();
            closing.Dispose();
        }
        base.Dispose(disposing);
    }

    internal AccessibleCheckBox EnableBoxForTest => enableBox;
    internal TextBox PasswordBoxForTest => passwordBox;
    internal AccessibleCheckBox AutoStartBoxForTest => autoStartBox;
    internal TextBox AddressBoxForTest => addressBox;
    internal TextBox StatusBoxForTest => statusBox;
    internal void RefreshStatusForTest() => RefreshStatus();
    internal Task AskTailscaleForTest() => AskTailscaleAsync();
    internal Task TurnOnFunnelForTest() => TurnOnFunnelAsync();
    internal void CopyAddressForTest() => CopyAddress();
}
