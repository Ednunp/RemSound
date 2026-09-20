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
    private readonly TextBox addressBox = new() { Width = 320, AccessibleName = "Server address" };
    private readonly ListBox rememberedList = new() { Width = 320, Height = 110, IntegralHeight = false, AccessibleName = "Remembered servers" };
    private readonly Button actionButton = new() { AutoSize = true };
    private readonly Label statusLabel = new() { AutoSize = true, AccessibleName = "Server status" };
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 400 };

    private readonly Func<string?> connectedTo;
    private readonly Action<string> connect;
    private readonly Action disconnect;
    private readonly Func<IReadOnlyList<string>> loadRemembered;
    private readonly Action<string> forget;
    private readonly Func<string> status;
    private bool wasConnected;

    /// <param name="connectedTo">The relay we are on, as it was typed, or null when we are on none.</param>
    /// <param name="connect">Go to this relay. Resolving happens off the window's thread, so the answer arrives later.</param>
    /// <param name="disconnect">Leave the relay we are on.</param>
    /// <param name="loadRemembered">The relays we have been on before, newest first.</param>
    /// <param name="forget">Take one off that list.</param>
    /// <param name="status">One line on what the relay is doing, for the line under the buttons.</param>
    internal RelayConnectDialog(Func<string?> connectedTo, Action<string> connect, Action disconnect,
        Func<IReadOnlyList<string>> loadRemembered, Action<string> forget, Func<string> status)
    {
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
        Padding = new Padding(12);

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
        rememberedList.KeyDown += (_, args) =>
        {
            if (args.KeyCode == Keys.Enter && rememberedList.SelectedItem is string chosen)
            {
                args.SuppressKeyPress = true;
                addressBox.Text = chosen;
                if (connectedTo() is null) connect(chosen);
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
            if (rememberedList.SelectedItem is not string chosen) return;
            addressBox.Text = chosen;
            if (connectedTo() is null) connect(chosen);
        };

        actionButton.Click += (_, _) => OnAction();
        var close = new Button { Text = "C&lose", AutoSize = true, DialogResult = DialogResult.OK, AccessibleName = "Close" };

        var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
        buttons.Controls.Add(actionButton);
        buttons.Controls.Add(close);

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
        foreach (var c in new Control[] { addressLabel, addressBox, rememberedLabel, rememberedList, buttons, statusLabel })
        {
            layout.Controls.Add(c);
        }
        Controls.Add(layout);

        addressLabel.TabIndex = 0;
        addressBox.TabIndex = 1;
        rememberedLabel.TabIndex = 2;
        rememberedList.TabIndex = 3;
        buttons.TabIndex = 4;
        actionButton.TabIndex = 0;
        close.TabIndex = 1;
        statusLabel.TabIndex = 5;
        AcceptButton = actionButton;
        CancelButton = close;

        FillRemembered();
        addressBox.Text = connectedTo() ?? loadRemembered().FirstOrDefault() ?? "";
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
        ApplyState(speak: false);
    }

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
        if (statusLabel.Text != line) statusLabel.Text = line;
        if (connected != wasConnected)
        {
            wasConnected = connected;
            FillRemembered();   // a relay we have just gone to is remembered now
            if (speak) ScreenReader.Speak(connected ? "Connected. The button is now Disconnect." : "Not connected.");
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
