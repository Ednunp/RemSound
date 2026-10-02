using RemSound.Core;

namespace RemSound.App;

/// <summary>
/// Connecting to a server. Ed's design, 2026-09-20: the Connectivity tab has one button for this, and everything
/// to do with getting onto a relay lives in here — the address, the relays you have been on before, and the one action
/// that applies right now.
///
/// <para>The action button says what pressing it will do: <b>Connect</b> when you are not on a relay, <b>Disconnect</b>
/// when you are. It changes the moment the state does, so leaving a relay leaves you looking at Connect, ready to go
/// somewhere else. Close is the only other button — there is nothing here to cancel, because everything happens when
/// you press the action, not when you leave.</para>
/// </summary>
internal sealed class RelayConnectDialog : Form
{
    private readonly TextBox addressBox = new() { Width = Dpi.Px(320), AccessibleName = "Server address" };
    private readonly ListBox rememberedList = new() { Width = Dpi.Px(320), Height = Dpi.Px(110), IntegralHeight = false, AccessibleName = "Remembered servers" };
    private readonly Button actionButton = new() { AutoSize = true };
    private readonly Label statusLabel = new() { AutoSize = true, AccessibleName = "Server connection" };
    // The server's status, while you are on one (Ed, 2026-10-01: "put it in the server window"): MainForm.ServerStatus.
    private readonly TextBox serverStatusBox = new()
    {
        ReadOnly = true, Multiline = true, WordWrap = true, ScrollBars = ScrollBars.Vertical,
        Width = Dpi.Px(420), Height = Dpi.Px(150), AccessibleName = "Server status", Visible = false,
    };
    private readonly MnemonicLabel serverStatusLabel = new() { Text = "Server &status (Alt+S):", AutoSize = true, Visible = false };
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 400 };

    private readonly Func<string?> connectedTo;
    private readonly Action<string> connect;
    private readonly Action disconnect;
    private readonly Func<IReadOnlyList<string>> loadRemembered;
    private readonly Action<string> forget;
    private readonly Func<string> status;
    private readonly Func<string?>? serverStatus;
    private bool wasConnected;
    private string? lastLine;

    /// <param name="connectedTo">The relay we are on, as it was typed, or null when we are on none.</param>
    /// <param name="connect">Go to this relay. Resolving happens off the window's thread, so the answer arrives later.</param>
    /// <param name="disconnect">Leave the relay we are on.</param>
    /// <param name="loadRemembered">The relays we have been on before, newest first.</param>
    /// <param name="forget">Take one off that list.</param>
    /// <param name="status">One line on what the relay is doing, for the line under the buttons.</param>
    /// <param name="serverStatus">The server's status while we are on one, a line each, or null.</param>
    internal RelayConnectDialog(Func<string?> connectedTo, Action<string> connect, Action disconnect,
        Func<IReadOnlyList<string>> loadRemembered, Action<string> forget, Func<string> status, Func<string?>? serverStatus = null)
    {
        this.serverStatus = serverStatus;
        serverStatusLabel.MnemonicTarget = serverStatusBox;
        this.connectedTo = connectedTo;
        this.connect = connect;
        this.disconnect = disconnect;
        this.loadRemembered = loadRemembered;
        this.forget = forget;
        this.status = status;

        Text = "Server";
        AccessibleRole = AccessibleRole.Dialog;
        AccessibleName = "Server";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = Dpi.Pad(12);

        var addressLabel = new MnemonicLabel { Text = "Server &address (Alt+A):", MnemonicTarget = addressBox, AutoSize = true };
        addressLabel.Click += (_, _) => addressBox.Focus();
        addressBox.KeyDown += (_, args) =>
        {
            if (args.KeyCode != Keys.Enter) return;
            args.SuppressKeyPress = true;
            OnAction();
        };

        var rememberedLabel = new MnemonicLabel { Text = "Re&membered servers (Alt+M):", MnemonicTarget = rememberedList, AutoSize = true };
        rememberedLabel.Click += (_, _) => rememberedList.Focus();
        // Enter on a remembered server goes to it (Ed, 2026-10-01: "on the remembered servers list, you should be able to
        // press enter and it connect to the server. at the moment you have to tab across to the connect button"). The list
        // asks for Enter itself: left to the window, Enter pressed the action button - which says Disconnect while you are
        // on a server, as you are whenever your profile reconnects at start - so Enter took you OFF the server.
        rememberedList.PreviewKeyDown += (_, args) => { if (args.KeyCode == Keys.Enter) args.IsInputKey = true; };
        rememberedList.KeyDown += (_, args) =>
        {
            if (args.KeyCode == Keys.Enter && rememberedList.SelectedItem is string chosen)
            {
                args.SuppressKeyPress = true;
                GoTo(chosen);
            }
            else if (args.KeyCode == Keys.Delete && rememberedList.SelectedItem is string doomed)
            {
                args.SuppressKeyPress = true;
                forget(doomed);
                FillRemembered();
                ScreenReader.Speak($"{doomed} removed");
            }
        };
        rememberedList.DoubleClick += (_, _) =>
        {
            if (rememberedList.SelectedItem is string chosen) GoTo(chosen);
        };

        actionButton.Click += (_, _) => OnAction();
        var close = new Button { Text = "C&lose", AutoSize = true, DialogResult = DialogResult.OK, AccessibleName = "Close" };

        // Right-aligned, in the order they always had (UI audit 2026-09-28, finding 11).
        var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty, Anchor = AnchorStyles.Right };
        buttons.Controls.Add(actionButton);
        buttons.Controls.Add(close);

        // The servers you have been on first, then the box to type a new one (Ed, 2026-09-29: "it should be remembered
        // servers 1st then the name box to enter one"). Moving through the list puts the server in the box, so Connect
        // (or Enter) goes to the one you are on in the list.
        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
        foreach (var c in new Control[] { rememberedLabel, rememberedList, addressLabel, addressBox, buttons, serverStatusLabel, serverStatusBox, statusLabel })
        {
            layout.Controls.Add(c);
        }
        Controls.Add(layout);
        rememberedList.SelectedIndexChanged += (_, _) =>
        {
            if (rememberedList.SelectedItem is string chosen) addressBox.Text = chosen;
        };

        rememberedLabel.TabIndex = 0;
        rememberedList.TabIndex = 1;
        addressLabel.TabIndex = 2;
        addressBox.TabIndex = 3;
        buttons.TabIndex = 4;
        actionButton.TabIndex = 0;
        close.TabIndex = 1;
        serverStatusLabel.TabIndex = 5;
        serverStatusBox.TabIndex = 6;
        statusLabel.TabIndex = 7;
        AcceptButton = actionButton;
        CancelButton = close;
        ContextHelp.Mark(addressBox, "dialog.server.address");
        ContextHelp.Mark(rememberedList, "dialog.server.remembered");
        ContextHelp.Mark(actionButton, "dialog.server.connect");
        ContextHelp.Mark(close, "dialog.server.close");
        ContextHelp.Mark(serverStatusBox, "dialog.server.status");

        FillRemembered();
        var opening = connectedTo() ?? loadRemembered().FirstOrDefault() ?? "";
        // It opens on the list, on the server you are on or went to last, when there is a list; with none, on the box.
        var shown = rememberedList.Items.IndexOf(opening);
        if (rememberedList.Items.Count > 0) rememberedList.SelectedIndex = shown >= 0 ? shown : 0;
        addressBox.Text = opening;
        ActiveControl = rememberedList.Items.Count > 0 ? rememberedList : addressBox;
        wasConnected = connectedTo() is not null;
        ApplyState(speak: false);

        // The address is resolved off the window's thread, so connecting finishes a moment after the press. Watch for
        // it rather than guess: the button must say what pressing it would do NOW, not what it did a second ago.
        refresh.Tick += (_, _) => ApplyState(speak: true);
        refresh.Start();
    }

    private void OnAction()
    {
        if (connectedTo() is not null)
        {
            disconnect();
            ApplyState(speak: false);
            ScreenReader.Speak("Disconnected from the server.");
            return;
        }
        var typed = addressBox.Text.Trim();
        if (typed.Length == 0)
        {
            statusLabel.Text = "Type a server address first.";
            ScreenReader.Speak("Type a server address first.");
            return;
        }
        connect(typed);
        // Spoken: an address connects there and then, and "Connecting..." is worth hearing. Recorded silently, the change
        // was never announced at all (2026-09-25 sweep).
        ApplyState(speak: true);
    }

    /// <summary>Go to a remembered server: from no server, or from another one (connecting there leaves the one you are on).
    /// On the one you are on already, you stay, and are told so.</summary>
    private void GoTo(string chosen)
    {
        addressBox.Text = chosen;
        if (string.Equals(connectedTo(), chosen, StringComparison.OrdinalIgnoreCase))
        {
            ScreenReader.Speak($"You're already on {chosen}.");
            return;
        }
        connect(chosen);
        ApplyState(speak: true);
    }

    /// <summary>The status with its figures taken out: what stays the same while only the numbers move.</summary>
    internal static string StatusShape(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\d+", "#");

    /// <summary>Gate seams: press the button's action, and one tick of the window's watch.</summary>
    internal void ActionForTest() => OnAction();
    internal void TickForTest() => ApplyState(speak: true);
    internal ListBox RememberedListForTest => rememberedList;
    internal TextBox ServerStatusBoxForTest => serverStatusBox;
    /// <summary>Test seam: whether the status box is being read, when Windows will not give a test the keyboard (null: it has
    /// the keyboard or not, as it really does).</summary>
    internal bool? BeingReadForTest;
    internal TextBox AddressBoxForTest => addressBox;

    private void FillRemembered()
    {
        var chosen = rememberedList.SelectedItem as string;
        rememberedList.BeginUpdate();
        rememberedList.Items.Clear();
        foreach (var entry in loadRemembered()) rememberedList.Items.Add(entry);
        rememberedList.EndUpdate();
        if (chosen is null) return;
        var index = rememberedList.Items.IndexOf(chosen);
        if (index >= 0) rememberedList.SelectedIndex = index;
    }

    /// <summary>Put the button and the line under it in step with what is actually happening.</summary>
    private void ApplyState(bool speak)
    {
        var connected = connectedTo() is not null;
        var text = connected ? "&Disconnect" : "&Connect";
        if (actionButton.Text != text)
        {
            actionButton.Text = text;
            actionButton.AccessibleName = connected ? "Disconnect from server" : "Connect to server";
        }
        var line = status();
        var lineChanged = line != lastLine;
        lastLine = line;
        if (statusLabel.Text != line) statusLabel.Text = line;
        // The server's status in place of the one line while you are on a server. Never rewritten while you are reading
        // it: setting the text puts the screen reader's cursor back at the top.
        var serverText = connected ? serverStatus?.Invoke() : null;
        var showBox = serverText is not null;
        if (serverStatusBox.Visible != showBox)
        {
            serverStatusLabel.Visible = showBox;
            serverStatusBox.Visible = showBox;
            statusLabel.Visible = !showBox;
        }
        // Held still while it is being read - but only against figures moving. A line changing in kind ("measuring" becoming a
        // figure, a server found to be an older one) is written even then: held whole, "measuring" stood for as long as Ed
        // read the box (2026-10-02).
        if (serverText is not null && serverStatusBox.Text != serverText
            && (!(BeingReadForTest ?? serverStatusBox.ContainsFocus) || StatusShape(serverStatusBox.Text) != StatusShape(serverText)))
            serverStatusBox.Text = serverText;
        if (connected != wasConnected)
        {
            wasConnected = connected;
            // Connected: the window has done its job and closes, without a word (Ed, 2026-09-29: "when you connect to a
            // server it should close the connect to server window ... that should just be silent when you connect").
            // The keyboard goes back to the tab's button, which now reads Disconnect from or change server.
            if (connected)
            {
                DialogResult = DialogResult.OK;
                Close();
                return;
            }
            FillRemembered();
            if (speak) ScreenReader.Speak("Not connected.");
        }
        // The line under the buttons, spoken when it changes while you are not on a server ("Connecting...", "Couldn't
        // find ..."), or when it says something is wrong. Only written to the label, a person on the button never heard a
        // failed connect, and assumed it was still trying (2026-09-25 sweep). Not while connected otherwise: the waiting
        // list changes there, and the person announcements have their own speech.
        else if (speak && lineChanged && line.Length > 0
                 && (!connected || line.Contains("isn't answering", StringComparison.Ordinal) || line.Contains("password", StringComparison.Ordinal)))
        {
            ScreenReader.Speak(line);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        refresh.Stop();
        refresh.Dispose();
        base.OnFormClosed(e);
    }

    /// <summary>Build it without showing it, so the dialog audits can reach it headlessly.</summary>
    internal static RelayConnectDialog Build() => new(() => null, _ => { }, () => { },
        () => Array.Empty<string>(), _ => { }, () => "Not connected to a server.");
}
